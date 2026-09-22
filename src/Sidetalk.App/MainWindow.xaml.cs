using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Sidetalk.Core;

namespace Sidetalk.App;

public partial class MainWindow : Window
{
    private const string SandboxieDownloadUrl = "https://sandboxie-plus.com/downloads/";
    private const string KakaoOfficialUrl = "https://www.kakaocorp.com/page/service/service/KakaoTalk";
    private readonly ISidetalkController _controller;
    private readonly DispatcherTimer _inspectionTimer;
    private readonly ObservableCollection<string> _checks = new();
    private readonly ObservableCollection<string> _recoveryChanges = new();
    private readonly ObservableCollection<string> _recoveryAffected = new();
    private readonly ObservableCollection<string> _recoveryPreserved = new();
    private AppSnapshot? _lastSnapshot;
    private AppSnapshot? _lastObservedSnapshot;
    private SetupRecoveryPlan? _recoveryPlan;
    private bool _inspectionInFlight;
    private bool _actionInFlight;
    private bool _operationInFlight;
    private bool _unsafeActionsInvalidated;
    private bool _automaticRecoveryAttempted;
    private string? _automaticRecoveryFailureScopeFingerprint;
    private bool _suppressRecoveryChoiceChange;
    private bool _automaticRecoveryChoiceDirty;
    private bool _initialRecoveryShown;
    private readonly bool _isRenderMode;

    public MainWindow(ISidetalkController controller, bool isRenderMode = false)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _isRenderMode = isRenderMode;
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.Apply(this);
        ChecksList.ItemsSource = _checks;
        RecoveryChangesList.ItemsSource = _recoveryChanges;
        RecoveryAffectedList.ItemsSource = _recoveryAffected;
        RecoveryPreservedList.ItemsSource = _recoveryPreserved;
        ApplyActionAvailability();

        _inspectionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _inspectionTimer.Tick += InspectionTimer_Tick;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_isRenderMode)
        {
            _inspectionTimer.Start();
            await InspectAndApplyAsync(allowAutomaticRecovery: true);
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_operationInFlight)
        {
            e.Cancel = true;
            MessageBox.Show(
                this,
                "현재 설정 복구나 저장 작업이 진행 중입니다. 진행 단계가 끝난 뒤 창을 닫아 주세요.",
                "SIDETALK 작업 진행 중",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }
    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _inspectionTimer.Stop();
    }

    private async void InspectionTimer_Tick(object? sender, EventArgs e)
    {
        await InspectAndApplyAsync(allowAutomaticRecovery: true);
    }

    private async void InspectButton_Click(object sender, RoutedEventArgs e)
    {
        await InspectAndApplyAsync(allowAutomaticRecovery: true);
    }

    /// <summary>Runs one normal, asynchronous refresh. Test and render callers can await the same path.</summary>
    public Task RefreshNowAsync() => InspectAndApplyAsync(allowAutomaticRecovery: false);

    private async Task InspectAndApplyAsync(bool allowAutomaticRecovery = false)
    {
        if (_isRenderMode || _inspectionInFlight || _operationInFlight)
        {
            return;
        }

        _inspectionInFlight = true;
        ApplyActionAvailability();
        try
        {
            AppSnapshot snapshot;
            try
            {
                snapshot = await _controller.InspectAsync();
            }
            catch (Exception exception)
            {
                ApplyMainInspectionError(exception);
                return;
            }

            if (snapshot.InspectionFailure is { } inspectionFailure)
            {
                ApplyMainInspectionFailure(inspectionFailure);
                return;
            }

            ApplySnapshot(snapshot);

            SetupRecoveryPlan recoveryPlan;
            try
            {
                recoveryPlan = await _controller.InspectSetupRecoveryAsync();
            }
            catch (Exception exception)
            {
                ApplyRecoveryInspectionError(exception);
                return;
            }

            ApplyRecoveryPlan(recoveryPlan);
            if (allowAutomaticRecovery &&
                !_isRenderMode &&
                !_automaticRecoveryAttempted &&
                _recoveryPlan is { State: SetupRecoveryState.AutomaticallyApplicable, AllowAutomatic: true, RequiresApproval: false })
            {
                await RunAutomaticRecoveryAsync();
            }
        }
        finally
        {
            _inspectionInFlight = false;
            ApplyActionAvailability();
        }
    }

    private async Task RunAutomaticRecoveryAsync()
    {
        if (_isRenderMode || _automaticRecoveryAttempted || _operationInFlight)
        {
            return;
        }

        _automaticRecoveryAttempted = true;
        BeginRecoveryOperation("동일한 검증 범위의 복구를 준비하고 있습니다.");
        _operationInFlight = true;
        SetRecoveryProgress("복구 준비 중…");
        ApplyActionAvailability();

        try
        {
            var progress = new Progress<string>(SetRecoveryProgress);
            ApplySnapshot(await _controller.PrepareAutomaticallyAsync(progress));
            ApplyRecoveryPlan(await _controller.InspectSetupRecoveryAsync());
            SetRecoveryResult("자동 복구를 마쳤습니다. 카카오톡은 자동으로 실행하지 않습니다.");
            _automaticRecoveryAttempted = false;
            _automaticRecoveryFailureScopeFingerprint = null;
        }
        catch (Exception exception)
        {
            ApplyRecoveryOperationError(exception, "자동 복구");
            SetRecoveryResult($"자동 복구를 완료하지 못했습니다: {ReadableException(exception)}");
            _automaticRecoveryFailureScopeFingerprint = _recoveryPlan?.ScopeFingerprint;
        }
        finally
        {
            _operationInFlight = false;
            RecoveryProgressText.Visibility = Visibility.Collapsed;
            ApplyActionAvailability();
        }
    }

    public void ApplySnapshot(AppSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _lastSnapshot = snapshot;
        _lastObservedSnapshot = snapshot;

        var running = snapshot.State is Readiness.Running or Readiness.RunningUnverified;
        var detail = string.IsNullOrWhiteSpace(snapshot.Detail) ? ReadinessDetail(snapshot.State) : snapshot.Detail;
        StatusTitleText.Text = running ? "보조 환경 실행 중"
            : string.IsNullOrWhiteSpace(snapshot.Title) ? ReadinessTitle(snapshot.State) : snapshot.Title;
        StatusDetailText.Text = running ? SummarizeRunningDetail(detail) : detail;
        PolicyDetailText.Text = detail;
        PolicySummaryText.Text = running
            ? (snapshot.CurrentConfigurationVerified ? "현재 격리 설정 확인됨 · 시작 당시 적용 규칙은 확인 불가" : "현재 격리 설정과 시작 당시 적용 규칙을 확인할 수 없습니다.")
            : string.Empty;
        PolicySummaryText.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        PolicySummaryText.Foreground = (Brush)FindResource("WarningInkBrush");
        StatusBadgeText.Text = ReadinessBadge(snapshot.State);
        var statusBrush = snapshot.State switch
        {
            Readiness.Ready or Readiness.Running or Readiness.RunningUnverified => "AccentBrush",
            Readiness.Blocked or Readiness.Unknown => "ErrorInkBrush",
            _ => "WarningInkBrush"
        };
        StatusDot.Fill = (Brush)FindResource(statusBrush);
        StatusBadgeText.Foreground = (Brush)FindResource(statusBrush);
        RefreshSummaryText.Text = $"{snapshot.CheckedAt.ToLocalTime():HH:mm:ss} 확인 · 4초마다 자동 갱신";
        KakaoSummaryText.Text = DependencySummary(snapshot.Kakao);
        SandboxieSummaryText.Text = DependencySummary(snapshot.Sandboxie);

        BoxNameText.Text = DisplayValue(snapshot.BoxName);
        StoragePathText.Text = DisplayValue(snapshot.StoragePath);
        ProcessIdsText.Text = snapshot.BoxProcessIds.Count > 0
            ? $"{snapshot.BoxProcessIds.Count}개 · PID {string.Join(", ", snapshot.BoxProcessIds)}"
            : snapshot.State == Readiness.Ready ? "없음" : "프로세스 상태 미확인";
        CheckedAtText.Text = snapshot.CheckedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        ApplyDependency(KakaoAvailabilityText, KakaoInfoText, snapshot.Kakao);
        ApplyDependency(SandboxieAvailabilityText, SandboxieInfoText, snapshot.Sandboxie);

        _checks.Clear();
        if (snapshot.Checks.Count == 0)
        {
            _checks.Add("Sandboxie 연결 및 설정 적용 후 검사합니다.");
        }
        else
        {
            foreach (var check in snapshot.Checks)
            {
                _checks.Add(check);
            }
        }

        ApplyActionAvailability();
    }

    public void ApplyRecoveryPlan(SetupRecoveryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var samePlan = _recoveryPlan is not null &&
            SameApproval(_recoveryPlan, plan);
        if (!samePlan)
        {
            _automaticRecoveryChoiceDirty = false;
        }

        _recoveryPlan = plan;
        if (_automaticRecoveryFailureScopeFingerprint is not null &&
            !string.Equals(_automaticRecoveryFailureScopeFingerprint, plan.ScopeFingerprint, StringComparison.Ordinal))
        {
            _automaticRecoveryAttempted = false;
            _automaticRecoveryFailureScopeFingerprint = null;
        }
        if (plan.IsHealthy)
        {
            _automaticRecoveryAttempted = false;
            _automaticRecoveryFailureScopeFingerprint = null;
        }


        if (!_initialRecoveryShown && plan.CanApply && plan.RequiresApproval && !_isRenderMode)
        {
            MainTabs.SelectedIndex = 1;
            _initialRecoveryShown = true;
        }

        var showPanel = !plan.IsHealthy || plan.AllowAutomatic;
        RecoveryPanel.Visibility = showPanel ? Visibility.Visible : Visibility.Collapsed;
        RecoveryStateText.Text = RecoveryStateLabel(plan.State);
        RecoverySummaryText.Text = string.IsNullOrWhiteSpace(plan.Summary)
            ? "현재 설정을 확인했습니다."
            : plan.Summary;

        _recoveryChanges.Clear();
        foreach (var change in plan.Changes.Where(change => !change.IsNoOp && change.Kind != SetupRecoveryChangeKind.AddOwnedBoxSetting))
        {
            _recoveryChanges.Add(FormatRecoveryChange(change));
        }

        var ownedChanges = plan.Changes.Count(change => !change.IsNoOp && change.Kind == SetupRecoveryChangeKind.AddOwnedBoxSetting);
        if (ownedChanges > 0)
            _recoveryChanges.Add($"SIDETALK 전용 환경 생성: 표시된 전용 저장 경로에 격리 설정 {ownedChanges}개 적용 · 기존 데이터 보존");

        if (_recoveryChanges.Count == 0)
        {
            _recoveryChanges.Add(plan.IsHealthy ? "변경할 설정이 없습니다." : "확인된 변경 항목이 없습니다.");
        }

        _recoveryAffected.Clear();
        var evidenceTargets = plan.Evidence
            .Where(text => !plan.Changes.Any(change => text == change.Section + "." + change.Key))
            .Select(SafeRecoveryText)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var evidence in evidenceTargets)
        {
            _recoveryAffected.Add("영향 확인: " + evidence);
        }

        _recoveryAffected.Add(evidenceTargets.Any(text => text.StartsWith("관리 UI 종료 대상:", StringComparison.Ordinal))
            ? "열린 Sandboxie 관리 창: 위에 표시된 종료 대상은 승인 후 닫힙니다."
            : "열린 Sandboxie 관리 창: 확인된 종료 대상이 없습니다.");


        _recoveryPreserved.Clear();
        _recoveryPreserved.Add("서비스·드라이버·Start.exe·메인 카카오톡·다른 Sandboxie 상자는 종료하거나 변경하지 않습니다.");
        _recoveryPreserved.Add("보조 계정과 대화 데이터는 삭제하거나 복사하지 않습니다.");
        _recoveryPreserved.Add("profile.json·소유 식별자·전용 저장 공간과 미완료 명령 기록을 보존합니다.");
        _recoveryPreserved.Add("확인된 범위 밖의 설정과 데이터는 건드리지 않습니다.");

        var safeBlockers = plan.Blockers.Select(SafeRecoveryText).Where(text => !string.IsNullOrWhiteSpace(text)).ToArray();
        RecoveryBlockerText.Text = safeBlockers.Length == 0
            ? string.Empty
            : "진행할 수 없는 이유: " + string.Join(" · ", safeBlockers);
        RecoveryBlockerText.Visibility = safeBlockers.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (!samePlan || !_automaticRecoveryChoiceDirty)
        {
            _suppressRecoveryChoiceChange = true;
            try
            {
                AutomaticRecoveryCheckBox.IsChecked = plan.AllowAutomatic;
            }
            finally
            {
                _suppressRecoveryChoiceChange = false;
            }
        }
        RecoveryBackupText.Text = string.IsNullOrWhiteSpace(plan.BackupPath)
            ? string.Empty
            : "백업 위치: " + plan.BackupPath;
        if (!samePlan) { SetRecoveryResult(string.Empty); }
        _unsafeActionsInvalidated = false;
        ApplyActionAvailability();
    }

    private async void PrepareRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRenderMode || _unsafeActionsInvalidated || _operationInFlight || _recoveryPlan is not { CanApply: true } plan)
        {
            return;
        }

        BeginRecoveryOperation("복구를 준비하고 있습니다. 기존 실행 가능 상태는 해제했습니다.");
        _operationInFlight = true;
        SetRecoveryProgress("설정 백업 중…");
        ApplyActionAvailability();

        try
        {
            var progress = new Progress<string>(SetRecoveryProgress);
            ApplySnapshot(await _controller.PrepareAsync(
                plan,
                AutomaticRecoveryCheckBox.IsChecked == true,
                progress));
            ApplyRecoveryPlan(await _controller.InspectSetupRecoveryAsync());
            SetRecoveryResult("복구가 완료되었습니다. 카카오톡은 자동으로 실행하지 않습니다.");
        }
        catch (Exception exception)
        {
            ApplyRecoveryOperationError(exception, "복구");
            SetRecoveryResult($"복구를 완료하지 못했습니다: {ReadableException(exception)}");
        }
        finally
        {
            _operationInFlight = false;
            RecoveryProgressText.Visibility = Visibility.Collapsed;
            ApplyActionAvailability();
        }
    }

    private async void RevokeAutomaticButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRenderMode || _unsafeActionsInvalidated || _operationInFlight || _recoveryPlan is not { AllowAutomatic: true })
        {
            return;
        }

        BeginRecoveryOperation("자동 복구 설정을 해제하고 있습니다.");
        _operationInFlight = true;
        SetRecoveryProgress("자동 복구 동의 해제 중…");
        ApplyActionAvailability();

        try
        {
            await _controller.RevokeAutomaticRecoveryAsync();
            SetRecoveryResult("자동 복구 동의를 해제했습니다.");
        }
        catch (Exception exception)
        {
            ApplyRecoveryOperationError(exception, "자동 복구 동의 해제");
            SetRecoveryResult($"자동 복구 동의를 해제하지 못했습니다: {ReadableException(exception)}");
        }
        finally
        {
            _operationInFlight = false;
            RecoveryProgressText.Visibility = Visibility.Collapsed;
            ApplyActionAvailability();
        }

        await InspectAndApplyAsync(allowAutomaticRecovery: true);
    }

    private void OpenRecoveryHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRenderMode || _operationInFlight)
        {
            return;
        }

        try
        {
            var historyPath = _controller.RecoveryHistoryPath;
            if (string.IsNullOrWhiteSpace(historyPath) || historyPath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("복구 내역의 로컬 폴더를 확인할 수 없습니다.");
            }

            var fullPath = Path.GetFullPath(historyPath);
            var folder = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                throw new DirectoryNotFoundException("복구 내역의 로컬 폴더를 확인할 수 없습니다.");
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            SetRecoveryResult("복구 내역 폴더를 열었습니다.");
        }
        catch (Exception exception)
        {
            SetRecoveryResult($"복구 내역 폴더를 열지 못했습니다: {ReadableException(exception)}");
        }
    }

    private void AutomaticRecoveryCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_suppressRecoveryChoiceChange)
        {
            _automaticRecoveryChoiceDirty = true;
        }
    }
    private void SetRecoveryProgress(string message)
    {
        RecoveryProgressText.Text = message;
        RecoveryProgressText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetRecoveryResult(string message)
    {
        RecoveryResultText.Text = message;
        RecoveryResultText.ToolTip = message;
    }

    private void ClearStaleSnapshot(string detail)
    {
        _lastSnapshot = null;
        _unsafeActionsInvalidated = true;
        StatusTitleText.Text = "작업 진행 중";
        StatusBadgeText.Text = "진행 중";
        StatusDot.Fill = (Brush)FindResource("WarningInkBrush");
        StatusBadgeText.Foreground = (Brush)FindResource("WarningInkBrush");
        StatusDetailText.Text = detail;
        PolicyDetailText.Text = detail;
        PolicySummaryText.Visibility = Visibility.Collapsed;
    }

    private void BeginRecoveryOperation(string detail)
    {
        _unsafeActionsInvalidated = true;
        StatusTitleText.Text = "복구 진행 중";
        StatusBadgeText.Text = "진행 중";
        StatusDot.Fill = (Brush)FindResource("WarningInkBrush");
        StatusBadgeText.Foreground = (Brush)FindResource("WarningInkBrush");
        StatusDetailText.Text = detail;
    }

    private static string SafeRecoveryText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            text.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("소유 식별자", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ScopeFingerprint", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (text.Contains("완료되지 않은 복구 기록", StringComparison.Ordinal))
        {
            return "완료되지 않은 복구 기록이 있어 먼저 상태를 확인해야 합니다.";
        }

        return text;
    }
    private static bool SameApproval(SetupRecoveryPlan left, SetupRecoveryPlan right) =>
        left.ScopeFingerprint == right.ScopeFingerprint && left.State == right.State &&
        left.IsFirstSetup == right.IsFirstSetup && left.IsReconciliation == right.IsReconciliation &&
        left.InterruptedOperationId == right.InterruptedOperationId && left.RequiresApproval == right.RequiresApproval &&
        left.AllowAutomatic == right.AllowAutomatic && left.Changes.Count == right.Changes.Count &&
        left.Changes.Zip(right.Changes).All(pair => pair.First.Identity == pair.Second.Identity &&
            pair.First.Kind == pair.Second.Kind && pair.First.CurrentValues.SequenceEqual(pair.Second.CurrentValues) &&
            pair.First.DesiredValues.SequenceEqual(pair.Second.DesiredValues));

    private static string FormatRecoveryChange(SetupRecoveryChange change)
    {
        if (change.Kind == SetupRecoveryChangeKind.RemoveGlobalTemplate)
            return "GlobalSettings의 Template 제거: " + string.Join(", ", change.CurrentValues.Except(change.DesiredValues, StringComparer.OrdinalIgnoreCase));
        if (change.Kind == SetupRecoveryChangeKind.SetUserSetting && change.Key == "SbieCtrl_AutoRunSoftCompat")
            return $"현재 사용자({change.Section})의 SbieCtrl_AutoRunSoftCompat=n · 자동 호환성 팝업 재발 방지";
        var description = SafeRecoveryText(change.Description);
        if (string.IsNullOrWhiteSpace(description))
        {
            description = $"{change.Section} · {change.Key}";
        }

        return $"• {description} ({RecoveryChangeLabel(change.Kind)})";
    }

    private static string RecoveryChangeLabel(SetupRecoveryChangeKind kind) => kind switch
    {
        SetupRecoveryChangeKind.RemoveGlobalTemplate => "검토된 전역 항목 제거",
        SetupRecoveryChangeKind.SetUserSetting => "현재 사용자 설정 적용",
        SetupRecoveryChangeKind.RemoveUserSetting => "현재 사용자 설정 제거",
        SetupRecoveryChangeKind.AddOwnedBoxSetting => "전용 box 설정 추가",
        SetupRecoveryChangeKind.RepairOwnedBoxSetting => "전용 box 설정 복구",
        _ => "검토된 설정 변경"
    };

    private static string RecoveryStateLabel(SetupRecoveryState state) => state switch
    {
        SetupRecoveryState.Healthy => "정상",
        SetupRecoveryState.FirstSetup => "최초 준비",
        SetupRecoveryState.ApprovalRequired => "승인 필요",
        SetupRecoveryState.AutomaticallyApplicable => "자동 복구 가능",
        SetupRecoveryState.Blocked => "복구 차단",
        SetupRecoveryState.Interrupted => "중단 복구 검토",
        SetupRecoveryState.Applied => "복구 완료",
        SetupRecoveryState.Failed => "복구 실패",
        _ => "확인 중"
    };
    public void PrepareRender(bool setupTab, bool compactWindow)
    {
        if (setupTab)
        {
            MainTabs.SelectedIndex = 1;
        }

        if (compactWindow)
        {
            Width = 740;
            Height = 640;
        }
    }

    public void ApplyMainInspectionError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        InspectionDiagnostics.Record("main-inspection", exception, persist: !_isRenderMode);
        ApplyMainInspectionFailureView(ReadableException(exception));
    }

    public void ApplyMainInspectionFailure(InspectionFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        InspectionDiagnostics.Record(failure, persist: !_isRenderMode);
        ApplyMainInspectionFailureView(ReadableFailure(failure.Message));
    }

    private void ApplyMainInspectionFailureView(string detail)
    {
        var lastSnapshot = _lastObservedSnapshot;
        _lastSnapshot = null;
        _unsafeActionsInvalidated = true;
        StatusDot.Fill = (Brush)FindResource("WarningInkBrush");
        StatusBadgeText.Foreground = (Brush)FindResource("WarningInkBrush");
        StatusBadgeText.Text = "현재 확인 실패";

        if (lastSnapshot is { } snapshot)
        {
            var checkedAt = snapshot.CheckedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            StatusTitleText.Text = "마지막 확인 결과";
            StatusDetailText.Text = "현재 상태를 확인하지 못했습니다. 아래 정보는 마지막 확인 결과이며 현재 상태를 보장하지 않습니다. 다시 확인하세요.";
            PolicyDetailText.Text = $"마지막 확인 ({checkedAt}) · {detail}";
            PolicySummaryText.Text = "마지막 확인 당시의 격리 설정이며 현재 상태는 미확인입니다.";
            KakaoSummaryText.Text = "마지막 확인 · " + DependencySummary(snapshot.Kakao);
            SandboxieSummaryText.Text = "마지막 확인 · " + DependencySummary(snapshot.Sandboxie);
            ProcessIdsText.Text = snapshot.BoxProcessIds.Count > 0
                ? $"마지막 확인 {snapshot.BoxProcessIds.Count}개 · PID {string.Join(", ", snapshot.BoxProcessIds)}"
                : "마지막 확인 결과 · 현재 프로세스 상태 미확인";
            CheckedAtText.Text = "마지막 확인 " + checkedAt;
            RefreshSummaryText.Text = $"마지막 확인 {checkedAt} · 현재 자동 확인 실패 · 다시 확인하세요";
            if (_checks.Count == 0 || !_checks[0].StartsWith("현재 상태를 확인하지 못했습니다", StringComparison.Ordinal))
            {
                _checks.Insert(0, "현재 상태를 확인하지 못했습니다. 아래 정보는 마지막 확인 결과입니다.");
            }
        }
        else
        {
            StatusTitleText.Text = "현재 상태를 확인하지 못했습니다";
            StatusDetailText.Text = "현재 상태를 확인하지 못했습니다. 다시 확인하거나 진단 JSON을 저장하세요.";
            PolicyDetailText.Text = $"현재 상태 확인 실패 · {detail}";
            PolicySummaryText.Visibility = Visibility.Collapsed;
            RefreshSummaryText.Text = "마지막 확인 없음 · 다시 확인하세요";
            KakaoSummaryText.Text = "확인할 수 없음";
            SandboxieSummaryText.Text = "확인할 수 없음";
            BoxNameText.Text = "확인할 수 없음";
            StoragePathText.Text = "확인할 수 없음";
            ProcessIdsText.Text = "확인할 수 없음";
            CheckedAtText.Text = "마지막 확인 없음";
            KakaoAvailabilityText.Text = "확인할 수 없음";
            SandboxieAvailabilityText.Text = "확인할 수 없음";
        }

        var errorLine = "상태 확인 오류: " + detail;
        if (!_checks.Contains(errorLine, StringComparer.Ordinal))
        {
            _checks.Add(errorLine);
        }
        ApplyActionAvailability();
    }

    public void ApplyRecoveryInspectionError(Exception exception)
    {
        ApplyRecoveryFailure(exception, "recovery-inspection", "복구 상태 확인");
    }

    private void ApplyRecoveryOperationError(Exception exception, string stage)
    {
        ApplyRecoveryFailure(exception, "recovery-operation", stage);
    }

    private void ApplyRecoveryFailure(Exception exception, string diagnosticStage, string stage)
    {
        ArgumentNullException.ThrowIfNull(exception);
        InspectionDiagnostics.Record(diagnosticStage, exception, persist: !_isRenderMode);
        _unsafeActionsInvalidated = true;
        RecoveryPanel.Visibility = Visibility.Visible;
        RecoveryStateText.Text = "복구 확인 실패";
        RecoverySummaryText.Text = $"{stage}에 실패했습니다. 실행·종료·복구 작업을 차단했습니다. 다시 확인하세요.";
        SetRecoveryResult($"{stage} 실패: {ReadableException(exception)}");

        var observedRunning = _lastSnapshot is { BoxProcessIds.Count: > 0 };
        StatusTitleText.Text = observedRunning ? "보조 환경 실행 중" : "복구 상태 확인 필요";
        StatusBadgeText.Text = observedRunning ? "실행 관찰됨" : "복구 확인 실패";
        StatusDot.Fill = (Brush)FindResource("WarningInkBrush");
        StatusBadgeText.Foreground = (Brush)FindResource("WarningInkBrush");
        StatusDetailText.Text = observedRunning
            ? $"전용 환경 프로세스 {_lastSnapshot!.BoxProcessIds.Count}개를 확인했습니다. 복구 상태 확인은 실패해 새 조작을 차단했습니다. 상태 새로 확인으로 다시 시도하세요."
            : "복구 상태를 확인하지 못했습니다. 상태 새로 확인으로 다시 시도하세요.";
        RefreshSummaryText.Text = _lastSnapshot is { } snapshot
            ? $"최근 상태 {snapshot.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 복구 확인 실패 · 다시 확인하세요"
            : "최근 상태 없음 · 복구 확인 실패 · 다시 확인하세요";

        var errorLine = $"{stage} 오류: " + ReadableException(exception);
        if (!_checks.Contains(errorLine, StringComparer.Ordinal))
        {
            _checks.Add(errorLine);
        }

        ApplyActionAvailability();
    }
    public void ApplyError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        InspectionDiagnostics.Record("ui", exception, persist: !_isRenderMode);
        _lastSnapshot = null;
        _unsafeActionsInvalidated = true;
        StatusTitleText.Text = "상태 확인 실패";
        StatusBadgeText.Text = "확인 실패";
        StatusDetailText.Text = ReadableException(exception);
        PolicyDetailText.Text = StatusDetailText.Text;
        PolicySummaryText.Visibility = Visibility.Collapsed;
        StatusDot.Fill = (Brush)FindResource("ErrorInkBrush");
        StatusBadgeText.Foreground = (Brush)FindResource("ErrorInkBrush");
        RefreshSummaryText.Text = "자동 확인 실패 · 상태를 다시 확인하세요";
        KakaoSummaryText.Text = "확인 실패";
        SandboxieSummaryText.Text = "확인 실패";
        BoxNameText.Text = "확인할 수 없음";
        StoragePathText.Text = "확인할 수 없음";
        ProcessIdsText.Text = "확인할 수 없음";
        CheckedAtText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        KakaoAvailabilityText.Text = "확인 실패";
        KakaoInfoText.Text = "진단을 실행해 오류 내용을 저장할 수 있습니다.";
        SandboxieAvailabilityText.Text = "확인 실패";
        SandboxieInfoText.Text = "진단을 실행해 오류 내용을 저장할 수 있습니다.";
        _checks.Clear();
        _checks.Add(ReadableException(exception));
        ApplyActionAvailability();
    }

    public void RenderToPng(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("렌더링할 PNG 경로가 비어 있습니다.", nameof(path));
        }

        UpdateLayout();
        // Native caption and borders are not drawn by RenderTargetBitmap.
        var content = (FrameworkElement)Content;
        var width = Math.Max(1, (int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right));
        var height = Math.Max(1, (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);

        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(fullPath);
        encoder.Save(stream);
    }

    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRenderMode || _unsafeActionsInvalidated || _actionInFlight || _operationInFlight || _lastSnapshot is not { ActionsPermitted: true, CanLaunch: true })
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "전용 Sandboxie Plus 상자에서 공식 카카오톡을 실행합니다.\n\n처음에는 전용 INI 파일의 데이터 분리 확인 내용을 읽고 직접 수락한 뒤, 카카오톡 창에서 보조 계정으로 로그인하고 기기 인증을 진행하세요. 메인 계정의 인증·대화 데이터는 복사하지 않습니다.\n\n실행을 계속할까요?",
            "보조 카카오톡 실행 확인",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information,
            MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        _actionInFlight = true;
        _operationInFlight = true;
        ClearStaleSnapshot("실행 요청을 보내고 있습니다.");
        ApplyActionAvailability();
        try
        {
            await _controller.LaunchAsync();
            StatusDetailText.Text = "실행 요청을 보냈습니다. 프로세스 상태를 다시 확인하고 있습니다.";
        }
        catch (Exception exception)
        {
            ApplyError(exception);
        }
        finally
        {
            _operationInFlight = false;
            _actionInFlight = false;
            ApplyActionAvailability();
            await InspectAndApplyAsync(allowAutomaticRecovery: true);
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRenderMode || _unsafeActionsInvalidated || _actionInFlight || _operationInFlight || _lastSnapshot is not { ActionsPermitted: true, CanStop: true })
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "SIDETALK 전용 Sandboxie Plus 상자 안의 프로세스만 강제 종료합니다.\n\n카카오톡에서 먼저 직접 종료하세요. 강제 종료하면 저장되지 않은 작업이 사라질 수 있습니다. 보조 환경의 설정과 로컬 데이터는 삭제하지 않으며, 메인 카카오톡이나 다른 샌드박스는 건드리지 않습니다.\n\n강제 종료를 계속할까요?",
            "보조 카카오톡 종료 확인",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        _actionInFlight = true;
        _operationInFlight = true;
        ClearStaleSnapshot("전용 상자를 종료하고 있습니다.");
        ApplyActionAvailability();
        try
        {
            await _controller.StopAsync();
            StatusDetailText.Text = "전용 상자의 종료 요청을 보냈습니다. 저장 데이터는 삭제하지 않았습니다.";
        }
        catch (Exception exception)
        {
            ApplyError(exception);
        }
        finally
        {
            _operationInFlight = false;
            _actionInFlight = false;
            ApplyActionAvailability();
            await InspectAndApplyAsync(allowAutomaticRecovery: true);
        }
    }

    private async void ExportSetupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRenderMode || _operationInFlight || _inspectionInFlight)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "전용 INI를 저장할 새 폴더 선택",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            return;
        }

        SetSetupResult("전용 INI 파일을 저장하는 중…");
        _operationInFlight = true;
        ClearStaleSnapshot("전용 INI를 저장하고 있습니다.");
        ApplyActionAvailability();
        try
        {
            var path = await _controller.ExportSetupAsync(dialog.FolderName);
            SetSetupResult($"전용 INI 파일을 저장했습니다: {path}");
        }
        catch (Exception exception)
        {
            SetSetupResult($"전용 INI 파일을 저장하지 못했습니다: {ReadableException(exception)}");
        }
        finally
        {
            _operationInFlight = false;
            ApplyActionAvailability();
            await InspectAndApplyAsync();
        }
    }

    private async void ExportDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRenderMode || _operationInFlight || _inspectionInFlight)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "진단 JSON 저장",
            FileName = $"sidetalk-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            DefaultExt = ".json",
            AddExtension = true,
            Filter = "JSON 파일 (*.json)|*.json|모든 파일 (*.*)|*.*",
            OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        if (File.Exists(dialog.FileName))
        {
            SetSetupResult("기존 진단 파일은 덮어쓰지 않습니다. 새 파일 이름을 선택하세요.");
            return;
        }

        SetSetupResult("진단 JSON을 저장하는 중…");
        _operationInFlight = true;
        ClearStaleSnapshot("진단 JSON을 저장하고 있습니다.");
        ApplyActionAvailability();
        try
        {
            var path = await _controller.ExportDiagnosticsAsync(dialog.FileName);
            InspectionDiagnostics.TryAttachToExport(path);
            SetSetupResult($"진단 JSON을 저장했습니다: {path}");
        }
        catch (Exception exception)
        {
            InspectionDiagnostics.Record("diagnostic-export", exception, persist: !_isRenderMode);
            SetSetupResult($"진단 JSON을 저장하지 못했습니다: {ReadableException(exception)}");
        }
        finally
        {
            _operationInFlight = false;
            ApplyActionAvailability();
            await InspectAndApplyAsync();
        }
    }

    private void SandboxieDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        OpenOfficialPage(SandboxieDownloadUrl, "Sandboxie Plus 다운로드 페이지");
    }

    private void KakaoOfficialButton_Click(object sender, RoutedEventArgs e)
    {
        OpenOfficialPage(KakaoOfficialUrl, "카카오톡 공식 안내 페이지");
    }

    private void OpenOfficialPage(string url, string description)
    {
        if (_isRenderMode) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            SetSetupResult($"{description}을 열지 못했습니다: {ReadableException(exception)}");
        }
    }

    private void ApplyActionAvailability()
    {
        var busy = _isRenderMode || _inspectionInFlight || _operationInFlight || _actionInFlight;
        LaunchButton.IsEnabled = !busy && !_unsafeActionsInvalidated && _lastSnapshot is { ActionsPermitted: true, CanLaunch: true } && _recoveryPlan is { IsHealthy: true };
        StopButton.IsEnabled = !busy && !_unsafeActionsInvalidated && _lastSnapshot is { ActionsPermitted: true, CanStop: true };
        InspectButton.IsEnabled = !busy;
        ExportSetupButton.IsEnabled = !busy;
        ExportDiagnosticsButton.IsEnabled = !busy;
        PrepareRecoveryButton.IsEnabled = !busy && !_unsafeActionsInvalidated && _recoveryPlan is { CanApply: true };
        AutomaticRecoveryCheckBox.IsEnabled = !busy && !_unsafeActionsInvalidated && _recoveryPlan is { RequiresApproval: true };
        RevokeAutomaticButton.IsEnabled = !busy && !_unsafeActionsInvalidated && _recoveryPlan is { AllowAutomatic: true };
        OpenRecoveryHistoryButton.IsEnabled = !busy;
    }

    private static void ApplyDependency(TextBlock availability, TextBlock info, DependencyInfo dependency)
    {
        availability.Text = dependency.Available
            ? $"{dependency.Name} · 확인됨"
            : $"{dependency.Name} · {(string.IsNullOrWhiteSpace(dependency.Path) ? "없음" : "확인 필요")}";
        info.Text = BuildDependencyInfo(dependency);
        info.ToolTip = info.Text;
    }

    private static string SummarizeRunningDetail(string detail)
    {
        // Condense only this exact standard policy paragraph, retained in diagnostics.
        // Lifecycle feedback before it and any unfamiliar detail stay on the primary screen.
        const string standardPolicyDetail = "현재 설정은 검증됐지만 실행 중 프로세스의 시작 당시 적용 규칙은 직접 확인할 수 없습니다. 설정을 다시 불러와도 기존 프로세스의 규칙이 바뀌었다고 판단하지 않습니다. 외부에서 시작했거나 실행 중 설정을 바꿨다면 보조 앱을 정상 종료해 상자가 빈 것을 확인한 뒤 새로 실행하세요.";
        var feedback = detail.EndsWith(standardPolicyDetail, StringComparison.Ordinal)
            ? detail[..^standardPolicyDetail.Length].Trim() : detail;
        return string.IsNullOrWhiteSpace(feedback)
            ? "전용 환경에서 프로세스가 실행 중입니다. 로그인과 대화는 카카오톡 창에서 확인하세요."
            : feedback;
    }

    private static string DependencySummary(DependencyInfo dependency) => dependency.Available
        ? string.IsNullOrWhiteSpace(dependency.Version) ? "확인됨" : $"{dependency.Version} · 확인됨"
        : string.IsNullOrWhiteSpace(dependency.Path) ? "설치 필요" : "확인 필요";

    private static string BuildDependencyInfo(DependencyInfo dependency)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(dependency.Version))
        {
            parts.Add($"버전 {dependency.Version}");
        }

        if (!string.IsNullOrWhiteSpace(dependency.Path))
        {
            parts.Add(dependency.Path!);
        }

        if (!string.IsNullOrWhiteSpace(dependency.Detail))
        {
            parts.Add(dependency.Detail);
        }

        return parts.Count == 0 ? "확인할 정보 없음" : string.Join(" · ", parts);
    }

    private static string DisplayValue(string value) => string.IsNullOrWhiteSpace(value) ? "지정되지 않음" : value;

    private static string ReadinessTitle(Readiness state) => state switch
    {
        Readiness.MissingDependency => "필수 프로그램이 필요합니다",
        Readiness.SetupRequired => "초기 설정이 필요합니다",
        Readiness.Blocked => "실행이 차단되었습니다",
        Readiness.Ready => "보조 환경이 실행 준비되었습니다",
        Readiness.Running => "보조 프로세스 실행 관찰됨",
        Readiness.RunningUnverified => "실행 중 · 적용 정책 미확인",
        _ => "상태를 확인할 수 없습니다"
    };

    private static string ReadinessDetail(Readiness state) => state switch
    {
        Readiness.MissingDependency => "Sandboxie Plus 설치 상태를 확인한 뒤 초기 설정을 진행하세요.",
        Readiness.SetupRequired => "전용 Sandboxie Plus 상자와 저장 공간을 확인한 뒤 실행하세요.",
        Readiness.Blocked => "안전한 격리를 확인할 수 없어 실행을 막았습니다.",
        Readiness.Ready => "실행 버튼을 누르면 전용 상자에서 공식 카카오톡을 엽니다.",
        Readiness.Running => "프로세스를 관찰했습니다. 실제 적용 규칙과 로그인 성공 여부는 별도 확인이 필요합니다.",
        Readiness.RunningUnverified => "현재 설정과 기존 프로세스의 시작 당시 적용 규칙은 별개입니다.",
        _ => "다시 확인하거나 진단 JSON을 저장해 오류를 확인하세요."
    };

    private static string ReadinessBadge(Readiness state) => state switch
    {
        Readiness.MissingDependency => "필수 항목 필요",
        Readiness.SetupRequired => "설정 필요",
        Readiness.Blocked => "실행 차단",
        Readiness.Ready => "실행 가능",
        Readiness.Running => "실행 중",
        Readiness.RunningUnverified => "실행 중",
        _ => "확인 실패"
    };

    private void SetSetupResult(string message)
    {
        SetupResultText.Text = message;
        SetupResultText.ToolTip = message;
    }

    private static string ReadableException(Exception exception) => ReadableFailure(exception.GetBaseException().Message);

    private static string ReadableFailure(string? message)
    {
        message = message?.Trim();
        if (string.IsNullOrWhiteSpace(message) ||
            message.Equals(".ctor", StringComparison.OrdinalIgnoreCase) ||
            message.Contains(".ctor", StringComparison.OrdinalIgnoreCase))
        {
            return "앱 내부 오류로 조회를 마치지 못했습니다. 다시 확인하거나 진단 JSON을 저장하세요.";
        }

        return message;
    }
}
