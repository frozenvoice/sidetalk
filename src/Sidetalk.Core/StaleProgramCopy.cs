using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sidetalk.Core;

/// <summary>
/// An older copy of the official KakaoTalk program folder inside the owned box. Sandboxie shows a
/// box copy in front of the host file, so after the host updates, the old executable runs beside
/// the new host files and KakaoTalk reports damaged files. Program files only; never account data.
/// </summary>
public sealed record StaleProgramCopyObservation(string Directory, string CopyVersion, string HostVersion,
    int FileCount, long TotalBytes, string Fingerprint, bool CanSetAside, string Guidance);

public sealed record ProgramFileIdentity(string Version, bool PublisherVerified);
public sealed record StaleProgramCopyFile(string RelativePath, long Length, long LastWriteUtcTicks);

/// <summary>A read-only listing of one exact directory. Created only by <see cref="StaleProgramCopy.Scan"/>.</summary>
public sealed class StaleProgramCopyScan
{
    internal StaleProgramCopyScan(string directory, string executablePath, string copyVersion, string hostVersion,
        IReadOnlyList<StaleProgramCopyFile> files, string fingerprint)
    {
        Directory = directory;
        ExecutablePath = executablePath;
        CopyVersion = copyVersion;
        HostVersion = hostVersion;
        Files = files;
        Fingerprint = fingerprint;
    }

    public string Directory { get; }
    public string ExecutablePath { get; }
    public string CopyVersion { get; }
    public string HostVersion { get; }
    public IReadOnlyList<StaleProgramCopyFile> Files { get; }
    public string Fingerprint { get; }
    public long TotalBytes => Files.Sum(file => file.Length);
}

public static class StaleProgramCopy
{
    internal const string PreservedFolderName = "program-copy-preserved";
    private const int MaximumEntries = 4096;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>
    /// Sandboxie's documented file layout for a local drive path: FileRootPath\drive\LETTER\rest.
    /// The result is only ever looked at; a different layout simply finds nothing.
    /// </summary>
    internal static string? MappedDirectory(string storagePath, string hostExecutable)
    {
        if (!Path.IsPathFullyQualified(storagePath) || !Path.IsPathFullyQualified(hostExecutable)) return null;
        var hostDirectory = Path.GetDirectoryName(Path.GetFullPath(hostExecutable));
        if (hostDirectory is null || hostDirectory.Length < 4 || hostDirectory[1] != ':' || hostDirectory[2] != '\\' ||
            !char.IsAsciiLetter(hostDirectory[0])) return null;
        var root = Path.GetFullPath(storagePath).TrimEnd(Path.DirectorySeparatorChar);
        var mapped = Path.GetFullPath(Path.Combine(root, "drive", char.ToUpperInvariant(hostDirectory[0]).ToString(), hostDirectory[3..]));
        return mapped.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? mapped : null;
    }

    /// <summary>
    /// Returns a scan only when the mapped folder holds a Kakao-signed KakaoTalk.exe that is older
    /// than the verified host executable. Anything unverifiable returns null: nothing is offered.
    /// Throws when the folder itself cannot be trusted (links, too many entries).
    /// </summary>
    public static StaleProgramCopyScan? Scan(BoxProfile profile, DependencyInfo host, Func<string, ProgramFileIdentity> readProgramFile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(readProgramFile);
        if (!host.Available || host.Path is null || !Version.TryParse(host.Version, out var hostVersion)) return null;
        var directory = MappedDirectory(profile.StoragePath, host.Path);
        if (directory is null) return null;
        ProfileStore.RejectReparseAncestors(directory);
        if (!System.IO.Directory.Exists(directory)) return null;
        var executable = Path.Combine(directory, "KakaoTalk.exe");
        if (!File.Exists(executable)) return null;

        var files = new List<StaleProgramCopyFile>();
        var entries = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(directory));
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in current.EnumerateFileSystemInfos())
            {
                if (++entries > MaximumEntries)
                    throw new InvalidDataException("전용 환경의 프로그램 사본 폴더에 항목이 너무 많아 확인하지 않았습니다.");
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("전용 환경의 프로그램 사본 폴더에 연결(reparse point)이 있어 확인하지 않았습니다.");
                if (entry is DirectoryInfo child) pending.Push(child);
                else if (entry is FileInfo file)
                    files.Add(new(Path.GetRelativePath(directory, file.FullName), file.Length, file.LastWriteTimeUtc.Ticks));
            }
        }

        var identity = readProgramFile(executable);
        if (!identity.PublisherVerified || !Version.TryParse(identity.Version, out var copyVersion) || copyVersion >= hostVersion)
            return null;
        files.Sort((first, second) => string.CompareOrdinal(first.RelativePath, second.RelativePath));
        var text = new StringBuilder().Append(directory.ToUpperInvariant()).Append('\n').Append(identity.Version).Append('\n').Append(host.Version);
        foreach (var file in files)
            text.Append('\n').Append(file.RelativePath).Append('|').Append(file.Length.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(file.LastWriteUtcTicks.ToString(CultureInfo.InvariantCulture));
        return new(directory, executable, identity.Version, host.Version, files,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))));
    }

    /// <summary>
    /// Moves the scanned folder out of the box with one same-volume rename. Nothing is deleted: the
    /// copy and a record of what it was stay under the SIDETALK data root. Returns the new location.
    /// </summary>
    internal static string SetAside(StaleProgramCopyScan scan, string dataRoot, BoxProfile profile)
    {
        var root = Path.Combine(Path.GetFullPath(dataRoot), PreservedFolderName);
        var folder = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8]);
        ProfileStore.RejectReparseAncestors(folder);
        System.IO.Directory.CreateDirectory(folder);
        ProfileStore.RejectReparseAncestors(folder);
        var executableHash = Hash(scan.ExecutablePath);
        // The record is durable before the move, so an interrupted run still explains itself.
        WriteNew(Path.Combine(folder, "record.json"), new
        {
            Schema = 1, RecordedAtUtc = DateTimeOffset.UtcNow, profile.BoxName, SourceDirectory = scan.Directory,
            scan.CopyVersion, scan.HostVersion, ExecutableSha256 = executableHash, scan.Fingerprint, scan.Files
        });
        var destination = Path.Combine(folder, "KakaoTalk");
        try
        {
            // Directory.Move is a rename and refuses a different volume; it never copies and deletes.
            System.IO.Directory.Move(scan.Directory, destination);
            var moved = Path.Combine(destination, "KakaoTalk.exe");
            if (System.IO.Directory.Exists(scan.Directory) || !File.Exists(moved) ||
                !Hash(moved).Equals(executableHash, StringComparison.Ordinal))
                throw new IOException("프로그램 사본을 옮긴 뒤 결과를 확인하지 못했습니다.");
            WriteNew(Path.Combine(folder, "completed.json"), new { Schema = 1, CompletedAtUtc = DateTimeOffset.UtcNow, PreservedDirectory = destination });
        }
        catch (Exception error)
        {
            try { WriteNew(Path.Combine(folder, "failed.json"), new { Schema = 1, FailedAtUtc = DateTimeOffset.UtcNow, Reason = InspectionFailure.Sanitize(error.Message) }); }
            catch (Exception recording) when (recording is IOException or UnauthorizedAccessException) { error.Data["ProgramCopyFailureRecording"] = recording.Message; }
            throw;
        }
        return destination;
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void WriteNew(string path, object value)
    {
        ProfileStore.RejectReparseAncestors(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, value, Json);
        stream.Flush(flushToDisk: true);
    }
}
