namespace Sidetalk.Core;

public enum Readiness { MissingDependency, SetupRequired, Blocked, Ready, Running, Unknown }
public sealed record DependencyInfo(string Name, string? Path, string Version, string Detail, bool Available);
public sealed record AppSnapshot(
    Readiness State, string Title, string Detail, string BoxName, string StoragePath,
    DependencyInfo Kakao, DependencyInfo Sandboxie, IReadOnlyList<int> BoxProcessIds,
    IReadOnlyList<string> Checks, bool CanLaunch, bool CanStop,
    DateTimeOffset CheckedAt, RuntimeVerifier.ServiceVerification? ServiceIdentity = null);

public interface ISidetalkController : IDisposable
{
    Task<AppSnapshot> InspectAsync();
    Task<string> ExportSetupAsync(string folder);
    Task LaunchAsync();
    Task StopAsync();
    Task<string> ExportDiagnosticsAsync(string path);
}
