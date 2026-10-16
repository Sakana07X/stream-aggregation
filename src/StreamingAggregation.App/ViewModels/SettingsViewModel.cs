using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Core.Configuration;
using StreamingAggregation.Infrastructure.ViewModels;

namespace StreamingAggregation.App.ViewModels;

/// <summary>
/// 设置页绑定：读写 <see cref="AppSettings"/>,并把校验结果翻译成界面提示。
/// </summary>
/// <remarks>
/// <para>
/// <b>敏感字段</b>：Token 与 API Key 不做特殊处理，直接交给 <see cref="ISettingsStore"/>。
/// SQLite 实现已经知道把这两个值写进 <see cref="ISecretStore"/>、并在 <c>app_settings</c>
/// 里只留空串占位；视图模型再自己加密一遍只会造成两套真相。
/// </para>
/// <para>
/// <b>保存成功后</b>：白名单、窗口时长、连接地址都可能已经变了，需要重建聚合管线并重连。
/// 这些动作属于应用编排，因此本类只负责"存好了"这一件事通知一声
/// （<see cref="Saved"/>），具体怎么做交给组合根。
/// </para>
/// </remarks>
public sealed class SettingsViewModel : ObservableObject
{
    /// <summary>端口合法区间。</summary>
    public const int MinPort = 1;

    /// <summary>端口合法区间。</summary>
    public const int MaxPort = 65535;

    /// <summary>聚合窗口时长上限（分钟），与 <c>AggregationOptions</c> 的上限保持一致。</summary>
    public const int MaxWindowMinutes = 24 * 60;

    /// <summary>保留天数下限。</summary>
    public const int MinRetentionDays = 1;

    private readonly ISettingsStore _settingsStore;

    private string _host = "127.0.0.1";
    private double _port = 3001;
    private string _token = string.Empty;
    private bool _useSsl;
    private string _whitelistText = string.Empty;
    private string _endpoint = string.Empty;
    private string _apiKey = string.Empty;
    private string _model = string.Empty;
    private double _timeoutSeconds = 30;
    private double _windowMinutes = 3;
    private double _retentionDays = 30;
    private bool _isBusy;
    private string _statusMessage = string.Empty;
    private string _errorMessage = string.Empty;

    /// <summary>
    /// 创建设置视图模型。
    /// </summary>
    /// <param name="settingsStore">设置存储。</param>
    /// <exception cref="ArgumentNullException"><paramref name="settingsStore"/> 为 <see langword="null"/>。</exception>
    public SettingsViewModel(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    }

    /// <summary>设置保存成功后触发，参数为真正落库的设置快照。</summary>
    public event EventHandler<AppSettings>? Saved;

    /// <summary>OneBot 主机。</summary>
    public string Host
    {
        get => _host;
        set => SetProperty(ref _host, value);
    }

    /// <summary>OneBot 端口。<c>NumberBox</c> 可能给出 <see cref="double.NaN"/>，读取时统一兜底。</summary>
    public double Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    /// <summary>OneBot 访问令牌。</summary>
    public string Token
    {
        get => _token;
        set => SetProperty(ref _token, value);
    }

    /// <summary>是否使用 TLS。</summary>
    public bool UseSsl
    {
        get => _useSsl;
        set => SetProperty(ref _useSsl, value);
    }

    /// <summary>群白名单，每行一个群 ID。</summary>
    public string WhitelistText
    {
        get => _whitelistText;
        set => SetProperty(ref _whitelistText, value);
    }

    /// <summary>OpenAI 兼容接口地址。</summary>
    public string Endpoint
    {
        get => _endpoint;
        set => SetProperty(ref _endpoint, value);
    }

    /// <summary>模型 API Key。</summary>
    public string ApiKey
    {
        get => _apiKey;
        set => SetProperty(ref _apiKey, value);
    }

    /// <summary>模型名称。</summary>
    public string Model
    {
        get => _model;
        set => SetProperty(ref _model, value);
    }

    /// <summary>单次模型调用超时（秒）。</summary>
    public double TimeoutSeconds
    {
        get => _timeoutSeconds;
        set => SetProperty(ref _timeoutSeconds, value);
    }

    /// <summary>聚合窗口时长（分钟）。</summary>
    public double WindowMinutes
    {
        get => _windowMinutes;
        set => SetProperty(ref _windowMinutes, value);
    }

    /// <summary>消息保留天数。</summary>
    public double RetentionDays
    {
        get => _retentionDays;
        set => SetProperty(ref _retentionDays, value);
    }

    /// <summary>是否正在读写存储。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>是否可以保存：空闲且当前取值合法时为 <see langword="true"/>。</summary>
    public bool CanSave => !_isBusy && Validate().Count == 0;

    /// <summary>
    /// 补发 <see cref="CanSave"/> 的变更通知。
    /// </summary>
    /// <remarks>
    /// <see cref="CanSave"/> 由"是否忙碌"和"各字段是否合法"共同算出，而这两个输入
    /// 会随任意一个字段变化，因此统一在这里补一次通知，省去在每个属性上重复处理。
    /// </remarks>
    /// <param name="propertyName">变化了的属性名。</param>
    protected override void OnPropertyChanged(string? propertyName)
    {
        base.OnPropertyChanged(propertyName);

        if (propertyName is not nameof(CanSave))
        {
            OnPropertyChanged(nameof(CanSave));
        }
    }

    /// <summary>最近一次操作的结果提示。</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>最近一次操作的错误提示；无错误时为空串。</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>
    /// 从存储读取设置并填充界面。
    /// </summary>
    /// <remarks>
    /// 会逐个触发属性变更通知，因此<b>必须在 UI 线程上调用</b>；组合根通过 UI 调度队列封送后调用。
    /// </remarks>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;

        try
        {
            var settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            Apply(settings);
            ErrorMessage = string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorMessage = $"读取设置失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 校验并保存当前界面上的设置。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>保存成功返回 <see langword="true"/>；校验或写入失败返回 <see langword="false"/>。</returns>
    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            ErrorMessage = string.Join(Environment.NewLine, errors);
            StatusMessage = string.Empty;
            return false;
        }

        IsBusy = true;

        try
        {
            var settings = Compose();
            await _settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
            ErrorMessage = string.Empty;
            StatusMessage = "设置已保存。";
            Saved?.Invoke(this, settings);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorMessage = $"保存设置失败：{ex.Message}";
            StatusMessage = string.Empty;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>把存储中的设置填入界面字段。</summary>
    /// <param name="settings">设置快照。</param>
    public void Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Host = settings.OneBotConnection.Host;
        Port = settings.OneBotConnection.Port;
        Token = settings.OneBotConnection.Token;
        UseSsl = settings.OneBotConnection.UseSsl;
        WhitelistText = string.Join(Environment.NewLine, settings.GroupWhitelist.GroupIds);
        Endpoint = settings.Model.Endpoint;
        ApiKey = settings.Model.ApiKey;
        Model = settings.Model.Model;
        TimeoutSeconds = settings.Model.TimeoutSeconds;
        WindowMinutes = settings.Aggregation.WindowMinutes;
        RetentionDays = settings.Aggregation.RetentionDays;
    }

    /// <summary>把界面字段组装成设置对象。</summary>
    /// <returns>设置快照。</returns>
    public AppSettings Compose() => new()
    {
        OneBotConnection = new OneBotConnectionSettings
        {
            Host = Host?.Trim() ?? string.Empty,
            Port = ToInt32(Port, 3001),
            Token = Token ?? string.Empty,
            UseSsl = UseSsl,
        },
        Model = new ModelSettings
        {
            Endpoint = Endpoint?.Trim() ?? string.Empty,
            ApiKey = ApiKey ?? string.Empty,
            Model = Model?.Trim() ?? string.Empty,
            TimeoutSeconds = ToInt32(TimeoutSeconds, 30),
        },
        Aggregation = new AggregationSettings
        {
            WindowMinutes = ToInt32(WindowMinutes, 3),
            RetentionDays = ToInt32(RetentionDays, 30),
        },
        GroupWhitelist = new GroupWhitelist { GroupIds = ParseWhitelist(WhitelistText) },
    };

    /// <summary>把白名单文本拆成群 ID 列表：按行切、去空白、丢空行、去重。</summary>
    /// <param name="text">每行一个群 ID 的文本。</param>
    /// <returns>群 ID 列表。</returns>
    public static IReadOnlyList<string> ParseWhitelist(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        return text
            .Split(['\r', '\n', ',', '，', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>校验界面上的取值，返回全部错误描述（空列表表示通过）。</summary>
    /// <returns>错误描述列表。</returns>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Host))
        {
            errors.Add("SnowLuma 主机不能为空。");
        }

        var port = ToInt32(Port, 0);
        if (port is < MinPort or > MaxPort)
        {
            errors.Add($"端口必须在 {MinPort} 到 {MaxPort} 之间。");
        }

        if (ToInt32(TimeoutSeconds, 0) < 1)
        {
            errors.Add("模型超时必须至少 1 秒。");
        }

        var window = ToInt32(WindowMinutes, 0);
        if (window is < 1 or > MaxWindowMinutes)
        {
            errors.Add($"聚合窗口必须在 1 到 {MaxWindowMinutes} 分钟之间。");
        }

        if (ToInt32(RetentionDays, 0) < MinRetentionDays)
        {
            errors.Add($"保留天数必须至少 {MinRetentionDays} 天。");
        }

        // 传输策略：非回环（远程）连接必须走 TLS。
        // EndpointTransportPolicy 失败即收紧：无法确认回环时按远程处理。
        if (!EndpointTransportPolicy.IsLoopback(Host) && !UseSsl)
        {
            errors.Add("远程连接必须使用 WSS。");
        }

        var endpoint = Endpoint ?? string.Empty;
        if (endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !EndpointTransportPolicy.IsLoopback(EndpointHost(endpoint)))
        {
            errors.Add("远程端点必须使用 HTTPS。");
        }

        return errors;
    }

    /// <summary>
    /// 从模型端点地址中取出主机名，供 <see cref="EndpointTransportPolicy"/> 判定回环。
    /// </summary>
    /// <remarks>
    /// 端点无法解析为绝对 URI 时返回原串——传输策略对无法解析的输入按远程处理，
    /// 与安全收紧语义保持一致。
    /// </remarks>
    /// <param name="endpoint">模型端点地址。</param>
    /// <returns>主机名；无法解析时为原串。</returns>
    private static string EndpointHost(string endpoint)
    {
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return endpoint;
    }

    /// <summary>
    /// 把 <c>NumberBox</c> 交来的 <see cref="double"/> 转成整数。
    /// </summary>
    /// <remarks>
    /// 用户把输入框清空时 <c>NumberBox.Value</c> 是 <see cref="double.NaN"/>，
    /// 直接强转会得到 <see cref="int.MinValue"/>，因此必须显式处理。
    /// </remarks>
    /// <param name="value">界面值。</param>
    /// <param name="fallback">值非法时使用的兜底值。</param>
    /// <returns>整数值。</returns>
    private static int ToInt32(double value, int fallback)
    {
        if (!double.IsFinite(value))
        {
            return fallback;
        }

        var rounded = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        return rounded < int.MinValue || rounded > int.MaxValue ? fallback : rounded;
    }
}