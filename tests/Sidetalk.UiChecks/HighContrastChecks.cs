using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Sidetalk.App;
using Sidetalk.Core;

internal static class HighContrastChecks
{
    public static void Run(App app, AppSnapshot snapshot, SetupRecoveryPlan healthy, string output)
    {
        using var controller = new PreviewController(snapshot, healthy);
        var window = new MainWindow(controller, isRenderMode: false) { Width = 740, Height = 640, ShowActivated = false };
        var quick = new QuickLaunchWindow { ShowActivated = false };
        var originalBrushes = app.Resources.Keys.OfType<string>()
            .Where(key => app.Resources[key] is SolidColorBrush)
            .ToDictionary(key => key, key => (Brush)app.Resources[key]);
        var normalBackground = ((SolidColorBrush)app.Resources["CanvasBrush"]).Color;
        var contrast = false;
        var colors = Palette(light: false);
        using var palette = new ThemePalette(app, () => contrast, role => colors[role]);
        try
        {
            window.ApplySnapshot(snapshot);
            window.Show();
            quick.Show();
            Pump();
            var launch = (Button)window.FindName("LaunchButton");
            var stop = (Button)window.FindName("StopButton");
            var status = (TextBlock)window.FindName("StatusBadgeText");
            var normalLaunchInk = Color(launch.Foreground);
            var normalStopInk = Color(stop.Foreground);
            var normalStatusInk = Color(status.Foreground);
            window.RenderToPng(Path.Combine(output, "ui-palette-normal-fixture.png"));
            foreach (var light in new[] { false, true })
            {
                colors = Palette(light);
                contrast = true;
                // Same dispatcher boundary as Windows preference notifications, without
                // changing the user's actual contrast setting or Windows palette.
                Task.Run(palette.Refresh).GetAwaiter().GetResult();
                Pump();
                Check(Color(window.Background) == colors["Window"] && Color(window.Foreground) == colors["WindowText"],
                    "An existing main window retained its normal palette.");
                Check(Color(quick.Background) == colors["Window"] && Color(quick.Foreground) == colors["WindowText"],
                    "An existing quick-launch window retained its normal palette.");
                Check(Color(launch.Background) == colors["Highlight"] && Color(launch.Foreground) == colors["HighlightText"],
                    "The primary action lost the OS highlight foreground/background pair.");
                Check(!stop.IsEnabled && Color(stop.Foreground) == colors["GrayText"] && Color(stop.Background) == colors["Window"],
                    "Disabled action colors did not follow the selected contrast palette.");
                Check(Color(status.Foreground) == colors["WindowText"], "Programmatically assigned status ink did not update.");
                var body = (TextBlock)window.FindName("StatusDetailText");
                Check(Color(body.Foreground) == colors["WindowText"], "Secondary body text retained a fixed gray.");
                var consent = (CheckBox)window.FindName("AutomaticRecoveryCheckBox");
                Check(Color(consent.Foreground) == colors["ControlText"], "Native checkbox text did not use OS control ink.");
                var tooltip = new ToolTip { Style = (Style)app.FindResource(typeof(ToolTip)) };
                Check(Color(tooltip.Background) == colors["Window"] && Color(tooltip.Foreground) == colors["WindowText"],
                    "Tooltip colors did not follow the selected contrast palette.");
                var scrollbar = new ScrollBar { Style = (Style)app.FindResource(typeof(ScrollBar)),
                    Minimum = 0, Maximum = 100, ViewportSize = 10, Height = 100 };
                scrollbar.ApplyTemplate();
                var track = (Track)scrollbar.Template.FindName("PART_Track", scrollbar);
                track.Thumb.ApplyTemplate();
                var grip = (Border)track.Thumb.Template.FindName("Grip", track.Thumb);
                Check(Color(grip.Background) == colors["WindowText"], "The custom scrollbar thumb retained its fixed gray.");
                window.RenderToPng(Path.Combine(output, $"ui-palette-contrast-{(light ? "light" : "dark")}-fixture.png"));
            }
            contrast = false;
            palette.Refresh();
            Pump();
            Check(Color(window.Background) == normalBackground && Color(launch.Foreground) == normalLaunchInk
                && Color(stop.Foreground) == normalStopInk && Color(status.Foreground) == normalStatusInk,
                "Leaving contrast mode did not restore the original normal colors.");
            Check(controller.WritableCallCount == 0, "Palette changes invoked a controller write.");

            contrast = true;
            Task.Run(palette.Refresh).GetAwaiter().GetResult();
            palette.Dispose();
            Pump();
            Check(Color(window.Background) == normalBackground, "A queued preference notification applied after disposal.");
            Console.WriteLine($"High-contrast WPF checks passed: existing main/quick-launch windows, body/status, primary/disabled actions, checkbox, tooltip, scrollbar, two injected OS palettes, dispatcher handoff, restore/disposal; synthetic only, actual Windows contrast setting unchanged. DPI={VisualTreeHelper.GetDpi(window).PixelsPerInchX}.");
        }
        finally
        {
            // Restore even when an assertion fails; the subsequent suites share App resources.
            palette.Dispose();
            foreach (var (key, brush) in originalBrushes) app.Resources[key] = brush;
            window.Close();
            quick.Complete();
        }
    }

    private static Dictionary<string, Color> Palette(bool light) => new()
    {
        ["Window"] = light ? Colors.White : Colors.Black,
        ["WindowText"] = light ? Colors.Black : Colors.White,
        ["Highlight"] = light ? Colors.DarkBlue : Colors.DarkGreen,
        ["HighlightText"] = light ? Colors.White : Colors.Yellow,
        ["GrayText"] = light ? Colors.DimGray : Colors.Silver,
        ["ControlText"] = light ? Colors.Navy : Colors.Aqua
    };

    private static Color Color(Brush brush) => ((SolidColorBrush)brush).Color;
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("High-contrast fixture: " + message);
    }
}
