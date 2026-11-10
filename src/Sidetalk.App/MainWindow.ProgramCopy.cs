using System.Windows;
using Sidetalk.Core;

namespace Sidetalk.App;

// Guidance for an older KakaoTalk program copy left inside the owned box. Core decides
// what is verified and whether it can be moved; this file only presents that observation,
// asks for one explicit approval and shows the result.
public partial class MainWindow
{
    private string? _programCopyResult;
    private bool _programCopyResultIsError;

    /// <summary>Replaced by UI checks; the default is a real confirmation dialog whose default answer is No.</summary>
    internal Func<StaleProgramCopyObservation, bool>? ConfirmProgramCopy { get; set; }

    private StaleProgramCopyObservation? ObservedProgramCopy => _lastObservedSnapshot?.StaleProgramCopy;

    // KakaoTalk's own updater needs administrator rights the box withholds. SIDETALK cannot see
    // KakaoTalk's dialog, so the hint appears only on evidence: the updater observed among the
    // verified box processes, or right after an older copy was set aside. It then stays for
    // this window, because the dialog outlives the updater process.
    private bool _updateAttemptHint;

    private void UpdateUpdateAttemptHint(AppSnapshot snapshot)
    {
        if (snapshot.AdminRights?.BoxProcesses.Any(process => process.IdentityVerified &&
                process.ImageName.Equals("KakaoUpdate.exe", StringComparison.OrdinalIgnoreCase)) == true)
            _updateAttemptHint = true;
        UpdateAttemptHintText.Visibility = _updateAttemptHint ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateProgramCopyCard()
    {
        if (_closed) return;
        var copy = ObservedProgramCopy;
        ProgramCopyCard.Visibility = copy is not null || _programCopyResult is not null ? Visibility.Visible : Visibility.Collapsed;
        if (copy is not null)
        {
            SetText(ProgramCopyTitleText, "보조 카카오톡 프로그램 사본이 오래됐어요");
            ProgramCopyBodyText.Text =
                $"전용 환경 안에 예전 버전({copy.CopyVersion}) 실행 파일 사본이 남아 있습니다. 공식 카카오톡은 {copy.HostVersion}으로 업데이트되어 두 버전의 파일이 섞이고, " +
                "보조 카카오톡이 ‘카카오톡 파일이 손상되었습니다’ 오류를 낼 수 있습니다. 사본을 치우면 공식 설치의 파일로 실행합니다. " + copy.Guidance;
            ProgramCopyScopeText.Text =
                $"옮기는 것은 전용 환경 안의 프로그램 파일 사본 {copy.FileCount}개뿐입니다. 삭제하지 않고 SIDETALK 보존 폴더에 둡니다. " +
                "계정·대화 데이터, 격리 설정, 공식 카카오톡 설치는 바꾸지 않습니다. 카카오톡을 다시 설치할 필요는 없습니다.";
            ProgramCopyScopeText.Visibility = Visibility.Visible;
        }
        else if (_programCopyResult is not null)
        {
            SetText(ProgramCopyTitleText, _programCopyResultIsError ? "프로그램 사본을 치우지 못했어요" : "오래된 프로그램 사본을 치웠어요");
            ProgramCopyBodyText.Text = _programCopyResultIsError
                ? "전용 환경은 그대로입니다. 아래 이유를 확인하고 ‘상태 새로 확인’ 뒤 다시 시도하세요."
                : "이제 ‘보조 카카오톡 실행’을 누르면 공식 설치의 파일로 실행합니다. 로그인과 대화는 카카오톡 창에서 확인하세요.";
            ProgramCopyScopeText.Visibility = Visibility.Collapsed;
        }
        ProgramCopyStatusText.Text = _programCopyResult ?? string.Empty;
        ProgramCopyStatusText.Visibility = _programCopyResult is null ? Visibility.Collapsed : Visibility.Visible;
        ProgramCopyStatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            _programCopyResultIsError ? "WarningInkBrush" : "SecondaryBrush");
        ApplyProgramCopyAvailability();
    }

    private void ApplyProgramCopyAvailability()
    {
        var copy = ObservedProgramCopy;
        var busy = _closed || _isRenderMode || _windowMinimized || _operationInFlight || _actionInFlight || _settingsLoading;
        ProgramCopyButton.Visibility = copy is null ? Visibility.Collapsed : Visibility.Visible;
        ProgramCopyButton.IsEnabled = copy is { CanSetAside: true } && !busy && !_restoreInspectionRequired && !_unsafeActionsInvalidated;
        ProgramCopyButton.ToolTip = copy is { CanSetAside: false } ? copy.Guidance
            : ProgramCopyButton.IsEnabled ? "전용 환경 안의 오래된 프로그램 사본만 보존 폴더로 옮깁니다."
            : "다른 작업이 끝난 뒤 사용할 수 있습니다.";
    }

    private bool ConfirmProgramCopyDefault(StaleProgramCopyObservation copy) => MessageBox.Show(this,
        $"전용 환경 안의 오래된 카카오톡 프로그램 사본을 보존 폴더로 옮길까요?\n\n" +
        $"사본 위치: {SafeRecoveryText(copy.Directory)}\n" +
        $"사본 버전 {copy.CopyVersion} · 공식 설치 버전 {copy.HostVersion} · 파일 {copy.FileCount}개\n\n" +
        "옮기는 것은 이 프로그램 파일 사본뿐이며 삭제하지 않습니다. 보조 계정·대화 데이터, 격리 설정, 공식 카카오톡 설치, 다른 Sandboxie 환경은 바꾸지 않습니다. " +
        "전용 환경이 비어 있는지와 격리 설정을 다시 확인한 뒤 진행합니다.",
        "오래된 프로그램 사본 치우기", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;

    private sealed class ProgramCopyProgress(MainWindow window) : IProgress<string>
    {
        public void Report(string value) => window.Dispatcher.InvokeAsync(() =>
        {
            if (window._closed || !window._operationInFlight) return;
            window._programCopyResult = value;
            window.UpdateProgramCopyCard();
        });
    }

    private async void ProgramCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _isRenderMode || _windowMinimized || _operationInFlight || _actionInFlight || _settingsLoading ||
            _restoreInspectionRequired || _unsafeActionsInvalidated || ObservedProgramCopy is not { CanSetAside: true } approved)
            return;

        // Reserve before any await or dialog: no launch, stop or recovery can start meanwhile.
        _operationInFlight = true;
        ApplyActionAvailability();
        try
        {
            await _inspectionTask;
            if (_closed) return;
            if (!(ConfirmProgramCopy ?? ConfirmProgramCopyDefault)(approved))
            {
                _programCopyResult = "프로그램 사본을 옮기지 않았습니다.";
                _programCopyResultIsError = false;
                return;
            }
            _programCopyResult = "전용 환경과 격리 설정을 다시 확인하고 있습니다…";
            _programCopyResultIsError = false;
            UpdateProgramCopyCard();
            // Core reports from a worker thread; always return to this window's dispatcher.
            var progress = new ProgramCopyProgress(this);
            var snapshot = await _controller.SetAsideStaleProgramCopyAsync(approved, progress);
            if (_closed) return;
            _programCopyResult = string.IsNullOrWhiteSpace(snapshot.RecoveryResultDetail)
                ? "프로그램 사본을 옮겼습니다." : ReadableFailure(snapshot.RecoveryResultDetail);
            _programCopyResultIsError = snapshot.StaleProgramCopy is not null;
            if (!_programCopyResultIsError) _updateAttemptHint = true;
            ApplySnapshot(snapshot);
        }
        catch (Exception exception)
        {
            _programCopyResult = ReadableException(exception);
            _programCopyResultIsError = true;
        }
        finally
        {
            _operationInFlight = false;
            if (!_closed)
            {
                UpdateProgramCopyCard();
                ApplyActionAvailability();
                await InspectAndApplyAsync();
            }
        }
    }
}
