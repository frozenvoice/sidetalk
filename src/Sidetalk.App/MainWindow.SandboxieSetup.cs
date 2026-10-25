using System.Windows;
using Sidetalk.Core;

namespace Sidetalk.App;

// Sandboxie Plus installation guidance on the main tab. The stage below lives only in this
// window; what is installed always comes from the latest real inspection, so restarting
// SIDETALK shows the actual state. Download, verification and the refusal to install over an
// existing installation are enforced in Core; this file only presents them.
public partial class MainWindow
{
    private enum SandboxieSetupStage { Idle, Confirming, Downloading, Verifying, Installing, Checking, Succeeded, Failed }

    private static readonly string[] SandboxieSteps =
        ["설치 안내", "다운로드", "검증", "설치 진행", "설치 확인", "전용 환경 준비", "보조 카카오톡 실행"];

    private readonly SandboxieInstaller _sandboxieInstaller;
    private readonly Func<SandboxieInstallerRelease, bool> _confirmSandboxieInstall;
    private SandboxieSetupStage _sandboxieStage;
    private CancellationTokenSource? _sandboxieDownloadCancellation;
    private CancellationTokenSource? _sandboxieWaitCancellation;
    private string? _sandboxieStatus;
    private string? _sandboxieResultTitle;

    private bool SandboxieSetupBusy => _sandboxieStage is SandboxieSetupStage.Confirming or SandboxieSetupStage.Downloading
        or SandboxieSetupStage.Verifying or SandboxieSetupStage.Installing or SandboxieSetupStage.Checking;

    private void InitializeSandboxieSetup()
    {
        var release = _sandboxieInstaller.Release;
        SandboxieReleasePageButton.Content = $"공식 {release.Version} 릴리스 페이지";
        ManualSandboxieReleaseButton.Content = $"Sandboxie Plus {release.Version} 공식 릴리스";
        SetupStepsText.Text =
            $"1. 공식 카카오톡을 설치합니다. Sandboxie Plus는 ‘보조 카카오톡’ 탭의 설치 안내에서 지원 버전 {release.Version}을 설치하거나, 아래 공식 릴리스 페이지에서 같은 버전을 직접 받을 수 있습니다.\n" +
            "2. 위의 변경 내용을 확인하고 ‘복구하고 실행 준비’를 누릅니다.\n" +
            "3. 실행 준비가 되면 ‘보조 카카오톡 실행’을 누르고 공식 창에서 직접 로그인합니다.";
        UpdateSandboxieSetupCard();
    }

    private DependencyCondition ObservedSandboxieCondition => _lastObservedSnapshot?.Sandboxie.Condition ?? DependencyCondition.Unknown;

    private static string SandboxieStep(int index) => index + 1 < SandboxieSteps.Length
        ? $"단계 {index + 1}/{SandboxieSteps.Length} · {SandboxieSteps[index]} — 다음: {SandboxieSteps[index + 1]}"
        : $"단계 {index + 1}/{SandboxieSteps.Length} · {SandboxieSteps[index]}";

    private void UpdateSandboxieSetupCard()
    {
        if (_closed) return;
        var release = _sandboxieInstaller.Release;
        var dependency = _lastObservedSnapshot?.Sandboxie;
        var condition = ObservedSandboxieCondition;
        // A stage result only describes the attempt; once the real state moves on, the
        // card follows the inspection again.
        if (_sandboxieStage == SandboxieSetupStage.Failed && condition is not (DependencyCondition.NotInstalled or DependencyCondition.ServiceUnavailable))
            _sandboxieStage = SandboxieSetupStage.Idle;
        if (_sandboxieStage == SandboxieSetupStage.Succeeded && condition != DependencyCondition.Verified)
            _sandboxieStage = SandboxieSetupStage.Idle;

        var problem = condition is DependencyCondition.NotInstalled or DependencyCondition.PathUnresolved or DependencyCondition.SignatureInvalid
            or DependencyCondition.UnsupportedVersion or DependencyCondition.ServiceUnavailable;
        var show = _sandboxieStage != SandboxieSetupStage.Idle || problem;
        SandboxieSetupCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ApplySandboxieSetupAvailability();
        if (!show) return;

        var supported = $"지원 버전: Sandboxie Plus {release.Version} (공식 x64)";
        string title, body, step;
        switch (_sandboxieStage)
        {
            case SandboxieSetupStage.Confirming:
                title = "Sandboxie Plus 설치를 준비하고 있어요";
                body = "현재 설치 상태를 다시 확인한 뒤 설치할 내용을 안내합니다.";
                step = SandboxieStep(0);
                break;
            case SandboxieSetupStage.Downloading:
                title = "Sandboxie Plus 설치 파일을 받고 있어요";
                body = $"공식 GitHub 릴리스에서 {release.FileName}을 받습니다. 검증이 끝나기 전에는 실행하지 않습니다.";
                step = SandboxieStep(1);
                break;
            case SandboxieSetupStage.Verifying:
                title = "설치 파일을 검증하고 있어요";
                body = $"파일 크기·SHA-256·공식 서명({release.Publisher})·제품 버전을 확인합니다.";
                step = SandboxieStep(2);
                break;
            case SandboxieSetupStage.Installing:
                title = "공식 설치 마법사를 진행하세요";
                body = "Windows 관리자 승인(UAC) 창과 Sandboxie Plus 설치 마법사는 직접 확인하고 진행하세요. SIDETALK는 관리자 권한으로 실행되지 않으며 설치 프로그램이 끝날 때까지 기다립니다.";
                step = SandboxieStep(3);
                break;
            case SandboxieSetupStage.Checking:
                title = "설치 결과를 확인하고 있어요";
                body = "설치 위치·공식 서명·버전·서비스 상태를 다시 검사합니다. 설치 프로그램이 끝났다는 사실만으로 성공으로 보지 않습니다.";
                step = SandboxieStep(4);
                break;
            case SandboxieSetupStage.Succeeded:
                title = $"Sandboxie Plus {release.Version} 설치를 확인했어요";
                body = "설치 위치·공식 서명·버전·서비스 실행을 확인했습니다. 이제 ‘초기 설정 · 진단’에서 전용 환경을 준비하세요.";
                step = SandboxieStep(5);
                break;
            case SandboxieSetupStage.Failed:
                title = _sandboxieResultTitle ?? "설치를 마치지 못했어요";
                body = condition == DependencyCondition.ServiceUnavailable
                    ? "Sandboxie는 설치됐지만 서비스가 아직 실행 중이 아닙니다. SIDETALK는 서비스를 시작하거나 재시작하지 않습니다. Windows를 다시 시작한 뒤 SIDETALK를 열면 다시 확인합니다."
                    : "Sandboxie Plus가 설치되지 않았습니다. 기존 SIDETALK 데이터는 바뀌지 않았습니다. 다시 시도하거나 공식 릴리스 페이지에서 같은 버전을 직접 받을 수 있습니다.";
                step = SandboxieStep(condition == DependencyCondition.ServiceUnavailable ? 4 : 0);
                break;
            default:
                (title, body) = condition switch
                {
                    DependencyCondition.NotInstalled => ("Sandboxie Plus 설치가 필요해요",
                        "보조 카카오톡을 실행하려면 Sandboxie Plus가 필요합니다. SIDETALK가 검증한 공식 설치 파일을 받아 공식 설치 마법사를 엽니다."),
                    DependencyCondition.PathUnresolved => ("Sandboxie Plus 설치 위치를 확인하지 못했어요",
                        "Sandboxie 설치 흔적은 있지만 SandMan.exe를 찾지 못했습니다. 설치가 끝나지 않았거나 Windows 재시작이 필요할 수 있습니다. SIDETALK는 기존 설치를 덮어쓰지 않습니다. Windows 설정의 설치된 앱에서 Sandboxie 상태를 확인하거나 Windows를 다시 시작한 뒤 다시 확인하세요."),
                    DependencyCondition.SignatureInvalid => ("Sandboxie Plus 서명을 확인하지 못했어요",
                        "설치된 Sandboxie 파일의 공식 서명을 검증하지 못해 실행을 막았습니다. 파일이 손상됐거나 공식 배포본이 아닐 수 있습니다. SIDETALK는 기존 설치를 바꾸거나 다시 설치하지 않습니다. 공식 릴리스에서 받은 설치 프로그램으로 직접 복구하세요."),
                    DependencyCondition.UnsupportedVersion => ("지원하지 않는 Sandboxie 버전이에요",
                        $"설치된 버전 {dependency?.Version}은(는) SIDETALK가 검증한 {release.Version}과 다릅니다. 새 버전은 API·드라이버·기본 템플릿·격리 정책을 따로 검증해야 하므로 실행을 막습니다. SIDETALK는 자동으로 업그레이드하거나 낮추지 않습니다. 버전을 바꾸려면 기존 Sandboxie 설정과 box를 보존하는 방법을 공식 안내에서 확인한 뒤 직접 진행하세요."),
                    DependencyCondition.ServiceUnavailable => ("Sandboxie 서비스를 확인해주세요",
                        "Sandboxie Plus는 설치되어 있지만 서비스(SbieSvc)가 실행 중이 아닙니다. SIDETALK는 서비스·드라이버를 시작하거나 재시작하지 않습니다. 설치 직후라면 Windows를 다시 시작한 뒤 다시 확인하세요."),
                    _ => ("Sandboxie Plus 상태 확인", "Sandboxie Plus 상태를 확인하고 있습니다.")
                };
                step = condition switch
                {
                    DependencyCondition.NotInstalled => SandboxieStep(0),
                    DependencyCondition.ServiceUnavailable => SandboxieStep(4),
                    _ => "다음: 위 안내대로 확인한 뒤 ‘상태 새로 확인’을 누르세요."
                };
                break;
        }
        if (condition == DependencyCondition.UnsupportedVersion && !string.IsNullOrWhiteSpace(dependency?.Version))
            supported += $" · 설치된 버전: {dependency!.Version}";
        SetText(SandboxieSetupTitleText, title);
        SandboxieSetupBodyText.Text = body;
        SandboxieSupportedVersionText.Text = supported;
        SandboxieSetupStepText.Text = step;
        SandboxieSetupStatusText.Text = _sandboxieStatus ?? string.Empty;
        SandboxieSetupStatusText.Visibility = string.IsNullOrWhiteSpace(_sandboxieStatus) ? Visibility.Collapsed : Visibility.Visible;
        var downloading = _sandboxieStage is SandboxieSetupStage.Downloading or SandboxieSetupStage.Verifying;
        SandboxieDownloadProgress.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        SandboxieDownloadProgress.IsIndeterminate = _sandboxieStage == SandboxieSetupStage.Verifying;
    }

    private void ApplySandboxieSetupAvailability()
    {
        var windowBusy = _closed || _isRenderMode || _windowMinimized || _operationInFlight || _actionInFlight || _settingsLoading;
        var offer = ObservedSandboxieCondition == DependencyCondition.NotInstalled &&
            _sandboxieStage is SandboxieSetupStage.Idle or SandboxieSetupStage.Failed;
        SandboxieInstallButton.Visibility = offer ? Visibility.Visible : Visibility.Collapsed;
        SandboxieInstallButton.Content = _sandboxieStage == SandboxieSetupStage.Failed ? "다시 시도" : "Sandboxie Plus 설치하기";
        SandboxieInstallButton.IsEnabled = offer && !windowBusy;
        SandboxieInstallButton.ToolTip = SandboxieInstallButton.IsEnabled
            ? $"공식 Sandboxie Plus {_sandboxieInstaller.Release.Version} 설치 파일을 받아 검증한 뒤 공식 설치 마법사를 엽니다."
            : "다른 작업이 끝난 뒤 사용할 수 있습니다.";
        SandboxieCancelDownloadButton.Visibility = _sandboxieStage is SandboxieSetupStage.Downloading or SandboxieSetupStage.Verifying
            ? Visibility.Visible : Visibility.Collapsed;
        SandboxieCancelDownloadButton.IsEnabled = _sandboxieDownloadCancellation is { IsCancellationRequested: false };
        SandboxieContinueSetupButton.Visibility = _sandboxieStage == SandboxieSetupStage.Succeeded ? Visibility.Visible : Visibility.Collapsed;
        SandboxieReleasePageButton.Visibility = _sandboxieStage is SandboxieSetupStage.Succeeded or SandboxieSetupStage.Installing or SandboxieSetupStage.Checking
            ? Visibility.Collapsed : Visibility.Visible;
        SandboxieReleasePageButton.IsEnabled = !_closed && !_isRenderMode;
    }

    private void SetSandboxieStage(SandboxieSetupStage stage, string? status, string? resultTitle = null)
    {
        _sandboxieStage = stage;
        _sandboxieStatus = status;
        _sandboxieResultTitle = resultTitle;
        UpdateSandboxieSetupCard();
    }

    private async void SandboxieInstallButton_Click(object sender, RoutedEventArgs e) => await InstallSandboxieAsync();

    /// <summary>The whole flow behind “Sandboxie Plus 설치하기”; UI checks await the same path.</summary>
    internal async Task InstallSandboxieAsync()
    {
        if (_closed || _isRenderMode || _windowMinimized || SandboxieSetupBusy || _operationInFlight || _actionInFlight || _settingsLoading) return;
        SetSandboxieStage(SandboxieSetupStage.Confirming, null);
        SandboxieInstallerRun? run = null;
        try
        {
            // A fresh read, not the last displayed one, decides whether anything is offered.
            await _inspectionTask;
            var fresh = await _controller.InspectAsync();
            if (_closed) return;
            ApplySnapshot(fresh);
            if (fresh.InspectionFailure is not null || !SandboxieInstaller.CanOffer(fresh.Sandboxie))
            {
                SetSandboxieStage(SandboxieSetupStage.Idle, fresh.InspectionFailure is not null
                    ? "현재 Sandboxie 설치 상태를 확인하지 못해 설치를 시작하지 않았습니다. ‘상태 새로 확인’ 후 다시 시도하세요."
                    : "새로 확인한 결과 Sandboxie 설치 흔적이 있어 설치를 시작하지 않았습니다. 기존 설치와 설정은 그대로 둡니다.");
                return;
            }
            if (!_confirmSandboxieInstall(_sandboxieInstaller.Release))
            {
                SetSandboxieStage(SandboxieSetupStage.Idle, "설치를 시작하지 않았습니다. 필요할 때 다시 ‘Sandboxie Plus 설치하기’를 누르세요.");
                return;
            }

            _sandboxieDownloadCancellation = new CancellationTokenSource();
            SandboxieDownloadProgress.Value = 0;
            SetSandboxieStage(SandboxieSetupStage.Downloading, "공식 배포 서버(GitHub)에 연결하고 있습니다…");
            var verified = await _sandboxieInstaller.DownloadAsync(
                new DispatcherProgress(this), _sandboxieDownloadCancellation.Token);
            DisposeSandboxieDownloadCancellation();
            if (_closed)
            {
                verified.Dispose();
                return;
            }

            _sandboxieWaitCancellation = new CancellationTokenSource();
            SetSandboxieStage(SandboxieSetupStage.Installing, $"검증 완료: SHA-256과 공식 서명({_sandboxieInstaller.Release.Publisher})을 확인했습니다.");
            run = await _sandboxieInstaller.RunAsync(verified, _sandboxieWaitCancellation.Token);
            if (_closed) return;

            SetSandboxieStage(SandboxieSetupStage.Checking, run.Detail);
            await _inspectionTask;
            await InspectAndApplyAsync();
            if (_closed) return;
            CompleteSandboxieInstall(run);
        }
        catch (OperationCanceledException) when (!_closed)
        {
            SetSandboxieStage(SandboxieSetupStage.Idle, "다운로드를 취소했습니다. 임시 파일을 삭제했습니다. 필요할 때 다시 시도하세요.");
        }
        catch (SandboxieInstallerException error) when (!_closed)
        {
            InspectionDiagnostics.Record("sandboxie-install", error, persist: !_isRenderMode);
            if (error.Kind == SandboxieInstallerFailure.Refused)
            {
                SetSandboxieStage(SandboxieSetupStage.Idle, error.Message);
                await InspectAndApplyAsync();
                return;
            }
            var retry = error.Kind is SandboxieInstallerFailure.Integrity or SandboxieInstallerFailure.Signature
                ? " 같은 결과가 반복되면 다운로드를 멈추고 공식 릴리스 페이지에서 파일과 해시를 직접 확인하세요."
                : " ‘다시 시도’를 누르거나 공식 릴리스 페이지에서 같은 버전을 직접 받을 수 있습니다.";
            SetSandboxieStage(SandboxieSetupStage.Failed, error.Message + retry, error.Kind switch
            {
                SandboxieInstallerFailure.Integrity or SandboxieInstallerFailure.Signature => "설치 파일 검증에 실패했어요",
                SandboxieInstallerFailure.Storage => "설치 파일을 저장하지 못했어요",
                _ => "설치 파일을 받지 못했어요"
            });
        }
        catch (Exception error) when (!_closed)
        {
            InspectionDiagnostics.Record("sandboxie-install", error, persist: !_isRenderMode);
            SetSandboxieStage(SandboxieSetupStage.Failed,
                $"설치를 진행하지 못했습니다: {ReadableException(error)} ‘상태 새로 확인’으로 실제 상태를 확인한 뒤 다시 시도하세요.",
                run is null ? "설치를 시작하지 못했어요" : "설치 결과를 확인하지 못했어요");
        }
        catch (Exception) when (_closed) { }
        finally
        {
            DisposeSandboxieDownloadCancellation();
            _sandboxieWaitCancellation?.Dispose();
            _sandboxieWaitCancellation = null;
            if (!_closed) ApplyActionAvailability();
        }
    }

    private void CompleteSandboxieInstall(SandboxieInstallerRun run)
    {
        if (_lastSnapshot is not { } snapshot)
        {
            SetSandboxieStage(SandboxieSetupStage.Failed,
                run.Detail + " 하지만 설치 결과를 확인하지 못했습니다. ‘상태 새로 확인’을 누르세요.", "설치 결과를 확인하지 못했어요");
            return;
        }
        var condition = snapshot.Sandboxie.Condition;
        switch (condition)
        {
            case DependencyCondition.Verified:
                SetSandboxieStage(SandboxieSetupStage.Succeeded, run.Outcome == SandboxieInstallerOutcome.RestartRequired
                    ? "공식 설치 프로그램이 Windows 재시작을 권했습니다. 재시작 후 SIDETALK를 열면 상태를 다시 확인합니다."
                    : null);
                return;
            case DependencyCondition.ServiceUnavailable:
                SetSandboxieStage(SandboxieSetupStage.Failed, run.Detail, "Windows를 다시 시작해야 할 수 있어요");
                return;
            case DependencyCondition.NotInstalled:
                var title = run.Outcome switch
                {
                    SandboxieInstallerOutcome.Cancelled or SandboxieInstallerOutcome.ElevationDeclined => "설치가 취소됐어요",
                    SandboxieInstallerOutcome.RestartRequired => "Windows를 다시 시작해야 할 수 있어요",
                    SandboxieInstallerOutcome.Exited => "설치를 확인하지 못했어요",
                    _ => "설치를 마치지 못했어요"
                };
                var detail = run.Outcome == SandboxieInstallerOutcome.Exited
                    ? "공식 설치 프로그램은 종료됐지만 Sandboxie 설치를 찾지 못했습니다."
                    : run.Detail;
                SetSandboxieStage(SandboxieSetupStage.Failed, detail, title);
                return;
            default:
                // Installed but not usable: the card explains the inspected condition itself.
                SetSandboxieStage(SandboxieSetupStage.Idle, run.Detail + " 설치 후 확인 결과 아래 상태 때문에 아직 실행할 수 없습니다.");
                return;
        }
    }

    private void ReportSandboxieDownload(SandboxieDownloadProgress progress)
    {
        if (_closed || _sandboxieStage is not (SandboxieSetupStage.Downloading or SandboxieSetupStage.Verifying)) return;
        if (progress.Verifying)
        {
            if (_sandboxieStage != SandboxieSetupStage.Verifying)
                SetSandboxieStage(SandboxieSetupStage.Verifying, "다운로드 완료 · 파일을 검증하고 있습니다…");
            return;
        }
        var percent = progress.Total > 0 ? Math.Clamp(progress.Received * 100.0 / progress.Total, 0, 100) : 0;
        SandboxieDownloadProgress.Value = percent;
        _sandboxieStatus = $"다운로드 중 {progress.Received / 1048576.0:0.0} / {progress.Total / 1048576.0:0.0} MB ({percent:0}%)";
        SandboxieSetupStatusText.Text = _sandboxieStatus;
        SandboxieSetupStatusText.Visibility = Visibility.Visible;
    }

    private void SandboxieCancelDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        _sandboxieDownloadCancellation?.Cancel();
        ApplySandboxieSetupAvailability();
    }

    private void SandboxieContinueSetupButton_Click(object sender, RoutedEventArgs e)
    {
        SetSandboxieStage(SandboxieSetupStage.Idle, null);
        MainTabs.SelectedIndex = 1;
    }

    private void SandboxieReleasePageButton_Click(object sender, RoutedEventArgs e)
    {
        var release = _sandboxieInstaller.Release;
        if (!OpenOfficialPage(release.ReleasePage.ToString(), $"Sandboxie Plus {release.Version} 공식 릴리스 페이지") && !_closed)
        {
            _sandboxieStatus = $"공식 릴리스 페이지를 열지 못했습니다. 브라우저에서 {release.ReleasePage}을(를) 직접 여세요.";
            UpdateSandboxieSetupCard();
        }
    }

    private bool ConfirmSandboxieInstall(SandboxieInstallerRelease release)
    {
        var message =
            "다음 공식 설치 프로그램을 내려받아 실행합니다.\n\n" +
            $"• 소프트웨어: Sandboxie Plus {release.Version} (Windows x64)\n" +
            $"• 출처: 공식 GitHub 릴리스\n   {release.DownloadUri}\n" +
            $"• 크기 {release.Size / 1048576.0:0.0} MB. 파일 크기·SHA-256·공식 서명({release.Publisher})을 확인한 뒤에만 실행합니다.\n\n" +
            "• 설치에는 관리자 권한이 필요할 수 있습니다. Windows 관리자 승인(UAC) 창과 공식 설치 마법사는 직접 확인하고 진행하세요. SIDETALK는 관리자 권한으로 다시 실행되지 않습니다.\n" +
            "• 설치 후 Windows 재시작이 필요할 수 있습니다.\n" +
            "• SIDETALK 프로필·전용 저장소·명령/복구 기록과 카카오톡 계정 정보는 변경하지 않습니다.\n\n" +
            "계속할까요?";
        return MessageBox.Show(this, message, $"Sandboxie Plus {release.Version} 설치", MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private void CancelSandboxieSetupForClose()
    {
        _sandboxieDownloadCancellation?.Cancel();
        // Stops waiting only; the official installer keeps running for the person to finish.
        _sandboxieWaitCancellation?.Cancel();
    }

    private void DisposeSandboxieDownloadCancellation()
    {
        _sandboxieDownloadCancellation?.Dispose();
        _sandboxieDownloadCancellation = null;
    }

    // Reports always reach this window's dispatcher, whichever thread the download continues on.
    private sealed class DispatcherProgress(MainWindow window) : IProgress<SandboxieDownloadProgress>
    {
        public void Report(SandboxieDownloadProgress value) =>
            window.Dispatcher.BeginInvoke(new Action(() => window.ReportSandboxieDownload(value)));
    }
}
