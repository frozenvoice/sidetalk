using System.Text.Json;

namespace Sidetalk.Core;

/// <summary>Recognizes the reviewed conflicts without adding them to the isolation policy allow-list.</summary>
public static class RecoveryTemplateRules
{
    private static readonly Lazy<Dictionary<string, Dictionary<string, string[]>>> Baseline = new(Load);
    public static IReadOnlyList<string> Names { get; } = Array.AsReadOnly(new[]
    {
        "WindowsRasMan", "OnScreenKeyboard", "WindowsLive", "Edge_Fix", "OfficeLicensing", "OfficeClickToRun"
    });

    public static string Fingerprint { get; } = BaselineFingerprint();

    private static string BaselineFingerprint()
    {
        using var bytes = new MemoryStream();
        foreach (var name in new[] { "RecoveryTemplates.json", "SandboxieDefaults.json", "BuiltinTemplateHashes.json" })
        {
            using var stream = typeof(RecoveryTemplateRules).Assembly.GetManifestResourceStream("Sidetalk.Core." + name)
                ?? throw new InvalidDataException("복구 검토 기준이 없습니다.");
            stream.CopyTo(bytes);
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.ToArray()));
    }

    public static void Verify(SandboxieConfigurationReader reader)
    {
        BuiltinTemplates.Verify(reader);
        foreach (var section in Baseline.Value)
            if (reader.ReadSection(section.Key, false).Count != 0 ||
                !SandboxieConfigurationReader.Same(section.Value, reader.ReadSection(section.Key)))
                throw new InvalidDataException("복구 대상 템플릿의 공식 출처·내용이 검토 기준과 다릅니다: " + section.Key);
    }

    public static bool IsReviewed(string name) => Names.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, string[]> ExpectedGlobals(IReadOnlyDictionary<string, string[]> globals)
    {
        var result = globals.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        if (globals.TryGetValue("Template", out var selectors))
        {
            if (selectors.Length != selectors.Distinct(StringComparer.OrdinalIgnoreCase).Count())
                throw new InvalidDataException("전역 Template 선택에 중복이 있어 자동 복구하지 않습니다.");
            foreach (var name in selectors)
            {
                if (!IsReviewed(name)) throw new InvalidDataException("검토되지 않은 전역 템플릿: " + name);
                foreach (var pair in Baseline.Value["Template_" + name])
                {
                    if (pair.Key.StartsWith("Tmpl.", StringComparison.OrdinalIgnoreCase)) continue;
                    if (pair.Key.Equals("Template", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("복구 기준의 중첩 템플릿은 별도 검토가 필요합니다.");
                    result[pair.Key] = result.TryGetValue(pair.Key, out var current)
                        ? [.. current, .. pair.Value] : pair.Value.ToArray();
                }
            }
        }
        // drv/conf.c appends explicit selectors first, then the pinned defaults.
        return BuiltinTemplates.ExpectedGlobals(result);
    }

    private static Dictionary<string, Dictionary<string, string[]>> Load()
    {
        using var stream = typeof(RecoveryTemplateRules).Assembly.GetManifestResourceStream("Sidetalk.Core.RecoveryTemplates.json")
            ?? throw new InvalidDataException("복구 템플릿의 고정 기준이 없습니다.");
        var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string[]>>>(stream)
            ?? throw new InvalidDataException("복구 템플릿 기준이 손상되었습니다.");
        if (data.Count != Names.Count || Names.Any(name => !data.ContainsKey("Template_" + name)))
            throw new InvalidDataException("복구 템플릿 기준 범위가 다릅니다.");
        return data.ToDictionary(pair => pair.Key,
            pair => new Dictionary<string, string[]>(pair.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    }
}
