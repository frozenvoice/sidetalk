using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Sidetalk.Core;

public sealed record CommandRecoveryContext(string UserSid, int SessionId, WindowsBootEvidence? Boot);
public sealed record WindowsBootEvidence(ulong UptimeMilliseconds, string ProtectedWitness);

/// <summary>
/// Read-only Windows evidence. A machine-bound DPAPI witness protects the recorded
/// monotonic uptime. Only a strictly lower native uptime proves an intervening boot;
/// equal/higher uptime is deliberately inconclusive. Wall-clock dates are not used.
/// </summary>
public static class WindowsCommandEvidence
{
    private const uint UiForbidden = 1;
    private const uint LocalMachine = 4;

    public static CommandRecoveryContext Capture(string operationId)
    {
        using var process = Process.GetCurrentProcess();
        var (sid, session) = ReadTokenContext(process.SafeHandle);
        var uptime = ReadUptimeMilliseconds();
        try
        {
            var witness = Convert.ToBase64String(Transform(Witness(operationId, sid, session, uptime), protect: true));
            return new(sid, session, new(uptime, witness));
        }
        catch (Win32Exception)
        {
            // SID/session still have independent token evidence if DPAPI is unavailable.
            return new(sid, session, null);
        }
    }

    public static ulong ReadUptimeMilliseconds() => GetTickCount64();

    public static bool ProvesPreviousBoot(string operationId, CommandRecoveryContext? context)
        => AuthenticatesBootEvidence(operationId, context) && ReadUptimeMilliseconds() < context!.Boot!.UptimeMilliseconds;

    public static bool AuthenticatesBootEvidence(string operationId, CommandRecoveryContext? context)
    {
        if (context?.Boot is not { } boot) return false;
        try
        {
            var actual = Transform(Convert.FromBase64String(boot.ProtectedWitness), protect: false);
            var expected = Witness(operationId, context.UserSid, context.SessionId, boot.UptimeMilliseconds);
            return actual.AsSpan().SequenceEqual(expected);
        }
        catch (Exception error) when (error is FormatException or Win32Exception or ArgumentException or InvalidDataException)
        {
            // A copied, damaged, or unreadable witness is not boot evidence. Ordinary
            // process identity recovery must still run and fail closed if unavailable.
            return false;
        }
    }

    public static (string UserSid, int SessionId) ReadTokenContext(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, 0x0008, out var token))
        {
            var error = Marshal.GetLastWin32Error();
            token?.Dispose();
            throw new Win32Exception(error, "명령 프로세스 사용자 토큰을 확인하지 못했습니다.");
        }
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            var sid = identity.User?.Value ?? throw new InvalidDataException("명령 프로세스 사용자 SID가 없습니다.");
            if (!GetTokenInformation(token, 12, out uint session, sizeof(uint), out var returned) || returned != sizeof(uint))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "명령 프로세스 토큰 세션을 확인하지 못했습니다.");
            return (sid, checked((int)session));
        }
    }

    private static byte[] Witness(string operationId, string sid, int session, ulong uptime) =>
        Encoding.UTF8.GetBytes(FormattableString.Invariant($"SIDETALK-command-boot-v1\n{operationId}\n{sid}\n{session}\n{uptime}"));

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new DataBlob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        DataBlob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden | LocalMachine, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "명령 부팅 근거를 확인하지 못했습니다.");
            if (output.Length <= 0 || output.Length > 16384 || output.Data == IntPtr.Zero)
                throw new InvalidDataException("명령 부팅 근거 크기가 유효하지 않습니다.");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }

    [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out uint information, int length, out int returnedLength);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
}
