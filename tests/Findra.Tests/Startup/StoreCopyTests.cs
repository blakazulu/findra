using System.Collections.Concurrent;

using Findra;
using Findra.Startup;
using Xunit;

/// <summary>
/// What a copy inside a Microsoft Store package does differently: the Store updates it, its folder
/// moves on every update, and its sign-in entry lives in the package. Each was a known gap in
/// docs/store.md before it was a behaviour.
/// </summary>
public class StoreCopyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-store-" + Guid.NewGuid().ToString("N"));

    public StoreCopyTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } GC.SuppressFinalize(this); }

    // ---- how it arrived ------------------------------------------------------------------------

    [Fact]
    public void APackagedCopyIsTheStoresWhateverTheFolderSays()
    {
        // No marker file exists in a package; without this a Store copy read as a source build and
        // was pointed at the GitHub release notes.
        Assert.Equal(InstallSource.Store, InstallSource.Detect(_dir, packaged: true));
        File.WriteAllText(Path.Combine(_dir, InstallSource.MarkerFile), "winget");
        Assert.Equal(InstallSource.Store, InstallSource.Detect(_dir, packaged: true));
    }

    [Fact]
    public void APackageOverridesWhatAnEarlierCopyRecorded()
    {
        // Recorded answers win because a marker can be lost. A package is not a guess, so it wins.
        Config earlier = Config.Default with { InstallSource = "installer" };
        Assert.Equal(InstallSource.Store, InstallSource.Resolve(earlier, _dir, packaged: true));
        Assert.Equal("installer", InstallSource.Resolve(earlier, _dir, packaged: false));
    }

    [Theory]
    [InlineData("store", true)]
    [InlineData("Store", true)]
    [InlineData("winget", false)]
    [InlineData(null, false)]
    public void IsStoreMatchesTheWayEveryOtherReaderDoes(string? source, bool expected) =>
        Assert.Equal(expected, InstallSource.IsStore(source));

    // ---- the update check ----------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStoreCopyNeverAsksGitHubEvenWhenSomebodyPressesCheck(bool manual)
    {
        bool called = false;
        Config store = Config.Default with { InstallSource = "store", CheckForUpdates = true };

        UpdateResult r = await UpdateCheck.CheckAsync(store,
            _ => { called = true; return Task.FromResult<LatestRelease?>(new LatestRelease("9.9.9", null)); },
            DateTime.UtcNow, default, manual);

        Assert.False(called);
        Assert.Equal(UpdateState.Disabled, r.State);
        Assert.Null(r.Config.LastUpdateCheck);
    }

    [Fact]
    public void TheWindowOpensOnTheStoresAnswerAndMakesNoCheck()
    {
        UpdateView v = UpdateFlow.Start("1.0.0", "store");
        Assert.Equal(UpdateStep.Store, v.Step);
        Assert.Equal(UpdateAction.None, UpdateFlow.Begin(v).Action);
        Assert.Equal(UpdateAction.Check, UpdateFlow.Begin(UpdateFlow.Start("1.0.0", "installer")).Action);
    }

    [Fact]
    public void ItsButtonsCloseOrOpenTheStore()
    {
        UpdateView v = UpdateFlow.Start("1.0.0", "store");
        Assert.Equal(UpdateAction.OpenStore, UpdateFlow.Go(v).Action);
        Assert.Equal(UpdateAction.Close, UpdateFlow.Close(v).Action);
        Assert.True(UpdateFlow.Closable(v));

        Assert.Equal(2, UpdatePrompt.Buttons(v));
        Assert.Equal("Open Store", UpdatePrompt.GoLabel(v));
        Assert.Equal("Close", UpdatePrompt.CloseLabel(v));
        Assert.Contains("Microsoft Store", UpdatePrompt.Title(v), StringComparison.Ordinal);
        Assert.Contains("1.0.0", UpdatePrompt.Body(v), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSessionOpensTheStoreAndNeverRunsTheCheck()
    {
        int checks = 0, stores = 0, closes = 0;
        var posted = new BlockingCollection<Action>();
        var session = new UpdateSession(
            UpdateFlow.Start("1.0.0", "store"),
            check: _ => { checks++; return new TaskCompletionSource<UpdateResult>().Task; },
            download: (_, _, _) => new TaskCompletionSource<DownloadResult>().Task,
            runInstaller: (_, _, _) => new TaskCompletionSource<Handoff>().Task,
            runWinget: _ => new TaskCompletionSource<Handoff>().Task,
            openReleases: () => { },
            show: _ => { },
            close: () => closes++,
            post: posted.Add,
            note: _ => { },
            openStore: () => stores++);

        session.Begin();
        Assert.Equal(0, checks);
        Assert.Equal(UpdateStep.Store, session.View.Step);

        session.PressGo();
        Assert.Equal(1, stores);
        Assert.Equal(1, closes);
    }

    [Fact]
    public void AboutSaysTheStoreUpdatesItAndDropsTheSwitchThatWouldChangeNothing()
    {
        var s = new SettingsState(Config.Default with { InstallSource = "store" }) { Section = Section.About };
        IReadOnlyList<Control> rows = SettingsModel.Controls(s);

        Assert.DoesNotContain(rows, c => c.Id == ControlId.CheckUpdates);
        Assert.Contains(rows, c => c.Id == ControlId.CheckNow);
        Assert.Equal("The Microsoft Store keeps Findra up to date.",
                     SettingsModel.AboutUpdateLine("1.0.0", UpdateState.Available, "2.0.0", "store"));

        var installer = new SettingsState(Config.Default with { InstallSource = "installer" }) { Section = Section.About };
        Assert.Contains(SettingsModel.Controls(installer), c => c.Id == ControlId.CheckUpdates);
    }

    // ---- the helper task's path ----------------------------------------------------------------

    [Fact]
    public void TheTasksCommandIsReadBackFromItsOwnDefinition()
    {
        const string exe = @"C:\Program Files\WindowsApps\LirazShakaAmir.Findra_0.7.2.0_x64__6wbaw9fmp3y9t\findra.exe";
        Assert.Equal(exe, HelperTask.CommandIn(HelperTask.BuildXml(exe)));
        Assert.Equal(@"D:\Tools & Utils\findra.exe", HelperTask.CommandIn(HelperTask.BuildXml(@"D:\Tools & Utils\findra.exe")));
        Assert.Null(HelperTask.CommandIn(""));
        Assert.Null(HelperTask.CommandIn("not xml"));
        Assert.Null(HelperTask.CommandIn("<Task/>"));
    }

    private const string Old = @"C:\Program Files\WindowsApps\LirazShakaAmir.Findra_0.7.2.0_x64__6wbaw9fmp3y9t\findra.exe";
    private const string New = @"C:\Program Files\WindowsApps\LirazShakaAmir.Findra_0.8.0.0_x64__6wbaw9fmp3y9t\findra.exe";

    [Fact]
    public void AStoreUpdateRewritesTheTaskEvenWhileTheOldFolderIsStillThere()
    {
        // Windows removes the replaced folder on its own schedule, so existence proves nothing.
        Assert.True(HelperTask.NeedsRewriting(Old, New, packaged: true, exists: _ => true));
    }

    [Fact]
    public void TheSameCopyIsNeverRewrittenWhateverTheCaseOrQuotes()
    {
        Assert.False(HelperTask.NeedsRewriting(New, New, packaged: true, exists: _ => false));
        Assert.False(HelperTask.NeedsRewriting(New.ToUpperInvariant(), New, packaged: true, exists: _ => false));
        Assert.False(HelperTask.NeedsRewriting(@"C:\Findra\.\findra.exe", @"C:\Findra\findra.exe", packaged: false, exists: _ => false));
    }

    [Fact]
    public void OutsideAPackageOnlyAGoneProgramIsReplaced()
    {
        // A build from source beside the installed copy must not take the task over (and ask for
        // permission) every time it runs; the installed copy would take it back the next time.
        const string installed = @"C:\Program Files\Findra\findra.exe";
        const string source = @"D:\Code\findra\src\Findra\bin\Debug\findra.exe";
        Assert.False(HelperTask.NeedsRewriting(installed, source, packaged: false, exists: _ => true));
        Assert.True(HelperTask.NeedsRewriting(installed, source, packaged: false, exists: _ => false));
    }

    [Fact]
    public void NoTaskOrNoRunningPathIsNothingToRewrite()
    {
        Assert.False(HelperTask.NeedsRewriting(null, New, packaged: true, exists: _ => false));
        Assert.False(HelperTask.NeedsRewriting(Old, "", packaged: true, exists: _ => false));
    }

    // ---- the listing ---------------------------------------------------------------------------

    [Fact]
    public void TheStorePageIsTheOneDocsStoreMdNames()
    {
        string doc = File.ReadAllText(Path.Combine(Repo.Root, "docs", "store.md"));
        Assert.Contains(StoreListing.ProductId, doc, StringComparison.Ordinal);
        Assert.StartsWith("ms-windows-store://pdp/?ProductId=", StoreListing.PageUri, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePackagedSignInEntryIsTheOneTheManifestDeclares()
    {
        string manifest = File.ReadAllText(Path.Combine(Repo.Root, "packaging", "store", "Package.appxmanifest"));
        Assert.Contains($"TaskId=\"{Autostart.PackageTaskId}\"", manifest, StringComparison.Ordinal);
    }
}
