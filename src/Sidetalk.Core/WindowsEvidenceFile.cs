using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Sidetalk.Core;

/// <summary>Renames the inspected file object, never a later replacement at its path.</summary>
internal static class WindowsEvidenceFile
{
    public static FileStream OpenExclusiveSource(string path)
    {
        ProfileStore.RejectReparseAncestors(path);
        // GENERIC_READ | DELETE, no share mode, OPEN_EXISTING, OPEN_REPARSE_POINT.
        // No other handle can obtain read/write/delete access until this handle closes.
        var handle = CreateFile(path, 0x80010000, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw NativeFailure(error, "보존 원본의 배타적 파일 핸들을 열 수 없습니다.");
        }
        try
        {
            ProfileStore.RejectReparseAncestors(path);
            RequireActualPath(handle, path);
            return new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static void RenameNoReplace(SafeFileHandle source, string destination)
    {
        ProfileStore.RejectReparseAncestors(destination);
        var nameBytes = Encoding.Unicode.GetBytes(Path.GetFullPath(destination));
        var nameOffset = Marshal.OffsetOf<FileRenameInfo>(nameof(FileRenameInfo.FileName)).ToInt32();
        var size = Math.Max(Marshal.SizeOf<FileRenameInfo>(), checked(nameOffset + nameBytes.Length + 2));
        var information = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, information, size);
            // FILE_RENAME_INFO union/ReplaceIfExists is zero: an existing preservation
            // file must fail atomically. FileRenameInfo (3) uses the source handle's
            // DELETE permission and does not reopen OriginalPath.
            Marshal.WriteInt32(information, Marshal.OffsetOf<FileRenameInfo>(nameof(FileRenameInfo.FileNameLength)).ToInt32(), nameBytes.Length);
            Marshal.Copy(nameBytes, 0, IntPtr.Add(information, nameOffset), nameBytes.Length);
            if (!SetFileInformationByHandle(source, 3, information, (uint)size))
                throw NativeFailure(Marshal.GetLastWin32Error(), "열린 원본 파일의 보존 위치 이동을 완료하지 못했습니다.");
            RequireActualPath(source, destination);
        }
        finally { Marshal.FreeHGlobal(information); }
    }

    private static void RequireActualPath(SafeFileHandle handle, string expected)
    {
        var path = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
        if (length == 0 || length >= path.Capacity)
            throw NativeFailure(Marshal.GetLastWin32Error(), "보존 파일 핸들의 실제 위치를 확인할 수 없습니다.");
        var actual = path.ToString();
        if (actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) actual = @"\\" + actual[8..];
        else if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        if (!Path.GetFullPath(actual).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("보존 파일 핸들의 실제 위치가 승인 경로와 다릅니다.");
    }

    private static IOException NativeFailure(int error, string detail) => new(detail, new Win32Exception(error));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileRenameInfo
    {
        public uint Flags;
        public IntPtr RootDirectory;
        public uint FileNameLength;
        public char FileName;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, IntPtr information, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
