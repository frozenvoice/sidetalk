using System.Runtime.InteropServices;

namespace Sidetalk.App;

/// <summary>
/// Windows Application User Model ID. Not the installer AppId: this identifies SIDETALK to
/// the shell for taskbar grouping and pinning. It must stay byte-for-byte equal to
/// AppUserModelID in installer/SIDETALK.iss, which the Start Menu shortcut carries, so a
/// pin from the running window resolves to that shortcut. It carries no version.
/// </summary>
internal static class AppIdentity
{
    internal const string AppUserModelId = "SIDETALK.SIDETALK";

    internal static void Apply()
    {
        // Cosmetic shell identity only; a failure must never block starting SIDETALK.
        _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
