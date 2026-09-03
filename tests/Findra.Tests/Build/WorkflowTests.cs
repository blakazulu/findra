using System.Text.RegularExpressions;

using Xunit;

/// <summary>
/// The three workflows, asserted as text. GitHub Actions cannot run on this machine, so the rules
/// that would be discovered by a bad release are checked here instead - and one of them, "nothing
/// but a person publishes to the catalogue", is a rule about something that cannot be undone.
/// </summary>
public class WorkflowTests
{
    private static string Ci => Repo.Read(".github/workflows/ci.yml");
    private static string Release => Repo.Read(".github/workflows/release.yml");
    private static string Winget => Repo.Read(".github/workflows/winget.yml");

    private static string[] AllWorkflows() =>
        Directory.GetFiles(Repo.Path_(".github/workflows"), "*.yml");

    /// <summary>
    /// The document's own <c>on:</c> block, and nothing else. Scoping matters: the word "push"
    /// appears in a `git push` step and in a job name, and a whole-file search for it would make
    /// the catalogue test pass or fail for reasons that have nothing to do with triggers.
    /// </summary>
    private static string TriggerBlock(string yaml)
    {
        string[] lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var block = new List<string>();
        bool inside = false;
        foreach (string line in lines)
        {
            if (!inside)
            {
                if (Regex.IsMatch(line, @"^on:")) { inside = true; block.Add(line); }
                continue;
            }

            // A new top-level key ends it: anything at column zero that is not a comment.
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('#')) break;
            block.Add(line);
        }

        Assert.True(inside, "the workflow declares no triggers at all");
        return string.Join("\n", block);
    }

    /// <summary>
    /// The workflow's jobs, keyed by name: everything under <c>jobs:</c> from one two-space key
    /// to the next. Written because "does the job that creates the release depend on the job that
    /// checked the tag" is a question about one job's <c>needs:</c>, and a whole-file search for
    /// the word answers it for any job at all - including one that already had it, for another
    /// reason, somewhere else in the same file.
    /// </summary>
    private static Dictionary<string, string> Jobs(string yaml)
    {
        string[] lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var jobs = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var body = new List<string>();
        bool inside = false;
        foreach (string line in lines)
        {
            if (!inside)
            {
                if (Regex.IsMatch(line, @"^jobs:")) inside = true;
                continue;
            }

            // A new top-level key ends it, the same rule TriggerBlock follows.
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('#')) break;

            Match head = Regex.Match(line, @"^  (?<name>[A-Za-z_][A-Za-z0-9_-]*):\s*$");
            if (head.Success)
            {
                if (current is not null) jobs[current] = string.Join("\n", body);
                current = head.Groups["name"].Value;
                body.Clear();
                continue;
            }

            body.Add(line);
        }

        if (current is not null) jobs[current] = string.Join("\n", body);
        Assert.True(jobs.Count > 0, "the workflow declares no jobs at all");
        return jobs;
    }

    /// <summary>
    /// The workflow with its comment lines taken out. Every "does this workflow DO X" assertion
    /// reads this rather than the file, because the comment explaining why a step calls
    /// Publish.ps1 contains the string "Publish.ps1" - so a whole-file search stays green on a
    /// workflow whose step was replaced by something else and whose explanation was left behind.
    /// This suite has now been bitten by that shape three times; twice it was the explanation
    /// that kept the test green.
    /// </summary>
    private static string WithoutComments(string yaml) =>
        string.Join("\n", yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
                              .Where(l => !l.TrimStart().StartsWith('#')));

    /// <summary>
    /// Every <c>run: |</c> block in a workflow, dedented, ending where a YAML block scalar really
    /// ends: at the first line that is not blank and is indented no further than the <c>run:</c>
    /// key itself.
    ///
    /// <para>This replaces a regex that took "one or more indented lines", which ends a block at
    /// the first BLANK line instead. Every run: block written with a paragraph break in it was
    /// therefore parsed as far as the break and no further, while the count guard below stayed
    /// satisfied because the block was found, just not all of it. release.yml happens to have no
    /// blank lines inside a block, so nothing had ever shown it; winget.yml's manifest step is
    /// thirty lines of PowerShell with four breaks in it, and a syntax error pasted in after the
    /// first one was not noticed at all. Indentation is what YAML uses to end a block scalar, so
    /// it is what this uses.</para>
    /// </summary>
    private static string[] RunBlocks(string yaml)
    {
        string[] lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var blocks = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            Match head = Regex.Match(lines[i], @"^(?<pad>\s*)run:\s*\|\s*$");
            if (!head.Success) continue;

            int depth = head.Groups["pad"].Value.Length;
            var body = new List<string>();
            while (i + 1 < lines.Length)
            {
                string line = lines[i + 1];
                if (line.Trim().Length > 0 && line.Length - line.TrimStart().Length <= depth) break;
                body.Add(line);
                i++;
            }

            int indent = body.Where(l => l.Trim().Length > 0)
                             .Select(l => l.Length - l.TrimStart().Length)
                             .DefaultIfEmpty(0).Min();
            blocks.Add(string.Join("\n", body.Select(l => l.Length >= indent ? l[indent..] : l)));
        }

        return [.. blocks];
    }

    /// <summary>
    /// The build matrix: from <c>matrix:</c> to the first line indented no further than it is.
    /// "Both architectures are built" is a claim about that block and nowhere else.
    /// </summary>
    private static string MatrixBlock(string yaml)
    {
        string[] lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var block = new List<string>();
        int depth = -1;
        foreach (string line in lines)
        {
            if (depth < 0)
            {
                Match head = Regex.Match(line, @"^(?<pad>\s*)matrix:\s*$");
                if (head.Success) depth = head.Groups["pad"].Value.Length;
                continue;
            }

            if (line.Trim().Length == 0) { block.Add(line); continue; }
            if (line.Length - line.TrimStart().Length <= depth) break;
            block.Add(line);
        }

        Assert.True(depth >= 0, "the workflow declares no build matrix at all");
        return string.Join("\n", block);
    }
    // ---- continuous integration ----------------------------------------------------------

    [Fact]
    public void EveryPushAndEveryPullRequestIsBuiltAndTested()
    {
        // "Every push" was a settled decision, so the trigger carries no branch filter: a branch
        // pushed without a pull request would otherwise never be built, and that is most of how
        // work arrives. The DoesNotContain is what makes this more than a word search.
        string on = TriggerBlock(Ci);
        Assert.Contains("push", on, StringComparison.Ordinal);
        Assert.Contains("pull_request", on, StringComparison.Ordinal);
        Assert.DoesNotContain("branches", on, StringComparison.Ordinal);
    }

    [Fact]
    public void NoWorkflowExpandsAnEnvironmentVariableWindowsSpellsWithBrackets()
    {
        // PowerShell ends a variable name at the first character that cannot be part of one, and
        // "(" is one of them: "$env:ProgramFiles(x86)\..." expands to "C:\Program Files(x86)\..."
        // - a path that does not exist. The correct form is ${env:ProgramFiles(x86)}.
        //
        // The first draft filed this under "only knowable on the first tag". It is knowable by
        // reading, so it is a test: one regex, and it catches the whole family.
        foreach (string path in AllWorkflows())
        {
            string text = File.ReadAllText(path);
            Assert.DoesNotMatch(@"\$env:[A-Za-z_][A-Za-z0-9_]*\(", text);
        }
    }

    [Fact]
    public void EveryPowerShellBlockInEveryWorkflowParses()
    {
        // A syntax check without execution. The workflows and the .ps1 scripts carry PowerShell
        // that nothing in this project can run - no runner, no Inno Setup, no tag - so the only
        // thing standing between a typo and a failed release is somebody reading it. This is the
        // cheap half of that, and it needs no runner at all.
        foreach (string path in AllWorkflows().Concat(Directory.GetFiles(Repo.Path_("build"), "*.ps1")))
        {
            string text = File.ReadAllText(path);
            bool script = path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);

            // For a workflow, only the run: blocks; for a .ps1, the whole file.
            string[] blocks = script ? [text] : RunBlocks(text);

            // A parser handed nothing reports no errors, so an extraction that quietly matched
            // none of a workflow's blocks would pass while checking nothing. Counting the block
            // scalars separately is what makes that visible: `run: | # a trailing comment` is
            // valid YAML the pattern above does not see, and this is what says so.
            int scalars = script ? 1 : Regex.Matches(text, @"(?m)^\s*run:\s*\|").Count;
            Assert.True(scalars == blocks.Length,
                $"{Path.GetFileName(path)}: {scalars} run: | block(s) written, {blocks.Length} read");

            foreach (string block in blocks)
            {
                System.Management.Automation.Language.Parser.ParseInput(
                    block, out _, out System.Management.Automation.Language.ParseError[] errors);
                Assert.True(errors.Length == 0,
                    $"{Path.GetFileName(path)}: {(errors.Length > 0 ? errors[0].Message : "")}");
            }
        }
    }

    [Fact]
    public void TheBuildIsTheOneThatFailsOnAWarning()
    {
        // "Build output pristine" is a rule in every plan this project has had. A CI build without
        // -warnaserror lets the first warning through and the next hundred follow it.
        Assert.Contains("-warnaserror", Ci, StringComparison.Ordinal);
        Assert.Contains("dotnet test", Ci, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHeadlessDiagnosticsRunOnEveryPush()
    {
        // Spec 9 calls the diagnostic modes non-negotiable and says they are how the app is
        // verified without a screen. A suite that never runs them lets a mode rot until somebody
        // needs it to diagnose something else.
        Assert.Contains("Check-Diagnostics.ps1", Ci, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDiagnosticsCheckCoversEveryModeTheProgramAdvertises()
    {
        // Extracted from Program.Main's own switch, so adding a mode without adding it here fails
        // rather than passing quietly. The four exclusions each need something CI has not got: an
        // elevated volume handle, a parent process, or a machine somebody is willing to lose.
        string program = Repo.Read("src/Findra/Program.cs");
        string script = Repo.Read("build/Check-Diagnostics.ps1");
        string[] skip = ["--names", "--index", "--uninstall", "--stop"];

        // The modes the script actually RUNS, read out of the argument array each Check is handed
        // rather than out of the file as a whole. A plain substring search over the text passes on
        // a mode that is only mentioned in a comment - deleting the --content check left the line
        // "`--models list` and `--content status` are what they do with no verb" behind, and the
        // search found that instead.
        var run = Regex.Matches(script, @"@\((?<args>'--[a-z-]+'(?:\s*,\s*'[^']*'|\s*,\s*\$[A-Za-z]+)*)\)")
                       .SelectMany(m => Regex.Matches(m.Groups["args"].Value, @"'(--[a-z-]+)'")
                                             .Select(a => a.Groups[1].Value))
                       .ToHashSet(StringComparer.Ordinal);

        int found = 0;
        foreach (Match m in Regex.Matches(program, @"""(--[a-z]+)""\s*=>"))
        {
            string mode = m.Groups[1].Value;
            if (skip.Contains(mode)) continue;
            Assert.True(run.Contains(mode),
                $"build/Check-Diagnostics.ps1 never runs {mode}; it runs {string.Join(" ", run.Order())}");
            found++;
        }

        // Without this the loop passes on an empty match set, which is exactly what renaming the
        // switch arms - or writing them any other way - would produce. Nine is what Program.cs
        // advertises today; a tenth mode is meant to raise this number and the script together.
        Assert.True(found >= 9,
            $"only {found} modes were read out of Program.cs; the shape of its switch changed");
    }

    [Fact]
    public void NoWorkflowEverRunsAnUninstallThatIsNotADryRun()
    {
        // A real --uninstall on a runner is harmless; a real --uninstall in a workflow somebody
        // later copies onto a self-hosted machine is not. The dry run prints the same report and
        // touches nothing, so there is never a reason for the other one to be in a workflow.
        foreach (string path in AllWorkflows())
        {
            string text = File.ReadAllText(path);
            foreach (Match m in Regex.Matches(text, @"--uninstall(?<rest>[^\r\n]*)"))
                Assert.Contains("--dry-run", m.Groups["rest"].Value, StringComparison.Ordinal);
        }
    }

    // ---- the release -----------------------------------------------------------------------

    [Fact]
    public void OnlyAVersionTagStartsARelease()
    {
        string on = TriggerBlock(Release);
        Assert.Contains("tags", on, StringComparison.Ordinal);
        Assert.Contains("v*", on, StringComparison.Ordinal);
        // A release that also fires on every push to main publishes whatever is on main under
        // whatever version the props file happens to hold.
        Assert.DoesNotContain("branches", on, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsPublishedBeforeTheTagHasBeenCheckedAgainstTheVersionAndTheChangelog()
    {
        // The gate is worth nothing if the release step does not depend on it. Both halves are
        // asserted: the script is called, and the job that creates the release needs the job that
        // called it.
        // The steps, not the header comment - which names Check-Release.ps1 while explaining
        // what it refuses, and would answer this on a workflow that had stopped calling it.
        string steps = WithoutComments(Release);
        Assert.Contains("Check-Release.ps1", steps, StringComparison.Ordinal);

        int gate = steps.IndexOf("Check-Release.ps1", StringComparison.Ordinal);
        int publish = steps.IndexOf("action-gh-release", StringComparison.Ordinal);
        Assert.True(gate >= 0 && publish > gate,
            "the release is created before the tag is checked");

        // Textual order is not dependency, and the plan's bare Contains for the word needs could
        // not fail here: the build job carries one of its own, so deleting the publish job's line
        // left the assertion green while the release ran BESIDE the gate instead of after it.
        // Name the job that runs the gate, name the job that creates the release, read that one.
        Dictionary<string, string> jobs = Jobs(steps);
        string gateJob = Assert.Single(
            jobs.Where(j => j.Value.Contains("Check-Release.ps1", StringComparison.Ordinal))
                .Select(j => j.Key));
        string publishJob = Assert.Single(
            jobs.Where(j => j.Value.Contains("action-gh-release", StringComparison.Ordinal))
                .Select(j => j.Key));

        Match needs = Regex.Match(jobs[publishJob], @"(?m)^\s*needs:\s*(?<on>.+)$");
        Assert.True(needs.Success, $"the '{publishJob}' job declares no needs of its own");
        Assert.Contains(gateJob, needs.Groups["on"].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReleaseNotesAreTheChangelogSectionAndNotAListOfCommits()
    {
        // GitHub's generated notes would make CHANGELOG.md decorative, and the changelog is also
        // where Findra's own update check sends anybody who built from source.
        Assert.Contains("body_path", Release, StringComparison.Ordinal);
        Assert.DoesNotContain("generate_release_notes: true", Release, StringComparison.Ordinal);
    }

    [Fact]
    public void BothArchitecturesAreBuiltFromTheFirstRelease()
    {
        // x64 ships first because it is the whole market today; arm64 is added when there is
        // demand, and the cost of keeping it possible is close to zero while the cost of
        // retrofitting it is not (spec 6). Retrofitting includes a second manifest lineage in a
        // catalogue somebody else owns.
        // The matrix, not the file. Deleting the arm64 row left a whole-file search green on a
        // comment three steps below that says the RID on the command line is what keeps
        // win-arm64 reachable - the explanation passing for the thing explained.
        string matrix = MatrixBlock(Release);
        Assert.Contains("win-x64", matrix, StringComparison.Ordinal);
        Assert.Contains("win-arm64", matrix, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePublishIsSelfContainedAndTheRuntimeIdentifierIsOnlyOnACommandLine()
    {
        // Spec 2: a stranger installing from winget must never be asked to install .NET first.
        // And the RID belongs on the command line, never in a project file - which is what keeps
        // win-arm64 reachable at all.
        // The steps, not the comment that explains them: replacing the call with a bare
        // dotnet publish left a whole-file search green on the sentence above it.
        Assert.Contains("Publish.ps1", WithoutComments(Release), StringComparison.Ordinal);
        Assert.DoesNotContain("<RuntimeIdentifier>", Release, StringComparison.Ordinal);

        // Half this test's name was unasserted as the plan wrote it: calling Publish.ps1 is only
        // self-contained for as long as Publish.ps1 is, and dropping the switch there is the edit
        // that puts a request to install .NET in front of a stranger. Read it where it lives.
        Assert.Contains("--self-contained", Repo.Read("build/Publish.ps1"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSigningSeamExistsAndIsHonestAboutDoingNothingYet()
    {
        // The step exists so that arranging signing later is a change to one step rather than a
        // reshuffle of the artefact flow. It says what it is, because a step called sign that
        // silently does nothing is worse than no step at all.
        Match sign = Regex.Match(Release, @"(?im)^\s*-\s*name:.*sign.*$(?<body>(\n(?!\s*-\s*name:).*)*)");
        Assert.True(sign.Success, "the release workflow has no signing step");
        Assert.Contains("not yet", sign.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NothingInTheReleaseWorkflowTouchesTheCatalogue()
    {
        // The one rule in this plan about something that cannot be undone: a mis-tagged build that
        // reaches a GitHub release can be deleted, and one that reaches the winget catalogue is
        // already on somebody else's machine by the next upgrade.
        Assert.DoesNotContain("wingetcreate", Release, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("winget-pkgs", Release, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NothingInTheReleaseClaimsTheArtefactsAreSigned()
    {
        Assert.DoesNotContain("digitally signed", Release, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the catalogue ----------------------------------------------------------------------

    [Fact]
    public void TheCatalogueIsOnlyEverPublishedByAPersonStartingItByHand()
    {
        // The one irreversible action in this plan. A push, a tag or a release trigger here means
        // a mis-tagged build reaches other people's machines by the next `winget upgrade`, and
        // there is no taking it back. This test is the rule.
        //
        // The comments come out before the triggers are read. TriggerBlock deliberately does not
        // end the block on a comment line, so the header that explains which triggers may never
        // appear here - naming every one of them - would land inside the block and fail the guard
        // for saying the right thing. The other direction is worse: a comment that mentioned
        // workflow_dispatch would answer the Contains on a workflow that no longer had one.
        string on = TriggerBlock(WithoutComments(Winget));

        Assert.Contains("workflow_dispatch", on, StringComparison.Ordinal);
        foreach (string trigger in new[] { "push:", "pull_request:", "release:", "schedule:", "repository_dispatch:" })
            Assert.DoesNotContain(trigger, on, StringComparison.Ordinal);
    }

    [Fact]
    public void ThisIsTheOnlyWorkflowThatMentionsTheCatalogueAtAll()
    {
        // The test above reads one file, so it cannot see the same step pasted into a second one.
        // This reads all of them.
        //
        // It also insists that one workflow does reach the catalogue. "No workflow mentions
        // winget-pkgs" is a state this repository reaches by deleting winget.yml, and the plan's
        // form of this test - assert only over the files that are not winget.yml - is green on
        // exactly that. A rule that says "only this one" has to know the one still exists.
        var mentions = new List<string>();
        foreach (string path in AllWorkflows())
        {
            string text = File.ReadAllText(path);
            if (text.Contains("wingetcreate", StringComparison.OrdinalIgnoreCase)
             || text.Contains("winget-pkgs", StringComparison.OrdinalIgnoreCase))
                mentions.Add(Path.GetFileName(path));
        }

        Assert.True(mentions.Count == 1 && mentions[0] == "winget.yml",
            $"the workflows that reach the catalogue are: [{string.Join(", ", mentions)}]");
    }
}
