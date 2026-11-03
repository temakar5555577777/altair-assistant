using System.Speech.Synthesis;
using Altair.Core.AI;
using Altair.Core.Engine;
using Altair.Core.Storage;
using Altair.Windows.Audio;

namespace Altair.Windows.Tts;

/// <summary>
/// Озвучка: голоса Windows (офлайн, бесплатно), OpenAI TTS и Fish Audio (свой ключ, в т.ч. свой клонированный голос),
/// плюс свои аудиофайлы-ответы. Кэширует облачные фразы на диске — повторные ответы бесплатны и мгновенны.
/// </summary>
public sealed class TtsService : ISpeaker, IDisposable
{
    private readonly Func<AppSettings> _settings;
    private readonly AudioPlayer _player = new();
    private readonly VolumeControl? _volume;
    private SpeechSynthesizer? _synth;
    private CancellationTokenSource? _cts;
    public event Action<string>? Speaking;
    public event Action? Finished;
    /// <summary>Пока говорим — распознавание на паузе (чтобы не слышать себя).</summary>
    public event Action<bool>? SpeakingChanged;

    public TtsService(Func<AppSettings> settings, VolumeControl? volume = null) { _settings = settings; _volume = volume; }

    public static IReadOnlyList<string> WindowsVoices()
    {
        using var s = new SpeechSynthesizer();
        return s.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name).ToList();
    }

    public async Task SayAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        StopSpeaking();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        var s = _settings();
        Speaking?.Invoke(text);
        SpeakingChanged?.Invoke(true);
        _volume?.Duck(s.DuckingPercent);
        try
        {
            switch (s.Tts)
            {
                case TtsEngine.OpenAI:
                    var p = s.Provider("openai");
                    if (p == null || string.IsNullOrEmpty(p.ApiKey)) goto default;
                    await PlayCloud(text, "openai-" + s.OpenAiVoice, () => new OpenAiAudio(p.BaseUrl, p.ApiKey).SpeakAsync(text, s.OpenAiVoice, ct: token), token);
                    break;
                case TtsEngine.FishAudio:
                    if (string.IsNullOrEmpty(s.FishAudioKey) || string.IsNullOrEmpty(s.FishAudioVoiceId)) goto default;
                    await PlayCloud(text, "fish-" + s.FishAudioVoiceId, () => new FishAudio(s.FishAudioKey).SpeakAsync(text, s.FishAudioVoiceId, token), token);
                    break;
                case TtsEngine.CustomAudioOnly:
                    break; // только свои аудиофайлы в ответах команд
                default:
                    await SpeakWindows(text, s, token);
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (AiException) when (s.Tts != TtsEngine.Windows)
        {
            // Облако недоступно — не молчим, говорим голосом Windows
            await SpeakWindows(text, s, token);
        }
        finally
        {
            _volume?.Unduck();
            SpeakingChanged?.Invoke(false);
            Finished?.Invoke();
        }
    }

    private async Task PlayCloud(string text, string voiceKey, Func<Task<byte[]>> synth, CancellationToken ct)
    {
        var dir = Path.Combine(AppPaths.Voices, "cache");
        Directory.CreateDirectory(dir);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(voiceKey + "|" + text)))[..16];
        var file = Path.Combine(dir, hash + ".mp3");
        if (!File.Exists(file)) await File.WriteAllBytesAsync(file, await synth(), ct);
        await _player.PlayAsync(File.OpenRead(file), true, ct);
    }

    private Task SpeakWindows(string text, AppSettings s, CancellationToken ct)
    {
        _synth ??= new SpeechSynthesizer();
        _synth.SetOutputToDefaultAudioDevice();
        if (!string.IsNullOrEmpty(s.WindowsVoice)) { try { _synth.SelectVoice(s.WindowsVoice); } catch { } }
        else
        {
            var ru = _synth.GetInstalledVoices().FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == s.Language);
            if (ru != null) _synth.SelectVoice(ru.VoiceInfo.Name);
        }
        _synth.Rate = Math.Clamp(s.TtsRate, -10, 10);
        _synth.Volume = Math.Clamp(s.TtsVolume, 0, 100);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Done(object? _, SpeakCompletedEventArgs e) { _synth!.SpeakCompleted -= Done; tcs.TrySetResult(); }
        _synth.SpeakCompleted += Done;
        _synth.SpeakAsync(text);
        ct.Register(() => { try { _synth?.SpeakAsyncCancelAll(); } catch { } tcs.TrySetResult(); });
        return tcs.Task;
    }

    public Task PlayAsync(string file, CancellationToken ct = default) => _player.PlayFileAsync(file, ct);

    public void StopSpeaking()
    {
        _cts?.Cancel();
        _player.Stop();
        try { _synth?.SpeakAsyncCancelAll(); } catch { }
    }

    public void Dispose() { StopSpeaking(); _synth?.Dispose(); }
}
