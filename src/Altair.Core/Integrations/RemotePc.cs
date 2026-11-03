using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Altair.Core.Integrations;

/// <summary>
/// «Второй компьютер»: команды между ПК в локальной сети. Сопряжение по 6-значному коду,
/// дальше каждое сообщение подписывается HMAC-SHA256 общим ключом; защита от повтора по метке времени и nonce.
/// Протокол: одна JSON-строка запрос → одна JSON-строка ответ.
/// </summary>
public sealed class RemotePcServer : IAsyncDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly HashSet<string> _seenNonces = new();
    public string SharedKey { get; set; }
    public string? PairingCode { get; private set; }
    private DateTime _pairingUntil;

    public Func<string, Task<string>>? RunCommand { get; set; }
    public Func<string, Task>? Say { get; set; }
    public event Action<string>? KeyIssued;

    public RemotePcServer(string sharedKey) => SharedKey = sharedKey;

    public string BeginPairing()
    {
        PairingCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _pairingUntil = DateTime.UtcNow.AddMinutes(3);
        return PairingCode;
    }

    public void Start(int port)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoop(_cts.Token));
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener!.AcceptTcpClientAsync(ct); } catch { break; }
            _ = Task.Run(() => Serve(c, ct));
        }
    }

    private async Task Serve(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        client.ReceiveTimeout = 10_000;
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        var line = await reader.ReadLineAsync(ct);
        if (line == null || line.Length > 64_000) return;
        JsonObject resp;
        try { resp = await Handle(JsonNode.Parse(line)!.AsObject()); }
        catch (Exception ex) { resp = new JsonObject { ["ok"] = false, ["error"] = ex.Message }; }
        await writer.WriteLineAsync(resp.ToJsonString());
    }

    private async Task<JsonObject> Handle(JsonObject req)
    {
        var op = req["op"]?.ToString();
        if (op == "pair")
        {
            if (PairingCode == null || DateTime.UtcNow > _pairingUntil || req["code"]?.ToString() != PairingCode)
                throw new UnauthorizedAccessException("Неверный или просроченный код сопряжения");
            PairingCode = null;
            SharedKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            KeyIssued?.Invoke(SharedKey);
            return new JsonObject { ["ok"] = true, ["key"] = SharedKey, ["name"] = Environment.MachineName };
        }

        RemotePcProtocol.Verify(req, SharedKey, _seenNonces);
        var arg = req["arg"]?.ToString() ?? "";
        switch (op)
        {
            case "ping": return new JsonObject { ["ok"] = true, ["name"] = Environment.MachineName };
            case "run":
                var r = RunCommand != null ? await RunCommand(arg) : "Команды не поддерживаются";
                return new JsonObject { ["ok"] = true, ["result"] = r };
            case "say":
                if (Say != null) await Say(arg);
                return new JsonObject { ["ok"] = true };
            default: throw new InvalidOperationException("Неизвестная операция");
        }
    }

    public ValueTask DisposeAsync() { _cts?.Cancel(); _listener?.Stop(); return ValueTask.CompletedTask; }
}

public static class RemotePcProtocol
{
    public static string Sign(string key, string op, string arg, long ts, string nonce)
    {
        using var h = new HMACSHA256(Convert.FromBase64String(key));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes($"{op}\n{arg}\n{ts}\n{nonce}")));
    }

    public static void Verify(JsonObject req, string key, HashSet<string> seen)
    {
        if (string.IsNullOrEmpty(key)) throw new UnauthorizedAccessException("Компьютеры не сопряжены");
        var ts = req["ts"]?.GetValue<long>() ?? 0;
        var nonce = req["nonce"]?.ToString() ?? "";
        if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) > 60) throw new UnauthorizedAccessException("Устаревший запрос");
        var expected = Sign(key, req["op"]?.ToString() ?? "", req["arg"]?.ToString() ?? "", ts, nonce);
        var given = req["sig"]?.ToString() ?? "";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(given)))
            throw new UnauthorizedAccessException("Неверная подпись");
        lock (seen)
        {
            if (!seen.Add(nonce)) throw new UnauthorizedAccessException("Повтор запроса");
            if (seen.Count > 5000) seen.Clear();
        }
    }

    public static async Task<JsonObject> SendAsync(string host, int port, JsonObject req, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(host, port, timeout.Token);
        var stream = client.GetStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await writer.WriteLineAsync(req.ToJsonString());
        var line = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Нет ответа");
        var resp = JsonNode.Parse(line)!.AsObject();
        if (resp["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException(resp["error"]?.ToString() ?? "Ошибка второго ПК");
        return resp;
    }

    public static Task<JsonObject> CallAsync(string host, int port, string key, string op, string arg, CancellationToken ct)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        var req = new JsonObject { ["op"] = op, ["arg"] = arg, ["ts"] = ts, ["nonce"] = nonce, ["sig"] = Sign(key, op, arg, ts, nonce) };
        return SendAsync(host, port, req, ct);
    }

    public static async Task<string> PairAsync(string host, int port, string code, CancellationToken ct)
    {
        var r = await SendAsync(host, port, new JsonObject { ["op"] = "pair", ["code"] = code }, ct);
        return r["key"]!.ToString();
    }
}
