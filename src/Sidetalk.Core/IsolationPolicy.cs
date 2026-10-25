using System.Text;

namespace Sidetalk.Core;

public sealed record PolicyResult(bool Owned, bool Safe, IReadOnlyList<string> Issues);

/// <summary>Conservative allowlist: an unfamiliar effective setting is a reason to stop, never to weaken isolation.</summary>
public static class IsolationPolicy
{
    public const string Revision = "2";
    public static Dictionary<string, string[]> Expected(BoxProfile profile)
    {
        ProfileStore.ValidateIdentity(profile);
        var hidden = new[] {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Kakao")
        }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var path in hidden.Append(profile.StoragePath))
            if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['\r', '\n', '%', '*', '?', ',']) >= 0 || !Path.IsPathFullyQualified(path))
                throw new InvalidDataException("안전하게 표현할 수 없는 사용자 데이터 경로입니다.");
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["Enabled"] = ["y"],
            ["BoxNameTitle"] = ["y"],
            ["BorderColor"] = [SecondaryAppearance.DefaultBorder],
            ["FileRootPath"] = [profile.StoragePath],
            ["ConfigLevel"] = ["10"],
            ["AutoDelete"] = ["n"],
            ["AutoRecover"] = ["n"],
            ["NeverDelete"] = ["y"],
            ["DropAdminRights"] = ["y"],
            // The official OpenWinInetCache template exposes host COM/IPC. Close those
            // same endpoints in this box; never edit the inherited global template.
            ["ClosedClsid"] = ["{0358B920-0AC7-461F-98F4-58E32CD89148}"],
            ["ClosedIpcPath"] = [@"\RPC Control\webcache_*", @"*\BaseNamedObjects\windows_webcache_counters_*"],
            ["WriteFilePath"] = hidden,
            ["WriteKeyPath"] = [@"HKEY_CURRENT_USER", @"HKEY_LOCAL_MACHINE\SOFTWARE\Kakao", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Kakao"],
            ["BoxInfo"] = [$"SIDETALK:{profile.OwnerToken}:policy={Revision}"]
        };
    }

    // SandMan display metadata is allowed; resource access, triggers and unknown settings are not.
    private static readonly HashSet<string> PresentationOnly = new(StringComparer.OrdinalIgnoreCase) { "BoxAlias", "BoxIcon", "BoxColor" };

    // Preserve only reviewed cosmetic values when resuming initial preparation.
    // The access policy and ownership revision remain exactly the same.
    internal static Dictionary<string, string[]> ExpectedForRecovery(BoxProfile profile, IReadOnlyDictionary<string, string[]> current)
    {
        var expected = Expected(profile);
        if (current.TryGetValue("BorderColor", out var border) && border.Length == 1 && SecondaryAppearance.IsBorderValue(border[0]))
            expected["BorderColor"] = border.ToArray();
        foreach (var key in PresentationOnly)
            if (current.TryGetValue(key, out var values)) expected[key] = values.ToArray();
        return expected;
    }
    private static readonly HashSet<string> GlobalUiOnly = new(StringComparer.OrdinalIgnoreCase) { "DefaultBox", "FileRootPath", "EditPassword", "EditAdminOnly", "ForceDisableAdminOnly", "NotifyStartRunAccessDenied", "AlertBeforeStart", "SbieCtrl_HideMessage", "SbieCtrl_EnableLogonStart", "SbieCtrl_EnableAutoStart", "SbieCtrl_EnableStopWarning", "SbieCtrl_EnableDeleteWarning", "SbieCtrl_WindowCoords", "SbieCtrl_BoxExpandedView", "SbieCtrl_UserName", "SbieCtrl_NextUpdateCheck", "BoxGrouping" };

    public static PolicyResult Validate(BoxProfile profile, IReadOnlyDictionary<string, string[]> box, IReadOnlyDictionary<string, string[]> globals)
    {
        var issues = new List<string>();
        var expected = Expected(profile);
        var owned = Equal(box, "BoxInfo", expected["BoxInfo"]) && Equal(box, "FileRootPath", [profile.StoragePath]);
        if (!owned) issues.Add("전용 환경의 소유 표식 또는 저장 경로가 일치하지 않습니다.");
        foreach (var pair in expected)
            if (pair.Key == "BorderColor")
            {
                if (!box.TryGetValue(pair.Key, out var border) || border.Length != 1 || !SecondaryAppearance.IsBorderValue(border[0]))
                    issues.Add("검토되지 않은 테두리 표시 설정: BorderColor");
            }
            else if (!Equal(box, pair.Key, pair.Value)) issues.Add($"필수 설정 불일치: {pair.Key}");
        foreach (var key in box.Keys)
            if (!expected.ContainsKey(key) && !PresentationOnly.Contains(key)) issues.Add($"검토되지 않은 환경 설정: {key}");
        foreach (var key in globals.Keys)
            if (!GlobalUiOnly.Contains(key)) issues.Add($"상속되는 전역 설정은 검토가 필요합니다: {key}");
        return new(owned, issues.Count == 0, issues);
    }
    public static PolicyResult Validate(BoxProfile profile, ConfigurationLayers config)
    {
        var explicitResult = Validate(profile, config.ExplicitBox, config.ExplicitGlobals);
        var issues = explicitResult.Issues.ToList();
        if (!SandboxieConfigurationReader.Same(config.ExplicitBox, config.EffectiveBox))
            issues.Add("전용 환경에 검토되지 않은 템플릿 규칙이 추가되었습니다.");
        if (!SandboxieConfigurationReader.Same(BuiltinTemplates.ExpectedGlobals(config.ExplicitGlobals), config.EffectiveGlobals))
            issues.Add($"전역 템플릿 확장이 공식 {SandboxieSupport.PlusVersion} 기본 규칙과 다릅니다.");
        return new(explicitResult.Owned, issues.Count == 0, issues);
    }
    private static bool Equal(IReadOnlyDictionary<string, string[]> config, string key, string[] expected) =>
        config.TryGetValue(key, out var actual) && actual.Length == expected.Length &&
        actual.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).SequenceEqual(expected.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    public static string ToIni(BoxProfile profile)
    {
        var settings = Expected(profile);
        var builder = new StringBuilder("# SIDETALK 전용 새 환경. 기존 설정 전체를 교체하지 마세요.\r\n# BoxInfo는 SIDETALK의 소유 표식입니다. 변경하지 마세요.\r\n");
        builder.AppendLine($"[{profile.BoxName}]");
        foreach (var pair in settings)
            foreach (var value in pair.Value) builder.AppendLine($"{pair.Key}={value}");
        return builder.ToString();
    }
}
