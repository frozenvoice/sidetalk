namespace Sidetalk.Core;

public sealed partial class SidetalkController
{
    private readonly SetupRecovery? setupRecovery;
    private readonly CancellationTokenSource setupCancellation = new();
    private string? automaticRecoveryFailureScope;

    public string RecoveryHistoryPath => setupRecovery?.HistoryPath ?? Path.Combine(store.Root, "setup-recovery");

    public async Task<SetupRecoveryPlan> InspectSetupRecoveryAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync();
        try
        {
            using var lease = journal.TryAcquireInspectionLease(recoverMissingLock: false);
            return await Task.Run(() => RequireSetupRecovery().Inspect());
        }
        finally { ReleaseGate(); }
    }

    public async Task<SetupRecoveryEvidenceInspection> InspectInterruptedEvidenceAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync();
        try
        {
            using var lease = journal.TryAcquireInspectionLease(recoverMissingLock: false);
            return await Task.Run(() =>
            {
                var inspection = RequireSetupRecovery().InspectInterruptedEvidence(setupCancellation.Token);
                if (!inspection.CanPreserve) return inspection;
                try { RequireNoPendingCommandForSetup(writeCompletion: false); }
                catch (Exception error) when (error is InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    return inspection with { CanPreserve = false, Detail = inspection.Detail + " " + InspectionFailure.Sanitize(error.Message) };
                }
                return inspection;
            });
        }
        finally { ReleaseGate(); }
    }

    public async Task<AppSnapshot> ReconcileInterruptedEvidenceAsync(SetupRecoveryEvidenceInspection approvedInspection,
        IProgress<string>? progress = null)
    {
        RequireWritable();
        await gate.WaitAsync();
        try
        {
            RequireSetupHost();
            using var lease = journal.AcquireLease();
            var result = await Task.Run(() =>
            {
                RequireNoPendingCommandForSetup();
                var applied = RequireSetupRecovery().ReconcileInterruptedEvidence(approvedInspection, progress, setupCancellation.Token);
                if (!applied.Succeeded) throw new InvalidOperationException(applied.Detail);
                return applied;
            });
            return (await Task.Run(() => Inspect(leaseHeld: true))) with { RecoveryResultDetail = result.Detail };
        }
        finally { ReleaseGate(); }
    }

    public Task<AppSnapshot> PrepareAsync(SetupRecoveryPlan approvedPlan, bool allowAutomatic,
        IProgress<string>? progress = null) => PrepareCoreAsync(approvedPlan, allowAutomatic, progress);

    public Task<AppSnapshot> PrepareAutomaticallyAsync(IProgress<string>? progress = null) =>
        PrepareCoreAsync(null, false, progress);

    private async Task<AppSnapshot> PrepareCoreAsync(SetupRecoveryPlan? approvedPlan, bool allowAutomatic, IProgress<string>? progress)
    {
        RequireWritable();
        await gate.WaitAsync();
        string? attemptedScope = approvedPlan?.ScopeFingerprint;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var preparation = RequireSetupRecovery();
            if (approvedPlan is null)
            {
                var current = await Task.Run(() => preparation.Inspect(setupCancellation.Token));
                if (string.Equals(automaticRecoveryFailureScope, current.ScopeFingerprint, StringComparison.Ordinal))
                    throw new InvalidOperationException("같은 상태에서 자동 복구가 중단되었습니다. 보존된 내역을 확인하고 앱에서 다시 승인하세요.");
                attemptedScope = current.ScopeFingerprint;
                if (current.State != SetupRecoveryState.AutomaticallyApplicable || !current.AllowAutomatic)
                    return await Task.Run(() => Inspect());
            }
            // All setup writes, launch and stop share this cross-process lock. The
            // recovery engine has its own durable step journal, never a Start record.
            RequireSetupHost();
            using var lease = journal.AcquireLease();
            var result = await Task.Run(() =>
            {
                runtime.RequireHost();
                RequireNoPendingCommandForSetup();
                var applied = approvedPlan is null
                    ? preparation.TryAutomatic(progress, setupCancellation.Token)
                    : preparation.Apply(approvedPlan, allowAutomatic, progress, setupCancellation.Token);
                if (applied is not null && !applied.Succeeded)
                    throw new InvalidOperationException(applied.Detail);
                return applied;
            });
            ObjectDisposedException.ThrowIf(disposed, this);
            automaticRecoveryFailureScope = null;
            return (await Task.Run(() => Inspect(leaseHeld: true))) with { RecoveryResultDetail = result?.Detail };
        }
        catch
        {
            automaticRecoveryFailureScope = attemptedScope;
            throw;
        }
        finally { ReleaseGate(); }
    }

    private void RequireNoPendingCommandForSetup(bool writeCompletion = true)
    {
        var profile = store.Load();
        if (profile is null)
        {
            if (Directory.Exists(journal.JournalDirectory) && Directory.EnumerateFiles(journal.JournalDirectory).Any(path => !Path.GetFileName(path).Equals("command.lock", StringComparison.Ordinal)))
                throw new InvalidDataException("프로필 없이 기존 명령 기록이 있습니다. 새 환경으로 덮어쓸 수 없습니다.");
            return;
        }
        var sandboxie = runtime.Sandboxie();
        if (!sandboxie.Available || sandboxie.Path is null)
            throw new InvalidOperationException("Sandboxie 설치 확인에 실패했습니다.");
        var startExe = Path.Combine(Path.GetDirectoryName(sandboxie.Path)!, "Start.exe");
        if (journal.ReadPending(profile, startExe) is null && !commandLifecycle.IsPending) return;
        // A policy conflict must not hide a still-running Start command. Reconcile
        // its identity and freshly query the owned box before allowing repair.
        using var session = runtime.Open(sandboxie);
        var configuration = session.ReadConfiguration(profile.BoxName);
        if (!IsolationPolicy.Validate(profile, configuration).Owned ||
            !SandboxieApi.SameNativePath(session.FileRoot(profile.BoxName), profile.StoragePath))
            throw new InvalidDataException("미완료 명령의 전용 환경 소유 정보와 저장 경로를 확인하지 못했습니다.");
        RecoverPending(profile, startExe);
        var (lifecycle, _) = commandLifecycle.InspectProcesses(() => session.Processes(profile));
        if (lifecycle.IsPending)
            throw new InvalidOperationException("미완료 SIDETALK 실행·종료 명령이 있습니다. 명령 완료와 전용 환경 상태 확인 전에는 설정을 복구하지 않습니다.");
        if (pendingRecord is not null && writeCompletion)
        {
            journal.Complete(pendingRecord);
            pendingRecord = null;
        }
    }

    public async Task RevokeAutomaticRecoveryAsync()
    {
        RequireWritable();
        await gate.WaitAsync();
        try
        {
            RequireSetupHost();
            using var lease = journal.AcquireLease();
            await Task.Run(() => RequireSetupRecovery().RevokeAutomatic());
        }
        finally { ReleaseGate(); }
    }

    private void RequireSetupHost()
    {
        runtime.RequireHost();
        if (runtime is SidetalkRuntime) HostProfileContext.RequireUnredirected(store);
    }

    private SetupRecovery RequireSetupRecovery() => setupRecovery
        ?? throw new InvalidOperationException("이 실행 환경에는 설정 준비 기능이 연결되지 않았습니다.");
}
