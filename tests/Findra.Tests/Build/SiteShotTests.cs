using System.Text.RegularExpressions;

using Findra;
using Findra.Diagnostics;

using Xunit;

/// <summary>
/// The two copies of every screenshot, held to each other.
///
/// <para>The README shows <c>docs/shots</c> and the website shows <c>website/public/shots</c>,
/// and they are the same renders of the same surfaces produced by the same commands. They have
/// to be two files rather than one: Netlify publishes <c>website/public</c> exactly as it sits,
/// so nothing under it can reach up into <c>docs/</c>, and the site deliberately has no build
/// step that could copy anything.</para>
///
/// <para>Nothing kept them together, and they drifted. The site served an <c>adv</c>, a
/// <c>firstrun</c> and a <c>settingscontent</c> from an older build while printing the command
/// that produces the current ones, and its Settings picture was missing "Start reading now" and
/// "Indexing power" - two controls the product had gained - under a heading that reads "Every
/// picture below is the product". Nothing in the suite could have noticed: <c>ReadmeTests</c>
/// checks the README's own images and had no reason to look at the site's.</para>
///
/// <para>These compare bytes and printed commands, which is all there is to compare - a PNG and
/// a stylesheet are data, and there is no code here to run instead. <c>build/Make-Shots.ps1</c>
/// is the way to satisfy them: it renders the README's own list and copies each result into the
/// site, so the remedy is one command rather than twelve.</para>
/// </summary>
public class SiteShotTests
{
    private const string Docs = "docs/shots";
    private const string Site = "website/public/shots";

    private static readonly string Readme = Repo.Read("README.md");
    private static readonly string Page = Repo.Read("website/public/index.html");

    /// <summary>Every <c>--searchshot &lt;prefix&gt;&lt;name&gt;.png &lt;state&gt; [palette]</c>
    /// a document prints, keyed by the image's file name. The README writes the path it stores
    /// the image at and the site writes a bare name, which is the only difference between
    /// them.</summary>
    private static Dictionary<string, string> Commands(string text, string prefix)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(text,
            @"--searchshot\s+" + Regex.Escape(prefix) +
            @"(?<file>[A-Za-z0-9._-]+\.png)\s+(?<state>[a-z]+)(?:\s+(?<palette>[A-Za-z]+))?"))
        {
            found[m.Groups["file"].Value] =
                (m.Groups["state"].Value + " " + m.Groups["palette"].Value).TrimEnd();
        }
        return found;
    }

    [Fact]
    public void TheSiteAndTheReadmeShowTheSameRenderOfTheSameSurface()
    {
        string[] site = Directory.GetFiles(Repo.Path_(Site), "*.png");
        Assert.NotEmpty(site);

        foreach (string path in site)
        {
            string name = Path.GetFileName(path);
            string twin = Path.Combine(Repo.Path_(Docs), name);

            Assert.True(File.Exists(twin), $"{Site}/{name} has no counterpart in {Docs}");
            Assert.True(File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(twin)),
                $"{name} differs between {Docs} and {Site}, so one of them is a stale render and " +
                "a reader of one page is being shown a product the other page does not show. " +
                "Regenerate BOTH: pwsh -File build/Make-Shots.ps1 -Exe <findra.exe>");
        }
    }

    [Fact]
    public void TheSiteAndTheReadmeQuoteTheSameCommandForTheSamePicture()
    {
        // Bytes matching is not enough on its own: two identical files under two different
        // printed commands means one page is telling you to run something that would produce a
        // different picture, and both pages promise that running the command gets you the image.
        Dictionary<string, string> readme = Commands(Readme, "docs/shots/");
        Dictionary<string, string> page = Commands(Page, "");
        Assert.NotEmpty(page);

        foreach ((string name, string call) in page)
            if (readme.TryGetValue(name, out string? theirs))
                Assert.True(theirs == call,
                    $"{name} is printed as '--searchshot {name} {call}' on the site and " +
                    $"'--searchshot docs/shots/{name} {theirs}' in the README");
    }

    [Fact]
    public void EveryPictureTheSiteShowsIsInTheRepositoryAndNamesASurfaceFindraCanDraw()
    {
        var shown = 0;
        foreach (Match m in Regex.Matches(Page, @"<img\s[^>]*?src=""(?<src>shots/[^""]+)"""))
        {
            Assert.True(Repo.Exists("website/public/" + m.Groups["src"].Value),
                $"the site shows {m.Groups["src"].Value}, which is not in the repository - that is " +
                "a broken image on a public page");
            shown++;
        }
        Assert.True(shown > 0, "no <img> on the site points at shots/, which cannot be right");

        foreach ((string name, string call) in Commands(Page, ""))
        {
            string[] parts = call.Split(' ');
            Assert.Contains(parts[0], SearchShot.States);
            if (parts.Length > 1)
                Assert.Contains(parts[1], Palette.BuiltIn.Select(p => p.Name));
        }
    }
}
