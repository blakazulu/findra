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
    private Process? _proc;
    private int _restarts;
    private DateTime _lastStart = DateTime.MinValue;
    private readonly object _gate = new();
    private bool _stopped;

    /// <summary>Created once and held for the life of the interface. Closing it is what kills the
    /// child, so it must outlive every child this host starts and must not be disposed anywhere
    /// but <see cref="Dispose"/>.</summary>
    private readonly JobObject? _job = JobObject.CreateKillOnClose();

    public bool Running { get { lock (_gate) return _proc is { HasExited: false }; } }

    public void EnsureRunning()
    {
        lock (_gate)
        {
            if (_stopped) return;
            if (_proc is { HasExited: false }) return;
            if (_proc is { HasExited: true })
            {
                Log.Warn("index", $"indexer exited with code {_proc.ExitCode.ToString(CultureInfo.InvariantCulture)} - restarting" +
                                   (_restarts > 0 ? $" (restart {(_restarts + 1).ToString(CultureInfo.InvariantCulture)})" : ""));
                _proc.Dispose(); _proc = null;
                _restarts++;
            }
            // backoff: 5 s, 10, 20 ... capped at 5 min, so a file that kills it every time does
            // not turn into a process storm
            double wait = Math.Min(300, 5 * Math.Pow(2, Math.Max(0, _restarts - 1)));
            if (_restarts > 0 && (DateTime.UtcNow - _lastStart).TotalSeconds < wait) return;

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
                return;
            }
            try
            {
                _proc = Process.Start(new ProcessStartInfo(exe, $"--index {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}")
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                });
                _lastStart = DateTime.UtcNow;

                // Assigned immediately, and the answer is written down. "The child outlived me"
                // is unanswerable without knowing which mechanism was holding it: the job, which
                // the kernel enforces, or the child's own poll on a process id Windows is free to
                // reissue to something else.
                bool inJob = _proc is not null && _job is not null && _job.Assign(_proc);
                Log.Info("index", $"indexer started (pid {_proc?.Id.ToString(CultureInfo.InvariantCulture)}) - " +
                                  (inJob
                                    ? "it is in a kill-on-close job, so it dies with this process whatever happens to it"
                                    : "it is NOT in a job; it falls back to watching this process's id"));
            }
            catch (Exception ex) { Log.Once("index|start", "ERROR", "index", $"cannot start the indexer :: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopped = true;
            try
            {
                if (_proc is { HasExited: false })
                {
                    // it watches our pid and exits on its own; give it a moment, then insist
                    if (!_proc.WaitForExit(3000)) _proc.Kill();
                    Log.Info("index", "indexer stopped");
                }
            }
            catch { }
            _proc?.Dispose(); _proc = null;
            // Last, and unconditionally: this is the handle whose closing kills anything still in
            // the job, including a child that ignored every polite request above.
            _job?.Dispose();
        }
    }
}
