using System.Diagnostics;
using System.Text.Json;
using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class ResidualDiagnosticsTests
{
    public static readonly (string Name, Action Run)[] Cases =
    [
        ("Residual diagnosis: process identity requires the owned SID, PID, name and uniqueness", ProcessIdentityRules),
        ("Residual diagnosis: empty, Kakao, mixed, residual and unknown states remain distinct", StateClassification),
        ("Residual diagnosis: read-only inspection keeps helper processes blocked", ReadOnlyInspection),
        ("Residual diagnosis: exported evidence contains only process names and PIDs", ExportedEvidence)
    ];

    private static void ProcessIdentityRules()
    {
        var profile = TestContext.CreateProfile("residual-identity");
        var known = new BoxProcess(1201, "Helper.exe", profile.UserSid, 3);
        Assert.True(AdminRightsObservation.IsKnownProcess(known, profile));
        Assert.False(AdminRightsObservation.IsKnownProcess(known with { Id = 0 }, profile));
        Assert.False(AdminRightsObservation.IsKnownProcess(known with { UserSid = profile.UserSid + "-other" }, profile));
        Assert.False(AdminRightsObservation.IsKnownProcess(known with { ImageName = "" }, profile));
        Assert.False(AdminRightsObservation.IsKnownProcess(known with { ImageName = "helper.dll" }, profile));
        Assert.False(AdminRightsObservation.IsKnownProcess(known with { ImageName = "C:\\box\\Helper.exe" }, profile));

        var duplicate = AdminRightsObservation.FromProcesses(profile, [known, known with { ImageName = "Other.exe" }]);
        Assert.Equal(2, duplicate.Count);
        Assert.True(duplicate.All(process => !process.IdentityVerified), "A repeated PID must remain unknown for cleanup safety.");
        Assert.Equal((uint)3, duplicate[0].SessionId);
    }

    private static void StateClassification()
    {
        var profile = TestContext.CreateProfile("residual-states");
        var empty = Observe(profile, []);
        Assert.Equal(BoxProcessState.Empty, empty.ProcessState);
        Assert.Equal(0, empty.KakaoProcessCount);
        Assert.Equal(0, empty.ResidualProcessCount);
        Assert.Equal(0, empty.UnknownProcessCount);
        var pidOnly = new AdminRightsObservation([], [], AdminRightsCondition.Expected, true, [1201], null, []);
        Assert.Equal(BoxProcessState.Unknown, pidOnly.ProcessState);

        foreach (var values in new[] { Array.Empty<string>(), new[] { "" } })
        {
            var config = new ConfigurationLayers(
                new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["DropAdminRights"] = values },
                new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["DropAdminRights"] = values },
                new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase));
            Assert.Equal(AdminRightsCondition.MissingOrEmpty, AdminRightsObservation.FromConfiguration(config).Condition);
        }

        var kakao = Observe(profile, [new BoxProcess(1202, "KakaoTalk.exe", profile.UserSid, 3)]);
        Assert.Equal(BoxProcessState.KakaoRunning, kakao.ProcessState);
        Assert.Equal(1, kakao.KakaoProcessCount);
        Assert.Equal(0, kakao.ResidualProcessCount);

        var mixed = Observe(profile, [
            new BoxProcess(1203, "KakaoTalk.exe", profile.UserSid, 3),
            new BoxProcess(1204, "Updater.exe", profile.UserSid, 3)
        ]);
        Assert.Equal(BoxProcessState.KakaoRunning, mixed.ProcessState);
        Assert.Equal(1, mixed.KakaoProcessCount);
        Assert.Equal(1, mixed.ResidualProcessCount);

        var residual = Observe(profile, [new BoxProcess(1205, "Updater.exe", profile.UserSid, 3)]);
        Assert.Equal(BoxProcessState.ResidualOnly, residual.ProcessState);
        Assert.Equal(0, residual.KakaoProcessCount);
        Assert.Equal(1, residual.ResidualProcessCount);

        var unknown = Observe(profile, [
            new BoxProcess(1206, "", profile.UserSid, 3),
            new BoxProcess(1207, "Updater.dll", profile.UserSid, 3),
            new BoxProcess(1208, "Updater.exe", profile.UserSid + "-foreign", 3)
        ]);
        Assert.Equal(BoxProcessState.Unknown, unknown.ProcessState);
        Assert.Equal(0, unknown.KakaoProcessCount);
        Assert.Equal(0, unknown.ResidualProcessCount);
        Assert.Equal(3, unknown.UnknownProcessCount);
        Assert.Equal(BoxProcessState.Unknown, new AdminRightsObservation([], [], AdminRightsCondition.Expected, false, [], null, []).ProcessState);
    }

    private static void ReadOnlyInspection()
    {
        using var fixture = new Fixture("residual-inspection", [new BoxProcess(1210, "Updater.exe", "", 4)]);
        fixture.Processes[0] = fixture.Processes[0] with { UserSid = fixture.Profile.UserSid };
        var before = SnapshotFiles(fixture.Store.Root);
        var snapshot = fixture.Controller.InspectAsync().GetAwaiter().GetResult();
        Assert.Equal(Readiness.Blocked, snapshot.State);
        Assert.Equal(BoxProcessState.ResidualOnly, snapshot.AdminRights?.ProcessState);
        Assert.Equal(1, snapshot.AdminRights?.ResidualProcessCount);
        Assert.False(snapshot.CanLaunch);
        Assert.False(snapshot.CanStop);
        AssertFileSnapshot(before);
    }

    private static void ExportedEvidence()
    {
        using var fixture = new Fixture("residual-export", [new BoxProcess(1211, "Updater.exe", "", 4)]);
        fixture.Processes[0] = fixture.Processes[0] with { UserSid = fixture.Profile.UserSid };
        var report = Path.Combine(fixture.Store.Root, "diagnostic.json");
        fixture.Controller.ExportDiagnosticsAsync(report).GetAwaiter().GetResult();
        using var json = JsonDocument.Parse(File.ReadAllBytes(report));
        var process = json.RootElement.GetProperty("snapshot").GetProperty("AdminRights").GetProperty("BoxProcesses")[0];
        Assert.Equal(1211, process.GetProperty("ProcessId").GetInt32());
        Assert.Equal("Updater.exe", process.GetProperty("ImageName").GetString());
        Assert.False(process.TryGetProperty("UserSid", out _), "Diagnostic DTOs must not expose the account SID.");
        Assert.False(File.ReadAllText(report).Contains(fixture.Profile.UserSid, StringComparison.Ordinal));
    }

    private static AdminRightsObservation Observe(BoxProfile profile, IReadOnlyList<BoxProcess> processes) =>
        new([], [], AdminRightsCondition.MissingOrEmpty, true, processes.Select(process => process.Id).ToArray(), null, [])
        {
            BoxProcesses = AdminRightsObservation.FromProcesses(profile, processes)
        };

    private static Dictionary<string, byte[]> SnapshotFiles(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private static void AssertFileSnapshot(IReadOnlyDictionary<string, byte[]> before)
    {
        Assert.Equal(before.Count, before.Keys.Count(File.Exists));
        foreach (var file in before)
            Assert.SequenceEqual(file.Value, File.ReadAllBytes(file.Key));
    }

    private sealed class Fixture : IDisposable
    {
        public ProfileStore Store { get; }
        public BoxProfile Profile { get; }
        public List<BoxProcess> Processes { get; }
        public SidetalkController Controller { get; }

        public Fixture(string name, IReadOnlyList<BoxProcess> processes)
        {
            Store = new ProfileStore(TestContext.CreateRunDirectory(name));
            Profile = Store.Create();
            Processes = processes.ToList();
            Controller = new SidetalkController(Store, new Runner(), new Runtime(Profile, Processes), new Recovery(), readOnly: true);
        }

        public void Dispose() => Controller.Dispose();
    }

    private sealed class Runtime(BoxProfile profile, List<BoxProcess> processes) : ISidetalkRuntime
    {
        public DependencyInfo Kakao() => new("공식 카카오톡", Path.Combine(profile.StoragePath, "KakaoTalk.exe"), "26.8.1", "synthetic", true);
        public DependencyInfo Sandboxie() => new("Sandboxie Plus", Path.Combine(profile.StoragePath, "SandMan.exe"), "1.18.4", "synthetic", true);
        public void RequireHost() { }
        public void VerifyCommandFiles(string startExe, string kakaoExe) => throw new InvalidOperationException("No command expected.");
        public ISidetalkRuntimeSession Open(DependencyInfo sandboxie) => new Session(profile, processes);

        private sealed class Session(BoxProfile profile, List<BoxProcess> processes) : ISidetalkRuntimeSession
        {
            public RuntimeVerifier.ServiceVerification ServiceIdentity { get; } = new(45001, false, "synthetic", null);
            public ConfigurationLayers ReadConfiguration(string boxName)
            {
                var expected = IsolationPolicy.Expected(profile);
                expected.Remove("DropAdminRights");
                return new(expected, expected, new Dictionary<string, string[]>(), BuiltinTemplates.ExpectedGlobals(new Dictionary<string, string[]>()));
            }
            public string FileRoot(string boxName) => profile.StoragePath;
            public IReadOnlyList<BoxProcess> Processes(BoxProfile requested) => processes.ToArray();
            public IReadOnlyList<KakaoImageEvidence> ReadKakaoImageEvidence(BoxProfile requested, DependencyInfo host, IReadOnlyList<BoxProcess> requestedProcesses) =>
                AdminRightsObservation.UnavailableImages(requestedProcesses);
            public void Dispose() { }
        }
    }

    private sealed class Runner : ISidetalkCommandRunner
    {
        public ISidetalkCommandHandle Start(ProcessStartInfo command) => throw new InvalidOperationException("No command expected.");
    }

    private sealed class Recovery : ICommandProcessRecovery
    {
        public ISidetalkCommandHandle Recover(CommandProcessIdentity identity) => throw new InvalidOperationException("No recovery expected.");
        public ISidetalkCommandHandle RecoverUnidentified(string expectedStartExe) => throw new InvalidOperationException("No recovery expected.");
    }
}
