using System.Diagnostics;

using Findra;
using Findra.Pipe;
using Xunit;

/// <summary>
/// Names on drives whose file table Findra does not read: sticks, memory cards, exFAT and FAT32
/// disks, ReFS. Each test walks a real folder standing in for a drive's root, which is what the
/// walk sees on a stick too.
/// </summary>
public class WalkedVolumeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "findra-walk-" + Guid.NewGuid().ToString("N"));

    public WalkedVolumeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private string Make(string relative, string? content = "x")
    {
        string full = Path.Combine(_root, relative);
        if (content is null) Directory.CreateDirectory(full);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return full;
    }

    private static readonly IDisposable Nothing = new Empty();
    private sealed class Empty : IDisposable { public void Dispose() { } }

    private NameIndex Walked()
    {
        var ix = new NameIndex('Z');
        foreach (WalkedVolume.Entry e in WalkedVolume.Walk(_root))
            ix.Upsert(e.Id, e.Parent, e.Attributes, e.Name);
        return ix;
    }

    private static List<string> Paths(NameIndex ix)
    {
        var all = new List<string>();
        for (int rec = 0; rec < ix.Capacity; rec++)
            if (ix.IsAlive(rec) && ix.PathOf(rec) is { } p) all.Add(p);
        all.Sort(StringComparer.OrdinalIgnoreCase);
        return all;
    }

    // ---- which drives ----------------------------------------------------------------------------

    [Theory]
    [InlineData(DriveType.Removable, "FAT32", true)]
    [InlineData(DriveType.Removable, "exFAT", true)]
    [InlineData(DriveType.Removable, "NTFS", true)]     // a stick formatted NTFS was skipped too
    [InlineData(DriveType.Fixed, "exFAT", true)]
    [InlineData(DriveType.Fixed, "ReFS", true)]         // a Dev Drive
    [InlineData(DriveType.Fixed, "NTFS", false)]        // its file table is read instead
    [InlineData(DriveType.Fixed, "ntfs", false)]
    [InlineData(DriveType.Network, "NTFS", false)]      // network shares are out of scope
    [InlineData(DriveType.CDRom, "UDF", false)]
    public void WhichDrivesAreWalked(DriveType type, string format, bool walked) =>
        Assert.Equal(walked, WalkedVolume.Walks(type, format));

    // ---- numbers ---------------------------------------------------------------------------------

    [Fact]
    public void TheRootIsNtfsOwnRootSoPathsAndDepthWorkUnchanged()
    {
        Assert.Equal(5ul, WalkedVolume.IdOf(""));
        Assert.Equal(5ul, WalkedVolume.IdOf("\\"));
        Assert.Equal(WalkedVolume.RootId, WalkedVolume.IdOf(""));
    }

    [Fact]
    public void ANumberIsThePathWhateverItsCaseOrTrailingSlash()
    {
        Assert.Equal(WalkedVolume.IdOf(@"Photos\Beach.JPG"), WalkedVolume.IdOf(@"photos\beach.jpg"));
        Assert.Equal(WalkedVolume.IdOf(@"Photos"), WalkedVolume.IdOf(@"Photos\"));
        Assert.NotEqual(WalkedVolume.IdOf(@"Photos\a.jpg"), WalkedVolume.IdOf(@"Photos\b.jpg"));
    }

    [Fact]
    public void NoOrdinaryPathEverGetsANumberTheIndexReadsAsTheRootOrAsNothing()
    {
        // The index compares the low 48 bits against 5 to find the root, and treats 0 and all ones
        // as an empty slot and a deleted one.
        for (int i = 0; i < 200_000; i++)
        {
            ulong id = WalkedVolume.IdOf("f" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.NotEqual(5ul, id & 0xFFFFFFFFFFFF);
            Assert.NotEqual(0ul, id & 0xFFFFFFFFFFFF);
            Assert.NotEqual(ulong.MaxValue, id);
        }
    }

    [Theory]
    [InlineData(@"E:\", @"E:\Photos\a.jpg", @"Photos\a.jpg")]
    [InlineData(@"E:\", @"e:\photos", "photos")]
    [InlineData(@"E:\", @"E:\", "")]
    [InlineData(@"E:\", @"F:\Photos", null)]
    public void RelativeIsThePathUnderTheRoot(string root, string path, string? expected) =>
        Assert.Equal(expected, WalkedVolume.Relative(root, path));

    [Theory]
    [InlineData(@"Photos\a.jpg", "Photos")]
    [InlineData(@"a.jpg", "")]
    [InlineData(@"A\B\C", @"A\B")]
    public void ParentOfIsTheFolderAboveOrTheRoot(string relative, string expected) =>
        Assert.Equal(expected, WalkedVolume.ParentOf(relative));

    // ---- the walk --------------------------------------------------------------------------------

    [Fact]
    public void AWalkPutsEveryFileAndFolderAtItsOwnPath()
    {
        Make(@"Photos\2024\beach.jpg");
        Make(@"Photos\notes.txt");
        Make(@"readme.md");
        Make(@"Empty", content: null);

        Assert.Equal(
            [@"Z:\Empty", @"Z:\Photos", @"Z:\Photos\2024", @"Z:\Photos\2024\beach.jpg", @"Z:\Photos\notes.txt", @"Z:\readme.md"],
            Paths(Walked()));
    }

    [Fact]
    public void AWalkedDriveIsSearchedAndRankedLikeAnyOther()
    {
        Make(@"Trips\beach.jpg");
        Make(@"Trips\Deep\Deeper\beach.jpg");
        NameIndex ix = Walked();

        var hits = new List<NameIndex.Hit>();
        ix.Search("beach", hits);
        Assert.Equal(2, hits.Count);
        // Depth breaks ties, and it can only do that when parents chain up to the root.
        int Depth(string path) => ix.Depth(hits.Select(h => h.Record).First(r => ix.PathOf(r) == path));
        Assert.Equal(Depth(@"Z:\Trips\beach.jpg") + 2, Depth(@"Z:\Trips\Deep\Deeper\beach.jpg"));
    }

    [Fact]
    public void HiddenEntriesAreListedLikeTheFileTableListsThem()
    {
        string secret = Make(@"secret.txt");
        File.SetAttributes(secret, FileAttributes.Hidden);
        Assert.Contains(@"Z:\secret.txt", Paths(Walked()));
    }

    [Fact]
    public void ALinkedFolderIsListedButNeverEntered()
    {
        // A junction back up the tree would loop for ever, and one to another drive would list
        // that drive under this one.
        Make(@"Real\inside.txt");
        string link = Path.Combine(_root, "Loop");
        using (Process? p = Process.Start(new ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{_root}\"")
               { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true }))
        {
            p!.StandardOutput.ReadToEnd();
            p.WaitForExit();
        }
        Assert.True(Directory.Exists(link), "the test could not make a junction");

        List<string> paths = Paths(Walked());
        Assert.Contains(@"Z:\Loop", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith(@"Z:\Loop\", StringComparison.OrdinalIgnoreCase));
        Directory.Delete(link);
    }

    // ---- walking again ---------------------------------------------------------------------------

    [Fact]
    public void WalkingAgainAddsWhatAppearedAndRemovesWhatWent()
    {
        Make(@"Keep\a.txt");
        string gone = Make(@"Gone\b.txt");
        NameIndex ix = Walked();

        Directory.Delete(Path.GetDirectoryName(gone)!, true);
        Make(@"New\c.txt");
        int holds = 0;
        (int written, int removed) = WalkedVolume.Reconcile(ix, WalkedVolume.Walk(_root), () => { holds++; return Nothing; });

        Assert.Equal([@"Z:\Keep", @"Z:\Keep\a.txt", @"Z:\New", @"Z:\New\c.txt"], Paths(ix));
        Assert.Equal(4, written);
        Assert.Equal(2, removed);
        Assert.Equal(4, ix.Count);
        Assert.True(holds >= 2, "every write to the index is made under the volume's write lock");
    }

    [Fact]
    public void ALongWalkIsWrittenInBatchesSoSearchesAreNotHeldUpForAllOfIt()
    {
        for (int i = 0; i < 25; i++) Make($"f{i}.txt");
        NameIndex ix = new('Z');
        int holds = 0;
        WalkedVolume.Reconcile(ix, WalkedVolume.Walk(_root), () => { holds++; return Nothing; }, batch: 10);
        Assert.Equal(4, holds);   // three batches and the sweep
        Assert.Equal(25, ix.Count);
    }

    // ---- following changes -----------------------------------------------------------------------

    private FileAttributes? Attrs(string rel)
    {
        try { return File.GetAttributes(Path.Combine(_root, rel)); }
        catch (IOException) { return null; }
    }

    [Fact]
    public void ANewFileIsAddedOnTheSpot()
    {
        Make(@"Docs", content: null);
        NameIndex ix = Walked();
        Make(@"Docs\new.pdf");

        Assert.False(WalkedVolume.Apply(ix, WalkedVolume.ChangeKind.Created, @"Docs\new.pdf", null, Attrs, () => Nothing));
        Assert.Contains(@"Z:\Docs\new.pdf", Paths(ix));
    }

    [Fact]
    public void ADeletedFileIsRemovedOnTheSpot()
    {
        string f = Make(@"Docs\old.pdf");
        NameIndex ix = Walked();
        File.Delete(f);

        Assert.False(WalkedVolume.Apply(ix, WalkedVolume.ChangeKind.Deleted, @"Docs\old.pdf", null, Attrs, () => Nothing));
        Assert.DoesNotContain(@"Z:\Docs\old.pdf", Paths(ix));
    }

    [Fact]
    public void ARenamedFileMovesToItsNewName()
    {
        string f = Make(@"Docs\draft.docx");
        NameIndex ix = Walked();
        File.Move(f, Path.Combine(_root, @"Docs\final.docx"));

        Assert.False(WalkedVolume.Apply(ix, WalkedVolume.ChangeKind.Renamed, @"Docs\final.docx", @"Docs\draft.docx", Attrs, () => Nothing));
        List<string> paths = Paths(ix);
        Assert.Contains(@"Z:\Docs\final.docx", paths);
        Assert.DoesNotContain(@"Z:\Docs\draft.docx", paths);
    }

    [Fact]
    public void AFileThatIsGoneAgainBeforeItIsLookedAtAddsNothing()
    {
        NameIndex ix = Walked();
        Assert.False(WalkedVolume.Apply(ix, WalkedVolume.ChangeKind.Created, @"brief.tmp", null, Attrs, () => Nothing));
        Assert.Equal(0, ix.Count);
    }

    [Fact]
    public void AnythingDoneToAFolderAsksForAWalk()
    {
        // A folder moved in arrives with its contents; one removed or renamed changes the number of
        // everything under it. One notification lists none of that.
        Make(@"Album\a.jpg");
        NameIndex ix = Walked();

        Make(@"Arrived\b.jpg");
        Assert.True(WalkedVolume.Apply(ix, WalkedVolume.ChangeKind.Created, "Arrived", null, Attrs, () => Nothing));
        Assert.True(WalkedVolume.Apply(ix, WalkedVolume.ChangeKind.Deleted, "Album", null, Attrs, () => Nothing));
        Assert.True(WalkedVolume.Apply(ix, WalkedVolume.ChangeKind.Renamed, "Renamed", "Album", Attrs, () => Nothing));
    }

    // ---- over the pipe ---------------------------------------------------------------------------

    private static IReadOnlyDictionary<char, VolumeView> NtfsAndAStick()
    {
        var c = new NameIndex('C');
        c.Upsert(10, 5, NtfsVolume.FileAttributeDirectory, "Papers");
        c.Upsert(11, 10, 0, "lease.pdf");
        var e = new NameIndex('E');
        e.Upsert(WalkedVolume.IdOf("Stick"), 5, NtfsVolume.FileAttributeDirectory, "Stick");
        e.Upsert(WalkedVolume.IdOf(@"Stick\lease-copy.pdf"), WalkedVolume.IdOf("Stick"), 0, "lease-copy.pdf");
        return new Dictionary<char, VolumeView>
        {
            ['C'] = new(c, 0xBEEF, 5000, 10),
            ['E'] = new(e, 0, 0, 3, NamesOnly: true),
        };
    }

    [Fact]
    public async Task AWalkedDriveAnswersNameQueriesAndSaysItIsNamesOnly()
    {
        var (server, client) = NameServerTests.PairForTests();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = NameServer.Serve(server, NtfsAndAStick(), new IndexLock(), new JournalBroadcast(), null, cts.Token);

        await Frame.WriteAsync(client, Envelope.Pack(Envelope.KindQuery, new QueryRequest(1, "lease", 10)), cts.Token);
        QueryReply reply = Envelope.Unpack((await Frame.ReadAsync(client, cts.Token))!).Body<QueryReply>();
        Assert.Contains(reply.Rows, r => r.Path == @"E:\Stick\lease-copy.pdf");
        Assert.Contains(reply.Rows, r => r.Path == @"C:\Papers\lease.pdf");

        await Frame.WriteAsync(client, Envelope.Pack(Envelope.KindStatus, new StatusRequest()), cts.Token);
        StatusReply status = Envelope.Unpack((await Frame.ReadAsync(client, cts.Token))!).Body<StatusReply>();
        Assert.True(status.Volumes.Single(v => v.Letter == 'E').NamesOnly);
        Assert.False(status.Volumes.Single(v => v.Letter == 'C').NamesOnly);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task AWalkedDriveIsNeverOfferedToTheContentIndex()
    {
        var (server, client) = NameServerTests.PairForTests();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = NameServer.Serve(server, NtfsAndAStick(), new IndexLock(), new JournalBroadcast(), null, cts.Token);

        // No journal to resume...
        await Frame.WriteAsync(client, Envelope.Pack(Envelope.KindSubscribe, new SubscribeRequest([])), cts.Token);
        SubscribeReply sub = Envelope.Unpack((await Frame.ReadAsync(client, cts.Token))!).Body<SubscribeReply>();
        Assert.DoesNotContain(sub.Volumes, v => v.Volume == 'E');
        Assert.Contains(sub.Volumes, v => v.Volume == 'C');

        // ...and no first pass: asked for its PDFs, it has none to give.
        await Frame.WriteAsync(client, Envelope.Pack(Envelope.KindEnumerate, new EnumerateRequest(3, 'E', [".pdf"], 100)), cts.Token);
        EnumerateReply files = Envelope.Unpack((await Frame.ReadAsync(client, cts.Token))!).Body<EnumerateReply>();
        Assert.True(files.Done);
        Assert.Empty(files.Files);
        await cts.CancelAsync();
    }
}
