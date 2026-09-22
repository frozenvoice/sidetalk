using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class SetupRecoveryJournalTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases =
    [
        ("Setup recovery blocks an orphan completed journal event", OrphanCompletedBlocks),
        ("Setup recovery blocks changed journal approval metadata", ChangedJournalMetadataBlocks),
        ("Setup recovery blocks duplicate journal sequence numbers", DuplicateJournalSequenceBlocks),
        ("Setup recovery revoke wins after a future-dated grant", RevokeWinsAfterFutureDatedGrant)
    ];

    private static void OrphanCompletedBlocks()
    {
        var harness = Harness.Create(conflict: false);
        var operationId = Guid.NewGuid().ToString("N");
        WriteEvent(harness.Root, operationId, 2, "Completed", Guid.NewGuid().ToString("N"), "scope-a");

        Assert.Equal(SetupRecoveryState.Blocked, harness.Service.Inspect().State);
    }

    private static void ChangedJournalMetadataBlocks()
    {
        var harness = Harness.Create(conflict: false);
        var operationId = Guid.NewGuid().ToString("N");
        var planId = Guid.NewGuid().ToString("N");
        WriteEvent(harness.Root, operationId, 0, "Started", planId, "scope-a");
        WriteEvent(harness.Root, operationId, 1, "BackupCreated", planId, "scope-a");
        WriteEvent(harness.Root, operationId, 2, "Completed", planId, "scope-b");

        Assert.Equal(SetupRecoveryState.Blocked, harness.Service.Inspect().State);
    }

    private static void DuplicateJournalSequenceBlocks()
    {
        var harness = Harness.Create(conflict: false);
        var operationId = Guid.NewGuid().ToString("N");
        var planId = Guid.NewGuid().ToString("N");
        WriteEvent(harness.Root, operationId, 0, "Started", planId, "scope-a");
        WriteEvent(harness.Root, operationId, 1, "BackupCreated", planId, "scope-a");
        WriteEvent(harness.Root, operationId, 1, "ChangeIntent", planId, "scope-a");

        Assert.Equal(SetupRecoveryState.Blocked, harness.Service.Inspect().State);
    }

    private static void RevokeWinsAfterFutureDatedGrant()
    {
        var harness = Harness.Create(conflict: true);
        var applied = harness.Service.Apply(harness.Service.Inspect(), allowAutomatic: true);
        Assert.True(applied.Succeeded, applied.Detail);

        var consentRoot = Path.Combine(harness.Root, "setup-recovery", "consents");
        var grantPath = Directory.EnumerateFiles(consentRoot, "*.json", SearchOption.TopDirectoryOnly).Single();
        var grant = JsonNode.Parse(File.ReadAllText(grantPath))!.AsObject();
        grant["RecordedAtUtc"] = "2100-01-01T00:00:00+00:00";
        File.WriteAllText(grantPath, grant.ToJsonString(RecoveryJson.Options), new UTF8Encoding(false));

        harness.Environment.ReintroduceConflict();
        harness.Service.RevokeAutomatic();

        var sequences = Directory.EnumerateFiles(consentRoot, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => JsonNode.Parse(File.ReadAllText(path))!["Sequence"]!.GetValue<long>())
            .OrderBy(sequence => sequence)
            .ToArray();
        Assert.SequenceEqual(new long[] { 1, 2 }, sequences);
        var plan = harness.Service.Inspect();
        Assert.Equal(SetupRecoveryState.ApprovalRequired, plan.State);
        Assert.False(plan.AllowAutomatic);
    }

    private static void WriteEvent(string root, string operationId, int sequence, string kind, string planId, string scope)
    {
        var operationsRoot = Path.Combine(root, "setup-recovery", "operations");
        Directory.CreateDirectory(operationsRoot);
        var record = new
        {
            OperationId = operationId,
            Sequence = sequence,
            Kind = kind,
            PlanId = planId,
            ScopeFingerprint = scope,
            Detail = kind,
            RecordedAtUtc = DateTimeOffset.UtcNow,
            RelatedOperationId = (string?)null
        };
        File.WriteAllText(Path.Combine(operationsRoot, $"{operationId}.{sequence:D6}.{kind}.json"),
            JsonSerializer.Serialize(record, RecoveryJson.Options), new UTF8Encoding(false));
    }

    private sealed class Harness
    {
        private Harness(string root, ProfileStore store, Environment environment)
        {
            Root = root;
            Environment = environment;
            Service = new SetupRecovery(store, environment);
        }

        public string Root { get; }
        public Environment Environment { get; }
        public SetupRecovery Service { get; }

        public static Harness Create(bool conflict)
        {
            var root = TestContext.CreateRunDirectory("setup-recovery-journal");
            var store = new ProfileStore(root);
            var profile = store.Create();
            return new Harness(root, store, new Environment(profile, Path.Combine(root, "Sandboxie.ini"), conflict));
        }
    }

    private sealed class Environment : ISetupRecoveryEnvironment
    {
        private const string UserSection = "UserSettings_TEST";
        private readonly BoxProfile profile;
        private readonly string configPath;
        private readonly Dictionary<string, string[]> box;
        private Dictionary<string, string[]> globals = new(StringComparer.OrdinalIgnoreCase);
        private byte[] iniBytes = Encoding.UTF8.GetBytes("clean");

        public Environment(BoxProfile profile, string configPath, bool conflict)
        {
            this.profile = profile;
            this.configPath = configPath;
            box = IsolationPolicy.Expected(profile);
            if (conflict) ReintroduceConflict();
        }

        public SetupRecoveryObservation Inspect(ProfileStore store, BoxProfile? inspectedProfile,
            CancellationToken cancellationToken = default)
        {
            var explicitBox = Copy(box);
            var explicitGlobals = Copy(globals);
            var configuration = new ConfigurationLayers(explicitBox, Copy(box), explicitGlobals,
                Copy(RecoveryTemplateRules.ExpectedGlobals(globals)));
            var sections = new Dictionary<string, IReadOnlyDictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase)
            {
                [profile.BoxName] = Copy(box),
                ["GlobalSettings"] = Copy(globals),
                [UserSection] = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            };
            IReadOnlyList<SetupRecoveryChange> changes = globals.TryGetValue("Template", out var templates)
                ? [new SetupRecoveryChange("GlobalSettings", "Template", templates.ToArray(), Array.Empty<string>(),
                    SetupRecoveryChangeKind.RemoveGlobalTemplate, "호환성 템플릿 복구")]
                : Array.Empty<SetupRecoveryChange>();
            return new SetupRecoveryObservation(
                configPath, iniBytes.ToArray(), configuration, sections, changes, profile.BoxName, profile.UserSid,
                profile.StoragePath, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<int>(), true,
                new SetupRecoveryInstallContext(Path.Combine(TestContext.WorkspaceRoot, "Sandboxie.exe"),
                    Path.Combine(TestContext.WorkspaceRoot, "Start.exe"), "1.18.4", true),
                SetupRecoveryUiState.Empty, true, true, true, string.Empty, DateTimeOffset.UtcNow)
            {
                CurrentUserSettingsSection = UserSection
            };
        }

        public SetupRecoveryUiResult QuiesceManagementUi(SetupRecoveryObservation observation, SetupRecoveryPlan plan,
            CancellationToken cancellationToken = default) => SetupRecoveryUiResult.NoUi;

        public SetupRecoveryChangeResult ApplyChange(SetupRecoveryObservation observation, SetupRecoveryPlan plan,
            SetupRecoveryChange change, CancellationToken cancellationToken = default)
        {
            globals.Remove(change.Key);
            iniBytes = Encoding.UTF8.GetBytes("changed");
            return new SetupRecoveryChangeResult(true, true);
        }

        public void ReintroduceConflict()
        {
            globals["Template"] = ["WindowsRasMan"];
            iniBytes = Encoding.UTF8.GetBytes("conflict");
        }

        private static Dictionary<string, string[]> Copy(IReadOnlyDictionary<string, string[]> source) =>
            source.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }
}
