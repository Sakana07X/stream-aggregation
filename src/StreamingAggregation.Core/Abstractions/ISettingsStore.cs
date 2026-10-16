using StreamingAggregation.Core.Configuration;

namespace StreamingAggregation.Core.Abstractions;

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// 检测数据库中是否已存在设置行（即用户是否至少保存过一次设置）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>app_settings</c> 是单行表，但迁移只建表不插行——只有 <see cref="SaveAsync"/>
    /// 才会写入第一行。因此"从未保存过设置"这条状态可以用一条
    /// <c>SELECT EXISTS</c> 精确检测，不需要任何启发式判断。
    /// </para>
    /// <para>
    /// 这是首启遮罩层的第一判据：<c>HasStoredSettingsAsync</c> 返回 <see langword="false"/>
    /// 说明是首次运行，应当引导用户去设置页配置。
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>存在设置行返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    Task<bool> HasStoredSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 把"欢迎页已看过"标记写入数据库。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是首启遮罩层的第二判据：用户在欢迎页点「稍后再说」后，应用必须在下次启动时
    /// 还记得这个选择，否则每次启动都会重新弹出欢迎页。
    /// </para>
    /// <para>
    /// 实现上采用 UPSERT：数据库里已有设置行时只更新标记列，其他列保持不变；
    /// 还没有设置行时插入一行仅含默认值 + 标记的行。后续 <see cref="SaveAsync"/>
    /// 的 UPSERT 不会触碰此列，标记不会被覆盖。
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">取消令牌。</param>
    Task MarkWelcomeDismissedAsync(CancellationToken cancellationToken = default);
}
