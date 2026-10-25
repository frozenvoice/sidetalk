using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Sidetalk.Core;

public static class RuntimeVerifier
{
    public const string PlusVersion = SandboxieSupport.PlusVersion;
    public const string CoreVersion = SandboxieSupport.CoreVersion;
    public const string DriverVersion = SandboxieSupport.DriverVersion;
    private const string DriverSha256 = SandboxieSupport.DriverSha256;
    public const uint ServiceRunningState = 4;
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ErrorAccessDenied = 5;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint ErrorServiceDoesNotExist = 1060;

    public static bool MatchesComponentVersion(string actual, bool driver) =>
        MatchesVersion(actual, driver ? DriverVersion : CoreVersion);

    public static bool MatchesVersion(string actual, string expected) =>
        Version.TryParse(actual.Trim(), out var found) && Version.TryParse(expected, out var baseline) &&
        found.Major == baseline.Major && found.Minor == baseline.Minor && found.Build == baseline.Build && found.Revision is -1 or 0;

    public static void RequireComponent(string path, bool driver = false)
    {
        if (driver)
            Authenticode.RequirePublisher(path, "NOVASYNC LABS PTE. LTD.", "Microsoft Windows Hardware Compatibility Publisher", "Xanasoft", "David Xanatos", "Open Source Developer, David Xanatos");
        else DependencyDetector.VerifySandboxieComponent(path);
        var version = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "";
        if (!MatchesComponentVersion(version, driver))
            throw new InvalidDataException($"구성요소 버전 검토가 필요합니다: {Path.GetFileName(path)} {version.Trim()} (기준 {(driver ? DriverVersion : CoreVersion)})");
        if (driver)
        {
            using var file = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(DriverSha256, StringComparison.Ordinal))
                throw new InvalidDataException($"드라이버가 검토된 공식 {PlusVersion} x64 설치 파일의 구성요소와 다릅니다.");
        }
    }

    public static ServiceVerification RequireInstallation(string folder)
    {
        var installationDirectory = Path.GetFullPath(folder);
        foreach (var component in new[] { "Start.exe", "SbieDll.dll", "SbieSvc.exe" })
            RequireComponent(Path.Combine(installationDirectory, component));

        var expectedService = Path.Combine(installationDirectory, "SbieSvc.exe");
        var driverPath = RegisteredImage("SbieDrv", "SbieDrv.sys");
        if (!driverPath.Equals(Path.Combine(installationDirectory, "SbieDrv.sys"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("등록된 드라이버가 선택한 Sandboxie 설치 폴더와 다릅니다.");
        RequireComponent(driverPath, driver: true);
        return RequireRunningService(expectedService);
    }

    // This legacy registry reader is retained only for the driver registration,
    // which is independently compared with the signed, hash-pinned disk file.
    // SbieSvc trust is based on QueryServiceConfig below, never on truncating
    // an arbitrary ImagePath string at the first matching filename.
    private static string RegisteredImage(string serviceName, string fileName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + serviceName);
        var image = key?.GetValue("ImagePath") as string ?? throw new InvalidDataException($"Sandboxie 서비스/드라이버 등록이 없습니다: {serviceName}");
        image = Environment.ExpandEnvironmentVariables(image).Trim();
        if (image.Length >= 2 && image[0] == '"' && image[^1] == '"') image = image[1..^1];
        if (image.Contains('"') || !Path.GetFileName(image).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("등록된 드라이버 경로에 다른 파일이나 인자가 있습니다.");
        if (image.StartsWith(@"\??\", StringComparison.Ordinal)) image = image[4..];
        if (image.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) image = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), image[12..]);
        if (!Path.IsPathFullyQualified(image)) throw new InvalidDataException("등록 경로가 절대 경로가 아닙니다.");
        return Path.GetFullPath(image);
    }

    private static ServiceVerification RequireRunningService(string expectedPath)
    {
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new InvalidOperationException("서비스 상태를 읽을 수 없습니다.");
        try
        {
            var service = OpenService(manager, "SbieSvc", 0x0001 | 0x0004);
            if (service == IntPtr.Zero) throw new InvalidOperationException("Sandboxie 서비스 상태를 읽을 수 없습니다.");
            try
            {
                var before = ReadServiceStatus(service);
                var configuration = ReadServiceConfiguration(service);
                var configurationResult = ValidateServiceConfiguration(
                    configuration.ServiceType,
                    configuration.BinaryPathName,
                    configuration.ServiceStartName,
                    expectedPath);
                if (!configurationResult.Valid)
                    throw new InvalidDataException(configurationResult.Issue!);

                var after = ReadServiceStatus(service);
                var statusResult = ValidateStableRunningStatus(
                    before.CurrentState,
                    before.ProcessId,
                    after.CurrentState,
                    after.ProcessId);
                if (!statusResult.Valid)
                    throw new InvalidOperationException(statusResult.Issue!);

                var process = OpenProcess(0x1000, false, after.ProcessId);
                if (process == IntPtr.Zero)
                {
                    var error = (uint)Marshal.GetLastWin32Error();
                    if (error == ErrorAccessDenied)
                        return Complete(AccessDeniedVerification(after.ProcessId));
                    throw new InvalidOperationException($"실행 중인 Sandboxie 서비스 경로를 확인할 수 없습니다 (Win32 오류 {error}).");
                }
                try
                {
                    var buffer = new StringBuilder(32768);
                    uint length = (uint)buffer.Capacity;
                    if (!QueryFullProcessImageName(process, 0, buffer, ref length))
                    {
                        var error = (uint)Marshal.GetLastWin32Error();

                        throw new InvalidOperationException($"실행 중인 Sandboxie 서비스 경로를 확인할 수 없습니다 (Win32 오류 {error}).");
                    }
                    if (!string.Equals(Path.GetFullPath(buffer.ToString()), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("실행 중인 Sandboxie 서비스가 설치 파일과 일치하지 않습니다.");
                    return Complete(new ServiceVerification(
                        checked((int)after.ProcessId),
                        true,
                        "SCM 구성 + 실행 이미지 경로 + 서명된 구성요소",
                        null));
                }
                finally { CloseHandle(process); }

                ServiceVerification Complete(ServiceVerification result)
                {
                    var final = ReadServiceStatus(service);
                    var stable = ValidateStableRunningStatus(before.CurrentState, before.ProcessId, final.CurrentState, final.ProcessId);
                    if (!stable.Valid || before.Type != ServiceWin32OwnProcess || final.Type != ServiceWin32OwnProcess)
                        throw new InvalidOperationException(stable.Issue ?? "실행 중 서비스 형식이 검토 기준과 다릅니다.");
                    return result;
                }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    /// <summary>Read-only SCM status for installation guidance. It never starts, stops or reconfigures a service.</summary>
    public static SandboxieServiceState QueryServiceState(string serviceName = "SbieSvc")
    {
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) return SandboxieServiceState.Unreadable;
        try
        {
            var service = OpenService(manager, serviceName, 0x0004);
            if (service == IntPtr.Zero)
                return (uint)Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist ? SandboxieServiceState.Missing : SandboxieServiceState.Unreadable;
            try
            {
                if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _))
                    return SandboxieServiceState.Unreadable;
                return status.CurrentState == ServiceRunningState ? SandboxieServiceState.Running : SandboxieServiceState.NotRunning;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static ServiceVerification AccessDeniedVerification(uint processId) => new(
        checked((int)processId),
        false,
        "SCM 구성 + 서명된 구성요소",
        "SbieSvc 실행 경로 조회가 일반 사용자 권한으로 거부되었습니다 (ERROR_ACCESS_DENIED). SCM 등록 경로·계정·PID와 서명된 설치 파일로 확인했습니다. 이 값은 실행 이미지 경로를 직접 읽은 증거가 아닙니다.");

    public static ServiceConfigurationValidation ValidateServiceConfiguration(
        uint serviceType,
        string? binaryPathName,
        string? serviceStartName,
        string expectedPath)
    {
        if (serviceType != ServiceWin32OwnProcess)
            return new(false, "Sandboxie 서비스 형식이 SERVICE_WIN32_OWN_PROCESS와 다릅니다.");
        if (!string.Equals(serviceStartName, "LocalSystem", StringComparison.OrdinalIgnoreCase))
            return new(false, "Sandboxie 서비스 계정이 LocalSystem과 다릅니다.");
        if (string.IsNullOrEmpty(binaryPathName))
            return new(false, "Sandboxie 서비스 실행 경로가 비어 있습니다.");

        var fullExpected = Path.GetFullPath(expectedPath);
        var quoted = binaryPathName.Length >= 2 && binaryPathName[0] == '"' && binaryPathName[^1] == '"';
        var exactPath = quoted ? binaryPathName[1..^1] : binaryPathName;
        if (quoted && exactPath.IndexOf('"') >= 0)
            return new(false, "Sandboxie 서비스 실행 경로에 허용되지 않은 따옴표가 있습니다.");
        if (!quoted && fullExpected.Any(char.IsWhiteSpace))
            return new(false, "공백이 있는 Sandboxie 서비스 경로는 따옴표로 등록해야 합니다.");
        if (!string.Equals(exactPath, fullExpected, StringComparison.OrdinalIgnoreCase))
            return new(false, "Sandboxie 서비스 실행 경로가 선택한 설치 파일과 정확히 일치하지 않습니다.");
        return new(true, null);
    }

    public static ServiceStatusValidation ValidateStableRunningStatus(
        uint beforeState,
        uint beforeProcessId,
        uint afterState,
        uint afterProcessId)
    {
        if (beforeState != ServiceRunningState || afterState != ServiceRunningState)
            return new(false, "Sandboxie 서비스가 실행 중이 아닙니다. SIDETALK는 서비스를 시작하거나 재시작하지 않습니다.");
        if (beforeProcessId == 0 || afterProcessId == 0)
            return new(false, "Sandboxie 서비스 프로세스 ID를 확인할 수 없습니다.");
        if (beforeProcessId != afterProcessId)
            return new(false, "Sandboxie 서비스 프로세스가 상태 확인 중 교체되었습니다.");
        return new(true, null);
    }

    private static ServiceStatus ReadServiceStatus(IntPtr service)
    {
        if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _))
            throw new InvalidOperationException("Sandboxie 서비스 상태를 읽을 수 없습니다.");
        return status;
    }

    private static ServiceConfiguration ReadServiceConfiguration(IntPtr service)
    {
        QueryServiceConfig(service, IntPtr.Zero, 0, out var needed);
        var firstError = (uint)Marshal.GetLastWin32Error();
        if (firstError != ErrorInsufficientBuffer || needed <= 0 || needed > 8192)
            throw new InvalidOperationException($"Sandboxie 서비스 구성을 읽을 수 없습니다 (Win32 오류 {firstError}).");
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!QueryServiceConfig(service, buffer, needed, out _))
                throw new InvalidOperationException($"Sandboxie 서비스 구성을 읽을 수 없습니다 (Win32 오류 {(uint)Marshal.GetLastWin32Error()}).");
            var native = Marshal.PtrToStructure<NativeServiceConfiguration>(buffer);
            return new ServiceConfiguration(
                native.ServiceType,
                Marshal.PtrToStringUni(native.BinaryPathName),
                Marshal.PtrToStringUni(native.ServiceStartName));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public enum SandboxieServiceState { Running, NotRunning, Missing, Unreadable }
    public sealed record ServiceVerification(int ProcessId, bool LiveImagePathVerified, string IdentityBasis, string? LiveImagePathIssue);
    public sealed record ServiceConfigurationValidation(bool Valid, string? Issue);
    public sealed record ServiceStatusValidation(bool Valid, string? Issue);
    private sealed record ServiceConfiguration(uint ServiceType, string? BinaryPathName, string? ServiceStartName);

    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint Type, CurrentState, Controls, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint, ProcessId, Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeServiceConfiguration
    {
        public uint ServiceType, StartType, ErrorControl;
        public IntPtr BinaryPathName, LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies, ServiceStartName, DisplayName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string service, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatus status, int size, out int needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceConfig(IntPtr service, IntPtr config, int configSize, out int bytesNeeded);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}