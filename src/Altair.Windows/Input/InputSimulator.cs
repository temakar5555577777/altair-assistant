using System.Runtime.InteropServices;
using Altair.Windows.Native;
using static Altair.Windows.Native.Win32;

namespace Altair.Windows.Input;

/// <summary>Клавиатура и мышь через SendInput. Плавное движение курсора по кривой Безье (жалоба: «курсор телепортируется»).</summary>
public static class InputSimulator
{
    private static readonly Dictionary<string, ushort> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B, ["lwin"] = 0x5B, ["rwin"] = 0x5C,
        ["enter"] = 0x0D, ["return"] = 0x0D, ["esc"] = 0x1B, ["escape"] = 0x1B, ["tab"] = 0x09, ["space"] = 0x20, ["пробел"] = 0x20,
        ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["ins"] = 0x2D,
        ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pgup"] = 0x21, ["pagedown"] = 0x22, ["pgdn"] = 0x22,
        ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
        ["влево"] = 0x25, ["вверх"] = 0x26, ["вправо"] = 0x27, ["вниз"] = 0x28,
        ["printscreen"] = 0x2C, ["prtsc"] = 0x2C, ["pause"] = 0x13, ["capslock"] = 0x14, ["numlock"] = 0x90, ["scrolllock"] = 0x91,
        ["apps"] = 0x5D, ["menu"] = 0x5D,
        ["volumeup"] = 0xAF, ["volumedown"] = 0xAE, ["volumemute"] = 0xAD,
        ["medianext"] = 0xB0, ["mediaprev"] = 0xB1, ["mediastop"] = 0xB2, ["mediaplaypause"] = 0xB3,
        ["browserback"] = 0xA6, ["browserforward"] = 0xA7, ["browserrefresh"] = 0xA8,
        ["rctrl"] = 0xA3, ["lctrl"] = 0xA2, ["rshift"] = 0xA1, ["lshift"] = 0xA0, ["ralt"] = 0xA5, ["lalt"] = 0xA4,
        [";"] = 0xBA, ["="] = 0xBB, [","] = 0xBC, ["-"] = 0xBD, ["."] = 0xBE, ["/"] = 0xBF, ["`"] = 0xC0, ["["] = 0xDB, ["\\"] = 0xDC, ["]"] = 0xDD, ["'"] = 0xDE,
        ["plus"] = 0xBB, ["minus"] = 0xBD,
    };

    private static readonly HashSet<ushort> Extended = new() { 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0xA3, 0xA5, 0x6F, 0x90 };

    public static ushort ParseKey(string name)
    {
        name = name.Trim();
        if (Keys.TryGetValue(name, out var vk)) return vk;
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
            var scan = VkKeyScanW(name[0]);
            if (scan != -1) return (ushort)(scan & 0xFF);
        }
        if ((name[0] == 'F' || name[0] == 'f') && int.TryParse(name[1..], out var f) && f is >= 1 and <= 24) return (ushort)(0x70 + f - 1);
        if (name.StartsWith("num", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[3..], out var n) && n is >= 0 and <= 9) return (ushort)(0x60 + n);
        throw new ArgumentException($"Неизвестная клавиша «{name}»");
    }

    private static INPUT Key(ushort vk, bool up)
    {
        uint flags = up ? KEYEVENTF_KEYUP : 0;
        if (Extended.Contains(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKeyW(vk, 0), dwFlags = flags } } };
    }

    private static void Send(params INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            throw new InvalidOperationException("Windows заблокировал ввод (окно запущено от администратора или защищённый рабочий стол UAC).");
    }

    public static void Press(string key, int times = 1)
    {
        var vk = ParseKey(key);
        for (int i = 0; i < Math.Max(1, times); i++) { Send(Key(vk, false), Key(vk, true)); Thread.Sleep(15); }
    }

    /// <summary>«Ctrl+Shift+Esc», «Win+E», «Alt+F4».</summary>
    public static void Combo(string combo)
    {
        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (combo.EndsWith("++")) parts = parts.Append("plus").ToArray();
        var vks = parts.Select(ParseKey).ToArray();
        var list = new List<INPUT>();
        foreach (var vk in vks) list.Add(Key(vk, false));
        foreach (var vk in vks.Reverse()) list.Add(Key(vk, true));
        Send(list.ToArray());
    }

    public static async Task Hold(string key, int ms, CancellationToken ct)
    {
        var vk = ParseKey(key);
        Send(Key(vk, false));
        try { await Task.Delay(ms, ct); }
        finally { Send(Key(vk, true)); }
    }

    /// <summary>Печать Unicode-текста (любая раскладка). <paramref name="human"/> — случайные паузы как у человека.</summary>
    public static async Task Type(string text, bool human = false, CancellationToken ct = default)
    {
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            ct.ThrowIfCancellationRequested();
            if (ch == '\n') { Press("enter"); }
            else
            {
                var down = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } } };
                var up = down; up.U.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
                Send(down, up);
            }
            if (human) await Task.Delay(Random.Shared.Next(35, 140), ct);
            else if (text.Length > 200) await Task.Delay(1, ct);
        }
    }

    // ---------- Мышь ----------

    public static (int X, int Y) Cursor() { GetCursorPos(out var p); return (p.X, p.Y); }

    /// <summary>Плавное перемещение: кубическая кривая Безье со случайным изгибом и easing (ease-in-out).</summary>
    public static async Task MoveSmooth(int x, int y, int durationMs = 350, CancellationToken ct = default)
    {
        var (sx, sy) = Cursor();
        double dx = x - sx, dy = y - sy;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < 2 || durationMs <= 0) { SetCursorPos(x, y); return; }
        durationMs = (int)Math.Clamp(durationMs * Math.Min(1.0, 0.4 + dist / 1200), 80, 2000);

        // Контрольные точки — смещение перпендикулярно траектории (естественная дуга)
        double nx = -dy / dist, ny = dx / dist;
        double bend = (Random.Shared.NextDouble() - 0.5) * Math.Min(120, dist * 0.25);
        double c1x = sx + dx * 0.3 + nx * bend, c1y = sy + dy * 0.3 + ny * bend;
        double c2x = sx + dx * 0.7 + nx * bend * 0.5, c2y = sy + dy * 0.7 + ny * bend * 0.5;

        int steps = Math.Max(10, durationMs / 8);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 1; i <= steps; i++)
        {
            ct.ThrowIfCancellationRequested();
            double t = (double)i / steps;
            t = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; // easeInOutCubic
            double u = 1 - t;
            double px = u * u * u * sx + 3 * u * u * t * c1x + 3 * u * t * t * c2x + t * t * t * x;
            double py = u * u * u * sy + 3 * u * u * t * c1y + 3 * u * t * t * c2y + t * t * t * y;
            SetCursorPos((int)Math.Round(px), (int)Math.Round(py));
            var target = durationMs * i / steps;
            var wait = target - (int)sw.ElapsedMilliseconds;
            if (wait > 0) await Task.Delay(wait, ct);
        }
        SetCursorPos(x, y);
    }

    public static void Click(string button = "left", bool dbl = false)
    {
        (uint down, uint up) = button.ToLowerInvariant() switch
        {
            "right" or "правая" or "правой" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            "middle" or "средняя" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        };
        for (int i = 0; i < (dbl ? 2 : 1); i++)
        {
            Send(Mouse(down), Mouse(up));
            if (dbl) Thread.Sleep(60);
        }
    }

    public static void Scroll(int notches, bool horizontal = false) =>
        Send(new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { mouseData = notches * 120, dwFlags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL } } });

    public static async Task Drag(int x1, int y1, int x2, int y2, CancellationToken ct)
    {
        await MoveSmooth(x1, y1, 250, ct);
        Send(Mouse(MOUSEEVENTF_LEFTDOWN));
        try { await Task.Delay(60, ct); await MoveSmooth(x2, y2, 450, ct); }
        finally { Send(Mouse(MOUSEEVENTF_LEFTUP)); }
    }

    public static async Task ClickAt(int x, int y, bool smooth = true, string button = "left", CancellationToken ct = default)
    {
        if (smooth) await MoveSmooth(x, y, 300, ct); else SetCursorPos(x, y);
        await Task.Delay(40, ct);
        Click(button);
    }

    private static INPUT Mouse(uint flags) => new() { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } } };
}
