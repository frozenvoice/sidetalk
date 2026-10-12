using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sidetalk.App;
using Sidetalk.Core;

internal static class QuickLaunchUiChecks
{
    private static int cases;

    internal static void Run(AppSnapshot snapshot, SetupRecoveryPlan healthy, string output)
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { Pump(RunAsync(snapshot with { CommandPending = false, CurrentConfigurationVerified = true }, healthy, output)); }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
    }

    private static async Task RunAsync(AppSnapshot snapshot, SetupRecoveryPlan healthy, string output)
    {
        Console.WriteLine("RUN quick UI: GrantedConsentSkipsOnlyConfirmation");
        await GrantedConsentSkipsOnlyConfirmation(snapshot, healthy);
        Console.WriteLine("RUN quick UI: MissingConsentKeepsConfirmation");
        await MissingConsentKeepsConfirmation(snapshot, healthy);
        Console.WriteLine("RUN quick UI: RejectedConfirmationNeverDispatches");
        await RejectedConfirmationNeverDispatches(snapshot, healthy);
        Console.WriteLine("RUN quick UI: FailedInspectionShowsGui");
        await FailedInspectionShowsGui(snapshot, healthy);
        Console.WriteLine("RUN quick UI: ExistingProcessesAreReported");
        await ExistingProcessesAreReported(snapshot, healthy);
        Console.WriteLine("RUN quick UI: CancelDuringInspection");
        await CancelDuringInspection(snapshot, healthy);
        Console.WriteLine("RUN quick UI: CancelUnknownInspectionNeedsGui");
        await CancelUnknownInspectionNeedsGui(snapshot, healthy);
        Console.WriteLine("RUN quick UI: CancelDuringConfirmation");
        await CancelDuringConfirmation(snapshot, healthy);
        Console.WriteLine("RUN quick UI: DispatchedRequestDrainsAfterCancel");
        await DispatchedRequestDrainsAfterCancel(snapshot, healthy);
        Console.WriteLine("RUN quick UI: ChangedScopeDoesNotReplay");
        await ChangedScopeDoesNotReplay(snapshot, healthy);
        Console.WriteLine("RUN quick UI: UncertainAndFailureNeedGui");
        await UncertainAndFailureNeedGui(snapshot, healthy);
        StartupIsIndependentAndDefaultOff();
        Console.WriteLine("RUN quick UI: ExistingGuiOwnsRequest");
        await ExistingGuiOwnsRequest(snapshot, healthy);
        Console.WriteLine("RUN quick UI: ScheduledPollKeepsSettingsStable");
        await ScheduledPollKeepsSettingsStable(snapshot, healthy);
        Console.WriteLine("RUN quick UI: SettingsReloadWaitsForPoll");
        await SettingsReloadWaitsForPoll(snapshot, healthy);
        Console.WriteLine("RUN quick UI: ConsentSaveWaitsForPoll");
        await ConsentSaveWaitsForPoll(snapshot, healthy);
        Console.WriteLine("RUN quick UI: ClosedQueuedSettingsCannotStart");
        await ClosedQueuedSettingsCannotStart(snapshot, healthy);
        Console.WriteLine("RUN quick UI: ClosedQueuedConsentCannotWrite");
        await ClosedQueuedConsentCannotWrite(snapshot, healthy);
        Console.WriteLine("RUN quick UI: SettingsReadDrainsBeforeExternalRequest");
        await SettingsReadDrainsBeforeExternalRequest(snapshot, healthy);
        Console.WriteLine("RUN quick UI: ClosedSettingsReadCannotApply");
        await ClosedSettingsReadCannotApply(snapshot, healthy);
        Console.WriteLine("RUN quick UI: SameUserPipeCarriesIdentity");
        await PipeInitializationFailureIsSurfaced();
        await SameUserPipeCarriesIdentity();
        await TruncatedPipeAndDelayedReceiptDoNotLoseNextRequest();
        Console.WriteLine("RUN quick UI: PipeAcknowledgesBeforeConfirmationCompletes");
        await PipeAcknowledgesBeforeConfirmationCompletes();
        ClosingLauncherCancelsButWaits();
        RenderSettings(snapshot, healthy, output);
        File.WriteAllText(Path.Combine(output, "quick-launch-metrics.json"), JsonSerializer.Serialize(new
        {
            Scope = "Synthetic controllers, WPF and private same-user pipe; no actual accounts or user startup settings",
            GrantedConsentConfirmationCount = 0,
            GrantedConsentLaunchCount = 1,
            MissingConsentConfirmationCount = 1,
            StartupDefault = false,
            CompletedScenarios = cases
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Quick-launch UI checks: {cases} synchronized scenarios passed; approved confirmation=0, launch=1; unapproved confirmation=1; startup default=off.");
    }

    private static async Task GrantedConsentSkipsOnlyConfirmation(AppSnapshot snapshot, SetupRecoveryPlan plan)
    {
        using var controller = new Fake(snapshot, plan, consent: true);
        var confirmations = 0;
        var result = await new QuickLaunchFlow(controller).RunAsync(() => { confirmations++; return Task.FromResult(true); }, default);
        Check(confirmations == 0 && controller.QuickCalls == 1 && controller.LastApproval is null,
            "Saved consent did not use one Core request without UI confirmation");
        Check(!QuickLaunchFlow.NeedsGui(result), "Settled owned launch unnecessarily retained the launcher");
        cases++;
    }

    private static async Task MissingConsentKeepsConfirmation(AppSnapshot snapshot, SetupRecoveryPlan plan)
    {
        using var controller = new Fake(snapshot, plan);
        var confirmations = 0;
        var result = await new QuickLaunchFlow(controller).RunAsync(() => { confirmations++; return Task.FromResult(true); }, default);
        Check(confirmations == 1 && controller.QuickCalls == 1 && ReferenceEquals(controller.LastApproval, controller.Status.Approval),
            "One-time approval was missing or replaced");
        Check(result.Outcome == QuickLaunchOutcome.Launched, "One-time confirmation failed to route to Core");
        cases++;
    }

    private static async Task RejectedConfirmationNeverDispatches(AppSnapshot snapshot, SetupRecoveryPlan plan)
    {
        using var controller = new Fake(snapshot, plan);
        var result = await new QuickLaunchFlow(controller).RunAsync(() => Task.FromResult(false), default);
        Check(result.Outcome == QuickLaunchOutcome.Cancelled && controller.QuickCalls == 0,
            "Rejected confirmation dispatched a command");
        cases++;
    }

    private static async Task FailedInspectionShowsGui(AppSnapshot snapshot, SetupRecoveryPlan plan)
    {
        using var controller = new Fake(snapshot, plan, consent: true);
        controller.Status = controller.Status with { Snapshot = snapshot with { State = Readiness.Blocked, CurrentConfigurationVerified = false, CanLaunch = false } };
        var result = await new QuickLaunchFlow(controller).RunAsync(() => throw new InvalidOperationException("Unexpected confirmation"), default);
        Check(controller.QuickCalls == 0 && QuickLaunchFlow.NeedsGui(result), "Failed preflight retried or exited invisibly");
        cases++;
    }

    private static async Task ExistingProcessesAreReported(AppSnapshot snapshot, SetupRecoveryPlan plan)
    {
        using var controller = new Fake(snapshot, plan);
        var running = snapshot with { State = Readiness.RunningUnverified, CanLaunch = false, BoxProcessIds = new[] { 10001 } };
        controller.Status = controller.Status with { Snapshot = running };
        controller.Result = new(QuickLaunchOutcome.AlreadyRunning, running, "전용 환경이 이미 실행 중입니다. 새 프로세스를 만들지 않았습니다.");
        var result = await new QuickLaunchFlow(controller).RunAsync(() => throw new InvalidOperationException("Already-running confirmation"), default);
        Check(result.Outcome == QuickLaunchOutcome.AlreadyRunning && QuickLaunchFlow.NeedsGui(result), "Existing process was not reported");
        cases++;
    }

    private static async Task CancelDuringInspection(AppSnapshot snapshot, SetupRecoveryPlan plan)
    {
        using var controller = new Fake(snapshot, plan, consent: true);
        using var cancellation = new CancellationTokenSource();
        var barrier = new InteractionChecks.Barrier<QuickLaunchStatus>();
        controller.Inspection = barrier.WaitAsync;
        var task = new QuickLaunchFlow(controller).RunAsync(() => throw new InvalidOperationException("Late confirmation"), cancellation.Token);
        await barrier.Started.Task;
        cancellation.Cancel();
        Check(!task.IsCompleted, "Cancellation abandoned the active inspection");
        barrier.Complete(controller.Status);
        var result = await task;
        Check(result.Outcome == QuickLaunchOutcome.Cancelled && controller.QuickCalls == 0, "Late inspection launched a cancelled request");
        cases++;
    }

    private static async Task CancelDuringConfirmation(AppSnapshot snapshot, SetupRecoveryPlan plan)
    {
        using var controller = new Fake(snapshot, plan);
        using var cancellation = new CancellationTokenSource();
        var barrier = new InteractionChecks.Barrier<bool>();
        var task = new QuickLaunchFlow(controller).RunAsync(barrier.WaitAsync, cancellation.Token);
        await barrier.Started.Task;
        cancellation.Cancel();
        barrier.Complete(true);
        var result = await task;
        Check(result.Outcome == QuickLaunchOutcome.Cancelled && controller.QuickCalls == 0, "Late approval dispatched a cancelled request");
        cases++;
    }

    private static async Task CancelUnknownInspectionNeedsGui(AppSnapshot snapshot, SetupRecoveryPlan plan)
    {
        using var controller = new Fake(snapshot, plan, consent: true);
        using var cancellation = new CancellationTokenSource();
        var barrier = new InteractionChecks.Barrier<QuickLaunchStatus>();
        controller.Inspection = barrier.WaitAsync;
        var task = new QuickLaunchFlow(controller).RunAsync(() => Task.FromResult(true), cancellation.Token);
        await barrier.Started.Task;
        cancellation.Cancel();
        barrier.Complete(controller.Status with { Snapshot = snapshot with { State = Readiness.Unknown, CommandPending = null } });
        var result = await task;
        Check(controller.QuickCalls == 0 && result.Outcome == QuickLaunchOutcome.Cancelled && QuickLaunchFlow.NeedsGui(result),
            "Unknown journal state after cancellation silently ended the launcher");
        cases++;
    }

    private static async Task DispatchedRequestDrainsAfterCancel(AppSnapshot snapshot, SetupRecoveryPlan plan)
    {
        using var controller = new Fake(snapshot, plan, consent: true);
        using var cancellation = new CancellationTokenSource();
        var barrier = new InteractionChecks.Barrier<QuickLaunchResult>();
        controller.Launch = _ => barrier.WaitAsync();
        var task = new QuickLaunchFlow(controller).RunAsync(() => Task.FromResult(true), cancellation.Token);
        await barrier.Started.Task;
        cancellation.Cancel();
        Check(!task.IsCompleted, "Dispatched command's journal wait was abandoned");
        barrier.Complete(controller.Result);
        var result = await task;
        Check(controller.QuickCalls == 1 && result.Outcome == QuickLaunchOutcome.Launched, "Cancellation replayed or mislabelled a settled command");
        cases++;
    }

    private static async Task ChangedScopeDoesNotReplay(AppSnapshot snapshot, SetupRecoveryPlan plan)
    {
        using var controller = new Fake(snapshot, plan, consent: true);
        controller.Result = new(QuickLaunchOutcome.ConfirmationRequired, snapshot, "확인 범위가 달라졌습니다.");
        var result = await new QuickLaunchFlow(controller).RunAsync(() => Task.FromResult(true), default);
        Check(controller.QuickCalls == 1 && QuickLaunchFlow.NeedsGui(result), "Changed scope auto-confirmed or replayed");
        cases++;
    }

    private static async Task UncertainAndFailureNeedGui(AppSnapshot snapshot, SetupRecoveryPlan plan)
    {
        foreach (var outcome in new[] { QuickLaunchOutcome.Uncertain, QuickLaunchOutcome.Blocked, QuickLaunchOutcome.Launched })
        {
            using var controller = new Fake(snapshot, plan, consent: true);
            controller.Result = new(outcome, snapshot with { CommandPending = true }, "기록 확인이 필요합니다.");
            var result = await new QuickLaunchFlow(controller).RunAsync(() => Task.FromResult(true), default);
            Check(QuickLaunchFlow.NeedsGui(result) && controller.QuickCalls == 1, "Pending/failure result closed the launcher or retried");
        }
        cases++;
    }

    private static void StartupIsIndependentAndDefaultOff()
    {
        Check(!QuickLaunchFlow.StartupAllowed(true, false, false, 1, new(new(), false)), "Startup default was enabled");
        var enabled = new UserLaunchIntegrationStatus(new(LoginStartup: true), false);
        Check(QuickLaunchFlow.StartupAllowed(true, false, false, 1, enabled), "Explicit non-elevated login selection was ignored");
        Check(!QuickLaunchFlow.StartupAllowed(false, false, false, 1, enabled) &&
            !QuickLaunchFlow.StartupAllowed(true, true, false, 1, enabled) &&
            !QuickLaunchFlow.StartupAllowed(true, false, true, 1, enabled) &&
            !QuickLaunchFlow.StartupAllowed(true, false, false, 0, enabled) &&
            !QuickLaunchFlow.StartupAllowed(true, false, false, 1, enabled with { NeedsUpdate = true }),
            "Startup accepted a system, elevated, noninteractive, or obsolete installation context");
        cases++;
    }

    private static async Task ExistingGuiOwnsRequest(AppSnapshot snapshot, SetupRecoveryPlan plan)
    {
        using var controller = new Fake(snapshot, plan);
        var window = new MainWindow(controller, _ => Task.FromResult(true));
        window.ApplySnapshot(snapshot);
        var poll = new InteractionChecks.Barrier<AppSnapshot>();
        controller.MainInspection = poll.WaitAsync;
        var refresh = window.RefreshNowAsync();
        await poll.Started.Task;
        var request = new InteractionChecks.Barrier<bool>();
        var first = window.RunExternalQuickLaunchAsync(async () => { await request.WaitAsync(); });
        var secondCalled = false;
        var second = await window.RunExternalQuickLaunchAsync(() => { secondCalled = true; return Task.CompletedTask; });
        await window.RequestActionAsync(SidetalkCommandAction.Launch);
        Check(!second && !secondCalled && controller.NormalLaunchCalls == 0, "GUI and shortcut requests overlapped");
        Check(!request.Started.Task.IsCompleted, "External request bypassed the current whole inspection");
        controller.MainInspection = null;
        poll.Complete(snapshot);
        await request.Started.Task;
        Check(!((Button)window.FindName("LaunchButton")).IsEnabled, "External command lost GUI operation ownership");
        request.Complete(true);
        Check(await first, "First external request was discarded");
        await refresh;
        window.Close();
        cases++;
    }

    private static async Task SameUserPipeCarriesIdentity()
    {
        var name = "synthetic-" + Guid.NewGuid().ToString("N");
        using var primary = new InstanceRequestBroker(name);
        using var secondary = new InstanceRequestBroker(name);
        Check(primary.TryAcquire() && !secondary.TryAcquire(), "Two shortcut instances acquired ownership");
        var arrived = new TaskCompletionSource<InstanceRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        await primary.ListenAsync(request => { arrived.TrySetResult(request); return Task.CompletedTask; });
        var expected = new InstanceRequest(Guid.NewGuid(), true, true);
        await secondary.ForwardAsync(expected);
        Check(await arrived.Task == expected, "Pipe changed request identity or startup kind");
        var next = new InstanceRequest(Guid.NewGuid(), true, false);
        await secondary.ForwardAsync(next);
        cases++;
    }

    private static async Task PipeInitializationFailureIsSurfaced()
    {
        var attempts = 0;
        using var broker = new InstanceRequestBroker("synthetic-failure-" + Guid.NewGuid().ToString("N"), _ =>
        {
            Interlocked.Increment(ref attempts);
            throw new UnauthorizedAccessException("Synthetic pipe initialization denial");
        });
        Check(broker.TryAcquire(), "Failure fixture did not acquire its own mutex");
        try
        {
            await broker.ListenAsync(_ => throw new InvalidOperationException("Failed listener dispatched a request"));
            throw new InvalidOperationException("Pipe initialization failure was swallowed");
        }
        catch (UnauthorizedAccessException error) when (error.Message == "Synthetic pipe initialization denial") { }
        Check(attempts == 1 && broker.Completion.IsFaulted, "Pipe initialization was retried or left an unobserved pending listener");
        cases++;
    }

    private static async Task TruncatedPipeAndDelayedReceiptDoNotLoseNextRequest()
    {
        var scope = "synthetic-protocol-" + Guid.NewGuid().ToString("N");
        string? pipeName = null;
        using var primary = new InstanceRequestBroker(scope, name =>
        {
            pipeName = name;
            return new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        });
        using var secondary = new InstanceRequestBroker(scope);
        Check(primary.TryAcquire() && !secondary.TryAcquire(), "Protocol fixture lost ownership");
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await primary.ListenAsync(_ => { Interlocked.Increment(ref calls); received.TrySetResult(); return Task.CompletedTask; });
        using (var truncated = new NamedPipeClientStream(".", pipeName!, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await truncated.ConnectAsync(3000);
            await truncated.WriteAsync(new byte[] { 1, 1 });
        }
        using (var delayed = new NamedPipeClientStream(".", pipeName!, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await delayed.ConnectAsync(3000);
            var payload = new byte[18];
            payload[0] = payload[1] = 1;
            Guid.NewGuid().TryWriteBytes(payload.AsSpan(2));
            await delayed.WriteAsync(payload);
            await received.Task;
            Check(calls == 1, "Truncated sender dispatched work");
            var ack = new byte[1];
            await delayed.ReadExactlyAsync(ack);
            Check(ack[0] == 1, "Delayed client lost its acknowledgement");
            await delayed.WriteAsync(new byte[] { 1 });
        }
        await secondary.ForwardAsync(new(Guid.NewGuid(), true, false));
        Check(calls == 2 && !primary.Completion.IsCompleted, "Broken or delayed client disabled the next request");
        cases++;
    }

    private static async Task ScheduledPollKeepsSettingsStable(AppSnapshot snapshot, SetupRecoveryPlan plan)
    {
        using var controller = new Fake(snapshot, plan);
        var window = new MainWindow(controller, _ => Task.FromResult(true));
        window.ApplySnapshot(snapshot);
        await window.ReloadConvenienceAsync();
        var controls = new UIElement[]
        {
            (Button)window.FindName("SaveQuickConsentButton"),
            (Button)window.FindName("ReloadQuickSettingsButton"),
            (CheckBox)window.FindName("QuickConsentCheckBox")
        };
        var transitions = 0;
        DependencyPropertyChangedEventHandler changed = (_, _) => transitions++;
        foreach (var control in controls)
        {
            Check(control.IsEnabled, "Loaded convenience settings were unavailable before polling");
            control.IsEnabledChanged += changed;
        }
        try
        {
            for (var cycle = 0; cycle < 3; cycle++)
            {
                var poll = new InteractionChecks.Barrier<AppSnapshot>();
                controller.MainInspection = poll.WaitAsync;
                var scheduled = window.RunScheduledInspectionAsync();
                await poll.Started.Task;
                Check(controls.All(control => control.IsEnabled), "Scheduled inspection disabled convenience settings");
                Check(transitions == 0, "Held scheduled inspection changed convenience availability");
                poll.Complete(snapshot with { CheckedAt = snapshot.CheckedAt.AddSeconds(cycle + 1) });
                await scheduled;
                Check(controls.All(control => control.IsEnabled) && transitions == 0,
                    "Completed scheduled inspection flickered convenience settings");
            }
        }
        finally
        {
            foreach (var control in controls) control.IsEnabledChanged -= changed;
            window.Close();
        }
        cases++;
    }

    private static async Task SettingsReloadWaitsForPoll(AppSnapshot snapshot, SetupRecoveryPlan plan)
    {
        using var controller = new Fake(snapshot, plan);
        var window = new MainWindow(controller, _ => Task.FromResult(true));
        window.ApplySnapshot(snapshot);
        await window.ReloadConvenienceAsync();
        var poll = new InteractionChecks.Barrier<AppSnapshot>();
        controller.MainInspection = poll.WaitAsync;
        var scheduled = window.RunScheduledInspectionAsync();
        await poll.Started.Task;
        var settings = new InteractionChecks.Barrier<QuickLaunchStatus>();
        controller.Inspection = settings.WaitAsync;
        var reloadButton = (Button)window.FindName("ReloadQuickSettingsButton");
        Check(reloadButton.IsEnabled, "Polling prevented the explicit settings reload click");
        reloadButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var reload = window.ReloadConvenienceAsync();
        Check(!reloadButton.IsEnabled && !((Button)window.FindName("SaveQuickConsentButton")).IsEnabled,
            "Queued settings reload did not reserve convenience controls");
        Check(!reload.IsCompleted && !settings.Started.Task.IsCompleted && controller.QuickInspectionCalls == 1,
            "Settings reload overlapped the held scheduled poll");
        await window.RequestActionAsync(SidetalkCommandAction.Launch);
        await window.RunScheduledInspectionAsync();
        Check(controller.NormalLaunchCalls == 0 && controller.MainInspectionCalls == 1,
            "Queued settings reload admitted another action or poll");
        controller.MainInspection = null;
        poll.Complete(snapshot);
        await settings.Started.Task;
        Check(!reloadButton.IsEnabled && controller.QuickInspectionCalls == 2,
            "Explicit settings inspection released its controls early or ran more than once");
        settings.Complete(controller.Status);
        await reload;
        await scheduled;
        Check(reloadButton.IsEnabled && ((Button)window.FindName("SaveQuickConsentButton")).IsEnabled,
            "Completed settings reload did not restore convenience controls");
        window.Close();
        cases++;
    }

    private static async Task ConsentSaveWaitsForPoll(AppSnapshot snapshot, SetupRecoveryPlan plan)
    {
        using var controller = new Fake(snapshot, plan);
        var window = new MainWindow(controller, _ => Task.FromResult(true));
        window.ApplySnapshot(snapshot);
        await window.ReloadConvenienceAsync();
        var poll = new InteractionChecks.Barrier<AppSnapshot>();
        controller.MainInspection = poll.WaitAsync;
        var scheduled = window.RunScheduledInspectionAsync();
        await poll.Started.Task;
        var write = new InteractionChecks.Barrier<QuickLaunchStatus>();
        controller.ConsentWrite = (approval, enabled) =>
        {
            Check(approval == controller.Status.Approval && enabled, "Queued save changed the user's selected consent scope");
            return write.WaitAsync();
        };
        ((CheckBox)window.FindName("QuickConsentCheckBox")).IsChecked = true;
        var save = (Button)window.FindName("SaveQuickConsentButton");
        var reload = (Button)window.FindName("ReloadQuickSettingsButton");
        Check(save.IsEnabled, "Polling prevented the consent save click");
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!save.IsEnabled && !reload.IsEnabled, "Queued consent save did not immediately reserve settings");
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await window.RequestActionAsync(SidetalkCommandAction.Launch);
        var externalCalled = false;
        var external = await window.RunExternalQuickLaunchAsync(() => { externalCalled = true; return Task.CompletedTask; });
        await window.RunScheduledInspectionAsync();
        Check(!write.Started.Task.IsCompleted && controller.ConsentWriteCalls == 0 && controller.QuickInspectionCalls == 1,
            "Consent write or duplicate settings read bypassed the held poll");
        Check(!external && !externalCalled && controller.NormalLaunchCalls == 0 && controller.MainInspectionCalls == 1,
            "Queued consent save admitted competing work");
        controller.MainInspection = null;
        poll.Complete(snapshot);
        await write.Started.Task;
        Check(controller.ConsentWriteCalls == 1 && !save.IsEnabled && !reload.IsEnabled,
            "Consent save replayed or released controls while its write was pending");
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(controller.ConsentWriteCalls == 1, "Duplicate click dispatched a second consent write");
        var finalPoll = new InteractionChecks.Barrier<AppSnapshot>();
        controller.MainInspection = finalPoll.WaitAsync;
        write.Complete(controller.Status with { ConsentMatches = true });
        await finalPoll.Started.Task;
        var completion = window.RunScheduledInspectionAsync();
        finalPoll.Complete(snapshot);
        await completion;
        await scheduled;
        Check(controller.ConsentWriteCalls == 1 && save.IsEnabled && reload.IsEnabled &&
            ((CheckBox)window.FindName("QuickConsentCheckBox")).IsChecked == true,
            "Completed consent save lost the selected state or failed to restore availability");
        window.Close();
        cases++;
    }

    private static async Task ClosedQueuedSettingsCannotStart(AppSnapshot snapshot, SetupRecoveryPlan plan)
    {
        using var controller = new Fake(snapshot, plan);
        var window = new MainWindow(controller, _ => Task.FromResult(true));
        window.ApplySnapshot(snapshot);
        var poll = new InteractionChecks.Barrier<AppSnapshot>();
        controller.MainInspection = poll.WaitAsync;
        var scheduled = window.RunScheduledInspectionAsync();
        await poll.Started.Task;
        var reload = window.ReloadConvenienceAsync();
        Check(!reload.IsCompleted && controller.QuickInspectionCalls == 0, "Queued settings reload did not wait for polling");
        window.Close();
        poll.Complete(snapshot);
        await reload;
        await scheduled;
        Check(controller.QuickInspectionCalls == 0 && controller.ConsentWriteCalls == 0,
            "Closed window started a queued convenience operation");
        cases++;
    }

    private static async Task ClosedQueuedConsentCannotWrite(AppSnapshot snapshot, SetupRecoveryPlan plan)
    {
        using var controller = new Fake(snapshot, plan);
        var window = new MainWindow(controller, _ => Task.FromResult(true));
        window.ApplySnapshot(snapshot);
        await window.ReloadConvenienceAsync();
        var poll = new InteractionChecks.Barrier<AppSnapshot>();
        controller.MainInspection = poll.WaitAsync;
        var scheduled = window.RunScheduledInspectionAsync();
        await poll.Started.Task;
        ((CheckBox)window.FindName("QuickConsentCheckBox")).IsChecked = true;
        var save = (Button)window.FindName("SaveQuickConsentButton");
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!save.IsEnabled && controller.ConsentWriteCalls == 0,
            "Consent save did not remain pending behind the held poll");
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        Check(closed, "Pending consent save prevented the window from closing");
        poll.Complete(snapshot);
        await scheduled;
        // Drain the queued save continuation as well as the inspection awaited
        // above; the actual click handler is async void.
        await Dispatcher.Yield(DispatcherPriority.Background);
        Check(controller.ConsentWriteCalls == 0 && controller.MainInspectionCalls == 1 &&
            controller.QuickInspectionCalls == 1,
            "Closed pending consent save wrote settings or started another inspection");
        cases++;
    }

    private static async Task SettingsReadDrainsBeforeExternalRequest(AppSnapshot snapshot, SetupRecoveryPlan plan)
    {
        using var controller = new Fake(snapshot, plan);
        var window = new MainWindow(controller, _ => Task.FromResult(true));
        window.ApplySnapshot(snapshot);
        var settings = new InteractionChecks.Barrier<QuickLaunchStatus>();
        controller.Inspection = settings.WaitAsync;
        var reload = window.ReloadConvenienceAsync();
        await settings.Started.Task;
        await window.RunScheduledInspectionAsync();
        await window.RequestActionAsync(SidetalkCommandAction.Launch);
        var entered = false;
        var external = window.RunExternalQuickLaunchAsync(() => { entered = true; return Task.CompletedTask; });
        Check(!entered && controller.MainInspectionCalls == 0 && controller.NormalLaunchCalls == 0,
            "Settings inspection overlapped timer/action/external work");
        settings.Complete(controller.Status);
        await reload;
        Check(await external && entered && controller.MainInspectionCalls == 1,
            "External request did not wait for settings and perform one final refresh");
        window.Close();
        cases++;
    }

    private static async Task ClosedSettingsReadCannotApply(AppSnapshot snapshot, SetupRecoveryPlan plan)
    {
        using var controller = new Fake(snapshot, plan);
        var window = new MainWindow(controller, _ => Task.FromResult(true));
        var before = ((TextBlock)window.FindName("QuickConsentScopeText")).Text;
        var settings = new InteractionChecks.Barrier<QuickLaunchStatus>();
        controller.Inspection = settings.WaitAsync;
        var reload = window.ReloadConvenienceAsync();
        await settings.Started.Task;
        window.Close();
        settings.Complete(controller.Status with { ConsentMatches = true });
        await reload;
        Check(((TextBlock)window.FindName("QuickConsentScopeText")).Text == before,
            "Late settings response replaced a closed window's state");
        cases++;
    }

    private static async Task PipeAcknowledgesBeforeConfirmationCompletes()
    {
        var name = "synthetic-ack-" + Guid.NewGuid().ToString("N");
        using var primary = new InstanceRequestBroker(name);
        using var secondary = new InstanceRequestBroker(name);
        Check(primary.TryAcquire() && !secondary.TryAcquire(), "Pipe ack fixture did not reserve its own instance");
        var dispatcher = Dispatcher.CurrentDispatcher;
        var confirmation = new InteractionChecks.Barrier<bool>();
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await primary.ListenAsync(_ => dispatcher.InvokeAsync(() =>
        {
            // Match App's reserve-then-post boundary; the accepted operation may
            // enter a native modal loop, but transport completion is independent.
            dispatcher.BeginInvoke(new Action(async () =>
            {
                await confirmation.WaitAsync();
                finished.SetResult(true);
            }));
        }).Task);
        await secondary.ForwardAsync(new(Guid.NewGuid(), true, false));
        await confirmation.Started.Task;
        Check(!finished.Task.IsCompleted, "Ack fixture did not keep confirmation pending");
        confirmation.Complete(true);
        await finished.Task;
        cases++;
    }

    private static void ClosingLauncherCancelsButWaits()
    {
        var window = new QuickLaunchWindow();
        window.Show();
        window.Close();
        Check(window.Cancellation.IsCancellationRequested && window.IsVisible, "Closing exited an unsettled launcher");
        window.Complete();
        Check(!window.IsVisible, "Settled launcher did not close");
        cases++;
    }

    private static void RenderSettings(AppSnapshot snapshot, SetupRecoveryPlan plan, string output)
    {
        using var controller = new Fake(snapshot, plan);
        foreach (var small in new[] { false, true })
        {
            var window = new MainWindow(controller, isRenderMode: true);
            window.PrepareRender(false, small);
            window.ApplySnapshot(snapshot);
            window.ApplyRecoveryPlan(plan);
            window.ApplyQuickSettings(controller.Status, new("#5B6821", "ttl", "", true, null,
                "현재 테두리 색과 표시 방식을 읽었습니다. 저장은 별도 선택입니다.", new(new string('B', 64), "#5B6821")));
            window.ApplyShortcutSettings(new(new(), false));
            ((TabControl)window.FindName("MainTabs")).SelectedIndex = 2;
            window.Show();
            window.UpdateLayout();
            Check(((CheckBox)window.FindName("QuickConsentCheckBox")).IsChecked == false &&
                ((CheckBox)window.FindName("LoginStartupCheckBox")).IsChecked == false, "Synthetic settings defaults were not OFF");
            window.RenderToPng(Path.Combine(output, small ? "ui-quick-settings-small-fixture.png" : "ui-quick-settings-fixture.png"));
            // Scroll the same real WPF panel to inspect the border preview and
            // support limitations as well as the first viewport.
            var tabs = (TabControl)window.FindName("MainTabs");
            var tab = (TabItem)tabs.Items[2];
            ((ScrollViewer)tab.Content).ScrollToEnd();
            window.UpdateLayout();
            window.RenderToPng(Path.Combine(output, small ? "ui-identification-small-fixture.png" : "ui-identification-fixture.png"));
            window.Close();
        }
        var launcher = new QuickLaunchWindow();
        launcher.Show();
        launcher.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)launcher.ActualWidth, (int)launcher.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(launcher);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(output, "ui-quick-launcher-fixture.png"))) encoder.Save(stream);
        launcher.Complete();
        var fallback = new MainWindow(controller, isRenderMode: true);
        fallback.ApplySnapshot(snapshot with { State = Readiness.Unknown, CanLaunch = false, CanStop = false,
            CommandPending = true, CurrentConfigurationVerified = false, Title = "명령 결과 확인이 필요합니다",
            Detail = "미완료 명령 기록을 보존했습니다. 현재 설정과 전용 프로세스의 재확인이 필요합니다." });
        fallback.ShowEntryFeedback("빠른 실행을 완료로 판단하지 않았습니다. 미완료 명령 기록을 보존하고 현재 상태를 다시 확인합니다.");
        fallback.Show();
        fallback.UpdateLayout();
        Check(((TextBlock)fallback.FindName("EntryFeedbackText")).IsVisible,
            "Quick-launch failure reason was hidden in the fallback GUI");
        fallback.RenderToPng(Path.Combine(output, "ui-quick-fallback-fixture.png"));
        fallback.Close();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Quick-launch UI assertion failed: " + message);
    }

    private static void Pump(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            watchdog.Tick += (_, _) => { watchdog.Stop(); frame.Continue = false; };
            task.GetAwaiter().OnCompleted(() => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
            watchdog.Start();
            Dispatcher.PushFrame(frame);
            watchdog.Stop();
            Check(task.IsCompleted, "Completion barrier exceeded deadlock watchdog");
        }
        task.GetAwaiter().GetResult();
    }

    private sealed class Fake : ISidetalkController, IQuickLaunchController, ISecondaryAppearanceController
    {
        private readonly SetupRecoveryPlan plan;
        internal Fake(AppSnapshot snapshot, SetupRecoveryPlan plan, bool consent = false)
        {
            this.plan = plan;
            Status = new(snapshot, new(new string('A', 64)), consent, "합성 프로필 범위");
            Result = new(QuickLaunchOutcome.Launched, snapshot with { State = Readiness.RunningUnverified,
                CanLaunch = false, BoxProcessIds = new[] { 10001 }, CommandPending = false }, "전용 프로세스 관찰됨 · 로그인은 미확인");
        }
        internal QuickLaunchStatus Status { get; set; }
        internal QuickLaunchResult Result { get; set; }
        internal Func<Task<QuickLaunchStatus>>? Inspection { get; set; }
        internal Func<CancellationToken, Task<QuickLaunchResult>>? Launch { get; set; }
        internal Func<Task<AppSnapshot>>? MainInspection { get; set; }
        internal Func<QuickLaunchApproval, bool, Task<QuickLaunchStatus>>? ConsentWrite { get; set; }
        internal int QuickCalls { get; private set; }
        internal int QuickInspectionCalls { get; private set; }
        internal int ConsentWriteCalls { get; private set; }
        internal int NormalLaunchCalls { get; private set; }
        internal int MainInspectionCalls { get; private set; }
        internal QuickLaunchApproval? LastApproval { get; private set; }
        public Task<QuickLaunchStatus> InspectQuickLaunchAsync()
        {
            QuickInspectionCalls++;
            return Inspection?.Invoke() ?? Task.FromResult(Status);
        }
        public Task<QuickLaunchResult> LaunchSecondaryAsync(QuickLaunchApproval? confirmedScope, CancellationToken cancellationToken = default)
        {
            QuickCalls++;
            LastApproval = confirmedScope;
            return Launch?.Invoke(cancellationToken) ?? Task.FromResult(Result);
        }
        public Task<QuickLaunchStatus> SetQuickLaunchConsentAsync(QuickLaunchApproval approval, bool enabled)
        {
            ConsentWriteCalls++;
            return ConsentWrite?.Invoke(approval, enabled) ?? throw new InvalidOperationException("Unexpected consent write");
        }
        public Task<SecondaryAppearanceState> InspectSecondaryAppearanceAsync() => Task.FromResult(new SecondaryAppearanceState("#5B6821", "ttl", "", false, null, "synthetic", null));
        public Task<SecondaryAppearanceState> ApplySecondaryAppearanceAsync(SecondaryAppearancePlan plan) => throw new InvalidOperationException("Unexpected appearance write");
        public Task<AppSnapshot> InspectAsync() { MainInspectionCalls++; return MainInspection?.Invoke() ?? Task.FromResult(Status.Snapshot); }
        public Task LaunchAsync() { NormalLaunchCalls++; return Task.CompletedTask; }
        public Task StopAsync() => throw new InvalidOperationException("Unexpected stop");
        public Task<SetupRecoveryPlan> InspectSetupRecoveryAsync() => Task.FromResult(plan);
        public Task<SetupRecoveryEvidenceInspection> InspectInterruptedEvidenceAsync() => Task.FromResult(new SetupRecoveryEvidenceInspection([], false, false, "synthetic", "", "", ""));
        public Task<AppSnapshot> PrepareAsync(SetupRecoveryPlan approvedPlan, bool allowAutomatic, IProgress<string>? progress = null) => throw new InvalidOperationException("Unexpected recovery");
        public Task<AppSnapshot> PrepareAutomaticallyAsync(IProgress<string>? progress = null) => throw new InvalidOperationException("Unexpected automatic recovery");
        public Task<AppSnapshot> ReconcileInterruptedEvidenceAsync(SetupRecoveryEvidenceInspection approvedInspection, IProgress<string>? progress = null) => throw new InvalidOperationException("Unexpected reconciliation");
        public Task RevokeAutomaticRecoveryAsync() => throw new InvalidOperationException("Unexpected revoke");
        public Task<string> ExportSetupAsync(string folder) => throw new InvalidOperationException("Unexpected setup export");
        public Task<string> ExportDiagnosticsAsync(string path) => throw new InvalidOperationException("Unexpected diagnostics export");
        public string RecoveryHistoryPath => "synthetic";
        public void Dispose() { }
    }
}
