using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Sidetalk.App;

/// <summary>
/// Keeps an edge or corner drag responsive. A hardware-rendered WPF window waits on its GPU render target
/// for every size step; on the measured Windows 11 machine (4K, hybrid GPU) even an empty WPF window then
/// followed the mouse at about 25 frames/s with 100 ms+ stalls, while the same window rendered in software
/// followed at well over 100 frames/s. So only during an interactive resize this window renders in
/// software, and its previous render mode returns as soon as the resize ends. Moving the window,
/// maximize/restore and snapping keep the hardware path. A window or process already set to software
/// rendering is left alone.
/// </summary>
internal sealed class InteractiveResizeRendering
{
    internal const int WmNcLeftButtonDown = 0x00A1, WmNcLeftButtonUp = 0x00A2, WmSizing = 0x0214, WmExitSizeMove = 0x0232;
    private const int HtLeft = 10, HtBottomRight = 17;

    internal enum Decision { None, UseSoftware, Restore }

    private readonly HwndSource source;
    private RenderMode? restore;

    private InteractiveResizeRendering(HwndSource source) => this.source = source;

    public static InteractiveResizeRendering? Attach(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || HwndSource.FromHwnd(handle) is not { } source) return null;
        var rendering = new InteractiveResizeRendering(source);
        source.AddHook(rendering.Hook);
        return rendering;
    }

    /// <summary>True while an interactive resize is rendering in software.</summary>
    public bool IsSoftwareDuringResize => restore is not null;

    /// <summary>
    /// A press on a resize border (HTLEFT..HTBOTTOMRIGHT) of a restored window switches before the first move,
    /// so the one-time render target change is not paid mid-drag; WM_SIZING covers keyboard sizing. Windows
    /// ends every sizing loop with WM_EXITSIZEMOVE; a press that starts no loop ends with WM_NCLBUTTONUP.
    /// </summary>
    internal static Decision Decide(int message, nint wParam, bool maximized, bool active, bool hardwareByDefault) => message switch
    {
        WmExitSizeMove or WmNcLeftButtonUp when active => Decision.Restore,
        WmSizing when !active && hardwareByDefault => Decision.UseSoftware,
        WmNcLeftButtonDown when !active && hardwareByDefault && !maximized && wParam >= HtLeft && wParam <= HtBottomRight => Decision.UseSoftware,
        _ => Decision.None
    };

    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // Messages are observed, never handled: Windows' own sizing loop and WPF layout run unchanged.
        if (message is not (WmNcLeftButtonDown or WmNcLeftButtonUp or WmSizing or WmExitSizeMove)) return 0;
        var target = source.CompositionTarget;
        if (target is null) return 0;
        var hardware = target.RenderMode == RenderMode.Default && RenderOptions.ProcessRenderMode == RenderMode.Default;
        switch (Decide(message, wParam, IsZoomed(hwnd), restore is not null, hardware))
        {
            case Decision.UseSoftware:
                restore = target.RenderMode;
                target.RenderMode = RenderMode.SoftwareOnly;
                break;
            case Decision.Restore:
                Restore();
                break;
        }
        return 0;
    }

    /// <summary>Removes the hook; the resize probe uses it to measure the unassisted hardware path.</summary>
    internal void Detach()
    {
        Restore();
        source.RemoveHook(Hook);
    }

    public void Restore()
    {
        if (restore is not { } previous) return;
        restore = null;
        if (source.CompositionTarget is { } target) target.RenderMode = previous;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint hwnd);
}
