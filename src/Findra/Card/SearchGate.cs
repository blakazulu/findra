using System;
using System.Threading;

namespace Findra;

/// <summary>
/// Which answer is allowed to paint the card, as one decision with no window around it.
///
/// A search is a round trip, so three different races can hand back an answer nobody wants any
/// more, and each has its own evidence:
/// <list type="bullet">
/// <item>the WIRE dropped it - a newer query had already been written when this reply landed, so
/// the pipe client's own generation gate returned null;</item>
/// <item>the DEBOUNCE dropped it - the user typed again while this request was out, so this
/// search's own token was cancelled by the newer one;</item>
/// <item>the UI THREAD dropped it - the post runs later still, and by then the newest generation
/// has moved on.</item>
/// </list>
///
/// The generation itself lives here rather than in a loose int field so that "issue" and
/// "abandon" are named operations a test can drive: the window they belong to cannot be
/// constructed without a display, but this can.
/// </summary>
public sealed class SearchGate
{
    private int _newest;

    /// <summary>The newest generation issued or abandoned. Nothing older may be applied.</summary>
    public int Newest => Volatile.Read(ref _newest);

    /// <summary>Stamp a search about to be run. The caller carries the number it gets back all
    /// the way to <see cref="MayApply"/>.</summary>
    public int Issue() => Interlocked.Increment(ref _newest);

    /// <summary>Give up on everything in flight without issuing anything - the field was cleared,
    /// so no answer to the old text may be painted over an empty card.</summary>
    public int Abandon() => Interlocked.Increment(ref _newest);

    /// <summary>True only for the newest generation, and only when it really has an answer.
    /// <paramref name="replyIsNull"/> is the wire's verdict, <paramref name="tokenCancelled"/>
    /// the debounce's, and the comparison against <see cref="Newest"/> is the UI thread's.</summary>
    public bool MayApply(int localGen, bool replyIsNull, bool tokenCancelled)
    {
        if (replyIsNull) return false;
        if (tokenCancelled) return false;
        return localGen == Newest;
    }

    /// <summary>Is this search still the one the card is waiting for? The card can drop an answer
    /// and still be the newest search there is (a helper that went away, say), and then nothing
    /// else is coming: whoever asked has to take the "searching" indicator down itself.</summary>
    public bool IsNewest(int localGen) => localGen == Newest;
}
