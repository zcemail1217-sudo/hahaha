using System.IO;
using System.Windows;
using Prism.DryIoc;
using Prism.Ioc;
using Prism.Navigation.Regions;
using VisionStation.Application;
using VisionStation.Application.Presentation;
using VisionStation.Client.Services;
using VisionStation.Client.ViewModels;
using VisionStation.Client.Views;
using VisionStation.Communication;
using VisionStation.Devices;
using VisionStation.Devices.CvCommunication;
using VisionStation.Devices.Hikvision;
using VisionStation.Devices.UI.Views;
using VisionStation.Domain;
using VisionStation.Domain.Utilities;
using VisionStation.Infrastructure;
using VisionStation.Vision;
using VisionStation.Vision.UI.Services;
using VisionStation.Vision.UI.ViewModels;
using VisionStation.Vision.UI.Views;

namespace VisionStation.Client;

public partial class App : PrismApplication
{
    private readonly object _shutdownSyncRoot = new();
    private readonly CancellationTokenSource _startupCancellation = new();
    private CrashGuardService? _crashGuard;
    private IAppLogService? _appLogService;
    private ICommunicationChannelRuntime? _communicationRuntime;
    private ProductionCoordinator? _productionCoordinator;
    private ITemplateMatchingService? _templateMatchingService;
    private ApplicationShutdownService? _shutdownService;
    private Task? _shutdownTask;
    private bool _mainWindowShown;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override Window CreateShell()
    {
        return Container.Resolve<StartupWindow>();
    }

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        var runtimePaths = new RuntimePaths();
        var deviceConfigurationRepository = new JsonDeviceConfigurationRepository(runtimePaths);
        var deviceConfiguration = deviceConfigurationRepository.Get();

        containerRegistry.RegisterInstance(runtimePaths);
        containerRegistry.RegisterInstance<IDeviceConfigurationRepository>(deviceConfigurationRepository);
        containerRegistry.RegisterInstance(deviceConfiguration);
        containerRegistry.RegisterSingleton<IUiDispatcher, WpfUiDispatcher>();
        containerRegistry.RegisterSingleton<IUnsavedChangesService, UnsavedChangesService>();
        containerRegistry.RegisterSingleton<ISystemLoadingService, SystemLoadingService>();
        var appLogService = new FileAppLogService(runtimePaths, deviceConfiguration.SystemSettings.Logging);
        var communicationChannels = new CommunicationChannelRuntime(deviceConfigurationRepository, appLogService);
        _appLogService = appLogService;
        _communicationRuntime = communicationChannels;
        containerRegistry.RegisterInstance<IAppLogService>(appLogService);
        containerRegistry.RegisterInstance<ICommunicationChannelRuntime>(communicationChannels);
        containerRegistry.RegisterSingleton<IAlarmEventRepository, SqliteAlarmEventRepository>();
        containerRegistry.RegisterSingleton<IAlarmService, AlarmService>();
        containerRegistry.RegisterSingleton<IRecipeRepository, JsonRecipeRepository>();
        containerRegistry.RegisterSingleton<IInspectionRecordRepository, SqliteInspectionRecordRepository>();
        containerRegistry.RegisterSingleton<IInspectionRunControl, InspectionRunControl>();
        containerRegistry.RegisterSingleton<IImageTraceStore, BmpImageTraceStore>();
        containerRegistry.RegisterSingleton<IVisionOverlayBuilder, VisionOverlayBuilder>();
        containerRegistry.RegisterSingleton<IImageFrameFileService, WpfImageFrameFileService>();
        containerRegistry.RegisterSingleton<IToolParameterDialogService, WpfToolParameterDialogService>();
        containerRegistry.RegisterSingleton<IFlowEditorDialogService, WpfFlowEditorDialogService>();
        containerRegistry.Register<ITcpDebugSession, TcpDebugSession>();
        containerRegistry.Register<ISerialDebugSession, SerialDebugSession>();
        containerRegistry.RegisterSingleton<ProductionDashboardLayoutService>();
        containerRegistry.RegisterSingleton<CrashGuardService>();
        containerRegistry.RegisterSingleton<VisionDebugViewModel>();
        containerRegistry.RegisterSingleton<OpenCvCalibrationService>();

        var templateMatching = new TemplateMatchingComposition(
            runtimePaths,
            deviceConfiguration.SystemSettings.Halcon,
            appLogService);
        var templateMatchingService = templateMatching.Service;
        _templateMatchingService = templateMatchingService;
        containerRegistry.RegisterInstance<ITemplateMatchingService>(templateMatchingService);
        containerRegistry.RegisterInstance<ITemplateModelStore>(templateMatching.Store);
        containerRegistry.RegisterInstance<ITemplateModelResourceManager>(templateMatching.Resources);

        var camera = new HikvisionMvsCameraDevice();
        containerRegistry.RegisterInstance<ICameraDevice>(camera);
        containerRegistry.RegisterInstance<ICameraDeviceDiscovery>(camera);
        containerRegistry.RegisterInstance<ISelectableCameraDevice>(camera);
        containerRegistry.RegisterInstance<IConfigurableCameraDevice>(camera);
        containerRegistry.RegisterInstance<ICameraDiagnosticsProvider>(camera);
        var plcClient = CommunicationRuntimeFactory.CreateMainPlcClient(deviceConfiguration);
        containerRegistry.RegisterInstance<IPlcClient>(plcClient);
        if (plcClient is IAdvancedPlcClient advancedPlcClient)
        {
            containerRegistry.RegisterInstance<IAdvancedPlcClient>(advancedPlcClient);
        }
        var motionControllers = MotionControllerFactory.Create(deviceConfiguration);
        containerRegistry.RegisterInstance(motionControllers.Axis);
        containerRegistry.RegisterInstance(motionControllers.DigitalIo);
        containerRegistry.RegisterInstance<IDeviceRuntime>(CommunicationRuntimeFactory.CreateDeviceRuntime(
            deviceConfiguration,
            camera,
            plcClient,
            motionControllers.Axis,
            motionControllers.DigitalIo));

        containerRegistry.RegisterInstance<IVisionPipeline>(
            VisionPipelineFactory.CreateDefault(
                deviceConfigurationRepository,
                templateMatchingService,
                communicationChannels));

        containerRegistry.RegisterSingleton<IInspectionRunner, InspectionRunner>();
        containerRegistry.RegisterSingleton<ProductionCoordinator>();

        containerRegistry.RegisterForNavigation<ProductionDashboardView>(NavigationKeys.ProductionDashboard);
        containerRegistry.RegisterForNavigation<VisionDebugView>(NavigationKeys.VisionDebug);
        containerRegistry.RegisterForNavigation<CalibrationView>(NavigationKeys.Calibration);
        containerRegistry.RegisterForNavigation<RecipeManagementView>(NavigationKeys.RecipeManagement);
        containerRegistry.RegisterForNavigation<VariableCenterView>(NavigationKeys.VariableCenter);
        containerRegistry.RegisterForNavigation<DeviceStatusView>(NavigationKeys.DeviceStatus);
        containerRegistry.RegisterForNavigation<DeviceConfigurationView>(NavigationKeys.DeviceConfiguration);
        containerRegistry.RegisterForNavigation<HistoryRecordsView>(NavigationKeys.HistoryRecords);
        containerRegistry.RegisterForNavigation<SystemLogsView>(NavigationKeys.SystemLogs);
        containerRegistry.RegisterForNavigation<PermissionLoginView>(NavigationKeys.PermissionLogin);
        containerRegistry.RegisterForNavigation<SystemSettingsView>(NavigationKeys.SystemSettings);
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        _crashGuard = Container.Resolve<CrashGuardService>();
        _ = RunBootSequenceAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _startupCancellation.Cancel();
        if (!_mainWindowShown)
        {
            // No production coordinator or production UI callbacks exist before the main shell.
            // The shutdown service does not capture the WPF synchronization context, so this
            // fallback may safely finish owned resources before Prism tears down the container.
            ShutdownRuntimeAsync().GetAwaiter().GetResult();
        }
        else if (_shutdownTask is { IsCompleted: true } completedShutdown)
        {
            ObserveCompletedShutdown(completedShutdown);
        }
        else if (_shutdownTask is null)
        {
            TryLogShutdownFailure(
                new InvalidOperationException(
                    "The main window exited before the asynchronous runtime shutdown sequence completed."));
        }
        else
        {
            TryLogShutdownFailure(
                new InvalidOperationException(
                    "The main window exited while the asynchronous runtime shutdown sequence was still running."));
        }

        base.OnExit(e);
        _startupCancellation.Dispose();
    }

    internal Task ShutdownRuntimeAsync()
    {
        lock (_shutdownSyncRoot)
        {
            _startupCancellation.Cancel();
            if (_shutdownTask is not null)
            {
                return _shutdownTask;
            }

            var templateMatchingService = _templateMatchingService;
            var communicationRuntime = _communicationRuntime;
            if (templateMatchingService is null || communicationRuntime is null)
            {
                _shutdownTask = Task.CompletedTask;
                return _shutdownTask;
            }

            _shutdownService = _productionCoordinator is null
                ? ApplicationShutdownService.WithoutProduction(
                    templateMatchingService,
                    communicationRuntime)
                : new ApplicationShutdownService(
                    _productionCoordinator,
                    templateMatchingService,
                    communicationRuntime);
            _shutdownTask = ShutdownRuntimeCoreAsync(_shutdownService);
            return _shutdownTask;
        }
    }

    private async Task ShutdownRuntimeCoreAsync(ApplicationShutdownService shutdownService)
    {
        try
        {
            await shutdownService.ShutdownAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            TryLogShutdownFailure(exception);
        }
        finally
        {
            _templateMatchingService = null;
            _communicationRuntime = null;
        }
    }

    private static void ObserveCompletedShutdown(Task shutdownTask)
    {
        if (!shutdownTask.IsCompleted)
        {
            return;
        }

        shutdownTask.GetAwaiter().GetResult();
    }

    private void TryLogShutdownFailure(Exception exception)
    {
        try
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                _appLogService?.Error(nameof(App), exception.ToString());
                return;
            }

            FallbackWriteCrash("shutdown", exception);
        }
        catch
        {
            FallbackWriteCrash("shutdown", exception);
        }
    }

    private async Task RunBootSequenceAsync()
    {
        var loading = Container.Resolve<ISystemLoadingService>();
        var log = Container.Resolve<IAppLogService>();
        var configuration = Container.Resolve<DeviceConfiguration>();
        var startupToken = _startupCancellation.Token;

        loading.ResetDefaultStages();
        loading.Show(
            "系统启动加载",
            "正在按顺序初始化轴卡、数字IO、相机、PLC、配方文件、图像文件和视觉引擎。");

        try
        {
            var deviceStartupStates = new List<LoadingStageState>();
            var axis = Container.Resolve<IAxisController>();
            var io = Container.Resolve<IDigitalIoController>();
            var camera = Container.Resolve<ICameraDevice>();
            var cameraDiscovery = Container.Resolve<ICameraDeviceDiscovery>();
            var configurableCamera = Container.Resolve<IConfigurableCameraDevice>();
            var plc = Container.Resolve<IPlcClient>();
            var recipes = Container.Resolve<IRecipeRepository>();

            deviceStartupStates.Add(await RunDeviceStartupStageAsync(
                loading,
                "axis-card",
                "轴卡",
                "正在打开运动控制卡，加载轴映射与卡配置文件。",
                token => axis.ConnectAsync(token),
                () => axis.Snapshot,
                IsSimulatedRuntime(axis) || configuration.AxisController == AxisControllerKind.Simulated,
                "当前轴卡处于模拟模式，未检测真实运动控制卡连接。",
                startupToken));

            deviceStartupStates.Add(await RunDeviceStartupStageAsync(
                loading,
                "digital-io",
                "数字IO",
                "正在连接数字 IO，加载 DI/DO 点表与扩展 IO 模块。",
                token => io.ConnectAsync(token),
                () => io.Snapshot,
                IsSimulatedRuntime(io) || configuration.AxisController == AxisControllerKind.Simulated,
                "当前数字 IO 处于模拟模式，未检测真实 DI/DO 模块连接。",
                startupToken));

            deviceStartupStates.Add(await RunCameraStartupStageAsync(
                loading,
                camera,
                cameraDiscovery,
                configurableCamera,
                recipes,
                startupToken));

            deviceStartupStates.Add(await RunDeviceStartupStageAsync(
                loading,
                "plc",
                "PLC",
                "正在建立 PLC 通讯通道，加载心跳与读写地址映射。",
                token => plc.ConnectAsync(token),
                () => plc.Snapshot,
                IsSimulatedRuntime(plc) || CommunicationRuntimeFactory.IsSimulatedPlcProtocol(configuration.SystemSettings.Plc.Protocol),
                "当前 PLC 处于模拟模式，未建立真实 PLC 通讯。",
                startupToken));

            deviceStartupStates.Add(await RunCommunicationStartupStageAsync(
                loading,
                configuration,
                Container.Resolve<ICommunicationChannelRuntime>(),
                log,
                startupToken));

            await RunStartupStageAsync(
                loading,
                "recipe-files",
                "配方文件",
                "正在读取当前产品配方、流程图与参数文件。",
                "配方文件加载成功，当前产品模型已就绪。",
                LoadingStageState.Ready,
                token => Container.Resolve<IRecipeRepository>().GetCurrentAsync(token),
                startupToken);

            await RunStartupStageAsync(
                loading,
                "image-files",
                "图像文件",
                "正在准备本地图像目录、模板资源目录和追溯目录。",
                "图像文件加载成功，图像与追溯目录已就绪。",
                LoadingStageState.Ready,
                _ =>
                {
                    var paths = Container.Resolve<RuntimePaths>();
                    Directory.CreateDirectory(paths.ImageDirectory);
                    Directory.CreateDirectory(paths.DataDirectory);
                    return Task.CompletedTask;
                },
                startupToken);

            await RunStartupStageAsync(
                loading,
                "vision-engine",
                "视觉引擎",
                "正在注册视觉工具、检测管线与 OpenCV 运行时。",
                "视觉引擎加载成功，检测管线已注册。",
                LoadingStageState.Ready,
                _ =>
                {
                    var pipeline = Container.Resolve<IVisionPipeline>();
                    return Task.CompletedTask;
                },
                startupToken);

            await RunStartupStageAsync(
                loading,
                "workspace",
                "工作区",
                "正在创建主界面、区域路由和初始 UI 状态。",
                "工作区加载成功，主界面即将显示。",
                LoadingStageState.Ready,
                _ => Task.CompletedTask,
                startupToken);

            await CompleteStartupAndShowMainWindowAsync(
                loading,
                deviceStartupStates.Any(state => state is LoadingStageState.Warning or LoadingStageState.Reserved or LoadingStageState.Error));
            loading.Hide();
            log.Info("System", "VisionStation startup completed");
        }
        catch (OperationCanceledException) when (startupToken.IsCancellationRequested)
        {
            loading.Hide();
        }
        catch (Exception ex)
        {
            log.Error("Startup", ex.Message);
            Container.Resolve<IAlarmService>().Raise(
                AlarmSeverity.Critical,
                "Startup",
                $"Startup failed: {ex.Message}",
                ex.ToString(),
                "startup:boot");
        }
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        var guard = TryGetCrashGuard();
        if (guard is not null)
        {
            guard.HandleUiException(e.Exception);
        }
        else
        {
            FallbackWriteCrash("ui", e.Exception);
        }

        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception
            ?? new InvalidOperationException(e.ExceptionObject?.ToString() ?? "Unknown fatal exception");
        var guard = TryGetCrashGuard();
        if (guard is not null)
        {
            guard.HandleFatalException(exception, e.IsTerminating);
        }
        else
        {
            FallbackWriteCrash("fatal", exception);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        var exception = e.Exception.Flatten();
        var guard = TryGetCrashGuard();
        if (guard is not null)
        {
            guard.HandleUnobservedTaskException(exception);
        }
        else
        {
            FallbackWriteCrash("task", exception);
        }

        e.SetObserved();
    }

    private CrashGuardService? TryGetCrashGuard()
    {
        if (_crashGuard is not null)
        {
            return _crashGuard;
        }

        try
        {
            _crashGuard = Container.Resolve<CrashGuardService>();
            return _crashGuard;
        }
        catch
        {
            return null;
        }
    }

    private static void FallbackWriteCrash(string kind, Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                RuntimePaths.DefaultBaseDirectory,
                "Crashes");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{DateTimeOffset.Now:yyyyMMdd-HHmmssfff}-{kind}.log");
            File.WriteAllText(
                path,
                string.Join(
                    Environment.NewLine,
                    $"Kind: {kind}",
                    $"Timestamp: {DateTimeOffset.Now:O}",
                    "",
                    exception.ToString()));
        }
        catch
        {
        }
    }

    private async Task CompleteStartupAndShowMainWindowAsync(ISystemLoadingService loading, bool hasDeviceWarnings)
    {
        loading.Show(
            hasDeviceWarnings ? "启动检查完成" : "启动加载完成",
            hasDeviceWarnings
                ? "存在设备未真实连接或处于模拟模式，请在设备状态页确认后再运行生产。"
                : "所有启动项已加载成功，正在进入主体软件。");
        await Task.Delay(420);
        ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (_mainWindowShown)
        {
            return;
        }

        var startupWindow = Current.MainWindow;
        _productionCoordinator = Container.Resolve<ProductionCoordinator>();
        var shell = Container.Resolve<ShellWindow>();
        var regionManager = Container.Resolve<IRegionManager>();
        _mainWindowShown = true;

        Current.MainWindow = shell;
        RegionManager.SetRegionManager(shell, regionManager);
        RegionManager.UpdateRegions();

        RoutedEventHandler? loadedHandler = null;
        loadedHandler = (_, _) =>
        {
            shell.Loaded -= loadedHandler;
            shell.Dispatcher.BeginInvoke(
                () => regionManager.RequestNavigate(RegionNames.MainRegion, NavigationKeys.ProductionDashboard),
                System.Windows.Threading.DispatcherPriority.Loaded);
        };
        shell.Loaded += loadedHandler;

        shell.Show();
        startupWindow?.Close();
    }

    private static async Task<LoadingStageState> RunCommunicationStartupStageAsync(
        ISystemLoadingService loading,
        DeviceConfiguration configuration,
        ICommunicationChannelRuntime runtime,
        IAppLogService log,
        CancellationToken cancellationToken)
    {
        const string key = "communication-channels";
        const string name = "通讯通道";
        loading.SetStage(key, name, "正在按策略打开 TCP 与串口通讯通道。", LoadingStageState.Running);
        var startedAt = DateTimeOffset.Now;

        try
        {
            await Task.Delay(80, cancellationToken);
            await runtime.ConnectAsync(CommunicationChannelConnectionPolicies.Startup, cancellationToken);

            var results = new List<CommunicationStartupResult>();
            foreach (var channel in configuration.SystemSettings.Communication.TcpChannels
                         .Where(channel => channel.Enabled &&
                                           CommunicationChannelConnectionPolicies.Normalize(channel.ConnectionPolicy) == CommunicationChannelConnectionPolicies.Startup))
            {
                var snapshot = await runtime.GetTcpSnapshotAsync(channel, cancellationToken);
                var isServer = string.Equals(channel.Mode, "Server", StringComparison.OrdinalIgnoreCase);
                var isReady = isServer ? snapshot.IsListening : snapshot.IsConnected;
                var isWaitingForPeer = isServer && snapshot.IsListening && !snapshot.IsConnected;
                var result = new CommunicationStartupResult(
                    FormatTcpStartupLabel(channel),
                    snapshot,
                    isReady,
                    isWaitingForPeer);
                results.Add(result);
                LogCommunicationStartupResult(log, result);
            }

            foreach (var channel in configuration.SystemSettings.Communication.SerialChannels
                         .Where(channel => channel.Enabled &&
                                           CommunicationChannelConnectionPolicies.Normalize(channel.ConnectionPolicy) == CommunicationChannelConnectionPolicies.Startup))
            {
                var snapshot = await runtime.GetSerialSnapshotAsync(channel, cancellationToken);
                var result = new CommunicationStartupResult(
                    FormatSerialStartupLabel(channel),
                    snapshot,
                    snapshot.IsConnected,
                    false);
                results.Add(result);
                LogCommunicationStartupResult(log, result);
            }

            await DelayRemainingStartupStageAsync(startedAt, cancellationToken);

            if (results.Count == 0)
            {
                var detail = "未配置为“启动时连接”的通讯通道；按需或生产阶段通道不会在加载页主动连接。";
                loading.SetStage(key, name, detail, LoadingStageState.Ready);
                log.Info("Communication", detail);
                return LoadingStageState.Ready;
            }

            var failed = results.Where(result => !result.IsReady).ToArray();
            if (failed.Length > 0)
            {
                var detail = $"通讯通道启动失败 {failed.Length}/{results.Count}：{string.Join("；", failed.Select(FormatCommunicationStartupDetail))}";
                loading.SetStage(key, name, detail, LoadingStageState.Error);
                log.Error("Communication", detail);
                return LoadingStageState.Error;
            }

            var waiting = results.Where(result => result.IsWaitingForPeer).ToArray();
            if (waiting.Length > 0)
            {
                var detail = $"通讯通道已启动，{waiting.Length} 个 TCP 服务端正在等待客户端：{string.Join("；", waiting.Select(FormatCommunicationStartupDetail))}";
                loading.SetStage(key, name, detail, LoadingStageState.Warning);
                log.Warning("Communication", detail);
                return LoadingStageState.Warning;
            }

            var readyDetail = $"通讯通道启动完成：{string.Join("；", results.Select(FormatCommunicationStartupDetail))}";
            loading.SetStage(key, name, readyDetail, LoadingStageState.Ready);
            log.Info("Communication", readyDetail);
            return LoadingStageState.Ready;
        }
        catch (Exception ex)
        {
            var detail = $"通讯通道启动异常：{ex.Message}";
            loading.SetStage(key, name, detail, LoadingStageState.Error);
            log.Error("Communication", detail);
            return LoadingStageState.Error;
        }
    }

    private static void LogCommunicationStartupResult(IAppLogService log, CommunicationStartupResult result)
    {
        var message = $"{result.Label}：{result.Snapshot.StatusText}";
        if (!result.IsReady)
        {
            log.Error("Communication", message);
            return;
        }

        if (result.IsWaitingForPeer)
        {
            log.Warning("Communication", message);
            return;
        }

        log.Info("Communication", message);
    }

    private static string FormatCommunicationStartupDetail(CommunicationStartupResult result)
    {
        return $"{result.Label} -> {result.Snapshot.StatusText}";
    }

    private static string FormatTcpStartupLabel(TcpCommunicationChannelSettings channel)
    {
        return $"TCP '{channel.Name}'({channel.Key}, {channel.Mode}, {channel.Host}:{channel.Port})";
    }

    private static string FormatSerialStartupLabel(SerialCommunicationChannelSettings channel)
    {
        return $"串口 '{channel.Name}'({channel.Key}, {channel.PortName})";
    }

    private sealed record CommunicationStartupResult(
        string Label,
        CommunicationChannelRuntimeSnapshot Snapshot,
        bool IsReady,
        bool IsWaitingForPeer);

    private static async Task RunStartupStageAsync(
        ISystemLoadingService loading,
        string key,
        string name,
        string runningDetail,
        string completedDetail,
        LoadingStageState completedState,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken)
    {
        loading.SetStage(key, name, runningDetail, LoadingStageState.Running);
        var startedAt = DateTimeOffset.Now;

        try
        {
            await Task.Delay(80, cancellationToken);
            await Task.Run(() => work(cancellationToken), cancellationToken);

            var remainingMilliseconds = 260 - (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            if (remainingMilliseconds > 0)
            {
                await Task.Delay(remainingMilliseconds, cancellationToken);
            }

            loading.SetStage(key, name, completedDetail, completedState);
        }
        catch (Exception ex)
        {
            loading.SetStage(key, name, ex.Message, LoadingStageState.Error);
            return;
        }
    }

    private static async Task<LoadingStageState> RunCameraStartupStageAsync(
        ISystemLoadingService loading,
        ICameraDevice camera,
        ICameraDeviceDiscovery discovery,
        IConfigurableCameraDevice configurableCamera,
        IRecipeRepository recipes,
        CancellationToken cancellationToken)
    {
        const string key = "camera";
        const string name = "相机";
        loading.SetStage(key, name, "正在读取当前配方的采图配置，并枚举海康 MVS 设备。", LoadingStageState.Running);
        var startedAt = DateTimeOffset.Now;

        try
        {
            await Task.Delay(80, cancellationToken);
            var plan = await ResolveCameraStartupPlanAsync(recipes, cancellationToken);

            if (IsSimulatedRuntime(camera))
            {
                await camera.ConnectAsync(cancellationToken);
                var simulated = ResolveDeviceStartupResult(camera.Snapshot, true, "当前相机处于模拟模式，未检测真实相机连接。");
                loading.SetStage(key, name, simulated.Detail, simulated.State);
                return simulated.State;
            }

            if (!plan.ShouldConnect)
            {
                var devices = await discovery.DiscoverAsync(cancellationToken);
                var detail = devices.Count == 0
                    ? $"{plan.SkipReason} 未发现海康 MVS 设备。"
                    : $"{plan.SkipReason} 已发现 {devices.Count} 台海康 MVS 设备（可能包含相机和扫码枪），启动阶段不强制占用。";
                await DelayRemainingStartupStageAsync(startedAt, cancellationToken);
                loading.SetStage(key, name, detail, LoadingStageState.Reserved);
                return LoadingStageState.Reserved;
            }

            await configurableCamera.ApplyAcquisitionSettingsAsync(
                new CameraAcquisitionSettings
                {
                    DeviceId = plan.DeviceId,
                    ExposureTimeMs = plan.ExposureTimeMs,
                    TriggerSource = plan.TriggerSource,
                    HeartbeatTimeoutMs = plan.HeartbeatTimeoutMs,
                    ClearBufferBeforeTrigger = plan.ClearBufferBeforeTrigger
                },
                cancellationToken);

            await camera.ConnectAsync(cancellationToken);
            await DelayRemainingStartupStageAsync(startedAt, cancellationToken);
            var (state, snapshotDetail) = ResolveDeviceStartupResult(camera.Snapshot, false, string.Empty);
            loading.SetStage(key, name, $"{plan.Description} {snapshotDetail}", state);
            return state;
        }
        catch (Exception ex)
        {
            loading.SetStage(key, name, ex.Message, LoadingStageState.Error);
            return LoadingStageState.Error;
        }
    }

    private static async Task<LoadingStageState> RunDeviceStartupStageAsync(
        ISystemLoadingService loading,
        string key,
        string name,
        string runningDetail,
        Func<CancellationToken, Task> connect,
        Func<DeviceSnapshot> getSnapshot,
        bool isSimulated,
        string simulatedDetail,
        CancellationToken cancellationToken)
    {
        loading.SetStage(key, name, runningDetail, LoadingStageState.Running);
        var startedAt = DateTimeOffset.Now;

        try
        {
            await Task.Delay(80, cancellationToken);
            await Task.Run(() => connect(cancellationToken), cancellationToken);
            await DelayRemainingStartupStageAsync(startedAt, cancellationToken);

            var snapshot = getSnapshot();
            var (state, detail) = ResolveDeviceStartupResult(snapshot, isSimulated, simulatedDetail);
            loading.SetStage(key, name, detail, state);
            return state;
        }
        catch (Exception ex)
        {
            loading.SetStage(key, name, ex.Message, LoadingStageState.Error);
            return LoadingStageState.Error;
        }
    }

    private static (LoadingStageState State, string Detail) ResolveDeviceStartupResult(
        DeviceSnapshot snapshot,
        bool isSimulated,
        string simulatedDetail)
    {
        if (isSimulated)
        {
            return (LoadingStageState.Warning, $"{simulatedDetail} 当前状态：{snapshot.Message}");
        }

        return snapshot.State switch
        {
            DeviceConnectionState.Connected => (LoadingStageState.Ready, $"真实连接成功：{snapshot.Name}，{snapshot.Message}"),
            DeviceConnectionState.Connecting => (LoadingStageState.Warning, $"仍在连接：{snapshot.Name}，{snapshot.Message}"),
            DeviceConnectionState.Faulted => (LoadingStageState.Error, $"连接失败：{snapshot.Name}，{snapshot.Message}"),
            _ => (LoadingStageState.Warning, $"未连接：{snapshot.Name}，{snapshot.Message}")
        };
    }

    private static async Task DelayRemainingStartupStageAsync(DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var remainingMilliseconds = 260 - (int)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
        if (remainingMilliseconds > 0)
        {
            await Task.Delay(remainingMilliseconds, cancellationToken);
        }
    }

    private static async Task<CameraStartupPlan> ResolveCameraStartupPlanAsync(
        IRecipeRepository recipes,
        CancellationToken cancellationToken)
    {
        var recipe = await recipes.GetCurrentAsync(cancellationToken);
        var flow = recipe.GetActiveFlow();
        var acquire = flow.Tools.FirstOrDefault(tool => tool.Enabled && tool.Kind == VisionToolKind.AcquireImage);
        if (acquire is null)
        {
            return CameraStartupPlan.Skip("当前流程没有启用采图工具。");
        }

        var source = ParameterParser.GetString(acquire.Parameters, "source", "Camera");
        if (!string.Equals(source, "Camera", StringComparison.OrdinalIgnoreCase))
        {
            return CameraStartupPlan.Skip($"当前流程采图来源为 {source}，不是相机采集。");
        }

        var deviceId = ParameterParser.FirstNonEmpty(
            ParameterParser.GetString(acquire.Parameters, "device"),
            ParameterParser.GetString(acquire.Parameters, "cameraSerial"),
            recipe.Camera.CameraId);
        if (IsPlaceholderCameraId(deviceId))
        {
            return CameraStartupPlan.Skip("当前配方未保存真实相机序列号/IP。");
        }

        var triggerSource = ParameterParser.GetString(acquire.Parameters, "triggerSource", "软件触发");
        var exposureTimeMs = GetExposureTimeMs(acquire.Parameters, recipe.Camera.ExposureTimeUs / 1000.0);
        var heartbeatTimeoutMs = (int)Math.Clamp(
            ParameterParser.GetDouble(acquire.Parameters, "heartbeatTimeoutMs", 3000),
            1000,
            60000);
        var clearBufferBeforeTrigger = ParameterParser.GetBool(acquire.Parameters, "clearBufferBeforeTrigger", true);
        return CameraStartupPlan.Connect(
            deviceId,
            exposureTimeMs,
            triggerSource,
            heartbeatTimeoutMs,
            clearBufferBeforeTrigger,
            $"按当前配方连接采图设备 {deviceId}。");
    }

    private static double GetExposureTimeMs(IReadOnlyDictionary<string, string> parameters, double fallback)
    {
        if (parameters.TryGetValue("exposureUs", out var exposureUs) &&
            double.TryParse(exposureUs, out var us))
        {
            return us / 1000.0;
        }

        return ParameterParser.GetDouble(parameters, "exposureMs", fallback);
    }

    private static bool IsPlaceholderCameraId(string? deviceId)
    {
        return string.IsNullOrWhiteSpace(deviceId)
            || deviceId.StartsWith("SIM-", StringComparison.OrdinalIgnoreCase)
            || deviceId.StartsWith("HIK-DEV-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSimulatedRuntime(object instance)
    {
        return instance.GetType().Name.Contains("Simulated", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CameraStartupPlan(
        bool ShouldConnect,
        string DeviceId,
        double ExposureTimeMs,
        string TriggerSource,
        int HeartbeatTimeoutMs,
        bool ClearBufferBeforeTrigger,
        string Description,
        string SkipReason)
    {
        public static CameraStartupPlan Connect(
            string deviceId,
            double exposureTimeMs,
            string triggerSource,
            int heartbeatTimeoutMs,
            bool clearBufferBeforeTrigger,
            string description)
        {
            return new CameraStartupPlan(true, deviceId, exposureTimeMs, triggerSource, heartbeatTimeoutMs, clearBufferBeforeTrigger, description, string.Empty);
        }

        public static CameraStartupPlan Skip(string reason)
        {
            return new CameraStartupPlan(false, string.Empty, 0, string.Empty, 3000, true, string.Empty, reason);
        }
    }
}
