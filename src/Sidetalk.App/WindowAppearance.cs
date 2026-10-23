using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Sidetalk.App;

internal static class WindowAppearance
{
    // Only SIDETALK's own native caption is styled. Standard Windows window controls remain intact.
    public static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        var contrast = SystemParameters.HighContrast;
        var dark = contrast ? 0 : 1;
        // DWMWA_COLOR_DEFAULT restores the OS-selected caption when contrast is enabled.
        var captionColor = contrast ? -1 : 0x00191614; // COLORREF: #141619
        var textColor = contrast ? -1 : 0x00F0F5F4; // COLORREF: #F4F5F0
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 35, ref captionColor, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 36, ref textColor, sizeof(int));
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int valueSize);
}
