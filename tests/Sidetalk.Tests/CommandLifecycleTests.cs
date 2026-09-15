using System.Diagnostics;
using Sidetalk.Core;

namespace Sidetalk.Tests;

/// <summary>
/// Deterministic command lifecycle coverage. The parent test entry registers Run().
/// No Sandboxie installation or external process is used.
/// </summary>
public static class CommandLifecycleTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases { get; } =
    [
        ("Command lifecycle: completed launch without child can retry", CompletedLaunchWithoutChildCanRetry),
        ("Command lifecycle: immediate exit is observed without early disposal", ImmediateExitIsObservedWithoutDisposingEarly),
        ("Command lifecycle: runner seam feeds lifecycle", RunnerSeamFeedsLifecycle),
        ("Command lifecycle: child observation never claims login", LaunchWithKakaoChildIsObservedWithoutLoginClaim),
        ("Command lifecycle: timed out command blocks duplicate actions", TimedOutCommandBlocksDuplicateActions),
        ("Command lifecycle: unknown exit state remains distinct", UnknownExitStateRemainsDistinct),
        ("Command lifecycle: later exit refresh reconciles early child", LaterExitRefreshReconcilesEarlyChild),
        ("Command lifecycle: helper residual allows stop only", HelperOnlyResidualAllowsStopOnly),
        ("Command lifecycle: successful empty stop is ready", SuccessfulStopWithEmptyBoxIsReady),
        ("Command lifecycle: failed stop with residual allows retry", FailedStopWithResidualAllowsStopRetry),
        ("Command lifecycle: query failure retains completed command", QueryFailureRetainsCompletedCommand),
        ("Command lifecycle: double action is rejected", DoubleActionIsRejected),
        ("Command lifecycle: production inspection waits for exit before querying", InspectionWaitsForExit),
        ("Command lifecycle: production query failure is recoverable", InspectionQueryFailureRecovers),
        ("Command lifecycle: zero exit stop with residual is retryable", ZeroExitStopResidual),
        ("Command lifecycle: nonzero exit with empty box records actual state", NonzeroEmptyStop)
    ];

    public static void Run()
    {
        foreach (var test in Cases)
            test.Run();
    }

    private static void InspectionWaitsForExit()
    {
        var handle = new FakeHandle(exited: false, exitCode: 0);
        using var lifecycle = new CommandLifecycle();
        lifecycle.Begin(SidetalkCommandAction.Launch, handle);
        var queried = false;
        var before = lifecycle.InspectProcesses(() =>
        {
            queried = true;
            handle.Complete(0);
            return Array.Empty<BoxProcess>();
        });
        Assert.False(queried, "Never enumerate while command may still create a child.");
        Assert.True(before.State.IsPending);
        handle.Complete(0);
        var after = lifecycle.InspectProcesses(() =>
        {
            Assert.True(handle.HasExited, "Enumeration must start after the command exit was observed.");
            queried = true;
            return Array.Empty<BoxProcess>();
        });
        Assert.True(queried);
        Assert.Equal(CommandLifecycleOutcome.LaunchFailed, after.State.Outcome);
        Assert.False(after.State.IsPending);
    }

    private static void InspectionQueryFailureRecovers()
    {
        var handle = new FakeHandle(exited: true, exitCode: 0);
        using var lifecycle = new CommandLifecycle();
        lifecycle.Begin(SidetalkCommandAction.Launch, handle);
        Assert.Throws<InvalidOperationException>(() => lifecycle.InspectProcesses(() => throw new InvalidOperationException("query failed")));
        Assert.True(lifecycle.IsPending);
        Assert.Equal(0, handle.DisposeCount);
        var recovered = lifecycle.InspectProcesses(() => Array.Empty<BoxProcess>());
        Assert.False(recovered.State.IsPending);
        Assert.Equal(CommandLifecycleOutcome.LaunchFailed, recovered.State.Outcome);
    }

    private static void ZeroExitStopResidual()
    {
        using var lifecycle = new CommandLifecycle();
        lifecycle.Begin(SidetalkCommandAction.Stop, new FakeHandle(true, 0));
        var result = lifecycle.InspectProcesses(() => new[] { Process("KakaoTalk.exe", 128) });
        Assert.Equal(CommandLifecycleOutcome.StopResidual, result.State.Outcome);
        Assert.False(result.State.IsPending);
        Assert.Equal(1, result.Processes.Count);
        lifecycle.Begin(SidetalkCommandAction.Stop, new FakeHandle(true, 0));
        Assert.Equal(CommandLifecycleOutcome.StopSucceeded, lifecycle.InspectProcesses(() => Array.Empty<BoxProcess>()).State.Outcome);
    }

    private static void NonzeroEmptyStop()
    {
        using var lifecycle = new CommandLifecycle();
        lifecycle.Begin(SidetalkCommandAction.Stop, new FakeHandle(true, 5));
        var result = lifecycle.InspectProcesses(() => Array.Empty<BoxProcess>());
        Assert.Equal(CommandLifecycleOutcome.StopFailed, result.State.Outcome);
        Assert.False(result.State.IsPending);
        Assert.Equal(0, result.Processes.Count);
        Assert.Contains("다시 실행할 수", result.State.Message);
    }
    private static void CompletedLaunchWithoutChildCanRetry()
    {
        var handle = new FakeHandle(exited: true, exitCode: 0);
        using var lifecycle = new CommandLifecycle();
        lifecycle.Begin(SidetalkCommandAction.Launch, handle);

        var result = lifecycle.Observe(Array.Empty<BoxProcess>());

        Assert.Equal(CommandLifecycleOutcome.LaunchFailed, result.Outcome);
        Assert.False(result.IsPending);
        Assert.Equal(1, handle.DisposeCount);
    }

    private static void ImmediateExitIsObservedWithoutDisposingEarly()
    {
        var handle = new FakeHandle(exited: true, exitCode: 1);
        using var lifecycle = new CommandLifecycle();
        lifecycle.Begin(SidetalkCommandAction.Launch, handle);

        var wait = lifecycle.WaitForExitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();

        Assert.Equal(CommandWaitOutcome.Completed, wait);
        Assert.Equal(0, handle.DisposeCount);
        var result = lifecycle.Observe(Array.Empty<BoxProcess>());
        Assert.Equal(CommandLifecycleOutcome.LaunchFailed, result.Outcome);
        Assert.Equal(1, handle.DisposeCount);
    }

    private static void RunnerSeamFeedsLifecycle()
    {
        var handle = new FakeHandle(exited: true, exitCode: 0);
        var runner = new FakeRunner(handle);
        using var lifecycle = new CommandLifecycle();

        lifecycle.Begin(SidetalkCommandAction.Launch, runner.Start(new ProcessStartInfo("Start.exe")));

        Assert.Equal(1, runner.StartCount);
        Assert.Equal(CommandLifecycleOutcome.LaunchFailed, lifecycle.Observe(Array.Empty<BoxProcess>()).Outcome);
    }

    private static void LaunchWithKakaoChildIsObservedWithoutLoginClaim()
    {
        var handle = new FakeHandle(exited: true, exitCode: 0);
        using var lifecycle = new CommandLifecycle();
        lifecycle.Begin(SidetalkCommandAction.Launch, handle);

        var result = lifecycle.Observe([Process("KakaoTalk.exe", 120)]);

        Assert.Equal(CommandLifecycleOutcome.LaunchObserved, result.Outcome);
        Assert.True(result.HasKakao);
        Assert.False(result.Message.Contains("로그인 성공", StringComparison.Ordinal));
        Assert.Equal(1, handle.DisposeCount);
    }

    private static void TimedOutCommandBlocksDuplicateActions()
    {
        var handle = new FakeHandle(exited: false, exitCode: 0);
        using var lifecycle = new CommandLifecycle();
        lifecycle.Begin(SidetalkCommandAction.Launch, handle);

        var wait = lifecycle.WaitForExitAsync(TimeSpan.FromMilliseconds(30)).GetAwaiter().GetResult();

        Assert.Equal(CommandWaitOutcome.TimedOut, wait);
        Assert.True(lifecycle.IsPending);
        Assert.Equal(CommandLifecycleOutcome.WaitingForCommand, lifecycle.Observe([Process("KakaoTalk.exe", 121)]).Outcome);
        Assert.Equal(0, handle.DisposeCount);
        Assert.Throws<InvalidOperationException>(() => lifecycle.Begin(SidetalkCommandAction.Stop, new FakeHandle(false, 0)));
    }

    private static void UnknownExitStateRemainsDistinct()
    {
        using var lifecycle = new CommandLifecycle();
        lifecycle.Begin(SidetalkCommandAction.Launch, new UnknownExitHandle());

        var result = lifecycle.Observe(Array.Empty<BoxProcess>());

        Assert.Equal(CommandLifecycleOutcome.WaitingForCommand, result.Outcome);
        Assert.True(result.IsPending);
        Assert.Contains("종료 상태를 확인하지 못했습니다", result.Message);
    }

    private static void LaterExitRefreshReconcilesEarlyChild()
    {
        var handle = new FakeHandle(exited: false, exitCode: 0);
        using var lifecycle = new CommandLifecycle();
        lifecycle.Begin(SidetalkCommandAction.Launch, handle);

        var early = lifecycle.Observe([Process("KakaoTalk.exe", 122)]);
        Assert.Equal(CommandLifecycleOutcome.WaitingForCommand, early.Outcome);
        Assert.True(lifecycle.IsPending);

        handle.Complete(0);
        var completion = lifecycle.Observe(null);
        Assert.Equal(CommandLifecycleOutcome.WaitingForObservation, completion.Outcome);
        Assert.True(lifecycle.IsPending);
        var refreshed = lifecycle.Observe([Process("KakaoTalk.exe", 122)]);

        Assert.Equal(CommandLifecycleOutcome.LaunchObserved, refreshed.Outcome);
        Assert.False(lifecycle.IsPending);
        Assert.Equal(1, handle.DisposeCount);
    }

    private static void HelperOnlyResidualAllowsStopOnly()
    {
        var handle = new FakeHandle(exited: true, exitCode: 0);
        using var lifecycle = new CommandLifecycle();
        lifecycle.Begin(SidetalkCommandAction.Launch, handle);

        var result = lifecycle.Observe([Process("SbieSvc.exe", 123)]);

        Assert.Equal(CommandLifecycleOutcome.BoxOnlyResidual, result.Outcome);
        Assert.False(result.HasKakao);
        Assert.Equal(1, result.ProcessCount);
    }

    private static void SuccessfulStopWithEmptyBoxIsReady()
    {
        var handle = new FakeHandle(exited: true, exitCode: 0);
        using var lifecycle = new CommandLifecycle();
        lifecycle.Begin(SidetalkCommandAction.Stop, handle);

        var result = lifecycle.Observe(Array.Empty<BoxProcess>());

        Assert.Equal(CommandLifecycleOutcome.StopSucceeded, result.Outcome);
        Assert.False(result.IsPending);
        Assert.Equal(0, result.ProcessCount);
    }

    private static void FailedStopWithResidualAllowsStopRetry()
    {
        var handle = new FakeHandle(exited: true, exitCode: 5);
        using var lifecycle = new CommandLifecycle();
        lifecycle.Begin(SidetalkCommandAction.Stop, handle);

        var result = lifecycle.Observe([Process("SbieSvc.exe", 124)]);

        Assert.Equal(CommandLifecycleOutcome.StopFailed, result.Outcome);
        Assert.False(result.IsPending);
        Assert.Equal(1, result.ProcessCount);
        Assert.Equal(1, handle.DisposeCount);
    }

    private static void QueryFailureRetainsCompletedCommand()
    {
        var handle = new FakeHandle(exited: true, exitCode: 0);
        using var lifecycle = new CommandLifecycle();
        lifecycle.Begin(SidetalkCommandAction.Launch, handle);

        var unavailable = lifecycle.Observe(null);

        Assert.Equal(CommandLifecycleOutcome.WaitingForObservation, unavailable.Outcome);
        Assert.True(unavailable.IsPending);
        Assert.Equal(0, handle.DisposeCount);

        var recovered = lifecycle.Observe(Array.Empty<BoxProcess>());
        Assert.Equal(CommandLifecycleOutcome.LaunchFailed, recovered.Outcome);
        Assert.False(lifecycle.IsPending);
    }

    private static void DoubleActionIsRejected()
    {
        var first = new FakeHandle(exited: false, exitCode: 0);
        using var lifecycle = new CommandLifecycle();
        lifecycle.Begin(SidetalkCommandAction.Stop, first);

        Assert.Throws<InvalidOperationException>(() => lifecycle.Begin(SidetalkCommandAction.Launch, new FakeHandle(false, 0)));
        lifecycle.Observe(null);
        Assert.True(lifecycle.IsPending);
        Assert.Equal(0, first.DisposeCount);
    }

    private static BoxProcess Process(string imageName, int id) =>
        new(id, imageName, "test-user", 1);

    private sealed class UnknownExitHandle : ISidetalkCommandHandle
    {
        public bool HasExited => throw new InvalidOperationException("query unavailable");
        public int ExitCode => throw new InvalidOperationException("query unavailable");
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class FakeRunner(ISidetalkCommandHandle handle) : ISidetalkCommandRunner
    {
        public int StartCount { get; private set; }

        public ISidetalkCommandHandle Start(ProcessStartInfo command)
        {
            StartCount++;
            return handle;
        }
    }

    private sealed class FakeHandle(bool exited, int exitCode) : ISidetalkCommandHandle
    {
        private bool exited = exited;
        private int exitCode = exitCode;
        public int DisposeCount { get; private set; }

        public bool HasExited => exited;
        public int ExitCode => exitCode;

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            while (!exited)
                await Task.Delay(1, cancellationToken);
        }

        public void Complete(int code)
        {
            exitCode = code;
            exited = true;
        }

        public void Dispose() => DisposeCount++;
    }
}
