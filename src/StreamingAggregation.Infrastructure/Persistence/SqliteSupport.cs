using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StreamingAggregation.Core.Abstractions;

namespace StreamingAggregation.Infrastructure.Persistence;

/// <summary>
/// 时间与 SQLite 存储格式之间的转换。
/// </summary>
/// <remarks>
/// 统一使用定宽 ISO-8601 UTC 文本（<c>DateTime.ToString("O")</c>）：
/// 字典序等价于时间序，可直接用 SQL 的 <c>&lt;</c> / <c>&gt;</c> 和 ORDER BY，
/// 也让数据库文件在排障时肉眼可读。
/// </remarks>
internal static class SqliteTime
{
    internal static string ToStorage(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            // Kind 未指定时按 UTC 处理：直接换算会在 DateTime.MinValue 这类边界上越界。
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

        return utc.ToString("O", CultureInfo.InvariantCulture);
    }

    internal static DateTime FromStorage(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

/// <summary>SQLite 命令与参数的常用写法收敛在此，避免每个仓储重复拼字符串。</summary>
internal static class SqliteCommandExtensions
{
    /// <summary>绑定 <c>$name</c> 参数；null 统一转成 <see cref="DBNull.Value"/>。</summary>
    internal static SqliteCommand Bind(this SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    internal static string GetRequiredString(this SqliteDataReader reader, int ordinal) =>
        reader.GetString(ordinal);

    internal static DateTime GetDateTime(this SqliteDataReader reader, int ordinal) =>
        SqliteTime.FromStorage(reader.GetString(ordinal));

    /// <summary>把 TEXT 列里的 JSON 数组读成字符串列表；空串或非法 JSON 视为空列表。</summary>
    internal static IReadOnlyList<string> GetStringList(this SqliteDataReader reader, int ordinal)
    {
        var json = reader.GetString(ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    internal static string ToJsonArray(IEnumerable<string> values) => JsonSerializer.Serialize(values.ToArray());
}

/// <summary>
/// "最近 N 条"这类列表查询的行数约定。仓储共用，避免每个仓储各写一套边界。
/// </summary>
internal static class QueryLimit
{
    /// <summary>下限。</summary>
    internal const int Min = 1;

    /// <summary>上限：够 UI 一次渲染一个页面，又不至于把大表拖进内存。</summary>
    internal const int Max = 1000;

    /// <summary>
    /// 把调用方传入的行数钳制到 <c>[Min, Max]</c>。
    /// </summary>
    /// <remarks>
    /// 必须钳制后再绑定参数，不能把原值直接丢给 SQL：SQLite 把<b>负数</b> <c>LIMIT</c>
    /// 解释为"不限制"（传 -1 会把整张表拉进内存），而 <c>0</c> 会返回空列表——
    /// 两种都不是"给我最近几条"想要的语义。钳制让边界输入退化成"取一条"，
    /// 既不会炸内存，也不会让调用方误以为库里真的没有数据。
    /// </summary>
    /// <remarks>
    /// net48 没有 <c>Math.Clamp</c>（.NET Core 2.0+），这里手写等价的三段比较：
    /// 小于下界取下界，大于上界取上界，区间内原样返回。
    /// </remarks>
    internal static int Clamp(int limit) =>
        limit < Min ? Min
        : limit > Max ? Max
        : limit;
}

/// <summary>
/// 生产环境默认时钟：以 UTC 读取当前时间。
/// </summary>
/// <remarks>
/// 公开是为了让应用层的组合根能把它登记成 <see cref="IClock"/>——仓储内部继续沿用
/// 默认值即可，无需显式传入。测试请用假时钟覆盖，不要直接改这个类。
/// </remarks>
public sealed class SystemClock : IClock
{
    /// <summary>共享实例。无状态，直接复用。</summary>
    public static SystemClock Instance { get; } = new();

    /// <inheritdoc />
    public DateTime Now => DateTime.UtcNow;
}