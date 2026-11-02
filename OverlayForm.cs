using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FrameGlass;

/// <summary>The overlay itself: a click-through topmost layered window. No taskbar button by design.</summary>
sealed class OverlayForm : Form
{
    const int WS_EX_LAYERED = 0x00080000;
    const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
    const int WM_HOTKEY = 0x0312;
    const uint LWA_COLORKEY = 0x00000001, LWA_ALPHA = 0x00000002;
    const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool IsWow64(IntPtr process, out bool wow64);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern short VkKeyScanEx(char ch, IntPtr layout);
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    static readonly Dictionary<string, uint> Mods = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CTRL"] = 1, ["ALT"] = 2, ["SHIFT"] = 4, ["WIN"] = 8,
    };

    Config _cfg;
    readonly Sensors _sensors;
    readonly FrameTimes _frames;
    readonly Net _net;
    readonly System.Windows.Forms.Timer _timer = new();
    Font _font;
    string _fontName = "";
    double _fontSize;
    DateTime _cfgStamp;
    int _toggleId, _quitId;
    int _cachedPid, _cachedNamePid = -1;
    string _cachedName = "", _cachedTitle = "";
    IntPtr _gameHwnd;
    string _lastScreen = "";
    IntPtr _lastAnchor;
    /// <summary>The pair currently bound to the handle, so re-binding is skipped when unchanged.</summary>
    string? _boundToggle, _boundQuit;
    string _pinName = "";
    int _pinPid;
    Facts _facts;

    /// <summary>Static per-process facts: the exe name, its bitness and which graphics API it loaded.</summary>
    readonly record struct Facts(string Exe, string Api, string Arch)
    {
        static readonly (string Dll, string Name)[] Apis =
        {
            ("d3d12.dll", "D3D12"), ("d3d11.dll", "D3D11"), ("d3d9.dll", "D3D9"),
            ("vulkan-1.dll", "Vulkan"), ("opengl32.dll", "OpenGL"), ("dxgi.dll", "DXGI"),
        };

        public static Facts Of(Process p)
        {
            string arch = "x86";
            try { if (Environment.Is64BitOperatingSystem && IsWow64(p.Handle, out var wow) && !wow) arch = "x64"; } catch { }
            return new Facts(p.ProcessName + ".exe", Which(p), arch);
        }

        static string Which(Process p)
        {
            var loaded = p.Modules.Cast<ProcessModule?>().Select(m => m?.ModuleName ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (dll, name) in Apis)
                if (loaded.Contains(dll)) return name;
            return "";
        }
    }
    Bitmap? _bmp;
    static readonly uint KeyRgb = 0x000101;   // near-black, never used for drawing: colour key for the corners

    public FrameStats LastFrames { get; private set; }
    public Reading LastReading { get; private set; } = new();
    public event Action? Quitting;

    public OverlayForm(Config cfg, Sensors sensors, FrameTimes frames, Net net)
    {
        _cfg = cfg;
        _sensors = sensors;
        _frames = frames;
        _net = net;
        _fontName = cfg.Font;
        _fontSize = cfg.FontSize;
        _font = new Font(_fontName, (float)_fontSize, GraphicsUnit.Point);
        _cfgStamp = File.GetLastWriteTimeUtc(Config.ConfigPath);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // No WS_EX_TRANSPARENT: on a layered window it tells the compositor not to obscure
            // what is behind, and the window then simply never appears. Click-through comes from
            // the hit test below.
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    const int WM_NCHITTEST = 0x0084, HTTRANSPARENT = -1;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterKeys(_cfg.ToggleKey, _cfg.QuitKey);

        // start polling here, not in OnShown: Shown never fires when the form is shown
        // before Application.Run, and without this nothing is ever painted.
        _timer.Interval = Math.Max(100, _cfg.IntervalMs);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>
    /// Binds the two global hotkeys, replacing any earlier binding. Called again whenever the
    /// settings window changes them, so a new combination takes effect at once -
    /// RegisterHotKey binds to the window, so the binding has to be redone, not just the config read.
    /// </summary>
    public void RegisterKeys(string toggle, string quit)
    {
        // Nothing to do when the pair has not changed: this is called from Adopt, which runs on
        // every edit, including every step of a slider drag.
        if (toggle == _boundToggle && quit == _boundQuit) return;
        _boundToggle = toggle;
        _boundQuit = quit;

        if (_toggleId != 0) { UnregisterHotKey(Handle, _toggleId); _toggleId = 0; }
        if (_quitId != 0) { UnregisterHotKey(Handle, _quitId); _quitId = 0; }
        if (ParseHotkey(toggle, out var m1, out var v1)) _toggleId = RegisterHotKey(Handle, 1, m1, v1) ? 1 : 0;
        if (ParseHotkey(quit, out var m2, out var v2)) _quitId = RegisterHotKey(Handle, 2, m2, v2) ? 2 : 0;
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_toggleId != 0) UnregisterHotKey(Handle, _toggleId);
        if (_quitId != 0) UnregisterHotKey(Handle, _quitId);
        // Cleared so a recreated handle re-binds them. Without this the "unchanged pair" guard above
        // would see the same strings, return early, and leave the new handle with no hotkeys.
        _boundToggle = _boundQuit = null;
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        // Clicks belong to the game under us, never to the overlay.
        if (m.Msg == WM_NCHITTEST) { m.Result = new IntPtr(HTTRANSPARENT); return; }
        if (m.Msg == WM_HOTKEY)
        {
            if (m.WParam.ToInt32() == _toggleId) Visible = !Visible;
            else if (m.WParam.ToInt32() == _quitId) { Quitting?.Invoke(); Close(); }
        }
        base.WndProc(ref m);
    }

    static bool ParseHotkey(string spec, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        for (int i = 0; i < parts.Length; i++)
        {
            if (i < parts.Length - 1)
            {
                if (Mods.TryGetValue(parts[i], out var mm)) mods |= mm;
            }
            else
            {
                short scan = VkKeyScanEx(parts[i][0], GetKeyboardLayout(GetCurrentThreadId()));
                if (scan == -1) return false;
                vk = (uint)(scan & 0xFF);
            }
        }
        return vk != 0 && mods != 0;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Tick();
    }

    void Tick()
    {
        ReloadConfig();

        // A pinned process wins over whatever is focused: that is what keeps the overlay on the
        // game, and on the game's monitor, while you alt-tab to a browser on the other screen.
        int pid = 0;
        if (_cfg.TargetProcess.Length > 0) pid = FindPid(_cfg.TargetProcess);
        else if (_cfg.TrackForeground)
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint fg);
            pid = (int)fg;
        }
        if (pid != 0 && pid != _cachedPid)
        {
            _cachedPid = pid;
            _cachedNamePid = -1;
            _frames.SetTarget(_cachedPid);
        }

        _frames.MaybeUpgrade();

        var r = _sensors.Read();
        if (_cachedNamePid != _cachedPid)
        {
            _cachedNamePid = _cachedPid;
            _facts = new Facts();
            try
            {
                using var p = Process.GetProcessById(_cachedPid);
                _cachedName = p.ProcessName;
                _gameHwnd = p.MainWindowHandle;
                _facts = Facts.Of(p);
            }
            catch { _cachedName = ""; _gameHwnd = IntPtr.Zero; }
        }
        else if (_gameHwnd != IntPtr.Zero && !IsWindow(_gameHwnd))
        {
            _gameHwnd = IntPtr.Zero;   // game recreated its window (fullscreen switch): re-resolve
        }
        if (_gameHwnd != IntPtr.Zero)
        {
            var sb = new System.Text.StringBuilder(128);
            GetWindowText(_gameHwnd, sb, sb.Capacity);
            _cachedTitle = sb.ToString();
        }
        if (_gameHwnd == IntPtr.Zero && _cachedPid != 0)
        {
            try { using var p = Process.GetProcessById(_cachedPid); _gameHwnd = p.MainWindowHandle; } catch { }
        }

        r.GameName = ShortTitle(_cachedTitle, _cachedName);
        r.AppName = _cachedName;
        r.Exe = _facts.Exe;
        r.Api = _facts.Api;
        r.Arch = _facts.Arch;
        if (_gameHwnd != IntPtr.Zero && GetClientRect(_gameHwnd, out var cr))
        {
            r.Res = $"{cr.R - cr.L}x{cr.B - cr.T}";
            var panel = Displays.PanelSize(Screen.FromHandle(_gameHwnd).DeviceName);
            r.ResNative = panel.Width > 0 ? $"{panel.Width}x{panel.Height}" : "";
            if (panel.Width > 0 && int.TryParse(r.Res.Split('x')[0], out var w) && w > 0)
                r.ResScale = $"{panel.Width * 100 / w}%";
        }
        r.Display = Displays.Describe(_gameHwnd, full: false);
        r.DisplayFull = Displays.Describe(_gameHwnd, full: true);
        r.Driver = Displays.DriverVersion(Screen.FromHandle(_gameHwnd).DeviceName);
        r.NetUp = _net.Up; r.NetDown = _net.Down;
        r.DiskRead = _net.Read; r.DiskWrite = _net.Write; r.PingMs = _net.PingMs;
        r.Ping = r.PingMs is null ? "--" : $"{r.PingMs:0}ms";

        LastReading = r;
        LastFrames = _frames.Snapshot(LowWindowSec);

        // Tray tooltip mirrors the live numbers. Assigned only when the text changes, so the shell is
        // not told about the same icon text 10 times a second.
        var tip = TraySummary(r, LastFrames);
        if (Program.Tray is { } tray && tray.Text != tip) tray.Text = tip;

        if (Visible) Blit();
    }

    /// <summary>
    /// One-line tray tooltip: fps, then CPU and GPU load and temperature. A missing or NaN reading is
    /// left out rather than printed. Zero fps means no frames yet, so it is treated as missing too.
    /// Truncated to 63 characters because NotifyIcon.Text throws on anything longer.
    /// </summary>
    static string TraySummary(Reading r, FrameStats f)
    {
        var parts = new List<string>();
        if (f.Fps > 0) parts.Add($"{f.Fps:0} fps");
        if (Pair("CPU", r.CpuLoad, r.CpuTemp) is { } cpu) parts.Add(cpu);
        if (Pair("GPU", r.GpuLoad, r.GpuTemp) is { } gpu) parts.Add(gpu);

        var text = parts.Count == 0 ? "FrameGlass" : "FrameGlass — " + string.Join(" · ", parts);
        return text.Length > 63 ? text[..63] : text;
    }

    /// <summary>"CPU 45% 62°C" with whichever of load and temperature are present, or null if neither.</summary>
    static string? Pair(string label, float? load, float? temp)
    {
        var bits = new List<string>();
        if (load is { } l && float.IsFinite(l)) bits.Add($"{l:0}%");
        if (temp is { } t && float.IsFinite(t)) bits.Add($"{t:0}°C");
        return bits.Count == 0 ? null : $"{label} {string.Join(" ", bits)}";
    }

    /// <summary>Re-reads config.json if it changed on disk (the settings window saves it).</summary>
    public void ReloadConfig()
    {
        var stamp = File.GetLastWriteTimeUtc(Config.ConfigPath);
        if (stamp == _cfgStamp) return;
        _cfgStamp = stamp;
        try
        {
            var c = Config.Load();
            ApplyFont(c.Font, c.FontSize);
            _timer.Interval = Math.Max(100, c.IntervalMs);
            _cfg = c;
        }
        catch { /* keep last good config */ }
    }

    /// <summary>
    /// Swaps the render font, building the replacement before releasing the old one.
    ///
    /// The order matters. Disposing first and then constructing meant a font that could not be
    /// built - a hand-edited "FontSize": 0, or a family that is not installed - left the field
    /// pointing at a disposed Font, and every later Compose threw. The overlay simply stopped
    /// painting, with nothing but a line in the log to say why.
    /// </summary>
    void ApplyFont(string name, double size)
    {
        if (name == _fontName && Math.Abs(size - _fontSize) < 0.01) return;
        Font next;
        try { next = new Font(name, (float)Math.Clamp(size, 6, 72), GraphicsUnit.Point); }
        catch (Exception e) { Program.Log($"font '{name}' {size:0.#}pt unusable: {e.Message}"); return; }

        _font.Dispose();
        _font = next;
        _fontName = name;
        _fontSize = size;
    }

    public void Adopt(Config cfg)
    {
        _cfg = cfg;
        ApplyFont(cfg.Font, cfg.FontSize);
        _timer.Interval = Math.Max(100, cfg.IntervalMs);

        // Re-bind the hotkeys on every change, so the settings window's "it registers as you type" note
        // holds. RegisterHotKey binds to a window handle, so the binding must be redone when the config
        // changes, not only when the handle is created. RegisterKeys returns immediately when the pair
        // is unchanged, so the cost of calling it on every edit is a string comparison.
        RegisterKeys(cfg.ToggleKey, cfg.QuitKey);

        _cfgStamp = File.GetLastWriteTimeUtc(Config.ConfigPath);
    }

    /// <summary>
    /// The window title is the human name ("Townfall"); the process name is not ("Townfall-Win64-Shipping").
    /// Falls back to a tidied process name when there is no usable title.
    /// </summary>
    static string ShortTitle(string title, string process)
    {
        var t = title.Trim();
        if (t.Length is > 0 and <= 40 && !t.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return t;

        foreach (var cut in new[] { "-Win64-Shipping", "-Win64-Shipping.exe", "-Shipping", "-Win64", "-DX11", "-DX12" })
            if (process.Contains(cut, StringComparison.OrdinalIgnoreCase))
                return process[..process.IndexOf(cut, StringComparison.OrdinalIgnoreCase)];
        return process;
    }

    /// <summary>
    /// Pinned process, resolved once and kept until it dies. Scanning every process each tick
    /// would cost more than the rest of the poll put together.
    /// </summary>
    int FindPid(string name)
    {
        if (_pinName == name && _pinPid != 0)
        {
            try { using var p = Process.GetProcessById(_pinPid); return _pinPid; }
            catch { _pinPid = 0; }   // gone: fall through and look again
        }
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                _pinName = name;
                return _pinPid = p.Id;
            }
            catch { /* exited or access denied */ }
        }
        _pinName = name;
        return _pinPid = 0;
    }

    /// <summary>
    /// The monitor the overlay is pinned to: the game when we have a window for it, otherwise
    /// whatever is in front. Placement AND the layout logging have to resolve it the same way, or
    /// the overlay gets positioned against one monitor and measured against another.
    /// </summary>
    Screen AnchorScreen() =>
        Screen.FromHandle(_gameHwnd != IntPtr.Zero ? _gameHwnd : GetForegroundWindow());

    /// <summary>
    /// X/Y are percentages of the working area of the monitor the *game* is on, not the one the
    /// pointer happens to be on: the overlay has to stay pinned to the game while you alt-tab.
    /// X runs left to right, Y bottom to top, and the result is always fully on screen.
    /// The maths itself lives in Composer.Anchor so the settings window and the overlay cannot
    /// disagree, and so it can be tested without a screen.
    /// </summary>
    (int x, int y) Place(int w, int h) =>
        Composer.Anchor(AnchorScreen().WorkingArea, _cfg.X, _cfg.Y, w, h);

    /// <summary>
    /// Repaints the overlay in place. UpdateLayeredWindow is deliberately not used: it fails
    /// against a fullscreen flip-model surface (a 4K borderless DX12 game), and the window
    /// silently never appears. A normal GDI paint plus SetLayeredWindowAttributes draws there.
    /// </summary>
    void Blit()
    {
        var screen = AnchorScreen();
        if (_lastScreen != screen.DeviceName || _lastAnchor != _gameHwnd)
        {
            _lastScreen = screen.DeviceName;
            _lastAnchor = _gameHwnd;
            // The display cache is per device and holds the mode, which a resolution change makes
            // stale. Invalidate exists for exactly this, so call it here, or the overlay keeps
            // reporting the mode it saw at startup.
            Displays.Invalidate();
            Program.Log($"layout anchor hwnd=0x{_gameHwnd.ToInt64():X} monitor={screen.DeviceName} "
                + $"{screen.Bounds.Width}x{screen.Bounds.Height} work={screen.WorkingArea.Width}");
        }
        Composer._anchor = _gameHwnd;   // the one-line layout measures itself against this monitor
        _bmp?.Dispose();
        _bmp = Composer.Compose(_cfg, _font, LastFrames, LastReading, opaque: true);

        // Re-assert HWND_TOPMOST on every tick. A fullscreen game window is itself topmost, and
        // among two topmost windows the active one wins, so the game covers us the moment it takes
        // focus and we only reappear on alt-tab. SetWindowPos puts us back on top without
        // activating, which is what SWP_NOACTIVATE is for.
        var at = Place(_bmp.Width, _bmp.Height);
        SetWindowPos(Handle, HWND_TOPMOST, at.x, at.y, _bmp.Width, _bmp.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);

        // A layered window only composites GDI painting once the colour-key attributes are set,
        // and a failed call leaves it invisible with no error anywhere. Push them every tick:
        // the call is cheap and it self-heals a lost window state.
        var a = (byte)Math.Clamp(255 * _cfg.Opacity, 0, 255);
        if (!SetLayeredWindowAttributes(Handle, KeyRgb, a, LWA_COLORKEY | LWA_ALPHA) && !_warnedLayer)
        {
            _warnedLayer = true;
            Program.Log($"SetLayeredWindowAttributes failed: {Marshal.GetLastWin32Error()}");
        }
        Invalidate();
    }

    bool _warnedLayer;

    /// <summary>
    /// The window the 1%-low tokens are measured over. Fixed, not a setting: those rows are no
    /// longer offered, and a stale config still using {fps1low} should not change meaning.
    /// </summary>
    const int LowWindowSec = 10;

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_bmp is null) return;
        e.Graphics.DrawImageUnscaled(_bmp, 0, 0);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _font.Dispose();
            _bmp?.Dispose();
        }
        base.Dispose(disposing);
    }
}
