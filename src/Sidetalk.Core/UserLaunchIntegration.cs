using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Sidetalk.Core;

public sealed record UserLaunchOptions(bool DesktopShortcut = false, bool StartMenuShortcut = false, bool LoginStartup = false);
public sealed record UserLaunchIntegrationStatus(UserLaunchOptions Options, bool NeedsUpdate, IReadOnlyList<string>? PreviousTargets = null);
public sealed record UserLaunchUninstallResult(IReadOnlyList<string> Removed, IReadOnlyList<string> Preserved);

public sealed record UserLaunchLocations(string Desktop, string StartMenu, string Startup, string SettingsRoot)
{
    public static UserLaunchLocations ForCurrentUser() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SIDETALK", "user-launch-integration"));
}

public sealed record UserLaunchLink(string TargetPath, string Arguments, string WorkingDirectory,
    string Description, string IconPath, int IconIndex = 0, int ShowCommand = 1);

/// <summary>A link codec, not a process launcher. Tests inject a codec and disposable locations.</summary>
public interface IUserLaunchLinkBackend
{
    byte[] Encode(UserLaunchLink link);
    UserLaunchLink Decode(byte[] bytes);
}

/// <summary>Manages three exact per-user paths; never discovers or removes arbitrary shortcuts.</summary>
public sealed class UserLaunchIntegration
{
    public const string LinkFileName = "보조 카카오톡 실행.lnk";
    public const string LaunchArguments = "--launch-secondary";
    public const string StartupArguments = "--launch-secondary --login-startup";
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly UserLaunchLocations locations;
    private readonly string executable;
    private readonly IUserLaunchLinkBackend backend;
    private string ManifestPath => Path.Combine(locations.SettingsRoot, "manifest.json");
    private string PendingPath => Path.Combine(locations.SettingsRoot, "pending.json");

    public UserLaunchIntegration(UserLaunchLocations locations, string executablePath, IUserLaunchLinkBackend? backend = null)
    {
        ArgumentNullException.ThrowIfNull(locations);
        this.locations = new(CanonicalDirectory(locations.Desktop), CanonicalDirectory(locations.StartMenu),
            CanonicalDirectory(locations.Startup), CanonicalDirectory(locations.SettingsRoot));
        if (new[] { this.locations.Desktop, this.locations.StartMenu, this.locations.Startup, this.locations.SettingsRoot }
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 4)
            throw new InvalidDataException("바로가기와 설정 저장 위치가 서로 겹칩니다.");
        executable = CanonicalExecutable(executablePath);
        ProfileStore.RejectReparseAncestors(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("현재 SIDETALK 실행 파일이 없습니다.", executable);
        this.backend = backend ?? new WindowsUserLaunchLinkBackend();
    }

    /// <summary>Read-only. Incomplete writes and edited links fail closed instead of enabling startup.</summary>
    public UserLaunchIntegrationStatus Inspect()
    {
        var manifest = ReadManifest();
        if (manifest is null) return new(new(), false);
        var needsUpdate = false;
        var previousTargets = new List<string>();
        foreach (var record in manifest.Links)
        {
            var bytes = ReadOptional(record.Path);
            if (bytes is null) needsUpdate = true;
            else RequireOwned(record, bytes);
            if (!SamePath(record.Link.TargetPath, executable))
            {
                needsUpdate = true;
                if (!previousTargets.Contains(record.Link.TargetPath, StringComparer.OrdinalIgnoreCase))
                    previousTargets.Add(record.Link.TargetPath);
            }
        }
        return new(manifest.Options, needsUpdate, previousTargets);
    }

    /// <summary>Called only after an explicit settings choice. Startup defaults to OFF.</summary>
    public UserLaunchIntegrationStatus Apply(UserLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RequireDirectoriesSafe();
        Directory.CreateDirectory(locations.SettingsRoot);
        ProfileStore.RejectReparseAncestors(locations.SettingsRoot);
        var lockPath = Path.Combine(locations.SettingsRoot, "integration.lock");
        ProfileStore.RejectReparseAncestors(lockPath);
        using var integrationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var previousBytes = ReadOptional(ManifestPath);
        var previous = ReadManifest();
        var owner = previous?.OwnerId ?? Guid.NewGuid();
        var links = new List<LinkRecord>();
        var changes = new List<Change>();
        foreach (var kind in Enum.GetValues<LinkKind>())
        {
            var path = LinkPath(kind);
            var oldRecord = previous?.Links.SingleOrDefault(link => link.Kind == kind);
            var oldBytes = ReadOptional(path);
            if (oldRecord is not null && oldBytes is not null) RequireOwned(oldRecord, oldBytes);
            if (Enabled(options, kind))
            {
                if (oldBytes is not null && oldRecord is null)
                    throw new InvalidDataException($"SIDETALK 소유가 아닌 바로가기를 보존했습니다: {path}");
                var link = MakeLink(owner, kind);
                var bytes = oldRecord?.Link == link && oldBytes is not null ? oldBytes : backend.Encode(link);
                if (backend.Decode(bytes) != link) throw new InvalidDataException("생성한 바로가기의 경로·인자를 확인할 수 없습니다.");
                links.Add(new(kind, path, link, Hash(bytes)));
                if (oldBytes is null || !oldBytes.AsSpan().SequenceEqual(bytes)) changes.Add(new(path, oldBytes, bytes, link));
            }
            else if (oldRecord is not null && oldBytes is not null)
                changes.Add(new(path, oldBytes, null));
        }
        if (previous is not null && previous.Options == options && changes.Count == 0 &&
            previous.Links.SequenceEqual(links)) return new(options, false);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new Manifest(1, owner, options, links.ToArray()), Json);
        changes.Add(new(ManifestPath, previousBytes, manifestBytes));
        foreach (var change in changes)
        {
            var parent = Path.GetDirectoryName(change.Path)!;
            ProfileStore.RejectReparseAncestors(parent);
            Directory.CreateDirectory(parent);
            ProfileStore.RejectReparseAncestors(parent);
        }
        ApplyTransaction(changes);
        return Inspect();
    }

    /// <summary>
    /// Uninstall cleanup for this executable only. Removes owned links that target it after
    /// verifying their exact bytes, so none is left pointing at deleted files. Edited links
    /// and links for another installation are preserved. The manifest and the saved choice
    /// stay as they are: a later install reports the missing links and repairs them only on
    /// an explicit settings save. Reads nothing beyond the manifest and its listed paths.
    /// </summary>
    public UserLaunchUninstallResult RemoveLinksForUninstall()
    {
        RequireDirectoriesSafe();
        if (!Directory.Exists(locations.SettingsRoot)) return new([], []);
        var lockPath = Path.Combine(locations.SettingsRoot, "integration.lock");
        ProfileStore.RejectReparseAncestors(lockPath);
        using var integrationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var manifest = ReadManifest();
        if (manifest is null) return new([], []);
        var removed = new List<string>();
        var preserved = new List<string>();
        foreach (var record in manifest.Links.Where(record => SamePath(record.Link.TargetPath, executable)))
        {
            var bytes = ReadOptional(record.Path);
            if (bytes is null) continue;
            try { RequireOwned(record, bytes); }
            catch (InvalidDataException)
            {
                preserved.Add(record.Path);
                continue;
            }
            DeleteVerified(record.Path, bytes);
            removed.Add(record.Path);
        }
        return new(removed, preserved);
    }

    private Manifest? ReadManifest()
    {
        RequireDirectoriesSafe();
        if (File.Exists(PendingPath) || Directory.Exists(PendingPath) ||
            (Directory.Exists(locations.SettingsRoot) && Directory.EnumerateFileSystemEntries(locations.SettingsRoot, "transaction-*").Any()))
            throw new InvalidDataException($"미완료 바로가기 설정 변경을 보존했습니다. 저장 상태를 먼저 확인하세요: {locations.SettingsRoot}");
        var bytes = ReadOptional(ManifestPath);
        if (bytes is null) return null;
        Manifest manifest;
        try { manifest = JsonSerializer.Deserialize<Manifest>(bytes, Json) ?? throw new InvalidDataException("바로가기 설정이 비어 있습니다."); }
        catch (JsonException error) { throw new InvalidDataException("손상된 바로가기 설정을 덮어쓰지 않았습니다.", error); }
        if (manifest.SchemaVersion != 1 || manifest.OwnerId == Guid.Empty || manifest.Options is null || manifest.Links is null ||
            manifest.Links.Length > 3 || manifest.Links.Any(link => link is null) ||
            manifest.Links.Select(link => link.Kind).Distinct().Count() != manifest.Links.Length)
            throw new InvalidDataException("바로가기 설정의 소유 정보·형식을 확인할 수 없습니다.");
        foreach (var kind in Enum.GetValues<LinkKind>())
            if (Enabled(manifest.Options, kind) != manifest.Links.Any(link => link.Kind == kind))
                throw new InvalidDataException("바로가기 설정과 소유 목록이 일치하지 않습니다.");
        foreach (var record in manifest.Links)
        {
            if (!Enum.IsDefined(record.Kind) || record.Link is null || record.Path is null || !SamePath(record.Path, LinkPath(record.Kind)) ||
                record.Hash is null || record.Hash.Length != 64 || record.Hash.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException("바로가기 설정의 경로·해시를 확인할 수 없습니다.");
            var oldTarget = CanonicalExecutable(record.Link.TargetPath);
            if (record.Link != MakeLink(manifest.OwnerId, record.Kind, oldTarget))
                throw new InvalidDataException("바로가기의 이전 설치 경로·인자·소유 표식이 일치하지 않습니다.");
        }
        return manifest;
    }

    private void RequireOwned(LinkRecord record, byte[] bytes)
    {
        if (!Hash(bytes).Equals(record.Hash, StringComparison.OrdinalIgnoreCase) || backend.Decode(bytes) != record.Link)
            throw new InvalidDataException($"사용자가 변경했거나 소유를 확인할 수 없는 바로가기를 보존했습니다: {record.Path}");
    }

    private void ApplyTransaction(List<Change> changes)
    {
        var transaction = Path.Combine(locations.SettingsRoot, "transaction-" + Guid.NewGuid().ToString("N"));
        ProfileStore.RejectReparseAncestors(transaction);
        Directory.CreateDirectory(transaction);
        for (var index = 0; index < changes.Count; index++)
        {
            // Stage and preserve on the destination volume, including redirected
            // per-user folders. Atomic rename must never become a copy/delete.
            var prefix = Path.Combine(Path.GetDirectoryName(changes[index].Path)!,
                ".sidetalk-" + Path.GetFileName(transaction) + "-" + index);
            changes[index].BackupPath = prefix + ".previous";
            changes[index].StagedPath = prefix + ".new";
        }
        var journalBytes = JsonSerializer.SerializeToUtf8Bytes(changes.Select(change => new
        {
            change.Path, PreviousHash = change.OldBytes is null ? null : Hash(change.OldBytes),
            NewHash = change.NewBytes is null ? null : Hash(change.NewBytes), change.BackupPath, change.StagedPath
        }), Json);
        var pendingBytes = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, TransactionPath = transaction }, Json);
        var committed = false;
        try
        {
            WriteNew(Path.Combine(transaction, "changes.json"), journalBytes);
            WriteNew(PendingPath, pendingBytes);
            for (var index = 0; index < changes.Count; index++)
            {
                var change = changes[index];
                if (change.NewBytes is not null) WriteNew(change.StagedPath!, change.NewBytes);
                if (change.OldBytes is not null)
                {
                    using var oldFile = WindowsEvidenceFile.OpenExclusiveSource(change.Path);
                    RequireBytes(oldFile, change.OldBytes);
                    WindowsEvidenceFile.RenameNoReplace(oldFile.SafeFileHandle, change.BackupPath!);
                    change.OldMoved = true;
                }
                if (change.NewBytes is not null)
                {
                    using var staged = WindowsEvidenceFile.OpenExclusiveSource(change.StagedPath!);
                    RequireBytes(staged, change.NewBytes);
                    WindowsEvidenceFile.RenameNoReplace(staged.SafeFileHandle, change.Path);
                    change.NewPublished = true;
                }
            }
            foreach (var change in changes)
            {
                var actual = ReadOptional(change.Path);
                if (change.NewBytes is null ? actual is not null : actual is null || !actual.AsSpan().SequenceEqual(change.NewBytes))
                    throw new IOException("바로가기 설정의 저장 후 검증에 실패했습니다.");
                if (change.NewLink is not null && backend.Decode(actual!) != change.NewLink)
                    throw new IOException("저장한 바로가기의 실행 파일·인자 검증에 실패했습니다.");
            }
            committed = true;
            ClosePending(transaction, pendingBytes);
            CleanTransaction(transaction, changes, journalBytes, pendingBytes);
        }
        catch (Exception failure)
        {
            if (committed)
                throw new IOException($"바로가기 저장 후 정리를 완료하지 못했습니다. 근거를 보존했습니다: {transaction}", failure);
            var rollbackFailures = new List<Exception>();
            foreach (var change in changes.AsEnumerable().Reverse())
            {
                try
                {
                    if (change.NewPublished)
                    {
                        using var published = WindowsEvidenceFile.OpenExclusiveSource(change.Path);
                        RequireBytes(published, change.NewBytes!);
                        WindowsEvidenceFile.RenameNoReplace(published.SafeFileHandle, change.StagedPath! + ".rollback");
                    }
                    if (change.OldMoved)
                    {
                        using var backup = WindowsEvidenceFile.OpenExclusiveSource(change.BackupPath!);
                        RequireBytes(backup, change.OldBytes!);
                        WindowsEvidenceFile.RenameNoReplace(backup.SafeFileHandle, change.Path);
                    }
                }
                catch (Exception error) { rollbackFailures.Add(error); }
            }
            if (rollbackFailures.Count == 0)
            {
                try
                {
                    if (File.Exists(PendingPath)) ClosePending(transaction, pendingBytes);
                    CleanTransaction(transaction, changes, journalBytes, pendingBytes);
                }
                catch (Exception error) { rollbackFailures.Add(error); }
            }
            if (rollbackFailures.Count > 0)
                throw new IOException($"바로가기 설정 변경과 복원에 실패했습니다. 원본·실패 근거를 보존했습니다: {transaction}",
                    new AggregateException(new[] { failure }.Concat(rollbackFailures)));
            throw new IOException("바로가기 설정을 저장하지 못해 이전 상태로 복원했습니다.", failure);
        }
    }

    private void ClosePending(string transaction, byte[] expected)
    {
        using var pending = WindowsEvidenceFile.OpenExclusiveSource(PendingPath);
        RequireBytes(pending, expected);
        WindowsEvidenceFile.RenameNoReplace(pending.SafeFileHandle, Path.Combine(transaction, "pending-closed.json"));
    }

    private static void CleanTransaction(string path, List<Change> changes, byte[] journalBytes, byte[] pendingBytes)
    {
        foreach (var change in changes)
        {
            foreach (var file in new[] { change.BackupPath, change.StagedPath, change.StagedPath + ".rollback" })
            {
                if (file is null || !File.Exists(file)) continue;
                ProfileStore.RejectReparseAncestors(file);
                var expected = file == change.BackupPath ? change.OldBytes : change.NewBytes;
                DeleteVerified(file, expected ?? throw new IOException("예상하지 않은 임시 설정 파일을 보존했습니다."));
            }
        }
        ProfileStore.RejectReparseAncestors(path);
        if (Directory.EnumerateDirectories(path).Any()) throw new IOException("설정 변경 임시 폴더에 예상하지 않은 하위 폴더가 있습니다.");
        foreach (var file in Directory.EnumerateFiles(path))
        {
            var name = Path.GetFileName(file);
            var expected = name switch
            {
                "changes.json" => journalBytes,
                "pending-closed.json" => pendingBytes,
                _ => throw new IOException("설정 변경 임시 폴더의 알 수 없는 파일을 보존했습니다.")
            };
            DeleteVerified(file, expected);
        }
        Directory.Delete(path, recursive: false);
    }

    private static void DeleteVerified(string path, byte[] expected)
    {
        using var file = WindowsEvidenceFile.OpenExclusiveSource(path);
        RequireBytes(file, expected);
        var disposition = new FileDispositionInfo { DeleteFile = 1 };
        if (!SetFileInformationByHandle(file.SafeFileHandle, 4, ref disposition, 1))
            throw new IOException("확인한 임시 설정 파일의 삭제를 완료하지 못했습니다.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    private static void RequireBytes(FileStream stream, byte[] expected)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        if (!memory.ToArray().AsSpan().SequenceEqual(expected))
            throw new IOException("설정 파일이 검사 후 변경되어 덮어쓰거나 삭제하지 않았습니다.");
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        ProfileStore.RejectReparseAncestors(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static byte[]? ReadOptional(string path)
    {
        ProfileStore.RejectReparseAncestors(path);
        if (Directory.Exists(path)) throw new InvalidDataException("설정 파일 위치에 폴더가 있습니다.");
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 1024 * 1024) throw new InvalidDataException("바로가기 설정 파일의 크기가 허용 범위를 넘습니다.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private void RequireDirectoriesSafe()
    {
        foreach (var path in new[] { locations.Desktop, locations.StartMenu, locations.Startup, locations.SettingsRoot })
            ProfileStore.RejectReparseAncestors(path);
    }

    private static string CanonicalDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.Contains('"') || path.Any(char.IsControl)) throw new InvalidDataException("현재 사용자의 정확한 로컬 경로가 필요합니다.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string CanonicalExecutable(string path)
    {
        var full = CanonicalDirectory(path);
        if (!Path.GetFileName(full).Equals("Sidetalk.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("바로가기는 현재 Sidetalk.exe만 실행할 수 있습니다.");
        return full;
    }

    private string LinkPath(LinkKind kind) => Path.Combine(kind switch
    {
        LinkKind.Desktop => locations.Desktop,
        LinkKind.StartMenu => locations.StartMenu,
        LinkKind.Startup => locations.Startup,
        _ => throw new InvalidDataException("알 수 없는 바로가기 종류입니다.")
    }, LinkFileName);

    private UserLaunchLink MakeLink(Guid owner, LinkKind kind, string? target = null)
    {
        var image = target ?? executable;
        return new(image, kind == LinkKind.Startup ? StartupArguments : LaunchArguments, Path.GetDirectoryName(image)!,
            $"SIDETALK.UserLaunch.v1:{owner:N}:{kind}", image);
    }

    private static bool Enabled(UserLaunchOptions options, LinkKind kind) => kind switch
    {
        LinkKind.Desktop => options.DesktopShortcut,
        LinkKind.StartMenu => options.StartMenuShortcut,
        LinkKind.Startup => options.LoginStartup,
        _ => false
    };
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool SamePath(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
    private enum LinkKind { Desktop, StartMenu, Startup }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo { public byte DeleteFile; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
        ref FileDispositionInfo information, uint size);
    private sealed record Manifest(int SchemaVersion, Guid OwnerId, UserLaunchOptions Options, LinkRecord[] Links);
    private sealed record LinkRecord(LinkKind Kind, string Path, UserLaunchLink Link, string Hash);
    private sealed class Change(string path, byte[]? oldBytes, byte[]? newBytes, UserLaunchLink? newLink = null)
    {
        public string Path { get; } = path;
        public byte[]? OldBytes { get; } = oldBytes;
        public byte[]? NewBytes { get; } = newBytes;
        public UserLaunchLink? NewLink { get; } = newLink;
        public string? BackupPath { get; set; }
        public string? StagedPath { get; set; }
        public bool OldMoved { get; set; }
        public bool NewPublished { get; set; }
    }
}

/// <summary>Official Shell Link COM API. Target and arguments are separate typed properties.</summary>
public sealed class WindowsUserLaunchLinkBackend : IUserLaunchLinkBackend
{
    public byte[] Encode(UserLaunchLink link) => OnSta(() => WithTemporaryFile(path =>
    {
        object shell = new ShellLink();
        try
        {
            var shortcut = (IShellLinkW)shell;
            shortcut.SetPath(link.TargetPath);
            shortcut.SetArguments(link.Arguments);
            shortcut.SetWorkingDirectory(link.WorkingDirectory);
            shortcut.SetDescription(link.Description);
            shortcut.SetIconLocation(link.IconPath, link.IconIndex);
            shortcut.SetShowCmd(link.ShowCommand);
            ((IPersistFile)shell).Save(path, true);
            return File.ReadAllBytes(path);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }));

    public UserLaunchLink Decode(byte[] bytes) => OnSta(() => WithTemporaryFile(path =>
    {
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) output.Write(bytes);
        object shell = new ShellLink();
        try
        {
            ((IPersistFile)shell).Load(path, 0);
            var shortcut = (IShellLinkW)shell;
            var target = new StringBuilder(32768);
            var arguments = new StringBuilder(32768);
            var workingDirectory = new StringBuilder(32768);
            var description = new StringBuilder(32768);
            var icon = new StringBuilder(32768);
            shortcut.GetPath(target, target.Capacity, IntPtr.Zero, 4); // SLGP_RAWPATH; never Resolve or execute.
            shortcut.GetArguments(arguments, arguments.Capacity);
            shortcut.GetWorkingDirectory(workingDirectory, workingDirectory.Capacity);
            shortcut.GetDescription(description, description.Capacity);
            shortcut.GetIconLocation(icon, icon.Capacity, out var iconIndex);
            shortcut.GetShowCmd(out var show);
            return new UserLaunchLink(target.ToString(), arguments.ToString(), workingDirectory.ToString(),
                description.ToString(), icon.ToString(), iconIndex, show);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }));

    private static T OnSta<T>(Func<T> body)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return body();
        T? value = default;
        Exception? error = null;
        var thread = new Thread(() => { try { value = body(); } catch (Exception failure) { error = failure; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return value!;
    }

    private static T WithTemporaryFile<T>(Func<string, T> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sidetalk-link-codec-" + Guid.NewGuid().ToString("N"));
        ProfileStore.RejectReparseAncestors(directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "link.lnk");
        try { return body(path); }
        finally
        {
            ProfileStore.RejectReparseAncestors(path);
            if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory, recursive: false);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
