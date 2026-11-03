using System.Text;
using System.Windows;
using System.Windows.Threading;
using Altair.App.Overlays;
using Altair.Core.Models;
using Altair.Windows.Screen;
using Altair.Windows.Sys;

namespace Altair.App;

/// <summary>
/// Самопроверка (запуск с --selftest): поднимает все сервисы без микрофона, открывает каждую страницу и оверлей,
/// проверяет захват экрана, OCR, UI Automation и сопоставление команд. Код выхода 0 — всё работает.
/// Используется в CI на настоящей Windows.
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(AppHost host)
    {
        var log = new StringBuilder();
        int failures = 0;
        async Task Check(string name, Func<Task> f)
        {
            try { await f(); log.AppendLine("PASS " + name); }
            catch (Exception ex) { failures++; log.AppendLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
        }
        Task Sync(Action a) { a(); return Task.CompletedTask; }

        await Check("AppHost.Start (без микрофона)", () => host.StartAsync(new Progress<(string, double)>(), startSpeech: false));
        await Check("Встроенные паки загружены", () => Sync(() => { if (host.Store.All().Count() < 50) throw new Exception("Мало команд: " + host.Store.All().Count()); }));
        await Check("Все действия каталога зарегистрированы", () => Sync(() =>
        {
            var flow = new[] { ActionTypes.If, ActionTypes.Repeat, ActionTypes.While, ActionTypes.SetVar, ActionTypes.Wait, ActionTypes.Stop, ActionTypes.RunCommand, ActionTypes.AskUser };
            var missing = ActionTypes.Catalog.Select(a => a.Type).Where(t => !flow.Contains(t) && !host.Registry.TryGet(t, out _)).ToList();
            if (missing.Count > 0) throw new Exception("Нет обработчиков: " + string.Join(", ", missing));
        }));
        await Check("Сопоставление фраз", () => Sync(() =>
        {
            foreach (var (phrase, name) in new[] { ("открой ютуб", "YouTube"), ("громкость тридцать", "Громкость"), ("сверни все", "Свернуть всё"), ("который час", "Время") })
            {
                var m = host.Assistant.Matcher.MatchChain(phrase);
                if (m.Count == 0 || m[0].Command.Name != name) throw new Exception($"«{phrase}» → {(m.Count == 0 ? "ничего" : m[0].Command.Name)}");
            }
        }));
        await Check("Команда «Время» выполняется", async () => { var r = await host.Assistant.HandleAsync("который час", "selftest"); log.AppendLine("  ответ: " + r); });
        await Check("Сценарий с условием и переменными", async () =>
        {
            var cmd = new VoiceCommand { Name = "t", Actions = { ActionStep.Of(ActionTypes.SetVar, ("name", "x"), ("value", "2 * 21")), new ActionStep { Type = ActionTypes.If, Condition = "{x} == 42", Then = new() { ActionStep.Of(ActionTypes.ClipboardSet, ("text", "ok {x}")) } } } };
            await host.Assistant.ExecuteAsync(cmd, null, "t", "selftest");
            var clip = await Altair.Windows.Actions.Sta.GetClipboard();
            if (clip != "ok 42") throw new Exception("Буфер: " + clip);
        });
        await Check("Захват экрана", () => Sync(() => { using var b = ScreenCapture.Capture(); if (b.Width < 100) throw new Exception("Слишком маленький экран"); }));
        await Check("OCR", async () =>
        {
            using var b = ScreenCapture.Capture();
            try { var lines = await Ocr.RecognizeAsync(b, "en"); log.AppendLine($"  строк OCR: {lines.Count}"); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("OCR недоступен")) { log.AppendLine("  (на этой машине нет языкового пакета OCR — пропуск)"); }
        });
        await Check("Список окон и UI Automation", () => Sync(() =>
        {
            var wins = WindowManager.List();
            log.AppendLine($"  окон: {wins.Count}");
            var fg = Altair.Windows.Native.Win32.GetForegroundWindow();
            if (fg != IntPtr.Zero) log.AppendLine($"  элементов UIA: {UiAutomation.Elements(fg, 50).Count}");
        }));
        await Check("Системная сводка", async () => log.AppendLine("  " + await Altair.Windows.Actions.WindowsActions.SystemSummary("all")));
        await Check("Python/PowerShell", async () => { var r = await Shell.PowerShell("Write-Output 'привет'", default); if (!r.Output.Contains("привет")) throw new Exception(r.Output); });

        MainWindow? main = null;
        await Check("Главное окно", () => Sync(() => { main = new MainWindow(); main.Show(); }));
        foreach (var page in new[] { "Главная", "Команды", "Нейросеть", "ИИ-агент", "Расширения", "Оформление", "Настройки", "Журнал" })
            await Check("Страница " + page, async () =>
            {
                main!.Navigate(page);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await Task.Delay(300);
                if (main.Content is FrameworkElement fe) fe.UpdateLayout();
            });
        foreach (var theme in ThemeManager.Themes)
            await Check("Тема " + theme.Name, () => Sync(() => { host.Settings.Theme = theme.Name; ThemeManager.Apply(host.Settings); main!.Reload(); }));
        foreach (var fx in new[] { "stars", "snow", "waves", "grid", "none" })
            await Check("Эффект фона " + fx, async () => { host.Settings.BackgroundEffect = fx; main!.ApplyBackground(); await Task.Delay(150); });

        await Check("Островок", async () => { host.Island.Flash("Самопроверка", 1); host.Island.SetAgentStep(2, 5, "шаг"); host.Island.ToggleExpanded(); host.Island.ToggleExpanded(); await Task.Delay(200); });
        await Check("Окно подтверждения", async () => { var w = new ConfirmWindow("Тестовое действие", false); bool? got = null; w.Decided += ok => got = ok; w.Show(); await Task.Delay(200); w.CloseSafe(); if (got != false) throw new Exception("Не сработал отказ при закрытии"); });
        await Check("Питомец и аватар", async () => { PetWindow.Toggle(true); AvatarWindow.Toggle(true); await Task.Delay(300); PetWindow.Toggle(false); AvatarWindow.Toggle(false); });
        await Check("История буфера", async () => { ClipboardWindow.ShowAtCursor(); await Task.Delay(200); foreach (var w in Application.Current.Windows.OfType<ClipboardWindow>().ToList()) w.Close(); });
        await Check("Скриншот-оверлей", async () => { ScreenshotOverlay.Start(); await Task.Delay(400); foreach (var w in Application.Current.Windows.OfType<ScreenshotOverlay>().ToList()) w.Close(); });

        main?.Hide();
        await host.DisposeAsync();

        log.Insert(0, $"Altair self-test: {(failures == 0 ? "OK" : failures + " FAIL")}\n");
        var path = Path.Combine(AppContext.BaseDirectory, "selftest.log");
        File.WriteAllText(path, log.ToString());
        Console.WriteLine(log.ToString());
        return failures == 0 ? 0 : 1;
    }
}
