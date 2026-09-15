using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sidetalk.Core;

public static class BuiltinTemplates
{
    // The driver reads these special sections directly, outside ordinary per-box Template inheritance.
    // Fingerprints are data-only, from official Sandboxie v1.18.4 Templates.ini. See docs/SOURCES.md.
    public static void Verify(SandboxieApi api)
    {
        using var stream = typeof(BuiltinTemplates).Assembly.GetManifestResourceStream("Sidetalk.Core.BuiltinTemplateHashes.json")
            ?? throw new InvalidDataException("기본 템플릿 검증 정보가 없습니다.");
        var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? throw new InvalidDataException("기본 템플릿 기준이 손상되었습니다.");
        foreach (var entry in hashes)
        {
            if (!Fingerprint(api.ReadSection(entry.Key)).Equals(entry.Value, StringComparison.Ordinal))
                throw new InvalidDataException($"공식 기본 규칙이 검토 기준과 다릅니다: {entry.Key}. 기존 설정은 변경하지 말고 해당 버전을 검토하세요.");
        }
    }
    public static string Fingerprint(IReadOnlyDictionary<string, string[]> section)
    {
        var lines = section.SelectMany(pair => pair.Value.Select(value => $"{pair.Key.Trim()}={value.Trim()}".ToUpperInvariant())).OrderBy(x => x, StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
    }
}
