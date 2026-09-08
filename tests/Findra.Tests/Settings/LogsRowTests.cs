using System.Linq;

using Findra;
using Xunit;

/// <summary>
/// The row that opens the log folder.
///
/// <para>Everything Findra records about itself lived at a path printed by <c>--version</c>, which
/// is a command somebody has to already know exists, typed into a terminal they have to already
/// have open. The one place a person goes when something is wrong is Settings, and About is where
/// they end up - so the folder they need to send somebody was the one thing that screen would not
/// give them.</para>
///
/// <para>Written as tests over the row rather than over the action, because
/// <c>SettingsActionTests</c> already proves every action reaches its host method. What it cannot
/// see is a row that was never added, or one added and wired to the wrong action - which is the
/// drawn-and-dead defect this whole interface exists to prevent.</para>
/// </summary>
[Collection("culture")]
public class LogsRowTests
{
    private static SettingsState About() => new(Config.Default) { Section = Section.About };

    private static IReadOnlyList<Control> Rows() => SettingsModel.Controls(About());

    [Fact]
    public void AboutOffersOneWayToReachTheLogs()
    {
        Control row = Rows().Single(c => c.Id == ControlId.Logs);
        Assert.Equal(ControlKind.Button, row.Kind);
        Assert.Equal("Logs", row.Label);
        Assert.Equal("Open folder", row.Value);
    }

    [Fact]
    public void PressingItAsksTheShellForTheFolder()
    {
        IReadOnlyList<Control> rows = Rows();
        int at = -1;
        for (int i = 0; i < rows.Count; i++) if (rows[i].Id == ControlId.Logs) at = i;
        Assert.True(at >= 0, "About has no Logs row");

        SettingsOutcome outcome = SettingsModel.Apply(About(), new PanelHit(PanelTarget.Control, at, -1));
        Assert.Equal(SettingsAction.OpenLogs, outcome.Action);
    }

    [Fact]
    public void ItSitsUnderTheInstallSourceAndAboveTheClosingNote()
    {
        // Placement is not decoration here. The Removing note is a closing statement rather than a
        // control, and a button underneath it reads as belonging to the sentence about uninstalling
        // - which is the one thing this button must not be mistaken for.
        IReadOnlyList<Control> rows = Rows();
        int via = -1, logs = -1, removing = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Id == ControlId.InstalledVia) via = i;
            if (rows[i].Id == ControlId.Logs) logs = i;
            if (rows[i].Id == ControlId.Removing) removing = i;
        }
        Assert.True(via < logs, "the logs row is above the install source");
        Assert.True(logs < removing, "the logs row is below the closing note");
    }

    [Fact]
    public void ItIsTheOnlyRowInTheWindowThatOpensTheLogs()
    {
        // One way in. A second copy on another pane is a second thing to keep in step, and the
        // question "where do I find the logs" has one answer or it has none.
        foreach (Section section in RailLayout.Sections)
        {
            var state = new SettingsState(Config.Default) { Section = section, HebrewOffered = true, Drives = ["C"] };
            int found = SettingsModel.Controls(state).Count(c => c.Id == ControlId.Logs);
            Assert.True(found <= 1, $"{section} draws {found} logs rows");
            if (section != Section.About) Assert.Equal(0, found);
        }
    }
}
