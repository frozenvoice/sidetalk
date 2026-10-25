using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sidetalk.Core;

public sealed partial class SidetalkController : ISidetalkController, IQuickLaunchController
{
    private readonly ProfileStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ISidetalkCommandRunner commandRunner;
    private readonly ISidetalkRuntime runtime;
    private readonly ICommandProcessRecovery recovery;
    private readonly CommandJournal journal;
    private readonly CommandLifecycle commandLifecycle = new();
    private readonly TimeSpan commandTimeout;
    private readonly bool readOnly;
    private PendingCommandRecord? pendingRecord;
    private volatile bool disposed;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public SidetalkController(bool readOnly = false) : this(new ProfileStore(), new ProcessCommandRunner(), new SidetalkRuntime(), new ProcessCommandRecovery(), readOnly: readOnly, setupEnvironment: new WindowsSetupRecoveryEnvironment()) { }
    public SidetalkController(ProfileStore store) : this(store, new ProcessCommandRunner()) { }
    public SidetalkController(ProfileStore store, ISidetalkCommandRunner commandRunner)
        : this(store, commandRunner, new SidetalkRuntime(), new ProcessCommandRecovery(), setupEnvironment: new WindowsSetupRecoveryEnvironment()) { }
    public SidetalkController(ProfileStore store, ISidetalkCommandRunner commandRunner, ISidetalkRuntime runtime,
        ICommandProcessRecovery recovery, TimeSpan? commandTimeout = null, bool readOnly = false, ISetupRecoveryEnvironment? setupEnvironment = null)
    {
        this.store = store;
        this.commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        this.commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(12);
        this.readOnly = readOnly;
        journal = new CommandJournal(store.Root);
        setupRecovery = setupEnvironment is null ? null : new SetupRecovery(store, setupEnvironment);
    }

    public async Task<AppSnapshot> InspectAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync();
        try { return await Task.Run(() => Inspect()); }
        finally { ReleaseGate(); }
    }
    private AppSnapshot Inspect(bool leaseHeld = false,
        Action<BoxProfile, DependencyInfo, DependencyInfo, ConfigurationLayers>? captureValidatedScope = null)
    {
        var kakao = new DependencyInfo("공식 카카오톡", null, "", "미확인", false);
        var sandboxie = new DependencyInfo("Sandboxie Plus", null, "", "미확인", false);
        BoxProfile? profile = null;
        RuntimeVerifier.ServiceVerification? serviceIdentity = null;
        var currentConfigurationVerified = false;
        var processPolicy = ProcessPolicyStatus.NotObserved;
        InspectionFailure? inspectionFailure = null;
        ConfigurationLayers? verifiedConfiguration = null;
        AdminRightsObservation? adminRights = null;
        bool? commandPending = pendingRecord is not null || commandLifecycle.IsPending ? true : null;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var lease = leaseHeld ? null : journal.TryAcquireInspectionLease(recoverMissingLock: !readOnly);
            kakao = runtime.Kakao();
            sandboxie = runtime.Sandboxie();
            profile = store.Load();
            SecondaryAppearanceStore.RequireResolved(store);
            if (setupRecovery?.HasUnfinishedRecovery == true)
                return Blocked("설정 복구 기록을 확인해주세요", "이전 설정 준비가 완료되지 않았습니다. 초기 설정 · 진단에서 보존된 복구 내역과 현재 상태를 확인하세요.");
            try { runtime.RequireHost(); if (runtime is SidetalkRuntime) HostProfileContext.RequireUnredirected(store); }
            catch (Exception ex) { inspectionFailure = InspectionFailure.Capture("실행 환경 조회", ex); return Blocked("지원 실행 환경을 확인해주세요", "일반 사용자 실행 환경을 확인하지 못했습니다. 시작 메뉴의 SIDETALK로 열고 다시 확인해 주세요."); }
            if (!kakao.Available || !sandboxie.Available)
                return Snapshot(Readiness.MissingDependency, "실행 준비가 필요해요", !sandboxie.Available ? sandboxie.Condition switch
                {
                    DependencyCondition.NotInstalled => $"Sandboxie Plus가 설치되어 있지 않습니다. 아래 안내에서 지원 버전 {SandboxieSupport.PlusVersion}을 설치하세요.",
                    DependencyCondition.SignatureInvalid => "설치된 Sandboxie Plus의 공식 서명을 확인하지 못해 실행을 막았습니다. 기존 설치는 바꾸지 않습니다.",
                    DependencyCondition.PathUnresolved => "Sandboxie 설치 흔적은 있지만 설치 위치를 확인하지 못했습니다. 기존 설치를 덮어쓰지 않습니다.",
                    _ => "Sandboxie Plus를 확인한 뒤 전용 환경을 설정하세요."
                } : "공식 카카오톡의 설치 경로와 서명을 확인하세요.");
            if (!RuntimeVerifier.MatchesVersion(sandboxie.Version, RuntimeVerifier.PlusVersion))
                return Blocked("Sandboxie 버전 검토가 필요해요", $"이 빌드는 Sandboxie Plus {SandboxieSupport.PlusVersion}의 API·설정을 기준으로 작성되었습니다. 설치된 버전을 자동으로 바꾸지 않습니다.");
            if (sandboxie.Condition == DependencyCondition.ServiceUnavailable)
                return Blocked("Sandboxie 서비스를 확인해주세요", sandboxie.Detail + " SIDETALK는 서비스를 시작하거나 재시작하지 않습니다. 설치 직후라면 Windows를 다시 시작한 뒤 다시 확인하세요.");
            if (profile is null)
                return Snapshot(Readiness.SetupRequired, "전용 환경을 준비하세요", "초기 설정 · 진단에서 변경 내용을 확인하고 복구하고 실행 준비를 누르세요.");
            using var api = runtime.Open(sandboxie);
            serviceIdentity = api.ServiceIdentity;
            var config = api.ReadConfiguration(profile.BoxName);
            if (config.ExplicitBox.Count == 0)
                return Snapshot(Readiness.SetupRequired, "설정 적용을 기다리고 있어요", "초기 설정 · 진단에서 필요한 전용 환경 설정과 복구 가능 여부를 확인하세요.");
            var policy = IsolationPolicy.Validate(profile, config);
            if (!policy.Owned)
                return Blocked("격리 설정을 확인해주세요", "안전을 확인할 수 없어 실행과 종료를 차단했습니다. 초기 설정 · 진단에서 확인된 복구 계획과 차단 이유를 확인하세요.", policy.Issues);
            if (!SandboxieApi.SameNativePath(api.FileRoot(profile.BoxName), profile.StoragePath))
                return Blocked("저장 경로가 일치하지 않아요", "Sandboxie가 실제 사용하는 저장 경로와 전용 환경 경로가 다릅니다.");
            adminRights = AdminRightsObservation.FromConfiguration(config);
            if (!policy.Safe)
            {
                // Observation is not permission: an owned box can be diagnosed despite
                // a policy mismatch, without reconciling commands or enabling control.
                IReadOnlyList<BoxProcess>? observedProcesses = null;
                try
                {
                    observedProcesses = api.Processes(profile);
                    adminRights = ObserveImages(api, adminRights, observedProcesses);
                    processPolicy = observedProcesses.Count == 0 ? ProcessPolicyStatus.NoProcesses : ProcessPolicyStatus.EffectiveRulesUnverified;
                }
                catch (Exception error)
                {
                    adminRights = adminRights with { ProcessesQueried = false, ProcessIds = [],
                        ProcessFailure = InspectionFailure.Sanitize(error.Message), Images = [] };
                }
                var notes = policy.Issues.ToList();
                try
                {
                    var start = Path.Combine(Path.GetDirectoryName(sandboxie.Path!)!, "Start.exe");
                    var hasPendingRecord = journal.ReadPending(profile, start) is not null;
                    // A diagnostic read can prove an intent exists, but absence is
                    // not the validated command reconciliation required for false.
                    commandPending = hasPendingRecord || commandLifecycle.IsPending || pendingRecord is not null ? true : null;
                    notes.Add(hasPendingRecord ? "읽기 전용 명령 기록 조회: 미완료 의도 있음"
                        : "읽기 전용 명령 기록 조회: 미완료 의도 없음 · 명령 종료 재조정은 수행하지 않음");
                }
                catch (Exception error)
                {
                    commandPending = null;
                    notes.Add("미완료 명령 기록 확인 실패: " + InspectionFailure.Sanitize(error.Message));
                }
                var changed = adminRights.Condition is AdminRightsCondition.MissingOrEmpty or AdminRightsCondition.Disabled;
                var title = changed ? "보조 환경의 관리자 권한 제한이 변경되었습니다" : "격리 설정을 확인해주세요";
                var detail = changed
                    ? "현재 전용 박스의 DropAdminRights가 필수 값 y와 다릅니다. 카카오톡 업데이트 중 권한 허용을 선택하면 이 설정이 바뀔 수 있습니다. 사용자가 승인한 창이나 실제 관리자 토큰·업데이트 완료 여부는 확인하지 않았습니다. 새 실행과 강제 종료는 차단합니다."
                    : "안전을 확인할 수 없어 실행과 종료를 차단했습니다. 초기 설정 · 진단에서 확인된 복구 계획과 차단 이유를 확인하세요.";
                var processDetail = !adminRights.ProcessesQueried
                    ? " 프로세스 상태를 확인하지 못했습니다. 빈 상자로 간주하지 않습니다."
                    : adminRights.ProcessState switch
                    {
                        BoxProcessState.KakaoRunning => " 보조 카카오톡을 트레이 메뉴에서 정상 종료한 뒤 다시 확인하세요. 로그아웃이나 계정 삭제는 필요하지 않습니다. 현재 설정 변경은 기존 프로세스에 소급 적용됐다고 판단하지 않습니다.",
                        BoxProcessState.ResidualOnly => $" 보조 카카오톡은 종료됐지만 전용 환경에 잔여 프로세스 {adminRights.ResidualProcessCount}개가 있습니다. 일반 종료 권한을 부여하지 않고, 이름과 PID를 확인한 뒤 별도 잔여 정리 승인을 요구합니다.",
                        BoxProcessState.Unknown => " 전용 환경 프로세스의 신원을 확인하지 못했습니다. 빈 상자로 간주하지 않으며 정리를 차단합니다.",
                        _ => " 최신 조회에서 전용 박스가 빈 것을 확인했습니다. 초기 설정 · 진단에서 필요한 변경 계획을 확인하세요."
                    };
                detail += processDetail;
                return Snapshot(Readiness.Blocked, title, detail, notes, observedProcesses);
            }
            currentConfigurationVerified = true;
            verifiedConfiguration = config;
            var startExe = Path.Combine(Path.GetDirectoryName(sandboxie.Path!)!, "Start.exe");
            RecoverPending(profile, startExe);
            var (lifecycle, processes) = commandLifecycle.InspectProcesses(() => api.Processes(profile));
            adminRights = ObserveImages(api, adminRights, processes);
            commandPending = lifecycle.IsPending || pendingRecord is not null;
            var checks = new[] {
                "현재 전용 환경 소유 정보와 저장 경로 일치",
                "현재 설정의 사용자 파일·레지스트리 숨김 규칙 확인",
                "현재 설정의 자동 삭제·복구 비활성 확인",
                "현재 전역 설정·템플릿에 미검토 예외 없음",
                serviceIdentity.LiveImagePathVerified ? "서비스 실행 이미지 경로 확인" : "서비스 SCM·서명 검증 (실행 이미지 경로 직접 확인 불가)",
                "프로세스 시작 당시 적용 규칙·실제 데이터 분리·로그인·송수신·알림은 별도 확인 필요"
            };
            if (lifecycle.IsPending)
                return Snapshot(Readiness.Unknown, "이전 명령의 완료를 확인하고 있어요", lifecycle.Message, checks, processes);

            // Only a confirmed command exit followed by a fresh owned-box observation can
            // close a durable operation. Utility render/diagnose never writes journal state.
            if (pendingRecord is not null && !readOnly && !disposed)
            {
                journal.Complete(pendingRecord);
                pendingRecord = null;
            }
            commandPending = pendingRecord is not null || commandLifecycle.IsPending;
            if (adminRights.ProcessState == BoxProcessState.KakaoRunning)
            {
                processPolicy = ProcessPolicyStatus.EffectiveRulesUnverified;
                var detail = "현재 설정은 검증됐지만 실행 중 프로세스의 시작 당시 적용 규칙은 직접 확인할 수 없습니다. 설정을 다시 불러와도 기존 프로세스의 규칙이 바뀌었다고 판단하지 않습니다. 외부에서 시작했거나 실행 중 설정을 바꿨다면 보조 앱을 정상 종료해 상자가 빈 것을 확인한 뒤 새로 실행하세요.";
                if (lifecycle.Action is not null) detail = lifecycle.Message + " " + detail;
                return Snapshot(Readiness.RunningUnverified, "실행 중 · 적용 정책 미확인",
                    detail, checks, processes, canStop: true);
            }
            if (adminRights.ProcessState == BoxProcessState.ResidualOnly)
            {
                processPolicy = ProcessPolicyStatus.EffectiveRulesUnverified;
                return Snapshot(Readiness.RunningUnverified, "전용 환경에 잔여 프로세스가 있어요",
                    $"보조 카카오톡은 조회 시점에 없지만 전용 환경에 잔여 프로세스 {adminRights.ResidualProcessCount}개가 있습니다. 현재 격리 정책은 검증됐으며 기존 전용 환경 종료 경로를 사용할 수 있습니다.",
                    checks, processes, canStop: true);
            }
            if (adminRights.ProcessState == BoxProcessState.Unknown)
            {
                processPolicy = ProcessPolicyStatus.EffectiveRulesUnverified;
                return Snapshot(Readiness.Unknown, "전용 환경 프로세스 신원을 확인할 수 없어요",
                    "프로세스 신원이 확인되지 않아 빈 상자로 판단하거나 정리하지 않습니다. 상태를 다시 확인하세요.",
                    checks, processes);
            }
            processPolicy = ProcessPolicyStatus.NoProcesses;
            // Current policy, ownership and command evidence determine execution.
            // Optional recurrence prevention and shared-INI repair permissions are
            // inspected separately; unfinished mutation evidence is checked above.
            return Snapshot(Readiness.Ready,
                lifecycle.Outcome == CommandLifecycleOutcome.LaunchFailed ? "보조 카카오톡을 실행하지 못했어요" : "보조 카카오톡 실행 준비",
                lifecycle.Action is not null ? lifecycle.Message : "현재 구성과 빈 상자를 확인했습니다. 새 실행 전 설정을 다시 검사합니다.",
                checks, [], canLaunch: true);
        }
        catch (Exception ex)
        {
            inspectionFailure = InspectionFailure.Capture("상태 조회", ex);
            return Snapshot(Readiness.Unknown, "상태 확인 실패", ex is InvalidDataException
                ? "현재 상태를 확인하지 못했습니다: " + InspectionFailure.Sanitize(ex.Message)
                : "현재 실행 상태를 확인하지 못했습니다. 상태 새로 확인으로 다시 시도하거나 진단을 저장해 주세요.");
        }

        AppSnapshot Blocked(string title, string detail, IReadOnlyList<string>? issues = null)
        {
            return Snapshot(Readiness.Blocked, title, detail, issues);
        }
        AppSnapshot Snapshot(Readiness state, string title, string detail, IReadOnlyList<string>? checks = null,
            IReadOnlyList<BoxProcess>? processes = null, bool canLaunch = false, bool canStop = false)
        {
            var snapshot = new AppSnapshot(state, title, detail, profile?.BoxName ?? "아직 생성하지 않음", profile?.StoragePath ?? Path.Combine(store.Root, "Boxes"), kakao, sandboxie,
                processes?.Select(p => p.Id).ToArray() ?? [], checks ?? [], canLaunch, canStop,
                DateTimeOffset.Now, serviceIdentity, currentConfigurationVerified, processPolicy, !readOnly && !disposed)
            { InspectionFailure = inspectionFailure, CommandPending = commandPending, AdminRights = adminRights };
            if (captureValidatedScope is not null && profile is not null && verifiedConfiguration is not null &&
                currentConfigurationVerified && inspectionFailure is null && commandPending == false &&
                (canLaunch || canStop))
                captureValidatedScope(profile, kakao, sandboxie, verifiedConfiguration);
            return snapshot;
        }

        AdminRightsObservation ObserveImages(ISidetalkRuntimeSession session, AdminRightsObservation observation, IReadOnlyList<BoxProcess> processes)
        {
            var boxProcesses = AdminRightsObservation.FromProcesses(profile!, processes);
            IReadOnlyList<KakaoImageEvidence> images;
            try { images = session.ReadKakaoImageEvidence(profile!, kakao, processes); }
            catch (Exception error)
            {
                images = processes.Where(AdminRightsObservation.IsKakao).Select(process => new KakaoImageEvidence(
                    process.Id, process.ImageName, null, null, ProgramPhysicalResolution.Unknown, null, null,
                    InspectionFailure.Sanitize(error.Message))).ToArray();
            }
            return observation with
            {
                ProcessesQueried = true,
                ProcessIds = processes.Select(process => process.Id).ToArray(),
                BoxProcesses = boxProcesses,
                Images = images
            };

        }
    }

    private void RecoverPending(BoxProfile profile, string startExe)
    {
        var saved = journal.ReadPending(profile, startExe);
        if (saved is null && pendingRecord is not null && !commandLifecycle.IsPending && journal.IsCompleted(pendingRecord))
            pendingRecord = null;
        if (pendingRecord is not null && (saved is null || saved.OperationId != pendingRecord.OperationId ||
            saved.Action != pendingRecord.Action || saved.Profile != pendingRecord.Profile ||
            saved.Schema != pendingRecord.Schema || saved.RecoveryContext != pendingRecord.RecoveryContext ||
            saved.RecoveryObservationFingerprint != pendingRecord.RecoveryObservationFingerprint ||
            saved.StartExe != pendingRecord.StartExe || saved.RequestedAtUtc != pendingRecord.RequestedAtUtc ||
            (pendingRecord.Process is not null && saved.Process != pendingRecord.Process)))
            throw new InvalidDataException("명령 기록이 확인 중 변경됐습니다. 새 명령을 차단했습니다.");
        if (saved is null) return;
        commandLifecycle.RetryUncertainRecovery();
        if (!commandLifecycle.IsPending)
        {
            var handle = recovery.Recover(saved);
            commandLifecycle.Begin(saved.Action, handle);
        }
        pendingRecord = saved;
    }
    public async Task<string> ExportSetupAsync(string folder)
    {
        RequireWritable();
        await gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                if (runtime is SidetalkRuntime) HostProfileContext.RequireUnredirected(store);
                ProfileStore.RejectReparseAncestors(folder);
                var profile = store.Create();
                var destination = Path.Combine(Path.GetFullPath(folder), $"{profile.BoxName}-{DateTime.Now:yyyyMMdd-HHmmss}.ini");
                Directory.CreateDirectory(folder);
                using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream, new UTF8Encoding(true));
                writer.Write(IsolationPolicy.ToIni(profile));
                return destination;
            });
        }
        finally { ReleaseGate(); }
    }
    public Task LaunchAsync() => ExecuteAsync(true);
    public Task StopAsync() => ExecuteAsync(false);
    private async Task<AppSnapshot> ExecuteAsync(bool launch, QuickLaunchExecution? quickLaunch = null,
        CancellationToken cancellationToken = default)
    {
        RequireWritable();
        await gate.WaitAsync(cancellationToken);
        PendingCommandRecord? attemptedCommand = null;
        IDisposable? commandLease = null;
        var stage = "실행 권한 확인";
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var profile = store.Load() ?? throw new InvalidOperationException("전용 환경 정보가 없습니다.");
            commandLease = journal.AcquireLease();
            var initial = await Task.Run(() => InspectForExecution(quickLaunch));
            var snapshot = initial.Snapshot;
            cancellationToken.ThrowIfCancellationRequested();
            if (quickLaunch is not null) RequireQuickLaunchReadiness(snapshot);
            if (!(launch ? snapshot.CanLaunch : snapshot.CanStop)) throw new InvalidOperationException(snapshot.Detail);
            cancellationToken.ThrowIfCancellationRequested();
            var startExe = Path.Combine(Path.GetDirectoryName(snapshot.Sandboxie.Path!)!, "Start.exe");
            runtime.VerifyCommandFiles(startExe, snapshot.Kakao.Path!);
            cancellationToken.ThrowIfCancellationRequested();
            var final = await Task.Run(() => InspectForExecution(quickLaunch));
            var verified = final.Snapshot;
            cancellationToken.ThrowIfCancellationRequested();
            if (quickLaunch is not null) RequireQuickLaunchReadiness(verified);
            if (!(launch ? verified.CanLaunch : verified.CanStop)) throw new InvalidOperationException(verified.Detail);
            if (store.Load() != profile || verified.BoxName != profile.BoxName || verified.StoragePath != profile.StoragePath ||
                verified.Kakao.Path != snapshot.Kakao.Path || verified.Sandboxie.Path != snapshot.Sandboxie.Path)
                throw new InvalidDataException("실행 준비 중 프로필 또는 설치 경로가 바뀌었습니다.");
            if (quickLaunch is not null) RequireQuickLaunchPermission(quickLaunch, verified, final.Approval);
            var action = launch ? SidetalkCommandAction.Launch : SidetalkCommandAction.Stop;
            var command = launch ? SandboxieCommands.Launch(startExe, profile.BoxName, snapshot.Kakao.Path!) : SandboxieCommands.Stop(startExe, profile.BoxName);

            // The durable intent exists before ANY process creation. A crash at this point
            // leaves a recoverable uncertain operation, never an apparently idle launcher.
            // Cancellation is honoured only before durable intent. Once intent is
            // published, retain the handle and wait/reconcile exactly like GUI launch.
            cancellationToken.ThrowIfCancellationRequested();
            pendingRecord = journal.Prepare(profile, action, startExe);
            attemptedCommand = pendingRecord;
            if (quickLaunch is not null)
            {
                quickLaunch.IntentPrepared = true;
                quickLaunch.LastSnapshot = verified with
                {
                    State = Readiness.Unknown,
                    Title = "명령 완료 확인 필요",
                    Detail = "기록된 명령의 종료와 전용 환경 상태를 함께 확인하고 있습니다.",
                    CanLaunch = false,
                    CanStop = false,
                    CommandPending = true
                };
            }
            stage = "명령 프로세스 생성";
            var handle = commandRunner.Start(command);
            commandLifecycle.Begin(action, handle);
            stage = "명령 PID·생성 시각 확인";
            var identity = handle.Identity ?? throw new InvalidOperationException("시작한 명령의 PID·생성 시각을 기록하지 못했습니다. 완료 확인 전 새 명령을 차단합니다.");
            stage = "명령 신원 기록 저장";
            journal.RecordStarted(pendingRecord, identity);
            pendingRecord = pendingRecord with { Process = identity };

            CommandWaitOutcome waitOutcome;
            stage = "명령 종료 확인";
            try { waitOutcome = await commandLifecycle.WaitForExitAsync(commandTimeout); }
            catch (Exception error)
            {
                throw new InvalidOperationException($"Sandboxie 명령 종료를 확인하지 못했습니다. 기록은 보존되며 재시작 후에도 완료 전 중복 실행을 막습니다. ({error.Message})", error);
            }
            if (waitOutcome != CommandWaitOutcome.Completed)
                throw new InvalidOperationException("Sandboxie 명령 응답 시간이 초과되었거나 종료 상태가 미확인입니다. 기록은 보존되며 새로고침 또는 앱 재시작 후 완료를 다시 확인합니다.");
            ObjectDisposedException.ThrowIf(disposed, this);
            stage = "전용 환경 프로세스 재조회";
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var observed = await Task.Run(() => Inspect(leaseHeld: true));
                if (quickLaunch is not null) quickLaunch.LastSnapshot = observed;
                if (observed.State is Readiness.Blocked or Readiness.MissingDependency)
                    throw new InvalidOperationException(observed.Detail);
                if (!commandLifecycle.IsPending && pendingRecord is null)
                {
                    if (commandLifecycle.LastResult.Outcome is CommandLifecycleOutcome.LaunchObserved or CommandLifecycleOutcome.StopSucceeded) return observed;
                    throw new InvalidOperationException(commandLifecycle.LastResult.Message);
                }
                await Task.Delay(500);
            }
            throw new InvalidOperationException("명령 기록을 보존했습니다. 명령 종료와 전용 상자 상태가 함께 확인될 때까지 새 명령을 차단합니다.");
        }
        catch (Exception error)
        {
            if (attemptedCommand is not null)
            {
                // The failure event is evidence only; it never closes an uncertain
                // intent and cannot replace the original failure if storage fails.
                var recordingError = journal.TryRecordFailure(pendingRecord ?? attemptedCommand, stage, error);
                if (recordingError is not null) error.Data["CommandFailureRecording"] = InspectionFailure.Capture("명령 실패 기록 저장", recordingError);
            }
            throw;
        }
        finally { try { commandLease?.Dispose(); } finally { ReleaseGate(); } }
    }

    public async Task<string> ExportDiagnosticsAsync(string path)
    {
        ProfileStore.RejectReparseAncestors(path);
        var failures = new List<InspectionFailure>();
        AppSnapshot? snapshot = null;
        SetupRecoveryPlan? preparation = null;
        SetupRecoveryEvidenceInspection? interruptedEvidence = null;
        try { snapshot = await InspectAsync(); if (snapshot.InspectionFailure is { } failure) failures.Add(failure); }
        catch (Exception error) { failures.Add(InspectionFailure.Capture("상태 조회", error)); }
        if (setupRecovery is not null)
        {
            try { preparation = await InspectSetupRecoveryAsync(); }
            catch (Exception error) { failures.Add(InspectionFailure.Capture("복구 상태 조회", error)); }
            try { interruptedEvidence = await InspectInterruptedEvidenceAsync(); }
            catch (Exception error) { failures.Add(InspectionFailure.Capture("중단 흔적 조회", error)); }
        }
        var setupReport = preparation is null ? null : new
        {
            preparation.PlanRevision, preparation.State, preparation.Summary, preparation.AllowAutomatic,
            CanCleanResidualBoxForRecovery = preparation.ResidualCleanupApproval is not null,
            Changes = preparation.Changes.Select(change => new { change.Section, change.Key, change.Description }).ToArray(),
            preparation.Blockers, preparation.BackupPath, preparation.HistoryPath
        };
        var report = new { InspectionFailures = failures, SetupRecovery = setupReport, InterruptedEvidence = interruptedEvidence, SidetalkVersion = SidetalkBuild.Version, PolicyRevision = IsolationPolicy.Revision, Windows = Environment.OSVersion.Version.ToString(), snapshot,
            Verification = new { ProcessStateIsLogin = false, RunningProcessEffectiveRulesVerified = false, AccountLogin = "미검증",
                SimultaneousAccounts = "미검증", Messaging = "미검증", Notifications = "미검증", Persistence = "미검증",
                MainAccountNonInterference = "미검증", RuntimeDataSeparation = "미검증" } };
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
        await JsonSerializer.SerializeAsync(stream, report, Json);
        return path;
    }
    private void RequireWritable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (readOnly) throw new InvalidOperationException("읽기 전용 진단에서는 실행·종료·프로필 저장을 수행하지 않습니다.");
    }
    private void ReleaseGate()
    {
        try { if (disposed) commandLifecycle.Dispose(); }
        finally { gate.Release(); }
    }
    public void Dispose()
    {
        disposed = true;
        setupCancellation.Cancel();
        // Do not synchronously wait for the WPF continuation that owns this gate.
        // Its finally block releases the handle after its last inspection/wait.
        if (!gate.Wait(0)) return;
        try { commandLifecycle.Dispose(); }
        finally { gate.Release(); }
        // The durable journal and every external process/data file remain intact.
    }
}
