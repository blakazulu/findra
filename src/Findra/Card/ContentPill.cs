namespace Findra;

/// <summary>The three things a press on the Content pill can mean.</summary>
public enum ContentPress
{
    /// <summary>Flip the pill and run the query again. The ordinary answer, and the only one
    /// releasing the pill ever gives.</summary>
    Search,

    /// <summary>Flip the pill, and turn reading inside files back on while we are here. There is
    /// already something in the index to search, so this is the question being answered rather
    /// than a detour away from it.</summary>
    TurnOnReading,

    /// <summary>Take the person to the settings section that owns all of this, and leave the pill
    /// where it was. There is nothing to search and nothing a card can offer that would change
    /// that.</summary>
    OpenSettings,

    /// <summary>Nothing at all, and the pill says so before it is pressed: it is drawn faded and
    /// takes the plain arrow. Findra IS reading and has simply not finished the first file yet, so
    /// there is nothing to search, nothing to turn on, and nothing settings could add - every
    /// answer is "wait a minute". This is the only press in the product that is refused, and it is
    /// refused for a few minutes on a fresh install rather than for the hours the whole walk
    /// takes: it lifts on the FIRST file read, not on the last.</summary>
    Nothing,
}

/// <summary>
/// What pressing Content means, given what the index actually holds.
///
/// <para>The pill used to do one thing: flip a flag and re-run the query. With reading turned off,
/// or turned on and nothing read yet, that emptied the card and offered nothing to press next -
/// which is the state a new install is in, so it is the state most people met it in.</para>
///
/// <para>The two dead ends are not the same and do not get the same answer. Reading that is merely
/// off, over an index with files already in it, is turned on in place: there are results to show
/// this second, and walking somebody to another window for them would be worse than the bug. An
/// index with nothing in it is not a search that failed - it is a machine nobody has set up, and
/// everything that sets it up is on one page.</para>
/// </summary>
public static class ContentPill
{
    /// <summary>Where <see cref="ContentPress.OpenSettings"/> leads. The switch that starts
    /// reading, the indexing power, the transcription limit and the capability list are all in
    /// this one section, and none of them is in the section a settings window opens on.</summary>
    public const Section Section = Findra.Section.Content;

    /// <summary>
    /// What this press means.
    /// </summary>
    /// <param name="pillOn">Is the pill already down? Then this press releases it.</param>
    /// <param name="haveStore">Is there a content index open in this session at all? Null is an
    /// ordinary state and not a fault.</param>
    /// <param name="readingOn">Is Findra reading inside files - the <c>index:paused</c> row, which
    /// is the one record of that switch a card can see through its read-only connection.</param>
    /// <param name="indexed">How many files have been read, or null when nothing has read the
    /// count yet. The count arrives on a pool thread about a second after a card opens, so null
    /// really happens and has to mean something safe.</param>
    public static ContentPress Decide(bool pillOn, bool haveStore, bool readingOn, long? indexed)
    {
        // Asking for names back can never open a window or move a setting.
        if (pillOn) return ContentPress.Search;

        if (!haveStore) return ContentPress.OpenSettings;

        // Nobody has read the count yet. Search: it paints whatever the index holds, and a window
        // thrown over a card that has just been opened is not undone by pressing anything.
        if (indexed is null) return ContentPress.Search;

        if (indexed > 0) return readingOn ? ContentPress.Search : ContentPress.TurnOnReading;

        // Nothing read. The two halves of that are not the same question and do not get the same
        // answer. Reading is OFF, so nothing ever will be read: everything that changes it - the
        // switch, the power, the limit, the capability list - is on one settings page, and that is
        // where a press goes. Reading is ON, so this is a machine part way through its first pass
        // with the first file not finished: there is nothing to search and nothing to set, and the
        // only honest answer is to stop offering until there is something behind it.
        return readingOn ? ContentPress.Nothing : ContentPress.OpenSettings;
    }

    /// <summary>
    /// Is the pill offering anything? What the painter fades and what the pointer reads, off the
    /// same call that decides the press - so a pill drawn live cannot refuse a click, and a pill
    /// drawn faded cannot answer one.
    /// </summary>
    public static bool Offers(bool pillOn, bool haveStore, bool readingOn, long? indexed) =>
        Decide(pillOn, haveStore, readingOn, indexed) != ContentPress.Nothing;
}
