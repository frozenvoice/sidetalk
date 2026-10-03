using System.Text;
using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class SetupRecoverySafetyTests
{
    private static readonly string[] ReviewedTemplates = RecoveryTemplateRules.Names.ToArray();

    public static IReadOnlyList<(string Name, Action Run)> Cases =
    [
        ("Setup safety: first setup keeps exact candidate approval before profile publish", FirstSetupRequiresExactCandidate),
        ("Setup safety: profile publish failure resumes with the same profile", ProfilePublishFailureResumesWithSameProfile),
        ("Setup safety: complete staged profile preserves original approved identity", CompleteProfileStagePreservesApproval),
        ("Setup safety: truncated staged profile preserves original approved identity", TruncatedProfileStagePreservesApproval),
        ("Setup safety: healthy consent survives recurrence and revocation", HealthyConsentIsRetainedAndRevoked),
        ("Setup safety: every reviewed template subset recurs within scope", EveryReviewedTemplateSubsetIsRepairable),
        ("Setup safety: backup failure performs no environment writes", BackupFailureDoesNotWrite),
        ("Setup safety: unrelated setting race stops the repair", UnrelatedSettingRaceStops),
        ("Setup safety: tampered baseline and unknown template never write", TamperedBaselineAndUnknownTemplateDoNotWrite),
        ("Setup safety: temporary operation evidence blocks first setup", TemporaryOperationEvidenceBlocks)
    ];

    private static void CompleteProfileStagePreservesApproval() => StagedProfilePreservesApproval(truncated: false);
    private static void TruncatedProfileStagePreservesApproval() => StagedProfilePreservesApproval(truncated: true);

    private static void StagedProfilePreservesApproval(bool truncated)
    {
        var harness = Harness.FirstSetup("setup-safety-staged-profile-" + truncated, needsRepair: true);
        var originalService = harness.Service();
        var approved = originalService.Inspect();
        var failingStore = truncated
            ? new ProfileStore(harness.Store.Root, profileWriter: (stream, _) =>
            {
                stream.Write(Encoding.UTF8.GetBytes("{\"Schema\":1,"));
                throw new IOException("interrupted profile serialization");
            })
            : new ProfileStore(harness.Store.Root, beforePublish: (_, _) => throw new IOException("interrupted profile publication"));
        var failingService = new SetupRecovery(failingStore, harness.Environment);
        Assert.False(failingService.Apply(approved, false).Succeeded);
        var staged = Directory.EnumerateFiles(harness.Store.Root, ".profile.*.tmp").Single();
        var bytes = File.ReadAllBytes(staged);
        Assert.True(harness.Store.Load() is null);
        Assert.Equal(SetupRecoveryState.Blocked, originalService.Inspect().State);
        var inspection = originalService.InspectInterruptedEvidence();
        Assert.Equal(SetupRecoveryEvidenceKind.ApprovedProfileCandidate, inspection.Items.Single().Kind);
        Assert.True(originalService.ReconcileInterruptedEvidence(inspection).Succeeded);
        Assert.Equal(approved.ProposedProfile, harness.Store.Load());
        Assert.SequenceEqual(bytes, File.ReadAllBytes(originalService.InspectInterruptedEvidence().Items.Single().PreservationPath!));
        Assert.True(originalService.HasUnfinishedRecovery);
        var resumedPlan = originalService.Inspect();
        Assert.Equal(SetupRecoveryState.Interrupted, resumedPlan.State);
        Assert.Equal(approved.ProposedProfile, resumedPlan.ProposedProfile);
        var completed = originalService.Apply(resumedPlan, false);
        Assert.True(completed.Succeeded, completed.Detail);
        Assert.False(originalService.HasUnfinishedRecovery);
    }

    private static void FirstSetupRequiresExactCandidate()
    {
        var harness = Harness.FirstSetup("setup-safety-first", needsRepair: true);
        var service = harness.Service();
        var plan = service.Inspect();

        Assert.True(plan.IsFirstSetup);
        Assert.Equal(SetupRecoveryState.FirstSetup, plan.State);
        Assert.True(plan.RequiresApproval);
        Assert.True(plan.ProposedProfile is not null);
        Assert.True(harness.Store.Load() is null, "Inspection must not publish profile.json.");
        Assert.Equal(0, harness.Environment.ApplyCount);

        var expectedBox = IsolationPolicy.Expected(plan.ProposedProfile!);
        var boxChanges = plan.Changes
            .Where(change => string.Equals(change.Section, plan.ProposedProfile!.BoxName, StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(expectedBox.Count, boxChanges.Length);
        foreach (var expected in expectedBox)
        {
            var change = boxChanges.Single(candidate => candidate.Key == expected.Key);
            Assert.Equal(SetupRecoveryChangeKind.AddOwnedBoxSetting, change.Kind);
            Assert.SequenceEqual(expected.Value, change.DesiredValues);
        }

        var result = service.Apply(plan, allowAutomatic: false);
        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(plan.ProposedProfile, harness.Store.Load());
        Assert.Equal(plan.Changes.Count, harness.Environment.ApplyCount);
        Assert.False(service.HasUnfinishedRecovery);
    }

    private static void ProfilePublishFailureResumesWithSameProfile()
    {
        var harness = Harness.FirstSetup("setup-safety-profile-resume", needsRepair: true);
        harness.Environment.ThrowAfterFirstBoxWrite = true;
        var service = harness.Service();
        var approved = service.Inspect();

        var first = service.Apply(approved, allowAutomatic: false);
        Assert.False(first.Succeeded);
        Assert.True(harness.Environment.ApplyCount > 0);
        var published = harness.Store.Load();
        Assert.Equal(approved.ProposedProfile, published);
        Assert.True(service.HasUnfinishedRecovery);

        var interrupted = service.Inspect();
        Assert.Equal(SetupRecoveryState.Interrupted, interrupted.State);
        Assert.True(interrupted.IsFirstSetup);
        Assert.True(interrupted.IsReconciliation);
        Assert.Equal(published, harness.Store.Load());

        harness.Environment.ThrowAfterFirstBoxWrite = false;
        var resumed = service.Apply(interrupted, allowAutomatic: false);
        Assert.True(resumed.Succeeded, resumed.Detail);
        Assert.Equal(published, harness.Store.Load());
        Assert.Equal(approved.Changes.Count, harness.Environment.ApplyCount);
        Assert.False(service.HasUnfinishedRecovery);
        Assert.Equal(SetupRecoveryState.Healthy, service.Inspect().State);
    }

    private static void HealthyConsentIsRetainedAndRevoked()
    {
        var harness = Harness.Existing("setup-safety-consent", needsRepair: true);
        var service = harness.Service();

        var approval = service.Inspect();
        Assert.Equal(SetupRecoveryState.ApprovalRequired, approval.State);
        var applied = service.Apply(approval, allowAutomatic: true);
        Assert.True(applied.Succeeded, applied.Detail);

        var healthy = service.Inspect();
        Assert.Equal(SetupRecoveryState.Healthy, healthy.State);
        Assert.True(healthy.AllowAutomatic, "A successful opt-in repair must retain same-scope consent.");

        harness.Environment.ReintroduceReviewedConflict();
        var recurrence = service.Inspect();
        Assert.Equal(SetupRecoveryState.AutomaticallyApplicable, recurrence.State);
        Assert.True(recurrence.AllowAutomatic);
        Assert.False(recurrence.RequiresApproval);

        service.RevokeAutomatic();
        var revoked = service.Inspect();
        Assert.Equal(SetupRecoveryState.ApprovalRequired, revoked.State);
        Assert.False(revoked.AllowAutomatic);
        Assert.True(revoked.RequiresApproval);
        Assert.Equal(2, harness.Environment.ApplyCount);
    }

    private static void EveryReviewedTemplateSubsetIsRepairable()
    {
        for (var mask = 1; mask < (1 << ReviewedTemplates.Length); mask++)
        {
            var harness = Harness.Existing("setup-safety-template-" + mask, needsRepair: false);
            var selected = ReviewedTemplates
                .Where((_, index) => (mask & (1 << index)) != 0)
                .ToArray();
            harness.Environment.SetTemplate(selected);

            var service = harness.Service();
            var plan = service.Inspect();
            Assert.Equal(SetupRecoveryState.ApprovalRequired, plan.State);
            var templateChange = plan.Changes.Single(change =>
                string.Equals(change.Section, "GlobalSettings", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(change.Key, "Template", StringComparison.OrdinalIgnoreCase));
            Assert.SequenceEqual(selected, templateChange.CurrentValues);
            Assert.Empty(templateChange.DesiredValues);

            var result = service.Apply(plan, allowAutomatic: false);
            Assert.True(result.Succeeded, $"Subset {string.Join(",", selected)}: {result.Detail}");
            Assert.Empty(harness.Environment.CurrentGlobal("Template"));
            Assert.Equal(1, harness.Environment.ApplyCount);
        }
    }

    private static void BackupFailureDoesNotWrite()
    {
        var harness = Harness.Existing("setup-safety-backup-failure", needsRepair: true);
        var service = harness.Service();
        var plan = service.Inspect();
        var beforeTemplate = harness.Environment.CurrentGlobal("Template").ToArray();
        var beforeAutoRun = harness.Environment.CurrentUserAutoRun();

        var historyPath = service.HistoryPath;
        Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
        File.WriteAllText(historyPath, "history path occupied", Encoding.UTF8);

        var result = service.Apply(plan, allowAutomatic: false);
        Assert.False(result.Succeeded);
        Assert.Equal(0, harness.Environment.ApplyCount);
        Assert.SequenceEqual(beforeTemplate, harness.Environment.CurrentGlobal("Template"));
        Assert.SequenceEqual(beforeAutoRun, harness.Environment.CurrentUserAutoRun());
        Assert.Equal(harness.Profile, harness.Store.Load());
        Assert.True(service.HasUnfinishedRecovery, "The intent/failure evidence must remain reviewable.");
    }

    private static void UnrelatedSettingRaceStops()
    {
        var harness = Harness.Existing("setup-safety-unrelated-race", needsRepair: true);
        harness.Environment.MutateUnrelatedSettingOnFirstApply = true;
        var service = harness.Service();
        var plan = service.Inspect();

        var result = service.Apply(plan, allowAutomatic: false);
        Assert.False(result.Succeeded);
        Assert.Equal(1, harness.Environment.ApplyCount);
        Assert.SequenceEqual(new[] { "y" }, harness.Environment.CurrentUserAutoRun());
        Assert.SequenceEqual(new[] { "race" }, harness.Environment.CurrentGlobal("UnrelatedSetting"));
        Assert.True(service.HasUnfinishedRecovery);
    }

    private static void TamperedBaselineAndUnknownTemplateDoNotWrite()
    {
        var tampered = Harness.Existing("setup-safety-tampered-baseline", needsRepair: true);
        tampered.Environment.OfficialTemplateBaselineVerified = false;
        var tamperedService = tampered.Service();
        var tamperedPlan = tamperedService.Inspect();
        Assert.Equal(SetupRecoveryState.Blocked, tamperedPlan.State);
        var tamperedResult = tamperedService.Apply(tamperedPlan, allowAutomatic: false);
        Assert.False(tamperedResult.Succeeded);
        Assert.Equal(0, tampered.Environment.ApplyCount);

        var unknown = Harness.Existing("setup-safety-unknown-template", needsRepair: true);
        unknown.Environment.UnknownTemplateNames.Add("UnreviewedTemplate");
        var unknownService = unknown.Service();
        var unknownPlan = unknownService.Inspect();
        Assert.Equal(SetupRecoveryState.Blocked, unknownPlan.State);
        var unknownResult = unknownService.Apply(unknownPlan, allowAutomatic: false);
        Assert.False(unknownResult.Succeeded);
        Assert.Equal(0, unknown.Environment.ApplyCount);
        Assert.SequenceEqual(ReviewedTemplates, unknown.Environment.CurrentGlobal("Template"));
    }

    private static void TemporaryOperationEvidenceBlocks()
    {
        var harness = Harness.FirstSetup("setup-safety-temp-evidence", needsRepair: false);
        File.WriteAllText(Path.Combine(harness.Store.Root, ".profile.pending.tmp"), "candidate not published", Encoding.UTF8);

        var service = harness.Service();
        var plan = service.Inspect();
        Assert.Equal(SetupRecoveryState.Blocked, plan.State);
        Assert.Contains("흔적", string.Join(";", plan.Blockers));

        var result = service.Apply(plan, allowAutomatic: false);
        Assert.False(result.Succeeded);
        Assert.Equal(0, harness.Environment.ApplyCount);
        Assert.True(harness.Store.Load() is null);
    }

    private sealed class Harness
    {
        private Harness(string root, ProfileStore store, BoxProfile? profile, MutableRecoveryEnvironment environment)
        {
            Root = root;
            Store = store;
            Profile = profile;
            Environment = environment;
        }

        public string Root { get; }
        public ProfileStore Store { get; }
        public BoxProfile? Profile { get; }
        public MutableRecoveryEnvironment Environment { get; }

        public SetupRecovery Service() => new(Store, Environment);

        public static Harness Existing(string name, bool needsRepair)
        {
            var root = TestContext.CreateRunDirectory(name);
            var store = new ProfileStore(root);
            var profile = store.Create();
            var environment = new MutableRecoveryEnvironment(root, profile);
            environment.SetBox(IsolationPolicy.Expected(profile));
            environment.SetAutoRun(needsRepair ? "y" : "n");
            if (needsRepair)
                environment.SetTemplate(ReviewedTemplates);
            return new(root, store, profile, environment);
        }

        public static Harness FirstSetup(string name, bool needsRepair)
        {
            var root = TestContext.CreateRunDirectory(name);
            var store = new ProfileStore(root);
            var environment = new MutableRecoveryEnvironment(root, null);
            environment.SetAutoRun(needsRepair ? "y" : "n");
            if (needsRepair)
                environment.SetTemplate(ReviewedTemplates);
            return new(root, store, null, environment);
        }
    }

    private sealed class MutableRecoveryEnvironment
        : ISetupRecoveryEnvironment
    {
        private const string GlobalSection = "GlobalSettings";
        private const string UserSection = "UserSettings_SIDETALK_TEST";
        private readonly string root;
        private readonly string? configuredBoxName;
        private readonly Dictionary<string, Dictionary<string, string[]>> sections =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, string[]>> boxSections =
            new(StringComparer.OrdinalIgnoreCase);
        private int iniRevision;

        public MutableRecoveryEnvironment(string root, BoxProfile? profile)
        {
            this.root = root;
            configuredBoxName = profile?.BoxName;
            sections[GlobalSection] = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            sections[UserSection] = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            if (profile is not null)
                SetBox(IsolationPolicy.Expected(profile));
        }

        public int ApplyCount { get; private set; }
        public bool ReturnCompleteBoxOnFirstCandidateInspection { get; set; }
        public bool ThrowAfterFirstBoxWrite { get; set; }
        public bool MutateUnrelatedSettingOnFirstApply { get; set; }
        public bool OfficialTemplateBaselineVerified { get; set; } = true;
        public List<string> UnknownTemplateNames { get; } = [];
        public List<string> OtherBoxNames { get; } = [];
        public IReadOnlyList<int> BoxProcessIds { get; set; } = [];
        public bool HostContextVerified { get; set; } = true;
        public bool InstallVerified { get; set; } = true;
        public bool PolicySafe { get; set; } = true;
        public bool ProcessesQueried { get; set; } = true;
        public bool UiFresh { get; set; } = true;
        public bool FailUiQuiesce { get; set; }
        public string ConfigPath => Path.Combine(root, "Sandboxie.ini");
        public bool CandidateBoxReturned { get; private set; }
        public SetupRecoveryUiState UiState { get; set; } = SetupRecoveryUiState.Empty;

        public void SetTemplate(IEnumerable<string> values)
        {
            Set(GlobalSection, "Template", values.ToArray());
        }

        private void Set(string section, string key, IReadOnlyList<string> values)
        {
            GetOrCreate(section)[key] = values.ToArray();
        }

        public void SetAutoRun(string value)
        {
            Set(UserSection, "SbieCtrl_AutoRunSoftCompat", [value]);
        }

        public void ReintroduceReviewedConflict()
        {
            SetTemplate(ReviewedTemplates);
            SetAutoRun("y");
        }

        public IReadOnlyList<string> CurrentGlobal(string key) => Read(GlobalSection, key);
        public IReadOnlyList<string> CurrentUserAutoRun() => Read(UserSection, "SbieCtrl_AutoRunSoftCompat");

        public void SetBox(IReadOnlyDictionary<string, string[]> values)
        {
            boxSections[configuredBoxName ?? "__pending__"] = Clone(values);
        }

        public SetupRecoveryObservation Inspect(ProfileStore requestedStore, BoxProfile? inspectedProfile, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var globals = GetSection(GlobalSection);
            var user = GetSection(UserSection);
            var explicitSections = new Dictionary<string, IReadOnlyDictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase)
            {
                [GlobalSection] = Clone(globals),
                [UserSection] = Clone(user)
            };

            var explicitBox = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            if (inspectedProfile is not null)
            {
                if (ReturnCompleteBoxOnFirstCandidateInspection && !CandidateBoxReturned)
                {
                    CandidateBoxReturned = true;
                    boxSections[inspectedProfile.BoxName] = Clone(IsolationPolicy.Expected(inspectedProfile));
                }

                if (!boxSections.TryGetValue(inspectedProfile.BoxName, out var box))
                    box = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                explicitBox = Clone(box);
                explicitSections[inspectedProfile.BoxName] = Clone(explicitBox);
            }

            var changes = new List<SetupRecoveryChange>();
            var templates = Read(GlobalSection, "Template");
            if (templates.Count != 0)
            {
                changes.Add(new(
                    GlobalSection,
                    "Template",
                    templates.ToArray(),
                    Array.Empty<string>(),
                    SetupRecoveryChangeKind.RemoveGlobalTemplate,
                    "Remove reviewed global templates"));
            }

            var autoRun = Read(UserSection, "SbieCtrl_AutoRunSoftCompat");
            if (!autoRun.SequenceEqual(["n"], StringComparer.Ordinal))
            {
                changes.Add(new(
                    UserSection,
                    "SbieCtrl_AutoRunSoftCompat",
                    autoRun.ToArray(),
                    ["n"],
                    SetupRecoveryChangeKind.SetUserSetting,
                    "Disable current-user compatibility popup"));
            }

            var expected = inspectedProfile is null ? null : IsolationPolicy.Expected(inspectedProfile);
            var owned = inspectedProfile is not null && explicitBox.TryGetValue("BoxInfo", out var markers) && markers.SequenceEqual(expected!["BoxInfo"]) && explicitBox.TryGetValue("FileRootPath", out var roots) && roots.SequenceEqual(expected!["FileRootPath"]);
            var prospectiveGlobals = Clone(globals); prospectiveGlobals.Remove("Template");
            var ready = inspectedProfile is not null && IsolationPolicy.Validate(inspectedProfile, new ConfigurationLayers(explicitBox, explicitBox, prospectiveGlobals, BuiltinTemplates.ExpectedGlobals(prospectiveGlobals))).Safe;
            var configuration = new ConfigurationLayers(
                Clone(explicitBox),
                Clone(explicitBox),
                Clone(globals),
                RecoveryTemplateRules.ExpectedGlobals(globals));

            return new SetupRecoveryObservation(
                ConfigPath,
                BuildIni(explicitSections),
                configuration,
                explicitSections,
                changes,
                owned ? inspectedProfile!.BoxName : null,
                inspectedProfile?.UserSid ?? ProfileStore.CurrentSid(),
                owned ? inspectedProfile!.StoragePath : null,
                OtherBoxNames.ToArray(),
                UnknownTemplateNames.ToArray(),
                BoxProcessIds.ToArray(),
                ProcessesQueried,
                new SetupRecoveryInstallContext(
                    Path.Combine(root, "Sandboxie.exe"),
                    Path.Combine(root, "Start.exe"),
                    "1.18.4",
                    InstallVerified,
                    "safety fake"),
                UiFresh ? UiState : UiState with { Fresh = false },
                HostContextVerified,
                OfficialTemplateBaselineVerified,
                PolicySafe && ready,
                "safety fake",
                DateTimeOffset.UtcNow);
        }

        public SetupRecoveryUiResult QuiesceManagementUi(
            SetupRecoveryObservation observation,
            SetupRecoveryPlan plan,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return FailUiQuiesce ? new(false, "fake UI quiesce failure") : SetupRecoveryUiResult.NoUi;
        }

        public SetupRecoveryChangeResult ApplyChange(
            SetupRecoveryObservation observation,
            SetupRecoveryPlan plan,
            SetupRecoveryChange change,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCount++;

            var target = GetOrCreate(change.Section);
            if (change.DesiredValues.Count == 0)
                target.Remove(change.Key);
            else
                target[change.Key] = change.DesiredValues.ToArray();

            iniRevision++;
            if (MutateUnrelatedSettingOnFirstApply && ApplyCount == 1)
            {
                GetOrCreate(GlobalSection)["UnrelatedSetting"] = ["race"];
                iniRevision++;
            }

            if (ThrowAfterFirstBoxWrite && change.Kind == SetupRecoveryChangeKind.AddOwnedBoxSetting)
                throw new IOException("Simulated app interruption after first box setting.");
            return new(true, true, "mutable fake");
        }

        private Dictionary<string, string[]> GetOrCreate(string section)
        {
            if (string.Equals(section, GlobalSection, StringComparison.OrdinalIgnoreCase))
                return sections[GlobalSection];
            if (string.Equals(section, UserSection, StringComparison.OrdinalIgnoreCase))
                return sections[UserSection];

            if (!boxSections.TryGetValue(section, out var box))
            {
                box = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                boxSections[section] = box;
            }

            return box;
        }

        private Dictionary<string, string[]> GetSection(string section)
        {
            if (sections.TryGetValue(section, out var values))
                return Clone(values);
            return boxSections.TryGetValue(section, out var box)
                ? Clone(box)
                : new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        }

        private IReadOnlyList<string> Read(string section, string key)
        {
            var values = GetSection(section);
            return values.TryGetValue(key, out var result) ? result.ToArray() : Array.Empty<string>();
        }

        private byte[] BuildIni(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> values)
        {
            var builder = new StringBuilder();
            foreach (var section in values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append('[').Append(section.Key).AppendLine("]");
                foreach (var pair in section.Value.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                    foreach (var value in pair.Value)
                        builder.Append(pair.Key).Append('=').AppendLine(value);
            }

            builder.Append("# revision=").AppendLine(iniRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Encoding.UTF8.GetBytes(builder.ToString());
        }

        private static Dictionary<string, string[]> Clone(IReadOnlyDictionary<string, string[]> values) =>
            values.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }
}
