using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Altair.Core.AI;
using Altair.Core.Engine;
using Altair.Core.Models;
using Altair.Core.Storage;

namespace Altair.Core.Agent;

public enum Risk { Safe, Important, Critical }

/// <summary>Что видит агент: экран (картинка + распознанный текст + элементы UI Automation), окна, страница браузера.</summary>
public sealed record Observation(string Text, ImagePart? Screenshot = null);

public interface IAgentEnvironment
{
    Task<Observation> ObserveScreenAsync(bool withImage, CancellationToken ct);
    Task<string> ListWindowsAsync(CancellationToken ct);
    Task<string?> ReadBrowserPageAsync(CancellationToken ct);
}

public sealed record AgentStep(int Index, string Thought, string Tool, Dictionary<string, string> Args, Risk Risk)
{
    public string? Result { get; set; }
    public bool Ok { get; set; }
    public bool Skipped { get; set; }
}

public sealed class ConfirmRequest
{
    public required AgentStep Step { get; init; }
    public required string Description { get; init; }
    /// <summary>Оплата/пароли: голосом можно только отклонить, разрешить — только кнопкой.</summary>
    public bool VoiceApproveForbidden { get; init; }
}

public sealed class AgentRunner
{
    private readonly ChatService _chat;
    private readonly ActionRunner _runner;
    private readonly IAgentEnvironment _env;
    private readonly Func<AppSettings> _settings;
    private readonly Queue<string> _interjections = new();
    private CancellationTokenSource? _cts;
    private volatile bool _skipCurrent;

    public event Action<IReadOnlyList<string>>? PlanUpdated;
    public event Action<AgentStep, int, int>? StepStarted;      // шаг, номер, всего по плану
    public event Action<AgentStep>? StepFinished;
    public event Action<string>? Finished;
    public event Action<string>? Log;

    /// <summary>Запрос подтверждения у пользователя (плашка «Разрешить / Отклонить»).</summary>
    public Func<ConfirmRequest, CancellationToken, Task<bool>>? Confirm { get; set; }

    public bool Running => _cts != null;
    public List<AgentStep> Steps { get; } = new();
    public List<string> Plan { get; private set; } = new();

    // Инструменты агента = действия редактора (чтобы удачный сценарий сохранялся как обычная команда) + наблюдение
    private static readonly string[] ToolTypes =
    {
        ActionTypes.OpenApp, ActionTypes.OpenUrl, ActionTypes.OpenFile, ActionTypes.OpenFolder, ActionTypes.CloseApp, ActionTypes.WebSearch,
        ActionTypes.KeyPress, ActionTypes.KeyCombo, ActionTypes.TypeText,
        ActionTypes.MouseMove, ActionTypes.MouseClick, ActionTypes.MouseScroll,
        ActionTypes.ClickText, ActionTypes.ClickElement, ActionTypes.WaitForText,
        ActionTypes.WindowFocus, ActionTypes.WindowMaximize, ActionTypes.WindowMinimize, ActionTypes.WindowClose, ActionTypes.WindowSnap,
        ActionTypes.VolumeSet, ActionTypes.Mute, ActionTypes.MediaPlayPause,
        ActionTypes.BrowserNewTab, ActionTypes.BrowserClick, ActionTypes.BrowserFill,
        ActionTypes.ClipboardSet, ActionTypes.Paste,
        ActionTypes.FileWrite, ActionTypes.FileCopy, ActionTypes.FileMove, ActionTypes.FileDelete, ActionTypes.FindFile,
        ActionTypes.RunCmd, ActionTypes.RunPowerShell, ActionTypes.RunPython,
        ActionTypes.HomeDevice, ActionTypes.HomeScene, ActionTypes.RemoteRun, ActionTypes.McpTool, ActionTypes.HttpRequest,
        ActionTypes.Say, ActionTypes.Wait, ActionTypes.Lock,
    };

    private static readonly string[] ImportantWords =
        { "отправ", "удал", "опублик", "установ", "оплат", "купить", "заказ", "перевод", "send", "delete", "publish", "install", "pay", "buy", "submit" };
    private static readonly string[] CriticalWords =
        { "оплат", "pay", "карт", "card", "пароль", "password", "cvv", "код из смс", "otp" };

    public AgentRunner(ChatService chat, ActionRunner runner, IAgentEnvironment env, Func<AppSettings> settings)
    {
        _chat = chat; _runner = runner; _env = env; _settings = settings;
    }

    public void Stop() => _cts?.Cancel();
    public void SkipCurrentStep() => _skipCurrent = true;
    public void AddInstruction(string text) { lock (_interjections) _interjections.Enqueue(text); }

    public static Risk Classify(string tool, IReadOnlyDictionary<string, string> args)
    {
        var blob = (tool + " " + string.Join(' ', args.Values)).ToLowerInvariant();
        if (CriticalWords.Any(blob.Contains)) return Risk.Critical;
        if (tool is ActionTypes.FileDelete or ActionTypes.RunCmd or ActionTypes.RunPowerShell or ActionTypes.RunPython
            or ActionTypes.RemoteRun or ActionTypes.McpTool or ActionTypes.Lock) return Risk.Important;
        if (tool == ActionTypes.HttpRequest && !args.GetValueOrDefault("method", "GET").Equals("GET", StringComparison.OrdinalIgnoreCase)) return Risk.Important;
        if (ImportantWords.Any(blob.Contains)) return Risk.Important;
        return Risk.Safe;
    }

    public static bool NeedsConfirm(ConfirmMode mode, Risk risk) => mode switch
    {
        ConfirmMode.All => true,
        ConfirmMode.Important => risk >= Risk.Important,
        ConfirmMode.Auto => risk >= Risk.Critical,
        _ => risk >= Risk.Critical, // «Полный доступ» — всё равно спрашиваем про оплату и пароли
    };

    public static string ToolCatalogText()
    {
        var sb = new StringBuilder();
        foreach (var t in ToolTypes)
        {
            var info = ActionTypes.Find(t);
            if (info == null) continue;
            sb.Append("- ").Append(t).Append(": ").Append(info.Title);
            if (info.Args.Length > 0)
                sb.Append(" (").Append(string.Join(", ", info.Args.Select(a => $"{a.Key}: {a.Title}"))).Append(')');
            sb.AppendLine();
        }
        sb.AppendLine("- observe.screen: посмотреть на экран (скриншот, текст, элементы окна)");
        sb.AppendLine("- observe.windows: список открытых окон");
        sb.AppendLine("- observe.page: элементы текущей страницы браузера (нужно расширение)");
        return sb.ToString();
    }

    private string SystemPrompt(AppSettings s) => $$"""
        Ты — ИИ-агент, управляющий Windows-компьютером пользователя. Выполняешь задание по шагам.
        На каждом ходе отвечай ТОЛЬКО одним JSON-объектом без пояснений:
        {"thought":"кратко, что видишь и зачем шаг","plan":["шаг 1","шаг 2",...],"tool":"<инструмент>","args":{"ключ":"значение"},"done":false}
        Когда задание выполнено (или невозможно): {"thought":"...","done":true,"answer":"итог для пользователя, 1-2 фразы"}.
        Правила:
        - Сначала составь план (поле plan), обновляй его, если ситуация меняется.
        - Прежде чем нажимать — посмотри (observe.screen / observe.page). Нажимай по видимой подписи (screen.clickelement, screen.clicktext, browser.click), координаты мыши — только если иначе нельзя.
        - Никогда не вводи пароли, данные карт и одноразовые коды — попроси пользователя сделать это самому.
        - Не больше {{s.AgentMaxSteps}} шагов. Не повторяй один и тот же неудачный шаг более 2 раз.
        - Все значения args — строки.
        Инструменты:
        {{ToolCatalogText()}}
        """;

    public async Task<string> RunAsync(string task, CancellationToken external = default)
    {
        if (_cts != null) throw new InvalidOperationException("Агент уже выполняет задание");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        var ct = _cts.Token;
        Steps.Clear(); Plan = new();
        var s = _settings();
        var transcript = new List<ChatMessage> { ChatMessage.System(SystemPrompt(s)), ChatMessage.User("Задание: " + task) };
        var vars = new VariableStore();
        int failuresInRow = 0;

        try
        {
            for (int i = 1; i <= s.AgentMaxSteps; i++)
            {
                ct.ThrowIfCancellationRequested();
                lock (_interjections)
                    while (_interjections.Count > 0)
                        transcript.Add(ChatMessage.User("Уточнение от пользователя: " + _interjections.Dequeue()));

                var reply = await _chat.Complete(Compact(transcript), new ChatOptions(Temperature: 0.2, MaxTokens: 900, Json: true), ct);
                transcript.Add(ChatMessage.Assistant(reply.Text));

                JsonObject obj;
                try { obj = ParseJson(reply.Text); }
                catch
                {
                    transcript.Add(ChatMessage.User("Ответ не является корректным JSON. Повтори строго в формате."));
                    continue;
                }

                if (obj["plan"] is JsonArray planArr && planArr.Count > 0)
                {
                    Plan = planArr.Select(p => p?.ToString() ?? "").Where(p => p.Length > 0).ToList();
                    PlanUpdated?.Invoke(Plan);
                }

                if (string.Equals(obj["done"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase))
                {
                    var answer = obj["answer"]?.ToString() ?? "Готово.";
                    Finished?.Invoke(answer);
                    return answer;
                }

                var tool = obj["tool"]?.ToString() ?? "";
                var args = new Dictionary<string, string>();
                if (obj["args"] is JsonObject a)
                    foreach (var kv in a) args[kv.Key] = kv.Value is JsonValue v ? v.ToString() : kv.Value?.ToJsonString() ?? "";

                var step = new AgentStep(i, obj["thought"]?.ToString() ?? "", tool, args, Classify(tool, args));
                Steps.Add(step);
                StepStarted?.Invoke(step, i, Math.Max(Plan.Count, i));

                string observation;
                ImagePart? image = null;

                if (NeedsConfirm(s.AgentConfirm, step.Risk))
                {
                    bool ok = Confirm != null && await Confirm(new ConfirmRequest
                    {
                        Step = step,
                        Description = Describe(step),
                        VoiceApproveForbidden = step.Risk == Risk.Critical
                    }, ct);
                    if (!ok)
                    {
                        step.Result = "Пользователь отклонил шаг"; step.Skipped = true;
                        StepFinished?.Invoke(step);
                        transcript.Add(ChatMessage.User("Пользователь ОТКЛОНИЛ этот шаг. Предложи другой путь или заверши."));
                        continue;
                    }
                }

                if (_skipCurrent)
                {
                    _skipCurrent = false; step.Skipped = true; step.Result = "Пропущено пользователем";
                    StepFinished?.Invoke(step);
                    transcript.Add(ChatMessage.User("Пользователь пропустил этот шаг."));
                    continue;
                }

                try
                {
                    (observation, image) = await Execute(step, vars, ct);
                    step.Ok = true; failuresInRow = 0;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    observation = "ОШИБКА: " + ex.Message;
                    step.Ok = false;
                    if (++failuresInRow >= 4) throw new AiException("Агент остановлен: 4 ошибки подряд. Последняя: " + ex.Message);
                }

                step.Result = observation.Length > 4000 ? observation[..4000] + "…" : observation;
                StepFinished?.Invoke(step);
                var msg = ChatMessage.User("Результат шага: " + step.Result);
                if (image != null) msg.Images.Add(image);
                transcript.Add(msg);
            }

            var limit = $"Достигнут предел шагов ({s.AgentMaxSteps}). Сделано: {Steps.Count(x => x.Ok)} шагов.";
            Finished?.Invoke(limit);
            return limit;
        }
        catch (OperationCanceledException)
        {
            Finished?.Invoke("Агент остановлен.");
            return "Агент остановлен.";
        }
        finally
        {
            _cts.Dispose(); _cts = null;
        }
    }

    private async Task<(string, ImagePart?)> Execute(AgentStep step, VariableStore vars, CancellationToken ct)
    {
        switch (step.Tool)
        {
            case "observe.screen":
            {
                var o = await _env.ObserveScreenAsync(_settings().AgentScreenVision, ct);
                return (o.Text, o.Screenshot);
            }
            case "observe.windows": return (await _env.ListWindowsAsync(ct), null);
            case "observe.page": return (await _env.ReadBrowserPageAsync(ct) ?? "Расширение браузера не подключено.", null);
        }

        if (!ToolTypes.Contains(step.Tool)) throw new InvalidOperationException($"Неизвестный инструмент «{step.Tool}»");
        var action = new ActionStep { Type = step.Tool, Args = new Dictionary<string, string>(step.Args) };
        // Результат в переменную — чтобы вернуть агенту вывод команд, чтение файлов и т.п.
        if (!action.Args.ContainsKey("var")) action.Args["var"] = "_out";
        vars.Set("_out", "");
        await _runner.RunAsync(new[] { action }, new RunContext { Vars = vars, Token = ct, Source = "agent", Log = l => Log?.Invoke(l) });
        var output = vars.Get("_out");
        return (string.IsNullOrEmpty(output) ? "Выполнено." : "Выполнено. Вывод: " + output, null);
    }

    /// <summary>Держим контекст компактным: старые скриншоты выбрасываем, оставляем последний.</summary>
    private static List<ChatMessage> Compact(List<ChatMessage> t)
    {
        var lastImg = t.FindLastIndex(m => m.Images.Count > 0);
        var list = new List<ChatMessage>(t.Count);
        for (int i = 0; i < t.Count; i++)
        {
            var m = t[i];
            if (m.Images.Count > 0 && i != lastImg) list.Add(new ChatMessage(m.Role, m.Text + " [скриншот опущен]"));
            else list.Add(m);
        }
        // Не больше 40 сообщений: system + задание + хвост
        if (list.Count > 40) list = list.Take(2).Concat(list.Skip(list.Count - 38)).ToList();
        return list;
    }

    public static JsonObject ParseJson(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("```")) t = t.Trim('`').Replace("json", "", StringComparison.OrdinalIgnoreCase).Trim();
        int a = t.IndexOf('{'), b = t.LastIndexOf('}');
        if (a < 0 || b <= a) throw new JsonException("Нет JSON-объекта");
        return JsonNode.Parse(t[a..(b + 1)])!.AsObject();
    }

    public static string Describe(AgentStep s)
    {
        var title = ActionTypes.Find(s.Tool)?.Title ?? s.Tool;
        var args = string.Join(", ", s.Args.Where(kv => kv.Key != "var").Select(kv => $"{kv.Key}: {Trim(kv.Value)}"));
        return args.Length > 0 ? $"{title} — {args}" : title;
        static string Trim(string v) => v.Length > 80 ? v[..80] + "…" : v;
    }

    /// <summary>Удачный прогон → обычная команда (запускается по фразе без нейросети).</summary>
    public VoiceCommand ToCommand(string name, string phrase)
    {
        var cmd = new VoiceCommand { Name = name, Phrases = { phrase }, Description = "Создано ИИ-агентом" };
        foreach (var s in Steps.Where(s => s.Ok && !s.Skipped && !s.Tool.StartsWith("observe.")))
        {
            var args = s.Args.Where(kv => kv.Key != "var").ToDictionary(kv => kv.Key, kv => kv.Value);
            cmd.Actions.Add(new ActionStep { Type = s.Tool, Args = args, Comment = s.Thought });
            cmd.Actions.Add(ActionStep.Of(ActionTypes.Wait, ("ms", "400")));
        }
        return cmd;
    }
}
