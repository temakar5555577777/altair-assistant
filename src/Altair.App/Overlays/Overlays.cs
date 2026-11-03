using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Altair.Core.Engine;
using Altair.Windows.Actions;

namespace Altair.App.Overlays;

/// <summary>Базовое окно-оверлей: без рамки, поверх всех, не в панели задач, не забирает фокус.</summary>
public abstract class OverlayWindow : Window
{
    protected OverlayWindow(bool clickThrough = false)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        if (clickThrough) SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int ex = Altair.Windows.Native.Win32.GetWindowLongW(hwnd, -20);
            SetWindowLong(hwnd, -20, ex | 0x20 | 0x80); // WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW
        };
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    protected static Border Pill(UIElement child, double radius = 22) => new()
    {
        CornerRadius = new CornerRadius(radius), Padding = new Thickness(16, 10, 16, 10), Child = child,
        Background = new SolidColorBrush(Color.FromArgb(235, 16, 20, 34)), BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
        BorderThickness = new Thickness(1), Effect = Ui.Shadow(),
    };

    protected void PlaceTopCenter(double top = 14)
    {
        UpdateLayout();
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - ActualWidth) / 2; Top = wa.Top + top;
    }

    protected void PlaceAtCursor()
    {
        Altair.Windows.Native.Win32.GetCursorPos(out var p);
        var src = PresentationSource.FromVisual(Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible) ?? this);
        double k = src?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        Left = p.X * k + 8; Top = p.Y * k + 8;
        UpdateLayout();
        var wa = SystemParameters.WorkArea;
        if (Left + ActualWidth > wa.Right) Left = wa.Right - ActualWidth - 8;
        if (Top + ActualHeight > wa.Bottom) Top = wa.Bottom - ActualHeight - 8;
    }
}

/// <summary>«Островок»: плашка сверху — состояние ассистента, распознанная фраза, шаг агента «Шаг 2 из 5» с кольцом хода.</summary>
public sealed class IslandWindow : OverlayWindow
{
    private readonly TextBlock _title = new() { Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap };
    private readonly TextBlock _sub = new() { Foreground = new SolidColorBrush(Color.FromRgb(170, 182, 210)), FontSize = 12, MaxWidth = 520, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
    private readonly Ellipse _dot = new() { Width = 10, Height = 10, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly System.Windows.Shapes.Path _ring = new() { Stroke = Brushes.White, StrokeThickness = 3, Width = 22, Height = 22, Margin = new Thickness(10, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _hide = new();
    private bool _expanded;

    public IslandWindow() : base(clickThrough: false)
    {
        var text = new StackPanel { Children = { _title, _sub } };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { _dot, text, _ring } };
        Content = Pill(row);
        _hide.Tick += (_, _) => { _hide.Stop(); if (!_expanded) Hide(); };
        MouseLeftButtonUp += (_, _) => ((App)Application.Current).ShowMain();
        MouseRightButtonUp += (_, _) => AppHost.Current.Agent.Stop();
        SetState(AssistantState.Idle);
    }

    private bool Enabled => AppHost.Current.Settings.PluginOn("island", true);

    private void ShowFor(double seconds)
    {
        if (!Enabled) return;
        if (!IsVisible) Show();
        PlaceTopCenter();
        _hide.Stop();
        if (seconds > 0) { _hide.Interval = TimeSpan.FromSeconds(seconds); _hide.Start(); }
    }

    public void SetState(AssistantState s)
    {
        var (color, title) = s switch
        {
            AssistantState.Listening => (Color.FromRgb(61, 214, 140), "Слушаю…"),
            AssistantState.Thinking => (Color.FromRgb(124, 92, 255), "Думаю…"),
            AssistantState.Executing => (Color.FromRgb(79, 140, 255), "Выполняю"),
            AssistantState.Speaking => (Color.FromRgb(255, 181, 71), "Говорю"),
            _ => (Color.FromRgb(120, 130, 150), "Altair"),
        };
        _dot.Fill = new SolidColorBrush(color);
        _title.Text = title;
        if (s != AssistantState.Idle) ShowFor(0); else ShowFor(2.5);
    }

    public void SetPartial(string text) { _sub.Text = "«" + text + "»"; ShowFor(4); }

    public void Flash(string text, double seconds) { _sub.Text = text; ShowFor(seconds); }

    /// <summary>Кольцо хода агента: доля выполненных шагов.</summary>
    public void SetAgentStep(int i, int n, string text)
    {
        _title.Text = n > 0 ? $"Агент · шаг {i} из {n}" : "Агент";
        _sub.Text = text + "   (ПКМ — стоп)";
        _ring.Visibility = Visibility.Visible;
        double frac = n > 0 ? Math.Clamp((double)i / n, 0.02, 0.999) : 0.1;
        double a = frac * 2 * Math.PI, r = 9;
        var end = new Point(11 + r * Math.Sin(a), 11 - r * Math.Cos(a));
        _ring.Data = new PathGeometry(new[] { new PathFigure(new Point(11, 2), new[] { new ArcSegment(end, new Size(r, r), 0, frac > 0.5, SweepDirection.Clockwise, true) }, false) });
        ShowFor(0);
    }

    public void ToggleExpanded()
    {
        _expanded = !_expanded;
        if (_expanded)
        {
            var last = AppHost.Current.Assistant.History.FirstOrDefault();
            _sub.Text = $"{DateTime.Now:HH:mm} · громкость {SafeVol()}% · {(last != null ? "последнее: " + last.Heard : "команд ещё не было")}";
            ShowFor(0);
        }
        else Hide();
        _ring.Visibility = Visibility.Collapsed;
    }

    private static int SafeVol() { try { return AppHost.Current.Volume.Get(); } catch { return 0; } }
}

/// <summary>Подтверждение шага агента: «Разрешить / Отклонить».</summary>
public sealed class ConfirmWindow : OverlayWindow
{
    public event Action<bool>? Decided;
    private bool _done;

    public ConfirmWindow(string description, bool critical)
    {
        ShowActivated = true;
        var allow = new Button { Content = "Разрешить", Style = (Style)Application.Current.Resources["Primary"] };
        var deny = new Button { Content = "Отклонить" };
        allow.Click += (_, _) => Finish(true);
        deny.Click += (_, _) => Finish(false);
        var body = new StackPanel
        {
            MaxWidth = 520,
            Children =
            {
                new TextBlock { Text = critical ? "⚠ Оплата или пароль — подтвердите кнопкой" : "Агент хочет выполнить действие", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) },
                new TextBlock { Text = description, Foreground = new SolidColorBrush(Color.FromRgb(200, 208, 228)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) },
                new StackPanel { Orientation = Orientation.Horizontal, Children = { allow, deny } },
            }
        };
        Content = Pill(body, 18);
        Loaded += (_, _) => PlaceTopCenter(70);
        Closed += (_, _) => Finish(false);
    }

    private void Finish(bool ok)
    {
        if (_done) return;
        _done = true;
        Decided?.Invoke(ok);
        if (IsLoaded) Dispatcher.BeginInvoke(Close);
    }
}

/// <summary>Живые субтитры поверх экрана для звука компьютера.</summary>
public sealed class SubtitlesWindow : OverlayWindow
{
    private static SubtitlesWindow? _instance;
    private readonly TextBlock _text = new() { Foreground = Brushes.White, FontSize = 22, TextWrapping = TextWrapping.Wrap, MaxWidth = 1000, TextAlignment = TextAlignment.Center };
    private string _final = "";

    private SubtitlesWindow() : base(clickThrough: true)
    {
        Content = Pill(_text, 14);
        var lb = AppHost.Current.Loopback;
        lb.Partial += p => Dispatcher.BeginInvoke(() => { _text.Text = (_final.Length > 0 ? _final + " " : "") + p; Place(); });
        lb.Final += f => Dispatcher.BeginInvoke(() => { _final = f; _text.Text = f; Place(); });
    }

    private void Place()
    {
        UpdateLayout();
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - ActualWidth) / 2; Top = wa.Bottom - ActualHeight - 60;
    }

    public static void Toggle(bool on)
    {
        var h = AppHost.Current;
        if (on)
        {
            if (h.Speech?.MainModel == null) { Ui.Info("Сначала должна загрузиться модель распознавания."); return; }
            h.Loopback.Start(h.Speech.MainModel);
            _instance ??= new SubtitlesWindow();
            _instance._text.Text = "Субтитры включены…";
            _instance.Show(); _instance.Place();
        }
        else
        {
            h.Loopback.Stop();
            _instance?.Hide();
        }
    }
}

/// <summary>Питомец: живёт у панели задач, ходит, моргает, реагирует на ассистента.</summary>
public sealed class PetWindow : OverlayWindow
{
    private static PetWindow? _instance;
    private readonly Canvas _canvas = new() { Width = 90, Height = 80 };
    private readonly Ellipse _body = new() { Width = 64, Height = 54 };
    private readonly Ellipse _eyeL = new() { Width = 9, Height = 11, Fill = Brushes.White }, _eyeR = new() { Width = 9, Height = 11, Fill = Brushes.White };
    private double _dir = 1, _t;

    private PetWindow() : base()
    {
        _body.Fill = new RadialGradientBrush(Color.FromRgb(120, 170, 255), Color.FromRgb(70, 90, 230));
        Canvas.SetLeft(_body, 13); Canvas.SetTop(_body, 20);
        Canvas.SetLeft(_eyeL, 30); Canvas.SetTop(_eyeL, 36); Canvas.SetLeft(_eyeR, 50); Canvas.SetTop(_eyeR, 36);
        _canvas.Children.Add(_body); _canvas.Children.Add(_eyeL); _canvas.Children.Add(_eyeR);
        Content = _canvas;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) => Step();
        timer.Start();
        MouseLeftButtonDown += (_, _) => DragMove();
        MouseRightButtonUp += (_, _) => _ = AppHost.Current.Tts.SayAsync(new[] { "Мур.", "Я тут.", "Чем помочь?", "Пип!" }[Random.Shared.Next(4)]);
        AppHost.Current.Assistant.StateChanged += s => Dispatcher.BeginInvoke(() =>
            _body.Fill = s == AssistantState.Idle ? new RadialGradientBrush(Color.FromRgb(120, 170, 255), Color.FromRgb(70, 90, 230)) : new RadialGradientBrush(Color.FromRgb(140, 255, 190), Color.FromRgb(40, 180, 120)));
    }

    private void Step()
    {
        _t += 0.04;
        var wa = SystemParameters.WorkArea;
        if (Mouse.LeftButton == MouseButtonState.Released)
        {
            Left += _dir * 0.8;
            if (Left < wa.Left || Left > wa.Right - 90) _dir = -_dir;
            Top = wa.Bottom - 80;
        }
        var bounce = Math.Abs(Math.Sin(_t * 3)) * 3;
        Canvas.SetTop(_body, 20 - bounce);
        bool blink = (_t % 4) < 0.12;
        _eyeL.Height = _eyeR.Height = blink ? 2 : 11;
        Canvas.SetTop(_eyeL, 36 - bounce + (blink ? 5 : 0)); Canvas.SetTop(_eyeR, 36 - bounce + (blink ? 5 : 0));
        Canvas.SetLeft(_eyeL, _dir > 0 ? 34 : 28); Canvas.SetLeft(_eyeR, _dir > 0 ? 54 : 48);
    }

    public static void Toggle(bool on)
    {
        if (on) { _instance ??= new PetWindow(); _instance.Left = SystemParameters.WorkArea.Left + 200; _instance.Show(); }
        else _instance?.Hide();
    }
}

/// <summary>Визуальный аватар: светящаяся сфера, пульсирует от громкости голоса и состояния.</summary>
public sealed class AvatarWindow : OverlayWindow
{
    private static AvatarWindow? _instance;
    private readonly Ellipse _orb = new() { Width = 110, Height = 110 };
    private readonly ScaleTransform _scale = new(1, 1, 55, 55);
    private float _level;

    private AvatarWindow() : base()
    {
        _orb.Fill = new RadialGradientBrush(Color.FromArgb(255, 180, 210, 255), Color.FromArgb(0, 79, 140, 255));
        _orb.RenderTransform = _scale;
        Content = new Grid { Width = 140, Height = 140, Children = { _orb } };
        _orb.HorizontalAlignment = HorizontalAlignment.Center; _orb.VerticalAlignment = VerticalAlignment.Center;
        MouseLeftButtonDown += (_, _) => DragMove();
        var wa = SystemParameters.WorkArea; Left = wa.Right - 170; Top = wa.Bottom - 190;
        var h = AppHost.Current;
        if (h.Speech != null) h.Speech.Level += l => _level = l;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        double phase = 0;
        t.Tick += (_, _) =>
        {
            phase += 0.05;
            double target = 1 + Math.Min(0.5, _level * 6) + 0.04 * Math.Sin(phase);
            _scale.ScaleX = _scale.ScaleY = _scale.ScaleX + (target - _scale.ScaleX) * 0.3;
            var st = h.Assistant.State;
            var c = st switch { AssistantState.Speaking => Color.FromRgb(255, 200, 120), AssistantState.Thinking => Color.FromRgb(190, 160, 255), AssistantState.Listening => Color.FromRgb(140, 255, 200), _ => Color.FromRgb(180, 210, 255) };
            _orb.Fill = new RadialGradientBrush(c, Color.FromArgb(0, c.R, c.G, c.B));
        };
        t.Start();
    }

    public static void Toggle(bool on)
    {
        if (on) { _instance ??= new AvatarWindow(); _instance.Show(); }
        else _instance?.Hide();
    }
}

/// <summary>Помощник текста: выделили текст в любой программе → исправить / улучшить / перевести / короче / вежливее.</summary>
public sealed class TextAssistWindow : OverlayWindow
{
    private TextAssistWindow(string selected) : base()
    {
        ShowActivated = true;
        var wrap = new WrapPanel { MaxWidth = 360 };
        var status = new TextBlock { Foreground = Brushes.LightGray, Margin = new Thickness(0, 4, 0, 0), FontSize = 12 };
        (string Title, string Prompt)[] ops =
        {
            ("Исправить ошибки", "Исправь орфографию, пунктуацию и грамматику. Не меняй смысл и стиль."),
            ("Улучшить", "Сделай текст яснее и грамотнее, сохрани смысл и длину."),
            ("Короче", "Сократи текст вдвое без потери смысла."),
            ("Вежливее", "Перепиши текст вежливо и дружелюбно."),
            ("Деловой стиль", "Перепиши текст в деловом стиле."),
            ("→ English", "Translate to English. Keep formatting."),
            ("→ Русский", "Переведи на русский. Сохрани форматирование."),
            ("→ Қазақша", "Қазақ тіліне аудар. Пішімін сақта."),
        };
        foreach (var (title, prompt) in ops)
        {
            var b = new Button { Content = title };
            b.Click += async (_, _) =>
            {
                status.Text = "Обрабатываю…";
                try
                {
                    var r = await AppHost.Current.Chat.OneShot(prompt + " Верни только итоговый текст без пояснений.", selected);
                    Hide();
                    await Task.Delay(120);
                    await WindowsActions.ReplaceSelection(r.Text);
                    Close();
                }
                catch (Exception ex) { status.Text = ex.Message; }
            };
            wrap.Children.Add(b);
        }
        var close = new Button { Content = "✕" };
        close.Click += (_, _) => Close();
        wrap.Children.Add(close);
        Content = Pill(new StackPanel { Children = { new TextBlock { Text = "Помощник текста", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) }, wrap, status } }, 16);
        Deactivated += (_, _) => { if (IsVisible) Close(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    public static async void ShowAtCursor()
    {
        try
        {
            var sel = await WindowsActions.GetSelectedText();
            if (string.IsNullOrWhiteSpace(sel)) { AppHost.Current.Island.Flash("Выделите текст и нажмите сочетание ещё раз", 3); return; }
            var w = new TextAssistWindow(sel);
            w.Show(); w.PlaceAtCursor(); w.Activate();
        }
        catch (Exception ex) { AppHost.Current.Write("Помощник текста: " + ex.Message); }
    }
}

/// <summary>История буфера обмена с поиском: Enter — вставить, Del — удалить, Ctrl+P — закрепить.</summary>
public sealed class ClipboardWindow : OverlayWindow
{
    private readonly ListBox _list = new() { Width = 420, MaxHeight = 420, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White };
    private readonly TextBox _search = new();

    private ClipboardWindow() : base()
    {
        ShowActivated = true;
        _search.TextChanged += (_, _) => Render();
        _list.MouseDoubleClick += (_, _) => _ = PasteSelected();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Enter) _ = PasteSelected();
            else if (e.Key == Key.Delete && _list.SelectedItem is ListBoxItem { Tag: Altair.Core.Plugins.ClipboardHistory.Entry d }) { AppHost.Current.Clipboard.Items.Remove(d); AppHost.Current.Clipboard.Save(); Render(); }
            else if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control && _list.SelectedItem is ListBoxItem { Tag: Altair.Core.Plugins.ClipboardHistory.Entry p }) { p.Pinned = !p.Pinned; AppHost.Current.Clipboard.Save(); Render(); }
            else if (e.Key == Key.Down && _search.IsFocused) { _list.Focus(); _list.SelectedIndex = 0; }
        };
        Content = Pill(new StackPanel { Children = { new TextBlock { Text = "Буфер обмена", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold }, _search, _list,
            new TextBlock { Text = "Enter — вставить · Del — удалить · Ctrl+P — закрепить", Foreground = Brushes.Gray, FontSize = 11 } } }, 16);
        Deactivated += (_, _) => { if (IsVisible) Close(); };
        Render();
    }

    private void Render()
    {
        _list.Items.Clear();
        foreach (var e in AppHost.Current.Clipboard.Search(_search.Text).OrderByDescending(x => x.Pinned).Take(60))
            _list.Items.Add(new ListBoxItem { Content = (e.Pinned ? "📌 " : "") + (e.Text.Length > 120 ? e.Text[..120] + "…" : e.Text).Replace("\r", "").Replace('\n', ' '), Tag = e, Foreground = Brushes.White });
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private async Task PasteSelected()
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: Altair.Core.Plugins.ClipboardHistory.Entry e }) return;
        Close();
        await Task.Delay(120);
        await Sta.SetClipboard(e.Text);
        Altair.Windows.Input.InputSimulator.Combo("Ctrl+V");
    }

    public static void ShowAtCursor() { var w = new ClipboardWindow(); w.Show(); w.PlaceAtCursor(); w.Activate(); w._search.Focus(); }
}
