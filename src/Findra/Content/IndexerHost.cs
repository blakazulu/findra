using System;
using System.Diagnostics;
using System.Globalization;

namespace Findra;

/// <summary>
/// Starts and supervises the indexer child from the interface. Restarts it with backoff when it
/// dies (a malformed file took a decoder down with it), and stops it when Findra exits.
///
/// <para>What actually guarantees that indexing stops when the interface quits is the JOB OBJECT,
/// not this class's <see cref="Dispose"/> and not the child's own poll. The job is created here,
/// the child is assigned to it, and the kernel terminates whatever is inside when the last handle
/// closes - which happens however this process ends, including a force-kill and a crash, because
/// the kernel closes a dead process's handles for it. That is what makes spec §3's "by
/// construction, with no lifetime code to write" a fact rather than an intention.</para>
///
/// <para>The child's parent poll stays as the fallback for any environment that refuses the
/// assignment, and which of the two is in force is logged at startup rather than assumed.</para>
/// </summary>
public sealed class IndexerHost : IDisposable
{
    /// <summary>
    /// The child, as this host needs to see it: whether it is still there, what it ended with, and
    /// how to end it.
    ///
    /// <para>A seam rather than a <see cref="Process"/>, because the sequence this class can
    /// actually get wrong is start, kill, start again - and what the next turn of the interface's
    /// pump then believes about the child it has just been handed. None of that can be driven
    /// against a real process in a test, and a <see cref="Process"/> cannot be faked without
    /// starting one.</para>
    /// </summary>
    public interface IChild : IDisposable
    {
        /// <summary>False once it has gone, however it went.</summary>
        bool Alive { get; }

        /// <summary>What it ended with, for the restart line. Read only once <see cref="Alive"/>
        /// is false.</summary>
        int ExitCode { get; }

        /// <summary>End it now.</summary>
        void Kill();

        /// <summary>Wait for it to go of its own accord. False means it did not, which is the cue
        /// to insist.</summary>
        bool WaitForExit(int milliseconds);
    }

    private IChild? _child;
    private int _restarts;
    private DateTime _lastStart = DateTime.MinValue;
    private readonly object _gate = new();
    private bool _stopped;
    private readonly Func<IChild?> _spawn;
    private readonly Func<DateTime> _now;

    /// <summary>Created once and held for the life of the interface. Closing it is what kills the
    /// child, so it must outlive every child this host starts and must not be disposed anywhere
    /// but <see cref="Dispose"/>.</summary>
    private readonly JobObject? _job;

    public IndexerHost() : this(null, null) { }

    /// <summary>
    /// The two effects as delegates, so the whole supervision sequence can be driven without a
    /// process: what starting a child does, and what the clock says.
    ///
    /// <para>The job object belongs to the real spawn and is not created for an injected one -
    /// there is nothing to put in it, and a test has no business taking a kernel handle whose
    /// closing kills processes.</para>
    /// </summary>
    public IndexerHost(Func<IChild?>? spawn, Func<DateTime>? now)
    {
        _now = now ?? (() => DateTime.UtcNow);
        _job = spawn is null ? JobObject.CreateKillOnClose() : null;
        _spawn = spawn ?? StartTheIndexer;
    }

    public bool Running { get { lock (_gate) return _child is { Alive: true }; } }

    /// <summary>
    /// How long the child now running has been running, or null when there is not one.
    ///
    /// <para>The watchdog needs it and nothing else does. <c>indexer:beat</c> is written by the
    /// CHILD, and a child that is killed leaves its last beat behind: judged on that row alone, the
    /// replacement started on the very next turn of the pump is condemned microseconds after it
    /// starts, on evidence about its predecessor. That is a kill and a restart every 400 ms for as
    /// long as Findra runs, and the file that caused it is never written off - an attempt is only
    /// committed once a child has taken a row off the queue, and this one never gets that far.</para>
    /// </summary>
    public TimeSpan? SinceStart
    {
        get
        {
            lock (_gate)
                return _child is { Alive: true } && _lastStart != DateTime.MinValue
                    ? _now() - _lastStart
                    : null;
        }
    }

    public void EnsureRunning()
    {
        lock (_gate)
        {
            if (_stopped) return;
            if (_child is { Alive: true }) return;
            if (_child is not null)
            {
                Log.Warn("index", $"indexer exited with code {_child.ExitCode.ToString(CultureInfo.InvariantCulture)} - restarting" +
                                   (_restarts > 0 ? $" (restart {(_restarts + 1).ToString(CultureInfo.InvariantCulture)})" : ""));
                _child.Dispose(); _child = null;
                _restarts++;
            }
            // backoff: 5 s, 10, 20 ... capped at 5 min, so a file that kills it every time does
            // not turn into a process storm. A WATCHDOG kill leaves _restarts at 0 and so restarts
            // at once, which is deliberate and is bounded by the grace period in
            // IndexerWatch.ShouldRestart rather than by a wait here.
            double wait = Math.Min(300, 5 * Math.Pow(2, Math.Max(0, _restarts - 1)));
            if (_restarts > 0 && (_now() - _lastStart).TotalSeconds < wait) return;

            IChild? started = _spawn();
            if (started is null) return;
            _child = started;
            _lastStart = _now();
        }
    }

    /// <summary>Start the real <c>--index</c> child, put it in the job, and write down which of the
    /// two lifetimes is holding it. Null means there is no child this turn, and every path that
    /// returns it has already said why.</summary>
    private IChild? StartTheIndexer()
    {
        string exe = Environment.ProcessPath ?? "";
        if (exe.Length == 0)
        {
            // Said out loud rather than returned from quietly. There is no path to the
            // executable, so there is no indexer this session and no later call will find
            // one - and an unexplained silence here reads exactly like a child that started
            // and died, which is a different fault with a different fix.
            Log.Once("index|nopath", "ERROR", "index",
                "there is no path to this executable, so the indexer child cannot be started; " +
                "nothing will be read inside files this session");
            return null;
        }
        try
        {
            var start = new ProcessStartInfo(exe, $"--index {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            };

            // Which Vulkan device transcribes, decided HERE because it cannot be decided
            // anywhere later. The speech runtime takes the first device it is shown, which on
            // a machine with integrated graphics beside a card is the integrated one - and for
            // this workload that is slower than the processor the chain skipped to reach it.
            // The only lever is which devices exist at all, and the environment a process
            // reads is fixed before it starts: setting this from inside the child would update
            // the runtime's own copy and not the block the native code reads.
            string? visible = VulkanAdapter.Visible();
            if (visible is not null)
            {
                start.Environment[VulkanAdapter.VisibleDevices] = visible;
                Log.Info("index", $"speech devices: {VulkanAdapter.Describe()}");
            }

            Process? proc = Process.Start(start);
            if (proc is null) return null;

            // Assigned immediately, and the answer is written down. "The child outlived me"
            // is unanswerable without knowing which mechanism was holding it: the job, which
            // the kernel enforces, or the child's own poll on a process id Windows is free to
            // reissue to something else.
            bool inJob = _job is not null && _job.Assign(proc);
            Log.Info("index", $"indexer started (pid {proc.Id.ToString(CultureInfo.InvariantCulture)}) - " +
                              (inJob
                                ? "it is in a kill-on-close job, so it dies with this process whatever happens to it"
                                : "it is NOT in a job; it falls back to watching this process's id"));
            return new ProcessChild(proc);
        }
        catch (Exception ex)
        {
            Log.Once("index|start", "ERROR", "index", $"cannot start the indexer :: {ex.Message}");
            return null;
        }
    }

    /// <summary>The real child: a Windows process.</summary>
    private sealed class ProcessChild(Process proc) : IChild
    {
        public bool Alive => !proc.HasExited;
        public int ExitCode => proc.ExitCode;
        public void Kill() => proc.Kill();
        public bool WaitForExit(int milliseconds) => proc.WaitForExit(milliseconds);
        public void Dispose() => proc.Dispose();
    }

    /// <summary>Stop a child that has stopped making progress, so that its attempt is spent and the
    /// next one starts.
    ///
    /// <para>The restart is IMMEDIATE: the crash backoff is reset rather than escalated, because a
    /// stalled file is not a process storm and a file that hangs every time has to reach its third
    /// attempt in minutes rather than over an afternoon. What stops that becoming a storm of its own
    /// is <see cref="IndexerWatch.ShouldRestart"/>'s grace period - the beat row on disk belongs to
    /// the child just killed, and the one that replaces it is not judged on it.</para></summary>
    public void Kill(string reason) => Kill(reason, m => Log.Warn("index", m), () =>
    {
        lock (_gate)
        {
            if (_child is not { Alive: true }) return false;
            _child.Kill();
            _child.Dispose();
            _child = null;
            _restarts = 0;
            _lastStart = DateTime.MinValue;
            return true;
        }
    });

    /// <summary>The effects as delegates, so a test can assert what was killed and what was said
    /// without a process.</summary>
    public static void Kill(string reason, Action<string> log, Func<bool> kill)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(kill);
        if (kill()) log(reason);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopped = true;
            try
            {
                if (_child is { Alive: true })
                {
                    // it watches our pid and exits on its own; give it a moment, then insist
                    if (!_child.WaitForExit(3000)) _child.Kill();
                    Log.Info("index", "indexer stopped");
                }
            }
            catch { }
            _child?.Dispose(); _child = null;
            // Last, and unconditionally: this is the handle whose closing kills anything still in
            // the job, including a child that ignored every polite request above.
            _job?.Dispose();
        }
    }
}
