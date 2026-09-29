using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Findra;

/// <summary>A few decoded previews, keyed on path. An image is disposed only when a full capacity
/// of newer ones has pushed it out, so a frame still drawing it cannot be pulled out from under.
/// Never dispose the current one on swap.</summary>
public sealed class PreviewCache : IDisposable
{
    private readonly int _capacity;
    private readonly LinkedList<(string Path, SKImage Image)> _list = new();

    public PreviewCache(int capacity) => _capacity = Math.Max(2, capacity);

    public SKImage? Get(string path)
    {
        for (var n = _list.First; n is not null; n = n.Next)
            if (n.Value.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                _list.Remove(n); _list.AddFirst(n);
                return n.Value.Image;
            }
        return null;
    }

    /// <summary>Keep <paramref name="image"/> for <paramref name="path"/> and return the image the
    /// cache now holds for it, which is the one to draw. When a second decode of the same file
    /// lands, the first is kept, the second is disposed, and the FIRST comes back: drawing the
    /// argument instead would put a disposed image on the card.</summary>
    public SKImage Put(string path, SKImage image)
    {
        if (Get(path) is { } existing)
        {
            if (!ReferenceEquals(existing, image)) image.Dispose();
            return existing;
        }
        _list.AddFirst((path, image));
        while (_list.Count > _capacity)
        {
            var last = _list.Last!;
            _list.RemoveLast();
            last.Value.Image.Dispose();
        }
        return image;
    }

    public void Dispose()
    {
        // the window is closing: nothing draws again, and the images go with it
        foreach (var (_, img) in _list) img.Dispose();
        _list.Clear();
    }
}
