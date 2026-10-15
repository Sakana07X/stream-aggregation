namespace StreamingAggregation.Infrastructure.OneBot;

/// <summary>
/// 读取 OneBot 访问令牌时发生异常。
/// </summary>
/// <remarks>
/// <para>
/// 令牌读取失败属于「不知道令牌」而不是「没有令牌」，两者语义完全不同：
/// 后者是合法状态（本地 SnowLuma 可能不配 access token，匿名连接即可），
/// 前者意味着密钥库损坏、DPAPI 上下文不对或 IO 失败——此时若悄悄退化成匿名连接，
/// 连接多半会被服务端拒绝，而日志里只剩一句「握手失败」，用户无从判断真因。
/// 因此异常路径一律 fail-closed：中止本次连接，并把可读的原因上报到连接状态。
/// </para>
/// <para>
/// <b>脱敏契约</b>：消息由本类型拼装，调用方无法传入自定义文案，
/// 因此 <see cref="Exception.Message"/> 在结构上就不可能包含令牌值。
/// 同样<strong>不</strong>把原始异常挂到 <see cref="Exception.InnerException"/>——
/// 那会让 <see cref="Exception.ToString"/> 原样带出第三方 <c>ISecretStore</c> 的文案，
/// 与 <see cref="OneBotWebSocketClient"/>「令牌绝不写入日志或 ToString」的约定冲突。
/// 需要的信息保留在 <see cref="SourceType"/> 里：类型名足以区分 DPAPI、IO 与自定义故障。
/// </para>
/// </remarks>
public sealed class OneBotAccessTokenException : Exception
{
    /// <summary>异常消息前缀；不含任何凭据，可安全进入日志与界面。</summary>
    public const string Reason =
        "读取 OneBot 访问令牌失败，已中止本次连接（不会退化为不带 Authorization 头的匿名连接）。";

    /// <summary>创建异常。</summary>
    /// <param name="innerException">令牌提供委托抛出的原始异常；只取其类型用于排障，不保留实例。</param>
    public OneBotAccessTokenException(Exception? innerException = null)
        : base(BuildMessage(innerException))
    {
        SourceType = innerException?.GetType().FullName;
    }

    /// <summary>
    /// 令牌提供委托抛出的异常类型全名；无原始异常时为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 只保留类型、不保留实例，是为了让排障能区分「DPAPI 解密失败」与「密钥文件读不了」，
    /// 同时避免原始文案把令牌带进 <see cref="Exception.ToString"/>。
    /// </remarks>
    public string? SourceType { get; }

    private static string BuildMessage(Exception? innerException) =>
        innerException is null ? Reason : $"{Reason}（原始异常类型：{innerException.GetType().Name}）";
}