using Microsoft.Data.Sqlite;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Core.Diagnostics;

namespace StreamingAggregation.Infrastructure.Persistence;

/// <summary>一次保留期清理的结果。</summary>
/// <param name="CutoffUtc">清理截止时间（UTC），早于该时间的数据被删除。</param>
/// <param name="Batches">删除的聚合批次数（其摘要随外键级联删除）。</param>
/// <param name="Digests">删除的摘要数（含级联删除的部分）。</param>
/// <param name="Messages">删除的消息数。</param>
/// <param name="ProcessedEvents">删除的去重记录数。</param>
public sealed record RetentionReport(
    DateTime CutoffUtc,
    int Batches,
    int Digests,
    int Messages,
    int ProcessedEvents);

/// <summary>
/// 保留期清理：删除超出保留期的消息、批次、摘要和去重记录。
/// </summary>
/// <remarks>
/// <para>
/// 默认保留 30 天（与 <c>AggregationSettings.RetentionDays</c> 的默认值一致）。
/// </para>
/// <para>
/// 整个清理在一个事务里完成，中途失败不会留下"删一半"的中间态。
/// </para>
/// <para>
/// 不清理 <c>tasks</c>：行动项是用户自己维护的待办，删除等于丢用户数据。
/// 不清理 <c>llm_calls</c>：调用记录由模型客户端（T4/T5）自行管理策略。
/// </para>
/// </remarks>
public sealed class RetentionCleaner
{
    /// <summary>默认保留期：30 天。</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(30);

    private const string DeleteBatchesSql = "DELETE FROM aggregation_batches WHERE window_end < $cutoff;";
    private const string DeleteDigestsSql = "DELETE FROM digests WHERE created_at < $cutoff;";
    private const string DeleteMessagesSql = "DELETE FROM messages WHERE timestamp < $cutoff;";
    private const string DeleteProcessedEventsSql = "DELETE FROM processed_events WHERE processed_at < $cutoff;";

    private const string CountCascadeDigestsSql = """
        SELECT COUNT(*)
        FROM digests
        WHERE batch_id IN (SELECT id FROM aggregation_batches WHERE window_end < $cutoff);
        """;

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;

    /// <param name="connectionFactory">连接工厂。</param>
    /// <param name="clock">时间源；不传时使用 UTC 系统时钟。</param>
    public RetentionCleaner(ISqliteConnectionFactory connectionFactory, IClock? clock = null)
    {
        Guard.NotNull(connectionFactory);

        _connectionFactory = connectionFactory;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>按默认 30 天保留期清理。</summary>
    public Task<RetentionReport> CleanupAsync(CancellationToken cancellationToken = default) =>
        CleanupAsync(DefaultRetention, cancellationToken);

    /// <summary>按指定保留期清理，返回各表删除行数。</summary>
    public async Task<RetentionReport> CleanupAsync(
        TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero, nameof(retention));

        var cutoff = _clock.Now - retention;
        var cutoffStorage = SqliteTime.ToStorage(cutoff);

        using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        // 先删批次：digests.batch_id 上的外键是 ON DELETE CASCADE，摘要会跟着消失，
        // 所以要在删除前先把会被级联带走的行数统计出来。
        var cascadeDigests = await CountAsync(connection, transaction, CountCascadeDigestsSql, cutoffStorage, cancellationToken)
            .ConfigureAwait(false);

        var batches = await ExecuteDeleteAsync(connection, transaction, DeleteBatchesSql, cutoffStorage, cancellationToken)
            .ConfigureAwait(false);
        var digests = cascadeDigests
            + await ExecuteDeleteAsync(connection, transaction, DeleteDigestsSql, cutoffStorage, cancellationToken)
                .ConfigureAwait(false);
        var messages = await ExecuteDeleteAsync(connection, transaction, DeleteMessagesSql, cutoffStorage, cancellationToken)
            .ConfigureAwait(false);
        var processedEvents = await ExecuteDeleteAsync(
            connection, transaction, DeleteProcessedEventsSql, cutoffStorage, cancellationToken).ConfigureAwait(false);

        // net48 的 SqliteTransaction 没有 CommitAsync：提交本身是同步的。
        transaction.Commit();

        CheckpointWal(connection);

        return new RetentionReport(cutoff, batches, digests, messages, processedEvents);
    }

    private static async Task<int> ExecuteDeleteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string cutoffStorage,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Bind("$cutoff", cutoffStorage);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> CountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string cutoffStorage,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Bind("$cutoff", cutoffStorage);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 尽力而为地回收 WAL：大批量删除后 WAL 会明显膨胀，PASSIVE 检查点不阻塞其他连接，
    /// 失败也不影响清理结果。
    /// </summary>
    private static void CheckpointWal(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // 检查点只是空间回收，收不到就留给下一次 WAL 自动回收。
        }
    }
}