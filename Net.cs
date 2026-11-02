using System.Diagnostics;
using System.Net.NetworkInformation;

namespace FrameGlass;

/// <summary>
/// Network throughput, disk throughput and ICMP ping. All of it blocks, so it is sampled on a
/// background timer and the UI just reads the last numbers.
/// </summary>
sealed class Net : IDisposable
{
    readonly string _pingAddr;
    readonly System.Threading.Timer _timer;
    readonly Ping _ping = new();
    readonly PerformanceCounter? _diskRead, _diskWrite;
    long _rx, _tx;
    DateTime _at = DateTime.MinValue;

    public float? Up, Down, Read, Write, PingMs;

    public Net(string pingAddr)
    {
        _pingAddr = pingAddr;
        _diskRead = Counter("Disk Read Bytes/sec");
        _diskWrite = Counter("Disk Write Bytes/sec");
        _timer = new System.Threading.Timer(_ => Sample(), null, 500, 1000);
    }

    static PerformanceCounter? Counter(string name)
    {
        // the aggregate instance is "_Total" (Get-Counter shows it lower-cased); "Total" does not
        // exist and the constructor throws, which is how this silently ended up disabled.
        foreach (var instance in new[] { "_Total", "Total" })
        {
            try
            {
                var c = new PerformanceCounter("PhysicalDisk", name, instance, true);
                c.NextValue();   // prime: the first read of a raw counter is always 0
                return c;
            }
            catch (Exception e)
            {
                // Logged, not swallowed. These counters are created by their English names, so on a
                // localised Windows both spellings fail and the disk rows simply never appear -
                // with nothing anywhere to say why. Once, not per attempt.
                if (!_warnedCounters)
                {
                    _warnedCounters = true;
                    Program.Log($"disk counters '{name}' unavailable: {e.Message}");
                }
            }
        }
        return null;
    }

    static bool _warnedCounters;

    readonly SemaphoreSlim _sampling = new(1, 1);

    /// <summary>
    /// One sample per second, never two at once.
    ///
    /// The timer fires on a threadpool thread every second regardless of whether the last callback
    /// finished. The ping's 900ms timeout does not cover DNS resolution, so a slow lookup can let the
    /// next tick start while the previous await is still outstanding. Two overlapping samples would
    /// write the same rate fields (a spike or a negative rate) and use one Ping instance at once.
    /// </summary>
    async void Sample()
    {
        if (!await _sampling.WaitAsync(0)) return;
        try
        {
            SampleNetwork();
            SampleDisk();
            await SamplePing();
        }
        finally { _sampling.Release(); }
    }

    void SampleNetwork()
    {
        try
        {
            long rx = 0, tx = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                var st = nic.GetIPv4Statistics();
                rx += st.BytesReceived;
                tx += st.BytesSent;
            }

            var now = DateTime.UtcNow;
            if (_at != DateTime.MinValue)
            {
                var secs = (now - _at).TotalSeconds;
                if (secs > 0)
                {
                    Down = (float)Math.Max(0, rx - _rx) / (float)secs;
                    Up = (float)Math.Max(0, tx - _tx) / (float)secs;
                }
            }
            _rx = rx; _tx = tx; _at = now;
        }
        catch { /* keep the last values */ }
    }

    void SampleDisk()
    {
        try { if (_diskRead is not null) Read = (float)_diskRead.NextValue(); } catch { }
        try { if (_diskWrite is not null) Write = (float)_diskWrite.NextValue(); } catch { }
    }

    async Task SamplePing()
    {
        // IsNullOrEmpty, not .Length: Sample runs as async void on a threadpool timer, so a null
        // here - "PingAddr": null in a hand-edited config - would throw with no handler and take the
        // whole process down. Config.Load also normalises the value; both checks are kept because
        // this is the one that would kill the app.
        if (string.IsNullOrEmpty(_pingAddr)) return;
        try
        {
            var reply = await _ping.SendPingAsync(_pingAddr, 900);
            if (reply.Status == IPStatus.Success) PingMs = (float)reply.RoundtripTime;
        }
        catch { /* host unreachable or ICMP blocked */ }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _ping.Dispose();
        _diskRead?.Dispose();
        _diskWrite?.Dispose();
        _sampling.Dispose();
    }
}
