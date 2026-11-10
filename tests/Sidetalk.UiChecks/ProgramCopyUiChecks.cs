using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Sidetalk.App;
using Sidetalk.Core;

/// <summary>Production window and handlers with a synthetic controller; no real box, KakaoTalk or file is touched.</summary>
internal static class ProgramCopyUiChecks
{
    public static void Run(AppSnapshot ready, SetupRecoveryPlan healthy, string output)
    {
        var cases = 0;
        var offered = ready with { StaleProgramCopy = Copy(canSetAside: true) };

        // Read-only render: the guidance is visible, the action is not.
        {
            using var controller = new PreviewController(offered, healthy);
            var window = new MainWindow(controller, isRenderMode: true);
            try
            {
                window.ApplySnapshot(offered);
                Check(Find<Border>(window, "ProgramCopyCard").Visibility == Visibility.Visible, "The older copy is not explained on the main tab");
                Check(Text(window, "ProgramCopyBodyText").Contains("26.8.2.5324", StringComparison.Ordinal) &&
                      Text(window, "ProgramCopyBodyText").Contains("26.9.0.5337", StringComparison.Ordinal) &&
                      Text(window, "ProgramCopyBodyText").Contains("손상", StringComparison.Ordinal), "Versions or the visible symptom are missing");
                Check(Text(window, "ProgramCopyScopeText").Contains("삭제하지 않고", StringComparison.Ordinal) &&
                      Text(window, "ProgramCopyScopeText").Contains("계정·대화 데이터", StringComparison.Ordinal), "The preserved scope is not stated before approval");
                Check(!Find<Button>(window, "ProgramCopyButton").IsEnabled, "Read-only renderer enables the move");
                window.Show();
                foreach (var compact in new[] { false, true })
                {
                    window.PrepareRender(false, compact);
                    window.Dispatcher.Invoke(window.UpdateLayout, DispatcherPriority.Render);
                    window.RenderToPng(Path.Combine(output, $"ui-program-copy-{(compact ? "min" : "default")}-fixture.png"));
                }
                Check(controller.WritableCallCount == 0, "Rendering invoked a write");
            }
            finally { window.Close(); }
            cases++;
        }

        // No observation: no card.
        {
            using var controller = new CopyController(ready, healthy);
            var window = Create(controller, confirm: true, out _);
            window.ApplySnapshot(ready);
            Check(Find<Border>(window, "ProgramCopyCard").Visibility == Visibility.Collapsed, "Guidance is shown without an observed older copy");
            window.Close();
            cases++;
        }

        // A running box explains why, and the action stays unavailable.
        {
            var running = ready with { State = Readiness.RunningUnverified, CanLaunch = false, CanStop = true, StaleProgramCopy = Copy(canSetAside: false) };
            using var controller = new CopyController(running, healthy);
            var window = Create(controller, confirm: true, out _);
            window.ApplySnapshot(running);
            var button = Find<Button>(window, "ProgramCopyButton");
            Check(button.Visibility == Visibility.Visible && !button.IsEnabled, "A running box enables the move");
            Check(Text(window, "ProgramCopyBodyText").Contains("종료한 뒤", StringComparison.Ordinal), "A running box does not say what to do first");
            Click(window);
            Idle();
            Check(controller.SetAsideCalls == 0, "A click on the unavailable action reached the controller");
            window.Close();
            cases++;
        }

        // Declined approval changes nothing.
        {
            using var controller = new CopyController(offered, healthy);
            var window = Create(controller, confirm: false, out var confirmations);
            window.ApplySnapshot(offered);
            Click(window);
            PumpUntil(() => Text(window, "ProgramCopyStatusText").Contains("옮기지 않았습니다", StringComparison.Ordinal), "the declined result");
            Check(confirmations.Count == 1 && controller.SetAsideCalls == 0, "Declining did not ask once or still moved the copy");
            Check(Find<Button>(window, "ProgramCopyButton").Visibility == Visibility.Visible, "The action disappeared after declining");
            window.Close();
            cases++;
        }

        // Approved: exactly the observed scope is passed, the result is shown, launch becomes the next step.
        {
            using var controller = new CopyController(offered, healthy) { AfterMove = ready with { RecoveryResultDetail = @"오래된 프로그램 사본(26.8.2.5324)을 보존 폴더로 옮겼습니다. 보존 위치: C:\Synthetic\program-copy-preserved\x\KakaoTalk" } };
            var window = Create(controller, confirm: true, out var confirmations);
            window.ApplySnapshot(offered);
            Check(Find<Button>(window, "ProgramCopyButton").IsEnabled && Find<Button>(window, "LaunchButton").IsEnabled, "The verified empty state does not offer the move");
            Click(window);
            PumpUntil(() => Text(window, "ProgramCopyTitleText") == "오래된 프로그램 사본을 치웠어요", "the completed result");
            Check(confirmations.Count == 1 && ReferenceEquals(confirmations[0], offered.StaleProgramCopy), "Approval did not describe the observed copy");
            Check(controller.SetAsideCalls == 1 && ReferenceEquals(controller.Approved, offered.StaleProgramCopy), "The controller did not receive the approved observation once");
            Check(Text(window, "ProgramCopyStatusText").Contains("보존 위치", StringComparison.Ordinal), "The preserved location is not shown");
            Check(Find<Button>(window, "ProgramCopyButton").Visibility == Visibility.Collapsed && Find<Border>(window, "ProgramCopyCard").Visibility == Visibility.Visible,
                "The result is not kept visible after the action disappears");
            Check(controller.OtherWrites == 0, "The move invoked launch, stop or recovery");
            window.Close();
            cases++;
        }

        // A refused move keeps the reason and the action.
        {
            using var controller = new CopyController(offered, healthy) { Failure = new InvalidDataException("승인 후 프로그램 사본이나 공식 설치가 바뀌었습니다. 상태를 다시 확인하고 승인하세요.") };
            var window = Create(controller, confirm: true, out _);
            window.ApplySnapshot(offered);
            Click(window);
            PumpUntil(() => Text(window, "ProgramCopyStatusText").Contains("바뀌었습니다", StringComparison.Ordinal), "the refusal reason");
            Check(Find<Button>(window, "ProgramCopyButton").Visibility == Visibility.Visible, "The action disappeared after a refused move");
            window.Close();
            cases++;
        }
        Console.WriteLine($"Program-copy WPF checks passed: {cases} cases (render, absent, running, declined, approved, refused); synthetic controller only.");
    }

    private static StaleProgramCopyObservation Copy(bool canSetAside) => new(
        @"C:\Synthetic-SIDETALK\Boxes\SideTalk_visual_preview\drive\C\Program Files (x86)\Kakao\KakaoTalk", "26.8.2.5324", "26.9.0.5337",
        1, 26_307_176, new string('A', 64), canSetAside,
        canSetAside ? "전용 환경이 비어 있어 지금 치울 수 있습니다." : "보조 카카오톡과 남은 전용 프로세스를 종료한 뒤 치울 수 있습니다.");

    private static MainWindow Create(CopyController controller, bool confirm, out List<StaleProgramCopyObservation> confirmations)
    {
        var asked = new List<StaleProgramCopyObservation>();
        confirmations = asked;
        return new MainWindow(controller, _ => Task.FromResult(false))
        {
            ShowActivated = false,
            ConfirmProgramCopy = copy => { asked.Add(copy); return confirm; }
        };
    }

    // Inside a dispatcher operation, as a real click is: awaits resume on the window's thread.
    private static void Click(MainWindow window) => window.Dispatcher.Invoke(() =>
        Find<Button>(window, "ProgramCopyButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)));

    private static void Idle() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void PumpUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Check(DateTime.UtcNow < deadline, $"Timed out waiting for {what}");
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(10);
        }
    }

    private static T Find<T>(MainWindow window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException("Missing production control " + name);
    private static string Text(MainWindow window, string name) => Find<TextBlock>(window, name).Text;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Program-copy fixture: " + message);
    }

    private sealed class CopyController(AppSnapshot initial, SetupRecoveryPlan plan) : ISidetalkController
    {
        private AppSnapshot current = initial;
        public AppSnapshot? AfterMove { get; set; }
        public Exception? Failure { get; set; }
        public int SetAsideCalls { get; private set; }
        public int OtherWrites { get; private set; }
        public StaleProgramCopyObservation? Approved { get; private set; }
        public string RecoveryHistoryPath => plan.HistoryPath;
        public Task<AppSnapshot> InspectAsync() => Task.FromResult(current with { CheckedAt = DateTimeOffset.Now, RecoveryResultDetail = null });
        public async Task<AppSnapshot> SetAsideStaleProgramCopyAsync(StaleProgramCopyObservation approved, IProgress<string>? progress = null)
        {
            SetAsideCalls++;
            Approved = approved;
            await Task.Yield();
            if (Failure is not null) throw Failure;
            progress?.Report("오래된 프로그램 사본을 보존 폴더로 옮기는 중…");
            current = AfterMove ?? current;
            return current;
        }
        public Task<SetupRecoveryPlan> InspectSetupRecoveryAsync() => Task.FromResult(plan);
        public Task<SetupRecoveryEvidenceInspection> InspectInterruptedEvidenceAsync() => Task.FromResult(new SetupRecoveryEvidenceInspection([], false, false, "중단 흔적 없음", "", "", ""));
        public Task<AppSnapshot> ReconcileInterruptedEvidenceAsync(SetupRecoveryEvidenceInspection approvedInspection, IProgress<string>? progress = null) => Write();
        public Task<AppSnapshot> PrepareAsync(SetupRecoveryPlan approvedPlan, bool allowAutomatic, IProgress<string>? progress = null) => Write();
        public Task<AppSnapshot> PrepareAutomaticallyAsync(IProgress<string>? progress = null) => Write();
        public Task RevokeAutomaticRecoveryAsync() => Write();
        public Task LaunchAsync() => Write();
        public Task StopAsync() => Write();
        public Task<string> ExportSetupAsync(string folder) => throw new InvalidOperationException("Not used");
        public Task<string> ExportDiagnosticsAsync(string path) => throw new InvalidOperationException("Not used");
        public void Dispose() { }
        private Task<AppSnapshot> Write()
        {
            OtherWrites++;
            return Task.FromResult(current);
        }
    }
}
