using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Sidetalk.Core;

/// <summary>Read-only proof that setup is using the ordinary user's physical profile root.</summary>
public static class HostProfileContext
{
    private static readonly Guid LocalAppDataId = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");

    public static void RequireUnredirected(ProfileStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        new SidetalkRuntime().RequireHost();
        uint packageLength = 0;
        var packageStatus = GetCurrentPackageFullName(ref packageLength, null);
        if (packageStatus != 15700) // APPMODEL_ERROR_NO_PACKAGE
            throw new InvalidOperationException("패키지 파일 가상화가 적용될 수 있는 실행입니다. 탐색기에서 run-local.cmd로 SIDETALK를 열어 현재 일반 사용자 환경을 확인하세요.");

        var id = LocalAppDataId;
        var status = SHGetKnownFolderPath(ref id, 0x4000, IntPtr.Zero, out var pointer);
        if (status != 0) Marshal.ThrowExceptionForHR(status);
        string local;
        try { local = Marshal.PtrToStringUni(pointer) ?? throw new InvalidDataException("실제 사용자 저장 경로를 확인하지 못했습니다."); }
        finally { Marshal.FreeCoTaskMem(pointer); }
        var expected = Path.GetFullPath(Path.Combine(local, "SIDETALK"));
        if (!SamePath(expected, store.Root))
            throw new InvalidDataException("복구 대상이 현재 일반 사용자의 SIDETALK 저장 경로가 아닙니다.");
        ProfileStore.RejectReparseAncestors(expected);
        VerifyNearestExistingPath(expected);
        VerifyNearestExistingPath(store.ProfilePath);
    }

    private static void VerifyNearestExistingPath(string path)
    {
        var candidate = Path.GetFullPath(path);
        while (true)
        {
            using var handle = CreateFile(candidate, 0x80, 0x7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                var actual = new StringBuilder(32768);
                var count = GetFinalPathNameByHandle(handle, actual, (uint)actual.Capacity, 0);
                if (count == 0 || count >= actual.Capacity)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "사용자 저장 경로의 실제 파일 위치를 확인하지 못했습니다.");
                var finalPath = actual.ToString();
                if (finalPath.StartsWith(@"\\?\", StringComparison.Ordinal)) finalPath = finalPath[4..];
                if (!SamePath(candidate, finalPath))
                    throw new InvalidDataException("SIDETALK 프로필 경로가 다른 실제 위치로 연결되어 있습니다. 탐색기에서 run-local.cmd로 다시 열어 확인하세요.");
                return;
            }
            var error = Marshal.GetLastWin32Error();
            if (error is not (2 or 3))
                throw new Win32Exception(error, "일반 사용자 SIDETALK 경로를 읽을 수 없습니다.");
            candidate = Path.GetDirectoryName(candidate)
                ?? throw new InvalidDataException("실제 사용자 저장 경로의 상위 폴더를 확인하지 못했습니다.");
        }
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, StringBuilder? packageFullName);
    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
