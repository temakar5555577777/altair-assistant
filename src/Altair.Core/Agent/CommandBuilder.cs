using System.Text.Json;
using Altair.Core.AI;
using Altair.Core.Models;
using Altair.Core.Storage;

namespace Altair.Core.Agent;

/// <summary>Создание команды ИИ по описанию: «сделай команду, которая открывает YouTube и ставит громкость 30».</summary>
public sealed class CommandBuilder
{
    private readonly ChatService _chat;
    public CommandBuilder(ChatService chat) => _chat = chat;

    public static string Prompt()
    {
        var actions = string.Join("\n", ActionTypes.Catalog.Select(a =>
            $"{a.Type} — {a.Title}" + (a.Args.Length > 0 ? " [" + string.Join(", ", a.Args.Select(x => x.Key)) + "]" : "")));
        return $$$"""
            Ты генерируешь голосовую команду для ассистента Windows. Верни ТОЛЬКО JSON:
            {"name":"…","phrases":["фраза 1","фраза 2","фраза 3"],"actions":[{"type":"…","args":{"…":"…"}}],
             "responses":{"items":[{"text":"…","weight":1}]}}
            Фразы — естественные русские варианты, 3–6 шт., слоты в фигурных скобках: {число}, {text}.
            Ответы — 2–4 коротких варианта подтверждения в разном стиле.
            Условие: {"type":"flow.if","condition":"{hour} >= 22","then":[…],"else":[…]}.
            Повтор: {"type":"flow.repeat","args":{"count":"3"},"then":[…]}.
            Доступные действия:
            {{{actions}}}
            """;
    }

    public async Task<VoiceCommand> BuildAsync(string description, CancellationToken ct = default)
    {
        var r = await _chat.OneShot(Prompt(), description, new ChatOptions(Temperature: 0.3, MaxTokens: 1500, Json: true), ct: ct);
        var obj = AgentRunner.ParseJson(r.Text);
        var cmd = JsonSerializer.Deserialize<VoiceCommand>(obj.ToJsonString(), Json.Options)
                  ?? throw new AiException("ИИ вернул пустую команду");
        cmd.Id = Guid.NewGuid().ToString("N");
        cmd.Description = "Создано ИИ: " + description;
        Validate(cmd.Actions);
        if (cmd.Phrases.Count == 0) throw new AiException("ИИ не придумал фразы для команды");
        return cmd;
    }

    private static void Validate(IEnumerable<ActionStep>? steps)
    {
        if (steps == null) return;
        foreach (var s in steps)
        {
            if (ActionTypes.Find(s.Type) == null) throw new AiException($"ИИ предложил несуществующее действие «{s.Type}»");
            Validate(s.Then); Validate(s.Else);
        }
    }
}
