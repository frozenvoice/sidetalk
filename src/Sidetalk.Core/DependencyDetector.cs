using System.Diagnostics;
using Microsoft.Win32;

namespace Sidetalk.Core;

public static class DependencyDetector
{
    private static readonly string[] SandboxiePublishers = ["NOVASYNC LABS PTE. LTD.", "Xanasoft", "Xanasoft Ltd.", "David Xanatos", "Open Source Developer, David Xanatos", "Sandboxie Holdings, LLC"];

    public static DependencyInfo Kakao()
    {
        var paths = new List<string>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\KakaoTalk.exe");
            if (key?.GetValue(null) is string path) paths.Add(path.Trim('"'));
        }
        paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Kakao", "KakaoTalk", "KakaoTalk.exe"));
        paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Kakao", "KakaoTalk", "KakaoTalk.exe"));
        return Detect("공식 카카오톡", paths, ["Kakao Corp."]);
    }

    public static DependencyInfo Sandboxie()
    {
        var paths = new List<string>();
        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SbieSvc"))
        {
            if (key?.GetValue("ImagePath") is string raw)
            {
                raw = Environment.ExpandEnvironmentVariables(raw);
                var end = raw.IndexOf("SbieSvc.exe", StringComparison.OrdinalIgnoreCase);
                if (end >= 0)
                {
                    var exe = raw[..(end + "SbieSvc.exe".Length)].Trim('"');
                    var folder = Path.GetDirectoryName(exe);
                    if (folder is not null) paths.Add(Path.Combine(folder, "SandMan.exe"));
                }
            }
        }
        var defaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sandboxie-Plus");
        paths.Add(Path.Combine(defaultFolder, "SandMan.exe"));
        var path = paths.FirstOrDefault(File.Exists);
        return ClassifySandboxie(
            path,
            path is null && HasSandboxieInstallationTrace(defaultFolder),
            file => FileVersionInfo.GetVersionInfo(file).FileVersion?.Trim() ?? "알 수 없음",
            VerifySandboxieComponent,
            () => RuntimeVerifier.QueryServiceState());
    }

    /// <summary>
    /// Separates "nothing installed" from an installation that exists but cannot be trusted or used, so an
    /// unverified installation is never presented as missing and never offered a fresh install over it.
    /// </summary>
    public static DependencyInfo ClassifySandboxie(
        string? path,
        bool installationTrace,
        Func<string, string> readVersion,
        Action<string> verifySignature,
        Func<RuntimeVerifier.SandboxieServiceState> serviceState)
    {
        const string name = "Sandboxie Plus";
        if (path is null)
        {
            return installationTrace
                ? new DependencyInfo(name, null, "", "Sandboxie 설치 흔적(서비스·드라이버 등록, 설치 폴더 또는 설치 기록)은 있지만 SandMan.exe를 찾지 못했습니다.", false)
                    { Condition = DependencyCondition.PathUnresolved }
                : new DependencyInfo(name, null, "미설치", "설치 경로를 찾지 못했습니다.", false) { Condition = DependencyCondition.NotInstalled };
        }
        var version = readVersion(path);
        try { verifySignature(path); }
        catch (Exception ex) { return new DependencyInfo(name, path, version, ex.Message, false) { Condition = DependencyCondition.SignatureInvalid }; }
        if (!RuntimeVerifier.MatchesVersion(version, SandboxieSupport.PlusVersion))
            return new DependencyInfo(name, path, version, $"설치됨 · 배포자 서명 확인 · 지원 버전 {SandboxieSupport.PlusVersion}와 다름", true)
                { Condition = DependencyCondition.UnsupportedVersion };
        var state = serviceState();
        if (state is RuntimeVerifier.SandboxieServiceState.NotRunning or RuntimeVerifier.SandboxieServiceState.Missing)
        {
            var detail = state == RuntimeVerifier.SandboxieServiceState.Missing
                ? "설치됨 · 배포자 서명 확인 · Sandboxie 서비스(SbieSvc)가 등록되어 있지 않습니다."
                : "설치됨 · 배포자 서명 확인 · Sandboxie 서비스(SbieSvc)가 실행 중이 아닙니다.";
            return new DependencyInfo(name, path, version, detail, true) { Condition = DependencyCondition.ServiceUnavailable };
        }
        // An unreadable service status is not evidence of a fault; RuntimeVerifier still
        // requires the exact running service before any configuration is read.
        return new DependencyInfo(name, path, version, "설치됨 · 배포자 서명 확인", true) { Condition = DependencyCondition.Verified };
    }

    private static bool HasSandboxieInstallationTrace(string defaultFolder)
    {
        if (Directory.Exists(defaultFolder)) return true;
        using (var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
        {
            foreach (var serviceName in new[] { "SbieSvc", "SbieDrv" })
            {
                using var service = services?.OpenSubKey(serviceName);
                if (service is not null) return true;
            }
        }
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var uninstall = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) continue;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(name);
                if (entry?.GetValue("DisplayName") is string display && display.StartsWith("Sandboxie", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static DependencyInfo Detect(string name, IEnumerable<string> paths, string[] publishers)
    {
        var path = paths.FirstOrDefault(File.Exists);
        if (path is null) return new(name, null, "미설치", "설치 경로를 찾지 못했습니다. 공식 설치 프로그램을 확인하세요.", false) { Condition = DependencyCondition.NotInstalled };
        var version = FileVersionInfo.GetVersionInfo(path).FileVersion?.Trim() ?? "알 수 없음";
        try
        {
            Authenticode.RequirePublisher(path, publishers);
            return new(name, path, version, "설치됨 · 배포자 서명 확인", true) { Condition = DependencyCondition.Verified };
        }
        catch (Exception ex) { return new(name, path, version, ex.Message, false) { Condition = DependencyCondition.SignatureInvalid }; }
    }

    public static void VerifySandboxieComponent(string path) => Authenticode.RequirePublisher(path, SandboxiePublishers);
}
