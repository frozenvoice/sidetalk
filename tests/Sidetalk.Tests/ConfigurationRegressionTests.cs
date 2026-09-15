using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class ConfigurationRegressionTests
{
    public static IEnumerable<(string Name, Action Run)> Cases =>
    [
        ("Configuration reader accepts pinned default-template fixture", OfficialDefaultsPass),
        ("Configuration reader filters origins in both name and value queries", OriginFlags),
        ("Configuration policy requires local host-WebCache barriers", WebCacheBarriers),
        ("Configuration policy still rejects explicit global exceptions", ExplicitGlobalsFail),
        ("Configuration policy rejects unreviewed box template expansion", BoxExpansionFails),
        ("Configuration policy rejects changed effective defaults", EffectiveDefaultsFail),
        ("Configuration reader rejects locally overridden official sections", LocalOverridesFail),
        ("Configuration reader preserves default order and duplicates", OrderAndDuplicatesFail),
        ("Configuration reader detects configuration changes between snapshots", ChangingConfigurationFails),
        ("Configuration reader fails closed on query and enumeration errors", QueryErrorsFail)
    ];

    private static void OfficialDefaultsPass()
    {
        var fixture = new SourceFixture();
        var config = fixture.Read();
        Assert.True(config.EffectiveGlobals.ContainsKey("RpcPortBinding"));
        Assert.True(config.EffectiveGlobals.ContainsKey("ClosedClsid"));
        Assert.True(config.EffectiveGlobals.ContainsKey("ClosedFilePath"));
        Assert.False(config.EffectiveGlobals.ContainsKey("Template"), "Default selectors are not copied to GlobalSettings.");
        Assert.False(config.ExplicitGlobals.ContainsKey("RpcPortBinding"));
        Assert.True(IsolationPolicy.Validate(fixture.Profile, config).Safe);
    }

    private static void WebCacheBarriers()
    {
        foreach (var key in new[] { "ClosedClsid", "ClosedIpcPath" })
        {
            var fixture = new SourceFixture();
            fixture.Explicit[fixture.Profile.BoxName].Remove(key);
            fixture.Effective[fixture.Profile.BoxName].Remove(key);
            var result = IsolationPolicy.Validate(fixture.Profile, fixture.Read());
            Assert.False(result.Safe, key);
            Assert.IssueContains(result, key);
        }
        foreach (var key in new[] { "BreakoutDocument", "BreakoutProcess", "BreakoutFolder" })
        {
            var fixture = new SourceFixture();
            fixture.Explicit[fixture.Profile.BoxName][key] = ["*"];
            fixture.Effective[fixture.Profile.BoxName][key] = ["*"];
            Assert.False(IsolationPolicy.Validate(fixture.Profile, fixture.Read()).Safe, key);
        }
    }
    private static void OriginFlags()
    {
        var fixture = new SourceFixture();
        fixture.Read();
        foreach (var keyQuery in new[] { false, true })
            foreach (var includeTemplates in new[] { false, true })
                Assert.True(fixture.Calls.Any(call => call.Section == "GlobalSettings" &&
                    (call.Key is not null) == keyQuery &&
                    ((call.Index & 0x10000000) == 0) == includeTemplates));
        Assert.True(fixture.Calls.All(call =>
            (call.Index & 0xF0000000) is SandboxieConfigurationReader.WithoutGlobalsOrExpansion
                or SandboxieConfigurationReader.WithoutTemplatesGlobalsOrExpansion));
    }

    private static void ExplicitGlobalsFail()
    {
        foreach (var key in new[] { "OpenFilePath", "OpenIpcPath", "Template", "TemplateReject", "RpcPortBinding" })
        {
            var fixture = new SourceFixture();
            // Even a value identical to a reviewed default is untrusted when explicitly configured.
            var value = fixture.Effective["GlobalSettings"].TryGetValue(key, out var defaults) ? defaults[0] : "*";
            fixture.Explicit["GlobalSettings"][key] = [value];
            fixture.Effective["GlobalSettings"][key] = fixture.Effective["GlobalSettings"].TryGetValue(key, out var old)
                ? [value, .. old] : [value];
            var result = IsolationPolicy.Validate(fixture.Profile, fixture.Read());
            Assert.False(result.Safe, key);
            Assert.IssueContains(result, key);
        }
    }

    private static void BoxExpansionFails()
    {
        var fixture = new SourceFixture();
        fixture.Effective[fixture.Profile.BoxName]["OpenFilePath"] = ["*"];
        Assert.False(IsolationPolicy.Validate(fixture.Profile, fixture.Read()).Safe);
    }

    private static void EffectiveDefaultsFail()
    {
        foreach (var mutation in new[] { "missing", "value", "extra" })
        {
            var fixture = new SourceFixture();
            var globals = fixture.Effective["GlobalSettings"];
            if (mutation == "missing") globals.Remove("ClosedClsid");
            else if (mutation == "value") globals["ClosedFilePath"] = ["changed"];
            else globals["OpenFilePath"] = ["*"];
            Assert.False(IsolationPolicy.Validate(fixture.Profile, fixture.Read()).Safe, mutation);
        }
    }

    private static void LocalOverridesFail()
    {
        foreach (var section in new[] { "DefaultTemplates", "Template_COM", "TemplateDefaultPaths" })
        {
            var fixture = new SourceFixture();
            fixture.Explicit[section] = Copy(fixture.Effective[section]);
            Assert.Throws<InvalidDataException>(() => fixture.Read(), section);
        }
        var changed = new SourceFixture();
        changed.Effective["DefaultTemplates"]["Template"] = ["Local_Unreviewed"];
        Assert.Throws<InvalidDataException>(() => changed.Read(), "default closure mutation");
    }

    private static void OrderAndDuplicatesFail()
    {
        foreach (var section in new[] { "GlobalSettings", "Template_COM" })
        {
            foreach (var duplicate in new[] { false, true })
            {
                var fixture = new SourceFixture();
                var values = fixture.Effective[section]["ClosedClsid"];
                fixture.Effective[section]["ClosedClsid"] = duplicate ? [.. values, values[0]] : values.Reverse().ToArray();
                if (section == "GlobalSettings")
                    Assert.False(IsolationPolicy.Validate(fixture.Profile, fixture.Read()).Safe);
                else
                    Assert.Throws<InvalidDataException>(() => fixture.Read());
            }
        }
    }

    private static void ChangingConfigurationFails()
    {
        var fixture = new SourceFixture();
        var snapshots = 0;
        var reader = new SandboxieConfigurationReader((section, key, index) =>
        {
            if (section == fixture.Profile.BoxName && key is null && index == 0x70000000 && ++snapshots == 2)
                fixture.Explicit[section]["BoxAlias"] = ["changed during query"];
            return fixture.Query(section, key, index);
        });
        Assert.Throws<InvalidDataException>(() => reader.ReadConfiguration(fixture.Profile.BoxName));
    }

    private static void QueryErrorsFail()
    {
        var failure = new SandboxieConfigurationReader((_, _, _) => throw new InvalidOperationException("native query failure"));
        Assert.Throws<InvalidOperationException>(() => failure.ReadSection("GlobalSettings"));
        var duplicate = new SandboxieConfigurationReader((_, key, index) =>
            key is null ? "Duplicated" : (index & 0x0FFFFFFF) == 0 ? "value" : null);
        Assert.Throws<InvalidDataException>(() => duplicate.ReadSection("GlobalSettings"));
        var disappearing = new SandboxieConfigurationReader((_, key, _) => key is null ? "MissingValue" : null);
        Assert.Throws<InvalidDataException>(() => disappearing.ReadSection("GlobalSettings"));
        var endless = new SandboxieConfigurationReader((_, key, index) => key is null ? "Endless" : index.ToString());
        Assert.Throws<InvalidDataException>(() => endless.ReadSection("GlobalSettings"));
    }

    private static Dictionary<string, string[]> Copy(IReadOnlyDictionary<string, string[]> source) =>
        source.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A source-derived QueryConf model, NOT a live driver capture. Literal input comes
    /// from the pinned upstream INI fixture, independently of the embedded JSON baseline.
    /// Models conf.c section/setting origin flags and append-only default merging.
    /// </summary>
    private sealed class SourceFixture
    {
        public BoxProfile Profile { get; } = TestContext.CreateProfile("source-config");
        public Dictionary<string, Dictionary<string, string[]>> Explicit { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Dictionary<string, string[]>> Effective { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Section, string? Key, uint Index)> Calls { get; } = [];

        public SourceFixture()
        {
            var fixturePath = Path.Combine(TestContext.WorkspaceRoot, "tests", "Sidetalk.Tests", "Fixtures", "sandboxie-1.18.4.ini");
            Dictionary<string, string[]>? section = null;
            foreach (var text in File.ReadLines(fixturePath))
            {
                var line = text.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
                if (line.StartsWith('[') && line.EndsWith(']'))
                    Effective[line[1..^1]] = section = new(StringComparer.OrdinalIgnoreCase);
                else
                {
                    var equals = line.IndexOf('=');
                    if (equals < 1 || section is null) throw new InvalidDataException("Invalid source fixture.");
                    var key = line[..equals].Trim();
                    var value = line[(equals + 1)..].Trim();
                    section[key] = section.TryGetValue(key, out var prior) ? [.. prior, value] : [value];
                }
            }

            Explicit[Profile.BoxName] = IsolationPolicy.Expected(Profile);
            Effective[Profile.BoxName] = Copy(Explicit[Profile.BoxName]);
            Explicit["GlobalSettings"] = new(StringComparer.OrdinalIgnoreCase) { ["DefaultBox"] = ["DefaultBox"] };
            var globals = Copy(Explicit["GlobalSettings"]);
            // Reference model from drv/conf.c Conf_Merge_AllTemplates / Conf_Merge_Template.
            // Unlike the application baseline, this reads literal upstream INI sections.
            foreach (var template in Effective["DefaultTemplates"]["Template"])
                foreach (var pair in Effective["Template_" + template].Where(pair => !pair.Key.StartsWith("Tmpl.", StringComparison.OrdinalIgnoreCase)))
                    globals[pair.Key] = globals.TryGetValue(pair.Key, out var prior) ? [.. prior, .. pair.Value] : pair.Value.ToArray();
            Effective["GlobalSettings"] = globals;
        }

        public ConfigurationLayers Read() => new SandboxieConfigurationReader(Query).ReadConfiguration(Profile.BoxName);

        public string? Query(string sectionName, string? key, uint index)
        {
            Calls.Add((sectionName, key, index));
            var includeTemplates = (index & 0x10000000) == 0;
            var source = includeTemplates ? Effective : Explicit;
            var position = checked((int)(index & 0x0FFFFFFF));
            if (!source.TryGetValue(sectionName, out var section)) return null;
            if (key is null) return section.Keys.ElementAtOrDefault(position);
            return section.TryGetValue(key, out var values) ? values.ElementAtOrDefault(position) : null;
        }
    }
}