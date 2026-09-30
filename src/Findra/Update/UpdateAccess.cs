using SkiaSharp;

namespace Findra;

/// <summary>The update window as a screen reader and the keyboard see it: what it says, the
/// download's progress, and its one or two buttons, at the rectangles the painter uses.</summary>
public static class UpdateAccess
{
    public const string Name = "Findra update";

    public static IReadOnlyList<AccessNode> Nodes(UpdateView v, SKTypeface face)
    {
        ArgumentNullException.ThrowIfNull(v);
        ArgumentNullException.ThrowIfNull(face);
        SKRect panel = UpdatePainter.Surface(v, face);
        var nodes = new List<AccessNode>
        {
            new("title", AccessRole.Text, UpdatePrompt.Title(v), panel) { Help = UpdatePrompt.Body(v) },
        };
        if (UpdatePrompt.HasBar(v))
            nodes.Add(new AccessNode("bar", AccessRole.Text, UpdatePrompt.Count(v), panel));

        int buttons = UpdatePrompt.Buttons(v);
        if (buttons >= 1)
            nodes.Add(new AccessNode("close", AccessRole.Button, UpdatePrompt.CloseLabel(v), UpdatePrompt.Button(panel, 0, buttons)));
        if (buttons == 2)
            nodes.Add(new AccessNode("go", AccessRole.Button, UpdatePrompt.GoLabel(v), UpdatePrompt.Button(panel, 1, buttons)));
        return nodes;
    }

    /// <summary>What is said when the window moves to another step: its title and what it says.</summary>
    public static string Announcement(UpdateView v) => $"{UpdatePrompt.Title(v)}. {UpdatePrompt.Body(v)}";
}
