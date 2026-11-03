using System.Text.Json;
using System.Text.Json.Serialization;
using Altair.Core.Models;

namespace Altair.Core.Storage;

public static class AppPaths
{
    public static string Root { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AltairAssistant");

    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Commands => Path.Combine(Root, "commands");
    public static string Models => Path.Combine(Root, "models");
    public static string Voices => Path.Combine(Root, "voices");
    public static string Logs => Path.Combine(Root, "logs");
    public static string Data => Path.Combine(Root, "data");
    public static string Plugins => Path.Combine(Root, "plugins");

    public static void Ensure()
    {
        foreach (var d in new[] { Root, Commands, Models, Voices, Logs, Data, Plugins }) Directory.CreateDirectory(d);
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static T Load<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch (JsonException)
        {
            // Повреждённый файл — сохраняем копию и начинаем с чистого
            File.Copy(path, path + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
        }
        return new T();
    }

    /// <summary>Атомарная запись: во временный файл, затем замена.</summary>
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, true);
    }
}

public enum SttEngine { VoskLocal, VoskLocalAccurate, CloudOpenAI, CloudCompatible }
public enum TtsEngine { Windows, OpenAI, FishAudio, CustomAudioOnly }
public enum ConfirmMode { All, Important, Auto, FullAccess }
public enum ListenMode { WakeWord, PushToTalk, Always }

public sealed class AiProviderConfig
{
    public string Id { get; set; } = "openai";
    public string Kind { get; set; } = "openai"; // openai-compatible | anthropic | gemini | yandex | gigachat
    public string Name { get; set; } = "OpenAI";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";
    public string? VisionModel { get; set; }
    public string? FolderId { get; set; } // YandexGPT
    public bool Enabled { get; set; } = true;
}

public sealed class McpServerConfig
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string Args { get; set; } = "";
    public Dictionary<string, string> Env { get; set; } = new();
    public bool Enabled { get; set; } = true;
}

public sealed class RemotePcConfig
{
    public string Name { get; set; } = "Второй ПК";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 47821;
    public string SharedKey { get; set; } = "";
}

public sealed class AppSettings
{
    public string Language { get; set; } = "ru";

    // Голос и распознавание
    public List<string> WakeWords { get; set; } = new() { "альтаир", "компьютер" };
    public ListenMode ListenMode { get; set; } = ListenMode.WakeWord;
    public bool PrefixMode { get; set; } = true; // команда в той же фразе: «альтаир, открой браузер»
    public string PushToTalkKey { get; set; } = "F9";
    public string? MicrophoneId { get; set; }
    public int Sensitivity { get; set; } = 70; // 60–80 оптимально
    public SttEngine Stt { get; set; } = SttEngine.VoskLocal;
    public string VoskModelRu { get; set; } = "vosk-model-small-ru-0.22";
    public string VoskModelEn { get; set; } = "vosk-model-small-en-us-0.15";
    public string VoskModelAccurate { get; set; } = "vosk-model-ru-0.42";
    public int DuckingPercent { get; set; } = 30;
    public bool OnlyMyVoice { get; set; }
    public int ListenWindowSec { get; set; } = 6;
    public string AddressAs { get; set; } = "сэр"; // обращение

    // Озвучка
    public TtsEngine Tts { get; set; } = TtsEngine.Windows;
    public string? WindowsVoice { get; set; }
    public int TtsRate { get; set; } = 0;
    public int TtsVolume { get; set; } = 100;
    public string OpenAiVoice { get; set; } = "onyx";
    public string FishAudioVoiceId { get; set; } = "";
    public string FishAudioKey { get; set; } = "";
    public string Persona { get; set; } = "altair";

    // ИИ
    public List<AiProviderConfig> Providers { get; set; } = new()
    {
        new() { Id = "openai", Kind = "openai", Name = "OpenAI", BaseUrl = "https://api.openai.com/v1", Model = "gpt-4o-mini", VisionModel = "gpt-4o-mini" },
        new() { Id = "anthropic", Kind = "anthropic", Name = "Claude", BaseUrl = "https://api.anthropic.com", Model = "claude-sonnet-4-5", Enabled = false },
        new() { Id = "gemini", Kind = "gemini", Name = "Gemini", BaseUrl = "https://generativelanguage.googleapis.com", Model = "gemini-2.5-flash", Enabled = false },
        new() { Id = "deepseek", Kind = "openai", Name = "DeepSeek", BaseUrl = "https://api.deepseek.com/v1", Model = "deepseek-chat", Enabled = false },
        new() { Id = "grok", Kind = "openai", Name = "Grok", BaseUrl = "https://api.x.ai/v1", Model = "grok-3-mini", Enabled = false },
        new() { Id = "openrouter", Kind = "openai", Name = "OpenRouter", BaseUrl = "https://openrouter.ai/api/v1", Model = "openai/gpt-4o-mini", Enabled = false },
        new() { Id = "yandex", Kind = "yandex", Name = "YandexGPT", BaseUrl = "https://llm.api.cloud.yandex.net", Model = "yandexgpt-lite/latest", Enabled = false },
        new() { Id = "gigachat", Kind = "gigachat", Name = "GigaChat", BaseUrl = "https://gigachat.devices.sberbank.ru/api/v1", Model = "GigaChat", Enabled = false },
        new() { Id = "ollama", Kind = "openai", Name = "Ollama (локально)", BaseUrl = "http://localhost:11434/v1", Model = "llama3.1", Enabled = false },
        new() { Id = "lmstudio", Kind = "openai", Name = "LM Studio (локально)", BaseUrl = "http://localhost:1234/v1", Model = "local-model", Enabled = false },
    };
    public string ActiveProvider { get; set; } = "openai";
    public string SystemPrompt { get; set; } = "Ты — Альтаир, голосовой ассистент на компьютере пользователя. Отвечай кратко, по-русски, обращайся «{address}».";
    public int ChatHistoryLimit { get; set; } = 20;
    public bool AiFallbackForUnknown { get; set; } = true; // нераспознанная команда → ИИ
    public int DailyRequestSoftLimit { get; set; } = 0; // 0 — без ограничений (свой ключ)

    // Агент
    public ConfirmMode AgentConfirm { get; set; } = ConfirmMode.Important;
    public int AgentMaxSteps { get; set; } = 25;
    public bool AgentScreenVision { get; set; } = true;

    // Telegram (PC Control)
    public string TelegramToken { get; set; } = "";
    public long TelegramOwnerId { get; set; }
    public bool TelegramEnabled { get; set; }

    // Умный дом
    public string HomeAssistantUrl { get; set; } = "http://homeassistant.local:8123";
    public string HomeAssistantToken { get; set; } = "";
    public string YandexIotToken { get; set; } = "";

    // Стрим
    public string ObsUrl { get; set; } = "ws://127.0.0.1:4455";
    public string ObsPassword { get; set; } = "";
    public string TwitchChannel { get; set; } = "";
    public string TwitchOAuth { get; set; } = "";
    public string TwitchNick { get; set; } = "";

    // Подключения и второй ПК
    public List<McpServerConfig> McpServers { get; set; } = new();
    public List<RemotePcConfig> RemotePcs { get; set; } = new();
    public bool RemoteServerEnabled { get; set; }
    public int RemoteServerPort { get; set; } = 47821;
    public string RemoteSharedKey { get; set; } = "";

    // Плагины
    public Dictionary<string, bool> Plugins { get; set; } = new();
    public Dictionary<string, string> Hotkeys { get; set; } = new()
    {
        ["dictation"] = "RightCtrl×2",
        ["textAssist"] = "Ctrl+Alt+W",
        ["clipboard"] = "Win+Alt+V",
        ["island"] = "Ctrl+Alt+I",
        ["converter"] = "Ctrl+Alt+X",
        ["screenshot"] = "Ctrl+Alt+A",
        ["listen"] = "F9",
    };

    // Оформление
    public string Theme { get; set; } = "Midnight";
    public string Accent { get; set; } = "#4F8CFF";
    public string? BackgroundFile { get; set; }
    public string BackgroundEffect { get; set; } = "none"; // none|stars|waves|snow|grid
    public double CardBlur { get; set; } = 0;
    public string FontFamily { get; set; } = "Segoe UI Variable";

    // Главная
    public List<WidgetPlacement> Widgets { get; set; } = new()
    {
        new() { Kind = "clock", Desk = 0, X = 0, Y = 0, W = 2, H = 1 },
        new() { Kind = "system", Desk = 0, X = 2, Y = 0, W = 2, H = 1 },
        new() { Kind = "weather", Desk = 0, X = 0, Y = 1, W = 2, H = 1 },
        new() { Kind = "lastCommands", Desk = 0, X = 2, Y = 1, W = 2, H = 2 },
        new() { Kind = "notes", Desk = 0, X = 0, Y = 2, W = 2, H = 1 },
    };
    public int Desks { get; set; } = 1;

    public bool StartWithWindows { get; set; } = true;
    public bool ElevatedAutostart { get; set; }
    public string UpdateUrl { get; set; } = "";
    public double WeatherLat { get; set; } = 43.2389;
    public double WeatherLon { get; set; } = 76.8897;
    public string WeatherCity { get; set; } = "Алматы";
    public Dictionary<string, string> Snippets { get; set; } = new() { [";дата"] = "{date}", [";почта"] = "example@mail.com" };
    public List<string> DocFolders { get; set; } = new();
    public string? AwayLockDevice { get; set; }
    public int BreakWorkMinutes { get; set; } = 50;
    public string Notes { get; set; } = "";
    public bool GameMode { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.Now;

    public AiProviderConfig? Provider(string? id = null) =>
        Providers.FirstOrDefault(p => p.Id == (id ?? ActiveProvider));

    public bool PluginOn(string id, bool def = false) => Plugins.TryGetValue(id, out var v) ? v : def;
}

public sealed class WidgetPlacement
{
    public string Kind { get; set; } = "clock";
    public int Desk { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; } = 1;
    public int H { get; set; } = 1;
    public Dictionary<string, string> Options { get; set; } = new();
}

/// <summary>Хранилище команд: по файлу на коллекцию в %AppData%/AltairAssistant/commands.</summary>
public sealed class CommandStore
{
    private readonly string _dir;
    public List<CommandCollection> Collections { get; } = new();
    public event Action? Changed;

    public CommandStore(string? dir = null) => _dir = dir ?? AppPaths.Commands;

    public void Load()
    {
        Collections.Clear();
        Directory.CreateDirectory(_dir);
        foreach (var f in Directory.EnumerateFiles(_dir, "*.json").OrderBy(f => f))
        {
            try
            {
                var c = JsonSerializer.Deserialize<CommandCollection>(File.ReadAllText(f), Json.Options);
                if (c != null) Collections.Add(c);
            }
            catch (JsonException) { /* пропускаем битый файл */ }
        }
        Changed?.Invoke();
    }

    public void Save(CommandCollection c)
    {
        Json.Save(Path.Combine(_dir, c.Id + ".json"), c);
        Changed?.Invoke();
    }

    public void SaveAll() { foreach (var c in Collections) Json.Save(Path.Combine(_dir, c.Id + ".json"), c); Changed?.Invoke(); }

    public void Delete(CommandCollection c)
    {
        Collections.Remove(c);
        var p = Path.Combine(_dir, c.Id + ".json");
        if (File.Exists(p)) File.Delete(p);
        Changed?.Invoke();
    }

    /// <summary>Импорт пака/экспорта. Конфликт Id → новый Id, чтобы не затирать.</summary>
    public CommandCollection Import(string json)
    {
        var c = JsonSerializer.Deserialize<CommandCollection>(json, Json.Options) ?? throw new InvalidDataException("Пустой файл");
        if (Collections.Any(x => x.Id == c.Id)) c.Id = Guid.NewGuid().ToString("N");
        Collections.Add(c);
        Save(c);
        return c;
    }

    public string Export(CommandCollection c) => JsonSerializer.Serialize(c, Json.Options);

    public IEnumerable<VoiceCommand> All() => Collections.SelectMany(c => c.Folders).SelectMany(f => f.Commands);

    public IEnumerable<(VoiceCommand, Trigger)> AllTriggers() =>
        Collections.Where(c => c.Enabled)
            .SelectMany(c => c.Folders.Where(f => f.Enabled))
            .SelectMany(f => f.Commands.Where(x => x.Enabled))
            .SelectMany(cmd => cmd.Triggers.Select(t => (cmd, t)));

    public VoiceCommand? FindByName(string name) =>
        All().FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? All().FirstOrDefault(c => c.Phrases.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase)));
}
