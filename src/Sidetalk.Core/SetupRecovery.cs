using System.Text.Json;

namespace Sidetalk.Core;

/// <summary>
/// Plans and executes the narrow SIDETALK setup repair.  The service never edits a
/// whole Sandboxie INI.  Every mutation is supplied by the verified native boundary
/// as one named setting and is checked against a fresh file/API capture afterwards.
/// </summary>
internal sealed partial class SetupRecovery
{
    private const int PlanRevision = 1;
    private readonly ProfileStore store;
    private readonly ISetupRecoveryEnvironment environment;
    private readonly string recoveryRoot;
    private readonly string historyRoot;
    private readonly string operationsRoot;
    private readonly string consentRoot;
    private readonly Action<string, string>? beforePreservationRename;
    private readonly Action<string, string>? beforeEventPublish;

    public SetupRecovery(ProfileStore store, ISetupRecoveryEnvironment environment,
        Action<string, string>? beforePreservationRename = null, Action<string, string>? beforeEventPublish = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.environment = environment ?? throw new ArgumentNullException(nameof(environment));
        this.beforePreservationRename = beforePreservationRename;
        this.beforeEventPublish = beforeEventPublish;
        recoveryRoot = Path.Combine(store.Root, "setup-recovery");
        historyRoot = Path.Combine(recoveryRoot, "history");
        operationsRoot = Path.Combine(recoveryRoot, "operations");
        consentRoot = Path.Combine(recoveryRoot, "consents");
        ProfileStore.RejectReparseAncestors(store.Root);
    }

    /// <summary>Directory containing immutable operation records and backups.</summary>
    public string HistoryPath => historyRoot;
    public string RecoveryHistoryPath => historyRoot;

    /// <summary>True when a durable recovery operation is not completed.</summary>
    public bool HasUnfinishedRecovery => DiscoverInterruptedEvidence().Any(item => item.BlocksExecution) || ReadInterruptedOperation() is not null;

    public SetupRecoveryPlan Inspect(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BoxProfile? profile;
        try
        {
            profile = store.Load();
            SecondaryAppearanceStore.RequireResolved(store);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            return BlockedPlan("SIDETALK 프로필을 안전하게 읽을 수 없어 복구를 차단했습니다.", [error.Message], historyRoot);
        }

        SetupRecoveryObservation observation;
        try
        {
            observation = environment.Inspect(store, profile, cancellationToken).Normalize();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            return BlockedPlan("Sandboxie 설정을 완전하게 조회하지 못해 복구를 차단했습니다.", [error.Message], historyRoot);
        }

        var fingerprint = SetupRecoveryFingerprint.FromObservation(observation, profile);
        var blockers = new List<string>();
        var evidence = new List<string>();
        RecoveryEvent? interrupted;
        BoxProfile? savedCandidate;
        try
        {
            var interruptedFiles = DiscoverInterruptedEvidence();
            if (interruptedFiles.Any(item => item.PreservationPath is null || item.BlocksExecution))
                return BlockedPlan("중단된 파일의 원본을 보존하고 다시 확인해야 합니다.",
                    interruptedFiles.Where(item => item.PreservationPath is null || item.BlocksExecution).Select(item => item.Detail + " " + item.OriginalPath).ToArray(), historyRoot);
            interrupted = ReadInterruptedOperation();
            savedCandidate = interrupted is null ? null : LoadCandidate(interrupted.OperationId);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return BlockedPlan("복구 기록을 안전하게 읽을 수 없어 상태를 확정할 수 없습니다.", [error.Message], historyRoot);
        }
        var resumingFirstSetup = savedCandidate is not null && savedCandidate == CandidateProfile(observation) &&
            (profile is null || profile == savedCandidate);
        if (savedCandidate is not null && !resumingFirstSetup)
            blockers.Add("기존 최초 설정 기록과 현재 프로필·사용자·설정 경로가 다릅니다. 새 프로필로 대체하지 않습니다.");
        var firstSetup = profile is null || resumingFirstSetup;
        var proposedProfile = firstSetup ? savedCandidate ?? CandidateProfile(observation) : null;
        var firstSetupPolicySafe = proposedProfile is not null && IsFirstSetupCandidateSafe(observation, proposedProfile);
        if (profile is null)
        {
            if ((!resumingFirstSetup && HasPreexistingProfileEvidence(observation)) || HasFilesystemEvidence(interrupted?.OperationId))
                blockers.Add("기존 전용 환경 또는 설정 흔적이 있어 새 프로필을 자동으로 만들 수 없습니다.");
            if (interrupted is not null && savedCandidate is null)
                blockers.Add("중단된 최초 설정의 승인 후보를 확인할 수 없습니다. 기존 기록을 보존합니다.");
        }
        else if (!resumingFirstSetup)
        {
            if (!string.Equals(observation.OwnedBoxName, profile.BoxName, StringComparison.Ordinal) ||
                !string.Equals(observation.StoragePath, profile.StoragePath, StringComparison.OrdinalIgnoreCase))
                blockers.Add("SIDETALK 전용 환경의 실제 소유 표식과 저장 경로를 확인하지 못했습니다.");
        }
        if (profile is not null && observation.UserSid != profile.UserSid)
            blockers.Add("전용 환경의 Windows 사용자 범위가 현재 사용자와 다릅니다.");
        if (!observation.HostContextVerified) blockers.Add("일반 사용자 실행 컨텍스트를 확인하지 못했습니다.");
        if (!observation.Install.Verified) blockers.Add("검토된 Sandboxie 설치 경로·서명·버전을 확인하지 못했습니다.");
        if (!observation.OfficialTemplateBaselineVerified) blockers.Add("공식 Sandboxie 1.18.4 템플릿 기준을 확인하지 못했습니다.");
        if (firstSetup ? !firstSetupPolicySafe : !observation.PolicySafe &&
            (profile is null || !AdminRightsRecovery.CapturePermitsRepair(observation, profile)))
            blockers.Add("전용 환경에 검토되지 않은 설정이 있어 복구를 차단했습니다.");
        if (!observation.ProcessesQueried) blockers.Add("전용 환경 프로세스를 조회하지 못했습니다.");
        if (!observation.Ui.Fresh) blockers.Add("관리 UI 또는 설정 도구 때문에 공유 설정을 안전하게 변경할 수 없습니다. " + observation.Ui.Detail);
        if (observation.Ui.ForeignManagementProcesses.Count != 0)
            evidence.Add("다른 사용자/세션의 관리 프로세스: " + observation.Ui.ForeignManagementProcesses.Count + "개. 종료하지 않습니다.");
        if (observation.Ui.UnqueryableManagementProcessIds.Count != 0)
            evidence.Add("신원 조회 불가 관리 PID: " + string.Join(", ", observation.Ui.UnqueryableManagementProcessIds));
        if (observation.UnknownTemplateNames.Count != 0)
            blockers.Add("검토되지 않은 전역 템플릿: " + string.Join(", ", observation.UnknownTemplateNames));
        if (proposedProfile is not null)
        {
            evidence.Add("준비할 전용 환경: " + proposedProfile.BoxName);
            evidence.Add("전용 저장 경로: " + proposedProfile.StoragePath);
        }
        if (interrupted is not null)
            evidence.Add("완료되지 않은 복구 기록을 명시적으로 재조정해야 합니다: " + interrupted.OperationId);
        var changes = observation.ProposedChanges.Where(change => !change.Normalize().IsNoOp).ToArray();
        if (!firstSetup && profile is not null && AdminRightsRecovery.ProposedChange(profile, observation.Configuration) is { } rightsRepair &&
            !changes.Any(change => change.Identity.Equals(rightsRepair.Identity, StringComparison.OrdinalIgnoreCase)))
            changes = changes.Append(rightsRepair).ToArray();
        if (proposedProfile is not null)
            changes = changes.Concat(FirstSetupChanges(observation, proposedProfile)).ToArray();
        var duplicate = changes.GroupBy(change => change.Identity, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            blockers.Add("복구 계획에 같은 설정이 여러 번 포함되어 있습니다: " + duplicate.Key);

        foreach (var change in changes)
            evidence.Add($"{change.Section}.{change.Key}");
        var otherBoxes = observation.OtherBoxNames.Where(name => !name.StartsWith("UserSettings_", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (otherBoxes.Length != 0 && changes.Any(change => string.Equals(change.Section, "GlobalSettings", StringComparison.OrdinalIgnoreCase)))
            blockers.Add("SIDETALK 외 샌드박스가 확인되어 전역 설정 변경을 차단했습니다: " + string.Join(", ", otherBoxes));
        if (observation.BoxProcessIds.Count != 0 && changes.Length != 0)
            blockers.Add("전용 환경 프로세스가 실행 중이어서 설정 복구를 안전하게 적용할 수 없습니다.");

        foreach (var process in observation.Ui.ApprovedManagementProcesses)
            evidence.Add($"관리 UI 종료 대상: {Path.GetFileName(process.ImagePath)} ({process.ImagePath}), PID {process.ProcessId}, 세션 {process.SessionId?.ToString() ?? "확인되지 않음"}");
        if (observation.Ui.ApprovedManagementProcesses.Count != 0)
            evidence.Add("저장하지 않은 관리 설정은 사라질 수 있습니다.");
        if (blockers.Count != 0)
            return NewPlan(SetupRecoveryState.Blocked, "현재 범위를 안전하게 확인할 수 없어 복구를 차단했습니다.", changes, blockers, evidence,
                requiresApproval: false, allowAutomatic: false, observation, fingerprint, firstSetup);

        if (interrupted is not null)
            return NewPlan(SetupRecoveryState.Interrupted, "이전 복구가 완료되지 않았습니다. 현재 상태와 보존된 근거를 확인한 뒤 재조정해야 합니다.",
                changes, blockers, evidence, requiresApproval: true, allowAutomatic: false, observation, fingerprint, firstSetup, interrupted);

        bool consent;
        try
        {
            consent = HasCurrentConsent(observation, fingerprint, profile, observation.Install, changes);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return BlockedPlan("자동 복구 동의 기록을 안전하게 읽을 수 없어 복구를 차단했습니다.", [error.Message], historyRoot);
        }
        if (changes.Length == 0 && !firstSetup)
            return NewPlan(SetupRecoveryState.Healthy, "Sandboxie 전용 환경이 검증된 상태입니다.", changes, blockers, evidence,
                requiresApproval: false, allowAutomatic: consent, observation, fingerprint, false);
        var state = consent ? SetupRecoveryState.AutomaticallyApplicable : (firstSetup ? SetupRecoveryState.FirstSetup : SetupRecoveryState.ApprovalRequired);
        var hasAdminRightsRepair = !firstSetup && changes.Any(change => change.Kind == SetupRecoveryChangeKind.RepairOwnedBoxSetting &&
            change.Key.Equals(AdminRightsRecovery.Key, StringComparison.OrdinalIgnoreCase));
        var summary = hasAdminRightsRepair
            ? "보조 환경의 관리자 권한 제한이 변경되었습니다. 전용 앱을 정상 종료한 뒤 변경 계획을 새로 승인하여 제한을 복구하세요."
            : !firstSetup && changes.All(change => change.Kind == SetupRecoveryChangeKind.SetUserSetting)
            ? "현재 격리 정책이 정상이라면 실행할 수 있습니다. 현재 사용자 자동 호환성 검사 재발 방지는 선택적 준비입니다."
            : firstSetup
            ? "SIDETALK 전용 환경을 처음 준비하려면 설정 변경을 승인해야 합니다."
            : consent
                ? "승인된 범위와 동일한 Sandboxie 충돌을 자동으로 복구할 수 있습니다."
                : "Sandboxie 호환성 설정을 복구하려면 한 번의 승인이 필요합니다.";
        return NewPlan(state, summary, changes, blockers, evidence, requiresApproval: !consent, allowAutomatic: consent,
            observation, fingerprint, firstSetup);
    }

    /// <summary>
    /// Applies a plan after a fresh scope check.  <paramref name="allowAutomatic"/>
    /// means that the caller is using a previously recorded exact-scope consent; a
    /// false value is an explicit one-time approval from the current UI.
    /// </summary>
    public SetupRecoveryApplyResult Apply(SetupRecoveryPlan plan, bool allowAutomatic,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        ApplyCore(plan, allowAutomatic, requireExistingConsent: false, progress, cancellationToken);

    private SetupRecoveryApplyResult ApplyCore(SetupRecoveryPlan plan, bool grantAutomaticConsent,
        bool requireExistingConsent, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (grantAutomaticConsent && AdminRightsRecovery.ContainsRepair(plan))
            return Failed(string.Empty, "관리자 권한 제한 복구는 새 명시적 승인이 필요하며 자동 복구 동의에 포함할 수 없습니다.", null);
        if (!plan.CanApply)
            return Failed(string.Empty, "현재 복구 계획은 적용 가능한 상태가 아닙니다.", null);
        if (requireExistingConsent && !plan.AllowAutomatic)
            return Failed(string.Empty, "현재 복구 계획에 대한 자동 복구 동의가 없습니다.", null);

        var operationId = Guid.NewGuid().ToString("N");
        var journalPlanId = plan.PlanId;
        var journalScopeFingerprint = plan.ScopeFingerprint;
        SetupRecoveryObservation? original = null;
        string? backupPath = null;
        FileStream? recoveryLease = null;
        var intentPublished = false;
        var stage = "승인 범위 확인";
        SetupRecoveryApplyResult Fail(string id, string detail, string? backup)
        {
            if (intentPublished && !TryPublishFailure(id, journalPlanId, journalScopeFingerprint, stage + ": " + detail))
                detail += " 실패 기록 저장도 완료하지 못했습니다. 시작 기록과 원본 근거를 보존합니다.";
            return Failed(id, detail, backup) with { HistoryPath = historyRoot };
        }
        try
        {
            // Controller callers already hold CommandJournal's shared lease. This
            // second, setup-specific lease serializes two recovery requests while
            // keeping lock ordering command -> recovery.
            try
            {
                recoveryLease = AcquireRecoveryLease();
            }
            catch (IOException)
            {
                return Fail(operationId, "다른 SIDETALK 복구가 진행 중이어서 현재 요청을 중단했습니다.", null);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var profile = store.Load();
            original = environment.Inspect(store, profile, cancellationToken).Normalize();
            var originalFingerprint = SetupRecoveryFingerprint.FromObservation(original, profile);
            if (!string.Equals(plan.ScopeFingerprint, originalFingerprint.ScopeKey, StringComparison.Ordinal))
                return Fail(operationId, "승인 후 Sandboxie 설정이 변경되어 복구 계획을 다시 확인해야 합니다.", null);

            // Never trust a caller supplied plan or change list. Rebuild the plan from
            // the fresh native capture and require an exact semantic match.
            var freshPlan = Inspect(cancellationToken);
            if (freshPlan.State == SetupRecoveryState.Blocked ||
                (freshPlan.State == SetupRecoveryState.Interrupted && !plan.IsReconciliation) ||
                freshPlan.IsFirstSetup != plan.IsFirstSetup ||
                freshPlan.ProposedProfile != plan.ProposedProfile || freshPlan.PlanRevision != plan.PlanRevision ||
                freshPlan.ScopeFingerprint != plan.ScopeFingerprint ||
                (requireExistingConsent && (!freshPlan.AllowAutomatic || freshPlan.State != SetupRecoveryState.AutomaticallyApplicable)) ||
                freshPlan.IsReconciliation != plan.IsReconciliation ||
                !string.Equals(freshPlan.InterruptedOperationId, plan.InterruptedOperationId, StringComparison.Ordinal) ||
                !SameChanges(freshPlan.Changes, plan.Changes))
                return Fail(operationId, "복구 승인 범위가 현재 새로 조회한 상태와 다릅니다.", null);

            PublishEvent(new RecoveryEvent(operationId, 0, "Started", journalPlanId, journalScopeFingerprint, "복구 의도를 기록했습니다.", DateTimeOffset.UtcNow));
            intentPublished = true;
            stage = "설정 백업";
            progress?.Report(stage);
            backupPath = Backup(original, plan, operationId);
            PublishEvent(new RecoveryEvent(operationId, 1, "BackupCreated", journalPlanId, journalScopeFingerprint, backupPath, DateTimeOffset.UtcNow));

            if (plan.IsFirstSetup && profile is null)
            {
                if ((!plan.IsReconciliation && HasPreexistingProfileEvidence(original)) ||
                    HasFilesystemEvidence(plan.InterruptedOperationId ?? operationId, operationId))
                    return Fail(operationId, "승인 이후 기존 환경 흔적이 발견되어 새 프로필 생성을 중단했습니다.", backupPath);
                if (plan.ProposedProfile is null)
                    return Fail(operationId, "첫 설정 프로필 후보가 없어 생성하지 않았습니다.", backupPath);
                stage = "승인된 프로필 게시";
                profile = store.CreateCandidate(plan.ProposedProfile);
                original = environment.Inspect(store, profile, cancellationToken).Normalize();
                // First setup is the one place where the new box settings are derived
                // from the signed-in profile.  The profile was persisted only after the
                // user approved this plan and before any Sandboxie setting is written.
                var firstSetupChanges = original.ProposedChanges.Concat(FirstSetupChanges(original, profile)).Where(change => !change.Normalize().IsNoOp).ToArray();
                if (!SameChanges(firstSetupChanges, plan.Changes))
                    return Fail(operationId, "첫 설정 승인 범위와 현재 프로필 설정이 달라 생성 후 적용을 중단했습니다.", backupPath);
                plan = plan with
                {
                    Changes = firstSetupChanges,
                    Fingerprint = SetupRecoveryFingerprint.FromObservation(original, profile),
                    ConfigPath = original.ConfigPath,
                    BackupPath = backupPath
                };
            }

            var changes = plan.Changes.Where(change => !change.Normalize().IsNoOp).ToArray();
            if (changes.Length == 0)
            {
                var noOpFinal = environment.Inspect(store, profile, cancellationToken).Normalize();
                if (!SameProfile(profile, store.Load()) || !CaptureIsSafe(noOpFinal) || profile is null || !IsolationPolicy.Validate(profile, noOpFinal.Configuration).Safe ||
                    !OnlyApprovedSettingChanged(original, noOpFinal, Array.Empty<string>()))
                    return Fail(operationId, "변경할 설정이 없어도 최종 안전 검증을 통과하지 못했습니다.", backupPath);
                PublishEvent(new RecoveryEvent(operationId, 2, "Completed", journalPlanId, journalScopeFingerprint, "변경할 설정이 없습니다.", DateTimeOffset.UtcNow));
                if (plan.IsReconciliation) ReconcilePrevious(plan, operationId, journalPlanId, journalScopeFingerprint);
                var consentDetail = grantAutomaticConsent ? RecordOptionalConsent(plan, profile, noOpFinal, operationId, 3, journalPlanId, journalScopeFingerprint) : string.Empty;
                return new(operationId, SetupRecoveryState.Applied, "변경할 설정이 없어 검증을 완료했습니다." + consentDetail, true, backupPath, historyRoot, noOpFinal);
            }

            stage = "승인된 관리 UI 정리";
            var ui = environment.QuiesceManagementUi(original, plan, cancellationToken);
            if (!ui.SafeToProceed)
                return Fail(operationId, "호환성 관리 UI를 안전하게 정리하지 못해 복구를 중단했습니다: " + ui.Detail, backupPath);
            var afterUi = environment.Inspect(store, profile, cancellationToken).Normalize();
            if (!SameProfile(profile, store.Load()) || !CapturePermitsRepair(afterUi, profile, plan))
                return Fail(operationId, "복구 UI 정리 후 프로필 또는 설치 검증이 변해 중단했습니다.", backupPath);
            if (!IsApprovedUiTransition(original.Ui, afterUi.Ui))
                return Fail(operationId, "복구 UI 정리 후 승인되지 않은 관리 프로세스 변화가 발견되어 중단했습니다.", backupPath);
            if (!original.IniBytes.SequenceEqual(afterUi.IniBytes) || !OnlyApprovedSettingChanged(original, afterUi, Array.Empty<string>()))
                return Fail(operationId, "복구 UI 정리 과정에서 설정 의미가 변경되어 중단했습니다.", backupPath);

            // The UI transition changes the observation fingerprint. It is safe only
            // after the fresh capture proves the exact approved identities disappeared.
            var baseline = afterUi;
            var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sequence = 2;
            foreach (var change in changes)
            {
                stage = "설정 적용: " + change.Section + "." + change.Key;
                cancellationToken.ThrowIfCancellationRequested();
                var before = environment.Inspect(store, profile, cancellationToken).Normalize();
                if (!SameProfile(profile, store.Load()) || !CapturePermitsRepair(before, profile, plan))
                    return Fail(operationId, "복구 중 프로필 또는 설치 검증이 변해 중단했습니다.", backupPath);
                var beforeFingerprint = SetupRecoveryFingerprint.FromObservation(before, profile);
                if (!string.Equals(beforeFingerprint.ScopeKey, SetupRecoveryFingerprint.FromObservation(baseline, profile).ScopeKey, StringComparison.Ordinal))
                    return Fail(operationId, "복구 중 설정 경합이 발견되어 현재 상태를 보존한 채 중단했습니다.", backupPath);
                var actual = ReadValue(before, change.Section, change.Key);
                if (!actual.SequenceEqual(change.CurrentValues, StringComparer.Ordinal))
                    return Fail(operationId, $"복구 전 설정이 계획과 달라졌습니다: {change.Section}.{change.Key}", backupPath);

                PublishEvent(new RecoveryEvent(operationId, sequence++, "ChangeIntent", journalPlanId, journalScopeFingerprint,
                    change.Identity, DateTimeOffset.UtcNow));
                progress?.Report(change.Description.Length == 0 ? $"복구: {change.Section}.{change.Key}" : change.Description);
                var command = environment.ApplyChange(before, plan, change, cancellationToken);
                if (!command.CommandSucceeded)
                    return Fail(operationId, "Sandboxie 설정 변경 명령이 성공하지 못했습니다: " + command.Detail, backupPath);

                var after = environment.Inspect(store, profile, cancellationToken).Normalize();
                if (!SameProfile(profile, store.Load()) || !CapturePermitsRepair(after, profile, plan))
                    return Fail(operationId, "설정 적용 후 프로필 또는 설치 검증이 변해 중단했습니다.", backupPath);
                if (before.IniBytes.SequenceEqual(after.IniBytes))
                    return Fail(operationId, "설정 변경 명령은 완료됐지만 실제 INI가 바뀌지 않았습니다.", backupPath);
                var observed = ReadValue(after, change.Section, change.Key);
                if (!observed.SequenceEqual(change.DesiredValues, StringComparer.Ordinal))
                    return Fail(operationId, "설정 변경 명령은 완료됐지만 Sandboxie API 결과가 목표와 다릅니다.", backupPath);
                if (!OnlyApprovedSettingChanged(baseline, after, [change.Identity]) ||
                    applied.Any(identity => !changes.First(candidate => string.Equals(candidate.Identity, identity, StringComparison.OrdinalIgnoreCase)).DesiredValues
                        .SequenceEqual(ReadIdentity(after, identity), StringComparer.Ordinal)))
                    return Fail(operationId, "복구 범위 밖의 Sandboxie 설정 변경이 발견되어 중단했습니다.", backupPath);
                applied.Add(change.Identity);
                baseline = after;
                PublishEvent(new RecoveryEvent(operationId, sequence++, "ChangeVerified", journalPlanId, journalScopeFingerprint,
                    change.Identity, DateTimeOffset.UtcNow));
            }

            stage = "최종 재검사";
            progress?.Report("재검사");
            var final = environment.Inspect(store, profile, cancellationToken).Normalize();
            if (!SameProfile(profile, store.Load()) || !CaptureIsSafe(final) || profile is null || !IsolationPolicy.Validate(profile, final.Configuration).Safe)
                return Fail(operationId, "복구 후 정책·프로필·설치 검증을 통과하지 못했습니다.", backupPath);
            foreach (var change in changes)
                if (!ReadValue(final, change.Section, change.Key).SequenceEqual(change.DesiredValues, StringComparer.Ordinal))
                    return Fail(operationId, "복구 후 재검사에서 목표 설정을 확인하지 못했습니다.", backupPath);
            if (!OnlyApprovedSettingChanged(original, final, changes.Select(change => change.Identity)))
                return Fail(operationId, "복구 후 허용하지 않은 설정 의미 변경이 발견되었습니다.", backupPath);

            PublishEvent(new RecoveryEvent(operationId, sequence, "Completed", journalPlanId, journalScopeFingerprint,
                "파일과 Sandboxie API의 복구 결과를 확인했습니다.", DateTimeOffset.UtcNow));
            if (plan.IsReconciliation) ReconcilePrevious(plan, operationId, journalPlanId, journalScopeFingerprint);
            var optionalConsentDetail = grantAutomaticConsent ? RecordOptionalConsent(plan, profile, final, operationId, sequence + 1, journalPlanId, journalScopeFingerprint) : string.Empty;
            return new(operationId, SetupRecoveryState.Applied, "복구와 재검사를 완료했습니다." + optionalConsentDetail, true, backupPath, historyRoot, final);
        }
        catch (OperationCanceledException error)
        {
            return Fail(operationId, "복구가 중단되었습니다. 백업과 미완료 기록을 확인해야 합니다: " + error.Message, backupPath);
        }
        catch (Exception error)
        {
            return Fail(operationId, "복구에 실패했습니다. 현재 설정과 백업을 보존했습니다: " + error.Message, backupPath);
        }
        finally
        {
            recoveryLease?.Dispose();
        }
    }

    public SetupRecoveryApplyResult TryAutomatic(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var plan = Inspect(cancellationToken);
        if (plan.State == SetupRecoveryState.Healthy)
            return new(string.Empty, SetupRecoveryState.Healthy, "이미 검증된 상태입니다.", true, null, historyRoot, null);
        return plan.AllowAutomatic && plan.State == SetupRecoveryState.AutomaticallyApplicable
            ? ApplyCore(plan, grantAutomaticConsent: false, requireExistingConsent: true, progress, cancellationToken)
            : Failed(string.Empty, "동일한 범위의 자동 복구 동의가 없거나 현재 상태가 자동 복구 대상이 아닙니다.", null);
    }

    public SetupRecoveryApplyResult PrepareAutomatically(IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        TryAutomatic(progress, cancellationToken);

    public void RevokeAutomatic()
    {
        using var recoveryLease = AcquireRecoveryLease();
        Directory.CreateDirectory(consentRoot);
        ProfileStore.RejectReparseAncestors(consentRoot);
        var id = Guid.NewGuid().ToString("N");
        var sequence = NextConsentSequence();
        WriteImmutable(Path.Combine(consentRoot, id + ".json"), new RecoveryConsent(id, sequence, DateTimeOffset.UtcNow, false,
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty));
    }

    public void RevokeAutomaticRecovery() => RevokeAutomatic();

    private SetupRecoveryPlan NewPlan(SetupRecoveryState state, string summary, IReadOnlyList<SetupRecoveryChange> changes,
        IReadOnlyList<string> blockers, IReadOnlyList<string> evidence, bool requiresApproval, bool allowAutomatic,
        SetupRecoveryObservation observation, SetupRecoveryFingerprint fingerprint, bool firstSetup, RecoveryEvent? interrupted = null) =>
        new(Guid.NewGuid().ToString("N"), PlanRevision, state, summary, changes.ToArray(), blockers.ToArray(), evidence.ToArray(),
            requiresApproval, allowAutomatic, observation.ConfigPath, fingerprint, null, historyRoot, firstSetup, DateTimeOffset.UtcNow,
            interrupted is not null, interrupted?.OperationId, firstSetup ? CandidateProfile(observation) : null);

    private static SetupRecoveryPlan BlockedPlan(string summary, IReadOnlyList<string> blockers, string historyPath) =>
        new(Guid.NewGuid().ToString("N"), PlanRevision, SetupRecoveryState.Blocked, summary, Array.Empty<SetupRecoveryChange>(),
            blockers.ToArray(), Array.Empty<string>(), false, false, string.Empty,
            new SetupRecoveryFingerprint(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty),
            null, historyPath, false, DateTimeOffset.UtcNow);

    private FileStream AcquireRecoveryLease()
    {
        Directory.CreateDirectory(recoveryRoot);
        ProfileStore.RejectReparseAncestors(recoveryRoot);
        var path = Path.Combine(recoveryRoot, "recovery.lock");
        ProfileStore.RejectReparseAncestors(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private string Backup(SetupRecoveryObservation observation, SetupRecoveryPlan plan, string operationId)
    {
        ProfileStore.RejectReparseAncestors(observation.ConfigPath);
        Directory.CreateDirectory(Path.Combine(historyRoot, operationId));
        ProfileStore.RejectReparseAncestors(Path.Combine(historyRoot, operationId));
        var backupPath = Path.Combine(historyRoot, operationId, "sandboxie.ini.backup");
        using (var stream = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(observation.IniBytes, 0, observation.IniBytes.Length);
            stream.Flush(true);
        }
        var operationDirectory = Path.Combine(historyRoot, operationId);
        if (plan.ProposedProfile is not null)
            WriteImmutable(Path.Combine(operationDirectory, "candidate.profile.json"), plan.ProposedProfile);
        WriteImmutable(Path.Combine(operationDirectory, "plan.json"), RedactPlanForHistory(plan with { BackupPath = backupPath }));
        return backupPath;
    }

    private static SetupRecoveryPlan RedactPlanForHistory(SetupRecoveryPlan plan)
    {
        var changes = plan.Changes.Select(change => change with
        {
            CurrentValues = change.CurrentValues.Select(RedactOwnerToken).ToArray(),
            DesiredValues = change.DesiredValues.Select(RedactOwnerToken).ToArray()
        }).ToArray();
        return plan with { Changes = changes, ProposedProfile = null };
    }

    private static string RedactOwnerToken(string value) =>
        value.StartsWith("SIDETALK:", StringComparison.OrdinalIgnoreCase) ? "[redacted]" : value;

    private BoxProfile? LoadCandidate(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId)) return null;
        var path = Path.Combine(historyRoot, operationId, "candidate.profile.json");
        if (!File.Exists(path)) return null;
        ProfileStore.RejectReparseAncestors(path);
        try
        {
            var candidate = JsonSerializer.Deserialize<BoxProfile>(File.ReadAllText(path));
            if (candidate is null) throw new InvalidDataException("승인된 프로필 후보가 비어 있습니다.");
            ProfileStore.ValidateIdentity(candidate);
            return candidate;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("승인된 프로필 후보가 손상되었습니다.", error);
        }
    }
    private void RecordConsent(SetupRecoveryPlan plan, BoxProfile? profile, SetupRecoveryObservation observation)
    {
        Directory.CreateDirectory(consentRoot);
        ProfileStore.RejectReparseAncestors(consentRoot);
        var id = Guid.NewGuid().ToString("N");
        var sequence = NextConsentSequence();
        var scopeHash = ConsentScopeHash(observation, profile);
        WriteImmutable(Path.Combine(consentRoot, id + ".json"), new RecoveryConsent(id, sequence, DateTimeOffset.UtcNow, true,
            plan.PlanRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), plan.Fingerprint.ConsentKey,
            profile?.UserSid ?? string.Empty, profile?.BoxName ?? string.Empty, profile?.StoragePath ?? string.Empty,
            plan.ConfigPath, observation.Install.Version, scopeHash));
    }

    private string RecordOptionalConsent(SetupRecoveryPlan plan, BoxProfile? profile, SetupRecoveryObservation observation,
        string operationId, int sequence, string planId, string scopeFingerprint)
    {
        try
        {
            RecordConsent(plan, profile, observation);
            return string.Empty;
        }
        catch (Exception error)
        {
            var detail = " 자동 복구 동의를 저장하지 못했습니다. 검증된 설정 복구 결과를 유지합니다: " + error.Message;
            try { PublishEvent(new RecoveryEvent(operationId, sequence, "ConsentFailed", planId, scopeFingerprint, detail, DateTimeOffset.UtcNow)); }
            catch { detail += " 동의 저장 실패 기록도 게시하지 못했습니다. 원본 기록을 보존합니다."; }
            return detail;
        }
    }

    private bool HasCurrentConsent(SetupRecoveryObservation observation, SetupRecoveryFingerprint fingerprint,
        BoxProfile? profile, SetupRecoveryInstallContext install, IReadOnlyList<SetupRecoveryChange> changes)
    {
        var latest = ReadConsentRecords().OrderByDescending(consent => consent.Sequence).FirstOrDefault();
        return latest is { Granted: true } &&
            latest.Sequence > ReadPreservedConsentSequenceFloor() &&
            string.Equals(latest.ScopeFingerprint, fingerprint.ConsentKey, StringComparison.Ordinal) &&
            string.Equals(latest.UserSid, profile?.UserSid ?? fingerprint.UserSid, StringComparison.Ordinal) &&
            string.Equals(latest.BoxName, profile?.BoxName ?? fingerprint.BoxName, StringComparison.Ordinal) &&
            string.Equals(latest.StoragePath, profile?.StoragePath ?? fingerprint.StoragePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(latest.ConfigPath, fingerprint.ConfigPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(latest.InstallVersion, install.Version, StringComparison.Ordinal) &&
            string.Equals(latest.PlanRevision, PlanRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
            string.Equals(latest.ChangeHash, ConsentScopeHash(observation, profile), StringComparison.Ordinal) &&
            changes.All(change => IsConsentAllowedChange(observation, profile, change));
    }

    private long NextConsentSequence()
    {
        var records = ReadConsentRecords();
        var maximum = Math.Max(records.Count == 0 ? 0 : records.Max(consent => consent.Sequence), ReadPreservedConsentSequenceFloor());
        if (maximum == long.MaxValue)
            throw new InvalidDataException("자동 복구 동의 기록 순서가 허용 범위를 벗어났습니다.");
        return maximum + 1;
    }

    private IReadOnlyList<RecoveryConsent> ReadConsentRecords()
    {
        if (!Directory.Exists(consentRoot)) return Array.Empty<RecoveryConsent>();
        ProfileStore.RejectReparseAncestors(consentRoot);
        var records = new List<RecoveryConsent>();
        var sequences = new HashSet<long>();
        foreach (var path in Directory.EnumerateFileSystemEntries(consentRoot, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("자동 복구 동의 폴더에 알 수 없는 항목이 있습니다.");
            ProfileStore.RejectReparseAncestors(path);
            var fileId = Path.GetFileNameWithoutExtension(path);
            if (!Guid.TryParseExact(fileId, "N", out _))
                throw new InvalidDataException("자동 복구 동의 파일 이름이 유효하지 않습니다.");
            RecoveryConsent? consent;
            try { consent = JsonSerializer.Deserialize<RecoveryConsent>(File.ReadAllText(path), RecoveryJson.Options); }
            catch (JsonException error) { throw new InvalidDataException("자동 복구 동의 기록이 손상되었습니다.", error); }
            if (consent is null || !string.Equals(consent.Id, fileId, StringComparison.Ordinal) ||
                consent.Sequence <= 0 || consent.RecordedAtUtc == default || !sequences.Add(consent.Sequence))
                throw new InvalidDataException("자동 복구 동의 기록의 식별자 또는 순서가 유효하지 않습니다.");
            if (consent.Granted &&
                (string.IsNullOrWhiteSpace(consent.PlanRevision) || string.IsNullOrWhiteSpace(consent.ScopeFingerprint) ||
                 string.IsNullOrWhiteSpace(consent.UserSid) || string.IsNullOrWhiteSpace(consent.BoxName) ||
                 string.IsNullOrWhiteSpace(consent.StoragePath) || string.IsNullOrWhiteSpace(consent.ConfigPath) ||
                 string.IsNullOrWhiteSpace(consent.InstallVersion) || string.IsNullOrWhiteSpace(consent.ChangeHash)))
                throw new InvalidDataException("자동 복구 동의 범위가 완전하지 않습니다.");
            records.Add(consent);
        }
        return records;
    }

    private static string ConsentScopeHash(SetupRecoveryObservation observation, BoxProfile? profile)
    {
        var userSections = new[] { CurrentUserSection(observation) };
        var boxKeys = profile is null ? Array.Empty<string>() : IsolationPolicy.Expected(profile).Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray();
        var scope = new
        {
            Revision = "setup-recovery-scope-1",
            PolicyRevision = IsolationPolicy.Revision,
            Baseline = RecoveryTemplateRules.Fingerprint,
            OfficialTemplates = RecoveryTemplateRules.Names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
            GlobalKeys = new[] { "GlobalSettings\u001fTemplate" },
            UserSections = userSections,
            UserKeys = new[] { "SbieCtrl_AutoRunSoftCompat" },
            BoxName = profile?.BoxName ?? observation.OwnedBoxName ?? string.Empty,
            BoxKeys = boxKeys
        };
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(scope, RecoveryJson.Options))));
    }

    private static bool IsConsentAllowedChange(SetupRecoveryObservation observation, BoxProfile? profile, SetupRecoveryChange change)
    {
        var normalized = change.Normalize();
        if (string.Equals(normalized.Section, "GlobalSettings", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(normalized.Key, "Template", StringComparison.OrdinalIgnoreCase))
            return normalized.Kind == SetupRecoveryChangeKind.RemoveGlobalTemplate && normalized.DesiredValues.Count == 0 &&
                normalized.CurrentValues.Count != 0 && normalized.CurrentValues.All(RecoveryTemplateRules.IsReviewed);
        if (normalized.Kind == SetupRecoveryChangeKind.SetUserSetting &&
            normalized.Section.StartsWith("UserSettings_", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(normalized.Key, "SbieCtrl_AutoRunSoftCompat", StringComparison.OrdinalIgnoreCase))
            return normalized.DesiredValues.SequenceEqual(["n"], StringComparer.Ordinal) &&
                string.Equals(CurrentUserSection(observation), normalized.Section, StringComparison.OrdinalIgnoreCase);
        return false;
    }
    private static string CurrentUserSection(SetupRecoveryObservation observation) =>
        observation.CurrentUserSettingsSection ??
        observation.ProposedChanges.FirstOrDefault(change => change.Section.StartsWith("UserSettings_", StringComparison.OrdinalIgnoreCase))?.Section ??
        observation.ExplicitSections.Keys.FirstOrDefault(section => section.StartsWith("UserSettings_", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
    private RecoveryEvent? ReadInterruptedOperation()
    {
        if (!Directory.Exists(operationsRoot)) return null;
        ProfileStore.RejectReparseAncestors(operationsRoot);
        var operations = new Dictionary<string, List<RecoveryEvent>>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFileSystemEntries(operationsRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetExtension(path).Equals(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("복구 기록 폴더에 알 수 없는 항목이 있습니다.");
            ProfileStore.RejectReparseAncestors(path);
            var parts = Path.GetFileNameWithoutExtension(path).Split('.', 3, StringSplitOptions.None);
            if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "N", out _) ||
                !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var sequence) ||
                string.IsNullOrWhiteSpace(parts[2]))
                throw new InvalidDataException("복구 기록 파일 이름이 유효하지 않습니다.");
            RecoveryEvent? entry;
            try { entry = JsonSerializer.Deserialize<RecoveryEvent>(File.ReadAllText(path), RecoveryJson.Options); }
            catch (JsonException error) { throw new InvalidDataException("복구 기록이 손상되었습니다. 상태를 자동으로 확정할 수 없습니다.", error); }
            if (entry is null || !string.Equals(entry.OperationId, parts[0], StringComparison.Ordinal) || entry.Sequence != sequence ||
                !string.Equals(entry.Kind, parts[2], StringComparison.Ordinal))
                throw new InvalidDataException("복구 기록의 파일 이름과 내용이 일치하지 않습니다.");
            if (!string.Equals(Path.GetFileName(path), $"{entry.OperationId}.{entry.Sequence:D6}.{entry.Kind}.json", StringComparison.Ordinal) ||
                entry.RecordedAtUtc == default || !Guid.TryParseExact(entry.PlanId, "N", out _) ||
                string.IsNullOrWhiteSpace(entry.ScopeFingerprint))
                throw new InvalidDataException("복구 기록의 필수 메타데이터가 유효하지 않습니다.");
            if (!operations.TryGetValue(entry.OperationId, out var entries))
            {
                entries = [];
                operations.Add(entry.OperationId, entries);
            }
            if (entries.Any(existing => existing.Sequence == entry.Sequence))
                throw new InvalidDataException("복구 기록에 중복된 순서가 있습니다.");
            entries.Add(entry);
        }

        var states = operations.ToDictionary(pair => pair.Key, pair => ValidateOperation(pair.Value), StringComparer.Ordinal);
        foreach (var state in states.Values.Where(state => state.Reconciled is not null))
        {
            var reconciled = state.Reconciled!;
            if (string.IsNullOrWhiteSpace(reconciled.RelatedOperationId) ||
                string.Equals(reconciled.OperationId, reconciled.RelatedOperationId, StringComparison.Ordinal) ||
                !states.TryGetValue(reconciled.RelatedOperationId, out var related) ||
                related.Latest.Kind is not "Completed" and not "ConsentFailed" ||
                !string.Equals(reconciled.PlanId, related.Latest.PlanId, StringComparison.Ordinal) ||
                !string.Equals(reconciled.ScopeFingerprint, related.Latest.ScopeFingerprint, StringComparison.Ordinal))
                throw new InvalidDataException("이전 복구 기록의 재조정 참조가 유효한 완료 작업과 일치하지 않습니다.");
        }

        // Failed and partial operations remain unfinished until their evidence is reviewed.
        return states.Values.Where(state => state.Latest.Kind is not "Completed" and not "ConsentFailed" and not "Reconciled")
            .OrderBy(state => state.Started.RecordedAtUtc).ThenBy(state => state.Started.OperationId, StringComparer.Ordinal)
            .Select(state => state.Latest).FirstOrDefault();
    }

    private static RecoveryOperationState ValidateOperation(IReadOnlyList<RecoveryEvent> source)
    {
        var entries = source.OrderBy(entry => entry.Sequence).ToArray();
        if (entries.Length == 0 || entries[0].Sequence != 0 || entries[0].Kind != "Started" ||
            entries[0].RelatedOperationId is not null)
            throw new InvalidDataException("복구 기록이 유효한 시작 이벤트로 시작하지 않습니다.");
        var started = entries[0];
        var expectedSequence = 1;
        var backupCreated = false;
        string? pendingChange = null;
        var completed = false;
        var failed = false;
        RecoveryEvent? reconciled = null;
        for (var index = 1; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry.Kind == "Reconciled")
            {
                if (entry.Sequence != 1000000 || index != entries.Length - 1 || reconciled is not null ||
                    !failed && completed || string.IsNullOrWhiteSpace(entry.RelatedOperationId))
                    throw new InvalidDataException("복구 재조정 이벤트의 순서가 유효하지 않습니다.");
                reconciled = entry;
                continue;
            }

            if (!string.Equals(entry.PlanId, started.PlanId, StringComparison.Ordinal) ||
                !string.Equals(entry.ScopeFingerprint, started.ScopeFingerprint, StringComparison.Ordinal) ||
                entry.RelatedOperationId is not null)
                throw new InvalidDataException("한 복구 작업의 승인 범위 메타데이터가 일치하지 않습니다.");
            if (entry.Kind == "Failed")
            {
                if (entry.Sequence != 999999 || index != entries.Length - 1 && entries[index + 1].Kind != "Reconciled" || failed)
                    throw new InvalidDataException("복구 실패 이벤트의 순서가 유효하지 않습니다.");
                failed = true;
                continue;
            }
            if (entry.Kind == "ConsentFailed")
            {
                if (!completed || failed || entry.Sequence != expectedSequence++ || index != entries.Length - 1)
                    throw new InvalidDataException("선택적 동의 저장 실패 이벤트가 검증된 완료 뒤에 있지 않습니다.");
                continue;
            }
            if (failed || completed || entry.Sequence != expectedSequence++)
                throw new InvalidDataException("복구 기록의 연속 순서가 유효하지 않습니다.");
            switch (entry.Kind)
            {
                case "BackupCreated" when !backupCreated && entry.Sequence == 1 && pendingChange is null:
                    backupCreated = true;
                    break;
                case "ChangeIntent" when backupCreated && pendingChange is null:
                    pendingChange = entry.Detail;
                    break;
                case "ChangeVerified" when backupCreated && pendingChange is not null &&
                    string.Equals(entry.Detail, pendingChange, StringComparison.Ordinal):
                    pendingChange = null;
                    break;
                case "Completed" when backupCreated && pendingChange is null:
                    completed = true;
                    break;
                default:
                    throw new InvalidDataException("복구 기록 이벤트 체인이 유효하지 않습니다.");
            }
        }
        return new RecoveryOperationState(started, entries[^1], reconciled);
    }
    private void PublishEvent(RecoveryEvent entry)
    {
        Directory.CreateDirectory(operationsRoot);
        ProfileStore.RejectReparseAncestors(operationsRoot);
        WriteImmutable(Path.Combine(operationsRoot, $"{entry.OperationId}.{entry.Sequence:D6}.{entry.Kind}.json"), entry, beforeEventPublish);
    }

    private void ReconcilePrevious(SetupRecoveryPlan plan, string operationId, string planId, string scopeFingerprint)
    {
        if (string.IsNullOrWhiteSpace(plan.InterruptedOperationId)) return;
        PublishEvent(new RecoveryEvent(plan.InterruptedOperationId, 1000000, "Reconciled", planId, scopeFingerprint,
            "사용자가 새로 조회한 상태를 승인하여 이전 미완료 기록을 재조정했습니다.", DateTimeOffset.UtcNow, operationId));
    }

    private bool TryPublishFailure(string operationId, string planId, string scopeFingerprint, string detail)
    {
        if (string.IsNullOrWhiteSpace(operationId)) return false;
        try
        {
            PublishEvent(new RecoveryEvent(operationId, 999999, "Failed", planId, scopeFingerprint, detail, DateTimeOffset.UtcNow));
            return true;
        }
        catch { return false; /* The original failure is retained in the returned detail. */ }
    }

    private static void WriteImmutable<T>(string path, T value, Action<string, string>? beforePublish = null)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, RecoveryJson.Options);
                stream.Flush(true);
            }
            beforePublish?.Invoke(temporary, path);
            File.Move(temporary, path, overwrite: false);
        }
        catch
        {
            // Keep failed temporary records as evidence; never silently clean a failed operation.
            throw;
        }
    }

    private static SetupRecoveryApplyResult Failed(string operationId, string detail, string? backupPath) =>
        new(operationId, SetupRecoveryState.Failed, detail, false, backupPath, string.Empty);

    private bool HasFilesystemEvidence(string? allowedOperationId = null, string? currentOperationId = null)
    {
        if (string.IsNullOrWhiteSpace(allowedOperationId))
        {
            try { allowedOperationId = ReadInterruptedOperation()?.OperationId; }
            catch (InvalidDataException) { return true; }
        }

        if (!Directory.Exists(store.Root)) return false;
        if (DiscoverInterruptedEvidence().Any(item => item.BlocksExecution && item.PreservationPath is not null)) return true;
        if (Directory.EnumerateFiles(store.Root, ".profile.*.tmp", SearchOption.TopDirectoryOnly).Any()) return true;
        var boxes = Path.Combine(store.Root, "Boxes");
        if (Directory.Exists(boxes) && Directory.EnumerateFileSystemEntries(boxes).Any()) return true;
        var journal = Path.Combine(store.Root, "command-journal");
        if (Directory.Exists(journal) && Directory.EnumerateFiles(journal, "*", SearchOption.TopDirectoryOnly).Any(path => !string.Equals(Path.GetFileName(path), "command.lock", StringComparison.OrdinalIgnoreCase))) return true;
        if (!Directory.Exists(recoveryRoot)) return false;
        foreach (var path in Directory.EnumerateFiles(recoveryRoot, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(path), "recovery.lock", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(allowedOperationId) && IsOwnedRecoveryEvidence(path, allowedOperationId)) continue;
            if (!string.IsNullOrWhiteSpace(currentOperationId) && IsOwnedRecoveryEvidence(path, currentOperationId)) continue;
            return true;
        }
        return false;
    }

    private bool IsOwnedRecoveryEvidence(string path, string operationId)
    {
        var full = Path.GetFullPath(path);
        var operationPrefix = Path.Combine(operationsRoot, operationId + ".");
        var historyPrefix = Path.Combine(historyRoot, operationId) + Path.DirectorySeparatorChar;
        return full.StartsWith(operationPrefix, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(historyPrefix, StringComparison.OrdinalIgnoreCase);
    }
    private static bool HasPreexistingProfileEvidence(SetupRecoveryObservation observation)
    {
        if (!string.IsNullOrWhiteSpace(observation.OwnedBoxName) || observation.OtherBoxNames.Count != 0)
            return true;
        return observation.ExplicitSections.Keys.Any(IsBoxSection);
    }

    private static bool IsBoxSection(string name) =>
        !string.Equals(name, "GlobalSettings", StringComparison.OrdinalIgnoreCase) &&
        !name.StartsWith("UserSettings_", StringComparison.OrdinalIgnoreCase) &&
        !name.StartsWith("Template_", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(name, "DefaultTemplates", StringComparison.OrdinalIgnoreCase);

    private BoxProfile CandidateProfile(SetupRecoveryObservation observation)
    {
        var sid = observation.UserSid ?? ProfileStore.CurrentSid();
        var seed = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(store.Root + "|" + observation.ConfigPath + "|" + sid));
        var owner = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(seed)).ToLowerInvariant();
        var suffix = Convert.ToHexString(seed).ToLowerInvariant()[..23];
        var name = "SideTalk_" + suffix;
        return new BoxProfile(1, name, owner[..32], sid, Path.Combine(store.Root, "Boxes", name));
    }

    private static bool IsFirstSetupCandidateSafe(SetupRecoveryObservation observation, BoxProfile profile)
    {
        var expected = IsolationPolicy.ExpectedForRecovery(profile, observation.Configuration.ExplicitBox);
        if (!observation.OfficialTemplateBaselineVerified || observation.UnknownTemplateNames.Count != 0 ||
            !SandboxieConfigurationReader.Same(observation.Configuration.ExplicitBox, observation.Configuration.EffectiveBox)) return false;
        foreach (var section in observation.ExplicitSections)
        {
            if (IsBoxSection(section.Key) && !section.Key.Equals(profile.BoxName, StringComparison.OrdinalIgnoreCase)) return false;
            if (!section.Key.Equals(profile.BoxName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var setting in section.Value)
                if (!expected.TryGetValue(setting.Key, out var target) || setting.Value.Length > target.Length ||
                    !setting.Value.SequenceEqual(target.Take(setting.Value.Length), StringComparer.Ordinal)) return false;
        }
        foreach (var setting in observation.Configuration.ExplicitBox)
            if (!expected.TryGetValue(setting.Key, out var target) || setting.Value.Length > target.Length ||
                !setting.Value.SequenceEqual(target.Take(setting.Value.Length), StringComparer.Ordinal)) return false;
        try
        {
            var globals = observation.Configuration.ExplicitGlobals;
            if (!SandboxieConfigurationReader.Same(RecoveryTemplateRules.ExpectedGlobals(globals), observation.Configuration.EffectiveGlobals)) return false;
            var prospective = globals.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            prospective.Remove("Template");
            return IsolationPolicy.Validate(profile, new ConfigurationLayers(expected, expected, prospective,
                BuiltinTemplates.ExpectedGlobals(prospective))).Safe;
        }
        catch (InvalidDataException) { return false; }
    }
    private static IReadOnlyList<SetupRecoveryChange> FirstSetupChanges(SetupRecoveryObservation observation, BoxProfile profile)
    {
        var current = observation.ExplicitSections.TryGetValue(profile.BoxName, out var section)
            ? section
            : new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var expected = IsolationPolicy.ExpectedForRecovery(profile, current);
        return expected.Select(pair => new SetupRecoveryChange(profile.BoxName, pair.Key,
            ReadValue(current, pair.Key), pair.Value.ToArray(),
            SetupRecoveryChangeKind.AddOwnedBoxSetting, "SIDETALK 전용 환경 설정")).Where(change => !change.IsNoOp).ToArray();
    }

    private static bool SameProfile(BoxProfile? expected, BoxProfile? actual) =>
        expected is null ? actual is null : actual is not null && expected == actual;

    private static bool CaptureIsSafe(SetupRecoveryObservation observation) =>
        observation.HostContextVerified && observation.Install.Verified && observation.OfficialTemplateBaselineVerified &&
        observation.ProcessesQueried && observation.BoxProcessIds.Count == 0 && observation.Ui.Fresh && observation.Ui.ApprovedManagementProcesses.Count == 0;

    private static bool CapturePermitsRepair(SetupRecoveryObservation observation, BoxProfile? profile, SetupRecoveryPlan plan) =>
        CaptureIsSafe(observation) && profile is not null &&
        (plan.IsFirstSetup ? IsFirstSetupCandidateSafe(observation, profile) : observation.PolicySafe ||
            AdminRightsRecovery.ContainsRepair(plan) && AdminRightsRecovery.CapturePermitsRepair(observation, profile));
    private static bool IsApprovedUiTransition(SetupRecoveryUiState before, SetupRecoveryUiState after)
    {
var afterIds = after.ApprovedManagementProcesses.Select(process => process.Normalize())
            .ToDictionary(process => (process.ProcessId, process.CreationTimeUtcTicks), process => process, EqualityComparer<(int, long)>.Default);
        return afterIds.Count == 0;
    }

    private static bool SameChanges(IReadOnlyList<SetupRecoveryChange> left, IReadOnlyList<SetupRecoveryChange> right)
    {
        if (left.Count != right.Count) return false;
        return left.OrderBy(change => change.Identity, StringComparer.OrdinalIgnoreCase)
            .Zip(right.OrderBy(change => change.Identity, StringComparer.OrdinalIgnoreCase))
            .All(pair => string.Equals(pair.First.Identity, pair.Second.Identity, StringComparison.OrdinalIgnoreCase) &&
                pair.First.Kind == pair.Second.Kind &&
                pair.First.CurrentValues.SequenceEqual(pair.Second.CurrentValues, StringComparer.Ordinal) &&
                pair.First.DesiredValues.SequenceEqual(pair.Second.DesiredValues, StringComparer.Ordinal));
    }

    private static string[] ReadValue(IReadOnlyDictionary<string, string[]> values, string key) =>
        values.TryGetValue(key, out var actual) ? actual.ToArray() : Array.Empty<string>();

    private static string[] ReadValue(SetupRecoveryObservation observation, string section, string key)
    {
        if (observation.ExplicitSections.TryGetValue(section, out var full) && full.TryGetValue(key, out var sectionValue))
            return sectionValue.ToArray();
        if (string.Equals(section, observation.OwnedBoxName, StringComparison.OrdinalIgnoreCase))
            return ReadValue(observation.Configuration.ExplicitBox, key);
        if (string.Equals(section, "GlobalSettings", StringComparison.OrdinalIgnoreCase))
            return ReadValue(observation.Configuration.ExplicitGlobals, key);
        return Array.Empty<string>();
    }

    private static bool OnlyApprovedSettingChanged(SetupRecoveryObservation before, SetupRecoveryObservation after,
        IEnumerable<string> approved)
    {
        var allowed = approved.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keys = FlattenKeys(before).Union(FlattenKeys(after), StringComparer.OrdinalIgnoreCase);
        foreach (var identity in keys)
        {
            if (allowed.Contains(identity)) continue;
            var beforeValue = ReadIdentity(before, identity);
            var afterValue = ReadIdentity(after, identity);
            if (!beforeValue.SequenceEqual(afterValue, StringComparer.Ordinal)) return false;
        }

        return true;
    }

    private static IEnumerable<string> FlattenKeys(SetupRecoveryObservation observation)
    {
        foreach (var section in observation.ExplicitSections)
            foreach (var key in section.Value.Keys)
                yield return section.Key + "\u001f" + key;
        if (!string.IsNullOrWhiteSpace(observation.OwnedBoxName))
            foreach (var key in observation.Configuration.ExplicitBox.Keys)
                yield return observation.OwnedBoxName + "\u001f" + key;
        foreach (var key in observation.Configuration.ExplicitGlobals.Keys)
            yield return "GlobalSettings\u001f" + key;
    }

    private static string[] ReadIdentity(SetupRecoveryObservation observation, string identity)
    {
        var separator = identity.IndexOf('\u001f');
        return separator < 0 ? Array.Empty<string>() : ReadValue(observation, identity[..separator], identity[(separator + 1)..]);
    }

    private sealed record RecoveryEvent(string OperationId, int Sequence, string Kind, string PlanId,
        string ScopeFingerprint, string Detail, DateTimeOffset RecordedAtUtc, string? RelatedOperationId = null);

    private sealed record RecoveryOperationState(RecoveryEvent Started, RecoveryEvent Latest, RecoveryEvent? Reconciled);

    private sealed record RecoveryConsent(string Id, long Sequence, DateTimeOffset RecordedAtUtc, bool Granted,
        string PlanRevision, string ScopeFingerprint, string UserSid, string BoxName, string StoragePath,
        string ConfigPath, string InstallVersion, string ChangeHash);
}
