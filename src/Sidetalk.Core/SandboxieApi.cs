using System.Runtime.InteropServices;
using System.Text;

namespace Sidetalk.Core;

public sealed record BoxProcess(int Id, string ImageName, string UserSid, uint SessionId);

/// <summary>Only read-only Sandboxie exports. No hooks, injection, configuration writes or arbitrary PID termination.</summary>
public sealed class SandboxieApi : IDisposable
{
    private readonly IntPtr library;
    private readonly QueryConf queryConf;
    private readonly EnumProcesses enumProcesses;
    private readonly QueryProcess queryProcess;
    private readonly QueryBoxPath queryBoxPath;
    private readonly QueryProcessPath queryProcessPath;
    public SandboxieApi(string directory)
    {
        var path = Path.Combine(directory, "SbieDll.dll");
        RuntimeVerifier.RequireComponent(path);
        // Resolve dependencies only from this verified installation directory and System32.
        library = LoadLibraryEx(path, IntPtr.Zero, 0x00000100 | 0x00000800);
        if (library == IntPtr.Zero) throw new InvalidOperationException("Sandboxie API를 불러올 수 없습니다. x64 설치와 서비스를 확인하세요.");
        try
        {
            var driverVersion = new StringBuilder(16);
            RequireSuccess(Export<GetVersion>("SbieApi_GetVersion")(driverVersion), "활성 드라이버 버전 조회");
            if (!RuntimeVerifier.MatchesVersion(driverVersion.ToString(), RuntimeVerifier.DriverVersion))
                throw new InvalidDataException("활성 드라이버 버전이 공식 1.18.4 설치본의 검토 기준 5.73.2와 다릅니다.");
            queryConf = Export<QueryConf>("SbieApi_QueryConf");
            enumProcesses = Export<EnumProcesses>("SbieApi_EnumProcessEx");
            queryProcess = Export<QueryProcess>("SbieApi_QueryProcess");
            queryBoxPath = Export<QueryBoxPath>("SbieApi_QueryBoxPath");
            queryProcessPath = Export<QueryProcessPath>("SbieApi_QueryProcessPath");
        }
        catch { FreeLibrary(library); throw; }
    }
    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    public Dictionary<string, string[]> ReadSection(string section, bool includeTemplates = true) =>
        new SandboxieConfigurationReader(Query).ReadSection(section, includeTemplates);
    public ConfigurationLayers ReadConfiguration(string boxName) =>
        new SandboxieConfigurationReader(Query).ReadConfiguration(boxName);
    public string? Query(string section, string? key, uint index = 0)
    {
        var value = new StringBuilder(16384);
        var status = queryConf(section, key, index, value, (uint)value.Capacity * 2);
        if (status == unchecked((int)0xC000008B)) return null;
        RequireSuccess(status, "설정 조회");
        return value.ToString();
    }
    public IReadOnlyList<BoxProcess> Processes(BoxProfile profile)
    {
        var pids = new uint[512];
        uint count = 512;
        RequireSuccess(enumProcesses(profile.BoxName, true, uint.MaxValue, pids, ref count), "전용 환경 프로세스 조회");
        // With a non-null count pointer, EnumProcessEx writes IDs from index 0 (drv/process_api.c).
        if (count >= 512) throw new InvalidDataException("프로세스 목록이 잘렸습니다. 상태를 확정할 수 없습니다.");
        var result = new List<BoxProcess>();
        var boxPaths = Paths(profile.BoxName);
        for (var i = 0; i < count; i++)
        {
            var box = new StringBuilder(34);
            var image = new StringBuilder(260);
            var sid = new StringBuilder(192);
            var status = queryProcess((IntPtr)pids[i], box, image, sid, out var session);
            if (status != 0) throw new InvalidOperationException("프로세스 소속 조회 중 상태가 바뀌었습니다. 새로고침하세요.");
            if (!box.ToString().Equals(profile.BoxName, StringComparison.OrdinalIgnoreCase) || sid.ToString() != profile.UserSid)
                throw new InvalidDataException("다른 환경 또는 다른 사용자의 프로세스가 발견되어 제어를 차단했습니다.");
            var file = new StringBuilder(16384);
            var key = new StringBuilder(16384);
            var ipc = new StringBuilder(16384);
            uint fileLength = 32768, keyLength = 32768, ipcLength = 32768;
            RequireSuccess(queryProcessPath((IntPtr)pids[i], file, key, ipc, ref fileLength, ref keyLength, ref ipcLength), "프로세스 격리 경로 조회");
            if (!SameNativePath(file.ToString(), profile.StoragePath) ||
                !key.ToString().Equals(boxPaths.Key, StringComparison.OrdinalIgnoreCase) ||
                !ipc.ToString().Equals(boxPaths.Ipc, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("실행 중 프로세스의 격리 경로가 현재 설정과 다릅니다.");
            result.Add(new(checked((int)pids[i]), image.ToString(), sid.ToString(), session));
        }
        return result;
    }
    public (string File, string Key, string Ipc) Paths(string boxName)
    {
        var file = new StringBuilder(16384);
        var key = new StringBuilder(16384);
        var ipc = new StringBuilder(16384);
        uint fileLength = 32768, keyLength = 32768, ipcLength = 32768;
        RequireSuccess(queryBoxPath(boxName, file, key, ipc, ref fileLength, ref keyLength, ref ipcLength), "격리 경로 조회");
        if (!key.ToString().Contains(boxName, StringComparison.OrdinalIgnoreCase) ||
            !ipc.ToString().Contains(boxName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("전용 환경 이름이 없는 레지스트리/IPC 경로는 사용하지 않습니다.");
        return (file.ToString(), key.ToString(), ipc.ToString());
    }
    public string FileRoot(string boxName) => Paths(boxName).File;
    public static bool SameNativePath(string actual, string expected)
    {
        if (actual.StartsWith(@"\??\", StringComparison.Ordinal)) actual = actual[4..];
        if (actual.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
        {
            var device = new StringBuilder(1024);
            if (QueryDosDevice(expected[..2], device, device.Capacity) == 0) return false;
            expected = device + expected[2..];
        }
        return actual.TrimEnd('\\').Equals(expected.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }
    private static void RequireSuccess(int status, string action)
    {
        if (status != 0) throw new InvalidOperationException($"Sandboxie {action} 실패 (0x{status:X8}). 서비스·드라이버와 설정을 확인하세요.");
    }
    public void Dispose() => FreeLibrary(library);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int GetVersion([Out] StringBuilder version);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int QueryConf([MarshalAs(UnmanagedType.LPWStr)] string section, [MarshalAs(UnmanagedType.LPWStr)] string? setting, uint index, [Out] StringBuilder value, uint valueLength);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int EnumProcesses([MarshalAs(UnmanagedType.LPWStr)] string box, [MarshalAs(UnmanagedType.U1)] bool allSessions, uint session, [Out] uint[] pids, ref uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int QueryProcess(IntPtr processId, [Out] StringBuilder box, [Out] StringBuilder image, [Out] StringBuilder sid, out uint session);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int QueryBoxPath([MarshalAs(UnmanagedType.LPWStr)] string box, [Out] StringBuilder file, [Out] StringBuilder key, [Out] StringBuilder ipc, ref uint fileLength, ref uint keyLength, ref uint ipcLength);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int QueryProcessPath(IntPtr pid, [Out] StringBuilder file, [Out] StringBuilder key, [Out] StringBuilder ipc, ref uint fileLength, ref uint keyLength, ref uint ipcLength);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint QueryDosDevice(string device, StringBuilder target, int max);
}
