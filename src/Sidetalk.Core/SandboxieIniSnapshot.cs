using System.Text;

namespace Sidetalk.Core;

/// <summary>Independent, conservative view of the physical INI before a setting write.</summary>
public static class SandboxieIniSnapshot
{
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("Sandboxie INI 크기가 검토 범위를 초과했습니다.");
        string text;
        try
        {
            if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
                text = new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2);
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
                text = new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2);
            else
            {
                var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
                text = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
            }
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("Sandboxie INI 인코딩을 확실하게 해석할 수 없습니다.");
        }
        if (text.Contains('\0')) throw new InvalidDataException("Sandboxie INI에 해석할 수 없는 문자가 있습니다.");
        var sections = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<string>>? current = null;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[')
            {
                if (line[^1] != ']' || line.Length < 3) throw InvalidSyntax();
                var name = line[1..^1].Trim();
                if (name.Length == 0 || name.IndexOfAny(['[', ']']) >= 0) throw InvalidSyntax();
                current = new(StringComparer.OrdinalIgnoreCase);
                if (!sections.TryAdd(name, current)) throw new InvalidDataException("중복 INI 섹션은 자동 복구하지 않습니다.");
                continue;
            }
            var equals = line.IndexOf('=');
            if (current is null || equals <= 0) throw InvalidSyntax();
            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (key.Length == 0) throw InvalidSyntax();
            if (!current.TryGetValue(key, out var values)) current.Add(key, values = []);
            values.Add(value);
        }
        return sections.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyDictionary<string, string[]>)pair.Value.ToDictionary(value => value.Key, value => value.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    public static void RequireEquivalent(byte[] bytes, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> apiSections)
    {
        var fileSections = Parse(bytes);
        // Empty sections carry no setting semantics and may not be enumerated by the driver.
        var fileNonempty = fileSections.Where(pair => pair.Value.Count != 0).ToArray();
        var apiNonempty = apiSections.Where(pair => pair.Value.Count != 0).ToArray();
        if (fileNonempty.Length != apiNonempty.Length || fileNonempty.Any(section =>
            !apiSections.TryGetValue(section.Key, out var actual) || !SandboxieConfigurationReader.Same(section.Value, actual)))
            throw new InvalidDataException("실제 Sandboxie INI와 API 설정이 일치하지 않습니다. 다시 조회하기 전에는 변경하지 않습니다.");
    }

    private static InvalidDataException InvalidSyntax() => new("Sandboxie INI 구문을 확실하게 해석할 수 없어 복구를 차단했습니다.");
}