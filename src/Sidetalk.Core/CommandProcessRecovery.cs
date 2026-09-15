using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Sidetalk.Core;

/// <summary>
/// The identity recorded for a Sandboxie Start.exe process.  The creation time is
/// stored as <see cref="DateTime"/> UTC ticks so a PID can never be treated as the
/// same command after Windows has reused it.
/// </summary>
public sealed record CommandProcessIdentity(int ProcessId, long CreationTimeUtcTicks, string ImagePath);

/// <summary>Recovers a previously journaled command without starting or terminating a process.</summary>
public interface ICommandProcessRecovery
{
    ISidetalkCommandHandle Recover(CommandProcessIdentity identity);

    /// <summary>
    /// Reconciles an intent that was persisted before its PID could be recorded.
    /// An exact live Start.exe is deliberately treated as ambiguous and blocks recovery.
    /// </summary>
    ISidetalkCommandHandle RecoverUnidentified(string expectedStartExe);
}

/// <summary>
/// Uses only the retained process handle and the read-only Windows process APIs needed
/// to prove command identity.  This class has no terminate, debug privilege, hook, or
/// security descriptor operations.
/// </summary>
public sealed class ProcessCommandRecovery : ICommandProcessRecovery
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint ProcessAccess = ProcessQueryLimitedInformation | Synchronize;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x102;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const uint StillActive = 259;
    private const int ErrorFileNotFound = 2;
    private const int ErrorInvalidHandle = 6;
    private const int ErrorInvalidParameter = 87;

    public ISidetalkCommandHandle Recover(CommandProcessIdentity identity)
    {
        ValidateIdentity(identity);

        var handle = Open(identity.ProcessId, out var openError);
        if (handle is null)
        {
            if (IsMissingPid(openError))
                return new MissingCommandHandle(identity);
            throw Win32Failure("명령 프로세스를 다시 열 수 없습니다", openError);
        }

        try
        {
            if (!TryGetCreationTicks(handle, out var actualTicks, out var creationError))
                throw Win32Failure("명령 프로세스 생성 시각을 확인할 수 없습니다", creationError);

            // A different creation time means that the recorded PID has already been
            // reused.  Do not retain or observe the replacement process.
            if (actualTicks != identity.CreationTimeUtcTicks)
            {
                handle.Dispose();
                return new MissingCommandHandle(identity);
            }

            if (!TryGetImagePath(handle, out var actualPath, out var imageError))
            {
                if (IsProcessGone(imageError) && IsExited(handle))
                {
                    handle.Dispose();
                    return new MissingCommandHandle(identity);
                }
                throw Win32Failure("명령 프로세스 실행 파일을 확인할 수 없습니다", imageError);
            }

            if (!SameImagePath(actualPath, identity.ImagePath))
                throw new InvalidDataException("기록된 명령 프로세스와 현재 실행 파일이 다릅니다. 복구를 차단했습니다.");

            return new RecoveredProcessHandle(handle, identity);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public ISidetalkCommandHandle RecoverUnidentified(string expectedStartExe)
    {
        var expected = ValidateImagePath(expectedStartExe);
        if (!Path.GetFileName(expected).Equals("Start.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("준비된 명령의 Sandboxie Start.exe 경로가 필요합니다.");
        var candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(expected));
        try
        {
            foreach (var candidate in candidates)
            {
                var pid = candidate.Id;
                var handle = Open(pid, out var openError);
                if (handle is null)
                    throw Win32Failure("후보 명령 프로세스를 확인할 수 없습니다", openError);

                using (handle)
                {
                    if (!TryGetImagePath(handle, out var actualPath, out var imageError))
                        throw Win32Failure("후보 명령 실행 파일을 확인할 수 없습니다", imageError);

                    // The exact candidate is considered ambiguous even if it exits while
                    // this inspection is running. A fresh inspection can be attempted
                    // once the process snapshot no longer contains it.
                    if (SameImagePath(actualPath, expected))
                        throw new InvalidOperationException("동일한 Start.exe 명령이 실행 중인지 식별할 수 없어 복구를 차단했습니다.");
                }
            }
        }
        finally
        {
            foreach (var candidate in candidates)
                candidate.Dispose();
        }
        // There is no PID or exit code to trust in this branch.  The controller must
        // perform a fresh, validated box enumeration before completing the journal.
        return new MissingCommandHandle(null);
    }

    /// <summary>
    /// Captures identity from the original Process.SafeHandle.  GetProcessTimes is
    /// attempted before image lookup so a command that exits immediately still retains
    /// its creation timestamp for later PID-reuse checks.
    /// </summary>
    public static CommandProcessIdentity Capture(Process process, string expectedImagePath)
    {
        ArgumentNullException.ThrowIfNull(process);
        var expected = ValidateImagePath(expectedImagePath);
        var handle = process.SafeHandle;
        if (handle.IsInvalid || handle.IsClosed)
            throw new InvalidOperationException("명령 프로세스 핸들이 유효하지 않습니다.");

        if (!TryGetCreationTicks(handle, out var creationTicks, out var creationError))
            throw Win32Failure("명령 프로세스 생성 시각을 확인할 수 없습니다", creationError);

        if (TryGetImagePath(handle, out var actualPath, out var imageError))
        {
            if (!SameImagePath(actualPath, expected))
                throw new InvalidDataException("시작된 명령의 실행 파일이 요청한 Start.exe와 다릅니다.");
        }
        else if (!IsExited(handle) || !IsProcessGone(imageError))
        {
            // A live command with an unreadable image is unsafe to journal.  A fast
            // exited command is allowed to use the already verified expected path.
            throw Win32Failure("명령 프로세스 실행 파일을 확인할 수 없습니다", imageError);
        }

        return new CommandProcessIdentity(process.Id, creationTicks, expected);
    }

    private static SafeProcessHandle? Open(int processId, out int error)
    {
        var handle = OpenProcess(ProcessAccess, inheritHandle: false, checked((uint)processId));
        if (!handle.IsInvalid)
        {
            error = 0;
            return handle;
        }

        error = Marshal.GetLastWin32Error();
        handle.Dispose();
        return null;
    }

    private static void ValidateIdentity(CommandProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.ProcessId <= 0 || identity.CreationTimeUtcTicks <= DateTime.MinValue.Ticks ||
            identity.CreationTimeUtcTicks > DateTime.MaxValue.Ticks)
            throw new InvalidDataException("기록된 명령 프로세스 식별자가 유효하지 않습니다.");
        ValidateImagePath(identity.ImagePath);
    }

    private static string ValidateImagePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            string.IsNullOrWhiteSpace(Path.GetFileName(path)) ||
            path.Any(char.IsControl))
            throw new InvalidDataException("Sandboxie Start.exe의 절대 경로가 필요합니다.");
        return Path.GetFullPath(path);
    }

    private static bool SameImagePath(string actual, string expected) =>
        NormalizeImagePath(actual).Equals(NormalizeImagePath(expected), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeImagePath(string path)
    {
        if (path.StartsWith(@"\??\", StringComparison.Ordinal))
            path = path[4..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path[4..];
        return Path.GetFullPath(path).TrimEnd('\\');
    }

    private static bool TryGetCreationTicks(SafeProcessHandle handle, out long ticks, out int error)
    {
        ticks = 0;
        if (!GetProcessTimes(handle, out var creation, out _, out _, out _))
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        var fileTime = ((long)(uint)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime;
        try
        {
            ticks = DateTime.FromFileTimeUtc(fileTime).Ticks;
        }
        catch (ArgumentOutOfRangeException)
        {
            error = ErrorInvalidParameter;
            return false;
        }

        error = 0;
        return ticks > DateTime.MinValue.Ticks;
    }

    private static bool TryGetImagePath(SafeProcessHandle handle, out string path, out int error)
    {
        var buffer = new StringBuilder(32768);
        uint length = (uint)buffer.Capacity;
        if (!QueryFullProcessImageName(handle, 0, buffer, ref length))
        {
            path = string.Empty;
            error = Marshal.GetLastWin32Error();
            return false;
        }

        path = buffer.ToString();
        error = 0;
        return !string.IsNullOrWhiteSpace(path);
    }

    private static bool IsExited(SafeProcessHandle handle)
    {
        var state = WaitForSingleObject(handle, 0);
        if (state == WaitObject0) return true;
        if (state == WaitTimeout) return false;
        throw Win32Failure("명령 프로세스 상태를 확인할 수 없습니다", Marshal.GetLastWin32Error());
    }

    private static bool IsMissingPid(int error) => error == ErrorInvalidParameter;
    private static bool IsProcessGone(int error) => error is ErrorInvalidParameter or ErrorInvalidHandle or ErrorFileNotFound;

    private static InvalidOperationException Win32Failure(string action, int error) =>
        new($"{action} (Win32 {error}: {new Win32Exception(error).Message})");

    private sealed class RecoveredProcessHandle(SafeProcessHandle processHandle, CommandProcessIdentity identity) : ISidetalkCommandHandle
    {
        public CommandProcessIdentity? Identity => identity;
        public bool HasExited
        {
            get
            {
                var state = WaitForSingleObject(processHandle, 0);
                if (state == WaitObject0) return true;
                if (state == WaitTimeout) return false;
                throw Win32Failure("명령 프로세스 상태를 확인할 수 없습니다", Marshal.GetLastWin32Error());
            }
        }

        public int ExitCode
        {
            get
            {
                if (!GetExitCodeProcess(processHandle, out var code))
                    throw Win32Failure("명령 프로세스 종료 코드를 확인할 수 없습니다", Marshal.GetLastWin32Error());
                if (code == StillActive)
                    throw new InvalidOperationException("명령 프로세스가 아직 실행 중입니다.");
                return unchecked((int)code);
            }
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var state = WaitForSingleObject(processHandle, 100);
                    if (state == WaitObject0) return;
                    if (state == WaitTimeout) continue;
                    throw Win32Failure("명령 프로세스 종료를 기다릴 수 없습니다", Marshal.GetLastWin32Error());
                }
            });
        }

        public void Dispose() => processHandle.Dispose();
    }

    private sealed class MissingCommandHandle(CommandProcessIdentity? identity) : ISidetalkCommandHandle
    {
        public CommandProcessIdentity? Identity => identity;
        public bool HasExited => true;
        public int ExitCode => throw new InvalidOperationException("이전 명령 프로세스의 종료 코드를 확인할 수 없습니다.");
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle processHandle, out System.Runtime.InteropServices.ComTypes.FILETIME creationTime, out System.Runtime.InteropServices.ComTypes.FILETIME exitTime, out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime, out System.Runtime.InteropServices.ComTypes.FILETIME userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle processHandle, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle processHandle, out uint exitCode);
}
