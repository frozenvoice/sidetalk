using System.Diagnostics;
using Sidetalk.Core;

namespace Sidetalk.Tests;

/// <summary>
/// Controller level regression coverage for durable command recovery and the distinction
/// between a verified current configuration and an unverified process launch policy.
/// These tests use only fakes; they never start Sandboxie or KakaoTalk.
/// </summary>
public static class ControllerRecoveryTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases =
    [
        ("Controller recovery: restart keeps a timed out launch blocked", RestartKeepsTimedOutLaunchBlocked),
        ("Controller recovery: dispose during wait preserves pending identity", DisposeDuringWaitPreservesPendingIdentity),
        ("Controller recovery: query failure keeps the journal pending", QueryFailureKeepsJournalPending),
        ("Controller recovery: missing exit code never becomes zero", MissingExitCodeNeverBecomesZero),
        ("Controller recovery: timed out stop remains retryable with residual process", TimedOutStopRemainsRetryableWithResidual),
        ("Controller recovery: prelaunch failure remains ambiguous until candidate is gone", PrelaunchFailureRemainsAmbiguousUntilCandidateGone),
        ("Controller policy: existing process remains rules unverified after config repair", ExistingProcessRemainsRulesUnverifiedAfterConfigRepair),
        ("Controller policy: own launch does not certify effective rules", OwnLaunchDoesNotCertifyEffectiveRules),
        ("Controller recovery: creation identity is forwarded exactly", RecoveryReceivesExactCreationIdentity),
        ("Controller recovery: read only inspection does not complete journal", ReadOnlyInspectionDoesNotCompleteJournal),
        ("Controller recovery: concurrent controllers allow one Start request", ConcurrentControllersAllowOneStartRequest),
        ("Controller recovery: corrupt journal blocks state changes", CorruptJournalBlocksStateChanges),
        ("Controller recovery: writable inspection recreates a missing journal lock", WritableInspectionRecreatesMissingJournalLock),
        ("Controller recovery: read only inspection does not recreate a missing journal lock", ReadOnlyInspectionDoesNotRecreateMissingJournalLock),
        ("Controller recovery: lockless pending journal remains durable", LocklessPendingJournalRemainsDurable),
        ("Controller recovery: lockless corrupt journal remains blocked", LocklessCorruptJournalRemainsBlocked)
    ];

    private static void RestartKeepsTimedOutLaunchBlocked()
    {
        var harness = Harness.Create("controller-restart");
        var identity = Identity(harness.StartExe, 1201);
        var firstHandle = new FakeHandle(false, 0, identity);
        harness.Runner.Enqueue(firstHandle);

        using (var first = harness.CreateController(commandTimeout: TimeSpan.FromMilliseconds(20)))
        {
            Assert.Throws<InvalidOperationException>(() => first.LaunchAsync().GetAwaiter().GetResult());
        }

        Assert.Equal(1, harness.Runner.StartCount);
        var beforeRestartQueries = harness.Runtime.ProcessQueryCount;
        var recoveredHandle = new FakeHandle(false, 0, identity);
        harness.Recovery.EnqueueRecovered(recoveredHandle);
        using var restarted = harness.CreateController(commandTimeout: TimeSpan.FromMilliseconds(20));

        var blocked = restarted.InspectAsync().GetAwaiter().GetResult();
        Assert.Equal(Readiness.Unknown, blocked.State);
        Assert.False(blocked.CanLaunch);
        Assert.False(blocked.CanStop);
        Assert.Equal(beforeRestartQueries, harness.Runtime.ProcessQueryCount);
        Assert.Equal(identity, harness.Recovery.LastIdentity);

        recoveredHandle.Complete(0);
        var ready = restarted.InspectAsync().GetAwaiter().GetResult();
        Assert.Equal(Readiness.Ready, ready.State);
        Assert.True(ready.CanLaunch);
        Assert.Equal(beforeRestartQueries + 1, harness.Runtime.ProcessQueryCount);
    }

    private static void DisposeDuringWaitPreservesPendingIdentity()
    {
        var harness = Harness.Create("controller-dispose-during-wait");
        var identity = Identity(harness.StartExe, 1210);
        var handle = new FakeHandle(false, 0, identity);
        harness.Runner.Enqueue(handle);
        var first = harness.CreateController(commandTimeout: TimeSpan.FromSeconds(5));
        var launch = first.LaunchAsync();
        Assert.True(handle.WaitEntered.Task.Wait(TimeSpan.FromSeconds(5)), "The command must reach its journaled wait.");
        first.Dispose();
        Assert.Equal(0, handle.DisposeCount);
        handle.Complete(0);
        Assert.Throws<ObjectDisposedException>(() => launch.GetAwaiter().GetResult());
        Assert.Equal(1, handle.DisposeCount);
        Assert.Equal(1, harness.Runner.StartCount);

        var recovered = new FakeHandle(false, 0, identity);
        harness.Recovery.EnqueueRecovered(recovered);
        using var restarted = harness.CreateController();
        var pending = restarted.InspectAsync().GetAwaiter().GetResult();
        Assert.Equal(Readiness.Unknown, pending.State);
        Assert.False(pending.CanLaunch);
        Assert.Equal(identity, harness.Recovery.LastIdentity);
        recovered.Complete(0);
        Assert.True(restarted.InspectAsync().GetAwaiter().GetResult().CanLaunch);
    }
    private static void QueryFailureKeepsJournalPending()
    {
        var harness = Harness.Create("controller-query-retry");
        var identity = Identity(harness.StartExe, 1202);
        harness.Runner.Enqueue(new FakeHandle(false, 0, identity));

        using (var first = harness.CreateController(commandTimeout: TimeSpan.FromMilliseconds(20)))
            Assert.Throws<InvalidOperationException>(() => first.LaunchAsync().GetAwaiter().GetResult());

        var recovered = new FakeHandle(true, 0, identity);
        harness.Recovery.EnqueueRecovered(recovered);
        var queryFailed = true;
        harness.Runtime.ProcessProvider = () =>
        {
            if (queryFailed)
            {
                queryFailed = false;
                throw new InvalidOperationException("시험 프로세스 조회 실패");
            }
            return Array.Empty<BoxProcess>();
        };

        using var controller = harness.CreateController(commandTimeout: TimeSpan.FromMilliseconds(20));
        var unavailable = controller.InspectAsync().GetAwaiter().GetResult();
        Assert.Equal(Readiness.Unknown, unavailable.State);
        Assert.False(unavailable.CanLaunch);
        Assert.False(unavailable.CanStop);

        var retry = controller.InspectAsync().GetAwaiter().GetResult();
        Assert.Equal(Readiness.Ready, retry.State);
        Assert.True(retry.CanLaunch);
        Assert.Equal(1, harness.Recovery.RecoverCount);
    }

    private static void MissingExitCodeNeverBecomesZero()
    {
        var harness = Harness.Create("controller-unknown-exit");
        var identity = Identity(harness.StartExe, 1203);
        harness.Runner.Enqueue(new FakeHandle(false, 0, identity));
        using (var first = harness.CreateController(commandTimeout: TimeSpan.FromMilliseconds(20)))
            Assert.Throws<InvalidOperationException>(() => first.LaunchAsync().GetAwaiter().GetResult());

        harness.Recovery.EnqueueRecovered(new FakeHandle(true, 0, identity, exitCodeAvailable: false));
        using var controller = harness.CreateController(commandTimeout: TimeSpan.FromMilliseconds(20));
        var snapshot = controller.InspectAsync().GetAwaiter().GetResult();

        Assert.Equal(Readiness.Ready, snapshot.State);
        Assert.True(snapshot.CanLaunch);
        Assert.Contains("종료 코드는 확인할 수 없으며", snapshot.Detail);
        Assert.False(snapshot.Detail.Contains("종료 코드가 0", StringComparison.Ordinal));
    }

    private static void TimedOutStopRemainsRetryableWithResidual()
    {
        var harness = Harness.Create("controller-stop-restart");
        harness.Runtime.ProcessProvider = () => [Process("KakaoTalk.exe", 1204)];
        var identity = Identity(harness.StartExe, 1204);
        harness.Runner.Enqueue(new FakeHandle(false, 0, identity));

        using (var first = harness.CreateController(commandTimeout: TimeSpan.FromMilliseconds(20)))
            Assert.Throws<InvalidOperationException>(() => first.StopAsync().GetAwaiter().GetResult());

        Assert.Equal(1, harness.Runner.StartCount);
        var beforeRestartQueries = harness.Runtime.ProcessQueryCount;
        var recovered = new FakeHandle(false, 0, identity);
        harness.Recovery.EnqueueRecovered(recovered);
        using var restarted = harness.CreateController(commandTimeout: TimeSpan.FromMilliseconds(20));
        var pending = restarted.InspectAsync().GetAwaiter().GetResult();
        Assert.Equal(Readiness.Unknown, pending.State);
        Assert.False(pending.CanStop);
        Assert.Equal(beforeRestartQueries, harness.Runtime.ProcessQueryCount);

        recovered.Complete(0);
        var residual = restarted.InspectAsync().GetAwaiter().GetResult();
        Assert.Equal(Readiness.RunningUnverified, residual.State);
        Assert.Equal(ProcessPolicyStatus.EffectiveRulesUnverified, residual.ProcessPolicy);
        Assert.True(residual.CanStop);
        Assert.False(residual.CanLaunch);
        Assert.Contains("시작 당시 적용 규칙", residual.Detail);
    }

    private static void PrelaunchFailureRemainsAmbiguousUntilCandidateGone()
    {
        var harness = Harness.Create("controller-prelaunch-crash");
        harness.Runner.BeforeStart = () =>
        {
            var journalDirectory = Path.Combine(harness.Root, "command-journal");
            Assert.True(Directory.EnumerateFiles(journalDirectory, "*.intent.json").Any(),
                "The intent must be durable before the command runner is called.");
        };
        harness.Runner.ThrowOnStart = true;
        using (var first = harness.CreateController(commandTimeout: TimeSpan.FromMilliseconds(20)))
            Assert.Throws<InvalidOperationException>(() => first.LaunchAsync().GetAwaiter().GetResult());

        Assert.Equal(1, harness.Runner.StartCount);
        var beforeRestartQueries = harness.Runtime.ProcessQueryCount;
        harness.Recovery.EnqueueUnidentified(() => throw new InvalidOperationException("동일한 Start.exe 후보가 아직 실행 중입니다."));
        harness.Recovery.EnqueueUnidentified(() => new FakeHandle(true, 0, null, exitCodeAvailable: false));
        using var restarted = harness.CreateController(commandTimeout: TimeSpan.FromMilliseconds(20));

        var candidateLive = restarted.InspectAsync().GetAwaiter().GetResult();
        Assert.Equal(Readiness.Unknown, candidateLive.State);
        Assert.False(candidateLive.CanLaunch);
        Assert.Equal(beforeRestartQueries, harness.Runtime.ProcessQueryCount);

        var candidateGone = restarted.InspectAsync().GetAwaiter().GetResult();
        Assert.Equal(Readiness.Ready, candidateGone.State);
        Assert.True(candidateGone.CanLaunch);
        Assert.Contains("종료 코드는 확인할 수 없으며", candidateGone.Detail);
    }

    private static void ExistingProcessRemainsRulesUnverifiedAfterConfigRepair()
    {
        var harness = Harness.Create("controller-policy-origin");
        harness.Runtime.ProcessProvider = () => [Process("KakaoTalk.exe", 1205)];
        var unsafeBox = Copy(IsolationPolicy.Expected(harness.Profile));
        unsafeBox["OpenFilePath"] = [Path.Combine(harness.Root, "outside.txt")];
        using var controller = harness.CreateController();
        var beforeChange = controller.InspectAsync().GetAwaiter().GetResult();
        Assert.Equal(ProcessPolicyStatus.EffectiveRulesUnverified, beforeChange.ProcessPolicy);
        harness.Runtime.ConfigurationProvider = () => harness.Configuration(unsafeBox);

        var blocked = controller.InspectAsync().GetAwaiter().GetResult();
        Assert.Equal(Readiness.Blocked, blocked.State);
        Assert.False(blocked.CurrentConfigurationVerified);

        harness.Runtime.ConfigurationProvider = () => harness.Configuration();
        var repaired = controller.InspectAsync().GetAwaiter().GetResult();
        Assert.Equal(Readiness.RunningUnverified, repaired.State);
        Assert.True(repaired.CurrentConfigurationVerified);
        Assert.Equal(ProcessPolicyStatus.EffectiveRulesUnverified, repaired.ProcessPolicy);
        Assert.False(repaired.CanLaunch);
        Assert.True(repaired.CanStop);
        Assert.Contains("현재 설정은 검증됐지만", repaired.Detail);

        harness.Runtime.ProcessProvider = () => Array.Empty<BoxProcess>();
        var empty = controller.InspectAsync().GetAwaiter().GetResult();
        Assert.Equal(Readiness.Ready, empty.State);
        Assert.Equal(ProcessPolicyStatus.NoProcesses, empty.ProcessPolicy);
        Assert.True(empty.CanLaunch);
    }

    private static void OwnLaunchDoesNotCertifyEffectiveRules()
    {
        var harness = Harness.Create("controller-own-launch-policy");
        var identity = Identity(harness.StartExe, 1206);
        var handle = new FakeHandle(true, 0, identity);
        harness.Runner.Enqueue(handle);
        var processVisible = false;
        harness.Runtime.ProcessProvider = () => processVisible
            ? [Process("KakaoTalk.exe", 1206)]
            : Array.Empty<BoxProcess>();
        harness.Runner.AfterStart = () => processVisible = true;

        using var controller = harness.CreateController(commandTimeout: TimeSpan.FromMilliseconds(20));
        controller.LaunchAsync().GetAwaiter().GetResult();
        var snapshot = controller.InspectAsync().GetAwaiter().GetResult();

        Assert.Equal(Readiness.RunningUnverified, snapshot.State);
        Assert.True(snapshot.CurrentConfigurationVerified);
        Assert.Equal(ProcessPolicyStatus.EffectiveRulesUnverified, snapshot.ProcessPolicy);
        Assert.False(snapshot.CanLaunch);
        Assert.True(snapshot.CanStop);
        Assert.False(snapshot.Detail.Contains("적용 규칙이 확인", StringComparison.Ordinal));
    }

    private static void RecoveryReceivesExactCreationIdentity()
    {
        var harness = Harness.Create("controller-identity-forwarding");
        var identity = new CommandProcessIdentity(1207, DateTime.UtcNow.AddMinutes(-1).Ticks, harness.StartExe);
        harness.Runner.Enqueue(new FakeHandle(false, 0, identity));
        using (var first = harness.CreateController(commandTimeout: TimeSpan.FromMilliseconds(20)))
            Assert.Throws<InvalidOperationException>(() => first.LaunchAsync().GetAwaiter().GetResult());

        harness.Recovery.ExpectedIdentity = identity;
        harness.Recovery.EnqueueRecovered(new FakeHandle(true, 0, identity));
        using var restarted = harness.CreateController(commandTimeout: TimeSpan.FromMilliseconds(20));
        var result = restarted.InspectAsync().GetAwaiter().GetResult();

        Assert.Equal(Readiness.Ready, result.State);
        Assert.Equal(identity, harness.Recovery.LastIdentity);
        Assert.Equal(identity.CreationTimeUtcTicks, harness.Recovery.LastIdentity!.CreationTimeUtcTicks);
    }

    private static void ReadOnlyInspectionDoesNotCompleteJournal()
    {
        var harness = Harness.Create("controller-readonly-journal");
        var identity = Identity(harness.StartExe, 1208);
        harness.Runner.Enqueue(new FakeHandle(false, 0, identity));
        using (var first = harness.CreateController(commandTimeout: TimeSpan.FromMilliseconds(20)))
            Assert.Throws<InvalidOperationException>(() => first.LaunchAsync().GetAwaiter().GetResult());

        harness.Recovery.EnqueueRecovered(new FakeHandle(true, 0, identity));
        using (var readOnly = harness.CreateController(readOnly: true, commandTimeout: TimeSpan.FromMilliseconds(20)))
        {
            var inspected = readOnly.InspectAsync().GetAwaiter().GetResult();
            Assert.Equal(Readiness.Ready, inspected.State);
            Assert.False(inspected.ActionsPermitted);
            Assert.Throws<InvalidOperationException>(() => readOnly.LaunchAsync().GetAwaiter().GetResult());
        }

        // A writable controller must still see and reconcile the same journal record.
        harness.Recovery.EnqueueRecovered(new FakeHandle(true, 0, identity));
        using var writable = harness.CreateController(commandTimeout: TimeSpan.FromMilliseconds(20));
        var reconciled = writable.InspectAsync().GetAwaiter().GetResult();
        Assert.Equal(Readiness.Ready, reconciled.State);
        Assert.Equal(2, harness.Recovery.RecoverCount);
    }

    private static void ConcurrentControllersAllowOneStartRequest()
    {
        var harness = Harness.Create("controller-concurrent-start");
        var identity = Identity(harness.StartExe, 1209);
        var handle = new FakeHandle(false, 0, identity);
        harness.Runner.Enqueue(handle);
        using var first = harness.CreateController(commandTimeout: TimeSpan.FromMilliseconds(30));
        using var second = harness.CreateController(commandTimeout: TimeSpan.FromMilliseconds(30));

        var firstTask = first.LaunchAsync();
        var secondTask = second.LaunchAsync();
        Assert.Throws<Exception>(() => Task.WhenAll(firstTask, secondTask).GetAwaiter().GetResult());
        Assert.Equal(1, harness.Runner.StartCount);
        Assert.True(harness.Recovery.RecoverCount <= 1);
    }

    private static void CorruptJournalBlocksStateChanges()
    {
        var harness = Harness.Create("controller-corrupt-journal");
        PendingCommandRecord record;
        var journal = new CommandJournal(harness.Root);
        using (journal.AcquireLease())
            record = journal.Prepare(harness.Profile, SidetalkCommandAction.Launch, harness.StartExe);

        File.WriteAllText(Path.Combine(harness.Root, "command-journal", $"{record.OperationId}.intent.json"), "{corrupt");
        using var controller = harness.CreateController();
        var snapshot = controller.InspectAsync().GetAwaiter().GetResult();

        Assert.Equal(Readiness.Unknown, snapshot.State);
        Assert.False(snapshot.CanLaunch);
        Assert.False(snapshot.CanStop);
        Assert.Equal(0, harness.Runtime.ProcessQueryCount);
        Assert.Contains("손상", snapshot.Detail);
    }
    private static void WritableInspectionRecreatesMissingJournalLock()
    {
        var harness = Harness.Create("controller-lockless-empty");
        var journalDirectory = Path.Combine(harness.Root, "command-journal");
        Directory.CreateDirectory(journalDirectory);
        var lockPath = Path.Combine(journalDirectory, "command.lock");
        Assert.False(File.Exists(lockPath));
        using var controller = harness.CreateController();

        var snapshot = controller.InspectAsync().GetAwaiter().GetResult();

        Assert.Equal(Readiness.Ready, snapshot.State);
        Assert.True(snapshot.CanLaunch);
        Assert.Equal(0, harness.Runner.StartCount);
        Assert.True(File.Exists(lockPath), "Writable inspection must recreate the missing journal lock.");
    }

    private static void ReadOnlyInspectionDoesNotRecreateMissingJournalLock()
    {
        var harness = Harness.Create("controller-lockless-readonly");
        var journalDirectory = Path.Combine(harness.Root, "command-journal");
        Directory.CreateDirectory(journalDirectory);
        var lockPath = Path.Combine(journalDirectory, "command.lock");
        using var controller = harness.CreateController(readOnly: true);

        var snapshot = controller.InspectAsync().GetAwaiter().GetResult();

        Assert.Equal(Readiness.Unknown, snapshot.State);
        Assert.False(snapshot.ActionsPermitted);
        Assert.False(File.Exists(lockPath), "Read only inspection must not create a missing journal lock.");
        Assert.Equal(0, harness.Runner.StartCount);
        Assert.Equal(0, harness.Runtime.ProcessQueryCount);
    }

    private static void LocklessPendingJournalRemainsDurable()
    {
        var harness = Harness.Create("controller-lockless-pending");
        var identity = Identity(harness.StartExe, 1211);
        harness.Runner.Enqueue(new FakeHandle(false, 0, identity));
        using (var first = harness.CreateController(commandTimeout: TimeSpan.FromMilliseconds(20)))
            Assert.Throws<InvalidOperationException>(() => first.LaunchAsync().GetAwaiter().GetResult());

        Assert.Equal(1, harness.Runner.StartCount);
        var journal = new CommandJournal(harness.Root);
        var intentPath = Directory.EnumerateFiles(journal.JournalDirectory, "*.intent.json").Single();
        var startedPath = Directory.EnumerateFiles(journal.JournalDirectory, "*.started.json").Single();
        var intentBytes = File.ReadAllBytes(intentPath);
        var startedBytes = File.ReadAllBytes(startedPath);
        File.Delete(journal.LockPath);
        Assert.False(File.Exists(journal.LockPath));

        var recovered = new FakeHandle(false, 0, identity);
        harness.Recovery.EnqueueRecovered(recovered);
        var beforeQueries = harness.Runtime.ProcessQueryCount;
        using var restarted = harness.CreateController();
        var blocked = restarted.InspectAsync().GetAwaiter().GetResult();
        Assert.Equal(Readiness.Unknown, blocked.State);
        Assert.False(blocked.CanLaunch);
        Assert.False(blocked.CanStop);
        Assert.True(File.Exists(journal.LockPath));
        Assert.Equal(beforeQueries, harness.Runtime.ProcessQueryCount);
        Assert.Equal(identity, harness.Recovery.LastIdentity);
        Assert.Equal(0, Directory.GetFiles(journal.JournalDirectory, "*.completed.json").Length);

        recovered.Complete(0);
        var ready = restarted.InspectAsync().GetAwaiter().GetResult();
        Assert.Equal(Readiness.Ready, ready.State);
        Assert.True(ready.CanLaunch);
        Assert.Equal(beforeQueries + 1, harness.Runtime.ProcessQueryCount);
        Assert.Equal(1, harness.Runner.StartCount);
        Assert.Equal(1, Directory.GetFiles(journal.JournalDirectory, "*.completed.json").Length);
        Assert.SequenceEqual(intentBytes, File.ReadAllBytes(intentPath));
        Assert.SequenceEqual(startedBytes, File.ReadAllBytes(startedPath));
    }

    private static void LocklessCorruptJournalRemainsBlocked()
    {
        var harness = Harness.Create("controller-lockless-corrupt");
        var journal = new CommandJournal(harness.Root);
        PendingCommandRecord record;
        using (journal.AcquireLease())
            record = journal.Prepare(harness.Profile, SidetalkCommandAction.Launch, harness.StartExe);
        var intentPath = Path.Combine(journal.JournalDirectory, $"{record.OperationId}.intent.json");
        File.Delete(journal.LockPath);
        File.WriteAllText(intentPath, "{corrupt");

        using var controller = harness.CreateController();
        var snapshot = controller.InspectAsync().GetAwaiter().GetResult();

        Assert.Equal(Readiness.Unknown, snapshot.State);
        Assert.False(snapshot.CanLaunch);
        Assert.False(snapshot.CanStop);
        Assert.Equal(0, harness.Runner.StartCount);
        Assert.Equal(0, harness.Runtime.ProcessQueryCount);
        Assert.Contains("손상", snapshot.Detail);
        Assert.True(File.Exists(journal.LockPath), "Writable inspection may recreate the lock before reading the record.");
        Assert.Equal("{corrupt", File.ReadAllText(intentPath));
    }
    private static CommandProcessIdentity Identity(string startExe, int pid) =>
        new(pid, DateTime.UtcNow.AddMinutes(-1).Ticks + pid, startExe);

    private static BoxProcess Process(string image, int id) => new(id, image, ProfileStore.CurrentSid(), 1);

    private static Dictionary<string, string[]> Copy(IReadOnlyDictionary<string, string[]> source) =>
        source.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);

    private sealed class Harness
    {
        private Harness(string root, ProfileStore store, BoxProfile profile, FakeRuntime runtime, FakeRunner runner, FakeRecovery recovery)
        {
            Root = root;
            Store = store;
            Profile = profile;
            Runtime = runtime;
            Runner = runner;
            Recovery = recovery;
            StartExe = runtime.StartExe;
        }

        public string Root { get; }
        public string StartExe { get; }
        public ProfileStore Store { get; }
        public BoxProfile Profile { get; }
        public FakeRuntime Runtime { get; }
        public FakeRunner Runner { get; }
        public FakeRecovery Recovery { get; }

        public static Harness Create(string name)
        {
            var root = TestContext.CreateRunDirectory(name);
            var store = new ProfileStore(root);
            var profile = store.Create();
            var runtime = new FakeRuntime(root, profile);
            var runner = new FakeRunner();
            var recovery = new FakeRecovery();
            return new(root, store, profile, runtime, runner, recovery);
        }

        public ConfigurationLayers Configuration(IReadOnlyDictionary<string, string[]>? box = null) =>
            new(box ?? IsolationPolicy.Expected(Profile), box ?? IsolationPolicy.Expected(Profile),
                new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase),
                BuiltinTemplates.ExpectedGlobals(new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)));

        public SidetalkController CreateController(bool readOnly = false, TimeSpan? commandTimeout = null) =>
            new(Store, Runner, Runtime, Recovery, commandTimeout, readOnly);
    }

    private sealed class FakeRuntime : ISidetalkRuntime
    {
        private readonly string root;
        private readonly BoxProfile profile;
        public FakeRuntime(string root, BoxProfile profile)
        {
            this.root = root;
            this.profile = profile;
            StartExe = Path.Combine(root, "Start.exe");
            ConfigurationProvider = () => new ConfigurationLayers(
                IsolationPolicy.Expected(profile), IsolationPolicy.Expected(profile),
                new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase),
                BuiltinTemplates.ExpectedGlobals(new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)));
        }

        public string StartExe { get; }
        public int ProcessQueryCount { get; private set; }
        public Func<IReadOnlyList<BoxProcess>> ProcessProvider { get; set; } = () => Array.Empty<BoxProcess>();
        public Func<ConfigurationLayers> ConfigurationProvider { get; set; }
        public DependencyInfo Kakao() => new("공식 카카오톡", Path.Combine(root, "KakaoTalk.exe"), "26.7.1", "시험", true);
        public DependencyInfo Sandboxie() => new("Sandboxie Plus", Path.Combine(root, "SandMan.exe"), "1.18.4", "시험", true);
        public void RequireHost() { }
        public void VerifyCommandFiles(string startExe, string kakaoExe)
        {
            Assert.Equal(StartExe, startExe);
            Assert.Equal(Path.Combine(root, "KakaoTalk.exe"), kakaoExe);
        }
        public ISidetalkRuntimeSession Open(DependencyInfo sandboxie) => new FakeSession(this, profile);

        private sealed class FakeSession(FakeRuntime runtime, BoxProfile profile) : ISidetalkRuntimeSession
        {
            public RuntimeVerifier.ServiceVerification ServiceIdentity { get; } =
                new(9999, false, "시험용 SCM 구성", "시험에서는 실행 이미지 직접 확인을 하지 않음");
            public ConfigurationLayers ReadConfiguration(string boxName)
            {
                Assert.Equal(profile.BoxName, boxName);
                return runtime.ConfigurationProvider();
            }
            public string FileRoot(string boxName)
            {
                Assert.Equal(profile.BoxName, boxName);
                return profile.StoragePath;
            }
            public IReadOnlyList<BoxProcess> Processes(BoxProfile requested)
            {
                Assert.Equal(profile, requested);
                runtime.ProcessQueryCount++;
                return runtime.ProcessProvider();
            }
            public void Dispose() { }
        }
    }

    private sealed class FakeRunner : ISidetalkCommandRunner
    {
        private readonly Queue<ISidetalkCommandHandle> handles = new();
        public int StartCount { get; private set; }
        public bool ThrowOnStart { get; set; }
        public Action? BeforeStart { get; set; }
        public Action? AfterStart { get; set; }
        public void Enqueue(ISidetalkCommandHandle handle) => handles.Enqueue(handle);
        public ISidetalkCommandHandle Start(ProcessStartInfo command)
        {
            StartCount++;
            BeforeStart?.Invoke();
            if (ThrowOnStart) throw new InvalidOperationException("시험용 프로세스 생성 실패");
            if (handles.Count == 0) throw new InvalidOperationException("시험용 명령 핸들이 없습니다.");
            var handle = handles.Dequeue();
            AfterStart?.Invoke();
            return handle;
        }
    }

    private sealed class FakeRecovery : ICommandProcessRecovery
    {
        private readonly Queue<ISidetalkCommandHandle> recovered = new();
        private readonly Queue<Func<ISidetalkCommandHandle>> unidentified = new();
        public int RecoverCount { get; private set; }
        public int RecoverUnidentifiedCount { get; private set; }
        public CommandProcessIdentity? LastIdentity { get; private set; }
        public CommandProcessIdentity? ExpectedIdentity { get; set; }
        public void EnqueueRecovered(ISidetalkCommandHandle handle) => recovered.Enqueue(handle);
        public void EnqueueUnidentified(Func<ISidetalkCommandHandle> factory) => unidentified.Enqueue(factory);
        public ISidetalkCommandHandle Recover(CommandProcessIdentity identity)
        {
            RecoverCount++;
            LastIdentity = identity;
            if (ExpectedIdentity is not null) Assert.Equal(ExpectedIdentity, identity);
            return recovered.Count == 0 ? new FakeHandle(false, 0, identity) : recovered.Dequeue();
        }
        public ISidetalkCommandHandle RecoverUnidentified(string expectedStartExe)
        {
            RecoverUnidentifiedCount++;
            if (unidentified.Count == 0) return new FakeHandle(true, 0, null);
            return unidentified.Dequeue()();
        }
    }

    private sealed class FakeHandle : ISidetalkCommandHandle
    {
        private volatile bool exited;
        public int DisposeCount { get; private set; }
        public TaskCompletionSource<bool> WaitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int exitCode;
        private readonly bool exitCodeAvailable;
        public FakeHandle(bool exited, int exitCode, CommandProcessIdentity? identity, bool exitCodeAvailable = true)
        {
            this.exited = exited;
            this.exitCode = exitCode;
            this.exitCodeAvailable = exitCodeAvailable;
            Identity = identity;
        }
        public CommandProcessIdentity? Identity { get; }
        public bool HasExited => exited;
        public int ExitCode => exitCodeAvailable ? exitCode : throw new InvalidOperationException("시험용 종료 코드 미확인");
        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitEntered.TrySetResult(true);
            while (!exited)
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
        public void Complete(int code)
        {
            exited = true;
        }
        public void Dispose() => DisposeCount++;
    }
}
