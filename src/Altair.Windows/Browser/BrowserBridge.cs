using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Altair.Core.Integrations;

namespace Altair.Windows.Browser;

/// <summary>
/// Локальный мост к расширению браузера: ws://localhost:47811/altair/. Принимаются только подключения
/// с Origin расширения (chrome-extension:// / extension://). Агент получает список элементов страницы и нажимает точно.
/// В поля паролей, карт и одноразовых кодов расширение не вводит (проверка на стороне расширения).
/// </summary>
public sealed class BrowserBridge : IAsyncDisposable
{
    public const int Port = 47811;
    private HttpListener? _http;
    private WebSocket? _socket;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode>> _pending = new();
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _send = new(1, 1);
    public bool Connected => _socket?.State == WebSocketState.Open;
    public string? BrowserName { get; private set; }
    public event Action<bool>? ConnectionChanged;

    public void Start()
    {
        _http = new HttpListener();
        _http.Prefixes.Add($"http://localhost:{Port}/altair/");
        _http.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => Accept(_cts.Token));
    }

    private async Task Accept(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _http!.GetContextAsync(); } catch { break; }
            var origin = ctx.Request.Headers["Origin"] ?? "";
            if (!ctx.Request.IsWebSocketRequest || !(origin.StartsWith("chrome-extension://") || origin.StartsWith("extension://") || origin.StartsWith("moz-extension://")))
            {
                ctx.Response.StatusCode = 403; ctx.Response.Close(); continue;
            }
            var wsCtx = await ctx.AcceptWebSocketAsync(null);
            _socket?.Abort();
            _socket = wsCtx.WebSocket;
            ConnectionChanged?.Invoke(true);
            _ = Task.Run(() => Read(wsCtx.WebSocket, ct));
        }
    }

    private async Task Read(WebSocket ws, CancellationToken ct)
    {
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var msg = JsonNode.Parse(await WsUtil.ReceiveText(ws, ct));
                if (msg == null) continue;
                var type = msg["type"]?.ToString();
                if (type == "hello") { BrowserName = msg["browser"]?.ToString(); continue; }
                if (type == "ping") { await Send(new JsonObject { ["type"] = "pong" }, ct); continue; }
                var id = msg["id"]?.ToString();
                if (id != null && _pending.TryRemove(id, out var tcs)) tcs.TrySetResult(msg);
            }
        }
        catch { }
        if (_socket == ws) { _socket = null; ConnectionChanged?.Invoke(false); }
    }

    private async Task Send(JsonObject o, CancellationToken ct)
    {
        var ws = _socket ?? throw new InvalidOperationException("Расширение браузера не подключено. Установите его: Altair → Расширения → Браузер.");
        await _send.WaitAsync(ct);
        try { await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(o.ToJsonString())), WebSocketMessageType.Text, true, ct); }
        finally { _send.Release(); }
    }

    public async Task<JsonNode> Request(string type, JsonObject? args = null, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var o = args ?? new JsonObject();
        o["id"] = id; o["type"] = type;
        await Send(o, ct);
        var r = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        if (r["ok"]?.GetValue<bool>() == false) throw new InvalidOperationException(r["error"]?.ToString() ?? "Ошибка расширения");
        return r;
    }

    public async Task<string> Snapshot(CancellationToken ct)
    {
        var r = await Request("snapshot", null, ct);
        var sb = new StringBuilder();
        sb.AppendLine($"Страница: {r["title"]} — {r["url"]}");
        foreach (var e in r["elements"]?.AsArray() ?? new JsonArray())
            sb.AppendLine($"[{e?["tag"]}] «{e?["label"]}»" + (e?["kind"] is JsonNode k ? $" ({k})" : ""));
        var text = r["text"]?.ToString();
        if (!string.IsNullOrEmpty(text)) sb.AppendLine("Текст: " + (text.Length > 3000 ? text[..3000] + "…" : text));
        return sb.ToString();
    }

    public Task Click(string label, CancellationToken ct) => Request("click", new JsonObject { ["label"] = label }, ct);
    public Task Fill(string label, string value, CancellationToken ct) => Request("fill", new JsonObject { ["label"] = label, ["value"] = value }, ct);
    public Task NewTab(string? url, CancellationToken ct) => Request("newtab", new JsonObject { ["url"] = url ?? "" }, ct);
    public Task CloseTab(CancellationToken ct) => Request("closetab", null, ct);
    public async Task<string> ReadText(CancellationToken ct) => (await Request("read", null, ct))["text"]?.ToString() ?? "";

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        try { _socket?.Abort(); } catch { }
        try { _http?.Stop(); } catch { }
        await Task.CompletedTask;
    }
}
