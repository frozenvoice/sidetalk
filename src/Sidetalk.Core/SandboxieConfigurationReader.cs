namespace Sidetalk.Core;

/// <summary>Layers returned by QueryConf, before and after Sandboxie's template merge.</summary>
public sealed record ConfigurationLayers(
    IReadOnlyDictionary<string, string[]> ExplicitBox,
    IReadOnlyDictionary<string, string[]> EffectiveBox,
    IReadOnlyDictionary<string, string[]> ExplicitGlobals,
    IReadOnlyDictionary<string, string[]> EffectiveGlobals);

/// <summary>
/// Uses the same QueryConf enumeration path in production and source-fixture tests.
/// Both name and value queries must carry the origin filter; filtering values alone
/// still exposes template-only setting names.
/// </summary>
public sealed class SandboxieConfigurationReader(Func<string, string?, uint, string?> query)
{
    public const uint WithoutGlobalsOrExpansion = 0x60000000;
    public const uint WithoutTemplatesGlobalsOrExpansion = 0x70000000;
    private const uint Limit = 4096;

    public Dictionary<string, string[]> ReadSection(string section, bool includeTemplates = true)
    {
        var flags = includeTemplates ? WithoutGlobalsOrExpansion : WithoutTemplatesGlobalsOrExpansion;
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        for (uint index = 0; index < Limit; index++)
        {
            var key = query(section, null, index | flags);
            if (key is null) return result;
            if (string.IsNullOrWhiteSpace(key) || result.ContainsKey(key))
                throw new InvalidDataException("중복되거나 비어 있는 Sandboxie 설정 이름입니다.");
            var values = new List<string>();
            for (uint valueIndex = 0; ; valueIndex++)
            {
                if (valueIndex >= Limit) throw new InvalidDataException("Sandboxie 설정 개수 제한을 초과했습니다.");
                var value = query(section, key, valueIndex | flags);
                if (value is null) break;
                values.Add(value);
            }
            if (values.Count == 0) throw new InvalidDataException("Sandboxie 설정 조회 도중 구성이 바뀌었습니다.");
            result.Add(key, values.ToArray());
        }
        throw new InvalidDataException("Sandboxie 설정 개수 제한을 초과했습니다.");
    }

    public ConfigurationLayers ReadConfiguration(string boxName)
    {
        BuiltinTemplates.Verify(this);
        var layers = new ConfigurationLayers(
            ReadSection(boxName, false), ReadSection(boxName),
            ReadSection("GlobalSettings", false), ReadSection("GlobalSettings"));
        BuiltinTemplates.Verify(this);
        var second = new ConfigurationLayers(
            ReadSection(boxName, false), ReadSection(boxName),
            ReadSection("GlobalSettings", false), ReadSection("GlobalSettings"));
        if (!Same(layers.ExplicitBox, second.ExplicitBox) || !Same(layers.EffectiveBox, second.EffectiveBox) ||
            !Same(layers.ExplicitGlobals, second.ExplicitGlobals) || !Same(layers.EffectiveGlobals, second.EffectiveGlobals))
            throw new InvalidDataException("조회 도중 Sandboxie 설정이 바뀌었습니다. 새로고침하세요.");
        return layers;
    }

    // Key order is immaterial; duplicate values and per-key order remain significant.
    public static bool Same(IReadOnlyDictionary<string, string[]> left, IReadOnlyDictionary<string, string[]> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var values) &&
            pair.Value.SequenceEqual(values, StringComparer.Ordinal));
}