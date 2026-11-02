using System.Reflection;
using System.Runtime.InteropServices;

namespace UiProbe;

/// <summary>
/// One check per bug that was fixed, written so it FAILS on the old code.
///
/// Each of these was found by reading, not by a red test, so the value here is the same as the
/// slider drag check: a fix with no failing case behind it is a claim, and the next person to
/// refactor has nothing to break.
/// </summary>
static class Fixes
{
    public static void Run()
    {
        int bad = 0;
        bad += CustomRowSurvivesLoad();
        bad += ForeignProcessFramesRejected();
        bad += MonitorNameIsRead();
        bad += Clock24ToggleApplies();
        bad += ConfigSurvivesNulls();
        bad += CopyFromDoesNotShare();
        bad += TimeRepairSparesQuotedText();
        bad += OneIconEverywhere();

        Console.WriteLine();
        Console.WriteLine(bad == 0 ? "ALL FIX CHECKS PASS" : $"{bad} CHECK(S) FAILED");
        Environment.ExitCode = bad;
    }

    static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {name,-42} {detail}");
    }

    /// <summary>
    /// A custom row must survive a load regardless of where it sits in Lines.
    ///
    /// Old code: c.Lines.Where(Composer.IsRow) bound to the indexed Where overload, so IsRow got the
    /// element's index as customCount and a custom row was dropped whenever its position was below
    /// its own number. With one custom row at index 1, "custom0" was silently deleted - and the
    /// settings window then saved the filtered list, making it permanent.
    /// </summary>
    static int CustomRowSurvivesLoad()
    {
        string path = FrameGlass.Config.ConfigPath;
        string backup = File.Exists(path) ? File.ReadAllText(path) : "";
        try
        {
            // Custom ids are 1-based ("custom1"), and the row must be FIRST here: the old code
            // passed the element index as customCount, so custom1 at index 0 failed 1 <= 0. Placed
            // second it would have passed by luck, which is why this checks the first slot.
            File.WriteAllText(path,
                """{ "Lines": ["custom1", "fps"], "CustomRows": ["my row"] }""");
            var c = FrameGlass.Config.Load();
            bool kept = c.Lines.Contains("custom1");
            Check("custom row survives load", kept, $"Lines = [{string.Join(", ", c.Lines)}]");
            return kept ? 0 : 1;
        }
        finally { if (backup.Length > 0) File.WriteAllText(path, backup); }
    }

    /// <summary>
    /// Frames from a process that is not the tracked one must not enter the ring.
    ///
    /// Push is private, so it is invoked reflectively - the alternative is a real ETW session with a
    /// second process rendering, which is not something a test should need.
    /// </summary>
    static int ForeignProcessFramesRejected()
    {
        using var ft = new FrameGlass.FrameTimes();
        var push = typeof(FrameGlass.FrameTimes).GetMethod("Push", BindingFlags.Instance | BindingFlags.NonPublic);
        if (push is null) { Check("foreign frames rejected", false, "Push not found"); return 1; }

        ft.SetTarget(4242);                       // track one pid
        long t = 1_000_000;
        for (int i = 0; i < 40; i++) push.Invoke(ft, [9999, t += 16_666]);   // a different one presents

        var stats = ft.Snapshot(10);
        bool empty = stats.Fps <= 0;
        Check("foreign frames rejected", empty,
              empty ? "no FPS recorded from an untracked pid" : $"FPS from another pid = {stats.Fps:0.0}");
        return empty ? 0 : 1;
    }

    /// <summary>
    /// {display} must print the monitor's name. The EDID tag was read from the wrong offset and the
    /// text decoded as UTF-16, so it was empty on every machine.
    /// </summary>
    static int MonitorNameIsRead()
    {
        var info = FrameGlass.Displays.Describe(IntPtr.Zero, full: false);
        var full = FrameGlass.Displays.Describe(IntPtr.Zero, full: true);
        // The name comes first in the short form, so a leading digit means the mode string is all
        // there is - which is what "no name found" looks like. Asserting only "non-empty" passed on
        // the broken code, because the mode alone is non-empty.
        bool ok = info.Length > 0 && !char.IsDigit(info.TrimStart()[0]);
        Check("monitor name from EDID", ok, $"{{display}} = \"{info}\"  (full: \"{full}\")");
        return ok ? 0 : 1;
    }

    /// <summary>The 12/24-hour switch must change what {time} prints.</summary>
    static int Clock24ToggleApplies()
    {
        var cfg = new FrameGlass.Config { Lines = ["time"], TimeFormat = "hh:mm tt", Clock24Hour = true };
        string h24 = FirstRow(cfg);
        cfg.Clock24Hour = false;
        string h12 = FirstRow(cfg);
        bool differs = h24 != h12;
        Check("12/24-hour switch applies", differs, $"{h24} vs {h12}");
        return differs ? 0 : 1;
    }

    static string FirstRow(FrameGlass.Config cfg) =>
        FrameGlass.Composer.Rows(cfg, FrameGlass.FrameStats.Empty, new FrameGlass.Reading())[0].Text;

    /// <summary>A hand-edited file with explicit nulls must not take the app down.</summary>
    static int ConfigSurvivesNulls()
    {
        string path = FrameGlass.Config.ConfigPath;
        string backup = File.Exists(path) ? File.ReadAllText(path) : "";
        int bad = 0;
        try
        {
            File.WriteAllText(path,
                """{ "Lines": null, "Formats": null, "CustomRows": null, "RowColors": null, "PingAddr": null, "FontSize": 0 }""");
            var c = FrameGlass.Config.Load();
            Check("null lines/collections survive", c.Lines.Length > 0, $"Lines = {c.Lines.Length}, Format = {c.TimeFormat}");
            Check("null PingAddr normalised", c.PingAddr is not null, $"PingAddr = \"{c.PingAddr}\"");
            Check("FontSize 0 clamped", c.FontSize >= 6, $"FontSize = {c.FontSize}");
            bad += c.Lines.Length > 0 && c.PingAddr is not null && c.FontSize >= 6 ? 0 : 1;

            // And building the rows must not throw on the same config: this is the path that ran
            // every tick from the render loop, where a throw left the overlay unpainted.
            var rows = FrameGlass.Composer.Rows(c, FrameGlass.FrameStats.Empty, new FrameGlass.Reading());
            Check("rows build with loaded config", rows.Count > 0, $"{rows.Count} rows");
        }
        catch (Exception e)
        {
            Check("null config handling", false, e.GetType().Name + ": " + e.Message);
            bad++;
        }
        finally { if (backup.Length > 0) File.WriteAllText(path, backup); }
        return bad;
    }

    /// <summary>
    /// The "a" repair must fix the meridiem and leave quoted literals alone.
    ///
    /// Old code chained Replace("a", "tt") over the whole string, so "'at' HH:mm" became
    /// "'ttt' HH:mm" - and the mangled value was saved back to disk.
    /// </summary>
    static int TimeRepairSparesQuotedText()
    {
        string quoted = FrameGlass.Config.FixTimeFormat("'at' HH:mm");
        string fixedA = FrameGlass.Config.FixTimeFormat("h:mm a");
        bool ok = quoted.Contains("'at'") && fixedA.Contains("tt") && !fixedA.Contains(" a");
        Check("time repair spares quoted text", ok, $"\"'at' HH:mm\" -> \"{quoted}\",  \"h:mm a\" -> \"{fixedA}\"");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// Every surface must get the same image.
    ///
    /// There is one icon, embedded in the assembly, and everything asks for it at the size it needs.
    /// The check is that the resource is actually there and that asking for a size gives that size -
    /// if the embed is dropped from the csproj the loader silently falls back to the stock
    /// application icon, which would look fine in a screenshot and be the wrong image everywhere.
    /// </summary>
    static int OneIconEverywhere()
    {
        var stream = typeof(FrameGlass.Program).Assembly
            .GetManifestResourceStream("FrameGlass.app.ico");
        bool embedded = stream is not null;
        stream?.Dispose();
        Check("icon embedded in the assembly", embedded,
              embedded ? "FrameGlass.app.ico found" : "MISSING - falling back to the stock icon");

        int bad = embedded ? 0 : 1;

        // Asking for a size must give that size, not the nearest one Windows scales.
        foreach (int want in new[] { 16, 32, 64 })
        {
            using var icon = FrameGlass.Program.LoadIcon(want);
            bool exact = icon.Width == want;
            Check($"loads at {want}px", exact, $"got {icon.Width}x{icon.Height}");
            if (!exact) bad++;
        }

        // And it must be the right PICTURE, not just the right size.
        //
        // This is the check that was missing. A dimension-only test passed on an icon whose every
        // frame was PNG-compressed: System.Drawing.Icon parses PNG bytes as a DIB header and hands
        // back static, while Windows itself renders PNG frames perfectly - so the file looked right
        // in Explorer and would have been noise in the tray, which is the only place the app reads
        // it. Every frame is an uncompressed DIB now, and this is what keeps it that way.
        using (var icon = FrameGlass.Program.LoadIcon(32))
        using (var bmp = icon.ToBitmap())
        {
            long sum = 0;
            var seen = new HashSet<int>();
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                {
                    var p = bmp.GetPixel(x, y);
                    sum += (p.R + p.G + p.B) / 3;
                    seen.Add(p.ToArgb());
                }
            int mean = (int)(sum / (bmp.Width * bmp.Height));
            var centre = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);

            // The mark is a dark disc (mean well under half) with blue bars through the middle.
            bool plausible = mean < 110 && centre.B > centre.R && centre.B > 120;
            Check("icon pixels are the mark, not noise", plausible,
                  $"mean={mean}, centre={centre.R},{centre.G},{centre.B}, colours={seen.Count}");
            if (!plausible) bad++;
        }

        // And the sizes Windows asks for must be in the file. Missing frames are not an error
        // Windows reports - it upscales the nearest one, so a missing 64 or 128 shows as a soft
        // icon in the Start menu and nothing anywhere says why.
        int[] wanted = [16, 24, 32, 48, 64, 128, 256];
        var present = IconSizes();
        var missing = wanted.Where(s => !present.Contains(s)).ToArray();
        Check("all Windows icon sizes present", missing.Length == 0,
              missing.Length == 0 ? string.Join(", ", present) : "missing " + string.Join(", ", missing));
        return bad + (missing.Length == 0 ? 0 : 1);
    }

    /// <summary>The frame sizes inside the embedded app.ico, read from the ICO directory.</summary>
    static List<int> IconSizes()
    {
        var sizes = new List<int>();
        using var s = typeof(FrameGlass.Program).Assembly
            .GetManifestResourceStream("FrameGlass.app.ico");
        if (s is null) return sizes;

        using var br = new BinaryReader(s);
        br.ReadUInt16(); br.ReadUInt16();            // reserved, type
        int count = br.ReadUInt16();
        for (int i = 0; i < count; i++)
        {
            int w = br.ReadByte(), h = br.ReadByte();   // 0 means 256
            br.ReadByte();                               // palette count
            br.ReadByte();                               // reserved
            br.ReadUInt16(); br.ReadUInt16();            // planes, bit depth
            br.ReadUInt32(); br.ReadUInt32();            // size, offset
            sizes.Add(w == 0 ? 256 : w);
        }
        return sizes;
    }

    /// <summary>Applying one config to another must not leave them sharing a collection.</summary>
    static int CopyFromDoesNotShare()
    {
        var a = new FrameGlass.Config { HiddenGroups = ["Disk"] };
        var b = new FrameGlass.Config().CopyFrom(a);
        b.HiddenGroups[0] = "Network";          // edit the copy
        bool independent = a.HiddenGroups[0] == "Disk";
        Check("CopyFrom does not share arrays", independent, $"original = {a.HiddenGroups[0]}, copy = {b.HiddenGroups[0]}");
        return independent ? 0 : 1;
    }
}
