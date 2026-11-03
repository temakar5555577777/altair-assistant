using System.Threading;
using System.Windows;
using Altair.App.Views;
using Velopack;

namespace Altair.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Установщик/обновления Velopack: обработка хуков установки — до любого UI
        VelopackApp.Build().Run();

        using var mutex = new Mutex(true, "AltairAssistant.SingleInstance", out bool first);
        if (!first)
        {
            // Уже запущен — просим первый экземпляр показать окно
            try { using var e = EventWaitHandle.OpenExisting("AltairAssistant.Show"); e.Set(); } catch { }
            return;
        }

        var app = new App { StartMinimized = args.Contains("--tray") };
        app.InitializeComponent();
        app.Run();
    }
}

public partial class App : Application
{
    public bool StartMinimized { get; init; }
    private AppHost? _host;
    private MainWindow? _main;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            _host?.Write("Ошибка интерфейса: " + ex.Exception);
            MessageBox.Show(ex.Exception.Message, "Altair", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, ex) => { _host?.Write("Фоновая ошибка: " + ex.Exception.GetBaseException().Message); ex.SetObserved(); };

        _host = new AppHost();
        ThemeManager.Apply(_host.Settings);
        Tray.Init(ShowMain, () => _ = ExitAsync());

        var splash = new SplashWindow();
        if (!StartMinimized) splash.Show();
        try
        {
            await _host.StartAsync(new Progress<(string, double)>(p => splash.Report(p.Item1, p.Item2)));
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось запустить ассистента: " + ex.Message, "Altair", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        splash.Close();

        if (!StartMinimized) ShowMain();
        Tray.Notify("Altair", "Ассистент слушает. Скажите «" + (_host.Settings.WakeWords.FirstOrDefault() ?? "Альтаир") + "».");

        // Сигнал «показать окно» от второго экземпляра
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, "AltairAssistant.Show");
        _ = Task.Run(() => { while (show.WaitOne()) Dispatcher.BeginInvoke(ShowMain); });
    }

    public void ShowMain()
    {
        if (_main == null) { _main = new MainWindow(); _main.Closed += (_, _) => _main = null; }
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public async Task ExitAsync()
    {
        Tray.Dispose();
        if (_host != null) await _host.DisposeAsync();
        Shutdown();
    }
}
