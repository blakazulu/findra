using Findra.Startup;
using Xunit;

public class HelperStartTests
{
    [Theory]
    [InlineData(true, false, false)]    // Findra is open: it ran the task to start the helper
    [InlineData(false, true, false)]    // sign-in, and Findra starts at sign-in too
    [InlineData(false, false, true)]    // somebody typed findra --names at a prompt
    public void TheHelperStaysWhenFindraIsOpenStartsAtSignInOrSomebodyAskedForIt(
        bool interfaceRunning, bool startsAtSignIn, bool fromATerminal)
        => Assert.True(HelperStart.ShouldStay(() => interfaceRunning, () => startsAtSignIn, () => fromATerminal));

    [Fact]
    public void ASignInThatDoesNotStartFindraDoesNotKeepTheHelper()
    {
        // After Quit and a restart, with Start Findra when I sign in off, nothing of Findra's runs.
        Assert.False(HelperStart.ShouldStay(() => false, () => false, () => false));
    }

    [Fact]
    public void TheHelperItselfIsNotAnInterface()
        => Assert.False(HelperStart.AnotherFindraIn(session: 1, self: 10, [(10, 1)]));

    [Fact]
    public void AnotherFindraInThisSessionIsTheInterface()
        => Assert.True(HelperStart.AnotherFindraIn(session: 1, self: 10, [(10, 1), (20, 1)]));

    [Fact]
    public void AFindraInAnotherSessionIsSomebodyElses()
        => Assert.False(HelperStart.AnotherFindraIn(session: 1, self: 10, [(10, 1), (20, 2)]));
}
