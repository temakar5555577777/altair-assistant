using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Windows.Automation;
using WinOcr = global::Windows.Media.Ocr;
using WinImg = global::Windows.Graphics.Imaging;
using WinStreams = global::Windows.Storage.Streams;

namespace Altair.Windows.Screen;

public sealed record OcrWord(string Text, Rectangle Box);
public sealed record OcrLine(string Text, Rectangle Box, IReadOnlyList<OcrWord> Words);
public sealed record UiItem(string Name, string Kind, Rectangle Box, AutomationElement Element);

public static class ScreenCapture
{
    public static Rectangle VirtualScreen => System.Windows.Forms.SystemInformation.VirtualScreen;

    public static Bitmap Capture(Rectangle? region = null)
    {
        var r = region ?? VirtualScreen;
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    public static byte[] Png(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>Уменьшенная JPEG-копия для отправки нейросети (экономия токенов).</summary>
    public static byte[] JpegForAi(Bitmap bmp, int maxSide = 1600, long quality = 70)
    {
        double k = Math.Min(1.0, (double)maxSide / Math.Max(bmp.Width, bmp.Height));
        using var small = new Bitmap(bmp, new Size((int)(bmp.Width * k), (int)(bmp.Height * k)));
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        using var ms = new MemoryStream();
        small.Save(ms, codec, ep);
        return ms.ToArray();
    }

    public static string SaveScreenshot(Bitmap bmp)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Altair");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"Скриншот {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
        bmp.Save(path, ImageFormat.Png);
        return path;
    }
}

/// <summary>Встроенный OCR Windows 10/11 (русский и английский пакеты распознавания), без интернета.</summary>
public static class Ocr
{
    public static async Task<IReadOnlyList<OcrLine>> RecognizeAsync(Bitmap bmp, string lang = "ru")
    {
        var engine = WinOcr.OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language(lang))
                     ?? WinOcr.OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? throw new InvalidOperationException("OCR недоступен: установите язык распознавания текста в «Параметры → Время и язык».");

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Bmp);
        ms.Position = 0;
        using var ras = new WinStreams.InMemoryRandomAccessStream();
        using (var writer = new WinStreams.DataWriter(ras.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(ms.ToArray());
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        ras.Seek(0);
        var decoder = await WinImg.BitmapDecoder.CreateAsync(ras);
        using var sb = await decoder.GetSoftwareBitmapAsync(WinImg.BitmapPixelFormat.Bgra8, WinImg.BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(sb);

        return result.Lines.Select(l =>
        {
            var words = l.Words.Select(w => new OcrWord(w.Text, new Rectangle((int)w.BoundingRect.X, (int)w.BoundingRect.Y, (int)w.BoundingRect.Width, (int)w.BoundingRect.Height))).ToList();
            var box = words.Count == 0 ? Rectangle.Empty : words.Select(w => w.Box).Aggregate(Rectangle.Union);
            return new OcrLine(l.Text, box, words);
        }).ToList();
    }

    /// <summary>Найти текст на экране (нечётко) и вернуть центр в экранных координатах.</summary>
    public static async Task<Point?> FindAsync(string text, string lang = "ru")
    {
        var screen = ScreenCapture.VirtualScreen;
        using var bmp = ScreenCapture.Capture(screen);
        var lines = await RecognizeAsync(bmp, lang);
        var q = Altair.Core.Engine.PhraseNormalizer.Normalize(text);
        var qTokens = Altair.Core.Engine.PhraseNormalizer.Tokens(q);
        (Rectangle box, double score) best = (Rectangle.Empty, 0);
        foreach (var line in lines)
        {
            var words = line.Words;
            for (int i = 0; i < words.Count; i++)
            {
                // окно из стольких же слов, сколько в запросе
                int len = Math.Min(qTokens.Length, words.Count - i);
                if (len <= 0) continue;
                var span = words.Skip(i).Take(len).ToList();
                var spanText = Altair.Core.Engine.PhraseNormalizer.Normalize(string.Join(' ', span.Select(w => w.Text)));
                double score = spanText == q ? 1 : spanText.Contains(q) ? 0.95 : Altair.Core.Engine.FuzzyMatcher.TokenSimilarity(spanText, q);
                if (score > best.score) best = (span.Select(w => w.Box).Aggregate(Rectangle.Union), score);
            }
        }
        if (best.score < 0.75) return null;
        return new Point(screen.Left + best.box.Left + best.box.Width / 2, screen.Top + best.box.Top + best.box.Height / 2);
    }
}

/// <summary>UI Automation: кнопки, ссылки, поля активного окна по подписи — точнее, чем OCR.</summary>
public static class UiAutomation
{
    private static readonly ControlType[] Clickable =
    {
        ControlType.Button, ControlType.Hyperlink, ControlType.MenuItem, ControlType.TabItem, ControlType.ListItem,
        ControlType.CheckBox, ControlType.RadioButton, ControlType.Edit, ControlType.ComboBox, ControlType.TreeItem, ControlType.SplitButton,
    };

    public static List<UiItem> Elements(IntPtr window, int max = 150)
    {
        var list = new List<UiItem>();
        AutomationElement root;
        try { root = AutomationElement.FromHandle(window); } catch { return list; }
        var cond = new OrCondition(Clickable.Select(t => (Condition)new PropertyCondition(AutomationElement.ControlTypeProperty, t)).ToArray());
        AutomationElementCollection all;
        try { all = root.FindAll(TreeScope.Descendants, cond); } catch { return list; }
        foreach (AutomationElement e in all)
        {
            try
            {
                if (e.Current.IsOffscreen) continue;
                var name = e.Current.Name;
                if (string.IsNullOrWhiteSpace(name) && e.Current.ControlType != ControlType.Edit) continue;
                var r = e.Current.BoundingRectangle;
                if (r.IsEmpty || r.Width < 2) continue;
                list.Add(new UiItem(name ?? "", e.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""),
                    new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height), e));
                if (list.Count >= max) break;
            }
            catch (ElementNotAvailableException) { }
        }
        return list;
    }

    public static UiItem? Find(IntPtr window, string label)
    {
        var q = Altair.Core.Engine.PhraseNormalizer.Normalize(label);
        return Elements(window, 400)
            .Select(i => (i, s: Score(q, Altair.Core.Engine.PhraseNormalizer.Normalize(i.Name))))
            .Where(x => x.s >= 0.72).OrderByDescending(x => x.s).Select(x => x.i).FirstOrDefault();
    }

    private static double Score(string q, string n) =>
        n == q ? 1 : n.StartsWith(q) ? 0.95 : n.Contains(q) ? 0.9 : Altair.Core.Engine.FuzzyMatcher.TokenSimilarity(n, q);

    /// <summary>Нажать: InvokePattern / Toggle / Select, иначе — плавный клик мышью по центру.</summary>
    public static async Task ActivateAsync(UiItem item, CancellationToken ct)
    {
        var e = item.Element;
        if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var inv)) { ((InvokePattern)inv).Invoke(); return; }
        if (e.TryGetCurrentPattern(TogglePattern.Pattern, out var tog)) { ((TogglePattern)tog).Toggle(); return; }
        if (e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sel)) { ((SelectionItemPattern)sel).Select(); return; }
        await Input.InputSimulator.ClickAt(item.Box.Left + item.Box.Width / 2, item.Box.Top + item.Box.Height / 2, true, "left", ct);
    }

    public static bool SetText(UiItem item, string text)
    {
        if (item.Element.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) { ((ValuePattern)vp).SetValue(text); return true; }
        return false;
    }

    public static string Describe(IEnumerable<UiItem> items) =>
        string.Join("\n", items.Select(i => $"[{i.Kind}] «{i.Name}» @({i.Box.Left + i.Box.Width / 2},{i.Box.Top + i.Box.Height / 2})"));
}
