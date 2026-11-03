using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Altair.Core.Storage;

namespace Altair.Core.AI;

internal static class Http
{
    public static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(120) };

    public static async Task<JsonNode> PostJson(HttpClient http, string url, JsonNode body, Action<HttpRequestHeaders>? headers, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        headers?.Invoke(req.Headers);
        using var resp = await http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new AiException(Describe((int)resp.StatusCode, text), (int)resp.StatusCode);
        return JsonNode.Parse(text) ?? throw new AiException("Пустой ответ сервера");
    }

    public static string Describe(int status, string body)
    {
        var hint = status switch
        {
            401 or 403 => "неверный или просроченный ключ",
            402 => "закончились средства на балансе провайдера",
            404 => "неверная модель или адрес",
            429 => "превышен лимит запросов провайдера — подождите или смените модель",
            >= 500 => "сервер провайдера недоступен",
            _ => "ошибка запроса"
        };
        var snippet = body.Length > 300 ? body[..300] : body;
        return $"HTTP {status}: {hint}. {snippet}";
    }
}

/// <summary>OpenAI и все совместимые: DeepSeek, Grok, OpenRouter, ProxyAPI, AITUNNEL, Ollama, LM Studio.</summary>
public sealed class OpenAiCompatibleProvider : IChatProvider
{
    private readonly AiProviderConfig _cfg;
    private readonly HttpClient _http;
    public OpenAiCompatibleProvider(AiProviderConfig cfg, HttpClient? http = null) { _cfg = cfg; _http = http ?? Http.Shared; }
    public string Id => _cfg.Id;
    public string Name => _cfg.Name;
    public bool SupportsVision => !string.IsNullOrEmpty(_cfg.VisionModel);

    public static JsonArray BuildMessages(IReadOnlyList<ChatMessage> messages)
    {
        var arr = new JsonArray();
        foreach (var m in messages)
        {
            var role = m.Role switch { Role.System => "system", Role.Assistant => "assistant", _ => "user" };
            if (m.Images.Count == 0)
            {
                arr.Add(new JsonObject { ["role"] = role, ["content"] = m.Text });
                continue;
            }
            var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = m.Text } };
            foreach (var img in m.Images)
                parts.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = $"data:{img.MimeType};base64,{img.Base64}" }
                });
            arr.Add(new JsonObject { ["role"] = role, ["content"] = parts });
        }
        return arr;
    }

    public async Task<ChatResult> CompleteAsync(IReadOnlyList<ChatMessage> messages, ChatOptions o, CancellationToken ct)
    {
        var hasImages = messages.Any(m => m.Images.Count > 0);
        var model = o.ModelOverride ?? (hasImages && SupportsVision ? _cfg.VisionModel! : _cfg.Model);
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = BuildMessages(messages),
            ["temperature"] = o.Temperature,
            ["max_tokens"] = o.MaxTokens,
        };
        if (o.Json) body["response_format"] = new JsonObject { ["type"] = "json_object" };

        var url = _cfg.BaseUrl.TrimEnd('/') + "/chat/completions";
        var json = await Http.PostJson(_http, url, body, h =>
        {
            if (!string.IsNullOrEmpty(_cfg.ApiKey)) h.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.ApiKey);
            if (_cfg.BaseUrl.Contains("openrouter", StringComparison.OrdinalIgnoreCase))
            {
                h.TryAddWithoutValidation("HTTP-Referer", "https://altair.local");
                h.TryAddWithoutValidation("X-Title", "Altair Assistant");
            }
        }, ct);

        var text = json["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        int inTok = json["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0;
        int outTok = json["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0;
        return new ChatResult(text.Trim(), inTok, outTok, Id, model);
    }
}

public sealed class AnthropicProvider : IChatProvider
{
    private readonly AiProviderConfig _cfg; private readonly HttpClient _http;
    public AnthropicProvider(AiProviderConfig cfg, HttpClient? http = null) { _cfg = cfg; _http = http ?? Http.Shared; }
    public string Id => _cfg.Id; public string Name => _cfg.Name; public bool SupportsVision => true;

    public async Task<ChatResult> CompleteAsync(IReadOnlyList<ChatMessage> messages, ChatOptions o, CancellationToken ct)
    {
        var system = string.Join("\n\n", messages.Where(m => m.Role == Role.System).Select(m => m.Text));
        var arr = new JsonArray();
        foreach (var m in messages.Where(m => m.Role != Role.System))
        {
            var content = new JsonArray();
            foreach (var img in m.Images)
                content.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = img.MimeType, ["data"] = img.Base64 }
                });
            content.Add(new JsonObject { ["type"] = "text", ["text"] = string.IsNullOrEmpty(m.Text) ? "." : m.Text });
            arr.Add(new JsonObject { ["role"] = m.Role == Role.Assistant ? "assistant" : "user", ["content"] = content });
        }
        var model = o.ModelOverride ?? _cfg.Model;
        var body = new JsonObject { ["model"] = model, ["max_tokens"] = o.MaxTokens, ["temperature"] = o.Temperature, ["messages"] = arr };
        if (system.Length > 0) body["system"] = o.Json ? system + "\n\nОтвечай только валидным JSON." : system;

        var json = await Http.PostJson(_http, _cfg.BaseUrl.TrimEnd('/') + "/v1/messages", body, h =>
        {
            h.TryAddWithoutValidation("x-api-key", _cfg.ApiKey);
            h.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }, ct);

        var sb = new StringBuilder();
        foreach (var part in json["content"]?.AsArray() ?? new JsonArray())
            if (part?["type"]?.GetValue<string>() == "text") sb.Append(part["text"]?.GetValue<string>());
        return new ChatResult(sb.ToString().Trim(),
            json["usage"]?["input_tokens"]?.GetValue<int>() ?? 0,
            json["usage"]?["output_tokens"]?.GetValue<int>() ?? 0, Id, model);
    }
}

public sealed class GeminiProvider : IChatProvider
{
    private readonly AiProviderConfig _cfg; private readonly HttpClient _http;
    public GeminiProvider(AiProviderConfig cfg, HttpClient? http = null) { _cfg = cfg; _http = http ?? Http.Shared; }
    public string Id => _cfg.Id; public string Name => _cfg.Name; public bool SupportsVision => true;

    public async Task<ChatResult> CompleteAsync(IReadOnlyList<ChatMessage> messages, ChatOptions o, CancellationToken ct)
    {
        var model = o.ModelOverride ?? _cfg.Model;
        var contents = new JsonArray();
        foreach (var m in messages.Where(m => m.Role != Role.System))
        {
            var parts = new JsonArray();
            foreach (var img in m.Images)
                parts.Add(new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = img.MimeType, ["data"] = img.Base64 } });
            parts.Add(new JsonObject { ["text"] = string.IsNullOrEmpty(m.Text) ? "." : m.Text });
            contents.Add(new JsonObject { ["role"] = m.Role == Role.Assistant ? "model" : "user", ["parts"] = parts });
        }
        var body = new JsonObject
        {
            ["contents"] = contents,
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = o.Temperature,
                ["maxOutputTokens"] = o.MaxTokens,
            }
        };
        if (o.Json) body["generationConfig"]!["responseMimeType"] = "application/json";
        var system = string.Join("\n\n", messages.Where(m => m.Role == Role.System).Select(m => m.Text));
        if (system.Length > 0) body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = system } } };

        var url = $"{_cfg.BaseUrl.TrimEnd('/')}/v1beta/models/{Uri.EscapeDataString(model)}:generateContent";
        var json = await Http.PostJson(_http, url, body, h => h.TryAddWithoutValidation("x-goog-api-key", _cfg.ApiKey), ct);
        var sb = new StringBuilder();
        foreach (var p in json["candidates"]?[0]?["content"]?["parts"]?.AsArray() ?? new JsonArray())
            sb.Append(p?["text"]?.GetValue<string>());
        return new ChatResult(sb.ToString().Trim(),
            json["usageMetadata"]?["promptTokenCount"]?.GetValue<int>() ?? 0,
            json["usageMetadata"]?["candidatesTokenCount"]?.GetValue<int>() ?? 0, Id, model);
    }
}

/// <summary>YandexGPT Foundation Models API (ключ сервисного аккаунта + folder id).</summary>
public sealed class YandexGptProvider : IChatProvider
{
    private readonly AiProviderConfig _cfg; private readonly HttpClient _http;
    public YandexGptProvider(AiProviderConfig cfg, HttpClient? http = null) { _cfg = cfg; _http = http ?? Http.Shared; }
    public string Id => _cfg.Id; public string Name => _cfg.Name; public bool SupportsVision => false;

    public async Task<ChatResult> CompleteAsync(IReadOnlyList<ChatMessage> messages, ChatOptions o, CancellationToken ct)
    {
        var model = o.ModelOverride ?? _cfg.Model;
        var msgs = new JsonArray();
        foreach (var m in messages)
            msgs.Add(new JsonObject { ["role"] = m.Role switch { Role.System => "system", Role.Assistant => "assistant", _ => "user" }, ["text"] = m.Text });
        var body = new JsonObject
        {
            ["modelUri"] = $"gpt://{_cfg.FolderId}/{model}",
            ["completionOptions"] = new JsonObject { ["stream"] = false, ["temperature"] = o.Temperature, ["maxTokens"] = o.MaxTokens.ToString() },
            ["messages"] = msgs
        };
        var json = await Http.PostJson(_http, _cfg.BaseUrl.TrimEnd('/') + "/foundationModels/v1/completion", body, h =>
        {
            h.TryAddWithoutValidation("Authorization", "Api-Key " + _cfg.ApiKey);
            if (!string.IsNullOrEmpty(_cfg.FolderId)) h.TryAddWithoutValidation("x-folder-id", _cfg.FolderId);
        }, ct);
        var text = json["result"]?["alternatives"]?[0]?["message"]?["text"]?.GetValue<string>() ?? "";
        int inTok = int.TryParse(json["result"]?["usage"]?["inputTextTokens"]?.ToString(), out var a) ? a : 0;
        int outTok = int.TryParse(json["result"]?["usage"]?["completionTokens"]?.ToString(), out var b) ? b : 0;
        return new ChatResult(text.Trim(), inTok, outTok, Id, model);
    }
}

/// <summary>GigaChat: OAuth по ключу авторизации (Basic), токен живёт 30 минут.</summary>
public sealed class GigaChatProvider : IChatProvider
{
    private readonly AiProviderConfig _cfg; private readonly HttpClient _http;
    private string? _token; private DateTimeOffset _expires;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public GigaChatProvider(AiProviderConfig cfg, HttpClient? http = null)
    {
        _cfg = cfg;
        _http = http ?? new HttpClient(new HttpClientHandler
        {
            // Серверы Сбера подписаны НУЦ Минцифры; доверяем только этой цепочке, остальное — стандартная проверка.
            ServerCertificateCustomValidationCallback = (_, cert, chain, errors) =>
            {
                if (errors == System.Net.Security.SslPolicyErrors.None) return true;
                if (cert is null || chain is null) return false;
                var host = cert.GetNameInfo(X509NameType.DnsName, false) ?? "";
                bool sberHost = host.EndsWith("sberbank.ru", StringComparison.OrdinalIgnoreCase);
                bool russianRoot = chain.ChainElements.Cast<X509ChainElement>()
                    .Any(e => e.Certificate.Subject.Contains("Russian Trusted", StringComparison.OrdinalIgnoreCase));
                return sberHost && russianRoot;
            }
        }) { Timeout = TimeSpan.FromSeconds(120) };
    }

    public string Id => _cfg.Id; public string Name => _cfg.Name; public bool SupportsVision => false;

    private async Task<string> Token(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_token != null && DateTimeOffset.UtcNow < _expires.AddMinutes(-1)) return _token;
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://ngw.devices.sberbank.ru:9443/api/v2/oauth")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["scope"] = "GIGACHAT_API_PERS" })
            };
            req.Headers.TryAddWithoutValidation("Authorization", "Basic " + _cfg.ApiKey);
            req.Headers.TryAddWithoutValidation("RqUID", Guid.NewGuid().ToString());
            using var resp = await _http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new AiException(Http.Describe((int)resp.StatusCode, text), (int)resp.StatusCode);
            var json = JsonNode.Parse(text)!;
            _token = json["access_token"]!.GetValue<string>();
            var expMs = json["expires_at"]?.GetValue<long>() ?? DateTimeOffset.UtcNow.AddMinutes(29).ToUnixTimeMilliseconds();
            _expires = DateTimeOffset.FromUnixTimeMilliseconds(expMs);
            return _token;
        }
        finally { _lock.Release(); }
    }

    public async Task<ChatResult> CompleteAsync(IReadOnlyList<ChatMessage> messages, ChatOptions o, CancellationToken ct)
    {
        var token = await Token(ct);
        var model = o.ModelOverride ?? _cfg.Model;
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = OpenAiCompatibleProvider.BuildMessages(messages.Select(m => new ChatMessage(m.Role, m.Text)).ToList()),
            ["temperature"] = o.Temperature,
            ["max_tokens"] = o.MaxTokens
        };
        var json = await Http.PostJson(_http, _cfg.BaseUrl.TrimEnd('/') + "/chat/completions", body,
            h => h.Authorization = new AuthenticationHeaderValue("Bearer", token), ct);
        return new ChatResult((json["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "").Trim(),
            json["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0,
            json["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0, Id, model);
    }
}

public static class ProviderFactory
{
    public static IChatProvider Create(AiProviderConfig cfg, HttpClient? http = null) => cfg.Kind switch
    {
        "anthropic" => new AnthropicProvider(cfg, http),
        "gemini" => new GeminiProvider(cfg, http),
        "yandex" => new YandexGptProvider(cfg, http),
        "gigachat" => new GigaChatProvider(cfg, http),
        _ => new OpenAiCompatibleProvider(cfg, http),
    };
}
