using System.Windows;
using System.Windows.Media;
using Altair.Core.Storage;

namespace Altair.App;

public sealed record Theme(string Name, string Bg, string Panel, string Card, string CardHover, string Border, string Fg, string Muted, bool Light = false);

/// <summary>Оформление: 9 тем, акцентный цвет, шрифт, свой фон (картинка/GIF/видео) и эффекты — без подписки.</summary>
public static class ThemeManager
{
    public static readonly Theme[] Themes =
    {
        new("Midnight", "#0E1220", "#141A2C", "#1A2136", "#212A44", "#2A3452", "#E9EDF7", "#8C97B5"),
        new("Graphite", "#111111", "#181818", "#202020", "#2A2A2A", "#333333", "#EDEDED", "#9A9A9A"),
        new("Nord", "#1F2430", "#252B38", "#2E3440", "#3B4252", "#434C5E", "#ECEFF4", "#A3ACBD"),
        new("Forest", "#0F1A14", "#14221A", "#1A2B21", "#21362A", "#2C4637", "#E6F2EA", "#8FB39D"),
        new("Wine", "#1A0F14", "#22141B", "#2C1A23", "#38212D", "#4A2C3B", "#F5E8EE", "#B892A4"),
        new("Ocean", "#07161F", "#0B1F2B", "#0F2938", "#143447", "#1D4560", "#E3F3FB", "#86AFC4"),
        new("Sand", "#F4EFE6", "#EBE4D6", "#FFFFFF", "#F7F2EA", "#DDD3C2", "#2B2620", "#7A6F60", true),
        new("Light", "#F3F5FA", "#E9EDF5", "#FFFFFF", "#F4F7FC", "#D9DFEA", "#151A26", "#667085", true),
        new("Neon", "#0A0614", "#120B22", "#1A1030", "#24163F", "#3B2466", "#F3EAFF", "#A28BCB"),
    };

    public static readonly string[] Accents = { "#4F8CFF", "#7C5CFF", "#00C2A8", "#FF6B6B", "#FFB547", "#3DD68C", "#FF4FA3", "#22B8F0" };

    public static readonly string[] Fonts = { "Segoe UI Variable", "Segoe UI", "Bahnschrift", "Cascadia Code", "Consolas", "Georgia", "Verdana" };

    public static void Apply(AppSettings s)
    {
        var t = Themes.FirstOrDefault(x => x.Name == s.Theme) ?? Themes[0];
        var r = Application.Current.Resources;
        Set(r, "Bg", t.Bg); Set(r, "Panel", t.Panel); Set(r, "Card", t.Card); Set(r, "CardHover", t.CardHover);
        Set(r, "Border", t.Border); Set(r, "Fg", t.Fg); Set(r, "Muted", t.Muted);
        Set(r, "Accent", s.Accent);
        r["UiFont"] = new FontFamily(s.FontFamily + ", Segoe UI");
        if (s.CardBlur > 0)
        {
            // Полупрозрачные карточки поверх своего фона
            var card = Ui.Parse(t.Card); card.A = (byte)Math.Clamp(255 - s.CardBlur * 12, 90, 255);
            r["Card"] = new SolidColorBrush(card);
        }
    }

    private static void Set(ResourceDictionary r, string key, string hex) => r[key] = new SolidColorBrush(Ui.Parse(hex));
}
