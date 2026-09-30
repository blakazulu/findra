using Findra;
using Findra.Diagnostics;
using SkiaSharp;
using Xunit;

/// <summary>
/// What a screen reader and the keyboard are told about the drawn surfaces. The rule every test
/// here holds: an element is where it is drawn, and pressing its centre is a press on it - because
/// that is exactly what invoking it from Narrator or from the keyboard does.
/// </summary>
public class AccessTests
{
    // ---- focus -----------------------------------------------------------------------------------

    private static AccessNode N(string key, AccessRole role = AccessRole.Button, bool enabled = true) =>
        new(key, role, key, SKRect.Create(0, 0, 10, 10)) { Enabled = enabled };

    [Fact]
    public void TabWalksTheActionableElementsInOrderAndWrapsBothWays()
    {
        AccessNode[] nodes = [N("title", AccessRole.Text), N("a"), N("off", enabled: false), N("b", AccessRole.Toggle), N("c", AccessRole.Option)];
        Assert.Equal("a", AccessFocus.Next(nodes, null, back: false));
        Assert.Equal("b", AccessFocus.Next(nodes, "a", back: false));
        Assert.Equal("c", AccessFocus.Next(nodes, "b", back: false));
        Assert.Equal("a", AccessFocus.Next(nodes, "c", back: false));
        Assert.Equal("c", AccessFocus.Next(nodes, "a", back: true));
        Assert.Equal("c", AccessFocus.Next(nodes, null, back: true));
    }

    [Fact]
    public void FocusOnSomethingThatHasGoneStartsAgainAtTheTop()
    {
        AccessNode[] nodes = [N("a"), N("b")];
        Assert.Equal("a", AccessFocus.Next(nodes, "scrolled-away", back: false));
        Assert.Null(AccessFocus.Next([N("t", AccessRole.Text)], null, back: false));
    }

    [Fact]
    public void WhatIsSaidCarriesTheStateAndTheLineUnderIt()
    {
        var toggle = new AccessNode("t", AccessRole.Toggle, "Put what I open first", SKRect.Empty) { On = true, Help = "Off forgets it." };
        Assert.Equal("Put what I open first, on, Off forgets it.", toggle.Spoken);
        var tab = new AccessNode("s", AccessRole.Tab, "About", SKRect.Empty) { On = true };
        Assert.Equal("About, selected", tab.Spoken);
        var off = new AccessNode("b", AccessRole.Button, "Content", SKRect.Empty) { Enabled = false };
        Assert.Equal("Content, unavailable", off.Spoken);
    }

    private static void KeysAreUnique(IReadOnlyList<AccessNode> nodes) =>
        Assert.Equal(nodes.Count, nodes.Select(n => n.Key).Distinct().Count());

    // ---- settings ------------------------------------------------------------------------------

    public static IEnumerable<object[]> Sections() => RailLayout.Sections.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Sections))]
    public void EverySettingsControlIsPressedWhereItIsDrawn(Section section)
    {
        var s = new SettingsState(Config.Default) { Section = section };
        IReadOnlyList<AccessNode> nodes = SettingsAccess.Nodes(s, Parts.Face);
        KeysAreUnique(nodes);
        IReadOnlyList<int> options = SettingsModel.OptionCounts(s), notes = SettingsModel.NoteLines(s, Parts.Face);

        foreach (AccessNode n in nodes.Where(n => n.Role is not AccessRole.Text))
        {
            PanelHit hit = RailLayout.HitTest(n.Bounds.MidX, n.Bounds.MidY, options, notes, SettingsModel.ListRows(s));
            PanelTarget want = n.Key switch
            {
                var k when k.StartsWith("section:", StringComparison.Ordinal) => PanelTarget.Section,
                "close" => PanelTarget.Close,
                var k when k.StartsWith("unskip:", StringComparison.Ordinal) => PanelTarget.ListRemove,
                _ => n.Role == AccessRole.Option ? PanelTarget.Option : PanelTarget.Control,
            };
            Assert.True(hit.Target == want, $"{section}: '{n.Name}' is pressed at its centre as {hit.Target}, not {want}");
        }

        Assert.Contains(nodes, n => n.Role == AccessRole.Tab && n.On && n.Name == RailLayout.Title(section));
    }

    [Fact]
    public void TheRemoveQuestionIsTheOnlyThingOfferedWhileItIsUp()
    {
        var s = new SettingsState(Config.Default)
        {
            Section = Section.AddOns,
            Removing = Capability.Photos,
            Installed = new CapabilitySet(new HashSet<Capability> { Capability.Photos }),
        };
        IReadOnlyList<AccessNode> nodes = SettingsAccess.Nodes(s, Parts.Face);
        Assert.DoesNotContain(nodes, n => n.Key.StartsWith("section:", StringComparison.Ordinal));
        Assert.Contains(nodes, n => n.Key == "remove:cancel");
        Assert.Contains(nodes, n => n.Key == "remove:go");
    }

    // ---- the first-run screen ------------------------------------------------------------------

    private static FirstRunState Choosing(bool speech = false) => new()
    {
        Chosen = speech ? Capabilities.Close([Capability.Photos, Capability.Speech]) : Capabilities.Close([Capability.Photos, Capability.Meaning]),
        HebrewOffered = true,
        ContentOn = true,
        CheckUpdates = true,
        StartAtLogon = true,
    };

    public static IEnumerable<object[]> FirstRunStates()
    {
        yield return [Choosing()];
        yield return [Choosing(speech: true)];
        yield return [Choosing() with { Stage = FirstRunStage.Finished, Hotkey = "Alt+Space" }];
        yield return [Choosing() with { Stage = FirstRunStage.Downloading, Hotkey = "Alt+Space",
            Downloads = [new CapabilityProgress(Capability.Photos, 10, 100)] }];
    }

    [Theory]
    [MemberData(nameof(FirstRunStates))]
    public void EveryFirstRunControlIsPressedWhereItIsDrawn(FirstRunState s)
    {
        IReadOnlyList<AccessNode> nodes = FirstRunAccess.Nodes(s);
        KeysAreUnique(nodes);
        foreach (AccessNode n in nodes.Where(n => n.Actionable))
        {
            FirstRunHit hit = FirstRunLayout.HitTest(n.Bounds.MidX, n.Bounds.MidY, s);
            FirstRunTarget want = n.Key.Split(':')[0] switch
            {
                "preset" => FirstRunTarget.Preset,
                "row" => FirstRunTarget.Row,
                "limit" => FirstRunTarget.Limit,
                "switch" => n.Key == "switch:0" ? FirstRunTarget.Content : n.Key == "switch:1" ? FirstRunTarget.Updates : FirstRunTarget.Autostart,
                "notnow" => FirstRunTarget.NotNow,
                "go" => FirstRunTarget.Go,
                "settings" => FirstRunTarget.Settings,
                "link" => FirstRunTarget.Link,
                _ => FirstRunTarget.None,
            };
            Assert.True(hit.Target == want, $"'{n.Name}' is pressed at its centre as {hit.Target}, not {want}");
            // The switches answer by target alone; everything else numbered answers by index too.
            if (n.Key.Contains(':', StringComparison.Ordinal) && !n.Key.StartsWith("switch:", StringComparison.Ordinal))
                Assert.Equal(int.Parse(n.Key.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture), hit.Index);
        }
        Assert.Contains(nodes, n => n.Key == "go");
    }

    [Fact]
    public void TheSpeechLimitIsOfferedOnlyWhenSpeechIsTicked()
    {
        Assert.DoesNotContain(FirstRunAccess.Nodes(Choosing()), n => n.Key.StartsWith("limit:", StringComparison.Ordinal));
        Assert.Contains(FirstRunAccess.Nodes(Choosing(speech: true)), n => n.Key.StartsWith("limit:", StringComparison.Ordinal) && n.On);
    }

    [Fact]
    public void WhileTheWorkCardIsUpNothingIsOfferedToPress()
    {
        FirstRunState s = Choosing() with { Work = FirstRunWork.SettingUp(downloading: true) };
        Assert.DoesNotContain(FirstRunAccess.Nodes(s), n => n.Actionable);
    }

    // ---- the update window ---------------------------------------------------------------------

    [Theory]
    [InlineData(UpdateStep.Checking, 0)]
    [InlineData(UpdateStep.UpToDate, 1)]
    [InlineData(UpdateStep.Available, 2)]
    [InlineData(UpdateStep.Store, 2)]
    public void TheUpdateWindowOffersItsButtonsWhereTheyAreDrawn(UpdateStep step, int buttons)
    {
        var installer = new ReleaseAsset("findra-setup-x64.exe", "https://github.com/x", 100, new string('0', 64));
        UpdateView v = new(step, "1.0.0", step == UpdateStep.Store ? "store" : "installer") { Latest = "1.1.0", Installer = installer };
        IReadOnlyList<AccessNode> nodes = UpdateAccess.Nodes(v, Parts.Face);
        SKRect panel = UpdatePainter.Surface(v, Parts.Face);
        var pressable = nodes.Where(n => n.Actionable).ToList();
        Assert.Equal(buttons, pressable.Count);
        foreach (AccessNode n in pressable)
            Assert.Equal(n.Key == "go" ? UpdatePromptTarget.Go : UpdatePromptTarget.Close,
                UpdatePrompt.HitTest(n.Bounds.MidX, n.Bounds.MidY, panel, buttons));
        Assert.StartsWith(UpdatePrompt.Title(v), UpdateAccess.Announcement(v), StringComparison.Ordinal);
    }

    // ---- the card ------------------------------------------------------------------------------

    private static SearchCardState Results()
    {
        List<SearchResult> rows = [.. Enumerable.Range(0, 12).Select(i =>
            new SearchResult(ResultKind.Document, $"report-{i}.pdf", $@"C:\Docs\report-{i}.pdf", 0.8f, "in the name"))];
        var r = new SearchResults("report", rows, 1, 0, false);
        return SearchCardState.Empty with { Query = "report", Results = r, Rows = rows, Highlight = 4, Scroll = 2 };
    }

    [Fact]
    public void EveryCardElementIsPressedWhereItIsDrawn()
    {
        SearchCardState s = Results();
        IReadOnlyList<AccessNode> nodes = CardAccess.Nodes(s);
        KeysAreUnique(nodes);
        foreach (AccessNode n in nodes.Where(n => n.Role is not AccessRole.Text))
        {
            SearchHit hit = SearchCardLayout.HitTest(n.Bounds.MidX, n.Bounds.MidY, s.Rows.Count, s.Scroll, s.ShowsBody, s.AdvOpen, s.OffersSimilar);
            SearchTarget want = n.Key.Split(':')[0] switch
            {
                "field" => SearchTarget.Field,
                "content" => SearchTarget.Content,
                "advanced" => SearchTarget.Adv,
                "settings" => SearchTarget.Settings,
                "reading" => SearchTarget.Reading,
                "chip" => SearchTarget.Chip,
                "row" => SearchTarget.Row,
                "action" => n.Key == "action:0" ? SearchTarget.Open : n.Key == "action:1" ? SearchTarget.Reveal : SearchTarget.Copy,
                "close" => SearchTarget.Close,
                "similar" => SearchTarget.Similar,
                _ => SearchTarget.None,
            };
            Assert.True(hit.Target == want, $"'{n.Name}' is pressed at its centre as {hit.Target}, not {want}");
        }
    }

    [Fact]
    public void OnlyTheRowsOnScreenAreListedAndTheHighlightedOneIsSelected()
    {
        SearchCardState s = Results();
        var rows = CardAccess.Nodes(s).Where(n => n.Role == AccessRole.Item).ToList();
        Assert.Equal(SearchCardLayout.VisibleRows(s.Rows.Count), rows.Count);
        Assert.Equal("report-2.pdf", rows[0].Name);
        AccessNode selected = Assert.Single(rows, r => r.On);
        Assert.Equal("report-4.pdf", selected.Name);
        Assert.Contains(@"in C:\Docs", selected.Help, StringComparison.Ordinal);
    }

    // ---- the Advanced popup --------------------------------------------------------------------

    public static IEnumerable<object[]> PopupStates()
    {
        yield return [SearchCardState.Empty with { AdvOpen = true }];
        yield return [Results() with { AdvOpen = true }];
        yield return [SearchShot.Build("adv")];
        yield return [Results() with { AdvOpen = true, AdvFocusOn = SearchTarget.AdvKind, AdvFocus = 3,
            AdvRules = new SearchAdvanced(MatchCase: true, Kind: 3, SizeTo: "10mb") }];
    }

    /// <summary>What a key names, as the hit test answers it: the stricter form, target AND index,
    /// the one the first-run screen is held to.</summary>
    private static SearchHit Expected(AccessNode n, SearchCardState s)
    {
        string[] parts = n.Key.Split(':');
        int Index(int at) => int.Parse(parts[at], System.Globalization.CultureInfo.InvariantCulture);
        return parts[0] switch
        {
            "field" => new SearchHit(SearchTarget.Field, -1),
            "content" => new SearchHit(SearchTarget.Content, -1),
            "advanced" => new SearchHit(SearchTarget.Adv, -1),
            "settings" => new SearchHit(SearchTarget.Settings, -1),
            "reading" => new SearchHit(SearchTarget.Reading, -1),
            "close" => new SearchHit(SearchTarget.Close, -1),
            "similar" => new SearchHit(SearchTarget.Similar, -1),
            "chip" => new SearchHit(SearchTarget.Chip, Index(1)),
            "row" => new SearchHit(SearchTarget.Row, s.Rows.ToList().FindIndex(r => "row:" + r.Path == n.Key)),
            "action" => new SearchHit(Index(1) switch { 0 => SearchTarget.Open, 1 => SearchTarget.Reveal, _ => SearchTarget.Copy }, -1),
            "adv" => new SearchHit(parts[1] switch
            {
                "field" => SearchTarget.AdvField,
                "check" => SearchTarget.AdvCheck,
                "kind" => SearchTarget.AdvKind,
                "button" => SearchTarget.AdvButton,
                _ => SearchTarget.None,
            }, Index(2)),
            _ => SearchHit.None,
        };
    }

    [Theory]
    [MemberData(nameof(PopupStates))]
    public void EveryElementOfTheOpenPopupIsPressedWhereItIsDrawn(SearchCardState s)
    {
        IReadOnlyList<AccessNode> nodes = CardAccess.Nodes(s);
        KeysAreUnique(nodes);
        foreach (AccessNode n in nodes.Where(n => n.Actionable))
        {
            SearchHit hit = SearchCardLayout.HitTest(n.Bounds.MidX, n.Bounds.MidY, s.Rows.Count, s.Scroll, s.ShowsBody, s.AdvOpen, s.OffersSimilar);
            Assert.False(hit.Target == SearchTarget.None, $"'{n.Name}' is pressed at its centre as nothing (index {hit.Index})");
            Assert.True(hit == Expected(n, s), $"'{n.Name}' is pressed at its centre as {hit}, not {Expected(n, s)}");
        }
    }

    [Fact]
    public void EveryCardElementIsPressedAsItselfAndNotItsNeighbour()
    {
        SearchCardState s = Results();
        foreach (AccessNode n in CardAccess.Nodes(s).Where(n => n.Actionable))
            Assert.Equal(Expected(n, s), SearchCardLayout.HitTest(n.Bounds.MidX, n.Bounds.MidY, s.Rows.Count, s.Scroll, s.ShowsBody, s.AdvOpen, s.OffersSimilar));
    }

    [Fact]
    public void ThePopupIsListedWhileItIsOpenInsteadOfWhatItCovers()
    {
        IReadOnlyList<AccessNode> closed = CardAccess.Nodes(Results());
        Assert.DoesNotContain(closed, n => n.Key.StartsWith("adv:", StringComparison.Ordinal));

        IReadOnlyList<AccessNode> open = CardAccess.Nodes(Results() with { AdvOpen = true });
        string[] kept = ["field", "content", "advanced", "settings", "close"];
        Assert.Equal(kept, open.Where(n => !n.Key.StartsWith("adv:", StringComparison.Ordinal)).Select(n => n.Key));
        Assert.True(open.Single(n => n.Key == "advanced").On);
        Assert.Equal(SearchAdvancedLayout.Stops.Count, open.Count(n => n.Key.StartsWith("adv:", StringComparison.Ordinal)));
        // In the order they are drawn, which is the order Tab walks.
        Assert.Equal(SearchAdvancedLayout.Stops.Select(CardAccess.PopupKey),
                     open.Where(n => n.Key.StartsWith("adv:", StringComparison.Ordinal)).Select(n => n.Key));
    }

    [Fact]
    public void TabWalksEveryStopInThePopupInTheOrderTheyAreDrawn()
    {
        string[] order =
        [
            "adv:field:0", "adv:field:1", "adv:field:2", "adv:field:3", "adv:check:0", "adv:check:1",
            "adv:field:4", "adv:field:5", "adv:kind:0", "adv:kind:1", "adv:kind:2", "adv:kind:3",
            "adv:kind:4", "adv:kind:5", "adv:field:6", "adv:field:7", "adv:field:8", "adv:field:9",
            "adv:button:0", "adv:button:1", "adv:button:2",
        ];
        SearchCardState s = SearchCardState.Empty with { AdvOpen = true };
        var forward = new List<string> { CardAccess.FocusedKey(s) };
        for (int i = 0; i < order.Length; i++)
        {
            SearchHit next = CardAccess.NextStop(s, back: false);
            s = s with { AdvFocusOn = next.Target, AdvFocus = next.Index };
            forward.Add(CardAccess.FocusedKey(s));
        }
        Assert.Equal([.. order, order[0]], forward);

        SearchHit back = CardAccess.NextStop(SearchCardState.Empty with { AdvOpen = true }, back: true);
        Assert.Equal("adv:button:2", CardAccess.PopupKey(back));
        back = CardAccess.NextStop(SearchCardState.Empty with { AdvOpen = true, AdvFocusOn = back.Target, AdvFocus = back.Index }, back: true);
        Assert.Equal("adv:button:1", CardAccess.PopupKey(back));
    }

    [Fact]
    public void TheFocusedKeyFollowsThePopupAndIsTheFieldOtherwise()
    {
        Assert.Equal("field", CardAccess.FocusedKey(Results()));
        Assert.Equal("adv:field:0", CardAccess.FocusedKey(Results() with { AdvOpen = true }));
        Assert.Equal("adv:check:1", CardAccess.FocusedKey(Results() with { AdvOpen = true, AdvFocusOn = SearchTarget.AdvCheck, AdvFocus = 1 }));
        // The key names a node that exists, so a screen reader is told focus is on something real.
        SearchCardState s = Results() with { AdvOpen = true, AdvFocusOn = SearchTarget.AdvButton, AdvFocus = 2 };
        Assert.NotNull(AccessFocus.Find(CardAccess.Nodes(s), CardAccess.FocusedKey(s)));
    }

    [Fact]
    public void ThePopupSaysWhatItHoldsAsTheShotDrawsIt()
    {
        SearchCardState s = SearchShot.Build("adv");
        IReadOnlyList<AccessNode> nodes = CardAccess.Nodes(s);
        AccessNode Node(string key) => nodes.Single(n => n.Key == key);

        AccessNode all = Node("adv:field:0");
        Assert.Equal(AccessRole.Edit, all.Role);
        Assert.Equal("All these words", all.Name);
        Assert.Equal("lake cabin", all.Value);
        Assert.Equal(SearchAdvancedLayout.Placeholders[0], all.Help);
        Assert.Equal("draft", Node("adv:field:3").Value);
        Assert.Equal("2026-01-01", Node("adv:field:6").Value);
        Assert.Equal("Modified to", Node("adv:field:7").Name);
        Assert.Equal("", Node("adv:field:7").Value);
        Assert.Equal("1mb", Node("adv:field:8").Value);
        Assert.Equal("Size to", Node("adv:field:9").Name);

        Assert.Equal(AccessRole.Toggle, Node("adv:check:0").Role);
        Assert.Equal("Match case", Node("adv:check:0").Name);
        Assert.False(Node("adv:check:0").On);
        Assert.Equal("Whole words", Node("adv:check:1").Name);

        // Kind 1 is chosen in the shot, and that is the one option said to be selected.
        AccessNode kind = Assert.Single(nodes, n => n.Key.StartsWith("adv:kind:", StringComparison.Ordinal) && n.On);
        Assert.Equal("Kind: Photos", kind.Name);
        Assert.Equal(AccessRole.Option, kind.Role);

        Assert.Equal(["Clear", "Close", "Apply"], nodes.Where(n => n.Key.StartsWith("adv:button:", StringComparison.Ordinal)).Select(n => n.Name));
        // Apply says what it would search for: the composed line drawn beside the buttons.
        Assert.Equal(s.Adv.Compose(s.Query.Trim()), Node("adv:button:2").Help);
        Assert.Contains("type:photo", Node("adv:button:2").Help, StringComparison.Ordinal);

        Assert.Equal("Match case, on", (CardAccess.Nodes(s with { AdvRules = s.Adv with { MatchCase = true } })
            .Single(n => n.Key == "adv:check:0")).Spoken);
    }

    [Fact]
    public void AnEmptyCardOffersTheFieldAndThePillsAndSaysWhatTheFieldIsFor()
    {
        IReadOnlyList<AccessNode> nodes = CardAccess.Nodes(SearchCardState.Empty);
        AccessNode field = nodes.Single(n => n.Key == "field");
        Assert.Equal(AccessRole.Edit, field.Role);
        Assert.Equal(SearchCardPainter.NamePlaceholder, field.Help);
        Assert.DoesNotContain(nodes, n => n.Role == AccessRole.Item);
        Assert.Equal("3 results for “report”", CardAccess.Arrived(Results() with
        {
            Rows = Results().Rows.Take(3).ToList(),
        }));
    }
}
