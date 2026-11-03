using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using Altair.Windows.Audio;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using static Altair.Windows.Native.Win32;

namespace Altair.Windows.Sys;

/// <summary>
/// Глобальный низкоуровневый хук клавиатуры: горячие клавиши без регистрации (в т.ч. «двойной Right Ctrl», удержание F9),
/// поток символов для «Быстрых вставок». Должен создаваться в потоке с циклом сообщений (UI-поток).
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private readonly LowLevelProc _proc;
    private IntPtr _hook;
    private readonly HashSet<uint> _down = new();
    private readonly List<(Hotkey Key, Action Down, Action? Up)> _bindings = new();
    private DateTime _lastTap = DateTime.MinValue; private uint _lastTapVk;

    public event Action<char>? Char;
    /// <summary>Сырые нажатия (для записи макросов): vk, нажата/отпущена.</summary>
    public event Action<uint, bool>? RawKey;
    public bool SuppressBound { get; set; } = true;

    public KeyboardHook()
    {
        _proc = Callback;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero) throw new InvalidOperationException("Не удалось установить хук клавиатуры: " + Marshal.GetLastWin32Error());
    }

    public void Bind(string spec, Action down, Action? up = null) => _bindings.Add((Hotkey.Parse(spec), down, up));
    public void ClearBindings() => _bindings.Clear();

    private IntPtr Callback(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(l);
            bool injected = (k.flags & LLKHF_INJECTED) != 0; // наш же SendInput — игнорируем
            int msg = (int)w;
            bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool isUp = msg is WM_KEYUP or WM_SYSKEYUP;
            if (!injected)
            {
                bool repeat = isDown && _down.Contains(k.vkCode);
                if (!repeat && (isDown || isUp)) RawKey?.Invoke(k.vkCode, isDown);
                if (isDown) _down.Add(k.vkCode); else if (isUp) _down.Remove(k.vkCode);
                bool handled = false;
                foreach (var (key, down, up) in _bindings)
                {
                    if (key.Vk != k.vkCode) continue;
                    if (key.DoubleTap)
                    {
                        if (isUp && _down.Count == 0)
                        {
                            var now = DateTime.Now;
                            if (_lastTapVk == k.vkCode && (now - _lastTap).TotalMilliseconds < 400) { _lastTap = DateTime.MinValue; Post(down); }
                            else { _lastTap = now; _lastTapVk = k.vkCode; }
                        }
                        continue;
                    }
                    if (!ModsMatch(key)) continue;
                    if (isDown && !repeat) { Post(down); handled = SuppressBound; }
                    else if (isDown && repeat) handled = SuppressBound;
                    else if (isUp && up != null) { Post(up); }
                }
                if (handled) return (IntPtr)1;
                if (isDown && Char != null) { var c = ToChar(k); if (c != null) Char(c.Value); }
            }
        }
        return CallNextHookEx(_hook, code, w, l);
    }

    private static void Post(Action a) => ThreadPool.QueueUserWorkItem(_ => { try { a(); } catch { } });

    private bool ModsMatch(Hotkey h)
    {
        bool ctrl = _down.Contains(0xA2) || _down.Contains(0xA3) || _down.Contains(0x11);
        bool alt = _down.Contains(0xA4) || _down.Contains(0xA5) || _down.Contains(0x12);
        bool shift = _down.Contains(0xA0) || _down.Contains(0xA1) || _down.Contains(0x10);
        bool win = _down.Contains(0x5B) || _down.Contains(0x5C);
        return ctrl == h.Ctrl && alt == h.Alt && shift == h.Shift && win == h.Win;
    }

    private static char? ToChar(KBDLLHOOKSTRUCT k)
    {
        var state = new byte[256];
        GetKeyboardState(state);
        foreach (var vk in new[] { 0x10, 0xA0, 0xA1 }) if ((GetAsyncKeyState(vk) & 0x8000) != 0) state[vk] = 0x80;
        if ((GetAsyncKeyState(0x14) & 1) != 0) state[0x14] = 1;
        var fg = GetForegroundWindow();
        var hkl = GetKeyboardLayout(GetWindowThreadProcessId(fg, out _));
        var sb = new StringBuilder(4);
        int r = ToUnicodeEx(k.vkCode, k.scanCode, state, sb, sb.Capacity, 4, hkl); // флаг 4: не менять состояние мёртвых клавиш
        if (k.vkCode == 0x08) return '\b';
        return r == 1 ? sb[0] : null;
    }

    public void Dispose() { if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
}

public sealed record Hotkey(uint Vk, bool Ctrl, bool Alt, bool Shift, bool Win, bool DoubleTap)
{
    /// <summary>«Ctrl+Alt+W», «F9», «RightCtrl×2».</summary>
    public static Hotkey Parse(string spec)
    {
        bool dbl = spec.EndsWith("×2") || spec.EndsWith("x2", StringComparison.OrdinalIgnoreCase);
        var s = dbl ? spec[..^2] : spec;
        bool ctrl = false, alt = false, shift = false, win = false; uint vk = 0;
        foreach (var part in s.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control" when !dbl: ctrl = true; break;
                case "alt" when !dbl: alt = true; break;
                case "shift" when !dbl: shift = true; break;
                case "win" when !dbl: win = true; break;
                case "rightctrl" or "rctrl": vk = 0xA3; break;
                case "leftctrl" or "lctrl": vk = 0xA2; break;
                case "rightalt" or "ralt": vk = 0xA5; break;
                case "rightshift" or "rshift": vk = 0xA1; break;
                default: vk = Input.InputSimulator.ParseKey(part); break;
            }
        }
        if (vk == 0) throw new ArgumentException($"В сочетании «{spec}» нет основной клавиши");
        return new Hotkey(vk, ctrl, alt, shift, win, dbl);
    }
}

/// <summary>
/// Источник событий для триггеров: питание и батарея, блокировка, сеть и Wi-Fi, программы, аудиоустройства, отход от ПК.
/// </summary>
public sealed class SystemEventsWatcher : IDisposable, IMMNotificationClient
{
    public event Action<string, string?>? Event;
    private readonly System.Threading.Timer _timer;
    private HashSet<string> _procs = new(StringComparer.OrdinalIgnoreCase);
    private bool _online = NetworkInterface.GetIsNetworkAvailable();
    private bool? _plugged; private int _lastBattery = -1;
    private bool _away; private string? _ssid;
    private readonly VolumeControl? _volume;
    public int AwayMinutes { get; set; } = 5;

    public SystemEventsWatcher(VolumeControl? volume)
    {
        _volume = volume;
        SystemEvents.SessionSwitch += OnSession;
        SystemEvents.PowerModeChanged += OnPower;
        NetworkChange.NetworkAvailabilityChanged += (_, e) => Network(e.IsAvailable);
        try { _volume?.RegisterNotifications(this); } catch { }
        _procs = Snapshot();
        _timer = new System.Threading.Timer(_ => Poll(), null, 3000, 3000);
    }

    private void Raise(string e, string? arg = null) { try { Event?.Invoke(e, arg); } catch { } }

    private void OnSession(object? s, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) Raise(SystemEvents_.SessionLocked);
        else if (e.Reason == SessionSwitchReason.SessionUnlock) Raise(SystemEvents_.SessionUnlocked);
    }

    private void OnPower(object? s, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.StatusChange) PollPower(); }

    private void Network(bool available)
    {
        if (available == _online) return;
        _online = available;
        Raise(available ? SystemEvents_.InternetRestored : SystemEvents_.InternetLost);
    }

    private static HashSet<string> Snapshot()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses()) { try { set.Add(p.ProcessName); } catch { } finally { p.Dispose(); } }
        return set;
    }

    private int _tick;
    private void Poll()
    {
        try
        {
            var now = Snapshot();
            foreach (var p in now.Except(_procs)) Raise(SystemEvents_.AppStarted, p);
            foreach (var p in _procs.Except(now)) Raise(SystemEvents_.AppClosed, p);
            _procs = now;

            PollPower();

            var idle = Altair.Windows.Native.Win32.IdleTime();
            if (!_away && idle.TotalMinutes >= AwayMinutes) { _away = true; Raise(SystemEvents_.UserAway, ((int)idle.TotalMinutes).ToString()); }
            else if (_away && idle.TotalSeconds < 5) { _away = false; Raise(SystemEvents_.UserBack); }

            if (++_tick % 5 == 0) // Wi-Fi раз в 15 с
            {
                var ssid = CurrentSsid();
                if (ssid != null && ssid != _ssid) Raise(SystemEvents_.WifiConnected, ssid);
                _ssid = ssid;
                Network(NetworkInterface.GetIsNetworkAvailable());
            }
        }
        catch { }
    }

    private void PollPower()
    {
        var ps = System.Windows.Forms.SystemInformation.PowerStatus;
        bool plugged = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
        if (_plugged != null && plugged != _plugged) Raise(plugged ? SystemEvents_.PowerPlugged : SystemEvents_.PowerUnplugged);
        _plugged = plugged;
        if (ps.BatteryLifePercent <= 1f && ps.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)
        {
            int pct = (int)Math.Round(ps.BatteryLifePercent * 100);
            if (_lastBattery >= 0 && pct < _lastBattery && !plugged) Raise(SystemEvents_.BatteryBelow, pct.ToString());
            _lastBattery = pct;
        }
    }

    public static string? CurrentSsid()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", "wlan show interfaces") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd(); p.WaitForExit(3000);
            var line = output.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("SSID", StringComparison.OrdinalIgnoreCase) && !l.Contains("BSSID"));
            return line?.Split(':', 2).ElementAtOrDefault(1)?.Trim();
        }
        catch { return null; }
    }

    public static int? BatteryPercent()
    {
        var ps = System.Windows.Forms.SystemInformation.PowerStatus;
        return ps.BatteryChargeStatus == System.Windows.Forms.BatteryChargeStatus.NoSystemBattery ? null : (int)Math.Round(ps.BatteryLifePercent * 100);
    }

    // IMMNotificationClient — аудиоустройства
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow != DataFlow.Render || role != Role.Multimedia) return;
        string name = "";
        try { name = _volume?.DefaultOutputName() ?? ""; } catch { }
        Raise(SystemEvents_.AudioDeviceChanged, name);
        var lower = name.ToLowerInvariant();
        if (lower.Contains("наушник") || lower.Contains("headphone") || lower.Contains("headset") || lower.Contains("airpods") || lower.Contains("buds"))
            Raise(SystemEvents_.HeadphonesConnected, name);
        else Raise(SystemEvents_.HeadphonesDisconnected, name);
    }
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        _timer.Dispose();
        SystemEvents.SessionSwitch -= OnSession;
        SystemEvents.PowerModeChanged -= OnPower;
    }
}

/// <summary>Короткое имя, чтобы не путать с Microsoft.Win32.SystemEvents.</summary>
internal static class SystemEvents_
{
    public const string AppStarted = Altair.Core.Models.SystemEvents.AppStarted;
    public const string AppClosed = Altair.Core.Models.SystemEvents.AppClosed;
    public const string HeadphonesConnected = Altair.Core.Models.SystemEvents.HeadphonesConnected;
    public const string HeadphonesDisconnected = Altair.Core.Models.SystemEvents.HeadphonesDisconnected;
    public const string AudioDeviceChanged = Altair.Core.Models.SystemEvents.AudioDeviceChanged;
    public const string PowerPlugged = Altair.Core.Models.SystemEvents.PowerPlugged;
    public const string PowerUnplugged = Altair.Core.Models.SystemEvents.PowerUnplugged;
    public const string BatteryBelow = Altair.Core.Models.SystemEvents.BatteryBelow;
    public const string UserAway = Altair.Core.Models.SystemEvents.UserAway;
    public const string UserBack = Altair.Core.Models.SystemEvents.UserBack;
    public const string SessionLocked = Altair.Core.Models.SystemEvents.SessionLocked;
    public const string SessionUnlocked = Altair.Core.Models.SystemEvents.SessionUnlocked;
    public const string InternetLost = Altair.Core.Models.SystemEvents.InternetLost;
    public const string InternetRestored = Altair.Core.Models.SystemEvents.InternetRestored;
    public const string WifiConnected = Altair.Core.Models.SystemEvents.WifiConnected;
}
