using Sidetalk.Core;

namespace Sidetalk.Tests;

/// <summary>
/// Pure checks for the service identity boundary. These tests do not read the
/// SCM, open a service process, or install/restart any component.
/// </summary>
public static class ServiceIdentityTests
{
    public static IEnumerable<(string Name, Action Run)> Cases =>
    [
        ("Service identity accepts exact quoted LocalSystem configuration", ExactQuotedConfigurationPasses),
        ("Service identity accepts unquoted path without whitespace", UnquotedPathWithoutWhitespacePasses),
        ("Service identity rejects unquoted path with whitespace", UnquotedPathWithWhitespaceFails),
        ("Service identity rejects command arguments and path mutations", ArgumentsAndMutationsFail),
        ("Service identity rejects wrong service type and account", TypeAndAccountFail),
        ("Service status accepts a stable running PID", StableRunningStatusPasses),
        ("Service status rejects stopped, zero, and replaced PID", UnstableStatusFails)
    ];

    private static void ExactQuotedConfigurationPasses()
    {
        var path = Path.Combine(TestContext.WorkspaceRoot, "Program Files", "Sandboxie", "SbieSvc.exe");
        var result = RuntimeVerifier.ValidateServiceConfiguration(16, $"\"{path}\"", "LocalSystem", path);

        Assert.True(result.Valid, result.Issue ?? "Expected exact SCM configuration to pass.");
        Assert.Equal<string?>(null, result.Issue);
    }

    private static void UnquotedPathWithoutWhitespacePasses()
    {
        var path = Path.Combine(TestContext.WorkspaceRoot, "Sandboxie", "SbieSvc.exe");
        var result = RuntimeVerifier.ValidateServiceConfiguration(16, path, "LocalSystem", path);

        Assert.True(result.Valid, result.Issue ?? "Expected an unquoted path without whitespace to pass.");
    }

    private static void UnquotedPathWithWhitespaceFails()
    {
        var path = Path.Combine(TestContext.WorkspaceRoot, "Sandboxie Plus", "SbieSvc.exe");
        var result = RuntimeVerifier.ValidateServiceConfiguration(16, path, "LocalSystem", path);

        Assert.False(result.Valid);
        Assert.Contains("따옴표", result.Issue ?? string.Empty);
    }

    private static void ArgumentsAndMutationsFail()
    {
        var path = Path.Combine(TestContext.WorkspaceRoot, "SbieSvc.exe");
        foreach (var command in new[]
        {
            $"\"{path}\" --unexpected",
            $"\"{path}x\"",
            $"\"{path}\"\"",
            path + " trailing"
        })
        {
            var result = RuntimeVerifier.ValidateServiceConfiguration(16, command, "LocalSystem", path);
            Assert.False(result.Valid, command);
        }
        var missing = RuntimeVerifier.ValidateServiceConfiguration(16, null, "LocalSystem", path);
        Assert.False(missing.Valid);
        Assert.Contains("경로", missing.Issue ?? string.Empty);
    }

    private static void TypeAndAccountFail()
    {
        var path = Path.Combine(TestContext.WorkspaceRoot, "SbieSvc.exe");
        var wrongType = RuntimeVerifier.ValidateServiceConfiguration(0x10 | 0x100, $"\"{path}\"", "LocalSystem", path);
        Assert.False(wrongType.Valid);
        Assert.Contains("SERVICE_WIN32_OWN_PROCESS", wrongType.Issue ?? string.Empty);

        var wrongAccount = RuntimeVerifier.ValidateServiceConfiguration(16, $"\"{path}\"", "LocalService", path);
        Assert.False(wrongAccount.Valid);
        Assert.Contains("LocalSystem", wrongAccount.Issue ?? string.Empty);
    }

    private static void StableRunningStatusPasses()
    {
        var result = RuntimeVerifier.ValidateStableRunningStatus(
            RuntimeVerifier.ServiceRunningState, 23876,
            RuntimeVerifier.ServiceRunningState, 23876);

        Assert.True(result.Valid, result.Issue ?? "Expected a stable running service to pass.");
    }

    private static void UnstableStatusFails()
    {
        var cases = new[]
        {
            (BeforeState: 1u, BeforePid: 23876u, AfterState: RuntimeVerifier.ServiceRunningState, AfterPid: 23876u),
            (BeforeState: RuntimeVerifier.ServiceRunningState, BeforePid: 0u, AfterState: RuntimeVerifier.ServiceRunningState, AfterPid: 0u),
            (BeforeState: RuntimeVerifier.ServiceRunningState, BeforePid: 23876u, AfterState: RuntimeVerifier.ServiceRunningState, AfterPid: 23877u),
            (BeforeState: RuntimeVerifier.ServiceRunningState, BeforePid: 23876u, AfterState: 1u, AfterPid: 23876u)
        };

        foreach (var item in cases)
        {
            var result = RuntimeVerifier.ValidateStableRunningStatus(item.BeforeState, item.BeforePid, item.AfterState, item.AfterPid);
            Assert.False(result.Valid);
            Assert.True(!string.IsNullOrWhiteSpace(result.Issue));
        }
    }
}
