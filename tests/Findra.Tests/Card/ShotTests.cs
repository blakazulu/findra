using Findra;
using Findra.Diagnostics;
using SkiaSharp;
using Xunit;

public class ShotTests
{
    public static TheoryData<string, string> EveryStateInEveryPalette()
    {
        var d = new TheoryData<string, string>();
        foreach (string s in SearchShot.States)
            foreach (Palette p in Palette.BuiltIn) d.Add(s, p.Name);
        return d;
    }

    [Theory, MemberData(nameof(EveryStateInEveryPalette))]
    public void EveryStateRendersInEveryPalette(string state, string palette)
    {
        // 42 renders. This is the whole safety net for a repaint that has no unit tests:
        // if any state in any palette throws or comes out empty, it fails here rather than
        // on someone's desktop.
        string path = Path.Combine(Path.GetTempPath(), $"findra-shot-{state}-{palette}.png");
        try
        {
            Assert.Equal(0, SearchShot.Render(path, state, palette));
            Assert.True(new FileInfo(path).Length > 1024, "the PNG is suspiciously small");

            using SKBitmap bmp = SKBitmap.Decode(path);
            Assert.True(bmp.Width > 100 && bmp.Height > 50);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void AnUnknownStateOrPaletteFailsLoudlyRatherThanRenderingSomethingWrong()
    {
        string path = Path.Combine(Path.GetTempPath(), "findra-shot-bad.png");
        Assert.NotEqual(0, SearchShot.Render(path, "nonesuch", "Mond"));
        Assert.NotEqual(0, SearchShot.Render(path, "results", "Nonesuch"));
    }
}
