namespace Sidetalk.Core;

/// <summary>
/// The one reviewed Sandboxie Plus release. Compatibility checks, user-facing version text and the
/// installer download all read these values, so supporting another release means reviewing its API,
/// driver, built-in templates and isolation policy and then replacing this file's values together.
/// </summary>
public static class SandboxieSupport
{
    public const string PlusVersion = "1.18.4";
    public const string CoreVersion = "5.73.4";
    // The signed official 1.18.4 x64 installer ships this attestation-signed driver.
    // User-mode components remain 5.73.4; arbitrary mixed versions are not accepted.
    public const string DriverVersion = "5.73.2";
    internal const string DriverSha256 = "B2BFAC3C9FD4C378400EE4821A34773CCBAF71BB1E836C7A03ACC9A9AB3CFE96";
    public const string Publisher = "NOVASYNC LABS PTE. LTD.";

    /// <summary>
    /// The official GitHub release asset for <see cref="PlusVersion"/>. Size and SHA-256 equal the
    /// release's asset digest and its sha256-checksums.txt; the file carries a valid Authenticode
    /// signature by <see cref="Publisher"/> and reports product version <see cref="PlusVersion"/>.
    /// </summary>
    public static SandboxieInstallerRelease Installer { get; } = new(
        PlusVersion,
        $"Sandboxie-Plus-x64-v{PlusVersion}.exe",
        new Uri($"https://github.com/sandboxie-plus/Sandboxie/releases/download/v{PlusVersion}/Sandboxie-Plus-x64-v{PlusVersion}.exe"),
        new Uri($"https://github.com/sandboxie-plus/Sandboxie/releases/tag/v{PlusVersion}"),
        24_853_640,
        "CFCEDA1B1A63ABCD2B3E5FBFDC4895F15DB56316F5A9F2F06AEA230E209B120E",
        Publisher);
}

public sealed record SandboxieInstallerRelease(
    string Version, string FileName, Uri DownloadUri, Uri ReleasePage, long Size, string Sha256, string Publisher);
