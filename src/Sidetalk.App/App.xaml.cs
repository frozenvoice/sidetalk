using System.Windows;
using System.Windows.Threading;
using Sidetalk.Core;

namespace Sidetalk.App;

public partial class App : Application
{
    private const string InstanceMutexPrefix = "Local\\SIDETALK";
    private ISidetalkController? _controller;
    private Mutex? _instanceMutex;

    public void OnStartup(object sender, StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var utilityMode = TryGetOption(e.Args, "--render", out _) || TryGetOption(e.Args, "--render-setup", out _) || TryGetOption(e.Args, "--render-small", out _) || TryGetOption(e.Args, "--diagnose", out _);
        if (!utilityMode && !TryAcquireSingleInstance())
        {
            Shutdown(0);
            return;
        }

        _ = StartAsync(e.Args);
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
            window.ApplySnapshot(snapshot);
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

    private bool TryAcquireSingleInstance()
    {
        var userPart = string.IsNullOrWhiteSpace(Environment.UserName) ? "current-user" : Environment.UserName;
        var name = $"{InstanceMutexPrefix}-{userPart}";
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, name, out var createdNew);
            if (createdNew)
            {
                return true;
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
            MessageBox.Show(
                "SIDETALK가 이미 실행 중입니다. 기존 창을 사용하세요.",
                "SIDETALK",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }
        catch (Exception exception)
        {
            _instanceMutex?.Dispose();
            _instanceMutex = null;
            ShowStartupError(new InvalidOperationException("SIDETALK의 단일 실행 상태를 확인할 수 없습니다.", exception));
            return false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _instanceMutex?.Dispose();
        _instanceMutex = null;
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
