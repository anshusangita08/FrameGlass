using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Text.RegularExpressions;

namespace FrameGlass;

/// <summary>An alignment box from &lt;A=n&gt;: right-align cells between Start and End, or left.</summary>
readonly record struct Box(int Start, int End, int Cells, bool Right);

/// <summary>
/// One rendered row. <paramref name="Text"/> is the left part, <paramref name="Right"/> whatever
/// followed &lt;R&gt;: every Right part in a block is flushed to one shared right edge, which is
/// what makes the values line up in a column instead of trailing off ragged.
/// </summary>
readonly record struct Row(string Text, string Right, Box[] Boxes, bool Hot, bool Graph, Color Tint, float Size, string Group);

/// <summary>Turns readings into the finished overlay image.</summary>
static class Composer
{
    // ---- screen anchoring ----------------------------------------------------------------------

    /// <summary>
    /// Where the overlay's top-left corner lands, in screen coordinates. Pure, so it can be tested
    /// against any monitor arrangement - including a secondary monitor whose origin is not (0,0) -
    /// with no screen attached at all.
    ///
    /// X is the percentage across the working area, left to right. Y is the percentage UP the
    /// working area, so Y=0 is the bottom edge and Y=100 is the top: a graph-style origin, which is
    /// what makes (0,0) the bottom-left corner. There are no anchors and no pixel nudge, so the
    /// two percentages are the only position controls.
    ///
    /// The result is always fully inside <paramref name="work"/>. A box larger than the working
    /// area cannot fit, so it pins to the work area's left rather than hanging off the far edge.
    /// </summary>
    public static (int X, int Y) Anchor(Rectangle work, double xPct, double yPct, int w, int h)
    {
        // The percentage is the position of the box's own travel, not of its left edge: the box
        // moves across (work.Width - w) pixels, so 0% is flush left, 50% centres it, 100% is flush
        // right. Measuring the travel this way keeps both ends flush without relying on the clamp below.
        // Y is bottom-origin, so the same travel is measured from the bottom edge.
        int x = work.Left + (int)Math.Round(Math.Clamp(xPct, 0, 100) / 100.0 * Math.Max(0, work.Width - w));
        int y = work.Top + (int)Math.Round((100 - Math.Clamp(yPct, 0, 100)) / 100.0 * Math.Max(0, work.Height - h));

        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - h));
        return (x, y);
    }

    // ---- layout arithmetic ---------------------------------------------------------------------

    /// <summary>
    /// Where a move of the item at <paramref name="index"/> should land. Pure, so the fiddly
    /// index maths is testable: <paramref name="isSection"/> marks the header rows, and every
    /// header owns the rows below it up to the next header.
    /// </summary>
    public static (int Lo, int Hi, int Target) MovePlan(bool[] isSection, int index, int delta)
    {
        int n = isSection.Length;
        bool Sec(int i) => i >= 0 && i < n && isSection[i];

        int lo = index, hi = index;
        if (Sec(index))
        {
            // A header owns the rows below it, up to the next header. Never walk upwards: those
            // rows belong to the section above.
            while (hi + 1 < n && !Sec(hi + 1)) hi++;
        }
        int size = hi - lo + 1;

        // Targets are computed as if the block were already lifted out, which is what makes
        // "one step down" land on the next item rather than the one after it.
        int target;
        if (delta == int.MinValue) target = 0;
        else if (delta == int.MaxValue) target = n - size;
        else if (delta < 0)
        {
            // A section swaps with the whole block above it; a plain row only moves one place.
            if (Sec(index))
            {
                int header = lo - 1;
                while (header > 0 && !Sec(header)) header--;
                target = header;
            }
            else target = lo - 1;
        }
        else
        {
            int q = hi + 1;
            int next = q >= n ? 0 : Sec(q) ? MovePlan(isSection, q, 0).Hi - q + 1 : 1;
            target = hi + next + 1 - size;
        }
        return (lo, hi, Math.Clamp(target, 0, n - size));
    }

    /// <summary>
    /// The index maths behind the Rows tab's move buttons, checked against the real 53-item list.
    /// Run the executable with --selftest.
    /// </summary>
    public static int SelfTest()
    {
        var secs = new (string Name, int Rows)[]
        {
            ("App", 8), ("Display", 3), ("Frames", 8), ("CPU", 7), ("GPU", 6),
            ("Memory", 4), ("Network", 3), ("Disk", 2), ("Time", 3),
        };
        var flags = new List<bool>();
        var names = new List<string>();
        foreach (var (name, rows) in secs)
        {
            flags.Add(true);
            names.Add("#" + name);
            for (int i = 0; i < rows; i++) { flags.Add(false); names.Add(name + i); }
        }
        var sec = flags.ToArray();
        int Index(string id) => names.IndexOf(id);
        int fail = 0;

        // A row moves exactly one place, up or down.
        int api = Index("App3");
        var (lo, hi, to) = MovePlan(sec, api, 1);
        if (lo != api || hi != api || to != api + 1) { fail++; Program.Log($"FAIL row down: {lo},{hi},{to}"); }
        var (u0, _, uTo) = MovePlan(sec, api, -1);
        if (u0 != api || uTo != api - 1) { fail++; Program.Log($"FAIL row up: {u0},{uTo}"); }

        // A section moves whole. Check the outcome, not the arithmetic: after the move the
        // neighbouring section's last row must sit directly above this section's header.
        var copy = new List<string>(names);
        int cpu = Index("#CPU");
        var (clo, chi, cTo) = MovePlan(sec, cpu, 1);
        if (clo != cpu || chi != Index("CPU6")) { fail++; Program.Log($"FAIL section block: {clo},{chi}"); }
        var moved = new List<string>();
        for (int i = clo; i <= chi; i++) moved.Add(copy[i]);
        copy.RemoveRange(clo, chi - clo + 1);
        copy.InsertRange(cTo, moved);
        int at = copy.IndexOf("#CPU");
        if (copy[at - 1] != "GPU5" || copy[at + moved.Count] != "#Memory")
        {
            fail++;
            Program.Log($"FAIL section down: {copy[at - 1]} above #CPU, {copy[at + moved.Count]} below");
        }
        int backTo = MovePlan(sec, cpu, -1).Target;
        if (backTo != Index("#Frames")) { fail++; Program.Log($"FAIL section up: {backTo}"); }

        // Straight to the very top and the very bottom.
        var (_, _, lastTo) = MovePlan(sec, Index("Time2"), int.MaxValue);
        if (lastTo != sec.Length - 1) { fail++; Program.Log($"FAIL to bottom: {lastTo} of {sec.Length}"); }
        var (_, _, firstTo) = MovePlan(sec, Index("Time0"), int.MinValue);
        if (firstTo != 0) { fail++; Program.Log($"FAIL to top: {firstTo}"); }

        // "Your row 3" left in a saved order after the third template went is not a row. Listing it
        // is what made Remove row do nothing.
        if (!IsRow("gputemp", 1)) { fail++; Program.Log("FAIL builtin row dropped"); }
        if (!IsRow("custom1", 1)) { fail++; Program.Log("FAIL live custom row dropped"); }
        if (IsRow("custom2", 1) || IsRow("custom0", 1) || IsRow("custom", 1) || IsRow("cpus", 1))
        {
            fail++;
            Program.Log("FAIL dead row still listed");
        }

        // Column packing. The two things that must never happen: a packed grid wider than the
        // monitor, and a line quietly dropped because its cell fell outside the bitmap.
        void Pack(string what, float[] widths, int want, float gap, float maxW, int wantCols)
        {
            var colOf = PackColumns(widths, want, gap, maxW);
            if (colOf is null) return;   // null is legal: the layout wraps down instead
            var colW = new float[colOf.Max() + 1];
            for (int i = 0; i < widths.Length; i++) colW[colOf[i]] = Math.Max(colW[colOf[i]], widths[i]);
            float total = 0;
            for (int c = 0; c < colW.Length; c++) total += colW[c] + (c > 0 ? gap : 0);
            if (total > maxW)
            {
                fail++;
                Program.Log($"FAIL {what}: packed {total:0} wider than maxW {maxW:0}");
            }
            if (colW.Length > wantCols)
            {
                fail++;
                Program.Log($"FAIL {what}: used {colW.Length} columns, asked at most {wantCols}");
            }
            if (colOf.Length != widths.Length)
            {
                fail++;
                Program.Log($"FAIL {what}: {colOf.Length} slots for {widths.Length} lines");
            }
        }
        Pack("one column", [80, 80], 1, 10, 200, 1);
        Pack("two columns", [80, 80, 80, 80], 2, 10, 400, 2);
        Pack("too many asked", [100, 100, 100, 100, 100], 5, 10, 250, 5);
        Pack("one wide line", [200, 60, 60], 3, 10, 400, 3);
        if (PackColumns([400, 400], 2, 1, 300) is not null)
        {
            fail++;
            Program.Log("FAIL unwideable rows reported as a grid");
        }

        // Hiding a group must empty it from the overlay, not just from the settings table.
        {
            // A hand-built config, NOT Config.Load(). Reading the operator's config.json made this
            // check meaningless: any config without network or disk rows passed with nothing
            // hidden to hide, so the assertion could never hold - and neither could the shipped
            // defaults, which have no network or disk row either. The rows must be named here for
            // this test to say anything.
            var g = new Config
            {
                Lines = ["fps", "ping", "netup", "diskread", "diskwrite", "cpu"],
                HiddenGroups = [],
            };
            int all = Rows(g, FrameStats.Empty, new Reading()).Count;
            g.HiddenGroups = ["Network", "Disk"];
            var left = Rows(g, FrameStats.Empty, new Reading());
            int fewer = left.Count;
            if (fewer >= all)
            {
                fail++;
                Program.Log($"FAIL hidden groups still rendered: {fewer} rows of {all}");
            }
            // Not just "fewer": the rows that went have to be the hidden ones. Row carries no id
            // list, so this asserts the group predicate directly on each hidden id - which is the
            // same call Rows() makes at line 614, so a pass here means a pass in the real path.
            else if (new[] { "ping", "netup", "diskread", "diskwrite" }.Any(id => !IsHidden(g, id)))
            {
                fail++;
                Program.Log("FAIL a hidden row was still reported visible");
            }
            g.HiddenGroups = [];
            if (Rows(g, FrameStats.Empty, new Reading()).Count != all)
            {
                fail++;
                Program.Log("FAIL unhiding a group did not bring its rows back");
            }
        }

        // The whole point: whatever the rows, the composed overlay has to fit the width it was
        // given. Composited against a known budget, in every layout shape, including an absurd
        // column count, so an overlay wider than its budget fails here.
        foreach (var (layout, cols) in new[] { ("line", 1), ("line", 3), ("line", 8), ("stack", 0) })
        {
            ForceMaxW = 2200;
            try
            {
                var probe = Config.Load();
                probe.Layout = layout;
                probe.Columns = cols;
                using var pf = new Font(probe.Font, (float)probe.FontSize, GraphicsUnit.Point);
                using var pic = Compose(probe, pf, FrameStats.Empty, new Reading(), opaque: true);
                if (pic.Width > 2200)
                {
                    fail++;
                    Program.Log($"FAIL {layout} cols={cols}: composed {pic.Width}px over a 2200px budget");
                }
            }
            catch (Exception e) { fail++; Program.Log($"FAIL {layout} cols={cols} threw: {e.Message}"); }
            finally { ForceMaxW = 0; }
        }

        // Screen anchoring: X/Y are the only position controls - no anchors, no pixel nudge -
        // and the overlay must land fully inside the working area on every monitor, including ones
        // whose origin is not (0,0). Swept rather than spot-checked: a placement bug only shows on a
        // secondary monitor, so one example on the primary would not catch it.
        {
            Rectangle[] areas =
            {
                new(0, 0, 1920, 1040),        // primary
                new(1920, 0, 2560, 1400),     // secondary to the right
                new(-1920, 0, 1920, 1040),    // secondary to the left  (negative origin)
                new(0, -1080, 1920, 1080),    // secondary above        (negative origin)
                new(1280, 200, 1366, 738),    // odd offset and size
            };
            (int W, int H)[] boxes = { (400, 30), (1200, 400), (3000, 2000) };  // last is oversized
            double[] pcts = { 0, 1, 25, 50, 75, 99, 100 };

            int swept = 0, escaped = 0;
            foreach (var work in areas)
            foreach (var (bw, bh) in boxes)
            foreach (double xp in pcts)
            foreach (double yp in pcts)
            {
                var (x, y) = Anchor(work, xp, yp, bw, bh);
                swept++;
                // Right/Bottom are exclusive, so "inside" is [Left, Right-Width]. A box wider than
                // the work area is pinned to Left and cannot be made to fit.
                int maxX = work.Left + Math.Max(0, work.Width - bw);
                int maxY = work.Top + Math.Max(0, work.Height - bh);
                if (x < work.Left || x > maxX || y < work.Top || y > maxY)
                {
                    escaped++;
                    if (escaped <= 5)
                        Program.Log($"FAIL {work.Width}x{work.Height}@{work.Left},{work.Top} " +
                                    $"box {bw}x{bh} at {xp},{yp}% -> ({x},{y}) outside");
                }
            }
            fail += escaped;
            Program.Log($"anchor sweep: {swept} combinations, {escaped} outside the working area");

            // Containment alone is not enough. The clamp keeps a mis-placed box *inside* the working
            // area, so an "is it on screen?" check still passes when the X anchor has no effect.
            // Each box must also land where its anchor puts it. These assert the direction and
            // the travel, which is the contract the user sees.
            int wrong = 0;
            foreach (var work in areas)
            {
                const int w = 400, h = 30;
                if (work.Width < w || work.Height < h) continue;

                // X: 0 is the left edge, 100 puts the box's right edge on the work area's right edge.
                var (x0, _) = Anchor(work, 0, 100, w, h);
                var (x100, _) = Anchor(work, 100, 100, w, h);
                if (x0 != work.Left) { wrong++; Program.Log($"FAIL X=0 not at the left edge on {work}: {x0} != {work.Left}"); }
                if (x100 != work.Right - w) { wrong++; Program.Log($"FAIL X=100 not flush right on {work}: {x100} != {work.Right - w}"); }

                // Y is bottom-origin: 0 is the BOTTOM edge, 100 puts the box's top edge on the top.
                var (_, y0) = Anchor(work, 0, 0, w, h);
                var (_, y100) = Anchor(work, 0, 100, w, h);
                if (y0 != work.Bottom - h) { wrong++; Program.Log($"FAIL Y=0 not at the bottom edge on {work}: {y0} != {work.Bottom - h}"); }
                if (y100 != work.Top) { wrong++; Program.Log($"FAIL Y=100 not at the top edge on {work}: {y100} != {work.Top}"); }

                // 50% must centre the box on each axis (the presets Left/Center/Right depend on it).
                // Allow one pixel for the rounding of an odd travel.
                var (xc, yc) = Anchor(work, 50, 50, w, h);
                if (Math.Abs(xc - (work.Left + (work.Width - w) / 2)) > 1) { wrong++; Program.Log($"FAIL X=50 not centred on {work}: {xc}"); }
                if (Math.Abs(yc - (work.Top + (work.Height - h) / 2)) > 1) { wrong++; Program.Log($"FAIL Y=50 not centred on {work}: {yc}"); }

                // Monotonic: X never walks backwards, Y never inverts. Without these an inverted
                // axis would still satisfy the four endpoint checks on a clamped box.
                var (px, py) = Anchor(work, 0, 0, w, h);
                for (double t = 1; t <= 100; t += 1)
                {
                    var (tx, ty) = Anchor(work, t, t, w, h);
                    if (tx < px) { wrong++; Program.Log($"FAIL X went backwards at {t}% on {work}: {tx} < {px}"); break; }
                    if (ty > py) { wrong++; Program.Log($"FAIL Y went backwards at {t}% on {work}: {ty} > {py}"); break; }
                    (px, py) = (tx, ty);
                }
            }
            fail += wrong;
            Program.Log($"anchor direction checks: {wrong} wrong");
        }

        var verdict = fail == 0
            ? $"selftest ok: {sec.Length} items, {secs.Length} sections"
            : $"selftest FAILED: {fail} check(s)";
        verdict += " | " + ComposeCheck();
        Program.Log(verdict);
        Console.WriteLine(verdict);
        return fail;
    }

    /// <summary>Composes the live config and reports what came out: an empty bitmap is a bug worth naming.</summary>
    static string ComposeCheck()
    {
        try
        {
            // Same awareness as the running app, or Screen.FromHandle hands back virtualised bounds
            // and every number in this verdict is fiction.
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
            var cfg = Config.Load();
            using var font = new Font(cfg.Font, (float)cfg.FontSize, GraphicsUnit.Point);
            using var bmp = Compose(cfg, font, FrameStats.Empty, new Reading(), opaque: true);
            // Empty readings still prove the shape: labels, clusters and the value column.
            // Saving it is the only way to actually look at the spacing.
            try { bmp.Save("selftest-overlay.png", ImageFormat.Png); } catch { /* read-only folder */ }
            bool drawn = false;
            for (int y = 0; y < bmp.Height && !drawn; y += 2)
                for (int x = 0; x < bmp.Width; x += 2)
                    if (bmp.GetPixel(x, y).ToArgb() != Key.ToArgb()) { drawn = true; break; }
            return $"compose {bmp.Width}x{bmp.Height} rows={cfg.Lines.Length} cols={cfg.Columns} "
                + $"layout={cfg.Layout} gap={cfg.Gap} maxW={LastMaxW} widest={LastWidest:0} drawn={(drawn ? "yes" : "NOTHING")}";
        }
        catch (Exception e) { return "compose threw: " + e.Message; }
    }

    /// <summary>
    /// Every row the overlay can draw: id, and the template used when none is configured.
    ///
    /// Each default is a bare token and nothing else - no caption, no alignment box, no colour tag.
    ///
    /// A default should show the value it names and nothing else. Captions, spacing, alignment and
    /// colour are the user's to add, and the token list in the settings help lists what they can add.
    ///
    /// The row ids do not always match their token: "frametime" prints {framems}, and "cpu" prints
    /// {cpus} (the model alone, "5600X", not "AMD Ryzen 5 5600X"). Those ids stay as they are -
    /// they are what config.json stores and what the clustering rules key on.
    /// </summary>
    public static readonly Dictionary<string, string> Catalog = new()
    {
        ["game"] = "{game}",
        ["app"] = "{app}",
        ["exe"] = "{exe}",
        ["api"] = "{api}",
        ["arch"] = "{arch}",
        ["res"] = "{res}",
        ["resnative"] = "{resnative}",
        ["resscale"] = "{resscale}",
        ["display"] = "{display}",
        ["displayfull"] = "{displayfull}",
        ["driver"] = "{driver}",
        ["time"] = "{time}",
        ["date"] = "{date}",

        ["fps"] = "{fps}",
        ["fpsmax"] = "{fpsmax}",
        ["fpsavg"] = "{fpsavg}",
        ["fpsmin"] = "{fpsmin}",
        ["fps1low"] = "{fps1low}",
        ["fps10low"] = "{fps10low}",
        ["frametime"] = "{framems}",
        ["graph"] = "{graph}",

        ["cpu"] = "{cpus}",
        ["cpuload"] = "{cpuload}",
        ["cputemp"] = "{cputemp}",
        ["cpuclock"] = "{cpuclock}",
        ["cpupower"] = "{cpupower}",
        ["cpufan"] = "{cpufan}",

        ["gpu"] = "{gpu}",
        ["gpuload"] = "{gpuload}",
        ["gputemp"] = "{gputemp}",
        ["gpuclock"] = "{gpuclock}",
        ["gpupower"] = "{gpupower}",
        ["gpufan"] = "{gpufan}",

        ["ping"] = "{ping}",
        ["netup"] = "{netup}",
        ["netdown"] = "{netdown}",
        ["diskread"] = "{diskread}",
        ["diskwrite"] = "{diskwrite}",

        ["ram"] = "{ram}",
        ["rampct"] = "{rampct}",
        ["vram"] = "{vram}",
        ["vrampct"] = "{vrampct}",
        ["vramclock"] = "{vramclock}",
        ["vramtemp"] = "{vramtemp}",
    };

    public static readonly string[] Groups =
    {
        "game", "app", "exe", "api", "arch", "res", "resnative", "resscale", "display", "displayfull",
        "driver", "time", "date",
        "fps", "fpsmax", "fpsavg", "fpsmin", "fps1low", "fps10low", "frametime", "graph",
        "cpu", "cpuload", "cputemp", "cpuclock", "cpupower", "cpufan",
        "gpu", "gpuload", "gputemp", "gpuclock", "gpupower", "gpufan",
        "ping", "netup", "netdown", "diskread", "diskwrite",
        "ram", "rampct", "vram", "vrampct", "vramclock", "vramtemp",
    };

    /// <summary>Full names for the settings table: the ids are the file format, not the label.</summary>
    public static readonly Dictionary<string, string> Names = new()
    {
        ["game"] = "Game window title",
        ["app"] = "Application name",
        ["exe"] = "Executable file",
        ["api"] = "Graphics API (DX12, Vulkan)",
        ["arch"] = "Process architecture",
        ["res"] = "Current resolution",
        ["resnative"] = "Monitor's native resolution",
        ["resscale"] = "Resolution scaling %",
        ["display"] = "Monitor name",
        ["displayfull"] = "Monitor name, mode and scale",
        ["driver"] = "Graphics driver version",
        ["time"] = "Clock time",
        ["date"] = "Date",
        ["fps"] = "Frames per second",
        ["fpsmax"] = "Peak FPS in window",
        ["fpsavg"] = "Average FPS in window",
        ["fpsmin"] = "Lowest FPS in window",
        ["fps1low"] = "1% low FPS",
        ["fps10low"] = "10% low FPS",
        ["frametime"] = "Frame time in ms",
        ["graph"] = "Frametime graph",
        ["cpu"] = "CPU model",
        ["cpuload"] = "CPU load %",
        ["cputemp"] = "CPU temperature",
        ["cpuclock"] = "CPU clock",
        ["cpupower"] = "CPU power draw",
        ["cpufan"] = "CPU fan speed",
        ["gpu"] = "GPU short model",
        ["gpuload"] = "GPU load %",
        ["gputemp"] = "GPU temperature",
        ["gpuclock"] = "GPU clock",
        ["gpupower"] = "GPU power draw",
        ["gpufan"] = "GPU fan speed",
        ["ping"] = "Ping to host",
        ["netup"] = "Network upload rate",
        ["netdown"] = "Network download rate",
        ["diskread"] = "Disk read rate",
        ["diskwrite"] = "Disk write rate",
        ["ram"] = "RAM used / total",
        ["rampct"] = "RAM used %",
        ["vram"] = "VRAM used / total",
        ["vrampct"] = "VRAM used %",
        ["vramclock"] = "VRAM clock",
        ["vramtemp"] = "VRAM temperature",
    };

    /// <summary>
    /// One short plain line per metric id, shown in the Description column of the settings table. Only the
    /// settings table reads this; the overlay never does. Keys match <see cref="Names"/> one for one.
    /// </summary>
    public static readonly Dictionary<string, string> Descriptions = new()
    {
        ["game"] = "Title of the game window being captured",
        ["app"] = "Name of the running application",
        ["exe"] = "File name of the game's executable",
        ["api"] = "Graphics API the game uses, such as DX12 or Vulkan",
        ["arch"] = "Whether the game runs as 32-bit or 64-bit",
        ["res"] = "Resolution the game is rendering at now",
        ["resnative"] = "Native resolution of the monitor's panel",
        ["resscale"] = "Resolution scaling as a percentage",
        ["display"] = "Name of the monitor the game is on",
        ["displayfull"] = "Monitor name with its mode and scale factor",
        ["driver"] = "Installed graphics driver version",
        ["time"] = "Current clock time",
        ["date"] = "Current date",
        ["fps"] = "Frames per second right now",
        ["fpsmax"] = "Highest FPS seen in the current window",
        ["fpsavg"] = "Average FPS over the current window",
        ["fpsmin"] = "Lowest FPS seen in the current window",
        ["fps1low"] = "FPS of the slowest 1% of frames",
        ["fps10low"] = "FPS of the slowest 10% of frames",
        ["frametime"] = "Time taken to render the last frame, in ms",
        ["graph"] = "Graph of recent frame times",
        ["cpu"] = "Name of the CPU model",
        ["cpuload"] = "How busy the CPU is, as a percentage",
        ["cputemp"] = "Current CPU temperature",
        ["cpuclock"] = "Current CPU clock speed",
        ["cpupower"] = "Power the CPU is drawing right now",
        ["cpufan"] = "Speed of the CPU cooler fan",
        ["gpu"] = "Short name of the graphics card",
        ["gpuload"] = "How busy the GPU is, as a percentage",
        ["gputemp"] = "Current GPU temperature",
        ["gpuclock"] = "Current GPU core clock speed",
        ["gpupower"] = "Power the GPU is drawing right now",
        ["gpufan"] = "Speed of the GPU fan",
        ["ping"] = "Round-trip network delay to the ping host, in ms",
        ["netup"] = "Network upload speed",
        ["netdown"] = "Network download speed",
        ["diskread"] = "Disk read speed",
        ["diskwrite"] = "Disk write speed",
        ["ram"] = "System RAM in use and total installed",
        ["rampct"] = "Percentage of system RAM in use",
        ["vram"] = "Graphics memory in use and total",
        ["vrampct"] = "Percentage of graphics memory in use",
        ["vramclock"] = "Graphics memory clock speed",
        ["vramtemp"] = "Graphics memory temperature",
    };

    /// <summary>
    /// A block of rows that can be switched on and off as one thing. The built-in sections are one
    /// of these; an add-on supplies its own and calls <see cref="AddGroup"/>, which is the whole
    /// extension point.
    /// </summary>
    public interface IGroup
    {
        string Header { get; }
        IReadOnlyList<string> Ids { get; }
    }

    sealed class BuiltInGroup(string header, string[] ids) : IGroup
    {
        public string Header { get; } = header;
        public IReadOnlyList<string> Ids { get; } = ids;
    }

    static readonly List<IGroup> _groups = new();

    /// <summary>Every group: the built-ins, then anything an add-on registered.</summary>
    public static IReadOnlyList<IGroup> AllGroups
    {
        get
        {
            if (_groups.Count == 0)
                foreach (var (header, ids) in BuiltInSections) AddGroup(new BuiltInGroup(header, ids));
            return _groups;
        }
    }

    /// <summary>The registration point. Built-ins use it too, and so can an add-on.</summary>
    public static void AddGroup(IGroup group)
    {
        if (!_groups.Any(g => g.Header.Equals(group.Header, StringComparison.OrdinalIgnoreCase)))
            _groups.Add(group);
    }

    /// <summary>Which group owns a row id, or null when no group claims it.</summary>
    public static string? HomeOf(string id) =>
        AllGroups.FirstOrDefault(g => g.Ids.Contains(id, StringComparer.OrdinalIgnoreCase))?.Header;

    /// <summary>Whether a row belongs to a group that is switched off, so it should not be shown.</summary>
    public static bool IsHidden(Config cfg, string id)
    {
        var home = HomeOf(id);
        return home is not null && !GroupOn(cfg, home);
    }

    /// <summary>Whether a group is switched on in this config.</summary>
    public static bool GroupOn(Config cfg, string header) =>
        cfg.HiddenGroups is null
        || !cfg.HiddenGroups.Any(h => h.Equals(header, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// How the Rows tab is sectioned, skipping the groups switched off. Header order is the list
    /// order; any id missing from every group lands in "Other" rather than disappearing.
    /// </summary>
    public static IGroup[] SectionsFor(Config cfg) => AllGroups.Where(g => GroupOn(cfg, g.Header)).ToArray();

    static readonly (string Header, string[] Ids)[] BuiltInSections =
    {
        ("App",     ["game", "app", "exe", "api", "arch", "res", "resnative", "resscale"]),
        ("Display", ["display", "displayfull", "driver"]),
        ("Frames",  ["fps", "fpsmax", "fpsavg", "fpsmin", "fps1low", "fps10low", "frametime", "graph"]),
        ("CPU",     ["cpu", "cpuload", "cputemp", "cpuclock", "cpupower", "cpufan"]),
        ("GPU",     ["gpu", "gpuload", "gputemp", "gpuclock", "gpupower", "gpufan"]),
        ("Memory",  ["ram", "rampct", "vram", "vrampct", "vramclock", "vramtemp"]),
        ("Network", ["ping", "netup", "netdown"]),
        ("Disk",    ["diskread", "diskwrite"]),
        ("Time",    ["time", "date"]),
    };

    /// <summary>Every group, ignoring the switches. Kept for callers that want the full set.</summary>
    public static IGroup[] Sections => AllGroups.ToArray();


    /// <summary>Every placeholder the templates understand, for the settings help.</summary>
    public const string TokenHelp =
        "{game} {app} {exe} {api} {arch} {res} {resnative} {resscale} {display} {displayfull} "
        + "{driver} {time} {date} {graph} | "
        + "{fps} {fpsmax} {fpsavg} {fpsmin} {fps1low} {fps10low} {framems} | "
        + "{cpuname} {cpus} {cpufull} {cpuload} {cputemp} {cpuclock} {cpupower} {cpufan} | "
        + "{gpu} {gpufull} {gpuload} {gputemp} {gpuclock} {gpupower} {gpufan} | "
        + "{ping} {netup} {netdown} {diskread} {diskwrite} | "
        + "{ram} {ramtotal} {rampct} {vram} {vramtotal} {vrampct} {vramclock} {vramtemp}";

    static readonly Regex Token = new(@"\{(\w+)\}", RegexOptions.Compiled);

    public static List<Row> Rows(Config cfg, FrameStats f, Reading r)
    {
        var v = Tokens(f, r, cfg);
        var rows = new List<Row>();
        foreach (var id in cfg.Lines)
        {
            // A group switched off in the Rows tab stays out of the overlay too, not just the table.
            if (IsHidden(cfg, id)) continue;
            // "custom1" and friends are the user's own rows: the template *is* the whole row.
            if (id.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(id.AsSpan(CustomPrefix.Length), out int n)
                && n >= 1 && n <= cfg.CustomRows.Count)
            {
                rows.Add(Build(cfg.CustomRows[n - 1], v, Color.Empty, ""));
                continue;
            }
            if (!Catalog.ContainsKey(id)) continue;
            var tpl = cfg.Formats.TryGetValue(id, out var t) && !string.IsNullOrWhiteSpace(t) ? t : Catalog[id];
            bool graph = tpl.Contains("{graph}", StringComparison.Ordinal);
            // A per-row swatch sets the default tint; an inline <C=..> in the template still wins.
            var row = Build(tpl, v, cfg.RowColors.TryGetValue(id, out var hex) ? Parse("#" + hex, Color.Empty) : Color.Empty, id);

            bool hot = (tpl.Contains("{cputemp}", StringComparison.Ordinal) && r.CpuTemp >= cfg.HotTempC)
                    || (tpl.Contains("{gputemp}", StringComparison.Ordinal)
                        && (r.GpuTemp >= cfg.HotTempC || r.GpuHot >= cfg.HotTempC))
                    || (tpl.Contains("{cpuload}", StringComparison.Ordinal) && r.CpuLoad >= cfg.HotLoadPct)
                    || (tpl.Contains("{gpuload}", StringComparison.Ordinal) && r.GpuLoad >= cfg.HotLoadPct);
            rows.Add(row with { Hot = hot, Graph = graph });
        }
        if (rows.Count == 0) rows.Add(new Row("no lines in config", "", [], false, false, Color.Empty, 1, ""));
        return Grouped(rows);
    }

    /// <summary>
    /// Every row carrying the same &lt;G=name&gt; collapses onto the first line that used that name,
    /// in list order. Non-adjacent rows join up as well: any row can be clubbed with any other.
    /// </summary>
    static List<Row> Grouped(List<Row> rows)
    {
        var outp = new List<Row>();
        var at = new Dictionary<string, int>();
        foreach (var row in rows)
        {
            if (row.Group.Length == 0) { outp.Add(row); continue; }
            if (!at.TryGetValue(row.Group, out int i))
            {
                at[row.Group] = outp.Count;
                outp.Add(row);
                continue;
            }
            var host = outp[i];
            // Everything after the cluster's leader goes into the value column, so the numbers
            // line up under each other instead of trailing off ragged.
            string label = row.Right.Length > 0 ? row.Text : "";
            string value = row.Right.Length > 0 ? row.Right : row.Text;
            outp[i] = host with
            {
                Text = host.Text + (label.Length > 0 ? (host.Text.Length > 0 ? " " : "") + label : ""),
                Right = host.Right.Length > 0 ? host.Right + " " + value : value,
                Hot = host.Hot || row.Hot,
                Graph = host.Graph || row.Graph,
                // The alignment boxes index into the row's own text. Once a second row is merged in
                // those offsets point at the wrong characters, so the cluster measured and drew the
                // wrong slices - which is where the phantom gaps came from. A merged cluster drops
                // them: boxes are a per-row device and a shared line cannot have one per row.
                Boxes = host.Boxes.Length == 0 ? row.Boxes : [],
            };
        }
        return outp;
    }

    /// <summary>Id prefix for a row the user wrote by hand. "custom1" is the first of them.</summary>
    public const string CustomPrefix = "custom";

    /// <summary>Whether a metric shares a line, so the rows table can show its group.</summary>
    public static string GroupOf(string id) => ClusterOf(id);

    /// <summary>
    /// True for a row that still exists: a built-in metric, or one of the <paramref name="customCount"/>
    /// rows the user has actually written. "custom3" left over in a saved order is not a row.
    /// Showing it would make Remove row bail out silently on a number that does not exist.
    /// </summary>
    public static bool IsRow(string id, int customCount) =>
        Catalog.ContainsKey(id)
        || (id.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(id.AsSpan(CustomPrefix.Length), out int n)
            && n >= 1 && n <= customCount);

    static string ClusterOf(string id) =>
        id.StartsWith("cpu", StringComparison.OrdinalIgnoreCase) ? "CPU"
        : id.StartsWith("gpu", StringComparison.OrdinalIgnoreCase) ? "GPU"
        : id.StartsWith("fps", StringComparison.OrdinalIgnoreCase) ? "FPS"
        : id.StartsWith("frametime", StringComparison.OrdinalIgnoreCase) ? "FPS"
        : id.Equals("graph", StringComparison.OrdinalIgnoreCase) ? "FPS"
        : id.StartsWith("net", StringComparison.OrdinalIgnoreCase) ? "NET"
        : id.StartsWith("disk", StringComparison.OrdinalIgnoreCase) ? "DISK"
        : "";

    static Row Build(string tpl, Dictionary<string, string> v, Color tint, string id)
    {
        var sb = new System.Text.StringBuilder();
        var right = new System.Text.StringBuilder();
        var boxes = new List<Box>();
        float scale = 1;
        string group = ClusterOf(id);
        var inherit = tint;   // what <C> returns to: the swatch, not the default text colour
        int openAt = -1, openCells = 0;

        int i = 0;
        // Normalise before parsing: box offsets are recorded as we append, so the string has to be
        // final by then or every box points at the wrong characters.
        string src = Token.Replace(tpl, m => v.TryGetValue(m.Groups[1].Value, out var s) ? s : m.Value)
                           .Replace("  ", " ").Trim();
        var outp = sb;      // <R> switches which buffer the rest of the template goes into
        while (i < src.Length)
        {
            if (src[i] != '<')
            {
                outp.Append(src[i++]);
                continue;
            }
            int close = src.IndexOf('>', i);
            if (close < 0) { outp.Append(src[i..]); break; }

            var body = src[(i + 1)..close];
            i = close + 1;

            if (body.Equals("C", StringComparison.OrdinalIgnoreCase)) { tint = inherit; continue; }
            if (body.Equals("S", StringComparison.OrdinalIgnoreCase)) { scale = 1; continue; }
            if (body.Equals("R", StringComparison.OrdinalIgnoreCase))
            {
                if (openAt >= 0) { boxes.Add(new Box(openAt, sb.Length, openCells, openCells < 0)); openAt = -1; }
                outp = right;
                continue;
            }
            if (body.StartsWith("G=", StringComparison.OrdinalIgnoreCase)) { group = body[2..]; continue; }
            if (body.StartsWith("C=", StringComparison.OrdinalIgnoreCase)) { tint = Parse("#" + body[2..], Color.Empty); continue; }
            if (body.StartsWith("S=", StringComparison.OrdinalIgnoreCase)
                && float.TryParse(body.AsSpan(2), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var pct))
            { scale = Math.Clamp(pct / 100f, 0.4f, 4f); continue; }

            if (body.Equals("/A", StringComparison.OrdinalIgnoreCase) || body.Equals("A=0", StringComparison.OrdinalIgnoreCase))
            {
                if (openAt >= 0) { boxes.Add(new Box(openAt, sb.Length, openCells, openCells < 0)); openAt = -1; }
                continue;
            }
            if (body.StartsWith("A=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(body.AsSpan(2), out var cells) && cells != 0)
            {
                if (openAt >= 0) boxes.Add(new Box(openAt, sb.Length, openCells, openCells < 0));
                openAt = sb.Length;
                openCells = cells;
                continue;
            }
            sb.Append('<').Append(body).Append('>');   // not ours: leave it visible
        }
        if (openAt >= 0) boxes.Add(new Box(openAt, sb.Length, openCells, openCells < 0));

        return new Row(sb.ToString(), right.ToString().Trim(), boxes.ToArray(), false, false, tint, scale, group);
    }

    /// <summary>Placeholder name to its formatted value, for one tick.</summary>
    static Dictionary<string, string> Tokens(FrameStats f, Reading r, Config cfg)
    {
        bool mb = cfg.RamUnit.Equals("MB", StringComparison.OrdinalIgnoreCase);
        string Mem(float? gb) => gb is null ? "--" : mb ? $"{gb * 1024:0} MB" : $"{gb:0.0} GB";
        string PctOf(float? used, float? total) =>
            used is null || total is null || total <= 0 ? "--" : $"{used * 100 / total:0} %";
        // The name overrides replace the detected model name in every token that shows it. A blank
        // override falls back to the detected name.
        string cpuName = string.IsNullOrWhiteSpace(cfg.CpuNameOverride) ? r.CpuName : cfg.CpuNameOverride.Trim();
        string gpuShort = string.IsNullOrWhiteSpace(cfg.GpuNameOverride) ? r.GpuName : cfg.GpuNameOverride.Trim();
        string gpuFull = string.IsNullOrWhiteSpace(cfg.GpuNameOverride) ? r.GpuNameFull : cfg.GpuNameOverride.Trim();

        return new Dictionary<string, string>
        {
            ["game"] = r.GameName,
            ["app"] = r.AppName,
            ["exe"] = r.Exe,
            ["api"] = r.Api,
            ["arch"] = r.Arch,
            ["res"] = r.Res,
            ["resnative"] = r.ResNative,
            ["resscale"] = r.ResScale,
            ["display"] = r.Display,
            ["displayfull"] = r.DisplayFull,
            ["driver"] = r.Driver,
            // EffectiveTimeFormat, not TimeFormat: it is the one that applies the 12/24-hour
            // toggle. The raw stored format would ignore the toggle.
            ["time"] = Safe(() => DateTime.Now.ToString(cfg.EffectiveTimeFormat()), "HH:mm"),
            ["date"] = DateTime.Now.ToString("dd/MM"),
            ["graph"] = "",

            ["fps"] = Fps(f.Fps),
            ["fpsmax"] = Fps(f.Max),
            ["fpsavg"] = Fps(f.AvgFps),
            ["fpsmin"] = Fps(f.Min),
            ["fps1low"] = Fps(f.Low1),
            ["fps10low"] = Fps(f.Low10),
            ["framems"] = Ms(f.MsAvg),

            ["cpuname"] = cpuName,
            // A custom override is shown in full, as typed. Only the detected name is shortened.
            ["cpus"] = string.IsNullOrWhiteSpace(cfg.CpuNameOverride) ? Short(cpuName) : cpuName,  // "AMD Ryzen 5 5600X" -> "5600X"
            ["cpufull"] = cpuName,
            ["cpuload"] = Pct(r.CpuLoad),
            ["cputemp"] = Temp(r.CpuTemp),
            ["cpuclock"] = Clock(r.CpuClock),
            ["cpupower"] = Watts(r.CpuPower),
            ["cpufan"] = Fan(r.CpuFan),

            ["gpu"] = gpuShort,
            ["gpufull"] = gpuFull,
            ["gpuload"] = Pct(r.GpuLoad),
            ["gputemp"] = Temp(r.GpuTemp),
            ["gpuclock"] = Clock(r.GpuClock),
            ["gpupower"] = Watts(r.GpuPower),
            ["gpufan"] = Fan(r.GpuFan),

            ["ping"] = r.Ping,
            ["netup"] = Rate(r.NetUp),
            ["netdown"] = Rate(r.NetDown),
            ["diskread"] = Rate(r.DiskRead),
            ["diskwrite"] = Rate(r.DiskWrite),

            ["ram"] = Mem(r.RamUsed),
            ["ramtotal"] = Mem(r.RamTotal),
            ["rampct"] = PctOf(r.RamUsed, r.RamTotal),
            ["vram"] = Mem(r.VramUsed),
            ["vramtotal"] = Mem(r.VramTotal),
            ["vrampct"] = PctOf(r.VramUsed, r.VramTotal),
            // Missing sensor -> "--" through Clock/Temp, the same placeholder as every other metric.
            ["vramclock"] = Clock(r.VramClock),
            ["vramtemp"] = Temp(r.VramTemp),
            ["pingms"] = r.Ping,
        };
    }

    // ---- rendering ------------------------------------------------------------------------------

    public static Bitmap Compose(Config cfg, Font font, FrameStats f, Reading r, bool opaque = false)
    {
        byte A(double a) => opaque ? (byte)255 : (byte)Math.Clamp(255 * a * cfg.Opacity, 0, 255);
        bool oneLine = cfg.Layout.Equals("line", StringComparison.OrdinalIgnoreCase);
        var rows = Rows(cfg, f, r);

        using var probe = new Bitmap(1, 1);
        using var g0 = Graphics.FromImage(probe);
        int lh = (int)Math.Round(font.GetHeight(g0)) + 5;
        int pad = 9;
        int gw = cfg.GraphWidth, gh = Math.Max(12, cfg.GraphHeight);

        var fonts = new Dictionary<float, Font>();
        Font At(float s) => fonts.TryGetValue(s, out var x)
            ? x
            : fonts[s] = new Font(font.FontFamily, font.Size * s, font.Style, GraphicsUnit.Point);

        float widest = 0, lineW = 0;
        var widths = new float[rows.Count];
        var leftW = new float[rows.Count];
        var rightW = new float[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            var ff = At(rows[i].Size);
            float cell = g0.MeasureString("0", ff).Width;
            leftW[i] = Width(g0, rows[i], ff, cell);
            rightW[i] = rows[i].Right.Length == 0 ? 0 : g0.MeasureString(rows[i].Right, ff).Width;
            widths[i] = leftW[i] + (rightW[i] > 0 ? cfg.Gap + rightW[i] : 0) + (rows[i].Graph ? gw + 8 : 0);
            widest = Math.Max(widest, widths[i]);
            lineW += widths[i] + cfg.Gap;
        }

        // Every <R> value starts at one shared x, taken from the widest cluster label. Right
        // aligning them instead leaves a gap the width of the longest line on the block, which
        // is where the "unnecessary spaces" came from.
        float valueX = 0;
        foreach (var row in rows)
        {
            if (row.Right.Length == 0) continue;
            var ff = At(row.Size);
            valueX = Math.Max(valueX, Width(g0, row, ff, g0.MeasureString("0", ff).Width) + cfg.Gap);
        }

        // One line still has to fit the monitor, and the column count turns it into a grid: the
        // cells of a column share a width, which is what lines the overlay up. The bitmap is drawn
        // at 1:1 with no scale control, so the budget is the work area itself.
        int maxW = ForceMaxW > 0
            ? ForceMaxW
            : Math.Max(200, (int)(Screen.FromHandle(_anchor).WorkingArea.Width * 0.92) - pad * 2);
        LastMaxW = maxW;
        LastWidest = widths.Length == 0 ? 0 : widths.Max();
        int cols = oneLine ? Math.Clamp(cfg.Columns, 0, 16) : 0;

        // Columns are packed over whole lines and always against maxW, so the overlay cannot end
        // up wider than the monitor it is on. colOf is null when even one column will not take the
        // widest line, and the layout falls back to wrapping it down instead.
        int[]? colOf = cols > 1 ? PackColumns(widths, cols, cfg.Gap, maxW) : null;
        var colW = new float[colOf?.Max() + 1 ?? 0];
        if (colOf is not null)
        {
            for (int i = 0; i < rows.Count; i++) colW[colOf[i]] = Math.Max(colW[colOf[i]], widths[i]);
            lineW = 0;
            for (int c = 0; c < colW.Length; c++) lineW += colW[c] + (c > 0 ? cfg.Gap : 0);
            cols = colW.Length;
            widest = lineW;
        }
        var wraps = oneLine && colOf is null && lineW > maxW;
        if (wraps)
        {
            // Re-measure the widest wrapped line. Gate this on colOf being null, not on cols <= 1:
            // packing can fail while cols is still 5, and the full unwrapped width must not size the
            // bitmap, or the overlay could be far wider than the monitor.
            lineW = 0;
            float cur = 0;
            foreach (var wd in widths)
            {
                if (cur > 0 && cur + wd > maxW) { lineW = Math.Max(lineW, cur); cur = 0; }
                cur += wd + cfg.Gap;
            }
            lineW = Math.Max(lineW, cur);
        }

        // How many lines the tallest column ends up with: the packer fills columns top to bottom,
        // so it is the busiest column that sets the height, not the average.
        int gridRows = 1;
        if (colOf is not null)
        {
            var fill = new int[colW.Length];
            foreach (int c in colOf) fill[c]++;
            gridRows = fill.Max();
        }
        int w = oneLine ? (int)(pad * 2 + lineW) : pad * 2 + (int)Math.Ceiling(Math.Max(widest, valueX + rightW.Max()));
        int lhMax = Math.Max(lh, rows.Any(x => x.Graph) ? gh : 0);
        int h = oneLine
            ? pad * 2 + lhMax * (colOf is not null ? gridRows : (wraps ? WrapLines(widths, maxW, cfg.Gap) : 1))
            : pad * 2 + rows.Sum(x => x.Graph ? gh + 4 : lh);

        var bmp = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(opaque ? Key : Color.Transparent);

            var text = Parse(cfg.TextColor, Color.FromArgb(236, 240, 245));
            var accentC = Parse(cfg.AccentColor, Color.FromArgb(118, 196, 255));
            var hotC = Parse(cfg.ResolveHotColor(), Color.FromArgb(255, 148, 60));

            if (cfg.Box)
            {
                // This overlay is composited with a colour key, not per-pixel alpha, so a
                // "translucent" fill would just blend into dark grey. What actually works is
                // fading the panel towards the key colour: at 0 the panel *is* the key, which is
                // fully transparent, and at 100 it is the solid colour.
                var panel = Parse(cfg.BgColor, Color.FromArgb(14, 16, 19));
                float k = (float)Math.Clamp(cfg.PanelOpacity, 0, 1);
                var faded = Color.FromArgb(
                    (int)(Key.R + (panel.R - Key.R) * k),
                    (int)(Key.G + (panel.G - Key.G) * k),
                    (int)(Key.B + (panel.B - Key.B) * k));
                using var path = Round(new RectangleF(0.5f, 0.5f, w - 1, h - 1), 8);
                // Both of these were created inline and never disposed: a brush on every Compose,
                // plus a pen when the panel is visible, at the refresh interval. Dispose() returns
                // a GDI+ object immediately; the finalizer is the slow path.
                using (var fill = new SolidBrush(Color.FromArgb(A(1), faded)))
                    g.FillPath(fill, path);
                if (k > 0.02)
                    using (var edge = new Pen(Color.FromArgb(A(0.35f * k), text), 1f))
                        g.DrawPath(edge, path);
            }

            using var bright = new SolidBrush(Color.FromArgb(A(1), text));
            using var hot = new SolidBrush(Color.FromArgb(A(1), hotC));
            using var accent = new Pen(Color.FromArgb(A(0.9f), accentC), 1.4f);

            // With a column grid every line's cell is known up front, so x and y are looked up
            // rather than accumulated. Without one, they are a running cursor.
            var colX = new float[colW.Length];
            for (int c = 1; c < colW.Length; c++) colX[c] = colX[c - 1] + colW[c - 1] + cfg.Gap;
            var lineOf = new int[rows.Count];
            if (colOf is not null)
            {
                var seen = new int[colW.Length];
                for (int i = 0; i < rows.Count; i++) lineOf[i] = seen[colOf[i]]++;
            }

            float x = pad, y = pad;
            for (int i = 0; i < rows.Count; i++)
            {
                if (colOf is not null) { x = colX[colOf[i]]; y = pad + lineOf[i] * lhMax; }
                var row = rows[i];
                var ff = At(row.Size);
                var brush = row.Hot ? hot : row.Tint.IsEmpty ? bright : new SolidBrush(Color.FromArgb(A(1), row.Tint));

                // One line never shares a value column: a global valueX would push every short row's
                // values out to the widest label's x. That is dead space, and because the measurement
                // does not account for it, a line measured as fitting could run off the screen.
                float cellValueX = oneLine ? leftW[i] + cfg.Gap : valueX;
                Draw(g, row, ff, g0, x, y + (row.Graph ? (gh - ff.GetHeight(g)) / 2f : 0), brush, ref x,
                    oneLine, cellValueX);
                if (row is { Tint.IsEmpty: false, Hot: false }) brush.Dispose();
                if (row.Graph)
                {
                    // Stack mode puts the graph on the right edge; one-line mode has to follow the
                    // cursor, or every graph row lands on top of the same spot.
                    float gx = oneLine ? x + 4 : w - pad - gw;
                    var pen = row.Tint.IsEmpty || row.Hot
                        ? accent
                        : new Pen(Color.FromArgb(A(0.9f), row.Tint), 1.4f);
                    DrawGraph(g, cfg, f, new RectangleF(gx, y, gw, gh), pen);
                    if (pen != accent) pen.Dispose();
                    if (oneLine) x = gx + gw;
                }
                if (oneLine)
                {
                    if (colOf is null)
                    {
                        x += cfg.Gap;
                        if (wraps && x + widths[i] > maxW + pad) { x = pad; y += Math.Max(lh, gh); }
                    }
                }
                else y += row.Graph ? gh + 4 : lh;
            }
        }
        foreach (var ff in fonts.Values) ff.Dispose();
        return bmp;
    }

    /// <summary>Width of a row once its alignment boxes are honoured.</summary>
    static float Width(Graphics g, Row row, Font font, float cell)
    {
        if (row.Boxes.Length == 0) return g.MeasureString(row.Text, font).Width;
        float total = 0;
        int at = 0;
        foreach (var b in row.Boxes)
        {
            total += g.MeasureString(Slice(row.Text, at, b.Start), font).Width;
            float boxW = Math.Abs(b.Cells) * cell;
            total += Math.Max(g.MeasureString(Slice(row.Text, b.Start, b.End), font).Width, boxW);
            at = b.End;
        }
        return total + g.MeasureString(Slice(row.Text, at, row.Text.Length), font).Width;
    }

    /// <summary>
    /// Draws a row honouring its boxes, then flushes the &lt;R&gt; part to
    /// <paramref name="startX"/> + the shared value column.
    /// <paramref name="x"/> is the cursor in one-line mode.
    /// </summary>
    static void Draw(Graphics g, Row row, Font font, Graphics g0, float startX, float y, Brush brush,
        ref float x, bool oneLine, float valueX)
    {
        float cell = g0.MeasureString("0", font).Width;
        float cursor = startX;

        void Flow(string s)
        {
            if (s.Length == 0) return;
            g.DrawString(s, font, brush, cursor, y);
            cursor += g0.MeasureString(s, font).Width;
        }

        if (row.Boxes.Length == 0) Flow(row.Text);
        else
        {
            int at = 0;
            foreach (var b in row.Boxes)
            {
                Flow(Slice(row.Text, at, b.Start));
                float boxW = Math.Abs(b.Cells) * cell;
                var seg = Slice(row.Text, b.Start, b.End);
                if (seg.Length > 0)
                {
                    float tw = g0.MeasureString(seg, font).Width;
                    g.DrawString(seg, font, brush, b.Right ? cursor + boxW - tw : cursor, y);
                    cursor += Math.Max(tw, boxW);
                }
                else cursor += boxW;
                at = b.End;
            }
            Flow(Slice(row.Text, at, row.Text.Length));
        }

        if (row.Right.Length > 0)
        {
            float rw = g0.MeasureString(row.Right, font).Width;
            float at = Math.Max(cursor, startX + valueX);
            g.DrawString(row.Right, font, brush, at, y);
            cursor = at + rw;
        }
        // Report the width actually drawn, for rows with or without an <R> part. Returning early
        // on rows without one would leave the cursor where it was, so in one-line mode every row
        // would be drawn on top of the last when Gap is 0.
        if (oneLine) x = cursor;
    }

    static string Slice(string s, int from, int to) =>
        from >= to ? "" : s[Math.Clamp(from, 0, s.Length)..Math.Clamp(to, 0, s.Length)];

    /// <summary>How many lines the one-line layout needs once it has to fit the monitor width.</summary>
    public static int[]? PackColumns(float[] widths, int want, float gap, float maxW)
    {
        // Whole lines only: a cluster that does not fit the current column starts a new one, it
        // never straddles two. Each column gets an equal share of the monitor, so a wide cluster
        // costs its own room instead of pushing the overlay off the screen. Columns come down
        // until the whole thing fits, so the requested count is a maximum, never a demand.
        for (int cols = Math.Clamp(want, 1, 16); cols >= 1; cols--)
        {
            float share = (maxW - (cols - 1) * gap) / cols;
            var colOf = new int[widths.Length];
            var colW = new float[cols];
            int col = 0;
            float used = 0;
            for (int i = 0; i < widths.Length; i++)
            {
                if (used > 0 && used + gap + widths[i] > share && col + 1 < cols) { col++; used = 0; }
                if (used > 0) used += gap;
                used += widths[i];
                colW[col] = Math.Max(colW[col], used);
                colOf[i] = col;
            }
            float total = 0;
            for (int c = 0; c < cols; c++) total += colW[c] + (c > 0 ? gap : 0);
            if (total <= maxW) return colOf;
        }
        return null;
    }

    static int WrapLines(float[] widths, int maxW, int gap)
    {
        int lines = 1;
        float cur = 0;
        foreach (var wd in widths)
        {
            if (cur > 0 && cur + wd > maxW) { lines++; cur = 0; }
            cur += wd + gap;
        }
        return lines;
    }

    static void DrawGraph(Graphics g, Config cfg, FrameStats f, RectangleF box, Pen pen)
    {
        if (f.Plot.Length < 2) return;
        double max = f.Plot.Max();
        double scale = Math.Max(f.Plot.Average() * 1.6, max * 1.05);
        var pts = new PointF[f.Plot.Length];
        for (int i = 0; i < f.Plot.Length; i++)
        {
            float x = box.Left + box.Width * i / (f.Plot.Length - 1f);
            float y = box.Bottom - (float)Math.Min(1, f.Plot[i] / scale) * box.Height;
            pts[i] = new PointF(x, y);
        }
        g.DrawLines(pen, pts);

        float gy = box.Bottom - (float)(16.7 / scale) * box.Height;   // 60Hz budget guide
        if (gy > box.Top)
            using (var guide = new Pen(Color.FromArgb((byte)(90 * cfg.Opacity), 255, 255, 255), 1f))
                g.DrawLine(guide, box.Left, gy, box.Right, gy);
    }

    static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ---- formatting -----------------------------------------------------------------------------

    public static string Fps(double v) => v <= 0 ? "--" : $"{v:0}";
    static string Ms(double v) => v <= 0 ? "--" : $"{v:0.0} ms";
    static string Pct(float? v) => v is null ? "--" : $"{v:0} %";
    static string Temp(float? v) => v is null ? "--" : $"{v:0} °C";      // real degree sign
    static string Watts(float? v) => v is null ? "--" : $"{v:0} W";
    static string Fan(float? v) => v is null ? "--" : $"{v:0} rpm";
    static string Clock(float? v) => v is null ? "--" : v >= 1000 ? $"{v / 1000:0.00} GHz" : $"{v:0} MHz";
    static string Rate(float? bps) => bps is null ? "--"
        : bps >= 1024 * 1024 ? $"{bps / 1048576:0.0} MB/s" : $"{bps / 1024:0} KB/s";

    /// <summary>Last word of a hardware name: the part that identifies the model.</summary>
    static string Short(string name)
    {
        var t = name.Trim();
        int sp = t.LastIndexOf(' ');
        return sp > 0 && sp < t.Length - 1 ? t[(sp + 1)..] : t;
    }

    /// <summary>A user-supplied time format must never take the overlay down.</summary>
    static string Safe(Func<string> f, string fallback)
    {
        try { return f(); } catch { return fallback; }
    }

    /// <summary>Colour the overlay window uses as its transparency key. Never drawn with.</summary>
    public static readonly Color Key = Color.FromArgb(255, 0, 0, 1);

    /// <summary>Which window the one-line layout measures itself against. Set by the overlay each tick.</summary>
    public static IntPtr _anchor;

    // Last layout's numbers, so --selftest can report why a composed image came out the size it
    // did instead of leaving it to be guessed at from a screenshot.
    internal static int LastMaxW;
    internal static float LastWidest;

    // Set by --selftest to a known budget. A compose check that asks "which monitor?" has no
    // monitor to ask about, so its width numbers mean nothing and cannot be asserted on.
    internal static int ForceMaxW;

    /// <summary>"#RRGGBB" from config; falls back when it is missing or malformed.</summary>
    public static Color Parse(string hex, Color fallback)
    {
        if (hex is not null && hex.Length == 7 && hex[0] == '#'
            && int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return Color.FromArgb((int)((rgb >> 16) & 0xFF), (int)((rgb >> 8) & 0xFF), (int)(rgb & 0xFF));
        return fallback;
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
