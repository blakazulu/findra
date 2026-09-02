using System.Text.Json;
using System.Text.Json.Serialization;

namespace Findra;

/// <summary>Follow Windows needs a dark pick and a light pick to switch between; the other
/// two modes pin one side and ignore the other.</summary>
public enum ThemeMode
{
    FollowWindows,
    AlwaysDark,
    AlwaysLight,
}

/// <summary>
/// Everything the app remembers between runs. A record with init properties, so a change is
/// always a `with` expression and never a mutation in place.
///
/// Nothing here throws. A hand-edited or half-written file costs its owner the defaults and a
/// line in the log, never a launch.
/// </summary>
public sealed record Config
{
    public string DarkPalette { get; init; } = "Mond";
    public string LightPalette { get; init; } = "Paper";
    public ThemeMode Mode { get; init; } = ThemeMode.FollowWindows;
    public string Hotkey { get; init; } = "Alt+Space";
    /// <summary>Where the capsule was left, in physical pixels, or null when it has never been
    /// dragged anywhere. Null rather than a sentinel: (0,0) is the top-left corner of the primary
    /// monitor, which is a place a user is entitled to park the capsule and expect to find it
    /// again, so it cannot also mean "no saved position".</summary>
    public int? CapsuleX { get; init; }
    public int? CapsuleY { get; init; }
    public bool ShowCapsule { get; init; } = true;
    public bool CheckForUpdates { get; init; } = true;
    public DateTime? LastUpdateCheck { get; init; }

    /// <summary>The release tag the last successful check returned, or null when no check has
    /// ever come back with one. Remembered so the ~23 launches in 24 that are not due for a check
    /// can still say whether an update is waiting, instead of going quiet for a day.</summary>
    public string? LatestKnownVersion { get; init; }

    public string? InstallSource { get; init; }

    /// <summary>Path fragments the indexer will not open. Names stay searchable regardless -
    /// this decides only what is read from inside a file.</summary>
    public string[] SearchExclusions { get; init; } = [.. FileKinds.DefaultExclusions];

    /// <summary>Drive letters to index. Empty means every fixed NTFS volume, which is what
    /// almost everyone wants and what a fresh install does.</summary>
    public string[] IndexDrives { get; init; } = [];

    /// <summary>Whether Findra reads the CONTENTS of files at all. Off by default, and that is
    /// the whole point (spec §6): a name index costs seconds and no disk reading, while looking
    /// inside files walks every drive, opens every document, and on a large disk runs for hours.
    /// Findra does not start that on its own - not even for the free, model-free document text.
    ///
    /// <para>One bit, not two. An "enabled" flag beside a "paused" flag is two settings that can
    /// disagree, and there is no honest sentence for the disagreement. What the interface says is
    /// derived from this and from how much has already been read.</para></summary>
    public bool IndexContent { get; init; }

    /// <summary>How long a recording is worth transcribing, in minutes: zero is off, a negative
    /// value is no limit, and any positive number is the limit itself (spec §6). It covers audio
    /// and video together, deliberately - an asymmetry between them would be invisible in the
    /// interface and surprising in use. The named choices in the settings screen are PRESETS OVER
    /// THIS NUMBER, so there is nothing here for a preset name to disagree with.
    ///
    /// <para>Not clamped. A negative value is meaningful and a very large one is simply a limit
    /// nothing reaches, so there is nothing to protect the user from.</para></summary>
    public int TranscribeMinutes { get; init; } = TranscribeLimit.Default;

    /// <summary>A duty cycle, 10..100. At 50 the indexer rests as long as it worked.</summary>
    public int IndexPower { get; init; } = 50;

    /// <summary>Whether the first-run screen has been answered. "Not now" answers it: content
    /// indexing is off by default, so choosing nothing is a complete answer rather than a
    /// deferral, and a screen that came back would be asking a settled question twice.</summary>
    public bool FirstRunDone { get; init; }

    public static Config Default { get; } = new();

    // The compiler-generated record equality compares SearchExclusions and IndexDrives by
    // reference, which fails RoundTripsEveryField the moment those arrays exist - a config
    // loaded back from JSON is never the same array instance as the one that was saved. Every
    // property on the class appears in both methods below; ConfigTests.EveryPropertyIsPartOfEquality
    // is the guard that catches the next property someone adds and forgets here.
    public bool Equals(Config? other) =>
        other is not null
        && DarkPalette == other.DarkPalette && LightPalette == other.LightPalette
        && Mode == other.Mode && Hotkey == other.Hotkey
        && CapsuleX == other.CapsuleX && CapsuleY == other.CapsuleY
        && ShowCapsule == other.ShowCapsule && CheckForUpdates == other.CheckForUpdates
        && LastUpdateCheck == other.LastUpdateCheck
        && LatestKnownVersion == other.LatestKnownVersion
        && InstallSource == other.InstallSource
        && IndexContent == other.IndexContent && IndexPower == other.IndexPower
        && FirstRunDone == other.FirstRunDone
        && TranscribeMinutes == other.TranscribeMinutes
        && SearchExclusions.AsSpan().SequenceEqual(other.SearchExclusions)
        && IndexDrives.AsSpan().SequenceEqual(other.IndexDrives);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(DarkPalette); h.Add(LightPalette); h.Add(Mode); h.Add(Hotkey);
        h.Add(CapsuleX); h.Add(CapsuleY); h.Add(ShowCapsule); h.Add(CheckForUpdates);
        h.Add(LastUpdateCheck); h.Add(LatestKnownVersion); h.Add(InstallSource);
        h.Add(IndexContent); h.Add(IndexPower); h.Add(TranscribeMinutes); h.Add(FirstRunDone);
        foreach (string s in SearchExclusions) h.Add(s);
        foreach (string s in IndexDrives) h.Add(s);
        return h.ToHashCode();
    }

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new ThemeModeConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Opts);

    /// <summary>Never throws for any input - a missing, empty, or corrupt string all give a
    /// value equal to <see cref="Default"/> back. A fresh instance rather than the shared
    /// singleton, so <see cref="SearchExclusions"/> and <see cref="IndexDrives"/> are never
    /// the same array a caller elsewhere is holding on <see cref="Default"/> itself.</summary>
    public static Config Load(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Config();

        try
        {
            Config c = JsonSerializer.Deserialize<Config>(json, Opts) ?? new Config();
            return c with { IndexPower = Math.Clamp(c.IndexPower, 10, 100) };
        }
        catch (Exception ex)
        {
            Log.Warn("config", "config.json is not readable: " + ex.Message);
            return new Config();
        }
    }

    public static Config LoadFromDisk()
    {
        try
        {
            string path = Paths.ConfigFile;
            return Load(File.Exists(path) ? File.ReadAllText(path) : null);
        }
        catch (Exception ex)
        {
            Log.Warn("startup", "could not read config.json: " + ex.Message);
            return Default;
        }
    }

    /// <summary>Saving settings must never take the app down.</summary>
    public void Save()
    {
        try
        {
            Paths.Ensure(Paths.Config);
            File.WriteAllText(Paths.ConfigFile, ToJson());
        }
        catch (Exception ex)
        {
            Log.Warn("config", "could not write config.json: " + ex.Message);
        }
    }

    // JsonStringEnumConverter throws on an unrecognised value, which would turn a stray typo
    // in a hand-edited file into a startup failure. This falls back to FollowWindows instead.
    private sealed class ThemeModeConverter : JsonConverter<ThemeMode>
    {
        public override ThemeMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // GetString throws on any token that is not a string, and that throw escapes the whole
            // deserialisation - so a single mistyped field would discard every other setting in the
            // file. A wrong TYPE gets the same forgiving treatment as a wrong VALUE.
            if (reader.TokenType != JsonTokenType.String) return ThemeMode.FollowWindows;
            string? s = reader.GetString();
            return Enum.TryParse<ThemeMode>(s, ignoreCase: true, out var mode) ? mode : ThemeMode.FollowWindows;
        }

        public override void Write(Utf8JsonWriter writer, ThemeMode value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
