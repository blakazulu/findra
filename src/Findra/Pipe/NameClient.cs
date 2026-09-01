using System.Collections.Concurrent;
using System.IO.Pipes;

namespace Findra.Pipe;

/// <summary>
/// The normal-integrity half. Every call is async - name search is a round trip,
/// never an in-RAM lookup, and pretending otherwise deadlocks the UI thread.
/// </summary>
public sealed class NameClient : IAsyncDisposable
{
    private readonly Stream _transport;
    private readonly Generation _gen = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<QueryReply>> _pending = new();
    private readonly ConcurrentQueue<TaskCompletionSource<StatusReply>> _statusWaiters = new();
    private readonly CancellationTokenSource _reader = new();
    private readonly Task _pump;

    public long CurrentGeneration => _gen.Current;

    public NameClient(Stream transport)
    {
        _transport = transport;
        _pump = Task.Run(() => PumpAsync(_reader.Token));
    }

    public static async Task<NameClient> ConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        // CurrentUserOnly is the client half of the squatting defence. The server sets
        // FirstPipeInstance so nobody can take the name first; this makes the client verify
        // the server is running as the same user before it trusts a single path it returns.
        // Without it, a pipe from another account could feed fabricated results into the
        // card, and a click would launch them as this user.
        var pipe = new NamedPipeClientStream(".", NameServer.PipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct).ConfigureAwait(false);
        return new NameClient(pipe);
    }

    /// <summary>Null means the answer arrived after a newer query had been issued.</summary>
    public async Task<QueryReply?> SearchAsync(string raw, int max, CancellationToken ct)
    {
        long gen = _gen.Next();
        var tcs = new TaskCompletionSource<QueryReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[gen] = tcs;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Frame.WriteAsync(_transport, Envelope.Pack(Envelope.KindQuery,
                new QueryRequest(gen, raw, max)), ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }

        QueryReply reply = await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        return _gen.Accept(reply.Gen) ? reply : null;
    }

    public async Task<StatusReply> StatusAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<StatusReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _statusWaiters.Enqueue(tcs);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Frame.WriteAsync(_transport, Envelope.Pack(Envelope.KindStatus, new StatusRequest()), ct)
                .ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }

        return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[]? payload = await Frame.ReadAsync(_transport, ct).ConfigureAwait(false);
                if (payload is null) break;

                Envelope e = Envelope.Unpack(payload);
                switch (e.Kind)
                {
                    case Envelope.KindQueryReply:
                    {
                        QueryReply r = e.Body<QueryReply>();
                        if (_pending.TryRemove(r.Gen, out var waiter)) waiter.TrySetResult(r);
                        break;
                    }
                    case Envelope.KindStatusReply:
                        if (_statusWaiters.TryDequeue(out var s)) s.TrySetResult(e.Body<StatusReply>());
                        break;
                    case Envelope.KindJournal:
                        // Plan 3 hooks the indexer up here.
                        break;
                    default:
                        Log.Info("pipe", $"client ignoring unknown kind '{e.Kind}'");
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("pipe", "client pump ended", ex); }
        finally
        {
            foreach (var kv in _pending) kv.Value.TrySetCanceled();
            while (_statusWaiters.TryDequeue(out var s)) s.TrySetCanceled();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _reader.CancelAsync().ConfigureAwait(false);
        try { await _pump.ConfigureAwait(false); } catch { }
        _transport.Dispose();
        _reader.Dispose();
        _writeLock.Dispose();
    }
}
