using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;

namespace Altair.Core.Integrations;

/// <summary>OBS WebSocket v5 (OBS 28+): сцены, запись, стрим, микрофон.</summary>
public sealed class ObsClient : IAsyncDisposable
{
    private ClientWebSocket? _ws;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode>> _pending = new();
    private CancellationTokenSource? _cts;

    public bool Connected => _ws?.State == WebSocketState.Open;

    public static string AuthString(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    public async Task ConnectAsync(string url, string password, CancellationToken ct)
    {
        await DisposeAsync();
        _ws = new ClientWebSocket();
        _ws.Options.AddSubProtocol("obswebsocket.json");
        await _ws.ConnectAsync(new Uri(url), ct);
        var hello = await Receive(ct);
        var ident = new JsonObject { ["rpcVersion"] = 1 };
        var auth = hello["d"]?["authentication"];
        if (auth != null)
            ident["authentication"] = AuthString(password, auth["salt"]!.ToString(), auth["challenge"]!.ToString());
        await Send(new JsonObject { ["op"] = 1, ["d"] = ident }, ct);
        var identified = await Receive(ct);
        if (identified["op"]?.GetValue<int>() != 2) throw new InvalidOperationException("OBS отклонил подключение (пароль?)");
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoop(_cts.Token));
    }

    public async Task<JsonNode> RequestAsync(string type, JsonObject? data = null, CancellationToken ct = default)
    {
        if (!Connected) throw new InvalidOperationException("OBS не подключён");
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var d = new JsonObject { ["requestType"] = type, ["requestId"] = id };
        if (data != null) d["requestData"] = data;
        await Send(new JsonObject { ["op"] = 6, ["d"] = d }, ct);
        using var reg = ct.Register(() => tcs.TrySetCanceled());
        var resp = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        if (resp["requestStatus"]?["result"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("OBS: " + (resp["requestStatus"]?["comment"]?.ToString() ?? "ошибка"));
        return resp["responseData"] ?? new JsonObject();
    }

    public Task SetScene(string name, CancellationToken ct = default) => RequestAsync("SetCurrentProgramScene", new JsonObject { ["sceneName"] = name }, ct);
    public Task ToggleRecord(CancellationToken ct = default) => RequestAsync("ToggleRecord", null, ct);
    public Task ToggleStream(CancellationToken ct = default) => RequestAsync("ToggleStream", null, ct);
    public Task ToggleMute(string input, CancellationToken ct = default) => RequestAsync("ToggleInputMute", new JsonObject { ["inputName"] = input }, ct);
    public Task SaveReplay(CancellationToken ct = default) => RequestAsync("SaveReplayBuffer", null, ct);

    public async Task<List<string>> Scenes(CancellationToken ct = default)
    {
        var r = await RequestAsync("GetSceneList", null, ct);
        return (r["scenes"]?.AsArray() ?? new JsonArray()).Select(s => s?["sceneName"]?.ToString() ?? "").Where(s => s.Length > 0).ToList();
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && Connected)
            {
                var msg = await Receive(ct);
                if (msg["op"]?.GetValue<int>() == 7)
                {
                    var id = msg["d"]?["requestId"]?.ToString();
                    if (id != null && _pending.TryRemove(id, out var tcs)) tcs.TrySetResult(msg["d"]!);
                }
            }
        }
        catch { /* соединение закрыто */ }
        foreach (var p in _pending.Values) p.TrySetException(new InvalidOperationException("OBS отключился"));
        _pending.Clear();
    }

    private async Task Send(JsonNode n, CancellationToken ct) =>
        await _ws!.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(n.ToJsonString())), WebSocketMessageType.Text, true, ct);

    private async Task<JsonNode> Receive(CancellationToken ct) =>
        JsonNode.Parse(await WsUtil.ReceiveText(_ws!, ct))!;

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_ws != null)
        {
            try { if (_ws.State == WebSocketState.Open) await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
            _ws.Dispose(); _ws = null;
        }
    }
}

public static class WsUtil
{
    public static async Task<string> ReceiveText(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (r.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Соединение закрыто");
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
        }
    }
}

public sealed record ChatLine(string User, string Text, DateTime At);

/// <summary>Чат Twitch через IRC over WebSocket: чтение (озвучка/модерация) и отправка сообщений.</summary>
public sealed class TwitchChat : IAsyncDisposable
{
    private ClientWebSocket? _ws; private CancellationTokenSource? _cts; private string _channel = "";
    public event Action<ChatLine>? Message;

    public async Task ConnectAsync(string channel, string? nick, string? oauth, CancellationToken ct)
    {
        _channel = channel.TrimStart('#').ToLowerInvariant();
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri("wss://irc-ws.chat.twitch.tv:443"), ct);
        bool anon = string.IsNullOrWhiteSpace(oauth);
        await Raw(anon ? "PASS SCHMOOPIIE" : "PASS oauth:" + oauth!.Replace("oauth:", ""), ct);
        await Raw("NICK " + (anon ? "justinfan" + Random.Shared.Next(10000, 99999) : nick), ct);
        await Raw("JOIN #" + _channel, ct);
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => Loop(_cts.Token));
    }

    public Task SendAsync(string text, CancellationToken ct = default) => Raw($"PRIVMSG #{_channel} :{text}", ct);

    private Task Raw(string line, CancellationToken ct) =>
        _ws!.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(line + "\r\n")), WebSocketMessageType.Text, true, ct);

    public static ChatLine? Parse(string line)
    {
        // :nick!nick@nick.tmi.twitch.tv PRIVMSG #channel :text
        var idx = line.IndexOf(" PRIVMSG ", StringComparison.Ordinal);
        if (idx < 0 || !line.StartsWith(':')) return null;
        var user = line[1..line.IndexOf('!')];
        var colon = line.IndexOf(" :", idx, StringComparison.Ordinal);
        if (colon < 0) return null;
        return new ChatLine(user, line[(colon + 2)..].TrimEnd('\r', '\n'), DateTime.Now);
    }

    private async Task Loop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var chunk = await WsUtil.ReceiveText(_ws!, ct);
                foreach (var line in chunk.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("PING")) { await Raw("PONG" + line[4..], ct); continue; }
                    var m = Parse(line);
                    if (m != null) Message?.Invoke(m);
                }
            }
        }
        catch { /* закрыто */ }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_ws != null) { try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { } _ws.Dispose(); }
    }
}
