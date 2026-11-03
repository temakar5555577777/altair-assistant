using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Altair.App.Overlays;
using Altair.Core.Integrations;
using Altair.Core.Plugins;
using Altair.Core.Storage;

namespace Altair.App.Views;

/// <summary>Расширения: все плагины (включая те, что у образца только в Plus) — включение и настройка.</summary>
public sealed class ExtensionsPage : UserControl
{
    private readonly AppHost _h = AppHost.Current;

    public ExtensionsPage()
    {
        var s = _h.Settings;
        var page = new List<UIElement> { Ui.H1("Расширения"), Ui.Hint("Всё доступно сразу, без подписки. Включите нужное и настройте.") };

        foreach (var theme in PluginCatalog.All.GroupBy(p => p.Theme))
        {
            page.Add(Ui.H2(theme.Key));
            var tiles = new WrapPanel();
            foreach (var p in theme)
            {
                var card = Ui.Card(p.Title,
                    Ui.Hint(p.Description + (p.Hotkey != null ? $"  ·  {s.Hotkeys.GetValueOrDefault(p.Id, p.Hotkey)}" : "")),
                    Ui.Toggle("Включено", s.PluginOn(p.Id, DefaultOn(p.Id)), v => { s.Plugins[p.Id] = v; _h.SaveSettings(); _h.RebindHotkeys(); OnToggle(p.Id, v); }),
                    Ui.Btn("Настроить", () => Configure(p.Id)));
                card.Width = 300;
                tiles.Children.Add(card);
            }
            page.Add(tiles);
        }

        page.Add(Ui.H2("Браузер"));
        page.Add(Ui.Card("Расширение для Chrome / Edge / Яндекс / Opera / Brave",
            Ui.Text(_h.Browser.Connected ? $"✓ Подключено: {_h.Browser.BrowserName}" : "Не подключено"),
            Ui.Hint("1) Откройте chrome://extensions  2) Включите «Режим разработчика»  3) «Загрузить распакованное» → выберите папку расширения."),
            Ui.Btn("Открыть папку расширения", () => Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "BrowserExtension")) { UseShellExecute = true })?.Dispose())));

        page.Add(Ui.H2("Раскладки окон"));
        var layoutName = new TextBox { Text = "Работа" };
        page.Add(Ui.Card("Сохранить текущее расположение окон",
            layoutName,
            Ui.Row(Ui.Btn("Сохранить", () => { _h.SaveLayout(layoutName.Text.Trim()); Ui.Info("Сохранено. Используйте действие «Раскладка окон» в команде."); }),
                   Ui.BtnAsync("Применить", () => _h.ApplyLayoutAsync(layoutName.Text.Trim())))));

        Content = Ui.Page(page.ToArray());
    }

    private static bool DefaultOn(string id) => id is "clipboard" or "dictation" or "textAssist" or "converter" or "screenshots" or "snippets" or "island" or "pcControl" or "readAloud";

    private void OnToggle(string id, bool on)
    {
        switch (id)
        {
            case "pet": PetWindow.Toggle(on); break;
            case "avatar": AvatarWindow.Toggle(on); break;
            case "subtitles": SubtitlesWindow.Toggle(on); break;
            case "pcControl": _h.Settings.TelegramEnabled = on; _ = _h.RestartIntegrationsAsync(); break;
            case "remotePc": _h.Settings.RemoteServerEnabled = on; _ = _h.RestartIntegrationsAsync(); break;
        }
    }

    private void Configure(string id)
    {
        var s = _h.Settings;
        UIElement body = id switch
        {
            "pcControl" => Ui.Stack(
                Ui.Text("Управление компьютером из Telegram через вашего собственного бота."),
                Ui.Hint("1) В @BotFather: /newbot → скопируйте токен.  2) Вставьте токен ниже.  3) Узнайте свой ID: напишите боту — он ответит вашим ID (или @userinfobot)."),
                Ui.Secret("Токен бота", s.TelegramToken, v => { s.TelegramToken = v.Trim(); _h.SaveSettings(); }),
                Ui.Field("Telegram ID владельца", s.TelegramOwnerId == 0 ? "" : s.TelegramOwnerId.ToString(), v => { if (long.TryParse(v.Trim(), out var n)) { s.TelegramOwnerId = n; _h.SaveSettings(); } }),
                Ui.Toggle("Включить PC Control", s.TelegramEnabled, v => { s.TelegramEnabled = v; _h.SaveSettings(); }),
                Ui.Hint("Бот принимает сообщения только от владельца. /menu — команды (отметьте «Показывать в меню Telegram» у нужных), /screen — скриншот, /cmd … — консоль, любой текст — вопрос ассистенту."),
                Ui.Row(Ui.BtnAsync("Проверить и запустить", async () =>
                {
                    var name = await new TelegramBot(s.TelegramToken, s.TelegramOwnerId).CheckAsync();
                    s.TelegramEnabled = true; _h.SaveSettings(); await _h.RestartIntegrationsAsync();
                    Ui.Info($"Бот {name} работает. Напишите ему /menu.");
                }, true))),

            "smartHome" => Ui.Stack(
                Ui.Field("Home Assistant — адрес", s.HomeAssistantUrl, v => { s.HomeAssistantUrl = v.Trim(); _h.SaveSettings(); }),
                Ui.Secret("Home Assistant — долгоживущий токен (Профиль → Безопасность)", s.HomeAssistantToken, v => { s.HomeAssistantToken = v.Trim(); _h.SaveSettings(); }),
                Ui.Secret("Яндекс Дом — OAuth-токен (iot:view, iot:control)", s.YandexIotToken, v => { s.YandexIotToken = v.Trim(); _h.SaveSettings(); }),
                Ui.Hint("Команды: действие «Умный дом → Устройство» с именем как в приложении («свет в спальне») и состоянием on/off/toggle/число."),
                Ui.BtnAsync("Показать устройства", async () =>
                {
                    await _h.RestartIntegrationsAsync();
                    var lines = new List<string>();
                    foreach (var p in _h.Platform.Home.Providers)
                        try { lines.AddRange((await p.DevicesAsync(default)).Select(d => $"{p.Name}: {d.Name} {(d.Room.Length > 0 ? "(" + d.Room + ")" : "")} — {d.State}")); }
                        catch (Exception ex) { lines.Add($"{p.Name}: {ex.Message}"); }
                    Ui.Info(lines.Count == 0 ? "Нет устройств" : string.Join("\n", lines.Take(40)));
                }, true)),

            "remotePc" => RemotePcPanel(),
            "connections" => McpPanel(),

            "streamer" => Ui.Stack(
                Ui.Field("OBS WebSocket (Инструменты → Настройки WebSocket-сервера)", s.ObsUrl, v => { s.ObsUrl = v.Trim(); _h.SaveSettings(); }),
                Ui.Secret("Пароль OBS", s.ObsPassword, v => { s.ObsPassword = v; _h.SaveSettings(); }),
                Ui.BtnAsync("Проверить OBS", async () => { await _h.Platform.Obs.ConnectAsync(s.ObsUrl, s.ObsPassword, default); Ui.Info("Сцены: " + string.Join(", ", await _h.Platform.Obs.Scenes())); }),
                Ui.Field("Twitch — канал", s.TwitchChannel, v => { s.TwitchChannel = v.Trim(); _h.SaveSettings(); }),
                Ui.Field("Twitch — ник бота (для отправки; пусто — только чтение)", s.TwitchNick, v => { s.TwitchNick = v.Trim(); _h.SaveSettings(); }),
                Ui.Secret("Twitch — OAuth-токен", s.TwitchOAuth, v => { s.TwitchOAuth = v.Trim(); _h.SaveSettings(); }),
                Ui.Toggle("Озвучивать сообщения чата", s.PluginOn("twitchTts"), v => { s.Plugins["twitchTts"] = v; _h.SaveSettings(); }),
                Ui.BtnAsync("Подключить чат", async () =>
                {
                    var chat = new TwitchChat();
                    chat.Message += m => { if (s.PluginOn("twitchTts")) _ = _h.Tts.SayAsync($"{m.User} пишет: {m.Text}"); Application.Current.Dispatcher.BeginInvoke(() => _h.Island.Flash($"💬 {m.User}: {m.Text}", 5)); };
                    await chat.ConnectAsync(s.TwitchChannel, s.TwitchNick, s.TwitchOAuth, default);
                    Ui.Info("Чат подключён.");
                }),
                Ui.Hint("Голосом: действие «OBS» (scene/record/stream/mute/replay) — например «начни запись», «сцена игра».")),

            "snippets" => SnippetsPanel(),

            "cleanup" => Ui.Stack(
                Ui.Text("Раз в 30 минут раскладывает «Загрузки» по папкам: Изображения, Видео, Музыка, Документы, Архивы, Программы, Торренты. Файлы моложе 10 минут не трогаются."),
                Ui.Btn("Предпросмотр", () =>
                {
                    var dl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    var plan = new DownloadsCleaner().Plan(dl);
                    Ui.Info(plan.Count == 0 ? "Нечего раскладывать" : string.Join("\n", plan.Take(30).Select(m => $"{Path.GetFileName(m.From)} → {Path.GetFileName(Path.GetDirectoryName(m.To))}")) + (plan.Count > 30 ? $"\n…и ещё {plan.Count - 30}" : ""));
                }),
                Ui.Btn("Разложить сейчас", () =>
                {
                    var dl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    var c = new DownloadsCleaner(); Ui.Info($"Перемещено файлов: {c.Apply(c.Plan(dl))}");
                }, true)),

            "docAsk" => DocsPanel(),

            "breaks" => Ui.Stack(Ui.SliderField("Разминка каждые, мин", 20, 120, s.BreakWorkMinutes, v => { s.BreakWorkMinutes = (int)v; _h.SaveSettings(); }),
                Ui.Hint("Глаза — по правилу 20-20-20. Таймер сбрасывается, если вы отошли от компьютера.")),

            "awayLock" => Ui.Stack(
                Ui.Text("Блокирует компьютер, если ваш телефон (сопряжённый по Bluetooth) больше минуты не на связи."),
                Ui.Field("Часть имени Bluetooth-устройства", s.AwayLockDevice ?? "", v => { s.AwayLockDevice = v.Trim(); _h.SaveSettings(); }),
                Ui.Hint("Телефон должен быть сопряжён в «Параметры → Bluetooth». Некоторые телефоны разрывают связь в режиме сна — проверьте перед использованием.")),

            "subtitles" or "summary" => Ui.Stack(
                Ui.Text("Распознаёт звук, который играет компьютер (видео, созвоны), офлайн."),
                Ui.Btn(_h.Loopback.Running ? "Остановить" : "Запустить запись звука", () => SubtitlesWindow.Toggle(!_h.Loopback.Running), true),
                Ui.BtnAsync("Сделать конспект записанного", async () =>
                {
                    var t = _h.Loopback.Transcript;
                    if (t.Length < 50) { Ui.Info("Пока слишком мало текста."); return; }
                    var r = await _h.Chat.OneShot("Сделай структурированный конспект: тема, ключевые мысли, решения и задачи (кто/что/когда). По-русски.", t.Length > 60000 ? t[^60000..] : t);
                    var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"Конспект {DateTime.Now:yyyy-MM-dd HH-mm}.txt");
                    await File.WriteAllTextAsync(file, r.Text + "\n\n--- Расшифровка ---\n" + t);
                    Process.Start(new ProcessStartInfo(file) { UseShellExecute = true })?.Dispose();
                })),

            "gameMode" => Ui.Stack(Ui.Text("При запуске игры (Steam, Epic, Riot и известные игры) ассистент отвечает только текстом на островке и не жмёт клавиши в фоне."),
                Ui.Hint("В играх с античитом эмуляция клавиш и мыши может считаться нарушением — используйте только голосовые ответы и запуск программ.")),

            "snapse" => Ui.Stack(Ui.Text("Щелчки пальцами (или хлопки) как команды: 1, 2 или 3 щелчка подряд."),
                Ui.Hint("Добавьте команде триггер «Событие → Snapse» с уточнением 1, 2 или 3."),
                Ui.SliderField("Чувствительность", 3, 12, _h.Snap.Sensitivity, v => _h.Snap.Sensitivity = v)),

            _ => Ui.Stack(Ui.Text(PluginCatalog.All.FirstOrDefault(p => p.Id == id)?.Description ?? ""),
                Ui.Hint("Дополнительных настроек нет. Горячие клавиши — в «Настройки → Горячие клавиши»."))
        };
        var win = new SimpleWindow(PluginCatalog.All.FirstOrDefault(p => p.Id == id)?.Title ?? id, body);
        win.Show();
    }

    private UIElement RemotePcPanel()
    {
        var s = _h.Settings;
        var code = Ui.Text("", 28);
        var host = new TextBox { Text = "192.168.1." }; var pair = new TextBox(); var name = new TextBox { Text = "Второй ПК" };
        var list = Ui.Hint(string.Join("\n", s.RemotePcs.Select(p => $"• {p.Name} — {p.Host}:{p.Port}")));
        return Ui.Stack(
            Ui.H2("Этот компьютер принимает команды"),
            Ui.Toggle("Разрешить управление этим ПК по локальной сети", s.RemoteServerEnabled, v => { s.RemoteServerEnabled = v; _h.SaveSettings(); _ = _h.RestartIntegrationsAsync(); }),
            Ui.Hint($"Порт {s.RemoteServerPort}. Имя: {Environment.MachineName}. Windows спросит разрешение брандмауэра — разрешите для частных сетей."),
            Ui.Btn("Показать код сопряжения", () =>
            {
                if (_h.Remote == null) { Ui.Info("Сначала включите приём команд"); return; }
                code.Text = _h.Remote.BeginPairing() + "  (3 мин)";
            }, true),
            code,
            Ui.H2("Подключить другой компьютер"),
            Ui.Hint("Имя"), name, Ui.Hint("IP-адрес другого ПК"), host, Ui.Hint("Код с другого ПК"), pair,
            Ui.BtnAsync("Сопрячь", async () =>
            {
                var key = await RemotePcProtocol.PairAsync(host.Text.Trim(), s.RemoteServerPort, pair.Text.Trim(), default);
                s.RemotePcs.RemoveAll(p => p.Host == host.Text.Trim());
                s.RemotePcs.Add(new RemotePcConfig { Name = name.Text.Trim(), Host = host.Text.Trim(), Port = s.RemoteServerPort, SharedKey = key });
                _h.SaveSettings();
                Ui.Info("Готово. Действия «Второй ПК» теперь работают.");
            }),
            list);
    }

    private UIElement McpPanel()
    {
        var s = _h.Settings;
        var list = new StackPanel();
        void Render()
        {
            list.Children.Clear();
            foreach (var m in s.McpServers.ToList())
            {
                var running = _h.Platform.Mcp.TryGetValue(m.Name, out var c);
                list.Children.Add(Ui.Card(m.Name,
                    Ui.Hint($"{m.Command} {m.Args}"),
                    Ui.Hint(running ? $"✓ запущено, инструментов: {c!.Tools.Count}: {string.Join(", ", c.Tools.Take(12).Select(t => t.Name))}" : "не запущено"),
                    Ui.Toggle("Включено", m.Enabled, v => { m.Enabled = v; _h.SaveSettings(); }),
                    Ui.Btn("Удалить", () => { s.McpServers.Remove(m); _h.SaveSettings(); Render(); })));
            }
        }
        Render();
        var catalog = new ComboBox();
        foreach (var e in McpCatalog.Items) catalog.Items.Add(new ComboBoxItem { Content = $"{e.Name} — {e.Description}", Tag = e });
        return Ui.Stack(
            Ui.Text("Подключения по стандарту MCP: ассистент и агент получают инструменты сервисов."),
            Ui.Hint("Нужен Node.js (для npx) или uv (для uvx). Ключи сервисов вводятся при добавлении."),
            catalog,
            Ui.Row(
                Ui.Btn("Добавить из каталога", () =>
                {
                    if (catalog.SelectedItem is not ComboBoxItem { Tag: McpCatalog.Entry e }) return;
                    var cfg = new McpServerConfig { Name = e.Name, Command = e.Command, Args = Environment.ExpandEnvironmentVariables(e.Args) };
                    foreach (var key in e.EnvKeys) { var v = Prompt.Ask(e.Name, $"Значение {key}:"); if (v == null) return; cfg.Env[key] = v; }
                    s.McpServers.Add(cfg); _h.SaveSettings(); Render();
                }, true),
                Ui.Btn("Своё подключение…", () =>
                {
                    var cmd = Prompt.Ask("Своё MCP-подключение", "Команда запуска, например: npx -y @scope/server аргументы");
                    if (string.IsNullOrWhiteSpace(cmd)) return;
                    var parts = cmd.Split(' ', 2);
                    s.McpServers.Add(new McpServerConfig { Name = Prompt.Ask("Имя", "Название подключения:") ?? parts[0], Command = parts[0], Args = parts.Length > 1 ? parts[1] : "" });
                    _h.SaveSettings(); Render();
                }),
                Ui.BtnAsync("Перезапустить все", async () => { await _h.RestartIntegrationsAsync(); Render(); })),
            list);
    }

    private UIElement SnippetsPanel()
    {
        var s = _h.Settings;
        var text = string.Join("\n", s.Snippets.Select(kv => $"{kv.Key} = {kv.Value.Replace("\n", "\\n")}"));
        return Ui.Stack(
            Ui.Text("Наберите сокращение в любой программе — оно заменится текстом."),
            Ui.Field("По одному в строке: «сокращение = текст». \\n — перенос строки. Переменные: {date}, {time}, {clipboard}", text, v =>
            {
                var d = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var line in v.Split('\n'))
                {
                    var i = line.IndexOf('=');
                    if (i <= 0) continue;
                    var key = line[..i].Trim(); var val = line[(i + 1)..].Trim().Replace("\\n", "\n");
                    if (key.Length > 0) d[key] = val;
                }
                s.Snippets = d; _h.Expander.Snippets = d; _h.SaveSettings();
            }, true, height: 200),
            Ui.Hint("Совет: начинайте сокращения с «;» — так они не сработают случайно."));
    }

    private UIElement DocsPanel()
    {
        var s = _h.Settings;
        var status = Ui.Hint($"В индексе фрагментов: {_h.Docs.Count}");
        var q = new TextBox();
        var answer = Ui.Text("");
        return Ui.Stack(
            Ui.Text("Ищет по содержимому ваших документов (txt, md, docx, csv, json, html, код) и отвечает с указанием файла."),
            Ui.Field("Папки (по одной в строке)", string.Join("\n", s.DocFolders), v => { s.DocFolders = v.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList(); _h.SaveSettings(); }, true),
            Ui.BtnAsync("Проиндексировать", async () =>
            {
                status.Text = "Индексирую…";
                await Task.Run(() => { foreach (var f in s.DocFolders.Where(Directory.Exists)) _h.Docs.AddFolder(f); });
                status.Text = $"В индексе фрагментов: {_h.Docs.Count}";
            }),
            status,
            Ui.Hint("Вопрос"), q,
            Ui.BtnAsync("Спросить", async () =>
            {
                var hits = _h.Docs.Search(q.Text, 6);
                if (hits.Count == 0) { answer.Text = "Ничего не найдено. Проиндексируйте папки."; return; }
                var ctx = string.Join("\n\n", hits.Select(h => $"[{Path.GetFileName(h.Chunk.File)}]\n{h.Chunk.Text}"));
                var r = await _h.Chat.OneShot("Ответь на вопрос только по фрагментам документов. В конце укажи имена файлов-источников. Если ответа нет — так и скажи.", $"Фрагменты:\n{ctx}\n\nВопрос: {q.Text}");
                answer.Text = r.Text;
            }, true),
            answer);
    }
}

public sealed class SimpleWindow : Window
{
    public SimpleWindow(string title, UIElement body)
    {
        Title = title; Width = 620; Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Bg");
        Content = new ScrollViewer { Content = new Border { Padding = new Thickness(20), Child = body }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
