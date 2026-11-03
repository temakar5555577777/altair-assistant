using System.Globalization;
using Altair.Core.Models;

namespace Altair.Core.Engine;

public sealed class StopScenarioException : Exception { }

/// <summary>Контекст выполнения сценария.</summary>
public sealed class RunContext
{
    public required VariableStore Vars { get; init; }
    public CancellationToken Token { get; init; }
    public VoiceCommand? Command { get; init; }
    public string Utterance { get; init; } = "";
    public string Source { get; init; } = "voice"; // voice | hotkey | schedule | event | telegram | agent
    public Action<string>? Log { get; init; }
    public int Depth { get; init; }

    /// <summary>Аргумент с подстановкой переменных.</summary>
    public string Arg(ActionStep s, string key, string fallback = "") => Vars.Expand(s.Arg(key, fallback));

    public int ArgInt(ActionStep s, string key, int fallback)
    {
        var v = Arg(s, key, fallback.ToString(CultureInfo.InvariantCulture));
        return double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)Math.Round(d) : fallback;
    }

    public bool ArgBool(ActionStep s, string key, bool fallback = false)
    {
        var v = Arg(s, key, fallback ? "true" : "false").Trim().ToLowerInvariant();
        return v is "true" or "1" or "да" or "yes" or "on";
    }
}

public delegate Task ActionHandler(ActionStep step, RunContext ctx);

/// <summary>Реестр обработчиков действий. Платформенный слой регистрирует реализации.</summary>
public sealed class ActionRegistry
{
    private readonly Dictionary<string, ActionHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);
    public void Register(string type, ActionHandler handler) => _handlers[type] = handler;
    public bool TryGet(string type, out ActionHandler handler) => _handlers.TryGetValue(type, out handler!);
    public IEnumerable<string> Registered => _handlers.Keys;
}

public sealed class ActionRunner
{
    private readonly ActionRegistry _registry;
    private readonly Func<string, VoiceCommand?> _findCommand;
    public Func<string, RunContext, Task<string?>>? AskUser { get; set; }

    public ActionRunner(ActionRegistry registry, Func<string, VoiceCommand?> findCommand)
    {
        _registry = registry;
        _findCommand = findCommand;
    }

    public async Task RunAsync(IEnumerable<ActionStep> steps, RunContext ctx)
    {
        try { await RunBlock(steps, ctx); }
        catch (StopScenarioException) { ctx.Log?.Invoke("Сценарий остановлен"); }
    }

    private async Task RunBlock(IEnumerable<ActionStep> steps, RunContext ctx)
    {
        foreach (var step in steps)
        {
            ctx.Token.ThrowIfCancellationRequested();
            try
            {
                await RunStep(step, ctx);
            }
            catch (StopScenarioException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (step.ContinueOnError)
            {
                ctx.Log?.Invoke($"Ошибка в «{step.Type}» (пропущено): {ex.Message}");
                ctx.Vars.Set("error", ex.Message);
            }
        }
    }

    private async Task RunStep(ActionStep step, RunContext ctx)
    {
        switch (step.Type)
        {
            case ActionTypes.If:
                if (Expr.IsTrue(step.Condition ?? "false", ctx.Vars)) await RunBlock(step.Then ?? new(), ctx);
                else await RunBlock(step.Else ?? new(), ctx);
                return;

            case ActionTypes.Repeat:
            {
                int n = Math.Clamp(ctx.ArgInt(step, "count", 1), 0, 10_000);
                for (int i = 0; i < n; i++)
                {
                    ctx.Vars.Set("i", (i + 1).ToString(CultureInfo.InvariantCulture));
                    await RunBlock(step.Then ?? new(), ctx);
                }
                return;
            }

            case ActionTypes.While:
            {
                int max = Math.Clamp(ctx.ArgInt(step, "max", 100), 1, 100_000);
                int i = 0;
                while (i < max && Expr.IsTrue(step.Condition ?? "false", ctx.Vars))
                {
                    ctx.Vars.Set("i", (++i).ToString(CultureInfo.InvariantCulture));
                    await RunBlock(step.Then ?? new(), ctx);
                }
                return;
            }

            case ActionTypes.SetVar:
            {
                var name = step.Arg("name");
                var raw = step.Arg("value");
                string value;
                try { value = Expr.ToText(Expr.Eval(raw, ctx.Vars)); }
                catch { value = ctx.Vars.Expand(raw); }
                ctx.Vars.Set(name, value);
                return;
            }

            case ActionTypes.Wait:
                await Task.Delay(Math.Clamp(ctx.ArgInt(step, "ms", 500), 0, 3_600_000), ctx.Token);
                return;

            case ActionTypes.Stop:
                throw new StopScenarioException();

            case ActionTypes.RunCommand:
            {
                if (ctx.Depth > 8) throw new InvalidOperationException("Слишком глубокая вложенность команд");
                var cmd = _findCommand(ctx.Arg(step, "command"));
                if (cmd is null) throw new InvalidOperationException($"Команда «{step.Arg("command")}» не найдена");
                var child = new RunContext { Vars = ctx.Vars, Token = ctx.Token, Command = cmd, Utterance = ctx.Utterance, Source = ctx.Source, Log = ctx.Log, Depth = ctx.Depth + 1 };
                await RunBlock(cmd.Actions, child);
                return;
            }

            case ActionTypes.AskUser:
            {
                if (AskUser is null) throw new InvalidOperationException("Голосовой вопрос недоступен");
                var answer = await AskUser(ctx.Arg(step, "question"), ctx) ?? "";
                ctx.Vars.Set(step.Arg("var", "answer"), answer);
                return;
            }
        }

        if (!_registry.TryGet(step.Type, out var handler))
            throw new NotSupportedException($"Действие «{step.Type}» не поддерживается на этом устройстве");

        ctx.Log?.Invoke($"→ {ActionTypes.Find(step.Type)?.Title ?? step.Type}");
        await handler(step, ctx);
    }
}
