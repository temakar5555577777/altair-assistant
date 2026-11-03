using System.Text.Json;
using Altair.Core.Storage;

namespace Altair.Core.AI;

/// <summary>Прозрачный учёт запросов и токенов (жалоба пользователей образца: «лимиты кончаются незаметно за 20 минут»).</summary>
public sealed class UsageMeter
{
    public sealed class Day
    {
        public string Date { get; set; } = "";
        public Dictionary<string, int> Requests { get; set; } = new();
        public Dictionary<string, long> TokensIn { get; set; } = new();
        public Dictionary<string, long> TokensOut { get; set; } = new();
    }

    private readonly string _path;
    private readonly object _gate = new();
    public List<Day> History { get; private set; } = new();
    public event Action? Changed;

    public UsageMeter(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.Data, "usage.json");
        try { if (File.Exists(_path)) History = JsonSerializer.Deserialize<List<Day>>(File.ReadAllText(_path), Json.Options) ?? new(); }
        catch { History = new(); }
    }

    public Day Today
    {
        get
        {
            var key = DateTime.Now.ToString("yyyy-MM-dd");
            var d = History.FirstOrDefault(x => x.Date == key);
            if (d == null) { d = new Day { Date = key }; History.Add(d); if (History.Count > 90) History.RemoveAt(0); }
            return d;
        }
    }

    public void Record(ChatResult r)
    {
        lock (_gate)
        {
            var d = Today;
            d.Requests[r.Provider] = d.Requests.GetValueOrDefault(r.Provider) + 1;
            d.TokensIn[r.Provider] = d.TokensIn.GetValueOrDefault(r.Provider) + r.InputTokens;
            d.TokensOut[r.Provider] = d.TokensOut.GetValueOrDefault(r.Provider) + r.OutputTokens;
            try { Json.Save(_path, History); } catch { /* диск недоступен — учёт в памяти */ }
        }
        Changed?.Invoke();
    }

    public int RequestsToday() => Today.Requests.Values.Sum();
    public long TokensToday() => Today.TokensIn.Values.Sum() + Today.TokensOut.Values.Sum();
}

/// <summary>Диалог с ИИ: история, системный промпт, переключение на резервного провайдера при сбое.</summary>
public sealed class ChatService
{
    private readonly Func<AppSettings> _settings;
    private readonly UsageMeter _usage;
    private readonly Dictionary<string, IChatProvider> _cache = new();
    private readonly List<ChatMessage> _history = new();
    private readonly object _gate = new();

    public IReadOnlyList<ChatMessage> History { get { lock (_gate) return _history.ToList(); } }
    public Func<AiProviderConfig, IChatProvider> Factory { get; set; } = c => ProviderFactory.Create(c);

    public ChatService(Func<AppSettings> settings, UsageMeter usage)
    {
        _settings = settings;
        _usage = usage;
    }

    public void InvalidateProviders() { lock (_gate) _cache.Clear(); }
    public void ClearHistory() { lock (_gate) _history.Clear(); }

    public IEnumerable<AiProviderConfig> Chain()
    {
        var s = _settings();
        var active = s.Provider();
        if (active is { Enabled: true } && IsConfigured(active)) yield return active;
        foreach (var p in s.Providers.Where(p => p.Enabled && p.Id != s.ActiveProvider && IsConfigured(p))) yield return p;
    }

    public static bool IsConfigured(AiProviderConfig p) =>
        !string.IsNullOrWhiteSpace(p.ApiKey) || p.BaseUrl.Contains("localhost") || p.BaseUrl.Contains("127.0.0.1");

    public IChatProvider Get(AiProviderConfig cfg)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue(cfg.Id, out var p)) _cache[cfg.Id] = p = Factory(cfg);
            return p;
        }
    }

    public string SystemPrompt()
    {
        var s = _settings();
        return s.SystemPrompt.Replace("{address}", s.AddressAs) +
               $"\nСейчас {DateTime.Now:dd.MM.yyyy HH:mm}. Ответ будет озвучен — без markdown, без списков длиннее 5 пунктов.";
    }

    /// <summary>Один запрос без истории (служебный: перевод, исправление текста, генерация команд).</summary>
    public async Task<ChatResult> OneShot(string system, string user, ChatOptions? o = null, IEnumerable<ImagePart>? images = null, CancellationToken ct = default)
    {
        var msg = ChatMessage.User(user);
        if (images != null) msg.Images.AddRange(images);
        return await Complete(new List<ChatMessage> { ChatMessage.System(system), msg }, o ?? new ChatOptions(), ct);
    }

    /// <summary>Сообщение в диалоге (с историей).</summary>
    public async Task<string> Ask(string text, CancellationToken ct = default, IEnumerable<ImagePart>? images = null)
    {
        var user = ChatMessage.User(text);
        if (images != null) user.Images.AddRange(images);
        List<ChatMessage> msgs;
        lock (_gate)
        {
            _history.Add(user);
            int limit = Math.Max(2, _settings().ChatHistoryLimit);
            while (_history.Count > limit) _history.RemoveAt(0);
            msgs = new List<ChatMessage> { ChatMessage.System(SystemPrompt()) };
            msgs.AddRange(_history);
        }
        var r = await Complete(msgs, new ChatOptions(), ct);
        lock (_gate) _history.Add(ChatMessage.Assistant(r.Text));
        return r.Text;
    }

    public async Task<ChatResult> Complete(IReadOnlyList<ChatMessage> messages, ChatOptions o, CancellationToken ct)
    {
        var s = _settings();
        if (s.DailyRequestSoftLimit > 0 && _usage.RequestsToday() >= s.DailyRequestSoftLimit)
            throw new AiException($"Достигнут ваш дневной лимит запросов ({s.DailyRequestSoftLimit}). Его можно изменить в настройках.");

        var chain = Chain().ToList();
        if (chain.Count == 0)
            throw new AiException("ИИ не настроен: добавьте ключ любого провайдера или включите локальную модель (Ollama/LM Studio) в «Нейросеть → Провайдеры».");

        bool needVision = messages.Any(m => m.Images.Count > 0);
        Exception? last = null;
        foreach (var cfg in chain)
        {
            var p = Get(cfg);
            if (needVision && !p.SupportsVision) continue;
            try
            {
                var r = await p.CompleteAsync(messages, o, ct);
                _usage.Record(r);
                return r;
            }
            catch (OperationCanceledException) { throw; }
            catch (AiException ex) when (ex.Status is 401 or 403 or 402 or 429 or >= 500 or null) { last = ex; }
            catch (HttpRequestException ex) { last = ex; }
        }
        throw last as AiException ?? new AiException(needVision
            ? "Нет провайдера с поддержкой изображений (нужна vision-модель: GPT-4o, Claude, Gemini)."
            : "Все провайдеры ИИ недоступны: " + last?.Message, null, last);
    }
}
