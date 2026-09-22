using System.Diagnostics;
using System.Text;
using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class ControllerSetupRecoveryTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases =
    [
        ("Setup controller: diagnostics and render authority never write", ReadOnlyNeverWrites),
        ("Setup controller: failed recovery removes launch readiness across restart", FailureBlocksAcrossRestart),
        ("Setup controller: unfinished Start command prevents configuration writes", PendingCommandBlocks),
        ("Setup controller: two instances serialize repair with command lease", TwoInstancesSerialize),
        ("Setup controller: launch requires recurrence prevention approval", LaunchRequiresPreparation),
        ("Setup controller: revocation verifies host before creating journals", RevocationRequiresHost),
        ("Setup controller: diagnostic preserves running observation when recovery fails", DiagnosticPreservesRunningObservation)
    ];

    private static void ReadOnlyNeverWrites()
    {
        var h = new Harness("setup-controller-readonly", needsRepair: false);
        var original = File.ReadAllBytes(h.Store.ProfilePath);
        using var controller = h.Controller(readOnly: true);
        var plan = controller.InspectSetupRecoveryAsync().GetAwaiter().GetResult();
        Assert.True(plan.IsHealthy);
        Assert.False(controller.InspectAsync().GetAwaiter().GetResult().ActionsPermitted);
        Assert.Throws<InvalidOperationException>(() => controller.PrepareAsync(plan, true).GetAwaiter().GetResult());
        Assert.Throws<InvalidOperationException>(() => controller.PrepareAutomaticallyAsync().GetAwaiter().GetResult());
        Assert.Throws<InvalidOperationException>(() => controller.RevokeAutomaticRecoveryAsync().GetAwaiter().GetResult());
        controller.ExportDiagnosticsAsync(Path.Combine(h.Store.Root, "diagnostics.json")).GetAwaiter().GetResult();
        Assert.Equal(0, h.Environment.WriteCount);
        Assert.Equal(0, h.Runner.Starts);
        Assert.False(Directory.Exists(Path.Combine(h.Store.Root, "command-journal")));
        Assert.False(Directory.Exists(controller.RecoveryHistoryPath));
        Assert.SequenceEqual(original, File.ReadAllBytes(h.Store.ProfilePath));
    }

    private static void FailureBlocksAcrossRestart()
    {
        var h = new Harness("setup-controller-failure");
        h.Environment.IgnoreWrite = true;
        using (var controller = h.Controller())
        {
            var plan = controller.InspectSetupRecoveryAsync().GetAwaiter().GetResult();
            Assert.True(plan.CanApply);
            Assert.Throws<InvalidOperationException>(() => controller.PrepareAsync(plan, false).GetAwaiter().GetResult());
            Assert.False(controller.InspectAsync().GetAwaiter().GetResult().CanLaunch);
        }
        using var restarted = h.Controller();
        var state = restarted.InspectAsync().GetAwaiter().GetResult();
        Assert.False(state.CanLaunch);
        Assert.False(state.CanStop);
        Assert.Equal(0, h.Runner.Starts);
        Assert.True(Directory.Exists(restarted.RecoveryHistoryPath));
    }

    private static void PendingCommandBlocks()
    {
        var h = new Harness("setup-controller-pending");
        var journal = new CommandJournal(h.Store.Root);
        using (journal.AcquireLease()) journal.Prepare(h.Profile, SidetalkCommandAction.Launch, h.Runtime.StartExe);
        using var controller = h.Controller();
        var plan = controller.InspectSetupRecoveryAsync().GetAwaiter().GetResult();
        Assert.Throws<InvalidOperationException>(() => controller.PrepareAsync(plan, false).GetAwaiter().GetResult());
        Assert.Equal(0, h.Environment.WriteCount);
        Assert.Equal(0, Directory.GetFiles(journal.JournalDirectory, "*.completed.json").Length);
        Assert.Equal(0, h.Runner.Starts);
    }

    private static void TwoInstancesSerialize()
    {
        var h = new Harness("setup-controller-concurrent");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        h.Environment.BeforeWrite = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Test synchronization failed.");
        };
        using var first = h.Controller();
        using var second = h.Controller();
        var plan = first.InspectSetupRecoveryAsync().GetAwaiter().GetResult();
        var pending = first.PrepareAsync(plan, false);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Throws<InvalidOperationException>(() => second.PrepareAsync(plan, false).GetAwaiter().GetResult());
            Assert.False(second.InspectAsync().GetAwaiter().GetResult().CanLaunch);
        }
        finally { release.Set(); }
        Assert.True(pending.GetAwaiter().GetResult().CanLaunch);
        Assert.Equal(1, h.Environment.WriteCount);
        Assert.Equal(0, h.Runner.Starts);
    }

    private static void LaunchRequiresPreparation()
    {
        var h = new Harness("setup-controller-launch-block");
        using var controller = h.Controller();
        Assert.False(controller.InspectAsync().GetAwaiter().GetResult().CanLaunch);
        Assert.Throws<InvalidOperationException>(() => controller.LaunchAsync().GetAwaiter().GetResult());
        Assert.Equal(0, h.Runner.Starts);
        Assert.Equal(0, h.Environment.WriteCount);
    }

    private static void RevocationRequiresHost()
    {
        var h = new Harness("setup-controller-revoke-host");
        h.Runtime.RejectHost = true;
        using var controller = h.Controller();
        Assert.Throws<InvalidOperationException>(() => controller.RevokeAutomaticRecoveryAsync().GetAwaiter().GetResult());
        Assert.False(Directory.Exists(Path.Combine(h.Store.Root, "command-journal")));
        Assert.False(Directory.Exists(Path.Combine(h.Store.Root, "setup-recovery")));
    }
    private static void DiagnosticPreservesRunningObservation()
    {
        var h = new Harness("setup-controller-diagnostic-stage", needsRepair: false);
        h.Runtime.ProcessIds = [12345];
        h.Environment.InspectionError = new MissingMethodException(".ctor");
        using var controller = h.Controller(readOnly: true);
        var path = Path.Combine(h.Store.Root, "diagnostics.json");
        controller.ExportDiagnosticsAsync(path).GetAwaiter().GetResult();
        using var report = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var snapshot = report.RootElement.GetProperty("snapshot");
        Assert.Equal("RunningUnverified", snapshot.GetProperty("State").GetString());
        Assert.True(snapshot.GetProperty("Kakao").GetProperty("Available").GetBoolean());
        Assert.Equal(12345, snapshot.GetProperty("BoxProcessIds")[0].GetInt32());
        var failure = report.RootElement.GetProperty("InspectionFailures")[0];
        Assert.Equal("복구 상태 조회", failure.GetProperty("Stage").GetString());
        Assert.Equal("System.MissingMethodException", failure.GetProperty("ExceptionType").GetString());
        Assert.True(failure.GetProperty("StackTrace").GetString()?.Contains("Inspect", StringComparison.Ordinal) == true);
        Assert.Equal(0, h.Environment.WriteCount);
        Assert.Equal(0, h.Runner.Starts);
    }
    private sealed class Harness
    {
        public ProfileStore Store { get; }
        public BoxProfile Profile { get; }
        public FakeEnvironment Environment { get; }
        public FakeRuntime Runtime { get; }
        public FakeRunner Runner { get; } = new();
        public Harness(string name, bool needsRepair = true)
        {
            Store = new ProfileStore(TestContext.CreateRunDirectory(name));
            Profile = Store.Create();
            Environment = new FakeEnvironment(Store, Profile, needsRepair);
            Runtime = new FakeRuntime(Store.Root, Profile);
        }
        public SidetalkController Controller(bool readOnly = false) =>
            new(Store, Runner, Runtime, new PendingRecovery(), readOnly: readOnly, setupEnvironment: Environment);
    }

    private sealed class FakeEnvironment(ProfileStore store, BoxProfile profile, bool needsRepair) : ISetupRecoveryEnvironment
    {
        private string option = needsRepair ? "y" : "n";
        public int WriteCount { get; private set; }
        public bool IgnoreWrite { get; set; }
        public Exception? InspectionError { get; set; }
        public Action? BeforeWrite { get; set; }
        public SetupRecoveryObservation Inspect(ProfileStore requested, BoxProfile? observed, CancellationToken cancellationToken = default)
        {
            if (InspectionError is not null) throw InspectionError;
            Assert.Equal(store, requested);
            Assert.Equal(profile, observed);
            var globals = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            var box = IsolationPolicy.Expected(profile);
            var user = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["SbieCtrl_AutoRunSoftCompat"] = [option] };
            var sections = new Dictionary<string, IReadOnlyDictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase)
            { ["GlobalSettings"] = globals, [profile.BoxName] = box, ["UserSettings_TEST"] = user };
            var text = string.Join("\r\n", sections.SelectMany(section =>
                new[] { "[" + section.Key + "]" }.Concat(section.Value.SelectMany(pair => pair.Value.Select(value => pair.Key + "=" + value)))));
            var changes = option == "n" ? Array.Empty<SetupRecoveryChange>() : new[]
            {
                new SetupRecoveryChange("UserSettings_TEST", "SbieCtrl_AutoRunSoftCompat", [option], ["n"],
                    SetupRecoveryChangeKind.SetUserSetting, "재발 방지")
            };
            return new(Path.Combine(store.Root, "Sandboxie.ini"), Encoding.Unicode.GetBytes(text),
                new(box, box, globals, BuiltinTemplates.ExpectedGlobals(globals)), sections, changes,
                profile.BoxName, profile.UserSid, profile.StoragePath, [], [], [], true,
                new(Path.Combine(store.Root, "SandMan.exe"), Path.Combine(store.Root, "Start.exe"), "1.18.4", true),
                new(true, []), true, true, true, "시험", DateTimeOffset.UtcNow);
        }
        public SetupRecoveryChangeResult ApplyChange(SetupRecoveryObservation observation, SetupRecoveryPlan plan,
            SetupRecoveryChange change, CancellationToken cancellationToken = default)
        {
            BeforeWrite?.Invoke();
            WriteCount++;
            if (!IgnoreWrite) option = "n";
            return new(true, !IgnoreWrite);
        }
    }

    private sealed class FakeRuntime(string root, BoxProfile profile) : ISidetalkRuntime
    {
        public string StartExe => Path.Combine(root, "Start.exe");
        public DependencyInfo Kakao() => new("카카오톡", Path.Combine(root, "KakaoTalk.exe"), "26.8", "시험", true);
        public DependencyInfo Sandboxie() => new("Sandboxie", Path.Combine(root, "SandMan.exe"), "1.18.4", "시험", true);
        public bool RejectHost { get; set; }
        public int[] ProcessIds { get; set; } = [];
        public void RequireHost() { if (RejectHost) throw new InvalidOperationException("Unverified host."); }
        public void VerifyCommandFiles(string startExe, string kakaoExe) { }
        public ISidetalkRuntimeSession Open(DependencyInfo sandboxie) => new Session(profile, ProcessIds);
        private sealed class Session(BoxProfile profile, int[] processIds) : ISidetalkRuntimeSession
        {
            public RuntimeVerifier.ServiceVerification ServiceIdentity => new(9999, false, "시험", "시험");
            public ConfigurationLayers ReadConfiguration(string boxName)
            {
                var box = IsolationPolicy.Expected(profile);
                var globals = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                return new(box, box, globals, BuiltinTemplates.ExpectedGlobals(globals));
            }
            public string FileRoot(string boxName) => profile.StoragePath;
            public IReadOnlyList<BoxProcess> Processes(BoxProfile requested) => processIds.Select(id => new BoxProcess(id, "KakaoTalk.exe", profile.UserSid, 1)).ToArray();
            public void Dispose() { }
        }
    }
    private sealed class FakeRunner : ISidetalkCommandRunner
    {
        public int Starts { get; private set; }
        public ISidetalkCommandHandle Start(ProcessStartInfo command) { Starts++; throw new InvalidOperationException("Must not launch."); }
    }
    private sealed class PendingRecovery : ICommandProcessRecovery
    {
        public ISidetalkCommandHandle Recover(CommandProcessIdentity identity) => new PendingHandle();
        public ISidetalkCommandHandle RecoverUnidentified(string expectedStartExe) => new PendingHandle();
    }
    private sealed class PendingHandle : ISidetalkCommandHandle
    {
        public CommandProcessIdentity? Identity => null;
        public bool HasExited => false;
        public int ExitCode => throw new InvalidOperationException("Still running.");
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
        public void Dispose() { }
    }
}
