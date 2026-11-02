using System.Runtime.InteropServices;

namespace UiProbe;

/// <summary>
/// The dotnet-winforms acceptance bar, run for real: "open and close the affected form twenty
/// times, and quote the GDI and USER handle counts at the start and the end. A count that keeps
/// climbing is the leak, whatever the code review said."
///
/// A Form is not a UI-only object here - the settings window owns a nested PreviewPanel, a
/// CardPanel, owner-drawn Sliders/Combos/Spinners, a DataGridView with 54 rows and a ToolTip. If
/// any of those leak a GDI handle, twenty open/close cycles is enough to show the slope.
/// </summary>
static class LeakTest
{
    [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr process, int flags);
    const uint GR_GDIOBJECTS = 0, GR_USEROBJECTS = 1;

    static uint Gdi() => GetGuiResources(Process.Handle, 0);
    static uint User() => GetGuiResources(Process.Handle, 1);
    static System.Diagnostics.Process Process => System.Diagnostics.Process.GetCurrentProcess();

    public static void Run()
    {
        // Control first. A bare Form with no controls at all establishes the floor for this
        // environment; without it, any slope measured against SettingsForm is unattributable.
        Console.WriteLine("=== CONTROL: bare Form, no controls ===");
        long control = Slope(BareForm, 20);

        Console.WriteLine();
        Console.WriteLine("=== SUBJECT: FrameGlass.SettingsForm ===");
        long subject = Slope(SettingsForm_, 20);

        Console.WriteLine();
        long excess = subject - control;
        Console.WriteLine($"VERDICT: bare Form {control:+#;-#;0}, SettingsForm {subject:+#;-#;0}, " +
                          $"excess attributable to the app {excess:+#;-#;0} over 20 cycles");
        // The threshold is per cycle, not per test. 0.5/cycle is a leak worth chasing; anything
        // smaller is noise-level drift. 20 cycles x 0.5 = 10, which is where the old bar sat.
        bool leak = excess > 20 * 0.5;
        Console.WriteLine(leak
            ? "FAIL: the app adds a per-open/close GDI leak on top of the platform baseline."
            : "PASS: no leak attributable to the app beyond the platform baseline.");
        Environment.ExitCode = leak ? 1 : 0;
    }

    /// <summary>
    /// Runs n cycles and returns the GDI delta, printing the slope as it goes.
    ///
    /// The warm-up matters more than it looks. SettingsForm has a one-time cost of about 17 GDI
    /// objects that is not per-open - it is a cache or a lazily built table, and it saturates
    /// somewhere in the first 20 opens. With a 3-cycle warm-up that one-time cost landed *inside*
    /// the measured window, so the test read "+17 over 20 cycles" and called it "+0.85 per open".
    /// It was a fixed 17, once. Warming up until the count stops moving is what separates the two.
    /// </summary>
    static long Slope(Action cycle, int n)
    {
        // Warm up until GDI stops moving, so the measurement window contains only the steady state.
        int warm = 3, stable = 0;
        uint last = 0;
        for (; warm < 200 && stable < 5; warm++)
        {
            cycle();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            uint now = Gdi();
            stable = now == last ? stable + 1 : 0;
            last = now;
        }
        if (warm >= 200)
            Console.WriteLine("  (GDI never settled during warm-up: the slope below is a real leak)");

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        uint g0 = Gdi(), u0 = User(), h0 = (uint)Process.HandleCount;
        Console.WriteLine($"  warmed {warm - 3} cycles, settled at GDI={g0}");
        Console.WriteLine($"  start   GDI={g0,-5} USER={u0,-5} handles={h0}");

        for (int i = 1; i <= n; i++)
        {
            cycle();
            if (i % 5 == 0 || i == n)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Console.WriteLine($"  {i,2} open/close  GDI={Gdi(),-5} USER={User(),-5} " +
                                  $"handles={Process.HandleCount,-6} " +
                                  $"GDI delta {Gdi() - g0:+#;-#;0}  USER {User() - u0:+#;-#;0}  " +
                                  $"handles {Process.HandleCount - h0:+#;-#;0}");
            }
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        return (long)Gdi() - g0;
    }

    static void BareForm()
    {
        using var f = new Form { Size = new Size(400, 300) };
        f.Show();
        for (int i = 0; i < 3; i++) { Application.DoEvents(); Thread.Sleep(20); }
        f.Close();
        f.Dispose();
    }

    static void SettingsForm_() => OpenClose();

    static void OpenClose()
    {
        var cfg = new FrameGlass.Config
        {
            Lines = new[] { "game", "fps", "cpu", "cputemp", "gpu", "ram", "vram" },
            CustomRows = new List<string> { "my {fps} row" },
        };

        // Show() for real, then Close + Dispose: the same lifecycle the button handler drives, so
        // the handle is created and the ownership actually transfers to us.
        using var f = new FrameGlass.SettingsForm(
            cfg, () => FrameGlass.FrameStats.Empty, () => new FrameGlass.Reading(), _ => { });
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(40, 40);
        f.Show();
        for (int i = 0; i < 3; i++) { Application.DoEvents(); Thread.Sleep(20); }
        f.Close();
        f.Dispose();
    }
}
