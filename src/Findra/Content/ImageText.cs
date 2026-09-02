using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace Findra;

/// <summary>
/// The words <em>inside</em> a picture, read by the recognisers Windows itself ships
/// (<c>Windows.Media.Ocr</c>): on-device, quick, and already installed.
///
/// <para>Much of an ordinary picture library is screenshots, and a vision model only knows what
/// those look like - "a screenshot of a program". The words in them are what somebody actually
/// remembers, so the text goes into the full-text index and is embedded like a chunk of a
/// document.</para>
///
/// <para>This costs no download and has no model file, which is why it is not one of the
/// capabilities and has nothing to offer to install. It runs whenever a picture is being opened
/// anyway, and when a language's recogniser is not on the machine it contributes nothing and says
/// nothing: a recogniser that is not installed is an ordinary state of an ordinary machine, not a
/// failure to report.</para>
///
/// <para>Two engines run when both are present. English and Hebrew are separate recognisers and a
/// single screenshot is routinely both, so each is given the whole image; the one reading a script
/// that is not there hallucinates, and a result only counts when a fair share of it is letters of
/// its own script.</para>
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public static class ImageText
{
    private static Windows.Media.Ocr.OcrEngine? _en, _he;
    private static bool _init;
    private static readonly object Gate = new();

    private static void Init()
    {
        lock (Gate)
        {
            if (_init) return;
            _init = true;
            try
            {
                _en = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
                      ?? Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
                _he = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("he"))
                      ?? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("he-IL"));
                var tags = new List<string>();
                foreach (var l in Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages) tags.Add(l.LanguageTag);
                // An absent recogniser is recorded, not warned about: nothing here is broken when
                // a language pack was never installed, and the reading simply covers less.
                Log.Info("index", $"ocr: english {(_en is null ? "unavailable" : "ready")}, hebrew {(_he is null ? "unavailable" : "ready")} (recognisers: {string.Join(", ", tags)})");
            }
            catch (Exception ex)
            {
                Log.Once("index|ocr|init", "INFO", "index", $"ocr unavailable :: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>The readable text in an image, or "" when there is none (or no recogniser is
    /// installed).</summary>
    public static string Read(string path)
    {
        Init();
        if (_en is null && _he is null) return "";
        try { return ReadAsync(path).GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Log.Once($"index|ocr|{ex.GetType().Name}", "WARN", "index", $"ocr failed :: {ex.GetType().Name}: {ex.Message}");
            return "";
        }
    }

    private static async Task<string> ReadAsync(string path)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);

        // the engine's hard cap is OcrEngine.MaxImageDimension (2600): larger images are scaled
        // down at decode, which also makes a 48-megapixel photo cheap to read
        uint w = decoder.PixelWidth, h = decoder.PixelHeight;
        uint max = Windows.Media.Ocr.OcrEngine.MaxImageDimension;
        var transform = new Windows.Graphics.Imaging.BitmapTransform();
        if (w > max || h > max)
        {
            double s = Math.Min((double)max / w, (double)max / h);
            transform.ScaledWidth = (uint)(w * s);
            transform.ScaledHeight = (uint)(h * s);
        }
        using var bmp = await decoder.GetSoftwareBitmapAsync(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            transform,
            Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
            Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);

        string english = "", hebrew = "";
        if (_en is not null)
        {
            var r = await _en.RecognizeAsync(bmp);
            english = r.Text?.Trim() ?? "";
        }
        if (_he is not null)
        {
            var r = await _he.RecognizeAsync(bmp);
            hebrew = r.Text?.Trim() ?? "";
        }

        // a recogniser reading the wrong script hallucinates: keep a result only when a fair share
        // of it is letters of its own script
        if (!MostlyScript(english, latin: true)) english = "";
        if (!MostlyScript(hebrew, latin: false)) hebrew = "";
        if (english.Length == 0) return hebrew;
        if (hebrew.Length == 0) return english;
        return english + "\n" + hebrew;
    }

    /// <summary>
    /// Whether <paramref name="s"/> is really written in the script the recogniser that produced
    /// it reads.
    ///
    /// <para>Both engines are given the whole image, so one of them is always reading a script
    /// that is not there and returning something anyway. Without this rule every screenshot
    /// carries a line of nonsense into the full-text index, and nonsense in a full-text index is
    /// matches nobody asked for. Too short to judge is thrown away as well - four letters is not
    /// enough evidence either way.</para>
    /// </summary>
    public static bool MostlyScript(string s, bool latin)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Length < 8) return false;
        int own = 0, letters = 0;
        foreach (char c in s)
        {
            if (!char.IsLetter(c)) continue;
            letters++;
            bool heb = c >= '֐' && c <= '׿';
            if (latin ? !heb : heb) own++;
        }
        return letters >= 4 && own * 3 >= letters * 2;
    }
}
