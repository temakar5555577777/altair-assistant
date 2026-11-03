using System.IO.Compression;
using System.Text.Json.Nodes;
using Altair.Core.AI;
using Altair.Core.Storage;
using Altair.Windows.Audio;
using Vosk;

namespace Altair.Windows.Speech;

/// <summary>Загрузка моделей Vosk при первом запуске (alphacephei.com), с прогрессом.</summary>
public static class ModelManager
{
    public const string BaseUrl = "https://alphacephei.com/vosk/models/";
    public const string SpeakerModel = "vosk-model-spk-0.4";

    public static string PathOf(string name) => Path.Combine(AppPaths.Models, name);
    public static bool Has(string name) => Directory.Exists(PathOf(name)) && Directory.EnumerateFileSystemEntries(PathOf(name)).Any();

    public static async Task EnsureAsync(string name, IProgress<double>? progress, CancellationToken ct)
    {
        if (Has(name)) return;
        Directory.CreateDirectory(AppPaths.Models);
        var zip = Path.Combine(AppPaths.Models, name + ".zip");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        using (var resp = await http.GetAsync(BaseUrl + name + ".zip", HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zip);
            var buf = new byte[1 << 16]; long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        var tmp = PathOf(name) + ".extract";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        ZipFile.ExtractToDirectory(zip, tmp);
        // В архиве есть корневая папка с именем модели
        var inner = Directory.GetDirectories(tmp).FirstOrDefault(d => File.Exists(Path.Combine(d, "conf", "model.conf")) || Directory.Exists(Path.Combine(d, "am")) || File.Exists(Path.Combine(d, "mfcc.conf"))) ?? tmp;
        Directory.Move(inner, PathOf(name));
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        File.Delete(zip);
    }
}

public sealed record Utterance(string Text, byte[] Pcm, float[]? SpeakerVector, double Confidence);

/// <summary>
/// Офлайн-распознавание Vosk с сегментацией речи. Режимы:
/// локальный; «повышенная точность» (вторая, большая модель перепроверяет фразу); облачный Whisper (Vosk только режет фразы).
/// </summary>
public sealed class SpeechService : IDisposable
{
    private readonly Func<AppSettings> _settings;
    private readonly MicCapture _mic = new();
    private Model? _model, _accurate;
    private SpkModel? _spk;
    private VoskRecognizer? _rec;
    private readonly MemoryStream _pcm = new();
    private readonly object _gate = new();
    private bool _paused, _pushToTalk;

    public event Action<string>? Partial;
    public event Action<Utterance>? Recognized;
    public event Action<float>? Level;
    public event Action<string>? Log;
    /// <summary>Сырой звук микрофона (для Snapse и записи голосового профиля).</summary>
    public event Action<byte[], int>? Audio;

    /// <summary>Подсказка для облачного распознавания (фразы команд повышают точность).</summary>
    public Func<string>? VocabularyHint { get; set; }

    public SpeechService(Func<AppSettings> settings) => _settings = settings;

    public bool Running { get; private set; }
    public Model? MainModel => _model;
    public bool Paused => _paused;
    /// <summary>Микрофон выключен пользователем (не путать с паузой на время ответа).</summary>
    public bool UserMuted { get; set; }

    public async Task StartAsync(IProgress<(string, double)>? progress, CancellationToken ct)
    {
        Vosk.Vosk.SetLogLevel(-1);
        var s = _settings();
        var main = s.Language == "en" ? s.VoskModelEn : s.VoskModelRu;
        await ModelManager.EnsureAsync(main, new Progress<double>(p => progress?.Report(("Модель распознавания", p))), ct);
        _model = new Model(ModelManager.PathOf(main));

        if (s.Stt == SttEngine.VoskLocalAccurate)
        {
            await ModelManager.EnsureAsync(s.VoskModelAccurate, new Progress<double>(p => progress?.Report(("Модель повышенной точности", p))), ct);
            _accurate = new Model(ModelManager.PathOf(s.VoskModelAccurate));
        }
        if (s.OnlyMyVoice)
        {
            await ModelManager.EnsureAsync(ModelManager.SpeakerModel, new Progress<double>(p => progress?.Report(("Модель голоса", p))), ct);
            _spk = new SpkModel(ModelManager.PathOf(ModelManager.SpeakerModel));
        }

        ResetRecognizer();
        _mic.Data += OnData;
        _mic.Level += l => Level?.Invoke(l);
        int dev = 0;
        if (s.MicrophoneId != null && int.TryParse(s.MicrophoneId, out var d)) dev = d;
        _mic.Start(dev);
        Running = true;
        Log?.Invoke("Распознавание запущено: " + main);
    }

    private void ResetRecognizer()
    {
        if (_model == null) return; // распознавание не запущено (нет модели/микрофона)
        lock (_gate)
        {
            _rec?.Dispose();
            _rec = new VoskRecognizer(_model!, 16000f);
            _rec.SetMaxAlternatives(0);
            _rec.SetWords(false);
            if (_spk != null) _rec.SetSpkModel(_spk);
            _pcm.SetLength(0);
        }
    }

    public void Pause(bool paused) { _paused = paused; if (paused) ResetRecognizer(); }

    /// <summary>Клавиша записи: пока зажата — копим, отпустили — распознаём.</summary>
    public void PushToTalk(bool down)
    {
        if (down) { ResetRecognizer(); _pushToTalk = true; return; }
        if (!_pushToTalk || _rec == null) return;
        _pushToTalk = false;
        string json; byte[] pcm;
        lock (_gate) { json = _rec.FinalResult(); pcm = _pcm.ToArray(); }
        _ = EmitAsync(json, pcm, true);
        ResetRecognizer();
    }

    private void OnData(byte[] buf, int len)
    {
        if (UserMuted) return;
        Audio?.Invoke(buf, len);
        if (_paused) return;
        string? final = null, partial = null; byte[]? pcm = null;
        lock (_gate)
        {
            if (_rec == null) return;
            _pcm.Write(buf, 0, len);
            if (_pcm.Length > 16000 * 2 * 30) _pcm.SetLength(0); // не больше 30 с на фразу
            if (_pushToTalk) { _rec.AcceptWaveform(buf, len); return; }
            if (_rec.AcceptWaveform(buf, len))
            {
                final = _rec.Result();
                pcm = _pcm.ToArray(); _pcm.SetLength(0);
            }
            else partial = _rec.PartialResult();
        }
        if (final != null) _ = EmitAsync(final, pcm!, false);
        else if (partial != null)
        {
            var p = JsonNode.Parse(partial)?["partial"]?.ToString();
            if (!string.IsNullOrEmpty(p)) Partial?.Invoke(p);
        }
    }

    private async Task EmitAsync(string json, byte[] pcm, bool forced)
    {
        try
        {
            var node = JsonNode.Parse(json);
            var text = node?["text"]?.ToString() ?? "";
            float[]? spk = node?["spk"]?.AsArray().Select(x => x!.GetValue<float>()).ToArray();
            if (text.Length == 0 && !forced) return;
            if (pcm.Length < 16000 * 2 / 4) return; // < 0.25 с — шум

            var s = _settings();
            if (s.Stt == SttEngine.VoskLocalAccurate && _accurate != null)
                text = RecognizeWith(_accurate, pcm) is { Length: > 0 } better ? better : text;
            else if (s.Stt is SttEngine.CloudOpenAI or SttEngine.CloudCompatible)
            {
                var p = s.Provider("openai") ?? s.Provider();
                if (p != null && !string.IsNullOrEmpty(p.ApiKey))
                {
                    try
                    {
                        var audio = new OpenAiAudio(p.BaseUrl, p.ApiKey);
                        var cloud = await audio.TranscribeAsync(MicCapture.ToWav(pcm), s.Language, VocabularyHint?.Invoke());
                        if (cloud.Length > 0) text = cloud;
                    }
                    catch (Exception ex) { Log?.Invoke("Облачное распознавание недоступно, использую локальное: " + ex.Message); }
                }
            }
            if (text.Length == 0) return;
            Recognized?.Invoke(new Utterance(text, pcm, spk, 1));
        }
        catch (Exception ex) { Log?.Invoke("Распознавание: " + ex.Message); }
    }

    /// <summary>Распознать готовый PCM-буфер указанной моделью (перепроверка / диктовка).</summary>
    public static string RecognizeWith(Model model, byte[] pcm)
    {
        using var rec = new VoskRecognizer(model, 16000f);
        rec.AcceptWaveform(pcm, pcm.Length);
        return JsonNode.Parse(rec.FinalResult())?["text"]?.ToString() ?? "";
    }

    public void Dispose()
    {
        _mic.Dispose();
        lock (_gate) { _rec?.Dispose(); _rec = null; }
        _model?.Dispose(); _accurate?.Dispose(); _spk?.Dispose();
    }
}

/// <summary>«Только мой голос»: эталонные x-векторы (до 4 голосов), косинусная близость.</summary>
public sealed class VoiceId
{
    public List<float[]> Profiles { get; set; } = new();
    public double Threshold { get; set; } = 0.55;

    public static float[] Average(IEnumerable<float[]> vectors)
    {
        var list = vectors.ToList();
        var avg = new float[list[0].Length];
        foreach (var v in list) for (int i = 0; i < avg.Length; i++) avg[i] += v[i] / list.Count;
        return avg;
    }

    public static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb) + 1e-9);
    }

    public bool IsKnown(float[]? v) => Profiles.Count == 0 || (v != null && Profiles.Any(p => Cosine(p, v) >= Threshold));

    public void Save() => Json.Save(Path.Combine(AppPaths.Data, "voiceid.json"), Profiles);
    public static VoiceId Load() => new() { Profiles = Json.Load<List<float[]>>(Path.Combine(AppPaths.Data, "voiceid.json")) };
}

/// <summary>
/// Snapse — щелчки пальцами и хлопки как команды (1 / 2 / 3 щелчка). Детектор резкого короткого пика энергии
/// с высокой долей высоких частот (zero-crossing rate), без нейросети и интернета.
/// </summary>
public sealed class SnapDetector
{
    private readonly List<DateTime> _hits = new();
    private DateTime _lastPeak = DateTime.MinValue;
    private double _noise = 0.01;
    public double Sensitivity { get; set; } = 6.0; // во сколько раз пик выше фона
    public event Action<int>? Snaps;

    public void Feed(byte[] buf, int len, DateTime? now = null)
    {
        var t = now ?? DateTime.Now;
        int n = len / 2; if (n == 0) return;
        double sum = 0; int zc = 0; short prev = 0; double peak = 0;
        for (int i = 0; i < len - 1; i += 2)
        {
            short s = BitConverter.ToInt16(buf, i);
            double a = Math.Abs(s / 32768.0);
            sum += a * a; if (a > peak) peak = a;
            if ((s >= 0) != (prev >= 0)) zc++;
            prev = s;
        }
        double rms = Math.Sqrt(sum / n);
        double zcr = (double)zc / n;
        bool impulse = peak > 0.25 && peak > _noise * Sensitivity && zcr > 0.15 && peak / (rms + 1e-6) > 3.0;
        if (impulse && (t - _lastPeak).TotalMilliseconds > 120)
        {
            _lastPeak = t;
            _hits.Add(t);
        }
        else _noise = _noise * 0.98 + rms * 0.02;

        if (_hits.Count > 0 && (t - _hits[^1]).TotalMilliseconds > 700)
        {
            int count = _hits.Count;
            _hits.Clear();
            if (count <= 3) Snaps?.Invoke(count);
        }
    }
}
