using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Findra;

/// <summary>What the gate answered for the next file: go ahead, or wait - and if waiting, the
/// state token the indexer writes to <c>indexer:state</c> and a reason a person can read.</summary>
public readonly record struct GateVerdict(bool Run, string State, string Reason)
{
    public static readonly GateVerdict Go = new(true, "", "");
}

/// <summary>What is wrong with the card, if anything: nothing, too little of its memory left for
/// Findra's models, or another program working one of its engines. Worst last, because a held
/// reading keeps the worst it has seen.</summary>
public enum GpuPressure { None, Memory, Engine }

/// <summary>
/// Whether the indexer may open the next file now, or has to step aside for somebody else.
///
/// <para><b>A power setting is not enough on its own.</b> It shapes how hard Findra works, and
/// nothing about it knows that the machine is busy. The journal hands every new file to the queue
/// the moment it closes, so a program writing recordings or pictures - a transcription pipeline, a
/// render, an export - has Findra loading its speech, meaning and picture models onto the same
/// card, over and over, in the middle of that program's own work. The models are gigabytes; on a
/// card that is already full, the other program slows by an order of magnitude and the desktop
/// stops drawing smoothly. Nothing about that is fullscreen, so a rule that only knows about
/// fullscreen lets all of it through.</para>
///
/// <para>So there are two rules, checked between files, in this order:</para>
/// <list type="bullet">
/// <item><b>Fullscreen.</b> A game, a presentation or Focus Assist, as Windows itself reports it
/// to anything deciding whether to interrupt somebody.</item>
/// <item><b>Somebody else on the card.</b> Another program working one of its engines hard, or
/// using so much of its memory that what Findra would load no longer fits. Only for a file that
/// would load a model: extracting words from a document is not graphics work, and holding it back
/// because a card is busy would stop the one part of indexing that never touches it.</item>
/// </list>
///
/// <para>A delete passes both - it is a row in a database, not a model. The pause switch is not
/// here: it is checked before the queue is even looked at, and holds back everything.</para>
///
/// <para><b>Anything that cannot be measured is not busy.</b> A gate that blocked on a reading it
/// could not take would stop indexing for good on a machine whose counters are missing, and
/// nothing would say why. Waiting costs time; never starting costs the whole feature.</para>
/// </summary>
public static class IndexGate
{
    /// <summary>The state tokens a waiting indexer writes. <see cref="IndexStatus"/> turns them into
    /// words; nothing else should compare against them by hand.</summary>
    public const string Fullscreen = "fullscreen", GpuBusy = "gpu busy";

    /// <summary>Not a wait: go ahead, on the processor. What a card too full for the models answers
    /// for everything that can be moved there.</summary>
    public const string OnProcessor = "processor";

    /// <summary>
    /// Whether what Windows says about the person at the screen holds reading back, and the words
    /// for it. The number is <c>SHQueryUserNotificationState</c>'s answer.
    ///
    /// <para><b>Quiet time (6) does not hold.</b> It is not Focus Assist: Windows documents it as
    /// the first hour after a person's first sign-in following a clean install or an upgrade - the
    /// very hour a new machine's owner installs Findra, which then read nothing for an hour while
    /// saying something was fullscreen. Neither is not-present (1), a locked screen, which is the
    /// best time there is to read files.</para>
    /// </summary>
    public static (bool Hold, string Reason) Holds(int notificationState) => notificationState switch
    {
        3 => (true, "a fullscreen game"),
        2 => (true, "a fullscreen app"),
        4 => (true, "presentation mode"),
        _ => (false, ""),
    };

    /// <summary>The whole rule, as a pure function, so the order can be tested without a card.</summary>
    public static GateVerdict Decide(bool isDelete, bool usesModels, bool fullscreen, string fullscreenReason,
                                     bool gpuBusy, string gpuReason)
        => Decide(isDelete, usesModels, usesSpeech: false, fullscreen, fullscreenReason,
                  gpuBusy ? GpuPressure.Engine : GpuPressure.None, gpuReason);

    /// <summary>
    /// The same, knowing WHY the card is busy.
    ///
    /// <para><b>A card too full is not a reason to wait.</b> On a small card the ordinary desktop
    /// alone holds most of it - a 3 GB card with 2 GB in use by nothing in particular - and waiting
    /// for room there meant photos were not read for hours. Pictures and meaning run through the
    /// same models on the processor, slower, and the processor never touches the card. A card
    /// somebody is WORKING still makes everything that needs it wait: that is a game or a model,
    /// and the gate exists for it.</para>
    ///
    /// <para>Speech is the exception. Its runtime is chosen once per process, so a recording
    /// cannot move to the processor in the middle of a session; it waits as before.</para>
    /// </summary>
    public static GateVerdict Decide(bool isDelete, bool usesModels, bool usesSpeech, bool fullscreen,
                                     string fullscreenReason, GpuPressure pressure, string gpuReason)
    {
        if (isDelete) return GateVerdict.Go;
        if (fullscreen) return new(false, Fullscreen, fullscreenReason);
        if (!usesModels) return GateVerdict.Go;
        return pressure switch
        {
            GpuPressure.Engine => new(false, GpuBusy, gpuReason),
            GpuPressure.Memory when usesSpeech => new(false, GpuBusy, gpuReason),
            GpuPressure.Memory => new(true, OnProcessor, gpuReason),
            _ => GateVerdict.Go,
        };
    }

    /// <summary>Would reading a file of this kind load the speech model? Recordings, and videos
    /// once Speech is installed (their sound is transcribed).</summary>
    public static bool UsesSpeech(ResultKind kind, CapabilitySet installed)
        => installed.Has(Capability.Speech) && kind is ResultKind.Audio or ResultKind.Video;

    /// <summary>Does reading a file of this kind load a model onto the accelerator, given what is
    /// installed? Documents only through the meaning model; every other kind the indexer reads
    /// does, by definition - a kind with no model installed is skipped without being opened.</summary>
    public static bool UsesModels(ResultKind kind, CapabilitySet installed)
        => kind != ResultKind.Document || installed.Has(Capability.Meaning);

    /// <summary>
    /// When other programs' use of the card leaves too little room for Findra's own models.
    ///
    /// <para>Both halves are needed. <paramref name="othersBytes"/> over a floor, because a small
    /// card with an ordinary desktop on it already has less free than the full model set - and
    /// holding indexing back there for ever would take content search away from every modest
    /// machine, which is the opposite of what the gate is for. And the room left under what Findra
    /// would load, because that is what makes the next model load hurt somebody.</para>
    /// </summary>
    public static bool MemoryBusy(long cardBytes, long othersBytes, long needBytes)
        => cardBytes > 0 && othersBytes > OthersFloor && cardBytes - othersBytes < needBytes;

    /// <summary>What an ordinary desktop may hold on the card without counting as "somebody else is
    /// using it": a compositor, a browser and a few windows sit well under this.</summary>
    public const long OthersFloor = 2L << 30;

    /// <summary>Room left beyond the model files themselves: a session holds working buffers as
    /// well as weights.</summary>
    public const long Margin = 2L << 30;

    /// <summary>Percent of one of the card's engines, by one other program, that counts as working
    /// it. A video playing in a browser sits well under it; a game or a model does not.</summary>
    public const double BusyPercent = 30;

    /// <summary>How long the card has to read free, continuously, before indexing resumes. A
    /// pipeline pauses between steps for a few seconds at a time, and a model load that lands in
    /// that gap is exactly the load this exists to stop.</summary>
    public const double ClearSeconds = 60;
}

/// <summary>Busy at once, free only after <see cref="IndexGate.ClearSeconds"/> of free readings in
/// a row. Pure, and fed the time, so the hysteresis is testable without waiting a minute.</summary>
public sealed class GpuHold
{
    private bool _busy;
    private string _reason = "";
    private double _freeSince = -1;
    private double _lowerSince = -1;

    /// <summary>What the held reading is about. The worst seen since the card was last free, so a
    /// game started over a full card is waited for rather than read around.</summary>
    public GpuPressure Pressure { get; private set; }

    public (bool Busy, string Reason, bool Changed) Update(bool busyNow, string why, double nowSeconds,
                                                           GpuPressure kind = GpuPressure.Engine)
    {
        if (busyNow)
        {
            _freeSince = -1;
            bool changed = !_busy || kind > Pressure;
            _busy = true;
            if (kind >= Pressure)
            {
                Pressure = kind;
                _reason = why;
                _lowerSince = -1;
            }
            else
            {
                // Still busy, but for a lesser reason - the game closed and the card is merely
                // full. The same minute as clearing, so a pause between rounds is not a step down.
                if (_lowerSince < 0) _lowerSince = nowSeconds;
                if (nowSeconds - _lowerSince >= IndexGate.ClearSeconds)
                {
                    Pressure = kind;
                    _reason = why;
                    _lowerSince = -1;
                    changed = true;
                }
            }
            return (true, _reason, changed);
        }
        if (!_busy) return (false, "", false);
        if (_freeSince < 0) _freeSince = nowSeconds;
        if (nowSeconds - _freeSince < IndexGate.ClearSeconds) return (true, _reason, false);
        _busy = false;
        _reason = "";
        _freeSince = -1;
        _lowerSince = -1;
        Pressure = GpuPressure.None;
        return (false, "", true);
    }
}

/// <summary>
/// The gate on this machine: Windows' own "is somebody busy" answer and the GPU performance
/// counters. Owned by the indexer child for its whole life. Every reading is cached for a couple of
/// seconds, because the queue asks per file and a first pass through small documents asks many
/// times a second.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MachineGate : IDisposable
{
    private const double SampleSeconds = 2;

    private readonly Func<CapabilitySet> _installed;
    private readonly int _self = Environment.ProcessId;
    private readonly int _parent;
    private readonly GpuHold _hold = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly string _luid;
    private readonly long _cardBytes;

    private IntPtr _query, _engine, _procMem, _adapterMem;
    private bool _countersBroken;
    private double _sampledAt = double.NegativeInfinity;
    private GpuPressure _gpuPressure;
    private string _gpuReason = "";
    private double _quietAt = double.NegativeInfinity;
    private bool _quiet;
    private string _quietReason = "";

    public MachineGate(Func<CapabilitySet> installed, int parentPid)
    {
        _installed = installed;
        _parent = parentPid;
        GpuChoice? card = GpuAdapter.Best() ?? FirstHardware();
        _luid = card?.Luid ?? "";
        _cardBytes = card?.DedicatedBytes ?? 0;
        if (_luid.Length == 0)
        {
            _countersBroken = true;
            Log.Info("index", "gpu gate: no graphics adapter could be named - the indexer cannot see who else is using the card");
        }
        else Log.Info("index", $"gpu gate: watching {card!.Name} ({Sizes.Human(_cardBytes)})");
    }

    private static GpuChoice? FirstHardware()
    {
        foreach (GpuChoice a in GpuAdapter.All()) if (!a.Software) return a;
        return null;
    }

    /// <summary>The verdict for the next file.</summary>
    public GateVerdict Ask(bool isDelete, ResultKind kind)
    {
        if (isDelete) return GateVerdict.Go;
        (bool quiet, string quietWhy) = Quiet();
        CapabilitySet installed = _installed();
        bool usesModels = IndexGate.UsesModels(kind, installed);
        (GpuPressure pressure, string busyWhy) = usesModels && !quiet ? Gpu() : (GpuPressure.None, "");
        return IndexGate.Decide(isDelete, usesModels, IndexGate.UsesSpeech(kind, installed), quiet, quietWhy,
                                pressure, busyWhy);
    }

    /// <summary>What the gate would say about the heaviest file there is, for <c>--searchprobe</c>.
    /// A probe runs once, so the card's hold cannot build up; this is one reading, not a verdict
    /// with a minute of history behind it. It waits a second first, because a utilisation is a
    /// rate and the counters need two samples some time apart to report one.</summary>
    public GateVerdict Probe()
    {
        if (!_countersBroken && _query == IntPtr.Zero && Open()) System.Threading.Thread.Sleep(1000);
        return Ask(isDelete: false, ResultKind.Video);
    }

    // ---- fullscreen ----

    private enum Quns
    {
        NotPresent = 1, Busy = 2, RunningD3dFullScreen = 3, PresentationMode = 4,
        AcceptsNotifications = 5, QuietTime = 6, App = 7,
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out Quns state);

    private (bool, string) Quiet()
    {
        double now = _clock.Elapsed.TotalSeconds;
        if (now - _quietAt < 1) return (_quiet, _quietReason);
        _quietAt = now;
        try
        {
            // NotPresent - a locked screen, a screen saver, another user switched in - is the best
            // time there is to read files, not a reason to stop.
            (_quiet, _quietReason) = SHQueryUserNotificationState(out Quns st) == 0
                ? IndexGate.Holds((int)st)
                : (false, "");
        }
        catch (Exception ex)
        {
            Log.Once("index|quiet", "WARN", "index", "gpu gate: Windows would not say whether something is fullscreen :: " + ex.Message);
            (_quiet, _quietReason) = (false, "");
        }
        return (_quiet, _quietReason);
    }

    // ---- the card ----

    private (GpuPressure, string) Gpu()
    {
        double now = _clock.Elapsed.TotalSeconds;
        if (now - _sampledAt >= SampleSeconds)
        {
            _sampledAt = now;
            (GpuPressure seen, string why) = Sample();
            (bool busy, string reason, bool changed) = _hold.Update(seen != GpuPressure.None, why, now, seen);
            if (changed)
                Log.Info("index", !busy
                    ? $"gpu gate: the card has been free for {IndexGate.ClearSeconds.ToString("0", CultureInfo.InvariantCulture)} s"
                    : _hold.Pressure == GpuPressure.Memory
                        ? $"gpu gate: too full ({reason}) - pictures and meaning are read on the processor, speech waits"
                        : $"gpu gate: busy ({reason}) - the indexer waits and lets its models go");
            _gpuPressure = busy ? _hold.Pressure : GpuPressure.None;
            _gpuReason = reason;
        }
        return (_gpuPressure, _gpuReason);
    }

    // Engines FIRST: a card somebody is working is a wait, a card that is only full is a move to
    // the processor, and a game on a full card has to read as the first of those.
    private (GpuPressure, string) Sample()
    {
        if (_countersBroken) return (GpuPressure.None, "");
        try
        {
            if (_query == IntPtr.Zero && !Open()) return (GpuPressure.None, "");
            if (PdhCollectQueryData(_query) != 0) return (GpuPressure.None, "");

            // Engines: another program working the card hard.
            var perPid = new Dictionary<int, double>();
            foreach ((string name, double v) in Read(_engine))
            {
                if (!name.StartsWith("pid_", StringComparison.Ordinal) || !name.Contains(_luid, StringComparison.OrdinalIgnoreCase)) continue;
                int us = name.IndexOf('_', 4);
                if (us < 0 || !int.TryParse(name.AsSpan(4, us - 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)) continue;
                if (pid == _self || pid == _parent) continue;
                perPid[pid] = Math.Max(perPid.TryGetValue(pid, out double m) ? m : 0, v);
            }
            foreach ((int pid, double util) in perPid)
            {
                if (util < IndexGate.BusyPercent) continue;
                string proc = NameOf(pid);
                // Composing the desktop is not a workload somebody is waiting on.
                if (proc.Equals("dwm", StringComparison.OrdinalIgnoreCase)) continue;
                return (GpuPressure.Engine, $"{proc} is using {util.ToString("0", CultureInfo.InvariantCulture)}% of the card");
            }

            // Memory: the card's dedicated use, less Findra's own share of it.
            double adapter = 0, ours = 0;
            foreach ((string name, double v) in Read(_adapterMem))
                if (name.StartsWith(_luid, StringComparison.OrdinalIgnoreCase)) adapter += v;
            foreach ((string name, double v) in Read(_procMem))
                if (Mine(name) && name.Contains(_luid, StringComparison.OrdinalIgnoreCase)) ours += v;
            long others = (long)Math.Max(0, adapter - ours);
            long need = Capabilities.TotalBytes(_installed().Have ?? new HashSet<Capability>()) + IndexGate.Margin;
            if (IndexGate.MemoryBusy(_cardBytes, others, need))
                return (GpuPressure.Memory, $"other programs hold {Sizes.Human(others)} of the card's {Sizes.Human(_cardBytes)}");
            return (GpuPressure.None, "");
        }
        catch (Exception ex)
        {
            _countersBroken = true;
            Log.Once("index|gpugate", "WARN", "index", $"gpu gate: the GPU counters could not be read, the gate is off :: {ex.GetType().Name}: {ex.Message}");
            return (GpuPressure.None, "");
        }
    }

    private bool Mine(string instance)
        => instance.StartsWith($"pid_{_self.ToString(CultureInfo.InvariantCulture)}_", StringComparison.Ordinal)
           || (_parent > 0 && instance.StartsWith($"pid_{_parent.ToString(CultureInfo.InvariantCulture)}_", StringComparison.Ordinal));

    private static string NameOf(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return "pid " + pid.ToString(CultureInfo.InvariantCulture); }
    }

    private bool Open()
    {
        // The ENGLISH counter paths: the localised names differ with the display language.
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0
            || PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _engine) != 0
            || PdhAddEnglishCounterW(_query, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out _procMem) != 0
            || PdhAddEnglishCounterW(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _adapterMem) != 0)
        {
            _countersBroken = true;
            Log.Once("index|gpugate", "WARN", "index", "gpu gate: the GPU performance counters are not available, the gate is off");
            return false;
        }
        PdhCollectQueryData(_query);   // a utilisation is a rate, and a rate needs two samples
        return true;
    }

    private static List<(string, double)> Read(IntPtr counter)
    {
        var list = new List<(string, double)>();
        uint size = 0, count = 0;
        int rc = PdhGetFormattedCounterArrayW(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, ref count, IntPtr.Zero);
        if (rc != PdhMoreData || size == 0) return list;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, ref count, buf) != 0) return list;
            // PDH_FMT_COUNTERVALUE_ITEM_W: a name pointer, then { CStatus, padding, double }.
            int stride = IntPtr.Size + 16;
            for (int i = 0; i < count; i++)
            {
                IntPtr item = buf + i * stride;
                uint status = (uint)Marshal.ReadInt32(item, IntPtr.Size);
                if (status != 0 && status != 1) continue;   // valid data, new data
                string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, IntPtr.Size + 8));
                list.Add((name, v));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }

    private const uint PdhFmtDouble = 0x00000200, PdhFmtNoCap100 = 0x00008000;
    private const int PdhMoreData = unchecked((int)0x800007D2);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr buffer);
}
