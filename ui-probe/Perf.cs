using System.Diagnostics;

namespace UiProbe;

/// <summary>
/// "The sliders feel sluggish" is a claim about time, so measure the time. Live() runs on every
/// Scroll and every TextChanged, and it does a blocking File.WriteAllText plus a full Compose()
/// bitmap render. One of those per mouse-move event is the obvious suspect, but obvious is not
/// measured - so time each part, then time a simulated drag.
/// </summary>
static class Perf
{
    public static void Run()
    {
        // Time the whole path a real drag takes: show the form, then push values through the actual
        // Scroll handler and measure the wall clock per step with the message pump running. The
        // per-part numbers below are arithmetic; this is the thing the user feels.
        Drag();
        Console.WriteLine();
        PreviewDrag();
        Console.WriteLine();
        DoesPreviewUpdate();
        Console.WriteLine();
        SaveLands();
        Console.WriteLine();
        SliderReachesApply();
        Console.WriteLine();
        var cfg = new FrameGlass.Config
        {
            Lines = new[] { "game", "res", "fps", "frametime", "graph", "cpu", "cputemp", "cpuload",
                            "cpupower", "gpu", "gputemp", "gpuload", "gpupower", "ram", "vram" },
            CustomRows = new List<string> { "my {fps} row" },
        };
        using var font = new Font("Consolas", 13, GraphicsUnit.Point);
        var frames = FrameGlass.FrameStats.Empty;
        var reading = new FrameGlass.Reading();

        // Warm up: first Compose and first Save both pay one-off costs (font caches, file creation).
        for (int i = 0; i < 3; i++)
        {
            FrameGlass.Composer.Compose(cfg, font, frames, reading).Dispose();
            FrameGlass.Config.Save(cfg);
        }

        Console.WriteLine("What one drag step costs. A Scroll event fires per mouse-move, so a");
        Console.WriteLine("one-second drag is roughly 50-100 of these.");
        Console.WriteLine();
        Console.WriteLine($"{"operation",-42} {"ms each",-10} verdict");
        Console.WriteLine(new string('-', 74));

        double compose = Time(() => FrameGlass.Composer.Compose(cfg, font, frames, reading).Dispose());
        double save = Time(() => FrameGlass.Config.Save(cfg));
        double build = Time(() => { var f = new Font("Consolas", 13, GraphicsUnit.Point); f.Dispose(); });

        Row("Config.Save  (blocking File.WriteAllText)", save, 8);
        Row("Composer.Compose  (full bitmap render)", compose, 8);
        Row("new Font  (Build, per Live)", build, 2);
        Row("Live() total, if it is the sum of the above", compose + save + build, 16);

        Console.WriteLine();
        Console.WriteLine("A slider drag is only smooth if one step lands well under the ~16ms frame.");
        Console.WriteLine($"Measured here: {(compose + save + build):0.0} ms per step, " +
                          $"so a drag runs at about {1000.0 / Math.Max(1, compose + save + build):0} updates/second.");
    }

    /// <summary>
    /// Walk the real form the way a drag does: raise Scroll on an actual slider and pump messages
    /// between steps, so every repaint the cascade causes is included. Comparing this against the
    /// Live()-only number says whether the cost is in the work or in the repaint.
    /// </summary>
    static void Drag()
    {
        var cfg = new FrameGlass.Config
        {
            Lines = new[] { "game", "res", "fps", "frametime", "graph", "cpu", "cputemp", "cpuload",
                            "cpupower", "gpu", "gputemp", "gpuload", "gpupower", "ram", "vram" },
            CustomRows = new List<string> { "my {fps} row" },
        };

        using var f = new FrameGlass.SettingsForm(
            cfg, () => FrameGlass.FrameStats.Empty, () => new FrameGlass.Reading(), _ => { });
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(40, 40);
        f.Show();
        for (int i = 0; i < 3; i++) { Application.DoEvents(); Thread.Sleep(20); }

        var sliders = Find(f).OfType<FrameGlass.Theme.Slider>().ToList();
        Console.WriteLine($"Sliders on the first page: {sliders.Count}  (a drag repaints these containers too)");

        var bar = sliders.FirstOrDefault();
        if (bar is null) { Console.WriteLine("no Theme.Slider found"); f.Close(); f.Dispose(); return; }

        // Nothing in Skin() sets a Theme.Slider's BackColor, so what does it actually resolve to?
        var pb = bar.BackColor;
        Console.WriteLine($"slider BackColor = #{pb.R:X2}{pb.G:X2}{pb.B:X2}  " +
                          $"(parent {bar.Parent?.GetType().Name} = " +
                          $"#{bar.Parent?.BackColor.R:X2}{bar.Parent?.BackColor.G:X2}{bar.Parent?.BackColor.B:X2})");
        Console.WriteLine($"slider size = {bar.Width}x{bar.Height}  DoubleBuffered={bar.GetType()}");
        Console.WriteLine($"canvas = #{FrameGlass.Theme.Canvas.R:X2}{FrameGlass.Theme.Canvas.G:X2}{FrameGlass.Theme.Canvas.B:X2}  " +
                          $"ink = #{FrameGlass.Theme.Ink.R:X2}{FrameGlass.Theme.Ink.G:X2}{FrameGlass.Theme.Ink.B:X2}  " +
                          $"line = #{FrameGlass.Theme.Line.R:X2}{FrameGlass.Theme.Line.G:X2}{FrameGlass.Theme.Line.B:X2}");

        // Warm up, then time 40 steps with the pump running, exactly as a drag would.
        for (int i = 0; i < 5; i++) { bar.Value = 40 + i; Application.DoEvents(); }

        // Capture BEFORE any drag, so "always like this" and "only during a drag" are told apart.
        // Without this the after-shot alone cannot say which one the artifact is.
        try
        {
            var b0 = bar.RectangleToScreen(bar.ClientRectangle);
            using var s0 = new Bitmap(Math.Max(1, b0.Width), Math.Max(1, b0.Height));
            using (var g0 = Graphics.FromImage(s0))
                g0.CopyFromScreen(b0.Left, b0.Top, 0, 0, s0.Size);
            s0.Save(Path.Combine(AppContext.BaseDirectory, "drag-before.png"),
                    System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("captured the slider before the drag -> drag-before.png");
        }
        catch (Exception e) { Console.WriteLine("pre-capture failed: " + e.Message); }

        var sw = Stopwatch.StartNew();
        const int steps = 40;
        for (int i = 0; i < steps; i++)
        {
            bar.Value = 10 + (i * 80 / steps);      // as Scroll would
            Application.DoEvents();                 // the repaints a real drag forces
        }
        sw.Stop();
        double per = sw.Elapsed.TotalMilliseconds / steps;

        Console.WriteLine($"real drag step, repaints included : {per,6:0.00} ms");
        Console.WriteLine($"  -> {(per > 16 ? "BELOW 60fps: visibly laggy" : per > 8 ? "ok-ish" : "smooth")}");

        // Timing said this was fast, so timing is not what the user is seeing. Capture the slider's
        // actual pixels off the screen: DrawToBitmap would render a clean paint and could never show
        // a trail, which is exactly the mistake this is here to avoid.
        try
        {
            var box = bar.RectangleToScreen(bar.ClientRectangle);
            using var shot = new Bitmap(Math.Max(1, box.Width), Math.Max(1, box.Height));
            using (var g = Graphics.FromImage(shot))
                g.CopyFromScreen(box.Left, box.Top, 0, 0, shot.Size);
            string outPath = Path.Combine(AppContext.BaseDirectory, "drag-trail.png");
            shot.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"captured the slider after the drag -> {outPath}");
        }
        catch (Exception e) { Console.WriteLine("capture failed: " + e.Message); }

        f.Close();
        f.Dispose();
    }

    /// <summary>
    /// The preview drag is the one path that runs per mouse-MOVE rather than per Scroll, and it is
    /// the one the user described as broken. Setting bar.Value above never fires Scroll, so that
    /// measurement missed it entirely. Raise real MouseMove events at the preview instead.
    /// </summary>
    static void PreviewDrag()
    {
        var cfg = new FrameGlass.Config
        {
            Lines = new[] { "game", "res", "fps", "frametime", "graph", "cpu", "cputemp", "cpuload",
                            "cpupower", "gpu", "gputemp", "gpuload", "gpupower", "ram", "vram" },
            CustomRows = new List<string> { "my {fps} row" },
        };

        using var f = new FrameGlass.SettingsForm(
            cfg, () => FrameGlass.FrameStats.Empty, () => new FrameGlass.Reading(), _ => { });
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(40, 40);
        f.Show();
        for (int i = 0; i < 3; i++) { Application.DoEvents(); Thread.Sleep(20); }

        var preview = Find(f).OfType<Panel>()
            .FirstOrDefault(p => p.Cursor == Cursors.SizeAll);
        if (preview is null) { Console.WriteLine("preview panel not found"); f.Close(); f.Dispose(); return; }

        Console.WriteLine($"Preview drag: {preview.GetType().Name}, {preview.Width}x{preview.Height}");

        // What a real drag delivers: a MouseMove per mouse-move, with the button held.
        var down = new MouseEventArgs(MouseButtons.Left, 1, 10, 10, 0);
        typeof(Panel).GetMethod("OnMouseDown", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!.Invoke(preview, [down]);
        for (int i = 0; i < 5; i++)
        {
            typeof(Panel).GetMethod("OnMouseMove", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!.Invoke(preview,
                [new MouseEventArgs(MouseButtons.Left, 0, 20 + i, 20, 0)]);
            Application.DoEvents();
        }

        var sw = Stopwatch.StartNew();
        const int steps = 60;
        for (int i = 0; i < steps; i++)
        {
            typeof(Panel).GetMethod("OnMouseMove", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!.Invoke(preview,
                [new MouseEventArgs(MouseButtons.Left, 0, 30 + (i * 3 % 200), 30 + (i % 60), 0)]);
            Application.DoEvents();
        }
        sw.Stop();
        double per = sw.Elapsed.TotalMilliseconds / steps;
        Console.WriteLine($"preview MouseMove step          : {per,6:0.00} ms");
        Console.WriteLine($"  -> {(per > 16 ? "BELOW 60fps: the preview lags the cursor" : per > 8 ? "ok-ish" : "smooth")}");

        f.Close();
        f.Dispose();
    }

    /// <summary>
    /// "The preview is stuck" is a claim about whether the pixels change. Check that directly:
    /// drag a slider that the preview depends on, and compare the preview's rendered bitmap before
    /// and after. Timing said the drag was fast, so the question is not speed - it is whether
    /// Live() reaches _preview.Update at all when it comes from a Scroll event.
    /// </summary>
    static void DoesPreviewUpdate()
    {
        var cfg = new FrameGlass.Config
        {
            Lines = new[] { "game", "res", "fps", "frametime", "graph", "cpu", "cputemp", "cpuload",
                            "cpupower", "gpu", "gputemp", "gpuload", "gpupower", "ram", "vram" },
            CustomRows = new List<string> { "my {fps} row" },
        };

        using var f = new FrameGlass.SettingsForm(
            cfg, () => FrameGlass.FrameStats.Empty, () => new FrameGlass.Reading(), _ => { });
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(40, 40);
        f.Show();
        for (int i = 0; i < 3; i++) { Application.DoEvents(); Thread.Sleep(30); }

        var preview = Find(f).OfType<Panel>().FirstOrDefault(p => p.Cursor == Cursors.SizeAll);
        if (preview is null) { Console.WriteLine("preview not found"); f.Close(); f.Dispose(); return; }

        // Opacity is a slider the overlay actually renders, so a change to it must alter the pixels.
        var op = Find(f).OfType<FrameGlass.Theme.Slider>().FirstOrDefault(t => t.Name.Contains("opac", StringComparison.OrdinalIgnoreCase))
                 ?? Find(f).OfType<FrameGlass.Theme.Slider>().Skip(1).FirstOrDefault();

        string before = Hash(preview);
        Console.WriteLine($"preview hash before any change : {before}");

        // Theme.Slider raises ValueChanged on every Value change (drag, click, keyboard or code), so a
        // plain set takes the same path a drag does. The old TrackBar needed a reflected OnScroll call.
        if (op is not null)
        {
            for (int i = 0; i < 5; i++)
            {
                op.Value = 20 + i * 15;
                Application.DoEvents();
            }
            Console.WriteLine($"slider '{op.Name}' now at {op.Value}");
        }

        string after = Hash(preview);
        Console.WriteLine($"preview hash after slider drag  : {after}");
        Console.WriteLine(before != after
            ? "  -> the preview DOES redraw on a slider change"
            : "  -> the preview does NOT redraw on a slider change. That is the 'stuck' report.");

        // And the control that says it is the same one the user named.
        var live = Find(f).OfType<System.Windows.Forms.Timer>().FirstOrDefault();
        Console.WriteLine($"timer interval                  : {live?.Interval} ms");

        f.Close();
        f.Dispose();
    }

    /// <summary>
    /// Live() no longer writes the config on every edit - it defers to the timer. That is a
    /// data-loss path if the deferral never lands, and "the file looks fine" is not evidence, so:
    /// edit, close the form, and check the file on disk actually changed to match.
    /// </summary>
    public static void SaveLands()
    {
        string path = FrameGlass.Config.ConfigPath;
        string before = File.Exists(path) ? File.ReadAllText(path) : "";
        var beforeCfg = File.Exists(path) ? FrameGlass.Config.Load() : new FrameGlass.Config();

        var cfg = new FrameGlass.Config();
        using (var f = new FrameGlass.SettingsForm(
            cfg, () => FrameGlass.FrameStats.Empty, () => new FrameGlass.Reading(), _ => { }))
        {
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(40, 40);
            f.Show();
            for (int i = 0; i < 3; i++) { Application.DoEvents(); Thread.Sleep(30); }

            // Something the config actually stores, changed the way the UI changes it.
            var gap = Find(f).OfType<NumericUpDown>().FirstOrDefault();
            if (gap is null) { Console.WriteLine("  no NumericUpDown to edit"); return; }
            gap.Value = gap.Value == 7 ? 8 : 7;
            Application.DoEvents();
            Console.WriteLine($"  edited a spinner to {gap.Value}, then closing the form");

            // Deliberately do NOT wait for the 1s timer: closing must flush by itself.
            f.Close();
        }

        string after = File.Exists(path) ? File.ReadAllText(path) : "";
        var afterCfg = FrameGlass.Config.Load();
        Console.WriteLine($"  config changed on disk : {before != after}");
        Console.WriteLine($"  Gap in file            : {beforeCfg.Gap} -> {afterCfg.Gap}");
        Console.WriteLine(before != after
            ? "  -> the deferred write lands on close"
            : "  -> LOST: the deferred write never reached disk");

        // Put it back, so running this does not leave the user's config edited.
        FrameGlass.Config.Save(beforeCfg);
    }

    /// <summary>
    /// A slider whose value changed must reach the apply callback, because that is the only thing
    /// that moves the overlay. This is the reported bug in a form that can be tested without a
    /// mouse: "the overlay does not follow the slider, but it does after a save or load".
    ///
    /// It is also the reason the regressed wiring was invisible: setting Value programmatically
    /// raises ValueChanged but NOT Scroll, so a Scroll-based handler looks fine while the user, whose
    /// drags do raise Scroll, gets nothing.
    /// </summary>
    static void SliderReachesApply()
    {
        int applied = 0;
        FrameGlass.Config? last = null;
        var cfg = new FrameGlass.Config();
        using var f = new FrameGlass.SettingsForm(
            cfg, () => FrameGlass.FrameStats.Empty, () => new FrameGlass.Reading(),
            c => { applied++; last = c; });
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(40, 40);
        f.Show();
        for (int i = 0; i < 3; i++) { Application.DoEvents(); Thread.Sleep(30); }

        var bar = Find(f).OfType<FrameGlass.Theme.Slider>().FirstOrDefault();
        if (bar is null) { Console.WriteLine("  no Theme.Slider found"); f.Close(); f.Dispose(); return; }

        int appliedBefore = applied;
        bar.Value = bar.Value >= 80 ? 30 : 80;      // move it somewhere else
        Application.DoEvents(); Thread.Sleep(80);

        Console.WriteLine($"  slider {bar.Value}, apply ran {applied - appliedBefore} time(s)" +
                          (last is null ? "" : $", config X={last.X}"));
        Console.WriteLine(applied > appliedBefore
            ? "  -> a slider change reaches apply, so the overlay follows it"
            : "  -> BROKEN: a slider change never reaches apply, so the overlay will not move");

        f.Close();
        f.Dispose();
    }

    /// <summary>A content hash of what the control actually painted, so "changed" is measurable.</summary>
    static string Hash(Control c)
    {
        try
        {
            var bmp = new Bitmap(Math.Max(1, c.Width), Math.Max(1, c.Height));
            c.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            bmp.Dispose();
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(ms.ToArray()))[..12];
        }
        catch (Exception e) { return "err:" + e.GetType().Name; }
    }

    static IEnumerable<Control> Find(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in Find(c)) yield return d;
        }
    }

    static void Row(string name, double ms, double budget) =>
        Console.WriteLine($"{name,-42} {ms,10:0.00} {(ms > budget ? "TOO SLOW" : "ok")}");

    static double Time(Action a)
    {
        const int n = 20;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++) a();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / n;
    }
}
