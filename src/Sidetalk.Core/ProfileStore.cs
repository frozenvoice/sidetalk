using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sidetalk.Core;

public sealed record BoxProfile(int Schema, string BoxName, string OwnerToken, string UserSid, string StoragePath);

public sealed class ProfileStore
{
    private readonly Action<Stream, BoxProfile> profileWriter;
    private readonly Action<string, BoxProfile>? beforePublish;

    public string Root { get; }
    public string ProfilePath => Path.Combine(Root, "profile.json");
    public ProfileStore(string? root = null, Action<Stream, BoxProfile>? profileWriter = null, Action<string, BoxProfile>? beforePublish = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SIDETALK"));
        this.profileWriter = profileWriter ?? ((stream, profile) => JsonSerializer.Serialize(stream, profile, new JsonSerializerOptions { WriteIndented = true }));
        this.beforePublish = beforePublish;
    }
    public BoxProfile? Load()
    {
        RejectReparseAncestors(Root);
        RejectReparseAncestors(ProfilePath);
        BoxProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<BoxProfile>(File.ReadAllText(ProfilePath));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        if (profile is null) throw new InvalidDataException("SIDETALK 프로필을 읽을 수 없습니다. 기존 파일을 삭제하거나 새 환경으로 덮어쓰지 마세요.");
        Validate(profile);
        return profile;
    }
    public BoxProfile Create()
    {
        var existing = Load();
        if (existing is not null) return existing;
        Directory.CreateDirectory(Root);
        RejectReparseAncestors(Root);
        var id = Guid.NewGuid().ToString("N");
        var name = "SideTalk_" + id[..23];
        var profile = new BoxProfile(1, name, Guid.NewGuid().ToString("N"), CurrentSid(), Path.Combine(Root, "Boxes", name));
        var temporaryPath = Path.Combine(Root, $".profile.{id}.tmp");
        using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            profileWriter(stream, profile);
            stream.Flush(true);
        }

        beforePublish?.Invoke(temporaryPath, profile);
        try
        {
            File.Move(temporaryPath, ProfilePath, overwrite: false);
            return profile;
        }
        catch (IOException) when (File.Exists(ProfilePath))
        {
            // Another creator may have published first. Load it only after the
            // no-overwrite move fails; a corrupt winner still fails closed.
            var raced = Load();
            if (raced is not null) return raced;
            throw;
        }
    }
    /// <summary>Publishes an exact, pre-approved candidate without replacing an existing profile.</summary>
    public BoxProfile CreateCandidate(BoxProfile candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateIdentity(candidate);
        if (candidate.UserSid != CurrentSid() || !string.Equals(candidate.StoragePath, Path.Combine(Root, "Boxes", candidate.BoxName), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("승인된 SIDETALK 프로필 후보가 현재 사용자 또는 저장 경로와 다릅니다.");
        var existing = Load();
        if (existing is not null)
        {
            if (existing != candidate) throw new InvalidDataException("기존 SIDETALK 프로필이 승인된 후보와 다릅니다.");
            return existing;
        }
        Directory.CreateDirectory(Root);
        RejectReparseAncestors(Root);
        var temporaryPath = Path.Combine(Root, $".profile.{candidate.OwnerToken}.tmp");
        using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            profileWriter(stream, candidate);
            stream.Flush(true);
        }
        beforePublish?.Invoke(temporaryPath, candidate);
        try
        {
            File.Move(temporaryPath, ProfilePath, overwrite: false);
            return candidate;
        }
        catch (IOException) when (File.Exists(ProfilePath))
        {
            var raced = Load();
            if (raced == candidate) return raced;
            throw new InvalidDataException("경합으로 게시된 SIDETALK 프로필이 승인된 후보와 다릅니다.");
        }
    }

    public void Validate(BoxProfile profile)
    {
        ValidateIdentity(profile);
        if (profile.UserSid != CurrentSid() ||
            !string.Equals(profile.StoragePath, Path.Combine(Root, "Boxes", profile.BoxName), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("전용 환경의 소유 정보가 일치하지 않습니다. 실행과 종료를 차단했습니다. profile.json을 복구한 뒤 다시 확인하세요.");
        RejectReparseAncestors(profile.StoragePath);
    }
    public static void ValidateIdentity(BoxProfile profile)
    {
        if (profile.Schema != 1 || !Regex.IsMatch(profile.BoxName ?? "", @"\ASideTalk_[a-f0-9]{23}\z", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(profile.OwnerToken ?? "", @"\A[a-f0-9]{32}\z", RegexOptions.CultureInvariant) ||
            string.IsNullOrWhiteSpace(profile.UserSid) || !Path.IsPathFullyQualified(profile.StoragePath))
            throw new InvalidDataException("전용 환경 식별자가 유효하지 않습니다.");
    }
    public static string CurrentSid() => WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Windows 사용자 식별자를 확인할 수 없습니다.");
    public static void RejectReparseAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("연결 또는 재분석 지점이 있는 경로는 사용할 수 없습니다: " + current);
    }
}
