using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UiProbe;

/// <summary>
/// Tests the slider with REAL mouse input, which is the only way to reach the paths a user reaches.
///
/// This file exists because the earlier harness drove Value from code. Setting Value raises
/// ValueChanged but not Scroll, so a slider whose user-facing notification was dead still measured
/// green. Everything here is a genuine click or drag through SendInput.
/// </summary>
static class Drag
{
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern void mouse_event(uint f, int dx, int dy, uint data, UIntPtr extra);
    const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;

    /// <summary>Set once the control case proves synthesised input is delivered at all.</summary>
    static bool _inputWorks;

    public static void Run()
    {
        Control();
        Console.WriteLine();
        SliderClicks();
        Console.WriteLine();
        SliderDrags();
        Console.WriteLine();
        AppSlider();
    }

    /// <summary>
    /// A plain TrackBar, clicked for real. If nothing happens here the synthesiser is not reaching
    /// any window in this session and every result below is meaningless - which is the trap the
    /// first version of this file fell into when it blamed the app.
    /// </summary>
    static void Control()
    {
        using var f = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40), TopMost = true };
        var plain = new TrackBar { Minimum = 0, Maximum = 100, TickStyle = TickStyle.None, Width = 150, Height = 45 };
        f.Controls.Add(plain);
        f.Show(); f.Activate(); SetForegroundWindow(f.Handle);
        Pump(4);

        int scrolls = 0; plain.Scroll += (_, _) => scrolls++;
        ClickCentreRight(plain);
        _inputWorks = plain.Value > 0;

        Console.WriteLine($"CONTROL plain TrackBar after a real click: value={plain.Value} scrolls={scrolls}");
        Console.WriteLine(_inputWorks
            ? "  synthesised input IS delivered here, so the results below are meaningful"
            : "  synthesised input reaches NOTHING here; results below cannot be trusted");

        f.Close(); f.Dispose();
    }

    /// <summary>A click on the track must move the slider and raise ValueChanged.</summary>
    static void SliderClicks()
    {
        using var f = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40), TopMost = true };
        var bar = new FrameGlass.Theme.Slider { Minimum = 0, Maximum = 100, Width = 150, Height = 45 };
        f.Controls.Add(bar);
        f.Show(); f.Activate(); SetForegroundWindow(f.Handle);
        Pump(4);

        int changes = 0; bar.ValueChanged += (_, _) => changes++;
        int before = bar.Value;

        // Click at 75% of the track. A slider jumps to the position clicked; a TrackBar pages by
        // one step, so the value that comes back is itself evidence of which behaviour we have.
        var box = bar.RectangleToScreen(bar.ClientRectangle);
        Click(box.Left + box.Width * 3 / 4, box.Top + box.Height / 2);

        Console.WriteLine($"click at 75% of the track : value {before} -> {bar.Value}, ValueChanged {changes}x");
        Console.WriteLine(bar.Value > 60 && changes > 0
            ? "  ok: a click jumps to the position and raises the event"
            : "  FAIL: a real click did not move the slider or did not raise the event");

        f.Close(); f.Dispose();
    }

    /// <summary>A drag must track the pointer, from one end to the other, raising the event as it goes.</summary>
    static void SliderDrags()
    {
        using var f = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40), TopMost = true };
        var bar = new FrameGlass.Theme.Slider { Minimum = 0, Maximum = 100, Width = 150, Height = 45 };
        f.Controls.Add(bar);
        f.Show(); f.Activate(); SetForegroundWindow(f.Handle);
        Pump(4);

        int changes = 0; bar.ValueChanged += (_, _) => changes++;
        bar.Value = 0;
        changes = 0;

        var box = bar.RectangleToScreen(bar.ClientRectangle);
        int y = box.Top + box.Height / 2;

        SetCursorPos(box.Left + 4, y); Pump(1);
        mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero); Pump(1);
        for (int i = 1; i <= 20; i++)
        {
            SetCursorPos(box.Left + 4 + (box.Width - 8) * i / 20, y);
            Application.DoEvents(); Thread.Sleep(10);
        }
        mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);
        Pump(2);

        Console.WriteLine($"drag left to right       : value={bar.Value}, ValueChanged {changes}x");
        Console.WriteLine(bar.Value > 90 && changes > 5
            ? "  ok: the drag tracks the pointer and the event fires throughout"
            : "  FAIL: the drag did not track, or fired too few events to follow it");

        f.Close(); f.Dispose();
    }

    /// <summary>
    /// The one that matters: the app's own form, clicked for real, must reach the apply callback -
    /// that callback is the only thing that moves the overlay.
    /// </summary>
    static void AppSlider()
    {
        int applied = 0;
        FrameGlass.Config? last = null;
        var cfg = new FrameGlass.Config();
        using var f = new FrameGlass.SettingsForm(
            cfg, () => FrameGlass.FrameStats.Empty, () => new FrameGlass.Reading(),
            c => { applied++; last = c; });
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(40, 40);
        f.TopMost = true;
        f.Show(); f.Activate(); SetForegroundWindow(f.Handle);
        Pump(5);

        var bar = Find(f).OfType<FrameGlass.Theme.Slider>().FirstOrDefault();
        if (bar is null) { Console.WriteLine("  no slider found in the form"); f.Close(); f.Dispose(); return; }

        var box = bar.RectangleToScreen(bar.ClientRectangle);
        int before = bar.Value, appliedBefore = applied;
        Click(box.Left + box.Width * 3 / 4, box.Top + box.Height / 2);

        Console.WriteLine($"app slider clicked       : value {before} -> {bar.Value}, apply ran {applied - appliedBefore}x" +
                          (last is null ? "" : $", config X={last.X}"));
        Console.WriteLine(applied > appliedBefore
            ? "  ok: a real click reaches apply, so the overlay follows the slider"
            : "  FAIL: the click never reached apply, so the overlay would not move");

        f.Close(); f.Dispose();
    }

    static void Pump(int n) { for (int i = 0; i < n; i++) { Application.DoEvents(); Thread.Sleep(25); } }

    static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        Application.DoEvents(); Thread.Sleep(60);
        mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);
        Application.DoEvents(); Thread.Sleep(150);
    }

    static void ClickCentreRight(TrackBar bar)
    {
        var box = bar.RectangleToScreen(bar.ClientRectangle);
        Click(box.Left + box.Width * 3 / 4, box.Top + box.Height / 2);
    }

    static IEnumerable<Control> Find(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in Find(c)) yield return d;
        }
    }
}
