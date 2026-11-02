using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace FrameGlass;

/// <summary>
/// Names the display the game is actually on: EDID monitor name, device, mode, adapter, scale.
/// All of it is static per monitor, so it is cached and only re-read when the cached key changes.
/// </summary>
static class Displays
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int Cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    const int CURRENT_SETTINGS = -1, MONITOR_DEFAULTTONEAREST = 2, MDT_EFFECTIVE_DPI = 0, ATTACHED = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string? dev, uint num, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string? dev, int mode, ref DEVMODE dm);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dpiX, out uint dpiY);

    sealed record Info(string Adapter, string Monitor, uint W, uint H, uint Hz, uint Dpi);

    static readonly Dictionary<string, Info> Cache = new();

    /// <summary>One line describing the display holding <paramref name="window"/>. The short form is
    /// monitor name and mode only; the full form adds scale, device and adapter.</summary>
    public static string Describe(IntPtr window, bool full)
    {
        try
        {
            var mon = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return "";

            var screen = Screen.FromHandle(window);
            if (!Cache.TryGetValue(screen.DeviceName, out var i)) Cache[screen.DeviceName] = i = Read(screen.DeviceName, mon);

            var mode = i.W > 0 ? $"{i.W}x{i.H}@{i.Hz}" : "";
            if (!full) return string.Join(" ", new[] { i.Monitor, mode }.Where(s => s.Length > 0));

            return string.Join("  ", new[] { i.Monitor, mode + Scale(i.Dpi), screen.DeviceName.Replace(@"\\.\", ""), i.Adapter }
                .Where(s => s.Length > 0));
        }
        catch { return ""; }
    }

    /// <summary>The panel's own mode, e.g. 3840x2160, which is not the desktop size on a scaled monitor.</summary>
    public static Size PanelSize(string device)
    {
        var dm = new DEVMODE { DeviceName = device };
        return EnumDisplaySettings(device, CURRENT_SETTINGS, ref dm) ? new Size((int)dm.PelsWidth, (int)dm.PelsHeight) : Size.Empty;
    }

    static string Scale(uint dpi) => dpi > 0 && dpi != 96 ? $" ({dpi * 100 / 96}%)" : "";

    static Info Read(string device, IntPtr mon)
    {
        uint dpi = 96;
        try { GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out dpi, out _); } catch { }

        uint w = 0, h = 0, hz = 0;
        var dm = new DEVMODE { DeviceName = device };
        if (EnumDisplaySettings(device, CURRENT_SETTINGS, ref dm)) { w = dm.PelsWidth; h = dm.PelsHeight; hz = dm.DisplayFrequency; }

        string adapter = "", monitor = "";
        for (uint i = 0; ; i++)
        {
            var a = New();
            if (!EnumDisplayDevices(null, i, ref a, 0)) break;
            if (!a.DeviceName.Equals(device, StringComparison.OrdinalIgnoreCase) || (a.StateFlags & ATTACHED) == 0) continue;
            adapter = a.DeviceString;
            break;
        }
        for (uint j = 0; j < 8; j++)
        {
            var m = New();
            if (!EnumDisplayDevices(device, j, ref m, 0)) break;
            if ((m.StateFlags & ATTACHED) == 0) continue;
            monitor = EdidName(m.DeviceKey, m.DeviceID);
            if (monitor.Length > 0) break;
        }
        return new Info(adapter, monitor, w, h, hz, dpi);
    }

    /// <summary>Display driver version and date for the adapter driving this monitor, e.g. "32.0.31036.15".</summary>
    public static string DriverVersion(string device)
    {
        for (uint i = 0; ; i++)
        {
            var a = New();
            if (!EnumDisplayDevices(null, i, ref a, 0)) break;
            if (!a.DeviceName.Equals(device, StringComparison.OrdinalIgnoreCase) || (a.StateFlags & ATTACHED) == 0) continue;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{a.DeviceID}\Driver");
                if (key?.GetValue("DriverVersion") is string ver && ver.Length > 0) return ver;
            }
            catch { /* no rights or no key */ }
            break;
        }
        return "";
    }

    static DISPLAY_DEVICE New() => new()
    {
        Cb = Marshal.SizeOf<DISPLAY_DEVICE>(),
        DeviceName = "", DeviceString = "", DeviceID = "", DeviceKey = "",
    };

    /// <summary>
    /// Monitor friendly name, read from the EDID monitor-name descriptor. Empty when there is none.
    ///
    /// Two details must both be right for a name to be found. Getting either one wrong leaves every monitor nameless:
    ///
    /// 1. The descriptor tag is the fourth byte of an 18-byte block, not the first two read as a
    ///    little-endian word, and the 13 bytes of text are ASCII rather than UTF-16.
    /// 2. The registry path. EnumDisplayDevices returns DeviceKey as a full hive path
    ///    ("\Registry\Machine\System\..."), which OpenSubKey will not take. It can also point at the
    ///    display *class* key, which has no EDID at all - the EDID lives under
    ///    Enum\DISPLAY\&lt;hardware id&gt;\&lt;instance&gt;. So the lookup is retried against the hardware id
    ///    taken from DeviceID ("MONITOR\ACR0C59\{...}\0003").
    /// </summary>
    static string EdidName(string deviceKey, string deviceId)
    {
        var fromKey = NameFromEdid($@"{HiveRelative(deviceKey)}\Device Parameters");
        if (fromKey.Length > 0) return fromKey;

        // "MONITOR\ACR0C59\{4d36e96e-...}\0003" -> "ACR0C59"
        var parts = deviceId.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return "";
        var root = $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}";

        try
        {
            using var sub = Registry.LocalMachine.OpenSubKey(root);
            if (sub is null) return "";
            foreach (var instance in sub.GetSubKeyNames())
            {
                var name = NameFromEdid($@"{root}\{instance}\Device Parameters");
                if (name.Length > 0) return name;
            }
        }
        catch { /* no Enum entry for this monitor */ }
        return "";
    }

    /// <summary>Strips the hive prefix EnumDisplayDevices includes and OpenSubKey refuses.</summary>
    static string HiveRelative(string path)
    {
        const string hive = @"\Registry\Machine\";
        if (path.StartsWith(hive, StringComparison.OrdinalIgnoreCase)) return path[hive.Length..];
        return path.TrimStart('\\');
    }

    /// <summary>Reads the 0xFC descriptor out of an EDID value, or "" if this key has none.</summary>
    static string NameFromEdid(string keyPath)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key?.GetValue("EDID") is not byte[] edid || edid.Length < 126) return "";
            // Descriptors are 18 bytes each, starting at 54. The tag byte is the *fourth* byte of
            // the descriptor: bytes 0-1 and byte 2 are zero, and byte 3 is the tag. Reading bytes 0-1
            // as a little-endian value would never match the tag.
            //
            // The text is 13 bytes of ASCII, newline-terminated - not UTF-16.
            for (int d = 54; d + 17 < edid.Length; d += 18)
            {
                if (edid[d + 3] != 0xFC) continue;   // 0xFC = monitor name
                var s = Encoding.ASCII.GetString(edid, d + 5, 13).TrimEnd('\0', '\n', '\r', ' ');
                if (s.Length > 0) return s;
            }
        }
        catch { /* no readable EDID at this path */ }
        return "";
    }

    /// <summary>Drop the cache after a resolution, display or GPU change.</summary>
    public static void Invalidate() => Cache.Clear();
}
