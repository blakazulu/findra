using SkiaSharp;

namespace Findra;

/// <summary>
/// The search card as a screen reader sees it: the field, the four pills, what the rows are, the
/// filter chips, the rows on screen, the three actions and the close button, each at the rectangle
/// the painter draws it in. The card's keyboard needs nothing added - typing, arrows, Enter, Tab
/// through the chips and Escape already do everything - so this is the half that was missing.
/// </summary>
public static class CardAccess
{
    public const string Name = "Findra search";

    public static IReadOnlyList<AccessNode> Nodes(SearchCardState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var nodes = new List<AccessNode>
        {
            new("field", AccessRole.Edit, s.Content ? "Search inside files" : "Search", SearchCardLayout.FieldRect())
            {
                Value = s.Query,
                Help = s.HasQuery ? "" : s.Content ? SearchCardPainter.ContentPlaceholder : SearchCardPainter.NamePlaceholder,
            },
            new("content", AccessRole.Toggle, SearchCardPainter.ContentLabel, SearchCardLayout.ContentRect())
                { On = s.Content, Enabled = s.ContentOffered, Help = "Search inside files instead of their names" },
            new("advanced", AccessRole.Button, SearchCardPainter.AdvancedLabel, SearchCardLayout.AdvRect())
                { On = s.AdvOpen },
            new("settings", AccessRole.Button, SearchCardPainter.SettingsLabel, SearchCardLayout.SettingsRect()),
            new("reading", AccessRole.Button, ReadingPill.Label(s.Reading) + " reading inside files", SearchCardLayout.ReadingRect())
                { Enabled = ReadingPill.Offers(s.Reading) },
        };

        if (s.ShowsBody)
        {
            int count = s.Rows.Count;
            (string left, string right) = SearchCardPainter.Header(s, count);
            nodes.Add(new AccessNode("header", AccessRole.Text, left,
                new SKRect(SearchCardLayout.Pad, SearchCardLayout.HeaderTop, SearchCardLayout.HeaderRight, SearchCardLayout.HeaderTop + 18))
                { Help = right });

            for (int i = 0; i < SearchCardLayout.ChipLabels.Length; i++)
                nodes.Add(new AccessNode("chip:" + i, AccessRole.Option,
                    $"{SearchCardLayout.ChipLabels[i]}, {SearchCardState.CountOf(s.Results, i)}", SearchCardLayout.ChipRect(i))
                    { On = s.Filter == i });

            int scroll = SearchCardLayout.ClampScroll(s.Scroll, count);
            for (int v = 0; v < SearchCardLayout.VisibleRows(count); v++)
            {
                int i = scroll + v;
                SearchResult r = s.Rows[i];
                nodes.Add(new AccessNode("row:" + r.Path, AccessRole.Item, r.Name, SearchCardLayout.RowRect(v))
                {
                    On = i == s.Highlight,
                    Help = Describe(r),
                });
            }

            if (count > 0)
            {
                string[] actions = ["Open", "Reveal", "Copy path"];
                for (int a = 0; a < actions.Length; a++)
                    nodes.Add(new AccessNode("action:" + a, AccessRole.Button, actions[a],
                        SearchCardLayout.ActionRect(count, true, a)));
            }
        }

        nodes.Add(new AccessNode("close", AccessRole.Button, "Close", SearchCardLayout.CloseRect()));
        return nodes;
    }

    /// <summary>What is said about a row after its name: its kind, why it matched, where it is.</summary>
    public static string Describe(SearchResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        string folder = Path.GetDirectoryName(r.Path) ?? r.Path;
        return string.Join(", ", new[] { FileKinds.Label(r.Kind).ToLowerInvariant(), r.Why, "in " + folder }
            .Where(t => t.Length > 0));
    }

    /// <summary>What is said when an answer lands: the header's own words.</summary>
    public static string Arrived(SearchCardState s) => SearchCardPainter.Header(s, s.Rows.Count).Left;
}
