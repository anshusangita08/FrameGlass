using System.Reflection;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;

namespace FrameGlass;

sealed class Config
{
    // where. X runs left(0) to right(100) across the work area, Y runs BOTTOM(0) to TOP(100):
    // a graph-style origin, so (0,0) is the bottom-left corner. There are no anchors and no pixel
    // nudge - the percentages are the only position controls, and they are gated so the overlay can
    // never leave the screen.
    public double X { get; set; } = 0;
    public double Y { get; set; } = 100;
    /// <summary>Which adapter to read, or empty for the busiest one. "all" sums every adapter.</summary>
    public string GpuMatch { get; set; } = "";

    // shape
    public string Layout { get; set; } = "stack";   // stack | line
    public int Columns { get; set; }               // one-line mode: 0 = one long line, 2+ = grid of N columns
    public string[] HiddenGroups { get; set; } = [];  // section headers switched off, e.g. Network
    public bool Box { get; set; }                 // a panel behind the text; off = fully transparent
    public double PanelOpacity { get; set; } = 1;   // background fade: 0 = invisible, 1 = solid
    public int Gap { get; set; } = 10;
    public int GraphWidth { get; set; } = 110;
    public int GraphHeight { get; set; } = 46;

    // look
    public string Font { get; set; } = "Consolas";
    public double FontSize { get; set; } = 13;
    public double Opacity { get; set; } = 1;      // text fade: 0 = invisible, 1 = solid
    public double HotTempC { get; set; } = 85;
    public double HotLoadPct { get; set; } = 90;    // load over this turns a row hot too
    /// <summary>One of green / orange / red. No custom picker.</summary>
    public string HotColorName { get; set; } = "orange";

    // data
    public int IntervalMs { get; set; } = 500;
    public string TimeFormat { get; set; } = "HH:mm";   // .NET date format; "tt" is AM/PM, not "a"
    /// <summary>Clock shows a 12-hour face when true, overriding the AM/PM tokens in TimeFormat.</summary>
    public bool Clock24Hour { get; set; } = true;

    /// <summary>
    /// Rewrites a lone, unquoted "a" to "tt", the AM/PM specifier. "a" is not a .NET specifier,
    /// so left alone it prints a literal "a" instead of the meridiem.
    ///
    /// Only an unquoted, standalone "a" is rewritten. Quoted text is left alone: a .NET format uses
    /// quotes to print text literally, so "'at' HH:mm" must come through unchanged.
    /// </summary>
    public static string FixTimeFormat(string f)
    {
        if (string.IsNullOrWhiteSpace(f)) return "HH:mm";

        var sb = new System.Text.StringBuilder(f.Length + 2);
        bool quoted = false;
        for (int i = 0; i < f.Length; i++)
        {
            char ch = f[i];
            if (ch == '\'') { quoted = !quoted; sb.Append(ch); continue; }

            bool lone = ch is 'a' or 'A'
                        && (i == 0 || f[i - 1] is not ('a' or 'A'))
                        && (i + 1 >= f.Length || f[i + 1] is not ('a' or 'A'));
            if (!quoted && lone) sb.Append("tt");
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The clock format actually used to render, with the 12/24 toggle applied on top. The stored
    /// <see cref="TimeFormat"/> is kept as typed; this is what the renderer sees, so flipping the
    /// toggle never rewrites the user's chosen format.
    /// </summary>
    public string EffectiveTimeFormat() => Clock24Hour
        ? TimeFormat.Replace("hh", "HH").Replace("h", "H").Replace(" tt", "").Replace("tt", "").Trim()
        : To12Hour(TimeFormat);

    static string To12Hour(string f)
    {
        var s = f;
        if (!s.Contains("h", StringComparison.Ordinal)) return s;   // nothing 12-hour-ish to convert
        s = s.Replace("HH", "hh").Replace("H", "h");
        if (!s.Contains("tt", StringComparison.Ordinal)) s = s.TrimEnd() + " tt";
        return s;
    }
    public string RamUnit { get; set; } = "GB";     // GB | MB
    public string PingAddr { get; set; } = "";     // host to ICMP ping for {ping}; empty disables
    // Default layout. The 1%-low, upscaler and frame-generation rows were removed: nothing can
    // detect them, so they were only ever labels you typed at yourself.
    public string[] Lines { get; set; } =
    [
        "game", "res", "fps", "frametime", "graph",
        "cpu", "cputemp", "cpuload", "cpupower",
        "gpu", "gputemp", "gpuload", "gpupower",
        "ram", "vram",
    ];
    public bool TrackForeground { get; set; } = true;

    /// <summary>Process name to pin to. Empty = follow whatever is focused.</summary>
    public string TargetProcess { get; set; } = "";

    // per-row format templates: {fps}, {cputemp}, &lt;C=FF0000&gt;, see Composer.TokenHelp
    // Clustering is automatic (cpu* / gpu* / net* / disk* share a line), so the defaults only need
    // the model name opening the CPU and GPU lines.
    public Dictionary<string, string> Formats { get; set; } = new()
    {
        ["cpu"] = "{cpus}",
        ["gpu"] = "{gpu}",
    };

    /// <summary>Per-row colour, keyed by row id. An inline &lt;C=..&gt; tag in a template also works.</summary>
    public Dictionary<string, string> RowColors { get; set; } = new();

    /// <summary>
    /// CPU model shown by {cpuname}, {cpus} and {cpufull} in place of the detected name. Empty means
    /// the detected name. A config file without this key gets the empty default, so it shows the detected name.
    /// Trimmed in <see cref="Normalise"/>, so a stray space never reaches the overlay.
    /// </summary>
    public string CpuNameOverride { get; set; } = "";

    /// <summary>
    /// GPU model shown by {gpu} and {gpufull} in place of the detected name. Empty means the detected
    /// name. Same rules as <see cref="CpuNameOverride"/>.
    /// </summary>
    public string GpuNameOverride { get; set; } = "";

    // Rows-tab layout the user has rearranged: section order, then row order. Empty = the built-in
    // order, so a fresh config stays empty and keeps tracking the defaults.
    public string SectionOrder { get; set; } = "";
    public string RowOrder { get; set; } = "";

    /// <summary>Rows the user wrote from scratch. Each is a format string, ticked like any other.</summary>
    public List<string> CustomRows { get; set; } = new();

    // colours, "#RRGGBB". The hot colour is not one of these - it is chosen from three presets.
    public string BgColor { get; set; } = "#0E1013";      // panel colour
    public string TextColor { get; set; } = "#ECF0F5";
    public string AccentColor { get; set; } = "#76C4FF";   // graph

    /// <summary>The three hot colours, in the order they are offered.</summary>
    public static readonly (string Name, string Hex)[] HotPresets =
    [
        ("green", "#3CCF8E"),
        ("orange", "#FF943C"),
        ("red", "#FF5A5A"),
    ];

    /// <summary>Resolves <see cref="HotColorName"/>, falling back to orange for a bad value.</summary>
    public string ResolveHotColor() =>
        HotPresets.FirstOrDefault(p => p.Name == HotColorName).Hex is { Length: > 0 } hex
            ? hex
            : "#FF943C";

    // keys
    public string ToggleKey { get; set; } = "Ctrl+Alt+O";
    public string QuitKey { get; set; } = "Ctrl+Alt+Q";

    // window behaviour
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }

    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keep "+" / "Ctrl+Alt+O" readable in the file
    };

    // ConfigPath, not Path: a member called Path hides System.IO.Path inside this class, and the
    // original name then had to be written out in full on every line below. Not File either, for
    // the same reason - that would hide System.IO.File.
    public static string ConfigPath { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "config.json");

    /// <summary>Collections copied by reference in <see cref="CopyFrom"/>, so they need a real clone.</summary>
    static readonly HashSet<string> Deep = ["Lines", "Formats", "RowColors", "CustomRows", "HiddenGroups"];

    /// <summary>
    /// Overwrites this config with every value from <paramref name="c"/>.
    /// Reflection, not a hand-written field list, which is easy to leave a property out of.
    /// A new property is copied with no change here.
    ///
    /// The collections named in <see cref="Deep"/> are cloned, so the two configs never share one List.
    /// Sharing one would let editing a profile silently change the live config.
    /// </summary>
    public Config CopyFrom(Config c)
    {
        foreach (var p in typeof(Config).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || !p.CanWrite) continue;
            var v = p.GetValue(c);
            if (v is not null && Deep.Contains(p.Name)) v = Clone(v);
            p.SetValue(this, v);
        }
        return this;
    }

    /// <summary>A copy of one of the collection properties, so the two configs stop sharing it.</summary>
    static object Clone(object v) => v switch
    {
        string[] a => a.ToArray(),
        Dictionary<string, string> d => new Dictionary<string, string>(d),
        List<string> l => new List<string>(l),
        _ => v,
    };

    public static Config Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var c = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), Opts);
                if (c is not null)
                {
                    Normalise(c);

                    // A hand-edited file can hold ids or templates this build does not know; keep the
                    // good ones rather than silently rendering an empty overlay.
                    //
                    // The predicate is a lambda, not a method group. "c.Lines.Where(Composer.IsRow)"
                    // binds to the indexed Where(Func<string,int,bool>) overload, which passes the
                    // element's *index* as customCount - so a customN row survived only when its
                    // position in Lines happened to be >= N, and the settings window then saved the
                    // filtered list, making the loss permanent.
                    c.Lines = c.Lines.Where(id => Composer.IsRow(id, c.CustomRows.Count)).ToArray();
                    if (c.Lines.Length == 0) c.Lines = ["fps"];

                    // "a" is not a .NET specifier: it printed a literal "a" instead of AM/PM. Fix it
                    // on disk too, otherwise it sits there broken until the next settings save.
                    // The write is guarded separately: inside the outer try a read-only folder or a
                    // locked file would report a perfectly readable config as unreadable.
                    var fixedTime = FixTimeFormat(c.TimeFormat);
                    if (fixedTime != c.TimeFormat)
                    {
                        c.TimeFormat = fixedTime;
                        try { Save(c); } catch (Exception e) { Program.Log("config repair not saved: " + e.Message); }
                    }
                    return c;
                }
            }
        }
        catch (Exception e)
        {
            MessageBox.Show($"config.json is unreadable, using defaults.\n\n{e.Message}", "FrameGlass");
        }
        return new Config();
    }

    /// <summary>
    /// Fills in nulls a hand-edited file can produce.
    ///
    /// System.Text.Json sets a property to null when the JSON says null, and every collection here
    /// is dereferenced without a check. A null "Lines" would throw while loading and discard the whole
    /// config. A null "RowColors" would throw inside the render path on every tick. The render path
    /// expects a value, so each null is replaced with an empty or default value here, once. That
    /// makes these cases impossible rather than merely unlikely.
    /// </summary>
    static void Normalise(Config c)
    {
        c.Lines ??= [];
        c.Formats ??= new();
        c.CustomRows ??= new();
        c.RowColors ??= new();
        c.HiddenGroups ??= [];
        c.Font ??= "Consolas";
        c.TimeFormat ??= "HH:mm";
        c.PingAddr ??= "";
        c.TargetProcess ??= "";
        c.ToggleKey ??= "";
        c.QuitKey ??= "";
        c.GpuMatch ??= "";
        // Trim only: a null from JSON becomes "", and "" means "use the detected name".
        c.CpuNameOverride = c.CpuNameOverride?.Trim() ?? "";
        c.GpuNameOverride = c.GpuNameOverride?.Trim() ?? "";
        c.RamUnit ??= "GB";
        c.BgColor ??= "#0E1013";
        c.TextColor ??= "#ECF0F5";
        c.AccentColor ??= "#76C4FF";
        c.HotColorName ??= "orange";
        if (c.FontSize < 6) c.FontSize = 13;      // new Font(name, 0) throws, and a disposed font is worse
        if (c.IntervalMs < 100) c.IntervalMs = 500;
    }

    /// <summary>
    /// Writes the config atomically: a temporary file, then a replace.
    ///
    /// A plain WriteAllText truncates first, so a kill, a crash or a power loss partway through
    /// leaves a truncated config.json that the next launch reports unreadable and replaces with
    /// defaults - losing every row, format and position the user had set.
    /// </summary>
    public static void Save(Config c)
    {
        var json = JsonSerializer.Serialize(c, Opts);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, ConfigPath, overwrite: true);
    }

}
