using System.Globalization;
using Microsoft.Data.Sqlite;
using StreamingAggregation.Core;
using StreamingAggregation.Core.Models;
using StreamingAggregation.Infrastructure.Persistence;
using StreamingAggregation.Tests.Fakes;
using Xunit.Sdk;

namespace StreamingAggregation.Tests.Persistence;

/// <summary>
/// <c>TaskRepository</c> 的四组行为：
/// <list type="number">
/// <item>后台刷新（<c>SaveAsync</c> 的 UPSERT）不得冲掉用户已经勾掉的完成/忽略状态；</item>
/// <item>与用户状态更新交错时，旧状态必须活下来（交接文档点名的竞态回归）；</item>
/// <item>基础 CRUD、待办排序与脏数据降级；</item>
/// <item>整批写入 <c>SaveAllAsync</c>：要么全进库，要么一条都不进（交接文档 P1-3）。</item>
/// </list>
/// </summary>
/// <remarks>
/// 第一、二组与状态保护有关：保护已经下沉到 SQL 的 <c>ON CONFLICT DO UPDATE</c> 分支，
/// 冲突时保持数据库原值。两组测试如果同时红，说明保护被删了——只红其中一组，说明保护还在但有缺口。
/// 第四组守的是"部分写入不可能"：批处理路径没有逐条兜底的 <c>try/catch</c>，
/// 写失败必须让整个批次失败，而不是悄悄少写几条。
/// </remarks>
public sealed class TaskRepositoryTests
{
    private const string TaskId = "test-action-0001";
    private const string TestGroupId = "test-group-id";

    private static readonly DateTime BaseTime = new(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// 并发用例的兜底超时。SQLite 自己的 <c>busy_timeout</c> 就有 30 秒，
    /// 这里给测试侧一个同量级的上限：锁真出问题时要让测试红，而不是把测试进程挂住。
    /// </summary>
    private static readonly TimeSpan ConcurrencyGuard = TimeSpan.FromSeconds(30);

    // ---------------------------------------------------------------------
    // 第一组：状态保留语义（UPSERT 冲突分支不覆盖状态列）
    // ---------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_RefreshesBusinessFieldsButKeepsCompletedAndIgnored()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var tasks = database.CreateTaskRepository(new FakeClock(BaseTime));

        // 用户已经勾掉并忽略了这条行动项。
        await tasks.SaveAsync(BuildItem(TaskId, completed: true, ignored: true));

        // 模型重跑，产出一个全新的副本：业务字段全变，状态列回到模型默认值 false。
        await tasks.SaveAsync(BuildItem(
            TaskId,
            action: "test-action-refreshed",
            deadline: BaseTime.AddHours(2),
            importance: Importance.Low,
            reason: "test-reason-refreshed",
            sourceGroupId: "test-group-id-refreshed"));

        var stored = await GetRequiredAsync(tasks, TaskId);

        // 业务字段确实被刷新了——否则这条测试就只测到了"什么都没发生"。
        Assert.Equal("test-action-refreshed", stored.Action);
        Assert.Equal(BaseTime.AddHours(2), stored.Deadline);
        Assert.Equal(Importance.Low, stored.Importance);
        Assert.Equal("test-reason-refreshed", stored.Reason);
        Assert.Equal("test-group-id-refreshed", stored.SourceGroupId);

        // 核心断言：用户状态没有被冲回默认值。
        Assert.True(stored.Completed);
        Assert.True(stored.Ignored);

        // 端到端后果：这条行动项不该重新冒到待办列表里。
        Assert.Empty(await tasks.GetPendingAsync());
    }

    [Fact]
    public async Task SaveAsync_KeepsCompletedWhileRefreshingItemThatWasNeverIgnored()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var tasks = database.CreateTaskRepository(new FakeClock(BaseTime));

        await tasks.SaveAsync(BuildItem(TaskId, completed: true, ignored: false));
        await tasks.SaveAsync(BuildItem(TaskId, action: "test-action-refreshed"));

        var stored = await GetRequiredAsync(tasks, TaskId);

        Assert.True(stored.Completed);
        Assert.False(stored.Ignored);
        Assert.Equal("test-action-refreshed", stored.Action);
    }

    [Fact]
    public async Task SaveAsync_KeepsIgnoredWhileRefreshingItemThatWasNeverCompleted()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var tasks = database.CreateTaskRepository(new FakeClock(BaseTime));

        await tasks.SaveAsync(BuildItem(TaskId, completed: false, ignored: true));
        await tasks.SaveAsync(BuildItem(TaskId, action: "test-action-refreshed"));

        var stored = await GetRequiredAsync(tasks, TaskId);

        Assert.False(stored.Completed);
        Assert.True(stored.Ignored);
        Assert.Equal("test-action-refreshed", stored.Action);
    }

    [Fact]
    public async Task SaveAsync_DoesNotResetCompletedCheckedAfterLastRefresh()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var tasks = database.CreateTaskRepository(new FakeClock(BaseTime));

        await tasks.SaveAsync(BuildItem(TaskId));
        await tasks.SetCompletedAsync(TaskId, completed: true);

        // 后台刷新带着"未完成"的旧副本回来：状态不能被清零。
        await tasks.SaveAsync(BuildItem(TaskId, action: "test-action-refreshed"));

        var stored = await GetRequiredAsync(tasks, TaskId);

        Assert.True(stored.Completed);
        Assert.Equal("test-action-refreshed", stored.Action);
    }

    [Fact]
    public async Task SaveAsync_DoesNotResetIgnoredCheckedAfterLastRefresh()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var tasks = database.CreateTaskRepository(new FakeClock(BaseTime));

        await tasks.SaveAsync(BuildItem(TaskId));
        await tasks.SetIgnoredAsync(TaskId, ignored: true);

        await tasks.SaveAsync(BuildItem(TaskId, action: "test-action-refreshed"));

        var stored = await GetRequiredAsync(tasks, TaskId);

        Assert.True(stored.Ignored);
        Assert.Equal("test-action-refreshed", stored.Action);
    }

    [Fact]
    public async Task SaveAsync_StoresStatusOnFirstInsert()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var tasks = database.CreateTaskRepository(new FakeClock(BaseTime));

        // 保护只在冲突分支生效；新行是 INSERT，必须照实写入状态列，
        // 否则"把状态列从 INSERT 里删掉"这种改法也能让上面的用例全绿。
        await tasks.SaveAsync(BuildItem(TaskId, completed: true, ignored: true));

        var stored = await GetRequiredAsync(tasks, TaskId);

        Assert.True(stored.Completed);
        Assert.True(stored.Ignored);
        Assert.Empty(await tasks.GetPendingAsync());
    }

    [Fact]
    public async Task SaveAsync_KeepsCreatedAtButMovesUpdatedAtOnConflict()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new FakeClock(BaseTime);
        var tasks = database.CreateTaskRepository(clock);

        await tasks.SaveAsync(BuildItem(TaskId));
        clock.Advance(TimeSpan.FromMinutes(5));
        await tasks.SaveAsync(BuildItem(TaskId, action: "test-action-refreshed"));

        // 冲突分支只碰 updated_at：首次入库时间必须留着，否则排序与排障都失去锚点。
        Assert.Equal(FormatTimestamp(BaseTime), await ReadColumnAsync(database, "created_at", TaskId));
        Assert.Equal(FormatTimestamp(BaseTime.AddMinutes(5)), await ReadColumnAsync(database, "updated_at", TaskId));
    }

    // ---------------------------------------------------------------------
    // 第二组：并发交错回归（交接文档点名的"先查后写"竞态）
    // ---------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_AfterConcurrentSetCompleted_KeepsUserStatus()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var instanceA = database.CreateTaskRepository(new FakeClock(BaseTime));
        var instanceB = database.CreateTaskRepository(new FakeClock(BaseTime.AddMinutes(1)));

        await instanceA.SaveAsync(BuildItem(TaskId));

        // 实例 A 手上的副本是用户勾选之前读到的，已经"旧"了——这正是竞态的起点。
        var staleCopy = await GetRequiredAsync(instanceA, TaskId);
        Assert.False(staleCopy.Completed);

        var inserted = NewBarrier();
        var statusWritten = NewBarrier();

        // 屏障把两条写入强制排成"A 先入库 → B 改状态 → A 再刷新"，
        // 与用户点勾选恰好落在"读取之后、落库之前"的窗口一致。
        var refreshTask = Task.Run(async () =>
        {
            await inserted.Task;
            await statusWritten.Task;
            await instanceA.SaveAsync(staleCopy with { Action = "test-action-refreshed" });
        });

        inserted.SetResult(true);
        await instanceB.SetCompletedAsync(TaskId, completed: true);
        statusWritten.SetResult(true);
        await WaitWithinGuardAsync(refreshTask);

        var stored = await GetRequiredAsync(instanceA, TaskId);

        Assert.Equal("test-action-refreshed", stored.Action);
        Assert.True(stored.Completed);
    }

    [Fact]
    public async Task SaveAsync_AfterConcurrentSetIgnored_KeepsUserStatus()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var instanceA = database.CreateTaskRepository(new FakeClock(BaseTime));
        var instanceB = database.CreateTaskRepository(new FakeClock(BaseTime.AddMinutes(1)));

        await instanceA.SaveAsync(BuildItem(TaskId));

        var staleCopy = await GetRequiredAsync(instanceA, TaskId);
        Assert.False(staleCopy.Ignored);

        var inserted = NewBarrier();
        var statusWritten = NewBarrier();

        var refreshTask = Task.Run(async () =>
        {
            await inserted.Task;
            await statusWritten.Task;
            await instanceA.SaveAsync(staleCopy with { Action = "test-action-refreshed" });
        });

        inserted.SetResult(true);
        await instanceB.SetIgnoredAsync(TaskId, ignored: true);
        statusWritten.SetResult(true);
        await WaitWithinGuardAsync(refreshTask);

        var stored = await GetRequiredAsync(instanceA, TaskId);

        Assert.Equal("test-action-refreshed", stored.Action);
        Assert.True(stored.Ignored);
    }

    [Fact]
    public async Task ConcurrentRefreshAndStatusFlip_AlwaysEndsWithUserStatusIntact()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var refresher = database.CreateTaskRepository(new FakeClock(BaseTime.AddMinutes(1)));
        var user = database.CreateTaskRepository(new FakeClock(BaseTime.AddMinutes(2)));

        await user.SaveAsync(BuildItem(TaskId));
        var staleCopy = await GetRequiredAsync(refresher, TaskId);

        // 三条写入真正同时发起，谁先落库不确定；但结果是确定的：
        // 冲突分支不覆盖状态列，所以无论谁赢，completed/ignored 都必然是 true。
        // （这条断言本身没有概率性，只有对"没有状态保护"的实现才会偶发红。）
        await RunConcurrentlyAsync(
            () => refresher.SaveAsync(staleCopy with { Action = "test-action-refreshed" }),
            () => user.SetCompletedAsync(TaskId, completed: true),
            () => user.SetIgnoredAsync(TaskId, ignored: true));

        var stored = await GetRequiredAsync(user, TaskId);

        Assert.Equal("test-action-refreshed", stored.Action);
        Assert.True(stored.Completed);
        Assert.True(stored.Ignored);
    }

    // ---------------------------------------------------------------------
    // 第三组：基础 CRUD、待办排序与脏数据降级
    // ---------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_ThenGetByIdAsync_RoundTripsEveryField()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var tasks = database.CreateTaskRepository(new FakeClock(BaseTime));

        var expected = BuildItem(
            TaskId,
            action: "test-action-含中文与\"引号\"",
            deadline: BaseTime.AddHours(3),
            importance: Importance.High,
            reason: "test-reason-round-trip",
            sourceGroupId: TestGroupId,
            completed: true,
            ignored: true);
        await tasks.SaveAsync(expected);

        var stored = await GetRequiredAsync(tasks, TaskId);

        // ActionItem 是 record，整体相等一次就能覆盖全部字段。
        Assert.Equal(expected, stored);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNullForUnknownId()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var tasks = database.CreateTaskRepository(new FakeClock(BaseTime));

        await tasks.SaveAsync(BuildItem(TaskId));

        Assert.Null(await tasks.GetByIdAsync("test-action-not-exist"));
    }

    [Fact]
    public async Task GetPendingAsync_ReturnsOnlyUnfinishedAndUnignored()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var tasks = database.CreateTaskRepository(new FakeClock(BaseTime));

        await tasks.SaveAsync(BuildItem("test-action-pending"));
        await tasks.SaveAsync(BuildItem("test-action-completed", completed: true));
        await tasks.SaveAsync(BuildItem("test-action-ignored", ignored: true));
        await tasks.SaveAsync(BuildItem("test-action-both", completed: true, ignored: true));

        var pending = await tasks.GetPendingAsync();

        Assert.Equal(["test-action-pending"], pending.Select(static item => item.Id));
    }

    [Fact]
    public async Task GetPendingAsync_OrdersByImportanceThenDeadlineWithNullsLast()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new FakeClock(BaseTime);
        var tasks = database.CreateTaskRepository(clock);

        async Task SaveItemAsync(string id, DateTime? deadline, Importance importance)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await tasks.SaveAsync(BuildItem(id, deadline: deadline, importance: importance));
        }

        // 故意乱序写入：排序必须由 SQL 决定，不能靠插入顺序蒙对。
        await SaveItemAsync("low-no-deadline", null, Importance.Low);
        await SaveItemAsync("medium-no-deadline", null, Importance.Medium);
        await SaveItemAsync("high-no-deadline", null, Importance.High);
        await SaveItemAsync("medium-early", BaseTime.AddHours(1), Importance.Medium);
        await SaveItemAsync("high-late", BaseTime.AddHours(5), Importance.High);
        await SaveItemAsync("high-early", BaseTime.AddHours(2), Importance.High);

        var pending = await tasks.GetPendingAsync();

        // 重要程度升序（High 在前）→ 有截止时间的在前且早的更前 → 没有截止时间的压到最后。
        Assert.Equal(
            [
                "high-early",
                "high-late",
                "high-no-deadline",
                "medium-early",
                "medium-no-deadline",
                "low-no-deadline",
            ],
            pending.Select(static item => item.Id));
    }

    [Fact]
    public async Task GetPendingAsync_OrdersStablyWhenImportanceAndDeadlineMatch()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new FakeClock(BaseTime);
        var tasks = database.CreateTaskRepository(clock);

        async Task SaveItemAsync(string id, bool advanceClockFirst)
        {
            if (advanceClockFirst)
            {
                clock.Advance(TimeSpan.FromMinutes(1));
            }

            await tasks.SaveAsync(BuildItem(id));
        }

        // 先造一批 created_at 互不相同、id 故意乱序的行：应按入库时间排。
        await SaveItemAsync("test-action-b", advanceClockFirst: false);
        await SaveItemAsync("test-action-c", advanceClockFirst: true);
        await SaveItemAsync("test-action-a", advanceClockFirst: true);

        // 再造一批同一时刻入库、id 故意乱序的行：created_at 打平，只能靠 id 兜底。
        await SaveItemAsync("test-action-y", advanceClockFirst: true);
        await SaveItemAsync("test-action-x", advanceClockFirst: false);
        await SaveItemAsync("test-action-z", advanceClockFirst: false);

        var first = await tasks.GetPendingAsync();
        var second = await tasks.GetPendingAsync();

        Assert.Equal(
            ["test-action-b", "test-action-c", "test-action-a", "test-action-x", "test-action-y", "test-action-z"],
            first.Select(static item => item.Id));
        Assert.Equal(first.Select(static item => item.Id), second.Select(static item => item.Id));
    }

    [Fact]
    public async Task SetCompletedAsync_ChangesOnlyCompletedFlag()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var tasks = database.CreateTaskRepository(new FakeClock(BaseTime));

        var original = BuildItem(
            TaskId,
            action: "test-action-original",
            deadline: BaseTime.AddHours(1),
            importance: Importance.High,
            reason: "test-reason-original",
            sourceGroupId: TestGroupId);
        await tasks.SaveAsync(original);

        await tasks.SetCompletedAsync(TaskId, completed: true);

        // 整条记录相等：除 completed 外，其余列一个都不许被碰到。
        var stored = await GetRequiredAsync(tasks, TaskId);
        Assert.Equal(original with { Completed = true }, stored);
        Assert.True(stored.Completed);
        Assert.False(stored.Ignored);
    }

    [Fact]
    public async Task SetIgnoredAsync_ChangesOnlyIgnoredFlag()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var tasks = database.CreateTaskRepository(new FakeClock(BaseTime));

        var original = BuildItem(
            TaskId,
            action: "test-action-original",
            deadline: BaseTime.AddHours(1),
            importance: Importance.Low,
            reason: "test-reason-original",
            sourceGroupId: TestGroupId);
        await tasks.SaveAsync(original);

        await tasks.SetIgnoredAsync(TaskId, ignored: true);

        var stored = await GetRequiredAsync(tasks, TaskId);
        Assert.Equal(original with { Ignored = true }, stored);
        Assert.False(stored.Completed);
        Assert.True(stored.Ignored);
    }

    [Fact]
    public async Task SetCompletedAsync_CanAlsoUncheckCompleted()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var tasks = database.CreateTaskRepository(new FakeClock(BaseTime));

        await tasks.SaveAsync(BuildItem(TaskId));
        await tasks.SetCompletedAsync(TaskId, completed: true);
        await tasks.SetCompletedAsync(TaskId, completed: false);

        var stored = await GetRequiredAsync(tasks, TaskId);

        // 状态是"可写的窄列"而不是单向置位：取消勾选是合法操作，且只影响这一列。
        Assert.False(stored.Completed);
        Assert.Single(await tasks.GetPendingAsync());
    }

    [Fact]
    public async Task SetCompletedAsync_IsNoOpForUnknownId()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var tasks = database.CreateTaskRepository(new FakeClock(BaseTime));

        // 幂等：UI 重复点击或行已被清理时不应炸。
        await tasks.SetCompletedAsync("test-action-not-exist", completed: true);

        Assert.Null(await tasks.GetByIdAsync("test-action-not-exist"));
    }

    [Fact]
    public async Task SetIgnoredAsync_IsNoOpForUnknownId()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var tasks = database.CreateTaskRepository(new FakeClock(BaseTime));

        await tasks.SetIgnoredAsync("test-action-not-exist", ignored: true);

        Assert.Null(await tasks.GetByIdAsync("test-action-not-exist"));
    }

    [Fact]
    public async Task DeleteAsync_RemovesItemAndIsIdempotent()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var tasks = database.CreateTaskRepository(new FakeClock(BaseTime));

        await tasks.SaveAsync(BuildItem(TaskId));
        await tasks.SaveAsync(BuildItem("test-action-keep"));

        await tasks.DeleteAsync(TaskId);
        await tasks.DeleteAsync(TaskId);

        Assert.Null(await tasks.GetByIdAsync(TaskId));
        Assert.Equal(["test-action-keep"], (await tasks.GetPendingAsync()).Select(static item => item.Id));
    }

    [Fact]
    public async Task SaveAsync_StoresNullDeadlineAsSqlNull()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var tasks = database.CreateTaskRepository(new FakeClock(BaseTime));

        await tasks.SaveAsync(BuildItem(TaskId));

        Assert.Null((await GetRequiredAsync(tasks, TaskId)).Deadline);
        Assert.Null(await ReadColumnAsync(database, "deadline", TaskId));

        // 对照：非空 deadline 必须存成定宽 ISO-8601 文本，而不是别的格式或空串。
        await tasks.SaveAsync(BuildItem(TaskId, deadline: BaseTime));

        Assert.Equal(FormatTimestamp(BaseTime), await ReadColumnAsync(database, "deadline", TaskId));
        Assert.Equal(BaseTime, (await GetRequiredAsync(tasks, TaskId)).Deadline);
    }

    [Fact]
    public async Task Importance_ReadBack_DowngradesUndefinedStoredValuesToMedium()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var tasks = database.CreateTaskRepository(new FakeClock(BaseTime));

        // 绕开仓储的写入校验，直接改库造脏数据（仓储里 import 一枚举直接强转，什么都拦不住）。
        await InsertRawTaskAsync(database, "test-action-high", (int)Importance.High);
        await InsertRawTaskAsync(database, "test-action-dirty-high", 99);
        await InsertRawTaskAsync(database, "test-action-dirty-negative", -7);

        // 注意 SQLite 的 INTEGER 回来是 Int64，别拿 int 去比装箱后的值。
        Assert.Equal(99L, await ReadColumnAsync(database, "importance", "test-action-dirty-high"));
        Assert.Equal(Importance.Medium, (await GetRequiredAsync(tasks, "test-action-dirty-high")).Importance);
        Assert.Equal(Importance.Medium, (await GetRequiredAsync(tasks, "test-action-dirty-negative")).Importance);
        Assert.Equal(Importance.High, (await GetRequiredAsync(tasks, "test-action-high")).Importance);

        // 一行脏数据不该把整个待办列表读崩：三行都照常返回，且 Importance 全部是已定义的枚举值。
        // 注意这里不断言脏行的名次——排序式走的是 tasks.importance 的原始值（-7 排在最前、99 排在最后），
        // 降级只发生在读侧的 C# 映射里，与类注释里"避免被顶到列表最前面"的意图并不完全一致。
        var pending = await tasks.GetPendingAsync();

        Assert.Equal(
            ["test-action-dirty-negative", "test-action-high", "test-action-dirty-high"],
            pending.Select(static item => item.Id));
        // net48 没有泛型 Enum.IsDefined（net5+）：显式传 Type，语义与 item.Importance 一致。
        Assert.All(pending, static item => Assert.True(Enum.IsDefined(typeof(Importance), item.Importance)));
    }

    [Fact]
    public async Task SaveAsync_RejectsNullItem()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var tasks = database.CreateTaskRepository(new FakeClock(BaseTime));

        await Assert.ThrowsAsync<ArgumentNullException>(() => tasks.SaveAsync(null!));
    }

    [Fact]
    public async Task IdArguments_RejectBlankIds()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var tasks = database.CreateTaskRepository(new FakeClock(BaseTime));

        await Assert.ThrowsAsync<ArgumentException>(() => tasks.GetByIdAsync("  "));
        await Assert.ThrowsAsync<ArgumentException>(() => tasks.SetCompletedAsync(" ", completed: true));
        await Assert.ThrowsAsync<ArgumentException>(() => tasks.SetIgnoredAsync(string.Empty, ignored: true));
        await Assert.ThrowsAsync<ArgumentException>(() => tasks.DeleteAsync("\t"));
    }

    // ---------------------------------------------------------------------
    // 第四组：整批写入（全成功 / 全回滚），交接文档 P1-3 的护栏
    // ---------------------------------------------------------------------

    [Fact]
    public async Task SaveAllAsync_WritesEveryItemInOneCall()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var tasks = database.CreateTaskRepository(new FakeClock(BaseTime));

        await tasks.SaveAllAsync(
        [
            BuildItem("test-action-a", importance: Importance.High),
            BuildItem("test-action-b", deadline: BaseTime.AddHours(1)),
            BuildItem("test-action-c", completed: true),
        ]);

        Assert.Equal(
            ["test-action-a", "test-action-b"],
            (await tasks.GetPendingAsync()).Select(static item => item.Id));
        Assert.True((await GetRequiredAsync(tasks, "test-action-c")).Completed);
    }

    [Fact]
    public async Task SaveAllAsync_EmptyListIsANoOp()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var tasks = database.CreateTaskRepository(new FakeClock(BaseTime));

        await tasks.SaveAllAsync([]);

        Assert.Empty(await tasks.GetPendingAsync());
    }

    [Fact]
    public async Task SaveAllAsync_RollsBackEveryItemWhenOneFails()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var tasks = database.CreateTaskRepository(new FakeClock(BaseTime));

        // 故障注入：让指定 ID 的写入必定失败，且失败发生在前几条已经写进事务之后。
        // RAISE(ABORT) 只回滚"当前语句"，事务本身不会自动结束——所以能不能救回前面那几条，
        // 取决于仓储有没有自己发 ROLLBACK，这正是这条用例要证明的事。
        await database.ExecuteAsync($$"""
            CREATE TRIGGER tasks_reject_injected
            BEFORE INSERT ON tasks
            WHEN NEW.id = 'test-action-boom'
            BEGIN
                SELECT RAISE(ABORT, 'injected failure');
            END;
            """);

        // 预先存在的行也必须原样保留：整批回滚不能把老数据一起带走。
        await tasks.SaveAsync(BuildItem("test-action-existing", action: "test-action-before-batch"));

        await Assert.ThrowsAsync<SqliteException>(
            () => tasks.SaveAllAsync(
            [
                BuildItem("test-action-first"),
                BuildItem("test-action-boom"),
                BuildItem("test-action-last"),
            ]));

        // 三个新行一个都没留下——部分写入在结构上不可能发生。
        Assert.Null(await tasks.GetByIdAsync("test-action-first"));
        Assert.Null(await tasks.GetByIdAsync("test-action-boom"));
        Assert.Null(await tasks.GetByIdAsync("test-action-last"));

        // 老行仍在，且内容未被这批回滚波及。
        var existing = await tasks.GetByIdAsync("test-action-existing");
        Assert.NotNull(existing);
        Assert.Equal("test-action-before-batch", existing.Action);

        // 对照：撤掉注入故障后，同一批必须整批进去。
        // 没有这一步，"三行都不在"也可能只是因为循环压根没写——那样的实现同样能让断言变绿。
        await database.ExecuteAsync("DROP TRIGGER tasks_reject_injected;");

        await tasks.SaveAllAsync(
        [
            BuildItem("test-action-first"),
            BuildItem("test-action-boom"),
            BuildItem("test-action-last"),
        ]);

        // 四行都在库里；排序键（importance/deadline/created_at）全部相同，落到 id 字典序。
        Assert.Equal(
            ["test-action-boom", "test-action-existing", "test-action-first", "test-action-last"],
            (await tasks.GetPendingAsync()).Select(static item => item.Id));
    }

    [Fact]
    public async Task SaveAllAsync_KeepsUserStatusLikeSaveAsync()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var tasks = database.CreateTaskRepository(new FakeClock(BaseTime));

        await tasks.SaveAllAsync([BuildItem(TaskId, completed: true)]);
        await tasks.SetCompletedAsync(TaskId, completed: true);

        // 整批路径不能是"另一套写入语义"：用户已经勾掉的状态同样要活下来。
        await tasks.SaveAllAsync([BuildItem(TaskId, action: "test-action-refreshed")]);

        var stored = await GetRequiredAsync(tasks, TaskId);

        Assert.Equal("test-action-refreshed", stored.Action);
        Assert.True(stored.Completed);
    }

    [Fact]
    public async Task SaveAllAsync_RejectsNullListAndNullItemWithoutPartialWrite()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var tasks = database.CreateTaskRepository(new FakeClock(BaseTime));

        await Assert.ThrowsAsync<ArgumentNullException>(() => tasks.SaveAllAsync(null!));

        // 校验发生在事务之前，因此一条非法条目不能让同批的合法条目先进库。
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => tasks.SaveAllAsync([BuildItem("test-action-ok"), null!]));

        Assert.Empty(await tasks.GetPendingAsync());
    }

    // ---------------------------------------------------------------------
    // 夹具与辅助
    // ---------------------------------------------------------------------

    /// <summary>造一条行动项；除少数用例外，业务字段用固定的假值，避免断言里出现随机内容。</summary>
    private static ActionItem BuildItem(
        string id,
        string action = "test-action-default",
        DateTime? deadline = null,
        Importance importance = Importance.Medium,
        string reason = "test-reason-default",
        string sourceGroupId = TestGroupId,
        bool completed = false,
        bool ignored = false) => new()
        {
            Id = id,
            Action = action,
            Deadline = deadline,
            Importance = importance,
            Reason = reason,
            SourceGroupId = sourceGroupId,
            Completed = completed,
            Ignored = ignored,
        };

    /// <summary>取回一条行动项；为 null 时直接失败，好过让后续断言报一个看不懂的 NRE。</summary>
    private static async Task<ActionItem> GetRequiredAsync(TaskRepository repository, string id)
    {
        var stored = await repository.GetByIdAsync(id);

        return stored ?? throw new XunitException($"期望取回行动项 '{id}'，实际为 null。");
    }

    /// <summary>屏障：让续体离开完成线程被唤醒，强制真正的并发交错而不是同线程顺序执行。</summary>
    /// <remarks>
    /// 必须带 <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>：
    /// 缺了它续体会内联在 <c>SetResult</c> 的线程上跑，交错顺序塌成同线程顺序执行，
    /// 屏障就白设了，这两条用例会从"确定性回归"退化成"偶尔绿"。
    /// net48 的 <c>TaskCompletionSource</c> 只有泛型版，故取 <c>bool</c> 占位。
    /// </remarks>
    private static TaskCompletionSource<bool> NewBarrier() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 带超时兜底地等一个任务：超时抛 <see cref="TimeoutException"/>。
    /// </summary>
    /// <remarks>
    /// net48 没有 <c>Task.WaitAsync(TimeSpan)</c>，语义上等价的替代品就是
    /// <c>Task.WhenAny(任务, Task.Delay(超时))</c>：<c>WhenAny</c> 本身永不失败，
    /// 所以还要把原任务重新 await 一次，才能把它的真实异常原样抛出去。
    /// </remarks>
    private static async Task WaitWithinGuardAsync(Task task)
    {
        // 任务先完成时取消掉计时器，免得空等满 ConcurrencyGuard。
        // 取消出来的 Task 是 Canceled 而非 Faulted，不触发 UnobservedTaskException。
        using var timeout = new CancellationTokenSource();
        Task expired = Task.Delay(ConcurrencyGuard, timeout.Token);

        if (await Task.WhenAny(task, expired) == task)
        {
            timeout.Cancel();
            await task;
            return;
        }

        throw new TimeoutException($"并发用例等待超过 {ConcurrencyGuard}。");
    }

    /// <summary>并发发起若干条写入，并留一个超时兜底。</summary>
    private static async Task RunConcurrentlyAsync(params Func<Task>[] operations)
    {
        await WaitWithinGuardAsync(
            Task.WhenAll(operations.Select(static operation => Task.Run(operation))));
    }

    /// <summary>
    /// 绕开仓储直读 <c>tasks</c> 的一列。仓储自己的读路径会做格式转换与降级，
    /// 只有直读才能断言"列里到底存了什么"（例如 <c>deadline</c> 是真正的 SQL NULL 还是空串）。
    /// </summary>
    /// <param name="database">临时数据库。</param>
    /// <param name="column">列名，由测试代码写死，不是外部输入。</param>
    /// <param name="id">目标行。</param>
    private static async Task<object?> ReadColumnAsync(SqliteTestDatabase database, string column, string id)
    {
        using SqliteConnection connection = await database.Connections.OpenAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM tasks WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        var value = await command.ExecuteScalarAsync();

        return value is DBNull ? null : value;
    }

    /// <summary>用一条直连批量写入造脏数据行（这里只有一行），只用于测读取侧的降级。</summary>
    private static Task InsertRawTaskAsync(
        SqliteTestDatabase database,
        string id,
        int importance,
        bool completed = false,
        bool ignored = false)
    {
        var rows = new[]
        {
            new object[]
            {
                id,
                "test-action-raw",
                DBNull.Value,
                importance,
                "test-reason-raw",
                TestGroupId,
                completed ? 1 : 0,
                ignored ? 1 : 0,
                FormatTimestamp(BaseTime),
                FormatTimestamp(BaseTime),
            },
        };

        return database.InsertRowsAsync(
            "tasks",
            "id, action, deadline, importance, reason, source_group_id, completed, ignored, created_at, updated_at",
            rows);
    }

    /// <summary>与 <c>SqliteTime.ToStorage</c> 相同的定宽 ISO-8601 UTC 文本。</summary>
    private static string FormatTimestamp(DateTime value) => value.ToString("O", CultureInfo.InvariantCulture);
}
