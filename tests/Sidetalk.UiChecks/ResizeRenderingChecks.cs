using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Sidetalk.App;
using Sidetalk.Core;
using Decision = Sidetalk.App.InteractiveResizeRendering.Decision;

// Deterministic part of the resize fix: when the window switches to software rendering and that it
// always returns. Timing is measured separately by ResizeProbe (--resize-probe), not in verify.ps1.
internal static class ResizeRenderingChecks
{
    private const int WmEnterSizeMove = 0x0231, WmMoving = 0x0216;

    public static void Run(AppSnapshot snapshot, SetupRecoveryPlan healthy)
    {
        var cases = 0;
        foreach (var (message, wParam, maximized, active, hardware, expected) in new (int, nint, bool, bool, bool, Decision)[]
                 {
                     (InteractiveResizeRendering.WmNcLeftButtonDown, 17, false, false, true, Decision.UseSoftware), // bottom-right border
                     (InteractiveResizeRendering.WmNcLeftButtonDown, 10, false, false, true, Decision.UseSoftware), // left border
                     (InteractiveResizeRendering.WmNcLeftButtonDown, 2, false, false, true, Decision.None),         // caption: a move
                     (InteractiveResizeRendering.WmNcLeftButtonDown, 18, false, false, true, Decision.None),        // HTBORDER
                     (InteractiveResizeRendering.WmNcLeftButtonDown, 17, true, false, true, Decision.None),         // maximized: no sizing loop
                     (InteractiveResizeRendering.WmNcLeftButtonDown, 17, false, false, false, Decision.None),       // already software
                     (InteractiveResizeRendering.WmSizing, 8, false, false, true, Decision.UseSoftware),
                     (InteractiveResizeRendering.WmSizing, 8, false, true, true, Decision.None),
                     (InteractiveResizeRendering.WmExitSizeMove, 0, false, true, true, Decision.Restore),
                     (InteractiveResizeRendering.WmNcLeftButtonUp, 17, false, true, true, Decision.Restore),
                     (InteractiveResizeRendering.WmExitSizeMove, 0, false, false, true, Decision.None),
                     (WmMoving, 0, false, false, true, Decision.None)
                 })
        {
            var actual = InteractiveResizeRendering.Decide(message, wParam, maximized, active, hardware);
            Check(actual == expected, $"message 0x{message:X} hit {wParam} maximized={maximized} active={active} hardware={hardware}: {actual}, expected {expected}");
            cases++;
        }

        using var controller = new PreviewController(snapshot, healthy);
        var window = new MainWindow(controller, isRenderMode: true) { ShowActivated = false, Width = 880, Height = 740 };
        try
        {
            window.ApplySnapshot(snapshot);
            window.ApplyRecoveryPlan(healthy);
            window.Show();
            Pump();
            var hwnd = new WindowInteropHelper(window).Handle;
            var target = HwndSource.FromHwnd(hwnd).CompositionTarget;
            Check(target.RenderMode == RenderMode.Default && !window.IsSoftwareRenderingForResize, "The window does not start on the default render path");

            // Moving the window never changes rendering.
            Send(hwnd, WmEnterSizeMove);
            SendSizingRect(hwnd, WmMoving);
            Check(target.RenderMode == RenderMode.Default, "Moving the window switched rendering");
            Send(hwnd, InteractiveResizeRendering.WmExitSizeMove);
            Check(target.RenderMode == RenderMode.Default, "Ending a move changed rendering");

            // An edge drag renders in software and returns exactly to the previous mode.
            Send(hwnd, WmEnterSizeMove);
            SendSizingRect(hwnd, InteractiveResizeRendering.WmSizing);
            Check(target.RenderMode == RenderMode.SoftwareOnly && window.IsSoftwareRenderingForResize, "Sizing did not use software rendering");
            window.Width = 1180;
            Pump();
            Check(window.ActualWidth >= 1179, "Layout did not follow the size while rendering in software");
            Send(hwnd, InteractiveResizeRendering.WmExitSizeMove);
            Check(target.RenderMode == RenderMode.Default && !window.IsSoftwareRenderingForResize, "The render mode did not return after the resize");
            cases += 2;

            // A press without a sizing loop (button released) also restores.
            SendSizingRect(hwnd, InteractiveResizeRendering.WmSizing);
            Send(hwnd, InteractiveResizeRendering.WmNcLeftButtonUp, 17);
            Check(target.RenderMode == RenderMode.Default, "Button release did not restore rendering");

            // Maximize and restore keep the hardware path and the restored size.
            var restoredWidth = window.ActualWidth;
            window.WindowState = WindowState.Maximized;
            Pump();
            Check(target.RenderMode == RenderMode.Default && window.ActualWidth > restoredWidth, "Maximize changed rendering or did not enlarge the window");
            window.WindowState = WindowState.Normal;
            Pump();
            Check(target.RenderMode == RenderMode.Default && Math.Abs(window.ActualWidth - restoredWidth) < 1, "Restore changed rendering or the window size");
            cases++;

            // A window someone already set to software stays software afterwards.
            target.RenderMode = RenderMode.SoftwareOnly;
            SendSizingRect(hwnd, InteractiveResizeRendering.WmSizing);
            Send(hwnd, InteractiveResizeRendering.WmExitSizeMove);
            Check(target.RenderMode == RenderMode.SoftwareOnly && !window.IsSoftwareRenderingForResize, "An existing software render mode was overwritten");
            target.RenderMode = RenderMode.Default;
            Check(controller.WritableCallCount == 0, "Resize rendering invoked a controller write");
            cases += 2;
        }
        finally { window.Close(); }
        Console.WriteLine($"Resize rendering checks passed: {cases} cases (decision table, move vs. edge sizing, exact restore, existing software mode kept).");
    }

    private static void Send(nint hwnd, int message, nint wParam = 0) => SendMessage(hwnd, message, wParam, 0);

    // WM_SIZING and WM_MOVING carry a RECT that WPF reads; give it the current window rectangle.
    private static void SendSizingRect(nint hwnd, int message)
    {
        GetWindowRect(hwnd, out var rect);
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<Rect>());
        try
        {
            Marshal.StructureToPtr(rect, pointer, false);
            SendMessage(hwnd, message, 8, pointer);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++) Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Resize rendering fixture: " + message);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
}
