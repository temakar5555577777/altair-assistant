using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Altair.Windows.Audio;

/// <summary>Захват микрофона 16 кГц / 16 бит / моно — формат для Vosk и Whisper.</summary>
public sealed class MicCapture : IDisposable
{
    private WaveInEvent? _wave;
    public event Action<byte[], int>? Data;
    public event Action<float>? Level;
    public static readonly WaveFormat Format = new(16000, 16, 1);

    public static IReadOnlyList<(int Index, string Name)> Devices()
    {
        var list = new List<(int, string)>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++) list.Add((i, WaveInEvent.GetCapabilities(i).ProductName));
        return list;
    }

    public void Start(int deviceIndex = 0)
    {
        Stop();
        _wave = new WaveInEvent { DeviceNumber = Math.Clamp(deviceIndex, 0, Math.Max(0, WaveInEvent.DeviceCount - 1)), WaveFormat = Format, BufferMilliseconds = 50 };
        _wave.DataAvailable += (_, e) =>
        {
            Data?.Invoke(e.Buffer, e.BytesRecorded);
            if (Level != null)
            {
                double sum = 0; int n = e.BytesRecorded / 2;
                for (int i = 0; i < e.BytesRecorded - 1; i += 2) { short s = BitConverter.ToInt16(e.Buffer, i); sum += s * (double)s; }
                Level.Invoke(n == 0 ? 0 : (float)Math.Sqrt(sum / n) / 32768f);
            }
        };
        _wave.StartRecording();
    }

    public void Stop() { _wave?.StopRecording(); _wave?.Dispose(); _wave = null; }
    public void Dispose() => Stop();

    public static byte[] ToWav(byte[] pcm)
    {
        using var ms = new MemoryStream();
        using (var w = new WaveFileWriter(ms, Format)) w.Write(pcm, 0, pcm.Length);
        return ms.ToArray();
    }
}

/// <summary>Громкость, mute, приглушение других программ, переключение устройства вывода, микрофон.</summary>
public sealed class VolumeControl : IDisposable
{
    private readonly MMDeviceEnumerator _enum = new();
    private readonly Dictionary<uint, float> _ducked = new();

    private MMDevice Out => _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    private MMDevice Mic => _enum.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

    public int Get() => (int)Math.Round(Out.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
    public void Set(int percent) { var d = Out; d.AudioEndpointVolume.Mute = false; d.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(percent, 0, 100) / 100f; }
    public void Change(int delta) => Set(Get() + delta);
    public bool ToggleMute() { var v = Out.AudioEndpointVolume; v.Mute = !v.Mute; return v.Mute; }
    public bool ToggleMic() { var v = Mic.AudioEndpointVolume; v.Mute = !v.Mute; return v.Mute; }
    public string DefaultOutputName() => Out.FriendlyName;

    public IReadOnlyList<(string Id, string Name)> Outputs() =>
        _enum.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Select(d => (d.ID, d.FriendlyName)).ToList();

    /// <summary>Приглушить все остальные программы до N % (на время ответа ассистента).</summary>
    public void Duck(int toPercent)
    {
        if (toPercent >= 100) return;
        var sessions = Out.AudioSessionManager.Sessions;
        int self = Environment.ProcessId;
        for (int i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            if (s.GetProcessID == self || s.GetProcessID == 0) continue;
            if (_ducked.ContainsKey(s.GetProcessID)) continue;
            _ducked[s.GetProcessID] = s.SimpleAudioVolume.Volume;
            s.SimpleAudioVolume.Volume = s.SimpleAudioVolume.Volume * toPercent / 100f;
        }
    }

    public void Unduck()
    {
        if (_ducked.Count == 0) return;
        var sessions = Out.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            if (_ducked.TryGetValue(s.GetProcessID, out var v)) s.SimpleAudioVolume.Volume = v;
        }
        _ducked.Clear();
    }

    /// <summary>Сменить устройство вывода по части имени (недокументированный, но стабильный IPolicyConfig).</summary>
    public string SetOutput(string namePart)
    {
        var dev = _enum.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .FirstOrDefault(d => d.FriendlyName.Contains(namePart, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Устройство «{namePart}» не найдено");
        var cfg = (IPolicyConfig)new CPolicyConfigClient();
        foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications })
            Marshal.ThrowExceptionForHR(cfg.SetDefaultEndpoint(dev.ID, role));
        return dev.FriendlyName;
    }

    public void RegisterNotifications(IMMNotificationClient client) => _enum.RegisterEndpointNotificationCallback(client);

    public void Dispose() { Unduck(); _enum.Dispose(); }

    private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class CPolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        [PreserveSig] int SetEndpointVisibility();
    }
}

/// <summary>Воспроизведение mp3/wav (облачные голоса, свои звуки ответов). Прерывается командой «стоп».</summary>
public sealed class AudioPlayer
{
    private WaveOutEvent? _out;
    private readonly object _gate = new();

    public async Task PlayAsync(Stream audio, bool mp3, CancellationToken ct = default)
    {
        Stop();
        WaveStream reader = mp3 ? new Mp3FileReader(audio) : new WaveFileReader(audio);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new WaveOutEvent();
        lock (_gate) _out = output;
        output.PlaybackStopped += (_, _) => tcs.TrySetResult();
        output.Init(reader);
        output.Play();
        using var reg = ct.Register(Stop);
        try { await tcs.Task; }
        finally
        {
            lock (_gate) if (_out == output) _out = null;
            output.Dispose(); reader.Dispose();
        }
    }

    public Task PlayFileAsync(string path, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".mp3") return PlayAsync(File.OpenRead(path), true, ct);
        if (ext is ".wav") return PlayAsync(File.OpenRead(path), false, ct);
        return PlayAnyAsync(path, ct);
    }

    private async Task PlayAnyAsync(string path, CancellationToken ct)
    {
        // m4a/ogg и прочее — через Media Foundation
        Stop();
        using var reader = new MediaFoundationReader(path);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var output = new WaveOutEvent();
        lock (_gate) _out = output;
        output.PlaybackStopped += (_, _) => tcs.TrySetResult();
        output.Init(reader); output.Play();
        using var reg = ct.Register(Stop);
        await tcs.Task;
    }

    public void Stop() { lock (_gate) { try { _out?.Stop(); } catch { } } }
}
