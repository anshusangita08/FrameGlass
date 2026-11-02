using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace FrameGlass;

/// <summary>One poll of everything the overlay draws. Null = sensor not present on this machine.</summary>
sealed class Reading
{
    public bool Ok;
    public string CpuName = "", GpuName = "", GpuNameFull = "", GameName = "", AppName = "";
    public string Display = "", DisplayFull = "", Driver = "";
    public string Exe = "", Api = "", Arch = "", Res = "", ResNative = "", ResScale = "", Ping = "--";
    public float? CpuLoad, CpuTemp, CpuClock, CpuPower, CpuFan;
    public float? GpuLoad, GpuTemp, GpuHot, GpuClock, GpuPower, GpuFan, VramUsed, VramTotal;
    /// <summary>VRAM (memory) clock in MHz, LHM "GPU Memory" Clock sensor. Null when the GPU does not report it.</summary>
    public float? VramClock;
    /// <summary>VRAM (memory) temperature in °C, LHM "GPU Memory" Temperature sensor. Null when not reported.</summary>
    public float? VramTemp;
    public float? RamUsed, RamTotal;
    public float? NetUp, NetDown, DiskRead, DiskWrite, PingMs;
}

/// <summary>LibreHardwareMonitor wrapper for CPU/GPU, GlobalMemoryStatusEx for RAM.</summary>
sealed class Sensors : IDisposable
{
    readonly Computer _pc = new() { IsCpuEnabled = true, IsGpuEnabled = true };
    readonly List<IHardware> _gpus = [];
    readonly Dictionary<(string, SensorType), ISensor> _cpu = new();
    readonly Dictionary<IHardware, Dictionary<(string, SensorType), ISensor>> _gpuMaps = new();
    readonly string _match;
    IHardware? _cpuHw;
    IHardware? _pinnedGpu;

    // A sensor that throws does so on every tick, so the message is worth logging once, not 26
    // times a second. These hold the first report and nothing after it.
    string? _warnedCpu, _warnedGpu;

    /// <summary>Log a recurring sensor fault the first time only, and return the text for the flag.</summary>
    static string LogOnce(string msg) { Program.Log("sensors: " + msg); return msg; }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtended;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX s);

    public Sensors(string gpuMatch)
    {
        _match = gpuMatch;
        try { _pc.Open(); }
        catch (Exception e)
        {
            MessageBox.Show($"Sensor driver failed to load.\n\n{e.Message}", "FrameGlass");
        }

        foreach (var hw in _pc.Hardware)
        {
            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                    _cpuHw = hw;
                    _cpu = Map(hw);
                    break;
                case HardwareType t when t.ToString().StartsWith("Gpu"):
                    _gpus.Add(hw);
                    _gpuMaps[hw] = Map(hw);
                    break;
            }
        }

        if (_match == MatchAll) return;   // read every adapter, summed
        if (_match.Length > 0)
            _pinnedGpu = _gpus.FirstOrDefault(g => g.Name.Contains(_match, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The "read every adapter" choice, as it appears in the settings dropdown. The string is both
    /// the label and the stored value so the picker needs no display/value mapping.
    /// </summary>
    public const string MatchAll = "All GPUs";

    // GPUs report duplicate sensor names (two "D3D Copy" loads), so first one wins.
    static Dictionary<(string, SensorType), ISensor> Map(IHardware hw)
    {
        var map = new Dictionary<(string, SensorType), ISensor>();
        foreach (var s in hw.Sensors) map.TryAdd((s.Name, s.SensorType), s);
        return map;
    }

    static float? V(Dictionary<(string, SensorType), ISensor> m, string name, SensorType type) =>
        m.TryGetValue((name, type), out var s) ? s.Value : null;

    /// <summary>VRAM sensors report MB; everything else here is already GB.</summary>
    static float? Div(float? mb) => mb is null ? null : mb / 1024f;

    public Reading Read()
    {
        var r = new Reading();

        if (_cpuHw is not null)
        {
            // LibreHardwareMonitor throws from inside Update() on hardware it does not fully
            // understand - a 5600X hits NullReferenceException in Amd17Cpu.Update() on every tick.
            // Unguarded, that one throw skipped everything after it in Read(), so the CPU fields,
            // RAM and the whole GPU read all came back empty too. One bad sensor should cost the
            // reading it belongs to, not the entire frame.
            try { _cpuHw.Update(); }
            catch (Exception e) { _warnedCpu = _warnedCpu ?? LogOnce("cpu update: " + e.Message); }
            r.Ok = true;
            r.CpuName = _cpuHw.Name;
            r.CpuLoad = V(_cpu, "CPU Total", SensorType.Load);
            r.CpuTemp = V(_cpu, "Core (Tctl/Tdie)", SensorType.Temperature) ?? V(_cpu, "CPU Package", SensorType.Temperature);
            r.CpuClock = V(_cpu, "Cores (Average)", SensorType.Clock);
            r.CpuPower = V(_cpu, "Package", SensorType.Power);
            r.CpuFan = V(_cpu, "CPU Fan", SensorType.Fan)      // LHM calls it "CPU Fan"
                      ?? V(_cpu, "Fan", SensorType.Fan)
                      ?? V(_cpu, "CPU Total", SensorType.Fan);   // some boards report it as a total
        }

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            const double gb = 1024.0 * 1024 * 1024;
            r.RamTotal = (float)(mem.ullTotalPhys / gb);
            r.RamUsed = (float)((mem.ullTotalPhys - mem.ullAvailPhys) / gb);
        }

        if (_gpus.Count > 0)
        {
            // Same reason as the CPU: a driver that throws must not take the other adapters with it.
            foreach (var g in _gpus)
            {
                try { g.Update(); }
                catch (Exception e) { _warnedGpu = _warnedGpu ?? LogOnce("gpu update: " + e.Message); }
            }
            if (_match == MatchAll) ReadAll(r);
            else ReadOne(r, _pinnedGpu ?? _gpus
                // Two GPUs in the box: the busy one is the one the game is on.
                .OrderByDescending(g => V(_gpuMaps[g], "GPU Core", SensorType.Load) ?? 0)
                .First());
        }

        return r;
    }

    void ReadOne(Reading r, IHardware gpu)
    {
        var m = _gpuMaps[gpu];
        r.Ok = true;
        r.GpuNameFull = gpu.Name;
        r.GpuName = ShortGpuName(gpu.Name);
        r.GpuLoad = V(m, "GPU Core", SensorType.Load);
        r.GpuTemp = V(m, "GPU Core", SensorType.Temperature);
        r.GpuHot = V(m, "GPU Hot Spot", SensorType.Temperature);
        r.GpuClock = V(m, "GPU Core", SensorType.Clock);
        r.GpuPower = V(m, "GPU Package", SensorType.Power);
        r.GpuFan = V(m, "GPU Fan", SensorType.Fan);
        r.VramUsed = Div(V(m, "GPU Memory Used", SensorType.SmallData));
        r.VramTotal = Div(V(m, "GPU Memory Total", SensorType.SmallData));
        // Names confirmed against a live LHM 0.9.6 dump on an RX 7900 XT: both sensors are called
        // "GPU Memory" and are told apart only by SensorType.
        r.VramClock = V(m, "GPU Memory", SensorType.Clock);
        r.VramTemp = V(m, "GPU Memory", SensorType.Temperature);
    }

    /// <summary>
    /// "All GPUs" on a multi-GPU machine. Power and VRAM are additive, so they are summed; load and
    /// temperature are not - two cards at 60% is not 120% - so those show the busiest and the
    /// hottest, which is what you actually want to be warned about. Clock takes the fastest card.
    /// </summary>
    void ReadAll(Reading r)
    {
        r.Ok = true;
        r.GpuNameFull = $"{_gpus.Count} adapters";
        r.GpuName = _gpus.Count == 1 ? ShortGpuName(_gpus[0].Name) : $"{_gpus.Count} GPUs";
        r.GpuLoad = Max(_gpus, "GPU Core", SensorType.Load);
        r.GpuTemp = Max(_gpus, "GPU Core", SensorType.Temperature);
        r.GpuHot = Max(_gpus, "GPU Hot Spot", SensorType.Temperature);
        r.GpuClock = Max(_gpus, "GPU Core", SensorType.Clock);
        r.GpuFan = Max(_gpus, "GPU Fan", SensorType.Fan);
        r.GpuPower = Sum(_gpus, "GPU Package", SensorType.Power);
        r.VramUsed = Sum(_gpus, "GPU Memory Used", SensorType.SmallData) is { } u ? u / 1024f : null;
        r.VramTotal = Sum(_gpus, "GPU Memory Total", SensorType.SmallData) is { } t ? t / 1024f : null;
        // Same rule as the GPU clock and temperature above: fastest memory clock, hottest memory.
        r.VramClock = Max(_gpus, "GPU Memory", SensorType.Clock);
        r.VramTemp = Max(_gpus, "GPU Memory", SensorType.Temperature);
    }

    /// <summary>The highest reading of one sensor across every adapter, ignoring adapters lacking it.</summary>
    float? Max(List<IHardware> gpus, string name, SensorType type)
    {
        var vals = gpus.Select(g => V(_gpuMaps[g], name, type)).Where(x => x is not null).ToList();
        return vals.Count == 0 ? null : vals.Max();
    }

    /// <summary>The total of one sensor across every adapter, in the sensor's own unit.</summary>
    float? Sum(List<IHardware> gpus, string name, SensorType type)
    {
        var vals = gpus.Select(g => V(_gpuMaps[g], name, type)).Where(x => x is not null).ToList();
        return vals.Count == 0 ? null : vals.Sum();
    }

    static string ShortGpuName(string name)
    {
        foreach (var tag in new[] { "AMD Radeon ", "NVIDIA GeForce ", "Intel(R) ", "Intel " })
            if (name.StartsWith(tag, StringComparison.OrdinalIgnoreCase))
                return name[tag.Length..].Replace(" Graphics", "").Trim();
        return name;
    }

    public void Dispose() => _pc.Close();
}
