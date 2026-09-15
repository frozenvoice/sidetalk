using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sidetalk.Core;

public static class BuiltinTemplates
{
    private static readonly Lazy<Dictionary<string, Dictionary<string, string[]>>> Baseline = new(LoadBaseline);

    // Only the pinned DefaultTemplates closure and directly-read driver sections are trusted.
    // A user-defined section (even identical text) remains visible with NO_TEMPLS and is refused.
    public static void Verify(SandboxieConfigurationReader reader)
    {
        foreach (var section in Baseline.Value)
        {
            if (reader.ReadSection(section.Key, includeTemplates: false).Count != 0 ||
                !SandboxieConfigurationReader.Same(section.Value, reader.ReadSection(section.Key)))
                throw new InvalidDataException($"공식 기본 규칙의 출처 또는 내용이 검토 기준과 다릅니다: {section.Key}. 기존 설정을 변경하지 말고 해당 구성을 검토하세요.");
        }
    }

    public static Dictionary<string, string[]> ExpectedGlobals(IReadOnlyDictionary<string, string[]> explicitGlobals)
    {
        var result = explicitGlobals.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        // drv/conf.c: explicit global templates are merged first (policy refuses those),
        // then each DefaultTemplates entry is appended, preserving duplicates and value order.
        // Conf_Merge_Template skips Tmpl.* metadata and does not copy the selector itself.
        foreach (var name in Baseline.Value["DefaultTemplates"]["Template"])
            foreach (var pair in Baseline.Value["Template_" + name])
            {
                if (pair.Key.StartsWith("Tmpl.", StringComparison.OrdinalIgnoreCase)) continue;
                result[pair.Key] = result.TryGetValue(pair.Key, out var existing)
                    ? [.. existing, .. pair.Value] : pair.Value.ToArray();
            }
        return result;
    }

    private static Dictionary<string, Dictionary<string, string[]>> LoadBaseline()
    {
        using var stream = typeof(BuiltinTemplates).Assembly.GetManifestResourceStream("Sidetalk.Core.SandboxieDefaults.json")
            ?? throw new InvalidDataException("공식 기본 템플릿 검증 정보가 없습니다.");
        var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string[]>>>(stream)
            ?? throw new InvalidDataException("공식 기본 템플릿 기준이 손상되었습니다.");
        return data.ToDictionary(pair => pair.Key,
            pair => new Dictionary<string, string[]>(pair.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    }

    // Retained for comparing diagnostic data; security verification above also checks
    // origin, duplicates and value order, which this unordered fingerprint cannot prove.
    public static string Fingerprint(IReadOnlyDictionary<string, string[]> section)
    {
        var lines = section.SelectMany(pair => pair.Value.Select(value => $"{pair.Key.Trim()}={value.Trim()}".ToUpperInvariant())).OrderBy(x => x, StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
    }
}
