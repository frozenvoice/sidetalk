using System.Diagnostics;
using System.Text.Json;
using Sidetalk.Core;

namespace Sidetalk.Tests;

/// <summary>All launch and installation boundaries are fake; only disposable profile/journal/consent fixtures are written.</summary>
public static class QuickLaunchTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases =
    [
        ("Quick launch: inspection defaults off and creates no files", () => Run(InspectionDefaultsOff)),
        ("Quick launch: one-off approval launches without storing consent", () => Run(OneOffApprovalDoesNotPersist)),
        ("Quick launch: consent survives restart and revocation is append-only", () => Run(ConsentAndAppendOnlyRevocation)),
        ("Quick launch: full profile identity changes invalidate consent", () => Run(ProfileIdentityChangeInvalidatesConsent)),
        ("Quick launch: installation path and version changes invalidate consent", () => Run(InstallationChangesInvalidateConsent)),
        ("Quick launch: key order is canonical but value order and duplicates are bound", () => Run(CanonicalKeysPreserveValues)),
        ("Quick launch: all validated configuration layers are bound", () => Run(ConfigurationLayerChangesInvalidateConsent)),
        ("Quick launch: second inspection drift refuses command intent", () => Run(SecondInspectionDrift)),
        ("Quick launch: consent write revalidates after executable verification", () => Run(ConsentWriteRevalidation)),
        ("Quick launch: existing owned processes never cause duplicate launch", () => Run(AlreadyRunningDoesNotLaunch)),
        ("Quick launch: two requests and GUI launch serialize on one controller", () => Run(ConcurrentRequestsAndGui)),
        ("Quick launch: separate controllers share the command journal lease", () => Run(SeparateControllersShareLease)),
        ("Quick launch: cancellation before intent leaves no command record", () => Run(CancellationBeforeIntent)),
        ("Quick launch: cancellation after dispatch waits for completion", () => Run(CancellationAfterDispatch)),
        ("Quick launch: timeout retains pending journal and process handle", () => Run(TimeoutRemainsUncertain)),
        ("Quick launch: changed policy after dispatch cannot claim completion", () => Run(PostDispatchPolicyFailure)),
        ("Quick launch: damaged and partial consent records remain preserved and off", () => Run(DamagedConsentRemainsOff)),
        ("Quick launch: failed executable verification never creates intent", () => Run(ExecutableVerificationFailsClosed)),
        ("Quick launch: concurrent revocation waits for command reconciliation", () => Run(RevocationWaitsForCommand))
    ];

    private static void Run(Func<Task> test) => test().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();

    private static async Task InspectionDefaultsOff()
    {
        var fixture = new Fixture("quick-default-off");
        var before = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories);
        using var controller = fixture.Controller();
        var status = await controller.InspectQuickLaunchAsync();
        Assert.True(status.Approval is not null);
        Assert.False(status.ConsentMatches);
        Assert.Equal(false, status.Snapshot.CommandPending);
        Assert.SequenceEqual(before, Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
        var result = await controller.LaunchSecondaryAsync(null);
        Assert.Equal(QuickLaunchOutcome.ConfirmationRequired, result.Outcome);
        Assert.Equal(0, fixture.Runner.StartCount);
        Assert.False(Directory.Exists(fixture.ConsentDirectory));
        Assert.False(Directory.EnumerateFiles(fixture.Root, "*.intent.json", SearchOption.AllDirectories).Any());
    }

    private static async Task OneOffApprovalDoesNotPersist()
    {
        var fixture = new Fixture("quick-one-off");
        using var controller = fixture.Controller();
        var status = await controller.InspectQuickLaunchAsync();
        fixture.EnqueueLaunch(completed: true);
        var result = await controller.LaunchSecondaryAsync(status.Approval);
        Assert.Equal(QuickLaunchOutcome.Launched, result.Outcome);
        Assert.Equal(false, result.Snapshot.CommandPending);
        Assert.True(result.Snapshot.BoxProcessIds.Count > 0);
        Assert.Equal(1, fixture.Runner.StartCount);
        Assert.Equal(1, Directory.GetFiles(fixture.JournalDirectory, "*.completed.json").Length);
        Assert.False(Directory.Exists(fixture.ConsentDirectory));
        Assert.False((await controller.InspectQuickLaunchAsync()).ConsentMatches);
    }

    private static async Task ConsentAndAppendOnlyRevocation()
    {
        var fixture = new Fixture("quick-consent-revoke");
        using (var first = fixture.Controller())
        {
            var status = await first.InspectQuickLaunchAsync();
            Assert.True((await first.SetQuickLaunchConsentAsync(status.Approval!, true)).ConsentMatches);
        }
        var granted = Directory.GetFiles(fixture.ConsentDirectory).Single();
        var original = File.ReadAllBytes(granted);
        using var restarted = fixture.Controller();
        var current = await restarted.InspectQuickLaunchAsync();
        Assert.True(current.ConsentMatches);
        var revoked = await restarted.SetQuickLaunchConsentAsync(current.Approval!, false);
        Assert.False(revoked.ConsentMatches);
        Assert.Equal(2, Directory.GetFiles(fixture.ConsentDirectory).Length);
        Assert.SequenceEqual(original, File.ReadAllBytes(granted));
        Assert.Equal(QuickLaunchOutcome.ConfirmationRequired, (await restarted.LaunchSecondaryAsync(null)).Outcome);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "setup-recovery")));
        Assert.Equal(0, fixture.Runner.StartCount);
    }

    private static async Task ProfileIdentityChangeInvalidatesConsent()
    {
        var fixture = new Fixture("quick-profile-scope");
        using var controller = fixture.Controller();
        var original = await Enable(controller);
        var bytes = File.ReadAllBytes(Directory.GetFiles(fixture.ConsentDirectory).Single());
        fixture.Profile = fixture.Profile with { OwnerToken = Guid.NewGuid().ToString("N") };
        File.WriteAllText(fixture.Store.ProfilePath, JsonSerializer.Serialize(fixture.Profile));
        var changed = await controller.InspectQuickLaunchAsync();
        Assert.True(changed.Approval is not null);
        Assert.False(changed.ConsentMatches);
        Assert.NotEqual(original.Approval!.ScopeFingerprint, changed.Approval!.ScopeFingerprint);
        Assert.Throws<InvalidOperationException>(() => controller.SetQuickLaunchConsentAsync(original.Approval, true).GetAwaiter().GetResult());
        Assert.Equal(QuickLaunchOutcome.ConfirmationRequired, (await controller.LaunchSecondaryAsync(original.Approval)).Outcome);
        Assert.SequenceEqual(bytes, File.ReadAllBytes(Directory.GetFiles(fixture.ConsentDirectory).Single()));
        Assert.Equal(0, fixture.Runner.StartCount);
    }

    private static async Task InstallationChangesInvalidateConsent()
    {
        var fixture = new Fixture("quick-install-scope");
        using var controller = fixture.Controller();
        var original = await Enable(controller);
        fixture.Runtime.KakaoVersion = "26.8.0";
        var versionChanged = await controller.InspectQuickLaunchAsync();
        Assert.False(versionChanged.ConsentMatches);
        Assert.NotEqual(original.Approval!.ScopeFingerprint, versionChanged.Approval!.ScopeFingerprint);
        fixture.Runtime.KakaoVersion = "26.7.1";
        fixture.Runtime.KakaoPath = Path.Combine(fixture.Root, "moved", "KakaoTalk.exe");
        var pathChanged = await controller.InspectQuickLaunchAsync();
        Assert.False(pathChanged.ConsentMatches);
        Assert.NotEqual(original.Approval.ScopeFingerprint, pathChanged.Approval!.ScopeFingerprint);
        Assert.Equal(QuickLaunchOutcome.ConfirmationRequired, (await controller.LaunchSecondaryAsync(null)).Outcome);
        Assert.Equal(0, fixture.Runner.StartCount);
    }

    private static async Task CanonicalKeysPreserveValues()
    {
        var fixture = new Fixture("quick-canonical-scope");
        fixture.BoxMetadata["BoxAlias"] = ["first", "second"];
        using var controller = fixture.Controller();
        var original = await Enable(controller);
        fixture.Runtime.ConfigurationOverride = () =>
        {
            var current = fixture.Configuration();
            return new(ReverseKeys(current.ExplicitBox), ReverseKeys(current.EffectiveBox),
                ReverseKeys(current.ExplicitGlobals), ReverseKeys(current.EffectiveGlobals));
        };
        var reorderedKeys = await controller.InspectQuickLaunchAsync();
        Assert.True(reorderedKeys.ConsentMatches);
        Assert.Equal(original.Approval!.ScopeFingerprint, reorderedKeys.Approval!.ScopeFingerprint);
        fixture.BoxMetadata["BoxAlias"] = ["second", "first"];
        var reorderedValues = await controller.InspectQuickLaunchAsync();
        Assert.False(reorderedValues.ConsentMatches);
        Assert.NotEqual(original.Approval.ScopeFingerprint, reorderedValues.Approval!.ScopeFingerprint);
        fixture.BoxMetadata["BoxAlias"] = ["first", "second", "second"];
        var duplicates = await controller.InspectQuickLaunchAsync();
        Assert.False(duplicates.ConsentMatches);
        Assert.NotEqual(original.Approval.ScopeFingerprint, duplicates.Approval!.ScopeFingerprint);
        Assert.Equal(0, fixture.Runner.StartCount);
    }

    private static async Task ConfigurationLayerChangesInvalidateConsent()
    {
        var fixture = new Fixture("quick-all-config-layers");
        using var controller = fixture.Controller();
        var original = await Enable(controller);
        fixture.Globals["DefaultBox"] = ["DefaultBox"];
        var globalsChanged = await controller.InspectQuickLaunchAsync();
        Assert.True(globalsChanged.Snapshot.CurrentConfigurationVerified);
        Assert.False(globalsChanged.ConsentMatches);
        Assert.NotEqual(original.Approval!.ScopeFingerprint, globalsChanged.Approval!.ScopeFingerprint);
        fixture.Globals.Clear();
        fixture.Runtime.ConfigurationOverride = () =>
        {
            var current = fixture.Configuration();
            var changed = Copy(current.EffectiveGlobals);
            changed["OpenFilePath"] = ["C:\\outside\\*"];
            return current with { EffectiveGlobals = changed };
        };
        var inheritedChange = await controller.InspectQuickLaunchAsync();
        Assert.False(inheritedChange.ConsentMatches);
        Assert.True(inheritedChange.Approval is null);
        Assert.Equal(Readiness.Blocked, inheritedChange.Snapshot.State);
        Assert.True(inheritedChange.Snapshot.CommandPending is null);
    }

    private static async Task SecondInspectionDrift()
    {
        var fixture = new Fixture("quick-second-inspection-drift");
        using var controller = fixture.Controller();
        var original = await Enable(controller);
        var verifications = fixture.Runtime.VerifyCount;
        fixture.Runtime.AfterVerify = () => fixture.BoxMetadata["BoxAlias"] = ["changed after executable verification"];
        var result = await controller.LaunchSecondaryAsync(null);
        Assert.Equal(QuickLaunchOutcome.ConfirmationRequired, result.Outcome);
        Assert.Equal(verifications + 1, fixture.Runtime.VerifyCount);
        Assert.Equal(0, fixture.Runner.StartCount);
        Assert.False(Directory.GetFiles(fixture.JournalDirectory, "*.intent.json").Any());
        Assert.Equal(QuickLaunchOutcome.ConfirmationRequired, (await controller.LaunchSecondaryAsync(original.Approval)).Outcome);
        Assert.Equal(0, fixture.Runner.StartCount);
    }

    private static async Task ConsentWriteRevalidation()
    {
        var fixture = new Fixture("quick-consent-revalidation");
        using var controller = fixture.Controller();
        var original = await controller.InspectQuickLaunchAsync();
        fixture.Runtime.AfterVerify = () => fixture.Runtime.KakaoVersion = "changed-between-checks";
        Assert.Throws<InvalidOperationException>(() => controller.SetQuickLaunchConsentAsync(original.Approval!, true).GetAwaiter().GetResult());
        Assert.False(Directory.Exists(fixture.ConsentDirectory));
        Assert.Equal(0, fixture.Runner.StartCount);
    }

    private static async Task AlreadyRunningDoesNotLaunch()
    {
        var fixture = new Fixture("quick-already-running");
        fixture.Processes = [fixture.Process()];
        using var controller = fixture.Controller();
        var result = await controller.LaunchSecondaryAsync(null);
        Assert.Equal(QuickLaunchOutcome.AlreadyRunning, result.Outcome);
        Assert.Equal(false, result.Snapshot.CommandPending);
        Assert.True(result.Snapshot.CurrentConfigurationVerified);
        Assert.Equal(0, fixture.Runner.StartCount);
        Assert.False(Directory.GetFiles(fixture.JournalDirectory, "*.intent.json").Any());
    }

    private static async Task ConcurrentRequestsAndGui()
    {
        var fixture = new Fixture("quick-concurrent-gui");
        using var controller = fixture.Controller();
        await Enable(controller);
        var handle = fixture.EnqueueLaunch(completed: false);
        var first = controller.LaunchSecondaryAsync(null);
        await handle.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = controller.LaunchSecondaryAsync(null);
        var gui = controller.LaunchAsync();
        Assert.False(second.IsCompleted);
        Assert.False(gui.IsCompleted);
        Assert.Equal(1, fixture.Runner.StartCount);
        handle.Complete();
        Assert.Equal(QuickLaunchOutcome.Launched, (await first).Outcome);
        Assert.Equal(QuickLaunchOutcome.AlreadyRunning, (await second).Outcome);
        Assert.Throws<InvalidOperationException>(() => gui.GetAwaiter().GetResult());
        Assert.Equal(1, fixture.Runner.StartCount);
    }

    private static async Task SeparateControllersShareLease()
    {
        var fixture = new Fixture("quick-concurrent-controllers");
        using var firstController = fixture.Controller();
        using var secondController = fixture.Controller();
        await Enable(firstController);
        var handle = fixture.EnqueueLaunch(completed: false);
        var first = firstController.LaunchSecondaryAsync(null);
        await handle.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await secondController.LaunchSecondaryAsync(null);
        Assert.Equal(QuickLaunchOutcome.Uncertain, second.Outcome);
        Assert.Throws<InvalidOperationException>(() => secondController.LaunchAsync().GetAwaiter().GetResult());
        Assert.Equal(1, fixture.Runner.StartCount);
        handle.Complete();
        Assert.Equal(QuickLaunchOutcome.Launched, (await first).Outcome);
        Assert.Equal(1, fixture.Runner.StartCount);
    }

    private static async Task CancellationBeforeIntent()
    {
        var fixture = new Fixture("quick-cancel-before-intent");
        using var controller = fixture.Controller();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await controller.LaunchSecondaryAsync(null, cancellation.Token);
        Assert.Equal(QuickLaunchOutcome.Cancelled, result.Outcome);
        Assert.False(Directory.Exists(fixture.JournalDirectory));
        Assert.Equal(0, fixture.Runner.StartCount);
        var approved = await Enable(controller);
        using var beforeIntent = new CancellationTokenSource();
        fixture.Runtime.AfterVerify = beforeIntent.Cancel;
        result = await controller.LaunchSecondaryAsync(approved.Approval, beforeIntent.Token);
        Assert.Equal(QuickLaunchOutcome.Cancelled, result.Outcome);
        Assert.False(Directory.GetFiles(fixture.JournalDirectory, "*.intent.json").Any());
        Assert.Equal(0, fixture.Runner.StartCount);
    }

    private static async Task CancellationAfterDispatch()
    {
        var fixture = new Fixture("quick-cancel-after-dispatch");
        using var controller = fixture.Controller();
        await Enable(controller);
        using var cancellation = new CancellationTokenSource();
        var handle = fixture.EnqueueLaunch(completed: false);
        var launch = controller.LaunchSecondaryAsync(null, cancellation.Token);
        await handle.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Assert.False(launch.IsCompleted);
        Assert.Equal(0, handle.DisposeCount);
        Assert.Equal(0, Directory.GetFiles(fixture.JournalDirectory, "*.completed.json").Length);
        handle.Complete();
        var result = await launch;
        Assert.Equal(QuickLaunchOutcome.Launched, result.Outcome);
        Assert.Equal(false, result.Snapshot.CommandPending);
        Assert.Equal(1, handle.DisposeCount);
        Assert.Equal(1, fixture.Runner.StartCount);
    }

    private static async Task TimeoutRemainsUncertain()
    {
        var fixture = new Fixture("quick-timeout-pending");
        using var controller = fixture.Controller(TimeSpan.Zero);
        await Enable(controller);
        var handle = fixture.EnqueueLaunch(completed: false);
        var result = await controller.LaunchSecondaryAsync(null);
        Assert.Equal(QuickLaunchOutcome.Uncertain, result.Outcome);
        Assert.Equal(true, result.Snapshot.CommandPending);
        Assert.False(result.Snapshot.CanLaunch);
        Assert.Equal(0, handle.DisposeCount);
        Assert.Equal(0, Directory.GetFiles(fixture.JournalDirectory, "*.completed.json").Length);
        Assert.Equal(QuickLaunchOutcome.Uncertain, (await controller.LaunchSecondaryAsync(null)).Outcome);
        Assert.Equal(1, fixture.Runner.StartCount);
        handle.Complete();
        var reconciled = await controller.InspectQuickLaunchAsync();
        Assert.Equal(false, reconciled.Snapshot.CommandPending);
        Assert.Equal(1, Directory.GetFiles(fixture.JournalDirectory, "*.completed.json").Length);
        Assert.Equal(QuickLaunchOutcome.AlreadyRunning, (await controller.LaunchSecondaryAsync(null)).Outcome);
        Assert.Equal(1, fixture.Runner.StartCount);
    }

    private static async Task PostDispatchPolicyFailure()
    {
        var fixture = new Fixture("quick-post-dispatch-policy");
        using var controller = fixture.Controller();
        await Enable(controller);
        fixture.EnqueueLaunch(completed: true);
        fixture.Runner.AfterStart = () => fixture.BoxMetadata["OpenFilePath"] = ["C:\\outside\\*"];
        var result = await controller.LaunchSecondaryAsync(null);
        Assert.Equal(QuickLaunchOutcome.Uncertain, result.Outcome);
        Assert.Equal(true, result.Snapshot.CommandPending);
        Assert.False(result.Snapshot.CurrentConfigurationVerified);
        Assert.Equal(0, Directory.GetFiles(fixture.JournalDirectory, "*.completed.json").Length);
        fixture.BoxMetadata.Clear();
        fixture.Processes = [fixture.Process()];
        var reconciled = await controller.InspectQuickLaunchAsync();
        Assert.Equal(false, reconciled.Snapshot.CommandPending);
        Assert.Equal(1, Directory.GetFiles(fixture.JournalDirectory, "*.completed.json").Length);
    }

    private static async Task DamagedConsentRemainsOff()
    {
        var fixture = new Fixture("quick-damaged-consent");
        using var controller = fixture.Controller();
        await Enable(controller);
        var path = Directory.GetFiles(fixture.ConsentDirectory).Single();
        var original = File.ReadAllBytes(path);
        File.WriteAllText(path, "{broken");
        var broken = await controller.InspectQuickLaunchAsync();
        Assert.False(broken.ConsentMatches);
        Assert.True(broken.Approval is not null);
        Assert.Equal(QuickLaunchOutcome.ConfirmationRequired, (await controller.LaunchSecondaryAsync(null)).Outcome);
        Assert.Throws<InvalidDataException>(() => controller.SetQuickLaunchConsentAsync(broken.Approval!, false).GetAwaiter().GetResult());
        Assert.Equal("{broken", File.ReadAllText(path));
        File.WriteAllBytes(path, original);
        var incomplete = Path.Combine(fixture.ConsentDirectory, ".consent.fixture.tmp");
        File.WriteAllText(incomplete, "preserve interrupted fixture");
        var partial = await controller.InspectQuickLaunchAsync();
        Assert.False(partial.ConsentMatches);
        Assert.Throws<InvalidDataException>(() => controller.SetQuickLaunchConsentAsync(partial.Approval!, true).GetAwaiter().GetResult());
        Assert.Equal("preserve interrupted fixture", File.ReadAllText(incomplete));
        Assert.SequenceEqual(original, File.ReadAllBytes(path));
        Assert.Equal(0, fixture.Runner.StartCount);
    }

    private static async Task ExecutableVerificationFailsClosed()
    {
        var fixture = new Fixture("quick-signature-failure");
        using var controller = fixture.Controller();
        await Enable(controller);
        fixture.Runtime.AfterVerify = () => throw new InvalidDataException("synthetic executable signature rejection");
        var result = await controller.LaunchSecondaryAsync(null);
        Assert.Equal(QuickLaunchOutcome.Blocked, result.Outcome);
        Assert.False(result.Snapshot.ActionsPermitted);
        Assert.False(result.Snapshot.CanLaunch);
        Assert.True(result.Snapshot.CommandPending is null);
        Assert.Equal(0, fixture.Runner.StartCount);
        Assert.False(Directory.GetFiles(fixture.JournalDirectory, "*.intent.json").Any());
    }

    private static async Task RevocationWaitsForCommand()
    {
        var fixture = new Fixture("quick-revoke-concurrency");
        using var controller = fixture.Controller();
        var approved = await Enable(controller);
        var handle = fixture.EnqueueLaunch(completed: false);
        var launch = controller.LaunchSecondaryAsync(null);
        await handle.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var revoke = controller.SetQuickLaunchConsentAsync(approved.Approval!, false);
        Assert.False(revoke.IsCompleted);
        Assert.Equal(1, Directory.GetFiles(fixture.ConsentDirectory).Length);
        handle.Complete();
        Assert.Equal(QuickLaunchOutcome.Launched, (await launch).Outcome);
        Assert.False((await revoke).ConsentMatches);
        Assert.Equal(2, Directory.GetFiles(fixture.ConsentDirectory).Length);
        Assert.Equal(1, fixture.Runner.StartCount);
    }

    private static async Task<QuickLaunchStatus> Enable(SidetalkController controller)
    {
        var status = await controller.InspectQuickLaunchAsync();
        Assert.True(status.Approval is not null, status.Reason);
        var enabled = await controller.SetQuickLaunchConsentAsync(status.Approval!, true);
        Assert.True(enabled.ConsentMatches, enabled.Reason);
        return enabled;
    }

    private static Dictionary<string, string[]> Copy(IReadOnlyDictionary<string, string[]> values) =>
        values.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string[]> ReverseKeys(IReadOnlyDictionary<string, string[]> values) =>
        values.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);

    private sealed class Fixture
    {
        public Fixture(string name)
        {
            Root = TestContext.CreateRunDirectory(name);
            Store = new ProfileStore(Root);
            Profile = Store.Create();
            Runtime = new FakeRuntime(this);
            Runner = new FakeRunner(this);
        }
        private volatile IReadOnlyList<BoxProcess> processes = [];
        public string Root { get; }
        public ProfileStore Store { get; }
        public BoxProfile Profile { get; set; }
        public FakeRuntime Runtime { get; }
        public FakeRunner Runner { get; }
        public Dictionary<string, string[]> BoxMetadata { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string[]> Globals { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<BoxProcess> Processes { get => processes; set => processes = value; }
        public string ConsentDirectory => Path.Combine(Root, "quick-launch-consent");
        public string JournalDirectory => Path.Combine(Root, "command-journal");
        public string StartExe => Path.Combine(Root, "Start.exe");
        public BoxProcess Process() => new(14001, "KakaoTalk.exe", Profile.UserSid, 1);
        public ConfigurationLayers Configuration()
        {
            var box = IsolationPolicy.Expected(Profile);
            foreach (var pair in BoxMetadata) box[pair.Key] = pair.Value.ToArray();
            var globals = Copy(Globals);
            return new(Copy(box), Copy(box), globals, BuiltinTemplates.ExpectedGlobals(globals));
        }
        public FakeHandle EnqueueLaunch(bool completed)
        {
            var handle = new FakeHandle(new(14001, DateTime.UtcNow.Ticks, StartExe), completed);
            Runner.Handles.Enqueue(handle);
            Runner.AfterStart = () => Processes = [Process()];
            return handle;
        }
        public SidetalkController Controller(TimeSpan? timeout = null) =>
            new(Store, Runner, Runtime, new FakeRecovery(), timeout ?? TimeSpan.FromSeconds(30));
    }

    private sealed class FakeRuntime(Fixture fixture) : ISidetalkRuntime
    {
        public string KakaoPath { get; set; } = Path.Combine(fixture.Root, "KakaoTalk.exe");
        public string KakaoVersion { get; set; } = "26.7.1";
        public Action? AfterVerify { get; set; }
        public Func<ConfigurationLayers>? ConfigurationOverride { get; set; }
        public int VerifyCount { get; private set; }
        public DependencyInfo Kakao() => new("공식 카카오톡", KakaoPath, KakaoVersion, "synthetic", true);
        public DependencyInfo Sandboxie() => new("Sandboxie Plus", Path.Combine(fixture.Root, "SandMan.exe"), "1.18.4", "synthetic", true);
        public void RequireHost() { }
        public void VerifyCommandFiles(string startExe, string kakaoExe)
        {
            VerifyCount++;
            Assert.Equal(fixture.StartExe, startExe);
            Assert.Equal(KakaoPath, kakaoExe);
            AfterVerify?.Invoke();
        }
        public ISidetalkRuntimeSession Open(DependencyInfo sandboxie) => new Session(fixture, this);
        private sealed class Session(Fixture fixture, FakeRuntime runtime) : ISidetalkRuntimeSession
        {
            public RuntimeVerifier.ServiceVerification ServiceIdentity { get; } = new(14000, false, "synthetic SCM", "no native runtime accessed");
            public ConfigurationLayers ReadConfiguration(string boxName)
            {
                Assert.Equal(fixture.Profile.BoxName, boxName);
                return runtime.ConfigurationOverride?.Invoke() ?? fixture.Configuration();
            }
            public string FileRoot(string boxName) => fixture.Profile.StoragePath;
            public IReadOnlyList<BoxProcess> Processes(BoxProfile profile) { Assert.Equal(fixture.Profile, profile); return fixture.Processes; }
            public void Dispose() { }
        }
    }

    private sealed class FakeRunner(Fixture fixture) : ISidetalkCommandRunner
    {
        private int count;
        public int StartCount => Volatile.Read(ref count);
        public Queue<FakeHandle> Handles { get; } = new();
        public Action? AfterStart { get; set; }
        public ISidetalkCommandHandle Start(ProcessStartInfo command)
        {
            Interlocked.Increment(ref count);
            Assert.False(command.UseShellExecute);
            Assert.Equal(string.Empty, command.Arguments);
            Assert.SequenceEqual(SandboxieCommands.Launch(fixture.StartExe, fixture.Profile.BoxName, fixture.Runtime.KakaoPath).ArgumentList, command.ArgumentList);
            Assert.True(Directory.GetFiles(fixture.JournalDirectory, "*.intent.json").Length == 1, "Durable intent must precede fake process creation.");
            var handle = Handles.Dequeue();
            AfterStart?.Invoke();
            return handle;
        }
    }

    private sealed class FakeHandle : ISidetalkCommandHandle
    {
        private readonly TaskCompletionSource<bool> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int exited;
        private int disposed;
        public FakeHandle(CommandProcessIdentity identity, bool exited)
        {
            Identity = identity;
            if (exited) Complete();
        }
        public TaskCompletionSource<bool> WaitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CommandProcessIdentity? Identity { get; }
        public bool HasExited => Volatile.Read(ref exited) == 1;
        public int ExitCode => HasExited ? 0 : throw new InvalidOperationException("Synthetic command has not exited.");
        public int DisposeCount => Volatile.Read(ref disposed);
        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitEntered.TrySetResult(true);
            return completed.Task.WaitAsync(cancellationToken);
        }
        public void Complete() { Interlocked.Exchange(ref exited, 1); completed.TrySetResult(true); }
        public void Dispose() => Interlocked.Increment(ref disposed);
    }

    private sealed class FakeRecovery : ICommandProcessRecovery
    {
        public ISidetalkCommandHandle Recover(CommandProcessIdentity identity) => throw new InvalidOperationException("Unexpected fake restart recovery");
        public ISidetalkCommandHandle RecoverUnidentified(string expectedStartExe) => throw new InvalidOperationException("Unexpected fake unidentified recovery");
    }
}
