using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class ResidualCleanupJournalTests
{
    private static readonly string Fingerprint = new('A', 64);
    private static readonly string OtherFingerprint = new('B', 64);

    public static IEnumerable<(string Name, Action Run)> Cases =>
    [
        ("Residual cleanup journal binds durable intent, restart and completion to approval", FingerprintRoundTrips),
        ("Residual cleanup journal rejects missing and malformed approval before intent", InvalidFingerprintWritesNoIntent),
        ("Residual cleanup journal rejects approval change before started", ChangedStartedFingerprintFails),
        ("Residual cleanup journal rejects approval change before completion", ChangedCompletedFingerprintFails),
        ("Residual cleanup journal rejects changed approval between saved phases", ChangedSavedFingerprintFails),
        ("Residual cleanup journal read-only inspection preserves incomplete intent bytes", ReadOnlyInspectionPreservesIntent),
        ("Residual cleanup journal retains failed intent and approval across restart", FailedIntentRemainsPending),
        ("Residual cleanup journal reads old Launch and Stop records without approval", LegacyActionsRemainCompatible),
        ("Residual cleanup lifecycle requires exit zero and fresh empty box", ZeroAndEmptySucceeds),
        ("Residual cleanup lifecycle refuses nonzero even with empty box", NonzeroAndEmptyFails),
        ("Residual cleanup lifecycle confirmed exit with unknown code is terminal failure", UnknownCodeAndEmptyFailsWithoutDeadlock),
        ("Residual cleanup lifecycle unknown exit remains pending despite empty sample", UnknownExitRemainsPending),
        ("Residual cleanup lifecycle query failure remains pending until fresh observation", MissingQueryRemainsPending),
        ("Residual cleanup lifecycle query exception preserves command", FailedQueryRemainsPending),
        ("Residual cleanup lifecycle exit zero with residual blocks success", ResidualBlocksSuccess),
        ("Residual cleanup lifecycle a running command refuses transitional enumeration", RunningRefusesEnumeration),
        ("Residual cleanup lifecycle preserves legacy Stop unknown-code behavior", LegacyStopUnchanged)
    ];

    private static void FingerprintRoundTrips()
    {
        using var fixture = new JournalFixture("roundtrip");
        PendingCommandRecord prepared;
        using (fixture.Journal.AcquireLease())
        {
            prepared = fixture.Journal.Prepare(fixture.Profile, SidetalkCommandAction.RecoveryBoxCleanup, fixture.StartExe, Fingerprint);
            Assert.Equal(Fingerprint, prepared.RecoveryObservationFingerprint);
            Assert.True(prepared.Process is null);
            Assert.Contains(Fingerprint, File.ReadAllText(fixture.PathFor(prepared, "intent")));
            fixture.Journal.RecordStarted(prepared, fixture.Identity);
        }
        var restarted = new CommandJournal(fixture.Root);
        using (restarted.AcquireLease())
        {
            var pending = restarted.ReadPending(fixture.Profile, fixture.StartExe)!;
            Assert.Equal(Fingerprint, pending.RecoveryObservationFingerprint);
            Assert.Equal(SidetalkCommandAction.RecoveryBoxCleanup, pending.Action);
            Assert.Equal(fixture.Identity, pending.Process);
            restarted.Complete(pending);
            Assert.True(restarted.ReadPending(fixture.Profile, fixture.StartExe) is null);
            Assert.Contains(Fingerprint, File.ReadAllText(fixture.PathFor(prepared, "completed")));
            Assert.True(restarted.IsCompleted(prepared));
        }
    }

    private static void InvalidFingerprintWritesNoIntent()
    {
        using var fixture = new JournalFixture("invalid");
        using (fixture.Journal.AcquireLease())
        {
            foreach (var value in new string?[] { null, "", new('A', 63), new('a', 64), new('G', 64), Fingerprint + "\n" })
                Assert.Throws<InvalidDataException>(() => fixture.Journal.Prepare(fixture.Profile,
                    SidetalkCommandAction.RecoveryBoxCleanup, fixture.StartExe, value));
            Assert.Throws<InvalidDataException>(() => fixture.Journal.Prepare(fixture.Profile,
                SidetalkCommandAction.Stop, fixture.StartExe, Fingerprint));
            Assert.Equal(0, Directory.GetFiles(fixture.Journal.JournalDirectory, "*.json").Length);
        }
    }

    private static void ChangedStartedFingerprintFails()
    {
        using var fixture = new JournalFixture("started-change");
        using (fixture.Journal.AcquireLease())
        {
            var prepared = fixture.Prepare();
            Assert.Throws<InvalidDataException>(() => fixture.Journal.RecordStarted(
                prepared with { RecoveryObservationFingerprint = OtherFingerprint }, fixture.Identity));
            Assert.Equal(Fingerprint, fixture.Journal.ReadPending(fixture.Profile, fixture.StartExe)!.RecoveryObservationFingerprint);
            Assert.False(File.Exists(fixture.PathFor(prepared, "started")));
        }
    }

    private static void ChangedCompletedFingerprintFails()
    {
        using var fixture = new JournalFixture("completion-change");
        using (fixture.Journal.AcquireLease())
        {
            var prepared = fixture.Prepare();
            fixture.Journal.RecordStarted(prepared, fixture.Identity);
            Assert.Throws<InvalidDataException>(() => fixture.Journal.Complete(prepared with { RecoveryObservationFingerprint = OtherFingerprint }));
            Assert.False(File.Exists(fixture.PathFor(prepared, "completed")));
            Assert.True(fixture.Journal.ReadPending(fixture.Profile, fixture.StartExe) is not null);
        }
    }

    private static void ChangedSavedFingerprintFails()
    {
        using var fixture = new JournalFixture("saved-change");
        using (fixture.Journal.AcquireLease())
        {
            var prepared = fixture.Prepare();
            fixture.Journal.RecordStarted(prepared, fixture.Identity);
            var path = fixture.PathFor(prepared, "started");
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            document["RecoveryObservationFingerprint"] = OtherFingerprint;
            File.WriteAllText(path, document.ToJsonString());
            Assert.Throws<InvalidDataException>(() => fixture.Journal.ReadPending(fixture.Profile, fixture.StartExe));
        }
    }

    private static void ReadOnlyInspectionPreservesIntent()
    {
        using var fixture = new JournalFixture("readonly");
        using (fixture.Journal.AcquireLease()) fixture.Prepare();
        var before = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).ToDictionary(path => path,
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);
        var restarted = new CommandJournal(fixture.Root);
        using (restarted.TryAcquireInspectionLease(recoverMissingLock: false))
        {
            var pending = restarted.ReadPending(fixture.Profile, fixture.StartExe)!;
            Assert.Equal(Fingerprint, pending.RecoveryObservationFingerprint);
            Assert.True(pending.Process is null);
            Assert.False(restarted.IsCompleted(pending));
        }
        var after = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories);
        Assert.Equal(before.Count, after.Length);
        foreach (var path in after) Assert.Equal(before[path], Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    private static void FailedIntentRemainsPending()
    {
        using var fixture = new JournalFixture("failure");
        using (fixture.Journal.AcquireLease())
        {
            var pending = fixture.Prepare();
            Assert.True(fixture.Journal.TryRecordFailure(pending, "command creation", new InvalidOperationException("blocked")) is null);
        }
        var restarted = new CommandJournal(fixture.Root);
        using (restarted.TryAcquireInspectionLease())
        {
            var pending = restarted.ReadPending(fixture.Profile, fixture.StartExe)!;
            Assert.Equal(Fingerprint, pending.RecoveryObservationFingerprint);
            Assert.Equal("command creation", pending.Failure!.Stage);
            Assert.False(restarted.IsCompleted(pending));
        }
    }

    private static void LegacyActionsRemainCompatible()
    {
        foreach (var action in new[] { SidetalkCommandAction.Launch, SidetalkCommandAction.Stop })
        {
            using var fixture = new JournalFixture("legacy-" + action);
            PendingCommandRecord prepared;
            using (fixture.Journal.AcquireLease()) prepared = fixture.Journal.Prepare(fixture.Profile, action, fixture.StartExe);
            var path = fixture.PathFor(prepared, "intent");
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            document.AsObject().Remove("RecoveryObservationFingerprint");
            File.WriteAllText(path, document.ToJsonString());
            var restarted = new CommandJournal(fixture.Root);
            using (restarted.TryAcquireInspectionLease())
            {
                var pending = restarted.ReadPending(fixture.Profile, fixture.StartExe)!;
                Assert.Equal(action, pending.Action);
                Assert.True(pending.RecoveryObservationFingerprint is null);
            }
        }
    }

    private static void ZeroAndEmptySucceeds() => AssertTerminal(0, [], CommandLifecycleOutcome.RecoveryBoxCleanupSucceeded);
    private static void NonzeroAndEmptyFails() => AssertTerminal(5, [], CommandLifecycleOutcome.RecoveryBoxCleanupFailed);
    private static void UnknownCodeAndEmptyFailsWithoutDeadlock() => AssertTerminal(null, [], CommandLifecycleOutcome.RecoveryBoxCleanupFailed);
    private static void ResidualBlocksSuccess() => AssertTerminal(0, [Residual()], CommandLifecycleOutcome.RecoveryBoxCleanupResidual);

    private static void AssertTerminal(int? exitCode, IReadOnlyList<BoxProcess> processes, CommandLifecycleOutcome expected)
    {
        var handle = new Handle(true, exitCode);
        using var lifecycle = new CommandLifecycle();
        lifecycle.Begin(SidetalkCommandAction.RecoveryBoxCleanup, handle);
        var result = lifecycle.InspectProcesses(() => processes).State;
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.False(result.IsPending);
        Assert.False(lifecycle.IsPending);
        Assert.Equal(1, handle.DisposeCount);
        Assert.Equal(processes.Count, result.ProcessCount);
    }

    private static void UnknownExitRemainsPending()
    {
        var handle = new Handle(null, null);
        using var lifecycle = new CommandLifecycle();
        lifecycle.Begin(SidetalkCommandAction.RecoveryBoxCleanup, handle);
        Assert.True(lifecycle.Observe([]).IsPending);
        Assert.Equal(CommandLifecycleOutcome.WaitingForCommand, lifecycle.LastResult.Outcome);
        Assert.Equal(0, handle.DisposeCount);
    }

    private static void MissingQueryRemainsPending()
    {
        var handle = new Handle(true, 0);
        using var lifecycle = new CommandLifecycle();
        lifecycle.Begin(SidetalkCommandAction.RecoveryBoxCleanup, handle);
        Assert.Equal(CommandLifecycleOutcome.WaitingForObservation, lifecycle.Observe(null).Outcome);
        Assert.True(lifecycle.IsPending);
        Assert.Equal(0, handle.DisposeCount);
        Assert.Equal(CommandLifecycleOutcome.RecoveryBoxCleanupSucceeded, lifecycle.InspectProcesses(() => []).State.Outcome);
    }

    private static void FailedQueryRemainsPending()
    {
        var handle = new Handle(true, 0);
        using var lifecycle = new CommandLifecycle();
        lifecycle.Begin(SidetalkCommandAction.RecoveryBoxCleanup, handle);
        Assert.Throws<InvalidOperationException>(() => lifecycle.InspectProcesses(() => throw new InvalidOperationException("query failed")));
        Assert.True(lifecycle.IsPending);
        Assert.Equal(0, handle.DisposeCount);
    }

    private static void RunningRefusesEnumeration()
    {
        using var lifecycle = new CommandLifecycle();
        lifecycle.Begin(SidetalkCommandAction.RecoveryBoxCleanup, new Handle(false, null));
        var count = 0;
        var result = lifecycle.InspectProcesses(() => { count++; return []; });
        Assert.Equal(0, count);
        Assert.True(result.State.IsPending);
    }

    private static void LegacyStopUnchanged()
    {
        using var lifecycle = new CommandLifecycle();
        lifecycle.Begin(SidetalkCommandAction.Stop, new Handle(true, null));
        Assert.Equal(CommandLifecycleOutcome.StopSucceeded, lifecycle.InspectProcesses(() => []).State.Outcome);
    }

    private static BoxProcess Residual() => new(100, "KakaoTalkUpdater.exe", "test-user", 1);

    private sealed class Handle(bool? exited, int? exitCode) : ISidetalkCommandHandle
    {
        public int DisposeCount { get; private set; }
        public bool HasExited => exited ?? throw new InvalidOperationException("exit unknown");
        public int ExitCode => exitCode ?? throw new InvalidOperationException("code unknown");
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() => DisposeCount++;
    }

    private sealed class JournalFixture : IDisposable
    {
        public string Root { get; }
        public BoxProfile Profile { get; }
        public string StartExe { get; }
        public CommandJournal Journal { get; }
        public CommandProcessIdentity Identity { get; }
        public JournalFixture(string label)
        {
            Root = TestContext.CreateRunDirectory("residual-cleanup-" + label);
            Profile = new ProfileStore(Root).Create();
            StartExe = Path.Combine(Root, "Sandboxie", "Start.exe");
            Identity = new(100, DateTime.UtcNow.Ticks, StartExe);
            Journal = new CommandJournal(Root);
        }
        public PendingCommandRecord Prepare() => Journal.Prepare(Profile, SidetalkCommandAction.RecoveryBoxCleanup, StartExe, Fingerprint);
        public string PathFor(PendingCommandRecord record, string phase) => Path.Combine(Journal.JournalDirectory, $"{record.OperationId}.{phase}.json");
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
