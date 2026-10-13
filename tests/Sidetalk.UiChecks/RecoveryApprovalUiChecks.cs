using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sidetalk.App;
using Sidetalk.Core;

/// <summary>
/// Synthetic regressions for the recovery approval contract. These checks never
/// construct the production controller and never write Sandboxie/profile data.
/// </summary>
internal static class RecoveryApprovalUiChecks
{
    private static readonly string[] TemplateNames =
    [
        "WindowsRasMan",
        "OnScreenKeyboard",
        "WindowsLive",
        "Edge_Fix",
        "OfficeLicensing",
        "OfficeClickToRun"
    ];

    public static void Run(AppSnapshot baseline, SetupRecoveryPlan healthy, SetupRecoveryPlan approval, string output)
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { Pump(RunAsync(baseline, healthy, approval, output)); }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        Console.WriteLine("Recovery-approval WPF checks: approval hierarchy, preservation/loss disclosure, state transitions, manager scope, scaled compact render, and routed keyboard events passed; synthetic controller writes=0 for read-only fixtures.");
    }

    /// <summary>
    /// Opens the production MainWindow with only the synthetic controller. The
    /// normal timer performs read-only fixture inspections; closing the window
    /// shuts down the WPF dispatcher. This mode never constructs SidetalkController.
    /// </summary>
    public static void RunPreview(App app, AppSnapshot baseline, SetupRecoveryPlan approval, string output)
    {
        using var controller = new PreviewController(baseline, approval);
        var window = new MainWindow(controller);
        window.PrepareRender(setupTab: true, compactWindow: false);
        window.ApplySnapshot(baseline);
        window.ApplyRecoveryPlan(approval);
        window.Closed += (_, _) => app.Shutdown();
        Console.WriteLine("Recovery-approval interactive preview started with a synthetic read-only inspection controller. Close the window to finish.");
        app.Run(window);
    }

    private static async Task RunAsync(AppSnapshot baseline, SetupRecoveryPlan healthy, SetupRecoveryPlan approval, string output)
    {
        AssertApprovalInformation(baseline, approval, output);
        AssertManagementScope(baseline, approval, output);
        AssertAdminRepairRequiresFreshApproval(baseline, approval);
        AssertVerifiedResultAndConsentWarning(baseline, healthy);
        await AssertRecoveryStateTransitions(baseline, approval);
        await AssertRoutedKeyboardAndTabTraversal(baseline, approval, output);
    }

    private static void AssertApprovalInformation(AppSnapshot baseline, SetupRecoveryPlan approval, string output)
    {
        using var controller = new PreviewController(baseline, approval);
        var window = CreateWindow(controller, baseline, approval, isRenderMode: true, compact: false);
        try
        {
            AssertApprovalInformation(window);
            AssertExactTechnicalIdentifiers(window);
            window.RenderToPng(Path.Combine(output, "ui-recovery-approval-clarity-default.png"));

            var summary = Find<TextBlock>(window, "RecoveryChangesSummaryText");
            var normalBounds = ElementBounds(window, summary);

            window.PrepareRender(setupTab: true, compactWindow: true);
            var content = (FrameworkElement)window.Content;
            content.LayoutTransform = new ScaleTransform(1.5, 1.5);
            window.UpdateLayout();
            Assert(window.Width == 740 && window.Height == 640, "Scaled compact fixture changed its source viewport");
            Assert(content.LayoutTransform is ScaleTransform scale && scale.ScaleX == 1.5 && scale.ScaleY == 1.5,
                "Scaled compact fixture does not use content LayoutTransform 1.5");
            var scaledBounds = ElementBounds(window, summary);
            Assert(scaledBounds.Height >= normalBounds.Height * 1.4, "Scaled compact fixture did not enlarge rendered text");

            var approvalButton = Find<Button>(window, "PrepareRecoveryButton");
            var scrollViewer = FindVisualChildren<ScrollViewer>(window).First(view => view.IsAncestorOf(approvalButton));
            approvalButton.BringIntoView();
            window.Dispatcher.Invoke(window.UpdateLayout, DispatcherPriority.Render);
            var buttonBounds = approvalButton.TransformToAncestor(scrollViewer).TransformBounds(
                new Rect(0, 0, approvalButton.ActualWidth, approvalButton.ActualHeight));
            Assert(buttonBounds.Top >= -1 && buttonBounds.Bottom <= scrollViewer.ViewportHeight + 1,
                "Scaled compact approval button could not be brought into the scroll viewport");
            RenderScaledWindow(window, Path.Combine(output, "ui-recovery-approval-clarity-compact-scaled.png"));
        }
        finally
        {
            Assert(controller.WritableCallCount == 0, "Read-only approval render called a controller write");
            window.Close();
        }
    }

    private static void AssertApprovalInformation(MainWindow window)
    {
        var changesSummary = Text(window, "RecoveryChangesSummaryText");
        var preservationSummary = Text(window, "RecoveryPreservationSummaryText");
        var lossWarning = Text(window, "RecoveryLossWarningText");
        var nextStep = Text(window, "RecoveryNextStepText");

        Assert(!string.IsNullOrWhiteSpace(changesSummary), "Approval changes purpose summary is empty");
        Assert(!string.IsNullOrWhiteSpace(preservationSummary), "Approval preservation summary is empty");
        Assert(!string.IsNullOrWhiteSpace(lossWarning), "Approval loss warning is empty");
        Assert(!string.IsNullOrWhiteSpace(nextStep), "Approval next-step guidance is empty");
        var automaticConsent = Find<CheckBox>(window, "AutomaticRecoveryCheckBox");
        Assert(automaticConsent.IsChecked != true, "Approval fixture activated automatic recovery without opt-in");
        Assert(automaticConsent.IsVisible, "Automatic-recovery opt-in is hidden before approval");
        Assert(!ContainsTechnicalIdentifier(changesSummary), "Approval purpose summary exposes technical identifiers");
        Assert(ContainsAny(changesSummary, "복구", "설정", "실행", "안전", "준비", "격리", "호환성"), "Approval purpose summary does not explain its purpose");
        Assert(ContainsAny(preservationSummary, "보존", "유지", "데이터", "기록", "프로필"), "Approval preservation summary does not explain retained data");
        Assert(ContainsAny(lossWarning, "저장하지 않은", "손실", "사라질", "삭제", "주의"), "Approval loss warning does not disclose possible unsaved-setting loss");
        Assert(ContainsAny(nextStep, "승인", "확인", "복구하고 실행 준비", "다음"), "Approval next step is not actionable");

        var technical = Find<Expander>(window, "RecoveryTechnicalExpander");
        var preserved = Find<Expander>(window, "RecoveryPreservedExpander");
        Assert(!technical.IsExpanded, "Technical recovery details are expanded by default");
        Assert(!preserved.IsExpanded, "Preserved-data details are expanded by default");

        var changes = Find<ItemsControl>(window, "RecoveryChangesList");
        var affected = Find<ItemsControl>(window, "RecoveryAffectedList");
        var scope = Find<TextBlock>(window, "RecoveryScopeText");
        var preservedList = Find<ItemsControl>(window, "RecoveryPreservedList");
        technical.IsExpanded = true;
        preserved.IsExpanded = true;
        window.UpdateLayout();
        Assert(technical.IsAncestorOf(changes) && technical.IsAncestorOf(affected) && technical.IsAncestorOf(scope),
            "Technical expander no longer owns the detailed recovery scope");
        Assert(preserved.IsAncestorOf(preservedList), "Preserved-data expander no longer owns retained-data details");
        technical.IsExpanded = false;
        preserved.IsExpanded = false;
        window.UpdateLayout();

        var approvalButton = Find<Button>(window, "PrepareRecoveryButton");
        Assert(TextTop(window, "RecoveryChangesSummaryText") < ElementTop(window, approvalButton),
            "Approval changes summary is below the approval action");
        Assert(TextTop(window, "RecoveryPreservationSummaryText") < ElementTop(window, approvalButton),
            "Approval preservation summary is below the approval action");
        Assert(TextTop(window, "RecoveryLossWarningText") < ElementTop(window, approvalButton),
            "Approval loss warning is below the approval action");
        Assert(TextTop(window, "RecoveryNextStepText") < ElementTop(window, approvalButton),
            "Approval next step is below the approval action");
    }

    private static void AssertExactTechnicalIdentifiers(MainWindow window)
    {
        var changes = string.Join("\n", Items(Find<ItemsControl>(window, "RecoveryChangesList")));
        foreach (var templateName in TemplateNames)
            Assert(changes.Contains(templateName, StringComparison.Ordinal), "Recovery template identifier changed or disappeared: " + templateName);
        Assert(changes.Contains("SbieCtrl_AutoRunSoftCompat", StringComparison.Ordinal), "Current-user Sandboxie identifier disappeared");
        Assert(changes.Contains("SbieCtrl_AutoRunSoftCompat=n", StringComparison.Ordinal), "Current-user target value disappeared");

        var scope = Text(window, "RecoveryScopeText");
        foreach (var templateName in TemplateNames)
            Assert(scope.Contains(templateName, StringComparison.Ordinal), "Recovery scope omitted exact identifier: " + templateName);
        Assert(scope.Contains("SbieCtrl_AutoRunSoftCompat", StringComparison.Ordinal), "Recovery scope omitted current-user identifier");
    }

    private static void AssertManagementScope(AppSnapshot baseline, SetupRecoveryPlan approval, string output)
    {
        var managed = approval with { Evidence = ["관리 UI 종료 대상: SandMan.exe · PID 801"] };
        using (var controller = new PreviewController(baseline, managed))
        {
            var window = CreateWindow(controller, baseline, managed, isRenderMode: true, compact: false);
            try
            {
                var affected = string.Join("\n", Items(Find<ItemsControl>(window, "RecoveryAffectedList")));
                Assert(affected.Contains("관리 UI", StringComparison.Ordinal) && affected.Contains("SandMan.exe", StringComparison.Ordinal),
                    "Observed management UI target is missing from the impact scope");
                Assert(affected.Contains("종료", StringComparison.Ordinal), "Observed management UI impact does not explain the action");
                window.RenderToPng(Path.Combine(output, "ui-recovery-approval-manager-present.png"));
            }
            finally
            {
                Assert(controller.WritableCallCount == 0, "Management-present render called a controller write");
                window.Close();
            }
        }

        using (var controller = new PreviewController(baseline, approval))
        {
            var window = CreateWindow(controller, baseline, approval, isRenderMode: true, compact: false);
            try
            {
                var affected = string.Join("\n", Items(Find<ItemsControl>(window, "RecoveryAffectedList")));
                Assert(affected.Contains("확인된 종료 대상이 없습니다.", StringComparison.Ordinal), "No-manager-target disclosure disappeared");
                Assert(!affected.Contains("승인 후 닫힙니다.", StringComparison.Ordinal), "No-manager-target fixture claims a manager window will close");
            }
            finally
            {
                Assert(controller.WritableCallCount == 0, "Management-absent render called a controller write");
                window.Close();
            }
        }
    }

    private static void AssertAdminRepairRequiresFreshApproval(AppSnapshot baseline, SetupRecoveryPlan approval)
    {
        var adminPlan = approval with
        {
            State = SetupRecoveryState.ApprovalRequired,
            RequiresApproval = true,
            AllowAutomatic = false,
            Changes =
            [
                new SetupRecoveryChange(
                    "SideTalk_visual_preview",
                    "DropAdminRights",
                    [],
                    ["y"],
                    SetupRecoveryChangeKind.RepairOwnedBoxSetting,
                    "전용 환경 관리자 권한 제한 복구")
            ],
            Evidence = ["전용 환경이 비어 있는지 확인된 뒤 새 승인이 필요합니다."],
            Summary = "관리자 권한 제한 복구는 새 승인이 필요합니다."
        };

        using var controller = new PreviewController(baseline, adminPlan);
        var window = new MainWindow(controller);
        try
        {
            window.PrepareRender(setupTab: true, compactWindow: false);
            window.ApplySnapshot(baseline);
            window.ApplyRecoveryPlan(adminPlan);
            var consent = Find<CheckBox>(window, "AutomaticRecoveryCheckBox");
            Assert(consent.IsChecked != true, "Admin-rights repair inherited automatic consent");
            Assert(!consent.IsEnabled, "Read-only admin-rights repair enabled automatic consent");
            Assert(controller.WritableCallCount == 0, "Admin-rights approval inspection called a controller write");
        }
        finally { window.Close(); }
    }

    private static void AssertVerifiedResultAndConsentWarning(AppSnapshot baseline, SetupRecoveryPlan healthy)
    {
        const string warning = "설정 복구는 검증됐지만 자동 복구 동의를 저장하지 못했습니다. 원본 기록을 보존했습니다.";
        var snapshot = baseline with { RecoveryResultDetail = warning };
        using var controller = new PreviewController(snapshot, healthy);
        var window = new MainWindow(controller, isRenderMode: false);
        try
        {
            window.ApplySnapshot(snapshot);
            window.ApplyRecoveryPlan(healthy with { State = SetupRecoveryState.Applied });
            Assert(Text(window, "RecoveryResultText").Contains(warning, StringComparison.Ordinal), "Verified consent-persistence warning was discarded");
            Assert(Find<Border>(window, "RecoveryPanel").Visibility == Visibility.Visible, "Verified warning hid the recovery result panel");
            Assert(Text(window, "RecoveryStateText").Contains("검증", StringComparison.Ordinal) ||
                   Text(window, "RecoveryStateText").Contains("완료", StringComparison.Ordinal), "Verified result is not distinguishable from preparation");
            Assert(controller.WritableCallCount == 0, "Verified-result presentation called a controller write");
        }
        finally { window.Close(); }
    }

    private static async Task AssertRecoveryStateTransitions(AppSnapshot baseline, SetupRecoveryPlan approval)
    {
        using (var labelsController = new PreviewController(baseline, approval))
        {
            var labelsWindow = new MainWindow(labelsController);
            try
            {
                labelsWindow.ApplySnapshot(baseline);
                labelsWindow.ApplyRecoveryPlan(approval);
                InvokePrivate(labelsWindow, "BeginRecoveryOperation", "synthetic preparation");
                Assert(Text(labelsWindow, "RecoveryStateText") == "복구 준비 중", "Preparation label is not exposed before execution starts");
                InvokePrivate(labelsWindow, "SetRecoveryProgress", "설정 백업 중…");
                Assert(Text(labelsWindow, "RecoveryStateText") == "복구 준비 중", "Backup preparation was labelled as settings application");
                InvokePrivate(labelsWindow, "SetRecoveryProgress", "복구 실행 중…");
                Assert(Text(labelsWindow, "RecoveryStateText") == "복구 실행 중", "Execution label is not exposed when progress starts");
                Assert(Text(labelsWindow, "StatusTitleText") == Text(labelsWindow, "RecoveryStateText"), "Home and recovery execution phases disagree");
                InvokePrivate(labelsWindow, "SetRecoveryProgress", "재검사");
                Assert(Text(labelsWindow, "RecoveryStateText") == "복구 후 확인 중" && Text(labelsWindow, "StatusDetailText") == "재검사", "Reinspection phase did not reach the home card");
            }
            finally { labelsWindow.Close(); }
        }

        var holding = new ApprovalController(baseline, approval) { HoldPrepare = true };
        var window = new MainWindow(holding);
        try
        {
            window.ApplySnapshot(baseline);
            window.ApplyRecoveryPlan(approval);
            Find<Button>(window, "PrepareRecoveryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await holding.PrepareStarted.Task;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Assert(Text(window, "RecoveryStateText") == "복구 실행 중", "Execution state is not explicit while the controller is running");
            Assert(Text(window, "RecoveryProgressText").Contains("복구 대상", StringComparison.Ordinal), "Execution progress detail is not visible");
            holding.PrepareCompletion.TrySetResult(baseline);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert(Text(window, "RecoveryResultText").Contains("확인할 항목이 남아", StringComparison.Ordinal), "Unresolved post-apply plan was reported as completed");
        }
        finally
        {
            window.Close();
            holding.Dispose();
        }

        var failing = new ApprovalController(baseline, approval) { FailPrepare = true };
        window = new MainWindow(failing);
        try
        {
            window.ApplySnapshot(baseline);
            window.ApplyRecoveryPlan(approval);
            Find<Button>(window, "PrepareRecoveryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await failing.PrepareStarted.Task;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert(Text(window, "RecoveryStateText") == "복구 실패 · 재확인 필요", "Apply failure is not distinguished from inspection failure");
            Assert(Text(window, "StatusTitleText") == Text(window, "RecoveryStateText"), "Home and recovery failure states disagree");
            Assert(Text(window, "RecoveryResultText").Contains("복구", StringComparison.Ordinal), "Apply failure detail disappeared");
            Assert(Text(window, "RecoverySummaryText").Contains("복원됐다고 확인되지", StringComparison.Ordinal), "Apply failure did not preserve uncertain prior-state disclosure");
            Assert(Text(window, "RecoveryBackupText").Contains("백업 위치", StringComparison.Ordinal), "Apply failure hid the preserved backup path");
        }
        finally
        {
            window.Close();
            failing.Dispose();
        }

        using var inspection = new ApprovalController(baseline, approval);
        window = new MainWindow(inspection);
        try
        {
            window.ApplySnapshot(baseline);
            window.ApplyRecoveryPlan(approval);
            window.ApplyRecoveryInspectionError(new InvalidOperationException("synthetic recovery inspection failure"));
            Assert(Text(window, "RecoveryStateText") == "복구 상태 확인 실패", "Recovery inspection failure is not distinguished from apply failure");
            Assert(Text(window, "RecoveryResultText").Contains("복구 상태 확인 실패", StringComparison.Ordinal), "Recovery inspection failure detail disappeared");
            Assert(!Find<Button>(window, "PrepareRecoveryButton").IsEnabled, "Recovery inspection failure left approval enabled");
        }
        finally { window.Close(); }

        var revokePlan = approval with { AllowAutomatic = true };
        using var revoking = new ApprovalController(baseline, revokePlan);
        window = new MainWindow(revoking);
        try
        {
            window.ApplySnapshot(baseline);
            window.ApplyRecoveryPlan(revokePlan);
            Find<Button>(window, "RevokeAutomaticButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await revoking.RevokeStarted.Task;
            Assert(Text(window, "RecoveryStateText") == "자동 복구 해제 중" && Text(window, "StatusTitleText") == "자동 복구 해제 중", "Consent revocation was presented as settings repair");
            revoking.RevokeCompletion.SetException(new InvalidOperationException("synthetic consent revocation failure"));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert(Text(window, "RecoveryResultText").Contains("해제하지 못했습니다", StringComparison.Ordinal), "Consent revocation failure detail disappeared");
            Assert(!Text(window, "StatusDetailText").Contains("복구 작업 결과", StringComparison.Ordinal), "Consent failure implied a settings repair");
        }
        finally { window.Close(); }
    }

    private static async Task AssertRoutedKeyboardAndTabTraversal(AppSnapshot baseline, SetupRecoveryPlan approval, string output)
    {
        using var controller = new ApprovalController(baseline, approval) { HoldPrepare = true };
        var window = CreateWindow(controller, baseline, approval, isRenderMode: false, compact: false);
        try
        {
            window.Activate();
            var technical = Find<Expander>(window, "RecoveryTechnicalExpander");
            var preserved = Find<Expander>(window, "RecoveryPreservedExpander");
            var checkbox = Find<CheckBox>(window, "AutomaticRecoveryCheckBox");
            var prepare = Find<Button>(window, "PrepareRecoveryButton");
            var focusTargets = new Control[] { checkbox, prepare };
            foreach (var target in focusTargets)
                Assert(target.Focusable && target.IsVisible, "Approval control is not keyboard focusable: " + target.Name);

            Assert(Keyboard.Focus(checkbox) == checkbox, "Automatic-recovery choice could not receive focus");
            var tabbed = checkbox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Assert(tabbed && ReferenceEquals(Keyboard.FocusedElement, prepare),
                "Tab traversal did not move to the primary approval button");

            var observedDown = new List<Key>();
            var observedUp = new List<Key>();
            window.AddHandler(Keyboard.KeyDownEvent, new KeyEventHandler((_, args) => observedDown.Add(args.Key)), handledEventsToo: true);
            window.AddHandler(Keyboard.KeyUpEvent, new KeyEventHandler((_, args) => observedUp.Add(args.Key)), handledEventsToo: true);

            Assert(checkbox.IsChecked != true, "Approval keyboard fixture started with automatic consent enabled");
            RaiseKeyPair(checkbox, Key.Space, window);
            Assert(checkbox.IsChecked == true, "Space did not toggle the automatic-recovery choice on");
            RaiseKeyPair(checkbox, Key.Space, window);
            Assert(checkbox.IsChecked != true, "Space did not toggle the automatic-recovery choice off");

            Assert(Keyboard.Focus(checkbox) == checkbox, "Checkbox focus was lost before nonfocused Enter test");
            RaiseKeyPair(checkbox, Key.Enter, window);
            Assert(!controller.PrepareStarted.Task.IsCompleted && controller.WritableCallCount == 0,
                "Enter on a non-primary focused control invoked recovery");

            RaiseKeyPair(checkbox, Key.Escape, window);
            Assert(window.IsVisible && checkbox.IsChecked != true && controller.WritableCallCount == 0,
                "Escape changed consent, closed the preparation surface, or wrote through the controller");

            Assert(Keyboard.Focus(prepare) == prepare, "Primary approval button could not receive focus for the keyboard capture");
            window.Width = 740;
            window.Height = 640;
            window.Dispatcher.Invoke(window.UpdateLayout, DispatcherPriority.Render);
            prepare.BringIntoView();
            window.Dispatcher.Invoke(window.UpdateLayout, DispatcherPriority.Render);
            Assert(prepare.IsKeyboardFocused, "Primary approval focus was lost after compact scrolling");
            window.RenderToPng(Path.Combine(output, "ui-recovery-approval-keyboard-focus.png"));

            RaiseKeyPair(prepare, Key.Enter, window);
            await controller.PrepareStarted.Task;
            Assert(controller.WritableCallCount == 1, "Enter on the focused primary approval button did not invoke the synthetic controller exactly once");

            Assert(observedDown.Contains(Key.Space) && observedDown.Contains(Key.Enter) && observedDown.Contains(Key.Escape),
                "Routed key-down events were not observed on the approval surface");
            Assert(observedUp.Contains(Key.Space) && observedUp.Contains(Key.Enter) && observedUp.Contains(Key.Escape),
                "Routed key-up events were not observed on the approval surface");

            controller.PrepareCompletion.TrySetResult(baseline);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
        finally { window.Close(); }
    }

    private static MainWindow CreateWindow(ISidetalkController controller, AppSnapshot snapshot, SetupRecoveryPlan plan, bool isRenderMode, bool compact)
    {
        var window = new MainWindow(controller, isRenderMode);
        window.PrepareRender(setupTab: true, compactWindow: compact);
        window.ApplySnapshot(snapshot);
        window.ApplyRecoveryPlan(plan);
        window.Show();
        window.Dispatcher.Invoke(window.UpdateLayout, DispatcherPriority.Render);
        return window;
    }

    private static void RaiseKeyPair(UIElement target, Key key, Window window)
    {
        RaiseKey(target, key, window, Keyboard.KeyDownEvent);
        RaiseKey(target, key, window, Keyboard.KeyUpEvent);
    }

    private static void RaiseKey(UIElement target, Key key, Window window, RoutedEvent routedEvent)
    {
        var source = PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("Approval keyboard fixture has no presentation source");
        Keyboard.Focus(target);
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = routedEvent,
            Source = target
        };
        target.RaiseEvent(args);
    }

    private static void InvokePrivate(MainWindow window, string method, string argument)
    {
        var target = typeof(MainWindow).GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing recovery state method " + method);
        var parameterCount = target.GetParameters().Length;
        object?[] parameters = parameterCount == 1 ? [argument] : [argument, Type.Missing];
        target.Invoke(window, parameters);
    }

    private static string Text(MainWindow window, string name) => Find<TextBlock>(window, name).Text;
    private static IReadOnlyList<string> Items(ItemsControl control) => control.Items.Cast<object?>().Select(item => item?.ToString() ?? string.Empty).ToArray();

    private static double TextTop(MainWindow window, string name) => ElementTop(window, Find<TextBlock>(window, name));

    private static double ElementTop(MainWindow window, FrameworkElement element)
    {
        return ElementBounds(window, element).Top;
    }

    private static Rect ElementBounds(Visual ancestor, FrameworkElement element) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static void RenderScaledWindow(MainWindow window, string path)
    {
        // The production utility measures untransformed content. This fixture
        // scales the entire content; use window bounds so the evidence is not cropped.
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is null)
            yield break;

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;

            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private static T Find<T>(MainWindow window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException("Missing production approval control " + name);

    private static bool ContainsTechnicalIdentifier(string text) =>
        text.Contains("SbieCtrl", StringComparison.Ordinal) || TemplateNames.Any(name => text.Contains(name, StringComparison.Ordinal));

    private static bool ContainsAny(string text, params string[] terms) => terms.Any(term => text.Contains(term, StringComparison.Ordinal));

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Recovery-approval WPF assertion failed: " + message);
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
            Assert(task.IsCompleted, "Approval completion barrier did not settle before watchdog");
        }
        task.GetAwaiter().GetResult();
    }

    private sealed class ApprovalController(AppSnapshot snapshot, SetupRecoveryPlan plan) : ISidetalkController
    {
        private bool _disposed;

        public bool HoldPrepare { get; set; }
        public bool FailPrepare { get; set; }
        public TaskCompletionSource<bool> PrepareStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<AppSnapshot> PrepareCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> RevokeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RevokeCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WritableCallCount { get; private set; }
        public string RecoveryHistoryPath => plan.HistoryPath;

        public Task<AppSnapshot> InspectAsync() => Task.FromResult(snapshot);
        public Task<SetupRecoveryPlan> InspectSetupRecoveryAsync() => Task.FromResult(plan);
        public Task<SetupRecoveryEvidenceInspection> InspectInterruptedEvidenceAsync() => Task.FromResult(new SetupRecoveryEvidenceInspection([], false, false, "합성 확인", "", "", ""));
        public Task<AppSnapshot> ReconcileInterruptedEvidenceAsync(SetupRecoveryEvidenceInspection approvedInspection, IProgress<string>? progress = null) => UnexpectedWrite();
        public Task<AppSnapshot> PrepareAsync(SetupRecoveryPlan approvedPlan, bool allowAutomatic, IProgress<string>? progress = null)
        {
            WritableCallCount++;
            PrepareStarted.TrySetResult(true);
            if (FailPrepare) return Task.FromException<AppSnapshot>(new InvalidOperationException("synthetic apply failure"));
            progress?.Report("복구 대상과 백업을 확인 중…");
            if (HoldPrepare) return PrepareCompletion.Task;
            return Task.FromResult(snapshot);
        }
        public Task<AppSnapshot> PrepareAutomaticallyAsync(IProgress<string>? progress = null) => UnexpectedWrite();
        public Task RevokeAutomaticRecoveryAsync()
        {
            WritableCallCount++;
            RevokeStarted.TrySetResult(true);
            return RevokeCompletion.Task;
        }
        public Task<AppSnapshot> CleanResidualBoxAndPrepareAsync(ResidualBoxRecoveryApproval approval, IProgress<string>? progress = null) => UnexpectedWrite();
        public Task LaunchAsync() => UnexpectedWriteTask();
        public Task StopAsync() => UnexpectedWriteTask();
        public Task<string> ExportSetupAsync(string folder) => Task.FromException<string>(new InvalidOperationException("Unexpected synthetic write"));
        public Task<string> ExportDiagnosticsAsync(string path) => Task.FromException<string>(new InvalidOperationException("Unexpected synthetic write"));
        public void Dispose() => _disposed = true;

        private Task<AppSnapshot> UnexpectedWrite()
        {
            if (_disposed) return Task.FromException<AppSnapshot>(new ObjectDisposedException(nameof(ApprovalController)));
            throw new InvalidOperationException("Unexpected synthetic controller write");
        }

        private Task UnexpectedWriteTask() => Task.FromException(new InvalidOperationException("Unexpected synthetic controller write"));
    }
}
