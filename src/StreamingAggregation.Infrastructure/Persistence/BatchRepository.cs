using Microsoft.Data.Sqlite;
using StreamingAggregation.Core;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Infrastructure.Persistence;

/// <summary>
/// 基于 SQLite 的聚合批次存储。
/// </summary>
/// <remarks>
/// 批次行只保存消息 ID 列表（JSON 数组），消息正文仍在 <c>messages</c> 表，
/// 避免同一段文本存两份。<see cref="SaveAsync"/> 会在同一个事务里把批次携带的消息
/// 一并落盘，因此 <see cref="GetByIdAsync"/> 能完整还原批次；消息已被保留期清理时，
/// 还原结果会少掉对应条目（批次与摘要仍然保留）。
/// </remarks>
public sealed class BatchRepository : IBatchStore
{
    private const string SelectColumns =
        "id, group_id, group_name, window_start, window_end, status, message_ids";

    private const string UpsertBatchSql = """
        INSERT INTO aggregation_batches
            (id, group_id, group_name, window_start, window_end, status, message_ids, message_count,
             created_at, updated_at)
        VALUES
            ($id, $group_id, $group_name, $window_start, $window_end, $status, $message_ids, $message_count,
             $created_at, $updated_at)
        ON CONFLICT(id) DO UPDATE SET
            group_id      = excluded.group_id,
            group_name    = excluded.group_name,
            window_start  = excluded.window_start,
            window_end    = excluded.window_end,
            status        = excluded.status,
            message_ids   = excluded.message_ids,
            message_count = excluded.message_count,
            updated_at    = excluded.updated_at;
        """;

    private const string UpsertMessageSql = """
        INSERT INTO messages (message_id, group_id, sender, text, timestamp)
        VALUES ($message_id, $group_id, $sender, $text, $timestamp)
        ON CONFLICT(message_id) DO UPDATE SET
            group_id  = excluded.group_id,
            sender    = excluded.sender,
            text      = excluded.text,
            timestamp = excluded.timestamp;
        """;

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;

    /// <param name="connectionFactory">连接工厂。</param>
    /// <param name="clock">时间源；不传时使用 UTC 系统时钟。</param>
    public BatchRepository(ISqliteConnectionFactory connectionFactory, IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);

        _connectionFactory = connectionFactory;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>保存批次与其携带的消息（同一事务）。</summary>
    public async Task SaveAsync(AggregationBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(batch.Messages, nameof(batch.Messages));

        var messageIds = batch.Messages.Select(static message => message.MessageId).ToArray();

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);

        foreach (var message in batch.Messages)
        {
            await UpsertMessageAsync(connection, transaction, message, cancellationToken).ConfigureAwait(false);
        }

        await UpsertBatchAsync(connection, transaction, batch, messageIds, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AggregationBatch?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id, nameof(id));

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM aggregation_batches WHERE id = $id;";
        command.Bind("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var messageIds = reader.GetStringList(6);

        // 先把标量列读出来（reader 释放后不可再读），再去查消息正文。
        var groupId = reader.GetString(1);
        var groupName = reader.GetString(2);
        var windowStart = reader.GetDateTime(3);
        var windowEnd = reader.GetDateTime(4);
        var status = ToBatchStatus(reader.GetInt32(5));
        await reader.DisposeAsync().ConfigureAwait(false);

        var messages = await LoadMessagesAsync(connection, messageIds, cancellationToken).ConfigureAwait(false);
        return new AggregationBatch
        {
            Id = id,
            GroupId = groupId,
            GroupName = groupName,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            Status = status,
            Messages = messages,
        };
    }

    /// <summary>取所有 <see cref="BatchStatus.Pending"/> 批次，按窗口结束时间升序（先到先处理）。</summary>
    public async Task<IReadOnlyList<AggregationBatch>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var batches = new List<AggregationBatch>();
        var messageIdLists = new List<IReadOnlyList<string>>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT {SelectColumns}
                FROM aggregation_batches
                WHERE status = $status
                ORDER BY window_end ASC, id ASC;
                """;
            command.Bind("$status", (int)BatchStatus.Pending);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                messageIdLists.Add(reader.GetStringList(6));
                batches.Add(new AggregationBatch
                {
                    Id = reader.GetString(0),
                    GroupId = reader.GetString(1),
                    GroupName = reader.GetString(2),
                    WindowStart = reader.GetDateTime(3),
                    WindowEnd = reader.GetDateTime(4),
                    Status = ToBatchStatus(reader.GetInt32(5)),
                    Messages = Array.Empty<Message>(),
                });
            }
        }

        // 一次性把消息读回来再按批次回填，避免 N+1 查询。
        var messagesById = await LoadMessagesByIdAsync(
            connection,
            messageIdLists.SelectMany(static ids => ids),
            cancellationToken).ConfigureAwait(false);

        return batches
            .Select((batch, index) => batch with { Messages = ResolveMessages(messageIdLists[index], messagesById) })
            .ToArray();
    }

    private async Task UpsertBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AggregationBatch batch,
        IReadOnlyList<string> messageIds,
        CancellationToken cancellationToken)
    {
        var now = SqliteTime.ToStorage(_clock.Now);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UpsertBatchSql;
        command
            .Bind("$id", batch.Id)
            .Bind("$group_id", batch.GroupId)
            .Bind("$group_name", batch.GroupName ?? string.Empty)
            .Bind("$window_start", SqliteTime.ToStorage(batch.WindowStart))
            .Bind("$window_end", SqliteTime.ToStorage(batch.WindowEnd))
            .Bind("$status", (int)batch.Status)
            .Bind("$message_ids", SqliteCommandExtensions.ToJsonArray(messageIds))
            .Bind("$message_count", messageIds.Count)
            .Bind("$created_at", now)
            .Bind("$updated_at", now);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertMessageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Message message,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message.MessageId, nameof(message.MessageId));

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UpsertMessageSql;
        command
            .Bind("$message_id", message.MessageId)
            .Bind("$group_id", message.GroupId ?? string.Empty)
            .Bind("$sender", message.Sender ?? string.Empty)
            .Bind("$text", message.Text ?? string.Empty)
            .Bind("$timestamp", SqliteTime.ToStorage(message.Timestamp));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<Message>> LoadMessagesAsync(
        SqliteConnection connection,
        IReadOnlyList<string> messageIds,
        CancellationToken cancellationToken)
    {
        var messagesById = await LoadMessagesByIdAsync(connection, messageIds, cancellationToken).ConfigureAwait(false);
        return messageIds.Select(id => messagesById.GetValueOrDefault(id)).OfType<Message>().ToArray();
    }

    private static async Task<Dictionary<string, Message>> LoadMessagesByIdAsync(
        SqliteConnection connection,
        IEnumerable<string> messageIds,
        CancellationToken cancellationToken)
    {
        var distinctIds = messageIds.Where(static id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
        if (distinctIds.Length == 0)
        {
            return new Dictionary<string, Message>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, Message>(distinctIds.Length, StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT message_id, group_id, sender, text, timestamp
            FROM messages
            WHERE message_id IN ({string.Join(", ", distinctIds.Select(static (_, index) => "$id" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)))});
            """;
        for (var index = 0; index < distinctIds.Length; index++)
        {
            command.Bind("$id" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), distinctIds[index]);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[reader.GetString(0)] = MessageRepository.ReadMessage(reader);
        }

        return result;
    }

    private static IReadOnlyList<Message> ResolveMessages(
        IReadOnlyList<string> messageIds,
        IReadOnlyDictionary<string, Message> messagesById) =>
        messageIds.Select(id => messagesById.GetValueOrDefault(id)).OfType<Message>().ToArray();

    private static BatchStatus ToBatchStatus(int stored)
    {
        var status = (BatchStatus)stored;
        if (!Enum.IsDefined(status))
        {
            throw new InvalidOperationException($"aggregation_batches.status 存在非法取值：{stored}。");
        }

        return status;
    }
}