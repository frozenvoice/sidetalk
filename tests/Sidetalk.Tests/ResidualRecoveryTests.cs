using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class ResidualRecoveryTests
{
    public static IEnumerable<(string Name, Action Run)> Cases =>
    [
        ("Residual recovery: missing rights and empty box retains explicit repair", EmptyUsesExistingRepair),
        ("Residual recovery: healthy helper-only policy preserves existing ordinary stop", HealthyHelpersKeepExistingStop),
        ("Residual recovery: Kakao and mixed processes never offer cleanup", KakaoBlocksCleanup),
        ("Residual recovery: helpers-only observation offers cleanup without general stop", HelpersAreObservedReadOnly),
        ("Residual recovery: unknown image and SID identity blocks cleanup", UnknownIdentityBlocks),
        ("Residual recovery: duplicate PIDs block cleanup", DuplicateIdentityBlocks),
        ("Residual recovery: failed API process query never means empty", QueryFailureBlocks),
        ("Residual recovery: ownership mismatch blocks cleanup", OwnerMismatchBlocks),
        ("Residual recovery: actual root mismatch blocks cleanup", RootMismatchBlocks),
        ("Residual recovery: AutoDelete mismatch blocks cleanup", AutoDeleteBlocks),
        ("Residual recovery: NeverDelete mismatch blocks cleanup", NeverDeleteBlocks),
        ("Residual recovery: AutoRecover mismatch blocks cleanup", AutoRecoverBlocks),
        ("Residual recovery: unknown box and global rules block cleanup", OtherPolicyBlocks),
        ("Residual recovery: inherited effective ambiguity blocks cleanup", EffectiveAmbiguityBlocks),
        ("Residual recovery: interrupted setup evidence blocks cleanup", InterruptedSetupBlocks),
        ("Residual recovery: present or newly appeared management UI is never terminated", ManagementUiBlocks),
        ("Residual recovery: pending Launch, Stop and cleanup identity block cleanup", PendingCommandsBlock),
        ("Residual recovery: Kakao returning after approval prevents Start", KakaoReturnsBeforeSpawn),
        ("Residual recovery: process set change after approval requires new approval", ProcessSetChangesBeforeSpawn),
        ("Residual recovery: rejected Start identity prevents process creation", StartIdentityBlocks),
        ("Residual recovery: final scope race leaves intent but creates no process", FinalScopeRaceBlocks),
        ("Residual recovery: successful exact box terminate repairs y and preserves data", SuccessPreservesEverything),
        ("Residual recovery: nonzero exit even with empty box writes no setting", NonzeroExitDoesNotRepair),
        ("Residual recovery: residual timeout writes no setting", ResidualTimeoutDoesNotRepair),
        ("Residual recovery: Kakao returning after terminate blocks repair", KakaoReturnsAfterTerminate),
        ("Residual recovery: config and UI race after terminate requires fresh approval", PlanChangesAfterTerminate),
        ("Residual recovery: race immediately before Apply writes no setting", RaceBeforeApply),
        ("Residual recovery: duplicate approved calls issue exactly one command", DuplicateCalls),
        ("Residual recovery: restart intent and started empty records permit fresh explicit repair", RestartEmptyCanRepair),
        ("Residual recovery: confirmed exit with unknown code never silently repairs", UnknownCodeRequiresFreshRepair),
        ("Residual recovery: read-only diagnostics cannot terminate or rewrite prior records", ReadOnlyCannotClean)
    ];

    private static void ManagementUiBlocks()
    {
        foreach (var appearanceStage in new[] { 0, 1, 2 })
        {
            using var h = new Harness();
            var identity = new SetupRecoveryProcessIdentity(h.Process("SandMan.exe", 21).Id,
                DateTime.UtcNow.Ticks, h.Runtime.Sandboxie().Path!, h.Profile.UserSid, 1);
            var ui = new SetupRecoveryUiState(true, [identity]);
            using var controller = h.Controller();
            if (appearanceStage == 0)
            {
                h.Environment.Ui = ui;
                var plan = Inspect(controller);
                Assert.Equal(1, plan.ManagementProcesses.Count);
                Assert.True(plan.ResidualCleanupApproval is null);
                Assert.Equal(0, h.Runner.Starts);
            }
            else
            {
                var approval = Approval(controller);
                if (appearanceStage == 1)
                {
                    h.Runner.AfterStart = () => h.Environment.Ui = ui;
                    AssertCleanupFails(controller, approval, h);
                }
                else
                {
                    var progress = new SynchronousProgress(message =>
                    {
                        if (message.StartsWith("동일한 승인 범위", StringComparison.Ordinal)) h.Environment.Ui = ui;
                    });
                    Assert.Throws<InvalidOperationException>(() => controller.CleanResidualBoxAndPrepareAsync(approval, progress).GetAwaiter().GetResult());
                }
                Assert.Equal(1, h.Runner.Starts);
            }
            Assert.Equal(0, h.Environment.Quiesces);
            Assert.Equal(0, h.Environment.Writes);
            h.AssertPreserved();
        }
    }

    private static void HealthyHelpersKeepExistingStop()
    {
        using var h = new Harness();
        h.Box["DropAdminRights"] = ["y"];
        using var controller = h.Controller();
        var snapshot = controller.InspectAsync().GetAwaiter().GetResult();
        Assert.True(snapshot.CurrentConfigurationVerified);
        Assert.True(snapshot.CanStop);
        Assert.False(snapshot.CanLaunch);
        Assert.Equal(BoxProcessState.ResidualOnly, snapshot.AdminRights?.ProcessState);
        Assert.True(Inspect(controller).ResidualCleanupApproval is null);
        Assert.Equal(0, h.Runner.Starts);
        Assert.Equal(0, h.Environment.Writes);
    }

    private static void EmptyUsesExistingRepair()
    {
        using var h = new Harness();
        h.Processes = [];
        using var controller = h.Controller();
        var plan = Inspect(controller);
        Assert.Equal(SetupRecoveryState.ApprovalRequired, plan.State);
        Assert.True(plan.ResidualCleanupApproval is null);
        Assert.True(plan.CanApply);
        Assert.False(plan.AllowAutomatic);
        var snapshot = controller.PrepareAsync(plan, false).GetAwaiter().GetResult();
        Assert.True(snapshot.CurrentConfigurationVerified);
        Assert.Equal(1, h.Environment.Writes);
        Assert.Equal(0, h.Runner.Starts);
        h.AssertPreserved();
    }

    private static void KakaoBlocksCleanup()
    {
        foreach (var mixed in new[] { false, true })
        {
            using var h = new Harness();
            h.Processes = mixed ? h.Helpers().Append(h.Process("KakaoTalk.exe", 10)).ToArray() : [h.Process("KakaoTalk.exe", 10)];
            using var controller = h.Controller();
            Assert.True(Inspect(controller).ResidualCleanupApproval is null);
            var snapshot = controller.InspectAsync().GetAwaiter().GetResult();
            Assert.Equal(BoxProcessState.KakaoRunning, snapshot.AdminRights!.ProcessState);
            Assert.Contains("트레이", snapshot.Detail);
            h.AssertNoControl();
        }
    }

    private static void HelpersAreObservedReadOnly()
    {
        using var h = new Harness();
        using var controller = h.Controller(readOnly: true);
        var bytes = h.Hashes();
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var snapshot = controller.InspectAsync().GetAwaiter().GetResult();
            Assert.False(snapshot.CanLaunch);
            Assert.False(snapshot.CanStop);
            Assert.Equal(Readiness.Blocked, snapshot.State);
            Assert.Equal(BoxProcessState.ResidualOnly, snapshot.AdminRights!.ProcessState);
            Assert.Equal(0, snapshot.AdminRights.KakaoProcessCount);
            Assert.Equal(4, snapshot.AdminRights.ResidualProcessCount);
            Assert.False(snapshot.Detail.Contains("트레이", StringComparison.Ordinal));
            Assert.True(Inspect(controller).ResidualCleanupApproval is not null);
            Assert.SequenceEqual(h.Processes.Select(process => process.ImageName), snapshot.AdminRights.BoxProcesses.Select(process => process.ImageName));
            Assert.SequenceEqual(h.Processes.Select(process => process.Id), snapshot.AdminRights.BoxProcesses.Select(process => process.ProcessId));
        }
        h.AssertHashes(bytes);
        h.AssertNoControl();
    }

    private static void UnknownIdentityBlocks()
    {
        foreach (var name in new[] { "", " ", "C:\\helper.exe", "helper.exe\n", "helper" })
        {
            using var h = new Harness();
            h.Processes = [h.Process(name, 1)];
            AssertNoApproval(h);
        }
        using var sid = new Harness();
        sid.Processes = [sid.Process("helper.exe", 1) with { UserSid = "unverified-user" }];
        AssertNoApproval(sid);
    }

    private static void DuplicateIdentityBlocks()
    {
        using var h = new Harness();
        h.Processes = [h.Process("helperA.exe", 1), h.Process("helperB.exe", 1)];
        AssertNoApproval(h);
    }

    private static void QueryFailureBlocks()
    {
        using var h = new Harness();
        h.QueryFails = true;
        AssertNoApproval(h);
        using var controller = h.Controller();
        var snapshot = controller.InspectAsync().GetAwaiter().GetResult();
        Assert.Equal(BoxProcessState.Unknown, snapshot.AdminRights!.ProcessState);
        Assert.False(snapshot.AdminRights.ProcessesQueried);
    }

    private static void OwnerMismatchBlocks()
    {
        using var h = new Harness();
        h.Box["BoxInfo"] = ["foreign owner"];
        AssertNoApproval(h);
    }

    private static void RootMismatchBlocks()
    {
        using var h = new Harness();
        h.Runtime.FileRootOverride = Path.Combine(h.Root, "foreign-storage");
        AssertNoApproval(h);
    }

    private static void AutoDeleteBlocks() => BadRule("AutoDelete", "y");
    private static void NeverDeleteBlocks() => BadRule("NeverDelete", "n");
    private static void AutoRecoverBlocks() => BadRule("AutoRecover", "y");
    private static void BadRule(string key, string value)
    {
        using var h = new Harness();
        h.Box[key] = [value];
        AssertNoApproval(h);
    }

    private static void OtherPolicyBlocks()
    {
        using (var h = new Harness())
        {
            h.Box["OpenFilePath"] = ["unreviewed path"];
            AssertNoApproval(h);
        }
        using (var h = new Harness())
        {
            h.Globals["OpenFilePath"] = ["unreviewed global path"];
            AssertNoApproval(h);
        }
        using (var h = new Harness())
        {
            h.Globals["Template"] = ["WindowsRasMan"];
            AssertNoApproval(h);
        }
    }

    private static void EffectiveAmbiguityBlocks()
    {
        using var h = new Harness();
        h.EffectiveAmbiguity = true;
        AssertNoApproval(h);
    }

    private static void InterruptedSetupBlocks()
    {
        using var h = new Harness();
        var folder = Path.Combine(h.Root, "setup-recovery", "operations");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".000000.Started.json"), "{}");
        AssertNoApproval(h);
    }

    private static void PendingCommandsBlock()
    {
        foreach (var action in new[] { SidetalkCommandAction.Launch, SidetalkCommandAction.Stop, SidetalkCommandAction.RecoveryBoxCleanup })
        {
            using var h = new Harness();
            h.AddPending(action, started: true);
            h.Recovery.Exited = false;
            AssertNoApproval(h);
        }
    }

    private static void KakaoReturnsBeforeSpawn()
    {
        using var h = new Harness();
        using var controller = h.Controller();
        var approval = Approval(controller);
        h.Processes = h.Processes.Append(h.Process("KakaoTalk.exe", 10)).ToArray();
        AssertCleanupFails(controller, approval, h);
        Assert.Equal(0, h.Runner.Starts);
    }

    private static void ProcessSetChangesBeforeSpawn()
    {
        using var h = new Harness();
        using var controller = h.Controller();
        var approval = Approval(controller);
        h.Processes = [h.Process("changed-helper.exe", 20)];
        AssertCleanupFails(controller, approval, h);
        Assert.Equal(0, h.Runner.Starts);
    }

    private static void StartIdentityBlocks()
    {
        using var h = new Harness();
        using var controller = h.Controller();
        var approval = Approval(controller);
        h.Runtime.RejectStart = true;
        AssertCleanupFails(controller, approval, h);
        Assert.Equal(0, h.Runner.Starts);
    }

    private static void FinalScopeRaceBlocks()
    {
        using var h = new Harness();
        using var controller = h.Controller();
        var approval = Approval(controller);
        var beforeCount = h.Runtime.VerifyCount;
        h.Runtime.BeforeVerify = () =>
        {
            if (h.Runtime.VerifyCount == beforeCount + 2) h.Processes = [h.Process("changed-helper.exe", 20)];
        };
        AssertCleanupFails(controller, approval, h);
        Assert.Equal(0, h.Runner.Starts);
        using var lease = h.Journal.TryAcquireInspectionLease();
        Assert.True(h.Journal.ReadPending(h.Profile, h.Runtime.StartExe) is not null);
    }

    private static void SuccessPreservesEverything()
    {
        foreach (var rights in new string[]?[] { null, [""], ["n"] })
        {
            using var h = new Harness();
            h.SetRights(rights);
            using var controller = h.Controller();
            var approval = Approval(controller);
            var result = controller.CleanResidualBoxAndPrepareAsync(approval).GetAwaiter().GetResult();
            Assert.True(result.CurrentConfigurationVerified);
            Assert.True(IsolationPolicy.Validate(h.Profile, h.Configuration()).Safe);
            Assert.SequenceEqual(new[] { "y" }, h.Box["DropAdminRights"]);
            Assert.Equal(1, h.Runner.Starts);
            Assert.Equal(1, h.Environment.Writes);
            Assert.Equal(0, h.Environment.Quiesces);
            Assert.Empty(h.Processes);
            Assert.Equal(h.Runtime.StartExe, h.Runner.LastCommand!.FileName);
            Assert.False(h.Runner.LastCommand.UseShellExecute);
            Assert.SequenceEqual(new[] { "/silent", "/box:" + h.Profile.BoxName, "/terminate" }, h.Runner.LastCommand.ArgumentList);
            Assert.False(result.Detail.Contains("종료 코드는 확인할 수 없", StringComparison.Ordinal));
            using (h.Journal.TryAcquireInspectionLease()) Assert.True(h.Journal.ReadPending(h.Profile, h.Runtime.StartExe) is null);
            h.AssertPreserved();
        }
    }

    private static void NonzeroExitDoesNotRepair()
    {
        using var h = new Harness();
        h.Runner.ExitCode = 5;
        using var controller = h.Controller();
        AssertCleanupFails(controller, Approval(controller), h);
        Assert.Equal(1, h.Runner.Starts);
        Assert.Empty(h.Processes);
    }

    private static void ResidualTimeoutDoesNotRepair()
    {
        using var h = new Harness();
        h.Runner.RemoveProcesses = false;
        using var controller = h.Controller();
        controller.ResidualObservationTimeout = TimeSpan.FromMilliseconds(100);
        AssertCleanupFails(controller, Approval(controller), h);
        Assert.Equal(1, h.Runner.Starts);
        Assert.Equal(4, h.Processes.Count);
    }

    private static void KakaoReturnsAfterTerminate()
    {
        using var h = new Harness();
        h.Runner.AfterStart = () => h.Processes = [h.Process("KakaoTalk.exe", 10)];
        using var controller = h.Controller();
        AssertCleanupFails(controller, Approval(controller), h);
        Assert.Equal(1, h.Runner.Starts);
    }

    private static void PlanChangesAfterTerminate()
    {
        foreach (var uiChange in new[] { false, true })
        {
            using var h = new Harness();
            h.Runner.AfterStart = () =>
            {
                if (uiChange) h.Environment.Ui = new(true, [], false, "changed UI observation");
                else h.Globals["SbieCtrl_WindowCoords"] = ["changed configuration"];
            };
            using var controller = h.Controller();
            AssertCleanupFails(controller, Approval(controller), h);
            Assert.Equal(1, h.Runner.Starts);
        }
    }

    private static void RaceBeforeApply()
    {
        using var h = new Harness();
        using var controller = h.Controller();
        var approval = Approval(controller);
        var progress = new SynchronousProgress(message =>
        {
            if (message.StartsWith("동일한 승인 범위", StringComparison.Ordinal))
                h.Globals["SbieCtrl_WindowCoords"] = ["changed after final scope"];
        });
        Assert.Throws<InvalidOperationException>(() => controller.CleanResidualBoxAndPrepareAsync(approval, progress).GetAwaiter().GetResult());
        Assert.Equal(0, h.Environment.Writes);
        Assert.Equal(1, h.Runner.Starts);
        h.AssertPreserved();
    }

    private static void DuplicateCalls()
    {
        using var h = new Harness();
        h.Runner.DeferExit = true;
        using var controller = h.Controller();
        var approval = Approval(controller);
        var first = controller.CleanResidualBoxAndPrepareAsync(approval);
        var second = controller.CleanResidualBoxAndPrepareAsync(approval);
        Assert.Equal(1, h.Runner.Starts);
        h.Runner.Handle!.Complete();
        Assert.True(first.GetAwaiter().GetResult().CurrentConfigurationVerified);
        Assert.Throws<InvalidOperationException>(() => second.GetAwaiter().GetResult());
        Assert.Equal(1, h.Runner.Starts);
        Assert.Equal(1, h.Environment.Writes);
    }

    private static void RestartEmptyCanRepair()
    {
        foreach (var started in new[] { false, true })
        {
            using var h = new Harness();
            h.AddPending(SidetalkCommandAction.RecoveryBoxCleanup, started);
            h.Processes = [];
            h.Recovery.Exited = true;
            h.Recovery.ExitCode = null;
            var before = h.Hashes();
            using var controller = h.Controller();
            var plan = Inspect(controller);
            Assert.True(plan.CanApply);
            Assert.True(plan.ResidualCleanupApproval is null);
            h.AssertHashes(before);
            var result = controller.PrepareAsync(plan, false).GetAwaiter().GetResult();
            Assert.True(result.CurrentConfigurationVerified);
            Assert.Equal(0, h.Runner.Starts);
            Assert.Equal(1, h.Environment.Writes);
            using (h.Journal.TryAcquireInspectionLease()) Assert.True(h.Journal.ReadPending(h.Profile, h.Runtime.StartExe) is null);
            h.AssertPreserved();
        }
    }

    private static void UnknownCodeRequiresFreshRepair()
    {
        using var h = new Harness();
        h.Runner.ExitCode = null;
        using (var controller = h.Controller())
        {
            AssertCleanupFails(controller, Approval(controller), h);
            Assert.Equal(0, h.Environment.Writes);
        }
        h.Recovery.Exited = true;
        h.Recovery.ExitCode = null;
        using var restarted = h.Controller();
        var plan = Inspect(restarted);
        Assert.True(plan.CanApply);
        Assert.False(plan.AllowAutomatic);
        Assert.True(restarted.PrepareAsync(plan, false).GetAwaiter().GetResult().CurrentConfigurationVerified);
        Assert.Equal(1, h.Environment.Writes);
        Assert.Equal(1, h.Runner.Starts);
    }

    private static void ReadOnlyCannotClean()
    {
        using var h = new Harness();
        using var controller = h.Controller(readOnly: true);
        var approval = Approval(controller);
        var before = h.Hashes();
        Assert.Throws<InvalidOperationException>(() => controller.CleanResidualBoxAndPrepareAsync(approval).GetAwaiter().GetResult());
        var reportPath = Path.Combine(h.Root, "diagnostic-output.json");
        controller.ExportDiagnosticsAsync(reportPath).GetAwaiter().GetResult();
        using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
        var observation = report.RootElement.GetProperty("snapshot").GetProperty("AdminRights");
        Assert.Equal(4, observation.GetProperty("BoxProcesses").GetArrayLength());
        Assert.Equal(0, observation.GetProperty("KakaoProcessCount").GetInt32());
        Assert.Equal(4, observation.GetProperty("ResidualProcessCount").GetInt32());
        File.Delete(reportPath);
        h.AssertHashes(before);
        h.AssertNoControl();
    }

    private static SetupRecoveryPlan Inspect(SidetalkController controller) => controller.InspectSetupRecoveryAsync().GetAwaiter().GetResult();
    private static ResidualBoxRecoveryApproval Approval(SidetalkController controller) =>
        Inspect(controller).ResidualCleanupApproval ?? throw new InvalidOperationException("Expected issued residual cleanup approval.");
    private static void AssertNoApproval(Harness h)
    {
        using var controller = h.Controller();
        Assert.True(Inspect(controller).ResidualCleanupApproval is null);
        h.AssertNoControl();
    }
    private static void AssertCleanupFails(SidetalkController controller, ResidualBoxRecoveryApproval approval, Harness h)
    {
        Assert.Throws<Exception>(() => controller.CleanResidualBoxAndPrepareAsync(approval).GetAwaiter().GetResult());
        Assert.Equal(0, h.Environment.Writes);
        h.AssertPreserved();
    }

    private sealed class Harness : IDisposable
    {
        public string Root { get; } = TestContext.CreateRunDirectory("residual-recovery");
        public ProfileStore Store { get; }
        public BoxProfile Profile { get; }
        public CommandJournal Journal { get; }
        public Dictionary<string, string[]> Box { get; }
        public Dictionary<string, string[]> Globals { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string[]> OtherBox { get; } = new(StringComparer.OrdinalIgnoreCase) { ["fixture"] = ["preserved other box"] };
        public IReadOnlyList<BoxProcess> Processes { get; set; }
        public bool QueryFails { get; set; }
        public bool EffectiveAmbiguity { get; set; }
        public FakeRuntime Runtime { get; }
        public FakeEnvironment Environment { get; }
        public FakeRunner Runner { get; }
        public FakeRecovery Recovery { get; } = new();
        private readonly int pidBase;
        private readonly Dictionary<string, byte[]> preserved = new(StringComparer.Ordinal);
        private readonly string otherBoxBytes;

        public Harness()
        {
            Store = new(Root);
            Profile = Store.Create();
            pidBase = (BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(Profile.BoxName))) & 0x7FFF) + 1000;
            Box = IsolationPolicy.Expected(Profile);
            Box.Remove("DropAdminRights");
            Processes = Helpers();
            Runtime = new(this);
            Environment = new(this);
            Runner = new(this);
            Journal = new(Root);
            using (Journal.AcquireLease())
            {
                var old = Journal.Prepare(Profile, SidetalkCommandAction.Stop, Runtime.StartExe);
                Journal.Complete(old);
            }
            foreach (var path in new[] { Store.ProfilePath, Path.Combine(Profile.StoragePath, "data", "account.fixture"),
                Path.Combine(Profile.StoragePath, "registry.fixture"), Path.Combine(Root, "quick-launch", "consent.fixture"),
                Path.Combine(Root, "recovery-evidence.fixture"), Runtime.Kakao().Path! })
            {
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, "synthetic preserved fixture " + Path.GetFileName(path));
                }
                preserved[path] = File.ReadAllBytes(path);
            }
            foreach (var path in Directory.GetFiles(Journal.JournalDirectory, "*.json")) preserved[path] = File.ReadAllBytes(path);
            otherBoxBytes = JsonSerializer.Serialize(OtherBox);
        }
        public BoxProcess Process(string name, int offset) => new(pidBase + offset, name, Profile.UserSid, 1);
        public IReadOnlyList<BoxProcess> Helpers() => new[] { "helperA.exe", "helperB.exe", "helperC.exe", "helperD.exe" }
            .Select((name, index) => Process(name, index + 1)).ToArray();
        public void SetRights(string[]? value)
        {
            if (value is null) Box.Remove("DropAdminRights");
            else Box["DropAdminRights"] = value.ToArray();
        }
        public SidetalkController Controller(bool readOnly = false) => new(Store, Runner, Runtime, Recovery,
            TimeSpan.FromMilliseconds(300), readOnly, Environment) { ResidualObservationTimeout = TimeSpan.FromMilliseconds(100) };
        public ConfigurationLayers Configuration()
        {
            var box = Copy(Box);
            var effective = Copy(Box);
            if (EffectiveAmbiguity) effective["OpenFilePath"] = ["inherited ambiguity"];
            return new(box, effective, Copy(Globals), RecoveryTemplateRules.ExpectedGlobals(Globals));
        }
        public void AddPending(SidetalkCommandAction action, bool started)
        {
            using var lease = Journal.AcquireLease();
            var pending = Journal.Prepare(Profile, action, Runtime.StartExe,
                action == SidetalkCommandAction.RecoveryBoxCleanup ? new string('A', 64) : null);
            if (started) Journal.RecordStarted(pending, new(pidBase + 50, DateTime.UtcNow.Ticks, Runtime.StartExe));
        }
        public Dictionary<string, string> Hashes() => Directory.GetFiles(Root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);
        public void AssertHashes(Dictionary<string, string> before)
        {
            var after = Hashes();
            Assert.Equal(before.Count, after.Count);
            foreach (var (path, hash) in before) Assert.Equal(hash, after[path]);
        }
        public void AssertNoControl()
        {
            Assert.Equal(0, Runner.Starts);
            Assert.Equal(0, Environment.Writes);
            Assert.Equal(0, Environment.Quiesces);
            AssertPreserved();
        }
        public void AssertPreserved()
        {
            Assert.Equal(Profile, Store.Load());
            Assert.Equal(otherBoxBytes, JsonSerializer.Serialize(OtherBox));
            foreach (var (path, bytes) in preserved) Assert.SequenceEqual(bytes, File.ReadAllBytes(path));
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private static Dictionary<string, string[]> Copy(IReadOnlyDictionary<string, string[]> value) =>
        value.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);

    private sealed class FakeEnvironment(Harness h) : ISetupRecoveryEnvironment
    {
        public int Writes { get; private set; }
        public int Quiesces { get; private set; }
        public SetupRecoveryUiState Ui { get; set; } = SetupRecoveryUiState.Empty;
        public SetupRecoveryObservation Inspect(ProfileStore store, BoxProfile? profile, CancellationToken cancellationToken = default)
        {
            Assert.Equal(h.Profile, profile);
            var config = h.Configuration();
            var sections = new Dictionary<string, IReadOnlyDictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase)
            {
                [h.Profile.BoxName] = Copy(h.Box), ["GlobalSettings"] = Copy(h.Globals),
                ["UserSettings_TEST"] = new Dictionary<string, string[]> { ["SbieCtrl_AutoRunSoftCompat"] = ["n"] },
                ["OtherFixtureBox"] = Copy(h.OtherBox)
            };
            var ini = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(sections));
            return new(Path.Combine(h.Root, "Sandboxie.ini"), ini, config, sections, [],
                IsolationPolicy.Validate(h.Profile, config).Owned ? h.Profile.BoxName : null,
                h.Profile.UserSid, h.Runtime.FileRootOverride ?? h.Profile.StoragePath, ["OtherFixtureBox"], [],
                h.Processes.Select(process => process.Id).ToArray(), !h.QueryFails,
                new(Path.GetDirectoryName(h.Runtime.Sandboxie().Path!)!, h.Runtime.StartExe, "1.18.4", true), Ui,
                true, true, IsolationPolicy.Validate(h.Profile, config).Safe, "synthetic fixture", DateTimeOffset.UtcNow)
                { CurrentUserSettingsSection = "UserSettings_TEST" };
        }
        public SetupRecoveryUiResult QuiesceManagementUi(SetupRecoveryObservation observation, SetupRecoveryPlan plan,
            CancellationToken cancellationToken = default)
        {
            Quiesces += observation.Ui.ApprovedManagementProcesses.Count;
            return SetupRecoveryUiResult.NoUi;
        }
        public SetupRecoveryChangeResult ApplyChange(SetupRecoveryObservation observation, SetupRecoveryPlan plan,
            SetupRecoveryChange change, CancellationToken cancellationToken = default)
        {
            Assert.Equal(h.Profile.BoxName, change.Section);
            Assert.Equal("DropAdminRights", change.Key);
            Assert.SequenceEqual(new[] { "y" }, change.DesiredValues);
            Assert.Empty(h.Processes);
            Writes++;
            h.Box[change.Key] = change.DesiredValues.ToArray();
            return new(true, true);
        }
    }

    private sealed class FakeRuntime(Harness h) : ISidetalkRuntime
    {
        public string StartExe => Path.Combine(h.Root, "Sandboxie", "Start.exe");
        public bool RejectStart { get; set; }
        public string? FileRootOverride { get; set; }
        public int VerifyCount { get; private set; }
        public Action? BeforeVerify { get; set; }
        public DependencyInfo Kakao() => new("공식 카카오톡", Path.Combine(h.Root, "host", "KakaoTalk.exe"), "26.8.1.1", "synthetic verified", true);
        public DependencyInfo Sandboxie() => new("Sandboxie", Path.Combine(h.Root, "Sandboxie", "SandMan.exe"), "1.18.4", "synthetic verified", true);
        public void RequireHost() { }
        public void VerifyCommandFiles(string startExe, string kakaoExe)
        {
            VerifyCount++;
            BeforeVerify?.Invoke();
            Assert.Equal(StartExe, startExe);
            Assert.Equal(Kakao().Path, kakaoExe);
            if (RejectStart) throw new InvalidDataException("synthetic Start identity mismatch");
        }
        public ISidetalkRuntimeSession Open(DependencyInfo sandboxie) => new Session(h);
        private sealed class Session(Harness h) : ISidetalkRuntimeSession
        {
            public RuntimeVerifier.ServiceVerification ServiceIdentity => new(1, false, "synthetic verified", "synthetic verified");
            public ConfigurationLayers ReadConfiguration(string boxName)
            {
                Assert.Equal(h.Profile.BoxName, boxName);
                return h.Configuration();
            }
            public string FileRoot(string boxName) => h.Runtime.FileRootOverride ?? h.Profile.StoragePath;
            public IReadOnlyList<BoxProcess> Processes(BoxProfile profile)
            {
                Assert.Equal(h.Profile, profile);
                if (h.QueryFails) throw new InvalidOperationException("synthetic process query failure");
                return h.Processes.ToArray();
            }
            public void Dispose() { }
        }
    }

    private sealed class FakeRunner(Harness h) : ISidetalkCommandRunner
    {
        public int Starts { get; private set; }
        public int? ExitCode { get; set; } = 0;
        public bool RemoveProcesses { get; set; } = true;
        public bool DeferExit { get; set; }
        public Action? AfterStart { get; set; }
        public ProcessStartInfo? LastCommand { get; private set; }
        public Handle? Handle { get; private set; }
        public ISidetalkCommandHandle Start(ProcessStartInfo command)
        {
            Starts++;
            LastCommand = command;
            if (RemoveProcesses) h.Processes = [];
            AfterStart?.Invoke();
            Handle = new(!DeferExit, ExitCode, new(h.Process("command.fixture", 50).Id, DateTime.UtcNow.Ticks, h.Runtime.StartExe));
            return Handle;
        }
    }

    private sealed class FakeRecovery : ICommandProcessRecovery
    {
        public bool Exited { get; set; } = true;
        public int? ExitCode { get; set; }
        public ISidetalkCommandHandle Recover(CommandProcessIdentity identity) => new Handle(Exited, ExitCode, identity);
        public ISidetalkCommandHandle RecoverUnidentified(string expectedStartExe) => new Handle(Exited, ExitCode, null);
    }

    private sealed class Handle(bool exited, int? exitCode, CommandProcessIdentity? identity) : ISidetalkCommandHandle
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool exited = exited;
        public CommandProcessIdentity? Identity => identity;
        public bool HasExited => exited;
        public int ExitCode => exitCode ?? throw new InvalidOperationException("synthetic unknown exit code");
        public Task WaitForExitAsync(CancellationToken cancellationToken) => exited ? Task.CompletedTask : completion.Task.WaitAsync(cancellationToken);
        public void Complete() { exited = true; completion.TrySetResult(); }
        public void Dispose() { }
    }

    private sealed class SynchronousProgress(Action<string> callback) : IProgress<string>
    {
        public void Report(string value) => callback(value);
    }
}
