using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Sidetalk.Core;

public sealed partial class SidetalkController
{
    private ResidualBoxRecoveryApproval? issuedCleanupApproval;
    internal TimeSpan ResidualObservationTimeout { get; set; } = TimeSpan.FromSeconds(12);

    private ResidualBoxRecoveryApproval? TryIssueResidualCleanupApproval(SetupRecoveryPlan plan)
    {
        issuedCleanupApproval = null;
        try
        {
            var scope = CaptureResidualScope(plan, residualRequired: true);
            // Inspection may independently observe a prior cleanup's exit, but never
            // publishes completion. Explicit cleanup/repair owns that durable write.
            var pending = journal.ReadPending(scope.Profile, scope.StartExe);
            if (pending is not null && pending.Action != SidetalkCommandAction.RecoveryBoxCleanup) return null;
            RequireNoPendingCommandForSetup(writeCompletion: false);
            if (commandLifecycle.IsPending) return null;
            issuedCleanupApproval = scope;
            return scope;
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private ResidualBoxRecoveryApproval CaptureResidualScope(SetupRecoveryPlan plan, bool residualRequired)
    {
        RequireSetupHost();
        var profile = store.Load() ?? throw new InvalidDataException("전용 프로필을 확인하지 못했습니다.");
        var preparation = RequireSetupRecovery();
        if (preparation.HasUnfinishedRecovery || plan.ManagementProcesses.Count != 0 ||
            plan.IsFirstSetup || plan.IsReconciliation || plan.AllowAutomatic ||
            plan.Changes.Count != 1 || !AdminRightsRecovery.IsRepairChange(plan.Changes[0], profile))
            throw new InvalidDataException("관리 창 종료 없는 동일한 단일 관리자 권한 제한 복구 범위를 확인하지 못했습니다.");
        if (residualRequired
            ? plan.State != SetupRecoveryState.Blocked || plan.Blockers.Count != 1 || plan.Blockers[0] != SetupRecovery.ProcessesBlocker
            : !plan.CanApply || plan.Blockers.Count != 0)
            throw new InvalidDataException("현재 복구 계획에 잔여 프로세스 이외의 차단 사유가 있습니다.");
        var sandboxie = runtime.Sandboxie();
        var kakao = runtime.Kakao();
        if (!sandboxie.Available || sandboxie.Path is null || !kakao.Available || kakao.Path is null)
            throw new InvalidDataException("공식 설치·명령 경로를 확인하지 못했습니다.");
        var startExe = Path.Combine(Path.GetDirectoryName(sandboxie.Path)!, "Start.exe");
        if (!plan.Fingerprint.MatchesInstallation(Path.GetDirectoryName(sandboxie.Path)!, startExe, sandboxie.Version))
            throw new InvalidDataException("복구 계획과 현재 공식 설치 경로·버전이 다릅니다.");
        if (plan.Fingerprint.BoxName != profile.BoxName || plan.Fingerprint.UserSid != profile.UserSid ||
            !SandboxieApi.SameNativePath(plan.Fingerprint.StoragePath, profile.StoragePath))
            throw new InvalidDataException("복구 계획과 현재 프로필·사용자·저장 경로가 다릅니다.");
        runtime.VerifyCommandFiles(startExe, kakao.Path);
        using var session = runtime.Open(sandboxie);
        var configuration = session.ReadConfiguration(profile.BoxName);
        ResidualBoxRecoverySafety.RequirePolicy(profile, configuration);
        if (!plan.Fingerprint.MatchesConfiguration(configuration))
            throw new InvalidDataException("계획 조회 후 명시·상속 포함 설정이 변경됐습니다.");
        if (!SandboxieApi.SameNativePath(session.FileRoot(profile.BoxName), profile.StoragePath))
            throw new InvalidDataException("전용 환경의 실제 저장 경로를 확인하지 못했습니다.");
        var processes = session.Processes(profile).ToArray();
        ResidualBoxRecoverySafety.RequireProcesses(profile, processes, residualRequired);
        if (!residualRequired && processes.Length != 0)
            throw new InvalidOperationException("전용 환경이 다시 실행되어 복구를 차단했습니다.");
        if (store.Load() != profile) throw new InvalidDataException("조회 중 전용 프로필이 변경됐습니다.");
        return new(profile, plan, processes, startExe, kakao.Path);
    }

    private static bool SameRepairScope(ResidualBoxRecoveryApproval approved, ResidualBoxRecoveryApproval current) =>
        approved.Profile == current.Profile && approved.StartExe == current.StartExe && approved.KakaoExe == current.KakaoExe &&
        approved.RepairPlan.Fingerprint == current.RepairPlan.Fingerprint &&
        SameCleanupChanges(approved.RepairPlan.Changes, current.RepairPlan.Changes);

    private static bool SameCleanupChanges(IReadOnlyList<SetupRecoveryChange> first, IReadOnlyList<SetupRecoveryChange> second) =>
        first.Count == second.Count && first.Zip(second).All(pair => pair.First.Identity == pair.Second.Identity &&
            pair.First.Kind == pair.Second.Kind && pair.First.CurrentValues.SequenceEqual(pair.Second.CurrentValues, StringComparer.Ordinal) &&
            pair.First.DesiredValues.SequenceEqual(pair.Second.DesiredValues, StringComparer.Ordinal));

    private static bool SameCleanupProcesses(IReadOnlyList<BoxProcess> first, IReadOnlyList<BoxProcess> second) =>
        first.OrderBy(process => process.Id).SequenceEqual(second.OrderBy(process => process.Id));

    private static string CleanupObservationFingerprint(ResidualBoxRecoveryApproval scope) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope.RepairPlan.ScopeFingerprint + "\n" +
            string.Join("\n", scope.Processes.OrderBy(process => process.Id).Select(process =>
                $"{process.Id}|{process.ImageName}|{process.UserSid}|{process.SessionId}")))));

    public async Task<AppSnapshot> CleanResidualBoxAndPrepareAsync(ResidualBoxRecoveryApproval approval, IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(approval);
        RequireWritable();
        await gate.WaitAsync();
        PendingCommandRecord? attempted = null;
        IDisposable? commandLease = null;
        var stage = "잔여 정리 승인 범위 확인";
        try
        {
            if (!ReferenceEquals(issuedCleanupApproval, approval))
                throw new InvalidOperationException("이 정리 승인은 이미 사용됐거나 현재 컨트롤러가 발급하지 않았습니다. 새 상태를 확인하고 다시 승인하세요.");
            issuedCleanupApproval = null;
            RequireSetupHost();
            commandLease = journal.AcquireLease();
            RequireNoPendingCommandForSetup();
            var preparation = RequireSetupRecovery();
            var current = CaptureResidualScope(preparation.Inspect(), residualRequired: true);
            if (!SameRepairScope(approval, current) || !SameCleanupProcesses(approval.Processes, current.Processes))
                throw new InvalidDataException("승인 후 상태가 변경되어 잔여 프로세스와 설정 복구를 다시 확인해야 합니다.");
            setupCancellation.Token.ThrowIfCancellationRequested();

            // Intent is durable before process creation. It binds the observed process
            // set and repair scope; it is never an authorization to silently replay.
            pendingRecord = journal.Prepare(current.Profile, SidetalkCommandAction.RecoveryBoxCleanup,
                current.StartExe, CleanupObservationFingerprint(current));
            attempted = pendingRecord;
            stage = "정리 명령 직전 최종 안전 재검증";
            var final = CaptureResidualScope(preparation.Inspect(), residualRequired: true);
            if (!SameRepairScope(approval, final) || !SameCleanupProcesses(approval.Processes, final.Processes))
                throw new InvalidDataException("명령 실행 직전 상태가 변경되어 정리를 실행하지 않았습니다.");
            setupCancellation.Token.ThrowIfCancellationRequested();
            progress?.Report("검증된 전용 환경의 잔여 프로세스 정리 중…");
            stage = "전용 box 정리 명령 생성";
            var handle = commandRunner.Start(SandboxieCommands.Stop(final.StartExe, final.Profile.BoxName));
            commandLifecycle.Begin(SidetalkCommandAction.RecoveryBoxCleanup, handle);
            stage = "정리 명령 신원 기록";
            var identity = handle.Identity ?? throw new InvalidOperationException("정리 명령의 PID·생성 시각을 확인하지 못했습니다.");
            journal.RecordStarted(pendingRecord, identity);
            pendingRecord = pendingRecord with { Process = identity };
            stage = "정리 명령 종료 확인";
            if (await commandLifecycle.WaitForExitAsync(commandTimeout) != CommandWaitOutcome.Completed)
                throw new InvalidOperationException("정리 명령의 종료 상태를 확인하지 못했습니다. 기록을 보존하고 설정을 변경하지 않았습니다.");

            stage = "전용 환경 empty 재검증";
            progress?.Report("명령 종료 후 전용 환경이 비었는지 다시 확인 중…");
            var watch = Stopwatch.StartNew();
            IReadOnlyList<BoxProcess> remaining;
            CommandLifecycleResult result;
            while (true)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                var observedPlan = preparation.Inspect();
                // A plan can remain blocked solely by residuals while they drain.
                var observed = CaptureResidualScope(observedPlan, residualRequired: observedPlan.State == SetupRecoveryState.Blocked);
                if (!SameRepairScope(approval, observed))
                    throw new InvalidDataException("정리 후 상태가 변경되어 설정 복구를 다시 확인해야 합니다.");
                remaining = observed.Processes;
                if (remaining.Count == 0 || watch.Elapsed >= ResidualObservationTimeout) break;
                await Task.Delay(250);
            }
            result = commandLifecycle.Observe(remaining);
            if (result.IsPending) throw new InvalidOperationException(result.Message);
            if (result.Outcome != CommandLifecycleOutcome.RecoveryBoxCleanupSucceeded)
                throw new InvalidOperationException("잔여 프로세스가 종료되지 않았거나 정리 명령이 실패하여 설정 복구를 진행하지 않았습니다. " +
                    result.Message + " " + string.Join(" · ", remaining.Select(process => $"{process.ImageName} · PID {process.Id}")));
            journal.Complete(pendingRecord);
            pendingRecord = null;

            stage = "정리 후 승인된 복구 범위 재검증";
            var freshPlan = preparation.Inspect();
            var fresh = CaptureResidualScope(freshPlan, residualRequired: false);
            if (!SameRepairScope(approval, fresh))
                throw new InvalidDataException("상태가 변경되어 설정 복구를 다시 확인해야 합니다.");
            setupCancellation.Token.ThrowIfCancellationRequested();
            progress?.Report("동일한 승인 범위의 관리자 권한 제한 복구 중…");
            stage = "승인된 관리자 권한 제한 복구";
            var applied = preparation.Apply(freshPlan, allowAutomatic: false, progress, setupCancellation.Token);
            if (!applied.Succeeded) throw new InvalidOperationException(applied.Detail);
            var snapshot = Inspect(leaseHeld: true);
            if (!snapshot.CurrentConfigurationVerified || snapshot.InspectionFailure is not null)
                throw new InvalidOperationException("설정 복구 후 엄격한 격리 정책의 현재 상태를 확인하지 못했습니다.");
            return snapshot with { RecoveryResultDetail = "잔여 프로세스 종료와 빈 전용 환경, 관리자 권한 제한 복구를 확인했습니다. 기존 데이터와 기록을 보존했습니다." };
        }
        catch (Exception error)
        {
            if (attempted is not null)
            {
                var saved = pendingRecord ?? attempted;
                var recordingError = journal.TryRecordFailure(saved, stage, error);
                if (recordingError is not null) error.Data["CommandFailureRecording"] = recordingError.Message;
            }
            throw;
        }
        finally { try { commandLease?.Dispose(); } finally { ReleaseGate(); } }
    }
}
