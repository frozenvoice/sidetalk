using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shape = System.Windows.Shapes.Shape;
using System.Windows.Threading;
using Sidetalk.App;
using Sidetalk.Core;

/// <summary>
/// Focused STA regressions for quiet scheduled inspection and explicit manual feedback.
/// Every synthetic boundary is a completion barrier; the only wall-clock observation is
/// the final production DispatcherTimer probe requested by the UI specification.
/// </summary>
internal static class QuietInspectionChecks
{
    private static int _checks;

    public static void Run(AppSnapshot baseline, SetupRecoveryPlan healthy, string output)
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { Pump(RunAsync(baseline, healthy, output)); }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static async Task RunAsync(AppSnapshot baseline, SetupRecoveryPlan healthy, string output)
    {
        Directory.CreateDirectory(output);
        await ScheduledCyclesStayQuiet(baseline, healthy, output);
        await FailureRetainsLastSuccess(baseline, healthy);
        await ManualFeedbackAndMerge(baseline, healthy);
        await AutomaticRecoveryFeedback(baseline, healthy);
        await RenderProductionStates(baseline, healthy, output);
        await ObserveProductionTimer(baseline, healthy, output);
        Console.WriteLine($"Quiet inspection checks: {_checks} synchronized scenarios passed; production timer observation recorded for 24 seconds.");
    }

    private static async Task ScheduledCyclesStayQuiet(AppSnapshot baseline, SetupRecoveryPlan healthy, string output)
    {
        using var controller = new QuietController(baseline, healthy);
        var window = CreateWindow(controller, baseline, healthy);
        window.Show();
        window.UpdateLayout();
        var inspect = Button(window, "InspectButton");
        var indicator = Find<TextBlock>(window, "AutomaticInspectionIndicator");
        Check(indicator.Focus(), "Automatic inspection details cannot receive keyboard focus");
        var tooltip = (ToolTip)indicator.ToolTip;
        Check(tooltip.IsOpen && tooltip.Content is string help && help.Contains("마지막 성공 확인", StringComparison.Ordinal),
            "Keyboard focus did not expose the successful-inspection tooltip");
        inspect.Focus();
        Check(!tooltip.IsOpen, "Inspection tooltip remained open after keyboard focus moved away");
        var tabs = (TabControl)window.FindName("MainTabs");
        var scroll = FindVisualChildren<ScrollViewer>(window).First(viewer => viewer.IsAncestorOf(inspect));
        scroll.ScrollToVerticalOffset(Math.Min(12, scroll.ScrollableHeight));
        window.UpdateLayout();
        var baselineFrame = CaptureFrame(window, tabs, scroll);
        var initialCheckedAt = Text(window, "CheckedAtText");

        for (var cycle = 1; cycle <= 3; cycle++)
        {
            var checkedAt = baseline.CheckedAt.AddMinutes(cycle);
            var next = baseline with { CheckedAt = checkedAt, Checks = baseline.Checks };
            controller.Snapshot = next;
            var barrier = controller.QueueInspection();
            var scheduled = window.RunScheduledInspectionAsync();
            await barrier.Started.Task;

            Check(Text(window, "RefreshSummaryText") is not "확인 중…" and not "갱신 중…" &&
                  !VisibleText(window).Contains("확인 중…", StringComparison.Ordinal),
                $"Scheduled cycle {cycle} exposed visible progress");
            Check(Text(window, "ManualRefreshText") == "상태 새로 확인", $"Scheduled cycle {cycle} changed manual button text");
            Check(Text(window, "CheckedAtText") == initialCheckedAt, $"Scheduled cycle {cycle} changed checked time before success");
            AssertStableFrame(window, tabs, scroll, baselineFrame, $"scheduled cycle {cycle} while waiting");

            barrier.Complete(next);
            await scheduled;
            Check(controller.MaxConcurrentCalls == 1, $"Scheduled cycle {cycle} overlapped controller inspections");
            Check(Text(window, "CheckedAtText") == checkedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                $"Scheduled cycle {cycle} did not publish its successful checked time");
            Check(Text(window, "InspectionDetailText").Contains(checkedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), StringComparison.Ordinal),
                $"Scheduled cycle {cycle} did not update hidden inspection detail");
            AssertStableFrame(window, tabs, scroll, baselineFrame, $"scheduled cycle {cycle} after success");
            initialCheckedAt = Text(window, "CheckedAtText");
        }

        window.RenderToPng(Path.Combine(output, "quiet-scheduled-stable.png"));
        window.Close();
        _checks++;
    }

    private static async Task FailureRetainsLastSuccess(AppSnapshot baseline, SetupRecoveryPlan healthy)
    {
        using var controller = new QuietController(baseline, healthy);
        var window = CreateWindow(controller, baseline, healthy);
        var first = controller.QueueInspection();
        var firstTask = window.RunScheduledInspectionAsync();
        first.Complete(baseline);
        await firstTask;
        var successfulCheckedAt = baseline.CheckedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        controller.InspectFailure = new IOException("synthetic scheduled retry failure");
        await window.RunScheduledInspectionAsync();
        Check(Text(window, "CheckedAtText") == "마지막 확인 " + successfulCheckedAt, "Inspection failure replaced the last successful checked time");
        Check(Text(window, "InspectionDetailText").Contains("최근 상태 확인 실패", StringComparison.Ordinal), "Inspection failure was hidden from detail");
        Check(Text(window, "RefreshSummaryText").Contains("마지막 확인 결과", StringComparison.Ordinal), "Inspection failure lost the last-observation summary");

        controller.InspectFailure = null;
        var retrySnapshot = baseline with { CheckedAt = baseline.CheckedAt.AddMinutes(5) };
        controller.Snapshot = retrySnapshot;
        var retry = controller.QueueInspection();
        var retryTask = window.RunScheduledInspectionAsync();
        await retry.Started.Task;
        Check(Text(window, "InspectionDetailText").Contains("최근 상태 확인 실패", StringComparison.Ordinal), "Retry cleared an error before success");
        Check(Text(window, "CheckedAtText") == "마지막 확인 " + successfulCheckedAt, "Retry changed checked time before success");
        retry.Complete(retrySnapshot);
        await retryTask;
        Check(!Text(window, "InspectionDetailText").Contains("최근 상태 확인 실패", StringComparison.Ordinal), "Successful retry retained a stale main-inspection error");
        Check(Text(window, "CheckedAtText") == retrySnapshot.CheckedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), "Successful retry did not update checked time");

        controller.RecoveryInspectFailure = new IOException("synthetic recovery-only failure");
        await window.RunScheduledInspectionAsync();
        Check(Text(window, "InspectionDetailText").Contains("최근 복구 확인 실패", StringComparison.Ordinal), "Recovery-only failure was hidden");
        Check(Text(window, "RefreshSummaryText").Contains("복구", StringComparison.Ordinal), "Recovery-only failure lost operation feedback");
        controller.RecoveryInspectFailure = null;
        var recoveryRetry = controller.QueueRecoveryInspection();
        var recoveryRetryTask = window.RunScheduledInspectionAsync();
        await recoveryRetry.Started.Task;
        Check(Text(window, "InspectionDetailText").Contains("최근 복구 확인 실패", StringComparison.Ordinal), "Recovery retry cleared its error before success");
        recoveryRetry.Complete(healthy);
        await recoveryRetryTask;
        Check(!Text(window, "InspectionDetailText").Contains("최근 복구 확인 실패", StringComparison.Ordinal), "Successful recovery retry retained a stale error");
        window.Close();
        _checks++;
    }

    private static async Task ManualFeedbackAndMerge(AppSnapshot baseline, SetupRecoveryPlan healthy)
    {
        // A quick completion must cancel the 300ms callback before it can repaint the button.
        var quickController = new QuietController(baseline, healthy);
        var quickWindow = CreateWindow(quickController, baseline, healthy);
        try
        {
            await quickWindow.RefreshNowAsync();
            await DispatcherDelay(TimeSpan.FromMilliseconds(360));
            Check(Text(quickWindow, "ManualRefreshText") == "상태 새로 확인", "Quick manual refresh left a late progress callback armed");
            Check(!Text(quickWindow, "RefreshSummaryText").Contains("확인 중", StringComparison.Ordinal), "Quick manual refresh changed the outer summary");
        }
        finally { quickWindow.Close(); }

        var slowController = new QuietController(baseline, healthy);
        var slowWindow = CreateWindow(slowController, baseline, healthy);
        try
        {
            var barrier = slowController.QueueInspection();
            var manual = slowWindow.RefreshNowAsync();
            await barrier.Started.Task;
            await DispatcherDelay(TimeSpan.FromMilliseconds(360));
            Check(Text(slowWindow, "ManualRefreshText") == "확인 중…", "Slow manual refresh did not expose delayed button progress");
            Check(!Text(slowWindow, "RefreshSummaryText").Contains("확인 중", StringComparison.Ordinal), "Slow manual progress leaked outside the button");
            barrier.Complete(baseline);
            await manual;
            Check(Text(slowWindow, "ManualRefreshText") == "상태 새로 확인", "Manual success did not clean up button progress");
        }
        finally { slowWindow.Close(); }

        var slowFailureController = new QuietController(baseline, healthy);
        var slowFailureWindow = CreateWindow(slowFailureController, baseline, healthy);
        try
        {
            var barrier = slowFailureController.QueueInspection();
            var manual = slowFailureWindow.RefreshNowAsync();
            await barrier.Started.Task;
            await DispatcherDelay(TimeSpan.FromMilliseconds(360));
            Check(Text(slowFailureWindow, "ManualRefreshText") == "확인 중…", "Slow manual failure did not expose delayed button progress");
            barrier.Fail(new IOException("synthetic slow manual failure"));
            await manual;
            await DispatcherDelay(TimeSpan.FromMilliseconds(360));
            Check(Text(slowFailureWindow, "ManualRefreshText") == "상태 새로 확인", "Manual failure callback repainted progress after cleanup");
        }
        finally { slowFailureWindow.Close(); }

        var mergeController = new QuietController(baseline, healthy);
        var mergeWindow = CreateWindow(mergeController, baseline, healthy);
        try
        {
            var barrier = mergeController.QueueInspection();
            var automatic = mergeWindow.RunScheduledInspectionAsync();
            await barrier.Started.Task;
            var manual = mergeWindow.RefreshNowAsync();
            var duplicate = mergeWindow.RefreshNowAsync();
            await DispatcherDelay(TimeSpan.FromMilliseconds(360));
            Check(mergeController.InspectCount == 1, "Manual refresh created a parallel inspection while automatic inspection was active");
            Check(Text(mergeWindow, "ManualRefreshText") == "확인 중…", "Merged manual request did not show button feedback");
            barrier.Complete(baseline);
            await Task.WhenAll(automatic, manual, duplicate);
            Check(mergeController.InspectCount == 1, "Duplicate manual clicks queued another inspection");
            Check(mergeController.MaxConcurrentCalls == 1, "Merged automatic/manual inspection overlapped controller operations");
            Check(Text(mergeWindow, "ManualRefreshText") == "상태 새로 확인", "Merged manual request left stale button feedback");
        }
        finally { mergeWindow.Close(); }

        var failedController = new QuietController(baseline, healthy) { InspectFailure = new IOException("synthetic manual failure") };
        var failedWindow = CreateWindow(failedController, baseline, healthy);
        try
        {
            await failedWindow.RefreshNowAsync();
            Check(Text(failedWindow, "ManualRefreshText") == "상태 새로 확인", "Manual failure left progress text visible");
            Check(Text(failedWindow, "RefreshSummaryText").Contains("마지막 확인 결과", StringComparison.Ordinal), "Manual failure was hidden");
        }
        finally { failedWindow.Close(); }

        var minimizedController = new QuietController(baseline, healthy);
        var minimizedWindow = CreateWindow(minimizedController, baseline, healthy);
        try
        {
            var barrier = minimizedController.QueueInspection();
            var manual = minimizedWindow.RefreshNowAsync();
            await barrier.Started.Task;
            await minimizedWindow.SetMinimizedAsync(true);
            barrier.Complete(baseline with { Title = "late minimized manual result" });
            await manual;
            await DispatcherDelay(TimeSpan.FromMilliseconds(360));
            Check(Text(minimizedWindow, "ManualRefreshText") == "상태 새로 확인", "Minimize left manual progress presentation armed");
        }
        finally { minimizedWindow.Close(); }

        var closedController = new QuietController(baseline, healthy);
        var closedWindow = CreateWindow(closedController, baseline, healthy);
        try
        {
            var barrier = closedController.QueueInspection();
            var manual = closedWindow.RefreshNowAsync();
            await barrier.Started.Task;
            closedWindow.Close();
            barrier.Complete(baseline with { Title = "late closed manual result" });
            await manual;
            await DispatcherDelay(TimeSpan.FromMilliseconds(360));
            Check(Text(closedWindow, "ManualRefreshText") == "상태 새로 확인", "Close left manual progress presentation armed");
        }
        finally { if (closedWindow.IsVisible) closedWindow.Close(); }

        _checks++;
    }

    private static async Task AutomaticRecoveryFeedback(AppSnapshot baseline, SetupRecoveryPlan healthy)
    {
        var automaticPlan = healthy with
        {
            State = SetupRecoveryState.AutomaticallyApplicable,
            Summary = "동일한 검증 범위에서 자동 복구할 수 있습니다.",
            RequiresApproval = false,
            AllowAutomatic = true
        };

        var controller = new QuietController(baseline, automaticPlan);
        var successWindow = CreateWindow(controller, baseline, automaticPlan);
        try
        {
            var recovery = controller.QueueAutomaticRecovery();
            var scheduled = successWindow.RunScheduledInspectionAsync();
            await recovery.Started.Task;
            Check(Find<FrameworkElement>(successWindow, "RecoveryProgressText").Visibility == Visibility.Visible, "Automatic recovery progress was hidden");
            Check(Text(successWindow, "StatusTitleText").Contains("복구", StringComparison.Ordinal), "Automatic recovery did not expose main-window work state");
            Check(Text(successWindow, "RefreshSummaryText").Contains("복구", StringComparison.Ordinal), "Automatic recovery did not expose operation feedback");
            controller.Plan = healthy;
            recovery.Complete(baseline with { RecoveryResultDetail = "합성 자동 복구가 검증되었습니다." });
            await scheduled;
            Check(controller.MaxConcurrentCalls == 1, "Automatic recovery overlapped controller operations");
            Check(Find<FrameworkElement>(successWindow, "RecoveryProgressText").Visibility == Visibility.Collapsed, "Automatic recovery progress remained visible after success");
            Check(Text(successWindow, "RecoveryResultText").Contains("합성 자동 복구", StringComparison.Ordinal), "Automatic recovery result was hidden");
        }
        finally { successWindow.Close(); }

        var failureController = new QuietController(baseline, automaticPlan);
        {
            var failureWindow = CreateWindow(failureController, baseline, automaticPlan);
            try
            {
            var recovery = failureController.QueueAutomaticRecovery();
            var scheduled = failureWindow.RunScheduledInspectionAsync();
            await recovery.Started.Task;
            recovery.Fail(new IOException("synthetic automatic recovery failure"));
            await scheduled;
            Check(failureController.MaxConcurrentCalls == 1, "Failed automatic recovery overlapped controller operations");
            Check(Text(failureWindow, "RecoveryResultText").Contains("완료하지 못했습니다", StringComparison.Ordinal), "Automatic recovery failure was hidden");
            Check(Text(failureWindow, "RecoveryStateText").Contains("실패", StringComparison.Ordinal), "Automatic recovery failure lost its state label");
            Check(Find<FrameworkElement>(failureWindow, "RecoveryProgressText").Visibility == Visibility.Collapsed, "Automatic recovery failure left progress visible");
            }
            finally { failureWindow.Close(); }
        }

        using var unverifiedController = new QuietController(baseline, automaticPlan);
        var unverifiedWindow = CreateWindow(unverifiedController, baseline, automaticPlan);
        try
        {
            var recovery = unverifiedController.QueueAutomaticRecovery();
            var scheduled = unverifiedWindow.RunScheduledInspectionAsync();
            await recovery.Started.Task;
            var failedTime = baseline.CheckedAt.AddMinutes(10);
            recovery.Complete(baseline with
            {
                CheckedAt = failedTime,
                InspectionFailure = InspectionFailure.Capture("synthetic post-recovery", new IOException("합성 복구 후 조회 실패"))
            });
            await scheduled;
            Check(!Button(unverifiedWindow, "LaunchButton").IsEnabled && !Button(unverifiedWindow, "PrepareRecoveryButton").IsEnabled,
                "Failed post-recovery observation restored command or repair authority");
            Check(Text(unverifiedWindow, "RecoveryResultText").Contains("상태를 확인하지 못했습니다", StringComparison.Ordinal),
                "Failed post-recovery observation was described as completed recovery");
            Check(Text(unverifiedWindow, "InspectionDetailText").Contains(baseline.CheckedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), StringComparison.Ordinal) &&
                !Text(unverifiedWindow, "InspectionDetailText").Contains(failedTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), StringComparison.Ordinal),
                "Failed recovery snapshot became the last successful inspection time");
        }
        finally { unverifiedWindow.Close(); }

        _checks += 3;
    }

    private static async Task RenderProductionStates(AppSnapshot baseline, SetupRecoveryPlan healthy, string output)
    {
        RenderState(output, "quiet-ready-default.png", baseline, healthy, window =>
        {
            Check(Find<FrameworkElement>(window, "InitialSetupPanel").Visibility == Visibility.Collapsed, "Ready render retained initial setup panel");
            Check(!string.IsNullOrWhiteSpace(Text(window, "AppVersionText")) && Text(window, "AppVersionText").Contains(SidetalkBuild.Version, StringComparison.Ordinal), "Version render is not the actual build version");
        });
        RenderState(output, "quiet-running-default.png", baseline with
        {
            State = Readiness.RunningUnverified,
            Title = "보조 카카오톡 실행 중",
            Detail = "전용 환경에서 보조 프로세스가 실행 중입니다.",
            CanLaunch = false,
            CanStop = true,
            BoxProcessIds = new[] { 10001 },
            ProcessPolicy = ProcessPolicyStatus.EffectiveRulesUnverified
        }, healthy, window => Check(Text(window, "StatusTitleText").Contains("실행 중", StringComparison.Ordinal), "Running render lost lifecycle state"));
        RenderState(output, "quiet-error-default.png", baseline, healthy, window =>
        {
            window.ApplyMainInspectionError(new IOException("합성 상태 확인 실패"));
            Check(Text(window, "StatusDetailText").Contains("합성 상태 확인 실패", StringComparison.Ordinal), "Error render hid inspection failure");
        });
        await CaptureInitialRender(output, baseline, healthy);
        await CaptureManualRender(output, baseline, healthy);
        await CaptureRecoveryRender(output, baseline, healthy);
        RenderDpiProbes(output, baseline, healthy);
        _checks += 8;
    }

    private static async Task CaptureInitialRender(string output, AppSnapshot baseline, SetupRecoveryPlan healthy)
    {
        var controller = new QuietController(baseline, healthy);
        var initial = controller.QueueInspection();
        var window = new MainWindow(controller);
        try
        {
            window.Show();
            await initial.Started.Task;
            Check(Text(window, "RefreshSummaryText").Contains("환경 확인 중", StringComparison.Ordinal), "Initial render did not show the fixed initial inspection state");
            Check(!Text(window, "ManualRefreshText").Contains("확인 중", StringComparison.Ordinal), "Initial render exposed manual progress outside a user request");
            Capture(window, output, "quiet-initial-default.png");
            window.PrepareRender(false, true);
            Capture(window, output, "quiet-initial-min.png");
            initial.Complete(baseline);
            await window.RunScheduledInspectionAsync();
        }
        finally { window.Close(); }
    }

    private static async Task CaptureManualRender(string output, AppSnapshot baseline, SetupRecoveryPlan healthy)
    {
        var controller = new QuietController(baseline, healthy);
        var loaded = controller.QueueInspection();
        var window = CreateWindow(controller, baseline, healthy);
        try
        {
            window.Show();
            await loaded.Started.Task;
            loaded.Complete(baseline);
            await window.RunScheduledInspectionAsync();
            var manualBarrier = controller.QueueInspection();
            var manual = window.RefreshNowAsync();
            await manualBarrier.Started.Task;
            await DispatcherDelay(TimeSpan.FromMilliseconds(360));
            Check(Text(window, "ManualRefreshText") == "확인 중…", "Manual render was captured without the real delayed button state");
            Capture(window, output, "quiet-manual-default.png");
            window.PrepareRender(false, true);
            Capture(window, output, "quiet-manual-min.png");
            manualBarrier.Complete(baseline);
            await manual;
        }
        finally { window.Close(); }
    }

    private static async Task CaptureRecoveryRender(string output, AppSnapshot baseline, SetupRecoveryPlan healthy)
    {
        var automaticPlan = healthy with
        {
            State = SetupRecoveryState.AutomaticallyApplicable,
            Summary = "동일한 검증 범위에서 자동 복구할 수 있습니다.",
            RequiresApproval = false,
            AllowAutomatic = true
        };
        var controller = new QuietController(baseline, automaticPlan);
        var loaded = controller.QueueInspection();
        var window = CreateWindow(controller, baseline, healthy);
        try
        {
            window.Show();
            await loaded.Started.Task;
            loaded.Complete(baseline);
            await window.RunScheduledInspectionAsync();
            window.ApplyRecoveryPlan(automaticPlan);
            var recovery = controller.QueueAutomaticRecovery();
            var scheduled = window.RunScheduledInspectionAsync();
            await recovery.Started.Task;
            Check(Find<FrameworkElement>(window, "RecoveryProgressText").Visibility == Visibility.Visible, "Recovery render was captured without real progress");
            Capture(window, output, "quiet-recovery-default.png");
            window.PrepareRender(false, true);
            Capture(window, output, "quiet-recovery-min.png");
            controller.Plan = healthy;
            recovery.Complete(baseline with { RecoveryResultDetail = "합성 자동 복구가 검증되었습니다." });
            await scheduled;
        }
        finally { window.Close(); }
    }

    private static async Task ObserveProductionTimer(AppSnapshot baseline, SetupRecoveryPlan healthy, string output)
    {
        using var controller = new QuietController(baseline, healthy) { IncrementSnapshotOnInspect = true };
        var window = CreateWindow(controller, baseline, healthy);
        window.Show();
        // Loaded/activation and focus restoration from earlier fixture windows
        // must finish before the timer observation establishes its baseline.
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await window.RunScheduledInspectionAsync();
        Check(Button(window, "InspectButton").Focus(), "Timer observation could not establish keyboard focus");
        window.UpdateLayout();
        var frame = CaptureFrame(window, (TabControl)window.FindName("MainTabs"), FindVisualChildren<ScrollViewer>(window).First());
        var samples = 0;
        var violations = new List<string>();
        var started = DateTime.UtcNow;
        var observation = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        observation.Tick += (_, _) =>
        {
            samples++;
            if (Text(window, "RefreshSummaryText").Contains("확인 중", StringComparison.Ordinal) || VisibleText(window).Contains("확인 중…", StringComparison.Ordinal))
                violations.Add("visible in-progress text");
            try { AssertStableFrame(window, (TabControl)window.FindName("MainTabs"), FindVisualChildren<ScrollViewer>(window).First(), frame, "production timer sample"); }
            catch (Exception error) { violations.Add(error.Message); }
            if ((DateTime.UtcNow - started) >= TimeSpan.FromSeconds(24)) observation.Stop();
        };
        observation.Start();
        await WaitForTimer(observation, TimeSpan.FromSeconds(30));
        Check(samples >= 100, $"Production timer observation collected too few samples: {samples}");
        Check(controller.InspectCount >= 5, $"Production DispatcherTimer did not schedule repeated inspections: {controller.InspectCount}");
        Check(violations.Count == 0, "Production timer changed visible state: " + string.Join("; ", violations.Take(3)));
        File.WriteAllText(Path.Combine(output, "quiet-production-observation.json"), JsonSerializer.Serialize(new
        {
            DurationSeconds = 24,
            Samples = samples,
            ScheduledInspectionCount = controller.InspectCount,
            Violations = violations,
            Scope = "Production MainWindow DispatcherTimer with synthetic controller boundaries; no accounts or native settings"
        }, new JsonSerializerOptions { WriteIndented = true }));
        window.Close();
        _checks++;
    }

    private static void RenderState(string output, string fileName, AppSnapshot snapshot, SetupRecoveryPlan plan, Action<MainWindow> assertion)
    {
        using var controller = new QuietController(snapshot, plan);
        var window = new MainWindow(controller);
        window.ApplySnapshot(snapshot);
        window.ApplyRecoveryPlan(plan);
        window.Show();
        window.UpdateLayout();
        assertion(window);
        AssertBounds(window, "LaunchButton", "launch");
        AssertBounds(window, "StopButton", "stop");
        AssertBounds(window, "InspectButton", "inspect");
        window.RenderToPng(Path.Combine(output, fileName));
        if (fileName.Contains("-default", StringComparison.Ordinal))
        {
            window.PrepareRender(false, true);
            window.UpdateLayout();
            AssertBounds(window, "LaunchButton", "compact launch");
            AssertBounds(window, "StopButton", "compact stop");
            AssertBounds(window, "InspectButton", "compact inspect");
            window.RenderToPng(Path.Combine(output, fileName.Replace("-default", "-min", StringComparison.Ordinal)));
        }
        window.Close();
    }

    private static void Capture(MainWindow window, string output, string fileName)
    {
        window.UpdateLayout();
        AssertBounds(window, "LaunchButton", "capture launch");
        AssertBounds(window, "StopButton", "capture stop");
        AssertBounds(window, "InspectButton", "capture inspect");
        window.RenderToPng(Path.Combine(output, fileName));
    }

    private static void RenderDpiProbes(string output, AppSnapshot snapshot, SetupRecoveryPlan plan)
    {
        var probes = new List<object>();
        foreach (var dpi in new[] { 96d, 120d, 144d, 192d })
        {
            using var controller = new QuietController(snapshot, plan);
            var window = new MainWindow(controller, isRenderMode: true);
            try
            {
                window.ApplySnapshot(snapshot);
                window.ApplyRecoveryPlan(plan);
                window.Show();
                var scaleName = (dpi / 96d).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture).Replace('.', '_');
                foreach (var compact in new[] { false, true })
                {
                    if (compact) window.PrepareRender(false, true);
                    window.UpdateLayout();
                    foreach (var name in new[] { "LaunchButton", "StopButton", "InspectButton" })
                        AssertBounds(window, name, $"DPI {dpi / 96:0.##}x compact={compact} {name}");
                    Check(FindVisualChildren<ScrollViewer>(window).All(viewer => viewer.ScrollableWidth <= 1),
                        $"DPI {dpi / 96:0.##}x compact={compact} introduced horizontal scrolling");
                    var content = (FrameworkElement)window.Content;
                    var width = (int)Math.Ceiling((content.ActualWidth + content.Margin.Left + content.Margin.Right) * dpi / 96d);
                    var height = (int)Math.Ceiling((content.ActualHeight + content.Margin.Top + content.Margin.Bottom) * dpi / 96d);
                    var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    var sizeName = compact ? "min" : "default";
                    using (var file = File.Create(Path.Combine(output, $"quiet-dpi-{scaleName}x-{sizeName}.png"))) encoder.Save(file);
                    Check(bitmap.PixelWidth == width && bitmap.PixelHeight == height && bitmap.DpiX == dpi,
                        "Scaled WPF capture has incorrect dimensions or resolution");
                    probes.Add(new { Scale = dpi / 96d, Compact = compact, PixelWidth = width, PixelHeight = height,
                        WindowWidth = window.ActualWidth, WindowHeight = window.ActualHeight });
                }
            }
            finally { window.Close(); }
        }
        File.WriteAllText(Path.Combine(output, "quiet-dpi-probes.json"), JsonSerializer.Serialize(new
        {
            Probes = probes,
            Limitations = "Actual scaled RenderTargetBitmap PNGs and WPF DIP layout probes; real monitor DPI and WM_DPICHANGED are untested."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static MainWindow CreateWindow(QuietController controller, AppSnapshot snapshot, SetupRecoveryPlan plan)
    {
        var window = new MainWindow(controller);
        window.ApplySnapshot(snapshot);
        window.ApplyRecoveryPlan(plan);
        return window;
    }

    private static T Find<T>(MainWindow window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"Missing production control {name} ({typeof(T).Name})");

    private static Button Button(MainWindow window, string name) => Find<Button>(window, name);
    private static TextBlock TextBlock(MainWindow window, string name) => Find<TextBlock>(window, name);
    private static string Text(MainWindow window, string name) => TextBlock(window, name).Text;

    private static string VisibleText(MainWindow window) => string.Join("\n", FindVisualChildren<TextBlock>(window)
        .Where(block => block.IsVisible && !string.IsNullOrWhiteSpace(block.Text))
        .Select(block => block.Text));

    private static VisualFrame CaptureFrame(MainWindow window, TabControl tabs, ScrollViewer scroll) => new(
        tabs.SelectedIndex,
        scroll.VerticalOffset,
        Keyboard.FocusedElement,
        Text(window, "StatusTitleText"),
        Text(window, "StatusDetailText"),
        Text(window, "StatusBadgeText"),
        Text(window, "RefreshSummaryText"),
        Text(window, "ManualRefreshText"),
        Bounds(window, "LaunchButton"),
        Bounds(window, "StopButton"),
        Bounds(window, "InspectButton"),
        Bounds(window, "StatusTitleText"),
        Button(window, "LaunchButton").IsEnabled,
        Button(window, "StopButton").IsEnabled,
        Button(window, "InspectButton").IsEnabled,
        Find<Shape>(window, "StatusDot").Fill);

    private static void AssertStableFrame(MainWindow window, TabControl tabs, ScrollViewer scroll, VisualFrame frame, string stage)
    {
        Check(tabs.SelectedIndex == frame.SelectedTab, $"{stage} changed selected tab");
        Check(Math.Abs(scroll.VerticalOffset - frame.ScrollOffset) < 0.1, $"{stage} changed scroll position");
        Check(ReferenceEquals(Keyboard.FocusedElement, frame.Focus), $"{stage} changed keyboard focus");
        Check(Text(window, "StatusTitleText") == frame.StatusTitle && Text(window, "StatusDetailText") == frame.StatusDetail &&
              Text(window, "StatusBadgeText") == frame.StatusBadge && Text(window, "RefreshSummaryText") == frame.RefreshSummary &&
              Text(window, "ManualRefreshText") == frame.ManualText,
            $"{stage} changed main status layout text");
        Check(SameRect(Bounds(window, "LaunchButton"), frame.LaunchBounds) &&
              SameRect(Bounds(window, "StopButton"), frame.StopBounds) &&
              SameRect(Bounds(window, "InspectButton"), frame.InspectBounds) &&
              SameRect(Bounds(window, "StatusTitleText"), frame.StatusTitleBounds),
            $"{stage} changed control geometry");
        Check(Button(window, "LaunchButton").IsEnabled == frame.LaunchEnabled &&
              Button(window, "StopButton").IsEnabled == frame.StopEnabled &&
              Button(window, "InspectButton").IsEnabled == frame.InspectEnabled,
            $"{stage} changed button availability");
        Check(BrushEqual(Find<Shape>(window, "StatusDot").Fill, frame.StatusDotFill), $"{stage} changed status dot styling");
    }

    private static void AssertBounds(MainWindow window, string name, string label)
    {
        var bounds = Bounds(window, name);
        Check(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= -1 && bounds.Top >= -1 &&
              bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1,
            $"{label} control is outside rendered window bounds");
    }

    private static Rect Bounds(MainWindow window, string name)
    {
        var element = Find<FrameworkElement>(window, name);
        return element.TransformToAncestor(window).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    }

    private static bool SameRect(Rect left, Rect right) =>
        Math.Abs(left.Left - right.Left) < 0.1 && Math.Abs(left.Top - right.Top) < 0.1 &&
        Math.Abs(left.Width - right.Width) < 0.1 && Math.Abs(left.Height - right.Height) < 0.1;

    private static bool BrushEqual(Brush? left, Brush? right) =>
        ReferenceEquals(left, right) || string.Equals(left?.ToString(), right?.ToString(), StringComparison.Ordinal);

    private static async Task DispatcherDelay(TimeSpan duration)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        timer.Tick += (_, _) => { timer.Stop(); completed.TrySetResult(true); };
        timer.Start();
        await completed.Task;
    }

    private static async Task WaitForTimer(DispatcherTimer timer, TimeSpan watchdog)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchdogTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = watchdog };
        watchdogTimer.Tick += (_, _) => { watchdogTimer.Stop(); timer.Stop(); completed.TrySetResult(true); };
        watchdogTimer.Start();
        while (timer.IsEnabled)
            await DispatcherDelay(TimeSpan.FromMilliseconds(100));
        watchdogTimer.Stop();
        completed.TrySetResult(true);
        await completed.Task;
    }

    private static void Pump(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
            watchdog.Tick += (_, _) => { watchdog.Stop(); frame.Continue = false; };
            task.GetAwaiter().OnCompleted(() => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
            watchdog.Start();
            Dispatcher.PushFrame(frame);
            watchdog.Stop();
            Check(task.IsCompleted, "Quiet inspection completion exceeded watchdog");
        }
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

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Quiet inspection assertion failed: " + message);
    }

    private sealed record VisualFrame(int SelectedTab, double ScrollOffset, IInputElement? Focus,
        string StatusTitle, string StatusDetail, string StatusBadge, string RefreshSummary, string ManualText,
        Rect LaunchBounds, Rect StopBounds, Rect InspectBounds, Rect StatusTitleBounds,
        bool LaunchEnabled, bool StopEnabled, bool InspectEnabled, Brush? StatusDotFill);

    private sealed class QuietController : ISidetalkController
    {
        private readonly Queue<InteractionChecks.Barrier<AppSnapshot>> _inspections = new();
        private InteractionChecks.Barrier<SetupRecoveryPlan>? _recoveryInspection;
        private InteractionChecks.Barrier<AppSnapshot>? _automaticRecovery;
        private int _activeCalls;

        public QuietController(AppSnapshot snapshot, SetupRecoveryPlan plan)
        {
            Snapshot = snapshot;
            Plan = plan;
        }

        public AppSnapshot Snapshot { get; set; }
        public SetupRecoveryPlan Plan { get; set; }
        public Exception? InspectFailure { get; set; }
        public Exception? RecoveryInspectFailure { get; set; }
        public bool IncrementSnapshotOnInspect { get; set; }
        public int InspectCount { get; private set; }
        public int RecoveryCount { get; private set; }
        public int MaxConcurrentCalls { get; private set; }
        public string RecoveryHistoryPath => Plan.HistoryPath;

        public InteractionChecks.Barrier<AppSnapshot> QueueInspection()
        {
            var barrier = new InteractionChecks.Barrier<AppSnapshot>();
            _inspections.Enqueue(barrier);
            return barrier;
        }

        public InteractionChecks.Barrier<AppSnapshot> QueueAutomaticRecovery() => _automaticRecovery = new();
        public InteractionChecks.Barrier<SetupRecoveryPlan> QueueRecoveryInspection() => _recoveryInspection = new();

        public async Task<AppSnapshot> InspectAsync()
        {
            InspectCount++;
            Enter();
            try
            {
                if (InspectFailure is { } failure) throw failure;
                if (_inspections.TryDequeue(out var barrier)) return await barrier.WaitAsync();
                if (IncrementSnapshotOnInspect) Snapshot = Snapshot with { CheckedAt = Snapshot.CheckedAt.AddSeconds(1) };
                return Snapshot;
            }
            finally { Leave(); }
        }

        public async Task<SetupRecoveryPlan> InspectSetupRecoveryAsync()
        {
            Enter();
            try
            {
                if (RecoveryInspectFailure is { } failure) throw failure;
                if (_recoveryInspection is { } barrier)
                {
                    _recoveryInspection = null;
                    return await barrier.WaitAsync();
                }
                return Plan;
            }
            finally { Leave(); }
        }

        public Task<SetupRecoveryEvidenceInspection> InspectInterruptedEvidenceAsync() =>
            Task.FromResult(new SetupRecoveryEvidenceInspection([], false, false, "합성 중단 흔적 없음", "synthetic", "synthetic", "synthetic"));

        public async Task<AppSnapshot> PrepareAutomaticallyAsync(IProgress<string>? progress = null)
        {
            RecoveryCount++;
            Enter();
            progress?.Report("합성 자동 복구 진행 중…");
            try
            {
                if (_automaticRecovery is { } barrier) return await barrier.WaitAsync();
                return Snapshot;
            }
            finally { _automaticRecovery = null; Leave(); }
        }

        public Task<AppSnapshot> PrepareAsync(SetupRecoveryPlan approvedPlan, bool allowAutomatic, IProgress<string>? progress = null) => PrepareAutomaticallyAsync(progress);
        public Task<AppSnapshot> ReconcileInterruptedEvidenceAsync(SetupRecoveryEvidenceInspection approvedInspection, IProgress<string>? progress = null) => Task.FromResult(Snapshot);
        public Task RevokeAutomaticRecoveryAsync() => Task.CompletedTask;
        public Task LaunchAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task<string> ExportSetupAsync(string folder) => Task.FromResult(Path.Combine(folder, "synthetic.ini"));
        public Task<string> ExportDiagnosticsAsync(string path) => Task.FromResult(path);
        public void Dispose() { }

        private void Enter()
        {
            _activeCalls++;
            MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, _activeCalls);
        }

        private void Leave() => _activeCalls--;
    }
}
