using System.Diagnostics;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Altair.Windows.Actions;
using Altair.Windows.Screen;
using Drawing = System.Drawing;

namespace Altair.App.Overlays;

/// <summary>Конвертер: круг действий над выделенными в Проводнике файлами (или файлами из буфера).</summary>
public sealed class ConverterWindow : OverlayWindow
{
    private readonly List<string> _files;

    private ConverterWindow(List<string> files) : base()
    {
        _files = files;
        ShowActivated = true;
        var canvas = new Canvas { Width = 320, Height = 320 };
        var ops = new (string Title, Func<Task> Run)[]
        {
            ("В PNG", () => ConvertImages(Drawing.Imaging.ImageFormat.Png, ".png")),
            ("В JPG", () => ConvertImages(Drawing.Imaging.ImageFormat.Jpeg, ".jpg")),
            ("Уменьшить ×½", ResizeHalf),
            ("В ZIP", Zip),
            ("Копировать пути", () => Sta.SetClipboard(string.Join(Environment.NewLine, _files))),
            ("Имя по дате", RenameByDate),
            ("Спросить ИИ", AskAi),
            ("Распаковать", Unzip),
        };
        var center = new Ellipse { Width = 90, Height = 90, Fill = new SolidColorBrush(Color.FromArgb(240, 16, 20, 34)), Stroke = Brushes.White, StrokeThickness = 1 };
        Canvas.SetLeft(center, 115); Canvas.SetTop(center, 115);
        canvas.Children.Add(center);
        var label = new TextBlock { Text = $"{files.Count} файл.", Foreground = Brushes.White, Width = 90, TextAlignment = TextAlignment.Center };
        Canvas.SetLeft(label, 115); Canvas.SetTop(label, 150);
        canvas.Children.Add(label);
        for (int i = 0; i < ops.Length; i++)
        {
            double a = i * 2 * Math.PI / ops.Length - Math.PI / 2;
            var (title, run) = ops[i];
            var b = new Button { Content = title, Width = 104, FontSize = 12, Padding = new Thickness(6, 6, 6, 6) };
            b.Click += async (_, _) =>
            {
                Hide();
                try { await run(); AppHost.Current.Island.Flash("Конвертер: " + title + " — готово", 3); }
                catch (Exception ex) { Ui.Error(ex); }
                Close();
            };
            Canvas.SetLeft(b, 160 + 118 * Math.Cos(a) - 52); Canvas.SetTop(b, 160 + 118 * Math.Sin(a) - 16);
            canvas.Children.Add(b);
        }
        Content = canvas;
        Deactivated += (_, _) => { if (IsVisible) Close(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    private Task ConvertImages(Drawing.Imaging.ImageFormat fmt, string ext) => Task.Run(() =>
    {
        foreach (var f in _files.Where(f => ImageExt.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant())))
        {
            using var img = Drawing.Image.FromFile(f);
            var dest = Altair.Core.Plugins.DownloadsCleaner.Unique(System.IO.Path.ChangeExtension(f, ext));
            img.Save(dest, fmt);
        }
    });

    private Task ResizeHalf() => Task.Run(() =>
    {
        foreach (var f in _files.Where(f => ImageExt.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant())))
        {
            using var img = Drawing.Image.FromFile(f);
            using var small = new Drawing.Bitmap(img, new Drawing.Size(Math.Max(1, img.Width / 2), Math.Max(1, img.Height / 2)));
            var dest = Altair.Core.Plugins.DownloadsCleaner.Unique(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f)!, System.IO.Path.GetFileNameWithoutExtension(f) + "_small" + System.IO.Path.GetExtension(f)));
            small.Save(dest, img.RawFormat);
        }
    });

    private Task Zip() => Task.Run(() =>
    {
        var dir = System.IO.Path.GetDirectoryName(_files[0])!;
        var zip = Altair.Core.Plugins.DownloadsCleaner.Unique(System.IO.Path.Combine(dir, (_files.Count == 1 ? System.IO.Path.GetFileNameWithoutExtension(_files[0]) : "Архив") + ".zip"));
        using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var f in _files)
        {
            if (File.Exists(f)) z.CreateEntryFromFile(f, System.IO.Path.GetFileName(f), CompressionLevel.Optimal);
            else if (Directory.Exists(f))
                foreach (var sub in Directory.EnumerateFiles(f, "*", SearchOption.AllDirectories))
                    z.CreateEntryFromFile(sub, System.IO.Path.Combine(System.IO.Path.GetFileName(f), System.IO.Path.GetRelativePath(f, sub)), CompressionLevel.Optimal);
        }
    });

    private Task Unzip() => Task.Run(() =>
    {
        foreach (var f in _files.Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
            ZipFile.ExtractToDirectory(f, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f)!, System.IO.Path.GetFileNameWithoutExtension(f)), true);
    });

    private Task RenameByDate() => Task.Run(() =>
    {
        foreach (var f in _files.Where(File.Exists))
        {
            var t = File.GetLastWriteTime(f);
            var dest = Altair.Core.Plugins.DownloadsCleaner.Unique(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f)!, t.ToString("yyyy-MM-dd_HH-mm-ss") + System.IO.Path.GetExtension(f)));
            File.Move(f, dest);
        }
    });

    private async Task AskAi()
    {
        var q = Altair.App.Views.Prompt.Ask("Спросить ИИ о файлах", "Что сделать или что узнать?", "Кратко перескажи содержимое");
        if (string.IsNullOrWhiteSpace(q)) return;
        var h = AppHost.Current;
        var images = new List<Altair.Core.AI.ImagePart>();
        var texts = new List<string>();
        foreach (var f in _files.Take(5))
        {
            var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
            if (ImageExt.Contains(ext)) images.Add(new Altair.Core.AI.ImagePart(await File.ReadAllBytesAsync(f), ext is ".png" ? "image/png" : "image/jpeg"));
            else if (Altair.Core.Plugins.DocIndex.Extensions.Contains(ext)) { var t = Altair.Core.Plugins.DocIndex.ExtractText(f); texts.Add($"[{System.IO.Path.GetFileName(f)}]\n{(t.Length > 20000 ? t[..20000] : t)}"); }
        }
        var r = await h.Chat.OneShot("Ты помогаешь с файлами пользователя. Отвечай по-русски.", q + "\n\n" + string.Join("\n\n", texts), images: images);
        new Altair.App.Views.SimpleWindow("Ответ ИИ", new TextBox { Text = r.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true }).Show();
    }

    /// <summary>Выделенные файлы в активном окне Проводника (Shell.Application), иначе — файлы из буфера обмена.</summary>
    public static List<string> SelectedFiles()
    {
        var result = new List<string>();
        try
        {
            var fg = Altair.Windows.Native.Win32.GetForegroundWindow();
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type != null)
            {
                dynamic shell = Activator.CreateInstance(type)!;
                foreach (dynamic w in shell.Windows())
                {
                    try
                    {
                        if ((IntPtr)(long)w.HWND != fg) continue;
                        foreach (dynamic item in w.Document.SelectedItems()) result.Add((string)item.Path);
                    }
                    catch { }
                }
            }
        }
        catch { }
        if (result.Count == 0)
        {
            try
            {
                var files = Sta.Run(() => System.Windows.Clipboard.ContainsFileDropList() ? System.Windows.Clipboard.GetFileDropList().Cast<string>().ToList() : new List<string>()).Result;
                result.AddRange(files);
            }
            catch { }
        }
        return result;
    }

    public static void ShowAtCursor()
    {
        var files = SelectedFiles();
        if (files.Count == 0) { AppHost.Current.Island.Flash("Конвертер: выделите файлы в Проводнике или скопируйте их", 3); return; }
        var w = new ConverterWindow(files);
        w.Show(); w.PlaceAtCursor(); w.Activate();
    }
}

/// <summary>Скриншоты: выделение области на замороженном экране, пометки (перо, рамка, стрелка), копирование, сохранение, текст (OCR), вопрос ИИ, запись экрана.</summary>
public sealed class ScreenshotOverlay : Window
{
    private readonly Drawing.Bitmap _shot;
    private readonly Drawing.Rectangle _screen;
    private readonly Canvas _canvas = new();
    private readonly Rectangle _sel = new() { Stroke = Brushes.DeepSkyBlue, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(25, 0, 160, 255)) };
    private Point _start; private Rect? _region;
    private string _tool = "select";
    private Polyline? _pen; private Shape? _shape;
    private readonly StackPanel _bar = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    private double _k = 1;

    private ScreenshotOverlay()
    {
        _screen = ScreenCapture.VirtualScreen;
        _shot = ScreenCapture.Capture(_screen);
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; Topmost = true; ShowInTaskbar = false;
        AllowsTransparency = false; Cursor = Cursors.Cross;

        var bg = new Image { Source = ToSource(_shot), Stretch = Stretch.Fill };
        var dim = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), IsHitTestVisible = false };
        var grid = new Grid();
        grid.Children.Add(bg); grid.Children.Add(dim); grid.Children.Add(_canvas);
        _canvas.Background = Brushes.Transparent;
        _canvas.Children.Add(_sel);
        _canvas.Children.Add(_bar);
        Content = grid;

        void Tool(string id, string title) { var b = new Button { Content = title, Margin = new Thickness(0, 0, 4, 0) }; b.Click += (_, _) => _tool = id; _bar.Children.Add(b); }
        void Act(string title, Func<Task> a) { var b = new Button { Content = title, Margin = new Thickness(0, 0, 4, 0) }; b.Click += async (_, _) => { try { await a(); } catch (Exception ex) { Ui.Error(ex); } }; _bar.Children.Add(b); }
        Tool("pen", "✎ Перо"); Tool("rect", "▭ Рамка"); Tool("arrow", "➜ Стрелка");
        Act("Копировать", async () => { await Sta.Run(() => System.Windows.Clipboard.SetImage(Render())); Close(); });
        Act("Сохранить", () => { var p = SaveTo(); Process.Start(new ProcessStartInfo(p) { UseShellExecute = true })?.Dispose(); Close(); return Task.CompletedTask; });
        Act("Текст", async () =>
        {
            using var crop = Crop();
            var lines = await Ocr.RecognizeAsync(crop, AppHost.Current.Settings.Language);
            await Sta.SetClipboard(string.Join(Environment.NewLine, lines.Select(l => l.Text)));
            AppHost.Current.Island.Flash("Текст скопирован", 2); Close();
        });
        Act("Спросить ИИ", async () =>
        {
            var q = Altair.App.Views.Prompt.Ask("Вопрос по скриншоту", "Что нужно узнать?", "Что здесь изображено и что важно?");
            if (string.IsNullOrWhiteSpace(q)) return;
            using var crop = Crop();
            var r = await AppHost.Current.Chat.OneShot("Отвечай по-русски, кратко и по делу.", q, images: new[] { new Altair.Core.AI.ImagePart(ScreenCapture.JpegForAi(crop), "image/jpeg") });
            Close();
            new Altair.App.Views.SimpleWindow("Ответ ИИ", new TextBox { Text = r.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap }).Show();
        });
        Act("⏺ Запись", () => { var reg = RegionPixels(); Close(); ScreenRecorder.Start(reg); return Task.CompletedTask; });
        Act("✕", () => { Close(); return Task.CompletedTask; });

        _canvas.MouseLeftButtonDown += Down;
        _canvas.MouseMove += Move;
        _canvas.MouseLeftButtonUp += Up;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += (_, _) =>
        {
            _k = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice.M11 ?? 1;
            Left = _screen.Left * _k; Top = _screen.Top * _k; Width = _screen.Width * _k; Height = _screen.Height * _k;
        };
        Closed += (_, _) => _shot.Dispose();
    }

    private void Down(object s, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(_canvas);
        if (_region == null || _tool == "select") { _tool = "select"; _bar.Visibility = Visibility.Collapsed; return; }
        if (_tool == "pen") { _pen = new Polyline { Stroke = Brushes.Red, StrokeThickness = 3, Points = { _start } }; _canvas.Children.Add(_pen); }
        else if (_tool == "rect") { _shape = new Rectangle { Stroke = Brushes.Red, StrokeThickness = 3 }; _canvas.Children.Add(_shape); }
        else if (_tool == "arrow") { _shape = new Line { Stroke = Brushes.Red, StrokeThickness = 4, X1 = _start.X, Y1 = _start.Y, X2 = _start.X, Y2 = _start.Y, StrokeEndLineCap = PenLineCap.Triangle }; _canvas.Children.Add(_shape); }
    }

    private void Move(object s, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(_canvas);
        if (_tool == "select")
        {
            var r = new Rect(_start, p);
            Canvas.SetLeft(_sel, r.X); Canvas.SetTop(_sel, r.Y); _sel.Width = r.Width; _sel.Height = r.Height;
        }
        else if (_pen != null) _pen.Points.Add(p);
        else if (_shape is Rectangle rect)
        {
            var r = new Rect(_start, p);
            Canvas.SetLeft(rect, r.X); Canvas.SetTop(rect, r.Y); rect.Width = r.Width; rect.Height = r.Height;
        }
        else if (_shape is Line line) { line.X2 = p.X; line.Y2 = p.Y; }
    }

    private void Up(object s, MouseButtonEventArgs e)
    {
        if (_tool == "select")
        {
            var r = new Rect(_start, e.GetPosition(_canvas));
            if (r.Width < 5 || r.Height < 5) r = new Rect(0, 0, ActualWidth, ActualHeight); // клик — весь экран
            _region = r;
            Canvas.SetLeft(_sel, r.X); Canvas.SetTop(_sel, r.Y); _sel.Width = r.Width; _sel.Height = r.Height;
            Canvas.SetLeft(_bar, Math.Max(0, r.X)); Canvas.SetTop(_bar, Math.Min(ActualHeight - 40, r.Bottom + 8));
            _bar.Visibility = Visibility.Visible;
            _tool = "pen";
        }
        _pen = null; _shape = null;
    }

    private Drawing.Rectangle RegionPixels()
    {
        var r = _region ?? new Rect(0, 0, ActualWidth, ActualHeight);
        return new Drawing.Rectangle((int)(r.X / _k), (int)(r.Y / _k), Math.Max(1, (int)(r.Width / _k)), Math.Max(1, (int)(r.Height / _k)));
    }

    private Drawing.Bitmap Crop()
    {
        var px = RegionPixels();
        return _shot.Clone(new Drawing.Rectangle(px.X, px.Y, Math.Min(px.Width, _shot.Width - px.X), Math.Min(px.Height, _shot.Height - px.Y)), _shot.PixelFormat);
    }

    /// <summary>Снимок области вместе с пометками.</summary>
    private BitmapSource Render()
    {
        _bar.Visibility = Visibility.Collapsed; _sel.Visibility = Visibility.Collapsed;
        var r = _region ?? new Rect(0, 0, ActualWidth, ActualHeight);
        UpdateLayout();
        var dpi = 96 / _k;
        var rtb = new RenderTargetBitmap((int)(ActualWidth / _k), (int)(ActualHeight / _k), dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render((Visual)Content);
        var cropped = new CroppedBitmap(rtb, new Int32Rect((int)(r.X / _k), (int)(r.Y / _k), Math.Max(1, (int)(r.Width / _k)), Math.Max(1, (int)(r.Height / _k))));
        cropped.Freeze();
        return cropped;
    }

    private string SaveTo()
    {
        var img = Render();
        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Altair");
        Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, $"Скриншот {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
        using var fs = File.Create(path);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(img)); enc.Save(fs);
        return path;
    }

    private static BitmapSource ToSource(Drawing.Bitmap bmp)
    {
        var h = bmp.GetHbitmap();
        try { var s = Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); s.Freeze(); return s; }
        finally { DeleteObject(h); }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

    public static void Start() { try { var w = new ScreenshotOverlay(); w.Show(); w.Activate(); } catch (Exception ex) { Ui.Error(ex); } }
}

/// <summary>Запись экрана через ffmpeg (если установлен): область, 30 к/с, H.264. Повторное нажатие «Запись» — стоп.</summary>
public static class ScreenRecorder
{
    private static Process? _ff;
    public static bool Recording => _ff is { HasExited: false };

    public static void Start(Drawing.Rectangle region)
    {
        if (Recording) { Stop(); return; }
        var ffmpeg = Altair.Windows.Sys.Shell.ResolveExecutable("ffmpeg");
        if (ffmpeg == null) { Ui.Info("Для записи экрана нужен ffmpeg: winget install Gyan.FFmpeg"); return; }
        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Altair");
        Directory.CreateDirectory(dir);
        var file = System.IO.Path.Combine(dir, $"Запись {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
        int w = region.Width / 2 * 2, h = region.Height / 2 * 2;
        var args = $"-y -f gdigrab -framerate 30 -offset_x {region.X} -offset_y {region.Y} -video_size {w}x{h} -i desktop -c:v libx264 -preset veryfast -pix_fmt yuv420p \"{file}\"";
        _ff = Process.Start(new ProcessStartInfo(ffmpeg, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true });
        AppHost.Current.Island.Flash("⏺ Идёт запись экрана — Ctrl+Alt+A → «Запись», чтобы остановить", 5);
    }

    public static void Stop()
    {
        if (_ff == null) return;
        try { _ff.StandardInput.Write('q'); _ff.StandardInput.Flush(); _ff.WaitForExit(5000); } catch { }
        _ff.Dispose(); _ff = null;
        AppHost.Current.Island.Flash("Запись сохранена в «Видео → Altair»", 4);
    }
}
