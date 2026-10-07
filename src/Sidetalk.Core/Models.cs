namespace Sidetalk.Core;

public enum Readiness { MissingDependency, SetupRequired, Blocked, Ready, Running, RunningUnverified, Unknown }
public enum ProcessPolicyStatus { NotObserved, NoProcesses, EffectiveRulesUnverified }
public sealed record DependencyInfo(string Name, string? Path, string Version, string Detail, bool Available);
public sealed record AppSnapshot(
    Readiness State, string Title, string Detail, string BoxName, string StoragePath,
    DependencyInfo Kakao, DependencyInfo Sandboxie, IReadOnlyList<int> BoxProcessIds,
    IReadOnlyList<string> Checks, bool CanLaunch, bool CanStop,
    DateTimeOffset CheckedAt, RuntimeVerifier.ServiceVerification? ServiceIdentity = null,
    bool CurrentConfigurationVerified = false,
    ProcessPolicyStatus ProcessPolicy = ProcessPolicyStatus.NotObserved,
    bool ActionsPermitted = true)
{
    public InspectionFailure? InspectionFailure { get; init; }
    public AdminRightsObservation? AdminRights { get; init; }
    public string? RecoveryResultDetail { get; init; }
    /// <summary>Null when policy or journal state could not be verified; false only after validated reconciliation.</summary>
    public bool? CommandPending { get; init; }
}

/// <summary>Convenience entry point; it never replaces the normal GUI confirmation or Core command validation.</summary>
public interface IQuickLaunchController
{
    Task<QuickLaunchStatus> InspectQuickLaunchAsync();
    Task<QuickLaunchStatus> SetQuickLaunchConsentAsync(QuickLaunchApproval approval, bool enabled);
    Task<QuickLaunchResult> LaunchSecondaryAsync(QuickLaunchApproval? confirmedScope, CancellationToken cancellationToken = default);
}

/// <summary>A controller-issued immutable candidate for exactly one validated profile, installation, and configuration scope.</summary>
public sealed class QuickLaunchApproval
{
    internal QuickLaunchApproval(string scopeFingerprint)
    {
        if (scopeFingerprint.Length != 64 || !scopeFingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("빠른 실행 승인 범위가 유효하지 않습니다.", nameof(scopeFingerprint));
        ScopeFingerprint = scopeFingerprint.ToUpperInvariant();
    }

    public string ScopeFingerprint { get; }
}

public sealed record QuickLaunchStatus(AppSnapshot Snapshot, QuickLaunchApproval? Approval, bool ConsentMatches, string Reason);

public enum QuickLaunchOutcome
{
    Launched,
    AlreadyRunning,
    ConfirmationRequired,
    Blocked,
    Uncertain,
    Cancelled
}

public sealed record QuickLaunchResult(QuickLaunchOutcome Outcome, AppSnapshot Snapshot, string Detail);

public interface ISidetalkController : IDisposable
{
    Task<AppSnapshot> InspectAsync();
    Task<string> ExportSetupAsync(string folder);
    Task LaunchAsync();
    Task StopAsync();
    Task<string> ExportDiagnosticsAsync(string path);
    Task<SetupRecoveryPlan> InspectSetupRecoveryAsync();
    Task<SetupRecoveryEvidenceInspection> InspectInterruptedEvidenceAsync();
    Task<AppSnapshot> ReconcileInterruptedEvidenceAsync(SetupRecoveryEvidenceInspection approvedInspection, IProgress<string>? progress = null);
    Task<AppSnapshot> PrepareAsync(SetupRecoveryPlan approvedPlan, bool allowAutomatic, IProgress<string>? progress = null);
    Task<AppSnapshot> PrepareAutomaticallyAsync(IProgress<string>? progress = null);
    Task RevokeAutomaticRecoveryAsync();
    Task<AppSnapshot> CleanResidualBoxAndPrepareAsync(ResidualBoxRecoveryApproval approval, IProgress<string>? progress = null) =>
        throw new InvalidOperationException("이 실행 환경에서는 잔여 프로세스 복구를 지원하지 않습니다.");
    string RecoveryHistoryPath { get; }
}
