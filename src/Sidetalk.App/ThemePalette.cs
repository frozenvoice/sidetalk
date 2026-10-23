using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Sidetalk.App;

/// <summary>Paints only this application's resources; never changes Windows preferences.</summary>
internal sealed class ThemePalette : IDisposable
{
    private readonly Application application;
    private readonly Func<bool> highContrast;
    private readonly Func<string, Color> systemColor;
    private readonly Dictionary<string, Color> normal;
    private bool disposed;

    private static readonly Dictionary<string, string> SystemRoles = new()
    {
        ["CanvasBrush"] = "Window", ["SurfaceBrush"] = "Window",
        ["InkBrush"] = "WindowText", ["SecondaryBrush"] = "WindowText",
        ["DividerBrush"] = "WindowText", ["WarningInkBrush"] = "WindowText", ["ErrorInkBrush"] = "WindowText",
        ["HoverBrush"] = "Highlight", ["HoverInkBrush"] = "HighlightText",
        ["AccentBrush"] = "Highlight", ["AccentHoverBrush"] = "Highlight", ["AccentInkBrush"] = "HighlightText",
        ["AccentTextBrush"] = "WindowText",
        ["DisabledInkBrush"] = "GrayText", ["DisabledSurfaceBrush"] = "Window",
        ["ScrollThumbBrush"] = "WindowText", ["ScrollThumbHoverBrush"] = "Highlight",
        ["ControlInkBrush"] = "ControlText"
    };

    internal ThemePalette(Application application, Func<bool>? highContrast = null, Func<string, Color>? systemColor = null)
    {
        this.application = application;
        this.highContrast = highContrast ?? (() => SystemParameters.HighContrast);
        this.systemColor = systemColor ?? ReadSystemColor;
        normal = SystemRoles.Keys.ToDictionary(key => key, key => ((SolidColorBrush)application.Resources[key]).Color);
        SystemParameters.StaticPropertyChanged += ParametersChanged;
        SystemEvents.UserPreferenceChanged += PreferencesChanged;
        Refresh();
    }

    private static Color ReadSystemColor(string role) => role switch
    {
        "Window" => SystemColors.WindowColor,
        "WindowText" => SystemColors.WindowTextColor,
        "Highlight" => SystemColors.HighlightColor,
        "HighlightText" => SystemColors.HighlightTextColor,
        "GrayText" => SystemColors.GrayTextColor,
        "ControlText" => SystemColors.ControlTextColor,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    private void ParametersChanged(object? sender, PropertyChangedEventArgs e) => Refresh();
    private void PreferencesChanged(object sender, UserPreferenceChangedEventArgs e) => Refresh();

    internal void Refresh()
    {
        if (disposed || application.Dispatcher.HasShutdownStarted) return;
        if (!application.Dispatcher.CheckAccess())
        {
            application.Dispatcher.InvokeAsync(Refresh);
            return;
        }
        var contrast = highContrast();
        foreach (var (key, role) in SystemRoles)
        {
            var color = contrast ? systemColor(role) : normal[key];
            if (application.Resources[key] is SolidColorBrush current && current.Color == color) continue;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            application.Resources[key] = brush;
        }
        foreach (Window window in application.Windows) WindowAppearance.Apply(window);
    }

    public void Dispose()
    {
        disposed = true;
        SystemParameters.StaticPropertyChanged -= ParametersChanged;
        SystemEvents.UserPreferenceChanged -= PreferencesChanged;
    }
}
