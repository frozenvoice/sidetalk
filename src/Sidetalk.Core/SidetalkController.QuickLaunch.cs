using System.Security.Cryptography;
using System.Text.Json;

namespace Sidetalk.Core;

public sealed partial class SidetalkController
{
    public async Task<QuickLaunchStatus> InspectQuickLaunchAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync();
        try
        {
            // Opening the quick-launch surface does not create consent, profiles,
            // shortcut registration, or an absent command journal/lock.
            using var lease = journal.TryAcquireInspectionLease(recoverMissingLock: false);
            var observed = await Task.Run(() => InspectQuickLaunchObservation());
            return QuickLaunchStatusFor(observed.Snapshot, observed.Approval);
        }
        catch (Exception error)
        {
            var snapshot = QuickLaunchUnavailable(error);
            return new(snapshot, null, false, snapshot.Detail);
        }
        finally { ReleaseGate(); }
    }

    public async Task<QuickLaunchStatus> SetQuickLaunchConsentAsync(QuickLaunchApproval approval, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(approval);
        RequireWritable();
        await gate.WaitAsync();
        try
        {
            using var lease = journal.AcquireLease();
            var before = await Task.Run(() => InspectQuickLaunchObservation());
            RequireConsentCandidate(before.Snapshot, before.Approval, approval);
            var startExe = Path.Combine(Path.GetDirectoryName(before.Snapshot.Sandboxie.Path!)!, "Start.exe");
            runtime.VerifyCommandFiles(startExe, before.Snapshot.Kakao.Path!);
            // Consent grants only the exact scope rederived after executable
            // verification, under the shared operation lease. It never repairs.
            var current = await Task.Run(() => InspectQuickLaunchObservation());
            RequireConsentCandidate(current.Snapshot, current.Approval, approval);
            new QuickLaunchConsentStore(store.Root).Append(current.Approval!, enabled);
            return QuickLaunchStatusFor(current.Snapshot, current.Approval);
        }
        finally { ReleaseGate(); }
    }

    public async Task<QuickLaunchResult> LaunchSecondaryAsync(QuickLaunchApproval? confirmedScope,
        CancellationToken cancellationToken = default)
    {
        var execution = new QuickLaunchExecution(confirmedScope);
        try
        {
            var snapshot = await ExecuteAsync(launch: true, execution, cancellationToken);
            if (snapshot.CommandPending != false || !snapshot.CurrentConfigurationVerified || snapshot.BoxProcessIds.Count == 0)
                return new(QuickLaunchOutcome.Uncertain, snapshot, "명령 완료와 전용 환경 프로세스를 함께 확인하지 못했습니다.");
            return new(QuickLaunchOutcome.Launched, snapshot, snapshot.Detail);
        }
        catch (QuickLaunchDecision decision) { return decision.Result; }
        catch (OperationCanceledException) when (!execution.IntentPrepared)
        {
            return new(QuickLaunchOutcome.Cancelled, execution.LastSnapshot ?? QuickLaunchUnavailable(null),
                "명령 의도 기록과 프로세스 실행 전에 요청을 취소했습니다.");
        }
        catch (Exception error)
        {
            var snapshot = execution.LastSnapshot ?? QuickLaunchUnavailable(error);
            var uncertain = snapshot.CommandPending == true || (execution.IntentPrepared && snapshot.CommandPending != false) ||
                (execution.LastSnapshot is null && !readOnly && !disposed);
            if (!execution.IntentPrepared)
                snapshot = snapshot with
                {
                    State = Readiness.Unknown,
                    Title = "빠른 실행 상태 확인 필요",
                    Detail = InspectionFailure.Sanitize(error.GetBaseException().Message),
                    CanLaunch = false,
                    CanStop = false,
                    ActionsPermitted = false,
                    CurrentConfigurationVerified = false,
                    CommandPending = snapshot.CommandPending == true ? true : null,
                    InspectionFailure = InspectionFailure.Capture("빠른 실행 전 검증", error)
                };
            return new(uncertain ? QuickLaunchOutcome.Uncertain : QuickLaunchOutcome.Blocked,
                snapshot, InspectionFailure.Sanitize(error.GetBaseException().Message));
        }
    }

    private (AppSnapshot Snapshot, QuickLaunchApproval? Approval) InspectForExecution(QuickLaunchExecution? execution)
    {
        if (execution is null) return (Inspect(leaseHeld: true), null);
        var observed = InspectQuickLaunchObservation();
        execution.LastSnapshot = observed.Snapshot;
        return observed;
    }

    private (AppSnapshot Snapshot, QuickLaunchApproval? Approval) InspectQuickLaunchObservation()
    {
        QuickLaunchApproval? approval = null;
        var snapshot = Inspect(leaseHeld: true, (profile, kakao, sandboxie, configuration) =>
            approval = CreateQuickLaunchApproval(profile, kakao, sandboxie, configuration));
        return (snapshot, CanApproveQuickLaunch(snapshot) ? approval : null);
    }

    private QuickLaunchStatus QuickLaunchStatusFor(AppSnapshot snapshot, QuickLaunchApproval? approval)
    {
        if (approval is null || !CanApproveQuickLaunch(snapshot))
            return new(snapshot, null, false, snapshot.Detail);
        try
        {
            var matches = new QuickLaunchConsentStore(store.Root).Matches(approval);
            return new(snapshot, approval, matches, matches
                ? "현재 전용 환경·설치·격리 설정 범위에 빠른 실행이 승인되어 있습니다."
                : "빠른 실행 동의가 없거나 확인 범위가 바뀌었습니다. 기존 실행 확인이 필요합니다.");
        }
        catch (Exception error)
        {
            return new(snapshot, approval, false,
                "빠른 실행 동의 기록을 확인하지 못해 저장된 권한을 사용하지 않습니다. 원본 기록을 보존했습니다: " +
                InspectionFailure.Sanitize(error.GetBaseException().Message));
        }
    }

    private static bool CanApproveQuickLaunch(AppSnapshot snapshot) => snapshot is
        { ActionsPermitted: true, CurrentConfigurationVerified: true, InspectionFailure: null, CommandPending: false } &&
        (snapshot.CanLaunch || snapshot.CanStop);

    private static void RequireConsentCandidate(AppSnapshot snapshot, QuickLaunchApproval? current, QuickLaunchApproval proposed)
    {
        if (!CanApproveQuickLaunch(snapshot) || current is null)
            throw new InvalidOperationException("빠른 실행 동의를 저장할 현재 상태를 확인하지 못했습니다. " + snapshot.Detail);
        if (!SameQuickLaunchScope(current, proposed))
            throw new InvalidOperationException("빠른 실행 확인 범위가 바뀌었습니다. 현재 상태를 다시 확인하고 승인하세요.");
    }

    private static bool SameQuickLaunchScope(QuickLaunchApproval first, QuickLaunchApproval second) =>
        string.Equals(first.ScopeFingerprint, second.ScopeFingerprint, StringComparison.Ordinal);

    private static void RequireQuickLaunchReadiness(AppSnapshot snapshot)
    {
        if (CanApproveQuickLaunch(snapshot) && snapshot.CanStop && !snapshot.CanLaunch && snapshot.BoxProcessIds.Count > 0 &&
            snapshot.State is Readiness.Running or Readiness.RunningUnverified)
            throw new QuickLaunchDecision(new(QuickLaunchOutcome.AlreadyRunning, snapshot,
                "검증된 SIDETALK 전용 환경에 이미 프로세스가 있습니다. 실행 명령을 추가로 보내지 않았습니다."));
        if (snapshot.CommandPending == true || snapshot.State == Readiness.Unknown)
            throw new QuickLaunchDecision(new(QuickLaunchOutcome.Uncertain, snapshot, snapshot.Detail));
        if (!CanApproveQuickLaunch(snapshot) || !snapshot.CanLaunch)
            throw new QuickLaunchDecision(new(QuickLaunchOutcome.Blocked, snapshot, snapshot.Detail));
    }

    private void RequireQuickLaunchPermission(QuickLaunchExecution execution, AppSnapshot snapshot, QuickLaunchApproval? current)
    {
        if (current is null)
            throw new QuickLaunchDecision(new(QuickLaunchOutcome.Blocked, snapshot, "현재 빠른 실행 승인 범위를 확인하지 못했습니다."));
        if (execution.ConfirmedScope is { } confirmed)
        {
            if (SameQuickLaunchScope(current, confirmed)) return;
            throw new QuickLaunchDecision(new(QuickLaunchOutcome.ConfirmationRequired, snapshot,
                "실행 직전 확인 범위가 바뀌었습니다. 현재 상태에서 기존 실행 확인을 다시 진행하세요."));
        }
        var status = QuickLaunchStatusFor(snapshot, current);
        if (!status.ConsentMatches)
            throw new QuickLaunchDecision(new(QuickLaunchOutcome.ConfirmationRequired, snapshot, status.Reason));
    }

    private AppSnapshot QuickLaunchUnavailable(Exception? error)
    {
        var detail = error is null ? "현재 상태를 아직 확인하지 않았습니다." : InspectionFailure.Sanitize(error.GetBaseException().Message);
        return new(Readiness.Unknown, "빠른 실행 상태 확인 필요", detail, "확인 필요", Path.Combine(store.Root, "Boxes"),
            new("공식 카카오톡", null, "", "미확인", false), new("Sandboxie Plus", null, "", "미확인", false),
            [], [], false, false, DateTimeOffset.Now, ActionsPermitted: false)
        { CommandPending = pendingRecord is not null || commandLifecycle.IsPending ? true : null,
            InspectionFailure = error is null ? null : InspectionFailure.Capture("빠른 실행", error) };
    }

    private static QuickLaunchApproval CreateQuickLaunchApproval(BoxProfile profile, DependencyInfo kakao,
        DependencyInfo sandboxie, ConfigurationLayers configuration)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteString("ScopeSchema", "quick-launch-scope-1");
            writer.WriteString("PolicyRevision", IsolationPolicy.Revision);
            writer.WritePropertyName("Profile");
            JsonSerializer.Serialize(writer, profile);
            WriteInstallation(writer, "Kakao", kakao);
            WriteInstallation(writer, "Sandboxie", sandboxie);
            WriteLayer(writer, "ExplicitBox", configuration.ExplicitBox);
            WriteLayer(writer, "EffectiveBox", configuration.EffectiveBox);
            WriteLayer(writer, "ExplicitGlobals", configuration.ExplicitGlobals);
            WriteLayer(writer, "EffectiveGlobals", configuration.EffectiveGlobals);
            writer.WriteEndObject();
        }
        return new(Convert.ToHexString(SHA256.HashData(bytes.ToArray())));
    }

    private static void WriteInstallation(Utf8JsonWriter writer, string name, DependencyInfo dependency)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteString("Path", Path.GetFullPath(dependency.Path ?? throw new InvalidDataException("설치 경로를 확인하지 못했습니다.")).ToUpperInvariant());
        writer.WriteString("Version", dependency.Version);
        writer.WriteEndObject();
    }

    private static void WriteLayer(Utf8JsonWriter writer, string name, IReadOnlyDictionary<string, string[]> settings)
    {
        if (settings.Keys.Select(key => key.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Count() != settings.Count)
            throw new InvalidDataException("빠른 실행 범위에 중복된 설정 이름이 있습니다.");
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        foreach (var pair in settings.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            writer.WritePropertyName(pair.Key.ToUpperInvariant());
            writer.WriteStartArray();
            // Value order and duplicate values are part of the approval scope.
            foreach (var value in pair.Value) writer.WriteStringValue(value);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    private sealed class QuickLaunchExecution(QuickLaunchApproval? confirmedScope)
    {
        public QuickLaunchApproval? ConfirmedScope { get; } = confirmedScope;
        public AppSnapshot? LastSnapshot { get; set; }
        public bool IntentPrepared { get; set; }
    }

    private sealed class QuickLaunchDecision(QuickLaunchResult result) : Exception(result.Detail)
    {
        public QuickLaunchResult Result { get; } = result;
    }
}
