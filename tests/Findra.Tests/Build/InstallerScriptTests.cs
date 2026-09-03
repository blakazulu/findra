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
    public void EveryArchitectureTheReleaseBuildsNamesOneInnoSetupActuallyHas()
    {
        // Inno's architecture identifiers are not a regular family, and the script used to build
        // them by pasting "compatible" onto the {#Arch} the workflow passes. That gives x64 the
        // real word "x64compatible" and arm64 the word "arm64compatible", which does not exist:
        // the identifiers are x86compatible, x86os, x64compatible, x64os, arm32compatible, arm64
        // and win64. ISCC then fails the arm64 matrix leg, the build job fails, the publish job
        // needs it, and a tag produces NO release for EITHER architecture.
        //
        // Nothing caught it. This class asserted no [Setup] directive at all, and the workflow
        // test only checked that the string win-arm64 appears in the matrix. The comment three
        // lines above the defect named the correct pair while the code contradicted it, which is
        // why reading it was not enough either.
        string[] valid =
        [
            "x86compatible", "x86os", "x64compatible", "x64os", "arm32compatible", "arm64", "win64",
        ];

        MatchCollection used = Regex.Matches(
            Script, @"(?m)^\s*Architectures(?:Allowed|InstallIn64BitMode)\s*=\s*(.+?)\s*$", RegexOptions.IgnoreCase);
        Assert.True(used.Count >= 2, $"the script sets {used.Count} architecture directive(s)");

        foreach (Match m in used)
        {
            string value = m.Groups[1].Value;
            Assert.DoesNotContain("{#", value, StringComparison.Ordinal);   // never pasted together
            foreach (string word in value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                Assert.Contains(word, valid, StringComparer.OrdinalIgnoreCase);
        }

        // And both architectures the release builds must actually be reachable in the script,
        // or the fix could be "delete the arm64 branch", which compiles and ships the wrong thing.
        Assert.Contains("arm64", Script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x64compatible", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReportIsReadIntoTheTypeLoadStringFromFileDeclares()
    {
        // Inno 6 is Unicode-only, so String is UnicodeString, and PascalScript requires exact type
        // identity for a var parameter: LoadStringFromFile's second parameter is AnsiString, and
        // passing a String is an ISCC compile error rather than a conversion. SaveStringToFile
        // takes its text as a const parameter, which does convert, which is why only one of the
        // two ever had to change.
        //
        // This is asserted because it cannot be compiled here - Inno Setup is deliberately not
        // installed, and CI is where the script is built for the first time. A test that reads the
        // declaration is the only thing standing between that error and a failed first release.
        Match decl = Regex.Match(Script, @"(?m)^\s*report\s*:\s*(\w+)\s*;");
        Assert.True(decl.Success, "the uninstall routine declares no report variable");
        Assert.Equal("AnsiString", decl.Groups[1].Value);

        // The caption takes a String, so the conversion has to be written at the point of use.
        Assert.Contains("String(report)", Script, StringComparison.Ordinal);
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
        // A literal version in the script is a second place the version lives, and
        // Directory.Build.props is the only one there may be. The #error is what makes forgetting
        // to pass it loud rather than silent.
        //
        // Asserting the #ifndef guard exists is not enough on its own: the guard's own error
        // message carried "1.2.0" until the close-out read it, and every assertion here was green
        // the whole time. So the teeth are the third line - no three-part number anywhere in the
        // file, in a comment, in an error message or in a setting. Two-part numbers are left
        // alone: MinVersion=10.0 and the "Inno Setup 6.3" note are not Findra's version.
        Assert.Contains("#ifndef AppVersion", Script, StringComparison.Ordinal);
        Assert.Contains("{#AppVersion}", Script, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\d+\.\d+\.\d+", Script);
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

        // Comment lines come OUT. Two assertions in this class were answered by the very comment
        // written about the thing being asserted - StopFindra explains itself with "// --stop, not
        // CloseApplications", and InitializeUninstall with "// --dry-run --quiet writes that
        // report" - so both stayed green with the Exec call underneath them rewritten. A routine's
        // prose is not its behaviour, and nothing here should be able to read it.
        return string.Join('\n', m.Value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
                                        .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
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
        // The call, not the token: `--stop` also appears in StopFindra's own explanatory comment.
        Assert.Contains("findra.exe'), '--stop'", Body("StopFindra"), StringComparison.Ordinal);
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
        // The exact arguments, not the token. `--dry-run` also appears in the routine's own
        // comment, so a check for it was green with the Exec rewritten to `--uninstall --quiet` -
        // which really uninstalls, while the person is still reading the prompt that asks whether
        // they want to.
        string body = Body("InitializeUninstall");
        Assert.Contains("Exec(app, '--uninstall --dry-run --quiet'", body, StringComparison.Ordinal);
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
        // Scoped to the routine that does it, and to the parameter read rather than the word:
        // INSTALLSOURCE appears in the comment above the code as well, so renaming the parameter
        // left the whole-file check green and every winget install reporting itself as a source
        // build.
        string body = Body("RecordInstallSource");
        Assert.Contains("installed-by.txt", body, StringComparison.Ordinal);
        Assert.Contains("{param:INSTALLSOURCE|}", body, StringComparison.Ordinal);
        Assert.Contains("'winget'", body, StringComparison.Ordinal);
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
    public void TheInstallerRemovesTheQuietUninstallStringSoTheQuestionGetsAsked()
    {
        // Inno registers QuietUninstallString on its own, and Windows 11's Settings > Apps prefers
        // it: the uninstaller then runs with /SILENT, UninstallSilent() is true, InitializeUninstall
        // returns before it builds anything, and the checkbox nobody saw leaves 2.93 GB of models
        // and a whole index on the disk of somebody who believed they had asked for them gone.
        // That is the route nearly everybody takes, and it made PRIVACY.md's "a checkbox in the
        // uninstaller" untrue in practice. Deleting the value is what puts the question back.
        //
        // Scoped to the routine, not the file: the value's name also appears in the comment above
        // the call, so a whole-script search is answered by the prose explaining the code it is
        // meant to be checking.
        string body = Body("ForgetTheQuietUninstall");
        Assert.Contains("RegDeleteValue", body, StringComparison.Ordinal);
        Assert.Contains("'QuietUninstallString'", body, StringComparison.Ordinal);

        // And it has to happen AFTER the install, because Inno writes that key as part of it.
        // ssPostInstall is the only step that is late enough; doing it in ssInstall deletes a
        // value Inno then writes, which looks identical in review and does nothing at all.
        string step = Body("CurStepChanged");
        Assert.Contains("ForgetTheQuietUninstall", step, StringComparison.Ordinal);
        Assert.Contains("ssPostInstall", step, StringComparison.Ordinal);

        // The scripted quiet uninstall is not lost with it, and the reason the value can go is
        // that there is still one: findra.exe --uninstall --purge --quiet.
        Assert.Contains("--uninstall --purge --quiet", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheKeyTheInstallerEditsIsTheOneAppIdNames()
    {
        // Two copies of a GUID in one file is exactly how they come to differ, and a key built
        // from the wrong one deletes nothing, reports nothing, and fails only on a stranger's
        // machine months later. So the script may hold the GUID once - in AppId - and the routine
        // that builds the uninstall key must read it from there.
        string id = Setting("AppId");
        string guid = Regex.Match(id, @"[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}").Value;
        Assert.NotEqual("", guid);
        Assert.Single(Regex.Matches(Script, Regex.Escape(guid), RegexOptions.IgnoreCase));

        string body = Body("UninstallKey");
        Assert.Contains("SetupSetting(\"AppId\")", body, StringComparison.Ordinal);
        Assert.Contains(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\", body, StringComparison.Ordinal);
        Assert.Contains("_is1", body, StringComparison.Ordinal);

        // Inno writes that key in the 64-bit view, because the install runs in 64-bit mode. The
        // unsuffixed constant would follow the install mode too, but naming the view is the
        // difference between a silent no-op and a deletion nobody has to reason about.
        Assert.Matches(@"HKLM(?:64|32)", Body("ForgetTheQuietUninstall"));
    }

    [Fact]
    public void EveryFileTheScriptWritesItselfIsAlsoRemovedByHand()
    {
        // Inno removes what it recorded in its own uninstall log, which is the [Files] section and
        // nothing else. A file written from [Code] with SaveStringToFile is invisible to it, so it
        // survives the uninstall - and because {app} is then not empty, the DIRECTORY survives too.
        // A real uninstall on a real machine left "C:\Program Files\Findra" behind holding one
        // nine-byte installed-by.txt, which no test could see because nothing here had ever run.
        //
        // Driven off the SaveStringToFile calls rather than a hard-coded name, so a second file
        // written the same way fails this until it is listed too.
        MatchCollection written = Regex.Matches(
            Script, @"SaveStringToFile\(\s*ExpandConstant\('\{app\}\\([^']+)'\)");
        Assert.True(written.Count > 0, "no SaveStringToFile into {app} - has the script changed shape?");

        // Reuses the section reader the [UninstallRun] tests use, rather than a second regex: the
        // last two attempts to write one of these inline lost a backslash to an escaping layer and
        // produced a pattern that matched nothing, which is the shape of a test that cannot fail.
        Match section = Regex.Match(Script, @"(?m)^\[UninstallDelete\]$([\s\S]*?)(?=^\[|\z)", RegexOptions.Multiline);
        Assert.True(section.Success, "the script writes files into {app} but has no [UninstallDelete] section");

        foreach (Match m in written)
        {
            string name = m.Groups[1].Value;
            Assert.Contains(name, section.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NothingInTheInstallerClaimsTheBinariesAreSigned()
    {
        // The signing step in the release pipeline does nothing yet. A "digitally signed" line in
        // the installer's own copy would be a claim the product cannot support, on the surface a
        // stranger reads first.
        // Whole word. A bare substring search also fires on "assigned", "designed" and
        // "redesigned", which are ordinary words a comment in this file may legitimately want -
        // it caught the comment above CreateCustomForm the first time that comment was written.
        // The boundary keeps every real claim in range, including the hyphenated "code-signed",
        // because a hyphen is a word boundary too.
        Assert.DoesNotMatch(@"(?i)\bsigned\b", Script);
    }
}
