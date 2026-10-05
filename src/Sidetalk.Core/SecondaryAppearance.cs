using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Sidetalk.Core;

public interface ISecondaryAppearanceController
{
    Task<SecondaryAppearanceState> InspectSecondaryAppearanceAsync();
    Task<SecondaryAppearanceState> ApplySecondaryAppearanceAsync(SecondaryAppearancePlan plan);
}

public sealed record SecondaryAppearancePlan(string ScopeFingerprint, string ColorRgb, string? Label = null)
{
    public SecondaryAppearancePlan WithSelection(string colorRgb, string? label = null)
    {
        SecondaryAppearance.ValidateRgb(colorRgb);
        if (label is not null && label != SecondaryAppearance.SecondaryLabel)
            throw new ArgumentException("보조 계정 표시만 선택할 수 있습니다.", nameof(label));
        return this with { ColorRgb = colorRgb.ToUpperInvariant(), Label = label };
    }
}

public sealed record SecondaryAppearanceState(string ColorRgb, string BorderMode, string Label,
    bool CanApply, string? PendingReason, string Message, SecondaryAppearancePlan? Plan);

/// <summary>Only official window appearance values; no resource-access settings.</summary>
public static class SecondaryAppearance
{
    public const string SecondaryLabel = "보조 계정";
    public const string DefaultBorder = "#21685B,ttl,4";
    public static IReadOnlyList<string> PresetColors { get; } = Array.AsReadOnly(new[] { "#22C55E", "#3B82F6", "#A855F7", "#F97316", "#14B8A6" });

    public static void ValidateRgb(string rgb)
    {
        if (rgb is null || !Regex.IsMatch(rgb, "\\A#[0-9a-fA-F]{6}\\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("색상은 #RRGGBB 형식이어야 합니다.", nameof(rgb));
    }

    public static bool IsBorderValue(string? value)
    {
        if (value is null) return false;
        var fields = value.Split(',');
        if (fields.Length is not (3 or 5 or 6) || !Regex.IsMatch(fields[0], "\\A#[0-9a-fA-F]{6}\\z", RegexOptions.CultureInvariant)) return false;
        if (fields[1] is not ("ttl" or "on" or "all" or "ttloutside" or "onoutside" or "alloutside")) return false;
        if (!Number(fields[2], 1, 32)) return false;
        if (fields.Length >= 5 && (!Number(fields[3], 0, 255) || fields[4] is not ("no" or "in" or "out"))) return false;
        return fields.Length != 6 || Number(fields[5], 1, 32);
    }

    private static bool Number(string value, int minimum, int maximum) =>
        Regex.IsMatch(value, "\\A[0-9]{1,3}\\z", RegexOptions.CultureInvariant) && int.TryParse(value, out var number) && number >= minimum && number <= maximum;

    // Sandboxie's setting is a Win32 COLORREF (#BBGGRR), not a CSS RGB color.
    public static string RgbFromBorder(string border)
    {
        if (!IsBorderValue(border)) throw new InvalidDataException("검토되지 않은 테두리 설정입니다.");
        return ("#" + border.Substring(5, 2) + border.Substring(3, 2) + border.Substring(1, 2)).ToUpperInvariant();
    }

    public static string WithRgb(string border, string rgb)
    {
        _ = RgbFromBorder(border);
        ValidateRgb(rgb);
        return ("#" + rgb.Substring(5, 2) + rgb.Substring(3, 2) + rgb.Substring(1, 2)).ToUpperInvariant() + border[7..];
    }
}

internal interface ISecondaryAppearanceEnvironment
{
    SetupRecoveryObservation Inspect(ProfileStore store, BoxProfile profile);
    SetupRecoveryObservation ApplyAppearance(ProfileStore store, BoxProfile profile, SetupRecoveryObservation baseline, string key, string value);
}

internal sealed record SecondaryAppearanceIntent(int Schema, string OperationId, BoxProfile Profile,
    SetupRecoveryFingerprint BeforeFingerprint, Dictionary<string, Dictionary<string, string[]>> BeforeSections,
    string BorderColor, string? BoxAlias);
internal sealed record SecondaryAppearanceCompletion(int Schema, string OperationId, string IntentSha256);

/// <summary>Immutable evidence only. Reading never repairs, deletes or completes an operation.</summary>
internal sealed class SecondaryAppearanceStore(ProfileStore profileStore, Action<string>? beforeWrite = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal string DirectoryPath => Path.Combine(profileStore.Root, "secondary-appearance");
    internal static void RequireResolved(ProfileStore store)
    {
        if (new SecondaryAppearanceStore(store).ReadUnfinished() is not null)
            throw new InvalidDataException("보조 계정 표시 설정이 완료되지 않았습니다. 표시 설정에서 보존된 변경을 다시 확인하세요.");
    }

    internal SecondaryAppearanceIntent? ReadUnfinished()
    {
        ProfileStore.RejectReparseAncestors(DirectoryPath);
        if (!Directory.Exists(DirectoryPath)) return null;
        var files = Directory.EnumerateFileSystemEntries(DirectoryPath).ToArray();
        foreach (var path in files)
        {
            ProfileStore.RejectReparseAncestors(path);
            if (!File.Exists(path) || !Regex.IsMatch(Path.GetFileName(path), "\\A[a-f0-9]{32}\\.(intent\\.json|completed\\.json|ini\\.backup|failure\\.[a-f0-9]{32}\\.json)\\z", RegexOptions.CultureInvariant))
                throw new InvalidDataException("표시 설정 기록에 출처를 확인할 수 없는 원본이 있습니다. 삭제하지 마세요.");
        }
        var unfinished = new List<SecondaryAppearanceIntent>();
        foreach (var file in files.Where(path => path.EndsWith(".intent.json", StringComparison.Ordinal)))
        {
            var intent = Read<SecondaryAppearanceIntent>(file);
            if (intent.Schema != 1 || Path.GetFileName(file) != intent.OperationId + ".intent.json" ||
                !SecondaryAppearance.IsBorderValue(intent.BorderColor) || (intent.BoxAlias is not null && intent.BoxAlias != SecondaryAppearance.SecondaryLabel))
                throw new InvalidDataException("표시 설정 의도 기록이 손상되었습니다. 원본을 보존하세요.");
            ProfileStore.ValidateIdentity(intent.Profile);
            if (intent.BeforeFingerprint is null || intent.BeforeSections is null ||
                intent.BeforeFingerprint.BoxName != intent.Profile.BoxName || intent.BeforeFingerprint.StoragePath != intent.Profile.StoragePath ||
                intent.BeforeFingerprint.UserSid != intent.Profile.UserSid || RecordedSectionsHash(intent.BeforeSections) != intent.BeforeFingerprint.SectionsSha256)
                throw new InvalidDataException("표시 설정 의도의 프로필 연결 정보가 일치하지 않습니다.");
            var completed = PathFor(intent.OperationId, "completed.json");
            if (!File.Exists(completed)) { unfinished.Add(intent); continue; }
            var result = Read<SecondaryAppearanceCompletion>(completed);
            if (result.Schema != 1 || result.OperationId != intent.OperationId || result.IntentSha256 != Hash(File.ReadAllBytes(file)))
                throw new InvalidDataException("표시 설정 완료 기록의 연결 근거가 일치하지 않습니다.");
            RequireBackup(intent);
        }
        foreach (var file in files)
        {
            var id = Path.GetFileName(file)[..32];
            if (!File.Exists(PathFor(id, "intent.json"))) throw new InvalidDataException("표시 설정의 원래 의도 기록이 없습니다. 원본을 보존하세요.");
        }
        if (unfinished.Count > 1) throw new InvalidDataException("표시 설정의 미완료 기록이 여러 개입니다. 자동으로 선택하지 않습니다.");
        return unfinished.SingleOrDefault();
    }

    internal void Begin(SecondaryAppearanceIntent intent, byte[] backup)
    {
        Directory.CreateDirectory(DirectoryPath);
        ProfileStore.RejectReparseAncestors(DirectoryPath);
        Write(PathFor(intent.OperationId, "intent.json"), JsonSerializer.SerializeToUtf8Bytes(intent, Json));
        EnsureBackup(intent, backup);
    }

    internal void EnsureBackup(SecondaryAppearanceIntent intent, byte[] bytes)
    {
        var path = PathFor(intent.OperationId, "ini.backup");
        if (!File.Exists(path))
        {
            if (Hash(bytes) != intent.BeforeFingerprint.IniSha256) throw new InvalidDataException("표시 설정 백업 원본이 승인 시점과 다릅니다.");
            Write(path, bytes);
        }
        RequireBackup(intent);
    }

    private void RequireBackup(SecondaryAppearanceIntent intent)
    {
        var path = PathFor(intent.OperationId, "ini.backup");
        ProfileStore.RejectReparseAncestors(path);
        if (!File.Exists(path) || Hash(File.ReadAllBytes(path)) != intent.BeforeFingerprint.IniSha256)
            throw new InvalidDataException("표시 설정의 보존된 INI 백업이 없거나 손상되었습니다.");
    }

    internal void Complete(SecondaryAppearanceIntent intent)
    {
        RequireBackup(intent);
        var intentPath = PathFor(intent.OperationId, "intent.json");
        Write(PathFor(intent.OperationId, "completed.json"), JsonSerializer.SerializeToUtf8Bytes(
            new SecondaryAppearanceCompletion(1, intent.OperationId, Hash(File.ReadAllBytes(intentPath))), Json));
    }

    internal void RecordFailure(SecondaryAppearanceIntent intent, Exception error)
    {
        Write(PathFor(intent.OperationId, "failure." + Guid.NewGuid().ToString("N") + ".json"),
            JsonSerializer.SerializeToUtf8Bytes(new { Schema = 1, intent.OperationId, Message = InspectionFailure.Sanitize(error.Message) }, Json));
    }

    private string PathFor(string id, string suffix)
    {
        if (!Regex.IsMatch(id, "\\A[a-f0-9]{32}\\z", RegexOptions.CultureInvariant)) throw new InvalidDataException("표시 설정 작업 이름이 잘못되었습니다.");
        return Path.Combine(DirectoryPath, id + "." + suffix);
    }
    private void Write(string path, byte[] bytes)
    {
        ProfileStore.RejectReparseAncestors(path);
        beforeWrite?.Invoke(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
    }
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json) ?? throw new InvalidDataException("표시 설정 기록이 비어 있습니다.");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string RecordedSectionsHash(Dictionary<string, Dictionary<string, string[]>> sections)
    {
        // Keep the original capture tied to SetupRecoveryFingerprint's section hash.
        // Retry must not treat edited evidence as a newly approved baseline.
        var builder = new StringBuilder();
        foreach (var section in sections.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (section.Value is null) throw new InvalidDataException("표시 설정의 원래 섹션 기록이 비어 있습니다.");
            builder.Append("section\n").Append(section.Key).Append('\n').Append("values\n");
            foreach (var pair in section.Value.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (pair.Value is null || pair.Value.Any(value => value is null)) throw new InvalidDataException("표시 설정의 원래 값 기록이 비어 있습니다.");
                foreach (var value in pair.Value) builder.Append(pair.Key).Append('=').Append(value).Append('\n');
            }
        }
        return Hash(Encoding.UTF8.GetBytes(builder.ToString()));
    }
}

internal sealed class SecondaryAppearanceService(ProfileStore store, ISecondaryAppearanceEnvironment environment, Action<string>? beforeWrite = null)
{
    private readonly SecondaryAppearanceStore evidence = new(store, beforeWrite);

    internal SecondaryAppearanceState Inspect()
    {
        var profile = store.Load() ?? throw new InvalidOperationException("전용 환경을 먼저 준비하세요.");
        var observation = environment.Inspect(store, profile).Normalize();
        var border = Value(observation, profile.BoxName, "BorderColor").SingleOrDefault() ?? SecondaryAppearance.DefaultBorder;
        var rgb = SecondaryAppearance.RgbFromBorder(border);
        var alias = Value(observation, profile.BoxName, "BoxAlias").SingleOrDefault() ?? string.Empty;
        var fingerprint = SetupRecoveryFingerprint.FromObservation(observation, profile);
        var pending = evidence.ReadUnfinished();
        var blocked = Blocker(observation, profile);
        if (pending is not null)
        {
            if (!CanResume(pending, observation, profile)) blocked ??= "미완료 표시 설정의 승인 범위와 현재 상태가 다릅니다.";
            rgb = SecondaryAppearance.RgbFromBorder(pending.BorderColor);
        }
        return new(rgb, border.Split(',')[1], alias, blocked is null, pending is null ? blocked : "보존된 표시 설정을 다시 확인하고 재시도하세요.",
            blocked ?? (pending is null ? "현재 전용 환경의 표시 설정입니다." : "이전 변경의 백업과 의도를 보존했습니다. 명시적으로 재시도할 수 있습니다."),
            blocked is null ? new SecondaryAppearancePlan(fingerprint.ScopeKey, rgb, pending?.BoxAlias) : null);
    }

    internal SecondaryAppearanceState Apply(SecondaryAppearancePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        SecondaryAppearance.ValidateRgb(plan.ColorRgb);
        if (plan.Label is not null && plan.Label != SecondaryAppearance.SecondaryLabel) throw new InvalidDataException("허용되지 않은 표시 이름입니다.");
        var profile = store.Load() ?? throw new InvalidOperationException("전용 환경이 없습니다.");
        var current = environment.Inspect(store, profile).Normalize();
        var blocker = Blocker(current, profile);
        if (blocker is not null) throw new InvalidOperationException(blocker);
        var fingerprint = SetupRecoveryFingerprint.FromObservation(current, profile);
        if (plan.ScopeFingerprint != fingerprint.ScopeKey) throw new InvalidDataException("표시 설정을 검토한 뒤 상태가 바뀌었습니다. 다시 조회하세요.");
        var border = Value(current, profile.BoxName, "BorderColor").Single();
        var desired = SecondaryAppearance.WithRgb(border, plan.ColorRgb);
        var pending = evidence.ReadUnfinished();
        if (pending is not null)
        {
            if (!CanResume(pending, current, profile) || !desired.Equals(pending.BorderColor, StringComparison.OrdinalIgnoreCase) || plan.Label != pending.BoxAlias)
                throw new InvalidDataException("기존 미완료 표시 설정과 다른 변경은 시작하지 않습니다.");
        }
        else
        {
            if (desired.Equals(border, StringComparison.OrdinalIgnoreCase) && (plan.Label is null || Value(current, profile.BoxName, "BoxAlias").SequenceEqual([plan.Label], StringComparer.Ordinal))) return Inspect();
            pending = new(1, Guid.NewGuid().ToString("N"), profile, fingerprint,
                current.ExplicitSections.ToDictionary(pair => pair.Key, pair => pair.Value.ToDictionary(value => value.Key, value => value.Value.ToArray(), StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase), desired, plan.Label);
            evidence.Begin(pending, current.IniBytes);
        }
        try
        {
            evidence.EnsureBackup(pending, current.IniBytes);
            if (!Value(current, profile.BoxName, "BorderColor").SequenceEqual([pending.BorderColor], StringComparer.Ordinal))
                current = environment.ApplyAppearance(store, profile, current, "BorderColor", pending.BorderColor);
            if (pending.BoxAlias is not null && !Value(current, profile.BoxName, "BoxAlias").SequenceEqual([pending.BoxAlias], StringComparer.Ordinal))
                current = environment.ApplyAppearance(store, profile, current, "BoxAlias", pending.BoxAlias);
            var final = environment.Inspect(store, profile).Normalize();
            if (store.Load() != profile || Blocker(final, profile) is not null || !CanResume(pending, final, profile) ||
                !Value(final, profile.BoxName, "BorderColor").SequenceEqual([pending.BorderColor], StringComparer.Ordinal) ||
                (pending.BoxAlias is not null && !Value(final, profile.BoxName, "BoxAlias").SequenceEqual([pending.BoxAlias], StringComparer.Ordinal)))
                throw new InvalidDataException("표시 설정의 파일·API 최종 결과를 확인하지 못했습니다.");
            evidence.Complete(pending);
            return Inspect() with { Message = "전용 환경의 표시 설정을 저장했습니다. Sandboxie-Plus 관리 프로그램을 열어야 테두리가 표시됩니다." };
        }
        catch (Exception error)
        {
            try { evidence.RecordFailure(pending, error); } catch (Exception) { /* Original intent and backup remain authoritative and blocking. */ }
            throw new InvalidOperationException("표시 설정을 완료하지 못했습니다. 백업·변경 의도를 보존하고 실행을 차단합니다. 표시 설정에서 다시 확인하세요.", error);
        }
    }

    private static string? Blocker(SetupRecoveryObservation observation, BoxProfile profile)
    {
        if (!observation.HostContextVerified || !observation.Install.Verified || !observation.OfficialTemplateBaselineVerified ||
            !observation.ProcessesQueried || observation.OwnedBoxName != profile.BoxName || observation.UserSid != profile.UserSid || observation.StoragePath != profile.StoragePath ||
            !IsolationPolicy.Validate(profile, observation.Configuration).Safe) return "전용 환경의 소유 정보·설치·현재 격리 정책을 확인하지 못했습니다.";
        if (observation.BoxProcessIds.Count != 0) return "보조 계정을 종료한 뒤 표시 설정을 저장하세요.";
        if (!observation.Ui.Fresh || observation.Ui.ApprovedManagementProcesses.Count != 0) return "Sandboxie 관리 프로그램과 설정 도구를 닫은 뒤 저장하세요. 프로그램을 자동 종료하지 않습니다.";
        return null;
    }

    private static bool CanResume(SecondaryAppearanceIntent intent, SetupRecoveryObservation current, BoxProfile profile)
    {
        var now = SetupRecoveryFingerprint.FromObservation(current, profile);
        if (intent.Profile != profile || intent.BeforeFingerprint.ConsentKey != now.ConsentKey) return false;
        foreach (var section in intent.BeforeSections.Keys.Union(current.ExplicitSections.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var before = intent.BeforeSections.TryGetValue(section, out var original) ? original : new Dictionary<string, string[]>();
            var after = current.ExplicitSections.TryGetValue(section, out var actual) ? actual : new Dictionary<string, string[]>();
            foreach (var key in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase))
            {
                var first = Find(before, key); var second = Find(after, key);
                if (first.SequenceEqual(second, StringComparer.Ordinal)) continue;
                if (!section.Equals(profile.BoxName, StringComparison.OrdinalIgnoreCase)) return false;
                var target = key.Equals("BorderColor", StringComparison.OrdinalIgnoreCase) ? intent.BorderColor : key.Equals("BoxAlias", StringComparison.OrdinalIgnoreCase) ? intent.BoxAlias : null;
                if (target is null || !second.SequenceEqual([target], StringComparer.Ordinal)) return false;
            }
        }
        return true;
    }

    private static string[] Value(SetupRecoveryObservation observation, string section, string key) => observation.ExplicitSections.TryGetValue(section, out var values) ? Find(values, key) : [];
    private static string[] Find(IReadOnlyDictionary<string, string[]> values, string key) => values.FirstOrDefault(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value ?? [];
}
