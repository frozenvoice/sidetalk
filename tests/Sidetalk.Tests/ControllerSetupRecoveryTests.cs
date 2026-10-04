using System.Diagnostics;
using System.Text;
using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class ControllerSetupRecoveryTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases =
    [
        ("Setup controller: diagnostics and render authority never write", ReadOnlyNeverWrites),
        ("Setup controller: preserved Completed reconciliation survives writable and read-only restart", CompletedEvidenceSurvivesControllerRestart),
        ("Setup controller: failed recovery removes launch readiness across restart", FailureBlocksAcrossRestart),
        ("Setup controller: unfinished Start command prevents configuration writes", PendingCommandBlocks),
        ("Setup controller: staged recovery and terminated command reconcile without circular block", TerminatedCommandAndStagedEvidence),
        ("Setup controller: two instances serialize repair with command lease", TwoInstancesSerialize),
        ("Setup controller: optional recurrence prevention never gates healthy launch", LaunchAllowsOptionalPreparation),
        ("Setup controller: foreign management UI blocks repair only", ForeignManagementBlocksRepairOnly),
        ("Management identity classifier: current, foreign and inaccessible remain distinct", ClassifiesManagementIdentities),
        ("Setup controller: optional recovery inspection failure permits current verified execution", OptionalInspectionFailure),
        ("Setup controller: policy conflict and ownership mismatch remain execution blockers", PolicyStillBlocks),
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
        var evidence = controller.InspectInterruptedEvidenceAsync().GetAwaiter().GetResult();
        Assert.Throws<InvalidOperationException>(() => controller.ReconcileInterruptedEvidenceAsync(evidence).GetAwaiter().GetResult());
        controller.ExportDiagnosticsAsync(Path.Combine(h.Store.Root, "diagnostics.json")).GetAwaiter().GetResult();
        Assert.Equal(0, h.Environment.WriteCount);
        Assert.Equal(0, h.Runner.Starts);
        Assert.False(Directory.Exists(Path.Combine(h.Store.Root, "command-journal")));
        Assert.False(Directory.Exists(controller.RecoveryHistoryPath));
        Assert.SequenceEqual(original, File.ReadAllBytes(h.Store.ProfilePath));
    }

    private static void CompletedEvidenceSurvivesControllerRestart()
    {
        var h = new Harness("setup-controller-completed-restart");
        var service = new SetupRecovery(h.Store, h.Environment);
        var applied = service.Apply(service.Inspect(), false);
        Assert.True(applied.Succeeded, applied.Detail);
        Assert.Equal(1, h.Environment.WriteCount);
        Assert.True(service.Inspect().IsHealthy);
        var operations = Path.Combine(h.Store.Root, "setup-recovery", "operations");
        var completed = Directory.EnumerateFiles(operations, applied.OperationId + ".*.Completed.json").Single();
        var temporary = completed + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var completedBytes = File.ReadAllBytes(completed);
        File.Move(completed, temporary);
        Assert.False(Directory.EnumerateFiles(operations, applied.OperationId + ".*.Failed.json").Any());
        var beforePreservation = CaptureTree(h.Store.Root);
        var closeCount = h.Environment.CloseCount;
        var diagnostics = TestContext.CreateRunDirectory("setup-controller-completed-diagnostics");

        // This models the read-only authority shared by diagnose/render modes;
        // it does not exercise native GUI rendering or an installed environment.
        using (var readOnly = h.Controller(readOnly: true))
        {
            var snapshot = readOnly.InspectAsync().GetAwaiter().GetResult();
            Assert.False(snapshot.CanLaunch);
            Assert.False(snapshot.ActionsPermitted);
            var evidence = readOnly.InspectInterruptedEvidenceAsync().GetAwaiter().GetResult();
            Assert.True(evidence.CanPreserve, evidence.Detail);
            Assert.True(evidence.BlocksExecution, evidence.Detail);
            Assert.Equal(SetupRecoveryEvidenceKind.CompleteRecord, evidence.Items.Single().Kind);
            readOnly.ExportDiagnosticsAsync(Path.Combine(diagnostics, "before-preservation.json")).GetAwaiter().GetResult();
            Assert.Throws<InvalidOperationException>(() => readOnly.ReconcileInterruptedEvidenceAsync(evidence).GetAwaiter().GetResult());
        }
        AssertTreeUnchanged(beforePreservation, h.Store.Root);
        Assert.Equal(1, h.Environment.WriteCount);
        Assert.Equal(closeCount, h.Environment.CloseCount);
        Assert.Equal(0, h.Runner.Starts);

        string reconciledOperation;
        using (var writable = h.Controller())
        {
            var evidence = writable.InspectInterruptedEvidenceAsync().GetAwaiter().GetResult();
            Assert.True(evidence.CanPreserve, evidence.Detail);
            var preserved = writable.ReconcileInterruptedEvidenceAsync(evidence).GetAwaiter().GetResult();
            Assert.False(preserved.CanLaunch);
            var plan = writable.InspectSetupRecoveryAsync().GetAwaiter().GetResult();
            Assert.True(plan.IsReconciliation);
            Assert.Equal(applied.OperationId, plan.InterruptedOperationId);
            var prepared = writable.PrepareAsync(plan, false).GetAwaiter().GetResult();
            Assert.True(prepared.CanLaunch, prepared.Detail);
            using var reference = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(
                Path.Combine(operations, applied.OperationId + ".1000000.Reconciled.json")));
            reconciledOperation = reference.RootElement.GetProperty("RelatedOperationId").GetString()!;
            Assert.True(reconciledOperation != applied.OperationId);
            Assert.Equal(1, Directory.EnumerateFiles(operations, reconciledOperation + ".*.Completed.json").Count());
        }
        Assert.False(File.Exists(temporary));
        Assert.False(File.Exists(completed));
        Assert.False(Directory.EnumerateFiles(operations, applied.OperationId + ".*.Failed.json").Any());
        var preservedEvidence = new SetupRecovery(new ProfileStore(h.Store.Root), h.Environment).InspectInterruptedEvidence().Items.Single();
        Assert.SequenceEqual(completedBytes, File.ReadAllBytes(preservedEvidence.PreservationPath!));
        var afterReconciliation = CaptureTree(h.Store.Root);
        var writeCount = h.Environment.WriteCount;
        closeCount = h.Environment.CloseCount;
        var backupCount = Directory.EnumerateFiles(h.Store.Root, "sandboxie.ini.backup", SearchOption.AllDirectories).Count();
        var archiveCount = Directory.EnumerateFiles(h.Store.Root, "original.bin", SearchOption.AllDirectories).Count();
        Assert.Equal(2, backupCount);
        Assert.Equal(1, archiveCount);

        for (var iteration = 0; iteration < 3; iteration++)
        {
            foreach (var readOnly in new[] { false, true })
            {
                using var restarted = new SidetalkController(new ProfileStore(h.Store.Root), h.Runner, h.Runtime,
                    h.Recovery, readOnly: readOnly, setupEnvironment: h.Environment);
                var snapshot = restarted.InspectAsync().GetAwaiter().GetResult();
                Assert.True(snapshot.CanLaunch, snapshot.Detail);
                Assert.Equal(!readOnly, snapshot.ActionsPermitted);
                Assert.True(restarted.InspectSetupRecoveryAsync().GetAwaiter().GetResult().IsHealthy);
                var evidence = restarted.InspectInterruptedEvidenceAsync().GetAwaiter().GetResult();
                Assert.False(evidence.BlocksExecution, evidence.Detail);
                Assert.False(evidence.CanPreserve);
                Assert.False(evidence.Items.Single().NeedsPublication);
                restarted.ExportDiagnosticsAsync(Path.Combine(diagnostics, $"restart-{iteration}-{readOnly}.json")).GetAwaiter().GetResult();
                AssertTreeUnchanged(afterReconciliation, h.Store.Root);
                Assert.False(File.Exists(completed));
                Assert.Equal(1, Directory.EnumerateFiles(operations, reconciledOperation + ".*.Completed.json").Count());
                Assert.Equal(backupCount, Directory.EnumerateFiles(h.Store.Root, "sandboxie.ini.backup", SearchOption.AllDirectories).Count());
                Assert.Equal(archiveCount, Directory.EnumerateFiles(h.Store.Root, "original.bin", SearchOption.AllDirectories).Count());
                Assert.Equal(writeCount, h.Environment.WriteCount);
                Assert.Equal(closeCount, h.Environment.CloseCount);
                Assert.Equal(0, h.Runner.Starts);
            }
        }
    }

    private static (string[] Directories, Dictionary<string, byte[]> Files) CaptureTree(string root) =>
        (Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray(),
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes));

    private static void AssertTreeUnchanged((string[] Directories, Dictionary<string, byte[]> Files) expected, string root)
    {
        var actual = CaptureTree(root);
        Assert.SequenceEqual(expected.Directories, actual.Directories);
        Assert.SequenceEqual(expected.Files.Keys.Order(StringComparer.Ordinal), actual.Files.Keys.Order(StringComparer.Ordinal));
        foreach (var file in expected.Files) Assert.SequenceEqual(file.Value, actual.Files[file.Key]);
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

    private static void TerminatedCommandAndStagedEvidence()
    {
        foreach (var exited in new[] { false, true })
        {
            var h = new Harness("setup-controller-staged-and-command-" + exited);
            h.Recovery.Exited = exited;
            var journal = new CommandJournal(h.Store.Root);
            using (journal.AcquireLease()) journal.Prepare(h.Profile, SidetalkCommandAction.Launch, h.Runtime.StartExe);
            var stagingFolder = Path.Combine(h.Store.Root, "setup-recovery", "operations");
            Directory.CreateDirectory(stagingFolder);
            var original = Path.Combine(stagingFolder, "unknown.json." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(original, "{broken");
            var bytes = File.ReadAllBytes(original);
            using var controller = h.Controller();
            Assert.False(controller.InspectAsync().GetAwaiter().GetResult().CanLaunch);
            Assert.False(controller.InspectSetupRecoveryAsync().GetAwaiter().GetResult().CanApply);
            var inspection = controller.InspectInterruptedEvidenceAsync().GetAwaiter().GetResult();
            Assert.Equal(exited, inspection.CanPreserve);
            Assert.Equal(0, Directory.GetFiles(journal.JournalDirectory, "*.completed.json").Length);
            Assert.SequenceEqual(bytes, File.ReadAllBytes(original));
            if (exited)
            {
                controller.ReconcileInterruptedEvidenceAsync(inspection).GetAwaiter().GetResult();
                Assert.Equal(1, Directory.GetFiles(journal.JournalDirectory, "*.completed.json").Length);
                Assert.False(controller.InspectAsync().GetAwaiter().GetResult().CanLaunch);
                Assert.True(controller.InspectInterruptedEvidenceAsync().GetAwaiter().GetResult().Items.Count != 0);
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => controller.ReconcileInterruptedEvidenceAsync(inspection).GetAwaiter().GetResult());
                Assert.SequenceEqual(bytes, File.ReadAllBytes(original));
                Assert.Equal(0, Directory.GetFiles(journal.JournalDirectory, "*.completed.json").Length);
            }
            Assert.Equal(0, h.Environment.WriteCount);
            Assert.Equal(0, h.Runner.Starts);
        }
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
        var recovered = pending.GetAwaiter().GetResult();
        Assert.True(recovered.CanLaunch);
        Assert.False(string.IsNullOrWhiteSpace(recovered.RecoveryResultDetail));
        Assert.Equal(1, h.Environment.WriteCount);
        Assert.Equal(0, h.Runner.Starts);
    }

    private static void LaunchAllowsOptionalPreparation()
    {
        var h = new Harness("setup-controller-launch-block");
        using var controller = h.Controller();
        Assert.True(controller.InspectAsync().GetAwaiter().GetResult().CanLaunch);
        Assert.Equal(0, h.Environment.InspectCount);
        var plan = controller.InspectSetupRecoveryAsync().GetAwaiter().GetResult();
        Assert.True(plan.RequiresApproval);
        Assert.True(plan.CanApply);
        Assert.Equal(0, h.Environment.WriteCount);
        Assert.Equal(0, h.Runner.Starts);
        Assert.False(Directory.Exists(controller.RecoveryHistoryPath));
        // The fake runner throws at process creation; reaching it proves the fresh
        // execution gate accepted the current policy without recurrence consent.
        Assert.Throws<InvalidOperationException>(() => controller.LaunchAsync().GetAwaiter().GetResult());
        Assert.Equal(1, h.Runner.Starts);
        Assert.Equal(0, h.Environment.WriteCount);
        var failed = Directory.GetFiles(Path.Combine(h.Store.Root, "command-journal"), "*.failed.json");
        Assert.Equal(1, failed.Length);
        Assert.True(File.ReadAllText(failed[0]).Contains("Must not launch.", StringComparison.Ordinal));
        Assert.Equal(0, Directory.GetFiles(Path.Combine(h.Store.Root, "command-journal"), "*.completed.json").Length);
    }

    private static void ClassifiesManagementIdentities()
    {
        var image = Path.Combine(TestContext.CreateRunDirectory("management-classification"), "SandMan.exe");
        var classification = WindowsSetupRecoveryEnvironment.ClassifyManagementProcesses([1, 2, 3, 4, 5], pid => pid switch
        {
            1 => new(pid, 100, image, "CURRENT", 1),
            2 => new(pid, 200, image, "CURRENT", 2),
            3 => new(pid, 300, image, "FOREIGN", 1),
            4 => throw new UnauthorizedAccessException("Cannot open manager."),
            _ => new(pid, 500, image)
        }, "CURRENT", 1);
        Assert.False(classification.Fresh);
        Assert.SequenceEqual(new[] { 1 }, classification.ApprovedManagementProcesses.Select(item => item.ProcessId));
        Assert.SequenceEqual(new[] { 2, 3 }, classification.ForeignManagementProcesses.Select(item => item.ProcessId));
        Assert.SequenceEqual(new[] { 4, 5 }, classification.UnqueryableManagementProcessIds);
        var onlyCurrent = WindowsSetupRecoveryEnvironment.ClassifyManagementProcesses([1],
            pid => new(pid, 100, image, "CURRENT", 1), "CURRENT", 1);
        Assert.True(onlyCurrent.Fresh);
        Assert.Equal(1, onlyCurrent.ApprovedManagementProcesses.Count);
    }

    private static void ForeignManagementBlocksRepairOnly()
    {
        var h = new Harness("setup-controller-foreign-ui");
        h.Environment.ForeignManager = true;
        using var controller = h.Controller();
        Assert.True(controller.InspectAsync().GetAwaiter().GetResult().CanLaunch);
        var plan = controller.InspectSetupRecoveryAsync().GetAwaiter().GetResult();
        Assert.False(plan.CanApply);
        Assert.Throws<InvalidOperationException>(() => controller.PrepareAsync(plan, false).GetAwaiter().GetResult());
        Assert.True(controller.InspectAsync().GetAwaiter().GetResult().CanLaunch);
        Assert.Equal(0, h.Environment.WriteCount);
        Assert.Equal(0, h.Environment.CloseCount);
        Assert.Equal(0, h.Runner.Starts);
    }

    private static void OptionalInspectionFailure()
    {
        var h = new Harness("setup-controller-optional-inspection");
        h.Environment.InspectionError = new InvalidOperationException("Optional recurrence inspection failed.");
        using var controller = h.Controller();
        Assert.True(controller.InspectAsync().GetAwaiter().GetResult().CanLaunch);
        Assert.False(controller.InspectSetupRecoveryAsync().GetAwaiter().GetResult().CanApply);
        Assert.True(controller.InspectAsync().GetAwaiter().GetResult().CanLaunch);
        Assert.Equal(0, h.Environment.WriteCount);
    }

    private static void PolicyStillBlocks()
    {
        var h = new Harness("setup-controller-policy-safety");
        using var controller = h.Controller();
        h.Runtime.UnsafePolicy = true;
        Assert.False(controller.InspectAsync().GetAwaiter().GetResult().CanLaunch);
        h.Runtime.UnsafePolicy = false;
        h.Runtime.WrongOwner = true;
        Assert.False(controller.InspectAsync().GetAwaiter().GetResult().CanLaunch);
        h.Runtime.WrongOwner = false;
        Assert.True(controller.InspectAsync().GetAwaiter().GetResult().CanLaunch);
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
        public PendingRecovery Recovery { get; } = new();
        public Harness(string name, bool needsRepair = true)
        {
            Store = new ProfileStore(TestContext.CreateRunDirectory(name));
            Profile = Store.Create();
            Environment = new FakeEnvironment(Store, Profile, needsRepair);
            Runtime = new FakeRuntime(Store.Root, Profile);
        }
        public SidetalkController Controller(bool readOnly = false) =>
            new(Store, Runner, Runtime, Recovery, readOnly: readOnly, setupEnvironment: Environment);
    }

    private sealed class FakeEnvironment(ProfileStore store, BoxProfile profile, bool needsRepair) : ISetupRecoveryEnvironment
    {
        private string option = needsRepair ? "y" : "n";
        public int WriteCount { get; private set; }
        public int InspectCount { get; private set; }
        public int CloseCount { get; private set; }
        public bool ForeignManager { get; set; }
        public bool IgnoreWrite { get; set; }
        public Exception? InspectionError { get; set; }
        public Action? BeforeWrite { get; set; }
        public SetupRecoveryObservation Inspect(ProfileStore requested, BoxProfile? observed, CancellationToken cancellationToken = default)
        {
            InspectCount++;
            if (InspectionError is not null) throw InspectionError;
            Assert.Equal(store.Root, requested.Root);
            Assert.Equal(store.ProfilePath, requested.ProfilePath);
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
                new(!ForeignManager, []) { ForeignManagementProcesses = ForeignManager
                    ? [new(8765, DateTime.UtcNow.Ticks, Path.Combine(store.Root, "SandMan.exe"), "S-1-5-21-FOREIGN", 99)] : [] },
                true, true, true, "시험", DateTimeOffset.UtcNow);
        }
        public SetupRecoveryUiResult QuiesceManagementUi(SetupRecoveryObservation observation, SetupRecoveryPlan plan, CancellationToken cancellationToken = default)
        {
            CloseCount++;
            return SetupRecoveryUiResult.NoUi;
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
        public bool UnsafePolicy { get; set; }
        public bool WrongOwner { get; set; }
        public int[] ProcessIds { get; set; } = [];
        public void RequireHost() { if (RejectHost) throw new InvalidOperationException("Unverified host."); }
        public void VerifyCommandFiles(string startExe, string kakaoExe) { }
        public ISidetalkRuntimeSession Open(DependencyInfo sandboxie) => new Session(profile, ProcessIds, UnsafePolicy, WrongOwner);
        private sealed class Session(BoxProfile profile, int[] processIds, bool unsafePolicy, bool wrongOwner) : ISidetalkRuntimeSession
        {
            public RuntimeVerifier.ServiceVerification ServiceIdentity => new(9999, false, "시험", "시험");
            public ConfigurationLayers ReadConfiguration(string boxName)
            {
                var box = IsolationPolicy.Expected(profile);
                if (unsafePolicy) box["OpenFilePath"] = ["C:\\Users\\*"];
                if (wrongOwner) box["BoxInfo"] = ["foreign-owner"];
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
        public bool Exited { get; set; }
        public ISidetalkCommandHandle Recover(CommandProcessIdentity identity) => new PendingHandle(Exited);
        public ISidetalkCommandHandle RecoverUnidentified(string expectedStartExe) => new PendingHandle(Exited);
    }
    private sealed class PendingHandle(bool exited = false) : ISidetalkCommandHandle
    {
        public CommandProcessIdentity? Identity => null;
        public bool HasExited => exited;
        public int ExitCode => throw new InvalidOperationException("Still running.");
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
        public void Dispose() { }
    }
}
