using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace FrameGlass;

readonly record struct FrameStats(double Fps, double AvgFps, double Max, double Min, double Low1, double Low10, double MsAvg, double[] Plot)
{
    public static readonly FrameStats Empty = new(0, 0, 0, 0, 0, 0, 0, []);
}

/// <summary>
/// Per-frame present times for one process, read live from ETW (Microsoft-Windows-DxgKrnl "Present").
///
/// DxgKrnl fires ~200k events/s once enabled, and resolving the event name for each one costs ~6% of a
/// core. So: resolve names for one second to learn the raw IDs of "Present", then drop to AllEvents +
/// an int hash lookup, which is the ~2% floor for delivering the events at all.
/// </summary>
sealed class FrameTimes : IDisposable
{
    const int Cap = 4096;           // ~68s of frames at 60fps
    const int PlotPoints = 90;
    const double MinFrameMs = 0.2;  // sub-0.2ms deltas are double-presents in one frame
    const double MaxFrameMs = 2000; // alt-tab / loading gaps, not stutter

    static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D");

    readonly object _gate = new();
    readonly double[] _ms = new double[Cap];
    readonly long[] _ts = new long[Cap];
    readonly Dictionary<int, string> _idNames = new();
    readonly HashSet<int> _presentIds = new();
    TraceEventSession? _session;
    Thread? _pumpThread;
    int _head, _count, _lastPid = -1;
    long _lastTicks;
    int _targetPid;
    bool _ambiguous, _fast, _failed;

    public bool Failed => _failed;

    public void Start()
    {
        try
        {
            _session = NewSession();
            _session.Source.Dynamic.All += Learn;   // name resolution pass
            _session.EnableProvider(DxgKrnl);
            _pumpThread = new Thread(() => _session.Source.Process())
            {
                Name = "FrameGlass.ETW",
                IsBackground = true,
            };
            _pumpThread.Start();
        }
        catch (Exception e)
        {
            _failed = true;
            MessageBox.Show($"Frame timing unavailable (ETW session failed).\n\n{e.Message}", "FrameGlass");
        }
    }

    static TraceEventSession NewSession()
    {
        try { return new TraceEventSession("FrameGlass-Presents"); }
        catch (Exception)
        {
            return new TraceEventSession($"FrameGlass-Presents-{Environment.ProcessId}");  // stale session
        }
    }

    void Learn(TraceEvent e)
    {
        string name = e.EventName;
        int id = (int)e.ID;
        if (!_idNames.TryAdd(id, name) && _idNames[id] != name)
        {
            _ambiguous = true;   // two names share one raw id: stay on the slow path
            return;
        }

        if (name != "Present") return;

        _presentIds.Add(id);
        Push(e.ProcessID, e.TimeStamp.Ticks);   // slow path still records frames
    }

    /// <summary>Called from the UI tick so the handler swap never happens on the ETW callback thread.</summary>
    public void MaybeUpgrade()
    {
        if (_fast || _ambiguous || _presentIds.Count == 0 || _session is null) return;
        _fast = true;
        _session.Source.Dynamic.All -= Learn;
        _session.Source.AllEvents += Fast;
    }

    void Fast(TraceEvent e)
    {
        if (!_presentIds.Contains((int)e.ID)) return;
        Push(e.ProcessID, e.TimeStamp.Ticks);
    }

    void Push(int pid, long ticks)
    {
        // Only the tracked process's presents count. Frames from any other process (a browser playing
        // video, another game, the compositor) are dropped here, so FPS, frametime, the 1% low and the
        // graph describe the tracked process alone. SetTarget already resets the ring when the tracked
        // window changes, so this is the only filter needed.
        if (pid != Volatile.Read(ref _targetPid)) return;

        lock (_gate)
        {
            if (pid != _lastPid)
            {
                _lastPid = pid;
                _lastTicks = ticks;
                return;
            }

            double ms = (ticks - _lastTicks) / (double)TimeSpan.TicksPerMillisecond;
            _lastTicks = ticks;
            if (ms < MinFrameMs || ms > MaxFrameMs) return;

            _ms[_head] = ms;
            _ts[_head] = ticks;
            _head = (_head + 1) % Cap;
            if (_count < Cap) _count++;
        }
    }

    /// <summary>Follows the focused window's process; pass 0 to stop tracking.</summary>
    public void SetTarget(int pid)
    {
        if (pid == Volatile.Read(ref _targetPid)) return;
        Volatile.Write(ref _targetPid, pid);
        lock (_gate)
        {
            _head = _count = 0;
            _lastPid = pid;
            _lastTicks = 0;
        }
    }

    public FrameStats Snapshot(int windowSec)
    {
        lock (_gate)
        {
            if (_count == 0) return FrameStats.Empty;

            int n = _count;
            int newest = (_head - 1 + Cap) % Cap;
            long now = _ts[newest];
            long windowTicks = windowSec * TimeSpan.TicksPerSecond;

            var win = new List<double>(n);
            for (int i = 1; i <= n; i++)
            {
                int idx = (newest - i + 1 + Cap) % Cap;
                if (now - _ts[idx] > windowTicks) break;
                win.Add(_ms[idx]);
            }
            if (win.Count == 0) win.Add(_ms[newest]);

            double sum = 0;
            foreach (var v in win) sum += v;
            double avgFps = sum > 0 ? win.Count / (sum / 1000.0) : 0;

            // 1% low = the fps of the 99th percentile frame time, 10% low of the 90th.
            var sorted = win.ToArray();
            Array.Sort(sorted);
            double p99 = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))];
            double p90 = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.90))];

            int pn = Math.Min(n, PlotPoints);
            var plot = new double[pn];
            for (int i = 0; i < pn; i++) plot[i] = _ms[(newest - pn + 1 + i + Cap) % Cap];

            return new FrameStats(
                Fps: 1000.0 / sorted[sorted.Length / 2],
                AvgFps: avgFps,
                Max: 1000.0 / sorted[0],
                Min: 1000.0 / sorted[^1],
                Low1: 1000.0 / p99,
                Low10: 1000.0 / p90,
                MsAvg: sum / win.Count,
                Plot: plot);
        }
    }

    public void Dispose()
    {
        try
        {
            if (_session is not null)
            {
                _session.Source.Dynamic.All -= Learn;
                _session.Source.AllEvents -= Fast;
                _session.Source.StopProcessing();
            }

            // Join before disposing the session. The other order left the pump calling into a
            // disposed session whenever the join timed out, which throws on a background thread -
            // during shutdown, the one place an exception has nowhere to go.
            _pumpThread?.Join(500);
            _session?.Dispose();
        }
        catch { /* nothing useful to do at shutdown */ }
    }
}
