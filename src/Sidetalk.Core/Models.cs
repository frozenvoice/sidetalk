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
    public string? RecoveryResultDetail { get; init; }
}

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
    string RecoveryHistoryPath { get; }
}
