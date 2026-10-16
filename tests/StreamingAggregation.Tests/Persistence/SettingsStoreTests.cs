using StreamingAggregation.Core.Configuration;
using StreamingAggregation.Infrastructure.Persistence;

namespace StreamingAggregation.Tests.Persistence;

/// <summary>
/// <c>SettingsStore</c> 的首启检测三组行为：
/// <list type="number">
/// <item>空库（迁移后无任何行）→ <c>HasStoredSettingsAsync</c> 返回 <see langword="false"/>；</item>
/// <item>调用一次 <c>SaveAsync</c> → 返回 <see langword="true"/>；</item>
/// <item><c>MarkWelcomeDismissedAsync</c> 在空库上写入标记行 → 返回 <see langword="true"/>，
/// 且后续 <c>SaveAsync</c> 不会把标记冲掉。</item>
/// </list>
/// </summary>
/// <remarks>
/// 这组测试守住的是首启遮罩层的判据基础：如果 <c>HasStoredSettingsAsync</c> 在空库上
/// 返回 <see langword="true"/>，遮罩层永远不会出现；如果它在有行时返回 <see langword="false"/>，
/// 用户每次启动都会被欢迎页挡住。
/// </remarks>
public sealed class SettingsStoreTests
{
    [Fact]
    public async Task HasStoredSettingsAsync_ReturnsFalse_OnEmptyDatabase()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var store = new SettingsStore(database.Connections);

        var hasStored = await store.HasStoredSettingsAsync();

        Assert.False(hasStored, "空库（迁移后无行）应返回 false，否则首启遮罩层永远不会出现。");
    }

    [Fact]
    public async Task HasStoredSettingsAsync_ReturnsTrue_AfterSaveAsync()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var store = new SettingsStore(database.Connections);

        await store.SaveAsync(new AppSettings());

        var hasStored = await store.HasStoredSettingsAsync();
        Assert.True(hasStored, "SaveAsync 写入首行后应返回 true。");
    }

    [Fact]
    public async Task MarkWelcomeDismissedAsync_WritesFlagRow_OnEmptyDatabase()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var store = new SettingsStore(database.Connections);

        await store.MarkWelcomeDismissedAsync();

        // 标记写入后表里有了行，HasStoredSettingsAsync 随之返回 true——
        // 这正是"稍后再说后二次启动不再弹遮罩"的实现基础。
        var hasStored = await store.HasStoredSettingsAsync();
        Assert.True(hasStored, "MarkWelcomeDismissedAsync 应写入一行，使二次启动不再判定为首启。");
    }

    [Fact]
    public async Task SaveAsync_DoesNotClearWelcomeDismissedFlag()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var store = new SettingsStore(database.Connections);

        await store.MarkWelcomeDismissedAsync();
        await store.SaveAsync(new AppSettings());

        // SaveAsync 的 UPSERT 不触碰 welcome_dismissed 列，标记必须保持。
        var hasStored = await store.HasStoredSettingsAsync();
        Assert.True(hasStored, "SaveAsync 不应把 welcome_dismissed 标记冲掉。");
    }
}
