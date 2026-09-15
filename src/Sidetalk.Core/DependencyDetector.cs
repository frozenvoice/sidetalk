using System.Diagnostics;
using Microsoft.Win32;

namespace Sidetalk.Core;

public static class DependencyDetector
{
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
        paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sandboxie-Plus", "SandMan.exe"));
        return Detect("Sandboxie Plus", paths, ["NOVASYNC LABS PTE. LTD.", "Xanasoft", "Xanasoft Ltd.", "David Xanatos", "Open Source Developer, David Xanatos", "Sandboxie Holdings, LLC"]);
    }
    private static DependencyInfo Detect(string name, IEnumerable<string> paths, string[] publishers)
    {
        var path = paths.FirstOrDefault(File.Exists);
        if (path is null) return new(name, null, "미설치", "설치 경로를 찾지 못했습니다. 공식 설치 프로그램을 확인하세요.", false);
        var version = FileVersionInfo.GetVersionInfo(path).FileVersion?.Trim() ?? "알 수 없음";
        try
        {
            Authenticode.RequirePublisher(path, publishers);
            return new(name, path, version, "설치됨 · 배포자 서명 확인", true);
        }
        catch (Exception ex) { return new(name, path, version, ex.Message, false); }
    }
    public static void VerifySandboxieComponent(string path) => Authenticode.RequirePublisher(path, "NOVASYNC LABS PTE. LTD.", "Xanasoft", "Xanasoft Ltd.", "David Xanatos", "Open Source Developer, David Xanatos", "Sandboxie Holdings, LLC");
}
