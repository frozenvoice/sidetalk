using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class CommandJournalTests
{
    public static IEnumerable<(string Name, Action Run)> Cases =>
    [
        ("Command journal roundtrips an unfinished and completed operation", JournalRoundTrips),
        ("Command journal keeps an intent without a PID uncertain", IntentWithoutPidRemainsUncertain),
        ("Command journal rejects corrupt profile metadata", CorruptProfileMetadataFailsClosed),
        ("Command journal rejects multiple pending operations", MultiplePendingOperationsFailClosed),
        ("Command journal serializes the operation lease", LeaseSerializesReadersAndWriters),
        ("Command journal completion preserves immutable history", CompletionPreservesHistory),
        ("Process recovery checks PID creation time and image path", ProcessRecoveryChecksIdentity)
    ];

    private static void JournalRoundTrips()
    {
        var root = TestContext.CreateRunDirectory("journal-roundtrip");
        var store = new ProfileStore(root);
        var profile = store.Create();
        var startExe = Path.Combine(root, "Sandboxie", "Start.exe");
        var identity = FakeIdentity(startExe);

        var journal = new CommandJournal(store.Root);
        PendingCommandRecord prepared;
        using (journal.AcquireLease())
        {
            prepared = journal.Prepare(profile, SidetalkCommandAction.Launch, startExe);
            Assert.True(prepared.Process is null);
            journal.RecordStarted(prepared, identity);

            var started = journal.ReadPending(profile, startExe);
            Assert.True(started is not null);
            Assert.Equal(identity, started!.Process);
            Assert.Equal(prepared.OperationId, started.OperationId);
            Assert.False(journal.IsCompleted(prepared));

            journal.Complete(prepared);
            Assert.True(journal.IsCompleted(prepared));
            Assert.True(journal.ReadPending(profile, startExe) is null);
        }

        var second = new CommandJournal(store.Root);
        using (second.AcquireLease())
        {
            Assert.True(second.ReadPending(profile, startExe) is null);
            Assert.True(second.IsCompleted(prepared));
        }

        Assert.Equal(3, Directory.GetFiles(second.JournalDirectory, "*.json").Length);
    }

    private static void IntentWithoutPidRemainsUncertain()
    {
        var root = TestContext.CreateRunDirectory("journal-intent-only");
        var store = new ProfileStore(root);
        var profile = store.Create();
        var startExe = Path.Combine(root, "Sandboxie", "Start.exe");
        var journal = new CommandJournal(store.Root);

        PendingCommandRecord prepared;
        using (journal.AcquireLease())
        {
            prepared = journal.Prepare(profile, SidetalkCommandAction.Stop, startExe);
            var pending = journal.ReadPending(profile, startExe);
            Assert.True(pending is not null);
            Assert.True(pending!.Process is null);
            Assert.False(journal.IsCompleted(prepared));

            // This is the only completion route for the unidentified-PID recovery branch.
            // It does not invent a process identity or an exit code.
            journal.Complete(prepared);
            Assert.True(journal.IsCompleted(prepared));
            Assert.True(journal.ReadPending(profile, startExe) is null);
        }

        var completionPath = Path.Combine(journal.JournalDirectory, $"{prepared.OperationId}.completed.json");
        var completion = File.ReadAllText(completionPath, Encoding.UTF8);
        Assert.Contains("\"Process\":null", completion);
    }

    private static void CorruptProfileMetadataFailsClosed()
    {
        var root = TestContext.CreateRunDirectory("journal-corrupt-profile");
        var store = new ProfileStore(root);
        var profile = store.Create();
        var startExe = Path.Combine(root, "Sandboxie", "Start.exe");
        var journal = new CommandJournal(store.Root);

        using (journal.AcquireLease())
        {
            var prepared = journal.Prepare(profile, SidetalkCommandAction.Launch, startExe);
            var intentPath = Path.Combine(journal.JournalDirectory, $"{prepared.OperationId}.intent.json");
            var text = File.ReadAllText(intentPath, Encoding.UTF8);
            var oldPath = JsonSerializer.Serialize(profile.StoragePath);
            var foreignPath = JsonSerializer.Serialize(Path.Combine(root, "foreign-box"));
            Assert.True(text.Contains(oldPath, StringComparison.Ordinal));
            File.WriteAllText(intentPath, text.Replace(oldPath, foreignPath, StringComparison.Ordinal), new UTF8Encoding(false));

            Assert.Throws<InvalidDataException>(() => journal.ReadPending(profile, startExe));
            Assert.True(File.Exists(intentPath), "Corrupt journal history must be preserved.");
        }
    }

    private static void MultiplePendingOperationsFailClosed()
    {
        var root = TestContext.CreateRunDirectory("journal-multiple-pending");
        var store = new ProfileStore(root);
        var profile = store.Create();
        var startExe = Path.Combine(root, "Sandboxie", "Start.exe");
        var journal = new CommandJournal(store.Root);

        using (journal.AcquireLease())
        {
            var first = journal.Prepare(profile, SidetalkCommandAction.Launch, startExe);
            var source = Path.Combine(journal.JournalDirectory, $"{first.OperationId}.intent.json");
            var secondId = Guid.NewGuid().ToString("N");
            var target = Path.Combine(journal.JournalDirectory, $"{secondId}.intent.json");
            var text = File.ReadAllText(source, Encoding.UTF8);
            text = text.Replace($"\"OperationId\":\"{first.OperationId}\"", $"\"OperationId\":\"{secondId}\"", StringComparison.Ordinal);
            if (text == File.ReadAllText(source, Encoding.UTF8))
                text = text.Replace($"\"OperationId\": \"{first.OperationId}\"", $"\"OperationId\": \"{secondId}\"", StringComparison.Ordinal);
            File.WriteAllText(target, text, new UTF8Encoding(false));

            Assert.Throws<InvalidDataException>(() => journal.ReadPending(profile, startExe));
            Assert.True(File.Exists(source));
            Assert.True(File.Exists(target));
        }
    }

    private static void LeaseSerializesReadersAndWriters()
    {
        var root = TestContext.CreateRunDirectory("journal-lease");
        var store = new ProfileStore(root);
        var profile = store.Create();
        var startExe = Path.Combine(root, "Sandboxie", "Start.exe");
        var first = new CommandJournal(store.Root);
        var second = new CommandJournal(store.Root);

        using (first.AcquireLease())
        {
            Assert.Throws<InvalidOperationException>(() => second.AcquireLease());
            Assert.Throws<InvalidOperationException>(() => { _ = second.TryAcquireInspectionLease(); });
        }

        using (var inspection = second.TryAcquireInspectionLease())
        {
            Assert.True(inspection is not null);
        }
        using (first.AcquireLease())
        {
            Assert.True(first.ReadPending(profile, startExe) is null);
        }

    }
    private static void CompletionPreservesHistory()
    {
        var root = TestContext.CreateRunDirectory("journal-completion-history");
        var store = new ProfileStore(root);
        var profile = store.Create();
        var startExe = Path.Combine(root, "Sandboxie", "Start.exe");
        var identity = FakeIdentity(startExe);
        var journal = new CommandJournal(store.Root);

        PendingCommandRecord prepared;
        string[] before;
        using (journal.AcquireLease())
        {
            prepared = journal.Prepare(profile, SidetalkCommandAction.Launch, startExe);
            journal.RecordStarted(prepared, identity);
            before = Directory.GetFiles(journal.JournalDirectory, "*.json").Select(Path.GetFileName).OrderBy(x => x).ToArray()!;
            journal.Complete(prepared);
            journal.Complete(prepared);
            Assert.Throws<InvalidDataException>(() => journal.RecordStarted(prepared, identity));
        }

        var after = Directory.GetFiles(journal.JournalDirectory, "*.json").Select(Path.GetFileName).OrderBy(x => x).ToArray()!;
        Assert.Equal(3, after.Length);
        Assert.SequenceEqual(before.Append($"{prepared.OperationId}.completed.json").OrderBy(x => x), after);
        Assert.True(File.Exists(Path.Combine(journal.JournalDirectory, $"{prepared.OperationId}.intent.json")));
        Assert.True(File.Exists(Path.Combine(journal.JournalDirectory, $"{prepared.OperationId}.started.json")));
        Assert.True(File.Exists(Path.Combine(journal.JournalDirectory, $"{prepared.OperationId}.completed.json")));
    }

    private static void ProcessRecoveryChecksIdentity()
    {
        var expected = Environment.ProcessPath!;
        using var process = Process.GetCurrentProcess();
        var captured = ProcessCommandRecovery.Capture(process, expected);
        Assert.Equal(process.Id, captured.ProcessId);
        Assert.True(captured.CreationTimeUtcTicks > DateTime.MinValue.Ticks);
        Assert.True(captured.ImagePath.Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase));

        using var recovered = new ProcessCommandRecovery().Recover(captured);
        Assert.False(recovered.HasExited);
        Assert.Equal(captured, recovered.Identity);
        Assert.Throws<InvalidOperationException>(() => _ = recovered.ExitCode);

        var reused = captured with { CreationTimeUtcTicks = captured.CreationTimeUtcTicks + 1 };
        using var missing = new ProcessCommandRecovery().Recover(reused);
        Assert.True(missing.HasExited);
        Assert.Throws<InvalidOperationException>(() => _ = missing.ExitCode);

        var wrongImage = captured with { ImagePath = Path.Combine(Path.GetDirectoryName(expected)!, "SIDETALK-other.exe") };
        Assert.Throws<InvalidDataException>(() => new ProcessCommandRecovery().Recover(wrongImage));
    }

    private static CommandProcessIdentity FakeIdentity(string startExe) =>
        new(Environment.ProcessId, DateTime.UtcNow.Ticks, Path.GetFullPath(startExe));
}
