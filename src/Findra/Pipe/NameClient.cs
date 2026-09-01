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
    private volatile bool _pumpGone;
    private bool _disposed;

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
        ThrowIfPumpGone();

        long gen;
        TaskCompletionSource<QueryReply> tcs;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Stamp, register and write as one unit under the lock. Stamping outside it
            // bumps the generation for a query that may never reach the wire, and Accept
            // would then reject the genuinely-newest reply that did.
            gen = _gen.Next();
            tcs = new TaskCompletionSource<QueryReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[gen] = tcs;
            try
            {
                await Frame.WriteAsync(_transport, Envelope.Pack(Envelope.KindQuery,
                    new QueryRequest(gen, raw, max)), ct).ConfigureAwait(false);
            }
            catch { _pending.TryRemove(gen, out _); throw; }   // never reached the wire; do not leak it
        }
        finally { _writeLock.Release(); }

        // The pump may have died between the check above and the registration. Whichever
        // side loses that race, one of them sees the other: the pump's drain either finds
        // this entry, or this re-check finds the flag.
        if (_pumpGone) { _pending.TryRemove(gen, out _); ThrowIfPumpGone(); }

        QueryReply reply;
        try { reply = await tcs.Task.WaitAsync(ct).ConfigureAwait(false); }
        catch { _pending.TryRemove(gen, out _); throw; }

        return _gen.Accept(reply.Gen) ? reply : null;
    }

    public async Task<StatusReply> StatusAsync(CancellationToken ct)
    {
        ThrowIfPumpGone();

        var tcs = new TaskCompletionSource<StatusReply>(TaskCreationOptions.RunContinuationsAsynchronously);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Status replies carry no id, so waiters are matched positionally. Enqueue
            // inside the lock: enqueuing before it lets two concurrent callers queue in one
            // order and write in the other, and each then receives the other's reply.
            _statusWaiters.Enqueue(tcs);
            try
            {
                await Frame.WriteAsync(_transport, Envelope.Pack(Envelope.KindStatus, new StatusRequest()), ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                // A stranded waiter at the head of a positional queue desynchronises every
                // later status call permanently, so mark it dead rather than leaving it.
                // The pump skips dead entries when it dequeues; that is the other half.
                tcs.TrySetCanceled();
                throw;
            }
        }
        finally { _writeLock.Release(); }

        // Same race as SearchAsync: the pump may have died between the entry check and the
        // enqueue. Marking the waiter dead is enough - the pump's skip-on-dequeue discards it.
        if (_pumpGone) { tcs.TrySetCanceled(); ThrowIfPumpGone(); }

        return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private void ThrowIfPumpGone()
    {
        if (_pumpGone)
            throw new IOException("the name helper connection is closed");
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[]? payload = await Frame.ReadAsync(_transport, ct).ConfigureAwait(false);
                if (payload is null) break;

                // Guard decoding per frame. One undecodable reply must not end the pump:
                // the transport stays writable, so a dead reader turns every later search
                // into an await nobody will ever complete. The server guards its own
                // decode for the same reason - this is the matching half.
                Envelope e;
                try { e = Envelope.Unpack(payload); }
                catch (Exception ex) { Log.Warn("pipe", "undecodable frame from the helper: " + ex.Message); continue; }

                try
                {
                    switch (e.Kind)
                    {
                        case Envelope.KindQueryReply:
                        {
                            QueryReply r = e.Body<QueryReply>();
                            if (_pending.TryRemove(r.Gen, out var waiter)) waiter.TrySetResult(r);
                            break;
                        }
                        case Envelope.KindStatusReply:
                        {
                            // Status replies carry no id, so waiters match positionally -
                            // which means a dead entry at the head would swallow this reply
                            // and starve whoever queued behind it. TrySetResult returns
                            // false for an already-completed waiter, so walk past those.
                            StatusReply s = e.Body<StatusReply>();
                            while (_statusWaiters.TryDequeue(out var waiting))
                                if (waiting.TrySetResult(s)) break;
                            break;
                        }
                        case Envelope.KindJournal:
                            // Plan 3 hooks the indexer up here.
                            break;
                        default:
                            Log.Info("pipe", $"client ignoring unknown kind '{e.Kind}'");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    // Not just JsonException. Body<T> throws InvalidDataException for a body
                    // that decodes to null - the literal `null` in the Json field - and a
                    // JsonException-only guard lets that one shape through to kill the pump
                    // permanently, which is the failure this catch exists to prevent.
                    Log.Warn("pipe", $"undecodable body for '{e.Kind}': {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("pipe", "client pump ended", ex); }
        finally
        {
            // Set the flag BEFORE draining. A caller registering concurrently either has
            // its entry found by the drain below, or sees this flag on its own re-check -
            // one of the two always happens, so nobody is left awaiting a dead pump.
            _pumpGone = true;
            foreach (var kv in _pending) kv.Value.TrySetCanceled();
            while (_statusWaiters.TryDequeue(out var s)) s.TrySetCanceled();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _reader.CancelAsync().ConfigureAwait(false);
        try { await _pump.ConfigureAwait(false); } catch { }
        _transport.Dispose();
        _reader.Dispose();

        // _writeLock is deliberately NOT disposed. SemaphoreSlim.Dispose does not complete
        // queued async waiters - it neither resumes nor faults them - so disposing it while
        // a caller is parked on WaitAsync hangs that caller silently, and the Release in
        // its finally then throws ObjectDisposedException out of a finally block, masking
        // whatever it was really failing on. Nothing here allocates its wait handle, so
        // there is nothing to release.
    }
}
