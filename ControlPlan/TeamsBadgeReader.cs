using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Windows.Automation;
using Tesseract;

namespace ControlPlan;

/// <summary>
/// Reads the unread-activity count from the Microsoft Teams taskbar
/// overlay badge.
///
/// Detection pipeline:
///  1. UI Automation locates the Teams taskbar Button and its
///     <c>OverlayIcon</c> child, giving us screen coordinates for the
///     16x16 badge.
///  2. <c>HelpText</c> is checked for the en-US prefix
///     <c>"Attention requested"</c> as a fast presence signal.
///  3. The badge rectangle is screenshot'd and the saturated red pixels
///     are counted as a fallback presence signal.
///  4. If a badge is present, the white digit inside the red circle is
///     OCR'd with Tesseract to recover the actual count (1-9, or 9+).
///
/// Note: this returns *total* Teams activity (chats + channels + replies),
/// not just @-mentions. Teams renders both with the same badge.
///
/// Limitations:
///  - Teams must be running and pinned on the taskbar.
///  - The "Attention requested" wording is en-US; localised Windows builds
///    fall back to the pixel sampler.
///  - First OCR call downloads <c>eng.traineddata</c> (~4 MB) to
///    <c>%LOCALAPPDATA%\DingDong\tessdata\</c>.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public static class TeamsBadgeReader
{
    /// <summary>
    /// Returns the Teams unread count.
    ///   &gt;0  : that many unread (digit OCR'd from the badge).
    ///    1   : at least one unread but OCR couldn't read the digit
    ///          (presence detected via accessibility / pixels).
    ///    0   : no unread.
    ///   -1   : failure (reason in <paramref name="error"/>).
    /// </summary>
    public static async Task<int> TryGetUnreadCountAsync()
    {
        var (count, _) = await TryGetUnreadCountWithDiagAsync();
        return count;
    }

    /// <summary>Same as <see cref="TryGetUnreadCountAsync"/> but also returns a short diagnostic string.</summary>
    public static async Task<(int count, string diag)> TryGetUnreadCountWithDiagAsync()
    {
        try
        {
            var (button, overlayRect) = FindTeamsButton(out var err);
            if (button == null) return (err == null ? 0 : -1, err ?? "no Teams button");

            // ---- Step 1: presence signal (from HelpText or red pixels) ----
            bool presence = false;
            var help = button.Current.HelpText ?? string.Empty;
            if (help.IndexOf("Attention requested", StringComparison.OrdinalIgnoreCase) >= 0) presence = true;
            else if (System.Text.RegularExpressions.Regex.IsMatch(help, @"\d+\s+new", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) presence = true;

            if (overlayRect.IsEmpty) return (presence ? 1 : 0, "no overlay rect; presence=" + presence);

            using var raw = new Bitmap(overlayRect.Width, overlayRect.Height);
            using (var g = Graphics.FromImage(raw))
                g.CopyFromScreen(overlayRect.Location, Point.Empty, overlayRect.Size);

            if (!presence)
            {
                int redCount = 0;
                int total = raw.Width * raw.Height;
                for (int y = 0; y < raw.Height; y++)
                for (int x = 0; x < raw.Width; x++)
                {
                    var c = raw.GetPixel(x, y);
                    if (c.R >= 180 && c.G <= 110 && c.B <= 130 && (c.R - Math.Max(c.G, c.B)) >= 60) redCount++;
                }
                presence = redCount * 100 / total >= 6;
                if (!presence) return (0, "no badge");
            }

            // ---- Step 2: try to OCR the digit ----
            int ocr = await OcrBadgeDigitAsync(raw);
            if (ocr > 0) return (ocr, "ocr=" + ocr);

            // OCR failed but we know there's a badge; fall back to 1.
            return (1, "presence only");
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>
    /// Attempts to read the digit ("1"..."9", or "9+") drawn inside the
    /// Teams badge.  Returns 0 on failure.
    ///
    /// Pipeline:
    ///  1. Threshold the 16x16 raw screenshot to a 1-bit "white digit on
    ///     red background" mask.
    ///  2. Find the bounding box of the white pixels and crop to it.
    ///  3. Upscale 10x using nearest neighbour, paint black-on-white with
    ///     a generous border (Tesseract wants margin around the glyph).
    ///  4. Run Tesseract with PSM=SingleChar and a digit/+ whitelist.
    ///
    /// Tesseract's eng.traineddata is downloaded once on first use to
    /// <c>%LOCALAPPDATA%\DingDong\tessdata\</c> so we don't have to commit
    /// a 4 MB binary blob into the repository.
    /// </summary>
    private static async Task<int> OcrBadgeDigitAsync(Bitmap raw)
    {
        try
        {
            var tessDir = await EnsureTessdataAsync();
            if (tessDir == null) return 0;

            // Always save the raw capture for offline inspection.
            try { raw.Save(Path.Combine(Path.GetTempPath(), "ctrlplan-badge-raw.png"), System.Drawing.Imaging.ImageFormat.Png); } catch { }

            // Threshold: anything that's NOT strongly-red is foreground (digit
            // body + its anti-alias halo).  Strongly-red = badge background.
            // Using a green/red ratio captures both pure white interior pixels
            // and the pink anti-alias pixels along the glyph edge, which is
            // important because at 16x16 the pure-white interior is only a
            // sparse skeleton.
            int minX = raw.Width, minY = raw.Height, maxX = -1, maxY = -1;
            var fg = new byte[raw.Width, raw.Height]; // 0..255 darkness
            for (int y = 0; y < raw.Height; y++)
            for (int x = 0; x < raw.Width; x++)
            {
                var c = raw.GetPixel(x, y);
                bool isRed = c.R >= 150 && c.G <= 110 && c.B <= 130 && (c.R - Math.Max(c.G, c.B)) >= 50;
                if (!isRed)
                {
                    // Closer to white = stronger foreground.
                    int lum = (c.R + c.G + c.B) / 3;
                    fg[x, y] = (byte)Math.Min(255, Math.Max(0, lum));
                    if (lum > 120)
                    {
                        if (x < minX) minX = x;
                        if (y < minY) minY = y;
                        if (x > maxX) maxX = x;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX < 0) return 0;
            int gw = maxX - minX + 1, gh = maxY - minY + 1;
            if (gw < 3 || gh < 5) return 0;

            // Build a clean grayscale "digit on black" crop, then bicubic
            // upscale to a comfortable size for Tesseract (~120 px tall),
            // and finally invert to black-on-white.
            using var crop = new Bitmap(gw, gh, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            for (int y = 0; y < gh; y++)
            for (int x = 0; x < gw; x++)
            {
                byte v = fg[minX + x, minY + y];
                crop.SetPixel(x, y, Color.FromArgb(v, v, v));
            }

            int targetH = 160;
            int targetW = Math.Max(1, gw * targetH / gh);
            const int margin = 40;
            int outW = targetW + margin * 2;
            int outH = targetH + margin * 2;
            using var big = new Bitmap(outW, outH, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(big))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                // Draw inverted (so digit lands black on white) via a color matrix.
                var cm = new System.Drawing.Imaging.ColorMatrix(new float[][] {
                    new float[]{-1, 0, 0, 0, 0},
                    new float[]{ 0,-1, 0, 0, 0},
                    new float[]{ 0, 0,-1, 0, 0},
                    new float[]{ 0, 0, 0, 1, 0},
                    new float[]{ 1, 1, 1, 0, 1},
                });
                using var ia = new System.Drawing.Imaging.ImageAttributes();
                ia.SetColorMatrix(cm);
                g.DrawImage(crop,
                    new Rectangle(margin, margin, targetW, targetH),
                    0, 0, gw, gh, GraphicsUnit.Pixel, ia);
            }

            var tmp = Path.Combine(Path.GetTempPath(), "ctrlplan-badge.png");
            big.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);

            using var engine = new TesseractEngine(tessDir, "eng", EngineMode.Default);
            engine.SetVariable("tessedit_char_whitelist", "0123456789+");
            engine.DefaultPageSegMode = PageSegMode.SingleChar;
            using var pix = Pix.LoadFromFile(tmp);
            using var page = engine.Process(pix);
            var text = (page.GetText() ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(text)) return 0;
            if (text.Contains("9+") || text.Contains("+")) return 9;
            var m = System.Text.RegularExpressions.Regex.Match(text, @"\d+");
            if (m.Success && int.TryParse(m.Value, out var n) && n >= 1 && n <= 99) return n;
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string? s_tessDir;
    private static async Task<string?> EnsureTessdataAsync()
    {
        if (s_tessDir != null) return s_tessDir;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DingDong", "tessdata");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "eng.traineddata");
            if (!File.Exists(file) || new FileInfo(file).Length < 100_000)
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                // tessdata_fast is enough for our tiny single-character use case and is ~4 MB.
                var bytes = await http.GetByteArrayAsync("https://github.com/tesseract-ocr/tessdata_fast/raw/main/eng.traineddata");
                await File.WriteAllBytesAsync(file, bytes);
            }
            s_tessDir = dir;
            return dir;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Locates the Teams taskbar button and the screen rect of its
    /// OverlayIcon child.  Returns <c>(null, Empty)</c> if Teams isn't
    /// pinned / running, or <c>(button, Empty)</c> if the button is found
    /// but the overlay child isn't.
    /// </summary>
    private static (AutomationElement? button, Rectangle overlay) FindTeamsButton(out string? error)
    {
        error = null;
        var root = AutomationElement.RootElement;
        var tray = root.FindFirst(TreeScope.Children,
            new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
        if (tray == null) { error = "Shell_TrayWnd not found"; return (null, Rectangle.Empty); }

        var buttons = tray.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

        foreach (AutomationElement b in buttons)
        {
            var name = b.Current.Name ?? string.Empty;
            if (name.IndexOf("Microsoft Teams", StringComparison.OrdinalIgnoreCase) < 0) continue;

            var walker = TreeWalker.RawViewWalker;
            var child = walker.GetFirstChild(b);
            while (child != null)
            {
                if (string.Equals(child.Current.AutomationId, "OverlayIcon", StringComparison.Ordinal))
                {
                    var r = child.Current.BoundingRectangle;
                    if (r.IsEmpty || r.Width < 4 || r.Height < 4)
                        return (b, Rectangle.Empty);
                    return (b, new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height));
                }
                child = walker.GetNextSibling(child);
            }
            return (b, Rectangle.Empty);
        }
        return (null, Rectangle.Empty);
    }
}
