using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace FrameGlass;

/// <summary>
/// Fluent System Icons glyphs, drawn from an embedded icon font, used in place of hand-drawn GDI marks
/// (checkboxes, spinner chevrons, tray icons).
///
/// Source: https://github.com/microsoft/fluentui-system-icons (MIT), file
/// fonts/FluentSystemIcons-Regular.ttf at pinned commit 9eb184e96268d3293462f9d9d19225b4c7b07774.
/// The embedded file is a subset of 12 glyphs; see assets/README.md for the subset command and licence.
///
/// Codepoints are NOT stable across font releases. If the font is updated, re-read the codepoints from
/// the pinned JSON and update this file and assets/README.md together.
///
/// Every failure path (missing resource, bad font data) logs once and makes <see cref="Available"/>
/// false. Callers must keep their GDI fallback, so a font problem never breaks the UI.
/// </summary>
internal static class Icons
{
    /// <summary>Embedded manifest resource name (LogicalName in FrameGlass.csproj and ui-probe/UiProbe.csproj).</summary>
    const string ResourceName = "FrameGlass.FluentIcons.ttf";

    // Codepoints are written as escapes, never as raw private-use characters, so they survive editors and diffs.
    /// <summary>checkbox_checked (20 px), used for a row that is on.</summary>
    public const char CheckboxChecked = '\uF28D';
    /// <summary>checkbox_unchecked (20 px), used for a row that is off.</summary>
    public const char CheckboxUnchecked = '\uF291';
    /// <summary>chevron_up (20 px), spinner up.</summary>
    public const char ChevronUp = '\uF2B6';
    /// <summary>chevron_down (20 px), spinner down.</summary>
    public const char ChevronDown = '\uF2A3';
    /// <summary>eye (20 px), tray: "Show overlay" action, shown while the overlay is hidden.</summary>
    public const char Eye = '\uE5F2';
    /// <summary>eye_off (20 px), tray: "Hide overlay" action, shown while the overlay is visible.</summary>
    public const char EyeOff = '\uE5F5';
    /// <summary>settings (20 px), tray: Settings.</summary>
    public const char Settings = '\uF6A9';
    /// <summary>dismiss (20 px), tray: Exit.</summary>
    public const char Dismiss = '\uF369';
    /// <summary>layer (20 px), sidebar: Overlay section.</summary>
    public const char NavOverlay = '\uF4CB';
    /// <summary>text_bullet_list_ltr (20 px), sidebar: Rows section.</summary>
    public const char NavRows = '\uECD9';
    /// <summary>options (20 px), sidebar: General section.</summary>
    public const char NavGeneral = '\uF587';
    /// <summary>info (20 px), sidebar: About section.</summary>
    public const char NavAbout = '\uF4A3';

    /// <summary>All glyph constants above, so the probe can check every one of them renders.</summary>
    public static readonly char[] All =
    {
        CheckboxChecked, CheckboxUnchecked, ChevronUp, ChevronDown,
        Eye, EyeOff, Settings, Dismiss,
        NavOverlay, NavRows, NavGeneral, NavAbout,
    };

    // Lazy gives one load per process, and ExecutionAndPublication makes the load thread-safe.
    // The factory catches everything, so reading Value never throws.
    static readonly Lazy<FontFamily?> _family = new(LoadFamily, LazyThreadSafetyMode.ExecutionAndPublication);

    // GDI+ does not copy the bytes given to AddMemoryFont; it reads them whenever it needs glyph data.
    // The collection is therefore kept in a static field, and the buffer is never freed, because a
    // FontFamily from the collection can be used at any time for the rest of the process.
    // Freeing it early is the crash the plan warns about (2026-11-01 plan, risk table).
    static PrivateFontCollection? _collection;

    /// <summary>True when the icon font loaded. The first read triggers the one-time load.</summary>
    public static bool Available => _family.Value is not null;

    /// <summary>
    /// Renders one glyph into a new px x px 32 bpp ARGB bitmap with a transparent background.
    /// Returns null when the font is unavailable. The caller owns and must dispose the bitmap.
    /// </summary>
    public static Bitmap? Render(char glyph, int px, Color color)
    {
        if (!Available || px <= 0) return null;

        // 32 bpp ARGB so the background stays transparent for tray and overlay compositing.
        var bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            // AntiAlias, not AntiAliasGridFit: grid fitting fringes on transparent bitmaps.
            // The glyph is drawn as a path (see Draw), so this hint only matters for any text-based fallback.
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            Draw(g, glyph, new RectangleF(0, 0, px, px), color);
        }
        return bmp;
    }

    /// <summary>
    /// Paints one glyph centred in <paramref name="r"/>, sized so its ink fits the smaller side of the rect.
    /// No-op when the font is unavailable. Does not change the caller's Graphics state.
    /// </summary>
    public static void Draw(Graphics g, char glyph, RectangleF r, Color color)
    {
        var family = _family.Value;
        if (family is null) return;

        float size = Math.Min(r.Width, r.Height);
        if (size <= 0) return;

        // Build a GraphicsPath instead of DrawString or TextRenderer. TextRenderer ignores private
        // fonts, and DrawString can only centre by font metrics, which leave the ink off-centre.
        // The path's bounds are the real ink extents, so centring on them is exact.
        using var path = new GraphicsPath();
        path.AddString(glyph.ToString(), family, (int)FontStyle.Regular, size,
            PointF.Empty, StringFormat.GenericTypographic);

        RectangleF ink = path.GetBounds();
        if (ink.Width <= 0 || ink.Height <= 0) return; // glyph missing from the subset, or empty

        // Move the ink's own bounding box to the centre of r.
        using (var move = new Matrix())
        {
            move.Translate(
                r.X + (r.Width - ink.Width) / 2f - ink.X,
                r.Y + (r.Height - ink.Height) / 2f - ink.Y);
            path.Transform(move);
        }

        // Restore the caller's smoothing mode afterwards so that Draw has no side effects on g.
        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }
        finally
        {
            g.SmoothingMode = previous;
        }
    }

    /// <summary>
    /// One-time load. Copies the embedded font into unmanaged memory that is never freed, registers it
    /// with a PrivateFontCollection, and returns its family. Returns null and logs once on any failure.
    /// </summary>
    static FontFamily? LoadFamily()
    {
        try
        {
            using var stream = typeof(Icons).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                Program.Log($"icons: resource {ResourceName} missing; using GDI fallback");
                return null;
            }

            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            byte[] data = copy.ToArray();

            IntPtr mem = Marshal.AllocCoTaskMem(data.Length);
            Marshal.Copy(data, 0, mem, data.Length);

            var collection = new PrivateFontCollection();
            try
            {
                collection.AddMemoryFont(mem, data.Length);
            }
            catch
            {
                // Font rejected, so nothing references the buffer. Free it, then rethrow to the outer catch.
                collection.Dispose();
                Marshal.FreeCoTaskMem(mem);
                throw;
            }

            // Accepted: keep the collection and the buffer alive for the process lifetime.
            _collection = collection;
            if (collection.Families.Length == 0)
            {
                Program.Log("icons: font loaded but has no families; using GDI fallback");
                return null;
            }
            return collection.Families[0];
        }
        catch (Exception ex)
        {
            Program.Log($"icons: font load failed ({ex.GetType().Name}: {ex.Message}); using GDI fallback");
            return null;
        }
    }
}
