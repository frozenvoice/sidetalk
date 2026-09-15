using System.Diagnostics;
using System.Text.Json;
using Sidetalk.Core;

namespace Sidetalk.Tests;

/// <summary>
/// Read-only probe for a real, already-installed Sandboxie Plus instance.
/// It never creates a profile, changes configuration, enumerates processes, or
/// starts/stops Sandboxie or KakaoTalk.  The caller must separately approve
/// installation, configuration import, and any real-account test.
/// </summary>
public static class NativeConfigurationProbe
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>
    /// Verifies the installed components and compares Sandboxie's actual
    /// GlobalSettings template expansion with the pinned 1.18.4 baseline.
    /// The report is created with FileMode.CreateNew to avoid overwriting an
    /// earlier record.
    /// </summary>
    public static int Run(string installationDirectory, string reportPath)
    {
        var report = new ProbeReport(
            DateTimeOffset.UtcNow,
            false,
            new VersionReport(RuntimeVerifier.PlusVersion, RuntimeVerifier.CoreVersion, null, null, null, null),
            new Dictionary<string, SectionReport>(StringComparer.Ordinal),
            new BaselineReport(false, false, null, null),
            null);

        try
        {
            var directory = Path.GetFullPath(installationDirectory);
            // Configuration diagnostics may continue after service identity inspection
            // fails, but only with verified components and read-only exports. Production
            // Launch/Stop still require the complete RuntimeVerifier check.
            foreach (var component in new[] { "Start.exe", "SbieDll.dll", "SbieSvc.exe" })
                RuntimeVerifier.RequireComponent(Path.Combine(directory, component));
            RuntimeVerifier.RequireComponent(Path.Combine(directory, "SbieDrv.sys"), driver: true);
            var versions = ReadVersions(directory);
            report = report with { Versions = versions };
            try
            {
                var identity = RuntimeVerifier.RequireInstallation(directory);
                report = report with { InstallationVerified = true, ServiceIdentity = identity };
            }
            catch (Exception installationError)
            {
                report = report with { InstallationError = Redact(installationError, installationDirectory, reportPath) };
            }
            using var api = new SandboxieApi(directory);
            var reader = new SandboxieConfigurationReader(api.Query);

            // This checks both the explicit origin and the effective values for
            // every pinned built-in section.  It does not change Sandboxie's
            // configuration.
            BuiltinTemplates.Verify(reader);

            var explicitGlobals = reader.ReadSection("GlobalSettings", includeTemplates: false);
            var effectiveGlobals = reader.ReadSection("GlobalSettings", includeTemplates: true);
            var expectedGlobals = BuiltinTemplates.ExpectedGlobals(explicitGlobals);
            var globalsMatch = SandboxieConfigurationReader.Same(expectedGlobals, effectiveGlobals);

            var sections = new Dictionary<string, SectionReport>(StringComparer.Ordinal)
            {
                ["GlobalSettings.explicit"] = Describe(explicitGlobals),
                ["GlobalSettings.effective"] = Describe(effectiveGlobals)
            };
            var baseline = new BaselineReport(
                TemplatesVerified: true,
                EffectiveGlobalsMatchOfficialDefaultExpansion: globalsMatch,
                ExpectedEffectiveGlobalSettingCount: expectedGlobals.Count,
                ExpectedEffectiveGlobalSettingNames: expectedGlobals.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());

            report = new ProbeReport(
                report.CreatedAt,
                globalsMatch && report.InstallationVerified,
                versions,
                sections,
                baseline,
                globalsMatch ? null : "GlobalSettings의 실제 템플릿 확장이 검토된 1.18.4 기준과 일치하지 않습니다.")
            {
                InstallationVerified = report.InstallationVerified,
                ServiceIdentity = report.ServiceIdentity,
                InstallationError = report.InstallationError
            };
        }
        catch (Exception error)
        {
            report = report with
            {
                Error = Redact(error, installationDirectory, reportPath)
            };
        }

        try
        {
            WriteNewReport(reportPath, report);
        }
        catch (Exception)
        {
            // A pre-existing report must never be replaced.  If the requested
            // new report cannot be created, there is no safe path to record it.
            return 1;
        }

        return report.Success ? 0 : 1;
    }

    private static VersionReport ReadVersions(string directory)
    {
        static string Version(string path) =>
            FileVersionInfo.GetVersionInfo(path).FileVersion?.Trim() ?? "알 수 없음";

        return new VersionReport(
            RuntimeVerifier.PlusVersion,
            RuntimeVerifier.CoreVersion,
            Version(Path.Combine(directory, "SandMan.exe")),
            Version(Path.Combine(directory, "Start.exe")),
            Version(Path.Combine(directory, "SbieDll.dll")),
            Version(Path.Combine(directory, "SbieSvc.exe"))) { ExpectedDriverVersion = RuntimeVerifier.DriverVersion, SbieDrv = Version(Path.Combine(directory, "SbieDrv.sys")) };
    }

    private static SectionReport Describe(IReadOnlyDictionary<string, string[]> section) =>
        new(section.Count, section.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());

    private static void WriteNewReport(string reportPath, ProbeReport report)
    {
        var fullPath = Path.GetFullPath(reportPath);
        using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, report, Json);
    }

    private static string Redact(Exception error, string installationDirectory, string reportPath)
    {
        var message = error.GetBaseException().Message;
        if (string.IsNullOrWhiteSpace(message)) message = error.GetType().Name;
        message = message.Replace(Path.GetFullPath(installationDirectory), "<Sandboxie 설치 폴더>", StringComparison.OrdinalIgnoreCase)
            .Replace(Path.GetFullPath(reportPath), "<보고서 경로>", StringComparison.OrdinalIgnoreCase);
        return message.Length <= 512 ? message : message[..512] + "…";
    }

    private sealed record VersionReport(
        string ExpectedPlusVersion,
        string ExpectedCoreVersion,
        string? SandMan,
        string? Start,
        string? SbieDll,
        string? SbieSvc)
    {
        public string ExpectedDriverVersion { get; init; } = RuntimeVerifier.DriverVersion;
        public string? SbieDrv { get; init; }
    }

    private sealed record SectionReport(int SettingCount, string[] SettingNames);

    private sealed record BaselineReport(
        bool TemplatesVerified,
        bool EffectiveGlobalsMatchOfficialDefaultExpansion,
        int? ExpectedEffectiveGlobalSettingCount,
        string[]? ExpectedEffectiveGlobalSettingNames);

    private sealed record ProbeReport(
        DateTimeOffset CreatedAt,
        bool Success,
        VersionReport Versions,
        IReadOnlyDictionary<string, SectionReport> Sections,
        BaselineReport Baseline,
        string? Error)
    {
        public bool InstallationVerified { get; init; }
        public RuntimeVerifier.ServiceVerification? ServiceIdentity { get; init; }
        public string? InstallationError { get; init; }
    }
}
