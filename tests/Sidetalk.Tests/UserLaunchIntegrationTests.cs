using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class UserLaunchIntegrationTests
{
    public static IEnumerable<(string Name, Action Run)> Cases =>
    [
        ("User launch defaults to OFF and inspection creates no files", DefaultsAreReadOnly),
        ("User launch creates and removes only its manifest-bound links", CreateAndRemove),
        ("Login startup is an independent explicit option", IndependentStartup),
        ("Moved installations update only owned links and preserve old installation", MovedInstallation),
        ("User launch preserves an unowned same-name collision", UnownedCollision),
        ("User launch refuses edited links during inspection and removal", EditedLinks),
        ("User launch preserves corrupt and redirected manifests", CorruptManifest),
        ("User launch manifest publication race restores links without overwriting collision", ManifestRace),
        ("User launch final verification failure restores exact original bytes", VerificationRollback),
        ("User launch rollback failure preserves original bytes and blocks startup", FailedRollback),
        ("User launch concurrent settings writes are excluded", ConcurrentSettings),
        ("User launch requires a canonical existing Sidetalk executable", ExecutableValidation),
        ("User launch missing links require explicit repair before startup", MissingOwnedLink),
        ("Windows Shell Link COM roundtrip uses disposable locations and exact Unicode paths", NativeComRoundtrip),
        ("Moved installation reports the previous executable without rewriting links", PreviousTargetsAreReported),
        ("Uninstall removes only verified owned links for this executable and keeps the choice", UninstallRemovesOwnedLinks),
        ("Uninstall preserves edited links, other installations and unowned collisions", UninstallPreservesForeignLinks),
        ("Uninstall without settings creates nothing", UninstallWithoutSettingsIsReadOnly),
        ("Uninstall refuses corrupt or interrupted settings without deleting links", UninstallFailsClosed)
    ];

    private static void PreviousTargetsAreReported()
    {
        var fixture = new Fixture();
        var options = new UserLaunchOptions(true, true, false);
        fixture.Integration.Apply(options);
        var snapshot = fixture.SnapshotManaged();
        var installed = new UserLaunchIntegration(fixture.Locations, fixture.CreateExecutable("Programs SIDETALK 설치"), fixture.Backend);
        var status = installed.Inspect();
        Assert.True(status.NeedsUpdate);
        Assert.Equal(1, status.PreviousTargets!.Count);
        Assert.Equal(fixture.Executable, status.PreviousTargets[0]);
        fixture.AssertSnapshot(snapshot);
        Assert.Equal(0, fixture.Integration.Inspect().PreviousTargets!.Count);
    }

    private static void UninstallRemovesOwnedLinks()
    {
        var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Locations.Desktop);
        var unrelated = Path.Combine(fixture.Locations.Desktop, "SIDETALK.lnk");
        File.WriteAllText(unrelated, "installer-owned main shortcut is not this integration");
        var sentinel = Path.Combine(fixture.Root, "profile.json");
        File.WriteAllText(sentinel, "synthetic profile sentinel");
        var options = new UserLaunchOptions(true, true, true);
        fixture.Integration.Apply(options);
        var manifest = File.ReadAllBytes(fixture.Manifest);
        var result = fixture.Integration.RemoveLinksForUninstall();
        Assert.Equal(3, result.Removed.Count);
        Assert.Equal(0, result.Preserved.Count);
        foreach (var path in fixture.LinkPaths) Assert.False(File.Exists(path));
        Assert.SequenceEqual(manifest, File.ReadAllBytes(fixture.Manifest));
        Assert.Equal("installer-owned main shortcut is not this integration", File.ReadAllText(unrelated));
        Assert.Equal("synthetic profile sentinel", File.ReadAllText(sentinel));
        var reinstalled = fixture.Integration.Inspect();
        Assert.Equal(options, reinstalled.Options);
        Assert.True(reinstalled.NeedsUpdate);
        Assert.Equal(0, fixture.Integration.RemoveLinksForUninstall().Removed.Count);
        Assert.False(fixture.Integration.Apply(options).NeedsUpdate);
        foreach (var path in fixture.LinkPaths) Assert.True(File.Exists(path));
        fixture.AssertNoTransaction();
    }

    private static void UninstallPreservesForeignLinks()
    {
        var fixture = new Fixture();
        fixture.Integration.Apply(new(true, true, false));
        var edited = fixture.Backend.Encode(fixture.Backend.Decode(File.ReadAllBytes(fixture.DesktopLink)) with { Arguments = "--user-edited" });
        File.WriteAllBytes(fixture.DesktopLink, edited);
        var result = fixture.Integration.RemoveLinksForUninstall();
        Assert.Equal(1, result.Removed.Count);
        Assert.Equal(fixture.DesktopLink, result.Preserved.Single());
        Assert.SequenceEqual(edited, File.ReadAllBytes(fixture.DesktopLink));
        Assert.False(File.Exists(fixture.MenuLink));

        // Links that run another installation (e.g. the old artifacts build) stay.
        var other = new Fixture();
        other.Integration.Apply(new(true, false, true));
        var before = other.SnapshotManaged();
        var installed = new UserLaunchIntegration(other.Locations, other.CreateExecutable("Programs SIDETALK"), other.Backend);
        var none = installed.RemoveLinksForUninstall();
        Assert.Equal(0, none.Removed.Count + none.Preserved.Count);
        other.AssertSnapshot(before);

        // A same-name link without a manifest record is never inspected for removal.
        var collision = new Fixture();
        collision.Integration.Apply(new(false, true, false));
        Directory.CreateDirectory(collision.Locations.Desktop);
        File.WriteAllText(collision.DesktopLink, "user-made link with the same name");
        Assert.Equal(1, collision.Integration.RemoveLinksForUninstall().Removed.Count);
        Assert.Equal("user-made link with the same name", File.ReadAllText(collision.DesktopLink));
    }

    private static void UninstallWithoutSettingsIsReadOnly()
    {
        var fixture = new Fixture();
        var result = fixture.Integration.RemoveLinksForUninstall();
        Assert.Equal(0, result.Removed.Count + result.Preserved.Count);
        Assert.False(Directory.Exists(fixture.Locations.SettingsRoot));
        foreach (var folder in fixture.LinkFolders) Assert.False(Directory.Exists(folder));
    }

    private static void UninstallFailsClosed()
    {
        var fixture = new Fixture();
        fixture.Integration.Apply(new(true, true, true));
        var snapshot = fixture.SnapshotManaged();
        var corrupt = Encoding.UTF8.GetBytes("{interrupted settings");
        var manifest = File.ReadAllBytes(fixture.Manifest);
        File.WriteAllBytes(fixture.Manifest, corrupt);
        Assert.Throws<InvalidDataException>(() => fixture.Integration.RemoveLinksForUninstall());
        foreach (var path in fixture.LinkPaths) Assert.SequenceEqual(snapshot[path], File.ReadAllBytes(path));
        Assert.SequenceEqual(corrupt, File.ReadAllBytes(fixture.Manifest));

        File.WriteAllBytes(fixture.Manifest, manifest);
        File.WriteAllText(fixture.Pending, "{}");
        Assert.Throws<InvalidDataException>(() => fixture.Integration.RemoveLinksForUninstall());
        fixture.AssertSnapshot(snapshot);
    }

    private static void DefaultsAreReadOnly()
    {
        var fixture = new Fixture();
        var result = fixture.Integration.Inspect();
        Assert.Equal(new UserLaunchOptions(), result.Options);
        Assert.False(result.NeedsUpdate);
        Assert.False(Directory.Exists(fixture.Locations.SettingsRoot));
        foreach (var folder in fixture.LinkFolders) Assert.False(Directory.Exists(folder));
        Assert.Equal(0, fixture.Backend.EncodeCount);
        Assert.Equal(0, fixture.Backend.DecodeCount);
    }

    private static void CreateAndRemove()
    {
        var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Locations.Desktop);
        var unrelated = Path.Combine(fixture.Locations.Desktop, "another application.lnk");
        File.WriteAllText(unrelated, "foreign shortcut bytes");
        var sentinel = Encoding.UTF8.GetBytes("synthetic profile/account sentinel — never actual user data");
        var data = Path.Combine(fixture.Root, "preserved-profile.json");
        File.WriteAllBytes(data, sentinel);
        var options = new UserLaunchOptions(true, true, true);
        Assert.Equal(options, fixture.Integration.Apply(options).Options);
        foreach (var path in fixture.LinkPaths)
        {
            var link = fixture.Backend.Decode(File.ReadAllBytes(path));
            Assert.Equal(fixture.Executable, link.TargetPath);
            Assert.Equal(Path.GetDirectoryName(fixture.Executable), link.WorkingDirectory);
            Assert.Equal(fixture.Executable, link.IconPath);
            Assert.True(link.Description.StartsWith("SIDETALK.UserLaunch.v1:", StringComparison.Ordinal));
        }
        var manifest = File.ReadAllBytes(fixture.Manifest);
        fixture.Integration.Apply(options);
        Assert.SequenceEqual(manifest, File.ReadAllBytes(fixture.Manifest));
        fixture.Integration.Apply(new());
        foreach (var path in fixture.LinkPaths) Assert.False(File.Exists(path));
        Assert.Equal("foreign shortcut bytes", File.ReadAllText(unrelated));
        Assert.SequenceEqual(sentinel, File.ReadAllBytes(data));
        Assert.Equal(new UserLaunchOptions(), fixture.Integration.Inspect().Options);
        fixture.AssertNoTransaction();
    }

    private static void IndependentStartup()
    {
        var fixture = new Fixture();
        fixture.Integration.Apply(new(true, true, false));
        Assert.False(File.Exists(fixture.StartupLink));
        var desktop = File.ReadAllBytes(fixture.DesktopLink);
        var menu = File.ReadAllBytes(fixture.MenuLink);
        Assert.Equal(UserLaunchIntegration.LaunchArguments, fixture.Backend.Decode(desktop).Arguments);
        fixture.Integration.Apply(new(true, true, true));
        Assert.Equal(UserLaunchIntegration.StartupArguments, fixture.Backend.Decode(File.ReadAllBytes(fixture.StartupLink)).Arguments);
        fixture.Integration.Apply(new(true, true, false));
        Assert.False(File.Exists(fixture.StartupLink));
        Assert.SequenceEqual(desktop, File.ReadAllBytes(fixture.DesktopLink));
        Assert.SequenceEqual(menu, File.ReadAllBytes(fixture.MenuLink));
        fixture.AssertNoTransaction();
    }

    private static void MovedInstallation()
    {
        var fixture = new Fixture();
        var options = new UserLaunchOptions(true, true, true);
        fixture.Integration.Apply(options);
        var description = fixture.Backend.Decode(File.ReadAllBytes(fixture.DesktopLink)).Description;
        var originalImage = File.ReadAllBytes(fixture.Executable);
        var relocated = fixture.CreateExecutable("업데이트 설치 경로 with spaces % literal");
        var moved = new UserLaunchIntegration(fixture.Locations, relocated, fixture.Backend);
        Assert.True(moved.Inspect().NeedsUpdate);
        Assert.False(moved.Apply(options).NeedsUpdate);
        foreach (var path in fixture.LinkPaths)
            Assert.Equal(relocated, fixture.Backend.Decode(File.ReadAllBytes(path)).TargetPath);
        Assert.Equal(description, fixture.Backend.Decode(File.ReadAllBytes(fixture.DesktopLink)).Description);
        Assert.SequenceEqual(originalImage, File.ReadAllBytes(fixture.Executable));
        File.Delete(fixture.Executable); // Only this test's disposable synthetic image.
        Assert.False(moved.Inspect().NeedsUpdate);
        fixture.AssertNoTransaction();
    }

    private static void UnownedCollision()
    {
        var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Locations.Desktop);
        var foreign = Encoding.UTF8.GetBytes("user-created shortcut at the requested name");
        File.WriteAllBytes(fixture.DesktopLink, foreign);
        Assert.Throws<InvalidDataException>(() => fixture.Integration.Apply(new(true, false, false)));
        Assert.SequenceEqual(foreign, File.ReadAllBytes(fixture.DesktopLink));
        Assert.False(File.Exists(fixture.Manifest));
        // Disabling an unowned path is a no-op, never a request to remove it.
        fixture.Integration.Apply(new(false, true, false));
        fixture.Integration.Apply(new());
        Assert.SequenceEqual(foreign, File.ReadAllBytes(fixture.DesktopLink));
        Assert.False(File.Exists(fixture.MenuLink));
        fixture.AssertNoTransaction();
    }

    private static void EditedLinks()
    {
        var fixture = new Fixture();
        fixture.Integration.Apply(new(true, false, false));
        var original = File.ReadAllBytes(fixture.DesktopLink);
        var manifest = File.ReadAllBytes(fixture.Manifest);
        var link = fixture.Backend.Decode(original);
        var edits = new[]
        {
            link with { TargetPath = Path.Combine(fixture.Root, "Start.exe") },
            link with { Arguments = "/box:foreign arbitrary-command" },
            link with { WorkingDirectory = fixture.Root },
            link with { Description = "user replacement" },
            link with { IconPath = Path.Combine(fixture.Root, "user.ico") },
            link with { ShowCommand = 3 }
        };
        foreach (var edited in edits)
        {
            var bytes = fixture.Backend.Encode(edited);
            File.WriteAllBytes(fixture.DesktopLink, bytes);
            Assert.Throws<InvalidDataException>(() => fixture.Integration.Inspect());
            Assert.Throws<InvalidDataException>(() => fixture.Integration.Apply(new()));
            Assert.SequenceEqual(bytes, File.ReadAllBytes(fixture.DesktopLink));
            Assert.SequenceEqual(manifest, File.ReadAllBytes(fixture.Manifest));
        }
        File.WriteAllBytes(fixture.DesktopLink, original);
        Assert.False(fixture.Integration.Inspect().NeedsUpdate);
        fixture.AssertNoTransaction();
    }

    private static void CorruptManifest()
    {
        var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Locations.SettingsRoot);
        var corrupt = Encoding.UTF8.GetBytes("{interrupted settings");
        File.WriteAllBytes(fixture.Manifest, corrupt);
        Assert.Throws<InvalidDataException>(() => fixture.Integration.Inspect());
        Assert.Throws<InvalidDataException>(() => fixture.Integration.Apply(new(true, true, true)));
        Assert.SequenceEqual(corrupt, File.ReadAllBytes(fixture.Manifest));
        foreach (var path in fixture.LinkPaths) Assert.False(File.Exists(path));

        var valid = new Fixture();
        valid.Integration.Apply(new(true, false, false));
        var original = File.ReadAllBytes(valid.DesktopLink);
        var manifest = JsonNode.Parse(File.ReadAllBytes(valid.Manifest))!.AsObject();
        var unrelated = Path.Combine(valid.Root, "unrelated-user-data.bin");
        File.WriteAllText(unrelated, "preserve user data");
        manifest["Links"]![0]!["Path"] = unrelated;
        var redirected = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        File.WriteAllBytes(valid.Manifest, redirected);
        Assert.Throws<InvalidDataException>(() => valid.Integration.Apply(new()));
        Assert.SequenceEqual(redirected, File.ReadAllBytes(valid.Manifest));
        Assert.SequenceEqual(original, File.ReadAllBytes(valid.DesktopLink));
        Assert.Equal("preserve user data", File.ReadAllText(unrelated));
    }

    private static void ManifestRace()
    {
        var fixture = new Fixture();
        var raced = Encoding.UTF8.GetBytes("foreign manifest appeared after preflight");
        fixture.Backend.BeforeEncode = _ =>
        {
            fixture.Backend.BeforeEncode = null;
            File.WriteAllBytes(fixture.Manifest, raced);
        };
        var error = Assert.Throws<IOException>(() => fixture.Integration.Apply(new(true, true, true)));
        Assert.Contains("이전 상태로 복원", error.Message);
        foreach (var path in fixture.LinkPaths) Assert.False(File.Exists(path));
        Assert.SequenceEqual(raced, File.ReadAllBytes(fixture.Manifest));
        fixture.AssertNoTransaction();
    }

    private static void VerificationRollback()
    {
        var fixture = new Fixture();
        var options = new UserLaunchOptions(true, true, true);
        fixture.Integration.Apply(options);
        var original = fixture.SnapshotManaged();
        var moved = new UserLaunchIntegration(fixture.Locations, fixture.CreateExecutable("updated"), fixture.Backend);
        fixture.Backend.AfterDecode = _ =>
        {
            if (!File.Exists(fixture.Pending)) return;
            fixture.Backend.AfterDecode = null;
            throw new IOException("synthetic final verification failure");
        };
        var error = Assert.Throws<IOException>(() => moved.Apply(options));
        Assert.Contains("이전 상태로 복원", error.Message);
        Assert.Contains("synthetic final verification failure", error.ToString());
        fixture.AssertSnapshot(original);
        Assert.True(moved.Inspect().NeedsUpdate);
        fixture.AssertNoTransaction();
    }

    private static void FailedRollback()
    {
        var fixture = new Fixture();
        var options = new UserLaunchOptions(true, true, true);
        fixture.Integration.Apply(options);
        var originalDesktop = File.ReadAllBytes(fixture.DesktopLink);
        var moved = new UserLaunchIntegration(fixture.Locations, fixture.CreateExecutable("updated"), fixture.Backend);
        FileStream? heldBackup = null;
        string? backup = null;
        fixture.Backend.AfterDecode = _ =>
        {
            if (!File.Exists(fixture.Pending)) return;
            fixture.Backend.AfterDecode = null;
            backup = Directory.GetFiles(fixture.Locations.Desktop, ".sidetalk-transaction-*.previous").Single();
            heldBackup = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.None);
            throw new IOException("synthetic final verification failure before rollback");
        };
        IOException error;
        try { error = Assert.Throws<IOException>(() => moved.Apply(options)); }
        finally { heldBackup?.Dispose(); }
        Assert.Contains("변경과 복원에 실패", error.Message);
        Assert.Contains("synthetic final verification failure before rollback", error.ToString());
        Assert.True(error.InnerException is AggregateException { InnerExceptions.Count: >= 2 });
        Assert.True(File.Exists(fixture.Pending));
        Assert.True(Directory.EnumerateDirectories(fixture.Locations.SettingsRoot, "transaction-*").Any());
        Assert.SequenceEqual(originalDesktop, File.ReadAllBytes(backup!));
        Assert.Throws<InvalidDataException>(() => moved.Inspect());
        Assert.Throws<InvalidDataException>(() => moved.Apply(new()));
        Assert.SequenceEqual(originalDesktop, File.ReadAllBytes(backup!));
    }

    private static void ConcurrentSettings()
    {
        var fixture = new Fixture();
        var second = new UserLaunchIntegration(fixture.Locations, fixture.Executable, fixture.Backend);
        var excluded = 0;
        fixture.Backend.AfterDecode = _ =>
        {
            if (!File.Exists(fixture.Pending)) return;
            fixture.Backend.AfterDecode = null;
            Assert.Throws<IOException>(() => second.Apply(new()));
            Assert.Throws<InvalidDataException>(() => second.Inspect());
            excluded++;
        };
        var options = new UserLaunchOptions(true, true, true);
        Assert.Equal(options, fixture.Integration.Apply(options).Options);
        Assert.Equal(1, excluded);
        Assert.Equal(options, second.Inspect().Options);
        fixture.AssertNoTransaction();
    }

    private static void ExecutableValidation()
    {
        var fixture = new Fixture();
        foreach (var path in new[] { "Sidetalk.exe", "\"" + fixture.Executable + "\"", @"\\server\share\Sidetalk.exe",
                     Path.Combine(fixture.Root, "Start.exe"), fixture.Executable + "\r\n--arbitrary" })
            Assert.Throws<InvalidDataException>(() => new UserLaunchIntegration(fixture.Locations, path, fixture.Backend));
        Assert.Throws<FileNotFoundException>(() => new UserLaunchIntegration(fixture.Locations,
            Path.Combine(fixture.Root, "missing", "Sidetalk.exe"), fixture.Backend));
        fixture.Integration.Apply(new(true, false, false));
        var link = fixture.Backend.Decode(File.ReadAllBytes(fixture.DesktopLink));
        Assert.Equal(Path.GetFullPath(fixture.Executable), link.TargetPath);
        Assert.False(link.TargetPath.Contains('"'));
        Assert.Equal("--launch-secondary", link.Arguments);
    }

    private static void MissingOwnedLink()
    {
        var fixture = new Fixture();
        var options = new UserLaunchOptions(true, false, true);
        fixture.Integration.Apply(options);
        File.Delete(fixture.StartupLink);
        var result = fixture.Integration.Inspect();
        Assert.True(result.Options.LoginStartup);
        Assert.True(result.NeedsUpdate);
        Assert.False(File.Exists(fixture.StartupLink), "Read-only inspection must not recreate a removed startup entry.");
        Assert.False(fixture.Integration.Apply(options).NeedsUpdate);
        Assert.True(File.Exists(fixture.StartupLink));
        fixture.AssertNoTransaction();
    }

    private static void NativeComRoundtrip()
    {
        // Actual COM and temporary files, never actual desktop, Programs or Startup folders.
        var fixture = new Fixture();
        var backend = new WindowsUserLaunchLinkBackend();
        var integration = new UserLaunchIntegration(fixture.Locations, fixture.Executable, backend);
        integration.Apply(new(true, true, true));
        foreach (var path in fixture.LinkPaths)
        {
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(0x4c, BitConverter.ToInt32(bytes)); // SHELL_LINK_HEADER size.
            var link = backend.Decode(bytes);
            Assert.Equal(fixture.Executable, link.TargetPath);
            Assert.Equal(Path.GetDirectoryName(fixture.Executable), link.WorkingDirectory);
            Assert.Equal(fixture.Executable, link.IconPath);
            Assert.Equal(path == fixture.StartupLink ? UserLaunchIntegration.StartupArguments : UserLaunchIntegration.LaunchArguments, link.Arguments);
            Assert.True(link.Description.StartsWith("SIDETALK.UserLaunch.v1:", StringComparison.Ordinal));
        }
        Assert.False(integration.Inspect().NeedsUpdate);
        integration.Apply(new());
        foreach (var path in fixture.LinkPaths) Assert.False(File.Exists(path));
        fixture.AssertNoTransaction();
    }

    private sealed class Fixture
    {
        public string Root { get; } = TestContext.CreateRunDirectory("synthetic user launch 통합 with spaces");
        public JsonBackend Backend { get; } = new();
        public string Executable { get; }
        public UserLaunchLocations Locations { get; }
        public UserLaunchIntegration Integration { get; }
        public string Manifest => Path.Combine(Locations.SettingsRoot, "manifest.json");
        public string Pending => Path.Combine(Locations.SettingsRoot, "pending.json");
        public string DesktopLink => Path.Combine(Locations.Desktop, UserLaunchIntegration.LinkFileName);
        public string MenuLink => Path.Combine(Locations.StartMenu, UserLaunchIntegration.LinkFileName);
        public string StartupLink => Path.Combine(Locations.Startup, UserLaunchIntegration.LinkFileName);
        public string[] LinkFolders => [Locations.Desktop, Locations.StartMenu, Locations.Startup];
        public string[] LinkPaths => [DesktopLink, MenuLink, StartupLink];

        public Fixture()
        {
            Locations = new(Path.Combine(Root, "desktop"), Path.Combine(Root, "programs"),
                Path.Combine(Root, "startup"), Path.Combine(Root, "settings"));
            Executable = CreateExecutable("설치 위치 with spaces");
            Integration = new(Locations, Executable, Backend);
        }

        public string CreateExecutable(string folder)
        {
            var directory = Path.Combine(Root, folder);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "Sidetalk.exe");
            File.WriteAllText(path, "synthetic executable identity only — never executed");
            return path;
        }

        public Dictionary<string, byte[]> SnapshotManaged() => LinkPaths.Append(Manifest)
            .Where(File.Exists).ToDictionary(path => path, File.ReadAllBytes);
        public void AssertSnapshot(Dictionary<string, byte[]> expected)
        {
            foreach (var (path, bytes) in expected) Assert.SequenceEqual(bytes, File.ReadAllBytes(path));
        }

        public void AssertNoTransaction()
        {
            Assert.False(File.Exists(Pending));
            if (Directory.Exists(Locations.SettingsRoot))
                Assert.False(Directory.EnumerateDirectories(Locations.SettingsRoot, "transaction-*").Any());
            foreach (var folder in LinkFolders.Append(Locations.SettingsRoot))
                if (Directory.Exists(folder)) Assert.False(Directory.EnumerateFiles(folder, ".sidetalk-transaction-*").Any());
        }
    }

    private sealed class JsonBackend : IUserLaunchLinkBackend
    {
        public int EncodeCount { get; private set; }
        public int DecodeCount { get; private set; }
        public Action<UserLaunchLink>? BeforeEncode { get; set; }
        public Action<UserLaunchLink>? AfterDecode { get; set; }
        public byte[] Encode(UserLaunchLink link)
        {
            EncodeCount++;
            BeforeEncode?.Invoke(link);
            return JsonSerializer.SerializeToUtf8Bytes(link);
        }
        public UserLaunchLink Decode(byte[] bytes)
        {
            DecodeCount++;
            var link = JsonSerializer.Deserialize<UserLaunchLink>(bytes) ?? throw new InvalidDataException("Synthetic link empty.");
            AfterDecode?.Invoke(link);
            return link;
        }
    }
}
