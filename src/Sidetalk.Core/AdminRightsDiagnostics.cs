using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Sidetalk.Core;

public enum AdminRightsCondition { Expected, MissingOrEmpty, Disabled, Unrecognized }
public enum ProgramPhysicalResolution { Unknown, VerifiedBoxCopy }
public sealed record ProgramFileEvidence(string Path, string Version, bool PublisherVerified, string? Failure)
{
    public bool PhysicalPathVerified { get; init; }
}
public sealed record KakaoImageEvidence(int ProcessId, string ImageName, string? QueryReportedImagePath,
    long? CreationTimeUtcTicks, ProgramPhysicalResolution PhysicalResolution, ProgramFileEvidence? PhysicalFile,
    bool? VersionDiffersFromHost, string? Failure);
public sealed record AdminRightsObservation(IReadOnlyList<string> ExplicitValues, IReadOnlyList<string> EffectiveValues,
    AdminRightsCondition Condition, bool ProcessesQueried, IReadOnlyList<int> ProcessIds, string? ProcessFailure,
    IReadOnlyList<KakaoImageEvidence> Images)
{
    public static AdminRightsObservation FromConfiguration(ConfigurationLayers configuration)
    {
        var explicitValues = Values(configuration.ExplicitBox);
        var effectiveValues = Values(configuration.EffectiveBox);
        var condition = Classify(explicitValues);
        if (condition == AdminRightsCondition.Expected && Classify(effectiveValues) != AdminRightsCondition.Expected)
            condition = AdminRightsCondition.Unrecognized;
        return new(explicitValues, effectiveValues, condition, false, [], null, []);
    }

    public static AdminRightsCondition Classify(IReadOnlyList<string> values) => values.Count switch
    {
        0 => AdminRightsCondition.MissingOrEmpty,
        1 when string.IsNullOrWhiteSpace(values[0]) => AdminRightsCondition.MissingOrEmpty,
        1 when values[0].Equals("y", StringComparison.OrdinalIgnoreCase) => AdminRightsCondition.Expected,
        1 when values[0].Equals("n", StringComparison.OrdinalIgnoreCase) => AdminRightsCondition.Disabled,
        _ => AdminRightsCondition.Unrecognized
    };

    private static string[] Values(IReadOnlyDictionary<string, string[]> section) =>
        section.TryGetValue("DropAdminRights", out var values) ? values.ToArray() : [];

    public static IReadOnlyList<KakaoImageEvidence> UnavailableImages(IReadOnlyList<BoxProcess> processes) =>
        processes.Where(IsKakao).Select(process => new KakaoImageEvidence(process.Id, process.ImageName,
            null, null, ProgramPhysicalResolution.Unknown, null, null, "이 조회 경계에서는 실제 실행 이미지 경로를 확인하지 않았습니다.")).ToArray();

    internal static bool IsKakao(BoxProcess process) =>
        process.ImageName.Equals("KakaoTalk.exe", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reads only API-validated process identities and an exact physical KakaoTalk executable. No token or account data is read.</summary>
internal static class WindowsKakaoImageDiagnostics
{
    public static IReadOnlyList<KakaoImageEvidence> Read(BoxProfile profile, DependencyInfo host,
        IReadOnlyList<BoxProcess> processes, Func<IReadOnlyList<BoxProcess>> queryVerifiedProcesses)
    {
        var result = new List<KakaoImageEvidence>();
        foreach (var process in processes.Where(AdminRightsObservation.IsKakao))
        {
            string? path = null;
            long? creation = null;
            var identityVerified = false;
            try
            {
                using var handle = Open(process.Id);
                creation = Creation(handle);
                path = ImagePath(handle);
                var current = queryVerifiedProcesses().SingleOrDefault(candidate => candidate.Id == process.Id);
                if (current != process) throw new InvalidOperationException("실행 이미지 조회 중 전용 프로세스 소속이 바뀌었습니다.");
                using var fresh = Open(process.Id);
                if (Creation(fresh) != creation || !ImagePath(fresh).Equals(path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("실행 이미지 조회 중 프로세스 신원이 바뀌었습니다.");
                identityVerified = true;

                // A logical host path cannot prove which virtualized physical copy is used.
                // Never construct a Sandboxie drive mapping or search the account storage.
                var physical = IsExactBoxExecutable(path, profile.StoragePath) ? ReadFile(path) : null;
                result.Add(new(process.Id, process.ImageName, path, creation,
                    physical?.PhysicalPathVerified == true ? ProgramPhysicalResolution.VerifiedBoxCopy : ProgramPhysicalResolution.Unknown,
                    physical, physical?.PhysicalPathVerified == true && physical.PublisherVerified && host.Available
                        ? CompareVersions(physical.Version, host.Version) : null,
                    physical is null ? "조회 경로의 실제 가상화 파일을 확정하지 못했습니다. 호스트 경로 표시는 박스 사본 부재를 증명하지 않습니다." : null));
            }
            catch (Exception error)
            {
                result.Add(new(process.Id, process.ImageName, identityVerified ? path : null, identityVerified ? creation : null, ProgramPhysicalResolution.Unknown,
                    null, null, InspectionFailure.Sanitize(error.Message)));
            }
        }
        return result;
    }

    internal static bool? CompareVersions(string observed, string host) =>
        Version.TryParse(observed, out var observedVersion) && Version.TryParse(host, out var hostVersion)
            ? observedVersion != hostVersion : null;

    internal static bool IsExactBoxExecutable(string path, string storagePath)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.GetFileName(path).Equals("KakaoTalk.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(storagePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        ProfileStore.RejectReparseAncestors(full);
        return true;
    }

    private static ProgramFileEvidence ReadFile(string path)
    {
        var version = string.Empty;
        var physicalPathVerified = false;
        try
        {
            ProfileStore.RejectReparseAncestors(path);
            using var stable = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var finalPath = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(stable.SafeFileHandle, finalPath, (uint)finalPath.Capacity, 0);
            if (length == 0 || length >= finalPath.Capacity)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "프로그램 파일의 실제 위치를 확인하지 못했습니다.");
            var actual = finalPath.ToString();
            if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
            if (!Path.GetFullPath(actual).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("프로그램 파일이 조회한 실제 경로와 다른 위치로 연결됩니다.");
            physicalPathVerified = true;
            version = FileVersionInfo.GetVersionInfo(path).FileVersion?.Trim() ?? "알 수 없음";
            Authenticode.RequirePublisher(path, "Kakao Corp.");
            return new(path, version, true, null) { PhysicalPathVerified = true };
        }
        catch (Exception error) { return new(path, version, false, InspectionFailure.Sanitize(error.Message)) { PhysicalPathVerified = physicalPathVerified }; }
    }

    private static SafeProcessHandle Open(int id)
    {
        var handle = OpenProcess(0x1000, false, id);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error, "전용 카카오톡 실행 이미지 신원을 조회하지 못했습니다.");
    }
    private static long Creation(SafeProcessHandle handle)
    {
        if (!GetProcessTimes(handle, out var creation, out _, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "전용 프로세스 생성 시각을 조회하지 못했습니다.");
        return DateTime.FromFileTimeUtc(((long)(uint)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime).Ticks;
    }
    private static string ImagePath(SafeProcessHandle handle)
    {
        var text = new StringBuilder(32768);
        uint length = (uint)text.Capacity;
        if (!QueryFullProcessImageName(handle, 0, text, ref length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "전용 프로세스 실행 이미지 경로를 조회하지 못했습니다.");
        return text.ToString();
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle handle, out System.Runtime.InteropServices.ComTypes.FILETIME creation, out System.Runtime.InteropServices.ComTypes.FILETIME exit, out System.Runtime.InteropServices.ComTypes.FILETIME kernel, out System.Runtime.InteropServices.ComTypes.FILETIME user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle handle, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
