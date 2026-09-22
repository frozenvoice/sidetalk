using System.Text.Json;
using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class RecoveryTemplateRulesTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases =
    [
        ("Recovery baseline: identical user override is not official origin", RejectsUserOverride),
        ("Recovery baseline: tampered reviewed template stays blocked", RejectsTamperedTemplate),
        ("Recovery baseline: known selectors preserve explicit-before-default order", ChecksMergeAndPolicy),
        ("Recovery baseline: unknown and duplicate selectors stay blocked", RejectsUnknownAndDuplicates)
    ];

    private static void RejectsUserOverride()
    {
        var source = new Source();
        RecoveryTemplateRules.Verify(new(source.Query));
        source.Override = "Template_WindowsRasMan";
        Assert.Throws<InvalidDataException>(() => RecoveryTemplateRules.Verify(new(source.Query)));
    }
    private static void RejectsTamperedTemplate()
    {
        var source = new Source();
        source.Sections["Template_WindowsRasMan"]["OpenIpcPath"] = ["*"];
        Assert.Throws<InvalidDataException>(() => RecoveryTemplateRules.Verify(new(source.Query)));
    }
    private static void ChecksMergeAndPolicy()
    {
        var globals = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        { ["Template"] = ["WindowsRasMan", "OfficeLicensing"] };
        var actual = RecoveryTemplateRules.ExpectedGlobals(globals);
        Assert.SequenceEqual(new[] { @"\RPC Control\RasManLrpc", @"\RPC Control\OSPPCTransportEndpoint-*", @"\RPC Control\SPPCTransportEndpoint-*" }, actual["OpenIpcPath"].Take(3));
        var defaults = BuiltinTemplates.ExpectedGlobals(new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase));
        Assert.SequenceEqual(defaults["OpenIpcPath"], actual["OpenIpcPath"].Skip(3));
        var profile = TestContext.CreateProfile("reviewed-template-still-policy-blocked");
        var box = IsolationPolicy.Expected(profile);
        Assert.False(IsolationPolicy.Validate(profile, new ConfigurationLayers(box, box, globals, actual)).Safe);
    }
    private static void RejectsUnknownAndDuplicates()
    {
        Assert.Throws<InvalidDataException>(() => RecoveryTemplateRules.ExpectedGlobals(new Dictionary<string, string[]> { ["Template"] = ["Other"] }));
        Assert.Throws<InvalidDataException>(() => RecoveryTemplateRules.ExpectedGlobals(new Dictionary<string, string[]> { ["Template"] = ["WindowsRasMan", "windowsrasman"] }));
    }
    private sealed class Source
    {
        public Dictionary<string, Dictionary<string, string[]>> Sections { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Override { get; set; }
        public Source()
        {
            foreach (var name in new[] { "SandboxieDefaults.json", "RecoveryTemplates.json" })
            {
                using var stream = typeof(RecoveryTemplateRules).Assembly.GetManifestResourceStream("Sidetalk.Core." + name)!;
                foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string[]>>>(stream)!)
                    Sections.Add(pair.Key, new(pair.Value, StringComparer.OrdinalIgnoreCase));
            }
        }
        public string? Query(string section, string? key, uint index)
        {
            if ((index & SandboxieConfigurationReader.WithoutTemplatesGlobalsOrExpansion) == SandboxieConfigurationReader.WithoutTemplatesGlobalsOrExpansion && section != Override)
                return null;
            index &= 0x0fffffff;
            if (!Sections.TryGetValue(section, out var values)) return null;
            if (key is null) return index < values.Count ? values.Keys.ElementAt((int)index) : null;
            return values.TryGetValue(key, out var entries) && index < entries.Length ? entries[index] : null;
        }
    }
}
