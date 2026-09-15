using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sidetalk.Core;

public sealed class SidetalkController : ISidetalkController
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

    public SidetalkController(bool readOnly = false) : this(new ProfileStore(), new ProcessCommandRunner(), new SidetalkRuntime(), new ProcessCommandRecovery(), readOnly: readOnly) { }
    public SidetalkController(ProfileStore store) : this(store, new ProcessCommandRunner()) { }
    public SidetalkController(ProfileStore store, ISidetalkCommandRunner commandRunner)
        : this(store, commandRunner, new SidetalkRuntime(), new ProcessCommandRecovery()) { }
    public SidetalkController(ProfileStore store, ISidetalkCommandRunner commandRunner, ISidetalkRuntime runtime,
        ICommandProcessRecovery recovery, TimeSpan? commandTimeout = null, bool readOnly = false)
    {
        this.store = store;
        this.commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        this.commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(12);
        this.readOnly = readOnly;
        journal = new CommandJournal(store.Root);
    }

    public async Task<AppSnapshot> InspectAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync();
        try { return await Task.Run(() => Inspect()); }
        finally { ReleaseGate(); }
    }
    private AppSnapshot Inspect(bool leaseHeld = false)
    {
        var kakao = new DependencyInfo("공식 카카오톡", null, "", "미확인", false);
        var sandboxie = new DependencyInfo("Sandboxie Plus", null, "", "미확인", false);
        BoxProfile? profile = null;
        RuntimeVerifier.ServiceVerification? serviceIdentity = null;
        var currentConfigurationVerified = false;
        var processPolicy = ProcessPolicyStatus.NotObserved;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var lease = leaseHeld ? null : journal.TryAcquireInspectionLease();
            kakao = runtime.Kakao();
            sandboxie = runtime.Sandboxie();
            profile = store.Load();
            try { runtime.RequireHost(); }
            catch (Exception ex) { return Blocked("지원 실행 환경을 확인해주세요", ex.Message); }
            if (!kakao.Available || !sandboxie.Available)
                return Snapshot(Readiness.MissingDependency, "실행 준비가 필요해요", !sandboxie.Available ? "Sandboxie Plus를 확인한 뒤 전용 환경을 설정하세요." : "공식 카카오톡의 설치 경로와 서명을 확인하세요.");
            if (!RuntimeVerifier.MatchesVersion(sandboxie.Version, RuntimeVerifier.PlusVersion))
                return Blocked("Sandboxie 버전 검토가 필요해요", "이 빌드는 Sandboxie Plus 1.18.4의 API·설정을 기준으로 작성되었습니다.");
            if (profile is null)
                return Snapshot(Readiness.SetupRequired, "전용 환경을 준비하세요", "초기 설정에서 설정 파일을 내보내고 내용을 확인하세요. 다른 샌드박스는 가져오지 않습니다.");
            using var api = runtime.Open(sandboxie);
            serviceIdentity = api.ServiceIdentity;
            var config = api.ReadConfiguration(profile.BoxName);
            if (config.ExplicitBox.Count == 0)
                return Snapshot(Readiness.SetupRequired, "설정 적용을 기다리고 있어요", "내보낸 전용 환경 설정을 Sandboxie Plus에 추가한 뒤 새로고침하세요.");
            var policy = IsolationPolicy.Validate(profile, config);
            if (!policy.Safe)
                return Blocked("격리 설정을 확인해주세요", "안전을 확인할 수 없어 실행과 종료를 차단했습니다. 다른 샌드박스나 전역 설정은 변경하지 마세요.", policy.Issues);
            if (!SandboxieApi.SameNativePath(api.FileRoot(profile.BoxName), profile.StoragePath))
                return Blocked("저장 경로가 일치하지 않아요", "Sandboxie가 실제 사용하는 저장 경로와 전용 환경 경로가 다릅니다.");
            currentConfigurationVerified = true;
            var startExe = Path.Combine(Path.GetDirectoryName(sandboxie.Path!)!, "Start.exe");
            RecoverPending(profile, startExe);
            var (lifecycle, processes) = commandLifecycle.InspectProcesses(() => api.Processes(profile));
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
            if (processes.Count > 0)
            {
                processPolicy = ProcessPolicyStatus.EffectiveRulesUnverified;
                var detail = "현재 설정은 검증됐지만 실행 중 프로세스의 시작 당시 적용 규칙은 직접 확인할 수 없습니다. 설정을 다시 불러와도 기존 프로세스의 규칙이 바뀌었다고 판단하지 않습니다. 외부에서 시작했거나 실행 중 설정을 바꿨다면 보조 앱을 정상 종료해 상자가 빈 것을 확인한 뒤 새로 실행하세요.";
                if (lifecycle.Action is not null) detail = lifecycle.Message + " " + detail;
                return Snapshot(Readiness.RunningUnverified, "실행 중 · 적용 정책 미확인",
                    detail, checks, processes, canStop: true);
            }
            processPolicy = ProcessPolicyStatus.NoProcesses;
            return Snapshot(Readiness.Ready,
                lifecycle.Outcome == CommandLifecycleOutcome.LaunchFailed ? "보조 카카오톡을 실행하지 못했어요" : "보조 카카오톡 실행 준비",
                lifecycle.Action is not null ? lifecycle.Message : "현재 구성과 빈 상자를 확인했습니다. 새 실행 전 설정을 다시 검사합니다.",
                checks, [], canLaunch: true);
        }
        catch (Exception ex)
        {
            return Snapshot(Readiness.Unknown, "상태를 확인할 수 없어요", ex.Message);
        }

        AppSnapshot Blocked(string title, string detail, IReadOnlyList<string>? issues = null)
        {
            return Snapshot(Readiness.Blocked, title, detail, issues);
        }
        AppSnapshot Snapshot(Readiness state, string title, string detail, IReadOnlyList<string>? checks = null,
            IReadOnlyList<BoxProcess>? processes = null, bool canLaunch = false, bool canStop = false) =>
            new(state, title, detail, profile?.BoxName ?? "아직 생성하지 않음", profile?.StoragePath ?? Path.Combine(store.Root, "Boxes"), kakao, sandboxie,
                processes?.Select(p => p.Id).ToArray() ?? [], checks ?? [], canLaunch, canStop,
                DateTimeOffset.Now, serviceIdentity, currentConfigurationVerified, processPolicy, !readOnly && !disposed);
    }

    private void RecoverPending(BoxProfile profile, string startExe)
    {
        var saved = journal.ReadPending(profile, startExe);
        if (saved is null && pendingRecord is not null && !commandLifecycle.IsPending && journal.IsCompleted(pendingRecord))
            pendingRecord = null;
        if (pendingRecord is not null && (saved is null || saved.OperationId != pendingRecord.OperationId ||
            saved.Action != pendingRecord.Action || saved.Profile != pendingRecord.Profile ||
            saved.StartExe != pendingRecord.StartExe || saved.RequestedAtUtc != pendingRecord.RequestedAtUtc ||
            (pendingRecord.Process is not null && saved.Process != pendingRecord.Process)))
            throw new InvalidDataException("명령 기록이 확인 중 변경됐습니다. 새 명령을 차단했습니다.");
        if (saved is null) return;
        if (!commandLifecycle.IsPending)
        {
            var handle = saved.Process is null ? recovery.RecoverUnidentified(startExe) : recovery.Recover(saved.Process);
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
    private async Task ExecuteAsync(bool launch)
    {
        RequireWritable();
        await gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var profile = store.Load() ?? throw new InvalidOperationException("전용 환경 정보가 없습니다.");
            using var lease = journal.AcquireLease();
            var snapshot = await Task.Run(() => Inspect(leaseHeld: true));
            if (!(launch ? snapshot.CanLaunch : snapshot.CanStop)) throw new InvalidOperationException(snapshot.Detail);
            var startExe = Path.Combine(Path.GetDirectoryName(snapshot.Sandboxie.Path!)!, "Start.exe");
            runtime.VerifyCommandFiles(startExe, snapshot.Kakao.Path!);
            var verified = await Task.Run(() => Inspect(leaseHeld: true));
            if (!(launch ? verified.CanLaunch : verified.CanStop)) throw new InvalidOperationException(verified.Detail);
            if (store.Load() != profile || verified.BoxName != profile.BoxName || verified.StoragePath != profile.StoragePath ||
                verified.Kakao.Path != snapshot.Kakao.Path || verified.Sandboxie.Path != snapshot.Sandboxie.Path)
                throw new InvalidDataException("실행 준비 중 프로필 또는 설치 경로가 바뀌었습니다.");
            var action = launch ? SidetalkCommandAction.Launch : SidetalkCommandAction.Stop;
            var command = launch ? SandboxieCommands.Launch(startExe, profile.BoxName, snapshot.Kakao.Path!) : SandboxieCommands.Stop(startExe, profile.BoxName);

            // The durable intent exists before ANY process creation. A crash at this point
            // leaves a recoverable uncertain operation, never an apparently idle launcher.
            pendingRecord = journal.Prepare(profile, action, startExe);
            var handle = commandRunner.Start(command);
            commandLifecycle.Begin(action, handle);
            var identity = handle.Identity ?? throw new InvalidOperationException("시작한 명령의 PID·생성 시각을 기록하지 못했습니다. 완료 확인 전 새 명령을 차단합니다.");
            journal.RecordStarted(pendingRecord, identity);
            pendingRecord = pendingRecord with { Process = identity };

            CommandWaitOutcome waitOutcome;
            try { waitOutcome = await commandLifecycle.WaitForExitAsync(commandTimeout); }
            catch (Exception error)
            {
                throw new InvalidOperationException($"Sandboxie 명령 종료를 확인하지 못했습니다. 기록은 보존되며 재시작 후에도 완료 전 중복 실행을 막습니다. ({error.Message})", error);
            }
            if (waitOutcome != CommandWaitOutcome.Completed)
                throw new InvalidOperationException("Sandboxie 명령 응답 시간이 초과되었거나 종료 상태가 미확인입니다. 기록은 보존되며 새로고침 또는 앱 재시작 후 완료를 다시 확인합니다.");
            ObjectDisposedException.ThrowIf(disposed, this);
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var observed = await Task.Run(() => Inspect(leaseHeld: true));
                if (observed.State is Readiness.Blocked or Readiness.MissingDependency)
                    throw new InvalidOperationException(observed.Detail);
                if (!commandLifecycle.IsPending && pendingRecord is null)
                {
                    if (commandLifecycle.LastResult.Outcome is CommandLifecycleOutcome.LaunchObserved or CommandLifecycleOutcome.StopSucceeded) return;
                    throw new InvalidOperationException(commandLifecycle.LastResult.Message);
                }
                await Task.Delay(500);
            }
            throw new InvalidOperationException("명령 기록을 보존했습니다. 명령 종료와 전용 상자 상태가 함께 확인될 때까지 새 명령을 차단합니다.");
        }
        finally { ReleaseGate(); }
    }

    public async Task<string> ExportDiagnosticsAsync(string path)
    {
        var snapshot = await InspectAsync();
        ProfileStore.RejectReparseAncestors(path);
        var report = new { SidetalkVersion = "0.1.3", PolicyRevision = IsolationPolicy.Revision, Windows = Environment.OSVersion.Version.ToString(), snapshot,
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
        // Do not synchronously wait for the WPF continuation that owns this gate.
        // Its finally block releases the handle after its last inspection/wait.
        if (!gate.Wait(0)) return;
        try { commandLifecycle.Dispose(); }
        finally { gate.Release(); }
        // The durable journal and every external process/data file remain intact.
    }
}