using System;
using System.Diagnostics;
using System.Globalization;

namespace Findra;

/// <summary>Starts and supervises the indexer child from the interface. Restarts it with backoff
/// when it dies (a malformed file took a decoder down with it), and stops it when Findra exits -
/// which, together with the child's own parent check, is why indexing stops when the app quits and
/// why there is no other lifetime code anywhere.</summary>
public sealed class IndexerHost : IDisposable
{
    private Process? _proc;
    private int _restarts;
    private DateTime _lastStart = DateTime.MinValue;
    private readonly object _gate = new();
    private bool _stopped;

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
            if (exe.Length == 0) return;
            try
            {
                _proc = Process.Start(new ProcessStartInfo(exe, $"--index {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}")
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                });
                _lastStart = DateTime.UtcNow;
                Log.Info("index", $"indexer started (pid {_proc?.Id.ToString(CultureInfo.InvariantCulture)})");
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
        }
    }
}
