using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Altair.App.Views;

namespace Altair.App;

/// <summary>Главное окно: левое меню + страница. Закрытие сворачивает в трей.</summary>
public sealed class MainWindow : Window
{
    private readonly ContentControl _content = new();
    private readonly Dictionary<string, Func<FrameworkElement>> _pages;
    private readonly StackPanel _nav = new() { Margin = new Thickness(12, 18, 12, 12) };
    private readonly Image _bg = new() { Stretch = Stretch.UniformToFill, Opacity = 0.35 };
    private readonly MediaElement _bgVideo = new() { Stretch = Stretch.UniformToFill, Opacity = 0.35, LoadedBehavior = MediaState.Manual, IsMuted = true };
    private readonly BackgroundEffect _effect = new();

    public MainWindow()
    {
        Title = "Altair Assistant";
        Width = 1240; Height = 800; MinWidth = 960; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Bg");
        try { Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app.png")); } catch { }

        _pages = new()
        {
            ["Главная"] = () => new HomePage(),
            ["Команды"] = () => new CommandsPage(),
            ["Нейросеть"] = () => new AiPage(),
            ["ИИ-агент"] = () => new AgentPage(),
            ["Расширения"] = () => new ExtensionsPage(),
            ["Оформление"] = () => new AppearancePage(this),
            ["Настройки"] = () => new SettingsPage(),
            ["Журнал"] = () => new LogPage(),
        };

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 22) };
        try { brand.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/app.png")), Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0) }); } catch { }
        brand.Children.Add(new TextBlock { Text = "Altair", FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        _nav.Children.Add(brand);

        foreach (var name in _pages.Keys)
        {
            var rb = new RadioButton { Content = name, GroupName = "nav", Style = (Style)Application.Current.Resources["Nav"] };
            rb.Checked += (_, _) => Navigate(name);
            _nav.Children.Add(rb);
        }

        var status = new TextBlock { Style = (Style)Application.Current.Resources["MutedText"], Margin = new Thickness(14, 18, 8, 0) };
        _nav.Children.Add(status);
        void UpdateStatus()
        {
            var h = AppHost.Current;
            var mic = h.Speech is { Running: true } sp ? (sp.UserMuted ? "микрофон выключен" : "слушаю") : "распознавание не запущено";
            status.Text = $"● {mic}\nЗапросов к ИИ сегодня: {h.Usage?.RequestsToday() ?? 0}";
        }
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        t.Tick += (_, _) => UpdateStatus(); t.Start(); UpdateStatus();

        var navBorder = new Border { Child = _nav, Width = 230 };
        navBorder.SetResourceReference(Border.BackgroundProperty, "Panel");

        var stage = new Grid();
        stage.Children.Add(_bg);
        stage.Children.Add(_bgVideo);
        stage.Children.Add(_effect);
        stage.Children.Add(_content);

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(stage, 1);
        root.Children.Add(navBorder);
        root.Children.Add(stage);
        Content = root;

        ApplyBackground();
        ((RadioButton)_nav.Children[1]).IsChecked = true;
        SourceInitialized += (_, _) => DarkTitleBar();
    }

    public void Navigate(string name)
    {
        try { _content.Content = _pages[name](); }
        catch (Exception ex) { _content.Content = Ui.Page(Ui.H1(name), Ui.Text("Ошибка страницы: " + ex.Message)); }
    }

    public void Reload()
    {
        var checkedName = _nav.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Content as string;
        if (checkedName != null) Navigate(checkedName);
        ApplyBackground();
        DarkTitleBar();
    }

    public void ApplyBackground()
    {
        var s = AppHost.Current.Settings;
        _bg.Source = null; _bgVideo.Stop(); _bgVideo.Source = null;
        var file = s.BackgroundFile;
        if (!string.IsNullOrEmpty(file) && File.Exists(file))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".mp4" or ".wmv" or ".webm" or ".mov" or ".gif")
            {
                // GIF и видео — через MediaElement с зацикливанием
                _bgVideo.Source = new Uri(file);
                _bgVideo.MediaEnded += (_, _) => { _bgVideo.Position = TimeSpan.Zero; _bgVideo.Play(); };
                _bgVideo.Play();
            }
            else
            {
                try { _bg.Source = new BitmapImage(new Uri(file)); } catch { }
            }
        }
        _effect.Mode = s.BackgroundEffect;
    }

    private void DarkTitleBar()
    {
        try
        {
            var light = ThemeManager.Themes.FirstOrDefault(x => x.Name == AppHost.Current.Settings.Theme)?.Light ?? false;
            int v = light ? 0 : 1;
            var hwnd = new WindowInteropHelper(this).Handle;
            Altair.Windows.Native.Win32.DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int));
        }
        catch { }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Крестик — в трей, ассистент продолжает слушать
        e.Cancel = true;
        Hide();
        Tray.Notify("Altair", "Работаю в фоне. Выход — через значок в трее.");
    }
}

/// <summary>Анимированные эффекты фона: звёзды, снег, волны, сетка.</summary>
public sealed class BackgroundEffect : FrameworkElement
{
    private readonly Random _rng = new(7);
    private readonly List<(double X, double Y, double S, double V)> _p = new();
    private string _mode = "none";
    private double _t;

    public string Mode
    {
        get => _mode;
        set
        {
            _mode = value; _p.Clear();
            for (int i = 0; i < 140; i++) _p.Add((_rng.NextDouble(), _rng.NextDouble(), 0.5 + _rng.NextDouble() * 2, 0.2 + _rng.NextDouble()));
            InvalidateVisual();
        }
    }

    public BackgroundEffect()
    {
        IsHitTestVisible = false;
        CompositionTarget.Rendering += (_, _) => { if (_mode != "none" && IsVisible) { _t += 0.016; InvalidateVisual(); } };
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (_mode == "none" || w <= 0) return;
        var accent = ((SolidColorBrush)Application.Current.Resources["Accent"]).Color;
        switch (_mode)
        {
            case "stars":
                foreach (var (x, y, s, v) in _p)
                {
                    var a = (byte)(80 + 120 * (0.5 + 0.5 * Math.Sin(_t * v * 2 + x * 10)));
                    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(a, 255, 255, 255)), null, new Point(x * w, y * h), s * 0.6, s * 0.6);
                }
                break;
            case "snow":
                foreach (var (x, y, s, v) in _p)
                {
                    double yy = (y * h + _t * 30 * v) % h, xx = x * w + Math.Sin(_t * v + y * 6) * 12;
                    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), null, new Point(xx, yy), s, s);
                }
                break;
            case "waves":
                for (int k = 0; k < 4; k++)
                {
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        ctx.BeginFigure(new Point(0, h), true, true);
                        for (double x = 0; x <= w; x += 12)
                            ctx.LineTo(new Point(x, h * (0.62 + k * 0.08) + Math.Sin(x / 140 + _t * (0.6 + k * 0.2) + k) * 22), true, true);
                        ctx.LineTo(new Point(w, h), true, true);
                    }
                    g.Freeze();
                    dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(28, accent.R, accent.G, accent.B)), null, g);
                }
                break;
            case "grid":
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(22, accent.R, accent.G, accent.B)), 1);
                double step = 48, off = (_t * 10) % step;
                for (double x = -off; x < w; x += step) dc.DrawLine(pen, new Point(x, 0), new Point(x, h));
                for (double y = -off; y < h; y += step) dc.DrawLine(pen, new Point(0, y), new Point(w, y));
                break;
        }
    }
}
