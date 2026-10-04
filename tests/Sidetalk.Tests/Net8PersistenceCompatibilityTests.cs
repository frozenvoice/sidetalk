using System.Text;
using System.Text.Json;
using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class Net8PersistenceCompatibilityTests
{
    private const string FixtureRoot = @"C:\__SIDETALK_FIXTURE_ROOT__";
    private const string FixtureSid = "__SIDETALK_FIXTURE_SID__";

    public static IEnumerable<(string Name, Action Run)> Cases =>
    [
        (".NET 8 persisted profile keeps identity, paths and original bytes", ProfileKeepsIdentityPathsAndBytes),
        (".NET 8 pending command schemas keep duplicate protection and failure", PendingCommandsKeepProtection),
        (".NET 8 Completed command schemas remain completed across restart", CompletedCommandsRemainCompleted)
    ];

    // GetRawText retains the old runtime's JSON representation. Only the synthetic
    // absolute root and SID are rebound to this disposable test directory/user.
    internal static string FixtureRecord(string section, string? key = null, string? root = null)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(TestContext.WorkspaceRoot,
            "tests", "Sidetalk.Tests", "Fixtures", "net8-persistence.json")));
        Assert.Equal("8.0.424", document.RootElement.GetProperty("SdkVersion").GetString());
        Assert.Equal(".NET 8.0.30", document.RootElement.GetProperty("Runtime").GetString());
        var element = document.RootElement.GetProperty(section);
        if (key is not null) element = element.GetProperty(key);
        var text = element.GetRawText();
        if (root is null) return text;
        return text.Replace(JsonStringContent(FixtureRoot), JsonStringContent(root), StringComparison.Ordinal)
            .Replace(FixtureSid, ProfileStore.CurrentSid(), StringComparison.Ordinal);
    }

    private static string JsonStringContent(string value) => JsonSerializer.Serialize(value)[1..^1];

    private static (ProfileStore Store, BoxProfile Profile) ImportProfile(string name)
    {
        var root = TestContext.CreateRunDirectory(name);
        var store = new ProfileStore(root);
        File.WriteAllText(store.ProfilePath, FixtureRecord("Profile", root: root), new UTF8Encoding(false));
        return (store, store.Load()!);
    }

    private static void ProfileKeepsIdentityPathsAndBytes()
    {
        // Construction only: computing the production path performs no user-data IO.
        Assert.Equal(Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SIDETALK")),
            new ProfileStore().Root);
        var (store, profile) = ImportProfile("net8-profile-compatibility");
        var original = File.ReadAllBytes(store.ProfilePath);
        Assert.Equal(1, profile.Schema);
        Assert.Equal("SideTalk_aaaaaaaaaaaaaaaaaaaaaaa", profile.BoxName);
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", profile.OwnerToken);
        Assert.Equal(ProfileStore.CurrentSid(), profile.UserSid);
        Assert.Equal(Path.Combine(store.Root, "Boxes", profile.BoxName), profile.StoragePath);
        Directory.CreateDirectory(profile.StoragePath);
        var accountMarker = Path.Combine(profile.StoragePath, "synthetic-account.dat");
        File.WriteAllBytes(accountMarker, [0, 1, 2, 255]);
        var staged = Path.Combine(store.Root, ".profile.cccccccccccccccccccccccccccccccc.tmp");
        File.WriteAllText(staged, "{\"Schema\":1");
        Assert.Equal(profile, new ProfileStore(store.Root).Create());
        Assert.SequenceEqual(original, File.ReadAllBytes(store.ProfilePath));
        Assert.Equal("{\"Schema\":1", File.ReadAllText(staged));
        Assert.SequenceEqual(new byte[] { 0, 1, 2, 255 }, File.ReadAllBytes(accountMarker));
    }

    private static void PendingCommandsKeepProtection()
    {
        foreach (var schema in new[] { 1, 2 })
        {
            var (store, profile) = ImportProfile("net8-pending-command-" + schema);
            var journal = new CommandJournal(store.Root);
            Assert.Equal(Path.Combine(store.Root, "command-journal"), journal.JournalDirectory);
            Assert.Equal(Path.Combine(journal.JournalDirectory, "command.lock"), journal.LockPath);
            Directory.CreateDirectory(journal.JournalDirectory);
            var phases = schema == 1 ? new[] { "intent", "started" } : new[] { "intent", "started", "failed" };
            foreach (var phase in phases) ImportCommand(store.Root, journal.JournalDirectory, schema, phase);
            var before = Snapshot(journal.JournalDirectory);
            using (journal.AcquireLease())
            {
                var start = Path.Combine(store.Root, "Sandboxie", "Start.exe");
                var pending = journal.ReadPending(profile, start)!;
                Assert.Equal(schema, pending.Schema);
                Assert.Equal(1234, pending.Process!.ProcessId);
                Assert.Equal(new DateTimeOffset(2026, 9, 1, 2, 3, 4, TimeSpan.Zero).UtcTicks, pending.Process.CreationTimeUtcTicks);
                Assert.Equal(start, pending.Process.ImagePath);
                Assert.Equal(profile, pending.Profile);
                Assert.False(journal.IsCompleted(pending));
                if (schema == 1) Assert.True(pending.RecoveryContext is null && pending.Failure is null);
                else
                {
                    Assert.Equal(new CommandRecoveryContext(profile.UserSid, 1, null), pending.RecoveryContext);
                    Assert.Equal("process-query", pending.Failure!.Stage);
                    Assert.Equal("합성 조회 실패", pending.Failure.Message);
                }
                Assert.Throws<InvalidOperationException>(() => journal.Prepare(profile, SidetalkCommandAction.Launch, start));
                Assert.Throws<InvalidOperationException>(() => journal.Prepare(profile, SidetalkCommandAction.Stop, start));
                Assert.Throws<InvalidOperationException>(() => new CommandJournal(store.Root).AcquireLease());
            }
            AssertUnchanged(before);
            Assert.Equal(phases.Length, Directory.GetFiles(journal.JournalDirectory, "*.json").Length);
        }
    }

    private static void CompletedCommandsRemainCompleted()
    {
        foreach (var schema in new[] { 1, 2 })
        {
            var (store, profile) = ImportProfile("net8-completed-command-" + schema);
            var journal = new CommandJournal(store.Root);
            Directory.CreateDirectory(journal.JournalDirectory);
            foreach (var phase in new[] { "intent", "started" }) ImportCommand(store.Root, journal.JournalDirectory, schema, phase);
            PendingCommandRecord record;
            using (journal.AcquireLease())
            {
                record = journal.ReadPending(profile, Path.Combine(store.Root, "Sandboxie", "Start.exe"))!;
                ImportCommand(store.Root, journal.JournalDirectory, schema, "completed");
                if (schema == 2) ImportCommand(store.Root, journal.JournalDirectory, schema, "failed");
                Assert.True(journal.IsCompleted(record));
            }
            var before = Snapshot(journal.JournalDirectory);
            var restarted = new CommandJournal(store.Root);
            using (restarted.AcquireLease())
            {
                Assert.True(restarted.ReadPending(profile, record.StartExe) is null);
                Assert.True(restarted.IsCompleted(record));
                restarted.Complete(record);
            }
            AssertUnchanged(before);
            Assert.Equal(schema == 1 ? 3 : 4, Directory.GetFiles(journal.JournalDirectory, "*.json").Length);
        }
    }

    private static void ImportCommand(string root, string directory, int schema, string phase)
    {
        var text = FixtureRecord("Command", $"schema{schema}.{phase}", root);
        using var document = JsonDocument.Parse(text);
        var operation = document.RootElement.GetProperty("OperationId").GetString();
        File.WriteAllText(Path.Combine(directory, $"{operation}.{phase}.json"), text, new UTF8Encoding(false));
    }

    internal static Dictionary<string, byte[]> Snapshot(string directory) =>
        Directory.GetFiles(directory, "*.json").ToDictionary(path => path, File.ReadAllBytes);

    internal static void AssertUnchanged(Dictionary<string, byte[]> before)
    {
        foreach (var (path, bytes) in before) Assert.SequenceEqual(bytes, File.ReadAllBytes(path));
    }
}
