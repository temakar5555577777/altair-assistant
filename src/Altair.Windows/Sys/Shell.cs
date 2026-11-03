using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace Altair.Windows.Sys;

public static class Shell
{
    public static string ExpandPath(string p) => Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'));

    /// <summary>Открыть программу/файл/папку/ссылку/протокол (steam://, whatsapp:). Имя без пути ищется в PATH, App Paths и меню «Пуск».</summary>
    public static void Open(string target, string? args = null)
    {
        target = ExpandPath(Aliases.Resolve(target));
        if (target.Equals("browser", StringComparison.OrdinalIgnoreCase)) { OpenUrl("https://ya.ru"); return; }

        string file = target;
        if (!target.Contains("://") && !target.EndsWith(':') && !File.Exists(target) && !Directory.Exists(target))
            file = ResolveExecutable(target) ?? target;

        Process.Start(new ProcessStartInfo { FileName = file, Arguments = args ?? "", UseShellExecute = true })?.Dispose();
    }

    public static void OpenUrl(string url)
    {
        if (!url.Contains("://")) url = "https://" + url;
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true })?.Dispose();
    }

    public static string? ResolveExecutable(string name)
    {
        var exe = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        // 1. App Paths
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var k = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
            if (k?.GetValue(null) is string p && File.Exists(ExpandPath(p))) return ExpandPath(p);
        }
        // 2. PATH
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(dir.Trim(), exe); if (File.Exists(p)) return p; } catch { }
        }
        // 3. Ярлыки меню «Пуск» по нечёткому имени
        var dirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };
        var q = Altair.Core.Engine.PhraseNormalizer.Normalize(name);
        string? best = null; double bestScore = 0;
        foreach (var d in dirs.Where(Directory.Exists))
        {
            foreach (var lnk in Directory.EnumerateFiles(d, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                var n = Altair.Core.Engine.PhraseNormalizer.Normalize(Path.GetFileNameWithoutExtension(lnk));
                double s = n == q ? 1 : n.Contains(q) ? 0.9 : Altair.Core.Engine.FuzzyMatcher.TokenSimilarity(n, q);
                if (s > bestScore) { bestScore = s; best = lnk; }
            }
        }
        return bestScore >= 0.75 ? best : null;
    }

    public sealed record Result(int ExitCode, string Output);

    public static async Task<Result> Run(string file, string args, int timeoutSec, CancellationToken ct, bool showWindow = false, string? stdin = null)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false, CreateNoWindow = !showWindow,
            RedirectStandardOutput = !showWindow, RedirectStandardError = !showWindow, RedirectStandardInput = stdin != null,
            StandardOutputEncoding = showWindow ? null : Encoding.UTF8, StandardErrorEncoding = showWindow ? null : Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить " + file);
        if (stdin != null) { await p.StandardInput.WriteAsync(stdin); p.StandardInput.Close(); }
        if (showWindow) return new Result(0, "");

        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw new TimeoutException($"Команда не завершилась за {timeoutSec} с"); }
        var output = (await outTask + await errTask).Trim();
        return new Result(p.ExitCode, output);
    }

    public static Task<Result> Cmd(string script, bool keepWindow, CancellationToken ct) =>
        keepWindow
            ? Run("cmd.exe", "/k chcp 65001>nul & " + script, 0, ct, showWindow: true)
            : Run("cmd.exe", "/c chcp 65001>nul & " + script, 120, ct);

    public static Task<Result> PowerShell(string script, CancellationToken ct)
    {
        var wrapped = "[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));
        return Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + encoded, 120, ct);
    }

    /// <summary>Python: код или путь к .py. Ищет python/py в системе.</summary>
    public static async Task<Result> Python(string scriptOrPath, CancellationToken ct)
    {
        var py = ResolveExecutable("python") ?? ResolveExecutable("py")
                 ?? throw new InvalidOperationException("Python не найден. Установите с python.org и отметьте «Add to PATH».");
        var path = ExpandPath(scriptOrPath);
        if (path.EndsWith(".py", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            return await Run(py, $"-X utf8 \"{path}\"", 300, ct);
        var tmp = Path.Combine(Path.GetTempPath(), $"altair_{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(tmp, scriptOrPath, new UTF8Encoding(false), ct);
        try { return await Run(py, $"-X utf8 \"{tmp}\"", 300, ct); }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>Запуск с правами администратора (один запрос UAC). Окна UAC нажимать программно нельзя — это защищённый рабочий стол Windows.</summary>
    public static void RunElevated(string file, string args)
    {
        Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true, Verb = "runas" })?.Dispose();
    }

    /// <summary>Автозапуск Altair с наивысшими правами через планировщик (UAC — один раз при включении).</summary>
    public static void SetElevatedAutostart(bool enable, string exePath)
    {
        var args = enable
            ? $"/create /f /tn \"AltairAssistant\" /tr \"\\\"{exePath}\\\" --tray\" /sc onlogon /rl highest"
            : "/delete /f /tn \"AltairAssistant\"";
        RunElevated("schtasks.exe", args);
    }

    public static void SetAutostart(bool enable, string exePath)
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)!;
        if (enable) k.SetValue("AltairAssistant", $"\"{exePath}\" --tray"); else k.DeleteValue("AltairAssistant", false);
    }
}
