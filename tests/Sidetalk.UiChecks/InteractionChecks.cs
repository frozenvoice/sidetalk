using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Sidetalk.App;
using Sidetalk.Core;

/// <summary>STA regressions use explicit completion barriers, never sleeps or live installations.</summary>
internal static class InteractionChecks
{
    private static int _checks;

    public static void Run(AppSnapshot snapshot, SetupRecoveryPlan healthy, SetupRecoveryPlan approval, string output)
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { Pump(RunAsync(snapshot with { CanStop = true }, healthy, approval, output)); }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static async Task RunAsync(AppSnapshot snapshot, SetupRecoveryPlan healthy, SetupRecoveryPlan approval, string output)
    {
        var metrics = await StableButtonsAndQueuedClick(snapshot, healthy, output);
        await RoutedClickUsesSingleIntent(snapshot, healthy);
        await QueuedStop(snapshot, healthy);
        await FailedPollDropsClick(snapshot, healthy);
        await FreshValidationBlocksClick(snapshot, healthy);
        await DuplicateDuringCommand(snapshot, healthy);
        await CancelledConfirmation(snapshot, healthy);
        await CancelPendingAndLateConfirmation(snapshot, healthy);
        await ClosedWhileWaiting(snapshot, healthy);
        await ClosedDuringConfirmation(snapshot, healthy);
        await CancelAndCloseDuringValidation(snapshot, healthy);
        await FailedCommandDoesNotReplay(snapshot, healthy);
        await RecoveryCompetition(snapshot, healthy, approval);
        await PendingIntentPrecedesAutomaticRecovery(snapshot, healthy, approval);
        await ExportDialogOwnsOperation(snapshot, healthy, approval);
        var minimized = await MinimizeAndRestore(snapshot, healthy);
        File.WriteAllText(Path.Combine(output, "interaction-metrics.json"), JsonSerializer.Serialize(new
        {
            Scope = "Synthetic STA WPF fixtures; explicit barriers; no real accounts, settings, or timing performance measurement",
            BaselineCommit = "4e79d557b3c5bc6df829f6777c353ae9ad800db5",
            BaselineHeldRefreshLaunchTransitions = 2,
            BaselineHeldRefreshStopTransitions = 2,
            CurrentHeldRefreshLaunchTransitions = metrics.Launch,
            CurrentHeldRefreshStopTransitions = metrics.Stop,
            BaselineClickDuringPollLaunchCommands = 0,
            CurrentClickDuringPollLaunchCommands = metrics.Commands,
            CurrentClickDuringPollConfirmationCount = metrics.Confirmations,
            BaselineMinimizedPollSnapshotVisualUpdates = 1,
            MinimizedPollSnapshotVisualUpdates = minimized,
            CompletedScenarios = _checks
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Interaction checks: {_checks} synchronized scenarios passed; held-refresh button transitions launch={metrics.Launch}, stop={metrics.Stop}; queued launch={metrics.Commands}; minimized snapshot visual updates={minimized}.");
    }

    private static async Task<(int Launch, int Stop, int Commands, int Confirmations)> StableButtonsAndQueuedClick(AppSnapshot snapshot, SetupRecoveryPlan healthy, string output)
    {
        using var controller = new DelayedController(snapshot, healthy);
        var confirmations = 0;
        var window = CreateWindow(controller, snapshot, healthy, _ => { confirmations++; return Task.FromResult(true); });
        var launch = Button(window, "LaunchButton");
        var stop = Button(window, "StopButton");
        var launchChanges = 0;
        var stopChanges = 0;
        EventHandler launchChanged = (_, _) => launchChanges++;
        EventHandler stopChanged = (_, _) => stopChanges++;
        var descriptor = DependencyPropertyDescriptor.FromProperty(UIElement.IsEnabledProperty, typeof(Button));
        descriptor.AddValueChanged(launch, launchChanged);
        descriptor.AddValueChanged(stop, stopChanged);
        var poll = controller.QueueInspection();
        var refresh = window.RefreshNowAsync();
        await poll.Started.Task;
        Check(launch.IsEnabled && stop.IsEnabled, "Ordinary held inspection flickered command buttons");
        Check(!Text(window, "RefreshSummaryText").Contains("확인 중", StringComparison.Ordinal), "Held automatic inspection exposed in-progress feedback");
        poll.Complete(snapshot);
        await refresh;
        descriptor.RemoveValueChanged(launch, launchChanged);
        descriptor.RemoveValueChanged(stop, stopChanged);
        Check(launchChanges == 0 && stopChanges == 0, "Read-only poll changed request availability");

        var held = controller.QueueInspection();
        var validation = controller.QueueInspection();
        refresh = window.RefreshNowAsync();
        await held.Started.Task;
        var request = window.RequestActionAsync(SidetalkCommandAction.Launch);
        Check(!launch.IsEnabled && !stop.IsEnabled, "Accepted click did not immediately reserve the command");
        Check(Text(window, "RefreshSummaryText").Contains("요청 접수", StringComparison.Ordinal), "Accepted click has no immediate feedback");
        CapturePendingRequest(window, output);
        await window.RequestActionAsync(SidetalkCommandAction.Launch);
        Check(controller.LaunchCount == 0 && confirmations == 0, "Command bypassed held poll or duplicate was confirmed");
        held.Complete(snapshot);
        await validation.Started.Task;
        Check(confirmations == 1 && controller.LaunchCount == 0, "Post-confirmation validation was bypassed");
        validation.Complete(snapshot);
        await request;
        await refresh;
        Check(controller.LaunchCount == 1 && confirmations == 1, "Queued click did not dispatch exactly once");
        Check(controller.MaxConcurrentCalls == 1, "Inspection overlapped controller command");
        window.Close();
        _checks++;
        return (launchChanges, stopChanges, controller.LaunchCount, confirmations);
    }

    private static void CapturePendingRequest(MainWindow window, string output)
    {
        // The poll and pending intent are both held by explicit barriers. Loaded
        // and timer dispatch cannot enter the controller while that intent owns
        // the window; the scenario closes the window as soon as it settles.
        window.Show();
        var width = window.Width;
        var height = window.Height;
        foreach (var compact in new[] { false, true })
        {
            window.Width = compact ? window.MinWidth : width;
            window.Height = compact ? window.MinHeight : height;
            window.Dispatcher.Invoke(window.UpdateLayout, DispatcherPriority.Render);
            var cancel = Button(window, "CancelActionButton");
            Check(cancel.Visibility == Visibility.Visible && cancel.IsEnabled, "Pending request cancellation is not visible");
            var bounds = cancel.TransformToAncestor(window).TransformBounds(new Rect(0, 0, cancel.ActualWidth, cancel.ActualHeight));
            Check(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= 0 && bounds.Top >= 0 &&
                bounds.Right <= window.ActualWidth && bounds.Bottom <= window.ActualHeight, "Pending cancellation is outside window bounds");
            window.RenderToPng(Path.Combine(output, compact ? "ui-pending-request-small-fixture.png" : "ui-pending-request-fixture.png"));
        }
        window.Width = width;
        window.Height = height;
        window.UpdateLayout();
    }

    private static async Task QueuedStop(AppSnapshot snapshot, SetupRecoveryPlan healthy)
    {
        using var controller = new DelayedController(snapshot, healthy);
        var window = CreateWindow(controller, snapshot, healthy);
        var poll = controller.QueueInspection();
        var validation = controller.QueueInspection();
        var refresh = window.RefreshNowAsync();
        var request = window.RequestActionAsync(SidetalkCommandAction.Stop);
        await window.RequestActionAsync(SidetalkCommandAction.Launch);
        poll.Complete(snapshot);
        await validation.Started.Task;
        validation.Complete(snapshot);
        await Task.WhenAll(refresh, request);
        Check(controller.StopCount == 1 && controller.LaunchCount == 0, "Queued stop duplicated or changed command intent");
        window.Close();
        _checks++;
    }

    private static async Task RoutedClickUsesSingleIntent(AppSnapshot snapshot, SetupRecoveryPlan healthy)
    {
        using var controller = new DelayedController(snapshot, healthy);
        var confirmations = 0;
        var window = CreateWindow(controller, snapshot, healthy, _ => { confirmations++; return Task.FromResult(true); });
        var poll = controller.QueueInspection();
        var validation = controller.QueueInspection();
        var followup = controller.QueueInspection();
        var refresh = window.RefreshNowAsync();
        await poll.Started.Task;
        Button(window, "LaunchButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Button(window, "LaunchButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Button(window, "StopButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(Text(window, "RefreshSummaryText").Contains("요청 접수", StringComparison.Ordinal), "Routed click handler did not show accepted intent");
        poll.Complete(snapshot);
        await validation.Started.Task;
        validation.Complete(snapshot);
        await followup.Started.Task;
        var followupTask = window.RefreshNowAsync();
        Check(controller.LaunchCount == 1 && controller.StopCount == 0 && confirmations == 1, "Routed double click dispatched/confirmed more than once");
        followup.Complete(snapshot);
        await Task.WhenAll(refresh, followupTask);
        window.Close();
        _checks++;
    }

    private static async Task FailedPollDropsClick(AppSnapshot snapshot, SetupRecoveryPlan healthy)
    {
        using var controller = new DelayedController(snapshot, healthy);
        var confirmations = 0;
        var window = CreateWindow(controller, snapshot, healthy, _ => { confirmations++; return Task.FromResult(true); });
        var poll = controller.QueueInspection();
        var refresh = window.RefreshNowAsync();
        var request = window.RequestActionAsync(SidetalkCommandAction.Launch);
        poll.Fail(new IOException("synthetic held inspection failure"));
        await Task.WhenAll(refresh, request);
        Check(controller.LaunchCount == 0 && confirmations == 0, "Failed poll dispatched or confirmed queued click");
        Check(!Button(window, "LaunchButton").IsEnabled && !Button(window, "StopButton").IsEnabled, "Failed poll retained stale command authority");
        await window.RefreshNowAsync();
        Check(controller.LaunchCount == 0 && Button(window, "LaunchButton").IsEnabled, "Successful retry replayed discarded intent");
        Check(!Text(window, "RefreshSummaryText").Contains("확인 실패", StringComparison.Ordinal),
            "Successful revalidation retained stale queued-request failure feedback");
        var returnedFailure = controller.QueueInspection();
        refresh = window.RefreshNowAsync();
        request = window.RequestActionAsync(SidetalkCommandAction.Launch);
        returnedFailure.Complete(snapshot with { InspectionFailure = InspectionFailure.Capture("synthetic", new IOException("synthetic returned inspection failure")) });
        await Task.WhenAll(refresh, request);
        Check(controller.LaunchCount == 0 && confirmations == 0 && !Button(window, "LaunchButton").IsEnabled,
            "Snapshot carrying inspection failure conferred queued command authority");
        window.Close();
        _checks++;
    }

    private static async Task FreshValidationBlocksClick(AppSnapshot snapshot, SetupRecoveryPlan healthy)
    {
        using var controller = new DelayedController(snapshot, healthy);
        var window = CreateWindow(controller, snapshot, healthy);
        var validation = controller.QueueInspection();
        var request = window.RequestActionAsync(SidetalkCommandAction.Launch);
        await validation.Started.Task;
        validation.Complete(snapshot with { CanLaunch = false, CanStop = false, ActionsPermitted = false, State = Readiness.Blocked });
        await request;
        Check(controller.LaunchCount == 0, "Action used stale CanLaunch after confirmation");
        await window.RefreshNowAsync();
        Check(controller.LaunchCount == 0, "A blocked action replayed after retry");
        var failed = controller.QueueInspection();
        request = window.RequestActionAsync(SidetalkCommandAction.Launch);
        await failed.Started.Task;
        failed.Fail(new IOException("synthetic validation failure"));
        await request;
        Check(controller.LaunchCount == 0, "Failed command validation dispatched action");
        window.Close();
        _checks++;
    }

    private static async Task DuplicateDuringCommand(AppSnapshot snapshot, SetupRecoveryPlan healthy)
    {
        using var controller = new DelayedController(snapshot, healthy);
        var command = controller.HoldLaunch();
        var window = CreateWindow(controller, snapshot, healthy);
        var request = window.RequestActionAsync(SidetalkCommandAction.Launch);
        await command.Started.Task;
        await window.RequestActionAsync(SidetalkCommandAction.Launch);
        await window.RequestActionAsync(SidetalkCommandAction.Stop);
        var inspections = controller.InspectCount;
        await window.RunScheduledInspectionAsync();
        Check(controller.InspectCount == inspections && controller.LaunchCount == 1 && controller.StopCount == 0, "Busy command admitted a competing poll/action");
        command.Complete(true);
        await request;
        Check(controller.MaxConcurrentCalls == 1, "Command overlapped another controller call");
        window.Close();
        _checks++;
    }

    private static async Task CancelledConfirmation(AppSnapshot snapshot, SetupRecoveryPlan healthy)
    {
        using var controller = new DelayedController(snapshot, healthy);
        var window = CreateWindow(controller, snapshot, healthy, _ => Task.FromResult(false));
        await window.RequestActionAsync(SidetalkCommandAction.Launch);
        await window.RefreshNowAsync();
        Check(controller.LaunchCount == 0, "Cancelled confirmation was replayed by refresh");
        window.Close();
        _checks++;
    }

    private static async Task CancelPendingAndLateConfirmation(AppSnapshot snapshot, SetupRecoveryPlan healthy)
    {
        using var controller = new DelayedController(snapshot, healthy);
        var confirmation = new Barrier<bool>();
        var window = CreateWindow(controller, snapshot, healthy, _ => confirmation.WaitAsync());
        var poll = controller.QueueInspection();
        var refresh = window.RefreshNowAsync();
        var request = window.RequestActionAsync(SidetalkCommandAction.Launch);
        window.CancelPendingAction();
        poll.Complete(snapshot);
        await Task.WhenAll(refresh, request);
        Check(controller.LaunchCount == 0 && !confirmation.Started.Task.IsCompleted, "Cancelled waiting click reached confirmation");

        request = window.RequestActionAsync(SidetalkCommandAction.Launch);
        await confirmation.Started.Task;
        window.CancelPendingAction();
        var newer = snapshot with { Title = "newer observation", CheckedAt = snapshot.CheckedAt.AddSeconds(2) };
        controller.Snapshot = newer;
        var calls = controller.InspectCount;
        await window.RefreshNowAsync();
        Check(controller.InspectCount == calls, "Cancelled confirmation relinquished ownership before draining");
        window.ApplySnapshot(newer);
        confirmation.Complete(true);
        await request;
        Check(controller.LaunchCount == 0, "Late confirmation dispatched cancelled click");
        Check(Text(window, "StatusTitleText") == newer.Title, "Late cancelled continuation overwrote newer observation");
        window.Close();
        _checks++;
    }

    private static async Task ClosedWhileWaiting(AppSnapshot snapshot, SetupRecoveryPlan healthy)
    {
        using var controller = new DelayedController(snapshot, healthy);
        var confirmations = 0;
        var window = CreateWindow(controller, snapshot, healthy, _ => { confirmations++; return Task.FromResult(true); });
        var poll = controller.QueueInspection();
        var refresh = window.RefreshNowAsync();
        var request = window.RequestActionAsync(SidetalkCommandAction.Launch);
        window.Close();
        var closedTitle = Text(window, "StatusTitleText");
        poll.Complete(snapshot with { Title = "late result after close" });
        await Task.WhenAll(refresh, request);
        Check(controller.LaunchCount == 0 && confirmations == 0, "Closed window executed queued click");
        Check(Text(window, "StatusTitleText") == closedTitle, "Closed window applied late inspection");
        _checks++;
    }

    private static async Task ClosedDuringConfirmation(AppSnapshot snapshot, SetupRecoveryPlan healthy)
    {
        using var controller = new DelayedController(snapshot, healthy);
        var confirmation = new Barrier<bool>();
        var window = CreateWindow(controller, snapshot, healthy, _ => confirmation.WaitAsync());
        var request = window.RequestActionAsync(SidetalkCommandAction.Stop);
        await confirmation.Started.Task;
        window.Close();
        confirmation.Complete(true);
        await request;
        Check(controller.StopCount == 0 && controller.InspectCount == 0, "Closed confirmation dispatched or revalidated old click");
        _checks++;
    }

    private static async Task FailedCommandDoesNotReplay(AppSnapshot snapshot, SetupRecoveryPlan healthy)
    {
        using var controller = new DelayedController(snapshot, healthy);
        var command = controller.HoldLaunch();
        var window = CreateWindow(controller, snapshot, healthy);
        var request = window.RequestActionAsync(SidetalkCommandAction.Launch);
        await command.Started.Task;
        command.Fail(new IOException("synthetic command failure"));
        await request;
        await window.RefreshNowAsync();
        Check(controller.LaunchCount == 1, "Failure/retry replayed old command intent");
        window.Close();
        _checks++;
    }

    private static async Task CancelAndCloseDuringValidation(AppSnapshot snapshot, SetupRecoveryPlan healthy)
    {
        using var controller = new DelayedController(snapshot, healthy);
        var window = CreateWindow(controller, snapshot, healthy);
        var validation = controller.QueueInspection();
        var request = window.RequestActionAsync(SidetalkCommandAction.Launch);
        await validation.Started.Task;
        window.CancelPendingAction();
        validation.Complete(snapshot);
        await request;
        Check(controller.LaunchCount == 0, "Cancelled validation later dispatched command");
        await window.RefreshNowAsync();
        validation = controller.QueueInspection();
        request = window.RequestActionAsync(SidetalkCommandAction.Stop);
        await validation.Started.Task;
        window.Close();
        var closedTitle = Text(window, "StatusTitleText");
        validation.Complete(snapshot with { Title = "late validation after close" });
        await request;
        Check(controller.StopCount == 0 && Text(window, "StatusTitleText") == closedTitle, "Closed validation dispatched command or updated UI");
        _checks++;
    }

    private static async Task RecoveryCompetition(AppSnapshot snapshot, SetupRecoveryPlan healthy, SetupRecoveryPlan approval)
    {
        var automatic = approval with { State = SetupRecoveryState.AutomaticallyApplicable, AllowAutomatic = true, RequiresApproval = false };
        using var controller = new DelayedController(snapshot, automatic);
        var recovery = controller.HoldRecovery();
        var window = CreateWindow(controller, snapshot, automatic);
        var scheduled = window.RunScheduledInspectionAsync();
        await recovery.Started.Task;
        await window.RequestActionAsync(SidetalkCommandAction.Launch);
        await window.RequestActionAsync(SidetalkCommandAction.Stop);
        await window.RefreshNowAsync();
        Check(controller.LaunchCount == 0 && controller.StopCount == 0 && controller.RecoveryCount == 1, "Recovery admitted competing command");
        controller.Plan = healthy;
        recovery.Complete(snapshot);
        await scheduled;
        await window.RefreshNowAsync();
        Check(controller.LaunchCount == 0 && controller.StopCount == 0, "Recovery completion replayed rejected click");
        Check(controller.MaxConcurrentCalls == 1, "Recovery and inspection/action overlapped");
        window.Close();
        _checks++;
    }

    private static async Task PendingIntentPrecedesAutomaticRecovery(AppSnapshot snapshot, SetupRecoveryPlan healthy, SetupRecoveryPlan approval)
    {
        var automatic = approval with { State = SetupRecoveryState.AutomaticallyApplicable, AllowAutomatic = true, RequiresApproval = false };
        using var controller = new DelayedController(snapshot, automatic);
        var window = CreateWindow(controller, snapshot, healthy);
        var poll = controller.QueueInspection();
        var validation = controller.QueueInspection();
        var scheduled = window.RunScheduledInspectionAsync();
        var request = window.RequestActionAsync(SidetalkCommandAction.Launch);
        poll.Complete(snapshot);
        await validation.Started.Task;
        Check(controller.RecoveryCount == 0 && controller.LaunchCount == 0, "Pending intent raced automatic recovery");
        controller.Plan = healthy;
        validation.Complete(snapshot);
        await Task.WhenAll(scheduled, request);
        Check(controller.LaunchCount == 1 && controller.RecoveryCount == 0, "Freshly valid pending intent was lost to optional preparation");
        Check(controller.MaxConcurrentCalls == 1, "Pending intent/recovery controller calls overlapped");
        window.Close();
        _checks++;
    }

    private static async Task<int> MinimizeAndRestore(AppSnapshot snapshot, SetupRecoveryPlan healthy)
    {
        using var controller = new DelayedController(snapshot, healthy);
        var window = CreateWindow(controller, snapshot, healthy);
        await window.SetMinimizedAsync(true);
        var before = Text(window, "CheckedAtText");
        controller.Snapshot = snapshot with { CheckedAt = snapshot.CheckedAt.AddMinutes(1), Title = "minimized observation" };
        var count = controller.InspectCount;
        await window.RunScheduledInspectionAsync();
        Check(controller.InspectCount > count, "Minimization stopped required recovery/lifecycle inspection");
        var updates = Text(window, "CheckedAtText") == before ? 0 : 1;
        Check(updates == 0, "Minimized poll redrew observed state");
        var lateMinimizedCheck = controller.QueueInspection();
        var minimizedPoll = window.RunScheduledInspectionAsync();
        await lateMinimizedCheck.Started.Task;
        var restoreCheck = controller.QueueInspection();
        var restore = window.SetMinimizedAsync(false);
        Check(!restoreCheck.Started.Task.IsCompleted, "Restore inspection overlapped previous minimized poll");
        lateMinimizedCheck.Complete(snapshot with { CheckedAt = snapshot.CheckedAt.AddMinutes(3), Title = "obsolete pre-restore response" });
        await restoreCheck.Started.Task;
        Check(Text(window, "StatusTitleText") != "obsolete pre-restore response", "Late pre-restore response replaced foreground state");
        Check(!Button(window, "LaunchButton").IsEnabled && !Button(window, "StopButton").IsEnabled, "Restore admitted stale command authority");
        var ignored = window.RequestActionAsync(SidetalkCommandAction.Launch);
        restoreCheck.Complete(snapshot with { CheckedAt = snapshot.CheckedAt.AddMinutes(2), Title = "restored current observation" });
        await Task.WhenAll(restore, ignored, minimizedPoll);
        Check(Text(window, "StatusTitleText") == "restored current observation", "Restore did not apply immediate current observation");
        Check(controller.LaunchCount == 0, "Restoring window queued stale click");
        window.Close();
        _checks++;
        return updates;
    }

    private static async Task ExportDialogOwnsOperation(AppSnapshot snapshot, SetupRecoveryPlan healthy, SetupRecoveryPlan approval)
    {
        using var controller = new DelayedController(snapshot, healthy);
        var window = CreateWindow(controller, snapshot, approval);
        var selectors = 0;
        var nestedSelectorCalled = false;
        void AssertReserved()
        {
            selectors++;
            Check(!Button(window, "LaunchButton").IsEnabled && !Button(window, "StopButton").IsEnabled &&
                !Button(window, "PrepareRecoveryButton").IsEnabled, "Native export selector did not own operation availability");
            var inspections = controller.InspectCount;
            var timer = window.RunScheduledInspectionAsync();
            var launch = window.RequestActionAsync(SidetalkCommandAction.Launch);
            var stop = window.RequestActionAsync(SidetalkCommandAction.Stop);
            Check(timer.IsCompletedSuccessfully && launch.IsCompletedSuccessfully && stop.IsCompletedSuccessfully,
                "Nested modal dispatch retained competing work");
            Check(controller.InspectCount == inspections && controller.LaunchCount == 0 && controller.StopCount == 0 && controller.RecoveryCount == 0,
                "Nested modal dispatch invoked inspection, action, or recovery");
        }

        await window.ExportSetupAsync(() =>
        {
            AssertReserved();
            var nested = window.ExportDiagnosticsAsync(() => { nestedSelectorCalled = true; return null; });
            Check(nested.IsCompletedSuccessfully && !nestedSelectorCalled, "Setup dialog admitted nested diagnostics selector");
            return null;
        });
        Check(Button(window, "LaunchButton").IsEnabled, "Cancelled setup selector retained operation ownership");
        window.ApplyRecoveryPlan(approval);
        await window.ExportDiagnosticsAsync(() =>
        {
            AssertReserved();
            var nested = window.ExportSetupAsync(() => { nestedSelectorCalled = true; return null; });
            Check(nested.IsCompletedSuccessfully && !nestedSelectorCalled, "Diagnostics dialog admitted nested setup selector");
            return null;
        });
        Check(selectors == 2 && Button(window, "LaunchButton").IsEnabled && Button(window, "StopButton").IsEnabled,
            "Cancelled export selectors did not release operation ownership");
        Check(controller.ExportCount == 0 && controller.LaunchCount == 0 && controller.StopCount == 0 && controller.RecoveryCount == 0,
            "Cancelled native selectors dispatched work");
        window.Close();
        _checks++;
    }

    private static MainWindow CreateWindow(DelayedController controller, AppSnapshot snapshot, SetupRecoveryPlan plan,
        Func<SidetalkCommandAction, Task<bool>>? confirmation = null)
    {
        var window = new MainWindow(controller, confirmation ?? (_ => Task.FromResult(true)));
        window.ApplySnapshot(snapshot);
        window.ApplyRecoveryPlan(plan);
        return window;
    }

    private static Button Button(MainWindow window, string name) => (Button)window.FindName(name);
    private static string Text(MainWindow window, string name) => ((TextBlock)window.FindName(name)).Text;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Interaction assertion failed: " + message);
    }

    private static void Pump(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            // Only a deadlock watchdog; every tested ordering comes from a completion barrier.
            var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            watchdog.Tick += (_, _) => { watchdog.Stop(); frame.Continue = false; };
            task.GetAwaiter().OnCompleted(() => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
            watchdog.Start();
            Dispatcher.PushFrame(frame);
            watchdog.Stop();
            Check(task.IsCompleted, "Completion barrier did not settle before deadlock watchdog");
        }
        task.GetAwaiter().GetResult();
    }

    internal sealed class Barrier<T>
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<T> WaitAsync() { Started.TrySetResult(true); return _completion.Task; }
        public void Complete(T value) => _completion.SetResult(value);
        public void Fail(Exception exception) => _completion.SetException(exception);
    }

    private sealed class DelayedController(AppSnapshot snapshot, SetupRecoveryPlan plan) : ISidetalkController
    {
        private readonly Queue<Barrier<AppSnapshot>> _inspections = new();
        private Barrier<bool>? _launch;
        private Barrier<AppSnapshot>? _recovery;
        private int _activeCalls;
        public AppSnapshot Snapshot { get; set; } = snapshot;
        public SetupRecoveryPlan Plan { get; set; } = plan;
        public int InspectCount { get; private set; }
        public int LaunchCount { get; private set; }
        public int StopCount { get; private set; }
        public int RecoveryCount { get; private set; }
        public int ExportCount { get; private set; }
        public int MaxConcurrentCalls { get; private set; }
        public string RecoveryHistoryPath => Plan.HistoryPath;
        public Barrier<AppSnapshot> QueueInspection() { var barrier = new Barrier<AppSnapshot>(); _inspections.Enqueue(barrier); return barrier; }
        public Barrier<bool> HoldLaunch() => _launch = new();
        public Barrier<AppSnapshot> HoldRecovery() => _recovery = new();
        public async Task<AppSnapshot> InspectAsync()
        {
            InspectCount++;
            Enter();
            try { return _inspections.TryDequeue(out var barrier) ? await barrier.WaitAsync() : Snapshot; }
            finally { _activeCalls--; }
        }
        public Task<SetupRecoveryPlan> InspectSetupRecoveryAsync() => Task.FromResult(Plan);
        public Task<SetupRecoveryEvidenceInspection> InspectInterruptedEvidenceAsync() => Task.FromResult(new SetupRecoveryEvidenceInspection([], false, false, "synthetic", "", "", ""));
        public async Task LaunchAsync()
        {
            LaunchCount++;
            Enter();
            try { if (_launch is { } barrier) { _launch = null; await barrier.WaitAsync(); } }
            finally { _activeCalls--; }
        }
        public Task StopAsync() { StopCount++; Enter(); _activeCalls--; return Task.CompletedTask; }
        public Task<AppSnapshot> PrepareAsync(SetupRecoveryPlan approvedPlan, bool allowAutomatic, IProgress<string>? progress = null) => PrepareAutomaticallyAsync(progress);
        public async Task<AppSnapshot> PrepareAutomaticallyAsync(IProgress<string>? progress = null)
        {
            RecoveryCount++;
            Enter();
            try { return _recovery is { } barrier ? await barrier.WaitAsync() : Snapshot; }
            finally { _activeCalls--; }
        }
        public Task<AppSnapshot> ReconcileInterruptedEvidenceAsync(SetupRecoveryEvidenceInspection approvedInspection, IProgress<string>? progress = null) => throw new InvalidOperationException("Unexpected evidence write");
        public Task RevokeAutomaticRecoveryAsync() => throw new InvalidOperationException("Unexpected consent write");
        public Task<string> ExportSetupAsync(string folder) { ExportCount++; throw new InvalidOperationException("Unexpected export write"); }
        public Task<string> ExportDiagnosticsAsync(string path) { ExportCount++; throw new InvalidOperationException("Unexpected export write"); }
        private void Enter() { _activeCalls++; MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, _activeCalls); }
        public void Dispose() { }
    }
}
