using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Altair.Core.Agent;
using Altair.Core.AI;
using Altair.Core.Engine;
using Altair.Core.Integrations;
using Altair.Core.Models;
using Altair.Core.Storage;
using Altair.Windows.Audio;
using Altair.Windows.Browser;
using Altair.Windows.Input;
using Altair.Windows.Screen;
using Altair.Windows.Sys;
using static Altair.Windows.Native.Win32;
using A = Altair.Core.Models.ActionTypes;

namespace Altair.Windows.Actions;

/// <summary>Сервисы платформы, которые нужны действиям. Заполняется приложением.</summary>
public sealed class PlatformServices
{
    public required Func<AppSettings> Settings { get; init; }
    public required VolumeControl Volume { get; init; }
    public required ISpeaker Speaker { get; init; }
    public required ChatService Chat { get; init; }
    public required BrowserBridge Browser { get; init; }
    public SmartHomeHub Home { get; } = new();
    public Dictionary<string, McpClient> Mcp { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ObsClient Obs { get; } = new();
    public TelegramBot? Telegram { get; set; }
    public Func<string, Task<string>>? RunAgent { get; set; }
    public Action<string, string>? Notify { get; set; }
    public Action<string>? ShowText { get; set; }
    public Func<string, IReadOnlyDictionary<string, string>, Task<bool>>? Confirm { get; set; }
    public Func<Task>? StartDictation { get; set; }
    public Func<string, Task>? Layout { get; set; }
}

/// <summary>Выполнение кода в STA-потоке (буфер обмена Windows требует STA).</summary>
public static class Sta
{
    public static Task<T> Run<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>();
        var t = new Thread(() => { try { tcs.SetResult(f()); } catch (Exception ex) { tcs.SetException(ex); } });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        return tcs.Task;
    }

    public static Task Run(Action a) => Run(() => { a(); return true; });

    public static Task<string> GetClipboard() => Run(() => System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "");
    public static Task SetClipboard(string text) => Run(() => System.Windows.Clipboard.SetDataObject(text, true));
}

public static class WindowsActions
{
    private static void Out(RunContext c, ActionStep s, string value)
    {
        var v = s.Arg("var");
        if (!string.IsNullOrEmpty(v)) c.Vars.Set(v, value);
    }

    private static IntPtr Window(RunContext c, ActionStep s)
    {
        var q = c.Arg(s, "window", "active");
        return WindowManager.Find(q)?.Handle ?? throw new InvalidOperationException($"Окно «{q}» не найдено");
    }

    /// <summary>Выделенный текст: Ctrl+C с сохранением буфера.</summary>
    public static async Task<string> GetSelectedText()
    {
        var old = await Sta.GetClipboard();
        await Sta.SetClipboard("");
        InputSimulator.Combo("Ctrl+C");
        await Task.Delay(180);
        var sel = await Sta.GetClipboard();
        if (!string.IsNullOrEmpty(old)) await Sta.SetClipboard(old);
        return sel;
    }

    public static async Task ReplaceSelection(string text)
    {
        var old = await Sta.GetClipboard();
        await Sta.SetClipboard(text);
        InputSimulator.Combo("Ctrl+V");
        await Task.Delay(250);
        if (!string.IsNullOrEmpty(old)) await Sta.SetClipboard(old);
    }

    public static void RegisterAll(ActionRegistry r, PlatformServices p)
    {
        // ---------- Запуск ----------
        r.Register(A.OpenApp, (s, c) => { Shell.Open(c.Arg(s, "path"), c.Arg(s, "args")); return Task.CompletedTask; });
        r.Register(A.OpenUrl, (s, c) => { Shell.OpenUrl(c.Arg(s, "url").Replace(" ", "+")); return Task.CompletedTask; });
        r.Register(A.OpenFile, (s, c) => { Shell.Open(c.Arg(s, "path")); return Task.CompletedTask; });
        r.Register(A.OpenFolder, (s, c) => { Shell.Open(c.Arg(s, "path")); return Task.CompletedTask; });
        r.Register(A.CloseApp, (s, c) => { KillByName(Aliases.Resolve(c.Arg(s, "process")), graceful: true); return Task.CompletedTask; });
        r.Register(A.WebSearch, (s, c) =>
        {
            var q = Uri.EscapeDataString(c.Arg(s, "query", "{text}"));
            var url = c.Arg(s, "engine", "yandex").ToLowerInvariant() switch
            {
                "google" => "https://www.google.com/search?q=" + q,
                "youtube" => "https://www.youtube.com/results?search_query=" + q,
                "bing" => "https://www.bing.com/search?q=" + q,
                _ => "https://yandex.ru/search/?text=" + q,
            };
            Shell.OpenUrl(url);
            return Task.CompletedTask;
        });

        // ---------- Клавиши ----------
        r.Register(A.KeyPress, (s, c) => { InputSimulator.Press(c.Arg(s, "key", "enter"), c.ArgInt(s, "times", 1)); return Task.CompletedTask; });
        r.Register(A.KeyCombo, (s, c) => { InputSimulator.Combo(c.Arg(s, "combo")); return Task.CompletedTask; });
        r.Register(A.KeyHold, (s, c) => InputSimulator.Hold(c.Arg(s, "key"), c.ArgInt(s, "ms", 1000), c.Token));
        r.Register(A.TypeText, (s, c) => InputSimulator.Type(c.Arg(s, "text"), c.ArgBool(s, "human"), c.Token));

        // ---------- Мышь ----------
        r.Register(A.MouseMove, async (s, c) =>
        {
            int x = c.ArgInt(s, "x", 0), y = c.ArgInt(s, "y", 0);
            if (c.ArgBool(s, "smooth", true)) await InputSimulator.MoveSmooth(x, y, c.ArgInt(s, "ms", 350), c.Token);
            else SetCursorPos(x, y);
        });
        r.Register(A.MouseClick, (s, c) => { InputSimulator.Click(c.Arg(s, "button", "left"), c.ArgBool(s, "double")); return Task.CompletedTask; });
        r.Register(A.MouseScroll, (s, c) => { InputSimulator.Scroll(c.ArgInt(s, "delta", -3)); return Task.CompletedTask; });
        r.Register(A.MouseDrag, (s, c) => InputSimulator.Drag(c.ArgInt(s, "x1", 0), c.ArgInt(s, "y1", 0), c.ArgInt(s, "x2", 0), c.ArgInt(s, "y2", 0), c.Token));

        // ---------- Экран ----------
        r.Register(A.ClickText, async (s, c) =>
        {
            var text = c.Arg(s, "text");
            var pt = await Ocr.FindAsync(text, p.Settings().Language) ?? throw new InvalidOperationException($"Текст «{text}» на экране не найден");
            await InputSimulator.ClickAt(pt.X, pt.Y, true, "left", c.Token);
        });
        r.Register(A.WaitForText, async (s, c) =>
        {
            var text = c.Arg(s, "text");
            var until = DateTime.Now.AddSeconds(c.ArgInt(s, "timeout", 10));
            while (DateTime.Now < until)
            {
                if (await Ocr.FindAsync(text, p.Settings().Language) != null) return;
                await Task.Delay(700, c.Token);
            }
            throw new TimeoutException($"Текст «{text}» не появился");
        });
        r.Register(A.ReadScreen, async (s, c) =>
        {
            using var bmp = ScreenCapture.Capture();
            var lines = await Ocr.RecognizeAsync(bmp, p.Settings().Language);
            var text = string.Join("\n", lines.Select(l => l.Text));
            c.Vars.Set(s.Arg("var", "screen"), text);
            Out(c, s, text);
        });
        r.Register(A.Screenshot, (s, c) =>
        {
            System.Drawing.Rectangle? region = null;
            if (c.Arg(s, "region", "full") == "window" && GetWindowRect(GetForegroundWindow(), out var wr))
                region = new System.Drawing.Rectangle(wr.Left, wr.Top, wr.Width, wr.Height);
            using var bmp = ScreenCapture.Capture(region);
            var path = ScreenCapture.SaveScreenshot(bmp);
            Sta.Run(() => System.Windows.Clipboard.SetImage(ToBitmapSource(bmp))).Wait();
            Out(c, s, path);
            p.Notify?.Invoke("Скриншот", "Сохранён и скопирован в буфер: " + Path.GetFileName(path));
            return Task.CompletedTask;
        });
        r.Register(A.ClickElement, async (s, c) =>
        {
            var name = c.Arg(s, "name");
            var item = UiAutomation.Find(GetForegroundWindow(), name) ?? throw new InvalidOperationException($"Элемент «{name}» не найден в активном окне");
            await UiAutomation.ActivateAsync(item, c.Token);
        });

        // ---------- Окна ----------
        r.Register(A.WindowFocus, (s, c) => { WindowManager.Focus(Window(c, s)); return Task.CompletedTask; });
        r.Register(A.WindowMinimize, (s, c) => { WindowManager.Minimize(Window(c, s)); return Task.CompletedTask; });
        r.Register(A.WindowMaximize, (s, c) => { WindowManager.Maximize(Window(c, s)); return Task.CompletedTask; });
        r.Register(A.WindowClose, (s, c) => { WindowManager.Close(Window(c, s)); return Task.CompletedTask; });
        r.Register(A.WindowSnap, (s, c) => { WindowManager.Snap(Window(c, s), c.Arg(s, "side", "left")); return Task.CompletedTask; });
        r.Register(A.MinimizeAll, (_, _) => { WindowManager.MinimizeAll(); return Task.CompletedTask; });
        r.Register(A.WindowLayout, (s, c) => p.Layout?.Invoke(c.Arg(s, "layout")) ?? Task.CompletedTask);

        // ---------- Озвучка ----------
        r.Register(A.Say, (s, c) => p.Speaker.SayAsync(c.Arg(s, "text"), c.Token));
        r.Register(A.PlaySound, (s, c) => p.Speaker.PlayAsync(Shell.ExpandPath(c.Arg(s, "file")), c.Token));
        r.Register(A.Notify, (s, c) => { p.Notify?.Invoke(c.Arg(s, "title", "Altair"), c.Arg(s, "text")); return Task.CompletedTask; });

        // ---------- Звук ----------
        r.Register(A.VolumeSet, (s, c) => { p.Volume.Set(c.ArgInt(s, "value", 50)); return Task.CompletedTask; });
        r.Register(A.VolumeUp, (s, c) => { p.Volume.Change(c.ArgInt(s, "step", 10)); return Task.CompletedTask; });
        r.Register(A.VolumeDown, (s, c) => { p.Volume.Change(-c.ArgInt(s, "step", 10)); return Task.CompletedTask; });
        r.Register(A.Mute, (_, _) => { p.Volume.ToggleMute(); return Task.CompletedTask; });
        r.Register(A.MediaPlayPause, (_, _) => { InputSimulator.Press("mediaplaypause"); return Task.CompletedTask; });
        r.Register(A.MediaNext, (_, _) => { InputSimulator.Press("medianext"); return Task.CompletedTask; });
        r.Register(A.MediaPrev, (_, _) => { InputSimulator.Press("mediaprev"); return Task.CompletedTask; });
        r.Register(A.AudioDevice, (s, c) => { var n = p.Volume.SetOutput(c.Arg(s, "name")); Out(c, s, n); return Task.CompletedTask; });
        r.Register(A.MicMute, async (_, _) => { var muted = p.Volume.ToggleMic(); await p.Speaker.SayAsync(muted ? "Микрофон выключен." : "Микрофон включён."); });

        // ---------- Система ----------
        r.Register(A.Shutdown, (s, c) => { Process.Start("shutdown", $"/s /t {c.ArgInt(s, "delay", 0)}")?.Dispose(); return Task.CompletedTask; });
        r.Register(A.Restart, (_, _) => { Process.Start("shutdown", "/r /t 5")?.Dispose(); return Task.CompletedTask; });
        r.Register(A.Sleep, (_, _) => { SetSuspendState(false, false, false); return Task.CompletedTask; });
        r.Register(A.Lock, (_, _) => { LockWorkStation(); return Task.CompletedTask; });
        r.Register(A.Brightness, async (s, c) =>
        {
            var v = Math.Clamp(c.ArgInt(s, "value", 50), 0, 100);
            var res = await Shell.PowerShell($"(Get-WmiObject -Namespace root/WMI -Class WmiMonitorBrightnessMethods).WmiSetBrightness(1,{v})", c.Token);
            if (res.ExitCode != 0) throw new InvalidOperationException("Яркость можно менять только на встроенном экране ноутбука");
        });
        r.Register(A.RunCmd, async (s, c) =>
        {
            var res = await Shell.Cmd(c.Arg(s, "script"), c.ArgBool(s, "keep"), c.Token);
            Out(c, s, res.Output);
        });
        r.Register(A.RunPowerShell, async (s, c) => Out(c, s, (await Shell.PowerShell(c.Arg(s, "script"), c.Token)).Output));
        r.Register(A.RunPython, async (s, c) =>
        {
            var res = await Shell.Python(c.Arg(s, "script"), c.Token);
            if (res.ExitCode != 0) throw new InvalidOperationException("Python: " + res.Output);
            Out(c, s, res.Output);
        });
        r.Register(A.KillProcess, (s, c) => { KillByName(c.Arg(s, "process"), graceful: false); return Task.CompletedTask; });
        r.Register(A.SystemInfo, async (s, c) =>
        {
            var text = await SystemSummary(c.Arg(s, "what", "all"));
            Out(c, s, text);
            if (string.IsNullOrEmpty(s.Arg("var"))) await p.Speaker.SayAsync(text, c.Token);
        });
        r.Register(A.EmptyRecycleBin, (_, _) => { SHEmptyRecycleBinW(IntPtr.Zero, null, 0x7); return Task.CompletedTask; });
        r.Register(A.PowerPlan, async (s, c) =>
        {
            var guid = c.Arg(s, "plan", "balanced").ToLowerInvariant() switch
            {
                "high" or "высокая" or "максимальная" => "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
                "saver" or "экономия" => "a1841308-3541-4fab-bc81-f71556f20b4a",
                _ => "381b4222-f694-41f0-9685-ff5bb260df2e",
            };
            await Shell.Run("powercfg", "/setactive " + guid, 10, c.Token);
        });

        // ---------- Браузер ----------
        r.Register(A.BrowserNewTab, async (s, c) =>
        {
            var url = c.Arg(s, "url");
            if (p.Browser.Connected) await p.Browser.NewTab(url, c.Token);
            else if (url.Length > 0) Shell.OpenUrl(url); else InputSimulator.Combo("Ctrl+T");
        });
        r.Register(A.BrowserCloseTab, async (_, c) => { if (p.Browser.Connected) await p.Browser.CloseTab(c.Token); else InputSimulator.Combo("Ctrl+W"); });
        r.Register(A.BrowserClick, (s, c) => p.Browser.Click(c.Arg(s, "label"), c.Token));
        r.Register(A.BrowserFill, (s, c) => p.Browser.Fill(c.Arg(s, "label"), c.Arg(s, "value"), c.Token));
        r.Register(A.BrowserReadPage, async (s, c) =>
        {
            var t = await p.Browser.ReadText(c.Token);
            c.Vars.Set(s.Arg("var", "page"), t); Out(c, s, t);
        });

        // ---------- Текст ----------
        r.Register(A.ClipboardSet, (s, c) => Sta.SetClipboard(c.Arg(s, "text")));
        r.Register(A.ClipboardGet, async (s, c) => { var t = await Sta.GetClipboard(); c.Vars.Set(s.Arg("var", "clipboard"), t); Out(c, s, t); });
        r.Register(A.Paste, (s, c) => ReplaceSelection(c.Arg(s, "text")));
        r.Register(A.Dictate, (_, _) => p.StartDictation?.Invoke() ?? Task.CompletedTask);
        r.Register(A.Translate, async (s, c) =>
        {
            var sel = await GetSelectedText();
            if (string.IsNullOrWhiteSpace(sel)) throw new InvalidOperationException("Выделите текст для перевода");
            var to = c.Arg(s, "to", "en");
            var res = await p.Chat.OneShot($"Переведи текст на язык «{to}». Верни только перевод, сохрани форматирование.", sel, ct: c.Token);
            await ReplaceSelection(res.Text);
        });

        // ---------- Файлы ----------
        r.Register(A.FileCopy, (s, c) => { CopyOrMove(c.Arg(s, "from"), c.Arg(s, "to"), move: false); return Task.CompletedTask; });
        r.Register(A.FileMove, (s, c) => { CopyOrMove(c.Arg(s, "from"), c.Arg(s, "to"), move: true); return Task.CompletedTask; });
        r.Register(A.FileDelete, (s, c) =>
        {
            var path = Shell.ExpandPath(c.Arg(s, "path"));
            if (Directory.Exists(path)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else if (File.Exists(path)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else throw new FileNotFoundException("Не найдено: " + path);
            return Task.CompletedTask;
        });
        r.Register(A.FileWrite, async (s, c) =>
        {
            var path = Shell.ExpandPath(c.Arg(s, "path"));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var text = c.Arg(s, "text") + Environment.NewLine;
            if (c.ArgBool(s, "append", true)) await File.AppendAllTextAsync(path, text, c.Token);
            else await File.WriteAllTextAsync(path, text, c.Token);
        });
        r.Register(A.FindFile, (s, c) =>
        {
            var name = c.Arg(s, "name");
            var pattern = name.Contains('*') || name.Contains('?') ? name : $"*{name}*";
            var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
            var found = roots.SelectMany(root => Directory.EnumerateFiles(root, pattern, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 }))
                .Take(20).ToList();
            var v = string.Join("\n", found);
            c.Vars.Set(s.Arg("var", "found"), v); Out(c, s, v);
            return Task.CompletedTask;
        });

        // ---------- Нейросеть ----------
        r.Register(A.AiAsk, async (s, c) =>
        {
            var answer = await p.Chat.Ask(c.Arg(s, "prompt", "{text}"), c.Token);
            c.Vars.Set(s.Arg("var", "ai"), answer); Out(c, s, answer);
            if (c.ArgBool(s, "say", true)) await p.Speaker.SayAsync(answer, c.Token);
        });
        r.Register(A.AiAgent, async (s, c) =>
        {
            if (p.RunAgent == null) throw new InvalidOperationException("ИИ-агент недоступен");
            var result = await p.RunAgent(c.Arg(s, "task", "{text}"));
            Out(c, s, result);
        });
        r.Register(A.AiSummarizeClipboard, async (_, c) =>
        {
            var t = await Sta.GetClipboard();
            if (string.IsNullOrWhiteSpace(t)) t = await GetSelectedText();
            if (string.IsNullOrWhiteSpace(t)) throw new InvalidOperationException("Скопируйте или выделите текст");
            var r2 = await p.Chat.OneShot("Кратко перескажи текст в 2–4 предложениях по-русски.", t, ct: c.Token);
            p.ShowText?.Invoke(r2.Text);
            await p.Speaker.SayAsync(r2.Text, c.Token);
        });

        // ---------- Умный дом ----------
        r.Register(A.HomeDevice, (s, c) => p.Home.SetAsync(c.Arg(s, "device"), c.Arg(s, "state", "toggle"), c.Token));
        r.Register(A.HomeScene, (s, c) => p.Home.RunSceneAsync(c.Arg(s, "scene"), c.Token));

        // ---------- Второй ПК ----------
        r.Register(A.RemoteRun, async (s, c) =>
        {
            var pc = FindPc(p.Settings(), c.Arg(s, "pc"));
            var resp = await RemotePcProtocol.CallAsync(pc.Host, pc.Port, pc.SharedKey, "run", c.Arg(s, "command"), c.Token);
            Out(c, s, resp["result"]?.ToString() ?? "");
        });
        r.Register(A.RemoteSay, async (s, c) =>
        {
            var pc = FindPc(p.Settings(), c.Arg(s, "pc"));
            await RemotePcProtocol.CallAsync(pc.Host, pc.Port, pc.SharedKey, "say", c.Arg(s, "text"), c.Token);
        });

        // ---------- Интеграции ----------
        r.Register(A.Telegram, (s, c) => p.Telegram?.Notify(c.Arg(s, "text"), c.Token) ?? throw new InvalidOperationException("Telegram-бот не настроен"));
        r.Register(A.HttpRequest, async (s, c) =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var method = new HttpMethod(c.Arg(s, "method", "GET").ToUpperInvariant());
            using var req = new HttpRequestMessage(method, c.Arg(s, "url"));
            var body = c.Arg(s, "body");
            if (body.Length > 0) req.Content = new StringContent(body, Encoding.UTF8, body.TrimStart().StartsWith('{') ? "application/json" : "text/plain");
            using var resp = await http.SendAsync(req, c.Token);
            var text = await resp.Content.ReadAsStringAsync(c.Token);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {text[..Math.Min(200, text.Length)]}");
            Out(c, s, text);
        });
        r.Register(A.McpTool, async (s, c) =>
        {
            var server = c.Arg(s, "server");
            if (!p.Mcp.TryGetValue(server, out var client)) throw new InvalidOperationException($"Подключение «{server}» не запущено");
            var args = JsonNode.Parse(c.Arg(s, "json", "{}")) as JsonObject ?? new JsonObject();
            Out(c, s, await client.CallToolAsync(c.Arg(s, "tool"), args, c.Token));
        });
        r.Register(A.ObsAction, async (s, c) =>
        {
            var st = p.Settings();
            if (!p.Obs.Connected) await p.Obs.ConnectAsync(st.ObsUrl, st.ObsPassword, c.Token);
            var value = c.Arg(s, "value");
            switch (c.Arg(s, "op").ToLowerInvariant())
            {
                case "scene": await p.Obs.SetScene(value, c.Token); break;
                case "record": await p.Obs.ToggleRecord(c.Token); break;
                case "stream": await p.Obs.ToggleStream(c.Token); break;
                case "mute": await p.Obs.ToggleMute(value.Length > 0 ? value : "Mic/Aux", c.Token); break;
                case "replay": await p.Obs.SaveReplay(c.Token); break;
                default: throw new InvalidOperationException("OBS: op = scene | record | stream | mute | replay");
            }
        });
    }

    private static RemotePcConfig FindPc(AppSettings s, string name) =>
        s.RemotePcs.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? s.RemotePcs.FirstOrDefault()
        ?? throw new InvalidOperationException("Второй компьютер не сопряжён");

    private static void KillByName(string name, bool graceful)
    {
        var n = Path.GetFileNameWithoutExtension(name);
        var procs = Process.GetProcessesByName(n);
        if (procs.Length == 0)
            procs = Process.GetProcesses().Where(pr => { try { return pr.ProcessName.Contains(n, StringComparison.OrdinalIgnoreCase); } catch { return false; } }).ToArray();
        if (procs.Length == 0) throw new InvalidOperationException($"Программа «{name}» не запущена");
        foreach (var pr in procs)
        {
            try
            {
                if (graceful && pr.MainWindowHandle != IntPtr.Zero) { pr.CloseMainWindow(); if (!pr.WaitForExit(3000)) pr.Kill(true); }
                else pr.Kill(true);
            }
            catch { }
            finally { pr.Dispose(); }
        }
    }

    private static void CopyOrMove(string from, string to, bool move)
    {
        from = Shell.ExpandPath(from); to = Shell.ExpandPath(to);
        if (Directory.Exists(to)) to = Path.Combine(to, Path.GetFileName(from));
        to = Altair.Core.Plugins.DownloadsCleaner.Unique(to);
        if (move) File.Move(from, to); else File.Copy(from, to);
    }

    public static async Task<string> SystemSummary(string what)
    {
        var parts = new List<string>();
        if (what is "all" or "cpu")
        {
            var r = await Shell.PowerShell("(Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average", CancellationToken.None);
            if (double.TryParse(r.Output.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var cpu)) parts.Add($"процессор загружен на {cpu:0}%");
        }
        if (what is "all" or "ram")
        {
            var info = new Microsoft.VisualBasic.Devices.ComputerInfo();
            double used = 1 - (double)info.AvailablePhysicalMemory / info.TotalPhysicalMemory;
            parts.Add($"память занята на {used * 100:0}%");
        }
        if (what is "all" or "battery")
        {
            var b = SystemEventsWatcher.BatteryPercent();
            if (b != null) parts.Add($"заряд {b}%");
        }
        if (what is "all" or "disk")
        {
            var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            parts.Add($"на диске {d.Name.TrimEnd('\\')} свободно {d.AvailableFreeSpace / 1_000_000_000.0:0} ГБ");
        }
        return parts.Count == 0 ? "Нет данных." : char.ToUpper(parts[0][0]) + string.Join(", ", parts)[1..] + ".";
    }

    private static System.Windows.Media.Imaging.BitmapSource ToBitmapSource(System.Drawing.Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var img = new System.Windows.Media.Imaging.BitmapImage();
        img.BeginInit(); img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; img.StreamSource = ms; img.EndInit();
        img.Freeze();
        return img;
    }
}

/// <summary>Окружение ИИ-агента на Windows: скриншот + OCR + элементы UI Automation + страница браузера.</summary>
public sealed class WindowsAgentEnvironment : IAgentEnvironment
{
    private readonly BrowserBridge _browser;
    private readonly Func<AppSettings> _settings;
    public WindowsAgentEnvironment(BrowserBridge browser, Func<AppSettings> settings) { _browser = browser; _settings = settings; }

    public async Task<Observation> ObserveScreenAsync(bool withImage, CancellationToken ct)
    {
        var fg = GetForegroundWindow();
        var win = WindowManager.List().FirstOrDefault(w => w.Handle == fg);
        var sb = new StringBuilder();
        sb.AppendLine($"Активное окно: «{win?.Title}» ({win?.Process})");
        var items = UiAutomation.Elements(fg, 120);
        if (items.Count > 0) { sb.AppendLine("Элементы окна (подпись @центр):"); sb.AppendLine(UiAutomation.Describe(items)); }

        using var bmp = ScreenCapture.Capture();
        try
        {
            var lines = await Ocr.RecognizeAsync(bmp, _settings().Language);
            var text = string.Join("\n", lines.Take(150).Select(l => $"{l.Text} @({l.Box.Left + l.Box.Width / 2},{l.Box.Top + l.Box.Height / 2})"));
            sb.AppendLine("Текст на экране (OCR):").AppendLine(text);
        }
        catch (Exception ex) { sb.AppendLine("OCR недоступен: " + ex.Message); }

        if (_browser.Connected && win != null && IsBrowser(win.Process))
        {
            try { sb.AppendLine(await _browser.Snapshot(ct)); } catch { }
        }

        ImagePart? img = withImage ? new ImagePart(ScreenCapture.JpegForAi(bmp), "image/jpeg") : null;
        return new Observation(sb.ToString(), img);
    }

    private static bool IsBrowser(string proc) =>
        proc.ToLowerInvariant() is "chrome" or "msedge" or "browser" or "opera" or "brave" or "vivaldi" or "yandex";

    public Task<string> ListWindowsAsync(CancellationToken ct) =>
        Task.FromResult(string.Join("\n", WindowManager.List().Select(w => $"«{w.Title}» — {w.Process}{(w.Minimized ? " (свёрнуто)" : "")}")));

    public async Task<string?> ReadBrowserPageAsync(CancellationToken ct) => _browser.Connected ? await _browser.Snapshot(ct) : null;
}
