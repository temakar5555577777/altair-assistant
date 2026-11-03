using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Altair.App.Overlays;
using Altair.Core.Agent;
using Altair.Core.AI;
using Altair.Core.Engine;
using Altair.Core.Integrations;
using Altair.Core.Models;
using Altair.Core.Plugins;
using Altair.Core.Storage;
using Altair.Core.Triggers;
using Altair.Windows.Actions;
using Altair.Windows.Audio;
using Altair.Windows.Browser;
using Altair.Windows.Input;
using Altair.Windows.Screen;
using Altair.Windows.Speech;
using Altair.Windows.Sys;
using Altair.Windows.Tts;

namespace Altair.App;

/// <summary>Корень приложения: создаёт и связывает все сервисы.</summary>
public sealed class AppHost : IAsyncDisposable
{
    public static AppHost Current { get; private set; } = null!;

    public AppSettings Settings { get; private set; }
    public CommandStore Store { get; } = new();
    public UsageMeter Usage { get; private set; } = null!;
    public ChatService Chat { get; private set; } = null!;
    public ActionRegistry Registry { get; } = new();
    public ActionRunner Runner { get; private set; } = null!;
    public VolumeControl Volume { get; } = new();
    public TtsService Tts { get; private set; } = null!;
    public Assistant Assistant { get; private set; } = null!;
    public SpeechService Speech { get; private set; } = null!;
    public AgentRunner Agent { get; private set; } = null!;
    public CommandBuilder Builder { get; private set; } = null!;
    public BrowserBridge Browser { get; } = new();
    public PlatformServices Platform { get; private set; } = null!;
    public Scheduler Scheduler { get; private set; } = null!;
    public EventTriggerHub Events { get; private set; } = null!;
    public SystemEventsWatcher? Watcher { get; private set; }
    public KeyboardHook? Keys { get; private set; }
    public TelegramBot? Telegram { get; private set; }
    public RemotePcServer? Remote { get; private set; }
    public ClipboardHistory Clipboard { get; } = new();
    public TextExpander Expander { get; } = new();
    public DocIndex Docs { get; } = new();
    public VoiceId VoiceId { get; private set; } = new();
    public LoopbackTranscriber Loopback { get; } = new();
    public SnapDetector Snap { get; } = new();
    public VariableStore Globals { get; } = new();
    public ILicenseService License { get; } = new OpenLicense();

    public IslandWindow Island { get; private set; } = null!;
    public event Action<string>? Log;
    public List<string> LogLines { get; } = new();

    private readonly Dispatcher _ui;
    private DispatcherTimer? _tick, _saveTimer, _clipTimer;
    private string _lastClip = "";
    private bool _dictating;
    private readonly BreakReminder _breaks = new();
    private DateTime _lastCleanup = DateTime.MinValue;

    public AppHost()
    {
        Current = this;
        _ui = Application.Current.Dispatcher;
        AppPaths.Ensure();
        Settings = Json.Load<AppSettings>(AppPaths.Settings);
    }

    public void Write(string line)
    {
        var l = $"{DateTime.Now:HH:mm:ss} {line}";
        lock (LogLines) { LogLines.Add(l); if (LogLines.Count > 1000) LogLines.RemoveAt(0); }
        try { File.AppendAllText(Path.Combine(AppPaths.Logs, $"{DateTime.Now:yyyy-MM-dd}.log"), l + Environment.NewLine); } catch { }
        Log?.Invoke(l);
    }

    public void SaveSettings()
    {
        // Отложенное сохранение: много изменений подряд → одна запись
        _saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
        {
            _saveTimer!.Stop();
            Settings.LastSeen = DateTime.Now;
            Json.Save(AppPaths.Settings, Settings);
            Chat.InvalidateProviders();
        }, _ui);
        _saveTimer.Stop(); _saveTimer.Start();
    }

    public async Task StartAsync(IProgress<(string, double)> progress)
    {
        // Команды: при первом запуске — встроенные паки
        Store.Load();
        if (Store.Collections.Count == 0)
        {
            foreach (var p in DefaultPacks.All()) Store.Collections.Add(p);
            Store.SaveAll();
        }
        foreach (var p in DefaultPacks.All().Where(p => Store.Collections.All(c => c.Id != p.Id)))
        { Store.Collections.Add(p); Store.Save(p); } // новые паки после обновления

        Usage = new UsageMeter();
        Chat = new ChatService(() => Settings, Usage);
        Runner = new ActionRunner(Registry, name => Store.FindByName(name));
        Tts = new TtsService(() => Settings, Volume);
        Globals.RegisterProvider("battery", () => SystemEventsWatcher.BatteryPercent()?.ToString() ?? "100");
        Globals.RegisterProvider("volume", () => Volume.Get().ToString());
        Globals.RegisterProvider("window", () => WindowManager.Find("active")?.Title ?? "");
        Globals.RegisterProvider("clipboard", () => _lastClip);

        Assistant = new Assistant(() => Settings, Store, Runner, Chat, Tts, Globals);
        Assistant.Log += Write;
        Agent = new AgentRunner(Chat, Runner, new WindowsAgentEnvironment(Browser, () => Settings), () => Settings);
        Agent.Log += Write;
        Builder = new CommandBuilder(Chat);
        Assistant.AgentHandler = RunAgentAsync;

        Platform = new PlatformServices
        {
            Settings = () => Settings, Volume = Volume, Speaker = Tts, Chat = Chat, Browser = Browser,
            RunAgent = RunAgentAsync,
            Notify = (t, m) => _ui.Invoke(() => Tray.Notify(t, m)),
            ShowText = t => _ui.Invoke(() => Island.Flash(t, 8)),
            StartDictation = () => { ToggleDictation(); return Task.CompletedTask; },
            Layout = ApplyLayoutAsync,
        };
        WindowsActions.RegisterAll(Registry, Platform);

        // Оверлеи
        Island = new IslandWindow();
        Assistant.StateChanged += st => _ui.BeginInvoke(() => Island.SetState(st));
        Assistant.Handled += h => _ui.BeginInvoke(() => Island.Flash(h.Heard + " → " + h.Result, 3));
        Agent.StepStarted += (s, i, n) => _ui.BeginInvoke(() => Island.SetAgentStep(i, n, AgentRunner.Describe(s)));
        Agent.Finished += r => _ui.BeginInvoke(() => Island.Flash(r, 6));
        Agent.Confirm = ConfirmAgentStepAsync;
        Tts.Speaking += t => _ui.BeginInvoke(() => Island.Flash(t, Math.Clamp(t.Length / 12, 3, 12)));

        // Триггеры
        Scheduler = new Scheduler(Store.AllTriggers);
        Scheduler.Fired += (cmd, _) => _ = Assistant.ExecuteAsync(cmd, null, cmd.Name, "schedule");
        Scheduler.Rebuild(DateTime.Now, Settings.LastSeen);
        Store.Changed += () => Scheduler.Rebuild(DateTime.Now);
        Events = new EventTriggerHub(Store.AllTriggers);
        Events.Fired += (cmd, _, arg) =>
        {
            var slots = new Dictionary<string, string> { ["event"] = arg ?? "" };
            _ = Assistant.ExecuteAsync(cmd, slots, cmd.Name, "event");
        };
        Watcher = new SystemEventsWatcher(Volume);
        Watcher.Event += (e, arg) => { Events.Raise(e, arg); OnSystemEvent(e, arg); };

        _tick = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => OnTick(), _ui);
        _tick.Start();

        // Горячие клавиши
        Keys = new KeyboardHook();
        Keys.Char += OnChar;
        RebindHotkeys();
        Expander.Snippets = Settings.Snippets;

        // Буфер обмена
        Clipboard.Load();
        _clipTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background, (_, _) => PollClipboard(), _ui);
        if (Settings.PluginOn("clipboard", true)) _clipTimer.Start();

        // Сеть: браузер, Telegram, второй ПК, умный дом, подключения
        try { Browser.Start(); } catch (Exception ex) { Write("Мост браузера: " + ex.Message); }
        await RestartIntegrationsAsync();

        // Распознавание речи (модели скачиваются при первом запуске)
        VoiceId = VoiceId.Load();
        Speech = new SpeechService(() => Settings) { VocabularyHint = () => string.Join(", ", Store.All().Take(80).Select(c => c.Phrases.FirstOrDefault()).Where(p => p != null)) };
        Speech.Log += Write;
        Speech.Partial += p => _ui.BeginInvoke(() => Island.SetPartial(p));
        Speech.Recognized += OnUtterance;
        Speech.Audio += (b, n) => { if (Settings.PluginOn("snapse")) Snap.Feed(b, n); };
        Snap.Snaps += n => { Write($"Snapse: {n} щелч."); Events.Raise(SystemEvents.Snapse, n.ToString()); };
        Tts.SpeakingChanged += speaking => Speech.Pause(speaking && !Settings.PluginOn("bargeIn"));
        try { await Speech.StartAsync(progress, CancellationToken.None); }
        catch (Exception ex) { Write("Распознавание не запущено: " + ex.Message); Tray.Notify("Микрофон", "Распознавание не запущено: " + ex.Message); }

        Events.Raise(SystemEvents.AssistantStarted);
        Write("Altair запущен");
    }

    public async Task RestartIntegrationsAsync()
    {
        var s = Settings;
        if (Telegram != null) { await Telegram.DisposeAsync(); Telegram = null; Platform.Telegram = null; }
        if (s.TelegramEnabled && s.TelegramToken.Length > 10)
        {
            Telegram = new TelegramBot(s.TelegramToken, s.TelegramOwnerId)
            {
                ListCommands = () => Store.All().Where(c => c.Enabled && c.ExposeToTelegram).Select(c => new TgCommandItem(c.Id, c.Name, c.RequireConfirmation)).ToList(),
                RunCommand = async id =>
                {
                    var cmd = Store.All().FirstOrDefault(c => c.Id == id);
                    return cmd == null ? "Команда не найдена" : await Assistant.ExecuteAsync(cmd, null, cmd.Name, "telegram");
                },
                AskAi = text => Assistant.HandleAsync(text, "telegram"),
                Screenshot = () => Task.Run<byte[]?>(() => { using var b = ScreenCapture.Capture(); return ScreenCapture.Png(b); }),
                Shell = async cmd => (await Altair.Windows.Sys.Shell.Cmd(cmd, false, CancellationToken.None)).Output,
            };
            Telegram.Log += Write;
            Telegram.Start();
            Platform.Telegram = Telegram;
        }

        if (Remote != null) { await Remote.DisposeAsync(); Remote = null; }
        if (s.RemoteServerEnabled)
        {
            Remote = new RemotePcServer(s.RemoteSharedKey)
            {
                RunCommand = text => Assistant.HandleAsync(text, "remote"),
                Say = text => Tts.SayAsync(text),
            };
            Remote.KeyIssued += key => _ui.Invoke(() => { Settings.RemoteSharedKey = key; SaveSettings(); });
            try { Remote.Start(s.RemoteServerPort); } catch (Exception ex) { Write("Второй ПК: " + ex.Message); }
        }

        Platform.Home.Providers.Clear();
        if (s.HomeAssistantToken.Length > 0) Platform.Home.Providers.Add(new HomeAssistantClient(s.HomeAssistantUrl, s.HomeAssistantToken));
        if (s.YandexIotToken.Length > 0) Platform.Home.Providers.Add(new YandexHomeClient(s.YandexIotToken));

        foreach (var c in Platform.Mcp.Values) await c.DisposeAsync();
        Platform.Mcp.Clear();
        foreach (var m in s.McpServers.Where(m => m.Enabled))
        {
            var client = new McpClient(m);
            client.Log += Write;
            try { await client.StartAsync(CancellationToken.None); Platform.Mcp[m.Name] = client; Write($"Подключение {m.Name}: {client.Tools.Count} инструментов"); }
            catch (Exception ex) { Write($"Подключение {m.Name} не запущено: {ex.Message}"); }
        }
    }

    // ---------- Речь ----------

    private void OnUtterance(Utterance u)
    {
        Write("Услышал: " + u.Text);
        _ui.BeginInvoke(() => Island.SetPartial(u.Text));
        var normalized = PhraseNormalizer.Normalize(u.Text);
        if (Settings.OnlyMyVoice && !VoiceId.IsKnown(u.SpeakerVector) && normalized is not ("стоп" or "хватит"))
        { Write("Голос не опознан — пропускаю"); return; }

        if (_dictating)
        {
            if (normalized is "стоп диктовка" or "закончить диктовку" or "конец диктовки") { ToggleDictation(); return; }
            _ = TypeDictationAsync(u.Text);
            return;
        }
        _ = Assistant.OnSpeechAsync(u.Text);
    }

    public void ToggleDictation()
    {
        _dictating = !_dictating;
        _ui.BeginInvoke(() => Island.Flash(_dictating ? "🎙 Диктовка: говорите, «конец диктовки» — остановить" : "Диктовка выключена", 3));
    }

    private async Task TypeDictationAsync(string text)
    {
        var final = text;
        if (Settings.PluginOn("dictationAi", true) && ChatService.IsConfigured(Settings.Provider() ?? new AiProviderConfig()))
        {
            try
            {
                var r = await Chat.OneShot("Расставь знаки препинания и заглавные буквы в продиктованном тексте. Не меняй слова. Верни только текст.", text, new ChatOptions(0.1, 400));
                if (r.Text.Length > 0) final = r.Text;
            }
            catch { /* без ИИ — как есть */ }
        }
        else final = char.ToUpper(text[0]) + text[1..] + ".";
        await InputSimulator.Type(final + " ");
    }

    public async Task<string> RunAgentAsync(string task)
    {
        if (Agent.Running) return "Агент уже занят — скажите «стоп», чтобы прервать.";
        _ui.Invoke(() => Island.SetAgentStep(0, 0, "Составляю план: " + task));
        try { return await Agent.RunAsync(task); }
        catch (Exception ex) { return "Агент: " + ex.Message; }
    }

    private Task<bool> ConfirmAgentStepAsync(ConfirmRequest req, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Invoke(() =>
        {
            var w = new ConfirmWindow(req.Description, req.VoiceApproveForbidden);
            w.Decided += ok => tcs.TrySetResult(ok);
            w.Show();
            ct.Register(() => _ui.BeginInvoke(() => { w.Close(); tcs.TrySetResult(false); }));
            // Голосовой ответ: «разрешить»/«да» (кроме оплаты и паролей), «отклонить»/«нет»
            _ = Task.Run(async () =>
            {
                var ans = await Assistant.AskAsync(req.VoiceApproveForbidden ? "Это оплата или пароль. Разрешите кнопкой на экране." : "Разрешить?", null, 60);
                if (ans == null) return;
                bool yes = Assistant.IsYes(ans) || PhraseNormalizer.Normalize(ans).StartsWith("разреш");
                if (yes && req.VoiceApproveForbidden) return;
                tcs.TrySetResult(yes);
                _ = _ui.BeginInvoke(() => w.Close());
            });
        });
        return tcs.Task;
    }

    // ---------- Горячие клавиши ----------

    public void RebindHotkeys()
    {
        if (Keys == null) return;
        Keys.ClearBindings();
        var h = Settings.Hotkeys;
        void Bind(string id, Action down, Action? up = null)
        {
            if (!h.TryGetValue(id, out var spec) || string.IsNullOrWhiteSpace(spec)) return;
            try { Keys.Bind(spec, down, up); } catch (Exception ex) { Write($"Клавиша {id}: {ex.Message}"); }
        }

        Bind("listen", () => Speech?.PushToTalk(true), () => Speech?.PushToTalk(false));
        if (Settings.PluginOn("dictation", true)) Bind("dictation", ToggleDictation);
        if (Settings.PluginOn("textAssist", true)) Bind("textAssist", () => _ui.BeginInvoke(() => TextAssistWindow.ShowAtCursor()));
        if (Settings.PluginOn("clipboard", true)) Bind("clipboard", () => _ui.BeginInvoke(() => ClipboardWindow.ShowAtCursor()));
        if (Settings.PluginOn("island", true)) Bind("island", () => _ui.BeginInvoke(() => Island.ToggleExpanded()));
        if (Settings.PluginOn("converter", true)) Bind("converter", () => _ui.BeginInvoke(() => ConverterWindow.ShowAtCursor()));
        if (Settings.PluginOn("screenshots", true)) Bind("screenshot", () => _ui.BeginInvoke(() => ScreenshotOverlay.Start()));
        if (Settings.PluginOn("readAloud", true)) { h.TryAdd("readAloud", "Ctrl+Alt+R"); Bind("readAloud", () => _ = ReadAloudAsync()); }

        foreach (var (cmd, trg) in Store.AllTriggers().Where(t => t.Item2.Kind == TriggerKind.Hotkey && t.Item2.Enabled && !string.IsNullOrEmpty(t.Item2.Hotkey)))
        {
            var c = cmd;
            try { Keys.Bind(trg.Hotkey!, () => _ = Assistant.ExecuteAsync(c, null, c.Name, "hotkey")); }
            catch (Exception ex) { Write($"Клавиша команды «{cmd.Name}»: {ex.Message}"); }
        }
    }

    private async Task ReadAloudAsync()
    {
        var text = await WindowsActions.GetSelectedText();
        if (string.IsNullOrWhiteSpace(text) && Browser.Connected) text = await Browser.ReadText(CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(text)) await Tts.SayAsync(text.Length > 5000 ? text[..5000] : text);
    }

    private void OnChar(char c)
    {
        if (!Settings.PluginOn("snippets", true)) return;
        var hit = Expander.Feed(c, v => Globals.Expand(v));
        if (hit is { } h)
            _ = Task.Run(async () =>
            {
                await Task.Delay(30);
                InputSimulator.Press("backspace", h.Backspaces);
                await InputSimulator.Type(h.Text);
            });
    }

    // ---------- Фоновые задачи ----------

    private void PollClipboard()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText()) return;
            var t = System.Windows.Clipboard.GetText();
            if (t == _lastClip) return;
            _lastClip = t;
            Clipboard.Add(t);
            Clipboard.Save();
        }
        catch { /* буфер занят другой программой */ }
    }

    private void OnTick()
    {
        var now = DateTime.Now;
        Scheduler.Tick(now);

        if (Settings.PluginOn("breaks"))
        {
            _breaks.WorkMinutes = Settings.BreakWorkMinutes;
            var idle = Altair.Windows.Native.Win32.IdleTime();
            var msg = _breaks.Check(now, idle.TotalMinutes < 3);
            if (msg != null) { Tray.Notify("Перерыв", msg); _ = Tts.SayAsync(msg); }
        }

        if (Settings.PluginOn("cleanup") && (now - _lastCleanup).TotalMinutes >= 30)
        {
            _lastCleanup = now;
            _ = Task.Run(() =>
            {
                var dl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                var c = new DownloadsCleaner();
                int n = c.Apply(c.Plan(dl));
                if (n > 0) Write($"Уборка загрузок: разложено файлов — {n}");
            });
        }

        if (Settings.PluginOn("awayLock") && !string.IsNullOrEmpty(Settings.AwayLockDevice))
            _ = AwayLock.CheckAsync(Settings.AwayLockDevice!);

        if (now.Minute % 5 == 0 && now.Second < 15) { Settings.LastSeen = now; SaveSettings(); }
    }

    private void OnSystemEvent(string e, string? arg)
    {
        if (e == SystemEvents.AppStarted && Settings.PluginOn("gameMode") && arg != null && GameDetector.IsGame(arg))
        {
            Settings.GameMode = true; Write("Игровой режим: " + arg);
        }
        else if (e == SystemEvents.AppClosed && Settings.GameMode && arg != null && GameDetector.IsGame(arg))
        {
            Settings.GameMode = false; Write("Игровой режим выключен");
        }
    }

    public async Task ApplyLayoutAsync(string name)
    {
        var layouts = Json.Load<Dictionary<string, List<SavedWindow>>>(Path.Combine(AppPaths.Data, "layouts.json"));
        if (!layouts.TryGetValue(name, out var list)) throw new InvalidOperationException($"Раскладка «{name}» не найдена. Сохраните её в «Расширения → Раскладки окон».");
        foreach (var w in list)
        {
            var win = WindowManager.List().FirstOrDefault(x => x.Process.Equals(w.Process, StringComparison.OrdinalIgnoreCase));
            if (win == null && !string.IsNullOrEmpty(w.Path))
            {
                try { Process.Start(new ProcessStartInfo(w.Path) { UseShellExecute = true })?.Dispose(); } catch { continue; }
                for (int i = 0; i < 20 && win == null; i++) { await Task.Delay(300); win = WindowManager.List().FirstOrDefault(x => x.Process.Equals(w.Process, StringComparison.OrdinalIgnoreCase)); }
            }
            if (win != null) WindowManager.Place(win.Handle, w.X, w.Y, w.W, w.H);
        }
    }

    public void SaveLayout(string name)
    {
        var path = Path.Combine(AppPaths.Data, "layouts.json");
        var layouts = Json.Load<Dictionary<string, List<SavedWindow>>>(path);
        var list = new List<SavedWindow>();
        foreach (var w in WindowManager.List().Where(w => !w.Minimized && w.Process != "AltairAssistant"))
        {
            Altair.Windows.Native.Win32.GetWindowRect(w.Handle, out var r);
            string? exe = null;
            try { exe = Process.GetProcessById(w.Pid).MainModule?.FileName; } catch { }
            list.Add(new SavedWindow { Process = w.Process, Path = exe, X = r.Left, Y = r.Top, W = r.Width, H = r.Height });
        }
        layouts[name] = list;
        Json.Save(path, layouts);
    }

    public async ValueTask DisposeAsync()
    {
        _tick?.Stop(); _clipTimer?.Stop();
        Settings.LastSeen = DateTime.Now;
        Json.Save(AppPaths.Settings, Settings);
        Keys?.Dispose();
        Watcher?.Dispose();
        Speech?.Dispose();
        Loopback.Dispose();
        Tts?.Dispose();
        if (Telegram != null) await Telegram.DisposeAsync();
        if (Remote != null) await Remote.DisposeAsync();
        foreach (var c in Platform?.Mcp.Values ?? Enumerable.Empty<McpClient>()) await c.DisposeAsync();
        await Browser.DisposeAsync();
        Volume.Dispose();
    }
}

public sealed class SavedWindow
{
    public string Process { get; set; } = "";
    public string? Path { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
}

/// <summary>Определение игр: процесс из папки Steam/Epic/… или известное имя.</summary>
public static class GameDetector
{
    private static readonly string[] Known = { "cs2", "dota2", "valorant", "fortniteclient-win64-shipping", "gta5", "rdr2", "minecraft", "javaw", "eldenring", "cyberpunk2077", "r5apex", "overwatch", "pubg", "tslgame", "rocketleague", "witcher3", "bg3" };
    public static bool IsGame(string process)
    {
        var p = process.ToLowerInvariant();
        if (Known.Contains(p)) return true;
        try
        {
            var path = System.Diagnostics.Process.GetProcessesByName(process).FirstOrDefault()?.MainModule?.FileName?.ToLowerInvariant() ?? "";
            return path.Contains("steamapps\\common") || path.Contains("epic games") || path.Contains("riot games") || path.Contains("gog galaxy\\games");
        }
        catch { return false; }
    }
}

/// <summary>Блокировка при отходе: если сопряжённый по Bluetooth телефон перестал быть на связи 60 с — блокируем ПК.</summary>
public static class AwayLock
{
    private static DateTime? _lostSince;
    public static async Task CheckAsync(string deviceName)
    {
        try
        {
            var selector = global::Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            var devices = await global::Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector);
            var info = devices.FirstOrDefault(d => d.Name.Contains(deviceName, StringComparison.OrdinalIgnoreCase));
            if (info == null) return;
            using var dev = await global::Windows.Devices.Bluetooth.BluetoothDevice.FromIdAsync(info.Id);
            bool connected = dev?.ConnectionStatus == global::Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected;
            if (connected) { _lostSince = null; return; }
            _lostSince ??= DateTime.Now;
            if ((DateTime.Now - _lostSince.Value).TotalSeconds >= 60)
            {
                _lostSince = null;
                Altair.Windows.Native.Win32.LockWorkStation();
            }
        }
        catch { }
    }
}
