using Findra.Startup;
using Xunit;

/// <summary>
/// What is built when, and what waits for the first screen to be answered.
///
/// <para>The screen used to be shown and not waited for: <c>Show()</c> does not block, so the
/// hotkey, the capsule and the tray were all built behind it while somebody was still reading
/// the first sentence. By the time they pressed "Get these", Findra was already running, and the
/// download progress they had just asked to watch read as a window in the way of it.</para>
///
/// <para>The order is a list rather than a run of statements so that the decision has a test at
/// all: nothing about a window can be constructed here, but which stages a launch takes and in
/// what order is exactly the part that was wrong.</para>
/// </summary>
public class StartupOrderTests
{
    [Fact]
    public void NothingAppearsUntilTheFirstScreenIsAnswered()
    {
        // THE test of this change. A capsule, a tray icon and a global hotkey arriving behind the
        // welcome screen are the whole defect.
        IReadOnlyList<StartupStep> now = StartupOrder.Immediate(firstRunNeeded: true);

        Assert.Equal([StartupStep.FirstRun], now);
    }

    [Fact]
    public void AnOrdinaryLaunchBuildsEverythingAtOnceAndShowsNoScreen()
    {
        IReadOnlyList<StartupStep> now = StartupOrder.Immediate(firstRunNeeded: false);

        Assert.DoesNotContain(StartupStep.FirstRun, now);
        foreach (StartupStep step in Enum.GetValues<StartupStep>())
            if (step != StartupStep.FirstRun)
                Assert.Contains(step, now);
    }

    [Fact]
    public void TheNamesHelperIsTheOneThingTheScreenDoesNotHoldUp()
    {
        // The deliberate exception. Names are the half of Findra that works with nobody's models,
        // and the answer registers the scheduled task and starts the helper itself - so the step
        // is not in the list that follows, because it has already happened by then. Waiting for a
        // 1.5 GB download before a filename is searchable would be the wrong trade.
        IReadOnlyList<StartupStep> after = StartupOrder.AfterTheScreenIsAnswered();

        Assert.DoesNotContain(StartupStep.NamesHelper, after);
        Assert.DoesNotContain(StartupStep.FirstRun, after);
        Assert.Contains(StartupStep.Capsule, after);
        Assert.Contains(StartupStep.Tray, after);
        Assert.Contains(StartupStep.Hotkey, after);
        Assert.Contains(StartupStep.ContentIndex, after);
        Assert.Contains(StartupStep.UpdateCheck, after);
    }

    [Fact]
    public void AScreenThatCouldNotBeShownStillLeavesAWorkingFindra()
    {
        // The failure path, and it is not theoretical: putting the screen up is wrapped like every
        // other stage, so a throw inside it would otherwise leave a process with no tray icon, no
        // hotkey, no capsule and nothing to answer. Here the names helper IS in the list, because
        // the answer that would have started it never came.
        IReadOnlyList<StartupStep> instead = StartupOrder.WhenTheScreenCouldNotBeShown();

        Assert.DoesNotContain(StartupStep.FirstRun, instead);
        foreach (StartupStep step in Enum.GetValues<StartupStep>())
            if (step != StartupStep.FirstRun)
                Assert.Contains(step, instead);
    }

    [Fact]
    public void NoStepIsTakenTwiceOnAnyPath()
    {
        // A step listed twice is a second tray icon, a second capsule window or a second content
        // index over one file - and the two paths through a first run are exactly where a step
        // gets duplicated, because one of them is written from the other.
        foreach (IReadOnlyList<StartupStep> path in new[]
        {
            StartupOrder.Immediate(firstRunNeeded: false),
            StartupOrder.Immediate(firstRunNeeded: true),
            StartupOrder.AfterTheScreenIsAnswered(),
            StartupOrder.WhenTheScreenCouldNotBeShown(),
        })
            Assert.Equal(path.Count, path.Distinct().Count());
    }

    [Fact]
    public void TheOrderIsTheSameWhicheverWayTheLaunchGotThere()
    {
        // The content index is opened before the tray so the tray's tooltip and the capsule's
        // line have something to read, and the update check is last because it is the only thing
        // here that touches the network. A launch that answered a welcome screen must take those
        // stages in the same order as one that did not, or the two paths are two products.
        List<StartupStep> ordinary =
            [.. StartupOrder.Immediate(firstRunNeeded: false).Where(s => s != StartupStep.NamesHelper)];
        List<StartupStep> answered = [.. StartupOrder.AfterTheScreenIsAnswered()];

        Assert.Equal(ordinary, answered);
    }
}
