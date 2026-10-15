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

        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 取最近保存的摘要，按创建时间降序（新的在前）。
    /// </summary>
    /// <remarks>
    /// 排序键 <c>created_at DESC, id DESC</c>：只按 <c>created_at</c> 排时，
    /// 同一批窗口内生成的摘要时间戳可能完全相同，次级键保证每次翻页的行序稳定、不会重影或漏行。
    /// </remarks>
    /// <param name="limit">最多返回多少条；超出 1–1000 的取值会被钳制到该区间。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<Digest>> GetRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM digests
            ORDER BY created_at DESC, id DESC
            LIMIT $limit;
            """;
        command.Bind("$limit", QueryLimit.Clamp(limit));

        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 取某个分类下最近保存的摘要，按创建时间降序（新的在前）。
    /// </summary>
    /// <remarks>
    /// 次级排序键与 <see cref="GetRecentAsync"/> 一致（<c>id DESC</c>），保证同分类列表翻页稳定。
    /// </remarks>
    /// <param name="category">目标分类。</param>
    /// <param name="limit">最多返回多少条；超出 1–1000 的取值会被钳制到该区间。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="category"/> 不是已定义的枚举值。调用方错误，不能等到读行时再报"数据库有脏数据"。
    /// </exception>
    public async Task<IReadOnlyList<Digest>> GetByCategoryAsync(
        DigestCategory category,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(
                nameof(category), category, $"未知的摘要分类：{(int)category}。");
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM digests
            WHERE category = $category
            ORDER BY created_at DESC, id DESC
            LIMIT $limit;
            """;
        command
            .Bind("$category", (int)category)
            .Bind("$limit", QueryLimit.Clamp(limit));

        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<Digest>> ReadAllAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var digests = new List<Digest>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            digests.Add(ReadDigest(reader));
        }

        return digests;
    }

    private static Digest ReadDigest(SqliteDataReader reader) => new()
    {
        Id = reader.GetRequiredString(0),
        BatchId = reader.GetRequiredString(1),
        Category = ToDigestCategory(reader.GetInt32(2)),
        Summary = reader.GetRequiredString(3),
        Reason = reader.GetRequiredString(4),
        CreatedAt = reader.GetDateTime(5),
    };

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