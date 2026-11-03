using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Altair.Core.Storage;
using Altair.Windows.Actions;
using Altair.Windows.Sys;

namespace Altair.App.Views;

/// <summary>Главная: несколько рабочих столов с виджетами. Правый клик по виджету — переместить/удалить.</summary>
public sealed class HomePage : UserControl
{
    public static readonly (string Kind, string Title)[] Kinds =
    {
        ("clock", "Часы"), ("system", "Система"), ("weather", "Погода"), ("lastCommands", "Последние команды"),
        ("notes", "Заметки"), ("aiUsage", "Расход ИИ"), ("quick", "Быстрые команды"), ("volume", "Громкость"),
        ("timer", "Таймер"), ("currency", "Курсы валют"), ("planner", "Планер"), ("clipboard", "Буфер обмена"),
        ("calendar", "Календарь"), ("battery", "Батарея"), ("network", "Сеть"), ("agent", "ИИ-агент"),
    };

    private readonly AppHost _h = AppHost.Current;
    private int _desk;
    private readonly WrapPanel _wrap = new();
    private readonly List<DispatcherTimer> _timers = new();

    public HomePage()
    {
        Unloaded += (_, _) => { foreach (var t in _timers) t.Stop(); };
        Build();
    }

    private void Build()
    {
        foreach (var t in _timers) t.Stop();
        _timers.Clear();
        var s = _h.Settings;
        var tabs = new WrapPanel();
        for (int i = 0; i < Math.Max(1, s.Desks); i++)
        {
            int d = i;
            var b = new ToggleButton { Content = $"Стол {i + 1}", IsChecked = d == _desk, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(12, 6, 12, 6) };
            b.Click += (_, _) => { _desk = d; Build(); };
            tabs.Children.Add(b);
        }
        tabs.Children.Add(Ui.Btn("+ стол", () => { s.Desks++; _h.SaveSettings(); _desk = s.Desks - 1; Build(); }));
        if (s.Desks > 1) tabs.Children.Add(Ui.Btn("− стол", () =>
        {
            s.Widgets.RemoveAll(w => w.Desk == _desk);
            foreach (var w in s.Widgets.Where(w => w.Desk > _desk)) w.Desk--;
            s.Desks--; _desk = 0; _h.SaveSettings(); Build();
        }));

        var add = new ComboBox { Width = 220, Margin = new Thickness(12, 0, 0, 8) };
        add.Items.Add(new ComboBoxItem { Content = "+ Добавить виджет", IsEnabled = false });
        foreach (var (k, t) in Kinds) add.Items.Add(new ComboBoxItem { Content = t, Tag = k });
        add.SelectedIndex = 0;
        add.SelectionChanged += (_, _) =>
        {
            if (add.SelectedItem is ComboBoxItem { Tag: string kind })
            {
                s.Widgets.Add(new WidgetPlacement { Kind = kind, Desk = _desk, X = s.Widgets.Count });
                _h.SaveSettings(); Build();
            }
        };
        tabs.Children.Add(add);

        _wrap.Children.Clear();
        foreach (var w in s.Widgets.Where(w => w.Desk == _desk).OrderBy(w => w.Y).ThenBy(w => w.X))
            _wrap.Children.Add(Widget(w));

        var greeting = DateTime.Now.Hour switch { < 6 => "Доброй ночи", < 12 => "Доброе утро", < 18 => "Добрый день", _ => "Добрый вечер" };
        Content = Ui.Page(Ui.H1($"{greeting}, {s.AddressAs}"), Ui.Hint($"Скажите «{s.WakeWords.FirstOrDefault()}» и команду, или нажмите {s.PushToTalkKey}."), tabs, _wrap);
    }

    private Border Widget(WidgetPlacement w)
    {
        var title = Kinds.FirstOrDefault(k => k.Kind == w.Kind).Title ?? w.Kind;
        FrameworkElement body;
        try { body = Body(w); } catch (Exception ex) { body = Ui.Hint(ex.Message); }
        var card = Ui.Card(Ui.Hint(title.ToUpperInvariant()), body);
        card.Width = 270 * Math.Clamp(w.W, 1, 3) - 12 * (Math.Clamp(w.W, 1, 3) - 1);
        card.MinHeight = 150 * Math.Clamp(w.H, 1, 3);

        var menu = new ContextMenu();
        void Item(string t, Action a) { var mi = new MenuItem { Header = t }; mi.Click += (_, _) => { a(); _h.SaveSettings(); Build(); }; menu.Items.Add(mi); }
        var list = _h.Settings.Widgets;
        Item("← Раньше", () => { var i = list.IndexOf(w); if (i > 0) { list.RemoveAt(i); list.Insert(i - 1, w); Renumber(); } });
        Item("Позже →", () => { var i = list.IndexOf(w); if (i < list.Count - 1) { list.RemoveAt(i); list.Insert(i + 1, w); Renumber(); } });
        Item("Шире", () => w.W = Math.Min(3, w.W + 1));
        Item("Уже", () => w.W = Math.Max(1, w.W - 1));
        Item("Выше", () => w.H = Math.Min(3, w.H + 1));
        Item("Ниже", () => w.H = Math.Max(1, w.H - 1));
        for (int d = 0; d < _h.Settings.Desks; d++) { int dd = d; if (dd != _desk) Item($"На стол {dd + 1}", () => w.Desk = dd); }
        Item("Удалить", () => list.Remove(w));
        card.ContextMenu = menu;
        return card;

        void Renumber() { for (int i = 0; i < list.Count; i++) { list[i].X = i; list[i].Y = 0; } }
    }

    private DispatcherTimer Every(double sec, Action a)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(sec) };
        t.Tick += (_, _) => { try { a(); } catch { } };
        t.Start(); _timers.Add(t); a();
        return t;
    }

    private FrameworkElement Body(WidgetPlacement w)
    {
        var s = _h.Settings;
        switch (w.Kind)
        {
            case "clock":
            {
                var time = new TextBlock { FontSize = 44, FontWeight = FontWeights.Light };
                var date = Ui.Hint("");
                Every(1, () => { time.Text = DateTime.Now.ToString("HH:mm:ss"); date.Text = DateTime.Now.ToString("dddd, d MMMM", CultureInfo.GetCultureInfo("ru-RU")); });
                return Ui.Stack(time, date);
            }
            case "system":
            {
                var t = Ui.Text("…");
                var cpu = new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                Every(2, () =>
                {
                    var info = new Microsoft.VisualBasic.Devices.ComputerInfo();
                    var ram = 100 - info.AvailablePhysicalMemory * 100.0 / info.TotalPhysicalMemory;
                    t.Text = $"Процессор: {cpu.NextValue():0}%\nПамять: {ram:0}% из {info.TotalPhysicalMemory / 1073741824.0:0} ГБ\nРаботает: {TimeSpan.FromMilliseconds(Environment.TickCount64):d\\д\\ hh\\:mm}";
                });
                return t;
            }
            case "battery":
            {
                var t = Ui.Text("…", 28);
                Every(30, () => { var b = SystemEventsWatcher.BatteryPercent(); t.Text = b == null ? "Нет батареи" : $"{b}%"; });
                return t;
            }
            case "weather":
            {
                var t = Ui.Text("Загрузка…");
                _ = LoadWeather(t);
                return Ui.Stack(Ui.Hint(s.WeatherCity), t);
            }
            case "currency":
            {
                var t = Ui.Text("Загрузка…");
                _ = LoadRates(t);
                return t;
            }
            case "lastCommands":
            {
                var sp = new StackPanel();
                Every(3, () =>
                {
                    sp.Children.Clear();
                    List<Altair.Core.Engine.HistoryItem> items;
                    lock (_h.Assistant.History) items = _h.Assistant.History.Take(6).ToList();
                    if (items.Count == 0) sp.Children.Add(Ui.Hint("Пока нет команд"));
                    foreach (var i in items)
                        sp.Children.Add(new TextBlock { Text = $"{(i.Ok ? "✓" : "✗")} {i.At:HH:mm}  {i.Heard}", Foreground = Ui.Res(i.Ok ? "Fg" : "Danger"), Margin = new Thickness(0, 0, 0, 4), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
                });
                return sp;
            }
            case "notes":
            {
                var tb = new TextBox { Text = s.Notes, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90 };
                tb.TextChanged += (_, _) => { s.Notes = tb.Text; _h.SaveSettings(); };
                return tb;
            }
            case "aiUsage":
            {
                var t = Ui.Text("");
                Every(5, () => t.Text = $"Запросов сегодня: {_h.Usage.RequestsToday()}\nТокенов: {_h.Usage.TokensToday():N0}\nПровайдер: {s.Provider()?.Name ?? "—"}");
                return t;
            }
            case "quick":
            {
                var wrap = new WrapPanel();
                foreach (var phrase in new[] { "открой браузер", "сверни все", "сделай скриншот", "состояние системы", "выключи звук", "заблокируй компьютер" })
                    wrap.Children.Add(Ui.BtnAsync(phrase, () => _h.Assistant.HandleAsync(phrase, "ui")));
                return wrap;
            }
            case "volume":
            {
                var sl = new Slider { Minimum = 0, Maximum = 100, Value = SafeVolume() };
                var cap = Ui.Text($"{sl.Value:0}%", 22);
                sl.ValueChanged += (_, e) => { try { _h.Volume.Set((int)e.NewValue); cap.Text = $"{e.NewValue:0}%"; } catch { } };
                return Ui.Stack(cap, sl, Ui.Row(Ui.Btn("Без звука", () => _h.Volume.ToggleMute()), Ui.Btn("Микрофон", () => _h.Volume.ToggleMic())));
            }
            case "timer":
            {
                var cap = Ui.Text("00:00", 32);
                DateTime? end = null;
                Every(1, () =>
                {
                    if (end == null) return;
                    var left = end.Value - DateTime.Now;
                    if (left <= TimeSpan.Zero) { end = null; cap.Text = "Готово!"; _ = _h.Tts.SayAsync("Таймер сработал."); Tray.Notify("Таймер", "Время вышло"); return; }
                    cap.Text = left.ToString(left.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");
                });
                var row = new WrapPanel();
                foreach (var m in new[] { 1, 5, 10, 25, 60 }) { int mm = m; row.Children.Add(Ui.Btn($"{m} мин", () => end = DateTime.Now.AddMinutes(mm))); }
                row.Children.Add(Ui.Btn("Сброс", () => { end = null; cap.Text = "00:00"; }));
                return Ui.Stack(cap, row);
            }
            case "planner":
            {
                var path = Path.Combine(AppPaths.Data, "planner.json");
                var items = Json.Load<List<PlanItem>>(path);
                var sp = new StackPanel();
                void Render()
                {
                    sp.Children.Clear();
                    foreach (var it in items.ToList())
                    {
                        var cb = new CheckBox { Content = it.Text, IsChecked = it.Done };
                        cb.Click += (_, _) => { it.Done = cb.IsChecked == true; Json.Save(path, items); };
                        cb.MouseRightButtonUp += (_, _) => { items.Remove(it); Json.Save(path, items); Render(); };
                        sp.Children.Add(cb);
                    }
                    var input = new TextBox();
                    input.KeyDown += (_, e) =>
                    {
                        if (e.Key != System.Windows.Input.Key.Enter || input.Text.Trim().Length == 0) return;
                        items.Add(new PlanItem { Text = input.Text.Trim() }); Json.Save(path, items); Render();
                    };
                    sp.Children.Add(input);
                    sp.Children.Add(Ui.Hint("Enter — добавить, правый клик — удалить"));
                }
                Render();
                return sp;
            }
            case "clipboard":
            {
                var sp = new StackPanel();
                Every(3, () =>
                {
                    sp.Children.Clear();
                    foreach (var e in _h.Clipboard.Items.Take(5))
                    {
                        var b = new Button { Content = e.Text.Length > 40 ? e.Text[..40].Replace('\n', ' ') + "…" : e.Text.Replace('\n', ' '), HorizontalContentAlignment = HorizontalAlignment.Left };
                        var text = e.Text;
                        b.Click += (_, _) => System.Windows.Clipboard.SetText(text);
                        sp.Children.Add(b);
                    }
                });
                return sp;
            }
            case "calendar":
                return new Calendar { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            case "network":
            {
                var t = Ui.Text("…");
                Every(15, () =>
                {
                    var online = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
                    var ssid = SystemEventsWatcher.CurrentSsid();
                    t.Text = (online ? "Онлайн" : "Нет сети") + (ssid != null ? $"\nWi-Fi: {ssid}" : "");
                });
                return t;
            }
            case "agent":
            {
                var tb = new TextBox { Text = "" };
                var res = Ui.Hint("Опишите задачу — агент выполнит её на компьютере.");
                return Ui.Stack(tb, Ui.BtnAsync("Выполнить", async () => { res.Text = "Работаю…"; res.Text = await _h.RunAgentAsync(tb.Text); }, true), res);
            }
        }
        return Ui.Hint("Неизвестный виджет");
    }

    private int SafeVolume() { try { return _h.Volume.Get(); } catch { return 50; } }

    private async Task LoadWeather(TextBlock t)
    {
        try
        {
            var s = _h.Settings;
            using var http = new HttpClient();
            var url = string.Create(CultureInfo.InvariantCulture, $"https://api.open-meteo.com/v1/forecast?latitude={s.WeatherLat}&longitude={s.WeatherLon}&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m&timezone=auto");
            var j = JsonNode.Parse(await http.GetStringAsync(url))!["current"]!;
            t.Text = $"{j["temperature_2m"]:0}°, ощущается {j["apparent_temperature"]}°\n{WeatherText(j["weather_code"]!.GetValue<int>())}, ветер {j["wind_speed_10m"]} км/ч";
        }
        catch (Exception ex) { t.Text = "Нет данных: " + ex.Message; }
    }

    private static string WeatherText(int code) => code switch
    {
        0 => "Ясно", 1 or 2 => "Переменная облачность", 3 => "Пасмурно", 45 or 48 => "Туман",
        >= 51 and <= 57 => "Морось", >= 61 and <= 67 => "Дождь", >= 71 and <= 77 => "Снег",
        >= 80 and <= 82 => "Ливень", >= 85 and <= 86 => "Снегопад", >= 95 => "Гроза", _ => "—"
    };

    private static async Task LoadRates(TextBlock t)
    {
        try
        {
            using var http = new HttpClient();
            var j = JsonNode.Parse(await http.GetStringAsync("https://open.er-api.com/v6/latest/USD"))!["rates"]!;
            double kzt = j["KZT"]!.GetValue<double>(), rub = j["RUB"]!.GetValue<double>(), eur = j["EUR"]!.GetValue<double>();
            t.Text = $"USD: {kzt:0.00} ₸ · {rub:0.00} ₽\nEUR: {kzt / eur:0.00} ₸ · {rub / eur:0.00} ₽\nRUB: {kzt / rub:0.00} ₸";
        }
        catch (Exception ex) { t.Text = "Нет данных: " + ex.Message; }
    }
}

public sealed class PlanItem { public string Text { get; set; } = ""; public bool Done { get; set; } }
