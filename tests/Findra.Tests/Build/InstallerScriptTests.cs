using System.Text.RegularExpressions;

using Xunit;

/// <summary>
/// The installer script, asserted as text. It cannot be compiled on a machine without Inno Setup
/// and it cannot be run anywhere without administrator rights, so the rules that would break
/// somebody else's machine are checked here instead - each of them a thing that looks harmless in
/// review and is not.
/// </summary>
public class InstallerScriptTests
{
    private static readonly string Script = Repo.Read("installer/findra.iss");

    private static string Setting(string key)
    {
        Match m = Regex.Match(Script, $@"(?m)^\s*{key}\s*=\s*(.+?)\s*$", RegexOptions.IgnoreCase);
        Assert.True(m.Success, $"the installer script sets no {key}");
        return m.Groups[1].Value;
    }

    [Fact]
    public void TheInstallDirectoryCarriesNoVersionNumber()
    {
        // The scheduled task stores an ABSOLUTE path to findra.exe. A versioned directory means
        // every upgrade silently points an elevated logon task at a binary that no longer exists,
        // and the person's name search stops working with nothing anywhere to say why.
        string dir = Setting("DefaultDirName");

        Assert.DoesNotContain("{#", dir, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\d+\.\d+", dir);
    }

    [Fact]
    public void TheApplicationIdIsAFixedGuidAndNotSomethingThatMoves()
    {
        // AppId is the identity Windows upgrades and uninstalls by. If it carries the version,
        // every release installs beside the last one, and two copies register two scheduled tasks.
        string id = Setting("AppId");

        Assert.DoesNotContain("{#", id, StringComparison.Ordinal);
        Assert.Matches(@"\{?\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}\}?", id);
    }

    [Fact]
    public void TheInstallerAsksForAdministratorRights()
    {
        // The product needs them once, to register the HighestAvailable task, and the UNINSTALLER
        // needs them to remove it. PrivilegesRequired=lowest produces an uninstaller that cannot.
        Assert.Equal("admin", Setting("PrivilegesRequired"), ignoreCase: true);
    }

    [Fact]
    public void TheVersionIsPassedInRatherThanBakedIn()
    {
        // A literal version in the script is a second place the version lives, which is exactly
        // what Task 1 removed everywhere else. The #error is what makes forgetting it loud.
        Assert.Contains("#ifndef AppVersion", Script, StringComparison.Ordinal);
        Assert.Contains("#error", Script, StringComparison.Ordinal);
    }

    /// <summary>
    /// One Pascal routine, from its declaration to the start of the next one.
    ///
    /// <para>Not "up to the first `end;`": a routine with a nested `begin ... end;` would be cut
    /// off there, so the assertions below would silently depend on which line happened to come
    /// first. Bounded by the next declaration instead, which is a boundary the script's own shape
    /// guarantees.</para>
    /// </summary>
    private static string Body(string name)
    {
        Match m = Regex.Match(
            Script,
            @"(?:function|procedure)\s+" + name + @"\b(?:(?!(?:function|procedure)\s+\w+).)*",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        Assert.True(m.Success, $"the installer script has no {name}");
        return m.Value;
    }

    [Fact]
    public void TheProcessesAreStoppedBeforeAnyFileIsReplaced()
    {
        // Inno's own CloseApplications only closes windowed applications, and two of Findra's
        // three processes have no window at all - the helper is headless and elevated, and the
        // indexer is a hidden child. Replacing findra.exe underneath a running helper leaves a
        // process from the old version holding a volume handle.
        //
        // PrepareToInstall runs BEFORE files are copied; CurStepChanged(ssPostInstall) runs after,
        // which is the same code in a place where it achieves nothing.
        //
        // TWO captures, because `--stop` is not in PrepareToInstall - it calls StopFindra, which
        // is a different routine defined above it. A single non-greedy match ends at the first
        // `end;`, which is PrepareToInstall's own, so asserting on that alone can never pass.
        Assert.Contains("StopFindra", Body("PrepareToInstall"), StringComparison.Ordinal);
        Assert.Contains("--stop", Body("StopFindra"), StringComparison.Ordinal);
        Assert.DoesNotContain("--stop", Body("CurStepChanged"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The lines of <c>[UninstallRun]</c> that actually run something.
    ///
    /// <para>The section header is anchored to the start of a line. The draft of this class
    /// matched <c>\[UninstallRun\]</c> anywhere, and the <c>[Code]</c> section has a comment
    /// mentioning the section by name - so deleting the whole section left the search matching
    /// that comment, and every assertion downstream of it kept passing against an installer that
    /// ran nothing on the way out. Proved by deleting the section and watching the tests stay
    /// green.</para>
    /// </summary>
    private static string[] UninstallRunLines()
    {
        Match run = Regex.Match(Script, @"(?m)^\[UninstallRun\]$(.*?)(?=\n\[|\z)", RegexOptions.Singleline);
        Assert.True(run.Success, "the installer script has no [UninstallRun] section");
        return [.. run.Groups[1].Value
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("Filename:", StringComparison.OrdinalIgnoreCase))];
    }

    [Fact]
    public void TheUninstallerRunsFindrasOwnUninstallLogicRatherThanJustDeletingFiles()
    {
        // Deleting files leaves the scheduled task, which spec §2a calls a defect. [UninstallRun]
        // entries run before the uninstaller removes anything, which is when findra.exe still
        // exists to be run.
        string[] lines = UninstallRunLines();

        Assert.NotEmpty(lines);
        foreach (string line in lines)
        {
            Assert.Contains(@"findra.exe", line, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("--uninstall", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ExactlyOneOfTheTwoUninstallRunsCanEverFire()
    {
        // The keep run and the purge run are both listed, and the checkbox decides between them.
        // Two ways to get that wrong, and both delete somebody's models against their answer:
        // drop a Check and both entries run - the purge one second, so the data goes whatever the
        // box said; or give them the same RunOnceId and Inno runs whichever it reaches first and
        // skips the other for ever after.
        string[] lines = UninstallRunLines();
        Assert.Equal(2, lines.Length);

        string[] checks = [.. lines.Select(l => Regex.Match(l, @"Check:\s*(\w+)").Groups[1].Value)];
        Assert.DoesNotContain("", checks);
        Assert.Equal(2, checks.Distinct(StringComparer.Ordinal).Count());

        string[] ids = [.. lines.Select(l => Regex.Match(l, @"RunOnceId:\s*""([^""]+)""").Groups[1].Value)];
        Assert.DoesNotContain("", ids);
        Assert.Equal(2, ids.Distinct(StringComparer.Ordinal).Count());

        // And the keeping one is the negation, not a second copy of the same condition.
        Assert.Matches(@"Result\s*:=\s*not\s+Purge", Body("KeepWanted"));
    }

    [Fact]
    public void TheUninstallerOffersARealCheckboxAndNotJustAQuestion()
    {
        // Spec §2a: deleting the models and the index is opt-in "via a CHECKBOX in the uninstaller
        // and a flag on the command line", and PRIVACY.md promises the same thing in the same
        // words to the public.
        //
        // The first draft built a MsgBox with Yes and No, and the test meant to guard this grepped
        // for "--purge" - which cannot tell a checkbox from a message box from a comment, and
        // passed against an implementation that had no checkbox at all. An Inno uninstaller has no
        // wizard, so a checkbox means a custom form: CreateCustomForm plus a TNewCheckBox.
        Assert.Contains("CreateCustomForm", Script, StringComparison.Ordinal);
        Assert.Contains("TNewCheckBox", Script, StringComparison.Ordinal);
        Assert.Contains("--purge", Script, StringComparison.Ordinal);

        // And the box starts unticked: keeping is the default (spec §2a), and a box that starts
        // ticked is the same as no box at all for anybody who clicks straight through.
        Assert.Matches(@"Checked\s*:=\s*False", Body("InitializeUninstall"));
    }

    [Fact]
    public void TheUninstallPromptCarriesTheMeasuredSizeRatherThanAVagueWarning()
    {
        // Spec §2a: "The prompt states the measured size it would free ... not a vague warning."
        // The number comes from `findra --uninstall --dry-run --quiet`, which writes its report to
        // a temp file precisely because an Inno script cannot capture a child's standard output.
        string body = Body("InitializeUninstall");
        Assert.Contains("--dry-run", body, StringComparison.Ordinal);
        Assert.Contains("LoadStringFromFile", body, StringComparison.Ordinal);
    }

    [Fact]
    public void NoModelIsShippedInsideTheInstaller()
    {
        // Spec §2: "Models are never in the publish folder. They download on first run." An
        // installer carrying them would be 3 GB, and would put them somewhere the uninstaller's
        // keep-by-default rule does not cover.
        //
        // ONLY the Source: lines are examined. The [Files] section opens with a comment explaining
        // that models are downloaded into %LOCALAPPDATA%\Findra\models - which contains the word
        // twice, so an assertion over the raw section text can never pass against the script it is
        // written for. What matters is what the section installs.
        Match files = Regex.Match(Script, @"\[Files\](.*?)(?=\n\[|\z)", RegexOptions.Singleline);
        Assert.True(files.Success, "the installer script has no [Files] section");

        string[] sources = [.. files.Groups[1].Value
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("Source:", StringComparison.OrdinalIgnoreCase))];

        Assert.NotEmpty(sources);
        foreach (string line in sources)
            foreach (string forbidden in new[] { ".onnx", ".spm", "models", "whisper" })
                Assert.DoesNotContain(forbidden, line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheInstallerRecordsHowFindraGotOntoTheMachine()
    {
        // Without this, every winget install reports itself as a source build and every update
        // tells the person to read release notes instead of running one command.
        Assert.Contains("installed-by.txt", Script, StringComparison.Ordinal);
        Assert.Contains("INSTALLSOURCE", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInstallerDoesNotWriteTheAutostartEntryItself()
    {
        // An elevated installer's HKCU is the hive of whoever answered the UAC prompt. Writing a
        // Run value there puts Findra in an administrator's startup and not in the installing
        // user's. Findra writes it from its own session instead (Autostart).
        //
        // Scanned over the WHOLE script rather than inside a [Registry] section, for two reasons.
        // The first is that a section-scoped version asserts nothing until somebody adds the
        // section, which makes it read as a live assertion while it is vacuous. The second is that
        // this script's [Code] part is much the larger half, so RegWriteStringValue is the likelier
        // route in - and a [Registry]-only search would not look there at all.
        Assert.DoesNotContain(@"CurrentVersion\Run", Script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RegWriteStringValue", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingInTheInstallerClaimsTheBinariesAreSigned()
    {
        // The signing step in the release pipeline does nothing yet. A "digitally signed" line in
        // the installer's own copy would be a claim the product cannot support, on the surface a
        // stranger reads first.
        Assert.DoesNotContain("signed", Script, StringComparison.OrdinalIgnoreCase);
    }
}
