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
        if (args.Length != 1)
            throw new ArgumentException("Usage: UiPreview <output-directory>");

        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);

        var baseSnapshot = CreateSnapshot();
        RenderBasicFixtures(app, output, baseSnapshot);
        RenderRecoveryFixtures(app, output, baseSnapshot);
        AssertInterruptedChoiceReset(baseSnapshot);

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
                     "OpenRecoveryHistoryButton"
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

    public PreviewController(AppSnapshot snapshot, SetupRecoveryPlan plan, bool failPrepare = false)
    {
        _snapshot = snapshot;
        _plan = plan;
        _failPrepare = failPrepare;
    }

    public int WritableCallCount { get; private set; }

    public string RecoveryHistoryPath => _plan.HistoryPath;

    public Task<AppSnapshot> InspectAsync() => Task.FromResult(_snapshot);

    public Task<SetupRecoveryPlan> InspectSetupRecoveryAsync() => Task.FromResult(_plan);

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
