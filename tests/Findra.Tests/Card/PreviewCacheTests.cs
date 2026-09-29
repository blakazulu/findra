using Findra;
using SkiaSharp;
using Xunit;

/// <summary>
/// The card decodes a preview off the UI thread and caches it when it lands. Moving the highlight
/// A, B, A before the first decode of A finishes starts a second decode of the same file, and the
/// two arrive one after the other. Only one can be kept; the one the card goes on to draw has to be
/// the one that was kept, never the one that was thrown away.
/// </summary>
public class PreviewCacheTests
{
    private static SKImage Image()
    {
        using var bitmap = new SKBitmap(2, 2);
        return SKImage.FromBitmap(bitmap);
    }

    [Fact]
    public void ASecondDecodeOfTheSamePathHandsBackTheImageAlreadyKept()
    {
        using var cache = new PreviewCache(4);
        SKImage first = Image(), second = Image();

        SKImage keptFirst = cache.Put("a.jpg", first);
        SKImage keptSecond = cache.Put("A.JPG", second);

        // ReferenceEquals, never Assert.Same: a failing Same prints both objects, and printing a
        // disposed SKImage is the very access violation this guards against - it takes the whole
        // test host down instead of failing one test.
        Assert.True(ReferenceEquals(first, keptFirst));
        Assert.True(ReferenceEquals(first, keptSecond), "the card would draw the decode that was thrown away");
        Assert.NotEqual(IntPtr.Zero, keptSecond.Handle);
    }

    [Fact]
    public void PuttingTheImageAlreadyKeptKeepsItAlive()
    {
        using var cache = new PreviewCache(4);
        SKImage image = Image();

        cache.Put("a.jpg", image);
        SKImage kept = cache.Put("a.jpg", image);

        Assert.True(ReferenceEquals(image, kept));
        Assert.NotEqual(IntPtr.Zero, kept.Handle);
    }

    [Fact]
    public void AnImagePushedOutByAFullCapacityOfNewerOnesIsDisposed()
    {
        using var cache = new PreviewCache(2);
        SKImage oldest = Image();
        cache.Put("1.jpg", oldest);
        cache.Put("2.jpg", Image());
        cache.Put("3.jpg", Image());

        Assert.Null(cache.Get("1.jpg"));
        Assert.Equal(IntPtr.Zero, oldest.Handle);
    }
}
