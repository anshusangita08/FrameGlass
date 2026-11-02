using System.Text;

namespace UiProbe;

/// <summary>
/// Dumps the real geometry of one settings page's children (--tab N).
///
/// "Too much empty space" needs a number before it can be fixed: the suspect could be row margins,
/// the grid's padding, a control with a fixed height taller than its contents, or a percentage row
/// handing space to the wrong thing. This prints the bounds and the gap to the previous sibling, so
/// the space can be attributed rather than guessed at.
///
/// Pages are chosen through the sidebar (Theme.NavSidebar.SelectedIndex), the same way a user reaches
/// them. The exit code is 1 when the page index is out of range, when there is no sidebar, or when the
/// selection did not leave exactly the named page visible.
/// </summary>
static class Layout
{
    public static void Run(int tab)
    {
        var cfg = new FrameGlass.Config();
        using var f = new FrameGlass.SettingsForm(
            cfg, () => FrameGlass.FrameStats.Empty, () => new FrameGlass.Reading(), _ => { });
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(40, 40);
        f.Show();
        for (int i = 0; i < 4; i++) { Application.DoEvents(); Thread.Sleep(30); }

        var nav = Navigation.Nav(f);
        if (nav is null) { Fail(f, "no NavSidebar"); return; }
        if (tab < 0 || tab >= Navigation.PageNames.Length)
        {
            Fail(f, $"tab {tab} out of range (0..{Navigation.PageNames.Length - 1})");
            return;
        }

        nav.SelectedIndex = tab;
        Application.DoEvents(); Thread.Sleep(80);

        // Exactly one page must be visible, and it must be the one named by the index.
        var visible = Enumerable.Range(0, Navigation.PageNames.Length)
            .Where(i => Navigation.PageAt(f, i) is { Visible: true }).ToList();
        if (visible.Count != 1 || visible[0] != tab)
        {
            Console.WriteLine($"!! visible pages after selecting {tab}: [{string.Join(", ", visible)}]");
            Environment.ExitCode = 1;
        }

        var page = Navigation.PageAt(f, tab)!;
        Console.WriteLine($"tab {tab} '{Navigation.PageNames[tab]}'  page={page.Bounds}");
        Console.WriteLine();

        foreach (var child in Find(page))
        {
            var r = child.Bounds;
            string own = child.Parent is { } p && p != page
                ? $"  (in {p.GetType().Name} y={p.Bounds.Y} h={p.Bounds.Height})"
                : "";
            Console.WriteLine($"  {child.GetType().Name,-18} '{Trim(child.Text, 18),-18}' " +
                              $"y={r.Y,5} h={r.Height,4} bottom={r.Bottom,5} x={r.X,4} w={r.Width,4}{own}");
            if (child is TableLayoutPanel tlp && tlp.RowStyles.Count > 0)
            {
                var sb = new StringBuilder("      rows: ");
                for (int i = 0; i < tlp.RowStyles.Count; i++)
                    sb.Append($"[{i}] {tlp.RowStyles[i].SizeType}/{tlp.RowStyles[i].Height:0.#} h={tlp.GetRowHeights().ElementAtOrDefault(i)}  ");
                Console.WriteLine(sb.ToString());
            }
        }

        f.Close();
        f.Dispose();
    }

    /// <summary>Prints the reason, sets exit code 1, and disposes the form.</summary>
    static void Fail(Form f, string reason)
    {
        Console.WriteLine(reason);
        Environment.ExitCode = 1;
        f.Close();
        f.Dispose();
    }

    static string Trim(string s, int n) => s.Length <= n ? s.Replace("\n", " ") : s[..n].Replace("\n", " ");

    static IEnumerable<Control> Find(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in Find(c)) yield return d;
        }
    }
}
