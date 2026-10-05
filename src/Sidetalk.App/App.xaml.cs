using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using Sidetalk.Core;

namespace Sidetalk.App;

public partial class App : Application
{
    private ISidetalkController? _controller;
    private InstanceRequestBroker? _broker;
    private QuickLaunchWindow? _quickWindow;
    private bool _quickLaunchInFlight;
    private readonly HashSet<Guid> _receivedRequests = [];
    private readonly Queue<Guid> _receivedOrder = new();

    public void OnStartup(object sender, StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var utilityMode = TryGetOption(e.Args, "--render", out _) || TryGetOption(e.Args, "--render-setup", out _) || TryGetOption(e.Args, "--render-small", out _) || TryGetOption(e.Args, "--diagnose", out _);
        _ = utilityMode ? StartAsync(e.Args) : StartInstanceAsync(e.Args);
    }

    private async Task StartInstanceAsync(IReadOnlyList<string> args)
    {
        try
        {
            var quick = args.Contains("--launch-secondary", StringComparer.OrdinalIgnoreCase);
            var startup = args.Contains("--login-startup", StringComparer.OrdinalIgnoreCase);
            if (args.Count != 0 && (!quick || args.Count != (startup ? 2 : 1) ||
                args.Any(value => !value.Equals("--launch-secondary", StringComparison.OrdinalIgnoreCase) &&
                                  !value.Equals("--login-startup", StringComparison.OrdinalIgnoreCase))))
                throw new ArgumentException("사용법: Sidetalk.exe [--launch-secondary [--login-startup]]");

            _broker = new InstanceRequestBroker(ProfileStore.CurrentSid() + ":" + Process.GetCurrentProcess().SessionId);
            if (!_broker.TryAcquire())
            {
                await _broker.ForwardAsync(new(Guid.NewGuid(), quick, startup));
                Shutdown(0);
                return;
            }
            _controller = new SidetalkController();
            await _broker.ListenAsync(request => Dispatcher.InvokeAsync(() => AcceptRemoteRequest(request)).Task);
            _ = ObserveBrokerAsync(_broker);
            if (quick) await RunQuickLaunchAsync(startup);
            else ShowMainWindow();
        }
        catch (Exception exception)
        {
            ShowStartupError(exception);
            Shutdown(1);
        }
    }

    private async Task ObserveBrokerAsync(InstanceRequestBroker broker)
    {
        try { await broker.Completion; }
        catch (Exception error)
        {
            if (ReferenceEquals(_broker, broker))
                ShowMainWindow(reason: "추가 실행 요청을 받을 수 없습니다. " + error.GetBaseException().Message);
        }
    }

    private void AcceptRemoteRequest(InstanceRequest request)
    {
        if (!_receivedRequests.Add(request.Id)) return;
        _receivedOrder.Enqueue(request.Id);
        if (_receivedOrder.Count > 64) _receivedRequests.Remove(_receivedOrder.Dequeue());
        if (_quickLaunchInFlight)
        {
            _quickWindow?.Activate();
            return; // Busy clicks are never replayed after the current request.
        }
        if (request.QuickLaunch)
        {
            // Reserve synchronously but acknowledge before any inline inspection
            // or native confirmation can enter a nested dispatcher loop.
            _quickLaunchInFlight = true;
            Dispatcher.BeginInvoke(new Action(async () => await RunReservedQuickLaunchAsync(request.LoginStartup)));
        }
        else ShowMainWindow();
    }

    private void ShowMainWindow(AppSnapshot? snapshot = null, string? reason = null)
    {
        var window = MainWindow as MainWindow;
        if (window is null)
        {
            window = new MainWindow(_controller ?? throw new InvalidOperationException("Controller unavailable."));
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
        if (snapshot is not null) window.ApplySnapshot(snapshot);
        if (!string.IsNullOrWhiteSpace(reason)) window.ShowEntryFeedback(reason);
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
    }

    private async Task RunQuickLaunchAsync(bool loginStartup)
    {
        if (_quickLaunchInFlight) return;
        _quickLaunchInFlight = true;
        await RunReservedQuickLaunchAsync(loginStartup);
    }

    private async Task RunReservedQuickLaunchAsync(bool loginStartup)
    {
        try
        {
            if (MainWindow is MainWindow window)
            {
                if (!await window.RunExternalQuickLaunchAsync(() => RunQuickLaunchBodyAsync(loginStartup)))
                    ShowMainWindow(reason: "진행 중인 요청이 있어 빠른 실행을 접수하지 않았습니다. 작업이 끝난 뒤 다시 요청하세요.");
            }
            else await RunQuickLaunchBodyAsync(loginStartup);
        }
        catch (Exception exception)
        {
            ShowMainWindow(reason: "빠른 실행을 완료하지 못했습니다. " + exception.GetBaseException().Message);
        }
        finally { _quickLaunchInFlight = false; }
    }

    private async Task RunQuickLaunchBodyAsync(bool loginStartup)
    {
        var quickWindow = new QuickLaunchWindow();
        _quickWindow = quickWindow;
        if (MainWindow is MainWindow owner) quickWindow.Owner = owner;
        quickWindow.Show();
        var closeApplication = false;
        try
        {
            if (loginStartup) RequireStartupSelection();
            var flow = new QuickLaunchFlow((IQuickLaunchController)(_controller ?? throw new InvalidOperationException("Controller unavailable.")));
            var result = await flow.RunAsync(() => Sidetalk.App.MainWindow.ConfirmLaunchAsync(quickWindow),
                quickWindow.Cancellation, new Progress<string>(quickWindow.Report));
            if (!QuickLaunchFlow.NeedsGui(result))
                closeApplication = MainWindow is not Sidetalk.App.MainWindow;
            else ShowMainWindow(result.Snapshot, result.Detail);
        }
        catch (OperationCanceledException)
        {
            ShowMainWindow(reason: "빠른 실행 요청을 취소했습니다. 현재 명령 기록은 이 창에서 다시 확인합니다.");
        }
        catch (Exception exception)
        {
            ShowMainWindow(reason: "빠른 실행이 차단되었습니다. " + exception.GetBaseException().Message);
        }
        finally
        {
            quickWindow.Complete();
            _quickWindow = null;
            // Only a settled result may end this process; uncertain commands keep
            // the same controller and journal in the visible recovery UI.
            if (closeApplication) Shutdown(0);
        }
    }

    private static void RequireStartupSelection()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!Environment.UserInteractive || identity.IsSystem || Process.GetCurrentProcess().SessionId == 0 ||
            new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("로그인 시 실행은 관리자 권한 없는 현재 사용자의 대화형 세션에서만 사용할 수 있습니다.");
        HostProfileContext.RequireUnredirected(new ProfileStore());
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("현재 SIDETALK 실행 경로를 확인하지 못했습니다.");
        var selection = new UserLaunchIntegration(UserLaunchLocations.ForCurrentUser(), executable).Inspect();
        if (!QuickLaunchFlow.StartupAllowed(Environment.UserInteractive, identity.IsSystem,
            new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),
            Process.GetCurrentProcess().SessionId, selection))
            throw new InvalidOperationException("이 설치 경로의 Windows 로그인 실행이 켜져 있지 않거나 바로가기를 다시 확인해야 합니다. 설정에서 선택하세요.");
    }

    private async Task StartAsync(IReadOnlyList<string> args)
    {
        if (TryGetOption(args, "--render", out var renderPath))
        {
            var renderExitCode = await RunRenderAsync(renderPath, setupTab: false, compactWindow: false);
            Shutdown(renderExitCode);
            return;
        }

        if (TryGetOption(args, "--render-setup", out var renderSetupPath))
        {
            var renderExitCode = await RunRenderAsync(renderSetupPath, setupTab: true, compactWindow: false);
            Shutdown(renderExitCode);
            return;
        }

        if (TryGetOption(args, "--render-small", out var renderSmallPath))
        {
            var renderExitCode = await RunRenderAsync(renderSmallPath, setupTab: false, compactWindow: true);
            Shutdown(renderExitCode);
            return;
        }

        if (TryGetOption(args, "--diagnose", out var diagnosticsPath))
        {
            await RunDiagnosticsAsync(diagnosticsPath);
            return;
        }

        try
        {
            _controller = new SidetalkController();
            var window = new MainWindow(_controller);
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
        }
        catch (Exception exception)
        {
            ShowStartupError(exception);
            Shutdown(1);
        }
    }

    private async Task<int> RunRenderAsync(string path, bool setupTab, bool compactWindow)
    {
        ISidetalkController? controller = null;
        MainWindow? window = null;
        var exitCode = 0;
        try
        {
            controller = new SidetalkController(readOnly: true);
            window = new MainWindow(controller!, isRenderMode: true);
            MainWindow = window;
            window.PrepareRender(setupTab, compactWindow);

            // Render mode performs one read-only inspection. MainWindow never calls
            // LaunchAsync or StopAsync here, so external applications are untouched.
            var snapshot = await controller.InspectAsync();
            if (snapshot.InspectionFailure is { } failure)
            {
                window.ApplyMainInspectionFailure(failure);
                exitCode = 1;
            }
            else
            {
                window.ApplySnapshot(snapshot);
                try { window.ApplyRecoveryPlan(await controller.InspectSetupRecoveryAsync()); }
                catch (Exception error)
                {
                    window.ApplyRecoveryInspectionError(error);
                    exitCode = 1;
                }
            }
            window.Show();
            await Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
            window.RenderToPng(path);
        }
        catch (Exception exception)
        {
            // Keep render mode useful when a dependency is absent: the error remains
            // visible in the rendered WPF surface and is also reflected in the exit code.
            if (controller is null)
            {
                // A window cannot be rendered without a controller or a real snapshot.
                exitCode = 1;
                return exitCode;
            }

            if (window is null)
            {
                window = new MainWindow(controller, isRenderMode: true);
                MainWindow = window;
                window.PrepareRender(setupTab, compactWindow);
            }

            window.ApplyError(exception);
            window.Show();
            await Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
            window.RenderToPng(path);
            exitCode = 1;
        }
        finally
        {
            window?.Close();
            controller?.Dispose();
        }

        return exitCode;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _broker?.Dispose();
        _broker = null;
        base.OnExit(e);
    }

    private async Task RunDiagnosticsAsync(string path)
    {
        try
        {
            using var controller = new SidetalkController(readOnly: true);
            await controller.ExportDiagnosticsAsync(path);
            Shutdown(0);
        }
        catch (Exception exception)
        {
            ShowStartupError(exception);
            Shutdown(1);
        }
    }

    private void ShowStartupError(Exception exception)
    {
        MessageBox.Show(
            $"SIDETALK를 시작하지 못했습니다.\n\n{exception.Message}",
            "SIDETALK 시작 오류",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static bool TryGetOption(IReadOnlyList<string> args, string option, out string value)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(args[index + 1]))
            {
                value = args[index + 1];
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
