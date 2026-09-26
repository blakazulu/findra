using Findra;
using Xunit;

/// <summary>
/// The fourth pill on the card: the Settings "Start reading now" button, where the search is.
/// It says what pressing it would do - start, or stop - and while a start it asked for has not
/// shown up in the index yet, it says it is starting and takes no second press.
/// </summary>
public class ReadingPillTests
{
    [Fact]
    public void ReadingMeansTheSwitchIsOnAndAnIndexerIsAliveWithWorkInHand()
    {
        Assert.True(ReadingPill.Reading(readingOn: true, indexerAlive: true, pending: 12));
        Assert.False(ReadingPill.Reading(readingOn: false, indexerAlive: true, pending: 12));
        Assert.False(ReadingPill.Reading(readingOn: true, indexerAlive: false, pending: 12));
        Assert.False(ReadingPill.Reading(readingOn: true, indexerAlive: true, pending: 0));
    }

    [Fact]
    public void WithNothingAskedThePillFollowsTheIndex()
    {
        Assert.Equal(ReadingShown.Stop, ReadingPill.Shown(reading: true, asked: null));
        Assert.Equal(ReadingShown.Start, ReadingPill.Shown(reading: false, asked: null));
    }

    [Fact]
    public void AStartThatHasNotArrivedYetSaysStarting()
    {
        Assert.Equal(ReadingShown.Starting, ReadingPill.Shown(reading: false, asked: true));
        Assert.Equal(ReadingShown.Stop, ReadingPill.Shown(reading: true, asked: true));
    }

    [Fact]
    public void AStopIsBelievedAtOnceRatherThanWaitingForTheIndexToCatchUp()
    {
        // The status row the card reads is up to a second behind the setting. Without this the
        // pill would go on saying Stop after it was pressed.
        Assert.Equal(ReadingShown.Start, ReadingPill.Shown(reading: true, asked: false));
    }

    [Fact]
    public void WhatWasAskedIsForgottenOnceTheIndexAgreesOrTheWaitIsOver()
    {
        Assert.Null(ReadingPill.Settle(reading: true, asked: true, since: TimeSpan.FromSeconds(1)));
        Assert.Null(ReadingPill.Settle(reading: false, asked: false, since: TimeSpan.FromSeconds(1)));
        Assert.True(ReadingPill.Settle(reading: false, asked: true, since: TimeSpan.FromSeconds(1)));

        // A start over a finished disk never shows work in hand, so it cannot say Starting for
        // ever: after the wait it goes back to offering Start.
        Assert.Null(ReadingPill.Settle(reading: false, asked: true, since: ReadingPill.Hold));
        Assert.Null(ReadingPill.Settle(reading: true, asked: null, since: TimeSpan.Zero));
    }

    [Fact]
    public void APressStartsStopsOrIsRefusedWhileStarting()
    {
        Assert.Equal(ReadingPress.Start, ReadingPill.Press(ReadingShown.Start));
        Assert.Equal(ReadingPress.Stop, ReadingPill.Press(ReadingShown.Stop));
        Assert.Equal(ReadingPress.Nothing, ReadingPill.Press(ReadingShown.Starting));
    }

    [Fact]
    public void OnlyStartingIsNotOffered()
    {
        Assert.True(ReadingPill.Offers(ReadingShown.Start));
        Assert.True(ReadingPill.Offers(ReadingShown.Stop));
        Assert.False(ReadingPill.Offers(ReadingShown.Starting));
    }

    [Fact]
    public void EachShapeHasItsOwnLabel()
    {
        Assert.Equal("Start now", ReadingPill.Label(ReadingShown.Start));
        Assert.Equal("Starting...", ReadingPill.Label(ReadingShown.Starting));
        Assert.Equal("Stop", ReadingPill.Label(ReadingShown.Stop));
    }
}
