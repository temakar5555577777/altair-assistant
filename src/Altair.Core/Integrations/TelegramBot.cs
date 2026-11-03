using System.Text;
using System.Text.Json.Nodes;

namespace Altair.Core.Integrations;

public sealed record TgCommandItem(string Id, string Title, bool NeedsConfirm);

/// <summary>
/// PC Control: управление компьютером из Telegram через собственного бота пользователя.
/// Принимает сообщения ТОЛЬКО от владельца; команды с подтверждением запускаются кнопкой.
/// </summary>
public sealed class TelegramBot : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly string _token;
    private readonly long _owner;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _offset;

    public Func<IReadOnlyList<TgCommandItem>>? ListCommands { get; set; }
    public Func<string, Task<string>>? RunCommand { get; set; }
    public Func<string, Task<string>>? AskAi { get; set; }
    public Func<Task<byte[]?>>? Screenshot { get; set; }
    public Func<string, Task<string>>? Shell { get; set; }
    public event Action<string>? Log;

    public TelegramBot(string token, long ownerId, HttpClient? http = null)
    {
        _token = token; _owner = ownerId;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(70) };
    }

    private string Api(string method) => $"https://api.telegram.org/bot{_token}/{method}";

    public async Task<string> CheckAsync(CancellationToken ct = default)
    {
        var r = JsonNode.Parse(await _http.GetStringAsync(Api("getMe"), ct))!;
        if (r["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException("Неверный токен бота");
        return "@" + r["result"]?["username"];
    }

    public void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => Loop(_cts.Token));
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_loop != null) { try { await _loop; } catch { } }
        _cts?.Dispose();
    }

    private async Task Loop(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var url = Api("getUpdates") + $"?timeout=50&offset={_offset}&allowed_updates=%5B%22message%22,%22callback_query%22%5D";
                var json = JsonNode.Parse(await _http.GetStringAsync(url, ct))!;
                foreach (var u in json["result"]?.AsArray() ?? new JsonArray())
                {
                    _offset = u!["update_id"]!.GetValue<long>() + 1;
                    await Handle(u, ct);
                }
                delay = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log?.Invoke("Telegram: " + ex.Message);
                await Task.Delay(delay, ct).ContinueWith(_ => { });
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }
        }
    }

    private async Task Handle(JsonNode u, CancellationToken ct)
    {
        if (u["callback_query"] is JsonNode cb)
        {
            long from = cb["from"]?["id"]?.GetValue<long>() ?? 0;
            long chat = cb["message"]?["chat"]?["id"]?.GetValue<long>() ?? from;
            await Post("answerCallbackQuery", new JsonObject { ["callback_query_id"] = cb["id"]!.ToString() }, ct);
            if (from != _owner) return;
            var data = cb["data"]?.ToString() ?? "";
            if (data.StartsWith("ask:"))
            {
                var id = data[4..];
                var title = ListCommands?.Invoke().FirstOrDefault(c => c.Id == id)?.Title ?? id;
                await Send(chat, $"Выполнить «{title}»?", Keyboard(new[] { ("✅ Подтвердить", "run:" + id), ("✖ Отмена", "menu") }), ct);
            }
            else if (data.StartsWith("run:") && RunCommand != null)
                await Send(chat, await RunCommand(data[4..]), null, ct);
            else if (data == "menu")
                await SendMenu(chat, ct);
            else if (data == "shot")
                await SendShot(chat, ct);
            return;
        }

        var msg = u["message"];
        if (msg == null) return;
        long sender = msg["from"]?["id"]?.GetValue<long>() ?? 0;
        long chatId = msg["chat"]?["id"]?.GetValue<long>() ?? sender;
        var text = msg["text"]?.ToString() ?? "";

        if (_owner == 0 || sender != _owner)
        {
            // Помогаем узнать ID владельца, но ничего не выполняем
            await Send(chatId, $"Ваш Telegram ID: {sender}. Укажите его в Altair → Расширения → PC Control.", null, ct);
            return;
        }

        if (text is "/start" or "/menu" or "меню" or "Меню") { await SendMenu(chatId, ct); return; }
        if (text == "/screen") { await SendShot(chatId, ct); return; }
        if (text.StartsWith("/cmd ") && Shell != null) { await Send(chatId, Clip(await Shell(text[5..])), null, ct); return; }
        if (AskAi != null && text.Length > 0) { await Send(chatId, await AskAi(text), null, ct); }
    }

    private async Task SendShot(long chat, CancellationToken ct)
    {
        var png = Screenshot != null ? await Screenshot() : null;
        if (png == null) { await Send(chat, "Скриншот недоступен", null, ct); return; }
        using var form = new MultipartFormDataContent
        {
            { new StringContent(chat.ToString()), "chat_id" },
            { new ByteArrayContent(png), "photo", "screen.png" }
        };
        await _http.PostAsync(Api("sendPhoto"), form, ct);
    }

    private async Task SendMenu(long chat, CancellationToken ct)
    {
        var items = ListCommands?.Invoke() ?? Array.Empty<TgCommandItem>();
        var rows = items.Take(60).Select(c => (c.Title, (c.NeedsConfirm ? "ask:" : "run:") + c.Id)).ToList();
        rows.Add(("🖥 Скриншот", "shot"));
        await Send(chat, items.Count == 0 ? "Нет команд, открытых для Telegram." : "Команды:", Keyboard(rows, 2), ct);
    }

    public Task Notify(string text, CancellationToken ct = default) => _owner == 0 ? Task.CompletedTask : Send(_owner, text, null, ct);

    private async Task Send(long chat, string text, JsonNode? markup, CancellationToken ct)
    {
        var body = new JsonObject { ["chat_id"] = chat, ["text"] = Clip(text) };
        if (markup != null) body["reply_markup"] = markup;
        await Post("sendMessage", body, ct);
    }

    private async Task Post(string method, JsonObject body, CancellationToken ct)
    {
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var r = await _http.PostAsync(Api(method), content, ct);
    }

    private static string Clip(string s) => s.Length > 4000 ? s[..4000] + "…" : s;

    private static JsonNode Keyboard(IEnumerable<(string Text, string Data)> buttons, int perRow = 2)
    {
        var rows = new JsonArray();
        JsonArray? row = null;
        foreach (var (t, d) in buttons)
        {
            if (row == null || row.Count >= perRow) { row = new JsonArray(); rows.Add(row); }
            row.Add(new JsonObject { ["text"] = t, ["callback_data"] = d.Length > 64 ? d[..64] : d });
        }
        return new JsonObject { ["inline_keyboard"] = rows };
    }
}
