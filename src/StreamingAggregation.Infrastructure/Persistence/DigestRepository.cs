using Microsoft.Data.Sqlite;
using StreamingAggregation.Core;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Infrastructure.Persistence;

/// <summary>
/// 基于 SQLite 的摘要存储。
/// </summary>
/// <remarks>
/// <c>digests.batch_id</c> 上有指向 <c>aggregation_batches</c> 的外键（ON DELETE CASCADE），
/// 因此保存摘要前必须先保存对应批次；批次被保留期清理时，其摘要会一并删除。
/// </remarks>
public sealed class DigestRepository : IDigestStore
{
    /// <summary>SQLite 主结果码 SQLITE_CONSTRAINT（https://sqlite.org/rescode.html）。</summary>
    private const int SqliteConstraintErrorCode = 19;
    private const string SelectColumns = "id, batch_id, category, summary, reason, created_at";

    private const string UpsertSql = """
        INSERT INTO digests (id, batch_id, category, summary, reason, created_at)
        VALUES ($id, $batch_id, $category, $summary, $reason, $created_at)
        ON CONFLICT(id) DO UPDATE SET
            batch_id   = excluded.batch_id,
            category   = excluded.category,
            summary    = excluded.summary,
            reason     = excluded.reason,
            created_at = excluded.created_at;
        """;

    private readonly ISqliteConnectionFactory _connectionFactory;

    /// <param name="connectionFactory">连接工厂。</param>
    public DigestRepository(ISqliteConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);

        _connectionFactory = connectionFactory;
    }

    /// <summary>保存摘要（同一 <see cref="Digest.Id"/> 重复保存会覆盖旧值）。</summary>
    /// <exception cref="InvalidOperationException">所属批次不存在时抛出。</exception>
    public async Task SaveAsync(Digest digest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(digest);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = UpsertSql;
        command
            .Bind("$id", digest.Id)
            .Bind("$batch_id", digest.BatchId)
            .Bind("$category", (int)digest.Category)
            .Bind("$summary", digest.Summary ?? string.Empty)
            .Bind("$reason", digest.Reason ?? string.Empty)
            .Bind("$created_at", SqliteTime.ToStorage(digest.CreatedAt));

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraintErrorCode)
        {
            throw new InvalidOperationException(
                $"无法保存摘要 '{digest.Id}'：所属批次 '{digest.BatchId}' 不存在，请先保存批次。", ex);
        }
    }

    /// <summary>取某个批次的全部摘要，按创建时间升序。</summary>
    public async Task<IReadOnlyList<Digest>> GetByBatchAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId, nameof(batchId));

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM digests
            WHERE batch_id = $batch_id
            ORDER BY created_at ASC, id ASC;
            """;
        command.Bind("$batch_id", batchId);

        var digests = new List<Digest>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            digests.Add(new Digest
            {
                Id = reader.GetRequiredString(0),
                BatchId = reader.GetRequiredString(1),
                Category = ToDigestCategory(reader.GetInt32(2)),
                Summary = reader.GetRequiredString(3),
                Reason = reader.GetRequiredString(4),
                CreatedAt = reader.GetDateTime(5),
            });
        }

        return digests;
    }

    private static DigestCategory ToDigestCategory(int stored)
    {
        var category = (DigestCategory)stored;
        if (!Enum.IsDefined(category))
        {
            throw new InvalidOperationException($"digests.category 存在非法取值：{stored}。");
        }

        return category;
    }
}