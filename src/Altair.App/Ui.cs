using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Altair.App;

/// <summary>Компактный конструктор интерфейса в коде (страницы собираются без XAML — меньше шаблонного кода).</summary>
public static class Ui
{
    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public static TextBlock H1(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["H1"] };
    public static TextBlock H2(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["H2"] };
    public static TextBlock Text(string t, double size = 14) => new() { Text = t, FontSize = size, Margin = new Thickness(0, 0, 0, 6) };
    public static TextBlock Hint(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["MutedText"], Margin = new Thickness(0, 0, 0, 8) };

    public static Border Card(params UIElement[] children)
    {
        var sp = new StackPanel();
        foreach (var c in children) sp.Children.Add(c);
        return new Border { Style = (Style)Application.Current.Resources["CardBorder"], Child = sp };
    }

    public static Border Card(string title, params UIElement[] children) =>
        Card(new UIElement[] { H2(title) }.Concat(children).ToArray());

    public static StackPanel Stack(params UIElement[] children)
    {
        var sp = new StackPanel();
        foreach (var c in children) sp.Children.Add(c);
        return sp;
    }

    public static WrapPanel Row(params UIElement[] children)
    {
        var wp = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children) wp.Children.Add(c);
        return wp;
    }

    public static ScrollViewer Page(params UIElement[] children) => new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(28, 24, 16, 24),
        Content = Stack(children)
    };

    public static Button Btn(string text, Action onClick, bool primary = false)
    {
        var b = new Button { Content = text };
        if (primary) b.Style = (Style)Application.Current.Resources["Primary"];
        b.Click += (_, _) =>
        {
            try { onClick(); }
            catch (Exception ex) { Error(ex); }
        };
        return b;
    }

    public static Button BtnAsync(string text, Func<Task> onClick, bool primary = false)
    {
        var b = new Button { Content = text };
        if (primary) b.Style = (Style)Application.Current.Resources["Primary"];
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try { await onClick(); }
            catch (Exception ex) { Error(ex); }
            finally { b.IsEnabled = true; }
        };
        return b;
    }

    public static void Error(Exception ex) =>
        MessageBox.Show(ex.Message, "Altair", MessageBoxButton.OK, MessageBoxImage.Warning);

    public static void Info(string text) => MessageBox.Show(text, "Altair", MessageBoxButton.OK, MessageBoxImage.Information);

    public static bool Ask(string text) => MessageBox.Show(text, "Altair", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public static StackPanel Field(string label, string value, Action<string> onChange, bool multiline = false, string? hint = null, double height = 0)
    {
        var tb = new TextBox { Text = value, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (multiline) { tb.MinHeight = height > 0 ? height : 80; tb.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; }
        tb.TextChanged += (_, _) => onChange(tb.Text);
        var sp = Stack(Hint(label), tb);
        if (hint != null) sp.Children.Add(Hint(hint));
        return sp;
    }

    public static StackPanel Secret(string label, string value, Action<string> onChange)
    {
        var pb = new PasswordBox { Password = value };
        pb.PasswordChanged += (_, _) => onChange(pb.Password);
        return Stack(Hint(label), pb);
    }

    public static CheckBox Toggle(string label, bool value, Action<bool> onChange)
    {
        var cb = new CheckBox { Content = label, IsChecked = value };
        cb.Checked += (_, _) => onChange(true);
        cb.Unchecked += (_, _) => onChange(false);
        return cb;
    }

    public static StackPanel Combo<T>(string label, IEnumerable<T> items, T selected, Action<T> onChange, Func<T, string>? title = null)
    {
        var list = items.ToList();
        var cb = new ComboBox();
        foreach (var i in list) cb.Items.Add(new ComboBoxItem { Content = title != null ? title(i) : i?.ToString(), Tag = i });
        cb.SelectedIndex = Math.Max(0, list.FindIndex(i => Equals(i, selected)));
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is ComboBoxItem it && it.Tag is T v) onChange(v); };
        return Stack(Hint(label), cb);
    }

    public static StackPanel SliderField(string label, double min, double max, double value, Action<double> onChange, string unit = "")
    {
        var caption = Hint($"{label}: {value:0}{unit}");
        var s = new Slider { Minimum = min, Maximum = max, Value = value, IsSnapToTickEnabled = true, TickFrequency = 1 };
        s.ValueChanged += (_, e) => { caption.Text = $"{label}: {e.NewValue:0}{unit}"; onChange(e.NewValue); };
        return Stack(caption, s);
    }

    public static Grid Columns(params (UIElement El, double Star)[] cols)
    {
        var g = new Grid();
        for (int i = 0; i < cols.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = cols[i].Star > 0 ? new GridLength(cols[i].Star, GridUnitType.Star) : GridLength.Auto });
            Grid.SetColumn(cols[i].El, i);
            g.Children.Add(cols[i].El);
        }
        return g;
    }

    public static UniformGrid Tiles(int columns, params UIElement[] children)
    {
        var g = new UniformGrid { Columns = columns };
        foreach (var c in children) g.Children.Add(c);
        return g;
    }

    public static DropShadowEffect Shadow() => new() { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35, Color = Colors.Black };

    public static Color Parse(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return Color.FromRgb(0x4F, 0x8C, 0xFF); }
    }
}
