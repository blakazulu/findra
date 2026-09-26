using System;

namespace Findra;

// Editing the card's field the way every text box on Windows edits: a selection runs from an
// ANCHOR to the caret, typing and pasting replace it, Backspace and Delete take it before they
// take a character, and moving without Shift collapses it. The field is painted rather than a
// TextBox, so none of that comes from the platform.
//
// Indices are LOGICAL (positions in the string). Where they sit on the screen is FieldCaret's
// business, which is the only place Hebrew's reordering is known about. Every function is pure,
// so the keyboard, the mouse and the painter read one answer.
public static class FieldEdit
{
    /// <summary>The field's text, its caret, and the other end of the selection; an
    /// <paramref name="Anchor"/> of -1, or one on the caret, is no selection.</summary>
    public readonly record struct Text(string Value, int Caret, int Anchor = -1);

    public static bool HasSelection(Text t) => t.Anchor >= 0 && t.Anchor != t.Caret;

    /// <summary>The selection as a start and an end, whichever way it was made.</summary>
    public static (int Lo, int Hi) Range(Text t)
    {
        int caret = Math.Clamp(t.Caret, 0, t.Value.Length);
        if (!HasSelection(t)) return (caret, caret);
        int anchor = Math.Clamp(t.Anchor, 0, t.Value.Length);
        return (Math.Min(anchor, caret), Math.Max(anchor, caret));
    }

    public static string Selected(Text t)
    {
        var (lo, hi) = Range(t);
        return t.Value[lo..hi];
    }

    public static Text SelectAll(Text t)
        => t.Value.Length == 0 ? new Text(t.Value, 0) : new Text(t.Value, t.Value.Length, 0);

    /// <summary>Move the caret and keep the anchor: Shift with an arrow, Home or End, and a drag.
    /// The anchor is dropped where the caret was when the selection starts.</summary>
    public static Text Extend(Text t, int caret)
    {
        caret = Math.Clamp(caret, 0, t.Value.Length);
        int anchor = t.Anchor >= 0 ? t.Anchor : t.Caret;
        return anchor == caret ? new Text(t.Value, caret) : new Text(t.Value, caret, anchor);
    }

    /// <summary>An arrow without Shift over a selection: the caret goes to the selection's end in
    /// that direction and moves no further.</summary>
    public static Text Collapse(Text t, int dir)
    {
        var (lo, hi) = Range(t);
        return new Text(t.Value, dir < 0 ? lo : hi);
    }

    /// <summary>Typing or pasting: replaces the selection, or goes in at the caret, and the
    /// result is cut at <paramref name="max"/> characters.</summary>
    public static Text Insert(Text t, string s, int max)
    {
        var (lo, hi) = Range(t);
        string value = t.Value[..lo] + s + t.Value[hi..];
        int caret = lo + s.Length;
        if (value.Length > max) { value = value[..max]; caret = Math.Min(caret, max); }
        return new Text(value, caret);
    }

    public static Text Backspace(Text t, bool word)
    {
        if (HasSelection(t)) return Cut(t);
        int c = Math.Clamp(t.Caret, 0, t.Value.Length);
        if (c == 0) return new Text(t.Value, 0);
        int from = word ? WordLeft(t.Value, c) : c - 1;
        return new Text(t.Value.Remove(from, c - from), from);
    }

    public static Text Delete(Text t, bool word)
    {
        if (HasSelection(t)) return Cut(t);
        int c = Math.Clamp(t.Caret, 0, t.Value.Length);
        if (c >= t.Value.Length) return new Text(t.Value, c);
        int to = word ? WordRight(t.Value, c) : c + 1;
        return new Text(t.Value.Remove(c, to - c), c);
    }

    /// <summary>The text with its selection taken out; what Ctrl+X leaves behind.</summary>
    public static Text Cut(Text t)
    {
        var (lo, hi) = Range(t);
        return new Text(t.Value.Remove(lo, hi - lo), lo);
    }

    /// <summary>Ctrl+Left: back over any spaces, then back over a word.</summary>
    public static int WordLeft(string q, int from)
    {
        int i = Math.Clamp(from, 0, q.Length);
        while (i > 0 && q[i - 1] == ' ') i--;
        while (i > 0 && q[i - 1] != ' ') i--;
        return i;
    }

    /// <summary>Ctrl+Right: over a word, then over the spaces after it.</summary>
    public static int WordRight(string q, int from)
    {
        int i = Math.Clamp(from, 0, q.Length);
        while (i < q.Length && q[i] != ' ') i++;
        while (i < q.Length && q[i] == ' ') i++;
        return i;
    }

    /// <summary>What a double-click at <paramref name="at"/> takes: the word under it, the word
    /// that ends there when it lands just after one, or else the run of spaces it is in.</summary>
    public static (int Lo, int Hi) WordAt(string q, int at)
    {
        if (q.Length == 0) return (0, 0);
        int i = Math.Clamp(at, 0, q.Length);
        if (i == q.Length || (q[i] == ' ' && i > 0 && q[i - 1] != ' ')) i--;
        bool space = q[i] == ' ';
        int lo = i, hi = i + 1;
        while (lo > 0 && (q[lo - 1] == ' ') == space) lo--;
        while (hi < q.Length && (q[hi] == ' ') == space) hi++;
        return (lo, hi);
    }
}
