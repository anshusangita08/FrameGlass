using System.Drawing.Imaging;

namespace UiProbe;

/// <summary>
/// Renders the README images from the app's own drawing code.
///
/// Not a screen capture. Composer.Compose is the same call the overlay makes every tick, so the
/// mark, the layering and the panel are the real ones; only the readings are sample values, because
/// the machine this was built on is not always running a game. The README says so under the image.
///
/// The settings image is different: it is a real capture of the real window, cropped to the window
/// so nothing else on the desktop comes with it.
/// </summary>
static class Hero
{
    public static void Run()
    {
        var cfg = new FrameGlass.Config
        {
            // A panel behind the text, because the README page is not a game and unbacked text is
            // unreadable against a white or a dark theme. Both are real settings, on the Design tab.
            Box = true,
            PanelOpacity = 1,
            Layout = "stack",
            Gap = 10,
            GraphWidth = 220,
            GraphHeight = 96,
            Lines = ["game", "fps", "frametime", "graph",
                     "cpu", "cputemp", "cpuload", "cpupower",
                     "gpu", "gputemp", "gpuload", "gpupower",
                     "ram", "vram", "time"],

            // Labels, added the way a user adds them: the defaults are bare tokens, so anything
            // showing a caption is a configured row. This is also the clearest way for the image to
            // show what the templating is for.
            Formats = new Dictionary<string, string>
            {
                ["fps"] = "FPS {fps}",
                ["frametime"] = "{framems}",                    // {framems} already carries "ms"
                ["ram"] = "RAM   {ram} / {ramtotal}",           // and the memory tokens carry GB
                ["vram"] = "VRAM {vram} / {vramtotal}",
                ["cpu"] = "{cpus}",
                ["cputemp"] = "temp {cputemp}",
                ["cpuload"] = "load {cpuload}",
                ["cpupower"] = "power {cpupower}",
                ["gpu"] = "{gpu}",
                ["gputemp"] = "temp {gputemp}",
                ["gpuload"] = "load {gpuload}",
                ["gpupower"] = "power {gpupower}",
            },
            TimeFormat = "HH:mm",
            Clock24Hour = true,
        };

        var frames = new FrameGlass.FrameStats(
            Fps: 141, AvgFps: 139, Max: 168, Min: 92, Low1: 98, Low10: 121, MsAvg: 7.09,
            Plot: Plot());
        var reading = new FrameGlass.Reading
        {
            Ok = true,
            GameName = "Cyberpunk 2077",
            AppName = "Cyberpunk2077",
            CpuName = "AMD Ryzen 5 5600X",
            GpuName = "AMD Radeon RX 9060 XT",
            CpuLoad = 42, CpuTemp = 58, CpuClock = 4650, CpuPower = 68,
            GpuLoad = 97, GpuTemp = 62, GpuHot = 74, GpuClock = 2714, GpuPower = 187,
            RamUsed = 11.2f, RamTotal = 32, VramUsed = 7.4f, VramTotal = 16,
            Ping = "14ms", PingMs = 14,
        };

        string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "docs", "images"));
        Directory.CreateDirectory(dir);

        // 2x the body size so the image stays crisp when the README scales it down.
        using (var font = new Font("Consolas", 26, GraphicsUnit.Point))
        using (var bmp = FrameGlass.Composer.Compose(cfg, font, frames, reading, opaque: false))
        {
            string outPath = Path.Combine(dir, "overlay.png");
            bmp.Save(outPath, ImageFormat.Png);
            Console.WriteLine($"overlay.png : {bmp.Width}x{bmp.Height}");
        }

    }

    /// <summary>A frametime trace with the shape a real cap produces: tight, with a few spikes.</summary>
    static double[] Plot()
    {
        var rng = new Random(7);
        var ms = new double[240];
        for (int i = 0; i < ms.Length; i++)
        {
            ms[i] = 7.0 + rng.NextDouble() * 1.2;
            if (i % 37 == 0) ms[i] += 4.5;      // an occasional late frame
        }
        return ms;
    }
}
