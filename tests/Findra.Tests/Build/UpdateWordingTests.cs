using System.Text.RegularExpressions;

using Xunit;

/// <summary>
/// What the public documents say about updates. Findra installs an update when a person presses
/// Update now, and switching the check off stops the daily check but not a check somebody asks
/// for. Every document that once said otherwise is read here: two policy pages still said it after
/// the first pass over the wording, and one of them is a page SignPath's terms require to be true.
/// </summary>
public class UpdateWordingTests
{
    public static TheoryData<string> Documents() => new()
    {
        "README.md", "PRIVACY.md", "SECURITY.md", "docs/code-signing-policy.md",
        "website/public/llms.txt", "website/public/index.html",
        "website/content/about.md", "website/content/home.md",
        "website/content/pages/faq.md", "website/content/pages/faq.html",
    };

    [Theory, MemberData(nameof(Documents))]
    public void NoDocumentStillSaysFindraNeverInstallsOrThatOffRefusesACheckSomebodyAsksFor(string path)
    {
        string text = Regex.Replace(Repo.Read(path), @"\s+", " ");
        foreach (string untrue in new[]
                 {
                     "request is not made", "off means off", "never installs an update",
                     "never downloads or installs", "never installs anything",
                     "nothing else leaves the machine, ever",
                 })
            Assert.DoesNotContain(untrue, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSecurityPolicySaysWhatTheChecksumCannotCatch()
    {
        // The download is checked against the digest GitHub publishes, which catches a broken or
        // swapped file and not somebody who controls the account: only code signing closes that,
        // and the design says the documents say so plainly.
        string security = Regex.Replace(Repo.Read("SECURITY.md"), @"\s+", " ");
        Assert.Contains("checksum", security, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GitHub account", security, StringComparison.Ordinal);
        Assert.Contains("code signing", security, StringComparison.OrdinalIgnoreCase);
    }
}
