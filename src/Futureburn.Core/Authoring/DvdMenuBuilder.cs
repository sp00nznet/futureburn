using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.Versioning;
using System.Text;

namespace Futureburn.Core.Authoring;

// Renders the still images for a DVD-Video menu:
//   - a full-colour BACKGROUND (the menu art — title + button labels), and
//   - a HIGHLIGHT and a SELECT overlay (transparent PNGs with an opaque outline
//     box around each button), which spumux turns into the button subpicture.
//
// DVD subpicture overlays are 2-bit — 4 colours total, transparent included.
// So highlight + select must, between them, use at most 4 colours and draw the
// SAME shapes (only the colour differs), or spumux fails with "cannot pick
// button masks". We honour that by drawing identical outline boxes with
// anti-aliasing OFF (anti-aliased edges would smuggle in extra colours): the
// highlight image is just {transparent, gold}, the select image {transparent,
// white} — three colours across both.
//
// Everything is rendered at the raw DVD frame size (720x480 NTSC / 720x576 PAL)
// and button rectangles are rounded to even coordinates (DVD subpicture RLE is
// 2-pixel-granular horizontally; odd coordinates shift the highlight).

[SupportedOSPlatform("windows")]
public static class DvdMenuBuilder
{
    /// <summary>One clickable button: its identity and on-screen rectangle.</summary>
    public sealed record MenuButton(string Name, int X0, int Y0, int X1, int Y1);

    /// <summary>A rendered menu — three PNG paths plus the button rectangles.</summary>
    public sealed record RenderedMenu(
        string BackgroundPng,
        string HighlightPng,
        string SelectPng,
        IReadOnlyList<MenuButton> Buttons);

    /// <summary>Max chapter buttons on the (single-page) scene menu.</summary>
    public const int MaxSceneButtons = 12;

    private static readonly Color Background  = Color.FromArgb(24, 26, 34);
    private static readonly Color TitleColor  = Color.FromArgb(245, 245, 250);
    private static readonly Color LabelColor  = Color.FromArgb(225, 227, 235);
    private static readonly Color HighlightHi = Color.FromArgb(255, 255, 215, 0);   // gold
    private static readonly Color HighlightSel = Color.FromArgb(255, 255, 255, 255); // white

    /// <summary>
    /// Render the root menu: a "Play Movie" button and, if <paramref name="hasScenes"/>,
    /// a "Scene Selection" button. Button names are "play" and "scenes".
    /// </summary>
    public static RenderedMenu RenderRootMenu(string title, bool hasScenes, bool isPal, string outDir)
    {
        int w = 720, h = isPal ? 576 : 480;

        var items = new List<(string Name, string Label)> { ("play", "▶  Play Movie") };
        if (hasScenes) items.Add(("scenes", "Scene Selection"));

        const int btnW = 396, btnH = 60, gap = 28;
        int blockH  = items.Count * btnH + (items.Count - 1) * gap;
        int firstY  = Even((int)(h * 0.52) - blockH / 2);
        int x0      = Even((w - btnW) / 2);

        var buttons = new List<MenuButton>();
        for (int i = 0; i < items.Count; i++)
        {
            int y0 = Even(firstY + i * (btnH + gap));
            buttons.Add(new MenuButton(items[i].Name, x0, y0, Even(x0 + btnW), Even(y0 + btnH)));
        }

        return Compose(outDir, "root", w, h, title, 40f,
            buttons, items.Select(it => it.Label).ToList(), 30f);
    }

    /// <summary>
    /// Render the scene-selection menu: a grid of chapter buttons ("ch1".."chN")
    /// plus a "back" button. <paramref name="sceneLabels"/> is capped at
    /// <see cref="MaxSceneButtons"/>.
    /// </summary>
    public static RenderedMenu RenderSceneMenu(
        IReadOnlyList<string> sceneLabels, bool isPal, string outDir)
    {
        int w = 720, h = isPal ? 576 : 480;
        int n = Math.Min(sceneLabels.Count, MaxSceneButtons);

        const int cols = 3, btnW = 200, btnH = 56, hGap = 40, vGap = 22;
        int rows    = (n + cols - 1) / cols;
        int gridW   = cols * btnW + (cols - 1) * hGap;
        int gridX0  = Even((w - gridW) / 2);
        int gridY0  = Even((int)(h * 0.22));

        var buttons = new List<MenuButton>();
        var labels  = new List<string>();
        for (int i = 0; i < n; i++)
        {
            int r = i / cols, c = i % cols;
            int x0 = Even(gridX0 + c * (btnW + hGap));
            int y0 = Even(gridY0 + r * (btnH + vGap));
            buttons.Add(new MenuButton($"ch{i + 1}", x0, y0, Even(x0 + btnW), Even(y0 + btnH)));
            labels.Add(sceneLabels[i]);
        }

        // "Back" button, centred below the grid.
        const int backW = 168, backH = 50;
        int backY0 = Even(gridY0 + rows * (btnH + vGap) + 16);
        int backX0 = Even((w - backW) / 2);
        buttons.Add(new MenuButton("back", backX0, backY0, Even(backX0 + backW), Even(backY0 + backH)));
        labels.Add("◀  Back");

        return Compose(outDir, "scene", w, h, "Scene Selection", 32f, buttons, labels, 20f);
    }

    /// <summary>
    /// Draw the background, highlight and select PNGs for a menu whose buttons
    /// and per-button labels are already laid out.
    /// </summary>
    private static RenderedMenu Compose(
        string outDir, string prefix, int w, int h,
        string title, float titlePt,
        IReadOnlyList<MenuButton> buttons, IReadOnlyList<string> labels, float labelPt)
    {
        Directory.CreateDirectory(outDir);
        string bgPath = Path.Combine(outDir, $"{prefix}-bg.png");
        string hiPath = Path.Combine(outDir, $"{prefix}-highlight.png");
        string selPath = Path.Combine(outDir, $"{prefix}-select.png");

        // --- Background: full-colour menu art (anti-aliasing fine here).
        using (var bg = new Bitmap(w, h, PixelFormat.Format24bppRgb))
        {
            using (var g = Graphics.FromImage(bg))
            {
                g.Clear(Background);
                g.SmoothingMode     = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                using var titleFont = new Font("Arial", titlePt, FontStyle.Bold, GraphicsUnit.Point);
                using var labelFont = new Font("Arial", labelPt, FontStyle.Regular, GraphicsUnit.Point);
                using var center    = new StringFormat
                {
                    Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
                };
                using var titleBrush = new SolidBrush(TitleColor);
                using var labelBrush = new SolidBrush(LabelColor);

                g.DrawString(Trim(title, 48), titleFont, titleBrush,
                    new RectangleF(20, h * 0.06f, w - 40, titlePt * 2.2f), center);

                for (int i = 0; i < buttons.Count; i++)
                {
                    var b = buttons[i];
                    g.DrawString(labels[i], labelFont, labelBrush,
                        new RectangleF(b.X0, b.Y0, b.X1 - b.X0, b.Y1 - b.Y0), center);
                }
            }
            bg.Save(bgPath, ImageFormat.Png);
        }

        // --- Highlight + select: identical outline boxes, different colour,
        //     anti-aliasing OFF so each image stays exactly two colours.
        DrawOutlineOverlay(hiPath,  w, h, buttons, HighlightHi);
        DrawOutlineOverlay(selPath, w, h, buttons, HighlightSel);

        return new RenderedMenu(bgPath, hiPath, selPath, buttons);
    }

    private static void DrawOutlineOverlay(
        string path, int w, int h, IReadOnlyList<MenuButton> buttons, Color color)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.None;   // hard edges → no extra colours
            using var pen = new Pen(color, 4f);
            foreach (var b in buttons)
                g.DrawRectangle(pen, b.X0, b.Y0, b.X1 - b.X0 - 1, b.Y1 - b.Y0 - 1);
        }
        bmp.Save(path, ImageFormat.Png);
    }

    /// <summary>
    /// Build the spumux control file (for <c>spumux -m dvd</c>) that overlays a
    /// rendered menu's button rectangles. <c>force="yes"</c> keeps the highlight
    /// always visible; up/down/left/right are left for spumux to derive from the
    /// rectangle geometry.
    /// </summary>
    public static string BuildSpumuxXml(RenderedMenu menu)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<subpictures>");
        sb.AppendLine("  <stream>");
        sb.AppendLine("    <spu force=\"yes\" start=\"00:00:00.00\"");
        sb.AppendLine($"         highlight=\"{XmlEscape(menu.HighlightPng)}\"");
        sb.AppendLine($"         select=\"{XmlEscape(menu.SelectPng)}\">");
        foreach (var b in menu.Buttons)
            sb.AppendLine($"      <button name=\"{XmlEscape(b.Name)}\" " +
                          $"x0=\"{b.X0}\" y0=\"{b.Y0}\" x1=\"{b.X1}\" y1=\"{b.Y1}\"/>");
        sb.AppendLine("    </spu>");
        sb.AppendLine("  </stream>");
        sb.AppendLine("</subpictures>");
        return sb.ToString();
    }

    private static string XmlEscape(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&apos;");

    private static int Even(int v) => v & ~1;

    private static string Trim(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max - 1) + "…";
}
