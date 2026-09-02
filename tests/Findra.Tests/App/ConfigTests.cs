using System.Reflection;
using Findra;
using Xunit;

public class ConfigTests
{
    [Fact]
    public void DefaultsAreMondAndPaperFollowingWindows()
    {
        Config c = Config.Default;
        Assert.Equal("Mond", c.DarkPalette);
        Assert.Equal("Paper", c.LightPalette);
        Assert.Equal(ThemeMode.FollowWindows, c.Mode);
        Assert.True(c.ShowCapsule);
        Assert.True(c.CheckForUpdates);
    }

    [Fact]
    public void RoundTripsEveryField()
    {
        var c = Config.Default with
        {
            DarkPalette = "Verdigris", LightPalette = "Blueprint", Mode = ThemeMode.AlwaysDark,
            Hotkey = "Ctrl+Alt+F", CapsuleX = 120, CapsuleY = 900, ShowCapsule = false,
            CheckForUpdates = false, LastUpdateCheck = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
            LatestKnownVersion = "1.9.0", InstallSource = "winget",
        };

        Config back = Config.Load(c.ToJson());

        Assert.Equal(c, back);
    }

    [Fact]
    public void ACapsuleThatWasNeverDraggedHasNoSavedPosition()
    {
        // Null, not (0,0): the shell asks HasValue rather than guessing from the numbers.
        Assert.Null(Config.Default.CapsuleX);
        Assert.Null(Config.Default.CapsuleY);
    }

    [Fact]
    public void ACapsuleParkedAtTheOriginComesBackAsZeroAndNotAsNeverPlaced()
    {
        // The top-left corner of the primary monitor is somewhere a user is allowed to leave the
        // capsule, and it has to be found there again on the next launch.
        Config back = Config.Load((Config.Default with { CapsuleX = 0, CapsuleY = 0 }).ToJson());
        Assert.NotNull(back.CapsuleX);
        Assert.NotNull(back.CapsuleY);
        Assert.Equal(0, back.CapsuleX!.Value);
        Assert.Equal(0, back.CapsuleY!.Value);
    }

    [Fact]
    public void AMissingFileGivesTheDefaults()
    {
        Assert.Equal(Config.Default, Config.Load(null));
        Assert.Equal(Config.Default, Config.Load(""));
    }

    [Fact]
    public void BrokenJsonGivesTheDefaultsRatherThanThrowing()
    {
        // Someone's settings file must never be able to stop the app starting.
        Assert.Equal(Config.Default, Config.Load("{ not json"));
    }

    [Fact]
    public void AnUnknownFieldIsIgnoredAndTheRestSurvive()
    {
        // Forward compatibility: a newer Findra's file must not break an older one.
        Config c = Config.Load("""{ "darkPalette": "Brass", "somethingFromTheFuture": 42 }""");
        Assert.Equal("Brass", c.DarkPalette);
        Assert.Equal("Paper", c.LightPalette);
    }

    [Fact]
    public void AnUnknownModeFallsBackRatherThanThrowing()
    {
        Assert.Equal(ThemeMode.FollowWindows, Config.Load("""{ "mode": "Purple" }""").Mode);
    }

    [Fact]
    public void AFieldOfTheWrongTypeCostsOnlyThatField()
    {
        // A hand-edited file that says mode:1 instead of mode:"AlwaysDark" must not throw away
        // every OTHER setting on its way past. Losing one field to a typo is forgivable; losing
        // the palette, the hotkey and the capsule position along with it is not.
        Config c = Config.Load("""{ "darkPalette": "Brass", "mode": 1, "hotkey": "Ctrl+Alt+F" }""");

        Assert.Equal(ThemeMode.FollowWindows, c.Mode);
        Assert.Equal("Brass", c.DarkPalette);
        Assert.Equal("Ctrl+Alt+F", c.Hotkey);
    }

    [Fact]
    public void TheContentIndexDefaultsAreTheOnesTheSpecPromises()
    {
        Config c = Config.Default;

        Assert.Equal(FileKinds.DefaultExclusions, c.SearchExclusions);
        Assert.Empty(c.IndexDrives);          // empty means every fixed NTFS volume
        // Reads the same as the IndexPaused assertion it replaces and means the OPPOSITE:
        // false now means "do not read inside files", not "do not pause".
        Assert.False(c.IndexContent);
        Assert.Equal(50, c.IndexPower);
    }

    [Fact]
    public void TheDefaultExclusionsAreACopyNotTheSharedTable()
    {
        // FileKinds.DefaultExclusions is one static array and Config.Default is one static
        // record, so handing the reference out means any caller that writes to a Config's
        // exclusions rewrites the table for the whole process - including the copy every
        // future Config starts from.
        Assert.NotSame(FileKinds.DefaultExclusions, Config.Default.SearchExclusions);
        Assert.NotSame(Config.Default.SearchExclusions, Config.Load(null).SearchExclusions);
    }

    [Fact]
    public void TwoConfigsWithEqualExclusionListsAreEqual()
    {
        // A record compares an array member by REFERENCE. Without an explicit Equals, a
        // config that round-trips through JSON is never equal to the one that was saved,
        // and every equality assertion in this file becomes a test of object identity.
        var a = Config.Default with { SearchExclusions = [@"\a\", @"\b\"], IndexDrives = ["C"] };
        var b = Config.Default with { SearchExclusions = [@"\a\", @"\b\"], IndexDrives = ["C"] };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, Config.Default with { SearchExclusions = [@"\a\"], IndexDrives = ["C"] });
        Assert.NotEqual(a, Config.Default with { SearchExclusions = [@"\a\", @"\b\"], IndexDrives = ["D"] });
    }

    [Fact]
    public void EveryPropertyIsPartOfEquality()
    {
        // The guard. A hand-written Equals is a list somebody must keep in step with the
        // class, and the first draft of this change left LatestKnownVersion out - which made
        // RoundTripsEveryField pass while no longer covering it. This closes the CLASS of
        // bug: add a property, forget to add it to Equals, and this fails naming the property.
        foreach (PropertyInfo p in typeof(Config).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.SetMethod is null) continue;                       // computed, not state
            object? changed = Mutate(p.PropertyType, p.GetValue(Config.Default));
            Config other = Clone(Config.Default, p, changed);

            Assert.False(Config.Default.Equals(other),
                $"Config.Equals ignores '{p.Name}' - a change to it is invisible to equality, " +
                "so a value that fails to round-trip will not be caught by any test in this file.");
        }
    }

    /// <summary>A value of this type that differs from <paramref name="current"/>.</summary>
    private static object? Mutate(Type t, object? current) => t switch
    {
        _ when t == typeof(string)     => (string?)current == "x" ? "y" : "x",
        _ when t == typeof(string[])   => new[] { "\\findra-guard\\" },
        _ when t == typeof(bool)       => !(bool)current!,
        _ when t == typeof(int)        => (int)current! + 7,
        _ when t == typeof(int?)       => ((int?)current ?? 0) + 7,
        _ when t == typeof(DateTime?)  => ((DateTime?)current ?? DateTime.UnixEpoch).AddDays(1),
        _ when t == typeof(ThemeMode)  => (ThemeMode)current! == ThemeMode.AlwaysDark
                                            ? ThemeMode.AlwaysLight : ThemeMode.AlwaysDark,
        _ => throw new Xunit.Sdk.XunitException(
                 $"ConfigTests.Mutate has no case for {t.Name}. A new property type was added; " +
                 "teach this helper about it rather than deleting the guard."),
    };

    /// <summary>Config.Default with exactly one property replaced.</summary>
    private static Config Clone(Config from, PropertyInfo swap, object? value)
    {
        var clone = (Config)from.GetType()
            .GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(from, null)!;
        swap.SetValue(clone, value);        // init-only setters are ordinary setters to reflection
        return clone;
    }

    [Fact]
    public void TheContentFieldsRoundTrip()
    {
        var c = Config.Default with
        {
            SearchExclusions = [@"\Windows\", @"\my stuff\"],
            IndexDrives = ["C", "E"],
            IndexContent = true,
            TranscribeMinutes = TranscribeLimit.NoLimit,
            IndexPower = 25,
        };

        Config back = Config.Load(c.ToJson());

        Assert.Equal(c, back);
        Assert.Equal([@"\Windows\", @"\my stuff\"], back.SearchExclusions);
        Assert.Equal(25, back.IndexPower);
    }

    [Fact]
    public void AnEmptyExclusionListIsHonouredAndIsNotTheDefaults()
    {
        // Someone who empties the list means it. Treating empty as "unset" and substituting
        // the defaults would make the setting impossible to turn off.
        Config c = Config.Load("""{ "searchExclusions": [] }""");
        Assert.Empty(c.SearchExclusions);
    }

    [Fact]
    public void AnAbsurdPowerSettingIsClampedRatherThanObeyed()
    {
        Assert.Equal(10, Config.Load("""{ "indexPower": 0 }""").IndexPower);
        Assert.Equal(10, Config.Load("""{ "indexPower": -5 }""").IndexPower);
        Assert.Equal(100, Config.Load("""{ "indexPower": 4000 }""").IndexPower);
    }

    [Fact]
    public void ReadingInsideFilesIsOffUntilSomebodyAsksForIt()
    {
        // Spec §6, and it is the one place the product deliberately does less until asked. The
        // assertion reads the same as the IndexPaused one it replaces and means the opposite:
        // false now means "do not read inside files", not "do not pause".
        Assert.False(Config.Default.IndexContent);
        Assert.False(Config.Load(null).IndexContent);
        Assert.False(Config.Load("{}").IndexContent);
    }

    [Fact]
    public void TheTranscriptionLimitDefaultsToTheCheapPreset()
    {
        Assert.Equal(5, Config.Default.TranscribeMinutes);
        Assert.Equal("5 minutes", TranscribeLimit.Describe(Config.Default.TranscribeMinutes));
    }

    /// <summary>`{ "transcribeMinutes": 0 }` - off, spelled out, because zero is a real setting.</summary>
    private const string JsonZero = "{ \"transcribeMinutes\": 0 }";

    [Fact]
    public void ANegativeTranscriptionLimitSurvivesTheRoundTrip()
    {
        // "No limit" is a negative number, and a clamp added "for safety" would silently turn
        // the most expensive setting in the product into the cheapest.
        Config c = Config.Default with { TranscribeMinutes = TranscribeLimit.NoLimit };
        Assert.Equal(TranscribeLimit.NoLimit, Config.Load(c.ToJson()).TranscribeMinutes);
        Assert.Equal(0, Config.Load(JsonZero).TranscribeMinutes);
    }
}
