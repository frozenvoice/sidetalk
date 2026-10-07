using System.Diagnostics;
using System.Text.Json;
using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class AdminRightsDiagnosticTests
{
    public static readonly (string Name, Action Run)[] Cases =
    [
        ("Admin rights diagnosis: expected policy does not invent historical approval", Expected),
        ("Admin rights diagnosis: missing and n retain blocking and exact cause", Changed),
        ("Admin rights diagnosis: duplicate and inherited values stay unrecognized", OrderedValues),
        ("Admin rights diagnosis: running evidence grants no stop or repair authority", Running),
        ("Admin rights diagnosis: failed process query is not empty proof", QueryFailure),
        ("Admin rights diagnosis: foreign ownership or native root prevents process query", IdentityFailure),
        ("Admin rights diagnosis: read-only report preserves profile account sentinel and journal", ReadOnlyPreserves),
        ("Admin rights diagnosis: physical-copy boundary requires exact contained executable", PhysicalBoundary),
        ("Admin rights diagnosis: image failure cannot convert policy block into ready", ImageFailure),
        ("Admin rights diagnosis: unreadable version is not a proved version difference", VersionComparison)
    ];

    private static void VersionComparison()
    {
        Assert.Equal<bool?>(null, WindowsKakaoImageDiagnostics.CompareVersions("알 수 없음", "26.8.1.5315"));
        Assert.Equal<bool?>(null, WindowsKakaoImageDiagnostics.CompareVersions("26.8.1.5315", ""));
        Assert.Equal<bool?>(null, WindowsKakaoImageDiagnostics.CompareVersions("invalid", "invalid"));
        Assert.Equal<bool?>(false, WindowsKakaoImageDiagnostics.CompareVersions("26.8.1.5315", "26.8.1.5315"));
        Assert.Equal<bool?>(true, WindowsKakaoImageDiagnostics.CompareVersions("26.7.1.5314", "26.8.1.5315"));
    }

    private static void Expected()
    {
        using var fixture = new Fixture("expected");
        var snapshot = fixture.Controller.InspectAsync().GetAwaiter().GetResult();
        Assert.Equal(Readiness.Ready, snapshot.State);
        Assert.Equal(AdminRightsCondition.Expected, snapshot.AdminRights?.Condition);
        Assert.False(snapshot.Title.Contains("변경", StringComparison.Ordinal));
        Assert.Equal(0, fixture.Runner.Starts);
        Assert.Equal(0, fixture.Recovery.Calls);
    }

    private static void Changed()
    {
        foreach (var values in new string[][] { [], [""], ["n"] })
        {
            using var fixture = new Fixture("changed");
            fixture.Runtime.Values = values;
            var snapshot = fixture.Controller.InspectAsync().GetAwaiter().GetResult();
            Assert.Equal(Readiness.Blocked, snapshot.State);
            Assert.Contains("관리자 권한 제한이 변경", snapshot.Title);
            Assert.Contains("바뀔 수 있습니다", snapshot.Detail);
            Assert.False(snapshot.CanLaunch);
            Assert.False(snapshot.CanStop);
            Assert.False(snapshot.CurrentConfigurationVerified);
            Assert.True(snapshot.AdminRights?.ProcessesQueried == true);
            Assert.SequenceEqual(values, snapshot.AdminRights!.ExplicitValues);
            Assert.Equal(0, fixture.Runner.Starts);
            Assert.Equal(0, fixture.Recovery.Calls);
        }
    }

    private static void OrderedValues()
    {
        using var fixture = new Fixture("values");
        fixture.Runtime.Values = ["n", "y", "n"];
        fixture.Runtime.EffectiveValues = ["y", "n", "n"];
        var snapshot = fixture.Controller.InspectAsync().GetAwaiter().GetResult();
        Assert.Equal(AdminRightsCondition.Unrecognized, snapshot.AdminRights?.Condition);
        Assert.SequenceEqual(new[] { "n", "y", "n" }, snapshot.AdminRights!.ExplicitValues);
        Assert.SequenceEqual(new[] { "y", "n", "n" }, snapshot.AdminRights.EffectiveValues);
        Assert.False(snapshot.Title.Contains("관리자 권한 제한이 변경", StringComparison.Ordinal));
        fixture.Runtime.Values = ["y"];
        fixture.Runtime.EffectiveValues = ["n"];
        Assert.Equal(AdminRightsCondition.Unrecognized, fixture.Controller.InspectAsync().GetAwaiter().GetResult().AdminRights?.Condition);
    }

    private static void Running()
    {
        using var fixture = new Fixture("running");
        fixture.Runtime.Values = ["n"];
        fixture.Runtime.ProcessIds = [45101];
        var snapshot = fixture.Controller.InspectAsync().GetAwaiter().GetResult();
        Assert.Equal(Readiness.Blocked, snapshot.State);
        Assert.Equal(ProcessPolicyStatus.EffectiveRulesUnverified, snapshot.ProcessPolicy);
        Assert.SequenceEqual(new[] { 45101 }, snapshot.BoxProcessIds);
        Assert.Contains("트레이 메뉴에서 정상 종료", snapshot.Detail);
        Assert.False(snapshot.CanStop);
        var image = snapshot.AdminRights!.Images.Single();
        Assert.Equal(ProgramPhysicalResolution.Unknown, image.PhysicalResolution);
        Assert.True(image.PhysicalFile is null);
        Assert.Equal(0, fixture.Runner.Starts);
    }

    private static void QueryFailure()
    {
        using var fixture = new Fixture("query-error");
        fixture.Runtime.Values = [];
        fixture.Runtime.ProcessError = new InvalidOperationException("synthetic process uncertainty");
        var snapshot = fixture.Controller.InspectAsync().GetAwaiter().GetResult();
        Assert.Equal(Readiness.Blocked, snapshot.State);
        Assert.Equal(ProcessPolicyStatus.NotObserved, snapshot.ProcessPolicy);
        Assert.False(snapshot.AdminRights!.ProcessesQueried);
        Assert.Contains("synthetic process uncertainty", snapshot.AdminRights.ProcessFailure!);
        Assert.Contains("빈 상자로 간주하지 않습니다", snapshot.Detail);
        Assert.False(snapshot.CanLaunch);
    }

    private static void IdentityFailure()
    {
        using var fixture = new Fixture("identity");
        fixture.Runtime.Values = ["n"];
        fixture.Runtime.WrongOwner = true;
        var snapshot = fixture.Controller.InspectAsync().GetAwaiter().GetResult();
        Assert.True(snapshot.AdminRights is null);
        Assert.Equal(0, fixture.Runtime.ProcessQueries);
        fixture.Runtime.WrongOwner = false;
        fixture.Runtime.WrongRoot = true;
        snapshot = fixture.Controller.InspectAsync().GetAwaiter().GetResult();
        Assert.True(snapshot.AdminRights is null);
        Assert.Equal(0, fixture.Runtime.ProcessQueries);
        Assert.False(snapshot.CanStop);
    }

    private static void ReadOnlyPreserves()
    {
        using var fixture = new Fixture("preservation");
        fixture.Runtime.Values = ["n"];
        Directory.CreateDirectory(fixture.Profile.StoragePath);
        var sentinel = Path.Combine(fixture.Profile.StoragePath, "synthetic-account-sentinel.txt");
        File.WriteAllText(sentinel, "synthetic preservation witness only");
        var journal = new CommandJournal(fixture.Store.Root);
        using (journal.AcquireLease()) journal.Prepare(fixture.Profile, SidetalkCommandAction.Launch, Path.Combine(fixture.Store.Root, "Start.exe"));
        var before = Directory.GetFiles(fixture.Store.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        var reportPath = Path.Combine(fixture.Store.Root, "new-diagnostic.json");
        fixture.Controller.ExportDiagnosticsAsync(reportPath).GetAwaiter().GetResult();
        foreach (var file in before) Assert.SequenceEqual(file.Value, File.ReadAllBytes(file.Key));
        Assert.Equal(before.Count + 1, Directory.GetFiles(fixture.Store.Root, "*", SearchOption.AllDirectories).Length);
        using var report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
        var snapshot = report.RootElement.GetProperty("snapshot");
        Assert.Equal("Blocked", snapshot.GetProperty("State").GetString());
        Assert.True(snapshot.GetProperty("CommandPending").GetBoolean());
        Assert.True(snapshot.GetProperty("AdminRights").GetProperty("ProcessesQueried").GetBoolean());
        Assert.Equal(0, fixture.Recovery.Calls);
        Assert.Equal(0, fixture.Runner.Starts);
    }

    private static void PhysicalBoundary()
    {
        using var fixture = new Fixture("physical-boundary");
        var root = fixture.Profile.StoragePath;
        Assert.True(WindowsKakaoImageDiagnostics.IsExactBoxExecutable(Path.Combine(root, "program", "KakaoTalk.exe"), root));
        Assert.False(WindowsKakaoImageDiagnostics.IsExactBoxExecutable(Path.Combine(root + "-other", "KakaoTalk.exe"), root));
        Assert.False(WindowsKakaoImageDiagnostics.IsExactBoxExecutable(Path.Combine(root, "account.db"), root));
        Assert.False(WindowsKakaoImageDiagnostics.IsExactBoxExecutable(Path.Combine(fixture.Store.Root, "KakaoTalk.exe"), root));
        Assert.False(WindowsKakaoImageDiagnostics.IsExactBoxExecutable("KakaoTalk.exe", root));
    }

    private static void ImageFailure()
    {
        using var fixture = new Fixture("image-failure");
        fixture.Runtime.Values = ["n"];
        fixture.Runtime.ProcessIds = [45102];
        fixture.Runtime.ImageError = new InvalidOperationException("synthetic image read denied");
        var snapshot = fixture.Controller.InspectAsync().GetAwaiter().GetResult();
        Assert.Equal(Readiness.Blocked, snapshot.State);
        Assert.True(snapshot.AdminRights!.ProcessesQueried);
        Assert.Contains("synthetic image read denied", snapshot.AdminRights.Images.Single().Failure!);
        Assert.False(snapshot.CanLaunch);
        Assert.False(snapshot.CanStop);
    }

    private sealed class Fixture : IDisposable
    {
        public ProfileStore Store { get; }
        public BoxProfile Profile { get; }
        public FakeRuntime Runtime { get; }
        public FakeRunner Runner { get; } = new();
        public FakeRecovery Recovery { get; } = new();
        public SidetalkController Controller { get; }
        public Fixture(string name)
        {
            Store = new ProfileStore(TestContext.CreateRunDirectory("admin-diagnosis-" + name));
            Profile = Store.Create();
            Runtime = new FakeRuntime(Store.Root, Profile);
            Controller = new SidetalkController(Store, Runner, Runtime, Recovery, readOnly: true);
        }
        public void Dispose() => Controller.Dispose();
    }
    private sealed class FakeRuntime(string root, BoxProfile profile) : ISidetalkRuntime
    {
        public string[] Values { get; set; } = ["y"];
        public string[]? EffectiveValues { get; set; }
        public int[] ProcessIds { get; set; } = [];
        public bool WrongOwner { get; set; }
        public bool WrongRoot { get; set; }
        public int ProcessQueries { get; private set; }
        public Exception? ProcessError { get; set; }
        public Exception? ImageError { get; set; }
        public DependencyInfo Kakao() => new("공식 카카오톡", Path.Combine(root, "KakaoTalk.exe"), "26.8.0", "synthetic signed host", true);
        public DependencyInfo Sandboxie() => new("Sandboxie Plus", Path.Combine(root, "SandMan.exe"), "1.18.4", "synthetic", true);
        public void RequireHost() { }
        public void VerifyCommandFiles(string startExe, string kakaoExe) => throw new InvalidOperationException("No command validation expected");
        public ISidetalkRuntimeSession Open(DependencyInfo sandboxie) => new Session(this, profile);
        private sealed class Session(FakeRuntime runtime, BoxProfile profile) : ISidetalkRuntimeSession
        {
            public RuntimeVerifier.ServiceVerification ServiceIdentity { get; } = new(45000, false, "synthetic", null);
            public ConfigurationLayers ReadConfiguration(string boxName)
            {
                var box = IsolationPolicy.Expected(profile);
                if (runtime.Values.Length == 0) box.Remove("DropAdminRights");
                else box["DropAdminRights"] = runtime.Values.ToArray();
                if (runtime.WrongOwner) box["BoxInfo"] = ["foreign"];
                var effective = box.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
                if (runtime.EffectiveValues is { } values) effective["DropAdminRights"] = values.ToArray();
                var globals = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                return new(box, effective, globals, BuiltinTemplates.ExpectedGlobals(globals));
            }
            public string FileRoot(string boxName) => runtime.WrongRoot ? profile.StoragePath + "-other" : profile.StoragePath;
            public IReadOnlyList<BoxProcess> Processes(BoxProfile requested)
            {
                runtime.ProcessQueries++;
                if (runtime.ProcessError is { } error) throw error;
                return runtime.ProcessIds.Select(id => new BoxProcess(id, "KakaoTalk.exe", profile.UserSid, 1)).ToArray();
            }
            public IReadOnlyList<KakaoImageEvidence> ReadKakaoImageEvidence(BoxProfile requested, DependencyInfo host, IReadOnlyList<BoxProcess> processes)
            {
                if (runtime.ImageError is { } error) throw error;
                return AdminRightsObservation.UnavailableImages(processes);
            }
            public void Dispose() { }
        }
    }
    private sealed class FakeRunner : ISidetalkCommandRunner
    {
        public int Starts { get; private set; }
        public ISidetalkCommandHandle Start(ProcessStartInfo command) { Starts++; throw new InvalidOperationException("No process creation expected"); }
    }
    private sealed class FakeRecovery : ICommandProcessRecovery
    {
        public int Calls { get; private set; }
        public ISidetalkCommandHandle Recover(CommandProcessIdentity identity) { Calls++; throw new InvalidOperationException("No reconciliation expected"); }
        public ISidetalkCommandHandle RecoverUnidentified(string expectedStartExe) { Calls++; throw new InvalidOperationException("No reconciliation expected"); }
    }
}
