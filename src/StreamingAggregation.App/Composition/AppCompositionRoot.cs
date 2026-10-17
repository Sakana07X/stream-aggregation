using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using StreamingAggregation.App.Services;
using StreamingAggregation.App.ViewModels;
using StreamingAggregation.Core;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Core.Aggregation;
using StreamingAggregation.Core.Configuration;
using StreamingAggregation.Core.Diagnostics;
using StreamingAggregation.Core.Filtering;
using StreamingAggregation.Infrastructure;
using StreamingAggregation.Infrastructure.Aggregation;
using StreamingAggregation.Infrastructure.Llm;
using StreamingAggregation.Infrastructure.OneBot;
using StreamingAggregation.Infrastructure.Persistence;
using StreamingAggregation.Infrastructure.Pipeline;
using StreamingAggregation.Infrastructure.Security;
using StreamingAggregation.Infrastructure.ViewModels;

namespace StreamingAggregation.App.Composition;

/// <summary>
/// 组合根：把 T8–T13 的具体实现注册成 Core 接口，构造 T14 页面与 T15 托盘，并编排启动顺序。
/// </summary>
/// <remarks>
/// <para>
/// <b>唯一的组装点</b>：应用里不允许出现 <c>new</c> 出来的仓储、客户端或过滤器。
/// 新增依赖时在这里登记一次，其余代码只认接口。
/// </para>
/// <para>
/// <b>注册表</b>：<see cref="ServiceRegistry"/> 同时承担两件事——运行期按接口解析实例，
/// 以及启动时自检"每个 Core 接口是否都有实现"。后者靠 <see cref="Registrations"/> 与
/// <see cref="EnsureCoreInterfacesRegistered"/> 完成：漏注册会在启动阶段立刻失败，
/// 而不是等到某条消息进来才空引用。
/// </para>
/// <para>
/// <b>启动顺序（连接先起、窗口后开）</b>：先做迁移与设置加载，再把消息管线拉起来，
/// 最后才把窗口送到 UI 线程激活。反过来做的话，用户会先看到一个空壳界面，
/// 误以为功能不可用；而连接失败时，托盘里的状态提示才有东西可显示。
/// </para>
/// <para>
/// <b>设置变更</b>：白名单、窗口长度、连接地址都参与已建对象的构造，无法热改。
/// 保存设置后由 <see cref="HandleSettingsChanged"/> 重建事件源与聚合窗口并重连，
/// 仓储、密钥库、摘要客户端等与设置无关的部分保持复用。
/// </para>
/// </remarks>
public sealed class AppCompositionRoot : IDisposable
{
    private readonly ServiceRegistry _services = new();
    private readonly HttpClient _httpClient;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<Action, bool>? _marshalToUi;

    private AppSettings _settings = new();
    private CancellationTokenSource? _connectionScope;
    private OneBotWebSocketClient? _eventSource;
    private Task? _pipelineTask;
    private MainWindow? _mainWindow;
    private TrayIconService? _tray;
    private bool _disposed;

    /// <summary>
    /// 创建组合根并完成全部服务登记（不启动任何后台工作）。
    /// </summary>
    /// <param name="marshalToUi">
    /// 把一段动作搬到 UI 线程执行的封送委托（WPF <c>Dispatcher.BeginInvoke</c> 语义：
    /// 永不内联，调度器已关闭时返回 <see langword="false"/>）。
    /// 为 <see langword="null"/> 时就地执行——组合根的单元测试不需要真实消息循环。
    /// </param>
    public AppCompositionRoot(Func<Action, bool>? marshalToUi = null)
    {
        _marshalToUi = marshalToUi;

        // 超时由 OpenAiCompatibleClient 按 ModelSettings.TimeoutSeconds 单独计时，
        // 这里给无限值，避免 HttpClient 的 100 秒默认值反过来截断更长的合法超时。
        _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        RegisterPersistenceServices();
        RegisterAggregationServices();
        RegisterNotificationServices();
    }

    /// <summary>当前生效的设置快照。</summary>
    public AppSettings Settings => _settings;

    /// <summary>服务注册表快照：服务接口 → 实现类型（同一接口可能有多个实现）。</summary>
    public IReadOnlyDictionary<Type, IReadOnlyList<Type>> Registrations => _services.Registrations;

    /// <summary>连接状态绑定。</summary>
    public ConnectionStatusViewModel ConnectionStatus { get; private set; } = null!;

    /// <summary>设置页绑定。</summary>
    public SettingsViewModel SettingsViewModel { get; private set; } = null!;

    /// <summary>首启状态绑定。</summary>
    public FirstRunState FirstRun { get; private set; } = null!;

    /// <summary>暂停/恢复开关，托盘与管线共用同一份状态。</summary>
    public PauseResumeService PauseResume { get; } = new();

    // 【阶段 2】原来的 MainWindowRoot（XamlRoot）已整体删除：
    // 它唯一的用途是让启动早期的 ContentDialog 找到可挂载的界面根，而 WPF 版致命错误
    // 统一走 Win32 MessageBox（见 FatalErrorDialog），不再需要界面根。
    // 主窗口的持有者始终是下面的 _mainWindow 字段，不要为它另开公开访问口——
    // 那样等于允许调用方在窗口还没建好时就去碰界面元素。

    /// <summary>
    /// 按"连接先起、窗口后开"的顺序启动应用。
    /// </summary>
    /// <param name="cancellationToken">取消令牌；只覆盖初始化阶段。</param>
    /// <remarks>
    /// 返回后管线在后台继续跑，直到 <see cref="StopConnection"/> 或 <see cref="Dispose"/>。
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfDisposed(_disposed, nameof(AppCompositionRoot));

        EnsureCoreInterfacesRegistered();
        await InitializeCoreAsync(cancellationToken).ConfigureAwait(false);

        // ① 连接先起：管线在后台跑起来，状态变化会实时推到连接状态绑定。
        StartConnection(cancellationToken);

        // ② 窗口后开：回到 UI 线程创建并显示窗口与托盘。
        if (!TryMarshal(ShowMainWindow))
        {
            Log(PipelineLogLevel.Error, "界面调度队列已关闭，主窗口无法显示。");
        }
    }

    /// <summary>
    /// 停止后台连接与管线，但不释放已登记的服务。
    /// </summary>
    /// <remarks>
    /// 取消信号发出后立即返回：界面线程不该等管线收尾。旧管线的资源（事件源、取消源）
    /// 在它自己退出后再释放，因此两者可能短暂并存——这是刻意的，
    /// 换 UI 线程的一次阻塞来消除新旧重叠并不划算。
    /// </remarks>
    public void StopConnection()
    {
        var scope = Interlocked.Exchange(ref _connectionScope, null);
        if (scope is null)
        {
            return;
        }

        var source = Interlocked.Exchange(ref _eventSource, null);
        if (source is not null)
        {
            source.StateChanged -= OnConnectionStateChanged;
        }

        scope.Cancel();

        var running = _pipelineTask;
        if (running is null)
        {
            ReleaseConnectionResources(scope, source);
            return;
        }

        _ = running.ContinueWith(
            _ => ReleaseConnectionResources(scope, source),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>释放一次连接的资源。必须在管线退出之后调用。</summary>
    /// <param name="scope">该次连接的取消源。</param>
    /// <param name="source">该次连接的事件源。</param>
    private static void ReleaseConnectionResources(CancellationTokenSource scope, OneBotWebSocketClient? source)
    {
        try
        {
            scope.Dispose();
            source?.Dispose();
        }
        catch (Exception ex)
        {
            Log(PipelineLogLevel.Warning, $"释放连接资源失败（已忽略）：{ex.Message}");
        }
    }

    /// <summary>退出应用：先停托盘与窗口，再停管线，最后释放服务，顺序与启动相反。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        PauseResume.PauseStateChanged -= OnPauseStateChanged;

        _tray?.Dispose();
        _tray = null;

        // RequestClose 而不是 Close()：托盘常驻模式下 Close() 会被 MainWindow 的 Closing
        // 拦截改成「隐藏」，那样 Dispose 跑完了进程还留着，就永远退不出。

        _mainWindow?.RequestClose();
        _mainWindow = null;

        var running = _pipelineTask;
        StopConnection();

        if (running is null || running.IsCompleted)
        {
            ReleaseOwnedResources();
            return;
        }

        // 管线还在收尾：必须等它真正退出再释放它依赖的仓储与 HttpClient，
        // 否则会在它正读写的过程中把连接池拆掉。
        _ = running.ContinueWith(
            _ => ReleaseOwnedResources(),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    /// <summary>释放由本组合根直接拥有的资源。</summary>
    private void ReleaseOwnedResources()
    {
        // 通知通道是登记在注册表里的单例，生命周期由注册表负责，这里不重复释放。
        _services.Dispose();
        _httpClient.Dispose();
        _shutdown.Dispose();
    }

    /// <summary>登记存储、密钥与模型侧服务。</summary>
    private void RegisterPersistenceServices()
    {
        _services
            .AddSingleton<IClock, SystemClock>(static _ => SystemClock.Instance)
            .AddSingleton<ISqliteConnectionFactory, SqliteConnectionFactory>(static _ => new SqliteConnectionFactory())
            .AddSingleton<ISecretStore, DpapiSecretStore>(static _ => new DpapiSecretStore())
            .AddSingleton<SchemaMigrator>(sp => new SchemaMigrator(sp.Resolve<ISqliteConnectionFactory>()))
            .AddSingleton<ISettingsStore, SettingsStore>(sp => new SettingsStore(
                sp.Resolve<ISqliteConnectionFactory>(),
                sp.Resolve<ISecretStore>(),
                sp.Resolve<IClock>()))
            .AddSingleton<IMessageStore, MessageRepository>(sp => new MessageRepository(
                sp.Resolve<ISqliteConnectionFactory>(),
                sp.Resolve<IClock>()))
            .AddSingleton<IBatchStore, BatchRepository>(sp => new BatchRepository(
                sp.Resolve<ISqliteConnectionFactory>(),
                sp.Resolve<IClock>()))
            .AddSingleton<IDigestStore, DigestRepository>(sp => new DigestRepository(
                sp.Resolve<ISqliteConnectionFactory>()))
            .AddSingleton<ITaskStore, TaskRepository>(sp => new TaskRepository(
                sp.Resolve<ISqliteConnectionFactory>(),
                sp.Resolve<IClock>()))
            .AddSingleton<RetentionCleaner>(sp => new RetentionCleaner(
                sp.Resolve<ISqliteConnectionFactory>(),
                sp.Resolve<IClock>()))
            .AddSingleton<ILlmCallRecorder, LlmCallRecorder>(sp => new LlmCallRecorder(
                sp.Resolve<ISqliteConnectionFactory>(),
                sp.Resolve<IClock>(),
                static diagnostic => Log(PipelineLogLevel.Warning, diagnostic.Message)))

            // 摘要客户端只读"最新设置"，因此注入的是取当前快照的委托而不是某个固定对象；
            // 用户改完设置无需重建 HttpClient，下一次调用就会用上新值。
            .AddSingleton<LlmResultMapper>(sp => new LlmResultMapper(sp.Resolve<IClock>()))
            .AddSingleton<ISummarizationClient, OpenAiCompatibleClient>(sp => new OpenAiCompatibleClient(
                () => _settings.Model,
                sp.Resolve<ISecretStore>(),
                _httpClient,
                sp.Resolve<ILlmCallRecorder>(),
                static diagnostic => Log(PipelineLogLevel.Debug, diagnostic.Message),
                promptOptions: null,
                apiKeySecretKey: SettingsStore.ModelApiKeySecretKey,
                clock: sp.Resolve<IClock>(),
                resultMapper: sp.Resolve<LlmResultMapper>()));
    }

    /// <summary>登记聚合窗口、过滤器与事件源。</summary>
    private void RegisterAggregationServices()
    {
        _services
            // 聚合窗口与过滤器都依赖当前设置，属于"每次重连重建"的易失服务。
            .AddTransient<IAggregationWindow, TimeWindowAggregator>(sp => new TimeWindowAggregator(
                BuildAggregationOptions(),
                sp.Resolve<IClock>(),
                sp.Resolve<IMessageStore>()))
            // 身份校验必须最先执行：缺 group_id / user_id 的消息要在进入去重与闲聊判定前拦下，
            // 否则空身份消息会污染去重键并在摘要提示词中渲染成空发送者。
            .AddTransient<IMessageFilter, IdentityFilter>(static _ => new IdentityFilter())
            .AddTransient<IMessageFilter, ChatterFilter>(static _ => new ChatterFilter(new FilteringOptions()))
            .AddTransient<IMessageFilter, DuplicateMessageFilter>(
                static _ => new DuplicateMessageFilter(TimeSpan.FromMinutes(1)))

            // 事件源端点在构造时即固化，设置变更后必须整体重建，故同样注册为易失服务。
            .AddTransient<IOneBotEventSource, OneBotWebSocketClient>(_ => CreateEventSource());
    }

    /// <summary>登记通知通道。</summary>
    private void RegisterNotificationServices()
    {
        _services
            // 底层类型要保留一份，释放交给注册表，启动自检要拿它来探测通道能力。
            .AddSingleton<ToastNotificationService>(_ => new ToastNotificationService(
                (level, message, _) => Log(
                    level == NotificationLogLevel.Error ? PipelineLogLevel.Error : PipelineLogLevel.Warning,
                    message)))
            .AddSingleton<INotificationService>(sp => sp.Resolve<ToastNotificationService>());
    }

    /// <summary>
    /// 初始化阶段：迁移数据库、加载设置、建视图模型、探测通知通道、清理过期数据。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        var migrator = _services.Resolve<SchemaMigrator>();
        var applied = await migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);
        Log(PipelineLogLevel.Info, $"数据库结构已就绪，本次应用 {applied} 个迁移。");

        var settingsStore = _services.Resolve<ISettingsStore>();
        _settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        ConnectionStatus = new ConnectionStatusViewModel(_marshalToUi is null ? null : QueueOnUiThread);
        SettingsViewModel = new SettingsViewModel(settingsStore);
        SettingsViewModel.Saved += OnSettingsSaved;

        // 设置视图模型的属性变更通知会直接驱动界面，必须在 UI 线程上装载。
        await RunOnUiAsync(SettingsViewModel.LoadAsync, cancellationToken).ConfigureAwait(false);

        // 首启状态同样在 UI 线程上初始化：IsFirstRun 的变更通知会直接驱动遮罩层可见性。
        FirstRun = new FirstRunState(settingsStore);
        await RunOnUiAsync(FirstRun.InitializeAsync, cancellationToken).ConfigureAwait(false);

        var capability = _services.Resolve<ToastNotificationService>().Probe();
        Log(PipelineLogLevel.Info, $"通知通道自检结果：{capability}。");

        await RunRetentionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 启动消息管线。幂等：已存在运行中的管线时直接返回。
    /// </summary>
    /// <param name="cancellationToken">初始取消令牌。</param>
    private void StartConnection(CancellationToken cancellationToken)
    {
        if (_connectionScope is not null || _disposed)
        {
            return;
        }

        var scope = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
        Interlocked.Exchange(ref _connectionScope, scope);

        var source = _services.Resolve<IOneBotEventSource>();
        var observable = source as OneBotWebSocketClient;

        if (observable is not null)
        {
            observable.StateChanged += OnConnectionStateChanged;
            ConnectionStatus.SetEndpoint(observable.Endpoint);
        }
        else
        {
            // 换成别的实现时状态推送会静默失效，必须吵出来而不是假装正常。
            Log(PipelineLogLevel.Warning, "事件源实现不提供状态变更事件，连接状态将不会自动刷新。");
        }

        _eventSource = observable;
        _pipelineTask = BuildPipeline(source).RunAsync(scope.Token);

        ConnectionStatus.Apply(ConnectionState.Connecting, reason: null);
        Log(PipelineLogLevel.Info, $"消息管线已启动，端点 {observable?.Endpoint ?? "未知"}。");
    }

    /// <summary>按当前设置组装消息管线。</summary>
    /// <param name="source">OneBot 事件源。</param>
    /// <returns>消息管线。</returns>
    private MessagePipeline BuildPipeline(IOneBotEventSource source)
    {
        IMessageFilter[] filters = [.. _services.ResolveAll<IMessageFilter>().Cast<IMessageFilter>()];

        // 单批次的「调模型 → 落行动项 → 落摘要 → 标记完成」在 Infrastructure 的 BatchProcessor 里，
        // 与 UI 无关，测试项目可直接加载验证；管线只负责决定什么时候处理哪个批次。
        var batchProcessor = new BatchProcessor(
            _services.Resolve<IBatchStore>(),
            _services.Resolve<IDigestStore>(),
            _services.Resolve<ITaskStore>(),
            _services.Resolve<ISummarizationClient>(),
            _services.Resolve<INotificationService>(),
            Log);

        return new MessagePipeline(
            source,
            _services.Resolve<IMessageStore>(),
            batchProcessor,
            _services.Resolve<IAggregationWindow>(),
            CreateWhitelistMatcher,
            filters,
            () => PauseResume.IsPaused,
            Log,
            WindowDuration);
    }

    /// <summary>创建 OneBot 事件源；令牌每次重连都从密钥库重新取，轮换自动生效。</summary>
    /// <returns>事件源。</returns>
    private OneBotWebSocketClient CreateEventSource()
    {
        var secretStore = _services.Resolve<ISecretStore>();

        return new OneBotWebSocketClient(
            _settings.OneBotConnection,
            () => secretStore.GetSecret(SettingsStore.OneBotTokenSecretKey),
            ReconnectPolicy.Default,
            diagnostics: static diagnostic => Log(MapDiagnosticLevel(diagnostic.Level), diagnostic.Message));
    }

    /// <summary>按当前设置生成群白名单匹配器。</summary>
    /// <returns>白名单匹配器。</returns>
    private GroupWhitelistMatcher CreateWhitelistMatcher() =>
        GroupWhitelistMatcher.FromSettings(_settings.GroupWhitelist);

    /// <summary>由当前设置派生聚合窗口配置。</summary>
    /// <returns>聚合配置。</returns>
    private AggregationOptions BuildAggregationOptions() => new()
    {
        WindowMinutes = WindowMinutes,
        MaxMessagesPerWindow = AggregationOptions.DefaultMaxMessagesPerWindow,
    };

    /// <summary>当前聚合窗口时长。</summary>
    private TimeSpan WindowDuration => TimeSpan.FromMinutes(WindowMinutes);

    /// <summary>窗口时长（分钟）。非法值按默认兜底，避免 <see cref="AggregationOptions"/> 抛异常。</summary>
    private int WindowMinutes =>
        _settings.Aggregation.WindowMinutes is >= 1 and <= SettingsViewModel.MaxWindowMinutes
            ? _settings.Aggregation.WindowMinutes
            : AggregationOptions.DefaultWindowMinutes;

    /// <summary>
    /// 在 UI 线程上创建窗口、托盘并显示窗口。
    /// </summary>
    /// <remarks>
    /// 【阶段 2 变化】WinUI 3 的 <c>Window.Activate()</c> 在 WPF 里只能激活已可见的窗口，
    /// 首次显示必须 <c>Show()</c>。托盘「打开窗口」那条路径两者都要（见 <c>TrayIconService</c>），
    /// 这里只负责首次显示。
    /// </remarks>
    private void ShowMainWindow()
    {
        if (_mainWindow is not null)
        {
            if (!_mainWindow.IsVisible)
            {
                _mainWindow.Show();
            }

            _mainWindow.Activate();
            return;
        }

        var shell = new AppShell(ConnectionStatus, SettingsViewModel, FirstRun, CreatePage);
        _mainWindow = new MainWindow(shell);

        _tray = new TrayIconService(
            windowProvider: () => _mainWindow,
            isPausedProvider: () => PauseResume.IsPaused,
            togglePauseAction: PauseResume.Toggle,
            exitAction: ExitApplication);

        PauseResume.PauseStateChanged += OnPauseStateChanged;

        ConnectionStatus.StatusChanged += OnConnectionStatusChanged;
        _tray.UpdateConnectionState(ConnectionStatus.State);

        _mainWindow.Show();
    }

    /// <summary>
    /// 构造页面并注入视图模型。
    /// </summary>
    /// <remarks>
    /// 【阶段 2 TODO】四个页面的 XAML 仍在用 WinUI 专有控件（NavigationView / NumberBox /
    /// InfoBar / ContentDialog），WPF 里都没有对应物，因此这些页面暂时被 csproj 排除在编译之外，
    /// 具体的构造分支也一并摘掉。阶段 3/4 完成控件替换后，把分支原样加回来即可，
    /// <see cref="AppShell.PageFactory"/> 的 <c>Func&lt;Type, Page&gt;</c> 签名不变。
    /// </remarks>
    /// <param name="pageType">页面类型。</param>
    /// <returns>页面实例。</returns>
    /// <exception cref="NotSupportedException">阶段 2 尚未迁移任何页面。</exception>
    private Page CreatePage(Type pageType)
    {
        Guard.NotNull(pageType);

        throw new NotSupportedException(
            $"页面 {pageType.FullName} 尚未迁移到 WPF（阶段 3/4 处理），当前构建里没有已注册的页面类型。");
    }

    /// <summary>把事件源的状态变化推给界面与托盘。</summary>
    /// <remarks>
    /// 除状态与原因外，还要把 <see cref="ConnectionStateTracker.LastError"/> 一并带出去。
    /// 事件源的 reason 是「第 N 次连接尝试。」这类流程文案，真正的原因只存在 tracker 里；
    /// 不显式带上，状态栏就会显示泛化文案而把真实错误挡在门外。
    /// 直接从 <paramref name="sender"/> 取 tracker，是同一个 <see cref="ConnectionStateTracker"/>
    /// 实例（<c>OneBotWebSocketClient.StateChanged</c> 转发到内部 tracker），
    /// 也就不受 <c>_eventSource</c> 何时被置空的影响。
    /// </remarks>
    /// <param name="sender">发出状态变化的事件源；即内部的状态跟踪器。</param>
    /// <param name="e">状态变化参数。</param>
    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e) =>
        ConnectionStatus.Apply(e.Current, e.Reason, (sender as ConnectionStateTracker)?.LastError);

    /// <summary>连接状态变化后同步托盘提示文字。</summary>
    /// <param name="sender">连接状态绑定。</param>
    /// <param name="model">连接状态绑定。</param>
    private void OnConnectionStatusChanged(object? sender, ConnectionStatusViewModel model) =>
        _tray?.UpdateConnectionState(model.State);

    /// <summary>暂停状态变化后同步托盘菜单文案（「暂停处理」↔「恢复处理」）。</summary>
    /// <param name="sender">暂停/恢复服务。</param>
    /// <param name="isPaused">当前是否处于暂停状态。</param>
    private void OnPauseStateChanged(object? sender, bool isPaused) =>
        _tray?.UpdatePauseState();

    /// <summary>设置保存后接管新设置并重连。</summary>
    /// <param name="sender">设置视图模型。</param>
    /// <param name="saved">已落库的设置。</param>
    private void OnSettingsSaved(object? sender, AppSettings saved)
    {
        Guard.NotNull(saved);

        _settings = saved;
        HandleSettingsChanged();
    }

    /// <summary>设置变更后的重连编排：先停旧管线，再按新设置起新管线。</summary>
    private void HandleSettingsChanged()
    {
        Log(PipelineLogLevel.Info, "设置已变更，正在重建消息管线。");

        StopConnection();
        StartConnection(_shutdown.Token);
    }

    /// <summary>
    /// 退出应用：先让主窗口真正关闭，再请求 <see cref="Application"/> 结束消息循环。
    /// </summary>
    /// <remarks>
    /// 【阶段 2 变化】WinUI 3 里没有"关窗即退出"的对立面，<c>Dispose()</c> 走完进程就结束了。
    /// WPF 默认 <see cref="ShutdownMode.OnExplicitShutdown"/> 时关窗只是隐藏，
    /// <see cref="Dispose"/> 跑完进程照样活着——所以必须显式 <see cref="Application.Shutdown()"/>。
    /// 顺序也不可颠倒：窗口的 <c>Closing</c> 会拦截普通 <c>Close()</c>，
    /// 因此先 <see cref="MainWindow.RequestClose"/> 再 Shutdown。
    /// <para>
    /// 资源释放本身仍由 <see cref="App"/> 挂在 <see cref="Application.Exit"/> 上完成，
    /// 这里只负责把退出流程推起来，不重复释放——那样会与 Exit 里的 Dispose 打架。
    /// </para>
    /// </remarks>
    private void ExitApplication()
    {
        _mainWindow?.RequestClose();

        Application.Current?.Shutdown();
    }

    /// <summary>按设置的保留天数清理过期数据；失败只记日志，不影响启动。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task RunRetentionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var retentionDays = _settings.Aggregation.RetentionDays;
            if (retentionDays < 1)
            {
                return;
            }

            var report = await _services
                .Resolve<RetentionCleaner>()
                .CleanupAsync(TimeSpan.FromDays(retentionDays), cancellationToken)
                .ConfigureAwait(false);

            Log(
                PipelineLogLevel.Info,
                $"过期数据清理完成：批次 {report.Batches}、摘要 {report.Digests}、" +
                $"消息 {report.Messages}、去重记录 {report.ProcessedEvents}（早于 {report.CutoffUtc:u}）。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log(PipelineLogLevel.Warning, $"过期数据清理失败（已忽略）：{ex.Message}");
        }
    }

    /// <summary>
    /// 在 UI 线程上执行一个异步操作；未配置封送委托时就地执行。
    /// </summary>
    /// <param name="operation">要执行的操作。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>操作的任务。</returns>
    private Task RunOnUiAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        Guard.NotNull(operation);

        if (_marshalToUi is null)
        {
            return operation(cancellationToken);
        }

        // WPF 的 Dispatcher.BeginInvoke 没有返回值，只能用 TaskCompletionSource 把操作的
        // 结果接回来。这里必须用泛型版本：net48 没有无参 TaskCompletionSource（非泛型是 .NET 5 才有的）。
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var queued = TryMarshal(() =>
        {
            var running = operation(cancellationToken);
            _ = ForwardAsync(running, completion);
        });

        // 调度器已关闭：就地执行，好过让调用方永远等一个不会完成的 Task。
        return queued ? completion.Task : operation(cancellationToken);
    }

    /// <summary>
    /// 把一段动作送到 UI 线程执行；未配置封送委托时直接就地执行。
    /// </summary>
    /// <param name="action">要执行的动作。</param>
    /// <returns>已排队（或已就地执行）返回 <see langword="true"/>；调度器已关闭返回 <see langword="false"/>。</returns>
    private bool TryMarshal(Action action)
    {
        Guard.NotNull(action);

        return _marshalToUi?.Invoke(action) ?? ExecuteInline(action);
    }

    /// <summary>就地执行一段动作，并汇报结果。</summary>
    /// <param name="action">要执行的动作。</param>
    /// <returns>恒为 <see langword="true"/>。</returns>
    private static bool ExecuteInline(Action action)
    {
        action();
        return true;
    }

    /// <summary>
    /// 界面封送入口：丢弃"是否排队成功"，因为状态推送与页面数据回填都无需重试——
    /// 真正需要等待结果的场景另有 <see cref="RunOnUiAsync"/>。
    /// </summary>
    /// <param name="action">要执行的动作。</param>
    private void QueueOnUiThread(Action action) => _ = TryMarshal(action);

    /// <summary>把委托内操作的完成状态转达给等待方。</summary>
    /// <param name="running">实际操作的任务。</param>
    /// <param name="completion">等待方的完成源。</param>
    /// <returns>可等待的任务。</returns>
    private static async Task ForwardAsync(Task running, TaskCompletionSource<bool> completion)
    {
        try
        {
            await running.ConfigureAwait(false);
            completion.SetResult(true);
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
    }

    /// <summary>
    /// 启动自检：Core 里每个公开接口都必须在本组合根里有登记的实现。
    /// </summary>
    /// <exception cref="InvalidOperationException">存在未登记的接口。</exception>
    private void EnsureCoreInterfacesRegistered()
    {
        var missing = CoreInterfaceCatalog.Enumerate()
            .Where(service => !_services.Registrations.ContainsKey(service))
            .Select(service => service.FullName ?? service.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        if (missing.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"组合根缺少以下 Core 接口的注册：{string.Join("、", missing)}。");
    }

    /// <summary>统一日志出口。项目尚未引入日志框架，先走 <see cref="Trace"/>。</summary>
    /// <param name="level">级别。</param>
    /// <param name="message">消息。</param>
    private static void Log(PipelineLogLevel level, string message) =>
        Trace.WriteLine($"[{level}] {message}");

    /// <summary>
    /// 把 OneBot 诊断级别映射到管线日志级别。
    /// </summary>
    /// <remarks>
    /// 不能一律按 <see cref="PipelineLogLevel.Debug"/> 落地：那样握手失败、坏帧、
    /// 重试耗尽这些 Warning/Error 会被降级成噪声，真正需要排查的信息反而看不见。
    /// 未预见的枚举值按 <see cref="PipelineLogLevel.Info"/> 兜底——新级别第一次出现时
    /// 仍然可见，等同于「值得记一笔」。
    /// </remarks>
    /// <param name="level">OneBot 诊断级别。</param>
    /// <returns>对应的管线日志级别。</returns>
    private static PipelineLogLevel MapDiagnosticLevel(OneBotDiagnosticLevel level) => level switch
    {
        OneBotDiagnosticLevel.Debug => PipelineLogLevel.Debug,
        OneBotDiagnosticLevel.Info => PipelineLogLevel.Info,
        OneBotDiagnosticLevel.Warning => PipelineLogLevel.Warning,
        OneBotDiagnosticLevel.Error => PipelineLogLevel.Error,
        _ => PipelineLogLevel.Info,
    };
}