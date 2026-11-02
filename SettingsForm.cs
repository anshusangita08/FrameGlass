using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace FrameGlass;

/// <summary>
/// Normal window (taskbar button, minimizable) that edits config.json and applies every change to
/// the overlay as it is made. The overlay on screen is the preview.
/// </summary>
sealed class SettingsForm : Form
{
    const string FollowFocused = "(follow focused window)";
    /// <summary>Sampling interval in milliseconds. A fixed short list: finer starts costing frames.</summary>
    static readonly int[] SampleChoices = [500, 1000, 1500, 2000];
    /// <summary>
    /// The four sampling labels, as "0.5 s" / "1 s" / "1.5 s" / "2 s". A closed list on purpose:
    /// Intervals below 250 ms visibly cost frames, so they are not offered.
    /// </summary>
    static readonly string[] SampleLabels = SampleChoices.Select(c => $"{c / 1000d:0.##} s").ToArray();

    /// <summary>Turns a label back into milliseconds. The inverse of <see cref="SampleLabels"/>.</summary>
    static int SampleMs(string label) => (int)(SampleChoices[Math.Max(0, Array.IndexOf(SampleLabels, label))]);

    /// <summary>
    /// An interval not in the offered list snaps to the nearest offered value rather than silently resetting to the
    /// first one, so 250 ms becomes "0.5 s" and 500 ms stays "0.5 s" - the label the user sees is
    /// always the one that will actually be used.
    /// </summary>
    static string SampleLabel(int ms) =>
        SampleLabels[Array.IndexOf(SampleChoices, SampleChoices.OrderBy(c => Math.Abs(c - Math.Clamp(ms, 250, 5000))).First())];

    readonly Config _draft;
    readonly Action<Config> _apply;


    readonly ToolTip _tip = new() { InitialDelay = 350, ReshowDelay = 120 };
    readonly Theme.ToggleSwitch _minToTray, _startHidden, _panelBox;
    FlowLayoutPanel _groupBox = null!;   // set in RowsTab; Apply re-reads the ticks out of it
    Label AboutKeys = null!;            // set in About; Apply rewrites the key hint
    readonly RadioButton _stack, _line;
    readonly ComboBox _fontName, _target, _interval, _hotPreset;
    readonly NumericUpDown _gap, _cols, _hotTemp, _hotLoad, _graphW, _graphH;
    readonly Theme.Slider _x, _y, _opacity, _strength;
    // Font size is a slider, not a spinner: it is a feel-good-it adjustment, and clicking a spinner
    // ten times to go 12 -> 14 is the wrong control for it.
    readonly Theme.Slider _fontSize;
    readonly Label _xVal, _yVal, _opacityVal, _strengthVal, _fontSizeVal;
    readonly TextBox _pingAddr;
    // Row details page: the two name boxes, and the Metrics grid that RebuildDetailRows refills.
    readonly TextBox _cpuName, _gpuName;
    TableLayoutPanel _detailGrid = null!;
    // Page index of Row details, so ShowPage knows when to refill its rows. -1 until the page exists.
    int _detailIndex = -1;
    readonly HotkeyBox _toggleKey, _quitKey;
    // Both are built by RowsTab()/BuildRowTable() from the ctor, so they cannot be readonly: the
    // compiler would otherwise treat every read before the build as a null dereference.
    ListView _lines = null!;        // built in BuildRowTable
    Panel _table = null!;           // the bordered card the table sits in
    // Replaced by RebuildMarks() on every DPI change, because ImageSize (S(16)) depends on _k.
    ImageList _marks = new() { ColorDepth = ColorDepth.Depth32Bit };
    readonly Dictionary<string, Button> _designSwatches = new();
    readonly Button _swatchHot;
    Color _bg, _text, _accent, _hot;

    readonly System.Windows.Forms.Timer _live;
    /// <summary>True while the metrics list is being torn down and refilled. See RebuildRowList.</summary>
    bool _rebuilding;
    float _k = 1f;   // not readonly: DpiChanged re-reads it for the monitor we landed on

    // Navigation: a sidebar switches pages. Each page is a Fill panel inside _content,
    // and ShowPage shows exactly one of them. The sidebar's SelectedIndex is the page index.
    readonly Theme.NavSidebar _nav;
    readonly Panel _content;
    readonly List<Panel> _pages = new();

    // Row ids in list order, and section names in list order. The visible list is these two
    // interleaved: a header, then the rows that follow it belong to that section.
    List<string> _ids = new();
    List<string> _sections = new();

    /// <summary>
    /// The frames and reading callbacks are kept in the signature even though nothing here reads
    /// them any more. They fed the preview panel, which is gone; they are part of how Program builds
    /// this form and of how the probe harnesses drive it, and removing them would be churn across
    /// five call sites to delete an argument rather than to change anything.
    /// </summary>
    public SettingsForm(Config cfg, Func<FrameStats> frames, Func<Reading> reading, Action<Config> apply)
    {
        _ = frames; _ = reading;
        // Capped at 300%: higher factors would leave the text too wide for its rows.
        _k = Math.Max(0.75f, Math.Min(DeviceDpi / 96f, MaxScale));
        _draft = cfg;
        _apply = apply;

        Text = "FrameGlass Settings";
        // Set explicitly rather than inherited. A Form falls back to the application icon, which
        // would be the same image, but this way the window, the tray and the taskbar all name the
        // same source instead of three implied defaults agreeing by luck.
        Icon = Program.LoadIcon(32);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimumSize = MinimumWindowSize();
        BackColor = Theme.Canvas;
        ForeColor = Theme.Ink;
        Font = new Font(Theme.UiFont, Theme.TypeBody);

        // No preview panel: the real overlay is already on the real screen and updates live as you
        // edit, so a preview would only be a smaller copy of it at a scale where the text is hard to
        // read. The space goes to the tabs instead.

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = (int)(44 * _k),
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding((int)(8 * _k)),
        };
        var help = new Button { Text = "What do these mean?", AutoSize = true };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK };
        var reset = new Button { Text = "Reset everything", AutoSize = true };
        buttons.Controls.Add(close);
        buttons.Controls.Add(reset);
        buttons.Controls.Add(help);
        // ShowDialog does not dispose its dialog: a `new HelpWindow()` left to the GC leaks the whole
        // form and every GDI handle it owns, once per click. Both help buttons leaked this way.
        help.Click += (_, _) => { using var w = new HelpWindow(_k); w.ShowDialog(this); };

        // Left navigation: one sidebar row per page, in page order (0 Overlay, 1 Rows, 2 General, 3 About).
        // The width is 200 logical px, scaled by _k; DpiChanged re-applies it. Pages are added below.
        _nav = new Theme.NavSidebar { Dock = DockStyle.Left, Width = S(200), DpiScale = _k, TabIndex = 0 };
        _nav.SelectedIndexChanged += (_, _) => ShowPage(_nav.SelectedIndex);
        _content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas };

        // ============================ 1. Layout ==================================================
        // Layout and Design share one page ("Overlay") and one grid. Design's rows continue below
        // these, so the section headings are the only separation between them.
        _stack = new RadioButton { Text = "One row per metric", AutoSize = true };
        _line = new RadioButton { Text = "Everything on one line", AutoSize = true };
        _stack.Checked = !_draft.Layout.Equals("line", StringComparison.OrdinalIgnoreCase);
        _line.Checked = !_stack.Checked;
        _cols = Num(0, 8, _draft.Columns);
        _cols.Enabled = _line.Checked;
        _line.CheckedChanged += (_, _) => _cols.Enabled = _line.Checked;
        _panelBox = new Theme.ToggleSwitch { Text = "Draw the panel behind the text", Checked = _draft.Box, AutoSize = true };

        _x = new Theme.Slider { Minimum = 0, Maximum = 100, Value = (int)Clamp(_draft.X, 0, 100), Width = S(150) };
        _y = new Theme.Slider { Minimum = 0, Maximum = 100, Value = (int)Clamp(_draft.Y, 0, 100), Width = S(150) };
        _xVal = Val(_draft.X);
        _yVal = Val(_draft.Y);
        // Wired here rather than next to the Picker, because it is the first point where _x and _y
        // actually exist. A lambda cannot be proved to run after a later assignment, so capturing
        // them earlier could read a null field.
        Watch(_x, () => { _xVal.Text = Pct(_x.Value); Live(); });
        Watch(_y, () => { _yVal.Text = Pct(_y.Value); Live(); });

        _gap = Num(0, 40, _draft.Gap);

        // Overlay is one page with two cards, Layout and Design. Both grids are created here so the
        // page exists in sidebar order before its rows are added. Rows are added further down; a grid is a
        // live object, so rows added later appear inside the card.
        var layout = Grid();
        var design = Grid();
        AddPage("Overlay", Icons.NavOverlay, CardStack("Overlay", ("Layout", layout), ("Design", design)));

        // Horizontal presets. The percentages alone make "centred" a guess at a slider position;
        // these snap X to the three positions people actually pick. They go through the slider's
        // Value setter, so ValueChanged updates the readout and the live overlay exactly as a drag
        // does. They sit on the X track only: Y has no presets, so the row stays X-then-Y.
        var presets = new[]
        {
            PresetButton("Left", 0),
            PresetButton("Center", 50),
            PresetButton("Right", 100),
        };
        Add(layout, "Position", Stacked(Track(_x, _xVal, presets), Track(_y, _yVal)),
            "Where the overlay sits, as a percentage of the pinned window's monitor. X runs left to "
            + "right, Y runs bottom to top, so (0,0) is the bottom-left corner. The overlay itself "
            + "moves as you drag, and it is held on screen at every value.");
        // Sorted by what each row controls: where the overlay sits, how its rows stack, then the gap
        // between them. Graph size is added to this card in the Design section, where its controls exist.
        Add(layout, "Shape", Flow(_stack, _line), "One row per metric, or everything on a single line.");
        Add(layout, "Columns", Flow(_cols), "In one-line mode: 0 keeps everything on a single long line, "
            + "2 or more lays the rows out as a grid of that many columns.");
        Add(layout, "Spacing", Flow(_gap), "Blank pixels between columns and rows.");

        // ============================ 2. Rows =====================================================
        BuildRowTable();
        // Clicking the row picks it for editing; the tick lives in the right-hand column, and a
        // double click toggles from anywhere.
        _lines.MouseUp += (_, e) =>
        {
            if (e.Clicks < 2 && ColumnAt(e.X) != _lines.Columns.Count - 1) return;
            if (_lines.HitTest(e.Location).Item is { } hit) Toggle(hit);
        };

        // The page scrolls: its body is a fixed-height stack, not Fill.
        AddPage("Rows", Icons.NavRows, RowsTab());

        // ============================ 2b. Row details ============================================
        // The page after Rows: the CPU and GPU names, then the text each ticked row shows. The name boxes
        // are built here, before the change-wiring loop below, so they get Changed like every other field.
        // The Metrics grid is empty until the page is first shown; RebuildDetailRows fills it.
        _cpuName = NameBox(_draft.CpuNameOverride, "CPU name");
        _gpuName = NameBox(_draft.GpuNameOverride, "GPU name");
        var names = Grid();
        Add(names, "CPU name", _cpuName, "The CPU model the overlay shows for {cpuname}, {cpus} and {cpufull}.");
        Add(names, "GPU name", _gpuName, "The GPU model the overlay shows for {gpu} and {gpufull}.");
        Add(names, "", Note("Empty uses the detected name."));
        _detailGrid = Grid();
        AddPage("Row details", Icons.NavRows, CardStack("Row details", ("Names", names), ("Metrics", _detailGrid)));
        _detailIndex = _pages.Count - 1;

        // ============================ 3. Design ===================================================
        // Editable (DropDown): the font can be typed, including a family that is not in the list or a
        // saved value that is not installed. The field's selection highlight is cleared by ThemedCombo.
        _fontName = new Theme.ThemedCombo { DropDownStyle = ComboBoxStyle.DropDown, Width = (int)(190 * _k) };
        _fontName.Items.AddRange(FontFamily.Families.Select(f => f.Name).Cast<object>().ToArray());
        _fontName.Text = _draft.Font;
        // Size is a slider, not a box: it is a continuous feel-good-it adjustment, and a spinner
        // makes you click ten times to get from 12 to 14.
        _fontSize = new Theme.Slider { Minimum = 6, Maximum = 36, Value = (int)Clamp(_draft.FontSize, 6, 36), Width = (int)(150 * _k) };
        _fontSizeVal = Val(_draft.FontSize, "pt");
        Watch(_fontSize, () => { _fontSizeVal.Text = $"{_fontSize.Value} pt"; Live(); });
        // Graph size lives on the Design tab, next to the graph colour, so it is built here
        // rather than down with the Data tab's other counters.
        _graphW = Num(0, 800, _draft.GraphWidth);
        _graphH = Num(12, 400, _draft.GraphHeight);

        // The two fades live side by side in one row: they are the same control twice, and putting
        // them on separate rows made it look like two different things.
        _strength = Slider((int)Math.Round(_draft.PanelOpacity * 100), out _strengthVal, "bg");
        _opacity = Slider((int)Clamp(_draft.Opacity * 100, 0, 100), out _opacityVal, "text");
        // Background fade only means anything with a background to fade, so it is dead while the
        // box is off. Text fade is always live.
        _strength.Enabled = _panelBox.Checked;
        _panelBox.CheckedChanged += (_, _) => { _strength.Enabled = _panelBox.Checked; Live(); };

        // Hot temperature and hot load trigger the same colour, so they are one row of two.
        _hotTemp = Num(0, 120, (int)Clamp(_draft.HotTempC, 0, 120));
        _hotLoad = Num(0, 100, (int)Clamp(_draft.HotLoadPct, 0, 100));
        _bg = Composer.Parse(_draft.BgColor, Color.FromArgb(14, 16, 19));
        _text = Composer.Parse(_draft.TextColor, Color.FromArgb(236, 240, 245));
        _accent = Composer.Parse(_draft.AccentColor, Color.FromArgb(118, 196, 255));
        _hot = Composer.Parse(_draft.ResolveHotColor(), Color.FromArgb(255, 148, 60));
        // Three presets, no picker: the hot colour only ever signals "this is running hot", and a
        // colour chooser for it is a way to make the warning stop being readable.
        _hotPreset = new Theme.ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList };
        var hotLabels = Config.HotPresets.Select(p => $"{p.Name}  {p.Hex}").ToArray();
        _hotPreset.Items.AddRange(hotLabels.Cast<object>().ToArray());
        // The width fits the longest entry, so "orange  #FF943C" is not cut under the arrow. Measured at 100%
        // (ui-probe GetComboBoxInfo): the text area starts 3px in and ends 20px before the right edge, where
        // the arrow sits. The text is drawn 4px further in (OnDrawItem), so the width is:
        //   text start (3px border inset + 4px draw offset) + text + slack + arrow reserve.
        // The arrow reserve is the scroll-bar width plus 3px gap. The text is measured with
        // Graphics.MeasureString, the metrics DrawString uses, because TextRenderer measures narrower and
        // clips the text. The slack covers the few px it still under-measures. 120*k is the minimum width.
        using var probe = new Bitmap(1, 1);
        using var measure = Graphics.FromImage(probe);
        // The Bitmap's Graphics is at 96 DPI, but the font is drawn at this monitor's DPI, so the width is
        // scaled to DeviceDpi. Without that, the combo is too narrow at 150%.
        int longestHot = (int)Math.Ceiling(hotLabels.Max(t => measure.MeasureString(t, _hotPreset.Font).Width)
            * DeviceDpi / 96f);
        // Text start is S(3) border + S(4) draw offset; S(6) slack; arrow reserve is the scroll-bar width + S(3).
        _hotPreset.Width = Math.Max((int)(120 * _k),
            S(3) + S(4) + longestHot + S(6) + SystemInformation.VerticalScrollBarWidth + S(3));
        _hotPreset.SelectedIndex = Math.Max(0, Array.FindIndex(Config.HotPresets, p => p.Name == _draft.HotColorName));
        _swatchHot = PeekSwatch("hot", () => _hot);
        _hotPreset.SelectedIndexChanged += (_, _) =>
        {
            if (_hotPreset.SelectedIndex >= 0 && _hotPreset.SelectedIndex < Config.HotPresets.Length)
            {
                _hot = Composer.Parse(Config.HotPresets[_hotPreset.SelectedIndex].Hex, _hot);
                SetSwatch(_swatchHot, _hot);
                Live();
            }
        };

        // Design card rows. The card heading names the group, and each row label says what its control does.
        Add(design, "Font", _fontName, "Typeface used for every row.");
        Add(design, "Text size", Track(_fontSize, _fontSizeVal), "Point size of every row. Font size is the only size "
            + "control: the overlay is drawn at its natural size and the font is what you change.");
        Add(design, "Text colour", Swatch("text", () => _text, c => _text = c), "Default colour of the numbers.");
        Add(design, "Draw it", _panelBox, "Off: the game shows through and only the text is drawn, which is "
            + "what most people want. On: a panel is drawn behind the text in the colour below.");
        Add(design, "Panel colour", Swatch("panel", () => _bg, c => _bg = c), "The panel colour. The window background is not "
            + "configurable - the overlay is composited with a colour key, so the background IS this colour.");
        Add(design, "Fades", Track2(_opacity, _opacityVal, _strength, _strengthVal),
            "Text fade and background fade, side by side. At 0 the text (or the panel) is fully "
            + "transparent; at 100 it is solid. The overlay is composited with a colour key rather "
            + "than per-pixel alpha, so these fade towards transparency rather than making it glassy.");
        Add(design, "Graph colour", Swatch("graph", () => _accent, c => _accent = c), "Frametime graph line.");
        // Graph size is a size, so it sits in the Layout card, last.
        Add(layout, "Graph size", Pair(_graphW, _graphH), "Width and height in pixels of the frametime graph.");
        Add(design, "Hot colour", Flow(_hotPreset, _swatchHot), "Green, orange or red. Whichever you pick is also "
            + "swatched beside it so you can see it against the panel.");
        Add(design, "Hot thresholds", Pair(_hotTemp, _hotLoad), "Left: a CPU or GPU temperature at or above this "
            + "turns the row hot. Right: a CPU or GPU load at or above this does the same.");

        // ============================ 4. Data =====================================================
        // Editable, as Font above. The list offers the running windows, and a typed process name or a
        // saved one that is not running is kept as typed.
        _target = new Theme.ThemedCombo { DropDownStyle = ComboBoxStyle.DropDown, Width = (int)(190 * _k) };
        _target.Items.Add(FollowFocused);
        foreach (var name in Candidates()) _target.Items.Add(name);
        _target.Text = _draft.TargetProcess.Length > 0 ? _draft.TargetProcess : FollowFocused;

        // Sampling interval in SECONDS, from a fixed short list. Milliseconds and a 100-5000 spinner
        // invited values that stall the game; half a second to two seconds covers every real use.
        _interval = Combo(SampleLabel(_draft.IntervalMs), SampleLabels);
        _interval.Width = (int)(120 * _k);
        _pingAddr = Field(_draft.PingAddr);

        // General is one page with two cards, Data and Settings. Both grids are created here so the
        // page exists in sidebar order; the Settings rows are added once their controls exist.
        var data = Grid();
        var settings = Grid();
        AddPage("General", Icons.NavGeneral, CardStack("General", ("Data", data), ("Settings", settings)));
        Add(data, "Pin to window", _target, "Which window the overlay belongs to. Pinned, it also follows that "
            + "window's monitor, so a second screen does not drag the overlay away. Follow the focused "
            + "window makes it chase whatever you alt-tab to.");
        Add(data, "Sample every", _interval, "How often the overlay re-reads the counters. Sensors cannot go "
            + "below about 250 ms without slowing the game down, so the shortest option is half a second.");
        Add(data, "Ping host", _pingAddr, "Rarely needed: a ping row. Address pinged for it, e.g. 1.1.1.1. "
            + "Empty turns it off.");

        _toggleKey = HotkeyField(_draft.ToggleKey);
        _quitKey = HotkeyField(_draft.QuitKey);
        _minToTray = new Theme.ToggleSwitch { Text = "Closing this window keeps the overlay running in the tray", Checked = _draft.MinimizeToTray, AutoSize = true };
        _startHidden = new Theme.ToggleSwitch { Text = "Start with the overlay hidden in the tray", Checked = _draft.StartMinimized, AutoSize = true };

        // The hotkey note is attached to both hotkey boxes it describes, so the help shows when the
        // pointer is on either box.
        const string hotkeyNote = "Click a box and press the combination. It registers as you type, "
            + "so you do not have to reopen the window to try one.";
        Add(settings, "Show / hide", _toggleKey, hotkeyNote + " Shows or hides the overlay.");
        Add(settings, "Quit", _quitKey, hotkeyNote + " Closes the overlay for good.");
        Add(settings, "Window", _minToTray, "Leave it off to close the overlay when you close this window.");
        Add(settings, "Start", _startHidden, "Off means this window opens every time the overlay starts.");
        AddPage("About", Icons.NavAbout, About());
        ShowPage(_nav.SelectedIndex);   // only the first page is visible until the sidebar picks another

        // Add order is the docking order, and it matters. WinForms lays out the back-most control first,
        // so the bottom bar takes the full width, the sidebar then takes the height above it, and the
        // content fills what is left. Added in any other order the sidebar runs over the bottom bar.
        Controls.Add(_content);
        Controls.Add(_nav);
        Controls.Add(buttons);

        foreach (Control c in All(_content))
        {
            switch (c)
            {
                case NumericUpDown n: n.ValueChanged += Changed; break;
                // An editable combo changes its Text when typed in, and SelectedIndexChanged does not
                // fire for typing. A selection from its list also sets Text, so TextChanged covers both.
                case ComboBox cb when cb.DropDownStyle == ComboBoxStyle.DropDown: cb.TextChanged += Changed; break;
                case ComboBox cb: cb.SelectedIndexChanged += Changed; break;
                case CheckBox cb: cb.CheckedChanged += Changed; break;
                case RadioButton rb: rb.CheckedChanged += Changed; break;
                case TextBox tb: tb.TextChanged += Changed; break;
            }
        }
        // _fontName is a ComboBox, so the loop above already wires it. Do not wire it a second time:
        // every keystroke in the font box would then save the config twice.
        _live = new System.Windows.Forms.Timer { Interval = 1000 };
        // The timer lands the config write that Live() deferred. The overlay itself is the live view,
        // driven by _apply above, so the timer repaints nothing.
        _live.Tick += (_, _) => FlushSave();
        _live.Start();

        // Closing must not lose the last edit, so the deferred write is forced here rather than
        // left to a timer that is about to be disposed.
        close.Click += (_, _) => { Live(); FlushSave(); DialogResult = DialogResult.OK; Close(); };
        reset.Click += (_, _) => { Apply(new Config()); Live(); };
        AcceptButton = close;

        Shown += (_, _) => SelectFirstRow();

        // WinForms' font autoscale gets this wrong on a 150% monitor (window came out half size),
        // so lay out at 96dpi and scale the finished tree by the real factor once. None stops
        // WinForms applying a second, wrong factor on top of that when the handle is created.
        // Dark title bar + Mica, both best-effort. Re-applied on handle creation because the
        // first call here can run before the window has a real HWND at the right DPI.
        Load += (_, _) => Theme.ApplyWindowChrome(this);
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = WindowSize();
        Program.Log($"settings dpi={DeviceDpi} k={_k:0.00} client={ClientSize} autoscale={AutoScaleMode}");
        Skin(_content);
        Skin(buttons);   // the bottom bar sits outside _content, so it needs its own pass to get Theme.Flat
        ApplyDpiScale(this);   // custom-painted controls size their pixels from _k; push it in once
        FocusRing(close);      // the one default-action control gets a visible keyboard ring
        Live();
        LayOutPages();         // the hidden pages were never laid out; see LayOutPages

        // Dragging the window to the other display changes DPI, and WinForms' answer to that is to
        // autoscale everything again - which double-scales a layout that already scaled itself.
        // Re-apply the factor for the monitor we landed on instead.
        DpiChanged += (_, _) => SyncToMonitor();
    }

    /// <summary>
    /// The constructor runs before the window has a monitor, so DeviceDpi there is the primary's and _k
    /// is wrong for a window that opens on another display (the 175% scaled-monitor overlap). The handle is what
    /// binds the window to its monitor, so the factor is re-read once it exists, and again on every
    /// WM_DPICHANGED. Both paths come through here.
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SyncToMonitor();
        PinListFont();
    }

    /// <summary>
    /// Sets the metrics ListView font to the unscaled TypeBody point size, the same rule as the labels. WinForms'
    /// own autoscale rescales the ListView font after the handle exists (9.5pt became 29pt at 175%), so this is
    /// re-run from the Shown handler as well. The font is compared on the ListView itself, because WinForms may
    /// have replaced it. The font this method created is disposed when it is replaced.
    /// </summary>
    void PinListFont()
    {
        if (_lines is null) return;
        float pt = Theme.TypeBody;
        if (Math.Abs(_lines.Font.SizeInPoints - pt) < 0.01f) return;
        // Dispose only the font this method created. _lines.Font before the first call is the shared inherited
        // default font, and disposing it breaks every control created after (ComboBox ctor threw "Parameter is not valid").
        var old = _listFont;
        _listFont = new Font(Font.FontFamily, pt);
        _lines.Font = _listFont;
        old?.Dispose();
        FitColumns();
    }

    /// <summary>The font this window created for the metrics ListView; disposed only by <see cref="PinListFont"/>.</summary>
    Font? _listFont;

    /// <summary>
    /// Re-reads the factor for the monitor this window is on and re-lays the form out when it changed.
    /// DeviceDpi is already the target monitor's by the time this runs, from either caller.
    /// </summary>
    void SyncToMonitor()
    {
        float real = Math.Max(0.75f, Math.Min(DeviceDpi / 96f, MaxScale));
        if (Math.Abs(real - _k) < 0.01f) return;
        float by = real / _k;
        Program.Log($"settings dpi={DeviceDpi} {_k:0.00} -> rescale x{by:0.00}");
        _k = real;
        // The bottom buttons are AutoSize. When the window opens on a new DPI, WinForms has already re-measured
        // them at the new font before this runs, and Scale() then multiplies that measured size again: 41px
        // became 72px at 175%. Turn AutoSize off for the scale, then measure again at the font, so open and
        // drag arrive at the same size.
        var autoButtons = BottomButtons().Where(b => b.AutoSize).ToList();
        foreach (var b in autoButtons) b.AutoSize = false;
        Scale(new SizeF(by, by));
        foreach (var b in autoButtons) b.AutoSize = true;
        // Scale() also scales margins, so the readouts are centred again on their bars' new heights.
        foreach (var (bar, readout) in _readouts) CentreReadout(bar, readout);
        // Scale() already moved the sidebar's width, but set it from k so it is exact, and push the
        // new factor into the custom-painted row metrics, which Scale() cannot reach.
        _nav.DpiScale = _k;
        _nav.Width = S(200);
        ApplyDpiScale(this);
        // The row glyphs are rendered at S(16), so they are rebuilt for the new factor.
        RebuildMarks();
        // The metrics ListView font is set in pt and is not part of Scale(), so it is re-set from k here.
        PinListFont();
        // Lay the pages out again at the new sizes (the hidden pages are laid out here too, see LayOutPages).
        LayOutPages();
        FitToMonitor();
        // WM_DPICHANGED is still being handled when DpiChanged fires. WinForms then applies the suggested
        // rectangle, which keeps the pre-change pixel size and overwrites the size set above, so the
        // size is applied again once the message has finished.
        if (IsHandleCreated) BeginInvoke(new Action(FitToMonitor));
    }

    /// <summary>Sizes the window for the current factor and the working area of the monitor it is on.</summary>
    void FitToMonitor()
    {
        // Set the minimum first. A minimum left over from a higher factor would clamp the window back up
        // to that size when the factor drops, so the window would open too wide after a drag back.
        MinimumSize = MinimumWindowSize();
        ClientSize = WindowSize();
        Program.Log($"settings sized k={_k:0.00} client={ClientSize} min={MinimumSize} area={WorkArea()}");
    }

    /// <summary>
    /// Shows one page and hides the others. The page is brought to the front as well, so it is the
    /// first control the content panel lays out.
    /// </summary>
    void ShowPage(int index)
    {
        for (int i = 0; i < _pages.Count; i++) _pages[i].Visible = i == index;
        if (index >= 0 && index < _pages.Count) _pages[index].BringToFront();
        // The Row details page mirrors the Rows ticks, so it is refilled every time it is opened.
        if (index == _detailIndex) RebuildDetailRows();
    }

    /// <summary>
    /// Lays out every page once while it is visible, then shows only the selected page again. WinForms
    /// does not lay out a hidden control, so the pages built hidden kept the first, zero-width layout pass:
    /// the table gave each combo a 1px width, and the scale step on a later DPI change multiplied that
    /// 1px into 0 (the Font, Pin and Hot combos, and the General fields, did not paint at 150%). A layout
    /// at the real width here gives every page correct sizes before any DPI change scales them.
    /// </summary>
    void LayOutPages()
    {
        foreach (var page in _pages)
        {
            page.Visible = true;
            // Deepest containers first, so each row is measured from children that already have their new size.
            foreach (var c in All(page).Reverse()) if (c.HasChildren) c.PerformLayout();
            page.PerformLayout();
        }
        ShowPage(_nav.SelectedIndex);
        RemeasureCombos();
    }

    /// <summary>
    /// Makes each table re-measure its combos. A ThemedCombo's height follows its ItemHeight, which is
    /// set from the DPI factor without a bounds-change notification, so the parent TableLayoutPanel kept
    /// the row height it measured before the change (150%: Sample combo Height 45, row 38). A one-pixel
    /// height change and back on the combo is what the parent sees, and the combo ends at its original
    /// height. Runs after the layout pass, when every combo has its real width, and only for combos that
    /// are not focused (a focused combo keeps its state untouched).
    /// </summary>
    void RemeasureCombos()
    {
        foreach (var page in _pages)
            foreach (var c in All(page))
            {
                if (c is not Theme.ThemedCombo cb || cb.Width <= 0 || cb.Focused) continue;
                if (cb.Parent is not TableLayoutPanel tlp) continue;
                int x = cb.Left, y = cb.Top, w = cb.Width, h = cb.Height;
                cb.SetBounds(x, y, w, h + 1);
                cb.SetBounds(x, y, w, h);
                tlp.PerformLayout();
            }
    }

    /// <summary>
    /// Adds one page: its sidebar row, and its body in a Fill panel that <see cref="ShowPage"/> shows
    /// or hides. Pages are added in index order, so the sidebar row and the page share an index.
    /// </summary>
    void AddPage(string title, char glyph, Control body)
    {
        _nav.AddItem(title, glyph);
        var page = Page(title, body);
        page.Visible = false;
        _pages.Add(page);
        _content.Controls.Add(page);
    }

    /// <summary>
    /// Soft flat look over the whole tree: canvases, cards, flat buttons, quiet chrome. One pass,
    /// one place, so a control cannot end up half-themed by being built in the wrong page.
    /// </summary>
    void Skin(Control root)
    {
        // Visual styles must be off for BackColor to stick on a ComboBox and NumericUpDown: with
        // them on, the OS paints a light 3D trough regardless of what we set.
        foreach (Control c in All(root))
        {
            switch (c)
            {
                case Button b:
                    if (b.Tag as string == "swatch") break;   // its colour is the whole point
                    // Buttons with a DialogResult, such as Close, get the filled treatment.
                    Theme.Flat(b, b.DialogResult != DialogResult.None);
                    break;
                case TextBox tb:
                    // Fixed3D draws a light sunken bevel that ignores BackColor. FixedSingle is
                    // the only border style that respects a dark fill.
                    tb.BorderStyle = BorderStyle.FixedSingle;
                    tb.BackColor = Theme.Input;
                    if (string.IsNullOrEmpty(tb.AccessibleName)) tb.AccessibleName = tb.PlaceholderText;
                    break;
                case ComboBox cb:
                    cb.FlatStyle = FlatStyle.Flat;
                    cb.BackColor = Theme.Input;
                    cb.ForeColor = Theme.Ink;
                    // The font and process lists run to hundreds of entries, and an unbounded
                    // dropdown opens upward until it covers the top of the window. Scroll instead.
                    cb.MaxDropDownItems = 14;
                    // The open list is left to WinForms: owner-drawing it means handling the -1
                    // "draw the closed field" message, and getting that wrong throws on every
                    // repaint. A light dropdown in a dark window is a far smaller price than a
                    // settings window that will not open.
                    break;
                case NumericUpDown nu:
                    nu.BorderStyle = BorderStyle.FixedSingle;
                    nu.BackColor = Theme.Input;
                    nu.ForeColor = Theme.Ink;
                    break;
                case ListView lv:
                    // The metrics table is the Input surface, not a card (see BuildRowTable).
                    lv.BackColor = Theme.Input;
                    lv.ForeColor = Theme.Ink;
                    break;
                case CheckBox or RadioButton or Label:
                    c.ForeColor = c.ForeColor == SystemColors.ControlText ? Theme.Ink : c.ForeColor;
                    break;
                case Panel p:
                    // A panel inside a card sits on the card's fill, so it must be Card. A Canvas panel
                    // here paints a visible patch over the card. Cards and the page stack stay Canvas.
                    p.BackColor = InCard(p) ? Theme.Card : Theme.Canvas;
                    break;
            }

            // Screen-reader naming. A bare control is announced only as "edit" or "combo box", so each
            // one takes the text of its row label as its name. That is looked up at Skin() time, so the
            // name cannot drift from the label.
            if (string.IsNullOrEmpty(c.AccessibleName) && RowLabelFor(c) is { } text)
                c.AccessibleName = text;
        }
    }

    /// <summary>
    /// Pushes the current DPI factor into every custom-painted control under <paramref name="root"/>.
    /// Those controls size their pixels from their own DpiScale, which is not part of WinForms' autoscale,
    /// so without this push they would stay at the factor they were built with.
    /// </summary>
    void ApplyDpiScale(Control root)
    {
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case Theme.ToggleSwitch t: t.DpiScale = _k; break;
                case Theme.ThemedCombo cb: cb.DpiScale = _k; break;
                case Theme.ThemedSpinner sp: sp.DpiScale = _k; break;
                case Theme.Slider sl: sl.DpiScale = _k; break;
            }
            ApplyDpiScale(c);
        }
    }

    /// <summary>
    /// Puts a focus ring around a flat button. A flat control gets no OS focus rectangle, so
    /// without this a keyboard user cannot see where they are after tabbing. The ring is a child
    /// control rather than an override so it can follow the button when the layout moves.
    /// </summary>
    void FocusRing(Control host)
    {
        var ring = new Theme.FocusRing { Bounds = new Rectangle(-3, -3, host.Width + 6, host.Height + 6) };
        host.Controls.Add(ring);
        host.Resize += (_, _) => ring.Bounds = new Rectangle(-3, -3, host.Width + 6, host.Height + 6);
        void Show(bool on) => ring.Visible = on;
        host.GotFocus += (_, _) => Show(true);
        host.LostFocus += (_, _) => Show(false);
        host.MouseEnter += (_, _) => Show(host.Focused);
        ring.Visible = host.Focused;
    }

    /// <summary>
    /// The caption a sighted user reads on the same row as a control. Rows are built by Add(grid,
    /// label, control), so the caption is column 0 of the grid row that holds the control. A control
    /// is often wrapped first (Flow, Pair, slider plus readout), so this walks up through the wrappers
    /// until it reaches the grid cell that holds them. Only that row's caption counts: a label on a
    /// neighbouring row never does, and the walk stops at a card so nothing outside it is used.
    /// </summary>
    static string? RowLabelFor(Control c)
    {
        Control child = c;
        for (Control? p = c.Parent; p is not null; child = p, p = p.Parent)
        {
            if (p is Theme.CardPanel) return null;
            if (p is not TableLayoutPanel tlp) continue;
            var pos = tlp.GetPositionFromControl(child);
            // Column 0 is the caption column. A control there is a caption itself, or part of a
            // wrapper table that is nested in the caption column, so keep walking up.
            if (pos.Column < 1) continue;
            // No caption in this row means this table is a wrapper (Pair puts two controls side by
            // side with no caption), not the row table. Keep walking to the outer row that has one.
            if (tlp.GetControlFromPosition(0, pos.Row) is Label caption && caption.Text.Length > 0)
                return caption.Text;
        }
        return null;
    }

    // ---- the rows table ------------------------------------------------------------------------

    /// <summary>One checkbox per group, straight off the registry so an add-on's group shows up here.</summary>
    FlowLayoutPanel GroupToggles()
    {
        // Fills its card. The card's height is set from this flow's wrapped height at the card's real
        // width (see RowsTab), so no AutoSize parent ever has to measure this flow at an unknown width.
        var f = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0) };
        foreach (var g in Composer.AllGroups)
        {
            // Tag carries the group name for Apply() to re-read, so the ticks are derived state
            // rather than a second, independently-edited copy of HiddenGroups.
            var box = new Theme.ToggleSwitch
            {
                Text = g.Header,
                Checked = Composer.GroupOn(_draft, g.Header),
                AutoSize = true,
                Tag = g.Header,
                AccessibleName = $"Show the {g.Header} group",
                Margin = new Padding(S(Theme.SpaceXs), S(Theme.SpaceXs), S(Theme.SpaceSm), S(Theme.SpaceXs)),
            };
            box.CheckedChanged += (_, _) =>
            {
                var hidden = new List<string>(_draft.HiddenGroups ?? []);
                if (box.Checked) hidden.RemoveAll(h => h.Equals(g.Header, StringComparison.OrdinalIgnoreCase));
                else if (!hidden.Any(h => h.Equals(g.Header, StringComparison.OrdinalIgnoreCase)))
                    hidden.Add(g.Header);
                _draft.HiddenGroups = hidden.ToArray();
                LoadRowLayout();
                RebuildRowList();
                RebuildDetailRows();
                Live();
            };
            f.Controls.Add(box);
        }
        _groupBox = f;
        return f;
    }

    /// <summary>
    /// The Rows page, top to bottom: the title, the Groups card, the Metrics card, and the colour
    /// button for the selected row. The page scrolls (see <see cref="Page"/>). The metrics card takes
    /// the height the other parts leave free and its ListView scrolls inside it, so at the default
    /// window size only the table scrolls and the page itself does not.
    /// </summary>
    Control RowsTab()
    {
        // Dock Top and AutoSize, like CardStack: the body is as tall as its rows, so the page host
        // scrolls it when the window is short. The metrics row is sized from the viewport in the Layout
        // handler below, so at a tall window the body fills the viewport and nothing scrolls.
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Theme.Canvas,
            Padding = new Padding(S(Theme.SpaceXl)),
            Margin = new Padding(0),
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // One row sized to its content; the margin is the gap below it.
        void Fixed(Control c, Padding margin)
        {
            int r = body.RowCount++;
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            c.Margin = margin;
            body.Controls.Add(c, 0, r);
        }

        Fixed(PageTitle("Rows"), new Padding(0, 0, 0, S(Theme.SpaceLg)));

        // Groups card: the toggles wrap, so the card's height depends on the width it is given. The
        // body's Layout handler (below) sets this row's height from the wrapped toggles at the card's
        // real width. The toggles sit straight in the card; the label-column grid it used is gone.
        const string groupsHelp =
            "Untick a group and its rows leave the table and the overlay. Nothing is deleted: the "
            + "rows keep their place, and ticking the group back brings them back.";
        var groupsFlow = GroupToggles();
        Explain(groupsFlow, groupsHelp);
        // Explicit names: Skin() would otherwise take the nearest labelled ancestor, the page
        // title "Rows", which is not this control's name.
        groupsFlow.AccessibleName = "Groups";
        var groupsCard = new Theme.CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(S(Theme.SpaceSm)),
            Margin = new Padding(0),
        };
        groupsCard.Controls.Add(groupsFlow);
        var groupsHeading = SectionHeading("Groups");
        groupsHeading.AccessibleName = "Groups";
        _tip.SetToolTip(groupsHeading, groupsHelp);
        Fixed(groupsHeading, new Padding(0, 0, 0, S(Theme.SpaceXs)));
        int groupsRow = body.RowCount;
        Fixed(groupsCard, new Padding(0, 0, 0, S(Theme.SpaceSm)));
        body.RowStyles[groupsRow] = new RowStyle(SizeType.Absolute, 0);   // the Layout handler sets the real height

        // Metrics card: _table (built in BuildRowTable) holds the ListView. It is the one row that
        // takes the leftover height.
        const string metricsHelp =
            "Tick a row in the right-hand column, or double-click it anywhere, to show or hide it in the "
            + "overlay.";
        var metricsHeading = SectionHeading("Metrics");
        metricsHeading.AccessibleName = "Metrics";
        _tip.SetToolTip(metricsHeading, metricsHelp);
        _tip.SetToolTip(_table, metricsHelp);
        // Same reason as the Groups flow above: both were named "Metrics" by the label beside them.
        _table.AccessibleName = "Metrics";
        _lines.AccessibleName = "Metrics";
        Fixed(metricsHeading, new Padding(0, 0, 0, S(Theme.SpaceXs)));
        int fill = body.RowCount++;
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, S(200)));   // the Layout handler sets the real height
        _table.Margin = new Padding(0, 0, 0, S(Theme.SpaceSm));
        body.Controls.Add(_table, 0, fill);

        // Row heights that depend on the window are set here, not by Percent rows. Percent rows make the
        // body's preferred height depend on its own size, and the page host cannot scroll a body whose
        // size it does not know. Absolute rows set from the host's viewport avoid that.
        body.Layout += (_, _) =>
        {
            // The toggles wrap, so the card's height depends on the width it gets. The card fills the
            // body's one 100% column, so its width is the body's client width less the body padding.
            // The flow is measured at that width, not at the card's current width, which may be stale
            // during the layout pass. Row height = card height + card padding + card margin.
            int cardWidth = body.ClientSize.Width - body.Padding.Horizontal;
            int innerWidth = cardWidth - groupsCard.Padding.Horizontal;
            // Set when a row changes height. The host's scroll range is taken from this body's bounds
            // during the host's own layout, so a changed body must make the host lay out again.
            bool changed = false;
            if (innerWidth > 0)
            {
                int groupsWant = groupsFlow.GetPreferredSize(new Size(innerWidth, 0)).Height
                    + groupsCard.Padding.Vertical + groupsCard.Margin.Vertical;
                if (body.RowStyles[groupsRow].Height != groupsWant)
                {
                    body.RowStyles[groupsRow] = new RowStyle(SizeType.Absolute, groupsWant);
                    changed = true;
                }
            }

            // The metrics row takes what the viewport leaves after the fixed rows. It never goes below
            // S(200) of table, so a short window scrolls the page instead of squeezing the table further.
            var heights = body.GetRowHeights();
            int fixedHeight = 0;
            for (int i = 0; i < heights.Length; i++)
                if (i != fill) fixedHeight += heights[i];
            int viewport = body.Parent?.ClientSize.Height ?? 0;
            int tableWant = Math.Max(S(200) + _table.Margin.Vertical,
                viewport - body.Padding.Vertical - fixedHeight);
            if (body.RowStyles[fill].Height != tableWant)
            {
                body.RowStyles[fill] = new RowStyle(SizeType.Absolute, tableWant);
                changed = true;
            }

            // The host takes its scroll range from this body's bounds, and it reads them after this handler
            // returns. Calling host.PerformLayout() here re-enters the host's layout pass, and that nested
            // call does not refresh the range. Setting the height here makes the bounds the host reads
            // match the rows. The next pass sees the same height, so this cannot loop.
            if (changed) body.Height = body.GetPreferredSize(new Size(body.Width, 0)).Height;
        };
        return body;
    }

    /// <summary>
    /// ImageList image for a row state (0 = off, 1 = on). Renders the Fluent glyph from <see cref="Icons"/>
    /// at S(16) and falls back to <see cref="FallbackMark"/> when Icons.Render returns null (font unavailable).
    /// Returns a clone; the ImageList takes ownership of it, so the caller must not dispose it.
    /// </summary>
    Bitmap StateImage(int state)
    {
        int px = S(16);
        using var glyph = state == 0
            ? Icons.Render(Icons.CheckboxUnchecked, px, Theme.Muted)
            : Icons.Render(Icons.CheckboxChecked, px, Color.FromArgb(70, 200, 120));
        return glyph is not null ? (Bitmap)glyph.Clone() : FallbackMark(state);
    }

    /// <summary>
    /// Hand-drawn GDI row marks, used only when the Fluent icon font is unavailable.
    /// State 0 (off) is an empty muted box outline (D3, no tick); state 1 (on) is a green filled box with a white tick.
    /// Drawn in 16px design coordinates and scaled by _k so it stays sharp. Returns a clone the ImageList takes ownership of.
    /// </summary>
    Bitmap FallbackMark(int state)
    {
        // Glyphs are drawn at the DPI-scaled ImageSize and the 16px design coordinates are scaled up
        // with a transform, so they stay sharp instead of being bitmap-stretched by the ImageList.
        int px = S(16);   // the same size RebuildMarks gives the ImageList
        using var b = new Bitmap(px, px);
        using var g = Graphics.FromImage(b);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.ScaleTransform(_k, _k);
        g.Clear(Color.Transparent);
        if (state == 0)
        {
            using var pen = new Pen(Theme.Muted, 1f);
            g.DrawRectangle(pen, 2, 2, 11, 11);   // empty box, no tick
        }
        else
        {
            using (var fill = new SolidBrush(Color.FromArgb(70, 200, 120)))
                g.FillRectangle(fill, 2, 2, 12, 12);
            using (var pen = new Pen(Color.White, 2f))
                g.DrawLines(pen, [new Point(4, 8), new Point(7, 11), new Point(12, 4)]);
        }
        return (Bitmap)b.Clone();
    }

    /// <summary>
    /// Builds the row-mark ImageList at the current DPI and points the metrics ListView at it. Runs
    /// from BuildRowTable and from DpiChanged. The ListView is moved to the new list before the previous
    /// one is disposed, so it never holds a disposed list; disposing an ImageList also disposes its
    /// images, so the previous glyphs go with it.
    /// </summary>
    void RebuildMarks()
    {
        var old = _marks;
        var fresh = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(S(16), S(16)) };
        // ImageList takes ownership of each image added (see StateImage), so nothing is disposed here.
        fresh.Images.Add(StateImage(0));
        fresh.Images.Add(StateImage(1));
        _marks = fresh;
        if (_lines is not null) _lines.SmallImageList = fresh;
        old.Dispose();
    }

    /// <summary>A real table: a Show column with a tick glyph, the metric's full name, and section headers.</summary>
    void BuildRowTable()
    {
        // ImageList TAKES OWNERSHIP of an Image handed to Images.Add: it disposes them on Clear and
        // on Dispose. So the clone is deliberately not disposed here - the ImageList does it.
        //
        // Disposing it here would break the list: the native handle is rebuilt later (after a handle
        // recreate, or on a second call into this method), and CreateHandle reads each image again.
        // A disposed image there makes the ListView return null items, which Collect() must cope with.
        // Index 0 = off (empty box, D3), index 1 = on (checked box); see RebuildMarks.
        RebuildMarks();

        _lines = new ListView
        {
            // Height is a starting point only. The card below docks to Fill inside a percentage-height
            // row, so the table ends up as tall as the tab allows and this is just the layout floor.
            Height = (int)(230 * _k),
            Dock = DockStyle.Fill,
            View = View.Details,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            CheckBoxes = false,          // a tick glyph in its own column, so headers have none
            MultiSelect = false,
            SmallImageList = _marks,
            // No stock gridlines: the stock ListView draws them in the system's bright white, and the table is
            // dark. The owner-drawn cells draw their own 1px grid in the palette's Line colour (see DrawGrid).
            GridLines = false,
            FullRowSelect = true,
            HideSelection = false,
        };
        _lines.Columns.Add("Metric", (int)(150 * _k));
        // Description: the one-line meaning of the metric (Composer.Descriptions). Takes the rest of the width.
        _lines.Columns.Add("Description", (int)(70 * _k));
        _lines.Columns.Add("Show in overlay", (int)(115 * _k));
        // Two fixed columns and a metric column that takes the rest, so the table fills its width.
        // Re-fitted on resize, since the tab is resizable.
        _lines.Resize += (_, _) => FitColumns();
        _lines.HandleCreated += (_, _) => FitColumns();
        // Once more after the first layout pass, when the ListView's final client width is known.
        // WinForms' own autoscale after the handle exists scales the ListView font a second time (9.5pt became
        // 29pt at 175%), so the pin is repeated once the window is shown.
        Shown += (_, _) => { PinListFont(); FitColumns(); };
        // Point size, not point size times _k: points already map to device pixels through the monitor's DPI,
        // so multiplying by _k as well made the rows ~1.75x the labels at 175%.
        PinListFont();
        // The table surface is Input, one step lighter than the card it sits in.
        _lines.BackColor = Theme.Input;
        _lines.BorderStyle = BorderStyle.None;
        WireRowPainting();

        // The table gets a bordered card, so it reads as a panel rather than a list floating on the
        // background. It docks to Fill so the list takes whatever height the tab has left over.
        _table = new Theme.CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(1),
            Margin = new Padding(0),
        };
        _table.Controls.Add(_lines);

        LoadRowLayout();
        RebuildRowList();
    }

    /// <summary>Largest DPI factor the form lays out for: 300%, the top of Windows' scale options.</summary>
    const float MaxScale = 3f;

    /// <summary>
    /// Client size for the current factor. Width is 1060 points scaled by _k, capped to the primary
    /// working area less a border allowance, so at 250% and 300% the window cannot open wider than the
    /// screen. Height comes from WindowHeight, which already has the same cap.
    /// </summary>
    Size WindowSize()
    {
        int want = (int)(1060 * _k);
        int room = WorkArea().Width - S(16);
        return new Size(Math.Min(want, room), WindowHeight());
    }

    /// <summary>
    /// The working area of the monitor this window is on. Once the handle exists that is the monitor it
    /// occupies; before that (the constructor) it is the monitor at Location, which is the primary for the
    /// default CenterScreen start. Size against this monitor, not the primary: a window on a 4K monitor
    /// must not be capped to the primary's 1920x1032 working area.
    /// </summary>
    Rectangle WorkArea() =>
        (IsHandleCreated ? Screen.FromControl(this) : Screen.FromPoint(Location)).WorkingArea;

    /// <summary>
    /// Minimum window size for the current factor, capped to the working area. The caps only bite on a
    /// screen too small for the unclamped size, e.g. 250% on a 1080p display (2150 px wide, 1920 px screen).
    /// </summary>
    Size MinimumWindowSize()
    {
        var a = WorkArea();
        int w = (int)(860 * _k), h = (int)(470 * _k);
        w = Math.Min(w, a.Width - S(16));
        h = Math.Min(h, a.Height - S(80));
        return new Size(w, h);
    }

    /// <summary>
    /// The client height to open with.
    ///
    /// 760 because the Rows tab is the tallest thing here: the live preview takes 200, the group
    /// toggles and the format editor take about 170, and the metrics list needs room for more than
    /// about four rows. It is capped to the monitor's working area so the window cannot open taller
    /// than the screen - at 150% scaling 760 points would be 1140 pixels, which does not fit on a
    /// 1080p display, so the height is cut to fit instead.
    /// </summary>
    int WindowHeight()
    {
        int want = (int)(760 * _k);
        int room = WorkArea().Height - (int)(80 * _k);
        // The floor is capped to the same room, so the height never exceeds the screen even when 470 points
        // at this factor is taller than it (300% on 1080p).
        return Math.Max(Math.Min((int)(470 * _k), room), Math.Min(want, room));
    }

    /// <summary>
    /// Gives the metric column whatever the other two do not need, so the table uses its width
    /// rather than leaving a gap and truncating a heading. A floor keeps it readable when the window
    /// is narrow; below that the columns would collapse rather than scroll horizontally, which is
    /// the lesser evil in a 900px window.
    /// </summary>
    void FitColumns()
    {
        if (_lines.Columns.Count < 3) return;
        // The Show column is sized to its heading, not a fixed 115 logical px: the heading is set in the
        // ListView font, which is larger at 175%, and a fixed width cut "Show in overlay" short.
        int show = Math.Max(S(115), TextRenderer.MeasureText("Show in overlay", _lines.Font).Width + S(24));
        // The Metric and Description columns share what the Show column leaves: Metric gets about 38%, and
        // Description takes the rest. The two always sum to the ClientSize left after Show, so the ListView
        // never needs a horizontal scrollbar. ClientSize already excludes a visible vertical scrollbar.
        int avail = Math.Max(0, _lines.ClientSize.Width - show);
        int metric = avail * 38 / 100;
        // Section counts ("N of M shown") sit in the Description column and must stay fully visible. Floor
        // that column at the width of the widest plausible count, taking the space from Metric.
        int countWidth;
        using (var bold = new Font(_lines.Font, FontStyle.Bold))
            countWidth = TextRenderer.MeasureText("88 of 88 shown", bold).Width + S(24);
        int desc = avail - metric;
        if (desc < countWidth)
        {
            desc = Math.Min(countWidth, avail);
            metric = avail - desc;
        }
        _lines.Columns[0].Width = metric;
        _lines.Columns[1].Width = desc;
        _lines.Columns[2].Width = show;
    }

    /// <summary>Re-reads every group tick from the config, so the boxes cannot drift from the table.</summary>
    void SyncGroupToggles()
    {
        foreach (CheckBox box in _groupBox.Controls.OfType<CheckBox>())
            if (box.Tag is string header)
                box.Checked = Composer.GroupOn(_draft, header);
    }

    /// <summary>Sections and rows in the order the config remembers, or the built-in order.</summary>
    void LoadRowLayout()
    {
        // A name that is not a real section (for example "1 OF 8 SHOWN") is dropped rather than
        // kept, so a bad config can never grow a section of nonsense.
        var known = Composer.Sections.Select(s => s.Header).ToList();
        _sections = _draft.SectionOrder.Length > 0
            ? _draft.SectionOrder.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => known.Any(k => k.Equals(s, StringComparison.OrdinalIgnoreCase)))
                .Select(s => known.First(k => k.Equals(s, StringComparison.OrdinalIgnoreCase)))
                .ToList()
            : new List<string>(known);
        foreach (var s in known)
            if (!_sections.Contains(s)) _sections.Add(s);

        _ids = _draft.RowOrder.Length > 0
            ? _draft.RowOrder.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()
            : Composer.Sections.SelectMany(s => s.Ids).ToList();
        foreach (var id in LineIds)
            if (!_ids.Contains(id)) _ids.Add(id);
        foreach (var id in CustomIds())
            if (!_ids.Contains(id)) _ids.Add(id);

        // A saved order can name rows this build does not have (for example "cpus"), or a custom row
        // whose template no longer exists ("Your row 3"). Keeping either would put a row in the list
        // that nothing can remove, so Remove row would do nothing.
        _ids.RemoveAll(id => !Composer.IsRow(id, _draft.CustomRows.Count));
    }

    IEnumerable<string> CustomIds()
    {
        for (int i = 0; i < _draft.CustomRows.Count; i++) yield return Composer.CustomPrefix + (i + 1);
    }

    void RebuildRowList()
    {
        bool shown = _draft.Lines.Length > 0;
        bool On(string id) => shown && _draft.Lines.Contains(id);

        // A row lives in the section that defines it; within a section the saved row order wins.
        // Only the groups still switched on claim rows, so hiding one really does empty the table.
        var home = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in Composer.SectionsFor(_draft))
            foreach (var id in g.Ids) home[id] = g.Header;

        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The list is cleared and refilled here, and a 1s preview timer calls Collect(), which
        // enumerates these very items. A timer tick landing inside that window walked an item
        // collection mid-rebuild and came back with a null, which then threw out of the timer
        // callback. The rebuild is a few hundred microseconds, so the flag costs nothing and the
        // tick simply waits for the next one.
        _rebuilding = true;
        try
        {
            _lines.BeginUpdate();
            _lines.Items.Clear();
            foreach (var section in _sections)
            {
                var members = _ids.Where(id => !placed.Contains(id) && home.GetValueOrDefault(id) == section).ToList();
                foreach (var id in members) placed.Add(id);
                _lines.Items.Add(HeaderItem(section, members, On));
                foreach (var id in members) _lines.Items.Add(RowItem(id, On(id)));
            }

            // Anything the section list does not claim, including the user's own rows, goes last -
            // but not the rows of a group that is switched off. Those are hidden, not deleted: they
            // stay in _ids so the saved Lines and RowOrder keep them, and they come back with the
            // group.
            var rest = _ids.Where(id => !placed.Contains(id) && !Composer.IsHidden(_draft, id)).ToList();
            if (rest.Count > 0)
            {
                _lines.Items.Add(HeaderItem("Other", rest, On));
                foreach (var id in rest) _lines.Items.Add(RowItem(id, On(id)));
            }
            _lines.EndUpdate();
            // Refilling can make the vertical scrollbar appear, which shrinks ClientSize. Refit the columns
            // here, so the metric column is sized against the real width and no horizontal scrollbar results.
            FitColumns();
        }
        finally { _rebuilding = false; }
    }

    ListViewItem HeaderItem(string section, List<string> members, Func<string, bool> on)
    {
        int total = members.Count;
        int shown = members.Count(id => on(id));
        return new ListViewItem(new[]
        {
            section.ToUpperInvariant(),
            total > 0 ? $"{shown} of {total} shown" : "",
            "",
        })
        {
            ImageIndex = -1,
            Font = new Font(_lines.Font, FontStyle.Bold),
            // Ink, not Muted: the section band is a tinted fill, and the bold header text has to read on it.
            ForeColor = Theme.Ink,
            Tag = "section",
        };
    }

    /// <summary>
    /// One metric row: its label, its one-line description from <see cref="Composer.Descriptions"/>, and
    /// the Show state. Custom rows have no catalogue entry, so they get a generic description.
    /// </summary>
    ListViewItem RowItem(string id, bool on) => new(new[]
    {
        NameOf(id),
        Composer.Descriptions.GetValueOrDefault(id, "Your own row"),
        on ? "yes" : "no",
    })
    {
        ImageIndex = on ? 1 : 0,
        Tag = id,
    };

    /// <summary>The user's own rows are numbered, not named; the template is the label.</summary>
    string NameOf(string id) =>
        Composer.Names.GetValueOrDefault(id, id.StartsWith(Composer.CustomPrefix, StringComparison.OrdinalIgnoreCase)
            ? "Your row " + id[Composer.CustomPrefix.Length..]
            : id);

    static bool IsSection(ListViewItem i) => i is not null && i.Tag as string == "section";

    /// <summary>
    /// Switches the metrics table to owner draw and hooks the painters. The stock ListView cannot tint an
    /// enabled row or draw its selection in the theme's colours, so the header, the rows and the empty area
    /// right of the last column are all painted here. Nothing is stored for this: a row is enabled when its
    /// ImageIndex is 1, the same state Toggle and Collect read.
    /// </summary>
    void WireRowPainting()
    {
        _lines.OwnerDraw = true;
        _lines.DrawColumnHeader += (_, e) => DrawHeaderCell(e);
        _lines.DrawItem += (_, e) => DrawRowFill(e);
        _lines.DrawSubItem += (_, e) => DrawRowCell(e);
        _lines.Paint += (_, e) => PaintEmptyArea(e.Graphics);
    }

    /// <summary>
    /// Paints one column header on the Card surface, in Muted text, aligned as the column asks. The header is
    /// not clickable, so there is no pressed state to draw.
    /// </summary>
    void DrawHeaderCell(DrawListViewColumnHeaderEventArgs e)
    {
        e.DrawDefault = false;
        using (var fill = new SolidBrush(Theme.Card)) e.Graphics.FillRectangle(fill, e.Bounds);
        int pad = S(6);
        var text = new Rectangle(e.Bounds.X + pad, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 2 * pad), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? string.Empty, _lines.Font, text, Theme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | AlignFlags(e.Header?.TextAlign ?? HorizontalAlignment.Left));
        DrawGrid(e.Graphics, e.Bounds);
    }

    /// <summary>
    /// The table's grid: a 1px line along each cell's right edge and bottom edge, in a lighter mix of the input
    /// colour and white, because <see cref="Theme.Line"/> is nearly invisible against the row fill.
    /// Every cell draws its own two edges, so the lines run under each column and each row, and the outer
    /// edge is the card's own border around the list.
    /// </summary>
    static void DrawGrid(Graphics g, Rectangle r)
    {
        using var pen = new Pen(Theme.Mix(Theme.Input, Color.White, 0.25f));
        g.DrawLine(pen, r.Right - 1, r.Top, r.Right - 1, r.Bottom - 1);   // column right edge
        g.DrawLine(pen, r.Left, r.Bottom - 1, r.Right - 1, r.Bottom - 1); // row bottom edge
    }

    /// <summary>
    /// Fills a whole row before its cells are drawn. Section bands take their own colour first. Selected rows
    /// take the accent, the same job the stock selection did. Enabled rows take the accent blended 18% over
    /// the table surface. Disabled rows sit on the table surface itself.
    /// </summary>
    void DrawRowFill(DrawListViewItemEventArgs e)
    {
        e.DrawDefault = false;
        using var fill = new SolidBrush(RowFill(e.Item, e.Item.Selected));
        e.Graphics.FillRectangle(fill, e.Bounds);
    }

    /// <summary>
    /// Draws one cell. A metric row gets the checkbox glyph on the first column, with the glyph space reserved so
    /// its text starts after the mark. A section header has no glyph: its text sits flush left on the band. Each
    /// cell then gets its grid edges. The fill is repeated here so a cell never shows stale pixels.
    /// </summary>
    void DrawRowCell(DrawListViewSubItemEventArgs e)
    {
        e.DrawDefault = false;
        ListViewItem it = e.Item!;
        bool section = IsSection(it);
        bool selected = it.Selected;
        using (var fill = new SolidBrush(RowFill(it, selected))) e.Graphics.FillRectangle(fill, e.Bounds);

        int pad = S(6), gap = S(4);
        int glyphW = _marks.ImageSize.Width;
        int left = e.Bounds.X + pad;
        if (e.ColumnIndex == 0 && !section)
        {
            int idx = it.ImageIndex;
            if (idx >= 0 && idx < _marks.Images.Count)
                _marks.Draw(e.Graphics, left, e.Bounds.Y + (e.Bounds.Height - _marks.ImageSize.Height) / 2, idx);
            left += glyphW + gap;
        }
        var text = new Rectangle(left, e.Bounds.Y, Math.Max(0, e.Bounds.Right - pad - left), e.Bounds.Height);
        // A section header keeps its own ink on the band even when selected; the accent would clash with it.
        Color ink = selected && !section ? Theme.On(Theme.Accent) : it.ForeColor;
        TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? string.Empty, it.Font, text, ink,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis
            | AlignFlags(_lines.Columns[e.ColumnIndex].TextAlign));
        DrawGrid(e.Graphics, e.Bounds);
    }

    /// <summary>
    /// Paints the strip right of the last column with the table surface. The columns do not always reach the
    /// right edge, and in owner-draw mode that strip is not guaranteed to take the ListView's BackColor.
    /// </summary>
    void PaintEmptyArea(Graphics g)
    {
        int used = 0;
        foreach (ColumnHeader col in _lines.Columns) used += col.Width;
        if (used >= _lines.ClientSize.Width) return;
        using var fill = new SolidBrush(Theme.Input);
        g.FillRectangle(fill, used, 0, _lines.ClientSize.Width - used, _lines.ClientSize.Height);
    }

    /// <summary>
    /// Background of one row. A section header gets the solid section band first, so it stands apart from the
    /// metric rows and clicking it does not turn it accent. Then selected wins; then enabled (ImageIndex 1)
    /// gets the accent tint; anything else is the table surface. The state is read from the item, nothing is stored.
    /// </summary>
    static Color RowFill(ListViewItem? it, bool selected)
    {
        if (it is not null && IsSection(it)) return Theme.Mix(Theme.Input, Theme.Accent, 0.30f);
        if (selected) return Theme.Accent;
        if (it is not null && it.ImageIndex == 1) return Theme.Mix(Theme.Input, Theme.Accent, 0.18f);
        return Theme.Input;
    }

    /// <summary>Maps a column's alignment onto the GDI text flags used by <see cref="TextRenderer"/>.</summary>
    static TextFormatFlags AlignFlags(HorizontalAlignment a) => a switch
    {
        HorizontalAlignment.Right => TextFormatFlags.Right,
        HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
        _ => TextFormatFlags.Left,
    };

    /// <summary>Which column an x offset falls in. FullRowSelect hides this from the hit test.</summary>
    int ColumnAt(int x)
    {
        int at = 0;
        for (int c = 0; c < _lines.Columns.Count; c++)
        {
            int w = _lines.Columns[c].Width;
            if (x < at + w) return c;
            at += w;
        }
        return _lines.Columns.Count - 1;
    }

    void Toggle(ListViewItem it)
    {
        if (it is null || IsSection(it)) return;
        bool on = it.ImageIndex != 1;
        it.ImageIndex = on ? 1 : 0;
        it.SubItems[2].Text = on ? "yes" : "no";
        // The table is owner-drawn, so changing the item does not repaint its row by itself.
        if (_lines.IsHandleCreated) _lines.Invalidate(it.Bounds);
        Live();
        UpdateSectionSummaries();
        RebuildDetailRows();
    }

    /// <summary>A section header shows how many of its rows are switched on.</summary>
    void UpdateSectionSummaries()
    {
        for (int i = 0; i < _lines.Items.Count; i++)
        {
            if (!IsSection(_lines.Items[i])) continue;
            int end = i + 1;
            while (end < _lines.Items.Count && !IsSection(_lines.Items[end])) end++;
            int total = end - i - 1;
            int on = 0;
            for (int k = i + 1; k < end; k++) if (_lines.Items[k].ImageIndex == 1) on++;
            _lines.Items[i].SubItems[1].Text = total > 0 ? $"{on} of {total} shown" : "";
            // The header row's count text changed, so its row is repainted too.
            if (_lines.IsHandleCreated) _lines.Invalidate(_lines.Items[i].Bounds);
        }
    }

    void SelectFirstRow()
    {
        foreach (ListViewItem i in _lines.Items)
        {
            if (IsSection(i) || i.ImageIndex != 1) continue;
            i.Selected = true;
            i.EnsureVisible();
            _lines.Focus();
            return;
        }
    }

    /// <summary>A name box on the Row details page. Its text is the saved override; empty means the detected name.</summary>
    TextBox NameBox(string text, string accessible) => new()
    {
        Text = text,
        AccessibleName = accessible,
        PlaceholderText = "Detected name",
        Width = (int)(260 * _k),
    };

    /// <summary>
    /// Refills the Metrics card on the Row details page: one editable line per metric ticked on the Rows
    /// tab, in the order the Rows table lists them. Only rows the overlay shows are listed (ticked, and in
    /// a group that is on), and only built-in metrics: a user's own rows keep their text in CustomRows,
    /// which is not the Formats map, so they are not editable here.
    ///
    /// Each line reads its text from the draft, so typed edits survive a rebuild. The previous controls are
    /// disposed here. Rebuilding runs with <see cref="_rebuilding"/> set, so the new boxes filling their
    /// text does not count as an edit.
    /// </summary>
    void RebuildDetailRows()
    {
        if (_detailGrid is null || _lines is null) return;
        var ids = new List<string>();
        foreach (ListViewItem it in _lines.Items)
            if (!IsSection(it) && it.ImageIndex == 1 && it.Tag is string id && Composer.Catalog.ContainsKey(id))
                ids.Add(id);

        bool wasRebuilding = _rebuilding;
        _rebuilding = true;
        try
        {
            _detailGrid.SuspendLayout();
            // Remove first, then dispose: a removed control must not be left in the grid, and its
            // handle and any ToolTip hook go with it.
            var old = _detailGrid.Controls.Cast<Control>().ToList();
            _detailGrid.Controls.Clear();
            foreach (var c in old) c.Dispose();
            _detailGrid.RowStyles.Clear();
            _detailGrid.RowCount = 0;

            if (ids.Count == 0)
                Add(_detailGrid, "", Note("Tick a row on the Rows tab to edit the text it shows here."));
            foreach (var id in ids)
            {
                string key = id;   // captured per row; the loop variable is shared
                var box = new TextBox
                {
                    Text = FormatText(key),
                    AccessibleName = NameOf(key),
                    Width = (int)(260 * _k),
                };
                box.TextChanged += (_, _) => { if (!_rebuilding) FormatEdited(key, box.Text); };
                Add(_detailGrid, NameOf(key), box);
            }
            Skin(_detailGrid);   // the new boxes and captions were not in the constructor's Skin pass
            _detailGrid.ResumeLayout();
        }
        finally { _rebuilding = wasRebuilding; }
    }

    /// <summary>The text a row shows: its Formats entry when one is set, otherwise the built-in template.</summary>
    string FormatText(string id) =>
        _draft.Formats.TryGetValue(id, out var t) && !string.IsNullOrWhiteSpace(t) ? t : Composer.Catalog[id];

    /// <summary>
    /// Stores one edited row text in the draft and applies it at once. Blank, or exactly the built-in
    /// text, removes the Formats entry, so the config keeps only real edits and the row goes back to
    /// tracking the default. Anything else is kept as typed.
    /// </summary>
    void FormatEdited(string id, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text == Composer.Catalog[id]) _draft.Formats.Remove(id);
        else _draft.Formats[id] = text;
        Live();
    }

    /// <summary>
    /// What it is, its version and keyboard shortcuts, and who made it. Laid out as a growing,
    /// scrolling column like every other tab, so a short window scrolls rather than clipping the text.
    /// </summary>
    Control About()
    {
        var g = Grid();
        g.ColumnStyles.Clear();
        g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, (int)(150 * _k)));
        g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // Every value is plain primary text, the same size as the body copy. The GitHub link is the only
        // accent-coloured item. Each control carries an AccessibleName, so screen readers and the probe's
        // --names check see the same label as the row it sits on.
        var title = Value("FrameGlass");
        title.AccessibleName = "FrameGlass";
        Add(g, "", title);

        var blurb = Value("A frametime, temperature and load overlay for Windows games. It sits on top of "
            + "whatever you are playing, pinned to that window's monitor.");
        blurb.AccessibleName = "Description";
        Add(g, "", blurb);

        var version = Value(Program.Version);
        version.AccessibleName = "Version";
        Add(g, "Version", Stack(version, Note("Build " + Program.BuildDate + ".")));

        // Kept as a field: Apply() rewrites the shortcut text on Reset, otherwise the hint keeps printing
        // the keys the config had before.
        AboutKeys = Value(KeysText(_draft.ToggleKey, _draft.QuitKey));
        AboutKeys.AccessibleName = "Keyboard";
        Add(g, "Keyboard", Stack(AboutKeys, Note("Changed on the General tab.")));

        var author = Value("By " + Program.Author + ".");
        author.AccessibleName = "Author";
        Add(g, "Author", author);

        var link = Label2(Program.AuthorUrl, 10f, FontStyle.Regular, Theme.Accent, 0);
        link.AccessibleName = "GitHub project link";
        Add(g, "", link);

        // The page title, then all of the content in one card with no section heading above it.
        return CardStack("About", ("", g));
    }

    /// <summary>The shortcut sentence shown on About. Shared by the build and by Apply(), so they cannot drift.</summary>
    static string KeysText(string toggle, string quit) => $"{toggle} shows or hides the overlay, {quit} quits it.";

    /// <summary>A value on the About card: plain primary text.</summary>
    Label Value(string text) => Label2(text, 10f, FontStyle.Regular, Theme.Ink, 0);

    /// <summary>A secondary line under a value, in the muted colour.</summary>
    Label Note(string text)
    {
        var l = Label2(text, 9.5f, FontStyle.Regular, Theme.Muted, 0);
        l.AccessibleName = text;   // inside a stacked cell, so the row label is not its name
        return l;
    }

    /// <summary>Stacks items top to bottom in one grid cell, so a value can have a note under it.</summary>
    static FlowLayoutPanel Stack(params Control[] items)
    {
        var f = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };
        f.Controls.AddRange(items);
        return f;
    }

    /// <summary>A wrapped, auto-sizing block of text for the About tab. Uses SpacedLabel for its line pitch.</summary>
    Label Label2(string text, float size, FontStyle style, Color colour, int gap) => new SpacedLabel
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size((int)(520 * _k), 0),
        Font = new Font(Theme.UiFont, size, style),
        ForeColor = colour,
        Margin = new Padding(0, gap, 0, gap),
    };

    // ---- plumbing ------------------------------------------------------------------------------

    void Changed(object? sender, EventArgs e) => Live();

    void Live()
    {
        if (IsDisposed || !IsHandleCreated) return;
        var c = Collect();
        _apply(c);
        _draft.CopyFrom(c);
        // The disk write is deferred, the rest is not. Live() runs on every value change, and
        // Config.Save is a blocking File.WriteAllText of the whole config - so dragging a slider
        // wrote the file 50-100 times, every one a synchronous disk round trip on the UI thread.
        // The overlay is updated immediately above, so nothing visible waits on the write; it just
        // lands once the user stops moving.
        _savePending = true;
    }

    /// <summary>Write the config if an edit asked for it and it has not been written yet.</summary>
    void FlushSave()
    {
        if (!_savePending || IsDisposed) return;
        _savePending = false;
        Config.Save(_draft);
    }

    bool _savePending;

    /// <summary>
    /// Reacts to a slider's value changing, which is a different thing from reacting to its Scroll
    /// event.
    ///
    /// TrackBar raises Scroll only when Windows reflects the native scroll notification back
    /// through the control's parent chain, and that reflection stopped reaching these sliders: the
    /// thumb moved but nothing downstream ran, so the overlay and the preview never followed the
    /// drag and only caught up when a save or a profile load wrote the file and the overlay
    /// re-read it. ValueChanged is raised by the control itself whenever Value changes - a drag
    /// included - and does not depend on the parent chain at all.
    ///
    /// The _rebuilding guard is what makes this safe to use for the programmatic path too: Apply()
    /// assigns every value while repopulating the form, and without the guard each assignment
    /// would fire a full collect-render-apply cycle.
    /// </summary>
    void Watch(Theme.Slider bar, Action onChanged) =>
        bar.ValueChanged += (_, _) => { if (!_rebuilding) onChanged(); };

    void Apply(Config c)
    {
        // Values with no control in this window are taken from _draft by Collect(). A reset must put
        // its values into _draft first, or the Live() that follows would write the values from before
        // the reset back over them.
        _draft.Formats = new Dictionary<string, string>(c.Formats);
        _draft.GpuMatch = c.GpuMatch;
        _draft.RamUnit = c.RamUnit;
        _draft.TimeFormat = c.TimeFormat;
        _draft.Clock24Hour = c.Clock24Hour;
        _draft.CpuNameOverride = c.CpuNameOverride;
        _draft.GpuNameOverride = c.GpuNameOverride;
        _rebuilding = true;
        try { ApplyCore(c); }
        finally { _rebuilding = false; }
    }

    void ApplyCore(Config c)
    {
        _panelBox.Checked = c.Box;
        _stack.Checked = !c.Layout.Equals("line", StringComparison.OrdinalIgnoreCase);
        _line.Checked = !_stack.Checked;
        _cols.Value = Clamp(c.Columns, 0, 8);
        _graphW.Value = Clamp(c.GraphWidth, 0, 800);
        _graphH.Value = Clamp(c.GraphHeight, 12, 400);
        _gap.Value = Clamp(c.Gap, 0, 40);
        _opacity.Value = Clamp((int)Math.Round(c.Opacity * 100), 0, 100);
        _opacityVal.Text = Pct(_opacity.Value) + " text";
        _strength.Value = Clamp((int)Math.Round(c.PanelOpacity * 100), 0, 100);
        _strengthVal.Text = Pct(_strength.Value) + " bg";
        _strength.Enabled = _panelBox.Checked;
        _x.Value = Clamp((int)c.X, 0, 100); _y.Value = Clamp((int)c.Y, 0, 100);
        _xVal.Text = Pct(c.X); _yVal.Text = Pct(c.Y);
        _fontName.Text = c.Font;
        _fontSize.Value = Clamp((int)Math.Round(c.FontSize), 6, 36);
        _fontSizeVal.Text = $"{_fontSize.Value} pt";
        _hotTemp.Value = Clamp((int)c.HotTempC, 0, 120);
        _hotLoad.Value = Clamp((int)c.HotLoadPct, 0, 100);
        _interval.Text = SampleLabel(c.IntervalMs);
        _pingAddr.Text = c.PingAddr;
        _minToTray.Checked = c.MinimizeToTray; _startHidden.Checked = c.StartMinimized;
        _toggleKey.Text = c.ToggleKey; _quitKey.Text = c.QuitKey;
        _target.Text = c.TargetProcess.Length > 0 ? c.TargetProcess : FollowFocused;
        _bg = Composer.Parse(c.BgColor, _bg);
        _text = Composer.Parse(c.TextColor, _text);
        _accent = Composer.Parse(c.AccentColor, _accent);
        _hotPreset.SelectedIndex = Math.Max(0, Array.FindIndex(Config.HotPresets, p => p.Name == c.HotColorName));
        _hot = Composer.Parse(c.ResolveHotColor(), _hot);
        foreach (var (b, col) in new[]
        {
            (SwatchFor("panel"), _bg), (SwatchFor("text"), _text),
            (SwatchFor("graph"), _accent), (_swatchHot, _hot),
        })
        {
            SetSwatch(b, col);
        }

        // Derived state, not stored state. None of these is a control the user edits directly, so
        // they are recomputed here; otherwise the group ticks could disagree with the metrics table
        // right below them.
        SyncGroupToggles();
        AboutKeys.Text = KeysText(c.ToggleKey, c.QuitKey);
        LoadRowLayout();
        RebuildRowList();
        _cpuName.Text = c.CpuNameOverride;
        _gpuName.Text = c.GpuNameOverride;
        RebuildDetailRows();
    }

    Button SwatchFor(string name) => _designSwatches[name];

    Config Collect()
    {
        var c = new Config
        {
            // X left-to-right, Y bottom-to-top: (0,0) is the bottom-left corner. Both are gated to
            // the working area by Composer.Anchor, so no value can push the overlay off screen.
            X = _x.Value, Y = _y.Value,
            Layout = _stack.Checked ? "stack" : "line",
            Columns = (int)_cols.Value,
            HiddenGroups = _draft.HiddenGroups ?? [],
            Box = _panelBox.Checked,
            PanelOpacity = (double)_strength.Value / 100.0,
            GraphWidth = (int)_graphW.Value, GraphHeight = (int)_graphH.Value,
            Gap = (int)_gap.Value,
            Font = string.IsNullOrWhiteSpace(_fontName.Text) ? "Consolas" : _fontName.Text.Trim(),
            FontSize = _fontSize.Value,
            Opacity = (double)_opacity.Value / 100.0,
            HotTempC = (double)_hotTemp.Value,
            HotLoadPct = (double)_hotLoad.Value,
            HotColorName = _hotPreset.SelectedIndex >= 0 && _hotPreset.SelectedIndex < Config.HotPresets.Length
                ? Config.HotPresets[_hotPreset.SelectedIndex].Name : "orange",
            IntervalMs = SampleMs(_interval.Text),
            // No widgets for these any more, so the applied values are carried over.
            GpuMatch = _draft.GpuMatch,
            RamUnit = _draft.RamUnit,
            TimeFormat = _draft.TimeFormat,
            Clock24Hour = _draft.Clock24Hour,
            // Read from the Row details name boxes. Config.Normalise trims the same way on load.
            CpuNameOverride = _cpuName.Text.Trim(),
            GpuNameOverride = _gpuName.Text.Trim(),
            PingAddr = _pingAddr.Text.Trim(),
            // A null in this collection is not a row the user has, so it is skipped rather than
            // dereferenced. The 1s preview timer calls Collect() on its own thread's message loop, and
            // the enumerator of ListViewItemCollection can yield a null there. An exception thrown out of
            // a timer callback would take the whole settings window down, so the null is skipped.
            Lines = _lines.Items.Cast<ListViewItem?>()
                .Where(i => i is not null && !IsSection(i!) && i!.ImageIndex == 1)
                .Select(i => (string)i!.Tag!)
                .ToArray(),
            MinimizeToTray = _minToTray.Checked,
            StartMinimized = _startHidden.Checked,
            ToggleKey = _toggleKey.Text.Trim(),
            QuitKey = _quitKey.Text.Trim(),
            // Typed text is trimmed; blank or the follow entry means "follow the focused window".
            TrackForeground = string.IsNullOrWhiteSpace(_target.Text) || _target.Text.Trim() == FollowFocused,
            TargetProcess = string.IsNullOrWhiteSpace(_target.Text) || _target.Text.Trim() == FollowFocused
                ? "" : _target.Text.Trim(),
            SectionOrder = string.Join(',', _sections),
            RowOrder = string.Join(',', _ids),
            CustomRows = new List<string>(_draft.CustomRows),
            // No Format editor any more, so the applied Formats are carried over unchanged.
            Formats = new Dictionary<string, string>(_draft.Formats),
            RowColors = new Dictionary<string, string>(_draft.RowColors),
            BgColor = Composer.Hex(_bg),
            TextColor = Composer.Hex(_text),
            AccentColor = Composer.Hex(_accent),
        };
        if (c.Lines.Length == 0) c.Lines = ["fps"];
        return c;
    }

    // ---- helpers -------------------------------------------------------------------------------

    static readonly string[] LineIds = Composer.Groups;

    TableLayoutPanel Grid()
    {
        // AutoSize lets the grid grow to fit its rows. A fixed height would cut off everything below it,
        // and the host would have nothing to scroll.
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            // 6: the grid's padding repeats at the top and bottom of every tab, and with rows at a 4px
            // margin a larger padding would be most of the gap between the outer edge and the first row.
            Padding = new Padding((int)(6 * _k)),
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, (int)(130 * _k)));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    void Add(TableLayoutPanel g, string label, Control c, string help = "")
    {
        int r = g.RowCount++;
        g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (label.Length > 0)
        {
            var l = new Label
            {
                Text = label,
                // AutoSize with no fixed Height. A fixed-height label would sit at the top of its row while
                // the control is centred, so the label would look low and its text could clip against the
                // control. Anchor Left alone centres an auto-sized label vertically in its cell.
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                TextAlign = ContentAlignment.MiddleLeft,
                // 2px, not SpaceXs. This margin is paid twice per row, and the Design tab alone has
                // fourteen rows; at 4 there would be 8px of air between each pair. Scaled with DPI like the rest.
                Margin = new Padding(S(2), S(2), 0, S(2)),
            };
            g.Controls.Add(l, 0, r);
            if (help.Length > 0) _tip.SetToolTip(l, help);
        }
        c.Margin = new Padding(S(Theme.SpaceSm), 2, 0, 2);
        c.Anchor = AnchorStyles.Left;
        g.Controls.Add(c, 1, r);
        if (help.Length > 0) Explain(c, help);
    }

    /// <summary>
    /// Puts a tooltip on a control and on every interactive thing inside it. Rows are built as
    /// wrapper panels (two sliders side by side, a slider and its readout, a combo and a checkbox),
    /// and a ToolTip on a Panel never fires when the pointer is on the slider or combo inside it -
    /// so the help text appeared to be missing on exactly the controls people hovered most.
    /// </summary>
    void Explain(Control c, string help)
    {
        _tip.SetToolTip(c, help);
        foreach (var child in All(c))
            if (child is not (Label or PictureBox or TableLayoutPanel or FlowLayoutPanel))
                _tip.SetToolTip(child, help);
    }

    /// <summary>
    /// Wraps a page body in a scrolling host. Every page scrolls, including Rows.
    ///
    /// The body must size itself to its content (Dock Top, AutoSize) for the host to scroll it.
    /// A body that is Dock Fill inside an AutoScroll host gets only the viewport's height, so it
    /// clips instead of scrolling. That is why the Rows page's scrolling part is a fixed-height row
    /// rather than a percentage.
    /// </summary>
    static Panel Page(string title, Control body)
    {
        var p = new Panel { Name = title, Dock = DockStyle.Fill, BackColor = Theme.Canvas };
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Theme.Canvas,
            BorderStyle = BorderStyle.None,
        };
        host.Controls.Add(body);
        p.Controls.Add(host);
        return p;
    }

    /// <summary>
    /// One scrolling page body in the Windows 11 Settings layout: the page title, then each section
    /// as a heading over its card. The stack is Dock Top and sizes to its content, so the page's
    /// AutoScroll host scrolls it. It fills the content width, with 24*k between cards and 24*k
    /// around the page. Every value goes through S(), so DpiChanged's Scale() moves it like the rest.
    /// </summary>
    TableLayoutPanel CardStack(string title, params (string Heading, TableLayoutPanel Grid)[] sections)
    {
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(S(Theme.SpaceXl)),
            Margin = new Padding(0),
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // One row per item, sized to its content. The margin is the gap below the item.
        void Row(Control c, Padding margin)
        {
            int r = stack.RowCount++;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            c.Margin = margin;
            stack.Controls.Add(c, 0, r);
        }

        Row(PageTitle(title), new Padding(0, 0, 0, S(Theme.SpaceLg)));
        foreach (var (heading, grid) in sections)
        {
            // An empty heading means no label above the card (About has one card and no heading).
            if (heading.Length > 0) Row(SectionHeading(heading), new Padding(0, 0, 0, S(Theme.SpaceXs)));
            // The card is Fill in its row, and AutoSize so the row is as tall as the grid inside it.
            // The grid keeps its own Dock Top, AutoSize (see Grid()).
            var card = new Theme.CardPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(S(Theme.SpaceSm)),
                Margin = new Padding(0),
            };
            card.Controls.Add(grid);
            // 24*k (one text line) between cards: this is the gap to the next section heading or the end of the page.
            Row(card, new Padding(0, 0, 0, S(Theme.SpaceXl)));
        }
        return stack;
    }

    /// <summary>
    /// The page title at the top of a page. Font sizes are in points, so the monitor's DPI already
    /// scales them; only pixel sizes (margins, padding, widths) are multiplied by _k.
    /// </summary>
    Label PageTitle(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(Theme.UiFont, Theme.TypeTitle),
        ForeColor = Theme.Ink,
        Margin = new Padding(0),
    };

    /// <summary>The bottom bar's buttons (Help, Reset, Close), which SyncToMonitor re-measures after a scale.</summary>
    IEnumerable<Button> BottomButtons() =>
        Controls.OfType<FlowLayoutPanel>().Where(p => p.Dock == DockStyle.Bottom).SelectMany(p => p.Controls.OfType<Button>());

    /// <summary>The heading above a card, e.g. "Layout" or "Design". Same point-size rule as <see cref="PageTitle"/>.</summary>
    Label SectionHeading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(Theme.UiFont, Theme.TypeSubtitle),
        ForeColor = Theme.Ink,
        Margin = new Padding(0),
    };

    /// <summary>
    /// True when the control sits inside a card. Skin() uses it to paint such panels Card, so the
    /// card's own fill shows through them instead of a Canvas-coloured patch.
    /// </summary>
    static bool InCard(Control c)
    {
        for (Control? p = c.Parent; p is not null; p = p.Parent)
            if (p is Theme.CardPanel) return true;
        return false;
    }

    NumericUpDown Num(int lo, int hi, int val) =>
        new Theme.ThemedSpinner { Minimum = lo, Maximum = hi, Value = Clamp(val, lo, hi), Width = (int)(80 * _k) };

    /// <summary>A 0-100 slider with its own readout: a fade is a thing you drag, not type.</summary>
    Theme.Slider Slider(int val, out Label readout, string caption = "")
    {
        var bar = new Theme.Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = Clamp(val, 0, 100),
            Width = (int)(150 * _k),
        };
        var pct = Val(bar.Value, caption.Length == 0 ? null : caption);
        Watch(bar, () => { pct.Text = Pct(bar.Value) + (caption.Length == 0 ? "" : " " + caption); Live(); });
        // The readout is top-aligned in its flow, so it sat above the thumb's centre line. Centre it on the bar
        // whenever the bar is resized (a DPI change resizes it).
        _readouts.Add((bar, pct));
        bar.SizeChanged += (_, _) => CentreReadout(bar, pct);
        readout = pct;
        return bar;
    }

    /// <summary>Slider readouts and their bars, kept so SyncToMonitor can re-centre them after a scale.</summary>
    readonly List<(Theme.Slider Bar, Label Readout)> _readouts = new();

    /// <summary>
    /// Sets the readout's top margin so its text centre lands on the bar's centre line. A FlowLayoutPanel top-aligns
    /// its children, so the margin is the only lever. Only the top margin changes.
    /// </summary>
    static void CentreReadout(Theme.Slider bar, Label readout)
    {
        int top = Math.Max(0, (bar.Height - readout.PreferredHeight) / 2);
        if (readout.Margin.Top != top)
            readout.Margin = new Padding(readout.Margin.Left, top, readout.Margin.Right, readout.Margin.Bottom);
    }

    TextBox Field(string s) => new() { Text = s, Width = (int)(170 * _k) };

    ComboBox Combo(string selected, params string[] items)
    {
        var c = new Theme.ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = (int)(110 * _k) };
        c.Items.AddRange(items.Cast<object>().ToArray());
        c.Text = items.Contains(selected, StringComparer.OrdinalIgnoreCase) ? selected : items[0];
        return c;
    }


    FlowLayoutPanel Flow(params Control[] items)
    {
        // Wrap, never clip: a no-wrap panel silently runs off the right edge of the tab.
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0) };
        f.Controls.AddRange(items);
        return f;
    }

    /// <summary>
    /// Two short controls side by side. A wrapping FlowLayoutPanel mis-measures its own height
    /// when it has to break a line, which squashed these rows; two columns do not wrap.
    /// </summary>
    TableLayoutPanel Pair(Control a, Control b)
    {
        var t = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (Control c in new[] { a, b }) { c.Margin = new Padding((int)(3 * _k), 3, (int)(10 * _k), 3); t.Controls.Add(c, t.Controls.Count, 0); }
        return t;
    }

    /// <summary>
    /// Two controls one above the other. Used where a side-by-side pair would be wider than the card:
    /// the Position row has an X track with three preset buttons, and a Y track beside it pushed the
    /// Y readout ("100%") past the card's edge at every window width. Stacked, each track keeps the
    /// card's full width and the same margins as <see cref="Pair"/>.
    /// </summary>
    TableLayoutPanel Stacked(Control a, Control b)
    {
        var t = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        foreach (Control c in new[] { a, b }) { c.Margin = new Padding((int)(3 * _k), 3, (int)(10 * _k), 3); t.Controls.Add(c, 0, t.Controls.Count); }
        return t;
    }

    static FlowLayoutPanel Track(Theme.Slider bar, Label val, params Control[] extra)
    {
        var p = new FlowLayoutPanel { WrapContents = false, AutoSize = true, Margin = new Padding(0) };
        p.Controls.Add(bar);
        p.Controls.Add(val);
        p.Controls.AddRange(extra);
        return p;
    }

    /// <summary>
    /// A small button that snaps the X slider to <paramref name="percent"/>. Setting the slider
    /// (not the config) is the point: the slider's ValueChanged is what refreshes the readout and
    /// pushes the change to the live overlay, so a preset behaves exactly like a drag. Themed by
    /// Skin() like every other button, since it is added before Skin() runs.
    /// </summary>
    Button PresetButton(string text, int percent)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(S(4), 0, 0, 0),
            AccessibleName = $"Move the overlay to {text.ToLowerInvariant()}",
        };
        b.Click += (_, _) => _x.Value = percent;
        return b;
    }

    /// <summary>
    /// Two labelled slider-and-value pairs in one row, in two columns that do not wrap. Text fade
    /// and background fade are the same control twice, so showing them on separate rows made one
    /// look like a leftover from the other.
    /// </summary>
    TableLayoutPanel Track2(Theme.Slider a, Label av, Theme.Slider b, Label bv)
    {
        var t = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int i = 0; i < 2; i++)
        {
            var cell = new FlowLayoutPanel { WrapContents = false, AutoSize = true, Margin = new Padding((int)(6 * _k), 0, (int)(10 * _k), 0) };
            cell.Controls.Add(i == 0 ? a : b);
            cell.Controls.Add(i == 0 ? av : bv);
            t.Controls.Add(cell, i, 0);
        }
        return t;
    }

    /// <summary>
    /// A swatch that shows a colour without opening a picker. The hot colour comes from three
    /// presets, so offering a colour dialog for it would be a way to make the warning unreadable.
    /// </summary>
    /// <summary>
    /// A swatch button: dark like every other button on the form, with the colour it stands for
    /// shown as a small chip on its left. The chip is an Image, so the button's own BackColor stays
    /// the theme's Card colour. Before, the fill WAS the colour, which put the light default text
    /// colour (#ECF0F5) as a bright block on the dark window. Flat style with a Line border; hover
    /// and press keep the dark fill too.
    /// </summary>
    void SetSwatch(Button b, Color c)
    {
        b.BackColor = Theme.Card;
        b.ForeColor = Theme.Ink;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Theme.Line;
        b.FlatAppearance.MouseOverBackColor = Theme.Hover;
        b.FlatAppearance.MouseDownBackColor = Theme.AccentSoft;
        b.UseVisualStyleBackColor = false;
        b.Cursor = Cursors.Hand;
        // Explicit width, not AutoSize: AutoSize ignored the chip and clipped the first letter of
        // the label. Width = left pad + chip + gap + text + right pad; height kept from the preferred size.
        var pref = b.GetPreferredSize(Size.Empty);
        int textW = TextRenderer.MeasureText(b.Text, b.Font).Width;
        b.AutoSize = false;
        b.Size = new Size(S(6) + S(14) + S(8) + S(6) + textW + S(12), pref.Height);
        b.ImageAlign = ContentAlignment.MiddleLeft;
        b.TextAlign = ContentAlignment.MiddleLeft;
        b.TextImageRelation = TextImageRelation.ImageBeforeText;
        b.Padding = new Padding(S(6), 0, 0, 0);
        var old = b.Image;
        b.Image = Chip(c);
        old?.Dispose();   // the button owns its Image, so the chip being replaced is released here
    }

    /// <summary>
    /// A small square of colour with a thin outline, sized to the DPI-scaled button text. The bitmap
    /// is S(8) wider than the square: the extra strip on the right stays transparent and gives the
    /// gap between chip and text. Only the square is filled, so the transparent strip is not painted.
    /// </summary>
    Bitmap Chip(Color c)
    {
        int size = S(14);
        var bmp = new Bitmap(size + S(8), size);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        using (var fill = new SolidBrush(c))
            g.FillRectangle(fill, 0, 0, size, size);
        using var pen = new Pen(Theme.Line);
        g.DrawRectangle(pen, 0, 0, size - 1, size - 1);
        return bmp;
    }

    Button PeekSwatch(string name, Func<Color> get)
    {
        var b = new Button
        {
            Text = name,
            AutoSize = true,
            Tag = "swatch",
        };
        SetSwatch(b, get());
        return b;
    }

    Button Swatch(string name, Func<Color> get, Action<Color> set)
    {
        var b = new Button
        {
            Text = name,
            AutoSize = true,
            Tag = "swatch",          // Skin() leaves these alone: the fill is the value
        };
        SetSwatch(b, get());
        b.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { Color = get(), FullOpen = true };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            set(dlg.Color);
            SetSwatch(b, dlg.Color);
            Live();
        };
        _designSwatches[name] = b;
        return b;
    }

    /// <summary>Scales a spacing constant by the monitor's DPI.</summary>
    int S(int v) => (int)(v * _k);


    static IEnumerable<Control> All(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var n in All(c)) yield return n;
        }
    }

    /// <summary>Running processes that own a visible window: the only sensible pin targets.</summary>
    static List<string> Candidates()
    {
        var names = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero && !names.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase))
                    names.Add(p.ProcessName);
            }
            catch { /* exited or access denied */ }
            finally { p.Dispose(); }
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    static int Clamp(int v, int lo, int hi) => Math.Clamp(v, lo, hi);
    static int Clamp(double v, int lo, int hi) => (int)Math.Clamp(v, lo, hi);

    // Instance, not static: the margin is DPI-scaled through S(), which reads _k.
    Label Val(double v, string? suffix = null) =>
        new() { Text = suffix is null ? Pct(v) : $"{v:0} {suffix}", AutoSize = true, Margin = new Padding(S(6), S(7), 0, 0) };

    static string Pct(double v) => $"{v:0}%";

    /// <summary>
    /// Land any deferred config write. Live() defers the disk write to the timer, so a form closed
    /// through the window's X instead of the Close button would otherwise drop the last edit - the
    /// button handler is not the only way out of this window.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        FlushSave();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        // Before _live is disposed: anything still pending has to reach disk, including when the
        // window is disposed without going through FormClosing.
        if (disposing) FlushSave();
        if (disposing) { _live.Dispose(); _tip.Dispose(); }
        // base first, and _marks after: the metrics ListView holds _marks as its SmallImageList,
        // and destroying a ListView whose image list is already gone throws out of OnHandleDestroyed.
        base.Dispose(disposing);
        if (disposing) _marks.Dispose();
        // The ListView font this window made is not disposed by base; release it once the ListView is gone.
        if (disposing) _listFont?.Dispose();
    }

    /// <summary>Plain readable window: what every option does, grouped the way the tabs are.</summary>
    sealed class HelpWindow : Form
    {
        /// <param name="k">The owning window's DPI factor, used to scale the window size and button bar.</param>
        public HelpWindow(float k)
        {
            Text = "What the options do";
            Icon = Program.LoadIcon(32);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            BackColor = Theme.Canvas;
            ForeColor = Theme.Ink;
            ClientSize = new Size((int)(640 * k), (int)(620 * k));
            var text = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                Font = new Font(Theme.UiFont, Theme.TypeBody),
                BackColor = Theme.Card,
                ForeColor = Theme.Ink,
                Text = Body(),
            };
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = (int)(44 * k), Padding = new Padding((int)(8 * k)), FlowDirection = FlowDirection.RightToLeft };
            bar.Controls.Add(close);
            Controls.Add(text);
            Controls.Add(bar);
            AcceptButton = close;
        }

        // Written against the tabs as they are. The previous text described a pixel nudge, a scale
        // control, anchors, layout presets, font-size presets and upscaler rows - all removed - and
        // it told the user the clock format accepts any .NET string when the control is a closed
        // list. Help that is wrong is worse than no help: it sends people looking for controls that
        // are not there.
        static string Body() => string.Join("\r\n\r\n",
        [
            "LAYOUT",
            "Position - where the overlay sits, as a percentage of the pinned window's monitor. X runs left to right; Y runs bottom to top, so (0,0) is the bottom-left corner. The overlay moves as you drag, and it is held on screen at every value.",
            "Spacing - blank pixels between columns and rows.",
            "Shape - one row per metric, or everything on one line.",
            "Columns - in one-line mode: 0 keeps a single long line, 2 or more lays the rows out as a grid of that many columns.",
            "",
            "ROWS",
            "Groups - untick one and its rows leave the table and the overlay. Nothing is deleted; ticking it back restores them.",
            "Show in overlay - click that column to tick or untick a row. A double click toggles from anywhere.",
            "",
            "TOKENS",
            "Placeholders a format can use: " + Composer.TokenHelp + ". Tags: <R> starts the value column so numbers line up, <C=RRGGBB> colours from there on, <S=120> changes the size, <G=name> puts several rows on one line. Per-row formats are set in config.json.",
            "",
            "DESIGN",
            "Font, text size - the typeface and point size every row is drawn in.",
            "Text colour - the default colour of the numbers.",
            "Draw the panel - a rounded background behind the text. Off is cleaner over a game.",
            "Fades - the left slider fades the text, the right fades the panel, independently.",
            "Graph colour, graph size - the frametime graph's line colour and its width and height in pixels.",
            "Hot colour - green, orange or red. A row turns that colour when it crosses a threshold below.",
            "Hot thresholds - a CPU or GPU temperature, and a load percentage, at or above which a row goes hot.",
            "",
            "DATA",
            "Pin to window - the overlay belongs to that window and follows its monitor. Follow the focused window chases whatever you alt-tab to.",
            "Sample every - how often the counters are read. Sensors cannot go much below 250 ms without costing the game frames.",
            "Ping host - a host to ICMP ping for {ping}. Empty switches that row off.",
            "",
            "SETTINGS",
            "Show / hide, Quit - global hotkeys. Click a box and press the combination; it takes effect immediately.",
            "Window - whether closing this window keeps the overlay running in the tray, and whether it starts with the window hidden.",
            "",
            "ABOUT",
            "Version, the keyboard shortcuts and the author.",
        ]);
    }

    /// <summary>
    /// A hotkey field you type into with the keyboard rather than with the mouse. Click it, press
    /// the combination, done - which is the only way anyone actually remembers a shortcut.
    ///
    /// It writes the same "Ctrl+Alt+O" syntax <c>OverlayForm.ParseHotkey</c> reads, so there is one
    /// format, not two. A combination with no modifier is refused: RegisterHotKey accepts one, but
    /// a bare key is swallowed by every window you type in, and a hotkey you cannot type with is
    /// worse than no hotkey.
    /// </summary>
    sealed class HotkeyBox : TextBox
    {
        static readonly string[] ModNames = ["Ctrl", "Alt", "Shift", "Win"];

        public HotkeyBox(string spec) => Text = spec;

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (msg.Msg != 0x100) return base.ProcessCmdKey(ref msg, keyData);   // WM_KEYDOWN only

            var keys = (Keys)keyData;
            var mods = new List<string>();
            if (keys.HasFlag(Keys.Control)) mods.Add("Ctrl");
            if (keys.HasFlag(Keys.Alt)) mods.Add("Alt");
            if (keys.HasFlag(Keys.Shift)) mods.Add("Shift");
            if (keys.HasFlag(Keys.LWin) || keys.HasFlag(Keys.RWin)) mods.Add("Win");

            // Bare modifier: keep waiting, but stop the caret walking backwards over the text.
            if (mods.Count == 0 && keys is Keys.ControlKey or Keys.Menu or Keys.ShiftKey
                or Keys.LWin or Keys.RWin)
                return true;

            var bare = keys & ~Keys.Control & ~Keys.Alt & ~Keys.Shift & ~Keys.LWin & ~Keys.RWin;
            if (mods.Count == 0) return true;                 // no modifier: refuse, do not steal

            var spec = string.Join('+', mods.Concat(new[] { KeyName(bare) }));
            if (spec.EndsWith("+None", StringComparison.Ordinal)) return true;

            // No event to raise: setting Text is enough. The text box is in the walk that wires
            // Changed, so this runs Live() -> apply -> OverlayForm.Adopt -> RegisterKeys.
            Text = spec;
            SelectAll();
            return true;
        }

        /// <summary>One printable name for a key, so "O" not "75" and "F1" not "112".</summary>
        static string KeyName(Keys k)
        {
            if (k >= Keys.D0 && k <= Keys.D9) return ((char)('0' + (k - Keys.D0))).ToString();
            if (k >= Keys.A && k <= Keys.Z) return ((char)('A' + (k - Keys.A))).ToString();
            if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return "Num" + (char)('0' + (k - Keys.NumPad0));
            if (k is Keys.Oemplus or Keys.Add) return "+";
            if (k is Keys.OemMinus or Keys.Subtract) return "-";
            return k.ToString();
        }
    }

    /// <summary>
    /// A Label whose wrapped lines are spaced at 1.35 x the font height instead of GDI's tight default.
    ///
    /// Why: a plain Label with AutoSize and MaximumSize does wrap, but GDI sets the wrapped lines
    /// almost on top of each other, so the two-line About text looked cramped. This class does its own
    /// wrapping so the line pitch is ours to set. It is still a Label: AutoSize, MaximumSize, Margin,
    /// Padding, Font, ForeColor and BackColor work as they do on a Label. The visible change is the line pitch,
    /// and so the height of a multi-line label.
    ///
    /// Wrapping is greedy by words, measured with TextRenderer in the label's own font. Explicit '\n'
    /// breaks are kept. A single word wider than the width is not split; it stays on its own line and
    /// runs past the edge, as a Label does with a word too long to wrap.
    /// </summary>
    sealed class SpacedLabel : Label
    {
        /// <summary>Line pitch as a multiple of the font height. GDI's own pitch is about 1.0.</summary>
        const float LinePitch = 1.35f;

        // The same flags are used to measure and to draw, so a line measures exactly as it paints.
        // Prefixes are processed only when UseMnemonic is on, which is also what Label does.
        TextFormatFlags Flags => TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine
            | TextFormatFlags.NoClipping | (UseMnemonic ? TextFormatFlags.Default : TextFormatFlags.NoPrefix);

        /// <summary>Distance from one line's top to the next, in pixels.</summary>
        int Pitch => (int)Math.Round(Font.Height * LinePitch);

        int MeasureWidth(string line) =>
            TextRenderer.MeasureText(line, Font, new Size(int.MaxValue, int.MaxValue), Flags).Width;

        /// <summary>Splits Text into display lines, none wider than maxWidth unless one word is wider on its own.</summary>
        string[] WrapLines(int maxWidth)
        {
            var result = new List<string>();
            // Explicit breaks first. A trailing '\r' from a "\r\n" break is dropped.
            foreach (var paragraph in Text.Split('\n'))
            {
                // Split on single spaces, not whitespace runs, so joining the words gives back the text exactly.
                var words = paragraph.TrimEnd('\r').Split(' ');
                var current = words[0];
                for (int i = 1; i < words.Length; i++)
                {
                    var candidate = current + " " + words[i];
                    if (MeasureWidth(candidate) <= maxWidth) current = candidate;
                    else
                    {
                        result.Add(current);
                        current = words[i];
                    }
                }
                result.Add(current);
            }
            return result.ToArray();
        }

        /// <summary>
        /// The width and height the label wants. The wrap width is the narrower of what the layout offers
        /// and MaximumSize, less the padding; without the MaximumSize cap the lines would not wrap and the
        /// height would be one line too short for the width the label was clamped to.
        /// </summary>
        public override Size GetPreferredSize(Size proposedSize)
        {
            int wrap = int.MaxValue;
            if (proposedSize.Width > 0) wrap = proposedSize.Width;
            if (MaximumSize.Width > 0) wrap = Math.Min(wrap, MaximumSize.Width);
            if (wrap != int.MaxValue) wrap = Math.Max(1, wrap - Padding.Horizontal);

            var lines = WrapLines(wrap);
            int width = lines.Select(MeasureWidth).Max();
            return new Size(width + Padding.Horizontal, lines.Length * Pitch + Padding.Vertical);
        }

        /// <summary>
        /// Paints the background the way Label does, then each line at the pitch, in ForeColor. The wrap
        /// width is the client width, which AutoSize sets to the preferred width, so the lines match
        /// the ones GetPreferredSize measured.
        /// </summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaintBackground(e);

            int inner = Math.Max(1, ClientSize.Width - Padding.Horizontal);
            int pitch = Pitch;
            var lines = WrapLines(inner);
            for (int i = 0; i < lines.Length; i++)
            {
                var rect = new Rectangle(Padding.Left, Padding.Top + i * pitch, inner, pitch);
                TextRenderer.DrawText(e.Graphics, lines[i], Font, rect, ForeColor, Flags);
            }
        }
    }

    HotkeyBox HotkeyField(string spec) => new(spec);
}
