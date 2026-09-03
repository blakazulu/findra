namespace Findra;

/// <summary>
/// What the mouse pointer looks like over one part of one surface.
///
/// <para>Four shapes and no more, deliberately: a hand for anything that answers a click, an
/// I-beam for the one place text is typed, the four-way arrow for the one thing that is dragged
/// rather than pressed, and the plain arrow for the air between them.</para>
///
/// <para>Named <c>PointerShape</c> rather than <c>Pointer</c> because a type in this namespace
/// beats one arriving through a using directive, and every window here has
/// <c>using Avalonia.Input;</c> at the top of it.</para>
/// </summary>
public enum PointerShape { Arrow, Hand, Text, Move }

/// <summary>
/// Which shape belongs over which part of each surface.
///
/// <para>Findra paints all four of its surfaces itself, so nothing about a rectangle tells
/// Windows what is under the pointer: without this, a capsule that has been draggable since it
/// was written, a field that takes typing and every pill and row in the product all showed the
/// same plain arrow, and nothing on any of them invited the gesture it wanted.</para>
///
/// <para>A pure function of the hit test's own answer, so the shape and the behaviour can never
/// disagree about what is under the pointer, and so a target added to one of the three enums and
/// forgotten here throws where a test sees it rather than quietly taking the arrow.</para>
/// </summary>
public static class Pointers
{
    /// <summary>The capsule is one object with no parts: the whole of it is picked up and moved,
    /// and a press that does not travel opens the card.</summary>
    public static PointerShape OverCapsule => PointerShape.Move;

    public static PointerShape ForCard(SearchTarget target) => target switch
    {
        SearchTarget.None => PointerShape.Arrow,
        // The two places a caret goes. Everything else on the card is pressed, not typed into.
        SearchTarget.Field or SearchTarget.AdvField => PointerShape.Text,
        SearchTarget.Chip or SearchTarget.Row or SearchTarget.Open or SearchTarget.Reveal
            or SearchTarget.Copy or SearchTarget.Stage or SearchTarget.Content or SearchTarget.Adv
            or SearchTarget.Settings or SearchTarget.AdvCheck or SearchTarget.AdvKind
            or SearchTarget.AdvButton => PointerShape.Hand,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "no pointer shape for this card target"),
    };

    public static PointerShape ForPanel(PanelTarget target) => target switch
    {
        PanelTarget.None => PointerShape.Arrow,
        PanelTarget.Section or PanelTarget.Control or PanelTarget.Option
            or PanelTarget.ListItem or PanelTarget.ListRemove or PanelTarget.Close => PointerShape.Hand,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "no pointer shape for this panel target"),
    };

    public static PointerShape ForFirstRun(FirstRunTarget target) => target switch
    {
        FirstRunTarget.None => PointerShape.Arrow,
        FirstRunTarget.Preset or FirstRunTarget.Row or FirstRunTarget.Limit or FirstRunTarget.Content
            or FirstRunTarget.Updates or FirstRunTarget.Autostart or FirstRunTarget.NotNow
            or FirstRunTarget.Go => PointerShape.Hand,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "no pointer shape for this first-run target"),
    };
}
