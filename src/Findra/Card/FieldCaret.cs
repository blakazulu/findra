using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Findra;

// Where a caret is, and where it goes, in a field that can hold Hebrew.
//
// The field draws BidiText.ToVisual(text), so a logical caret index has to be turned into an x
// through the same reordering: after a Hebrew letter the caret sits at that letter's LEFT edge,
// after a Latin one at its RIGHT edge. Arrow keys move VISUALLY - Left goes left on the screen
// whatever the letters under it read.
//
// At the seam between a Hebrew run and a Latin one, ONE screen position holds TWO logical carets
// ("before the Hebrew word" and "before the Latin word" are the same x). Editors resolve that with
// an affinity; here every caret is a POSITION - a (slot, logical index) pair - and the field walks
// the list of positions in visual order. Right always moves right or stays put for one press at a
// seam, every logical index is reachable, and a click picks the position at the nearest slot.
// Every function is pure and runs off the same cell list, so the painter, the arrows and a click
// cannot disagree about where "there" is.
public static class FieldCaret
{
    /// <summary>One cluster in visual order: its logical char range, direction, and x extent.</summary>
    public readonly record struct Cell(int LogStart, int LogEnd, bool Rtl, float Left, float Right);

    /// <summary>A caret position: the visual slot (a gap between cells, 0..n) and the logical index.</summary>
    public readonly record struct Position(int Slot, int Caret);

    public static List<Cell> Cells(string text, SKTypeface face, float size)
    {
        var cells = new List<Cell>();
        if (string.IsNullOrEmpty(text)) return cells;
        var layout = BidiText.Layout(text);
        string visual = BidiText.ToVisual(text);
        int vpos = 0;
        float left = 0;
        foreach (var c in layout)
        {
            float right = CardText.Measure(visual[..(vpos + c.Length)], face, size);
            cells.Add(new Cell(c.Start, c.Start + c.Length, c.Rtl, left, right));
            left = right;
            vpos += c.Length;
        }
        return cells;
    }

    /// <summary>The x of a slot.</summary>
    public static float SlotX(List<Cell> cells, int slot)
    {
        if (cells.Count == 0) return 0;
        slot = Math.Clamp(slot, 0, cells.Count);
        return slot < cells.Count ? cells[slot].Left : cells[^1].Right;
    }

    /// <summary>Every caret position in visual order. A slot contributes the caret its LEFT
    /// neighbour puts there (after an LTR cell, before an RTL one) and the one its RIGHT neighbour
    /// puts there (before an LTR cell, after an RTL one); at a seam those differ and both are
    /// listed, left-derived first.</summary>
    public static List<Position> Positions(List<Cell> cells)
    {
        var list = new List<Position>();
        if (cells.Count == 0) { list.Add(new Position(0, 0)); return list; }
        for (int s = 0; s <= cells.Count; s++)
        {
            int? fromLeft = s > 0 ? (cells[s - 1].Rtl ? cells[s - 1].LogStart : cells[s - 1].LogEnd) : null;
            int? fromRight = s < cells.Count ? (cells[s].Rtl ? cells[s].LogEnd : cells[s].LogStart) : null;
            if (fromLeft is int l) list.Add(new Position(s, l));
            if (fromRight is int r && r != fromLeft) list.Add(new Position(s, r));
        }
        return list;
    }

    /// <summary>The position for a logical caret with no slot preference: the first in visual
    /// order that carries it (a fresh caret after typing, a click, an edit).</summary>
    public static Position Of(List<Cell> cells, string text, int caret, int slotHint = -1)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var ps = Positions(cells);
        if (slotHint >= 0)
            foreach (var p in ps) if (p.Slot == slotHint && p.Caret == caret) return p;
        // after typing a character the caret should sit AFTER it on its own side: prefer the
        // position derived from the character before the caret
        if (caret > 0)
        {
            for (int i = 0; i < cells.Count; i++)
                if (cells[i].LogEnd == caret && !cells[i].Rtl) return new Position(i + 1, caret);
            for (int i = 0; i < cells.Count; i++)
                if (cells[i].LogEnd == caret && cells[i].Rtl) return new Position(i, caret);
        }
        foreach (var p in ps) if (p.Caret == caret) return p;
        return new Position(cells.Count, text.Length);
    }

    /// <summary>One step left (-1) or right (+1) ON THE SCREEN. At a seam the first press changes
    /// the logical caret without moving; the next press moves.</summary>
    public static Position Move(List<Cell> cells, string text, Position from, int dir)
    {
        var ps = Positions(cells);
        int idx = ps.FindIndex(p => p == from);
        if (idx < 0) idx = ps.FindIndex(p => p.Caret == from.Caret);
        if (idx < 0) return from;
        idx = Math.Clamp(idx + Math.Sign(dir), 0, ps.Count - 1);
        return ps[idx];
    }

    /// <summary>The position a click at <paramref name="x"/> lands on: the nearest slot, and at a
    /// seam the caret its left neighbour puts there.</summary>
    public static Position AtX(List<Cell> cells, string text, float x)
    {
        if (cells.Count == 0) return new Position(0, 0);
        int best = 0; float bestD = float.MaxValue;
        for (int s = 0; s <= cells.Count; s++)
        {
            float d = Math.Abs(SlotX(cells, s) - x);
            if (d < bestD) { bestD = d; best = s; }
        }
        foreach (var p in Positions(cells)) if (p.Slot == best) return p;
        return new Position(cells.Count, text.Length);
    }
}
