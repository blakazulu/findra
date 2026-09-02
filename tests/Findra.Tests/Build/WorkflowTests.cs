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
            string[] blocks = script
                ? [text]
                : Regex.Matches(text, @"(?ms)^\s*run:\s*\|\s*\n(?<body>(?:^[ \t]+.*\n?)+)")
                       .Select(m => Dedent(m.Groups["body"].Value)).ToArray();

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

        static string Dedent(string block)
        {
            string[] lines = block.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            int indent = lines.Where(l => l.Trim().Length > 0)
                              .Select(l => l.Length - l.TrimStart().Length)
                              .DefaultIfEmpty(0).Min();
            return string.Join("\n", lines.Select(l => l.Length >= indent ? l[indent..] : l));
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
}
