using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Altair.Core.Storage;

namespace Altair.Core.Integrations;

public sealed record McpTool(string Server, string Name, string Description, JsonNode? Schema);

/// <summary>
/// Клиент Model Context Protocol (stdio, JSON-RPC 2.0). «Подключения»: GitHub, Notion, файлы, базы и т.д. —
/// любой MCP-сервер из каталога modelcontextprotocol.io запускается командой (npx/uvx/exe).
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    private readonly McpServerConfig _cfg;
    private Process? _proc;
    private int _nextId;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> _pending = new();
    public List<McpTool> Tools { get; } = new();
    public string Name => _cfg.Name;
    public event Action<string>? Log;

    public McpClient(McpServerConfig cfg) => _cfg = cfg;

    public async Task StartAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() && !_cfg.Command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "cmd.exe" : _cfg.Command,
            Arguments = OperatingSystem.IsWindows() && !_cfg.Command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? $"/c {_cfg.Command} {_cfg.Args}" : _cfg.Args,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var kv in _cfg.Env) psi.Environment[kv.Key] = kv.Value;
        _proc = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить " + _cfg.Command);
        _ = Task.Run(ReadLoop);
        _ = Task.Run(async () => { while (_proc is { HasExited: false }) { var l = await _proc.StandardError.ReadLineAsync(); if (l == null) break; Log?.Invoke($"[{Name}] {l}"); } });

        await Call("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "Altair Assistant", ["version"] = "1.0.0" }
        }, ct);
        await Notify("notifications/initialized");
        var list = await Call("tools/list", new JsonObject(), ct);
        Tools.Clear();
        foreach (var t in list["tools"]?.AsArray() ?? new JsonArray())
            Tools.Add(new McpTool(Name, t!["name"]!.ToString(), t["description"]?.ToString() ?? "", t["inputSchema"]?.DeepClone()));
    }

    public async Task<string> CallToolAsync(string tool, JsonObject args, CancellationToken ct)
    {
        var r = await Call("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args }, ct);
        var sb = new StringBuilder();
        foreach (var c in r["content"]?.AsArray() ?? new JsonArray())
            if (c?["type"]?.ToString() == "text") sb.AppendLine(c["text"]?.ToString());
        if (r["isError"]?.GetValue<bool>() == true) throw new InvalidOperationException(sb.ToString().Trim());
        return sb.ToString().Trim();
    }

    private async Task<JsonNode> Call(string method, JsonObject @params, CancellationToken ct)
    {
        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        await Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params });
        var resp = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
        if (resp["error"] != null) throw new InvalidOperationException($"MCP {Name}: {resp["error"]?["message"]}");
        return resp["result"] ?? new JsonObject();
    }

    private Task Notify(string method) => Write(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method });

    private async Task Write(JsonNode n)
    {
        if (_proc == null || _proc.HasExited) throw new InvalidOperationException($"MCP-сервер {Name} не запущен");
        await _proc.StandardInput.WriteLineAsync(n.ToJsonString());
        await _proc.StandardInput.FlushAsync();
    }

    private async Task ReadLoop()
    {
        try
        {
            while (_proc is { HasExited: false })
            {
                var line = await _proc.StandardOutput.ReadLineAsync();
                if (line == null) break;
                if (line.Length == 0 || line[0] != '{') continue;
                JsonNode? n; try { n = JsonNode.Parse(line); } catch { continue; }
                if (n?["id"] is JsonValue idv && idv.TryGetValue<int>(out var id) && _pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(n);
            }
        }
        catch { }
        foreach (var p in _pending.Values) p.TrySetException(new InvalidOperationException($"MCP-сервер {Name} завершился"));
    }

    public ValueTask DisposeAsync()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
        _proc?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Каталог готовых подключений (команда запуска официальных/популярных MCP-серверов).</summary>
public static class McpCatalog
{
    public sealed record Entry(string Name, string Command, string Args, string[] EnvKeys, string Description);

    public static readonly Entry[] Items =
    {
        new("GitHub", "npx", "-y @modelcontextprotocol/server-github", new[] { "GITHUB_PERSONAL_ACCESS_TOKEN" }, "Репозитории, issues, PR"),
        new("Notion", "npx", "-y @notionhq/notion-mcp-server", new[] { "NOTION_TOKEN" }, "Страницы и базы Notion"),
        new("Файлы", "npx", "-y @modelcontextprotocol/server-filesystem %USERPROFILE%\\Documents", Array.Empty<string>(), "Чтение и запись файлов в папке"),
        new("Память", "npx", "-y @modelcontextprotocol/server-memory", Array.Empty<string>(), "Граф знаний ассистента"),
        new("Браузер (Playwright)", "npx", "-y @playwright/mcp@latest", Array.Empty<string>(), "Управление браузером"),
        new("Время", "uvx", "mcp-server-time", Array.Empty<string>(), "Часовые пояса"),
        new("Fetch", "uvx", "mcp-server-fetch", Array.Empty<string>(), "Загрузка веб-страниц"),
        new("SQLite", "uvx", "mcp-server-sqlite --db-path %USERPROFILE%\\altair.db", Array.Empty<string>(), "Локальная база"),
        new("Slack", "npx", "-y @modelcontextprotocol/server-slack", new[] { "SLACK_BOT_TOKEN", "SLACK_TEAM_ID" }, "Каналы и сообщения"),
        new("Google Maps", "npx", "-y @modelcontextprotocol/server-google-maps", new[] { "GOOGLE_MAPS_API_KEY" }, "Места и маршруты"),
        new("Brave Search", "npx", "-y @modelcontextprotocol/server-brave-search", new[] { "BRAVE_API_KEY" }, "Поиск в интернете"),
        new("Puppeteer", "npx", "-y @modelcontextprotocol/server-puppeteer", Array.Empty<string>(), "Скриншоты и автоматизация сайтов"),
        new("Sequential Thinking", "npx", "-y @modelcontextprotocol/server-sequential-thinking", Array.Empty<string>(), "Пошаговое рассуждение"),
        new("Git", "uvx", "mcp-server-git", Array.Empty<string>(), "Локальные git-репозитории"),
    };
}
