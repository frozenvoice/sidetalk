using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Sidetalk.Core;

public static class RuntimeVerifier
{
    public const string PlusVersion = "1.18.4";
    public const string CoreVersion = "5.73.4";
    public static bool MatchesVersion(string actual, string expected) =>
        Version.TryParse(actual.Trim(), out var found) && Version.TryParse(expected, out var baseline) &&
        found.Major == baseline.Major && found.Minor == baseline.Minor && found.Build == baseline.Build && found.Revision is -1 or 0;

    public static void RequireComponent(string path, bool driver = false)
    {
        if (driver)
            Authenticode.RequirePublisher(path, "NOVASYNC LABS PTE. LTD.", "Microsoft Windows Hardware Compatibility Publisher", "Xanasoft", "David Xanatos", "Open Source Developer, David Xanatos");
        else DependencyDetector.VerifySandboxieComponent(path);
        var version = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "";
        if (!MatchesVersion(version, CoreVersion)) throw new InvalidDataException($"구성요소 버전 검토가 필요합니다: {Path.GetFileName(path)} {version.Trim()} (기준 {CoreVersion})");
    }
    public static void RequireInstallation(string folder)
    {
        foreach (var component in new[] { "Start.exe", "SbieDll.dll", "SbieSvc.exe" }) RequireComponent(Path.Combine(folder, component));
        var servicePath = RegisteredImage("SbieSvc", "SbieSvc.exe");
        var expectedService = Path.Combine(folder, "SbieSvc.exe");
        if (!string.Equals(servicePath, expectedService, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("등록된 Sandboxie 서비스가 선택한 설치 폴더와 다릅니다.");
        RequireComponent(RegisteredImage("SbieDrv", "SbieDrv.sys"), driver: true);
        RequireRunningService(expectedService);
    }
    private static string RegisteredImage(string serviceName, string fileName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + serviceName);
        var image = key?.GetValue("ImagePath") as string ?? throw new InvalidDataException($"Sandboxie 서비스/드라이버 등록이 없습니다: {serviceName}");
        image = Environment.ExpandEnvironmentVariables(image).Trim();
        var end = image.IndexOf(fileName, StringComparison.OrdinalIgnoreCase);
        if (end < 0) throw new InvalidDataException("등록된 프로그램 경로를 확인할 수 없습니다.");
        image = image[..(end + fileName.Length)].Trim('"');
        if (image.StartsWith(@"\??\", StringComparison.Ordinal)) image = image[4..];
        if (image.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) image = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), image[12..]);
        if (!Path.IsPathFullyQualified(image)) throw new InvalidDataException("등록 경로가 절대 경로가 아닙니다.");
        return Path.GetFullPath(image);
    }
    private static void RequireRunningService(string expectedPath)
    {
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new InvalidOperationException("서비스 상태를 읽을 수 없습니다.");
        try
        {
            var service = OpenService(manager, "SbieSvc", 4);
            if (service == IntPtr.Zero) throw new InvalidOperationException("Sandboxie 서비스 상태를 읽을 수 없습니다.");
            try
            {
                if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _) || status.CurrentState != 4 || status.ProcessId == 0)
                    throw new InvalidOperationException("Sandboxie 서비스가 실행 중이 아닙니다. SIDETALK는 서비스를 시작하거나 재시작하지 않습니다.");
                var process = OpenProcess(0x1000, false, status.ProcessId);
                if (process == IntPtr.Zero) throw new InvalidOperationException("실행 중인 Sandboxie 서비스 경로를 확인할 수 없습니다.");
                try
                {
                    var buffer = new StringBuilder(32768);
                    uint length = (uint)buffer.Capacity;
                    if (!QueryFullProcessImageName(process, 0, buffer, ref length) || !buffer.ToString().Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("실행 중인 Sandboxie 서비스가 설치 파일과 일치하지 않습니다.");
                }
                finally { CloseHandle(process); }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint Type, CurrentState, Controls, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint, ProcessId, Flags; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string service, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatus status, int size, out int needed);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
