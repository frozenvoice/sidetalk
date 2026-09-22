using System.Text;
using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class SetupRecoveryTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases =
    [
        ("Setup recovery healthy state performs no writes", HealthyNoOp),
        ("Setup recovery requires approval", ApprovalRequiredDoesNotWrite),
        ("Setup recovery applies and rechecks known conflict", AppliesKnownConflict),
        ("Setup recovery repeats approved scope automatically", RepeatsApprovedScopeAutomatically),
        ("Setup recovery treats UserSettings metadata as metadata", UserSettingsIsNotOtherBox),
        ("Setup recovery blocks other boxes", OtherBoxBlocksGlobalRepair),
        ("Setup recovery rejects forged plan changes", ForgedPlanIsRejected),
        ("Setup recovery rejects exit zero without file/API change", ExitZeroWithoutMutationFailsClosed),
        ("Setup recovery handles multiple approved settings", MultipleSettingsAreVerified),
        ("Setup recovery rejects UI replacement race", UiReplacementRaceFailsClosed),
        ("Setup recovery records cancellation as unfinished", CancellationLeavesEvidence)
    ];

    private static void HealthyNoOp()
    {
        var harness = Harness.Create();
        var service = new SetupRecovery(harness.Store, harness.Environment);
        var plan = service.Inspect();
        Assert.Equal(SetupRecoveryState.Healthy, plan.State);
        var result = service.TryAutomatic();
        Assert.True(result.Succeeded);
        Assert.Equal(0, harness.Environment.ApplyCount);
        Assert.False(Directory.Exists(service.HistoryPath));
    }

    private static void ApprovalRequiredDoesNotWrite()
    {
        var harness = Harness.Create(conflict: true);
        var service = new SetupRecovery(harness.Store, harness.Environment);
        var plan = service.Inspect();
        Assert.Equal(SetupRecoveryState.ApprovalRequired, plan.State);
        Assert.True(plan.RequiresApproval);
        Assert.Equal(0, harness.Environment.ApplyCount);
        Assert.False(Directory.Exists(service.HistoryPath));
    }

    private static void AppliesKnownConflict()
    {
        var harness = Harness.Create(conflict: true);
        var service = new SetupRecovery(harness.Store, harness.Environment);
        var result = service.Apply(service.Inspect(), allowAutomatic: false);
        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(1, harness.Environment.ApplyCount);
        Assert.Empty(harness.Environment.CurrentGlobal("Template"));
        Assert.False(service.HasUnfinishedRecovery);
        Assert.True(File.Exists(result.BackupPath!));
    }

    private static void RepeatsApprovedScopeAutomatically()
    {
        var harness = Harness.Create(conflict: true);
        var service = new SetupRecovery(harness.Store, harness.Environment);
        var first = service.Apply(service.Inspect(), allowAutomatic: true);
        Assert.True(first.Succeeded, first.Detail);
        harness.Environment.ReintroduceKnownConflict();
        var plan = service.Inspect();
        Assert.Equal(SetupRecoveryState.AutomaticallyApplicable, plan.State);
        var second = service.TryAutomatic();
        Assert.True(second.Succeeded, second.Detail);
        Assert.Equal(2, harness.Environment.ApplyCount);
    }

    private static void UserSettingsIsNotOtherBox()
    {
        var harness = Harness.Create(conflict: true);
        harness.Environment.OtherBoxNames = ["UserSettings_0CE20227"];
        var plan = new SetupRecovery(harness.Store, harness.Environment).Inspect();
        Assert.Equal(SetupRecoveryState.ApprovalRequired, plan.State);
    }

    private static void OtherBoxBlocksGlobalRepair()
    {
        var harness = Harness.Create(conflict: true);
        harness.Environment.OtherBoxNames = ["WorkBox"];
        var plan = new SetupRecovery(harness.Store, harness.Environment).Inspect();
        Assert.Equal(SetupRecoveryState.Blocked, plan.State);
        Assert.Contains("WorkBox", string.Join(";", plan.Blockers));
    }

    private static void ForgedPlanIsRejected()
    {
        var harness = Harness.Create(conflict: true);
        var service = new SetupRecovery(harness.Store, harness.Environment);
        var plan = service.Inspect() with
        {
            Changes = [new SetupRecoveryChange("GlobalSettings", "Template", ["WindowsRasMan"], [""])]
        };
        var result = service.Apply(plan, allowAutomatic: false);
        Assert.False(result.Succeeded);
        Assert.Equal(0, harness.Environment.ApplyCount);
    }

    private static void ExitZeroWithoutMutationFailsClosed()
    {
        var harness = Harness.Create(conflict: true);
        harness.Environment.NoOpCommand = true;
        var service = new SetupRecovery(harness.Store, harness.Environment);
        var result = service.Apply(service.Inspect(), allowAutomatic: false);
        Assert.False(result.Succeeded);
        Assert.True(service.HasUnfinishedRecovery);
        Assert.Equal(0, harness.Environment.ApplyCount);
    }

    private static void MultipleSettingsAreVerified()
    {
        var harness = Harness.Create(conflict: true);
        harness.Environment.AddSecondConflict();
        var service = new SetupRecovery(harness.Store, harness.Environment);
        var result = service.Apply(service.Inspect(), allowAutomatic: false);
        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(2, harness.Environment.ApplyCount);
    }

    private static void UiReplacementRaceFailsClosed()
    {
        var harness = Harness.Create(conflict: true);
        harness.Environment.ReplaceUiAfterQuiesce = true;
        var service = new SetupRecovery(harness.Store, harness.Environment);
        var result = service.Apply(service.Inspect(), allowAutomatic: false);
        Assert.False(result.Succeeded);
        Assert.Equal(0, harness.Environment.ApplyCount);
    }

    private static void CancellationLeavesEvidence()
    {
        var harness = Harness.Create(conflict: true);
        var service = new SetupRecovery(harness.Store, harness.Environment);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(_ => cancellation.Cancel());
        var result = service.Apply(service.Inspect(), allowAutomatic: false, progress: progress, cancellationToken: cancellation.Token);
        Assert.False(result.Succeeded);
        Assert.True(service.HasUnfinishedRecovery);
    }
    private sealed class Harness
    {
        private Harness(string root, ProfileStore store, BoxProfile profile, FakeEnvironment environment)
        {
            Root = root;
            Store = store;
            Profile = profile;
            Environment = environment;
        }

        public string Root { get; }
        public ProfileStore Store { get; }
        public BoxProfile Profile { get; }
        public FakeEnvironment Environment { get; }

        public static Harness Create(bool conflict = false)
        {
            var root = TestContext.CreateRunDirectory("setup-recovery");
            var store = new ProfileStore(root);
            var profile = store.Create();
            var environment = new FakeEnvironment(profile, Path.Combine(root, "Sandboxie.ini"));
            if (conflict) environment.ReintroduceKnownConflict();
            return new(root, store, profile, environment);
        }
    }

    private sealed class InlineProgress(Action<string> callback) : IProgress<string>
    {
        public void Report(string value) => callback(value);
    }

    private sealed class FakeEnvironment : ISetupRecoveryEnvironment
    {
        private readonly BoxProfile profile;
        private readonly string configPath;
        private Dictionary<string, string[]> globals = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string[]> user = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string[]> box;
        private byte[] iniBytes = Encoding.UTF8.GetBytes("clean");
        private bool uiReplacement;
        private bool secondConflict;

        public FakeEnvironment(BoxProfile profile, string configPath)
        {
            this.profile = profile;
            this.configPath = configPath;
            box = IsolationPolicy.Expected(profile);
            OtherBoxNames = Array.Empty<string>();
        }

        public int ApplyCount { get; private set; }
        public bool NoOpCommand { get; set; }
        public bool ReplaceUiAfterQuiesce { get; set; }
        public IReadOnlyList<string> OtherBoxNames { get; set; }

        public SetupRecoveryObservation Inspect(ProfileStore store, BoxProfile? inspectedProfile, CancellationToken cancellationToken = default)
        {
            var effectiveGlobals = RecoveryTemplateRules.ExpectedGlobals(globals);
            var sections = new Dictionary<string, IReadOnlyDictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase)
            {
                [profile.BoxName] = box.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
                ["GlobalSettings"] = globals.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
                ["UserSettings_TEST"] = user.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase)
            };
            var explicitBox = box.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            var effectiveBox = box.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            var explicitGlobals = globals.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            var effective = effectiveGlobals.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            var config = new ConfigurationLayers(explicitBox, effectiveBox, explicitGlobals, effective);
            var changes = new List<SetupRecoveryChange>();
            if (globals.TryGetValue("Template", out var templates) && templates.Length != 0)
                changes.Add(new SetupRecoveryChange("GlobalSettings", "Template", templates.ToArray(), Array.Empty<string>(),
                    SetupRecoveryChangeKind.RemoveGlobalTemplate, "호환성 템플릿 복구"));
            if (user.TryGetValue("SbieCtrl_AutoRunSoftCompat", out var autoRun) && autoRun.Length != 0)
                changes.Add(new SetupRecoveryChange("UserSettings_TEST", "SbieCtrl_AutoRunSoftCompat", autoRun.ToArray(), ["n"],
                    SetupRecoveryChangeKind.SetUserSetting, "호환성 자동 검사 재발 방지"));
            if (secondConflict && !user.ContainsKey("SbieCtrl_AutoRunSoftCompat"))
                changes.Add(new SetupRecoveryChange("UserSettings_TEST", "SbieCtrl_AutoRunSoftCompat", Array.Empty<string>(), ["n"],
                    SetupRecoveryChangeKind.SetUserSetting, "호환성 자동 검사 재발 방지"));

            return new SetupRecoveryObservation(
                configPath, iniBytes.ToArray(), config, sections, changes, profile.BoxName, profile.UserSid, profile.StoragePath,
                OtherBoxNames.ToArray(), Array.Empty<string>(), Array.Empty<int>(), true,
                new SetupRecoveryInstallContext(Path.Combine(TestContext.WorkspaceRoot, "Sandboxie.exe"),
                    Path.Combine(TestContext.WorkspaceRoot, "Start.exe"), "1.18.4", true),
                uiReplacement
                    ? new SetupRecoveryUiState(true, [new SetupRecoveryProcessIdentity(42, 1, Path.Combine(TestContext.WorkspaceRoot, "SbieCtrl.exe"))])
                    : SetupRecoveryUiState.Empty,
                true, true, true, string.Empty, DateTimeOffset.UtcNow);
        }

        public SetupRecoveryUiResult QuiesceManagementUi(SetupRecoveryObservation observation, SetupRecoveryPlan plan, CancellationToken cancellationToken = default)
        {
            if (ReplaceUiAfterQuiesce)
            {
                uiReplacement = true;
                return SetupRecoveryUiResult.NoUi;
            }
            return SetupRecoveryUiResult.NoUi;
        }

        public SetupRecoveryChangeResult ApplyChange(SetupRecoveryObservation observation, SetupRecoveryPlan plan, SetupRecoveryChange change, CancellationToken cancellationToken = default)
        {
            if (NoOpCommand) return new(true, false, "종료 코드는 성공했지만 변경 없음");
            ApplyCount++;
            var target = change.Section.StartsWith("UserSettings_", StringComparison.OrdinalIgnoreCase) ? user : globals;
            if (change.DesiredValues.Count == 0) target.Remove(change.Key);
            else target[change.Key] = change.DesiredValues.ToArray();            iniBytes = Encoding.UTF8.GetBytes("setting-" + ApplyCount);
            return new(true, true);
        }

        public void ReintroduceKnownConflict()
        {
            globals["Template"] = ["WindowsRasMan"];
            iniBytes = Encoding.UTF8.GetBytes("conflict");
        }

        public void AddSecondConflict()
        {
            globals["Template"] = ["WindowsRasMan"];
            user["SbieCtrl_AutoRunSoftCompat"] = ["y"];
            secondConflict = false;
            iniBytes = Encoding.UTF8.GetBytes("conflict-two");
        }

        public IReadOnlyList<string> CurrentGlobal(string key) => globals.TryGetValue(key, out var values) ? values : Array.Empty<string>();
    }
}
