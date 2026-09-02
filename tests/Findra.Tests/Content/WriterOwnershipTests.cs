using Findra;
using Xunit;

/// <summary>
/// The writer connection is owned by one flow at a time, and that has to be enforced rather than
/// remembered. The failure it prevents is a measured one, not a theory: two flows share one
/// connection, both open a transaction, the provider refuses the second, the catch swallows it,
/// and the batch of changes has already been taken out of its queue - so those files stay stale
/// for good, and the log says so once and then never again.
/// </summary>
public sealed class WriterOwnershipTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-owner-" + Guid.NewGuid().ToString("N"));

    private ContentDb Open()
    {
        Directory.CreateDirectory(_dir);
        return new ContentDb(Path.Combine(_dir, "search.db"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void ASecondFlowInsideTheWriterFailsAtTheCallThatBrokeTheRule()
    {
        // Loudly, immediately, and at the offending call - not four layers down as a nested
        // transaction the next catch swallows.
        using ContentDb db = Open();

        using ContentDb.Scope tx = db.Begin();
        db.Enqueue("C", 1, @"C:\a.pdf", ResultKind.Document, "new", tx);

        Exception? fromTheOtherThread = null;
        var other = new Thread(() =>
        {
            try { db.Enqueue("C", 2, @"C:\b.pdf", ResultKind.Document, "new"); }
            catch (Exception ex) { fromTheOtherThread = ex; }
        });
        other.Start();
        Assert.True(other.Join(TimeSpan.FromSeconds(10)), "the second thread must not block on the first");

        // The message has to name the RULE, not a symptom. Without the guard this call still
        // throws - the provider refuses a command that is not on the connection's active
        // transaction - but it throws InvalidOperationException with a sentence about commands and
        // transactions, which is exactly what the ported engine's catch swallowed as noise. The
        // difference between a bug that took months to find and one that is obvious is whether the
        // exception says "two flows are inside this writer" or "wrong transaction".
        Assert.IsType<InvalidOperationException>(fromTheOtherThread);
        Assert.Contains("One flow owns the writer connection", fromTheOtherThread!.Message, StringComparison.Ordinal);
        Assert.Contains("Enqueue", fromTheOtherThread.Message, StringComparison.Ordinal);

        tx.Commit();
    }

    [Fact]
    public void ASecondFlowCannotOpenATransactionOfItsOwnEither()
    {
        // The ingredient that did the damage. Left to the provider this is a nested-transaction
        // exception from inside somebody's catch; here it names the rule that was broken.
        using ContentDb db = Open();
        using ContentDb.Scope tx = db.Begin();

        Exception? fromTheOtherThread = null;
        var other = new Thread(() =>
        {
            try { using ContentDb.Scope theirs = db.Begin(); }
            catch (Exception ex) { fromTheOtherThread = ex; }
        });
        other.Start();
        other.Join(TimeSpan.FromSeconds(10));

        // Same reason as above: the provider's own nested-transaction refusal is an
        // InvalidOperationException too, so only the sentence tells the two apart.
        Assert.IsType<InvalidOperationException>(fromTheOtherThread);
        Assert.Contains("One flow owns the writer connection", fromTheOtherThread!.Message, StringComparison.Ordinal);
        Assert.Contains("Begin", fromTheOtherThread.Message, StringComparison.Ordinal);
        tx.Commit();
    }

    [Fact]
    public void TheWriterMovesBetweenThreadsWhenNothingIsInFlight()
    {
        // The rule is ONE FLOW AT A TIME, not one thread ever. The loop that owns this connection
        // is async: it awaits a pipe session and a delay with ConfigureAwait(false), so its
        // continuations resume on whatever thread-pool thread is free and the owning thread id
        // changes several times a minute in ordinary running. An ownership check pinned to the
        // thread that constructed the writer would fire on the first await and never stop.
        using ContentDb db = Open();

        using (ContentDb.Scope tx = db.Begin())
        {
            db.Enqueue("C", 1, @"C:\a.pdf", ResultKind.Document, "new", tx);
            tx.Commit();
        }

        Exception? failed = null;
        var next = new Thread(() =>
        {
            try
            {
                using ContentDb.Scope tx = db.Begin();
                db.Enqueue("C", 2, @"C:\b.pdf", ResultKind.Document, "new", tx);
                tx.Commit();
            }
            catch (Exception ex) { failed = ex; }
        });
        next.Start();
        next.Join(TimeSpan.FromSeconds(10));

        Assert.Null(failed);
        Assert.Equal(2, db.PendingCount());
    }

    [Fact]
    public void TheOwningFlowCanNestAsDeeplyAsItLikes()
    {
        // Every write inside a transaction is a call into the writer while the writer is already
        // claimed by the same flow. A guard that could not tell re-entry from intrusion would
        // make the ordinary path throw.
        using ContentDb db = Open();
        using ContentDb.Scope tx = db.Begin();

        db.Enqueue("C", 1, @"C:\a.pdf", ResultKind.Document, "new", tx);
        db.SetUsnPosition('C', 0xBEEF, 4242, tx);
        db.SetWalkOwed('C', tx);
        tx.Commit();

        Assert.Equal(1, db.PendingCount());
    }

    [Fact]
    public void AReadOnlyConnectionIsNotClaimedByAnybody()
    {
        // The card reads through its own read-only connection from Task.Run bodies, so more than
        // one thread legitimately touches it over a session. The rule is about the WRITER, and
        // arming it on a connection that cannot write would break the card for no gain.
        string path;
        using (ContentDb writer = Open())
        {
            path = writer.Path;
            writer.Enqueue("C", 1, @"C:\a.pdf", ResultKind.Document, "new");
        }

        using var reader = new ContentDb(path, readOnly: true);
        long fromHere = reader.PendingCount();
        long fromThere = 0;
        var other = new Thread(() => fromThere = reader.PendingCount());
        other.Start();
        other.Join(TimeSpan.FromSeconds(10));

        Assert.Equal(1, fromHere);
        Assert.Equal(1, fromThere);
    }
}
