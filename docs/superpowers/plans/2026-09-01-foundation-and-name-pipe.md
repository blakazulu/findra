# Findra Plan 1 - Foundation and the Name Pipe

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Name search works end to end across two processes - an elevated headless helper owning the NTFS volume, and a normal-integrity client - proven by `findra.exe --searchprobe <query>` returning real files, with the query generation counter verified against a deliberately delayed reply.

**Architecture:** One executable, three modes selected by argv. `findra.exe --names` runs elevated and owns `NtfsVolume` + `NameIndex`, serving a length-prefixed JSON protocol over a per-user-ACL'd named pipe. The default mode is the client. Query text crosses the wire raw and is parsed on the helper side, so `SearchQuery` never needs serializing. Every request carries a monotonic generation; every reply echoes it; the client drops any reply that is not the newest.

**Tech Stack:** .NET 10 (`net10.0-windows`), C# 13, xUnit, System.Text.Json, `System.IO.Pipes`. No Avalonia or SkiaSharp in this plan - there is no visible UI yet.

**Spec:** `docs/superpowers/specs/2026-09-01-findra-design.md`

## Global Constraints

- **Target framework:** `net10.0-windows`. **Publish is self-contained** - never framework-dependent.
- **Root namespace is `Findra`** for every file, ported or new.
- **No lineage anywhere in the product.** README, UI text, code comments, commit messages and log tags must not describe Findra as derived from, forked from, or a component of another project. Porting from a source path is fine; naming a parent project in shipped text is not. Ported comments that mention one get rewritten.
- **Config roams, bulk does not.** `%APPDATA%\Findra\` for `config.json` and `palettes.json`. `%LOCALAPPDATA%\Findra\` for `models\`, `index\`, `logs\`. Models must never be written to the publish folder or to Roaming.
- **The elevated helper never parses untrusted file content.** No document decoder, image codec, ONNX runtime or Whisper binding may be referenced from any code path reachable in `--names` mode.
- **Name search is async everywhere.** No synchronous wrapper over the pipe, no `.Result`, no `.Wait()`.
- **Every query carries a generation counter**, stamped on the reply and checked by the client.
- **TDD for all new code.** The ported engine is not rewritten test-first; it gets characterization tests only where behaviour changes.
- **Licence:** Apache-2.0 with a `NOTICE` file naming blakazulu and https://github.com/blakazulu/findra.
- **Log tag convention:** lowercase category strings - `names`, `pipe`, `probe`, `startup`.

## Port source

Files copied in this plan live on this machine at:

```
C:\Code\Personal\Prism\src\Search\NtfsVolume.cs     279 lines
C:\Code\Personal\Prism\src\Search\NameIndex.cs      555 lines
C:\Code\Personal\Prism\src\Search\SearchQuery.cs    470 lines
C:\Code\Personal\Prism\src\Search\FileKinds.cs       78 lines
C:\Code\Personal\Prism\src\App\Log.cs               234 lines
C:\Code\Personal\Prism\src\App\StartupManager.cs    208 lines
```

Copy verbatim, then change the namespace, rewrite any comment that names the source project, and change literal strings (`prism-*.log`, `"prism"`, `Prism.exe`) to their Findra equivalents. Do not restructure ported code beyond that.

---

## File structure

| File | Responsibility |
|---|---|
| `Findra.sln` | solution |
| `src/Findra/Findra.csproj` | the one executable, all modes |
| `src/Findra/Program.cs` | argv → mode dispatch, nothing else |
| `src/Findra/Core/Log.cs` | ported logging |
| `src/Findra/Core/Paths.cs` | the four directories, created on demand |
| `src/Findra/Names/NtfsVolume.cs` | ported volume handle, MFT enumeration, journal reads |
| `src/Findra/Names/NameIndex.cs` | ported in-RAM name index |
| `src/Findra/Names/SearchQuery.cs` | ported query grammar |
| `src/Findra/Names/FileKinds.cs` | ported extension → `ResultKind` mapping |
| `src/Findra/Pipe/Frame.cs` | length-prefixed framing over a `Stream` |
| `src/Findra/Pipe/Messages.cs` | the wire DTOs and their JSON context |
| `src/Findra/Pipe/NameServer.cs` | helper side: owns the index, answers requests |
| `src/Findra/Pipe/NameClient.cs` | client side: async request/reply, generation arbitration |
| `src/Findra/Pipe/Generation.cs` | monotonic counter + staleness rule |
| `src/Findra/Startup/StartupManager.cs` | ported scheduled-task registration |
| `src/Findra/Startup/HelperTask.cs` | registers/queries the `--names` logon task |
| `src/Findra/Diagnostics/SearchProbe.cs` | `--searchprobe` |
| `src/Findra/Diagnostics/SelfTest.cs` | `--searchtest` |
| `tests/Findra.Tests/Findra.Tests.csproj` | xUnit |
| `tests/Findra.Tests/Pipe/FrameTests.cs` | framing |
| `tests/Findra.Tests/Pipe/MessageTests.cs` | wire round-trips |
| `tests/Findra.Tests/Pipe/GenerationTests.cs` | staleness, including the adversarial case |
| `tests/Findra.Tests/Pipe/NameClientTests.cs` | client against a fake server |
| `tests/Findra.Tests/Names/NameIndexTests.cs` | characterization |
| `LICENSE`, `NOTICE`, `README.md` | licence and front page |

---

## Task 1: Solution, projects, licence, front page

**Files:**
- Create: `Findra.sln`, `src/Findra/Findra.csproj`, `src/Findra/Program.cs`, `tests/Findra.Tests/Findra.Tests.csproj`, `LICENSE`, `NOTICE`, `README.md`
- Modify: `.gitignore` (already ignores `bin/ obj/ publish/`)

**Interfaces:**
- Consumes: nothing.
- Produces: `Findra.Program.Main(string[] args) : int`, and the mode strings `--names`, `--searchprobe`, `--searchtest`.

- [ ] **Step 1: Create the projects**

```bash
dotnet new sln -n Findra
dotnet new console -o src/Findra -n Findra -f net10.0
dotnet new xunit -o tests/Findra.Tests -n Findra.Tests -f net10.0
dotnet sln add src/Findra/Findra.csproj tests/Findra.Tests/Findra.Tests.csproj
dotnet add tests/Findra.Tests/Findra.Tests.csproj reference src/Findra/Findra.csproj
```

- [ ] **Step 2: Set the project properties**

Replace `src/Findra/Findra.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <RootNamespace>Findra</RootNamespace>
    <AssemblyName>findra</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <InvariantGlobalization>false</InvariantGlobalization>
    <SelfContained>true</SelfContained>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <PublishSingleFile>false</PublishSingleFile>
  </PropertyGroup>
</Project>
```

`AllowUnsafeBlocks` is required: the ported `NtfsVolume` uses pointer arithmetic over the USN buffer.

Then add the one package the pipe ACL needs (`NamedPipeServerStreamAcl`, used in Task 7):

```bash
dotnet add src/Findra/Findra.csproj package System.IO.Pipes.AccessControl
```

In `tests/Findra.Tests/Findra.Tests.csproj`, set `<TargetFramework>net10.0-windows</TargetFramework>` so the test project can reference the exe project.

- [ ] **Step 3: Write the mode dispatcher**

`src/Findra/Program.cs`:

```csharp
namespace Findra;

public static class Program
{
    public static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "";
        return mode switch
        {
            "--names"       => 0,   // Task 7
            "--searchprobe" => 0,   // Task 10
            "--searchtest"  => 0,   // Task 10
            _               => 0,   // the UI, later plans
        };
    }
}
```

- [ ] **Step 4: Write LICENSE and NOTICE**

`LICENSE` is the Apache License 2.0, verbatim from https://www.apache.org/licenses/LICENSE-2.0.txt, with the bracketed placeholders in the appendix filled as `Copyright 2026 blakazulu`.

`NOTICE`:

```
Findra
Copyright (c) 2026 blakazulu (Liraz)

Original work and project home: https://github.com/blakazulu/findra

This product includes software developed by blakazulu.
If you use, modify or redistribute this software, you must retain this
notice and credit the original author and project page.
```

- [ ] **Step 5: Write README.md**

```markdown
# Findra

Desktop search for Windows that finds files by name in milliseconds, and by what is
*inside* them - words in documents, what a photo shows, what was said in a recording.

A capsule sits on your desktop. Click it, or press the hotkey, and it unfolds into results.

## Install

    winget install blakazulu.Findra

Or build it:

    git clone https://github.com/blakazulu/findra
    cd findra
    dotnet publish -c Release

## What it costs

Names and full-text search inside documents are free and need no download.
Searching photos, speech and document *meaning* uses local models - up to 2.9 GB,
chosen a capability at a time on first run, and never downloaded without asking.

Nothing leaves your machine. No account, no cloud, no telemetry.

## Licence

Apache-2.0. Free to use, clone and modify - see NOTICE for the attribution you must keep.
```

- [ ] **Step 6: Verify it builds and tests run**

Run: `dotnet build && dotnet test`
Expected: build succeeds; the xUnit template's single placeholder test passes. Delete `tests/Findra.Tests/UnitTest1.cs`.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Scaffold the solution, licence and front page"
```

---

## Task 2: Paths and logging

**Files:**
- Create: `src/Findra/Core/Paths.cs`, `src/Findra/Core/Log.cs`
- Test: `tests/Findra.Tests/Core/PathsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Findra.Paths.Config : string`, `Paths.Models : string`, `Paths.Index : string`, `Paths.Logs : string`, `Paths.ConfigFile : string`, `Paths.PalettesFile : string`; `Findra.Log.Info(string cat, string msg)`, `Log.Warn(...)`, `Log.Error(string cat, string msg)`, `Log.Error(string cat, string msg, Exception ex)`, `Log.Flush()`, `Log.Dir : string`.

- [ ] **Step 1: Write the failing test**

`tests/Findra.Tests/Core/PathsTests.cs`:

```csharp
using Findra;
using Xunit;

public class PathsTests
{
    [Fact]
    public void ConfigRoams_AndBulkDoesNot()
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local   = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(roaming, Paths.Config);
        Assert.StartsWith(local,   Paths.Models);
        Assert.StartsWith(local,   Paths.Index);
        Assert.StartsWith(local,   Paths.Logs);
    }

    [Fact]
    public void ModelsAreNeverUnderRoaming()
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.DoesNotContain(roaming, Paths.Models);
    }

    [Fact]
    public void ModelsAreNeverBesideTheExecutable()
    {
        string exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        Assert.DoesNotContain(exeDir, Paths.Models);
    }

    [Fact]
    public void FileNamesAreWhereTheSpecSaysTheyAre()
    {
        Assert.EndsWith(Path.Combine("Findra", "config.json"),   Paths.ConfigFile);
        Assert.EndsWith(Path.Combine("Findra", "palettes.json"), Paths.PalettesFile);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter PathsTests`
Expected: FAIL - `The type or namespace name 'Paths' does not exist`.

- [ ] **Step 3: Write Paths**

`src/Findra/Core/Paths.cs`:

```csharp
namespace Findra;

/// <summary>
/// Settings roam; models, index and logs do not. 2.9 GB of model files must never
/// end up in a roaming profile, and never beside the executable.
/// </summary>
public static class Paths
{
    private static string Roaming =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Findra");

    private static string Local =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Findra");

    public static string Config => Roaming;
    public static string Models => Path.Combine(Local, "models");
    public static string Index  => Path.Combine(Local, "index");
    public static string Logs   => Path.Combine(Local, "logs");

    public static string ConfigFile   => Path.Combine(Roaming, "config.json");
    public static string PalettesFile => Path.Combine(Roaming, "palettes.json");

    public static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter PathsTests`
Expected: PASS, 4 tests.

- [ ] **Step 5: Port Log**

Copy `C:\Code\Personal\Prism\src\App\Log.cs` to `src/Findra/Core/Log.cs`. Then:

1. Change the namespace to `Findra`.
2. Replace the hardcoded log directory with `Paths.Ensure(Paths.Logs)` - it currently builds a Roaming path itself, and logs belong under Local.
3. Change the log file name pattern from `prism-{day:yyyyMMdd}.log` to `findra-{day:yyyyMMdd}.log`, and the cleanup glob from `prism-*.log` to `findra-*.log`.
4. Rewrite any comment that names the source project.
5. Delete `PerfSampler` - nothing in this plan uses it, and it comes back with the render loop in Plan 2.

- [ ] **Step 6: Verify logging writes where the spec says**

`--searchtest` does not exist until Task 10, so log from the default arm for now. In
`src/Findra/Program.cs`, change the fallthrough arm to:

```csharp
_ => Hello(),
```

and add:

```csharp
private static int Hello()
{
    Log.Info("startup", $"findra {Log.Version} - no UI yet");
    Log.Flush();
    Console.WriteLine($"log: {Log.Dir}");
    return 0;
}
```

Run: `dotnet run --project src/Findra`

Expected: it prints a path under `%LOCALAPPDATA%\Findra\logs`. Confirm a file matching
`findra-*.log` exists there, that it contains the startup line, and that
`%APPDATA%\Findra\logs\` does **not** exist.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Paths and logging: settings roam, bulk stays local"
```

---

## Task 3: Pipe framing

**Files:**
- Create: `src/Findra/Pipe/Frame.cs`
- Test: `tests/Findra.Tests/Pipe/FrameTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Findra.Pipe.Frame.WriteAsync(Stream s, ReadOnlyMemory<byte> payload, CancellationToken ct) : Task`, `Frame.ReadAsync(Stream s, CancellationToken ct) : Task<byte[]?>` (null at clean end of stream), `Frame.MaxPayload : int`.

- [ ] **Step 1: Write the failing test**

`tests/Findra.Tests/Pipe/FrameTests.cs`:

```csharp
using System.Text;
using Findra.Pipe;
using Xunit;

public class FrameTests
{
    [Fact]
    public async Task RoundTripsOnePayload()
    {
        var ms = new MemoryStream();
        await Frame.WriteAsync(ms, Encoding.UTF8.GetBytes("hello"), default);
        ms.Position = 0;

        byte[]? got = await Frame.ReadAsync(ms, default);

        Assert.NotNull(got);
        Assert.Equal("hello", Encoding.UTF8.GetString(got!));
    }

    [Fact]
    public async Task RoundTripsManyPayloadsInOrder()
    {
        var ms = new MemoryStream();
        foreach (string s in new[] { "a", "bb", "ccc" })
            await Frame.WriteAsync(ms, Encoding.UTF8.GetBytes(s), default);
        ms.Position = 0;

        Assert.Equal("a",   Encoding.UTF8.GetString((await Frame.ReadAsync(ms, default))!));
        Assert.Equal("bb",  Encoding.UTF8.GetString((await Frame.ReadAsync(ms, default))!));
        Assert.Equal("ccc", Encoding.UTF8.GetString((await Frame.ReadAsync(ms, default))!));
    }

    [Fact]
    public async Task ReturnsNullAtCleanEndOfStream()
    {
        var ms = new MemoryStream();
        Assert.Null(await Frame.ReadAsync(ms, default));
    }

    [Fact]
    public async Task ReassemblesAPayloadDeliveredInPieces()
    {
        // a pipe hands over whatever arrived; a reader that assumes one read per
        // frame silently truncates under load.
        var full = new MemoryStream();
        await Frame.WriteAsync(full, Encoding.UTF8.GetBytes(new string('x', 5000)), default);
        var drip = new DripStream(full.ToArray(), chunk: 7);

        byte[]? got = await Frame.ReadAsync(drip, default);

        Assert.NotNull(got);
        Assert.Equal(5000, got!.Length);
    }

    [Fact]
    public async Task RejectsAnOversizedLengthPrefix()
    {
        var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(Frame.MaxPayload + 1));
        ms.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => Frame.ReadAsync(ms, default));
    }

    [Fact]
    public async Task ThrowsOnATruncatedPayload()
    {
        var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(10));
        ms.Write(new byte[4]);
        ms.Position = 0;

        await Assert.ThrowsAsync<EndOfStreamException>(() => Frame.ReadAsync(ms, default));
    }

    private sealed class DripStream(byte[] data, int chunk) : Stream
    {
        private int _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(Math.Min(chunk, count), data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => _pos = (int)value; }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FrameTests`
Expected: FAIL - `Frame` does not exist.

- [ ] **Step 3: Write Frame**

`src/Findra/Pipe/Frame.cs`:

```csharp
using System.Buffers.Binary;

namespace Findra.Pipe;

/// <summary>
/// Length-prefixed framing: 4 bytes little-endian payload length, then the payload.
/// A pipe read returns whatever has arrived, not whatever was written, so reads loop.
/// </summary>
public static class Frame
{
    public const int MaxPayload = 32 * 1024 * 1024;

    public static async Task WriteAsync(Stream s, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > MaxPayload)
            throw new InvalidDataException($"frame of {payload.Length} exceeds {MaxPayload}");

        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await s.WriteAsync(header, ct).ConfigureAwait(false);
        await s.WriteAsync(payload, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<byte[]?> ReadAsync(Stream s, CancellationToken ct)
    {
        byte[] header = new byte[4];
        int got = await FillAsync(s, header, ct).ConfigureAwait(false);
        if (got == 0) return null;                       // clean end of stream
        if (got < 4) throw new EndOfStreamException("truncated frame header");

        int len = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (len < 0 || len > MaxPayload)
            throw new InvalidDataException($"frame length {len} out of range");
        if (len == 0) return [];

        byte[] payload = new byte[len];
        if (await FillAsync(s, payload, ct).ConfigureAwait(false) < len)
            throw new EndOfStreamException("truncated frame payload");
        return payload;
    }

    private static async Task<int> FillAsync(Stream s, Memory<byte> buf, CancellationToken ct)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = await s.ReadAsync(buf[total..], ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter FrameTests`
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Pipe framing with partial-read reassembly"
```

---

## Task 4: Wire messages

**Files:**
- Create: `src/Findra/Pipe/Messages.cs`
- Test: `tests/Findra.Tests/Pipe/MessageTests.cs`

**Interfaces:**
- Consumes: `Frame` (Task 3).
- Produces:
  - `Findra.Pipe.QueryRequest(long Gen, string Raw, int Max)`
  - `Findra.Pipe.NameRow(ulong Frn, string Name, string Path, uint Attributes, float Score, int Match)`
  - `Findra.Pipe.QueryReply(long Gen, char Volume, long ElapsedTicks, IReadOnlyList<NameRow> Rows)`
  - `Findra.Pipe.StatusRequest()`
  - `Findra.Pipe.StatusReply(int ProcessId, IReadOnlyList<VolumeStatus> Volumes)`
  - `Findra.Pipe.VolumeStatus(char Letter, int Count, long BufferBytes, bool Live)`
  - `Findra.Pipe.JournalEvent(char Volume, ulong Frn, ulong Parent, uint Attributes, string Name, uint Reason, long Usn)`
  - `Findra.Pipe.Envelope(string Kind, string Json)` with `Envelope.Pack<T>(string kind, T body) : byte[]` and `Envelope.Unpack(byte[] payload) : Envelope`, `Envelope.Body<T>() : T`
  - Kind constants: `Envelope.KindQuery`, `KindQueryReply`, `KindStatus`, `KindStatusReply`, `KindJournal`

- [ ] **Step 1: Write the failing test**

`tests/Findra.Tests/Pipe/MessageTests.cs`:

```csharp
using Findra.Pipe;
using Xunit;

public class MessageTests
{
    [Fact]
    public void QueryRoundTripsThroughAnEnvelope()
    {
        byte[] packed = Envelope.Pack(Envelope.KindQuery, new QueryRequest(7, "sunset ext:jpg", 400));

        Envelope e = Envelope.Unpack(packed);
        Assert.Equal(Envelope.KindQuery, e.Kind);

        QueryRequest got = e.Body<QueryRequest>();
        Assert.Equal(7, got.Gen);
        Assert.Equal("sunset ext:jpg", got.Raw);
        Assert.Equal(400, got.Max);
    }

    [Fact]
    public void ReplyCarriesTheGenerationItWasAskedWith()
    {
        var reply = new QueryReply(42, 'C', 1234, new[]
        {
            new NameRow(0xABC, "IMG_4471.HEIC", @"D:\Photos\2025\IMG_4471.HEIC", 0x20, 0.91f, 0),
        });

        QueryReply got = Envelope.Unpack(Envelope.Pack(Envelope.KindQueryReply, reply)).Body<QueryReply>();

        Assert.Equal(42, got.Gen);
        Assert.Equal('C', got.Volume);
        Assert.Single(got.Rows);
        Assert.Equal("IMG_4471.HEIC", got.Rows[0].Name);
        Assert.Equal(0xABCu, (uint)got.Rows[0].Frn);
    }

    [Fact]
    public void NonAsciiNamesSurviveTheWire()
    {
        var reply = new QueryReply(1, 'C', 0, new[]
        {
            new NameRow(1, "הסכם-שכירות 2026.docx", @"D:\מסמכים\הסכם-שכירות 2026.docx", 0x20, 1f, 0),
        });

        QueryReply got = Envelope.Unpack(Envelope.Pack(Envelope.KindQueryReply, reply)).Body<QueryReply>();

        Assert.Equal("הסכם-שכירות 2026.docx", got.Rows[0].Name);
        Assert.Equal(@"D:\מסמכים\הסכם-שכירות 2026.docx", got.Rows[0].Path);
    }

    [Fact]
    public void StatusRoundTrips()
    {
        var reply = new StatusReply(4242, new[] { new VolumeStatus('C', 1_532_238, 90_000_000, true) });

        StatusReply got = Envelope.Unpack(Envelope.Pack(Envelope.KindStatusReply, reply)).Body<StatusReply>();

        Assert.Equal(4242, got.ProcessId);
        Assert.Equal('C', got.Volumes[0].Letter);
        Assert.Equal(1_532_238, got.Volumes[0].Count);
        Assert.True(got.Volumes[0].Live);
    }

    [Fact]
    public void UnknownKindIsReadableWithoutThrowing()
    {
        // forward compatibility: an older client must skip a kind it does not know
        byte[] packed = Envelope.Pack("something-new", new QueryRequest(1, "x", 1));
        Envelope e = Envelope.Unpack(packed);
        Assert.Equal("something-new", e.Kind);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter MessageTests`
Expected: FAIL - `Envelope` does not exist.

- [ ] **Step 3: Write Messages**

`src/Findra/Pipe/Messages.cs`:

```csharp
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Findra.Pipe;

public sealed record QueryRequest(long Gen, string Raw, int Max);

public sealed record NameRow(ulong Frn, string Name, string Path, uint Attributes, float Score, int Match);

public sealed record QueryReply(long Gen, char Volume, long ElapsedTicks, IReadOnlyList<NameRow> Rows);

public sealed record StatusRequest();

public sealed record VolumeStatus(char Letter, int Count, long BufferBytes, bool Live);

public sealed record StatusReply(int ProcessId, IReadOnlyList<VolumeStatus> Volumes);

public sealed record JournalEvent(char Volume, ulong Frn, ulong Parent, uint Attributes,
                                 string Name, uint Reason, long Usn);

/// <summary>
/// Kind outside, body inside. An envelope whose kind is unknown can still be read and
/// skipped, so one side can learn a message the other has not.
/// </summary>
public sealed record Envelope(string Kind, string Json)
{
    public const string KindQuery       = "query";
    public const string KindQueryReply  = "query-reply";
    public const string KindStatus      = "status";
    public const string KindStatusReply = "status-reply";
    public const string KindJournal     = "journal";

    private static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static byte[] Pack<T>(string kind, T body)
    {
        var e = new Envelope(kind, JsonSerializer.Serialize(body, Opts));
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(e, Opts));
    }

    public static Envelope Unpack(byte[] payload) =>
        JsonSerializer.Deserialize<Envelope>(Encoding.UTF8.GetString(payload), Opts)
            ?? throw new InvalidDataException("empty envelope");

    public T Body<T>() =>
        JsonSerializer.Deserialize<T>(Json, Opts) ?? throw new InvalidDataException($"empty body for {Kind}");
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter MessageTests`
Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Pipe wire messages"
```

---

## Task 5: Generation arbitration

This is the task the spec singles out. Without it a slow answer to an abandoned query
arrives late and overwrites a newer result.

**Files:**
- Create: `src/Findra/Pipe/Generation.cs`
- Test: `tests/Findra.Tests/Pipe/GenerationTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Findra.Pipe.Generation` with `Next() : long`, `Current : long`, `Accept(long gen) : bool`.

`Accept` is the gate the client puts every reply through: it returns true only for the newest generation, and only once, so a duplicate reply for the current generation is also refused.

- [ ] **Step 1: Write the failing test**

`tests/Findra.Tests/Pipe/GenerationTests.cs`:

```csharp
using Findra.Pipe;
using Xunit;

public class GenerationTests
{
    [Fact]
    public void NextIncreasesMonotonically()
    {
        var g = new Generation();
        Assert.Equal(1, g.Next());
        Assert.Equal(2, g.Next());
        Assert.Equal(3, g.Next());
        Assert.Equal(3, g.Current);
    }

    [Fact]
    public void AcceptsTheNewestGeneration()
    {
        var g = new Generation();
        long gen = g.Next();
        Assert.True(g.Accept(gen));
    }

    [Fact]
    public void RefusesAStaleGeneration()
    {
        var g = new Generation();
        long first = g.Next();
        g.Next();                        // the user typed another character
        Assert.False(g.Accept(first));
    }

    [Fact]
    public void RefusesADuplicateOfTheCurrentGeneration()
    {
        var g = new Generation();
        long gen = g.Next();
        Assert.True(g.Accept(gen));
        Assert.False(g.Accept(gen));
    }

    [Fact]
    public void RefusesAGenerationFromTheFuture()
    {
        // a reply claiming a generation never issued is a protocol fault, not a race
        var g = new Generation();
        g.Next();
        Assert.False(g.Accept(999));
    }

    [Fact]
    public void SlowFirstAnswerNeverBeatsFastSecondAnswer()
    {
        // the adversarial case the spec calls for: "sun" is slow, "sunset" is fast,
        // and the slow answer lands last.
        var g = new Generation();
        long slow = g.Next();      // "sun"
        long fast = g.Next();      // "sunset"

        Assert.True(g.Accept(fast));    // fast lands first and is shown
        Assert.False(g.Accept(slow));   // slow lands second and must be dropped
    }

    [Fact]
    public async Task IsSafeUnderConcurrentAccept()
    {
        var g = new Generation();
        long gen = g.Next();

        int accepted = 0;
        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            if (g.Accept(gen)) Interlocked.Increment(ref accepted);
        })));

        Assert.Equal(1, accepted);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter GenerationTests`
Expected: FAIL - `Generation` does not exist.

- [ ] **Step 3: Write Generation**

`src/Findra/Pipe/Generation.cs`:

```csharp
namespace Findra.Pipe;

/// <summary>
/// Name search is a round trip, so answers can arrive out of order. Every request is
/// stamped with a generation; every reply echoes it; only the newest generation is
/// allowed to reach the UI, and only once.
/// </summary>
public sealed class Generation
{
    private long _issued;
    private long _accepted;

    public long Current => Interlocked.Read(ref _issued);

    public long Next() => Interlocked.Increment(ref _issued);

    /// <summary>True at most once, and only for the newest generation issued.</summary>
    public bool Accept(long gen)
    {
        if (gen != Interlocked.Read(ref _issued)) return false;
        return Interlocked.CompareExchange(ref _accepted, gen, gen - 1) == gen - 1;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter GenerationTests`
Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Query generation arbitration: a stale answer can never win"
```

---

## Task 6: Port the name engine

**Files:**
- Create: `src/Findra/Names/NtfsVolume.cs`, `src/Findra/Names/NameIndex.cs`, `src/Findra/Names/SearchQuery.cs`, `src/Findra/Names/FileKinds.cs`
- Test: `tests/Findra.Tests/Names/NameIndexTests.cs`

**Interfaces:**
- Consumes: `Log` (Task 2).
- Produces, unchanged from the source:
  - `Findra.NtfsVolume(char letter)`, `.Enumerate() : IEnumerable<NtfsVolume.Record>`, `.Read(long fromUsn, List<NtfsVolume.Change> into) : bool`, `.QueryJournal() : bool`, `.Letter`, `.JournalId`, `.NextUsn`, `NtfsVolume.Volumes() : List<(char Letter, string Label, long Bytes, bool Fixed)>`, `NtfsVolume.FileAttributeDirectory`
  - `Findra.NameIndex(char letter)`, `.Upsert(ulong frn, ulong parent, uint attr, string name) : bool`, `.Remove(ulong frn) : bool`, `.Search(SearchQuery q, List<NameIndex.Hit> into, int max = 4000)`, `.Name(int record) : string`, `.PathOf(int record) : string?`, `.Attributes(int record) : uint`, `.Frn(int record) : ulong`, `.Count : int`, `.BufferBytes : long`, `.Trim()`
  - `Findra.SearchQuery(string raw)` and its members
  - `Findra.FileKinds` and `ResultKind`

- [ ] **Step 1: Copy the four files**

```bash
cp /c/Code/Personal/Prism/src/Search/NtfsVolume.cs  src/Findra/Names/
cp /c/Code/Personal/Prism/src/Search/NameIndex.cs   src/Findra/Names/
cp /c/Code/Personal/Prism/src/Search/SearchQuery.cs src/Findra/Names/
cp /c/Code/Personal/Prism/src/Search/FileKinds.cs   src/Findra/Names/
```

- [ ] **Step 2: Rename and scrub**

In all four files:
1. Change the namespace declaration to `namespace Findra;`.
2. Rewrite every comment that names the source project. There are 8 such mentions across these four files.
3. Remove any `using` that no longer resolves. If a file references a type not being ported in this task, stub nothing - come back and check whether it belongs in a later plan; `NameIndex` and `SearchQuery` reference only each other, `FileKinds`, `NtfsVolume` and `Log`.

Verify the scrub:

```bash
grep -ric "prism" src/Findra/Names/    # expected: 0 in every file
```

- [ ] **Step 3: Write characterization tests**

These pin the behaviour the pipe depends on. They are characterization tests, not TDD -
the code already works, and the point is to notice if the port broke it.

`tests/Findra.Tests/Names/NameIndexTests.cs`:

```csharp
using Findra;
using Xunit;

public class NameIndexTests
{
    private static NameIndex Sample()
    {
        var ix = new NameIndex('C');
        ix.Upsert(5,   0, NtfsVolume.FileAttributeDirectory, "C:");
        ix.Upsert(100, 5, NtfsVolume.FileAttributeDirectory, "Photos");
        ix.Upsert(101, 100, 0, "sunset over water.jpg");
        ix.Upsert(102, 100, 0, "SUNSET-final.png");
        ix.Upsert(103, 100, 0, "invoice.pdf");
        ix.Upsert(104, 100, 0, "הסכם-שכירות.docx");
        return ix;
    }

    [Fact]
    public void FindsBySubstringCaseInsensitively()
    {
        var hits = new List<NameIndex.Hit>();
        Sample().Search(new SearchQuery("sunset"), hits);
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public void FiltersByExtension()
    {
        var ix = Sample();
        var hits = new List<NameIndex.Hit>();
        ix.Search(new SearchQuery("sunset ext:png"), hits);
        Assert.Single(hits);
        Assert.Equal("SUNSET-final.png", ix.Name(hits[0].Record));
    }

    [Fact]
    public void FindsNonAsciiNames()
    {
        var ix = Sample();
        var hits = new List<NameIndex.Hit>();
        ix.Search(new SearchQuery("שכירות"), hits);
        Assert.Single(hits);
        Assert.Equal("הסכם-שכירות.docx", ix.Name(hits[0].Record));
    }

    [Fact]
    public void BuildsAFullPathFromTheParentChain()
    {
        var ix = Sample();
        var hits = new List<NameIndex.Hit>();
        ix.Search(new SearchQuery("invoice"), hits);
        Assert.Equal(@"C:\Photos\invoice.pdf", ix.PathOf(hits[0].Record));
    }

    [Fact]
    public void RemoveTakesARecordOutOfResults()
    {
        var ix = Sample();
        Assert.True(ix.Remove(103));
        var hits = new List<NameIndex.Hit>();
        ix.Search(new SearchQuery("invoice"), hits);
        Assert.Empty(hits);
    }

    [Fact]
    public void RespectsTheMaxArgument()
    {
        var ix = new NameIndex('C');
        ix.Upsert(5, 0, NtfsVolume.FileAttributeDirectory, "C:");
        for (ulong i = 0; i < 50; i++) ix.Upsert(100 + i, 5, 0, $"report{i}.txt");

        var hits = new List<NameIndex.Hit>();
        ix.Search(new SearchQuery("report"), hits, max: 10);
        Assert.Equal(10, hits.Count);
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test --filter NameIndexTests`
Expected: PASS, 6 tests. If `PathOf` returns something other than `C:\Photos\invoice.pdf`, read the source's root handling - `IsRoot` treats FRN `5` as the volume root - rather than changing the assertion.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Port the name engine: volume, index, query grammar, file kinds"
```

---

## Task 7: The helper server

**Files:**
- Create: `src/Findra/Pipe/NameServer.cs`
- Modify: `src/Findra/Program.cs` (the `--names` arm)
- Test: `tests/Findra.Tests/Pipe/NameServerTests.cs`

**Interfaces:**
- Consumes: `Frame`, `Envelope` and the message records (Tasks 3-4), `NameIndex`, `NtfsVolume`, `SearchQuery` (Task 6).
- Produces: `Findra.Pipe.NameServer.PipeName : string` (`"findra-names"`), `NameServer.Serve(Stream transport, IReadOnlyDictionary<char, NameIndex> indexes, CancellationToken ct) : Task`, `NameServer.RunAsync(CancellationToken ct) : Task`.

`Serve` takes a `Stream` rather than a pipe so it can be tested over an in-memory pair. `RunAsync` is what `--names` calls: it enumerates volumes, builds indexes, then listens.

- [ ] **Step 1: Write the failing test**

`tests/Findra.Tests/Pipe/NameServerTests.cs`:

```csharp
using System.IO.Pipelines;
using Findra;
using Findra.Pipe;
using Xunit;

public class NameServerTests
{
    private static NameIndex Sample()
    {
        var ix = new NameIndex('C');
        ix.Upsert(5, 0, NtfsVolume.FileAttributeDirectory, "C:");
        ix.Upsert(100, 5, NtfsVolume.FileAttributeDirectory, "Photos");
        ix.Upsert(101, 100, 0, "sunset over water.jpg");
        return ix;
    }

    /// <summary>A duplex pair of streams, so a server and a client can talk in-process.</summary>
    private static (Stream Server, Stream Client) Pair()
    {
        var a = new Pipe();
        var b = new Pipe();
        return (new DuplexStream(b.Reader.AsStream(), a.Writer.AsStream()),
                new DuplexStream(a.Reader.AsStream(), b.Writer.AsStream()));
    }

    [Fact]
    public async Task AnswersAQueryWithResolvedRows()
    {
        var (server, client) = Pair();
        var cts = new CancellationTokenSource();
        _ = NameServer.Serve(server, new Dictionary<char, NameIndex> { ['C'] = Sample() }, cts.Token);

        await Frame.WriteAsync(client, Envelope.Pack(Envelope.KindQuery, new QueryRequest(1, "sunset", 50)), default);
        byte[]? raw = await Frame.ReadAsync(client, default);

        QueryReply reply = Envelope.Unpack(raw!).Body<QueryReply>();
        Assert.Equal(1, reply.Gen);
        Assert.Single(reply.Rows);
        Assert.Equal("sunset over water.jpg", reply.Rows[0].Name);
        Assert.Equal(@"C:\Photos\sunset over water.jpg", reply.Rows[0].Path);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task EchoesTheGenerationItWasAskedWith()
    {
        var (server, client) = Pair();
        var cts = new CancellationTokenSource();
        _ = NameServer.Serve(server, new Dictionary<char, NameIndex> { ['C'] = Sample() }, cts.Token);

        await Frame.WriteAsync(client, Envelope.Pack(Envelope.KindQuery, new QueryRequest(913, "sunset", 50)), default);
        QueryReply reply = Envelope.Unpack((await Frame.ReadAsync(client, default))!).Body<QueryReply>();

        Assert.Equal(913, reply.Gen);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task AnswersStatusWithItsOwnProcessId()
    {
        var (server, client) = Pair();
        var cts = new CancellationTokenSource();
        _ = NameServer.Serve(server, new Dictionary<char, NameIndex> { ['C'] = Sample() }, cts.Token);

        await Frame.WriteAsync(client, Envelope.Pack(Envelope.KindStatus, new StatusRequest()), default);
        StatusReply reply = Envelope.Unpack((await Frame.ReadAsync(client, default))!).Body<StatusReply>();

        Assert.Equal(Environment.ProcessId, reply.ProcessId);
        Assert.Equal('C', reply.Volumes[0].Letter);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task IgnoresAnUnknownKindAndKeepsServing()
    {
        var (server, client) = Pair();
        var cts = new CancellationTokenSource();
        _ = NameServer.Serve(server, new Dictionary<char, NameIndex> { ['C'] = Sample() }, cts.Token);

        await Frame.WriteAsync(client, Envelope.Pack("nonsense", new StatusRequest()), default);
        await Frame.WriteAsync(client, Envelope.Pack(Envelope.KindQuery, new QueryRequest(2, "sunset", 50)), default);

        QueryReply reply = Envelope.Unpack((await Frame.ReadAsync(client, default))!).Body<QueryReply>();
        Assert.Equal(2, reply.Gen);
        await cts.CancelAsync();
    }

    private sealed class DuplexStream(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => write.Flush();
        public override Task FlushAsync(CancellationToken ct) => write.FlushAsync(ct);
        public override int Read(byte[] b, int o, int c) => read.Read(b, o, c);
        public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct) => read.ReadAsync(b, ct);
        public override void Write(byte[] b, int o, int c) => write.Write(b, o, c);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct) => write.WriteAsync(b, ct);
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter NameServerTests`
Expected: FAIL - `NameServer` does not exist.

- [ ] **Step 3: Write NameServer**

`src/Findra/Pipe/NameServer.cs`:

```csharp
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Findra.Pipe;

/// <summary>
/// The elevated half. It owns the volume handles and the in-RAM name index, and it
/// parses nothing but query text - never file content.
/// </summary>
public static class NameServer
{
    public const string PipeName = "findra-names";

    public static async Task Serve(Stream transport, IReadOnlyDictionary<char, NameIndex> indexes,
                                   CancellationToken ct)
    {
        var hits = new List<NameIndex.Hit>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[]? payload = await Frame.ReadAsync(transport, ct).ConfigureAwait(false);
                if (payload is null) return;

                Envelope e;
                try { e = Envelope.Unpack(payload); }
                catch (Exception ex) { Log.Warn("pipe", "undecodable frame: " + ex.Message); continue; }

                switch (e.Kind)
                {
                    case Envelope.KindQuery:
                        await AnswerQuery(transport, e.Body<QueryRequest>(), indexes, hits, ct).ConfigureAwait(false);
                        break;
                    case Envelope.KindStatus:
                        await AnswerStatus(transport, indexes, ct).ConfigureAwait(false);
                        break;
                    default:
                        Log.Info("pipe", $"ignoring unknown kind '{e.Kind}'");
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("pipe", "serve loop ended", ex); }
    }

    private static async Task AnswerQuery(Stream transport, QueryRequest req,
                                          IReadOnlyDictionary<char, NameIndex> indexes,
                                          List<NameIndex.Hit> hits, CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();
        var q = new SearchQuery(req.Raw);
        var rows = new List<NameRow>(Math.Min(req.Max, 512));
        char volume = '?';

        foreach ((char letter, NameIndex ix) in indexes)
        {
            hits.Clear();
            ix.Search(q, hits, req.Max);
            if (hits.Count > 0) volume = letter;
            foreach (NameIndex.Hit h in hits)
            {
                string? path = ix.PathOf(h.Record);
                if (path is null) continue;
                rows.Add(new NameRow(ix.Frn(h.Record), ix.Name(h.Record), path,
                                     ix.Attributes(h.Record), h.Score, h.Match));
                if (rows.Count >= req.Max) break;
            }
            if (rows.Count >= req.Max) break;
        }

        var reply = new QueryReply(req.Gen, volume, Stopwatch.GetTimestamp() - started, rows);
        await Frame.WriteAsync(transport, Envelope.Pack(Envelope.KindQueryReply, reply), ct).ConfigureAwait(false);
    }

    private static async Task AnswerStatus(Stream transport, IReadOnlyDictionary<char, NameIndex> indexes,
                                           CancellationToken ct)
    {
        var vols = indexes.Select(kv => new VolumeStatus(kv.Key, kv.Value.Count, kv.Value.BufferBytes, true)).ToList();
        var reply = new StatusReply(Environment.ProcessId, vols);
        await Frame.WriteAsync(transport, Envelope.Pack(Envelope.KindStatusReply, reply), ct).ConfigureAwait(false);
    }

    /// <summary>What `--names` runs: build the indexes, then listen for one client at a time.</summary>
    public static async Task RunAsync(CancellationToken ct)
    {
        var indexes = new Dictionary<char, NameIndex>();
        foreach ((char letter, _, _, bool fixedDisk) in NtfsVolume.Volumes())
        {
            if (!fixedDisk) continue;
            try
            {
                using var vol = new NtfsVolume(letter);
                var ix = new NameIndex(letter);
                long started = Stopwatch.GetTimestamp();
                foreach (NtfsVolume.Record r in vol.Enumerate())
                    ix.Upsert(r.Frn, r.ParentFrn, r.Attributes, r.Name);
                ix.Trim();
                indexes[letter] = ix;
                Log.Info("names", $"{letter}: {ix.Count:N0} names in " +
                    $"{Stopwatch.GetElapsedTime(started).TotalSeconds:F2}s, {ix.BufferBytes / 1048576} MB");
            }
            catch (Exception ex) { Log.Error("names", $"{letter}: enumeration failed", ex); }
        }

        if (indexes.Count == 0) { Log.Error("names", "no volume could be read - is this running elevated?"); return; }

        while (!ct.IsCancellationRequested)
        {
            var security = new PipeSecurity();
            var me = WindowsIdentity.GetCurrent().User!;
            security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.ReadWrite, AccessControlType.Allow));

            using var server = NamedPipeServerStreamAcl.Create(
                PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 0, 0, security);

            await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            Log.Info("pipe", "client connected");
            await Serve(server, indexes, ct).ConfigureAwait(false);
            Log.Info("pipe", "client gone");
        }
    }
}
```

The pipe ACL grants only the current user. The helper is elevated; an unrestricted pipe from an elevated process is a privilege-escalation surface, and this is the one line that closes it.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter NameServerTests`
Expected: PASS, 4 tests.

- [ ] **Step 5: Wire up the `--names` arm**

In `src/Findra/Program.cs`, replace the `"--names"` arm:

```csharp
"--names" => RunNames(),
```

and add:

```csharp
private static int RunNames()
{
    Log.Info("names", "helper starting");
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    try { Pipe.NameServer.RunAsync(cts.Token).GetAwaiter().GetResult(); }
    catch (OperationCanceledException) { }
    Log.Flush();
    return 0;
}
```

`GetAwaiter().GetResult()` at the process entry point is the one blocking call allowed - it is the top of the stack, not a wrapper over the pipe.

- [ ] **Step 6: Smoke-test the helper for real**

From an **elevated** terminal:

```bash
dotnet run --project src/Findra -- --names
```

Expected: a log line per fixed volume, e.g. `C: 1,532,238 names in 2.87s, 88 MB`, then it waits. Leave it running for Task 8.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Elevated name server over a per-user named pipe"
```

---

## Task 8: The client

**Files:**
- Create: `src/Findra/Pipe/NameClient.cs`
- Test: `tests/Findra.Tests/Pipe/NameClientTests.cs`

**Interfaces:**
- Consumes: `Frame`, `Envelope`, messages, `Generation` (Tasks 3-5), `NameServer.PipeName` (Task 7).
- Produces: `Findra.Pipe.NameClient` implementing `IAsyncDisposable`, with:
  - `NameClient(Stream transport)` - for tests
  - `NameClient.ConnectAsync(TimeSpan timeout, CancellationToken ct) : Task<NameClient>`
  - `.SearchAsync(string raw, int max, CancellationToken ct) : Task<QueryReply?>` - null when the answer was stale
  - `.StatusAsync(CancellationToken ct) : Task<StatusReply>`
  - `.CurrentGeneration : long`

- [ ] **Step 1: Write the failing test**

`tests/Findra.Tests/Pipe/NameClientTests.cs`:

```csharp
using Findra;
using Findra.Pipe;
using Xunit;

public class NameClientTests
{
    private static NameIndex Sample()
    {
        var ix = new NameIndex('C');
        ix.Upsert(5, 0, NtfsVolume.FileAttributeDirectory, "C:");
        ix.Upsert(100, 5, 0, "sunset.jpg");
        ix.Upsert(101, 5, 0, "sunrise.jpg");
        return ix;
    }

    [Fact]
    public async Task ReturnsRowsForALiveQuery()
    {
        var (server, client) = NameServerTests.PairForTests();
        var cts = new CancellationTokenSource();
        _ = NameServer.Serve(server, new Dictionary<char, NameIndex> { ['C'] = Sample() }, cts.Token);

        await using var c = new NameClient(client);
        QueryReply? reply = await c.SearchAsync("sunset", 50, default);

        Assert.NotNull(reply);
        Assert.Single(reply!.Rows);
        Assert.Equal("sunset.jpg", reply.Rows[0].Name);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task GenerationAdvancesWithEachSearch()
    {
        var (server, client) = NameServerTests.PairForTests();
        var cts = new CancellationTokenSource();
        _ = NameServer.Serve(server, new Dictionary<char, NameIndex> { ['C'] = Sample() }, cts.Token);

        await using var c = new NameClient(client);
        await c.SearchAsync("sun", 50, default);
        long first = c.CurrentGeneration;
        await c.SearchAsync("sunset", 50, default);

        Assert.Equal(first + 1, c.CurrentGeneration);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task DropsAStaleReply()
    {
        // A server that answers the FIRST query slowly and the second immediately,
        // so the slow answer lands last - exactly the race the counter exists for.
        var (server, client) = NameServerTests.PairForTests();
        var gate = new SemaphoreSlim(0);
        _ = Task.Run(async () =>
        {
            byte[]? a = await Frame.ReadAsync(server, default);
            byte[]? b = await Frame.ReadAsync(server, default);
            QueryRequest first  = Envelope.Unpack(a!).Body<QueryRequest>();
            QueryRequest second = Envelope.Unpack(b!).Body<QueryRequest>();

            await Frame.WriteAsync(server, Envelope.Pack(Envelope.KindQueryReply,
                new QueryReply(second.Gen, 'C', 0, new[] { new NameRow(1, "new.jpg", @"C:\new.jpg", 0, 1, 0) })), default);
            gate.Release();
            await Frame.WriteAsync(server, Envelope.Pack(Envelope.KindQueryReply,
                new QueryReply(first.Gen, 'C', 0, new[] { new NameRow(2, "old.jpg", @"C:\old.jpg", 0, 1, 0) })), default);
        });

        await using var c = new NameClient(client);
        Task<QueryReply?> slow = c.SearchAsync("sun", 50, default);
        await gate.WaitAsync();
        Task<QueryReply?> fast = c.SearchAsync("sunset", 50, default);

        Assert.NotNull(await fast);
        Assert.Null(await slow);           // the stale answer is dropped, not shown
    }
}
```

Add to `NameServerTests` so the client tests can reuse the duplex pair:

```csharp
public static (Stream Server, Stream Client) PairForTests() => Pair();
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter NameClientTests`
Expected: FAIL - `NameClient` does not exist.

- [ ] **Step 3: Write NameClient**

`src/Findra/Pipe/NameClient.cs`:

```csharp
using System.Collections.Concurrent;
using System.IO.Pipes;

namespace Findra.Pipe;

/// <summary>
/// The normal-integrity half. Every call is async - name search is a round trip,
/// never an in-RAM lookup, and pretending otherwise deadlocks the UI thread.
/// </summary>
public sealed class NameClient : IAsyncDisposable
{
    private readonly Stream _transport;
    private readonly Generation _gen = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<QueryReply>> _pending = new();
    private readonly ConcurrentQueue<TaskCompletionSource<StatusReply>> _statusWaiters = new();
    private readonly CancellationTokenSource _reader = new();
    private readonly Task _pump;

    public long CurrentGeneration => _gen.Current;

    public NameClient(Stream transport)
    {
        _transport = transport;
        _pump = Task.Run(() => PumpAsync(_reader.Token));
    }

    public static async Task<NameClient> ConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", NameServer.PipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct).ConfigureAwait(false);
        return new NameClient(pipe);
    }

    /// <summary>Null means the answer arrived after a newer query had been issued.</summary>
    public async Task<QueryReply?> SearchAsync(string raw, int max, CancellationToken ct)
    {
        long gen = _gen.Next();
        var tcs = new TaskCompletionSource<QueryReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[gen] = tcs;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Frame.WriteAsync(_transport, Envelope.Pack(Envelope.KindQuery,
                new QueryRequest(gen, raw, max)), ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }

        QueryReply reply = await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        return _gen.Accept(reply.Gen) ? reply : null;
    }

    public async Task<StatusReply> StatusAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<StatusReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _statusWaiters.Enqueue(tcs);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Frame.WriteAsync(_transport, Envelope.Pack(Envelope.KindStatus, new StatusRequest()), ct)
                .ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }

        return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[]? payload = await Frame.ReadAsync(_transport, ct).ConfigureAwait(false);
                if (payload is null) break;

                Envelope e = Envelope.Unpack(payload);
                switch (e.Kind)
                {
                    case Envelope.KindQueryReply:
                    {
                        QueryReply r = e.Body<QueryReply>();
                        if (_pending.TryRemove(r.Gen, out var waiter)) waiter.TrySetResult(r);
                        break;
                    }
                    case Envelope.KindStatusReply:
                        if (_statusWaiters.TryDequeue(out var s)) s.TrySetResult(e.Body<StatusReply>());
                        break;
                    case Envelope.KindJournal:
                        // Plan 3 hooks the indexer up here.
                        break;
                    default:
                        Log.Info("pipe", $"client ignoring unknown kind '{e.Kind}'");
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("pipe", "client pump ended", ex); }
        finally
        {
            foreach (var kv in _pending) kv.Value.TrySetCanceled();
            while (_statusWaiters.TryDequeue(out var s)) s.TrySetCanceled();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _reader.CancelAsync().ConfigureAwait(false);
        try { await _pump.ConfigureAwait(false); } catch { }
        _transport.Dispose();
        _reader.Dispose();
        _writeLock.Dispose();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter NameClientTests`
Expected: PASS, 3 tests. `DropsAStaleReply` is the one that matters - it must fail if `SearchAsync` returns `reply` unconditionally.

- [ ] **Step 5: Prove the test is load-bearing**

Temporarily change the last line of `SearchAsync` to `return reply;` and run
`dotnet test --filter DropsAStaleReply`. Expected: FAIL. Restore the line and confirm it
passes again. A generation test that passes either way is worthless.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Async name client that drops stale answers"
```

---

## Task 9: Helper registration

**Files:**
- Create: `src/Findra/Startup/StartupManager.cs` (ported), `src/Findra/Startup/HelperTask.cs`
- Test: `tests/Findra.Tests/Startup/HelperTaskTests.cs`

**Interfaces:**
- Consumes: `Log` (Task 2).
- Produces: `Findra.Startup.HelperTask.TaskName : string` (`"Findra names helper"`), `HelperTask.BuildXml(string exePath) : string`, `HelperTask.IsRegistered() : bool`, `HelperTask.Register(string exePath) : bool`, `HelperTask.EnsureRunning() : bool`.

- [ ] **Step 1: Port StartupManager**

```bash
cp /c/Code/Personal/Prism/src/App/StartupManager.cs src/Findra/Startup/
```

Change the namespace to `Findra.Startup`, rewrite comments naming the source project, change
the task name and any registry Run-key value name to Findra's, and delete `MigrateLegacy` -
Findra has no legacy install to migrate from.

- [ ] **Step 2: Write the failing test**

`tests/Findra.Tests/Startup/HelperTaskTests.cs`:

```csharp
using System.Xml.Linq;
using Findra.Startup;
using Xunit;

public class HelperTaskTests
{
    [Fact]
    public void XmlRequestsHighestAvailable()
    {
        var doc = XDocument.Parse(HelperTask.BuildXml(@"C:\Program Files\Findra\findra.exe"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal("HighestAvailable", doc.Descendants(ns + "RunLevel").Single().Value);
    }

    [Fact]
    public void XmlTriggersOnLogon()
    {
        var doc = XDocument.Parse(HelperTask.BuildXml(@"C:\Findra\findra.exe"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Single(doc.Descendants(ns + "LogonTrigger"));
    }

    [Fact]
    public void XmlPassesTheNamesArgument()
    {
        var doc = XDocument.Parse(HelperTask.BuildXml(@"C:\Findra\findra.exe"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal("--names", doc.Descendants(ns + "Arguments").Single().Value);
    }

    [Fact]
    public void XmlQuotesAnExePathContainingSpaces()
    {
        var doc = XDocument.Parse(HelperTask.BuildXml(@"C:\Program Files\Findra\findra.exe"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal(@"""C:\Program Files\Findra\findra.exe""", doc.Descendants(ns + "Command").Single().Value);
    }

    [Fact]
    public void XmlDoesNotStopTheHelperOnBattery()
    {
        // a search index that dies when the laptop unplugs is a search index that is
        // always cold
        var doc = XDocument.Parse(HelperTask.BuildXml(@"C:\Findra\findra.exe"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal("false", doc.Descendants(ns + "DisallowStartIfOnBatteries").Single().Value);
        Assert.Equal("false", doc.Descendants(ns + "StopIfGoingOnBatteries").Single().Value);
        Assert.Equal("PT0S",  doc.Descendants(ns + "ExecutionTimeLimit").Single().Value);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test --filter HelperTaskTests`
Expected: FAIL - `HelperTask` does not exist.

- [ ] **Step 4: Write HelperTask**

`src/Findra/Startup/HelperTask.cs`:

```csharp
using System.Diagnostics;
using System.Security.Principal;

namespace Findra.Startup;

/// <summary>
/// Registers the one elevated thing Findra needs: a logon task that starts
/// `findra.exe --names` at HighestAvailable. One UAC prompt, once, ever.
/// </summary>
public static class HelperTask
{
    public const string TaskName = "Findra names helper";

    public static string BuildXml(string exePath) =>
$"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Keeps the Findra file-name index live. Findra does not work without it.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{WindowsIdentity.GetCurrent().Name}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{WindowsIdentity.GetCurrent().Name}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>6</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>"{exePath}"</Command>
      <Arguments>--names</Arguments>
    </Exec>
  </Actions>
</Task>
""";

    /// <summary>
    /// Query the XML form, never the CSV form: `schtasks /query /fo csv` column
    /// headings are localized, the XML is not.
    /// </summary>
    public static bool IsRegistered()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks",
                $"/query /tn \"{TaskName}\" /xml ONE")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using Process? p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch (Exception ex) { Log.Warn("startup", "task query failed: " + ex.Message); return false; }
    }

    public static bool Register(string exePath)
    {
        string xml = Path.Combine(Path.GetTempPath(), $"findra-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xml, BuildXml(exePath), new System.Text.UnicodeEncoding(false, true));
            var psi = new ProcessStartInfo("schtasks", $"/create /tn \"{TaskName}\" /xml \"{xml}\" /f")
            { UseShellExecute = true, Verb = "runas", CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            using Process? p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(60_000);
            if (p.ExitCode != 0) { Log.Error("startup", $"schtasks /create exited {p.ExitCode}"); return false; }
            Log.Info("startup", "names helper task registered");
            return true;
        }
        catch (Exception ex) { Log.Error("startup", "task registration failed", ex); return false; }
        finally { try { File.Delete(xml); } catch { } }
    }

    /// <summary>
    /// Registration is the one thing that can fail on a stranger's machine in a way
    /// Findra cannot fix. Failing here is not fatal - the caller falls back to
    /// whatever can be read unelevated and says so.
    /// </summary>
    public static bool EnsureRunning()
    {
        // Ask the pipe, not the process list: the UI and the helper are both named
        // "findra", so a name check would always find this very process and conclude
        // the helper is up.
        if (IsHelperAnswering()) return true;
        try
        {
            var psi = new ProcessStartInfo("schtasks", $"/run /tn \"{TaskName}\"")
            { UseShellExecute = false, CreateNoWindow = true };
            using Process? p = Process.Start(psi);
            p?.WaitForExit(10_000);
            if (p?.ExitCode != 0) return false;
        }
        catch (Exception ex) { Log.Warn("startup", "could not start the helper: " + ex.Message); return false; }

        // schtasks returns as soon as it has asked; the helper still has to enumerate.
        for (int i = 0; i < 20; i++)
        {
            if (IsHelperAnswering()) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    private static bool IsHelperAnswering()
    {
        try
        {
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(
                ".", Pipe.NameServer.PipeName, System.IO.Pipes.PipeDirection.InOut);
            pipe.Connect(200);
            return true;
        }
        catch { return false; }
    }
}
```

`EnsureRunning` is written here but first called in Plan 2, when the UI starts and has
somewhere to show "the helper could not start". It is covered by the end-to-end check in
Task 10 rather than by a unit test - it talks to the real scheduler.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter HelperTaskTests`
Expected: PASS, 5 tests.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Register the elevated names helper as a logon task"
```

---

## Task 10: The probe

**Files:**
- Create: `src/Findra/Diagnostics/SearchProbe.cs`, `src/Findra/Diagnostics/SelfTest.cs`
- Modify: `src/Findra/Program.cs`

**Interfaces:**
- Consumes: `NameClient` (Task 8), `HelperTask` (Task 9), `Paths`, `Log` (Task 2).
- Produces: `Findra.Diagnostics.SearchProbe.Run(string[] args) : int`, `Findra.Diagnostics.SelfTest.Run() : int`.

The spec requires `--searchprobe` to report **which process answered** and **the current
generation counter**. That is what makes a pipe fault visible without a debugger.

- [ ] **Step 1: Write SearchProbe**

`src/Findra/Diagnostics/SearchProbe.cs`:

```csharp
using System.Diagnostics;
using Findra.Pipe;
using Findra.Startup;

namespace Findra.Diagnostics;

public static class SearchProbe
{
    public static int Run(string[] args)
    {
        string query = args.Length > 1 ? string.Join(' ', args[1..]) : "findra";
        Console.WriteLine($"findra --searchprobe  query: \"{query}\"");
        Console.WriteLine();

        Console.WriteLine($"  helper task registered : {(HelperTask.IsRegistered() ? "yes" : "NO")}");

        NameClient client;
        try
        {
            client = NameClient.ConnectAsync(TimeSpan.FromSeconds(5), default).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  pipe                   : UNREACHABLE ({ex.GetType().Name}: {ex.Message})");
            Console.WriteLine();
            Console.WriteLine("  The helper is not running. Start it from an elevated terminal with");
            Console.WriteLine("  `findra --names`, or register the logon task from Settings.");
            return 1;
        }

        try
        {
            StatusReply status = client.StatusAsync(default).GetAwaiter().GetResult();
            Console.WriteLine($"  answered by            : pid {status.ProcessId}" +
                              $"{(status.ProcessId == Environment.ProcessId ? "  (THIS process - wrong!)" : "  (the helper)")}");
            foreach (VolumeStatus v in status.Volumes)
                Console.WriteLine($"  volume {v.Letter}               : {v.Count:N0} names, {v.BufferBytes / 1048576} MB");

            long started = Stopwatch.GetTimestamp();
            QueryReply? reply = client.SearchAsync(query, 20, default).GetAwaiter().GetResult();
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

            Console.WriteLine($"  generation             : {client.CurrentGeneration}");
            if (reply is null) { Console.WriteLine("  result                 : STALE - dropped by the generation gate"); return 1; }

            Console.WriteLine($"  reply generation       : {reply.Gen}" +
                              $"{(reply.Gen == client.CurrentGeneration ? "" : "  (MISMATCH)")}");
            Console.WriteLine($"  round trip             : {elapsed.TotalMilliseconds:F1} ms");
            Console.WriteLine($"  rows                   : {reply.Rows.Count}");
            Console.WriteLine();
            foreach (NameRow r in reply.Rows.Take(20))
                Console.WriteLine($"    {r.Score,5:F2}  {r.Path}");

            return reply.Rows.Count > 0 ? 0 : 2;
        }
        finally { client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
}
```

- [ ] **Step 2: Write SelfTest**

`src/Findra/Diagnostics/SelfTest.cs`:

```csharp
namespace Findra.Diagnostics;

/// <summary>
/// `--searchtest`: everything that can be checked in this process, with no helper,
/// no pipe and no admin rights.
/// </summary>
public static class SelfTest
{
    public static int Run()
    {
        int failed = 0;
        Console.WriteLine("findra --searchtest");
        Console.WriteLine();

        failed += Check("paths are writable", () =>
        {
            foreach (string d in new[] { Paths.Config, Paths.Models, Paths.Index, Paths.Logs })
            {
                Paths.Ensure(d);
                string probe = Path.Combine(d, ".write-probe");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
            }
            return null;
        });

        failed += Check("models are not under Roaming", () =>
        {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Paths.Models.StartsWith(roaming, StringComparison.OrdinalIgnoreCase)
                 ? $"models resolve to {Paths.Models}" : null;
        });

        failed += Check("query grammar parses", () =>
        {
            var q = new SearchQuery("sunset ext:jpg size:>1mb");
            if (!q.HasNameTerms) return "no name terms parsed from a query that has one";
            if (!q.Exts.Contains("jpg")) return "ext:jpg not parsed";
            if (q.MinBytes <= 0) return "size:>1mb not parsed";
            return null;
        });

        failed += Check("name index round-trips a record", () =>
        {
            var ix = new NameIndex('C');
            ix.Upsert(5, 0, NtfsVolume.FileAttributeDirectory, "C:");
            ix.Upsert(100, 5, 0, "findra-selftest.txt");
            var hits = new List<NameIndex.Hit>();
            ix.Search(new SearchQuery("findra-selftest"), hits);
            if (hits.Count != 1) return $"expected 1 hit, got {hits.Count}";
            if (ix.PathOf(hits[0].Record) != @"C:\findra-selftest.txt") return "path rebuild wrong";
            return null;
        });

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "all checks passed" : $"{failed} check(s) FAILED");
        return failed == 0 ? 0 : 1;
    }

    private static int Check(string name, Func<string?> body)
    {
        try
        {
            string? problem = body();
            Console.WriteLine($"  {(problem is null ? "ok  " : "FAIL")}  {name}{(problem is null ? "" : "  -  " + problem)}");
            return problem is null ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  {name}  -  {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
```

- [ ] **Step 3: Wire both into Program**

In `src/Findra/Program.cs`:

```csharp
"--searchprobe" => Diagnostics.SearchProbe.Run(args),
"--searchtest"  => Diagnostics.SelfTest.Run(),
```

- [ ] **Step 4: Verify the self-test with no helper running**

Run: `dotnet run --project src/Findra -- --searchtest`
Expected: four `ok` lines and `all checks passed`, exit code 0. This must work unelevated.

- [ ] **Step 5: Verify the probe end to end**

With the helper from Task 7 running in an elevated terminal, in a **normal** terminal run:

```bash
dotnet run --project src/Findra -- --searchprobe sunset
```

Expected output shape:

```
findra --searchprobe  query: "sunset"

  helper task registered : yes
  answered by            : pid 24188  (the helper)
  volume C               : 1,532,238 names, 88 MB
  generation             : 1
  reply generation       : 1
  round trip             : 3.4 ms
  rows                   : 9

     0.98  C:\Users\...\sunset over water.jpg
```

Two things to confirm by eye, because they are the whole point of this plan: **`answered
by` is a different pid from the probe**, and **reply generation matches**. If `answered by`
shows this process, the client is somehow searching in-process and the split is broken.

- [ ] **Step 6: Verify the probe fails cleanly with no helper**

Stop the helper, then run the probe again.
Expected: `pipe : UNREACHABLE`, the two-line instruction, exit code 1. No stack trace.

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test`
Expected: PASS - 6 Frame, 5 Message, 7 Generation, 4 NameServer, 3 NameClient, 6 NameIndex, 5 HelperTask, 4 Paths = 40 tests.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Diagnostics: --searchprobe and --searchtest"
```

---

## Done when

- `dotnet test` is green.
- `findra --searchtest` passes unelevated, with no helper running.
- `findra --searchprobe sunset` returns real files from the machine, reports a helper pid
  different from its own, and shows matching generations.
- `grep -ric prism src/` returns 0 for every file.
- Nothing under `src/Findra/Names/` or `src/Findra/Pipe/` references a document decoder,
  image codec, ONNX runtime or Whisper binding.

## What comes next

| Plan | Delivers |
|---|---|
| **2 - The widget** | Palette layer with ground-aware derivation, six palettes, `palettes.json`, the ported card, the capsule, tray, hotkey with fallback chain, `--searchshot` |
| **3 - Content** | FTS5 document store, document text extraction, the indexer child process, journal-driven enqueue, `--searchindex` |
| **4 - Capabilities** | Model store, vector store, SigLIP-2, e5, Whisper, per-capability gating, the first-run download screen, `--searchmodels` |
| **5 - Settings and shipping** | Sectioned settings window, drives, exclusions, self-contained publish, winget manifest |
