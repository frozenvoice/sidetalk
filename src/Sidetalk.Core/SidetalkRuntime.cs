using System.Security.Principal;

namespace Sidetalk.Core;

/// <summary>Native boundaries only; policy and lifecycle decisions remain in the controller.</summary>
public interface ISidetalkRuntime
{
    DependencyInfo Kakao();
    DependencyInfo Sandboxie();
    void RequireHost();
    ISidetalkRuntimeSession Open(DependencyInfo sandboxie);
    void VerifyCommandFiles(string startExe, string kakaoExe);
}

public interface ISidetalkRuntimeSession : IDisposable
{
    RuntimeVerifier.ServiceVerification ServiceIdentity { get; }
    ConfigurationLayers ReadConfiguration(string boxName);
    string FileRoot(string boxName);
    IReadOnlyList<BoxProcess> Processes(BoxProfile profile);
}

public sealed class SidetalkRuntime : ISidetalkRuntime
{
    public DependencyInfo Kakao() => DependencyDetector.Kakao();
    public DependencyInfo Sandboxie() => DependencyDetector.Sandboxie();
    public void RequireHost()
    {
        if (!Environment.Is64BitProcess || !Environment.Is64BitOperatingSystem || Environment.OSVersion.Version.Build < 22000)
            throw new InvalidOperationException("Windows 11 x64가 필요합니다.");
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("일반 권한으로 다시 열어주세요. SIDETALK와 보조 카카오톡은 관리자 권한으로 실행하지 않습니다.");
    }
    public ISidetalkRuntimeSession Open(DependencyInfo sandboxie)
    {
        if (!sandboxie.Available || sandboxie.Path is null)
            throw new InvalidOperationException("Sandboxie Plus 설치와 서명을 확인하세요.");
        var folder = Path.GetDirectoryName(sandboxie.Path)!;
        var service = RuntimeVerifier.RequireInstallation(folder);
        return new Session(new SandboxieApi(folder), service);
    }
    public void VerifyCommandFiles(string startExe, string kakaoExe)
    {
        RuntimeVerifier.RequireComponent(startExe);
        Authenticode.RequirePublisher(kakaoExe, "Kakao Corp.");
    }
    private sealed class Session(SandboxieApi api, RuntimeVerifier.ServiceVerification service) : ISidetalkRuntimeSession
    {
        public RuntimeVerifier.ServiceVerification ServiceIdentity => service;
        public ConfigurationLayers ReadConfiguration(string boxName) => api.ReadConfiguration(boxName);
        public string FileRoot(string boxName) => api.FileRoot(boxName);
        public IReadOnlyList<BoxProcess> Processes(BoxProfile profile) => api.Processes(profile);
        public void Dispose() => api.Dispose();
    }
}