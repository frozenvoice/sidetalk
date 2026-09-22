using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class WindowsSetupRecoveryTokenTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases =
    [
        ("Native TOKEN_QUERY opens the current process token", CurrentProcessTokenMatchesSid),
        ("Native recovery SID query uses the current process token", RecoverySidQueryMatchesSid),
        ("Native TOKEN_QUERY failure returns a closed safe handle", InvalidProcessTokenFailsClosed)
    ];

    private static void CurrentProcessTokenMatchesSid()
    {
        SafeAccessTokenHandle? token = null;
        try
        {
            using var process = Process.GetCurrentProcess();
            var opened = NativeMethods.OpenProcessToken(process.Handle, NativeMethods.TokenQuery, out token);
            Assert.True(opened, "OpenProcessToken(TOKEN_QUERY) must succeed for the current process.");
            Assert.True(token is not null && !token.IsInvalid && !token.IsClosed, "The returned token handle must be valid.");
            using var identity = new WindowsIdentity(token!.DangerousGetHandle());
            Assert.True(string.Equals(ProfileStore.CurrentSid(), identity.User?.Value, StringComparison.Ordinal), "Current token belongs to the current user.");
        }
        finally
        {
            token?.Dispose();
        }

        Assert.True(token is null || token.IsClosed, "The token handle must be closed after disposal.");
    }

    private static void RecoverySidQueryMatchesSid()
    {
        var method = typeof(WindowsSetupRecoveryEnvironment).GetMethod(
            "QueryProcessSid",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(int)],
            modifiers: null);
        Assert.True(method is not null, "The read-only SID query method must remain available.");
        var sid = method!.Invoke(null, [Environment.ProcessId]) as string;
        Assert.True(string.Equals(ProfileStore.CurrentSid(), sid, StringComparison.Ordinal), "Recovery token query matches the current user.");
    }

    private static void InvalidProcessTokenFailsClosed()
    {
        SafeAccessTokenHandle? token = null;
        var opened = NativeMethods.OpenProcessToken(IntPtr.Zero, NativeMethods.TokenQuery, out token);
        try
        {
            Assert.False(opened, "OpenProcessToken must reject a null process handle.");
            Assert.True(token is null || token.IsInvalid, "A failed token query must not expose a valid handle.");
        }
        finally
        {
            token?.Dispose();
        }
    }

    private static class NativeMethods
    {
        public const uint TokenQuery = 0x0008;

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(
            IntPtr process,
            uint access,
            out SafeAccessTokenHandle token);
    }
}
