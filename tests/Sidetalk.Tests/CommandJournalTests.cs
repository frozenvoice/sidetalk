using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        ("Windows API (read-only): process identity, reuse and token context", ProcessRecoveryChecksIdentity),
        ("Command journal reads schema 1 without fabricated recovery metadata", LegacyRecordHasNoFabricatedMetadata),
        ("Command journal rejects recovery metadata changes between phases", ChangedRecoveryMetadataFailsClosed),
        ("Unidentified command candidate scope excludes only reliable unrelated identity", UnidentifiedCandidateScope),
        ("Command recovery: previous boot evidence still requires fresh box query", PreviousBootRequiresObservation),
        ("Windows API (read-only): native uptime and machine-bound DPAPI witness", WindowsBootEvidenceChecks),
        ("Windows API (read-only): absent PID has unknown exit code", MissingPidHasUnknownCode),
        ("Windows API: harmless exited child retains exact identity and exit code", ExitedChildRetainsIdentity),
        ("Windows API (read-only): inaccessible system PID never proves command exit", SystemPidDoesNotFakeExit),
        ("Windows API: harmless Start-named child is scoped by image and intent", NativeUnidentifiedImageScope),
        ("Command journal preserves stage and cause for failure and cancellation", FailureCauseRemainsPending),
        ("Command journal failure write failure cannot replace original cause", FailureWriteDoesNotMaskOriginal),
        ("Command journal preserves failed action cause after verified command completion", CompletedActionFailurePreservesCompletion)
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
            Assert.Equal(2, prepared.Schema);
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
        Assert.Equal(CommandRecoveryState.SameProcessAlive, recovered.RecoveryState);
        Assert.Equal(captured, recovered.Identity);
        Assert.Throws<InvalidOperationException>(() => _ = recovered.ExitCode);

        var reused = captured with { CreationTimeUtcTicks = captured.CreationTimeUtcTicks + 1 };
        using var missing = new ProcessCommandRecovery().Recover(reused);
        Assert.True(missing.HasExited);
        Assert.Equal(CommandRecoveryState.MissingOrReused, missing.RecoveryState);
        Assert.Throws<InvalidOperationException>(() => _ = missing.ExitCode);

        var wrongImage = captured with { ImagePath = Path.Combine(Path.GetDirectoryName(expected)!, "SIDETALK-other.exe") };
        Assert.Throws<InvalidDataException>(() => new ProcessCommandRecovery().Recover(wrongImage));

        var token = WindowsCommandEvidence.ReadTokenContext(process.SafeHandle);
        Assert.Equal(ProfileStore.CurrentSid(), token.UserSid);
        Assert.Equal(process.SessionId, token.SessionId);
    }

    private static void LegacyRecordHasNoFabricatedMetadata()
    {
        var root = TestContext.CreateRunDirectory("journal-schema-one");
        var store = new ProfileStore(root);
        var profile = store.Create();
        var startExe = Path.Combine(root, "Sandboxie", "Start.exe");
        var journal = new CommandJournal(root);
        using var lease = journal.AcquireLease();
        var prepared = journal.Prepare(profile, SidetalkCommandAction.Launch, startExe);
        var path = Path.Combine(journal.JournalDirectory, prepared.OperationId + ".intent.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["Schema"] = 1;
        node.Remove("RecoveryContext");
        File.WriteAllText(path, node.ToJsonString());
        var oldBytes = File.ReadAllBytes(path);
        var legacy = journal.ReadPending(profile, startExe)!;
        Assert.Equal(1, legacy.Schema);
        Assert.True(legacy.RecoveryContext is null);
        journal.Complete(legacy);
        Assert.SequenceEqual(oldBytes, File.ReadAllBytes(path));
        var completed = JsonNode.Parse(File.ReadAllText(Path.Combine(journal.JournalDirectory, prepared.OperationId + ".completed.json")))!;
        Assert.Equal(1, completed["Schema"]!.GetValue<int>());
        Assert.True(completed["RecoveryContext"] is null);
    }

    private static void ChangedRecoveryMetadataFailsClosed()
    {
        var root = TestContext.CreateRunDirectory("journal-context-race");
        var store = new ProfileStore(root);
        var profile = store.Create();
        var startExe = Path.Combine(root, "Sandboxie", "Start.exe");
        var journal = new CommandJournal(root);
        using var lease = journal.AcquireLease();
        var prepared = journal.Prepare(profile, SidetalkCommandAction.Launch, startExe);
        journal.RecordStarted(prepared, FakeIdentity(startExe));
        var path = Path.Combine(journal.JournalDirectory, prepared.OperationId + ".started.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["RecoveryContext"] = JsonSerializer.SerializeToNode(new CommandRecoveryContext(profile.UserSid, int.MaxValue, null));
        File.WriteAllText(path, node.ToJsonString());
        Assert.Throws<InvalidDataException>(() => journal.ReadPending(profile, startExe));
        Assert.True(File.Exists(path));
    }

    private static void UnidentifiedCandidateScope()
    {
        var expected = Path.Combine(TestContext.CreateRunDirectory("command-candidate"), "Sandboxie", "Start.exe");
        var other = Path.Combine(Path.GetDirectoryName(expected)!, "unrelated", "Start.exe");
        var context = new CommandRecoveryContext("S-1-5-21-1-2-3-1001", 3, null);
        Assert.True(ProcessCommandRecovery.CandidateIsUnrelated(expected, context, other, null, null));
        Assert.True(ProcessCommandRecovery.CandidateIsUnrelated(expected, context, expected, "S-1-5-21-1-2-3-1002", 3));
        Assert.True(ProcessCommandRecovery.CandidateIsUnrelated(expected, context, expected, context.UserSid, 4));
        Assert.False(ProcessCommandRecovery.CandidateIsUnrelated(expected, context, expected, context.UserSid, 3));
        Assert.False(ProcessCommandRecovery.CandidateIsUnrelated(expected, context, expected, null, 4));
        Assert.False(ProcessCommandRecovery.CandidateIsUnrelated(expected, null, expected, context.UserSid, 4));
    }

    private static void PreviousBootRequiresObservation()
    {
        // Fake external boot-proof boundary, not an actual reboot integration test.
        var root = TestContext.CreateRunDirectory("command-previous-boot");
        var profile = new ProfileStore(root).Create();
        var pending = new PendingCommandRecord(Guid.NewGuid().ToString("N"), SidetalkCommandAction.Stop, profile,
            Path.Combine(root, "Sandboxie", "Start.exe"), DateTimeOffset.UtcNow, new(4, DateTime.UtcNow.Ticks, Path.Combine(root, "Sandboxie", "Start.exe")));
        using var lifecycle = new CommandLifecycle();
        var handle = new ProcessCommandRecovery((_, _) => true).Recover(pending);
        Assert.Equal(CommandRecoveryState.PreviousBoot, handle.RecoveryState);
        Assert.True(handle.HasExited);
        Assert.Throws<InvalidOperationException>(() => _ = handle.ExitCode);
        lifecycle.Begin(pending.Action, handle);
        Assert.True(lifecycle.Observe(null).IsPending);
        var queryCount = 0;
        var result = lifecycle.InspectProcesses(() => { queryCount++; return Array.Empty<BoxProcess>(); });
        Assert.Equal(1, queryCount);
        Assert.False(result.State.IsPending);
        Assert.True(result.State.ExitCode is null);
        Assert.Contains("종료 코드는 확인할 수 없", result.State.Message);
    }

    private static void WindowsBootEvidenceChecks()
    {
        var operation = Guid.NewGuid().ToString("N");
        var before = WindowsCommandEvidence.ReadUptimeMilliseconds();
        var context = WindowsCommandEvidence.Capture(operation);
        Assert.True(context.Boot is not null);
        Assert.True(context.Boot!.UptimeMilliseconds >= before);
        Assert.Equal(ProfileStore.CurrentSid(), context.UserSid);
        Assert.True(WindowsCommandEvidence.AuthenticatesBootEvidence(operation, context));
        Assert.False(WindowsCommandEvidence.ProvesPreviousBoot(operation, context));
        Assert.False(WindowsCommandEvidence.ProvesPreviousBoot(Guid.NewGuid().ToString("N"), context));
        // Merely editing an uptime into the future must not create reboot evidence.
        var tampered = context with { Boot = context.Boot with { UptimeMilliseconds = ulong.MaxValue } };
        Assert.False(WindowsCommandEvidence.AuthenticatesBootEvidence(operation, tampered));
        Assert.False(WindowsCommandEvidence.ProvesPreviousBoot(operation, tampered));
        Assert.False(WindowsCommandEvidence.ProvesPreviousBoot(operation, context with { Boot = context.Boot with { ProtectedWitness = "not base64" } }));
        Assert.False(WindowsCommandEvidence.ProvesPreviousBoot(operation, null));
    }

    private static void MissingPidHasUnknownCode()
    {
        var identity = new CommandProcessIdentity(int.MaxValue, DateTime.UtcNow.Ticks, Environment.ProcessPath!);
        using var handle = new ProcessCommandRecovery().Recover(identity);
        Assert.Equal(CommandRecoveryState.MissingOrReused, handle.RecoveryState);
        Assert.True(handle.HasExited);
        Assert.Throws<InvalidOperationException>(() => _ = handle.ExitCode);
    }

    private static void ExitedChildRetainsIdentity()
    {
        var image = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        using var child = Process.Start(new ProcessStartInfo(image, "/d /c exit 17")
        { UseShellExecute = false, CreateNoWindow = true })!;
        // Retain the original handle: Windows cannot reuse its PID during this check.
        var identity = ProcessCommandRecovery.Capture(child, image);
        Assert.True(child.WaitForExit(5000));
        using var recovered = new ProcessCommandRecovery().Recover(identity);
        Assert.True(recovered.HasExited);
        Assert.Equal(CommandRecoveryState.SameProcessExited, recovered.RecoveryState);
        Assert.Equal(17, recovered.ExitCode);
    }

    private static void SystemPidDoesNotFakeExit()
    {
        // Nonprivileged hosts usually deny PID 4. Hosts granting this query may prove
        // a creation mismatch instead; do not weaken access rights to force a denial.
        using var recovered = new ProcessCommandRecovery().Recover(new CommandProcessIdentity(4, 1, Environment.ProcessPath!));
        Console.WriteLine($"INFO Windows system PID probe: {recovered.RecoveryState}; no termination requested.");
        if (recovered.RecoveryState == CommandRecoveryState.IdentityUnavailable)
        {
            Assert.Throws<InvalidOperationException>(() => _ = recovered.HasExited);
            Assert.Throws<InvalidOperationException>(() => _ = recovered.ExitCode);
        }
        else
            Assert.Equal(CommandRecoveryState.MissingOrReused, recovered.RecoveryState);
    }

    private static void NativeUnidentifiedImageScope()
    {
        var root = TestContext.CreateRunDirectory("command-native-start-scope");
        var image = Path.Combine(root, "Start.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), image);
        using var child = Process.Start(new ProcessStartInfo(image, "/d /c set /p sidetalk_test_input=")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true })!;
        try
        {
            Assert.False(child.HasExited);
            var recovery = new ProcessCommandRecovery();
            using var unrelated = recovery.RecoverUnidentified(Path.Combine(root, "Sandboxie", "Start.exe"));
            Assert.True(unrelated.HasExited);
            Assert.Throws<InvalidOperationException>(() => _ = unrelated.ExitCode);
            var profile = new ProfileStore(Path.Combine(root, "profile-root")).Create();
            var operationId = Guid.NewGuid().ToString("N");
            var record = new PendingCommandRecord(operationId, SidetalkCommandAction.Launch, profile, image, DateTimeOffset.UtcNow, null)
            { Schema = 2, RecoveryContext = WindowsCommandEvidence.Capture(operationId) };
            using var related = recovery.Recover(record);
            Assert.Equal(CommandRecoveryState.IntentWithoutPid, related.RecoveryState);
            Assert.Throws<InvalidOperationException>(() => _ = related.HasExited);
            var changedContext = record.RecoveryContext! with { SessionId = record.RecoveryContext!.SessionId + 1 };
            using var untrustedScope = recovery.Recover(record with { RecoveryContext = changedContext });
            Assert.Throws<InvalidOperationException>(() => _ = untrustedScope.HasExited);
        }
        finally
        {
            child.StandardInput.WriteLine("done");
            child.StandardInput.Close();
            Assert.True(child.WaitForExit(5000));
        }
        using var finished = new ProcessCommandRecovery().RecoverUnidentified(image);
        Assert.True(finished.HasExited);
        Assert.Throws<InvalidOperationException>(() => _ = finished.ExitCode);
    }

    private static void FailureCauseRemainsPending()
    {
        foreach (var started in new[] { false, true })
        foreach (var error in new Exception[] { new InvalidOperationException("original cause"), new OperationCanceledException("cancel cause") })
        {
            var root = TestContext.CreateRunDirectory("journal-failed-cause");
            var store = new ProfileStore(root);
            var profile = store.Create();
            var startExe = Path.Combine(root, "Sandboxie", "Start.exe");
            var journal = new CommandJournal(root);
            using var lease = journal.AcquireLease();
            var record = journal.Prepare(profile, SidetalkCommandAction.Launch, startExe);
            if (started) journal.RecordStarted(record, FakeIdentity(startExe));
            Assert.True(journal.TryRecordFailure(record, "command creation", error) is null);
            var path = Path.Combine(journal.JournalDirectory, record.OperationId + ".failed.json");
            var firstBytes = File.ReadAllBytes(path);
            var pending = journal.ReadPending(profile, startExe)!;
            Assert.False(journal.IsCompleted(record));
            Assert.Equal("command creation", pending.Failure!.Stage);
            Assert.Equal(error.Message, pending.Failure.Message);
            Assert.Equal(error is OperationCanceledException, pending.Failure.IsCancellation);
            Assert.True(journal.TryRecordFailure(record, "later failure", new IOException("secondary cause")) is null);
            Assert.SequenceEqual(firstBytes, File.ReadAllBytes(path));
            Assert.Throws<InvalidDataException>(() => journal.RecordStarted(record, FakeIdentity(startExe)));
            // This call models the controller after separate exit and fresh box proof.
            journal.Complete(pending);
            Assert.True(journal.ReadPending(profile, startExe) is null);
            Assert.SequenceEqual(firstBytes, File.ReadAllBytes(path));
        }
    }

    private static void FailureWriteDoesNotMaskOriginal()
    {
        var root = TestContext.CreateRunDirectory("journal-failure-write");
        var store = new ProfileStore(root);
        var profile = store.Create();
        var startExe = Path.Combine(root, "Sandboxie", "Start.exe");
        var journal = new CommandJournal(root);
        using var lease = journal.AcquireLease();
        var record = journal.Prepare(profile, SidetalkCommandAction.Launch, startExe);
        var original = new InvalidOperationException("the real command failure");
        // A no-overwrite publication conflict after bytes were flushed simulates a
        // failed failure-record publication. The immutable intent remains readable.
        Directory.CreateDirectory(Path.Combine(journal.JournalDirectory, record.OperationId + ".failed.json"));
        var secondary = journal.TryRecordFailure(record, "identity publication", original);
        Assert.True(secondary is IOException);
        Assert.Equal("the real command failure", original.Message);
        Assert.False(journal.IsCompleted(record));
        Assert.True(journal.ReadPending(profile, startExe) is not null);
        var preserved = Directory.GetFiles(journal.JournalDirectory, "." + record.OperationId + ".failed.*.tmp");
        Assert.Equal(1, preserved.Length);
        Assert.Contains(original.Message, File.ReadAllText(preserved[0]));
    }

    private static void CompletedActionFailurePreservesCompletion()
    {
        var root = TestContext.CreateRunDirectory("journal-completed-action-failure");
        var store = new ProfileStore(root);
        var profile = store.Create();
        var startExe = Path.Combine(root, "Sandboxie", "Start.exe");
        var journal = new CommandJournal(root);
        using var lease = journal.AcquireLease();
        var record = journal.Prepare(profile, SidetalkCommandAction.Launch, startExe);
        var identity = FakeIdentity(startExe);
        journal.RecordStarted(record, identity);
        using var lifecycle = new CommandLifecycle();
        lifecycle.Begin(record.Action, new FinishedFailureHandle(identity));
        var result = lifecycle.InspectProcesses(() => Array.Empty<BoxProcess>());
        Assert.Equal(CommandLifecycleOutcome.LaunchFailed, result.State.Outcome);
        Assert.Equal(17, result.State.ExitCode);
        Assert.False(result.State.IsPending);
        journal.Complete(record);
        var finishedPath = Path.Combine(journal.JournalDirectory, record.OperationId + ".completed.json");
        var finishedBytes = File.ReadAllBytes(finishedPath);
        Assert.True(journal.TryRecordFailure(record, "box observation", new InvalidOperationException(result.State.Message)) is null);
        Assert.True(journal.IsCompleted(record));
        Assert.True(journal.ReadPending(profile, startExe) is null);
        Assert.SequenceEqual(finishedBytes, File.ReadAllBytes(finishedPath));
        var failed = JsonNode.Parse(File.ReadAllText(Path.Combine(journal.JournalDirectory, record.OperationId + ".failed.json")))!;
        Assert.Equal(result.State.Message, failed["Failure"]!["Message"]!.GetValue<string>());
    }

    private sealed class FinishedFailureHandle(CommandProcessIdentity identity) : ISidetalkCommandHandle
    {
        public CommandProcessIdentity? Identity => identity;
        public bool HasExited => true;
        public int ExitCode => 17;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }

    private static CommandProcessIdentity FakeIdentity(string startExe) =>
        new(Environment.ProcessId, DateTime.UtcNow.Ticks, Path.GetFullPath(startExe));
}
