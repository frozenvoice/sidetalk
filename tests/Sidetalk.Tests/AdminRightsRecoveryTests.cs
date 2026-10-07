using System.Text;
using System.Text.Json;
using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class AdminRightsRecoveryTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases =
    [
        ("Admin rights: required y performs no recovery writes", HealthyNoWrites),
        ("Admin rights: missing value requires exact approval", MissingRequiresApproval),
        ("Admin rights: n and empty values restore only y", RestorableValues),
        ("Admin rights: unknown and duplicate values fail closed", UnknownValues),
        ("Admin rights: unrelated box policy differences fail closed", OtherPolicyDifferences),
        ("Admin rights: inherited box differences fail closed", InheritedDifferences),
        ("Admin rights: ownership drift fails closed", OwnershipDrift),
        ("Admin rights: running box and failed query cannot repair", ProcessesPreventRepair),
        ("Admin rights: explicit repair cannot grant automatic consent", CannotGrantAutomaticConsent),
        ("Admin rights: old automatic consent cannot approve security repair", ExistingConsentDoesNotApprove),
        ("Admin rights: approved repair preserves other boxes and user data", PreservesOtherBoxesAndData),
        ("Admin rights: forged setting target cannot repair", ForgedPlan),
        ("Admin rights: exit zero without mutation remains unfinished", NoOpResult),
        ("Admin rights: approval race performs no writes", ApprovalRace),
        ("Admin rights: unrelated concurrent mutation remains unfinished", MutationRace),
        ("Admin rights: partial failure and restart preserve evidence", PartialFailureRestart)
    ];

    private static void HealthyNoWrites()
    {
        var harness = new Harness();
        var service = harness.Service();
        Assert.Equal(SetupRecoveryState.Healthy, service.Inspect().State);
        Assert.True(service.TryAutomatic().Succeeded);
        Assert.Equal(0, harness.Environment.ApplyCount);
        Assert.False(Directory.Exists(service.HistoryPath));
        harness.AssertPreserved();
    }

    private static void MissingRequiresApproval()
    {
        var harness = new Harness();
        harness.Environment.SetRights(null);
        var service = harness.Service();
        var plan = service.Inspect();
        Assert.Equal(SetupRecoveryState.ApprovalRequired, plan.State);
        Assert.True(plan.RequiresApproval);
        Assert.False(plan.AllowAutomatic);
        Assert.Contains("관리자 권한 제한이 변경", plan.Summary);
        var change = plan.Changes.Single();
        Assert.Equal(SetupRecoveryChangeKind.RepairOwnedBoxSetting, change.Kind);
        Assert.Equal(harness.Profile.BoxName, change.Section);
        Assert.Equal("DropAdminRights", change.Key);
        Assert.Empty(change.CurrentValues);
        Assert.SequenceEqual(new[] { "y" }, change.DesiredValues);
        Assert.False(IsolationPolicy.Validate(harness.Profile, harness.Environment.Configuration()).Safe);
        Assert.False(service.TryAutomatic().Succeeded);
        Assert.Equal(0, harness.Environment.ApplyCount);
        Assert.False(Directory.Exists(service.HistoryPath));
        harness.AssertPreserved();
    }

    private static void RestorableValues()
    {
        foreach (var values in new string[]?[] { null, [], [""], ["n"], ["N"] })
        {
            var harness = new Harness();
            harness.Environment.SetRights(values);
            var before = harness.Environment.Bytes();
            var service = harness.Service();
            var result = service.Apply(service.Inspect(), false);
            Assert.True(result.Succeeded, result.Detail);
            Assert.Equal(1, harness.Environment.ApplyCount);
            Assert.SequenceEqual(new[] { "y" }, harness.Environment.Box["DropAdminRights"]);
            Assert.True(IsolationPolicy.Validate(harness.Profile, harness.Environment.Configuration()).Safe);
            Assert.SequenceEqual(before, File.ReadAllBytes(result.BackupPath!));
            Assert.False(service.HasUnfinishedRecovery);
            Assert.Equal(SetupRecoveryState.Healthy, service.Inspect().State);
            harness.AssertPreserved();
        }
    }

    private static void UnknownValues()
    {
        foreach (var values in new[] { new[] { "unknown" }, new[] { " " }, new[] { "KakaoTalk.exe,n" }, new[] { "n", "n" }, new[] { "", "" }, new[] { "y", "n" } })
        {
            var harness = new Harness();
            harness.Environment.SetRights(values);
            AssertBlockedWithoutWrites(harness);
        }
    }

    private static void OtherPolicyDifferences()
    {
        foreach (var key in new[] { "AutoDelete", "OpenFilePath", "Template" })
        {
            var harness = new Harness();
            harness.Environment.SetRights(["n"]);
            harness.Environment.Box[key] = ["unreviewed"];
            AssertBlockedWithoutWrites(harness);
        }
        var global = new Harness();
        global.Environment.SetRights(["n"]);
        global.Environment.Globals["OpenFilePath"] = ["unreviewed"];
        AssertBlockedWithoutWrites(global);
    }

    private static void InheritedDifferences()
    {
        var harness = new Harness();
        harness.Environment.SetRights(["n"]);
        harness.Environment.InheritedBoxDifference = true;
        AssertBlockedWithoutWrites(harness);
    }

    private static void OwnershipDrift()
    {
        var harness = new Harness();
        harness.Environment.SetRights(["n"]);
        harness.Environment.Box["BoxInfo"] = ["another owner"];
        AssertBlockedWithoutWrites(harness);
        var storage = new Harness();
        storage.Environment.SetRights(["n"]);
        storage.Environment.ObservedStorage = Path.Combine(storage.Root, "unowned");
        AssertBlockedWithoutWrites(storage);
    }

    private static void ProcessesPreventRepair()
    {
        var running = new Harness();
        running.Environment.SetRights(["n"]);
        running.Environment.ProcessIds = [41];
        AssertBlockedWithoutWrites(running);
        running.Environment.ProcessIds = [];
        var stoppedService = running.Service();
        Assert.True(stoppedService.Apply(stoppedService.Inspect(), false).Succeeded);
        Assert.Equal(1, running.Environment.ApplyCount);
        var unknown = new Harness();
        unknown.Environment.SetRights(["n"]);
        unknown.Environment.ProcessesQueried = false;
        AssertBlockedWithoutWrites(unknown);
    }

    private static void CannotGrantAutomaticConsent()
    {
        var harness = new Harness();
        harness.Environment.SetRights(["n"]);
        var service = harness.Service();
        var result = service.Apply(service.Inspect(), true);
        Assert.False(result.Succeeded);
        Assert.Contains("새 명시적 승인", result.Detail);
        Assert.Equal(0, harness.Environment.ApplyCount);
        Assert.False(Directory.Exists(service.HistoryPath));
        Assert.False(Directory.Exists(Path.Combine(harness.Root, "setup-recovery", "consents")));
    }

    private static void ExistingConsentDoesNotApprove()
    {
        var harness = new Harness();
        harness.Environment.Globals["Template"] = ["WindowsRasMan"];
        var service = harness.Service();
        Assert.True(service.Apply(service.Inspect(), true).Succeeded);
        var consents = Directory.EnumerateFiles(Path.Combine(harness.Root, "setup-recovery", "consents")).ToDictionary(path => path, File.ReadAllBytes);
        harness.Environment.SetRights(["n"]);
        var plan = service.Inspect();
        Assert.Equal(SetupRecoveryState.ApprovalRequired, plan.State);
        Assert.True(plan.RequiresApproval);
        Assert.False(plan.AllowAutomatic);
        var priorWrites = harness.Environment.ApplyCount;
        Assert.False(service.TryAutomatic().Succeeded);
        Assert.Equal(priorWrites, harness.Environment.ApplyCount);
        Assert.True(service.Apply(plan, false).Succeeded);
        Assert.Equal(consents.Count, Directory.EnumerateFiles(Path.Combine(harness.Root, "setup-recovery", "consents")).Count());
        foreach (var pair in consents) Assert.SequenceEqual(pair.Value, File.ReadAllBytes(pair.Key));
        harness.AssertPreserved();
    }

    private static void PreservesOtherBoxesAndData()
    {
        var harness = new Harness();
        harness.Environment.SetRights(["n"]);
        harness.Environment.OtherBox["OpenFilePath"] = ["unrelated box setting"];
        var originalOther = JsonSerializer.Serialize(harness.Environment.OtherBox);
        var originalGlobals = JsonSerializer.Serialize(harness.Environment.Globals);
        var service = harness.Service();
        Assert.True(service.Apply(service.Inspect(), false).Succeeded);
        Assert.Equal(originalOther, JsonSerializer.Serialize(harness.Environment.OtherBox));
        Assert.Equal(originalGlobals, JsonSerializer.Serialize(harness.Environment.Globals));
        harness.AssertPreserved();
    }

    private static void ForgedPlan()
    {
        var harness = new Harness();
        harness.Environment.SetRights(["n"]);
        var service = harness.Service();
        var plan = service.Inspect();
        var forged = plan with { Changes = [plan.Changes.Single() with { Key = "AutoDelete" }] };
        Assert.False(service.Apply(forged, false).Succeeded);
        Assert.Equal(0, harness.Environment.ApplyCount);
        harness.AssertPreserved();
    }

    private static void NoOpResult()
    {
        var harness = new Harness();
        harness.Environment.SetRights(["n"]);
        harness.Environment.NoOp = true;
        var before = harness.Environment.Bytes();
        var service = harness.Service();
        var result = service.Apply(service.Inspect(), false);
        Assert.False(result.Succeeded);
        Assert.True(service.HasUnfinishedRecovery);
        Assert.SequenceEqual(before, harness.Environment.Bytes());
        Assert.SequenceEqual(before, File.ReadAllBytes(result.BackupPath!));
        Assert.Equal(SetupRecoveryState.Interrupted, harness.Service().Inspect().State);
        harness.AssertPreserved();
    }

    private static void ApprovalRace()
    {
        var harness = new Harness();
        harness.Environment.SetRights(["n"]);
        var service = harness.Service();
        var plan = service.Inspect();
        harness.Environment.Globals["SbieCtrl_WindowCoords"] = ["changed after approval"];
        Assert.False(service.Apply(plan, false).Succeeded);
        Assert.Equal(0, harness.Environment.ApplyCount);
        harness.AssertPreserved();
    }

    private static void MutationRace()
    {
        var harness = new Harness();
        harness.Environment.SetRights(["n"]);
        harness.Environment.ConcurrentMutation = true;
        var service = harness.Service();
        Assert.False(service.Apply(service.Inspect(), false).Succeeded);
        Assert.True(harness.Service().HasUnfinishedRecovery);
        Assert.Equal("concurrent setting preserved", harness.Environment.Globals["SbieCtrl_WindowCoords"].Single());
        harness.AssertPreserved();
    }

    private static void PartialFailureRestart()
    {
        var harness = new Harness();
        harness.Environment.SetRights(["n"]);
        harness.Environment.FailAfterMutation = true;
        var before = harness.Environment.Bytes();
        var service = harness.Service();
        var result = service.Apply(service.Inspect(), false);
        Assert.False(result.Succeeded);
        Assert.Contains("synthetic command failure", result.Detail);
        Assert.SequenceEqual(new[] { "y" }, harness.Environment.Box["DropAdminRights"]);
        Assert.SequenceEqual(before, File.ReadAllBytes(result.BackupPath!));
        var restarted = harness.Service();
        Assert.True(restarted.HasUnfinishedRecovery);
        var interrupted = restarted.Inspect();
        Assert.Equal(SetupRecoveryState.Interrupted, interrupted.State);
        Assert.True(interrupted.RequiresApproval);
        Assert.False(interrupted.AllowAutomatic);
        Assert.True(Directory.EnumerateFiles(Path.Combine(harness.Root, "setup-recovery", "operations"), "*.Failed.json").Any());
        harness.AssertPreserved();
    }

    private static void AssertBlockedWithoutWrites(Harness harness)
    {
        var service = harness.Service();
        Assert.Equal(SetupRecoveryState.Blocked, service.Inspect().State);
        Assert.False(service.Apply(service.Inspect(), false).Succeeded);
        Assert.False(service.TryAutomatic().Succeeded);
        Assert.Equal(0, harness.Environment.ApplyCount);
        harness.AssertPreserved();
    }

    private sealed class Harness
    {
        private readonly Dictionary<string, byte[]> preserved = new();
        public string Root { get; } = TestContext.CreateRunDirectory("admin-rights-recovery");
        public ProfileStore Store { get; }
        public BoxProfile Profile { get; }
        public FakeEnvironment Environment { get; }

        public Harness()
        {
            Store = new ProfileStore(Root);
            Profile = Store.Create();
            Environment = new(Profile, Path.Combine(Root, "Sandboxie.ini"));
            preserved[Store.ProfilePath] = File.ReadAllBytes(Store.ProfilePath);
            foreach (var path in new[] { Path.Combine(Profile.StoragePath, "database.fixture"), Path.Combine(Profile.StoragePath, "registry.fixture"), Path.Combine(Root, "command-journal", "existing.fixture") })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "synthetic preserved data " + Path.GetFileName(path));
                preserved[path] = File.ReadAllBytes(path);
            }
        }

        public SetupRecovery Service() => new(Store, Environment);
        public void AssertPreserved()
        {
            Assert.Equal(Profile, Store.Load());
            foreach (var pair in preserved) Assert.SequenceEqual(pair.Value, File.ReadAllBytes(pair.Key));
        }
    }

    private sealed class FakeEnvironment(BoxProfile profile, string configPath) : ISetupRecoveryEnvironment
    {
        public Dictionary<string, string[]> Box { get; } = IsolationPolicy.Expected(profile);
        public Dictionary<string, string[]> Globals { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string[]> OtherBox { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int[] ProcessIds { get; set; } = [];
        public bool ProcessesQueried { get; set; } = true;
        public bool InheritedBoxDifference { get; set; }
        public bool NoOp { get; set; }
        public bool ConcurrentMutation { get; set; }
        public bool FailAfterMutation { get; set; }
        public string? ObservedStorage { get; set; }
        public int ApplyCount { get; private set; }

        public void SetRights(string[]? values)
        {
            if (values is null) Box.Remove("DropAdminRights");
            else Box["DropAdminRights"] = values.ToArray();
        }

        public byte[] Bytes() => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Box, Globals, OtherBox }));

        public ConfigurationLayers Configuration()
        {
            var explicitBox = Copy(Box);
            var effectiveBox = Copy(Box);
            if (InheritedBoxDifference) effectiveBox["OpenFilePath"] = ["inherited exception"];
            return new(explicitBox, effectiveBox, Copy(Globals), RecoveryTemplateRules.ExpectedGlobals(Globals));
        }

        public SetupRecoveryObservation Inspect(ProfileStore store, BoxProfile? inspectedProfile, CancellationToken cancellationToken = default)
        {
            var configuration = Configuration();
            var candidateGlobals = Copy(Globals);
            candidateGlobals.Remove("Template");
            var policySafe = IsolationPolicy.Validate(profile, new ConfigurationLayers(configuration.ExplicitBox, configuration.EffectiveBox,
                candidateGlobals, RecoveryTemplateRules.ExpectedGlobals(candidateGlobals))).Safe;
            var changes = new List<SetupRecoveryChange>();
            if (Globals.TryGetValue("Template", out var templates))
                changes.Add(new("GlobalSettings", "Template", templates, [], SetupRecoveryChangeKind.RemoveGlobalTemplate));
            var owned = IsolationPolicy.Validate(profile, configuration).Owned;
            return new(configPath, Bytes(), configuration,
                new Dictionary<string, IReadOnlyDictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase)
                {
                    [profile.BoxName] = Copy(Box), ["GlobalSettings"] = Copy(Globals),
                    ["UserSettings_TEST"] = new Dictionary<string, string[]> { ["SbieCtrl_AutoRunSoftCompat"] = ["n"] },
                    ["OtherFixtureBox"] = Copy(OtherBox)
                }, changes, owned ? profile.BoxName : null, profile.UserSid, ObservedStorage ?? profile.StoragePath,
                OtherBox.Count == 0 ? [] : ["OtherFixtureBox"], [], ProcessIds, ProcessesQueried,
                new(Path.Combine(TestContext.WorkspaceRoot, "Sandboxie"), Path.Combine(TestContext.WorkspaceRoot, "Start.exe"), "1.18.4", true),
                SetupRecoveryUiState.Empty, true, true, policySafe, string.Empty, DateTimeOffset.UtcNow)
                { CurrentUserSettingsSection = "UserSettings_TEST" };
        }

        public SetupRecoveryChangeResult ApplyChange(SetupRecoveryObservation observation, SetupRecoveryPlan plan,
            SetupRecoveryChange change, CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            if (NoOp) return new(true, false, "synthetic exit zero without mutation");
            var target = change.Section == profile.BoxName ? Box : Globals;
            if (change.DesiredValues.Count == 0) target.Remove(change.Key);
            else target[change.Key] = change.DesiredValues.ToArray();
            if (ConcurrentMutation) Globals["SbieCtrl_WindowCoords"] = ["concurrent setting preserved"];
            if (FailAfterMutation) return new(false, false, "synthetic command failure after mutation");
            return new(true, true);
        }

        private static Dictionary<string, string[]> Copy(IReadOnlyDictionary<string, string[]> source) =>
            source.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }
}
