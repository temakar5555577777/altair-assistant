using System.Diagnostics;
using System.Text;
using Altair.Core.Engine;
using static Altair.Windows.Native.Win32;

namespace Altair.Windows.Sys;

public sealed record WindowInfo(IntPtr Handle, string Title, string Process, int Pid, bool Minimized);

public static class WindowManager
{
    public static List<WindowInfo> List()
    {
        var list = new List<WindowInfo>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            if ((GetWindowLongW(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            int len = GetWindowTextLengthW(h);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowTextW(h, sb, sb.Capacity);
            GetWindowThreadProcessId(h, out var pid);
            string proc;
            try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { proc = "?"; }
            list.Add(new WindowInfo(h, sb.ToString(), proc, (int)pid, IsIconic(h)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>«active» — активное окно; иначе нечёткий поиск по заголовку и имени процесса.</summary>
    public static WindowInfo? Find(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Equals("active", StringComparison.OrdinalIgnoreCase) || query == "активное")
        {
            var h = GetForegroundWindow();
            return List().FirstOrDefault(w => w.Handle == h) ?? (h != IntPtr.Zero ? new WindowInfo(h, "", "", 0, false) : null);
        }
        var q = PhraseNormalizer.Normalize(Aliases.Resolve(query));
        return List()
            .Select(w => (w, score: Score(q, PhraseNormalizer.Normalize(w.Title + " " + w.Process))))
            .Where(x => x.score >= 0.6)
            .OrderByDescending(x => x.score)
            .Select(x => x.w)
            .FirstOrDefault();
    }

    private static double Score(string q, string target)
    {
        if (target.Contains(q)) return 1.0;
        var qt = PhraseNormalizer.Tokens(q); var tt = PhraseNormalizer.Tokens(target);
        if (qt.Length == 0 || tt.Length == 0) return 0;
        return qt.Average(a => tt.Max(b => FuzzyMatcher.TokenSimilarity(a, b)));
    }

    /// <summary>Надёжная активация окна (обход ограничения SetForegroundWindow через AttachThreadInput).</summary>
    public static void Focus(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        var fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint cur = GetCurrentThreadId();
        if (fgThread != cur) AttachThreadInput(cur, fgThread, true);
        BringWindowToTop(h);
        SetForegroundWindow(h);
        if (fgThread != cur) AttachThreadInput(cur, fgThread, false);
    }

    public static void Minimize(IntPtr h) => ShowWindow(h, SW_MINIMIZE);
    public static void Maximize(IntPtr h) => ShowWindow(h, SW_MAXIMIZE);
    public static void Close(IntPtr h) => PostMessageW(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    public static RECT WorkArea(IntPtr h)
    {
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST), ref mi);
        return mi.rcWork;
    }

    public static void Place(IntPtr h, int x, int y, int w, int hgt)
    {
        ShowWindow(h, SW_RESTORE);
        SetWindowPos(h, IntPtr.Zero, x, y, w, hgt, SWP_NOZORDER);
    }

    public static void Snap(IntPtr h, string side)
    {
        var a = WorkArea(h);
        int w = a.Width, hh = a.Height;
        switch (side.ToLowerInvariant())
        {
            case "left" or "влево" or "слева": Place(h, a.Left, a.Top, w / 2, hh); break;
            case "right" or "вправо" or "справа": Place(h, a.Left + w / 2, a.Top, w - w / 2, hh); break;
            case "top" or "вверх" or "сверху": Place(h, a.Left, a.Top, w, hh / 2); break;
            case "bottom" or "вниз" or "снизу": Place(h, a.Left, a.Top + hh / 2, w, hh - hh / 2); break;
            case "center" or "центр": Place(h, a.Left + w / 6, a.Top + hh / 8, w * 2 / 3, hh * 3 / 4); break;
            default: throw new ArgumentException($"Неизвестная сторона «{side}»");
        }
    }

    public static void MinimizeAll() => Altair.Windows.Input.InputSimulator.Combo("Win+D");
}

/// <summary>Разговорные имена программ → исполняемые файлы / протоколы.</summary>
public static class Aliases
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["браузер"] = "browser", ["хром"] = "chrome", ["гугл хром"] = "chrome", ["яндекс браузер"] = "browser.exe",
        ["эдж"] = "msedge", ["опера"] = "opera", ["фаерфокс"] = "firefox",
        ["телеграм"] = "telegram", ["дискорд"] = "discord", ["стим"] = "steam", ["спотифай"] = "spotify",
        ["блокнот"] = "notepad", ["калькулятор"] = "calc", ["проводник"] = "explorer", ["пейнт"] = "mspaint",
        ["ворд"] = "winword", ["эксель"] = "excel", ["повер поинт"] = "powerpnt", ["код"] = "code", ["обс"] = "obs64",
        ["терминал"] = "wt", ["диспетчер задач"] = "taskmgr", ["панель управления"] = "control",
    };

    public static string Resolve(string name) => Map.TryGetValue(name.Trim(), out var v) ? v : name;
}
