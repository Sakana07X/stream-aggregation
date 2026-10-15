using Microsoft.Data.Sqlite;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Infrastructure.Persistence;

/// <summary>
/// 基于 SQLite 的消息存储。
/// </summary>
/// <remarks>
/// 时间戳统一存 ISO-8601 UTC 文本，读取时返回 <see cref="DateTimeKind.Utc"/> 的
/// <see cref="DateTime"/>，因此 <c>timestamp &gt;= $since</c> 的比较结果与时间序一致。
/// </remarks>
public sealed class MessageRepository : IMessageStore
{
    private const string SelectColumns = "message_id, group_id, sender, text, timestamp";

    private const string UpsertSql = """
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
    /// <param name="clock">时间源；不传时使用 UTC 系统时钟，测试请注入假时钟。</param>
    public MessageRepository(ISqliteConnectionFactory connectionFactory, IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);

        _connectionFactory = connectionFactory;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>
    /// 写入一条消息。同一 <see cref="Message.MessageId"/> 重复写入会覆盖旧值（幂等）。
    /// </summary>
    public async Task SaveAsync(Message message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.MessageId, nameof(message.MessageId));

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = UpsertSql;
        command
            .Bind("$message_id", message.MessageId)
            .Bind("$group_id", message.GroupId ?? string.Empty)
            .Bind("$sender", message.Sender ?? string.Empty)
            .Bind("$text", message.Text ?? string.Empty)
            .Bind("$timestamp", SqliteTime.ToStorage(message.Timestamp));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Message?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id, nameof(id));

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM messages WHERE message_id = $message_id;";
        command.Bind("$message_id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadMessage(reader)
            : null;
    }

    /// <summary>按群组取时间点之后的消息，按时间升序。</summary>
    public async Task<IReadOnlyList<Message>> GetByGroupAsync(
        string groupId,
        DateTime since,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId, nameof(groupId));

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM messages
            WHERE group_id = $group_id AND timestamp >= $since
            ORDER BY timestamp ASC, message_id ASC;
            """;
        command
            .Bind("$group_id", groupId)
            .Bind("$since", SqliteTime.ToStorage(since));

        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 取最近写入的消息，按时间降序（新的在前），不限群组。
    /// </summary>
    /// <remarks>
    /// 排序键 <c>timestamp DESC, message_id DESC</c>：次级键与 <see cref="GetByGroupAsync"/> 同理，
    /// 保证时间戳相同的消息（批量导入、OneBot 重发）之间顺序稳定，翻页不重影不漏行。
    /// </remarks>
    /// <param name="limit">最多返回多少条；超出 1–1000 的取值会被钳制到该区间。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<Message>> GetRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM messages
            ORDER BY timestamp DESC, message_id DESC
            LIMIT $limit;
            """;
        command.Bind("$limit", QueryLimit.Clamp(limit));

        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按保留期删除过期消息（保留期为负数或零时直接返回，不做删除）。</summary>
    public Task CleanupAsync(TimeSpan retention, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero, nameof(retention));

        return DeleteOlderThanAsync(_clock.Now - retention, cancellationToken);
    }

    /// <summary>删除时间戳早于给定时刻（UTC）的消息，返回删除行数。</summary>
    public async Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM messages WHERE timestamp < $cutoff;";
        command.Bind("$cutoff", SqliteTime.ToStorage(cutoffUtc));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static Message ReadMessage(SqliteDataReader reader) => new()
    {
        MessageId = reader.GetRequiredString(0),
        GroupId = reader.GetRequiredString(1),
        Sender = reader.GetRequiredString(2),
        Text = reader.GetRequiredString(3),
        Timestamp = reader.GetDateTime(4),
    };

    private static async Task<IReadOnlyList<Message>> ReadAllAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var messages = new List<Message>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            messages.Add(ReadMessage(reader));
        }

        return messages;
    }
}