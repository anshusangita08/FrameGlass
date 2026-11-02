using System.Reflection;
using FrameGlass;

namespace UiProbe;

/// <summary>
/// Real-monitor DPI probe for the Settings window (--shots-real).
///
/// The emulated probe (Navigation.ScaleTo) sets _k by hand and never changes the window's real DPI, so
/// fonts and native children stay at 96 DPI and it cannot reproduce the 175% scaled-monitor overlaps. This
/// probe puts the real window on the real monitor, so Windows reports the real DeviceDpi and fires the
/// real WM_DPICHANGED. Three cases, each captured to the output directory:
///   primary100 - opened on the primary monitor (100%), no move.
///   open4k     - opened directly on the 175% monitor, so the handle is created at that DPI.
///   drag       - opened on the primary, moved to the 175% monitor (real DpiChanged), then moved back.
/// Each step logs DeviceDpi, the form's _k and the font sizes actually in use.
/// </summary>
static class RealDpi
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var screens = Screen.AllScreens;
        var prim = screens.FirstOrDefault(s => s.Primary);
        var other = screens.FirstOrDefault(s => !s.Primary);
        if (prim is null || other is null)
        {
            Console.WriteLine("needs two displays");
            return 1;
        }
        Console.WriteLine($"primary : {prim.DeviceName} bounds={prim.Bounds} wa={prim.WorkingArea}");
        Console.WriteLine($"other   : {other.DeviceName} bounds={other.Bounds} wa={other.WorkingArea}");
        Console.WriteLine($"out dir : {dir}");

        // Top-left points well inside each monitor, so MonitorFromWindow picks that monitor.
        var onPrim = new Point(prim.WorkingArea.Left + 40, prim.WorkingArea.Top + 40);
        var onOther = new Point(other.WorkingArea.Left + 80, other.WorkingArea.Top + 60);

        int fail = 0;

        Console.WriteLine();
        Console.WriteLine("== case primary100: opened on primary, no move");
        using (var f = Make(onPrim))
        {
            Log("primary100", f);
            fail += Capture(f, dir, "primary100");
        }

        Console.WriteLine();
        Console.WriteLine("== case open4k: opened directly on the other monitor");
        using (var f = Make(onOther))
        {
            Log("open4k", f);
            fail += Capture(f, dir, "open4k");
        }

        Console.WriteLine();
        Console.WriteLine("== case drag: opened on primary, moved to the other monitor, then back");
        using (var f = Make(onPrim))
        {
            Log("drag.start", f);
            f.Location = onOther;          // real move: Windows sends WM_DPICHANGED
            Entry.Pump(8);
            Log("drag.to4k", f);
            fail += Capture(f, dir, "drag-to4k");

            f.Location = onPrim;           // and back
            Entry.Pump(8);
            Log("drag.back", f);
            fail += Capture(f, dir, "drag-back");
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "SHOTS-REAL OK" : $"SHOTS-REAL FAIL ({fail})");
        return fail;
    }

    /// <summary>Builds the real form at a screen position, shown topmost so the capture reads it.</summary>
    static SettingsForm Make(Point at)
    {
        var form = new SettingsForm(Entry.BuildFixture(), () => FrameStats.Empty, () => new Reading(), _ => { });
        form.StartPosition = FormStartPosition.Manual;
        form.Location = at;
        form.TopMost = true;
        form.Show();
        Entry.Pump(8);
        return form;
    }

    /// <summary>Logs the DPI the window reports, the factor the layout uses, and the font sizes in the tree.</summary>
    static void Log(string tag, SettingsForm f)
    {
        float k = (float)(Get(f, "_k") ?? 0f);
        var screen = Screen.FromControl(f);
        Console.WriteLine($"[{tag}] DeviceDpi={f.DeviceDpi} ({f.DeviceDpi * 100 / 96}%) _k={k:0.00} screen={screen.DeviceName} bounds={f.Bounds} client={f.ClientSize} min={f.MinimumSize}");
        Console.WriteLine($"[{tag}] form font={f.Font.Name} {f.Font.SizeInPoints:0.##}pt  height={f.Font.Height}px");
        var sizes = Navigation.Walk(f)
            .GroupBy(c => $"{c.GetType().Name}@{c.Font.SizeInPoints:0.##}pt")
            .OrderByDescending(g => g.Count())
            .Take(8)
            .Select(g => $"{g.Key} x{g.Count()}");
        Console.WriteLine($"[{tag}] font sizes: {string.Join(", ", sizes)}");
    }

    /// <summary>Saves each of the four pages, selected through the sidebar as a user would.</summary>
    static int Capture(SettingsForm form, string dir, string prefix)
    {
        int fail = 0;
        var nav = Navigation.Nav(form) ?? throw new InvalidOperationException("no NavSidebar");
        for (int i = 0; i < Navigation.PageNames.Length; i++)
        {
            Navigation.Select(nav, i);
            Navigation.ScrollToTop(Navigation.PageAt(form, i)!);
            Entry.Pump(2);
            LogMeasures(form, prefix, Navigation.PageNames[i]);
            string file = Path.Combine(dir, $"{prefix}-p{i}-{Navigation.PageNames[i]}.png");
            if (Navigation.CaptureWindow(form, file)) Console.WriteLine($"  saved {file}");
            else fail++;
        }
        return fail;
    }

    /// <summary>
    /// Logs bounds of the bottom-bar buttons and, on the Rows page, the metrics ListView state at shot time.
    /// Used to measure the button sizes and to tell a blank list from a capture artifact.
    /// </summary>
    static void LogMeasures(SettingsForm form, string prefix, string page)
    {
        foreach (var b in Navigation.Walk(form).OfType<Button>()
                     .Where(b => b.Parent is FlowLayoutPanel p && p.Dock == DockStyle.Bottom))
            Console.WriteLine($"  [{prefix}/{page}] button '{b.Text}' bounds={b.Bounds} autosize={b.AutoSize} font={b.Font.SizeInPoints:0.##}pt");
        if (Get(form, "_lines") is ListView lv)
            Console.WriteLine($"  [{prefix}/{page}] metrics ListView Items.Count={lv.Items.Count} BackColor={lv.BackColor} " +
                              $"Bounds={lv.Bounds} IsHandleCreated={lv.IsHandleCreated} ClientSize={lv.ClientSize} " +
                              $"Visible={lv.Visible} font={lv.Font.SizeInPoints:0.##}pt columns={string.Join("|", lv.Columns.Cast<ColumnHeader>().Select(c => $"{c.Text}:{c.Width}"))}");
    }

    static object? Get(object o, string name) =>
        (o.GetType().GetField(name, Any) ?? throw new MissingFieldException(name)).GetValue(o);
}
