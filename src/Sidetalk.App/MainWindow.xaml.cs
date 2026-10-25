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
    private SetupRecoveryEvidenceInspection? _evidenceInspection;
    private bool _recoveryActionsInvalidated;
    private bool _hasRecoveryResult;
    private enum RecoveryOperationPresentation { Settings, ResidualCleanup, EvidencePreservation, ConsentRevocation }
    private RecoveryOperationPresentation _recoveryOperationPresentation;
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
    private readonly Func<SidetalkCommandAction, Task<bool>> _confirmAction;
    private Task _inspectionTask = Task.CompletedTask;
    private long _inspectionGeneration;
    private long _intentGeneration;
    private bool _closed;
    private bool _windowMinimized;
    private bool _restoreInspectionRequired;
    private bool _restoreRefreshQueued;
    private bool _intentCancelled;
    private string? _actionFeedback;
    private InteractiveResizeRendering? _resizeRendering;

    internal bool IsSoftwareRenderingForResize => _resizeRendering?.IsSoftwareDuringResize == true;
    internal void DetachResizeRenderingForDiagnostics() => _resizeRendering?.Detach();

    public MainWindow(ISidetalkController controller, bool isRenderMode = false)
        : this(controller, isRenderMode, null)
    {
    }

    internal MainWindow(ISidetalkController controller, Func<SidetalkCommandAction, Task<bool>> confirmAction)
        : this(controller, false, confirmAction)
    {
    }

    internal MainWindow(ISidetalkController controller, SandboxieInstaller sandboxieInstaller, Func<SandboxieInstallerRelease, bool> confirmSandboxieInstall)
        : this(controller, false, null, sandboxieInstaller, confirmSandboxieInstall)
    {
    }

    private MainWindow(ISidetalkController controller, bool isRenderMode, Func<SidetalkCommandAction, Task<bool>>? confirmAction,
        SandboxieInstaller? sandboxieInstaller = null, Func<SandboxieInstallerRelease, bool>? confirmSandboxieInstall = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _isRenderMode = isRenderMode;
        _confirmAction = confirmAction ?? ConfirmActionAsync;
        _sandboxieInstaller = sandboxieInstaller ?? SandboxieInstaller.CreateDefault();
        _confirmSandboxieInstall = confirmSandboxieInstall ?? ConfirmSandboxieInstall;
        InitializeComponent();
        InitializeSandboxieSetup();
        AppVersionText.Text = "v" + SidetalkBuild.Version;
        UpdateInspectionDetails();
        SourceInitialized += (_, _) =>
        {
            WindowAppearance.Apply(this);
            _resizeRendering = InteractiveResizeRendering.Attach(this);
        };
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
        StateChanged += MainWindow_StateChanged;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_isRenderMode)
        {
            _inspectionTimer.Start();
            await InspectAndApplyAsync(allowAutomaticRecovery: true);
            await LoadQuickSettingsAsync();
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_operationInFlight && !_settingsChangePending)
        {
            e.Cancel = true;
            MessageBox.Show(
                this,
                "현재 설정 복구나 저장 작업이 진행 중입니다. 진행 단계가 끝난 뒤 창을 닫아 주세요.",
                "SIDETALK 작업 진행 중",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            // Pending UI intent is cancellable; a command already dispatched to
            // Core is a journalled operation and still defers closing above.
            CancelPendingAction();
            CancelSandboxieSetupForClose();
        }
    }
    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        CancelSandboxieSetupForClose();
        CancelManualRefreshPresentation();
        _inspectionGeneration++;
        _intentGeneration++;
        _inspectionTimer.Stop();
    }

    private async void InspectionTimer_Tick(object? sender, EventArgs e)
    {
        await RunScheduledInspectionAsync();
    }

    internal Task RunScheduledInspectionAsync() => InspectAndApplyAsync(allowAutomaticRecovery: true);

    private async void MainWindow_StateChanged(object? sender, EventArgs e) =>
        await SetMinimizedAsync(WindowState == WindowState.Minimized);

    internal async Task SetMinimizedAsync(bool minimized)
    {
        if (_closed || _isRenderMode || _windowMinimized == minimized) return;
        _windowMinimized = minimized;
        if (minimized)
        {
            CancelManualRefreshPresentation();
            CancelPendingAction();
            ApplyActionAvailability();
            return;
        }

        // Do not display a pre-restore response as a fresh foreground check.
        _restoreInspectionRequired = true;
        _inspectionGeneration++;
        _lastSnapshot = null;
        _unsafeActionsInvalidated = true;
        ApplyActionAvailability();
        await _inspectionTask;
        if (!_closed && !_windowMinimized)
            await InspectAndApplyAsync(allowAutomaticRecovery: true);
    }

    private async void InspectButton_Click(object sender, RoutedEventArgs e)
    {
        await RequestManualRefreshAsync(allowAutomaticRecovery: true);
    }

    /// <summary>Runs one normal, asynchronous refresh. Test and render callers can await the same path.</summary>
    public Task RefreshNowAsync() => RequestManualRefreshAsync(allowAutomaticRecovery: false);

    private Task InspectAndApplyAsync(bool allowAutomaticRecovery = false)
    {
        if (_closed || _isRenderMode || _operationInFlight || _actionInFlight || _settingsLoading)
        {
            return Task.CompletedTask;
        }
        if (_inspectionInFlight) return _inspectionTask;

        _inspectionInFlight = true;
        _lastSnapshot = null;
        _recoveryActionsInvalidated = true;
        _evidenceInspection = null;
        UpdateRefreshSummary();
        ApplyActionAvailability();
        _inspectionTask = InspectCoreAsync(allowAutomaticRecovery, _inspectionGeneration);
        return _inspectionTask;
    }

    private bool IsCurrentInspection(long generation) => !_closed && generation == _inspectionGeneration;

    private async Task InspectCoreAsync(bool allowAutomaticRecovery, long generation)
    {
        try
        {
            // Display affordances survive a routine read, but _lastSnapshot is
            // empty until a current successful response earns authority again.
            var mainFailed = false;
            AppSnapshot? observed = null;
            Exception? mainError = null;
            try
            {
                observed = await _controller.InspectAsync();
                if (!IsCurrentInspection(generation)) return;
                _restoreInspectionRequired = false;
                mainFailed = observed.InspectionFailure is not null;
            }
            catch (Exception exception)
            {
                if (!IsCurrentInspection(generation)) return;
                _restoreInspectionRequired = false;
                mainError = exception;
                mainFailed = true;
            }

            var recoveryFresh = false;
            SetupRecoveryPlan? recovery = null;
            Exception? recoveryError = null;
            try
            {
                recovery = await _controller.InspectSetupRecoveryAsync();
                if (!IsCurrentInspection(generation)) return;
                recoveryFresh = true;
            }
            catch (Exception exception)
            {
                if (!IsCurrentInspection(generation)) return;
                recoveryError = exception;
            }

            // This action has separate safety conditions and remains inspectable
            // when the ordinary repair plan cannot be applied.
            SetupRecoveryEvidenceInspection? evidence = null;
            Exception? evidenceError = null;
            try
            {
                evidence = await _controller.InspectInterruptedEvidenceAsync();
                if (!IsCurrentInspection(generation)) return;
            }
            catch (Exception exception)
            {
                if (!IsCurrentInspection(generation)) return;
                evidenceError = exception;
            }

            // Publish the completed observation together, so a successful main
            // read cannot briefly erase a still-unverified recovery failure.
            if (mainError is not null) ApplyMainInspectionError(mainError);
            else if (observed?.InspectionFailure is { } failure) ApplyMainInspectionFailure(failure);
            else if (observed is not null) ApplySnapshot(observed, updateChecks: false);
            if (recovery is not null) ApplyRecoveryPlan(recovery);
            if (recoveryError is not null)
                ApplyRecoveryFailure(recoveryError, "recovery-inspection", "복구 상태 확인", preserveFreshExecution: !mainFailed);
            if (evidence is not null) ApplyInterruptedEvidence(evidence);
            if (evidenceError is not null)
            {
                _evidenceInspection = null;
                _evidenceInspectionFailure = ReadableException(evidenceError);
                EvidencePanel.Visibility = Visibility.Visible;
                EvidenceSummaryText.Text = "중단 흔적을 확인하지 못했습니다. 진단 저장 또는 상태 새로 확인을 이용하세요.";
                InspectionDiagnostics.Record("evidence-inspection", evidenceError, persist: !_isRenderMode);
            }
            if (!mainFailed && observed is not null)
            {
                var checks = observed.Checks.Count == 0
                    ? new List<string> { "Sandboxie 연결 및 설정 적용 후 검사합니다." } : observed.Checks.ToList();
                if (recoveryError is not null) checks.Add("복구 상태 확인 오류: " + ReadableException(recoveryError));
                if (evidenceError is not null) checks.Add("중단 흔적 확인 오류: " + ReadableException(evidenceError));
                ReplaceIfChanged(_checks, checks);
            }
            UpdateInspectionDetails();
            if (allowAutomaticRecovery && recoveryFresh && !mainFailed &&
                !_isRenderMode && !_actionInFlight && !_restoreInspectionRequired &&
                !_automaticRecoveryAttempted &&
                _recoveryPlan is { State: SetupRecoveryState.AutomaticallyApplicable, AllowAutomatic: true, RequiresApproval: false })
            {
                await RunAutomaticRecoveryAsync();
            }
        }
        finally
        {
            _inspectionInFlight = false;
            if (!_closed)
            {
                UpdateRefreshSummary();
                ApplyActionAvailability();
            }
        }
    }

    private async Task RunAutomaticRecoveryAsync()
    {
        if (_closed || _isRenderMode || _actionInFlight || _settingsLoading || _automaticRecoveryAttempted || _operationInFlight)
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
            var progress = RecoveryProgressForCurrentOperation();
            var snapshot = await _controller.PrepareAutomaticallyAsync(progress);
            ApplySnapshot(snapshot);
            if (snapshot.InspectionFailure is { } failure)
            {
                _recoveryActionsInvalidated = true;
                RecoveryStateText.Text = "복구 후 확인 실패";
                SetRecoveryResult("복구 작업 후 상태를 확인하지 못했습니다: " + ReadableFailure(failure.Message));
                _automaticRecoveryFailureScopeFingerprint = _recoveryPlan?.ScopeFingerprint;
                return;
            }
            ApplyRecoveryPlan(await _controller.InspectSetupRecoveryAsync());
            SetRecoveryResult(string.IsNullOrWhiteSpace(snapshot.RecoveryResultDetail)
                ? VerifiedRecoveryMessage(snapshot, "자동 복구")
                : ReadableFailure(snapshot.RecoveryResultDetail));
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
        => ApplySnapshot(snapshot, updateChecks: true);

    private void ApplySnapshot(AppSnapshot snapshot, bool updateChecks)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_closed) return;
        if (snapshot.InspectionFailure is { } failure)
        {
            ApplyMainInspectionFailure(failure);
            return;
        }
        _lastSnapshot = snapshot;
        _unsafeActionsInvalidated = false;
        _lastObservedSnapshot = snapshot;
        _lastSuccessfulInspectionAt = snapshot.CheckedAt;
        _mainInspectionFailure = null;
        if (!_actionInFlight && _actionFeedbackRequiresRevalidation)
        {
            _actionFeedback = null;
            _actionFeedbackRequiresRevalidation = false;
        }
        UpdateInspectionDetails();
        if (_windowMinimized) return;

        var running = snapshot.State is Readiness.Running or Readiness.RunningUnverified;
        var detail = string.IsNullOrWhiteSpace(snapshot.Detail) ? ReadinessDetail(snapshot.State) : snapshot.Detail;
        SetText(StatusTitleText, running ? "보조 환경 실행 중"
            : string.IsNullOrWhiteSpace(snapshot.Title) ? ReadinessTitle(snapshot.State) : snapshot.Title);
        StatusDetailText.Text = running ? SummarizeRunningDetail(detail) : SummarizeAdminRightsDetail(snapshot, detail);
        PolicyDetailText.Text = detail;
        PolicySummaryText.Text = running
            ? (snapshot.CurrentConfigurationVerified ? "현재 격리 설정 확인됨 · 시작 당시 적용 규칙은 확인 불가" : "현재 격리 설정과 시작 당시 적용 규칙을 확인할 수 없습니다.")
            : string.Empty;
        PolicySummaryText.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        PolicySummaryText.SetResourceReference(TextBlock.ForegroundProperty, "WarningInkBrush");
        ApplyAdminRightsPresentation(snapshot);
        StatusBadgeText.Text = ReadinessBadge(snapshot.State);
        var statusBrush = snapshot.State switch
        {
            Readiness.Ready or Readiness.Running or Readiness.RunningUnverified => "AccentTextBrush",
            Readiness.Blocked or Readiness.Unknown => "ErrorInkBrush",
            _ => "WarningInkBrush"
        };
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, statusBrush);
        StatusBadgeText.SetResourceReference(TextBlock.ForegroundProperty, statusBrush);
        InitialSetupPanel.Visibility = snapshot.State is Readiness.SetupRequired or Readiness.MissingDependency
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateRefreshSummary();
        KakaoSummaryText.Text = DependencySummary(snapshot.Kakao);
        SandboxieSummaryText.Text = DependencySummary(snapshot.Sandboxie);
        UpdateSandboxieSetupCard();

        BoxNameText.Text = DisplayValue(snapshot.BoxName);
        StoragePathText.Text = DisplayValue(snapshot.StoragePath);
        ProcessIdsText.Text = snapshot.BoxProcessIds.Count > 0
            ? $"{snapshot.BoxProcessIds.Count}개 · PID {string.Join(", ", snapshot.BoxProcessIds)}"
            : snapshot.State == Readiness.Ready ? "없음" : "프로세스 상태 미확인";
        CheckedAtText.Text = snapshot.CheckedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        ApplyDependency(KakaoAvailabilityText, KakaoInfoText, snapshot.Kakao);
        ApplyDependency(SandboxieAvailabilityText, SandboxieInfoText, snapshot.Sandboxie);

        if (updateChecks) ReplaceIfChanged(_checks, snapshot.Checks.Count == 0
            ? ["Sandboxie 연결 및 설정 적용 후 검사합니다."] : snapshot.Checks);

        if (!string.IsNullOrWhiteSpace(snapshot.RecoveryResultDetail))
            SetRecoveryResult(ReadableFailure(snapshot.RecoveryResultDetail));
        ApplyActionAvailability();
    }

    public void ApplyRecoveryPlan(SetupRecoveryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (_closed) return;
        var samePlan = _recoveryPlan is not null &&
            SameApproval(_recoveryPlan, plan);
        if (!samePlan)
        {
            _automaticRecoveryChoiceDirty = false;
        }

        _recoveryPlan = plan;
        if (_recoveryInspectionFailure is not null)
        {
            _recoveryInspectionFailure = null;
            SetRecoveryResult(string.Empty);
        }
        UpdateInspectionDetails();
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
        _recoveryActionsInvalidated = false;
        if (_windowMinimized) return;

        if (!_initialRecoveryShown && plan.CanApply && plan.RequiresApproval && !_isRenderMode)
        {
            MainTabs.SelectedIndex = 1;
            _initialRecoveryShown = true;
        }

        var showPanel = !plan.IsHealthy || plan.AllowAutomatic || _hasRecoveryResult;
        RecoveryPanel.Visibility = showPanel ? Visibility.Visible : Visibility.Collapsed;
        RecoveryStateText.Text = RecoveryStateLabel(plan.State);
        RecoverySummaryText.Text = string.IsNullOrWhiteSpace(plan.Summary)
            ? "현재 설정을 확인했습니다."
            : plan.Summary;
        ApplyAdminRightsRecoveryPresentation(plan);
        RecoveryChangesSummaryText.Text = SummarizeRecoveryChanges(plan);
        var managementTargets = plan.ManagementProcesses.Count != 0 ||
            plan.Evidence.Any(text => text.StartsWith("관리 UI 종료 대상:", StringComparison.Ordinal));
        RecoveryLossWarningText.Text = managementTargets
            ? "주의: 확인된 Sandboxie 관리 창이 승인 후 닫힙니다. 저장하지 않은 관리 설정은 사라질 수 있으니 먼저 저장하세요."
            : IsAdminRightsRepair(plan)
                ? "현재 확인된 관리 창 종료 대상은 없습니다. 종료 대상이 포함된 계획을 승인하면 저장하지 않은 관리 설정은 사라질 수 있으니 먼저 저장하세요."
                : "현재 확인된 관리 창 종료 대상은 없습니다. 자동 복구를 선택하면 재발 시 확인된 관리 창이 닫힐 수 있으며, 저장하지 않은 관리 설정은 사라질 수 있습니다.";
        RecoveryNextStepText.Text = plan.State is SetupRecoveryState.Blocked or SetupRecoveryState.Interrupted or SetupRecoveryState.Failed
            ? "다음: 차단 이유와 복구·보존 내역을 확인하고 ‘상태 새로 확인’을 누르세요. 미완료 작업을 완료로 처리하지 않습니다."
            : "복구 후: ‘실행 준비’가 확인되면 보조 카카오톡을 직접 실행하고 공식 창에서 로그인하세요.";

        var recoveryChanges = new List<string>();
        foreach (var change in plan.Changes.Where(change => !change.IsNoOp && change.Kind != SetupRecoveryChangeKind.AddOwnedBoxSetting))
        {
            recoveryChanges.Add(FormatRecoveryChange(change));
        }

        var ownedChanges = plan.Changes.Count(change => !change.IsNoOp && change.Kind == SetupRecoveryChangeKind.AddOwnedBoxSetting);
        if (ownedChanges > 0)
            recoveryChanges.Add($"SIDETALK 전용 환경 생성: 표시된 전용 저장 경로에 격리 설정 {ownedChanges}개 적용 · 기존 데이터 보존");

        if (recoveryChanges.Count == 0)
        {
            recoveryChanges.Add(plan.IsHealthy ? "변경할 설정이 없습니다." : "확인된 변경 항목이 없습니다.");
        }

        var recoveryAffected = new List<string>();
        var evidenceTargets = plan.Evidence
            .Where(text => !plan.Changes.Any(change => text == change.Section + "." + change.Key))
            .Select(SafeRecoveryText)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var evidence in evidenceTargets)
        {
            recoveryAffected.Add("영향 확인: " + evidence);
        }

        recoveryAffected.Add(evidenceTargets.Any(text => text.StartsWith("관리 UI 종료 대상:", StringComparison.Ordinal))
            ? "열린 Sandboxie 관리 창: 위에 표시된 종료 대상은 승인 후 닫힙니다."
            : "열린 Sandboxie 관리 창: 확인된 종료 대상이 없습니다.");


        var recoveryPreserved = new List<string>();
        recoveryPreserved.Add("서비스·드라이버·Start.exe·메인 카카오톡·다른 Sandboxie 상자는 종료하거나 변경하지 않습니다.");
        recoveryPreserved.Add("보조 계정과 대화 데이터는 삭제하거나 복사하지 않습니다.");
        recoveryPreserved.Add("profile.json·소유 식별자·전용 저장 공간과 미완료 명령 기록을 보존합니다.");
        recoveryPreserved.Add("확인된 범위 밖의 설정과 데이터는 건드리지 않습니다.");

        ReplaceIfChanged(_recoveryChanges, recoveryChanges);
        ReplaceIfChanged(_recoveryAffected, recoveryAffected);
        ReplaceIfChanged(_recoveryPreserved, recoveryPreserved);

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
        if (!samePlan && string.IsNullOrWhiteSpace(_lastSnapshot?.RecoveryResultDetail)) { SetRecoveryResult(string.Empty); }
        _recoveryActionsInvalidated = false;
        ApplyActionAvailability();
    }

    public void ApplyInterruptedEvidence(SetupRecoveryEvidenceInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        if (_closed) return;
        _evidenceInspection = inspection;
        _evidenceInspectionFailure = null;
        UpdateInspectionDetails();
        if (_windowMinimized) return;
        EvidencePanel.Visibility = inspection.Items.Count == 0 && !inspection.BlocksExecution ? Visibility.Collapsed : Visibility.Visible;
        EvidenceSummaryText.Text = ReadableFailure(inspection.Detail);
        EvidenceSummaryText.ToolTip = string.Join(Environment.NewLine, inspection.Items.Select(item =>
            $"{EvidenceKindLabel(item.Kind)}: {ReadableFailure(item.Detail)} · 원본 {item.OriginalPath} · SHA-256 {item.Sha256}"));
        EvidenceHistoryText.Text = inspection.Items.Count == 0 ? string.Empty : "원본과 대응 기록 보존 위치: " + inspection.HistoryPath;
        ApplyActionAvailability();
    }

    private static string EvidenceKindLabel(SetupRecoveryEvidenceKind kind) => kind switch
    {
        SetupRecoveryEvidenceKind.CompleteRecord => "완전한 중단 기록",
        SetupRecoveryEvidenceKind.ApprovedProfileCandidate => "승인된 프로필 후보",
        SetupRecoveryEvidenceKind.IncompleteRecord => "불완전한 기록",
        SetupRecoveryEvidenceKind.ConflictingRecord => "기존 파일과 충돌",
        SetupRecoveryEvidenceKind.ConsentRecord => "동의 저장 기록",
        _ => "출처 확인 필요"
    };

    private void OpenEvidenceHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRenderMode || _operationInFlight || _evidenceInspection is not { } inspection) return;
        try
        {
            var path = Path.GetFullPath(inspection.HistoryPath);
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || !Directory.Exists(path))
                throw new DirectoryNotFoundException("보존 내역 폴더는 원본 보존 후 확인할 수 있습니다.");
            ProfileStore.RejectReparseAncestors(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception exception) { SetRecoveryResult(ReadableException(exception)); }
    }

    private async void PreserveEvidenceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _isRenderMode || _actionInFlight || _operationInFlight || _inspectionInFlight || _settingsLoading || _evidenceInspection is not { CanPreserve: true } inspection) return;
        _operationInFlight = true;
        ApplyActionAvailability();
        var answer = MessageBox.Show(this,
            "확인된 중단 흔적의 원본 바이트·해시·원래 위치와 관련 작업을 보존한 뒤 현재 설정을 다시 확인합니다. 원본 보존만으로 미완료 작업을 완료 처리하지 않습니다.\n\n원본을 보존하고 다시 확인할까요?",
            "중단된 준비의 원본 보존", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK)
        {
            _operationInFlight = false;
            ApplyActionAvailability();
            return;
        }
        BeginRecoveryOperation("중단 흔적의 원본을 보존하고 현재 상태를 다시 확인하고 있습니다.", RecoveryOperationPresentation.EvidencePreservation);
        _lastSnapshot = null;
        _evidenceInspection = null;
        ApplyActionAvailability();
        try
        {
            var progress = RecoveryProgressForCurrentOperation();
            var snapshot = await _controller.ReconcileInterruptedEvidenceAsync(inspection, progress);
            ApplySnapshot(snapshot);
            if (snapshot.InspectionFailure is { } failure)
            {
                _recoveryActionsInvalidated = true;
                SetRecoveryResult("원본 보존 작업 후 상태를 확인하지 못했습니다: " + ReadableFailure(failure.Message));
                return;
            }
            SetRecoveryResult(string.IsNullOrWhiteSpace(snapshot.RecoveryResultDetail)
                ? "원본 보존과 재조정을 확인했습니다. 남은 차단 이유와 승인할 복구 항목을 확인하세요."
                : ReadableFailure(snapshot.RecoveryResultDetail));
        }
        catch (Exception exception) { ApplyRecoveryOperationError(exception, "중단 흔적 보존·재조정"); }
        finally
        {
            _operationInFlight = false;
            RecoveryProgressText.Visibility = Visibility.Collapsed;
            await InspectAndApplyAsync(allowAutomaticRecovery: false);
        }
    }

    private async void PrepareRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _isRenderMode || _actionInFlight || _inspectionInFlight || _settingsLoading || _recoveryActionsInvalidated || _operationInFlight || _recoveryPlan is not { CanApply: true } plan)
        {
            return;
        }

        if (IsAdminRightsRepair(plan))
        {
            _operationInFlight = true;
            ApplyActionAvailability();
            var answer = MessageBox.Show(this,
                "현재 조회로 확인한 관리자 권한 제한을 복구합니다. 아래 변경 계획을 새로 승인해야 하며 기존 자동 복구 동의로 적용하지 않습니다.\n\n" +
                string.Join("\n", plan.Changes.Select(FormatRecoveryChange)) +
                "\n\n승인된 관리 창 정리 대상은 복구 패널의 영향 범위를 확인하세요. 보조 환경이 비어 있고 현재 소유·설정이 승인 계획과 같을 때만 적용합니다. 프로필·계정 데이터·기존 기록은 보존합니다.\n\n표시된 변경 계획을 승인하고 복구할까요?",
                "관리자 권한 제한 복구 승인", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK)
            {
                _operationInFlight = false;
                SetRecoveryResult("관리자 권한 제한 복구를 취소했습니다.");
                ApplyActionAvailability();
                return;
            }
        }
        BeginRecoveryOperation("복구를 준비하고 있습니다. 기존 실행 가능 상태는 해제했습니다.");
        _operationInFlight = true;
        SetRecoveryProgress("설정 백업 중…");
        ApplyActionAvailability();

        try
        {
            var progress = RecoveryProgressForCurrentOperation();
            var snapshot = await _controller.PrepareAsync(
                plan,
                !IsAdminRightsRepair(plan) && AutomaticRecoveryCheckBox.IsChecked == true,
                progress);
            ApplySnapshot(snapshot);
            if (snapshot.InspectionFailure is { } failure)
            {
                _recoveryActionsInvalidated = true;
                RecoveryStateText.Text = "복구 후 확인 실패";
                SetRecoveryResult("복구 작업 후 상태를 확인하지 못했습니다: " + ReadableFailure(failure.Message));
                return;
            }
            ApplyRecoveryPlan(await _controller.InspectSetupRecoveryAsync());
            SetRecoveryResult(string.IsNullOrWhiteSpace(snapshot.RecoveryResultDetail)
                ? VerifiedRecoveryMessage(snapshot, "복구")
                : ReadableFailure(snapshot.RecoveryResultDetail));
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

    private async void ResidualCleanupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _isRenderMode || _actionInFlight || _inspectionInFlight || _settingsLoading ||
            _recoveryActionsInvalidated || _operationInFlight || _recoveryPlan?.ResidualCleanupApproval is not { } approval ||
            GetProcessState(_lastSnapshot?.AdminRights) != BoxProcessState.ResidualOnly)
        {
            return;
        }

        _operationInFlight = true;
        ApplyActionAvailability();
        var changeLines = approval.RepairPlan.Changes
            .Where(change => !change.IsNoOp)
            .Select(FormatRecoveryChange)
            .ToArray();
        var changes = changeLines.Length == 0 ? "표시된 관리자 권한 제한 복구 범위" : string.Join("\n", changeLines);
        var processes = approval.ResidualProcesses.Count == 0
            ? "- 잔여 프로세스 목록 없음"
            : string.Join("\n", approval.ResidualProcesses.Select(FormatResidualProcess));
        var answer = MessageBox.Show(this,
            $"SIDETALK 전용 box ‘{SafeRecoveryText(approval.BoxName)}’에서 확인된 잔여 프로세스입니다.\n\n" +
            processes +
            $"\n\nSIDETALK 전용 환경의 잔여 프로세스만 공식 Sandboxie 종료 경로로 정리한 뒤, box가 비었는지 다시 확인하고 다음 복구 범위를 이어서 확인합니다:\n{changes}\n\n" +
            "보조 계정 데이터·전용 환경·복구 기록은 삭제하지 않습니다. 다른 box, 호스트 카카오톡, Sandboxie 서비스·드라이버·관리 UI는 종료하지 않습니다.\n\n진행할까요?",
            "잔여 프로세스 정리 후 복구 승인", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK)
        {
            _operationInFlight = false;
            SetRecoveryResult("잔여 프로세스 정리 후 복구를 취소했습니다.");
            ApplyActionAvailability();
            return;
        }

        BeginRecoveryOperation("잔여 프로세스 정리와 관리자 권한 제한 복구를 준비하고 있습니다.", RecoveryOperationPresentation.ResidualCleanup);
        SetRecoveryProgress("잔여 프로세스 종료 전 안전 조건을 다시 확인 중…");
        ApplyActionAvailability();
        try
        {
            var progress = RecoveryProgressForCurrentOperation();
            var snapshot = await _controller.CleanResidualBoxAndPrepareAsync(approval, progress);
            ApplySnapshot(snapshot);
            if (snapshot.InspectionFailure is { } failure)
            {
                _recoveryActionsInvalidated = true;
                RecoveryStateText.Text = "잔여 정리 후 확인 실패";
                SetRecoveryResult("잔여 프로세스 정리 후 상태를 확인하지 못했습니다: " + ReadableFailure(failure.Message));
                return;
            }

            ApplyRecoveryPlan(await _controller.InspectSetupRecoveryAsync());
            SetRecoveryResult(string.IsNullOrWhiteSpace(snapshot.RecoveryResultDetail)
                ? VerifiedRecoveryMessage(snapshot, "잔여 프로세스 정리와 관리자 권한 제한 복구")
                : ReadableFailure(snapshot.RecoveryResultDetail));
        }
        catch (Exception exception)
        {
            ApplyRecoveryOperationError(exception, "잔여 프로세스 정리·복구");
            SetRecoveryResult($"잔여 프로세스 정리 후 복구를 완료하지 못했습니다: {ReadableException(exception)}");
        }
        finally
        {
            _operationInFlight = false;
            RecoveryProgressText.Visibility = Visibility.Collapsed;
            ApplyActionAvailability();
        }
    }

    private static string FormatResidualProcess(BoxProcessObservation process)
    {
        var image = process.IdentityVerified && !string.IsNullOrWhiteSpace(process.ImageName)
            ? process.ImageName.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal)
            : "신원 미확인 전용 프로세스";
        return $"- {image} · PID {process.ProcessId}";
    }

    private async void RevokeAutomaticButton_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _isRenderMode || _actionInFlight || _inspectionInFlight || _settingsLoading || _recoveryActionsInvalidated || _operationInFlight || _recoveryPlan is not { AllowAutomatic: true })
        {
            return;
        }

        BeginRecoveryOperation("자동 복구 설정을 해제하고 있습니다.", RecoveryOperationPresentation.ConsentRevocation);
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
        if (!string.IsNullOrWhiteSpace(message))
            RecoveryStateText.Text = _recoveryOperationPresentation switch
            {
                RecoveryOperationPresentation.ConsentRevocation => "자동 복구 해제 중",
                RecoveryOperationPresentation.EvidencePreservation => "원본 보존·재확인 중",
                RecoveryOperationPresentation.ResidualCleanup => "잔여 정리·복구 중",
                _ when message.StartsWith("복구 준비", StringComparison.Ordinal) || message.StartsWith("설정 백업", StringComparison.Ordinal) => "복구 준비 중",
                _ when message == "재검사" => "복구 후 확인 중",
                _ => "복구 실행 중"
            };
        if (!string.IsNullOrWhiteSpace(message))
        {
            SetText(StatusTitleText, RecoveryStateText.Text);
            StatusDetailText.Text = message;
        }
        var operationLabel = _recoveryOperationPresentation switch
        {
            RecoveryOperationPresentation.ConsentRevocation => "자동 복구 해제",
            RecoveryOperationPresentation.EvidencePreservation => "원본 보존",
            RecoveryOperationPresentation.ResidualCleanup => "잔여 정리·복구",
            _ => "설정 복구"
        };
        _recoveryFeedback = string.IsNullOrWhiteSpace(message) ? null : operationLabel + " · " + message;
        UpdateRefreshSummary();
    }

    private void SetRecoveryResult(string message)
    {
        RecoveryResultText.Text = message;
        RecoveryResultText.ToolTip = message;
        _hasRecoveryResult = !string.IsNullOrWhiteSpace(message);
        if (_hasRecoveryResult) RecoveryPanel.Visibility = Visibility.Visible;
        _recoveryFeedback = _hasRecoveryResult ? message : null;
        UpdateRefreshSummary();
    }

    private string VerifiedRecoveryMessage(AppSnapshot snapshot, string operation) =>
        _recoveryPlan is { State: SetupRecoveryState.Healthy or SetupRecoveryState.Applied } && snapshot.CurrentConfigurationVerified
            ? $"{operation} 후 현재 설정을 확인했습니다. 실행 준비 상태를 확인하고 보조 카카오톡을 직접 실행하세요."
            : $"{operation} 작업 이후에도 확인할 항목이 남아 있습니다. 차단 이유와 복구 내역을 확인하고 상태를 다시 확인하세요.";

    private void ClearStaleSnapshot(string detail)
    {
        _actionFeedback = detail;
        UpdateRefreshSummary();
        _inspectionGeneration++;
        _lastSnapshot = null;
        _unsafeActionsInvalidated = true;
        SetText(StatusTitleText, "작업 진행 중");
        StatusBadgeText.Text = "진행 중";
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "WarningInkBrush");
        StatusBadgeText.SetResourceReference(TextBlock.ForegroundProperty, "WarningInkBrush");
        StatusDetailText.Text = detail;
        PolicyDetailText.Text = detail;
        PolicySummaryText.Visibility = Visibility.Collapsed;
    }

    private void BeginRecoveryOperation(string detail, RecoveryOperationPresentation operation = RecoveryOperationPresentation.Settings)
    {
        _recoveryOperationPresentation = operation;
        SetRecoveryResult(string.Empty);
        RecoveryPanel.Visibility = Visibility.Visible;
        RecoveryStateText.Text = operation switch
        {
            RecoveryOperationPresentation.ConsentRevocation => "자동 복구 해제 중",
            RecoveryOperationPresentation.EvidencePreservation => "원본 보존 준비 중",
            RecoveryOperationPresentation.ResidualCleanup => "잔여 정리·복구 준비 중",
            _ => "복구 준비 중"
        };
        _actionFeedback = null;
        _recoveryFeedback = detail;
        UpdateRefreshSummary();
        _inspectionGeneration++;
        _lastSnapshot = null;
        _unsafeActionsInvalidated = true;
        SetText(StatusTitleText, RecoveryStateText.Text);
        StatusBadgeText.Text = "진행 중";
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "WarningInkBrush");
        StatusBadgeText.SetResourceReference(TextBlock.ForegroundProperty, "WarningInkBrush");
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
        if (change.Kind == SetupRecoveryChangeKind.RepairOwnedBoxSetting && change.Key.Equals("DropAdminRights", StringComparison.OrdinalIgnoreCase))
            return $"관리자 권한 제한 복구 · 전용 box {SafeRecoveryText(change.Section)}의 DropAdminRights: {FormatAdminRightsValues(change.CurrentValues)} → y";
        if (change.Kind == SetupRecoveryChangeKind.RemoveGlobalTemplate)
            return "격리 정책과 충돌하는 검토된 전역 호환성 선택 제거 · GlobalSettings.Template: " + string.Join(", ", change.CurrentValues.Except(change.DesiredValues, StringComparer.OrdinalIgnoreCase));
        if (change.Kind == SetupRecoveryChangeKind.SetUserSetting && change.Key == "SbieCtrl_AutoRunSoftCompat")
            return $"현재 사용자의 SbieCtrl 자동 호환성 팝업 재발 방지 · {change.Section}.SbieCtrl_AutoRunSoftCompat=n";
        var description = SafeRecoveryText(change.Description);
        if (string.IsNullOrWhiteSpace(description))
        {
            description = $"{change.Section} · {change.Key}";
        }

        return $"• {description} ({RecoveryChangeLabel(change.Kind)})";
    }

    private static string SummarizeRecoveryChanges(SetupRecoveryPlan plan)
    {
        var changes = plan.Changes.Where(change => !change.IsNoOp).ToArray();
        var purposes = new List<string>();
        if (changes.Any(change => change.Kind == SetupRecoveryChangeKind.RemoveGlobalTemplate))
            purposes.Add("격리 정책과 충돌하는 검토된 전역 호환성 선택을 제거합니다. 템플릿 파일은 삭제하지 않습니다.");
        if (changes.Any(change => change.Kind == SetupRecoveryChangeKind.SetUserSetting && change.Key == "SbieCtrl_AutoRunSoftCompat"))
            purposes.Add("현재 사용자의 자동 호환성 팝업 재발을 방지합니다.");
        if (IsAdminRightsRepair(plan))
            purposes.Add("전용 환경의 관리자 권한 제한을 복구합니다. 별도의 새 승인이 필요합니다.");
        if (changes.Any(change => change.Kind == SetupRecoveryChangeKind.AddOwnedBoxSetting))
            purposes.Add("표시된 전용 저장 공간에 SIDETALK 격리 환경을 준비합니다.");
        if (purposes.Count == 0)
            purposes.Add(changes.Length == 0 ? "확인된 변경 항목이 없습니다." : "검토된 설정만 변경합니다. 정확한 항목과 영향 범위를 아래에서 펼쳐 확인하세요.");
        return string.Join("\n", purposes);
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
        SetupRecoveryState.Healthy => "현재 설정 정상",
        SetupRecoveryState.FirstSetup => "최초 준비 · 승인 필요",
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
        _mainInspectionFailure = detail;
        UpdateInspectionDetails();
        var lastSnapshot = _lastObservedSnapshot;
        _lastSnapshot = null;
        _unsafeActionsInvalidated = true;
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "WarningInkBrush");
        StatusBadgeText.SetResourceReference(TextBlock.ForegroundProperty, "WarningInkBrush");
        StatusBadgeText.Text = "현재 확인 실패";

        if (lastSnapshot is { } snapshot)
        {
            var checkedAt = snapshot.CheckedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            SetText(StatusTitleText, "마지막 확인 결과");
            StatusDetailText.Text = "현재 상태를 확인하지 못했습니다. 아래 정보는 마지막 확인 결과이며 현재 상태를 보장하지 않습니다. 다시 확인하세요.\n" + detail;
            PolicyDetailText.Text = $"마지막 확인 ({checkedAt}) · {detail}";
            PolicySummaryText.Text = "마지막 확인 당시의 격리 설정이며 현재 상태는 미확인입니다.";
            KakaoSummaryText.Text = "마지막 확인 · " + DependencySummary(snapshot.Kakao);
            SandboxieSummaryText.Text = "마지막 확인 · " + DependencySummary(snapshot.Sandboxie);
            ProcessIdsText.Text = snapshot.BoxProcessIds.Count > 0
                ? $"마지막 확인 {snapshot.BoxProcessIds.Count}개 · PID {string.Join(", ", snapshot.BoxProcessIds)}"
                : "마지막 확인 결과 · 현재 프로세스 상태 미확인";
            CheckedAtText.Text = "마지막 확인 " + checkedAt;
            if (_checks.Count == 0 || !_checks[0].StartsWith("현재 상태를 확인하지 못했습니다", StringComparison.Ordinal))
            {
                _checks.Insert(0, "현재 상태를 확인하지 못했습니다. 아래 정보는 마지막 확인 결과입니다.");
            }
        }
        else
        {
            SetText(StatusTitleText, "현재 상태를 확인하지 못했습니다");
            StatusDetailText.Text = "현재 상태를 확인하지 못했습니다. 다시 확인하거나 진단 JSON을 저장하세요.\n" + detail;
            PolicyDetailText.Text = $"현재 상태 확인 실패 · {detail}";
            PolicySummaryText.Visibility = Visibility.Collapsed;
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
        UpdateRefreshSummary();
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

    private void ApplyRecoveryFailure(Exception exception, string diagnosticStage, string stage, bool preserveFreshExecution = false)
    {
        ArgumentNullException.ThrowIfNull(exception);
        InspectionDiagnostics.Record(diagnosticStage, exception, persist: !_isRenderMode);
        if (diagnosticStage == "recovery-inspection") _recoveryInspectionFailure = ReadableException(exception);
        UpdateInspectionDetails();
        _recoveryActionsInvalidated = true;
        if (!preserveFreshExecution)
        {
            _lastSnapshot = null;
            _unsafeActionsInvalidated = true;
        }
        RecoveryPanel.Visibility = Visibility.Visible;
        RecoveryStateText.Text = diagnosticStage != "recovery-operation" ? "복구 상태 확인 실패" : _recoveryOperationPresentation switch
        {
            RecoveryOperationPresentation.ConsentRevocation => "자동 복구 해제 실패",
            RecoveryOperationPresentation.EvidencePreservation => "원본 보존 실패 · 재확인 필요",
            _ => "복구 실패 · 재확인 필요"
        };
        RecoverySummaryText.Text = preserveFreshExecution
            ? $"{stage}에 실패해 설정 복구를 차단했습니다. 실행·종료는 이번에 새로 확인한 격리 정책과 명령 상태에 따릅니다."
            : diagnosticStage == "recovery-operation" && _recoveryOperationPresentation is RecoveryOperationPresentation.Settings or RecoveryOperationPresentation.ResidualCleanup
                ? $"{stage}를 완료하지 못했습니다. 일부 설정이 이미 적용됐을 수 있습니다. 변경 전 상태로 복원됐다고 확인되지 않았으므로 복구 내역과 백업을 확인하고 ‘상태 새로 확인’을 누르세요."
                : $"{stage}에 실패했습니다. 현재 실행 권한을 해제했습니다. 상태 새로 확인으로 다시 확인하세요.";
        SetRecoveryResult($"{stage} 실패: {ReadableException(exception)}");

        if (!preserveFreshExecution)
        {
            SetText(StatusTitleText, RecoveryStateText.Text);
            StatusBadgeText.Text = "다시 확인 필요";
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "WarningInkBrush");
            StatusBadgeText.SetResourceReference(TextBlock.ForegroundProperty, "WarningInkBrush");
            StatusDetailText.Text = diagnosticStage == "recovery-operation" ? _recoveryOperationPresentation switch
            {
                RecoveryOperationPresentation.ConsentRevocation => "자동 복구 동의 해제 결과를 확인하지 못했습니다. 저장된 동의 상태와 내역을 다시 확인하세요.",
                RecoveryOperationPresentation.EvidencePreservation => "원본 보존 결과와 미완료 기록을 다시 확인해야 합니다. 보존 내역과 상태 새로 확인을 이용하세요.",
                _ => "복구 작업 결과와 현재 실행 상태를 새로 확인해야 합니다. 상태 새로 확인을 누르세요."
            } : "복구 상태 조회 결과를 확인하지 못했습니다. 상태 새로 확인을 누르세요.";
        }
        else
        {
            StatusDetailText.Text += " 설정 복구 조회는 실패했습니다. 새로 확인한 실행 판단은 위 상태와 같습니다.";
        }
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
        SetText(StatusTitleText, "상태 확인 실패");
        StatusBadgeText.Text = "확인 실패";
        StatusDetailText.Text = ReadableException(exception);
        _mainInspectionFailure = StatusDetailText.Text;
        UpdateInspectionDetails();
        PolicyDetailText.Text = StatusDetailText.Text;
        PolicySummaryText.Visibility = Visibility.Collapsed;
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "ErrorInkBrush");
        StatusBadgeText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorInkBrush");
        UpdateRefreshSummary();
        KakaoSummaryText.Text = "확인 실패";
        SandboxieSummaryText.Text = "확인 실패";
        BoxNameText.Text = "확인할 수 없음";
        StoragePathText.Text = "확인할 수 없음";
        ProcessIdsText.Text = "확인할 수 없음";
        CheckedAtText.Text = _lastSuccessfulInspectionAt is { } last
            ? "마지막 확인 " + last.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "마지막 확인 없음";
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

    private async void ExportSetupButton_Click(object sender, RoutedEventArgs e)
    {
        await ExportSetupAsync(() =>
        {
            var dialog = new OpenFolderDialog
            {
                Title = "전용 INI를 저장할 새 폴더 선택",
                Multiselect = false
            };
            return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        });
    }

    internal async Task ExportSetupAsync(Func<string?> selectFolder)
    {
        if (_closed || _isRenderMode || _actionInFlight || _operationInFlight || _inspectionInFlight || _settingsLoading)
        {
            return;
        }

        // Native dialogs pump dispatcher work; reserve before opening one so a
        // timer-driven recovery cannot acquire and then lose this operation flag.
        _operationInFlight = true;
        ApplyActionAvailability();
        try
        {
            var folder = selectFolder();
            if (_closed || string.IsNullOrWhiteSpace(folder)) return;
            SetSetupResult("전용 INI 파일을 저장하는 중…");
            ClearStaleSnapshot("전용 INI를 저장하고 있습니다.");
            var path = await _controller.ExportSetupAsync(folder);
            SetSetupResult($"전용 INI 파일을 저장했습니다: {path}");
        }
        catch (Exception exception)
        {
            SetSetupResult($"전용 INI 파일을 저장하지 못했습니다: {ReadableException(exception)}");
        }
        finally
        {
            _operationInFlight = false;
            _actionFeedback = null;
            ApplyActionAvailability();
            await InspectAndApplyAsync();
        }
    }

    private async void ExportDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        await ExportDiagnosticsAsync(() =>
        {
            var dialog = new SaveFileDialog
            {
                Title = "진단 JSON 저장",
                FileName = $"sidetalk-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json",
                DefaultExt = ".json",
                AddExtension = true,
                Filter = "JSON 파일 (*.json)|*.json|모든 파일 (*.*)|*.*",
                OverwritePrompt = false
            };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        });
    }

    internal async Task ExportDiagnosticsAsync(Func<string?> selectFile)
    {
        if (_closed || _isRenderMode || _actionInFlight || _operationInFlight || _inspectionInFlight || _settingsLoading)
        {
            return;
        }

        _operationInFlight = true;
        ApplyActionAvailability();
        try
        {
            var file = selectFile();
            if (_closed || string.IsNullOrWhiteSpace(file)) return;
            if (File.Exists(file))
            {
                SetSetupResult("기존 진단 파일은 덮어쓰지 않습니다. 새 파일 이름을 선택하세요.");
                return;
            }

            SetSetupResult("진단 JSON을 저장하는 중…");
            ClearStaleSnapshot("진단 JSON을 저장하고 있습니다.");
            var path = await _controller.ExportDiagnosticsAsync(file);
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
            _actionFeedback = null;
            ApplyActionAvailability();
            await InspectAndApplyAsync();
        }
    }

    private void KakaoOfficialButton_Click(object sender, RoutedEventArgs e)
    {
        OpenOfficialPage(KakaoOfficialUrl, "카카오톡 공식 안내 페이지");
    }

    private bool OpenOfficialPage(string url, string description)
    {
        if (_isRenderMode) return true;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception exception)
        {
            SetSetupResult($"{description}을 열지 못했습니다: {ReadableException(exception)}");
            return false;
        }
    }

    private void ApplyActionAvailability()
    {
        var commandBusy = _closed || _isRenderMode || _windowMinimized || _operationInFlight || _actionInFlight || _settingsLoading;
        // These controls accept intent from the last successful observation.
        // RequestActionAsync earns execution authority again after confirmation.
        LaunchButton.IsEnabled = !commandBusy && !_restoreInspectionRequired && !_unsafeActionsInvalidated && Permits(_lastObservedSnapshot, SidetalkCommandAction.Launch);
        StopButton.IsEnabled = !commandBusy && !_restoreInspectionRequired && !_unsafeActionsInvalidated && Permits(_lastObservedSnapshot, SidetalkCommandAction.Stop);
        CancelActionButton.Visibility = _actionInFlight && !_operationInFlight && !_closed ? Visibility.Visible : Visibility.Collapsed;
        CancelActionButton.IsEnabled = _actionInFlight && !_operationInFlight && !_intentCancelled && !_closed;
        var busy = commandBusy || _inspectionInFlight || _restoreInspectionRequired;
        InspectButton.IsEnabled = !commandBusy && !_manualRefreshInFlight;
        ExportSetupButton.IsEnabled = !busy;
        ExportDiagnosticsButton.IsEnabled = !busy;
        PrepareRecoveryButton.IsEnabled = !busy && !_recoveryActionsInvalidated && _recoveryPlan is { CanApply: true };
        var residualState = GetProcessState(_lastSnapshot?.AdminRights) == BoxProcessState.ResidualOnly &&
            _lastSnapshot?.AdminRights?.Condition is AdminRightsCondition.MissingOrEmpty or AdminRightsCondition.Disabled;
        var residualApprovalAvailable = _recoveryPlan?.ResidualCleanupApproval is not null;
        ResidualCleanupButton.Visibility = residualState ? Visibility.Visible : Visibility.Collapsed;
        ResidualCleanupButton.IsEnabled = !busy && !_recoveryActionsInvalidated && residualApprovalAvailable;
        ResidualCleanupButton.ToolTip = residualApprovalAvailable
            ? "확인된 SIDETALK 전용 box의 잔여 프로세스만 정리한 뒤 같은 복구 범위를 다시 확인합니다."
            : "소유권·저장 경로·데이터 보존 정책·명령 상태를 새로 검증할 수 없어 잔여 프로세스 정리를 차단했습니다.";
        AutomaticRecoveryCheckBox.IsEnabled = !busy && !_recoveryActionsInvalidated && _recoveryPlan is { RequiresApproval: true } && !IsAdminRightsRepair(_recoveryPlan);
        KakaoUpdateGuideButton.IsEnabled = !commandBusy;
        RevokeAutomaticButton.IsEnabled = !busy && !_recoveryActionsInvalidated && _recoveryPlan is { AllowAutomatic: true };
        OpenRecoveryHistoryButton.IsEnabled = !busy;
        OpenEvidenceHistoryButton.IsEnabled = !busy && _evidenceInspection is { Items.Count: > 0 };
        PreserveEvidenceButton.IsEnabled = !busy && _evidenceInspection is { CanPreserve: true };
        // Routine reads keep convenience controls visually stable. Their handlers
        // reserve the request and drain the current inspection before doing work.
        ApplyQuickSettingsAvailability(commandBusy || _restoreInspectionRequired);
        ApplySandboxieSetupAvailability();
        if (_restoreInspectionRequired && !_closed && !_isRenderMode && !_windowMinimized &&
            !_inspectionInFlight && !_operationInFlight && !_actionInFlight && !_restoreRefreshQueued)
        {
            _restoreRefreshQueued = true;
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                _restoreRefreshQueued = false;
                if (_restoreInspectionRequired && !_closed && !_windowMinimized)
                    await InspectAndApplyAsync(allowAutomaticRecovery: true);
            }), DispatcherPriority.Background);
        }
    }

    private void UpdateRefreshSummary()
    {
        if (_closed || _windowMinimized) return;
        var summary = _actionFeedback ?? (_mainInspectionFailure is not null
            ? (_lastObservedSnapshot is null ? "현재 상태 확인 실패 · 마지막 확인 없음. " : "현재 상태 확인 실패 · 마지막 확인 결과입니다. ") + _mainInspectionFailure
            : _recoveryFeedback ?? (_evidenceInspectionFailure is not null
                ? "중단 흔적 확인 실패 · " + _evidenceInspectionFailure
                : _lastObservedSnapshot is null ? "환경 확인 중" : string.Empty));
        SetText(RefreshSummaryText, summary);
    }

    private static void ApplyDependency(TextBlock availability, TextBlock info, DependencyInfo dependency)
    {
        availability.Text = dependency.Available && dependency.Condition is not (DependencyCondition.UnsupportedVersion or DependencyCondition.ServiceUnavailable)
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

    private static string DependencySummary(DependencyInfo dependency) => dependency.Condition switch
    {
        DependencyCondition.UnsupportedVersion => $"{dependency.Version} · 지원 버전 아님",
        DependencyCondition.ServiceUnavailable => $"{dependency.Version} · 서비스 확인 필요",
        DependencyCondition.SignatureInvalid => "서명 확인 실패",
        DependencyCondition.PathUnresolved => "설치 위치 확인 필요",
        _ => dependency.Available
            ? string.IsNullOrWhiteSpace(dependency.Version) ? "확인됨" : $"{dependency.Version} · 확인됨"
            : string.IsNullOrWhiteSpace(dependency.Path) ? "설치 필요" : "확인 필요"
    };

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
