using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Sidetalk.App;
using Sidetalk.Core;

// Manual diagnostic, not part of verify.ps1: timings depend on the machine. It shows the production
// MainWindow with a synthetic (or the read-only production) controller and resizes it the way a mouse
// drag does: SetWindowPos from another thread at ~125 Hz, so every step is a real WM_WINDOWPOSCHANGED
// handled synchronously by WPF on the UI thread. The window procedure is subclassed to time WPF's own
// handling of each size message (including its synchronous layout); CompositionTarget.Rendering gives
// frame pacing and Dispatcher hooks report long UI-thread operations such as the 4-second inspection.
//
//   dotnet run --project tests/Sidetalk.UiChecks -c Release -- <output> --resize-probe
//   dotnet run --project tests/Sidetalk.UiChecks -c Release -- <output> --resize-interactive
// The interactive mode keeps the window open for 40 s and records a person's (or a tool's) real drag.
internal static class ResizeProbe
{
    private const int WmWindowPosChanged = 0x0047, WmEnterSizeMove = 0x0231, WmExitSizeMove = 0x0232, WmSizing = 0x0214;
    private const uint SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

    public static void Run(App app, AppSnapshot ready, SetupRecoveryPlan healthy, SetupRecoveryPlan approval, string output, bool interactive)
    {
        // As in the running app, continuations return to the UI thread.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        _ = TimeBeginPeriod(1);
        try
        {
            var missing = ready with
            {
                State = Readiness.MissingDependency, Title = "실행 준비가 필요해요", CanLaunch = false,
                Detail = "Sandboxie Plus가 설치되어 있지 않습니다. 아래 안내에서 지원 버전 1.18.4을 설치하세요.",
                Sandboxie = new DependencyInfo("Sandboxie Plus", null, "미설치", "설치 경로를 찾지 못했습니다.", false) { Condition = DependencyCondition.NotInstalled }
            };
            if (interactive)
            {
                var result = Measure(app, "interactive", missing, approval, tab: 0, expand: false, timer: true, contrast: false, real: false, interactiveSeconds: 40);
                Write(output, "resize-interactive.json", new[] { result });
                Console.WriteLine(Describe(result));
                return;
            }
            var only = Environment.GetEnvironmentVariable("SIDETALK_RESIZE_PROBE_ONLY");
            if (only == "drag")
            {
                // The first synthetic press only activates the window; this warm-up is discarded.
                _ = Measure(app, "drag-warm-up", ready, healthy, 0, false, false, false, false, sweepSeconds: 1, variant: "drag");
                var dragged = new List<ProbeResult>
                {
                    Measure(app, "drag-main-hardware", ready, healthy, 0, false, false, false, false, sweepSeconds: 4, variant: "drag-hardware"),
                    Measure(app, "drag-main-fixed", ready, healthy, 0, false, false, false, false, sweepSeconds: 4, variant: "drag"),
                    Measure(app, "drag-setup-hardware", ready, approval, 1, false, false, false, false, sweepSeconds: 4, variant: "drag-hardware"),
                    Measure(app, "drag-setup-fixed", ready, approval, 1, false, false, false, false, sweepSeconds: 4, variant: "drag"),
                    Measure(app, "drag-quick-fixed", ready, healthy, 2, false, false, false, false, sweepSeconds: 4, variant: "drag"),
                    Measure(app, "drag-sandboxie-timer-fixed", missing, approval, 0, false, true, false, false, sweepSeconds: 9, variant: "drag"),
                    Measure(app, "drag-contrast-fixed", ready, healthy, 0, false, false, true, false, sweepSeconds: 4, variant: "drag")
                };
                Write(output, "resize-drag.json", dragged);
                foreach (var result in dragged) Console.WriteLine(Describe(result));
                return;
            }
            if (only == "isolate")
            {
                var isolated = new List<ProbeResult>
                {
                    Measure(app, "empty-window", ready, healthy, 0, false, false, false, false, variant: "empty"),
                    Measure(app, "no-tab-content", ready, healthy, 0, false, false, false, false, variant: "no-content"),
                    Measure(app, "main-ready", ready, healthy, 0, false, false, false, false),
                    Measure(app, "setup-collapsed", ready, approval, 1, false, false, false, false),
                    Measure(app, "main-ready-software", ready, healthy, 0, false, false, false, false, variant: "software"),
                    Measure(app, "setup-collapsed-software", ready, approval, 1, false, false, false, false, variant: "software")
                };
                Write(output, "resize-isolate.json", isolated);
                foreach (var result in isolated) Console.WriteLine(Describe(result));
                return;
            }
            var results = new List<ProbeResult>
            {
                Measure(app, "main-ready", ready, healthy, 0, false, false, false, false),
                Measure(app, "main-sandboxie-card", missing, approval, 0, false, false, false, false),
                Measure(app, "setup-collapsed", ready, approval, 1, false, false, false, false),
                Measure(app, "setup-expanded", ready, approval, 1, true, false, false, false),
                Measure(app, "quick-settings", ready, healthy, 2, false, false, false, false),
                Measure(app, "main-ready-contrast", ready, healthy, 0, false, false, true, false),
                Measure(app, "main-sandboxie-timer", missing, approval, 0, false, true, false, false, sweepSeconds: 10),
                Measure(app, "main-real-readonly-timer", ready, healthy, 0, false, true, false, true, sweepSeconds: 10)
            };
            Write(output, "resize-probe.json", results);
            foreach (var result in results) Console.WriteLine(Describe(result));
        }
        finally { _ = TimeEndPeriod(1); }
    }

    private static string Describe(ProbeResult r) =>
        $"{r.Scenario,-26} steps {r.Steps,4} | WM_WINDOWPOSCHANGED p50 {r.PosChangedP50,6:0.00} p95 {r.PosChangedP95,6:0.00} max {r.PosChangedMax,7:0.00} ms | " +
        $"SetWindowPos p95 {r.RoundTripP95,6:0.00} | frames {r.Frames,4} ({r.FramesPerSecond,5:0.0}/s) gap p95 {r.FrameGapP95Ms,5:0.0} max {r.MaxFrameGapMs,6:0.0} ms | layout {r.LayoutUpdated,5} | ops>16ms {r.LongOperations.Count}" +
        (r.SizingMessages > 0 ? $" | WM_SIZING {r.SizingMessages} software frames {r.SoftwareFrames} restored {r.RestoredAfterDrag} lag p50 {r.LagP50Px:0} p95 {r.LagP95Px:0} px settle {r.SettleMs:0} ms" : "");

    private static ProbeResult Measure(App app, string scenario, AppSnapshot snapshot, SetupRecoveryPlan plan, int tab, bool expand, bool timer,
        bool contrast, bool real, double sweepSeconds = 3, int interactiveSeconds = 0, string variant = "")
    {
        Dictionary<string, Brush>? originalBrushes = null;
        ThemePalette? palette = null;
        if (contrast)
        {
            originalBrushes = app.Resources.Keys.OfType<string>().Where(key => app.Resources[key] is SolidColorBrush)
                .ToDictionary(key => key, key => (Brush)app.Resources[key]);
            var colors = new Dictionary<string, Color> { ["Window"] = Colors.Black, ["WindowText"] = Colors.White, ["Highlight"] = Colors.DarkGreen,
                ["HighlightText"] = Colors.Yellow, ["GrayText"] = Colors.Silver, ["ControlText"] = Colors.Aqua };
            palette = new ThemePalette(app, () => true, role => colors[role]);
        }
        ISidetalkController controller = real ? new SidetalkController(readOnly: true) : new ProbeController(snapshot, plan);
        // Drags start large enough that the path never reaches MinWidth/MinHeight, where no window can follow.
        var dragVariant = variant.StartsWith("drag", StringComparison.Ordinal);
        var window = new MainWindow(controller) { Left = 40, Top = 40, Width = dragVariant ? 1120 : 880, Height = dragVariant ? 880 : 740, WindowStartupLocation = WindowStartupLocation.Manual };
        var posChanged = new List<double>();
        var roundTrips = new List<double>();
        var frameTimes = new List<double>();
        var longOps = new List<string>();
        var layoutCount = 0;
        var sizingSessions = 0;
        var sizingMessages = 0;
        var softwareFrames = 0;
        var lags = new List<double>();
        double settleMs = 0;
        var clock = Stopwatch.StartNew();
        var recording = false;
        var operationStarts = new Dictionary<DispatcherOperation, long>();
        var nameProperty = typeof(DispatcherOperation).GetProperty("Name", BindingFlags.Instance | BindingFlags.NonPublic);
        TimeSpan lastRender = TimeSpan.Zero;
        EventHandler rendering = (_, e) =>
        {
            if (!recording || e is not RenderingEventArgs args || args.RenderingTime == lastRender) return;
            lastRender = args.RenderingTime;
            frameTimes.Add(clock.Elapsed.TotalMilliseconds);
            if (window.IsSoftwareRenderingForResize) softwareFrames++;
        };
        EventHandler layout = (_, _) => { if (recording) layoutCount++; };
        DispatcherHookEventHandler started = (_, e) => operationStarts[e.Operation] = Stopwatch.GetTimestamp();
        DispatcherHookEventHandler completed = (_, e) =>
        {
            if (!operationStarts.Remove(e.Operation, out var begin) || !recording) return;
            var ms = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
            if (ms > 16) longOps.Add($"{ms:0.0} ms {e.Operation.Priority} {nameProperty?.GetValue(e.Operation)}");
        };
        WndProc? subclass = null;
        nint original = 0;
        try
        {
            Console.WriteLine($"probe {scenario}: show");
            window.Show();
            Console.WriteLine($"probe {scenario}: refresh");
            Pump(window.RefreshNowAsync());
            Console.WriteLine($"probe {scenario}: configure");
            window.ApplySnapshot(snapshot);
            window.ApplyRecoveryPlan(plan);
            var tabs = (TabControl)window.FindName("MainTabs");
            tabs.SelectedIndex = tab;
            if (expand)
                foreach (var expander in FindVisualChildren<Expander>(window)) expander.IsExpanded = true;
            var timerField = typeof(MainWindow).GetField("_inspectionTimer", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var inspectionTimer = (DispatcherTimer)timerField.GetValue(window)!;
            if (!timer) inspectionTimer.Stop();
            var hwnd = new WindowInteropHelper(window).Handle;
            // Diagnostic variants only; the product never changes its render mode or content this way.
            if (variant == "software") HwndSource.FromHwnd(hwnd).CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
            if (variant == "no-content") tabs.Visibility = Visibility.Collapsed;
            if (variant == "empty") window.Content = new TextBlock { Text = "빈 창", Margin = new Thickness(32) };
            var drag = variant.StartsWith("drag", StringComparison.Ordinal);
            if (variant == "drag-hardware") window.DetachResizeRenderingForDiagnostics();
            if (drag) window.Topmost = true;
            Idle();

            subclass = (h, message, w, l) =>
            {
                if (message == WmEnterSizeMove && recording) sizingSessions++;
                if (message == WmSizing && recording) sizingMessages++;
                if (message != WmWindowPosChanged || !recording) return CallWindowProc(original, h, message, w, l);
                var begin = Stopwatch.GetTimestamp();
                var result = CallWindowProc(original, h, message, w, l);
                posChanged.Add(Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
                return result;
            };
            original = SetWindowLongPtr(hwnd, -4, Marshal.GetFunctionPointerForDelegate(subclass));
            CompositionTarget.Rendering += rendering;
            window.LayoutUpdated += layout;
            window.Dispatcher.Hooks.OperationStarted += started;
            window.Dispatcher.Hooks.OperationCompleted += completed;
            Console.WriteLine($"probe {scenario}: sweep");
            recording = true;
            var begin = clock.Elapsed.TotalMilliseconds;
            var steps = 0;
            if (interactiveSeconds > 0)
            {
                Console.WriteLine($"Resize the SIDETALK window now; recording for {interactiveSeconds} s.");
                PumpFor(TimeSpan.FromSeconds(interactiveSeconds));
            }
            else
            {
                var dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
                var done = false;
                var driver = new Thread(() =>
                {
                    if (drag)
                    {
                        settleMs = Drag(hwnd, sweepSeconds, dpi, lags, ref steps);
                        done = true;
                        return;
                    }
                    var total = (int)(sweepSeconds * 125);
                    for (var i = 0; i < total; i++)
                    {
                        var phase = i * 2 * Math.PI / 250;
                        var width = (int)((1120 + 380 * Math.Sin(phase)) * dpi);
                        var height = (int)((820 + 160 * Math.Sin(phase * 0.7)) * dpi);
                        var sw = Stopwatch.GetTimestamp();
                        SetWindowPos(hwnd, 0, 0, 0, width, height, SwpNoMove | SwpNoZOrder | SwpNoActivate);
                        lock (roundTrips) roundTrips.Add(Stopwatch.GetElapsedTime(sw).TotalMilliseconds);
                        steps++;
                        Thread.Sleep(8);
                    }
                    done = true;
                }) { IsBackground = true };
                driver.Start();
                var frame = new DispatcherFrame();
                var poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
                poll.Tick += (_, _) => { if (done) { poll.Stop(); frame.Continue = false; } };
                poll.Start();
                Dispatcher.PushFrame(frame);
                PumpFor(TimeSpan.FromMilliseconds(200));
            }
            recording = false;
            var elapsed = (clock.Elapsed.TotalMilliseconds - begin) / 1000;
            var gaps = frameTimes.Zip(frameTimes.Skip(1), (a, b) => b - a).ToList();
            return new ProbeResult(scenario, steps == 0 ? posChanged.Count : steps, Percentile(posChanged, 0.5), Percentile(posChanged, 0.95), posChanged.DefaultIfEmpty().Max(),
                Percentile(roundTrips, 0.95), frameTimes.Count, frameTimes.Count / Math.Max(elapsed, 0.001), gaps.DefaultIfEmpty().Max(), layoutCount,
                longOps, sizingSessions, RenderCapability.Tier >> 16, VisualTreeHelper.GetDpi(window).PixelsPerInchX, posChanged.Sum(),
                sizingMessages, softwareFrames, !window.IsSoftwareRenderingForResize, Percentile(lags, 0.5), Percentile(lags, 0.95), settleMs, Percentile(gaps, 0.95));
        }
        finally
        {
            recording = false;
            CompositionTarget.Rendering -= rendering;
            window.LayoutUpdated -= layout;
            window.Dispatcher.Hooks.OperationStarted -= started;
            window.Dispatcher.Hooks.OperationCompleted -= completed;
            if (original != 0) SetWindowLongPtr(new WindowInteropHelper(window).Handle, -4, original);
            GC.KeepAlive(subclass);
            window.Close();
            controller.Dispose();
            if (palette is not null)
            {
                palette.Dispose();
                foreach (var (key, brush) in originalBrushes!) app.Resources[key] = brush;
            }
        }
    }

    // A real edge drag through Windows' own sizing loop: press on the bottom-right resize border, move at
    // ~125 Hz, release, then put the cursor back. Lag is how far the window's size trails the pointer
    // just before the next move; settle is how long the final size takes to arrive after the last move.
    private static double Drag(nint hwnd, double seconds, double dpi, List<double> lags, ref int steps)
    {
        GetCursorPos(out var original);
        GetWindowRect(hwnd, out var rect);
        var startX = rect.Right - 3;
        var startY = rect.Bottom - 3;
        var startWidth = rect.Right - rect.Left;
        var startHeight = rect.Bottom - rect.Top;
        try
        {
            MoveTo(startX, startY);
            Thread.Sleep(80);
            Button(down: true);
            Thread.Sleep(80);
            var total = (int)(seconds * 125);
            int targetWidth = startWidth, targetHeight = startHeight;
            for (var i = 1; i <= total; i++)
            {
                var phase = i * 2 * Math.PI / 250;
                var dx = (int)(300 * dpi * Math.Sin(phase));
                var dy = (int)(150 * dpi * Math.Sin(phase * 0.7));
                MoveTo(startX + dx, startY + dy);
                targetWidth = startWidth + dx;
                targetHeight = startHeight + dy;
                steps++;
                Thread.Sleep(8);
                GetWindowRect(hwnd, out var now);
                lags.Add(Math.Abs(now.Right - now.Left - targetWidth) + Math.Abs(now.Bottom - now.Top - targetHeight));
            }
            var settle = Stopwatch.StartNew();
            while (settle.ElapsedMilliseconds < 3000)
            {
                GetWindowRect(hwnd, out var now);
                if (now.Right - now.Left == targetWidth && now.Bottom - now.Top == targetHeight) break;
                Thread.Sleep(1);
            }
            var settleMs = settle.Elapsed.TotalMilliseconds;
            Button(down: false);
            Thread.Sleep(150);
            return settleMs;
        }
        finally
        {
            Button(down: false);
            SetCursorPos(original.X, original.Y);
        }
    }

    private static void MoveTo(int x, int y)
    {
        var left = GetSystemMetrics(76); var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78); var height = GetSystemMetrics(79);
        var input = new Input
        {
            Type = 0,
            Mouse = new MouseInput
            {
                X = (int)Math.Round((x - left) * 65535.0 / Math.Max(1, width - 1)),
                Y = (int)Math.Round((y - top) * 65535.0 / Math.Max(1, height - 1)),
                Flags = 0x0001 | 0x8000 | 0x4000 // MOVE | ABSOLUTE | VIRTUALDESK
            }
        };
        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    private static void Button(bool down)
    {
        var input = new Input { Type = 0, Mouse = new MouseInput { Flags = down ? 0x0002u : 0x0004u } };
        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nint Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public MouseInput Mouse; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);

    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];
    }

    private static void Write(string output, string name, IEnumerable<ProbeResult> results)
    {
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(new
        {
            Machine = Environment.MachineName.Length > 0 ? "local" : "",
            Os = Environment.OSVersion.VersionString,
            Results = results
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Idle() => PumpFor(TimeSpan.FromMilliseconds(300));

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    public sealed record ProbeResult(string Scenario, int Steps, double PosChangedP50, double PosChangedP95, double PosChangedMax, double RoundTripP95,
        int Frames, double FramesPerSecond, double MaxFrameGapMs, int LayoutUpdated, List<string> LongOperations, int SizingSessions,
        int RenderTier, double Dpi, double PosChangedTotalMs, int SizingMessages = 0, int SoftwareFrames = 0, bool RestoredAfterDrag = true,
        double LagP50Px = 0, double LagP95Px = 0, double SettleMs = 0, double FrameGapP95Ms = 0);

    private delegate nint WndProc(nint hwnd, int message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")] private static extern nint CallWindowProc(nint previous, nint hwnd, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint period);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] private static extern uint TimeEndPeriod(uint period);

    private sealed class ProbeController(AppSnapshot snapshot, SetupRecoveryPlan plan) : ISidetalkController
    {
        public string RecoveryHistoryPath => plan.HistoryPath;
        public Task<AppSnapshot> InspectAsync() => Task.Run(() => snapshot with { CheckedAt = DateTimeOffset.Now });
        public Task<SetupRecoveryPlan> InspectSetupRecoveryAsync() => Task.Run(() => plan);
        public Task<SetupRecoveryEvidenceInspection> InspectInterruptedEvidenceAsync() => Task.Run(() => new SetupRecoveryEvidenceInspection([], false, false, "중단 흔적 없음", "", "", ""));
        public Task<AppSnapshot> ReconcileInterruptedEvidenceAsync(SetupRecoveryEvidenceInspection approvedInspection, IProgress<string>? progress = null) => throw new InvalidOperationException("Probe is read-only");
        public Task<AppSnapshot> PrepareAsync(SetupRecoveryPlan approvedPlan, bool allowAutomatic, IProgress<string>? progress = null) => throw new InvalidOperationException("Probe is read-only");
        public Task<AppSnapshot> PrepareAutomaticallyAsync(IProgress<string>? progress = null) => throw new InvalidOperationException("Probe is read-only");
        public Task RevokeAutomaticRecoveryAsync() => throw new InvalidOperationException("Probe is read-only");
        public Task LaunchAsync() => throw new InvalidOperationException("Probe is read-only");
        public Task StopAsync() => throw new InvalidOperationException("Probe is read-only");
        public Task<string> ExportSetupAsync(string folder) => throw new InvalidOperationException("Probe is read-only");
        public Task<string> ExportDiagnosticsAsync(string path) => throw new InvalidOperationException("Probe is read-only");
        public void Dispose() { }
    }
}
