using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Sidetalk.App;
using Sidetalk.Core;

internal static class Program
{
    private static readonly string[] PreviewTemplateNames =
    [
        "WindowsRasMan",
        "OnScreenKeyboard",
        "WindowsLive",
        "Edge_Fix",
        "OfficeLicensing",
        "OfficeClickToRun"
    ];

    [STAThread]
    public static void Main(string[] args)
    {
        try { Run(args); }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            Application.Current?.Shutdown(1);
            Environment.ExitCode = 1;
        }
    }

    private static void Run(string[] args)
    {
        if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] is not ("--interaction-only" or "--quick-launch-only" or "--quiet-only" or "--approval-only" or "--approval-preview")))
            throw new ArgumentException("Usage: UiPreview <output-directory> [--interaction-only|--quick-launch-only|--quiet-only|--approval-only|--approval-preview]");

        var app = new App();
        app.InitializeComponent();
        // Dispatcher turns in fixtures must never run production startup.
        app.Startup -= app.OnStartup;
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "src", "Sidetalk.App", "Assets", "Sidetalk.ico"))) repository = repository.Parent;
        if (repository is null) throw new InvalidOperationException("Repository icon resource not found.");
        IconResourceChecks.VerifySourceAndWpfResource(Path.Combine(repository.FullName, "src", "Sidetalk.App", "Assets", "Sidetalk.ico"));
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        InspectionDiagnostics.OutputDirectoryOverride = output;
        InspectionDiagnostics.Clear();

        var baseSnapshot = CreateSnapshot();
        if (args.Length == 2)
        {
            if (args[1] == "--interaction-only") InteractionChecks.Run(baseSnapshot, CreateHealthyPlan(), CreateApprovalPlan(), output);
            else if (args[1] == "--quick-launch-only") QuickLaunchUiChecks.Run(baseSnapshot, CreateHealthyPlan(), output);
            else if (args[1] == "--quiet-only") QuietInspectionChecks.Run(baseSnapshot, CreateHealthyPlan(), output);
            else if (args[1] == "--approval-preview")
            {
                RecoveryApprovalUiChecks.RunPreview(app, baseSnapshot, CreateApprovalPlan(), output);
                return;
            }
            else RecoveryApprovalUiChecks.Run(baseSnapshot, CreateHealthyPlan(), CreateApprovalPlan(), output);
            app.Shutdown();
            return;
        }
        RenderBasicFixtures(app, output, baseSnapshot);
        RenderRecoveryFixtures(app, output, baseSnapshot);
        AdminRightsUiChecks.Run(baseSnapshot, CreateHealthyPlan(), CreateApprovalPlan(), output);
        ResidualRecoveryUiChecks.Run(baseSnapshot, CreateHealthyPlan(), CreateApprovalPlan(), output);
        RecoveryApprovalUiChecks.Run(baseSnapshot, CreateHealthyPlan(), CreateApprovalPlan(), output);
        AssertInterruptedChoiceReset(baseSnapshot);
        RunRefreshFailureFixtures(baseSnapshot);
        InteractionChecks.Run(baseSnapshot, CreateHealthyPlan(), CreateApprovalPlan(), output);
        QuickLaunchUiChecks.Run(baseSnapshot, CreateHealthyPlan(), output);
        QuietInspectionChecks.Run(baseSnapshot, CreateHealthyPlan(), output);
        var invalidLogDirectory = Path.Combine(output, "log-path-is-a-file.fixture");
        File.WriteAllText(invalidLogDirectory, "fixture");
        InspectionDiagnostics.OutputDirectoryOverride = invalidLogDirectory;
        try
        {
            InspectionDiagnostics.Record("log-write-failure-fixture", new IOException("fixture log failure"), persist: true);
            Assert(InspectionDiagnostics.Snapshot().Any(failure => failure.Stage == "log-write-failure-fixture"), "Logging failure escaped or lost in-memory evidence");
        }
        finally { InspectionDiagnostics.OutputDirectoryOverride = output; }

        app.Shutdown();
    }


    private static void RenderBasicFixtures(App app, string output, AppSnapshot snapshot)
    {
        foreach (var sample in new[] { "ready", "running", "blocked", "setup", "small", "error", "launch-failed", "stop-residual" })
        {
            var state = sample is "running" or "stop-residual"
                ? snapshot with
                {
                    State = Readiness.RunningUnverified,
                    CanLaunch = false,
                    CanStop = true,
                    BoxProcessIds = new[] { 10001, 10002, 10003 },
                    ProcessPolicy = ProcessPolicyStatus.EffectiveRulesUnverified,
                    Detail = "현재 설정은 검증됐지만 실행 중 프로세스의 시작 당시 적용 규칙은 직접 확인할 수 없습니다. 설정을 다시 불러와도 기존 프로세스의 규칙이 바뀌었다고 판단하지 않습니다. 외부에서 시작했거나 실행 중 설정을 바꿨다면 보조 앱을 정상 종료해 상자가 빈 것을 확인한 뒤 새로 실행하세요."
                }
                : sample == "blocked"
                    ? snapshot with
                    {
                        State = Readiness.Blocked,
                        Title = "격리 설정을 확인해주세요",
                        Detail = "상속되는 전역 설정에 검토되지 않은 항목이 있습니다. 초기 설정 · 진단에서 차단 항목을 확인하세요.",
                        CurrentConfigurationVerified = false,
                        CanLaunch = false,
                        CanStop = false,
                        ProcessPolicy = ProcessPolicyStatus.NotObserved
                    }
                    : snapshot;

            if (sample == "launch-failed")
                state = state with
                {
                    Title = "보조 카카오톡을 실행하지 못했어요",
                    Detail = "실행 명령이 종료됐지만 전용 프로세스가 확인되지 않았습니다."
                };
            if (sample == "stop-residual")
                state = state with
                {
                    Detail = "종료 요청 뒤 전용 프로세스가 남아 있습니다. " + state.Detail
                };

            var plan = sample == "setup" ? CreateApprovalPlan() : CreateHealthyPlan();
            RenderWindow(
                app,
                output,
                "ui-" + sample + "-fixture.png",
                state,
                plan,
                setupTab: sample == "setup",
                compactWindow: sample == "small",
                assertion: window =>
                {
                    if (sample == "launch-failed")
                    {
                        Assert(
                            ((TextBlock)window.FindName("StatusTitleText")).Text == state.Title &&
                            ((TextBlock)window.FindName("StatusDetailText")).Text == state.Detail,
                            "Launch failure feedback hidden");
                    }

                    if (sample == "stop-residual")
                    {
                        Assert(
                            ((TextBlock)window.FindName("StatusDetailText")).Text.StartsWith("종료 요청 뒤 전용 프로세스가 남아 있습니다.", StringComparison.Ordinal),
                            "Residual-process feedback hidden");
                    }
                });
        }
    }

    private static void RenderRecoveryFixtures(App app, string output, AppSnapshot snapshot)
    {
        var approval = CreateApprovalPlan();

        RenderWindow(
            app,
            output,
            "ui-recovery-approval-fixture.png",
            snapshot,
            approval,
            setupTab: true,
            compactWindow: false,
            assertion: window => AssertApprovalPanel(window));

        RenderWindow(
            app,
            output,
            "ui-recovery-interrupted-fixture.png",
            snapshot,
            approval with
            {
                State = SetupRecoveryState.Interrupted,
                Summary = "이전 복구 작업이 중단되어 다시 확인해야 합니다.",
                InterruptedOperationId = "preview-operation"
            },
            setupTab: true,
            compactWindow: false,
            assertion: window =>
            {
                Assert(((TextBlock)window.FindName("RecoveryStateText")).Text == "중단 복구 검토", "Interrupted state is not visible");
            });

        RenderWindow(
            app,
            output,
            "ui-recovery-blocked-fixture.png",
            snapshot,
            approval with
            {
                State = SetupRecoveryState.Blocked,
                Summary = "현재 실행 컨텍스트를 검증하지 못해 복구를 차단했습니다.",
                Blockers = new[] { "현재 사용자 실행 컨텍스트를 확인하지 못했습니다." },
                RequiresApproval = false
            },
            setupTab: true,
            compactWindow: false,
            assertion: window =>
            {
                var blocker = ((TextBlock)window.FindName("RecoveryBlockerText")).Text;
                Assert(blocker.Contains("현재 사용자 실행 컨텍스트", StringComparison.Ordinal), "Blocked reason is not visible");
                Assert(!((Button)window.FindName("PrepareRecoveryButton")).IsEnabled, "Blocked recovery action is enabled");
            });

        RenderWindow(
            app,
            output,
            "ui-recovery-healthy-fixture.png",
            snapshot,
            CreateHealthyPlan(),
            setupTab: true,
            compactWindow: false,
            assertion: window =>
            {
                Assert(((Border)window.FindName("RecoveryPanel")).Visibility == Visibility.Collapsed, "Healthy recovery panel is visible");
            });

        RenderWindow(
            app,
            output,
            "ui-recovery-render-readonly-fixture.png",
            snapshot,
            approval,
            setupTab: true,
            compactWindow: true,
            assertion: AssertReadOnlyPanel);

        RenderFailedApplyFixture(app, output, snapshot);
    }

    private static void AssertApprovalPanel(MainWindow window, bool requireDefaultViewport = true)
    {
        Assert(((Border)window.FindName("RecoveryPanel")).Visibility == Visibility.Visible, "Approval panel is hidden");

        var changes = Items((ItemsControl)window.FindName("RecoveryChangesList"));
        var renderedChanges = string.Join("\n", changes);
        foreach (var templateName in PreviewTemplateNames)
            Assert(renderedChanges.Contains(templateName, StringComparison.Ordinal), "Missing exact template name: " + templateName);

        Assert(renderedChanges.Contains("SbieCtrl_AutoRunSoftCompat", StringComparison.Ordinal), "Missing current-user option");
        Assert(renderedChanges.Contains("현재 사용자", StringComparison.Ordinal), "Current-user scope is not shown");

        var affected = Items((ItemsControl)window.FindName("RecoveryAffectedList"));
        var renderedAffected = string.Join("\n", affected);
        Assert(renderedAffected.Contains("확인된 종료 대상이 없습니다.", StringComparison.Ordinal), "Missing no-manager-target disclosure");
        Assert(!renderedAffected.Contains("승인 후 닫힙니다.", StringComparison.Ordinal), "Manager termination claim shown without a target");

        if (!requireDefaultViewport)
            return;

        var approvalButton = (Button)window.FindName("PrepareRecoveryButton");
        var buttonBounds = approvalButton.TransformToAncestor(window).TransformBounds(
            new Rect(0, 0, approvalButton.ActualWidth, approvalButton.ActualHeight));
        var viewport = FindVisualChildren<ScrollContentPresenter>(window).First(view => view.IsAncestorOf(approvalButton));
        var viewportBounds = viewport.TransformToAncestor(window).TransformBounds(new Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight));
        Assert(buttonBounds.Top >= viewportBounds.Top - 1 && buttonBounds.Bottom <= viewportBounds.Bottom + 1,
            $"Approval button is below the default viewport: bottom={buttonBounds.Bottom:0.##}, viewport bottom={viewportBounds.Bottom:0.##}");
    }

    private static void AssertReadOnlyPanel(MainWindow window)
    {
        AssertApprovalPanel(window, requireDefaultViewport: false);

        foreach (var controlName in new[]
                 {
                     "LaunchButton",
                     "StopButton",
                     "InspectButton",
                     "ExportSetupButton",
                     "ExportDiagnosticsButton",
                     "PrepareRecoveryButton",
                     "RevokeAutomaticButton",
                     "OpenRecoveryHistoryButton",
                     "PreserveEvidenceButton",
                     "OpenEvidenceHistoryButton"
                 })
        {
            Assert(!((Button)window.FindName(controlName)).IsEnabled, "Render action is enabled: " + controlName);
        }

        Assert(!((CheckBox)window.FindName("AutomaticRecoveryCheckBox")).IsEnabled, "Render consent checkbox is enabled");
        Assert(window.Width == 740 && window.Height == 640, "Compact render dimensions changed");

        var scrollViewers = FindVisualChildren<ScrollViewer>(window).ToArray();
        Assert(scrollViewers.Length > 0, "Compact render has no scroll container");
    }

    private static void RenderFailedApplyFixture(App app, string output, AppSnapshot snapshot)
    {
        using var controller = new PreviewController(snapshot, CreateApprovalPlan(), failPrepare: true);
        var window = new MainWindow(controller, isRenderMode: true);
        window.PrepareRender(setupTab: true, compactWindow: false);
        window.ApplySnapshot(snapshot);
        window.ApplyRecoveryPlan(CreateApprovalPlan());
        window.ApplyError(new InvalidOperationException("복구 적용에 실패했습니다. 상태를 다시 확인해야 합니다."));

        window.Show();
        app.Dispatcher.Invoke(() => window.UpdateLayout(), DispatcherPriority.Render);
        Assert(!((Button)window.FindName("LaunchButton")).IsEnabled, "Failed apply left a stale launch action enabled");
        Assert(((TextBlock)window.FindName("StatusTitleText")).Text == "상태 확인 실패", "Failed apply status is not visible");
        window.RenderToPng(Path.Combine(output, "ui-recovery-failed-apply-fixture.png"));
        window.Close();
    }

    private static void AssertInterruptedChoiceReset(AppSnapshot snapshot)
    {
        using var controller = new PreviewController(snapshot, CreateApprovalPlan());
        var window = new MainWindow(controller, isRenderMode: false);
        var approval = CreateApprovalPlan();
        var interrupted = approval with
        {
            State = SetupRecoveryState.Interrupted,
            Summary = "이전 복구 작업이 중단되어 다시 확인해야 합니다.",
            InterruptedOperationId = "preview-operation"
        };

        window.ApplySnapshot(snapshot);
        window.ApplyRecoveryPlan(approval);
        var checkbox = (CheckBox)window.FindName("AutomaticRecoveryCheckBox");
        checkbox.IsChecked = true;
        window.ApplyRecoveryPlan(interrupted);

        Assert(checkbox.IsChecked != true, "Interrupted plan kept stale automatic-recovery consent");

    }

    private static void RunRefreshFailureFixtures(AppSnapshot snapshot)
    {
        InspectionDiagnostics.Clear();
        var actionableSnapshot = snapshot with { CanStop = true };
        using (var mainController = new PreviewController(actionableSnapshot, CreateHealthyPlan()))
        {
            var window = new MainWindow(mainController, isRenderMode: false);
            window.ApplySnapshot(actionableSnapshot);
            window.ApplyRecoveryPlan(CreateHealthyPlan());
            mainController.FailInspect = true;
            window.RefreshNowAsync().GetAwaiter().GetResult();

            Assert(((TextBlock)window.FindName("StatusDetailText")).Text.Contains("마지막 확인 결과", StringComparison.Ordinal), "Main inspection failure did not label last check");
            Assert(((TextBlock)window.FindName("RefreshSummaryText")).Text.Contains("마지막 확인", StringComparison.Ordinal), "Main inspection failure lost last-check timestamp");
            Assert(!((Button)window.FindName("LaunchButton")).IsEnabled, "Main inspection failure left launch enabled");
            Assert(!((Button)window.FindName("StopButton")).IsEnabled, "Main inspection failure left stop enabled");

            window.RefreshNowAsync().GetAwaiter().GetResult();
            Assert(((TextBlock)window.FindName("RefreshSummaryText")).Text.Contains("마지막 확인", StringComparison.Ordinal), "Repeated main failure lost its last-check label");
            Assert(((TextBlock)window.FindName("BoxNameText")).Text == actionableSnapshot.BoxName, "Repeated main failure discarded the last observation");
            Assert(!((Button)window.FindName("LaunchButton")).IsEnabled, "Repeated failure restored stale launch");
            mainController.FailInspect = false;
            window.RefreshNowAsync().GetAwaiter().GetResult();
            Assert(((Button)window.FindName("LaunchButton")).IsEnabled, "Main inspection retry did not restore launch");
            Assert(((Button)window.FindName("StopButton")).IsEnabled, "Main inspection retry did not restore stop");
            Assert(mainController.InspectCount >= 2, "Main inspection retry did not call controller again");
        }

        var runningSnapshot = actionableSnapshot with { State = Readiness.RunningUnverified, BoxProcessIds = [12345], CanLaunch = false, ProcessPolicy = ProcessPolicyStatus.EffectiveRulesUnverified };
        using (var recoveryController = new PreviewController(runningSnapshot, CreateApprovalPlan()))
        {
            var window = new MainWindow(recoveryController, isRenderMode: false);
            window.ApplySnapshot(actionableSnapshot);
            window.ApplyRecoveryPlan(CreateApprovalPlan());
            recoveryController.FailRecoveryInspect = true;
            window.RefreshNowAsync().GetAwaiter().GetResult();

            Assert(((TextBlock)window.FindName("StatusTitleText")).Text.Contains("실행 중", StringComparison.Ordinal), "Recovery failure hid the fresh running observation");
            Assert(((TextBlock)window.FindName("StatusDetailText")).Text.Contains("복구", StringComparison.Ordinal), "Recovery inspection failure did not identify its stage");
            Assert(((TextBlock)window.FindName("ProcessIdsText")).Text.Contains("12345", StringComparison.Ordinal), "Recovery failure lost the observed process");
            Assert(((TextBlock)window.FindName("BoxNameText")).Text == actionableSnapshot.BoxName, "Recovery inspection failure discarded fresh box evidence");
            Assert(((TextBlock)window.FindName("CheckedAtText")).Text == actionableSnapshot.CheckedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), "Recovery inspection failure discarded fresh timestamp");
            Assert(((Button)window.FindName("StopButton")).IsEnabled, "Optional recovery inspection failure blocked freshly verified stop");
            Assert(!((Button)window.FindName("PrepareRecoveryButton")).IsEnabled, "Recovery inspection failure left recovery enabled");

            recoveryController.FailRecoveryInspect = false;
            window.RefreshNowAsync().GetAwaiter().GetResult();
            Assert(((Button)window.FindName("StopButton")).IsEnabled, "Recovery inspection retry did not restore stop");
            Assert(((Button)window.FindName("PrepareRecoveryButton")).IsEnabled, "Recovery inspection retry did not restore recovery");
            Assert(recoveryController.RecoveryInspectCount >= 2, "Recovery inspection retry did not call controller again");
        }

        using (var controller = new PreviewController(actionableSnapshot, CreateApprovalPlan()))
        {
            var window = new MainWindow(controller, isRenderMode: false);
            window.RefreshNowAsync().GetAwaiter().GetResult();
            Assert(((Button)window.FindName("LaunchButton")).IsEnabled, "Optional prevention preparation blocked current launch");
            Assert(controller.RecoveryInspectCount == 1, "Refresh repeated full recovery inspection");
            controller.FailRecoveryInspect = true;
            window.RefreshNowAsync().GetAwaiter().GetResult();
            Assert(((Button)window.FindName("LaunchButton")).IsEnabled, "Fresh healthy execution depended on recovery query success");
            Assert(!((Button)window.FindName("PrepareRecoveryButton")).IsEnabled, "Failed recovery query retained repair authority");
            controller.FailInspect = true;
            window.RefreshNowAsync().GetAwaiter().GetResult();
            Assert(!((Button)window.FindName("LaunchButton")).IsEnabled, "Failed execution query reused old launch authority");
            controller.FailRecoveryInspect = false;
            window.ApplyRecoveryPlan(CreateHealthyPlan());
            Assert(!((Button)window.FindName("LaunchButton")).IsEnabled, "Recovery plan restored stale launch authority without a new execution observation");
            window.ApplyInterruptedEvidence(new([new("original.tmp", "A", 1, SetupRecoveryEvidenceKind.IncompleteRecord, null, true, "잘린 기록")],
                true, true, "원본 보존 가능", "preserved", "snapshot", "scope"));
            Assert(((Button)window.FindName("PreserveEvidenceButton")).IsEnabled, "Blocked normal repair disabled independently verified preservation");
            controller.FailInspect = false;
            window.RefreshNowAsync().GetAwaiter().GetResult();
            Assert(((Button)window.FindName("LaunchButton")).IsEnabled, "New execution observation did not restore launch authority");
            Assert(controller.WritableCallCount == 0, "Refresh performed a write");
        }

        const string optionalConsentWarning = "설정 복구는 검증됐지만 자동 복구 동의를 저장하지 못했습니다. 원본 기록을 보존했습니다.";
        using (var warningController = new PreviewController(actionableSnapshot with { RecoveryResultDetail = optionalConsentWarning }, CreateHealthyPlan()))
        {
            var window = new MainWindow(warningController, isRenderMode: false);
            window.RefreshNowAsync().GetAwaiter().GetResult();
            Assert(((TextBlock)window.FindName("RecoveryResultText")).Text.Contains(optionalConsentWarning, StringComparison.Ordinal), "Successful repair discarded optional consent persistence warning");
            Assert(((Border)window.FindName("RecoveryPanel")).Visibility == Visibility.Visible, "Healthy preparation hid the optional consent persistence warning");
            Assert(((Button)window.FindName("LaunchButton")).IsEnabled, "Optional persistence warning blocked fresh verified launch");
            window.RefreshNowAsync().GetAwaiter().GetResult();
            Assert(((TextBlock)window.FindName("RecoveryResultText")).Text.Contains(optionalConsentWarning, StringComparison.Ordinal), "Healthy refresh removed the completed repair warning");
            Assert(warningController.WritableCallCount == 0, "Warning display retried configuration writes");
        }

        var failures = InspectionDiagnostics.Snapshot();
        Assert(failures.Any(failure => failure.Stage == "main-inspection"), "Main inspection failure was not recorded");
        Assert(failures.Any(failure => failure.Stage == "recovery-inspection"), "Recovery inspection failure was not recorded");
    }
    private static void RenderWindow(
        App app,
        string output,
        string fileName,
        AppSnapshot snapshot,
        SetupRecoveryPlan plan,
        bool setupTab,
        bool compactWindow,
        Action<MainWindow>? assertion = null)
    {
        using var controller = new PreviewController(snapshot, plan);
        var window = new MainWindow(controller, isRenderMode: true);
        window.PrepareRender(setupTab, compactWindow);
        window.ApplySnapshot(snapshot);
        window.ApplyRecoveryPlan(plan);
        window.Show();
        app.Dispatcher.Invoke(() => window.UpdateLayout(), DispatcherPriority.Render);
        assertion?.Invoke(window);
        window.RenderToPng(Path.Combine(output, fileName));
        window.Close();
    }

    private static AppSnapshot CreateSnapshot()
    {
        var kakao = new DependencyInfo(
            "공식 카카오톡",
            @"C:\Program Files (x86)\Kakao\KakaoTalk\KakaoTalk.exe",
            "26.8.0.5312",
            "설치됨 · 배포자 서명 확인",
            true);
        var sandboxie = new DependencyInfo(
            "Sandboxie Plus",
            @"C:\Program Files\Sandboxie-Plus\SandMan.exe",
            "1.18.4",
            "설치됨 · 배포자 서명 확인",
            true);

        return new AppSnapshot(
            Readiness.Ready,
            "보조 카카오톡 실행 준비",
            "현재 구성과 빈 상자를 확인했습니다.",
            "SideTalk_visual_preview",
            @"C:\Users\preview\AppData\Local\SIDETALK\Boxes\SideTalk_visual_preview",
            kakao,
            sandboxie,
            [],
            ["화면 검증용 예시 · 실제 계정이나 설치 상태를 조회하지 않음"],
            true,
            false,
            DateTimeOffset.Now,
            CurrentConfigurationVerified: true,
            ProcessPolicy: ProcessPolicyStatus.NoProcesses);
    }

    private static SetupRecoveryPlan CreateApprovalPlan()
    {
        var changes = new List<SetupRecoveryChange>
        {
            new(
                "GlobalSettings",
                "Template",
                PreviewTemplateNames,
                Array.Empty<string>(),
                SetupRecoveryChangeKind.RemoveGlobalTemplate,
                "검토된 Sandboxie 전역 호환성 템플릿 제거"),
            new(
                "UserSettings_PREVIEW",
                "SbieCtrl_AutoRunSoftCompat",
                new[] { "y" },
                new[] { "n" },
                SetupRecoveryChangeKind.SetUserSetting,
                "SbieCtrl 자동 호환성 팝업 재발 방지")
        };

        return new SetupRecoveryPlan(
            "preview-approval",
            1,
            SetupRecoveryState.ApprovalRequired,
            "검토된 설정만 백업한 뒤 복구하고 실행을 준비합니다.",
            changes,
            Array.Empty<string>(),
            new[]
            {
                "GlobalSettings.Template",
                "UserSettings_PREVIEW.SbieCtrl_AutoRunSoftCompat"
            },
            RequiresApproval: true,
            AllowAutomatic: false,
            ConfigPath: @"C:\preview\Sandboxie.ini",
            Fingerprint: CreateFingerprint(),
            BackupPath: @"C:\Users\preview\AppData\Local\SIDETALK\Recovery\preview-backup.ini",
            HistoryPath: @"C:\Users\preview\AppData\Local\SIDETALK\Recovery",
            CreatedAtUtc: DateTimeOffset.UtcNow);
    }

    private static SetupRecoveryPlan CreateHealthyPlan()
    {
        return new SetupRecoveryPlan(
            "preview-healthy",
            1,
            SetupRecoveryState.Healthy,
            "현재 설정과 전용 환경이 검증된 상태입니다.",
            Array.Empty<SetupRecoveryChange>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            RequiresApproval: false,
            AllowAutomatic: false,
            ConfigPath: @"C:\preview\Sandboxie.ini",
            Fingerprint: CreateFingerprint(),
            BackupPath: null,
            HistoryPath: @"C:\Users\preview\AppData\Local\SIDETALK\Recovery",
            CreatedAtUtc: DateTimeOffset.UtcNow);
    }

    private static SetupRecoveryFingerprint CreateFingerprint()
    {
        return new SetupRecoveryFingerprint(
            @"C:\preview\Sandboxie.ini",
            "ini-preview",
            "explicit-preview",
            "effective-preview",
            "sections-preview",
            "profile-preview",
            "install-preview",
            "S-1-5-21-preview",
            "SideTalk_visual_preview",
            @"C:\Users\preview\AppData\Local\SIDETALK\Boxes\SideTalk_visual_preview",
            "ui-preview");
    }

    private static IReadOnlyList<string> Items(ItemsControl control)
    {
        return control.Items.Cast<object?>()
            .Select(item => item?.ToString() ?? string.Empty)
            .ToArray();
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

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("UI preview assertion failed: " + message);
    }
}

internal sealed class PreviewController : ISidetalkController
{
    private readonly AppSnapshot _snapshot;
    private readonly SetupRecoveryPlan _plan;
    private readonly bool _failPrepare;

    public bool FailInspect { get; set; }
    public bool FailRecoveryInspect { get; set; }

    public PreviewController(AppSnapshot snapshot, SetupRecoveryPlan plan, bool failPrepare = false)
    {
        _snapshot = snapshot;
        _plan = plan;
        _failPrepare = failPrepare;
    }

    public int WritableCallCount { get; private set; }
    public int InspectCount { get; private set; }
    public int RecoveryInspectCount { get; private set; }

    public string RecoveryHistoryPath => _plan.HistoryPath;

    public Task<AppSnapshot> InspectAsync()
    {
        InspectCount++;
        if (FailInspect)
            throw new InvalidOperationException("preview main inspect failure");
        return Task.FromResult(_snapshot);
    }

    public Task<SetupRecoveryPlan> InspectSetupRecoveryAsync()
    {
        RecoveryInspectCount++;
        if (FailRecoveryInspect)
            throw new InvalidOperationException("preview recovery inspect failure");
        return Task.FromResult(_plan);
    }

    public SetupRecoveryEvidenceInspection EvidenceInspection { get; set; } = new([], false, false, "중단 흔적 없음", "", "", "");
    public Task<SetupRecoveryEvidenceInspection> InspectInterruptedEvidenceAsync() => Task.FromResult(EvidenceInspection);
    public Task<AppSnapshot> ReconcileInterruptedEvidenceAsync(SetupRecoveryEvidenceInspection approvedInspection, IProgress<string>? progress = null)
    {
        WritableCallCount++;
        return Task.FromResult(_snapshot);
    }

    public Task<AppSnapshot> PrepareAsync(
        SetupRecoveryPlan approvedPlan,
        bool allowAutomatic,
        IProgress<string>? progress = null)
    {
        WritableCallCount++;
        progress?.Report("복구 적용 중…");
        if (_failPrepare)
            throw new InvalidOperationException("preview apply failure");
        return Task.FromResult(_snapshot);
    }

    public Task<AppSnapshot> PrepareAutomaticallyAsync(IProgress<string>? progress = null)
    {
        WritableCallCount++;
        progress?.Report("자동 복구 적용 중…");
        if (_failPrepare)
            throw new InvalidOperationException("preview automatic apply failure");
        return Task.FromResult(_snapshot);
    }

    public Task RevokeAutomaticRecoveryAsync()
    {
        WritableCallCount++;
        return Task.CompletedTask;
    }

    public Task LaunchAsync() => throw new InvalidOperationException("Preview only");

    public Task StopAsync() => throw new InvalidOperationException("Preview only");

    public Task<string> ExportSetupAsync(string folder) => throw new InvalidOperationException("Preview only");

    public Task<string> ExportDiagnosticsAsync(string path) => throw new InvalidOperationException("Preview only");

    public void Dispose() { }
}
