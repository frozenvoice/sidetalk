using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sidetalk.Core;

/// <summary>The state exposed by the setup/recovery flow.  A blocked state is fail-closed.</summary>
public enum SetupRecoveryState
{
    Healthy,
    FirstSetup,
    ApprovalRequired,
    AutomaticallyApplicable,
    Blocked,
    Interrupted,
    Applied,
    Failed
}

public enum SetupRecoveryChangeKind
{
    RemoveGlobalTemplate,
    SetUserSetting,
    RemoveUserSetting,
    AddOwnedBoxSetting,
    RepairOwnedBoxSetting,
    Other
}

/// <summary>A single, explicitly approved setting edit.  Values are ordered and duplicate-sensitive.</summary>
public sealed record SetupRecoveryChange(
    string Section,
    string Key,
    IReadOnlyList<string> CurrentValues,
    IReadOnlyList<string> DesiredValues,
    SetupRecoveryChangeKind Kind = SetupRecoveryChangeKind.Other,
    string Description = "")
{
    public bool IsNoOp => CurrentValues.SequenceEqual(DesiredValues, StringComparer.Ordinal);
    public string Identity => Section + "\u001f" + Key;

    public SetupRecoveryChange Normalize()
    {
        if (string.IsNullOrWhiteSpace(Section) || string.IsNullOrWhiteSpace(Key) ||
            Section.IndexOfAny(['\r', '\n', '\0']) >= 0 || Key.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new InvalidDataException("복구 설정 이름이 유효하지 않습니다.");
        var current = CurrentValues?.ToArray() ?? throw new InvalidDataException("복구 현재 값이 없습니다.");
        var desired = DesiredValues?.ToArray() ?? throw new InvalidDataException("복구 목표 값이 없습니다.");
        foreach (var value in current.Concat(desired))
            if (value is null || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new InvalidDataException("복구 설정 값이 유효하지 않습니다.");
        return this with { CurrentValues = current, DesiredValues = desired, Description = Description ?? string.Empty };
    }
}

public sealed record SetupRecoveryProcessIdentity(
    int ProcessId,
    long CreationTimeUtcTicks,
    string ImagePath,
    string? UserSid = null,
    int? SessionId = null)
{
    public SetupRecoveryProcessIdentity Normalize()
    {
        if (ProcessId <= 0 || CreationTimeUtcTicks <= 0 || string.IsNullOrWhiteSpace(ImagePath) || !Path.IsPathFullyQualified(ImagePath))
            throw new InvalidDataException("복구 대상 관리 프로세스 식별자가 유효하지 않습니다.");
        return this with { ImagePath = Path.GetFullPath(ImagePath) };
    }
}

public sealed record SetupRecoveryInstallContext(
    string SandboxiePath = "",
    string StartExePath = "",
    string Version = "",
    bool Verified = false,
    string Detail = "");

public sealed record SetupRecoveryUiState(
    bool Fresh,
    IReadOnlyList<SetupRecoveryProcessIdentity> ApprovedManagementProcesses,
    bool CompatibilityDialogObserved = false,
    string Detail = "")
{
    public static SetupRecoveryUiState Empty { get; } = new(true, Array.Empty<SetupRecoveryProcessIdentity>());
}

/// <summary>
/// A scope identity covers every input that can make a repair unsafe: the actual
/// configuration path and bytes, explicit/effective settings, all sections, profile,
/// install identity and current Windows user.
/// </summary>
public sealed record SetupRecoveryFingerprint(
    string ConfigPath,
    string IniSha256,
    string ExplicitSha256,
    string EffectiveSha256,
    string SectionsSha256,
    string ProfileSha256,
    string InstallSha256,
    string UserSid,
    string BoxName,
    string StoragePath,
    string UiSha256 = "")
{
    public string ConsentKey => string.Join("|", ConfigPath, ProfileSha256, InstallSha256, UserSid, BoxName, StoragePath);

    public string ScopeKey => string.Join("|", ConfigPath, IniSha256, ExplicitSha256, EffectiveSha256,
        SectionsSha256, ProfileSha256, InstallSha256, UiSha256, UserSid, BoxName, StoragePath);

    public static SetupRecoveryFingerprint FromObservation(SetupRecoveryObservation observation, BoxProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var iniHash = Convert.ToHexString(SHA256.HashData(observation.IniBytes ?? Array.Empty<byte>()));
        var explicitHash = HashConfiguration(observation.Configuration?.ExplicitBox, observation.Configuration?.ExplicitGlobals);
        var effectiveHash = HashConfiguration(observation.Configuration?.EffectiveBox, observation.Configuration?.EffectiveGlobals);
        var sectionsHash = HashSections(observation.ExplicitSections);
        var profileHash = HashText(profile is null ? string.Empty : JsonSerializer.Serialize(profile, RecoveryJson.Options));
        var installHash = HashText(JsonSerializer.Serialize(new { observation.Install.SandboxiePath, observation.Install.StartExePath, observation.Install.Version, observation.Install.Verified }, RecoveryJson.Options));
        var uiHash = HashText(JsonSerializer.Serialize(observation.Ui, RecoveryJson.Options));
        return new(
            observation.ConfigPath ?? string.Empty,
            iniHash,
            explicitHash,
            effectiveHash,
            sectionsHash,
            profileHash,
            installHash,
            observation.UserSid ?? string.Empty,
            profile?.BoxName ?? observation.OwnedBoxName ?? string.Empty,
            profile?.StoragePath ?? observation.StoragePath ?? string.Empty,
            uiHash);
    }

    private static string HashConfiguration(IReadOnlyDictionary<string, string[]>? box, IReadOnlyDictionary<string, string[]>? globals)
    {
        var builder = new StringBuilder();
        AppendDictionary(builder, "box", box);
        AppendDictionary(builder, "global", globals);
        return HashText(builder.ToString());
    }

    private static string HashSections(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>>? sections)
    {
        var builder = new StringBuilder();
        if (sections is not null)
            foreach (var section in sections.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append("section\n").Append(section.Key).Append('\n');
                AppendDictionary(builder, "values", section.Value);
            }
        return HashText(builder.ToString());
    }

    private static void AppendDictionary(StringBuilder builder, string label, IReadOnlyDictionary<string, string[]>? values)
    {
        builder.Append(label).Append('\n');
        if (values is null) return;
        foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            foreach (var value in pair.Value)
                builder.Append(pair.Key).Append('=').Append(value).Append('\n');
    }

    private static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>A fresh, read-only capture supplied by the verified native boundary.</summary>
public sealed record SetupRecoveryObservation(
    string ConfigPath,
    byte[] IniBytes,
    ConfigurationLayers Configuration,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> ExplicitSections,
    IReadOnlyList<SetupRecoveryChange> ProposedChanges,
    string? OwnedBoxName,
    string? UserSid,
    string? StoragePath,
    IReadOnlyList<string> OtherBoxNames,
    IReadOnlyList<string> UnknownTemplateNames,
    IReadOnlyList<int> BoxProcessIds,
    bool ProcessesQueried,
    SetupRecoveryInstallContext Install,
    SetupRecoveryUiState Ui,
    bool HostContextVerified,
    bool OfficialTemplateBaselineVerified,
    bool PolicySafe,
    string Detail,
    DateTimeOffset CapturedAtUtc)
{
    public string? CurrentUserSettingsSection { get; init; }

    public SetupRecoveryObservation Normalize()
    {
        if (string.IsNullOrWhiteSpace(ConfigPath) || !Path.IsPathFullyQualified(ConfigPath))
            throw new InvalidDataException("Sandboxie의 실제 설정 경로를 확인할 수 없습니다.");
        if (IniBytes is null || Configuration is null || ExplicitSections is null || ProposedChanges is null ||
            OtherBoxNames is null || UnknownTemplateNames is null || BoxProcessIds is null || Install is null || Ui is null)
            throw new InvalidDataException("복구 상태 조회 결과가 완전하지 않습니다.");
        if (!ProcessesQueried)
            throw new InvalidDataException("전용 환경 프로세스를 조회하지 못했습니다.");
        if (BoxProcessIds.Any(pid => pid <= 0))
            throw new InvalidDataException("전용 환경 프로세스 식별자가 유효하지 않습니다.");
        return this with
        {
            ConfigPath = Path.GetFullPath(ConfigPath),
            IniBytes = IniBytes.ToArray(),
            ProposedChanges = ProposedChanges.Select(change => change.Normalize()).ToArray(),
            OtherBoxNames = OtherBoxNames.ToArray(),
            UnknownTemplateNames = UnknownTemplateNames.ToArray(),
            BoxProcessIds = BoxProcessIds.ToArray(),
            Detail = Detail ?? string.Empty
        };
    }
}

public sealed record SetupRecoveryPlan(
    string PlanId,
    int PlanRevision,
    SetupRecoveryState State,
    string Summary,
    IReadOnlyList<SetupRecoveryChange> Changes,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Evidence,
    bool RequiresApproval,
    bool AllowAutomatic,
    string ConfigPath,
    SetupRecoveryFingerprint Fingerprint,
    string? BackupPath,
    string HistoryPath,
    bool IsFirstSetup = false,
    DateTimeOffset CreatedAtUtc = default,
    bool IsReconciliation = false,
    string? InterruptedOperationId = null,
    BoxProfile? ProposedProfile = null)
{
    public bool CanApply => State is SetupRecoveryState.FirstSetup or SetupRecoveryState.ApprovalRequired or SetupRecoveryState.AutomaticallyApplicable or SetupRecoveryState.Interrupted;
    public bool IsHealthy => State == SetupRecoveryState.Healthy;
    public string ScopeFingerprint => Fingerprint.ScopeKey;
    public IReadOnlyList<SetupRecoveryChange> ApprovedChanges => Changes;
}

public sealed record SetupRecoveryApplyResult(
    string OperationId,
    SetupRecoveryState State,
    string Detail,
    bool Verified,
    string? BackupPath,
    string HistoryPath,
    SetupRecoveryObservation? Observation = null)
{
    public bool Succeeded => Verified && (State is SetupRecoveryState.Healthy or SetupRecoveryState.Applied);
}

public sealed record SetupRecoveryChangeResult(
    bool CommandSucceeded,
    bool Verified,
    string Detail = "");

public sealed record SetupRecoveryUiResult(
    bool SafeToProceed,
    string Detail = "")
{
    public static SetupRecoveryUiResult NoUi { get; } = new(true);
}

public interface ISetupRecoveryEnvironment
{
    SetupRecoveryObservation Inspect(ProfileStore store, BoxProfile? profile, CancellationToken cancellationToken = default);

    /// <summary>Quiesces only the process identities captured in the fresh observation.</summary>
    SetupRecoveryUiResult QuiesceManagementUi(SetupRecoveryObservation observation, SetupRecoveryPlan plan,
        CancellationToken cancellationToken = default) => SetupRecoveryUiResult.NoUi;

    /// <summary>Applies exactly one approved setting through the verified native boundary.</summary>
    SetupRecoveryChangeResult ApplyChange(SetupRecoveryObservation observation, SetupRecoveryPlan plan,
        SetupRecoveryChange change, CancellationToken cancellationToken = default);
}

internal static class RecoveryJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}
