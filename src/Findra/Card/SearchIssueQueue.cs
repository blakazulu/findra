using System;
using System.Threading;
using System.Threading.Tasks;

namespace Findra;

/// <summary>
/// One at a time, in the order they were handed over.
///
/// This exists because the card stamps a search's generation on the UI thread but the pipe client
/// stamps the WIRE generation much later, inside its own write lock, on a pool thread. Nothing
/// made those two orders agree: two searches issued back to back could reach the write lock
/// inverted, and then BOTH answers were thrown away - the older one by its own cancelled token,
/// the newer one by the wire's generation gate, which had already seen a higher number. The card
/// kept the previous rows with the "searching" indicator still up, and only another keystroke
/// could clear it.
///
/// Serialising the round trips is what makes the two orders one order. It costs nothing measurable
/// because the helper's session loop already answers one frame at a time on a connection: it reads
/// a request, writes its reply, and only then reads the next. Overlapping requests were never
/// answered in parallel - they were only ever a way for the numbering to disagree.
/// </summary>
public sealed class SearchIssueQueue
{
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    /// <summary>Run <paramref name="work"/> after everything already queued. Call this from the
    /// thread that decides the order - for the card, the UI thread, in the same breath as the
    /// generation stamp - because it is the ENQUEUE order that becomes the wire order.</summary>
    public Task<T> Enqueue<T>(Func<Task<T>> work)
    {
        lock (_gate)
        {
            // TaskScheduler.Default and no ExecuteSynchronously: the work must not run on
            // whichever thread queued it, which is the UI thread.
            Task<T> next = _tail.ContinueWith(_ => work(), CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();

            // The tail carries only completion, never a fault. Chaining `next` itself would make
            // one failed search fail every later one, and would leave the fault unobserved on the
            // day nobody awaits the returned task.
            _tail = next.ContinueWith(static t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);

            return next;
        }
    }
}
