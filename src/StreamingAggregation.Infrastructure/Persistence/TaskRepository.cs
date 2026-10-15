using Microsoft.Data.Sqlite;
using StreamingAggregation.Core;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Infrastructure.Persistence;

/// <summary>
/// 基于 SQLite 的行动项（待办）存储。
/// </summary>
/// <remarks>
/// <para>
/// 写入路径刻意分成两类：<see cref="SaveAsync"/> 是全量 UPSERT，用于刷新描述、截止时间等业务字段；
/// <see cref="SetCompletedAsync"/> / <see cref="SetIgnoredAsync"/> 是只碰状态列的窄 UPDATE。
/// 两者不可合并——对同一批次重新摘要时，模型会重新产出全量行动项并走 <see cref="SaveAsync"/>，
/// 若那时连状态列一起覆盖，用户已经勾掉的「完成」与「忽略」会被冲回默认值。
/// </para>
/// <para>
/// <c>importance</c> 存 <see cref="Importance"/> 的枚举序号（High=0、Medium=1、Low=2），
/// 与 <c>DigestRepository</c> 对 <c>digests.category</c> 的处理保持一致。数据库可能被外部改坏，
/// 因此读取时校验枚举合法性，非法值降级为 <see cref="Importance.Medium"/> 而不是抛异常：
/// 坏掉的行不应该让整个待办列表读不出来，降级到中优先级也避免它被顶到列表最前面。
/// </para>
/// </remarks>
public sealed class TaskRepository : ITaskStore
{
    /// <summary>枚举非法时的降级取值：与 <c>tasks.importance</c> 的列默认值一致，且是中优先级。</summary>
    private const Importance FallbackImportance = Importance.Medium;

    private const string SelectColumns =
        "id, action, deadline, importance, reason, source_group_id, completed, ignored";

    private const string UpsertSql = """
        INSERT INTO tasks
            (id, action, deadline, importance, reason, source_group_id,
             completed, ignored, created_at, updated_at)
        VALUES
            ($id, $action, $deadline, $importance, $reason, $source_group_id,
             $completed, $ignored, $created_at, $updated_at)
        ON CONFLICT(id) DO UPDATE SET
            action          = excluded.action,
            deadline        = excluded.deadline,
            importance      = excluded.importance,
            reason          = excluded.reason,
            source_group_id = excluded.source_group_id,
            completed       = excluded.completed,
            ignored         = excluded.ignored,
            updated_at      = excluded.updated_at;
        """;

    private const string SetCompletedSql = """
        UPDATE tasks
        SET completed  = $completed,
            updated_at = $updated_at
        WHERE id = $id;
        """;

    private const string SetIgnoredSql = """
        UPDATE tasks
        SET ignored    = $ignored,
            updated_at = $updated_at
        WHERE id = $id;
        """;

    private const string DeleteSql = "DELETE FROM tasks WHERE id = $id;";

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;

    /// <param name="connectionFactory">连接工厂。</param>
    /// <param name="clock">时间源；不传时使用 UTC 系统时钟，测试请注入假时钟。</param>
    public TaskRepository(ISqliteConnectionFactory connectionFactory, IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);

        _connectionFactory = connectionFactory;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>
    /// 插入或更新行动项（同一 <see cref="ActionItem.Id"/> 重复保存会覆盖旧值）。
    /// </summary>
    /// <remarks>
    /// 注意这是全量覆盖，包含 <c>completed</c> / <c>ignored</c>；用户改状态请走
    /// <see cref="SetCompletedAsync"/> / <see cref="SetIgnoredAsync"/>。<c>created_at</c> 在
    /// 冲突分支里不参与更新，保持首次入库的时间。
    /// </remarks>
    public async Task SaveAsync(ActionItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.Id, nameof(item.Id));
        ArgumentException.ThrowIfNullOrWhiteSpace(item.Action, nameof(item.Action));

        var now = SqliteTime.ToStorage(_clock.Now);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = UpsertSql;
        command
            .Bind("$id", item.Id)
            .Bind("$action", item.Action)
            .Bind("$deadline", ToStorage(item.Deadline))
            .Bind("$importance", (int)item.Importance)
            .Bind("$reason", item.Reason ?? string.Empty)
            .Bind("$source_group_id", item.SourceGroupId)
            .Bind("$completed", ToStorage(item.Completed))
            .Bind("$ignored", ToStorage(item.Ignored))
            .Bind("$created_at", now)
            .Bind("$updated_at", now);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ActionItem?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id, nameof(id));

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM tasks WHERE id = $id;";
        command.Bind("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadTask(reader)
            : null;
    }

    /// <summary>
    /// 取待办列表（未完成且未忽略），先按重要程度升序（High 在前），再按截止时间升序（早的在前），
    /// 没有截止时间的排在最后。
    /// </summary>
    /// <remarks>
    /// SQLite 默认把 NULL 排在 ASC 结果的最前面，所以排序式里用 <c>deadline IS NULL ASC</c> 把它压到最后；
    /// 末尾再补 <c>created_at ASC, id ASC</c> 保证同优先级、同截止时间的行顺序稳定。
    /// </remarks>
    public async Task<IReadOnlyList<ActionItem>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM tasks
            WHERE completed = 0 AND ignored = 0
            ORDER BY importance ASC, deadline IS NULL ASC, deadline ASC, created_at ASC, id ASC;
            """;

        var items = new List<ActionItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadTask(reader));
        }

        return items;
    }

    /// <inheritdoc />
    public async Task SetCompletedAsync(
        string id,
        bool completed,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id, nameof(id));

        await UpdateStatusAsync(id, SetCompletedSql, "$completed", completed, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetIgnoredAsync(
        string id,
        bool ignored,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id, nameof(id));

        await UpdateStatusAsync(id, SetIgnoredSql, "$ignored", ignored, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id, nameof(id));

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = DeleteSql;
        command.Bind("$id", id);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 只更新 <paramref name="valueParameter"/> 指定的状态列与 <c>updated_at</c>，
    /// 其余业务字段（描述、截止时间、重要程度）一律不碰。
    /// </summary>
    private async Task UpdateStatusAsync(
        string id,
        string sql,
        string valueParameter,
        bool value,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command
            .Bind("$id", id)
            .Bind(valueParameter, ToStorage(value))
            .Bind("$updated_at", SqliteTime.ToStorage(_clock.Now));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ActionItem ReadTask(SqliteDataReader reader) => new()
    {
        Id = reader.GetRequiredString(0),
        Action = reader.GetRequiredString(1),
        Deadline = ReadOptionalDateTime(reader, 2),
        Importance = ToImportance(reader.GetInt32(3)),
        Reason = reader.GetRequiredString(4),
        SourceGroupId = reader.GetRequiredString(5),
        Completed = reader.GetInt64(6) != 0,
        Ignored = reader.GetInt64(7) != 0,
    };

    /// <summary><c>deadline</c> 可空：NULL 直接给 null，其余按定宽 ISO-8601 UTC 文本还原。</summary>
    private static DateTime? ReadOptionalDateTime(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : SqliteTime.FromStorage(reader.GetString(ordinal));

    /// <summary>可选时间列转存储文本；null 绑 <see cref="DBNull.Value"/>（NULL），不写空串。</summary>
    private static object ToStorage(DateTime? value) =>
        value is { } deadline ? SqliteTime.ToStorage(deadline) : DBNull.Value;

    /// <summary>布尔列以 0/1 整数存储（与 <c>app_settings</c> 的开关列一致）。</summary>
    private static int ToStorage(bool value) => value ? 1 : 0;

    private static Importance ToImportance(int stored)
    {
        var importance = (Importance)stored;

        // 数据库可能被外部改坏：降级而不是抛异常，别让一行脏数据把整个待办列表读崩。
        return Enum.IsDefined(importance) ? importance : FallbackImportance;
    }
}
