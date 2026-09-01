using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Findra;
using Xunit;

// The card's staleness decision and the queue that keeps the wire order equal to the local order.
// Both were extracted out of CardWindow for this: the window needs a display and cannot be
// constructed in a test, but these two carry the whole of the race it was getting wrong.
public class SearchGateTests
{
    // ---- the gate ----

    [Fact]
    public void TheNewestSearchWithAnAnswerIsApplied()
    {
        var gate = new SearchGate();
        int gen = gate.Issue();

        Assert.True(gate.MayApply(gen, replyIsNull: false, tokenCancelled: false));
    }

    [Fact]
    public void AnAnswerTheWireDroppedIsNotApplied()
    {
        var gate = new SearchGate();
        int gen = gate.Issue();

        Assert.False(gate.MayApply(gen, replyIsNull: true, tokenCancelled: false));
    }

    [Fact]
    public void AnAnswerTheDebounceCancelledIsNotApplied()
    {
        var gate = new SearchGate();
        int gen = gate.Issue();

        Assert.False(gate.MayApply(gen, replyIsNull: false, tokenCancelled: true));
    }

    [Fact]
    public void AnOlderSearchIsNotAppliedOnceANewerOneHasBeenIssued()
    {
        var gate = new SearchGate();
        int older = gate.Issue();
        gate.Issue();

        Assert.False(gate.MayApply(older, replyIsNull: false, tokenCancelled: false));
        Assert.False(gate.IsNewest(older));
    }

    [Fact]
    public void ClearingTheFieldAbandonsWhateverIsInFlight()
    {
        // SetQuery("") writes no query at all, so the wire's gate can never see it. The abandoned
        // generation is the only thing that stops the old text's answer painting over an empty
        // card, and it is checked on the UI thread.
        var gate = new SearchGate();
        int inFlight = gate.Issue();

        gate.Abandon();

        Assert.False(gate.MayApply(inFlight, replyIsNull: false, tokenCancelled: false));
    }

    [Fact]
    public void TheInversionInterleavingLeavesExactlyOneAnswerApplicable()
    {
        // The review's interleaving, replayed. Local G then G+1; the wire numbers them inverted,
        // so G's reply arrives with the wire's blessing (not null) but its own token cancelled,
        // and G+1's reply is refused by the wire (null). Under the old code both were dropped and
        // nothing painted. The fix is upstream - issuance is ordered - but the gate still has to
        // say plainly that neither of these two may be applied, and that the newer one is the one
        // still owed an answer.
        var gate = new SearchGate();
        int g = gate.Issue();
        int gPlus1 = gate.Issue();

        Assert.False(gate.MayApply(g, replyIsNull: false, tokenCancelled: true));
        Assert.False(gate.MayApply(gPlus1, replyIsNull: true, tokenCancelled: false));

        // ...and the newer one is the search the card is still waiting on, so it is the one that
        // has to take the indicator down rather than leaving it spinning.
        Assert.False(gate.IsNewest(g));
        Assert.True(gate.IsNewest(gPlus1));
    }

    [Fact]
    public void OrderedIssuanceMeansTheNewestSearchAlwaysHasAnApplicableAnswer()
    {
        // With issuance ordered, the wire cannot refuse the newest query: it carries the highest
        // wire generation there is. So the newest search always reaches the paint, whatever
        // happened to the ones behind it - which is what makes dropping the others safe.
        var gate = new SearchGate();
        gate.Issue();
        gate.Issue();
        int newest = gate.Issue();

        Assert.True(gate.MayApply(newest, replyIsNull: false, tokenCancelled: false));
    }

    // ---- the queue ----

    [Fact]
    public async Task OnlyOneSearchIsOnTheWireAtATimeAndItIsTheOldestStillOwed()
    {
        // This is the fix for the inversion itself. Three searches are handed over back to back;
        // the second may not reach the wire until the first has been answered, and the third not
        // until the second has - so the order they are numbered on the wire is the order they were
        // queued in, which is the order the card numbered them locally.
        var queue = new SearchIssueQueue();
        var started = new List<int>();
        var gates = new[]
        {
            new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        var tasks = new Task<int>[3];
        for (int i = 0; i < 3; i++)
        {
            int mine = i;
            tasks[i] = queue.Enqueue(() =>
            {
                lock (started) started.Add(mine);
                return gates[mine].Task;
            });
        }

        for (int i = 0; i < 3; i++)
        {
            await WaitForCountAsync(started, i + 1);
            lock (started) Assert.Equal(i + 1, started.Count);   // and no further: one at a time
            gates[i].SetResult(i);
        }

        await Task.WhenAll(tasks);
        Assert.Equal(new[] { 0, 1, 2 }, started);
    }

    [Fact]
    public async Task ANoticeablySlowerFirstSearchStillReachesTheWireFirst()
    {
        // The shape the review measured: two searches queued microseconds apart (a debounce tick
        // followed by Ctrl+2), where the second would otherwise win the write lock. Repeated,
        // because the defect it replaces reproduced only a few times in a hundred.
        for (int round = 0; round < 200; round++)
        {
            var queue = new SearchIssueQueue();
            var order = new List<int>();
            Task<int> first = queue.Enqueue(async () =>
            {
                await Task.Yield();
                lock (order) order.Add(1);
                return 1;
            });
            Task<int> second = queue.Enqueue(() =>
            {
                lock (order) order.Add(2);
                return Task.FromResult(2);
            });

            await Task.WhenAll(first, second);
            Assert.Equal(new[] { 1, 2 }, order);
        }
    }

    [Fact]
    public async Task AFailedSearchDoesNotFailTheOnesQueuedBehindIt()
    {
        // A helper that goes away throws out of one search. The chain has to carry completion
        // forward, never the fault, or the first failure would poison every later search.
        var queue = new SearchIssueQueue();

        Task<int> bad = queue.Enqueue<int>(() => Task.FromException<int>(new InvalidOperationException("gone")));
        Task<int> good = queue.Enqueue(() => Task.FromResult(7));

        await Assert.ThrowsAsync<InvalidOperationException>(() => bad);
        Assert.Equal(7, await good);
    }

    private static async Task WaitForCountAsync(List<int> list, int count)
    {
        for (int i = 0; i < 500; i++)
        {
            lock (list) if (list.Count >= count) return;
            await Task.Delay(2);
        }
        lock (list) Assert.Equal(count, list.Count);
    }
}
