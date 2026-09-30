using System.Globalization;
using SkiaSharp;

namespace Findra;

/// <summary>
/// The search card as a screen reader sees it: the field, the four pills, what the rows are, the
/// filter chips, the rows on screen, the three actions and the close button, each at the rectangle
/// the painter draws it in. The card's own keyboard needs nothing added - typing, arrows, Enter,
/// Tab through the chips and Escape already do everything.
///
/// <para>While the Advanced popup is open it is listed instead of what it covers: its ten fields,
/// two checks, six kind chips and three buttons, in the order they are drawn, beside the field,
/// the three pills above it and the close button. Inside it Tab walks every one of them, Space
/// presses a check, chip or button, and Enter presses a button or applies from anywhere else.</para>
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
        };

        // The popup covers the reading pill and everything under the pills: what it covers is not
        // there to be found or pressed while it is up, and pressing where it was drawn would land
        // on the popup.
        if (s.AdvOpen)
        {
            nodes.AddRange(Popup(s));
            nodes.Add(new AccessNode("close", AccessRole.Button, "Close", SearchCardLayout.CloseRect()));
            return nodes;
        }

        nodes.Add(new AccessNode("reading", AccessRole.Button, ReadingPill.Label(s.Reading) + " reading inside files", SearchCardLayout.ReadingRect())
            { Enabled = ReadingPill.Offers(s.Reading) });

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
                // Above the three, as it is drawn; unavailable until the highlighted file has
                // something to compare.
                if (s.OffersSimilar)
                    nodes.Add(new AccessNode("similar", AccessRole.Button, SearchCardPainter.SimilarLabel,
                        SearchCardLayout.SimilarRect(count, true))
                    {
                        Enabled = s.SimilarReady,
                        Help = "Files that look or read like " + s.Rows[s.Highlight].Name,
                    });
                IReadOnlyList<string> actions = SearchCardPainter.ActionLabels;
                for (int a = 0; a < actions.Count; a++)
                    nodes.Add(new AccessNode("action:" + a, AccessRole.Button, actions[a],
                        SearchCardLayout.ActionRect(count, true, a)));
            }
        }

        nodes.Add(new AccessNode("close", AccessRole.Button, "Close", SearchCardLayout.CloseRect()));
        return nodes;
    }

    /// <summary>The open popup's stops, in <see cref="SearchAdvancedLayout.Stops"/>' order.</summary>
    private static IEnumerable<AccessNode> Popup(SearchCardState s)
    {
        SearchAdvanced adv = s.Adv;
        foreach (SearchHit stop in SearchAdvancedLayout.Stops)
        {
            SKRect r = SearchAdvancedLayout.RectOf(stop);
            int i = stop.Index;
            yield return stop.Target switch
            {
                SearchTarget.AdvField => new AccessNode(PopupKey(stop), AccessRole.Edit, SearchAdvancedLayout.FieldName(i), r)
                {
                    Value = adv.Field(i),
                    Help = SearchAdvancedLayout.Placeholders[i],
                },
                SearchTarget.AdvCheck => new AccessNode(PopupKey(stop), AccessRole.Toggle, SearchAdvancedLayout.CheckLabels[i], r)
                    { On = i == 0 ? adv.MatchCase : adv.WholeWords },
                SearchTarget.AdvKind => new AccessNode(PopupKey(stop), AccessRole.Option, "Kind: " + SearchAdvanced.KindLabels[i], r)
                    { On = adv.Kind == i },
                SearchTarget.AdvButton => new AccessNode(PopupKey(stop), AccessRole.Button, SearchAdvancedLayout.ButtonLabels[i], r)
                {
                    // Apply says what it would search for: the line the painter draws beside it.
                    Help = i == 2 ? adv.Compose(s.Query.Trim()) : "",
                },
                _ => throw new InvalidOperationException($"{stop.Target} is not a stop in the popup"),
            };
        }
    }

    /// <summary>The automation key of a popup stop: <c>adv:field:3</c>, <c>adv:check:0</c>,
    /// <c>adv:kind:1</c>, <c>adv:button:2</c>.</summary>
    public static string PopupKey(SearchHit stop) => stop.Target switch
    {
        SearchTarget.AdvField => "adv:field:" + stop.Index.ToString(CultureInfo.InvariantCulture),
        SearchTarget.AdvCheck => "adv:check:" + stop.Index.ToString(CultureInfo.InvariantCulture),
        SearchTarget.AdvKind => "adv:kind:" + stop.Index.ToString(CultureInfo.InvariantCulture),
        SearchTarget.AdvButton => "adv:button:" + stop.Index.ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(stop), stop.Target, "not a stop in the popup"),
    };

    /// <summary>Which element has the keyboard: the popup's stop while it is open, otherwise the
    /// field, which has it whenever the card is up.</summary>
    public static string FocusedKey(SearchCardState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.AdvOpen ? PopupKey(s.AdvStop) : "field";
    }

    /// <summary>Where Tab (or Shift+Tab) takes the keyboard inside the open popup: the next stop in
    /// the order they are drawn, round from the last to the first.</summary>
    public static SearchHit NextStop(SearchCardState s, bool back)
    {
        ArgumentNullException.ThrowIfNull(s);
        IReadOnlyList<SearchHit> stops = SearchAdvancedLayout.Stops;
        int at = -1;
        for (int i = 0; i < stops.Count; i++) if (stops[i] == s.AdvStop) { at = i; break; }
        if (at < 0) return stops[back ? stops.Count - 1 : 0];
        return stops[(at + (back ? -1 : 1) + stops.Count) % stops.Count];
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
