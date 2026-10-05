using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Sidetalk.App;

internal sealed record InstanceRequest(Guid Id, bool QuickLaunch, bool LoginStartup);

/// <summary>Same-user/session transport. A request never carries launch authority.</summary>
internal sealed class InstanceRequestBroker : IDisposable
{
    private readonly string name;
    private readonly CancellationTokenSource stop = new();
    private Mutex? mutex;
    private Task? listener;
    private readonly Func<string, NamedPipeServerStream> createPipe;

    internal InstanceRequestBroker(string userAndSession, Func<string, NamedPipeServerStream>? createPipe = null)
    {
        name = "SIDETALK-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userAndSession)))[..32];
        this.createPipe = createPipe ?? (pipeName => new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly));
    }

    internal Task Completion => listener ?? Task.CompletedTask;

    internal bool TryAcquire()
    {
        mutex = new Mutex(true, "Local\\" + name, out var created);
        if (created) return true;
        mutex.Dispose();
        mutex = null;
        return false;
    }

    internal async Task ListenAsync(Func<InstanceRequest, Task> receive)
    {
        if (mutex is null || listener is not null) throw new InvalidOperationException("Single-instance ownership is required.");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listener = Task.Run(async () =>
        {
            try
            {
                // Native initialization must neither block the dispatcher nor be
                // swallowed as a malformed-client error in an immediate retry loop.
                using var pipe = createPipe(name);
                ready.SetResult();
                await ReceiveAsync(pipe, receive).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                ready.TrySetException(error);
                throw;
            }
        });
        try { await ready.Task.ConfigureAwait(false); }
        catch { await listener.ConfigureAwait(false); throw; }
    }

    private async Task ReceiveAsync(NamedPipeServerStream pipe, Func<InstanceRequest, Task> receive)
    {
        while (!stop.IsCancellationRequested)
        {
            try { await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            try
            {
                // Fixed 18-byte protocol: version, command, request identity. No
                // paths, process IDs, arbitrary arguments, or executable content.
                var bytes = new byte[18];
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                await pipe.ReadExactlyAsync(bytes, deadline.Token).ConfigureAwait(false);
                if (bytes[0] != 1 || bytes[1] > 2) continue;
                await receive(new(new Guid(bytes.AsSpan(2)), bytes[1] != 0, bytes[1] == 2)).ConfigureAwait(false);
                await pipe.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);
                await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
                var receipt = new byte[1];
                await pipe.ReadExactlyAsync(receipt, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                // A malformed/disconnected sender cannot dispatch work. The
                // existing visible process remains the only request owner.
            }
            finally
            {
                // Reuse the owned instance; recreating the first instance while a
                // previous client still holds its handle can fail with access denied.
                pipe.Disconnect();
            }
        }
    }

    internal async Task ForwardAsync(InstanceRequest request, CancellationToken cancellationToken = default)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(3000, cancellationToken).ConfigureAwait(false);
        var bytes = new byte[18];
        bytes[0] = 1;
        bytes[1] = request.LoginStartup ? (byte)2 : request.QuickLaunch ? (byte)1 : (byte)0;
        request.Id.TryWriteBytes(bytes.AsSpan(2));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        await pipe.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
        await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
        var response = new byte[1];
        await pipe.ReadExactlyAsync(response, deadline.Token).ConfigureAwait(false);
        if (response[0] != 1) throw new IOException("기존 SIDETALK가 요청을 확인하지 못했습니다.");
        // The server keeps its acknowledgement until the client consumed it.
        await pipe.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        stop.Cancel();
        mutex?.Dispose();
        mutex = null;
        // The async accept observes cancellation; no process-wide polling is used.
    }
}
