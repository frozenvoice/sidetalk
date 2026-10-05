using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Sidetalk.Core;

/// <summary>
/// Separate opt-in permission for convenient launch. Reads create nothing. Every
/// change is flushed and published without replacement; damaged or partial history
/// is retained and can never grant permission or be silently reset.
/// The caller holds the same CommandJournal lease used by launch and recovery.
/// </summary>
internal sealed class QuickLaunchConsentStore
{
    private const int MaximumRecords = 4096;
    private const long MaximumRecordBytes = 16 * 1024;
    private static readonly Regex RecordName = new(@"\A(?<sequence>[0-9]{20})\.(?<id>[a-f0-9]{32})\.json\z", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string root;
    private readonly string directory;

    public QuickLaunchConsentStore(string profileRoot)
    {
        root = Path.GetFullPath(profileRoot);
        directory = Path.Combine(root, "quick-launch-consent");
    }

    public bool Matches(QuickLaunchApproval approval) => Read().Latest is { Enabled: true } latest &&
        string.Equals(latest.ScopeFingerprint, approval.ScopeFingerprint, StringComparison.Ordinal);

    public void Append(QuickLaunchApproval approval, bool enabled)
    {
        var history = Read();
        // Keep the final readable slot available for turning permission off.
        if (history.Latest?.Sequence >= MaximumRecords || (enabled && history.Latest?.Sequence >= MaximumRecords - 1))
            throw new InvalidDataException("빠른 실행 동의 기록 개수 제한을 초과했습니다. 기존 기록을 보존했습니다.");
        var id = Guid.NewGuid().ToString("N");
        var record = new ConsentRecord(1, (history.Latest?.Sequence ?? 0) + 1, id,
            approval.ScopeFingerprint, enabled, DateTimeOffset.UtcNow, history.Hash);
        ProfileStore.RejectReparseAncestors(root);
        Directory.CreateDirectory(directory);
        ProfileStore.RejectReparseAncestors(directory);
        var temporary = Path.Combine(directory, $".consent.{id}.tmp");
        var destination = Path.Combine(directory, $"{record.Sequence.ToString("D20", CultureInfo.InvariantCulture)}.{id}.json");
        ProfileStore.RejectReparseAncestors(temporary);
        ProfileStore.RejectReparseAncestors(destination);
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, record, Json);
            stream.Flush(true);
        }
        File.Move(temporary, destination, overwrite: false);
    }

    private (ConsentRecord? Latest, string? Hash) Read()
    {
        ProfileStore.RejectReparseAncestors(root);
        ProfileStore.RejectReparseAncestors(directory);
        if (!Directory.Exists(directory)) return (null, null);
        ConsentRecord? latest = null;
        string? previousHash = null;
        var count = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(directory).OrderBy(path => path, StringComparer.Ordinal))
        {
            if (++count > MaximumRecords)
                throw new InvalidDataException("빠른 실행 동의 기록 개수 제한을 초과했습니다.");
            var name = RecordName.Match(Path.GetFileName(path));
            if (!name.Success || !File.Exists(path))
                throw new InvalidDataException("빠른 실행 동의 폴더에 중단되거나 알 수 없는 기록이 있습니다. 원본을 보존했습니다.");
            ProfileStore.RejectReparseAncestors(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > MaximumRecordBytes)
                throw new InvalidDataException("빠른 실행 동의 기록 크기가 유효하지 않습니다.");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            ConsentRecord? record;
            try { record = JsonSerializer.Deserialize<ConsentRecord>(bytes, Json); }
            catch (JsonException error) { throw new InvalidDataException("빠른 실행 동의 기록이 손상되었습니다. 원본을 보존했습니다.", error); }
            if (record is null || record.Schema != 1 || record.Sequence != (latest?.Sequence ?? 0) + 1 ||
                record.Sequence.ToString("D20", CultureInfo.InvariantCulture) != name.Groups["sequence"].Value ||
                record.Id != name.Groups["id"].Value || record.RecordedAtUtc == default || record.RecordedAtUtc.Offset != TimeSpan.Zero ||
                record.ScopeFingerprint is not { Length: 64 } || !record.ScopeFingerprint.All(Uri.IsHexDigit) ||
                !string.Equals(record.PreviousRecordHash, previousHash, StringComparison.Ordinal))
                throw new InvalidDataException("빠른 실행 동의 기록의 순서·범위·연결이 유효하지 않습니다. 원본을 보존했습니다.");
            latest = record;
            previousHash = Convert.ToHexString(SHA256.HashData(bytes));
        }
        return (latest, previousHash);
    }

    private sealed record ConsentRecord(int Schema, long Sequence, string Id, string ScopeFingerprint,
        bool Enabled, DateTimeOffset RecordedAtUtc, string? PreviousRecordHash);
}
