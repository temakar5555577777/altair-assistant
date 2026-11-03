using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Altair.Core.AI;

/// <summary>Облачное распознавание (OpenAI Whisper и совместимые: ProxyAPI, AITUNNEL) и синтез OpenAI TTS.</summary>
public sealed class OpenAiAudio
{
    private readonly HttpClient _http;
    private readonly string _base, _key;

    public OpenAiAudio(string baseUrl, string apiKey, HttpClient? http = null)
    {
        _base = baseUrl.TrimEnd('/'); _key = apiKey; _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task<string> TranscribeAsync(byte[] wav, string language = "ru", string? prompt = null, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "speech.wav");
        form.Add(new StringContent("whisper-1"), "model");
        form.Add(new StringContent(language), "language");
        if (!string.IsNullOrEmpty(prompt)) form.Add(new StringContent(prompt), "prompt"); // подсказка словаря: имена команд
        using var req = new HttpRequestMessage(HttpMethod.Post, _base + "/audio/transcriptions") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new AiException(Http.Describe((int)resp.StatusCode, text), (int)resp.StatusCode);
        return JsonNode.Parse(text)?["text"]?.ToString() ?? "";
    }

    public async Task<byte[]> SpeakAsync(string text, string voice = "onyx", string model = "gpt-4o-mini-tts", CancellationToken ct = default)
    {
        var body = new JsonObject { ["model"] = model, ["voice"] = voice, ["input"] = text, ["response_format"] = "mp3" };
        using var req = new HttpRequestMessage(HttpMethod.Post, _base + "/audio/speech")
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw new AiException(Http.Describe((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct)), (int)resp.StatusCode);
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }
}

/// <summary>Fish Audio: синтез голосом-моделью и создание своего голоса по образцу (ключ пользователя).</summary>
public sealed class FishAudio
{
    private readonly HttpClient _http; private readonly string _key;
    private const string Base = "https://api.fish.audio";

    public FishAudio(string apiKey, HttpClient? http = null) { _key = apiKey; _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) }; }

    public async Task<byte[]> SpeakAsync(string text, string voiceId, CancellationToken ct = default)
    {
        var body = new JsonObject { ["text"] = text, ["reference_id"] = voiceId, ["format"] = "mp3", ["latency"] = "balanced" };
        using var req = new HttpRequestMessage(HttpMethod.Post, Base + "/v1/tts")
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        req.Headers.TryAddWithoutValidation("model", "s1");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw new AiException(Http.Describe((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct)), (int)resp.StatusCode);
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Создаёт приватную голосовую модель из образцов (wav/mp3/m4a). Возвращает её id.</summary>
    public async Task<string> CreateVoiceAsync(string title, IEnumerable<string> sampleFiles, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("tts"), "type" },
            { new StringContent(title), "title" },
            { new StringContent("fast"), "train_mode" },
            { new StringContent("private"), "visibility" },
        };
        foreach (var f in sampleFiles)
        {
            var c = new ByteArrayContent(await File.ReadAllBytesAsync(f, ct));
            c.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(c, "voices", Path.GetFileName(f));
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, Base + "/model") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new AiException(Http.Describe((int)resp.StatusCode, text), (int)resp.StatusCode);
        return JsonNode.Parse(text)?["_id"]?.ToString() ?? throw new AiException("Fish Audio не вернул id голоса");
    }
}
