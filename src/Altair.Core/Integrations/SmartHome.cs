using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Altair.Core.Integrations;

public sealed record HomeDeviceInfo(string Id, string Name, string Room, string Kind, string State, string Provider);

public interface ISmartHome
{
    string Name { get; }
    Task<IReadOnlyList<HomeDeviceInfo>> DevicesAsync(CancellationToken ct);
    Task SetAsync(HomeDeviceInfo device, string state, CancellationToken ct);
    Task<IReadOnlyList<(string Id, string Name)>> ScenesAsync(CancellationToken ct);
    Task RunSceneAsync(string id, CancellationToken ct);
}

/// <summary>Home Assistant REST API (долгоживущий токен из профиля HA).</summary>
public sealed class HomeAssistantClient : ISmartHome
{
    private readonly HttpClient _http; private readonly string _base;
    public string Name => "Home Assistant";

    public HomeAssistantClient(string baseUrl, string token, HttpClient? http = null)
    {
        _base = baseUrl.TrimEnd('/');
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<IReadOnlyList<HomeDeviceInfo>> DevicesAsync(CancellationToken ct)
    {
        var arr = JsonNode.Parse(await _http.GetStringAsync(_base + "/api/states", ct))!.AsArray();
        var list = new List<HomeDeviceInfo>();
        foreach (var s in arr)
        {
            var id = s!["entity_id"]!.ToString();
            var domain = id.Split('.')[0];
            if (domain is not ("light" or "switch" or "fan" or "cover" or "climate" or "media_player" or "lock" or "vacuum" or "scene" or "script")) continue;
            var name = s["attributes"]?["friendly_name"]?.ToString() ?? id;
            list.Add(new HomeDeviceInfo(id, name, "", domain, s["state"]?.ToString() ?? "", Name));
        }
        return list;
    }

    public async Task SetAsync(HomeDeviceInfo d, string state, CancellationToken ct)
    {
        var domain = d.Kind;
        string service; var body = new JsonObject { ["entity_id"] = d.Id };
        switch (state.ToLowerInvariant())
        {
            case "on" or "вкл" or "включи": service = domain == "cover" ? "open_cover" : domain == "lock" ? "lock" : "turn_on"; break;
            case "off" or "выкл" or "выключи": service = domain == "cover" ? "close_cover" : domain == "lock" ? "unlock" : "turn_off"; break;
            case "toggle" or "переключи": service = "toggle"; break;
            default:
                if (int.TryParse(state.TrimEnd('%'), out var n))
                {
                    if (domain == "light") { service = "turn_on"; body["brightness_pct"] = Math.Clamp(n, 0, 100); }
                    else if (domain == "climate") { service = "set_temperature"; body["temperature"] = n; }
                    else if (domain == "media_player") { service = "volume_set"; body["volume_level"] = Math.Clamp(n, 0, 100) / 100.0; }
                    else throw new InvalidOperationException($"Устройство {d.Name} не принимает числовое значение");
                }
                else throw new InvalidOperationException($"Непонятное состояние «{state}»");
                break;
        }
        using var c = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var r = await _http.PostAsync($"{_base}/api/services/{domain}/{service}", c, ct);
        r.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<(string Id, string Name)>> ScenesAsync(CancellationToken ct) =>
        (await DevicesAsync(ct)).Where(d => d.Kind is "scene" or "script").Select(d => (d.Id, d.Name)).ToList();

    public async Task RunSceneAsync(string id, CancellationToken ct)
    {
        var domain = id.Split('.')[0];
        using var c = new StringContent(new JsonObject { ["entity_id"] = id }.ToJsonString(), Encoding.UTF8, "application/json");
        using var r = await _http.PostAsync($"{_base}/api/services/{domain}/turn_on", c, ct);
        r.EnsureSuccessStatusCode();
    }
}

/// <summary>Яндекс Дом (IoT API) — OAuth-токен со scope iot:view + iot:control.</summary>
public sealed class YandexHomeClient : ISmartHome
{
    private readonly HttpClient _http;
    private const string Base = "https://api.iot.yandex.net/v1.0";
    public string Name => "Яндекс Дом";

    public YandexHomeClient(string oauthToken, HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", oauthToken);
    }

    private JsonNode? _cache;

    private async Task<JsonNode> Info(CancellationToken ct) =>
        _cache = JsonNode.Parse(await _http.GetStringAsync(Base + "/user/info", ct))!;

    public async Task<IReadOnlyList<HomeDeviceInfo>> DevicesAsync(CancellationToken ct)
    {
        var info = await Info(ct);
        var rooms = (info["rooms"]?.AsArray() ?? new JsonArray()).ToDictionary(r => r!["id"]!.ToString(), r => r!["name"]!.ToString());
        var list = new List<HomeDeviceInfo>();
        foreach (var d in info["devices"]?.AsArray() ?? new JsonArray())
        {
            var on = d!["capabilities"]?.AsArray().FirstOrDefault(c => c?["type"]?.ToString() == "devices.capabilities.on_off")?["state"]?["value"]?.ToString();
            var roomId = d["room"]?.ToString() ?? "";
            list.Add(new HomeDeviceInfo(d["id"]!.ToString(), d["name"]!.ToString(), rooms.GetValueOrDefault(roomId, ""),
                d["type"]?.ToString() ?? "", on ?? "", Name));
        }
        return list;
    }

    public async Task SetAsync(HomeDeviceInfo d, string state, CancellationToken ct)
    {
        JsonObject action;
        var s = state.ToLowerInvariant();
        if (s is "on" or "вкл" or "включи" or "off" or "выкл" or "выключи" or "toggle" or "переключи")
        {
            bool value = s is "on" or "вкл" or "включи" || (s is "toggle" or "переключи" && d.State != "true");
            action = new JsonObject { ["type"] = "devices.capabilities.on_off", ["state"] = new JsonObject { ["instance"] = "on", ["value"] = value } };
        }
        else if (int.TryParse(s.TrimEnd('%'), out var n))
        {
            var instance = d.Kind.Contains("thermostat") || d.Kind.Contains("ac") ? "temperature" : "brightness";
            action = new JsonObject { ["type"] = "devices.capabilities.range", ["state"] = new JsonObject { ["instance"] = instance, ["value"] = n } };
        }
        else throw new InvalidOperationException($"Непонятное состояние «{state}»");

        var body = new JsonObject { ["devices"] = new JsonArray { new JsonObject { ["id"] = d.Id, ["actions"] = new JsonArray { action } } } };
        using var c = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var r = await _http.PostAsync(Base + "/devices/actions", c, ct);
        r.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<(string Id, string Name)>> ScenesAsync(CancellationToken ct)
    {
        var info = _cache ?? await Info(ct);
        return (info["scenarios"]?.AsArray() ?? new JsonArray()).Select(s => (s!["id"]!.ToString(), s["name"]!.ToString())).ToList();
    }

    public async Task RunSceneAsync(string id, CancellationToken ct)
    {
        using var r = await _http.PostAsync($"{Base}/scenarios/{id}/actions", null, ct);
        r.EnsureSuccessStatusCode();
    }
}

/// <summary>Объединяет провайдеров и ищет устройство по нечёткому имени («свет в спальне»).</summary>
public sealed class SmartHomeHub
{
    public List<ISmartHome> Providers { get; } = new();

    public async Task<HomeDeviceInfo> FindAsync(string query, CancellationToken ct)
    {
        var all = new List<HomeDeviceInfo>();
        foreach (var p in Providers)
            try { all.AddRange(await p.DevicesAsync(ct)); } catch { /* провайдер недоступен */ }
        if (all.Count == 0) throw new InvalidOperationException("Умный дом не подключён или нет устройств");
        var q = Engine.PhraseNormalizer.Normalize(query);
        var best = all.Select(d => (d, score: Score(q, Engine.PhraseNormalizer.Normalize(d.Name + " " + d.Room))))
            .OrderByDescending(x => x.score).First();
        if (best.score < 0.5) throw new InvalidOperationException($"Не нашёл устройство «{query}»");
        return best.d;
    }

    private static double Score(string q, string name)
    {
        var qt = Engine.PhraseNormalizer.Tokens(q); var nt = Engine.PhraseNormalizer.Tokens(name);
        if (qt.Length == 0 || nt.Length == 0) return 0;
        return qt.Average(t => nt.Max(n => Engine.FuzzyMatcher.TokenSimilarity(t, n)));
    }

    public async Task SetAsync(string device, string state, CancellationToken ct)
    {
        var d = await FindAsync(device, ct);
        var p = Providers.First(x => x.Name == d.Provider);
        await p.SetAsync(d, state, ct);
    }

    public async Task RunSceneAsync(string scene, CancellationToken ct)
    {
        var q = Engine.PhraseNormalizer.Normalize(scene);
        foreach (var p in Providers)
        {
            IReadOnlyList<(string Id, string Name)> scenes;
            try { scenes = await p.ScenesAsync(ct); } catch { continue; }
            var hit = scenes.OrderByDescending(s => Engine.FuzzyMatcher.TokenSimilarity(q, Engine.PhraseNormalizer.Normalize(s.Name))).FirstOrDefault();
            if (hit.Id != null && Engine.FuzzyMatcher.TokenSimilarity(q, Engine.PhraseNormalizer.Normalize(hit.Name)) > 0.6)
            { await p.RunSceneAsync(hit.Id, ct); return; }
        }
        throw new InvalidOperationException($"Сценарий «{scene}» не найден");
    }
}
