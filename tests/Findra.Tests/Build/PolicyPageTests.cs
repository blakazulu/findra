using System.Text.RegularExpressions;

using Xunit;

/// <summary>
/// Three committed, public pages that make promises about code. Nothing else in the suite reads
/// them, and a policy page that is wrong after the plan that claimed to make it true is worse than
/// one that was never written.
///
/// <para>Every assertion here is scoped to the paragraph that carries the promise rather than to
/// the whole page. A page-wide <c>Assert.Contains</c> over a phrase as ordinary as "scheduled
/// task" or "kept" is satisfied by any sentence anywhere - including the one that is wrong - so it
/// reads as a live coupling while being unable to fail.</para>
/// </summary>
public class PolicyPageTests
{
    private static readonly string Privacy = Repo.Read("PRIVACY.md");
    private static readonly string Signing = Repo.Read("docs/code-signing-policy.md");
    private static readonly string Security = Repo.Read("SECURITY.md");

    /// <summary>
    /// One Markdown section, from its heading to the next heading at the same level or above.
    /// The promises these tests hold up are paragraph-sized, and the page around them is full of
    /// the same words used correctly elsewhere.
    /// </summary>
    private static string Section(string page, string heading)
    {
        Match m = Regex.Match(
            page,
            @"(?m)^##\s+" + Regex.Escape(heading) + @"\s*$(?<body>.*?)(?=^##\s|\z)",
            RegexOptions.Singleline);
        Assert.True(m.Success, $"no '## {heading}' section - the page was restructured, so read it again");
        return m.Groups["body"].Value;
    }

    /// <summary>
    /// The same text with every run of whitespace flattened to one space. Both pages are hard
    /// wrapped, so a sentence a test quotes carries a newline in the middle at a column nobody
    /// chose deliberately - and an assertion that depends on where the wrap fell breaks on the
    /// next copy-edit while the sentence it was guarding is still there and still correct.
    /// </summary>
    private static string Flat(string text) => Regex.Replace(text, @"\s+", " ");

    [Fact]
    public void ThePrivacyPageDoesNotSayHandDeletionRemovesEverything()
    {
        // The sentence that was false: the scheduled task and the Run value are outside both
        // folders, so deleting the folders by hand leaves the orphan the rest of this plan exists
        // to prevent. The corrected paragraph names both and says what removes them.
        string deleting = Flat(Section(Privacy, "Deleting it"));

        // The old sentence, and the shape of any sentence that says the same thing again: the two
        // folders, then an unqualified "nothing else on your machine is touched".
        Assert.DoesNotContain("yourself. Nothing else on your machine is touched", deleting, StringComparison.Ordinal);

        Assert.Contains("deleting the folders by hand does not remove them", deleting, StringComparison.Ordinal);
        Assert.Contains("scheduled task", deleting, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("start-at-sign-in", deleting, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--uninstall", deleting, StringComparison.Ordinal);
    }

    [Fact]
    public void BothPagesPromiseTheCheckboxTheInstallerActuallyBuilds()
    {
        // PRIVACY.md says "a checkbox in the uninstaller"; spec §2a says the same. A page that
        // promises a control the installer does not have is the defect this task exists for, and
        // the coupling is what makes it a test rather than a reading.
        string iss = Repo.Read("installer/findra.iss");

        if (!Privacy.Contains("checkbox in the uninstaller", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Fail("PRIVACY.md no longer promises a checkbox - if that was deliberate, delete " +
                        "this test and say so; if it was a copy-edit, put the sentence back.");
        }

        Assert.Contains("CreateCustomForm", iss, StringComparison.Ordinal);
        Assert.Contains("TNewCheckBox", iss, StringComparison.Ordinal);

        // The box has to say what ticking it does. A checkbox with no caption, or one captioned
        // something else, is not the control the page describes to somebody about to click it.
        // Bound to the checkbox variable rather than to any control: with `\w+` the caption could
        // sit on the OK button while the box itself carried none, and the page would still be
        // describing a control nobody sees.
        string box = Regex.Match(iss, @"(?m)^\s*(?<v>\w+)\s*:=\s*TNewCheckBox\.Create").Groups["v"].Value;
        Assert.False(string.IsNullOrEmpty(box), "the installer creates no checkbox");
        Match caption = Regex.Match(iss, @"(?m)^\s*" + box + @"\.Caption\s*:=\s*'(?<text>[^']*delete[^']*)'");
        Assert.True(caption.Success, "the installer's checkbox is not captioned with what ticking it would do");
        foreach (string word in new[] { "models", "index", "settings" })
            Assert.Contains(word, caption.Groups["text"].Value, StringComparison.OrdinalIgnoreCase);

        // Unticked, because the page's next promise is that keeping is the default.
        Assert.Matches(@"Checked\s*:=\s*False", iss);

        // And the tick reaches the two mutually exclusive runs rather than stopping at the form.
        Assert.Matches(@"(?m)^Filename:.*--uninstall --quiet.*Check:\s*KeepWanted", iss);
        Assert.Matches(@"(?m)^Filename:.*--uninstall --purge --quiet.*Check:\s*PurgeWanted", iss);
    }

    [Fact]
    public void TheSigningPageSaysItIsNotInForceForAsLongAsTheSigningStepDoesNothing()
    {
        // The coupling that matters. Four tests forbid a "signed" claim in the README, the
        // installer, the release workflow and the winget listing; the one page that makes the
        // claim in the present tense had no test at all. It carries a status note now, and this is
        // what keeps the note and the placeholder step in step with each other: when signing is
        // arranged, the workflow's step stops doing nothing and this test is what makes you remove
        // the note in the same commit.
        //
        // The step BODY, and not the step's name and not the whole file. "not yet" appears in the
        // signing step's own name AND in the comment block above it, so anything wider keeps this
        // coupling stuck in the placeholder branch for whoever arranges signing and leaves a
        // nearby sentence behind - which is exactly the sentence somebody would leave behind.
        //
        // The body stops at the next `- name:` OR `- uses:`. Stopping only at `- name:` swallows
        // every `uses:` step that follows, which here is the rest of the workflow.
        string release = Repo.Read(".github/workflows/release.yml");
        Match step = Regex.Match(
            release, @"(?im)^[ \t]*-[ \t]*name:.*sign.*$(?<body>(\n(?![ \t]*-[ \t]).*)*)");
        Assert.True(step.Success, "the release workflow has no signing step");

        string body = step.Groups["body"].Value;
        Assert.False(string.IsNullOrWhiteSpace(body), "the signing step has no body to read");
        bool signingIsStillAPlaceholder = body.Contains("not yet", StringComparison.OrdinalIgnoreCase);

        if (signingIsStillAPlaceholder)
            Assert.Contains("Not yet in force", Signing, StringComparison.OrdinalIgnoreCase);
        else
            Assert.DoesNotContain("Not yet in force", Signing, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSigningPageDoesNotClaimTheIndexAndModelsAreRemoved()
    {
        // The second false sentence, corrected before this plan reached execution: "Both are
        // removed by the uninstaller" had "both" covering the scheduled task AND the folder that
        // holds the index and the models, while the very next sentence said those are kept. The
        // page contradicted itself in consecutive sentences.
        //
        // Read inside the paragraph that makes the claim. "kept" appears in PRIVACY.md's own
        // wording too, and a page-wide search for it would go on passing over a section that had
        // been rewritten to say the opposite.
        string changes = Flat(Section(Signing, "What Findra changes on the machine"));

        Assert.DoesNotContain("Both are removed by the uninstaller", changes, StringComparison.OrdinalIgnoreCase);

        // What it must say instead, and what Uninstall.Plan(purge: false, ...) actually does:
        // the task and the autostart entry always go, the data stays unless somebody asks.
        Assert.Matches(@"\*{0,2}always\*{0,2} removes the scheduled task and any autostart entry", changes);
        Assert.Matches(@"index and any downloaded models are \*{0,2}kept\*{0,2} by default", changes);
    }

    [Fact]
    public void TheSecurityPageSendsAReporterSomewhereOtherThanThePublicIssueTracker()
    {
        // The whole value of the page is the door it points at. A security page whose only
        // address is the issue tracker asks a reporter to publish the exploit, and one that
        // points at a private form without saying to keep details out of an issue leaves the
        // usual reflex - open an issue - as the path of least resistance.
        string report = Section(Security, "Reporting a vulnerability");

        Assert.Contains("security/advisories/new", report, StringComparison.Ordinal);
        Assert.Matches(@"(?i)do not open a public issue", Flat(report));

        // And the private form has to be the FIRST address on the page, not a footnote under the
        // issue tracker: whichever link is read first is the one that gets used.
        int advisory = report.IndexOf("security/advisories/new", StringComparison.Ordinal);
        int issues = report.IndexOf("findra/issues", StringComparison.Ordinal);
        Assert.True(issues < 0 || advisory < issues,
            "SECURITY.md names the public issue tracker before the private advisory form");
    }

    [Fact]
    public void NoPolicyPageDescribesAModeFindraDoesNotHave()
    {
        // All three pages tell people to run commands. A renamed mode makes that advice false on
        // the surfaces least likely to be re-read.
        string program = Repo.Read("src/Findra/Program.cs");
        var known = new HashSet<string>(
            Regex.Matches(program, @"""(--[a-z]+)""\s*=>").Select(m => m.Groups[1].Value), StringComparer.Ordinal);

        Assert.NotEmpty(known);

        // Counted PER PAGE. A floor over both together was met by PRIVACY.md's two commands on
        // its own, so the signing page could drop the only one it names - the thing this guard
        // exists to notice - and the total still cleared the bar.
        foreach ((string name, string page) in new[]
                 { ("PRIVACY.md", Privacy), ("code-signing-policy.md", Signing), ("SECURITY.md", Security) })
        {
            int found = 0;
            foreach (Match m in Regex.Matches(page, @"findra(?:\.exe)?\s+(--[a-z-]+)"))
            {
                Assert.Contains(m.Groups[1].Value, known);
                found++;
            }

            Assert.True(found >= 1, $"{name} names no findra command at all, so this test read nothing on it");
        }
    }
}
