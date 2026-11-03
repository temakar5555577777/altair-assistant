using Altair.Core.AI;
using Altair.Core.Models;
using Altair.Core.Storage;

namespace Altair.Core.Engine;

public interface ISpeaker
{
    Task SayAsync(string text, CancellationToken ct = default);
    Task PlayAsync(string file, CancellationToken ct = default);
    void StopSpeaking();
}

public enum AssistantState { Idle, Listening, Thinking, Executing, Speaking }

public sealed record HistoryItem(DateTime At, string Heard, string Result, bool Ok);

/// <summary>
/// Мозг ассистента: слово активации → команда (цепочка) → действия → ответ из пула;
/// нераспознанное — в ИИ (если включено). Платформа подаёт сюда распознанный текст.
/// </summary>
public sealed class Assistant
{
    private readonly Func<AppSettings> _settings;
    private readonly CommandStore _store;
    private readonly ActionRunner _runner;
    private readonly ResponsePicker _picker = new();
    private readonly ChatService _chat;
    private readonly ISpeaker _speaker;
    private readonly VariableStore _globals;
    private DateTime _awakeUntil = DateTime.MinValue;
    private CancellationTokenSource? _current;
    private TaskCompletionSource<string>? _awaitAnswer;

    public CommandMatcher Matcher { get; }
    public AssistantState State { get; private set; } = AssistantState.Idle;
    public List<HistoryItem> History { get; } = new();
    public event Action<AssistantState>? StateChanged;
    public event Action<HistoryItem>? Handled;
    public event Action<string>? Log;
    public Func<string, Task<string>>? AgentHandler { get; set; }

    public Assistant(Func<AppSettings> settings, CommandStore store, ActionRunner runner, ChatService chat, ISpeaker speaker, VariableStore globals)
    {
        _settings = settings; _store = store; _runner = runner; _chat = chat; _speaker = speaker; _globals = globals;
        Matcher = new CommandMatcher(() => _store.Collections);
        _runner.AskUser = AskAsync;
    }

    public bool Awake => DateTime.Now < _awakeUntil;

    private void SetState(AssistantState s) { State = s; StateChanged?.Invoke(s); }

    /// <summary>Отделяет слово активации. Возвращает (было ли слово, остаток фразы).</summary>
    public static (bool Wake, string Rest) StripWakeWord(string normalized, IEnumerable<string> wakeWords)
    {
        var tokens = PhraseNormalizer.Tokens(normalized);
        if (tokens.Length == 0) return (false, "");
        foreach (var w in wakeWords)
        {
            var wt = PhraseNormalizer.Tokens(PhraseNormalizer.Normalize(w));
            if (wt.Length == 0 || tokens.Length < wt.Length) continue;
            // Слово активации — в начале (допускаем одно слово-приветствие перед ним: «эй альтаир»)
            for (int offset = 0; offset <= Math.Min(1, tokens.Length - wt.Length); offset++)
            {
                bool ok = true;
                for (int i = 0; i < wt.Length; i++)
                    if (FuzzyMatcher.TokenSimilarity(tokens[offset + i], wt[i]) < 0.8) { ok = false; break; }
                if (ok) return (true, string.Join(' ', tokens.Skip(offset + wt.Length)));
            }
        }
        return (false, normalized);
    }

    /// <summary>Точка входа для распознанной речи.</summary>
    public async Task OnSpeechAsync(string recognized, bool pushToTalk = false)
    {
        var s = _settings();
        var text = PhraseNormalizer.Normalize(recognized);
        if (text.Length == 0) return;

        // Ответ на вопрос/подтверждение имеет приоритет
        if (_awaitAnswer is { } pending) { _awaitAnswer = null; pending.TrySetResult(text); return; }

        // «Стоп» — всегда, без слова активации
        if (text is "стоп" or "хватит" or "замолчи" or "stop" or "отмена")
        {
            _speaker.StopSpeaking();
            _current?.Cancel();
            return;
        }

        string command;
        if (pushToTalk || s.ListenMode == ListenMode.Always) command = StripWakeWord(text, s.WakeWords).Rest;
        else
        {
            var (wake, rest) = StripWakeWord(text, s.WakeWords);
            if (wake)
            {
                if (rest.Length == 0 || !s.PrefixMode)
                {
                    _awakeUntil = DateTime.Now.AddSeconds(s.ListenWindowSec);
                    SetState(AssistantState.Listening);
                    await RespondAsync(DefaultAck(s));
                    return;
                }
                command = rest;
            }
            else if (Awake) command = text;
            else return; // фоновая речь без обращения
        }

        _awakeUntil = DateTime.MinValue;
        await HandleAsync(command, "voice");
    }

    private static string DefaultAck(AppSettings s) => new[] { "Да?", "Слушаю.", $"Да, {s.AddressAs}?", "Я здесь." }[Random.Shared.Next(4)];

    /// <summary>Выполнить текстовую команду (голос, Telegram, чат, второй ПК).</summary>
    public async Task<string> HandleAsync(string text, string source)
    {
        var chain = Matcher.MatchChain(text);
        if (chain.Count == 0) return await FallbackAsync(text, source);

        var results = new List<string>();
        foreach (var m in chain)
        {
            var r = await ExecuteAsync(m.Command, m.Slots, m.Utterance, source);
            results.Add(r);
        }
        return string.Join(" ", results.Where(r => r.Length > 0));
    }

    public async Task<string> ExecuteAsync(VoiceCommand cmd, IReadOnlyDictionary<string, string>? slots, string utterance, string source)
    {
        var s = _settings();
        if (cmd.RequireConfirmation && source != "telegram")
        {
            var ans = await AskAsync($"Выполнить «{cmd.Name}»?", null, cmd.ConfirmationTimeoutSec);
            if (ans is null || !IsYes(ans)) { await RespondAsync("Отменено."); Record(utterance, "Отменено", false); return "Отменено"; }
        }

        _current?.Cancel();
        _current = new CancellationTokenSource();
        var vars = _globals.Fork();
        vars.Set("text", utterance);
        vars.Set("address", s.AddressAs);
        if (slots != null) foreach (var kv in slots) { vars.Set(kv.Key, kv.Value); if (kv.Key is "text" or "любое") vars.Set("text", kv.Value); }

        SetState(AssistantState.Executing);
        try
        {
            await _runner.RunAsync(cmd.Actions, new RunContext { Vars = vars, Token = _current.Token, Command = cmd, Utterance = utterance, Source = source, Log = l => Log?.Invoke(l) });
            var resp = _picker.Pick(cmd.Responses);
            string said = "";
            if (resp != null)
            {
                if (!string.IsNullOrWhiteSpace(resp.AudioFile) && File.Exists(resp.AudioFile)) await _speaker.PlayAsync(resp.AudioFile);
                else if (!string.IsNullOrWhiteSpace(resp.Text)) { said = vars.Expand(resp.Text); await RespondAsync(said); }
            }
            Record(utterance, said.Length > 0 ? said : cmd.Name, true);
            return said.Length > 0 ? said : $"Выполнено: {cmd.Name}";
        }
        catch (OperationCanceledException) { Record(utterance, "Прервано", false); return "Прервано"; }
        catch (Exception ex)
        {
            Log?.Invoke($"Ошибка «{cmd.Name}»: {ex.Message}");
            await RespondAsync("Не получилось: " + ex.Message);
            Record(utterance, ex.Message, false);
            return "Ошибка: " + ex.Message;
        }
        finally { SetState(AssistantState.Idle); }
    }

    private async Task<string> FallbackAsync(string text, string source)
    {
        var s = _settings();
        if (!s.AiFallbackForUnknown)
        {
            await RespondAsync("Не знаю такой команды.");
            Record(text, "Команда не найдена", false);
            return "Команда не найдена";
        }

        // «Агент, …» / «сделай …» — задание агенту
        var lowered = text.ToLowerInvariant();
        if (AgentHandler != null && (lowered.StartsWith("агент") || lowered.StartsWith("сделай за меня") || lowered.StartsWith("выполни задание")))
        {
            var task = text[(text.IndexOf(' ') + 1)..];
            var r = await AgentHandler(task);
            await RespondAsync(r);
            Record(text, r, true);
            return r;
        }

        SetState(AssistantState.Thinking);
        try
        {
            var answer = await _chat.Ask(text);
            await RespondAsync(answer);
            Record(text, answer, true);
            return answer;
        }
        catch (Exception ex)
        {
            await RespondAsync(ex is AiException ? ex.Message : "Нейросеть недоступна.");
            Record(text, ex.Message, false);
            return ex.Message;
        }
        finally { SetState(AssistantState.Idle); }
    }

    private async Task RespondAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_settings().GameMode) { Log?.Invoke("🔇 " + text); return; }
        SetState(AssistantState.Speaking);
        try { await _speaker.SayAsync(text); }
        catch (Exception ex) { Log?.Invoke("Озвучка: " + ex.Message); }
        finally { if (State == AssistantState.Speaking) SetState(AssistantState.Idle); }
    }

    public static bool IsYes(string a)
    {
        var n = PhraseNormalizer.Normalize(a);
        return n is "да" or "давай" or "конечно" or "подтверждаю" or "выполняй" or "yes" or "ок" or "окей" or "ага" or "угу" || n.StartsWith("да ");
    }

    /// <summary>Голосовой вопрос: ждём следующую распознанную фразу (без слова активации).</summary>
    public async Task<string?> AskAsync(string question, RunContext? ctx, int timeoutSec = 30)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _awaitAnswer = tcs;
        await RespondAsync(question);
        SetState(AssistantState.Listening);
        try { return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSec), ctx?.Token ?? CancellationToken.None); }
        catch (TimeoutException) { return null; }
        finally { if (_awaitAnswer == tcs) _awaitAnswer = null; SetState(AssistantState.Idle); }
    }

    private Task<string?> AskAsync(string question, RunContext ctx) => AskAsync(question, ctx, 30);

    private void Record(string heard, string result, bool ok)
    {
        var h = new HistoryItem(DateTime.Now, heard, result, ok);
        lock (History) { History.Insert(0, h); if (History.Count > 200) History.RemoveAt(History.Count - 1); }
        Handled?.Invoke(h);
    }
}
