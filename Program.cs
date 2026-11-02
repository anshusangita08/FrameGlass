using System.Reflection;
using System.Security.Principal;

namespace FrameGlass;

static class Program
{
    const string WakeName = @"Local\FrameGlassWake";

    public static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "frameglass.log");

    static Config _cfg = new();
    static OverlayForm? _overlay;
    static NotifyIcon? _tray;

    /// <summary>The tray icon, exposed so the overlay can set its live tooltip. Null before startup builds it.</summary>
    public static NotifyIcon? Tray => _tray;
    static SettingsForm? _settings;
    static bool _exiting;

    /// <summary>
    /// Author and contact, as shown in the About tab. The repository URL is the contact route: an
    /// email address in a public repository is scraped and cannot be retracted, and issues on the
    /// repository are a better channel for a bug report anyway.
    /// </summary>
    public const string Author = "anshusangita08";
    public const string AuthorUrl = "https://github.com/anshusangita08/FrameGlass";

    /// <summary>
    /// Version and build date for the About tab, read from the assembly rather than written twice.
    /// The version comes from the csproj; the date is the executable's own timestamp, so it shows the
    /// build that is running rather than the day the source was edited.
    /// </summary>
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"Version {v.Major}.{v.Minor}.{v.Build}"
            : "Version unknown";

    public static string BuildDate
    {
        get
        {
            try
            {
                var built = File.GetLastWriteTime(Environment.ProcessPath ?? "");
                return built.Year > 2000 ? built.ToString("d MMMM yyyy") : "date unknown";
            }
            catch { return "date unknown"; }
        }
    }

    public static void Log(string text)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {text}{Environment.NewLine}"); }
        catch { /* logging must never throw */ }
    }

    [STAThread]
    static void Main(string[] args)
    {
        // The move-button index maths is the one piece of logic a screenshot cannot check.
        // Console output would be lost (this is a windowed exe), so the result goes to the log.
        if (args.Contains("--selftest")) { Environment.ExitCode = Composer.SelfTest(); return; }

        Application.ThreadException += (_, e) => Log("UI: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("FATAL: " + e.ExceptionObject);
        Log("start");
        Thread.CurrentThread.Name = "FrameGlass.UI";

        using var single = new Mutex(true, @"Local\FrameGlass", out bool first);
        if (!first)
        {
            WakeExisting();      // second launch just opens the settings of the running one
            return;
        }

        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show("Run as administrator.\n\nCPU and GPU temperatures need the ring-0 sensor "
                + "driver, and frame timing needs an ETW session.", "FrameGlass",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // must happen before any window exists, otherwise WinForms guesses the DPI wrong
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4));   // PER_MONITOR_AWARE_V2
        ApplicationConfiguration.Initialize();

        // Ask the whole process for dark system rendering, before the first window exists.
        //
        // This is what draws the parts of this window that owner-drawing cannot reach. ListView and
        // DataGridView paint their column headers themselves, and both draw their own scrollbars. SetColorMode switches the
        // common controls to their dark rendering in one call, which is what Explorer and Office do.
        // Must be before any window exists, or it has nothing to apply to.
        try { Application.SetColorMode(SystemColorMode.Dark); }
        catch (Exception e) { Log("SetColorMode unavailable: " + e.Message); }

        _cfg = Config.Load();
        using var sensors = new Sensors(_cfg.GpuMatch);
        using var frames = new FrameTimes();
        using var net = new Net(_cfg.PingAddr);
        frames.Start();
        Log($"sensors ok={sensors.Read().Ok} etwFailed={frames.Failed}");

        _overlay = new OverlayForm(_cfg, sensors, frames, net);
        _overlay.Quitting += Application.Exit;
        // The menu label follows the real visibility, so a hotkey toggle or a hide done elsewhere
        // also updates it. Subscribed before the tray exists; SyncToggleLabel tolerates that.
        _overlay.VisibleChanged += (_, _) => SyncToggleLabel();
        Log("startup: overlay form built");

        _tray = new NotifyIcon
        {
            // The tray wants the small-icon size for this DPI, not a fixed 16 or 32.
            Icon = LoadIcon(SystemInformation.SmallIconSize.Width),
            Text = "FrameGlass",
            ContextMenuStrip = TrayMenu(),
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ToggleOverlay();
        Log("startup: tray up");

        using var wake = new EventWaitHandle(false, EventResetMode.AutoReset, WakeName);
        using var poller = new System.Windows.Forms.Timer { Interval = 400 };
        poller.Tick += (_, _) => { if (wake.WaitOne(0)) OpenSettings(); };
        poller.Start();

        _overlay.Show();
        if (_cfg.StartMinimized) _overlay.Hide();
        Log("startup: overlay shown");

        // open settings once the message loop is actually running
        var opener = new System.Windows.Forms.Timer { Interval = 1 };
        opener.Tick += (_, _) =>
        {
            opener.Stop();
            Log("startup: opening settings");
            if (!_cfg.StartMinimized) OpenSettings();
            Log(_cfg.StartMinimized ? "startup: staying in the tray" : "startup: settings opened");
        };
        opener.Start();

        Application.Run(new ApplicationContext());
        Log("startup: message loop ended");
        opener.Dispose();
        poller.Dispose();
    }

    static void WakeExisting()
    {
        try { using var e = EventWaitHandle.OpenExisting(WakeName); e.Set(); } catch { /* nothing running */ }
    }

    static ContextMenuStrip TrayMenu()
    {
        // Dark palette for the menu, not the system menu colours. See Theme.TrayMenuRenderer.
        var menu = new ContextMenuStrip
        {
            Renderer = new Theme.TrayMenuRenderer(),
            BackColor = Theme.Canvas,
            ForeColor = Theme.Ink,
        };
        // Start with the EyeOff icon to match the "Hide overlay" label. The first VisibleChanged can fire
        // before the tray exists, so SyncToggleLabel may not run yet. It disposes this bitmap when it replaces it.
        var toggle = new ToolStripMenuItem("Hide overlay", MenuIcon(Icons.EyeOff), (_, _) => ToggleOverlay())
        {
            Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
        };
        menu.Items.Add(toggle);
        menu.Items.Add(new ToolStripMenuItem("Settings...", MenuIcon(Icons.Settings), (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", MenuIcon(Icons.Dismiss), (_, _) => Exit()));
        return menu;
    }

    /// <summary>
    /// A tray menu glyph at the small-icon height, in the theme's primary text colour. Returns null when
    /// the icon font is unavailable, so the menu item has no image and still works. The caller owns the
    /// bitmap and must dispose it once it is replaced.
    /// </summary>
    static Bitmap? MenuIcon(char glyph) =>
        Icons.Render(glyph, SystemInformation.SmallIconSize.Height, Theme.Ink);

    static void ToggleOverlay()
    {
        if (_overlay is null) return;
        _overlay.Visible = !_overlay.Visible;   // VisibleChanged updates the menu label
    }

    /// <summary>
    /// Sets the first menu item (the Hide/Show toggle) from the overlay's actual visibility. Driven by
    /// VisibleChanged rather than by the menu click, so every way of hiding or showing it stays in sync.
    /// The icon follows the same rule: it shows what a click will do, so the label and icon always agree.
    /// </summary>
    static void SyncToggleLabel()
    {
        if (_overlay is null || _tray?.ContextMenuStrip is not { } menu) return;
        var item = menu.Items[0];
        var previous = item.Image;

        // While visible the label is "Hide overlay", so the click hides it: EyeOff. While hidden the label
        // is "Show overlay", so the click shows it: Eye. The icon is the action, matching the label.
        item.Text = _overlay.Visible ? "Hide overlay" : "Show overlay";
        item.Image = MenuIcon(_overlay.Visible ? Icons.EyeOff : Icons.Eye);

        // Dispose the replaced bitmap only after the new one is assigned, so the item never points at a disposed image.
        previous?.Dispose();
    }

    static void OpenSettings()
    {
        if (_settings is { IsDisposed: false })
        {
            _settings.Activate();
            return;
        }

        _settings = new SettingsForm(_cfg, () => _overlay!.LastFrames, () => _overlay!.LastReading, Apply);
        _settings.FormClosed += (_, _) =>
        {
            _settings = null;
            if (!_cfg.MinimizeToTray && !_exiting) Exit();
        };
        _settings.Show();
        _settings.Activate();
    }

    static void Apply(Config c)
    {
        _cfg.CopyFrom(c);
        _overlay?.Adopt(c);
    }

    static void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        _tray?.Visible = false;
        _tray?.Dispose();
        _overlay?.Close();
        Application.Exit();
    }

    /// <summary>
    /// The one icon, loaded from the resource embedded in this assembly.
    ///
    /// Everything that shows an icon goes through here - the tray, the overlay window, the settings
    /// window and the help window - so they are the same image by construction rather than by four
    /// copies agreeing. The taskbar and the Start menu take theirs from the executable itself, which
    /// is the same file. There is no second, hand-drawn fallback any more: it existed for a missing
    /// app.ico, and the resource cannot be missing.
    /// </summary>
    public static Icon LoadIcon(int size)
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("FrameGlass.app.ico");

        // Ask the icon for the size the caller needs. Passing a size picks the closest frame rather
        // than letting Windows scale the 256 down, which is what makes 16px look soft.
        if (stream is not null)
        {
            using var all = new Icon(stream);
            return new Icon(all, new Size(size, size));
        }

        // Only reachable if the resource was stripped from the build.
        return SystemIcons.Application;
    }
}






internal static class Native
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
