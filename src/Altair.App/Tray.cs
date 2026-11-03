using System.Windows;
using WinForms = System.Windows.Forms;

namespace Altair.App;

/// <summary>Значок в трее: меню и всплывающие уведомления.</summary>
public static class Tray
{
    private static WinForms.NotifyIcon? _icon;

    public static void Init(Action show, Action exit)
    {
        var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        _icon = new WinForms.NotifyIcon
        {
            Icon = File.Exists(ico) ? new System.Drawing.Icon(ico) : System.Drawing.SystemIcons.Application,
            Text = "Altair Assistant",
            Visible = true,
            ContextMenuStrip = new WinForms.ContextMenuStrip(),
        };
        _icon.ContextMenuStrip.Items.Add("Открыть", null, (_, _) => show());
        _icon.ContextMenuStrip.Items.Add("Микрофон вкл/выкл", null, (_, _) =>
        {
            var sp = AppHost.Current.Speech;
            if (sp == null) return;
            sp.UserMuted = !sp.UserMuted;
            Notify("Микрофон", sp.UserMuted ? "Выключен" : "Слушаю");
        });
        _icon.ContextMenuStrip.Items.Add("Игровой режим", null, (_, _) =>
        {
            var s = AppHost.Current.Settings; s.GameMode = !s.GameMode; AppHost.Current.SaveSettings();
            Notify("Игровой режим", s.GameMode ? "Включён: ответы только текстом" : "Выключен");
        });
        _icon.ContextMenuStrip.Items.Add(new WinForms.ToolStripSeparator());
        _icon.ContextMenuStrip.Items.Add("Выход", null, (_, _) => exit());
        _icon.DoubleClick += (_, _) => show();
    }

    public static void Notify(string title, string text)
    {
        if (_icon == null) return;
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = string.IsNullOrWhiteSpace(text) ? " " : (text.Length > 250 ? text[..250] + "…" : text);
        _icon.ShowBalloonTip(4000);
    }

    public static void Dispose() { if (_icon != null) { _icon.Visible = false; _icon.Dispose(); _icon = null; } }
}
