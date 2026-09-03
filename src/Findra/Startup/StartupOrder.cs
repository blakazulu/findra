using System.Collections.Generic;

namespace Findra.Startup;

/// <summary>One stage of a launch. The settings are not here: they are read before any of this
/// can be decided, because whether the first screen is needed at all is one of the things they
/// say.</summary>
public enum StartupStep
{
    /// <summary>Spec §6's welcome screen, shown once.</summary>
    FirstRun,

    /// <summary>Ask the scheduler to start the elevated name helper, and wait for the pipe to
    /// answer. Off the interface thread; names are what Findra can do with no models at all.
    /// </summary>
    NamesHelper,

    Hotkey,
    Capsule,
    ContentIndex,
    Tray,
    UpdateCheck,
}

/// <summary>
/// Which stages a launch takes, and when.
///
/// <para>The first screen used to be shown and not waited for. <c>Window.Show()</c> does not
/// block, so the hotkey, the capsule and the tray icon were all built behind it while somebody
/// was still reading the first sentence - and by the time they pressed "Get these" the whole
/// product was already running, which made the download they had just asked to watch read as a
/// window standing in front of it. <b>When the screen is needed, it owns the display until it is
/// answered</b>, and the rest of the launch continues from the answer.</para>
///
/// <para>The names helper is the one deliberate exception, and it is not in
/// <see cref="AfterTheScreenIsAnswered"/> because the answer starts it itself, immediately:
/// searching by name is the half of Findra that works with nobody's models, and nobody should
/// wait on a 1.5 GB download for their filenames.</para>
///
/// <para>A list rather than a run of statements, because the ordering is the part that was wrong
/// and a window cannot be built in a test. The shell switches on these and on nothing else, and
/// its default arm throws - a step added here and forgotten there is a stage that silently stops
/// happening.</para>
/// </summary>
public static class StartupOrder
{
    /// <summary>Everything after the settings have been read, before anything is on the display.
    /// </summary>
    public static IReadOnlyList<StartupStep> Immediate(bool firstRunNeeded) =>
        firstRunNeeded ? [StartupStep.FirstRun] : Rest(withNamesHelper: true);

    /// <summary>What was held back, once the screen has been answered.</summary>
    public static IReadOnlyList<StartupStep> AfterTheScreenIsAnswered() => Rest(withNamesHelper: false);

    /// <summary>What was held back, when the screen could not be put up at all. Every stage is
    /// wrapped, so a throw inside the one that shows it would otherwise leave a process with no
    /// tray icon, no hotkey, no capsule and nothing to answer.</summary>
    public static IReadOnlyList<StartupStep> WhenTheScreenCouldNotBeShown() => Rest(withNamesHelper: true);

    /// <summary>
    /// The order everything else is built in, one place, so a launch that answered a welcome
    /// screen and one that did not take the same stages in the same order.
    ///
    /// <para>The content index comes before the tray because the tray's tooltip and the capsule's
    /// line read what it holds, and the update check is last because it is the only stage here
    /// that touches the network.</para>
    /// </summary>
    private static IReadOnlyList<StartupStep> Rest(bool withNamesHelper) =>
        withNamesHelper
            ? [StartupStep.NamesHelper, StartupStep.Hotkey, StartupStep.Capsule,
               StartupStep.ContentIndex, StartupStep.Tray, StartupStep.UpdateCheck]
            : [StartupStep.Hotkey, StartupStep.Capsule,
               StartupStep.ContentIndex, StartupStep.Tray, StartupStep.UpdateCheck];
}
