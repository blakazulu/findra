using Findra;
using Xunit;

/// <summary>
/// What pressing Content does when there is nothing behind it.
///
/// <para>It used to flip a flag and re-run the query, whatever state the index was in - so with
/// reading turned off, or turned on and nothing read yet, the pill emptied the card and offered
/// no way forward at all. The two dead ends are different and get different answers: reading
/// that is merely off is turned on in place, and an index with nothing in it sends the person to
/// Settings, where the capabilities and the transcription limit are as well.</para>
/// </summary>
public class ContentPillTests
{
    [Fact]
    public void ReleasingThePillIsAlwaysJustTheSearchAgain()
    {
        // Pressing a pill that is already down asks for names back. It can never open a window
        // or change a setting, whatever the index looks like.
        foreach (bool store in new[] { true, false })
            foreach (bool reading in new[] { true, false })
                foreach (long? indexed in new long?[] { null, 0, 4_000 })
                    Assert.Equal(ContentPress.Search,
                        ContentPill.Decide(pillOn: true, haveStore: store, readingOn: reading, indexed: indexed));
    }

    [Fact]
    public void AnIndexWithSomethingInItAndReadingTurnedOffIsTurnedOnInPlace()
    {
        // There is something to search right now, so the card answers the question it was asked
        // and turns reading back on beside it. Sending this person to Settings would be taking
        // them away from results they were about to get.
        Assert.Equal(ContentPress.TurnOnReading,
            ContentPill.Decide(pillOn: false, haveStore: true, readingOn: false, indexed: 4_000));
    }

    [Fact]
    public void AnIndexWithSomethingInItAndReadingAlreadyOnJustSearches()
    {
        Assert.Equal(ContentPress.Search,
            ContentPill.Decide(pillOn: false, haveStore: true, readingOn: true, indexed: 4_000));
    }

    [Fact]
    public void NothingReadYetSendsThePersonToSettingsWhetherReadingIsOnOrOff()
    {
        // The verbatim decision: an index with nothing in it is not a search that failed, it is
        // a machine that has not been set up - and everything that sets it up (the switch, the
        // power, the limit, the capabilities) is on one page.
        Assert.Equal(ContentPress.OpenSettings,
            ContentPill.Decide(pillOn: false, haveStore: true, readingOn: false, indexed: 0));
        Assert.Equal(ContentPress.OpenSettings,
            ContentPill.Decide(pillOn: false, haveStore: true, readingOn: true, indexed: 0));
    }

    [Fact]
    public void ASessionWithNoContentStoreAtAllSendsThePersonToSettings()
    {
        // Null is an ordinary state - the content index could not be opened this session - and
        // an empty card would read as "your words are in no file", which is a different and
        // untrue claim.
        Assert.Equal(ContentPress.OpenSettings,
            ContentPill.Decide(pillOn: false, haveStore: false, readingOn: true, indexed: 4_000));
    }

    [Fact]
    public void AnIndexNobodyHasReadTheCountOfYetIsSearchedRatherThanGuessedAt()
    {
        // The count is read off the index once a second on a pool thread, so for the first frames
        // of a card there is no answer yet. Searching is the harmless arm: it paints what the
        // index holds, where opening a window over a card somebody has just opened is not
        // recoverable by pressing anything.
        Assert.Equal(ContentPress.Search,
            ContentPill.Decide(pillOn: false, haveStore: true, readingOn: false, indexed: null));
    }

    [Fact]
    public void TheContentPillIsWhereTheCardSendsPeopleForTheSectionThatOwnsIt()
    {
        // Which section, decided once: the capability rows, the transcription limit, the power
        // and the switch that starts reading are all in Content, and nothing about them is in
        // Look, which is where a settings window opens by default.
        Assert.Equal(Section.Content, ContentPill.Section);
    }
}
