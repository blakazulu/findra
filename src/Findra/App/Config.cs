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

    public static Config Default { get; } = new();

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

    /// <summary>Never throws for any input - a missing, empty, or corrupt string all give
    /// <see cref="Default"/> back.</summary>
    public static Config Load(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;

        try { return JsonSerializer.Deserialize<Config>(json, Opts) ?? Default; }
        catch (Exception ex)
        {
            Log.Warn("config", "config.json is not readable: " + ex.Message);
            return Default;
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
