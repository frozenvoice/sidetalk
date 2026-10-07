namespace Sidetalk.Core;

/// <summary>Only the reviewed administrator-rights drift can be shadowed during an approved repair.</summary>
internal static class AdminRightsRecovery
{
    internal const string Key = "DropAdminRights";

    internal static bool IsRestorableValue(IReadOnlyList<string> values) =>
        values.Count == 0 || values.Count == 1 &&
        (values[0].Length == 0 || values[0].Equals("n", StringComparison.OrdinalIgnoreCase));

    internal static bool IsRepairChange(SetupRecoveryChange change, BoxProfile profile) =>
        change.Kind == SetupRecoveryChangeKind.RepairOwnedBoxSetting &&
        change.Section.Equals(profile.BoxName, StringComparison.OrdinalIgnoreCase) &&
        change.Key.Equals(Key, StringComparison.OrdinalIgnoreCase) &&
        IsRestorableValue(change.CurrentValues) &&
        change.DesiredValues.SequenceEqual(["y"], StringComparer.Ordinal);

    internal static bool ContainsRepair(SetupRecoveryPlan plan) => !plan.IsFirstSetup &&
        plan.Changes.Any(change => change.Kind == SetupRecoveryChangeKind.RepairOwnedBoxSetting &&
            change.Key.Equals(Key, StringComparison.OrdinalIgnoreCase));

    internal static SetupRecoveryChange? ProposedChange(BoxProfile profile, ConfigurationLayers configuration)
    {
        var values = configuration.ExplicitBox.TryGetValue(Key, out var current) ? current : Array.Empty<string>();
        if (!IsRestorableValue(values) || !PolicyPermitsRepair(profile, configuration)) return null;
        return new(profile.BoxName, Key, values.ToArray(), ["y"],
            SetupRecoveryChangeKind.RepairOwnedBoxSetting, "보조 환경의 관리자 권한 제한 복구");
    }

    internal static bool CapturePermitsRepair(SetupRecoveryObservation observation, BoxProfile profile) =>
        observation.OwnedBoxName == profile.BoxName && observation.UserSid == profile.UserSid &&
        string.Equals(observation.StoragePath, profile.StoragePath, StringComparison.OrdinalIgnoreCase) &&
        PolicyPermitsRepair(profile, observation.Configuration);

    internal static bool PolicyPermitsRepair(BoxProfile profile, ConfigurationLayers configuration)
    {
        var values = configuration.ExplicitBox.TryGetValue(Key, out var current) ? current : Array.Empty<string>();
        if (!IsRestorableValue(values) && !values.SequenceEqual(["y"], StringComparer.Ordinal)) return false;
        if (!IsolationPolicy.Validate(profile, configuration).Owned ||
            !SandboxieConfigurationReader.Same(configuration.ExplicitBox, configuration.EffectiveBox)) return false;
        try
        {
            // Existing reviewed global repairs retain their own explicit change list.
            // This candidate shadows exactly one box key; no other policy is relaxed.
            if (!SandboxieConfigurationReader.Same(RecoveryTemplateRules.ExpectedGlobals(configuration.ExplicitGlobals), configuration.EffectiveGlobals))
                return false;
            var globals = configuration.ExplicitGlobals.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            if (globals.TryGetValue("Template", out var templates))
            {
                var remaining = templates.Where(name => !RecoveryTemplateRules.IsReviewed(name)).ToArray();
                if (remaining.Length == 0) globals.Remove("Template");
                else globals["Template"] = remaining;
            }
            var box = configuration.ExplicitBox.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            box[Key] = ["y"];
            return IsolationPolicy.Validate(profile, new ConfigurationLayers(box, box, globals,
                RecoveryTemplateRules.ExpectedGlobals(globals))).Safe;
        }
        catch (InvalidDataException) { return false; }
    }
}
