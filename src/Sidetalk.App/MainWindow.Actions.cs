using System.Windows;
using Sidetalk.Core;

namespace Sidetalk.App;

public partial class MainWindow
{
    private async void LaunchButton_Click(object sender, RoutedEventArgs e) =>
        await RequestActionAsync(SidetalkCommandAction.Launch);

    private async void StopButton_Click(object sender, RoutedEventArgs e) =>
        await RequestActionAsync(SidetalkCommandAction.Stop);

    private static bool Permits(AppSnapshot? snapshot, SidetalkCommandAction action) =>
        snapshot is { ActionsPermitted: true, InspectionFailure: null } &&
        (action == SidetalkCommandAction.Launch ? snapshot.CanLaunch : snapshot.CanStop);

    private bool CanRequestAction(SidetalkCommandAction action) =>
        !_closed && !_isRenderMode && !_windowMinimized && !_restoreInspectionRequired &&
        !_unsafeActionsInvalidated && !_actionInFlight && !_operationInFlight && !_settingsLoading &&
        Permits(_lastObservedSnapshot, action);

    private bool IsCurrentIntent(long intent) =>
        !_closed && !_windowMinimized && !_intentCancelled && intent == _intentGeneration;

    internal async Task RequestActionAsync(SidetalkCommandAction action)
    {
        if (action is not (SidetalkCommandAction.Launch or SidetalkCommandAction.Stop) || !CanRequestAction(action)) return;
        // Reserve before any await or modal dialog. Availability means a request
        // can be accepted, never permission to execute from a displayed snapshot.
        var intent = ++_intentGeneration;
        _intentCancelled = false;
        _actionInFlight = true;
        var dispatched = false;
        _actionFeedback = action == SidetalkCommandAction.Launch
            ? "실행 요청 접수 · 현재 상태 확인 중…" : "종료 요청 접수 · 현재 상태 확인 중…";
        UpdateRefreshSummary();
        ApplyActionAvailability();
        try
        {
            await _inspectionTask;
            if (!IsCurrentIntent(intent)) return;
            if (_unsafeActionsInvalidated || !Permits(_lastSnapshot, action))
            {
                _actionFeedback = _unsafeActionsInvalidated
                    ? "확인 실패 · 요청을 실행하지 않았습니다. 다시 확인하세요."
                    : "현재 확인 결과로 요청을 진행할 수 없습니다. 다시 확인하세요.";
                return;
            }

            _actionFeedback = "요청 확인을 기다리고 있습니다.";
            UpdateRefreshSummary();
            var confirmed = await _confirmAction(action);
            if (!IsCurrentIntent(intent)) return;
            if (!confirmed)
            {
                CancelPendingAction();
                return;
            }

            _actionFeedback = "요청 처리 전 현재 상태를 다시 확인하고 있습니다…";
            UpdateRefreshSummary();
            _lastSnapshot = null;
            var generation = _inspectionGeneration;
            AppSnapshot snapshot;
            try { snapshot = await _controller.InspectAsync(); }
            catch (Exception exception)
            {
                if (IsCurrentInspection(generation)) ApplyMainInspectionError(exception);
                _actionFeedback = "확인 실패 · 요청을 실행하지 않았습니다.";
                return;
            }
            if (!IsCurrentInspection(generation)) return;
            if (snapshot.InspectionFailure is { } failure)
            {
                ApplyMainInspectionFailure(failure);
                _actionFeedback = "확인 실패 · 요청을 실행하지 않았습니다.";
                return;
            }
            ApplySnapshot(snapshot);
            if (!IsCurrentIntent(intent)) return;
            if (!Permits(snapshot, action))
            {
                _actionFeedback = "현재 확인 결과로 요청을 진행할 수 없습니다.";
                return;
            }

            // No await between the last intent check and dispatch. Core still
            // revalidates installation/profile/policy under its gate and journal.
            _operationInFlight = true;
            dispatched = true;
            _actionFeedback = action == SidetalkCommandAction.Launch ? "실행 요청 처리 중…" : "종료 요청 처리 중…";
            ClearStaleSnapshot(action == SidetalkCommandAction.Launch ? "실행 요청을 보내고 있습니다." : "전용 상자를 종료하고 있습니다.");
            ApplyActionAvailability();
            if (action == SidetalkCommandAction.Launch) await _controller.LaunchAsync();
            else await _controller.StopAsync();
        }
        catch (Exception exception)
        {
            if (!_closed) ApplyError(exception);
        }
        finally
        {
            // Cancellation keeps ownership until its read/confirmation drains;
            // an old finally can never unlock a more recently accepted request.
            _operationInFlight = false;
            _actionInFlight = false;
            if (!_closed)
            {
                UpdateRefreshSummary();
                ApplyActionAvailability();
                if (dispatched) await InspectAndApplyAsync(allowAutomaticRecovery: true);
            }
        }
    }

    internal void CancelPendingAction()
    {
        if (!_actionInFlight || _operationInFlight || _intentCancelled) return;
        _intentCancelled = true;
        _intentGeneration++;
        _actionFeedback = "요청을 취소했습니다.";
        UpdateRefreshSummary();
        ApplyActionAvailability();
    }

    private void CancelActionButton_Click(object sender, RoutedEventArgs e) => CancelPendingAction();

    private Task<bool> ConfirmActionAsync(SidetalkCommandAction action)
    {
        var launch = action == SidetalkCommandAction.Launch;
        if (launch) return ConfirmLaunchAsync(this);
        var answer = MessageBox.Show(
            this,
            launch
                ? "전용 Sandboxie Plus 상자에서 공식 카카오톡을 실행합니다.\n\n처음에는 전용 INI 파일의 데이터 분리 확인 내용을 읽고 직접 수락한 뒤, 카카오톡 창에서 보조 계정으로 로그인하고 기기 인증을 진행하세요. 메인 계정의 인증·대화 데이터는 복사하지 않습니다.\n\n실행을 계속할까요?"
                : "SIDETALK 전용 Sandboxie Plus 상자 안의 프로세스만 강제 종료합니다.\n\n카카오톡에서 먼저 직접 종료하세요. 강제 종료하면 저장되지 않은 작업이 사라질 수 있습니다. 보조 환경의 설정과 로컬 데이터는 삭제하지 않으며, 메인 카카오톡이나 다른 샌드박스는 건드리지 않습니다.\n\n강제 종료를 계속할까요?",
            launch ? "보조 카카오톡 실행 확인" : "보조 카카오톡 종료 확인",
            MessageBoxButton.OKCancel,
            launch ? MessageBoxImage.Information : MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        return Task.FromResult(answer == MessageBoxResult.OK);
    }

    internal static Task<bool> ConfirmLaunchAsync(Window owner) => Task.FromResult(MessageBox.Show(
        owner,
        "전용 Sandboxie Plus 상자에서 공식 카카오톡을 실행합니다.\n\n처음에는 전용 INI 파일의 데이터 분리 확인 내용을 읽고 직접 수락한 뒤, 카카오톡 창에서 보조 계정으로 로그인하고 기기 인증을 진행하세요. 메인 계정의 인증·대화 데이터는 복사하지 않습니다.\n\n실행을 계속할까요?",
        "보조 카카오톡 실행 확인", MessageBoxButton.OKCancel, MessageBoxImage.Information,
        MessageBoxResult.Cancel) == MessageBoxResult.OK);
}
