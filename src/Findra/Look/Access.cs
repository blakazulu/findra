using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using SkiaSharp;

namespace Findra;

/// <summary>What a drawn element is to a screen reader.</summary>
public enum AccessRole { Button, Toggle, Edit, Item, Tab, Option, Text, Link }

/// <summary>
/// One element of a drawn surface as assistive technology sees it: what it is, what it is called,
/// where it is in the surface's LAYOUT units, and its state. Every surface in Findra is painted
/// with Skia onto one control, so without these Narrator found a window with nothing in it.
///
/// <para>Built by pure functions from the same state and the same layout the painter and the hit
/// test read, so a screen reader is told what is drawn - never a second description of it that
/// can drift. <see cref="Key"/> is stable while the element exists; it is what keeps one automation
/// element per control across repaints.</para>
/// </summary>
public sealed record AccessNode(string Key, AccessRole Role, string Name, SKRect Bounds)
{
    /// <summary>The line under a control, read after its name.</summary>
    public string Help { get; init; } = "";

    /// <summary>The text of a field, or what a text element says beyond its name.</summary>
    public string Value { get; init; } = "";

    /// <summary>A toggle's state, or whether a tab, option or item is the selected one.</summary>
    public bool On { get; init; }

    /// <summary>False for a control that is drawn but not offering, which is announced as such.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Whether keyboard focus can rest on it, and whether pressing it does anything.</summary>
    public bool Actionable => Enabled && Role is not AccessRole.Text;

    /// <summary>What the focus announcement says: the name, the state, and the line under it.</summary>
    public string Spoken => string.Join(", ", new[]
    {
        Name,
        Role switch
        {
            AccessRole.Toggle => On ? "on" : "off",
            AccessRole.Tab or AccessRole.Option or AccessRole.Item => On ? "selected" : "",
            AccessRole.Edit => Value,
            _ => "",
        },
        Enabled ? "" : "unavailable",
        Help,
    }.Where(s => s.Length > 0));
}

/// <summary>What a drawn surface offers assistive technology and the keyboard.</summary>
public interface IAccessibleSurface
{
    /// <summary>The surface's own name: "Findra search", "Findra settings".</summary>
    string AccessName { get; }

    /// <summary>Every element as drawn right now, in reading order.</summary>
    IReadOnlyList<AccessNode> AccessNodes();

    /// <summary>Layout units to the control's pixels (the fit a surface is drawn at).</summary>
    double AccessScale { get; }

    /// <summary>Do what pressing it does. Always the surface's own press path.</summary>
    void AccessInvoke(AccessNode node);

    /// <summary>Set a field's text; ignored by anything that is not an edit.</summary>
    void AccessSetValue(AccessNode node, string value) { }

    /// <summary>The key of the element keyboard focus is on, or null.</summary>
    string? FocusedKey { get; }
}

/// <summary>
/// Moves keyboard focus through the actionable elements in reading order. Pure, so Tab and
/// Shift+Tab are tested without a window. Focus that has gone (a row that scrolled away, a panel
/// that closed) starts again from the first element rather than from nowhere.
/// </summary>
public static class AccessFocus
{
    public static string? Next(IReadOnlyList<AccessNode> nodes, string? current, bool back)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var stops = nodes.Where(n => n.Actionable).ToList();
        if (stops.Count == 0) return null;
        int at = current is null ? -1 : stops.FindIndex(n => n.Key == current);
        if (at < 0) return back ? stops[^1].Key : stops[0].Key;
        int next = (at + (back ? -1 : 1) + stops.Count) % stops.Count;
        return stops[next].Key;
    }

    public static AccessNode? Find(IReadOnlyList<AccessNode> nodes, string? key) =>
        key is null ? null : nodes.FirstOrDefault(n => n.Key == key);
}

/// <summary>
/// The automation peer for a drawn surface's control: its children are the surface's
/// <see cref="AccessNode"/>s, and one more - a polite live region that says where keyboard focus
/// went, because a drawn element cannot take Windows' focus itself.
/// </summary>
public sealed class AccessiblePeer : ControlAutomationPeer
{
    private readonly IAccessibleSurface _surface;
    private readonly LivePeer _live;
    private string[] _keys = [];

    public AccessiblePeer(Avalonia.Controls.Control owner, IAccessibleSurface surface) : base(owner)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _live = new LivePeer(this);
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
    protected override string GetNameCore() => _surface.AccessName;
    protected override bool IsKeyboardFocusableCore() => true;
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;

    protected override IReadOnlyList<AutomationPeer> GetOrCreateChildrenCore() => Children();
    protected override IReadOnlyList<AutomationPeer> GetChildrenCore() => Children();

    // Peers are pooled by kind and position rather than made per element: each one subscribes to
    // the canvas's own events for the life of the canvas, and a list of results that changes with
    // every keystroke would otherwise leave a trail of them behind. The pool is as large as the
    // most elements of a kind a surface has ever shown at once.
    private readonly Dictionary<Type, List<NodePeer>> _pool = [];

    private List<AutomationPeer> Children()
    {
        IReadOnlyList<AccessNode> nodes = _surface.AccessNodes();
        var list = new List<AutomationPeer>(nodes.Count + 1);
        var used = new Dictionary<Type, int>();
        foreach (AccessNode n in nodes)
        {
            Type kind = NodePeer.KindOf(n.Role);
            if (!_pool.TryGetValue(kind, out List<NodePeer>? peers)) _pool[kind] = peers = [];
            int i = used.TryGetValue(kind, out int k) ? k : 0;
            used[kind] = i + 1;
            if (i == peers.Count) peers.Add(NodePeer.For(this, n));
            NodePeer p = peers[i];
            p.Node = n;
            list.Add(p);
        }
        list.Add(_live);
        return list;
    }

    /// <summary>The surface changed which elements exist (a new list of results, another section).
    /// Cheap to call on every repaint that changes the key set; it compares first.</summary>
    public void Refresh()
    {
        string[] keys = [.. _surface.AccessNodes().Select(n => n.Key)];
        if (keys.AsSpan().SequenceEqual(_keys)) return;
        _keys = keys;
        InvalidateChildren();
    }

    /// <summary>Say something now, politely: keyboard focus moved, or a count changed.</summary>
    public void Say(string text) => _live.Say(text);

    internal IAccessibleSurface Surface => _surface;

    /// <summary>Layout rectangle to the root's coordinates, which is what a bounding rectangle is
    /// measured in.</summary>
    internal Rect ToRoot(SKRect r)
    {
        double s = _surface.AccessScale;
        Visual? root = TopLevel.GetTopLevel(Owner);
        Point origin = root is null ? default : Owner.TranslatePoint(default, root) ?? default;
        return new Rect(origin.X + r.Left * s, origin.Y + r.Top * s, r.Width * s, r.Height * s);
    }

    /// <summary>
    /// One drawn element. A <see cref="ControlAutomationPeer"/> over the canvas only because that is
    /// the one kind of peer Windows can place on the screen; everything it says is the element's.
    /// </summary>
    internal class NodePeer : ControlAutomationPeer
    {
        protected readonly AccessiblePeer Surface;
        public AccessNode Node { get; set; }

        protected NodePeer(AccessiblePeer surface, AccessNode node) : base(surface.Owner)
        {
            Surface = surface;
            Node = node;
        }

        public static Type KindOf(AccessRole role) => role switch
        {
            AccessRole.Toggle => typeof(TogglePeer),
            AccessRole.Edit => typeof(EditPeer),
            AccessRole.Tab or AccessRole.Option or AccessRole.Item => typeof(SelectPeer),
            AccessRole.Text => typeof(NodePeer),
            _ => typeof(InvokePeer),
        };

        public static NodePeer For(AccessiblePeer surface, AccessNode node) => node.Role switch
        {
            AccessRole.Toggle => new TogglePeer(surface, node),
            AccessRole.Edit => new EditPeer(surface, node),
            AccessRole.Tab or AccessRole.Option or AccessRole.Item => new SelectPeer(surface, node),
            AccessRole.Text => new NodePeer(surface, node),
            _ => new InvokePeer(surface, node),
        };

        protected override AutomationControlType GetAutomationControlTypeCore() => Node.Role switch
        {
            AccessRole.Button => AutomationControlType.Button,
            AccessRole.Toggle => AutomationControlType.CheckBox,
            AccessRole.Edit => AutomationControlType.Edit,
            AccessRole.Item => AutomationControlType.ListItem,
            AccessRole.Tab => AutomationControlType.TabItem,
            AccessRole.Option => AutomationControlType.RadioButton,
            AccessRole.Link => AutomationControlType.Hyperlink,
            _ => AutomationControlType.Text,
        };

        protected override string GetNameCore() => Node.Name;
        protected override string? GetHelpTextCore() => Node.Help;
        protected override string GetAutomationIdCore() => Node.Key;
        protected override string GetClassNameCore() => "Findra." + Node.Role;
        protected override string? GetAcceleratorKeyCore() => null;
        protected override string? GetAccessKeyCore() => null;
        protected override AutomationPeer? GetLabeledByCore() => null;
        protected override Rect GetBoundingRectangleCore() => Surface.ToRoot(Node.Bounds);
        protected override IReadOnlyList<AutomationPeer> GetOrCreateChildrenCore() => [];
        protected override IReadOnlyList<AutomationPeer> GetChildrenCore() => [];
        protected override AutomationPeer? GetParentCore() => Surface;
        protected override bool HasKeyboardFocusCore() => Surface.Surface.FocusedKey == Node.Key;
        protected override bool IsContentElementCore() => true;
        protected override bool IsControlElementCore() => true;
        protected override bool IsEnabledCore() => Node.Enabled;
        protected override bool IsKeyboardFocusableCore() => Node.Actionable;
        protected override bool IsOffscreenCore() => false;
        protected override void SetFocusCore() { }
        protected override bool ShowContextMenuCore() => false;
        protected override bool TrySetParent(AutomationPeer? parent) => false;
        protected override void BringIntoViewCore() { }

        protected void Press()
        {
            if (!Node.Enabled) throw new ElementNotEnabledException();
            Surface.Surface.AccessInvoke(Node);
        }
    }

    internal sealed class InvokePeer(AccessiblePeer surface, AccessNode node) : NodePeer(surface, node), IInvokeProvider
    {
        public void Invoke() => Press();
    }

    internal sealed class TogglePeer(AccessiblePeer surface, AccessNode node) : NodePeer(surface, node), IToggleProvider
    {
        public ToggleState ToggleState => Node.On ? ToggleState.On : ToggleState.Off;
        public void Toggle() => Press();
    }

    internal sealed class SelectPeer(AccessiblePeer surface, AccessNode node)
        : NodePeer(surface, node), ISelectionItemProvider, IInvokeProvider
    {
        public bool IsSelected => Node.On;
        public ISelectionProvider? SelectionContainer => null;
        public void Select() { if (!Node.On) Press(); }
        public void AddToSelection() => Select();
        public void RemoveFromSelection() { }
        public void Invoke() => Press();
    }

    internal sealed class EditPeer(AccessiblePeer surface, AccessNode node) : NodePeer(surface, node), IValueProvider
    {
        public bool IsReadOnly => !Node.Enabled;
        public string? Value => Node.Value;
        public void SetValue(string? value) => Surface.Surface.AccessSetValue(Node, value ?? "");
    }

    /// <summary>The announcement: a text element whose name is the last thing said, polite, so a
    /// screen reader reads it when it changes without interrupting what it was saying.</summary>
    internal sealed class LivePeer(AccessiblePeer surface) : NodePeer(surface, new AccessNode("live", AccessRole.Text, "", SKRect.Empty))
    {
        protected override AutomationLiveSetting GetLiveSettingCore() => AutomationLiveSetting.Polite;
        protected override bool IsControlElementCore() => false;
        protected override bool IsKeyboardFocusableCore() => false;

        public void Say(string text)
        {
            string was = Node.Name;
            // The same words twice would change nothing, and nothing would be read.
            if (text == was) text += " ";
            Node = Node with { Name = text };
            RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, was, text);
        }
    }
}

/// <summary>
/// Where keyboard focus is, drawn: the accent, two pixels, just outside the element. Shown only
/// once the keyboard has been used, and gone again at the next click, so a mouse user never sees a
/// ring they did not ask for.
/// </summary>
public static class AccessRing
{
    public const float Outset = 3f;
    public const float Width = 2f;

    public static void Draw(SKCanvas canvas, SKRect r, Derived d)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(d);
        SKRect ring = SKRect.Inflate(r, Outset, Outset);
        using var p = new SKPaint { Color = d.Accent, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Width };
        canvas.DrawRoundRect(new SKRoundRect(ring, Math.Min(ring.Height / 2, 10f)), p);
    }
}
