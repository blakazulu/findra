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
    public void NothingReadYetIsTwoDifferentStatesAndGetsTwoDifferentAnswers()
    {
        // Reading OFF, nothing read: an index with nothing in it is not a search that failed, it
        // is a machine that has not been set up - and everything that sets it up (the switch, the
        // power, the limit, the capabilities) is on one page.
        Assert.Equal(ContentPress.OpenSettings,
            ContentPill.Decide(pillOn: false, haveStore: true, readingOn: false, indexed: 0));

        // Reading ON, nothing read: this used to give the same answer, and it is the wrong one.
        // The machine IS set up and IS working; the first file simply is not finished. Sending
        // somebody to a settings page that says "reading is on" answers a question they did not
        // ask, over the card they had just opened. There is nothing to search, nothing to turn on
        // and nothing to set, so the pill stops offering - faded, plain arrow, press refused.
        Assert.Equal(ContentPress.Nothing,
            ContentPill.Decide(pillOn: false, haveStore: true, readingOn: true, indexed: 0));
    }

    [Fact]
    public void TheOnlyRefusedPressLiftsOnTheFirstFileAndNotOnTheLast()
    {
        // Which is the difference between a pill that is dead for a minute or two and a pill that
        // is dead for the several hours a first pass over a real disk takes. One file read is
        // enough to have something to show.
        Assert.False(ContentPill.Offers(pillOn: false, haveStore: true, readingOn: true, indexed: 0));
        Assert.True(ContentPill.Offers(pillOn: false, haveStore: true, readingOn: true, indexed: 1));

        // And a pill already down is always releasable, whatever the index holds. Somebody stuck
        // in content mode with no way back out would be a worse trap than the one this removes.
        Assert.True(ContentPill.Offers(pillOn: true, haveStore: true, readingOn: true, indexed: 0));

        // Nobody has read the count yet - the second after a card opens. Offered, for the same
        // reason Decide answers Search there: a control that appears disabled and then works is
        // read as broken.
        Assert.True(ContentPill.Offers(pillOn: false, haveStore: true, readingOn: true, indexed: null));
    }

    [Fact]
    public void WhatIsDrawnAndWhatIsAnsweredComeFromTheSameCall()
    {
        // Offers is Decide, asked a different way. Two rules would let the card paint a live pill
        // that refuses the click, or a faded one that answers it - and the faded-but-live pill is
        // the exact defect this whole change removes.
        foreach (bool on in new[] { false, true })
            foreach (bool store in new[] { false, true })
                foreach (bool reading in new[] { false, true })
                    foreach (long? indexed in new long?[] { null, 0, 1, 4_000 })
                        Assert.Equal(
                            ContentPill.Decide(on, store, reading, indexed) != ContentPress.Nothing,
                            ContentPill.Offers(on, store, reading, indexed));
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
