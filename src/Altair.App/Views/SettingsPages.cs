using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Altair.Core.Storage;
using Altair.Windows.Audio;
using Altair.Windows.Speech;
using Altair.Windows.Sys;
using Altair.Windows.Tts;
using Microsoft.Win32;

namespace Altair.App.Views;

public sealed class SettingsPage : UserControl
{
    private readonly AppHost _h = AppHost.Current;

    public SettingsPage()
    {
        var s = _h.Settings;
        void Save() => _h.SaveSettings();

        var mics = MicCapture.Devices();
        var voices = SafeVoices();
        var hotkeys = new StackPanel();
        foreach (var (id, title) in new[] { ("listen", "Удерживать для команды"), ("dictation", "Диктовка"), ("textAssist", "Помощник текста"), ("clipboard", "История буфера"), ("island", "Островок"), ("converter", "Конвертер"), ("screenshot", "Скриншот"), ("readAloud", "Чтение вслух") })
            hotkeys.Children.Add(Ui.Field(title, s.Hotkeys.GetValueOrDefault(id, ""), v => { s.Hotkeys[id] = v.Trim(); Save(); }));

        Content = Ui.Page(
            Ui.H1("Настройки"),
            Ui.Card("Голос и обращение",
                Ui.Field("Слова активации (через запятую)", string.Join(", ", s.WakeWords), v => { s.WakeWords = v.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList(); Save(); },
                    hint: "Короткие редкие слова распознаются лучше. Можно несколько: «альтаир, компьютер, джарвис»."),
                Ui.Combo("Режим прослушивания", new[] { ListenMode.WakeWord, ListenMode.PushToTalk, ListenMode.Always }, s.ListenMode, v => { s.ListenMode = v; Save(); },
                    v => v switch { ListenMode.WakeWord => "По слову активации", ListenMode.PushToTalk => "Только по клавише", _ => "Всегда (каждая фраза — команда)" }),
                Ui.Toggle("Режим префикса: команда сразу после слова («альтаир, открой ютуб»)", s.PrefixMode, v => { s.PrefixMode = v; Save(); }),
                Ui.Field("Как обращаться к вам", s.AddressAs, v => { s.AddressAs = v; Save(); }),
                Ui.SliderField("Окно ожидания команды после слова активации, с", 2, 20, s.ListenWindowSec, v => { s.ListenWindowSec = (int)v; Save(); }),
                Ui.Combo("Язык", new[] { "ru", "en" }, s.Language, v => { s.Language = v; Save(); }, v => v == "ru" ? "Русский" : "English")),
            Ui.Card("Распознавание речи",
                Ui.Combo("Микрофон", mics.Select(m => m.Index.ToString()).DefaultIfEmpty("0"), s.MicrophoneId ?? "0", v => { s.MicrophoneId = v; Save(); },
                    v => mics.FirstOrDefault(m => m.Index.ToString() == v).Name ?? "По умолчанию"),
                Ui.Combo("Движок", new[] { SttEngine.VoskLocal, SttEngine.VoskLocalAccurate, SttEngine.CloudOpenAI }, s.Stt, v => { s.Stt = v; Save(); },
                    v => v switch { SttEngine.VoskLocal => "Локальный (офлайн, быстрый)", SttEngine.VoskLocalAccurate => "Повышенная точность (офлайн, +1,8 ГБ, перепроверка большой моделью)", _ => "Облачный Whisper (свой ключ OpenAI/ProxyAPI)" }),
                Ui.SliderField("Приглушать другие программы во время ответа до, %", 0, 100, s.DuckingPercent, v => { s.DuckingPercent = (int)v; Save(); }, "%"),
                Ui.Toggle("Можно перебивать ответ голосом (микрофон не выключается, пока ассистент говорит)", s.PluginOn("bargeIn"), v => { s.Plugins["bargeIn"] = v; Save(); }),
                Ui.Hint("Изменения движка и микрофона применяются после перезапуска Altair.")),
            VoiceIdCard(),
            Ui.Card("Озвучка",
                Ui.Combo("Голос", new[] { TtsEngine.Windows, TtsEngine.OpenAI, TtsEngine.FishAudio, TtsEngine.CustomAudioOnly }, s.Tts, v => { s.Tts = v; Save(); },
                    v => v switch { TtsEngine.Windows => "Голоса Windows (офлайн, бесплатно)", TtsEngine.OpenAI => "OpenAI (свой ключ)", TtsEngine.FishAudio => "Fish Audio (свой ключ, свой голос)", _ => "Только свои аудиофайлы" }),
                Ui.Combo("Голос Windows", voices.DefaultIfEmpty(""), s.WindowsVoice ?? voices.FirstOrDefault() ?? "", v => { s.WindowsVoice = v; Save(); }),
                Ui.Hint("Больше голосов: «Параметры → Время и язык → Речь → Добавить голоса»."),
                Ui.SliderField("Скорость", -5, 5, s.TtsRate, v => { s.TtsRate = (int)v; Save(); }),
                Ui.SliderField("Громкость", 10, 100, s.TtsVolume, v => { s.TtsVolume = (int)v; Save(); }, "%"),
                Ui.Combo("Голос OpenAI", new[] { "onyx", "alloy", "echo", "fable", "nova", "shimmer", "ash", "coral", "sage" }, s.OpenAiVoice, v => { s.OpenAiVoice = v; Save(); }),
                Ui.Secret("Ключ Fish Audio", s.FishAudioKey, v => { s.FishAudioKey = v.Trim(); Save(); }),
                Ui.Field("ID голоса Fish Audio", s.FishAudioVoiceId, v => { s.FishAudioVoiceId = v.Trim(); Save(); }),
                Ui.BtnAsync("Создать свой голос по образцу…", async () =>
                {
                    var dlg = new OpenFileDialog { Filter = "Аудио (*.wav;*.mp3;*.m4a)|*.wav;*.mp3;*.m4a", Multiselect = true, Title = "Записи голоса: 10–60 секунд чистой речи" };
                    if (dlg.ShowDialog() != true) return;
                    var id = await new Altair.Core.AI.FishAudio(s.FishAudioKey).CreateVoiceAsync("Altair — мой голос", dlg.FileNames);
                    s.FishAudioVoiceId = id; s.Tts = TtsEngine.FishAudio; Save();
                    Ui.Info("Голос создан и выбран. Образцы на сервер больше не отправляются.");
                }),
                Ui.BtnAsync("Проверить голос", () => _h.Tts.SayAsync($"Здравствуйте, {s.AddressAs}. Я ваш ассистент."))),
            Ui.Card("Горячие клавиши", hotkeys, Ui.Btn("Применить", () => _h.RebindHotkeys(), true)),
            Ui.Card("Система",
                Ui.Toggle("Запускать вместе с Windows", s.StartWithWindows, v => { s.StartWithWindows = v; Save(); Shell.SetAutostart(v, Environment.ProcessPath!); }),
                Ui.Toggle("Запускать с правами администратора без запроса (один раз спросит UAC)", s.ElevatedAutostart, v =>
                {
                    s.ElevatedAutostart = v; Save();
                    Shell.SetElevatedAutostart(v, Environment.ProcessPath!);
                    if (v) Shell.SetAutostart(false, Environment.ProcessPath!);
                }),
                Ui.Hint("С правами администратора Altair может управлять программами, запущенными от администратора. Окна UAC Windows не позволяет нажимать программно — это защищённый рабочий стол."),
                Ui.Toggle("Игровой режим (ответы только текстом)", s.GameMode, v => { s.GameMode = v; Save(); }),
                Ui.Field("Погода: город", s.WeatherCity, v => { s.WeatherCity = v; Save(); }),
                Ui.Row(Ui.Field("Широта", s.WeatherLat.ToString(System.Globalization.CultureInfo.InvariantCulture), v => { if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) { s.WeatherLat = d; Save(); } }),
                       Ui.Field("Долгота", s.WeatherLon.ToString(System.Globalization.CultureInfo.InvariantCulture), v => { if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) { s.WeatherLon = d; Save(); } })),
                Ui.Row(
                    Ui.Btn("Папка данных", () => Process.Start(new ProcessStartInfo(AppPaths.Root) { UseShellExecute = true })?.Dispose()),
                    Ui.Btn("Экспорт настроек", () =>
                    {
                        var d = new SaveFileDialog { FileName = "altair-backup.json", Filter = "JSON|*.json" };
                        if (d.ShowDialog() == true) Json.Save(d.FileName, new Backup { Settings = s, Collections = _h.Store.Collections });
                    }),
                    Ui.Btn("Импорт настроек", () =>
                    {
                        var d = new OpenFileDialog { Filter = "JSON|*.json" };
                        if (d.ShowDialog() != true) return;
                        var b = Json.Load<Backup>(d.FileName);
                        if (b.Settings != null) Json.Save(AppPaths.Settings, b.Settings);
                        foreach (var c in b.Collections) _h.Store.Save(c);
                        Ui.Info("Импортировано. Перезапустите Altair.");
                    }))),
            Ui.Card("Обновления",
                Ui.Text($"Версия {typeof(App).Assembly.GetName().Version}"),
                Ui.Field("Адрес обновлений (папка с релизами Velopack)", s.UpdateUrl, v => { s.UpdateUrl = v.Trim(); Save(); }),
                Ui.BtnAsync("Проверить обновления", async () =>
                {
                    if (string.IsNullOrWhiteSpace(s.UpdateUrl)) { Ui.Info("Укажите адрес обновлений"); return; }
                    var um = new Velopack.UpdateManager(s.UpdateUrl);
                    var info = await um.CheckForUpdatesAsync();
                    if (info == null) { Ui.Info("Установлена последняя версия"); return; }
                    if (!Ui.Ask($"Доступна версия {info.TargetFullRelease.Version}. Обновить?")) return;
                    await um.DownloadUpdatesAsync(info);
                    um.ApplyUpdatesAndRestart(info);
                })));
    }

    private static List<string> SafeVoices() { try { return TtsService.WindowsVoices().ToList(); } catch { return new(); } }

    private UIElement VoiceIdCard()
    {
        var s = _h.Settings;
        var status = Ui.Hint($"Записано голосов: {_h.VoiceId.Profiles.Count} из 4");
        return Ui.Card("Только мой голос",
            Ui.Toggle("Выполнять команды только от записанных голосов (перезапуск)", s.OnlyMyVoice, v => { s.OnlyMyVoice = v; _h.SaveSettings(); }),
            Ui.Hint("Нужна модель голоса (~13 МБ, скачается автоматически). «Стоп» работает для любого голоса."),
            Ui.BtnAsync("Записать голос (6 фраз)", async () =>
            {
                if (!s.OnlyMyVoice) { Ui.Info("Сначала включите опцию и перезапустите Altair — загрузится модель голоса."); return; }
                if (_h.VoiceId.Profiles.Count >= 4) { Ui.Info("Уже записано 4 голоса. Сбросьте, чтобы записать заново."); return; }
                var phrases = new[] { "Альтаир, открой браузер", "Сделай громкость тридцать", "Какая сегодня погода", "Включи следующую песню", "Запиши заметку на завтра", "Выключи компьютер через час" };
                var vectors = new List<float[]>();
                var tcs = new TaskCompletionSource<float[]?>();
                void Handler(Utterance u) { if (u.SpeakerVector != null) tcs.TrySetResult(u.SpeakerVector); }
                _h.Speech.Recognized += Handler;
                try
                {
                    foreach (var p in phrases)
                    {
                        tcs = new TaskCompletionSource<float[]?>();
                        status.Text = $"Скажите: «{p}» ({vectors.Count + 1}/6)";
                        var v = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
                        if (v != null) vectors.Add(v);
                    }
                }
                finally { _h.Speech.Recognized -= Handler; }
                _h.VoiceId.Profiles.Add(VoiceId.Average(vectors));
                _h.VoiceId.Save();
                status.Text = $"Готово. Записано голосов: {_h.VoiceId.Profiles.Count} из 4";
            }, true),
            Ui.Btn("Сбросить голоса", () => { _h.VoiceId.Profiles.Clear(); _h.VoiceId.Save(); status.Text = "Голоса сброшены"; }),
            status);
    }

    private sealed class Backup
    {
        public AppSettings? Settings { get; set; }
        public List<Altair.Core.Models.CommandCollection> Collections { get; set; } = new();
    }
}

public sealed class AppearancePage : UserControl
{
    public AppearancePage(MainWindow main)
    {
        var h = AppHost.Current; var s = h.Settings;
        void Apply() { h.SaveSettings(); ThemeManager.Apply(s); main.Reload(); }

        var themes = new WrapPanel();
        foreach (var t in ThemeManager.Themes)
        {
            var sw = new Border { Width = 150, Height = 90, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 10, 10), Cursor = System.Windows.Input.Cursors.Hand,
                Background = new SolidColorBrush(Ui.Parse(t.Bg)), BorderBrush = new SolidColorBrush(Ui.Parse(s.Theme == t.Name ? s.Accent : t.Border)), BorderThickness = new Thickness(s.Theme == t.Name ? 3 : 1) };
            sw.Child = new StackPanel
            {
                Margin = new Thickness(12),
                Children =
                {
                    new Border { Height = 22, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Ui.Parse(t.Card)) },
                    new TextBlock { Text = t.Name, Foreground = new SolidColorBrush(Ui.Parse(t.Fg)), Margin = new Thickness(0, 10, 0, 0) },
                }
            };
            var name = t.Name;
            sw.MouseLeftButtonUp += (_, _) => { s.Theme = name; Apply(); };
            themes.Children.Add(sw);
        }

        var accents = new WrapPanel();
        foreach (var a in ThemeManager.Accents)
        {
            var c = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Margin = new Thickness(0, 0, 10, 10), Background = new SolidColorBrush(Ui.Parse(a)), Cursor = System.Windows.Input.Cursors.Hand,
                BorderBrush = Brushes.White, BorderThickness = new Thickness(s.Accent == a ? 3 : 0) };
            var hex = a;
            c.MouseLeftButtonUp += (_, _) => { s.Accent = hex; Apply(); };
            accents.Children.Add(c);
        }

        Content = Ui.Page(
            Ui.H1("Оформление"),
            Ui.Card("Тема", themes),
            Ui.Card("Акцентный цвет", accents, Ui.Field("Свой цвет (#RRGGBB)", s.Accent, v => { if (v.Length == 7 && v[0] == '#') { s.Accent = v; Apply(); } })),
            Ui.Card("Фон",
                Ui.Row(
                    Ui.Btn("Выбрать картинку, GIF или видео…", () =>
                    {
                        var d = new OpenFileDialog { Filter = "Медиа|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.mp4;*.wmv;*.mov" };
                        if (d.ShowDialog() == true) { s.BackgroundFile = d.FileName; Apply(); }
                    }),
                    Ui.Btn("Убрать фон", () => { s.BackgroundFile = null; Apply(); })),
                Ui.Combo("Эффект", new[] { "none", "stars", "snow", "waves", "grid" }, s.BackgroundEffect, v => { s.BackgroundEffect = v; Apply(); },
                    v => v switch { "none" => "Без эффекта", "stars" => "Звёзды", "snow" => "Снег", "waves" => "Волны", _ => "Сетка" }),
                Ui.SliderField("Прозрачность карточек", 0, 14, s.CardBlur, v => { s.CardBlur = v; Apply(); })),
            Ui.Card("Шрифт", Ui.Combo("Шрифт интерфейса", ThemeManager.Fonts, s.FontFamily, v => { s.FontFamily = v; Apply(); })));
    }
}

public sealed class LogPage : UserControl
{
    public LogPage()
    {
        var h = AppHost.Current;
        var box = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Code, Consolas"), MinHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        lock (h.LogLines) box.Text = string.Join("\n", h.LogLines);
        Action<string> onLog = l => Dispatcher.BeginInvoke(() => { box.AppendText("\n" + l); box.ScrollToEnd(); });
        h.Log += onLog;
        Unloaded += (_, _) => h.Log -= onLog;
        Content = Ui.Page(Ui.H1("Журнал"), Ui.Hint("Что услышал ассистент, какие команды выполнил и какие были ошибки."), box,
            Ui.Btn("Открыть папку журналов", () => Process.Start(new ProcessStartInfo(AppPaths.Logs) { UseShellExecute = true })?.Dispose()));
    }
}

public sealed class SplashWindow : Window
{
    private readonly TextBlock _text = new() { Text = "Запуск…", HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ProgressBar _bar = new() { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 14, 0, 0) };

    public SplashWindow()
    {
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; Width = 420; Height = 220;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Topmost = true; AllowsTransparency = true; Background = Brushes.Transparent;
        var b = new Border { CornerRadius = new CornerRadius(20), Padding = new Thickness(30) };
        b.SetResourceReference(Border.BackgroundProperty, "Bg");
        b.Child = Ui.Stack(new TextBlock { Text = "Altair", FontSize = 34, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center }, _text, _bar,
            Ui.Hint("При первом запуске скачиваются модели распознавания (~50 МБ)."));
        Content = b;
    }

    public void Report(string what, double p) { _text.Text = $"{what}: {p:P0}"; _bar.Value = p; }
}
