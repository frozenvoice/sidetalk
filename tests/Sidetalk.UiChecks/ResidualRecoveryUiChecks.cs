using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Sidetalk.App;
using Sidetalk.Core;

internal static class ResidualRecoveryUiChecks
{
    public static void Run(AppSnapshot baseline, SetupRecoveryPlan healthy, SetupRecoveryPlan approval, string output)
    {
        var profile = new BoxProfile(1, baseline.BoxName, "preview-owner", "S-1-5-21-preview", baseline.StoragePath);
        var repair = new SetupRecoveryChange(baseline.BoxName, "DropAdminRights", [], ["y"], SetupRecoveryChangeKind.RepairOwnedBoxSetting);
        var repairPlan = approval with
        {
            State = SetupRecoveryState.Blocked,
            Changes = [repair],
            Blockers = ["전용 환경의 잔여 프로세스를 먼저 정리해야 합니다."],
            RequiresApproval = false,
            AllowAutomatic = false,
            IsFirstSetup = false,
            Summary = "카카오톡은 종료됐지만 전용 환경에 잔여 프로세스가 남아 있습니다."
        };
        var helpers = new[]
        {
            new BoxProcess(1401, "helperA.exe", profile.UserSid, 1),
            new BoxProcess(1402, "helperB.exe", profile.UserSid, 1),
            new BoxProcess(1403, "helperC.exe", profile.UserSid, 1),
            new BoxProcess(1404, "helperD.exe", profile.UserSid, 1)
        };
        var residualApproval = new ResidualBoxRecoveryApproval(profile, repairPlan, helpers,
            @"C:\Synthetic-Sandboxie\Start.exe", @"C:\Synthetic-SIDETALK\KakaoTalk.exe");
        var residualPlan = repairPlan with { ResidualCleanupApproval = residualApproval };

        var missing = baseline with
        {
            State = Readiness.Blocked,
            Title = "보조 환경의 관리자 권한 제한이 변경되었습니다",
            Detail = "전용 환경 프로세스가 남아 있어 설정 복구를 기다리고 있습니다.",
            CanLaunch = false,
            CanStop = false,
            CurrentConfigurationVerified = false,
            AdminRights = new([], [], AdminRightsCondition.MissingOrEmpty, true, helpers.Select(process => process.Id).ToArray(), null, [])
            {
                BoxProcesses = helpers.Select(process => new BoxProcessObservation(process.Id, process.ImageName, process.SessionId, true)).ToArray()
            }
        };

        Verify(missing, residualPlan, "residual", output, window =>
        {
            Check(Text(window, "AdminRightsActionText").Contains("카카오톡은 종료됐습니다", StringComparison.Ordinal), "Residual state does not say KakaoTalk exited");
            Check(Text(window, "AdminRightsActionText").Contains("잔여 프로세스 4개", StringComparison.Ordinal), "Residual count is missing");
            Check(Text(window, "ResidualProcessesText").Contains("helperA.exe · PID 1401", StringComparison.Ordinal), "Residual image/PID is missing");
            Check(Text(window, "ResidualProcessesText").Contains("helperD.exe · PID 1404", StringComparison.Ordinal), "Last residual image/PID is missing");
            Check(Find<Button>(window, "ResidualCleanupButton").Visibility == Visibility.Visible, "Residual cleanup action is hidden");
            Check(!Find<Button>(window, "ResidualCleanupButton").IsEnabled, "Read-only renderer enabled residual cleanup");
            Check(!Text(window, "AdminRightsActionText").Contains("트레이 메뉴에서 정상 종료", StringComparison.Ordinal), "Residual state asks for a normal KakaoTalk exit");
        });

        Verify(missing, repairPlan, "residual-blocked", output, window =>
        {
            Check(Find<Button>(window, "ResidualCleanupButton").Visibility == Visibility.Visible, "Residual evidence hid the blocked cleanup action");
            Check(!Find<Button>(window, "ResidualCleanupButton").IsEnabled, "Cleanup without a controller-issued approval was enabled");
            Check(Find<Button>(window, "ResidualCleanupButton").ToolTip?.ToString()?.Contains("차단", StringComparison.Ordinal) == true,
                "Blocked cleanup reason is not available");
        });

        var kakao = helpers[0] with { ImageName = "KakaoTalk.exe" };
        Verify(missing with
        {
            BoxProcessIds = [kakao.Id],
            AdminRights = missing.AdminRights! with
            {
                ProcessIds = [kakao.Id],
                BoxProcesses = [new BoxProcessObservation(kakao.Id, kakao.ImageName, kakao.SessionId, true)]
            }
        }, repairPlan, "kakao-running", output, window =>
        {
            Check(Text(window, "AdminRightsActionText").Contains("트레이 메뉴에서 정상 종료", StringComparison.Ordinal), "Running KakaoTalk lacks normal exit guidance");
            Check(Find<Button>(window, "ResidualCleanupButton").Visibility == Visibility.Collapsed, "Running KakaoTalk exposed residual cleanup");
        });

        Verify(missing with
        {
            BoxProcessIds = [kakao.Id, helpers[1].Id],
            AdminRights = missing.AdminRights! with
            {
                ProcessIds = [kakao.Id, helpers[1].Id],
                BoxProcesses = [
                    new BoxProcessObservation(kakao.Id, kakao.ImageName, kakao.SessionId, true),
                    new BoxProcessObservation(helpers[1].Id, helpers[1].ImageName, helpers[1].SessionId, true)]
            }
        }, repairPlan, "kakao-mixed", output, window =>
        {
            Check(Text(window, "AdminRightsActionText").Contains("트레이 메뉴에서 정상 종료", StringComparison.Ordinal), "Mixed state does not prioritize running KakaoTalk");
            Check(Find<Button>(window, "ResidualCleanupButton").Visibility == Visibility.Collapsed, "Mixed state exposed residual cleanup");
        });

        Verify(missing with
        {
            BoxProcessIds = [helpers[0].Id],
            AdminRights = missing.AdminRights! with
            {
                ProcessIds = [helpers[0].Id],
                BoxProcesses = [new BoxProcessObservation(helpers[0].Id, helpers[0].ImageName, helpers[0].SessionId, false)]
            }
        }, repairPlan, "unknown-process", output, window =>
        {
            Check(Text(window, "ResidualProcessesText").Contains("신원 미확인 전용 프로세스 · PID 1401", StringComparison.Ordinal), "Unknown process identity is hidden");
            Check(!Text(window, "AdminRightsActionText").Contains("카카오톡은 종료됐습니다", StringComparison.Ordinal), "Unknown process state claims KakaoTalk exited");
            Check(Find<Button>(window, "ResidualCleanupButton").Visibility == Visibility.Collapsed, "Unknown process identity exposed cleanup");
        });

        var empty = missing with
        {
            BoxProcessIds = [],
            AdminRights = missing.AdminRights! with { ProcessIds = [], BoxProcesses = [] }
        };
        Verify(empty, approval with
        {
            State = SetupRecoveryState.ApprovalRequired,
            Changes = [repair],
            Blockers = [],
            RequiresApproval = true,
            AllowAutomatic = false,
            Summary = "전용 환경이 비어 있어 관리자 권한 제한 복구를 새로 승인할 수 있습니다."
        }, "empty", output, window =>
        {
            Check(Text(window, "AdminRightsActionText").Contains("전용 환경이 비어 있는 것을 확인", StringComparison.Ordinal), "Empty state does not expose explicit repair path");
            Check(Find<Button>(window, "ResidualCleanupButton").Visibility == Visibility.Collapsed, "Empty state exposed residual cleanup");
        });

        VerifyInteractiveAvailability(missing, residualPlan);
        Console.WriteLine("Residual-recovery WPF checks: 6 scenarios passed; Kakao/residual/mixed/unknown/empty and interactive availability; cleanup controller writes=0.");
    }

    private static void VerifyInteractiveAvailability(AppSnapshot snapshot, SetupRecoveryPlan plan)
    {
        using var controller = new PreviewController(snapshot, plan);
        var window = new MainWindow(controller);
        try
        {
            window.ApplySnapshot(snapshot);
            window.ApplyRecoveryPlan(plan);
            Check(Find<Button>(window, "ResidualCleanupButton").IsEnabled, "Interactive residual approval does not enable cleanup");
            Check(!Find<Button>(window, "LaunchButton").IsEnabled && !Find<Button>(window, "StopButton").IsEnabled, "Residual approval grants ordinary command permissions");
            window.ApplyRecoveryPlan(plan with { ResidualCleanupApproval = null });
            Check(!Find<Button>(window, "ResidualCleanupButton").IsEnabled, "Missing Core authority enables cleanup");
            window.ApplySnapshot(snapshot with { AdminRights = snapshot.AdminRights! with { Condition = AdminRightsCondition.Expected } });
            Check(Find<Button>(window, "ResidualCleanupButton").Visibility == Visibility.Collapsed, "Healthy helper state exposes drift recovery");
            Check(controller.WritableCallCount == 0, "Availability inspection performed a write");
        }
        finally { window.Close(); }
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
            Check(!Find<Button>(window, "LaunchButton").IsEnabled && !Find<Button>(window, "StopButton").IsEnabled, "Read-only renderer enabled normal command control");
            Check(!Find<Button>(window, "KakaoUpdateGuideButton").IsEnabled, "Read-only renderer enabled external update guide");
            window.Show();
            foreach (var compact in new[] { false, true })
            {
                window.PrepareRender(true, compact);
                Find<Expander>(window, "KakaoUpdateExpander").IsExpanded = true;
                window.Dispatcher.Invoke(window.UpdateLayout, DispatcherPriority.Render);
                var target = Find<Button>(window, "ResidualCleanupButton");
                if (target.Visibility == Visibility.Visible) target.BringIntoView();
                else Find<Expander>(window, "KakaoUpdateExpander").BringIntoView();
                window.Dispatcher.Invoke(window.UpdateLayout, DispatcherPriority.Render);
                window.RenderToPng(Path.Combine(output, $"residual-recovery-{name}-{(compact ? "min" : "default")}.png"));
            }
            window.PrepareRender(false, true);
            Find<TabControl>(window, "MainTabs").SelectedIndex = 0;
            window.Dispatcher.Invoke(window.UpdateLayout, DispatcherPriority.Render);
            window.RenderToPng(Path.Combine(output, $"residual-recovery-{name}-main-min.png"));
            Check(controller.WritableCallCount == 0, "Rendering performed a controller write");
        }
        finally
        {
            window.Close();
        }
    }

    private static T Find<T>(MainWindow window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException("Missing production control " + name);

    private static string Text(MainWindow window, string name) => Find<TextBlock>(window, name).Text;

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Residual-recovery WPF assertion failed: " + message);
    }
}
