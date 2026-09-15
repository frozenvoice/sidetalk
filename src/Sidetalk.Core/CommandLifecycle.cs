using System.Diagnostics;

namespace Sidetalk.Core;

/// <summary>The operation sent to Sandboxie Start.exe.</summary>
public enum SidetalkCommandAction
{
    Launch,
    Stop
}

/// <summary>A process handle retained until its exit and the box state are known.</summary>
public interface ISidetalkCommandHandle : IDisposable
{
    bool HasExited { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

/// <summary>Starts a Sandboxie command. The seam keeps lifecycle tests process-free.</summary>
public interface ISidetalkCommandRunner
{
    ISidetalkCommandHandle Start(ProcessStartInfo command);
}

internal sealed class ProcessCommandRunner : ISidetalkCommandRunner
{
    public ISidetalkCommandHandle Start(ProcessStartInfo command)
    {
        var process = Process.Start(command) ?? throw new InvalidOperationException("Sandboxie 명령을 시작하지 못했습니다.");
        return new ProcessCommandHandle(process);
    }

    private sealed class ProcessCommandHandle(Process process) : ISidetalkCommandHandle
    {
        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public void Dispose() => process.Dispose();
    }
}

public enum CommandLifecycleOutcome
{
    Idle,
    WaitingForCommand,
    WaitingForObservation,
    LaunchObserved,
    LaunchFailed,
    BoxOnlyResidual,
    StopSucceeded,
    StopResidual,
    StopFailed
}

public enum CommandWaitOutcome
{
    Completed,
    TimedOut,
    ExitUnknown
}

public sealed record CommandLifecycleResult(
    CommandLifecycleOutcome Outcome,
    SidetalkCommandAction? Action,
    int? ExitCode,
    bool IsPending,
    bool HasKakao,
    int ProcessCount,
    string Message);

/// <summary>
/// Tracks a Start.exe request separately from the process state it is expected to change.
/// A command is finalized only after it has exited and a fresh, validated box enumeration
/// has succeeded. It never terminates a command or a box process itself.
/// </summary>
public sealed class CommandLifecycle : IDisposable
{
    private PendingCommand? pending;
    private CommandLifecycleResult lastResult = IdleResult();

    public bool IsPending => pending is not null;
    public SidetalkCommandAction? PendingAction => pending?.Action;
    public CommandLifecycleResult LastResult => lastResult;

    public void Begin(SidetalkCommandAction action, ISidetalkCommandHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (pending is not null)
            throw new InvalidOperationException("Sandboxie 명령 결과를 확인하는 중에는 다른 명령을 실행할 수 없습니다.");

        pending = new PendingCommand(action, handle);
        lastResult = new(CommandLifecycleOutcome.WaitingForCommand, action, null, true, false, 0,
            "Sandboxie 명령 결과를 확인하고 있습니다.");
    }

    public async Task<CommandWaitOutcome> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var current = pending ?? throw new InvalidOperationException("대기할 Sandboxie 명령이 없습니다.");
        if (ReadExit(current.Handle, out _) == CommandExitState.Exited)
            return CommandWaitOutcome.Completed;

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await current.Handle.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return CommandWaitOutcome.TimedOut;
        }

        return ReadExit(current.Handle, out _) == CommandExitState.Exited
            ? CommandWaitOutcome.Completed
            : CommandWaitOutcome.ExitUnknown;
    }

    /// <summary>
    /// Reconciles a completed command with a fresh validated enumeration. Passing null means
    /// enumeration was unavailable; the retained command remains pending in that case.
    /// </summary>
    public (CommandLifecycleResult State, IReadOnlyList<BoxProcess> Processes) InspectProcesses(Func<IReadOnlyList<BoxProcess>> query)
    {
        // This guard and enumeration are one production path, also used by regression
        // tests. A running/unknown command must never authorize a transitional sample.
        var before = Observe(null);
        if (before.IsPending && before.Outcome == CommandLifecycleOutcome.WaitingForCommand)
            return (before, Array.Empty<BoxProcess>());
        var processes = query();
        return (Observe(processes), processes);
    }
    public CommandLifecycleResult Observe(IReadOnlyList<BoxProcess>? processes)
    {
        var current = pending;
        if (current is null)
            return lastResult = IdleResult();

        var exitState = ReadExit(current.Handle, out var exitCode);
        if (exitState == CommandExitState.Running)
            return lastResult = new(CommandLifecycleOutcome.WaitingForCommand, current.Action, null, true, false, 0,
                "Sandboxie 명령이 아직 실행 중입니다. 명령을 중복 실행하지 않습니다.");
        if (exitState == CommandExitState.Unknown)
            return lastResult = new(CommandLifecycleOutcome.WaitingForCommand, current.Action, null, true, false, 0,
                "Sandboxie 명령의 종료 상태를 확인하지 못했습니다. 새로고침으로 다시 확인하세요. 명령을 중복 실행하지 않습니다.");

        if (processes is null)
            return lastResult = new(CommandLifecycleOutcome.WaitingForObservation, current.Action, exitCode, true, false, 0,
                "명령은 끝났지만 전용 환경의 검증된 프로세스 상태를 확인하지 못했습니다. 새로고침하세요.");

        var hasKakao = processes.Any(IsKakao);
        var hasResidual = processes.Count > 0;
        var result = current.Action switch
        {
            SidetalkCommandAction.Launch when hasKakao => new CommandLifecycleResult(CommandLifecycleOutcome.LaunchObserved, current.Action, exitCode, false, true, processes.Count,
                "보조 카카오톡 프로세스가 전용 환경에서 확인되었습니다. 로그인 상태는 창에서 직접 확인하세요."),
            SidetalkCommandAction.Launch when hasResidual => new CommandLifecycleResult(CommandLifecycleOutcome.BoxOnlyResidual, current.Action, exitCode, false, false, processes.Count,
                "카카오톡은 시작되지 않았지만 전용 환경의 보조 프로세스가 남아 있습니다. 새 실행은 막고 전용 상자 종료만 허용합니다."),
            SidetalkCommandAction.Launch => new CommandLifecycleResult(CommandLifecycleOutcome.LaunchFailed, current.Action, exitCode, false, false, 0,
                "Sandboxie 명령은 끝났지만 보조 카카오톡 프로세스가 확인되지 않았습니다. 실행을 다시 시도할 수 있습니다."),
            SidetalkCommandAction.Stop when exitCode == 0 && !hasResidual => new CommandLifecycleResult(CommandLifecycleOutcome.StopSucceeded, current.Action, exitCode, false, false, 0,
                "전용 상자가 비어 있어 종료가 확인되었습니다. 저장 데이터는 삭제하지 않았습니다."),
            SidetalkCommandAction.Stop when exitCode == 0 => new CommandLifecycleResult(CommandLifecycleOutcome.StopResidual, current.Action, exitCode, false, hasKakao, processes.Count,
                "종료 명령은 끝났지만 전용 환경 프로세스가 남아 있습니다. 보조 종료를 다시 시도하세요."),
            _ => new CommandLifecycleResult(CommandLifecycleOutcome.StopFailed, current.Action, exitCode, false, hasKakao, processes.Count,
                hasResidual ? "전용 상자 종료 명령이 실패했고 프로세스가 남아 있습니다. 보조 종료를 다시 시도하세요." : "종료 명령은 오류를 반환했지만 전용 환경이 비어 있는 것을 확인했습니다. 다시 실행할 수 있습니다.")
        };

        // The command has exited and the process enumeration was validated. Only now is it
        // safe to release the retained OS handle. No process termination occurs here.
        try
        {
            current.Handle.Dispose();
        }
        catch (Exception error)
        {
            return lastResult = new(CommandLifecycleOutcome.WaitingForObservation, current.Action, exitCode, true, hasKakao, processes.Count,
                $"명령 핸들을 정리하지 못했습니다. 새로고침하세요. ({error.Message})");
        }

        pending = null;
        return lastResult = result;
    }

    public void Dispose()
    {
        // Disposing the OS process handle never terminates the process. During normal
        // operation a live handle is retained for refresh/reconcile; when the app itself
        // closes, releasing it is safe and leaves the Sandboxie command untouched.
        var current = pending;
        pending = null;
        if (current is not null)
            current.Handle.Dispose();
    }

    private static bool IsKakao(BoxProcess process) =>
        process.ImageName.Equals("KakaoTalk.exe", StringComparison.OrdinalIgnoreCase);

    private static CommandExitState ReadExit(ISidetalkCommandHandle handle, out int exitCode)
    {
        exitCode = 0;
        try
        {
            if (!handle.HasExited)
                return CommandExitState.Running;
            exitCode = handle.ExitCode;
            return CommandExitState.Exited;
        }
        catch
        {
            return CommandExitState.Unknown;
        }
    }

    private enum CommandExitState
    {
        Running,
        Exited,
        Unknown
    }

    private static CommandLifecycleResult IdleResult() =>
        new(CommandLifecycleOutcome.Idle, null, null, false, false, 0, string.Empty);

    private sealed record PendingCommand(SidetalkCommandAction Action, ISidetalkCommandHandle Handle);
}
