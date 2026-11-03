using System.Text;
using System.Text.Json.Nodes;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Vosk;

namespace Altair.Windows.Speech;

/// <summary>
/// Распознавание звука, который играет компьютер (WASAPI loopback): «Живые субтитры» и «Конспект видео и созвонов».
/// Работает офлайн на той же модели Vosk.
/// </summary>
public sealed class LoopbackTranscriber : IDisposable
{
    private WasapiLoopbackCapture? _cap;
    private VoskRecognizer? _rec;
    private BufferedWaveProvider? _buffer;
    private ISampleProvider? _resampled;
    private readonly StringBuilder _transcript = new();
    private CancellationTokenSource? _cts;

    public event Action<string>? Partial;
    public event Action<string>? Final;
    public bool Running => _cap != null;
    public string Transcript { get { lock (_transcript) return _transcript.ToString(); } }

    public void Start(Model model)
    {
        Stop();
        lock (_transcript) _transcript.Clear();
        _rec = new VoskRecognizer(model, 16000f);
        _cap = new WasapiLoopbackCapture();
        _buffer = new BufferedWaveProvider(_cap.WaveFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(10) };
        ISampleProvider sp = _buffer.ToSampleProvider();
        if (sp.WaveFormat.Channels == 2) sp = sp.ToMono();
        else if (sp.WaveFormat.Channels > 2) { var mux = new MultiplexingSampleProvider(new[] { sp }, 1); mux.ConnectInputToOutput(0, 0); sp = mux; }
        _resampled = new WdlResamplingSampleProvider(sp, 16000);
        _cap.DataAvailable += (_, e) => _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        _cap.StartRecording();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => Pump(ct));
    }

    private async Task Pump(CancellationToken ct)
    {
        var floats = new float[1600]; // 100 мс
        var bytes = new byte[floats.Length * 2];
        while (!ct.IsCancellationRequested)
        {
            int n = _buffer!.BufferedBytes > 0 ? _resampled!.Read(floats, 0, floats.Length) : 0;
            if (n == 0) { await Task.Delay(50, ct).ContinueWith(_ => { }); continue; }
            for (int i = 0; i < n; i++)
            {
                short v = (short)Math.Clamp(floats[i] * 32767f, short.MinValue, short.MaxValue);
                bytes[2 * i] = (byte)(v & 0xFF); bytes[2 * i + 1] = (byte)((v >> 8) & 0xFF);
            }
            if (_rec!.AcceptWaveform(bytes, n * 2))
            {
                var text = JsonNode.Parse(_rec.Result())?["text"]?.ToString();
                if (!string.IsNullOrEmpty(text))
                {
                    lock (_transcript) _transcript.AppendLine(text);
                    Final?.Invoke(text);
                }
            }
            else
            {
                var p = JsonNode.Parse(_rec.PartialResult())?["partial"]?.ToString();
                if (!string.IsNullOrEmpty(p)) Partial?.Invoke(p);
            }
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _cap?.StopRecording(); } catch { }
        _cap?.Dispose(); _cap = null;
        _rec?.Dispose(); _rec = null;
    }

    public void Dispose() => Stop();
}
