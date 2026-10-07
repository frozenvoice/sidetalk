using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Sidetalk.App;
using Sidetalk.Core;

internal static class AdminRightsUiChecks
{
    public static void Run(AppSnapshot baseline, SetupRecoveryPlan healthy, SetupRecoveryPlan approval, string output)
    {
        var repair = new SetupRecoveryChange(baseline.BoxName, "DropAdminRights", ["n"], ["y"], SetupRecoveryChangeKind.RepairOwnedBoxSetting);
        var plan = approval with { State = SetupRecoveryState.ApprovalRequired, Changes = [repair], Blockers = [],
            RequiresApproval = true, AllowAutomatic = false, IsFirstSetup = false,
            Summary = "전용 박스의 관리자 권한 제한 복구에 새 승인이 필요합니다." };
        var disabled = baseline with { State = Readiness.Blocked, Title = "보조 환경의 관리자 권한 제한이 변경되었습니다",
            Detail = "현재 DropAdminRights=n을 확인했습니다. 새 실행과 강제 종료는 차단됩니다.",
            CurrentConfigurationVerified = false, CanLaunch = false, CanStop = false,
            AdminRights = new(["n"], ["n"], AdminRightsCondition.Disabled, true, [], null, []) };

        Verify(baseline with { AdminRights = new(["y"], ["y"], AdminRightsCondition.Expected, true, [], null, []) }, healthy,
            "expected", output, window =>
            {
                Check(Find<TextBlock>(window, "AdminRightsActionText").Visibility == Visibility.Collapsed, "Expected restriction retained repair warning");
                Check(Text(window, "AdminRightsSummaryText").Contains("과거 업데이트 완료", StringComparison.Ordinal), "Expected setting claimed past update completion");
            });
        Verify(disabled, plan, "repair", output, window =>
        {
            Check(Find<CheckBox>(window, "AutomaticRecoveryCheckBox").Visibility == Visibility.Collapsed &&
                !Find<CheckBox>(window, "AutomaticRecoveryCheckBox").IsEnabled, "Security repair exposes old automatic consent");
            Check((string)Find<Button>(window, "PrepareRecoveryButton").Content == "관리자 권한 제한 복구", "Explicit security repair action missing");
            Check(Text(window, "AdminRightsActionText").Contains("새로 승인", StringComparison.Ordinal), "Empty-box repair hides fresh approval requirement");
            Check(Text(window, "RecoveryScopeText").Contains("새 승인", StringComparison.Ordinal), "Security repair displays automatic consent scope");
        });
        Verify(disabled with { BoxProcessIds = [10001], ProcessPolicy = ProcessPolicyStatus.EffectiveRulesUnverified,
            AdminRights = disabled.AdminRights! with
            {
                ProcessIds = [10001],
                BoxProcesses = [new BoxProcessObservation(10001, "KakaoTalk.exe", 1, true)]
            } },
            plan with { State = SetupRecoveryState.Blocked, RequiresApproval = false, Blockers = ["전용 프로세스가 실행 중입니다."] },
            "running", output, window =>
        {
            Check(Text(window, "AdminRightsActionText").Contains("트레이", StringComparison.Ordinal) &&
                Text(window, "AdminRightsActionText").Contains("로그아웃할 필요는 없습니다", StringComparison.Ordinal), "Running repair lacks normal exit/data-preservation guidance");
            Check(!Find<Button>(window, "PrepareRecoveryButton").IsEnabled, "Running environment enables repair");
        });
        Verify(disabled with { AdminRights = new([], [], AdminRightsCondition.MissingOrEmpty, false, [], "합성 프로세스 조회 실패", []) },
            plan with { State = SetupRecoveryState.Blocked, RequiresApproval = false, Blockers = ["프로세스 조회 실패"] },
            "unknown", output, window =>
        {
            Check(Text(window, "AdminRightsValuesText").Contains("전용 프로세스: 미확인", StringComparison.Ordinal), "Process failure was represented as empty box");
            Check(!Text(window, "AdminRightsActionText").Contains("없음을 확인", StringComparison.Ordinal), "Unknown process state claims no processes");
            Check(!Find<Button>(window, "PrepareRecoveryButton").IsEnabled, "Unknown process state enables repair");
        });
        var physicalPath = @"C:\Synthetic-SIDETALK\program\KakaoTalk.exe";
        Verify(baseline with { State = Readiness.RunningUnverified, CanLaunch = false, CanStop = true,
            AdminRights = new(["y"], ["y"], AdminRightsCondition.Expected, true, [10001], null,
                [new(10001, "KakaoTalk.exe", physicalPath, 1, ProgramPhysicalResolution.VerifiedBoxCopy,
                    new(physicalPath, "26.7.0", true, null) { PhysicalPathVerified = true }, true, null)]) }, healthy,
            "version-difference", output, window =>
        {
            Check(Text(window, "KakaoUpdateEvidenceText").Contains("버전 차이를 확인", StringComparison.Ordinal), "Verified file difference is hidden");
            Check(Text(window, "KakaoUpdateEvidenceText").Contains("사본 교체는 아직 지원하지 않습니다", StringComparison.Ordinal), "Unsupported copy replacement is claimed complete");
        });
        Verify(disabled with { AdminRights = new(["y", "n"], ["y", "n"], AdminRightsCondition.Unrecognized, true, [], null, []) },
            healthy, "unrecognized", output, window =>
        {
            Check(Text(window, "AdminRightsSummaryText").Contains("덮어쓰지 않습니다", StringComparison.Ordinal), "Unreviewed restriction is described as repairable");
            Check(!Text(window, "AdminRightsSummaryText").Contains("n으로 변경", StringComparison.Ordinal), "Duplicate value falsely identified as user change to n");
        });
        Console.WriteLine("Admin-rights WPF checks: 6 synthetic presentation scenarios passed; render/controller writes=0.");
    }

    private static void Verify(AppSnapshot snapshot, SetupRecoveryPlan plan, string name, string output, Action<MainWindow> assertion)
    {
        using var controller = new PreviewController(snapshot, plan);
        var window = new MainWindow(controller, isRenderMode: true);
        try
        {
            window.ApplySnapshot(snapshot);
            window.ApplyRecoveryPlan(plan);
            assertion(window);
            Check(!Find<Button>(window, "LaunchButton").IsEnabled && !Find<Button>(window, "StopButton").IsEnabled, "Read-only renderer enables command control");
            Check(!Find<Button>(window, "KakaoUpdateGuideButton").IsEnabled, "Read-only renderer enables external update guide launch");
            window.Show();
            foreach (var compact in new[] { false, true })
            {
                window.PrepareRender(true, compact);
                Find<Expander>(window, "KakaoUpdateExpander").IsExpanded = true;
                window.Dispatcher.Invoke(window.UpdateLayout, DispatcherPriority.Render);
                window.RenderToPng(Path.Combine(output, $"admin-rights-{name}-{(compact ? "min" : "default")}.png"));
            }
            if (name is "running" or "unknown" or "repair")
            {
                window.PrepareRender(false, true);
                Find<TabControl>(window, "MainTabs").SelectedIndex = 0;
                window.UpdateLayout();
                window.RenderToPng(Path.Combine(output, $"admin-rights-{name}-main-min.png"));
            }
            Check(controller.WritableCallCount == 0, "Rendering wrote settings, accounts or command records");
        }
        finally { window.Close(); }
    }
    private static T Find<T>(MainWindow window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException("Missing production control " + name);
    private static string Text(MainWindow window, string name) => Find<TextBlock>(window, name).Text;
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Admin-rights WPF assertion failed: " + message);
    }
}
