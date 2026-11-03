using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Altair.Core.Models;
using Trigger = Altair.Core.Models.Trigger;
using Altair.Core.Storage;
using Microsoft.Win32;

namespace Altair.App.Views;

public abstract class DialogBase : Window
{
    protected readonly StackPanel Body = new() { Margin = new Thickness(20) };

    protected DialogBase(string title, double width = 560)
    {
        Title = title; Width = width; SizeToContent = SizeToContent.Height; MaxHeight = 820;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w != this)
                    ?? Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
        if (owner != null && owner != this && owner.IsVisible) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Bg");
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Content = new ScrollViewer { Content = Body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    protected void Buttons(Func<bool> onOk)
    {
        var ok = new Button { Content = "OK", IsDefault = true, Style = (Style)Application.Current.Resources["Primary"] };
        ok.Click += (_, _) => { try { if (onOk()) DialogResult = true; } catch (Exception ex) { Ui.Error(ex); } };
        var cancel = new Button { Content = "Отмена", IsCancel = true };
        Body.Children.Add(Ui.Row(ok, cancel));
    }
}

public sealed class Prompt : DialogBase
{
    private readonly TextBox _box = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60 };
    private Prompt(string title, string text, string value) : base(title)
    {
        Body.Children.Add(Ui.Text(text));
        _box.Text = value;
        Body.Children.Add(_box);
        Buttons(() => true);
        Loaded += (_, _) => _box.Focus();
    }

    public static string? Ask(string title, string text, string value = "")
    {
        var p = new Prompt(title, text, value);
        return p.ShowDialog() == true ? p._box.Text.Trim() : null;
    }
}

/// <summary>Редактор шага: поля по описанию действия; для условий и циклов — вложенные шаги (JSON).</summary>
public sealed class ActionEditor : DialogBase
{
    private ActionEditor(ActionStep step) : base("Действие: " + (ActionTypes.Find(step.Type)?.Title ?? step.Type), 620)
    {
        var info = ActionTypes.Find(step.Type);
        var values = new Dictionary<string, Func<string>>();
        Body.Children.Add(Ui.Hint(info?.Group ?? ""));

        foreach (var a in info?.Args ?? Array.Empty<ArgSpec>())
        {
            if (a.Key == "—") continue;
            var cur = step.Args.TryGetValue(a.Key, out var v) ? v : a.Default ?? "";
            if (a.Kind == "bool")
            {
                var cb = new CheckBox { Content = a.Title, IsChecked = cur is "true" or "1" or "да" };
                values[a.Key] = () => cb.IsChecked == true ? "true" : "false";
                Body.Children.Add(cb);
                continue;
            }
            bool multiline = a.Key is "script" or "text" or "body" or "json" or "prompt" or "task";
            var tb = new TextBox { Text = cur, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 70 : 0 };
            values[a.Key] = () => tb.Text;
            var row = new DockPanel();
            if (a.Kind is "file" or "folder")
            {
                var browse = new Button { Content = "…", Margin = new Thickness(6, 0, 0, 8) };
                browse.Click += (_, _) =>
                {
                    if (a.Kind == "file") { var d = new OpenFileDialog(); if (d.ShowDialog() == true) tb.Text = d.FileName; }
                    else { var d = new OpenFolderDialog(); if (d.ShowDialog() == true) tb.Text = d.FolderName; }
                };
                DockPanel.SetDock(browse, Dock.Right);
                row.Children.Add(browse);
            }
            row.Children.Add(tb);
            Body.Children.Add(Ui.Hint(a.Title + (a.Optional ? " (необязательно)" : "")));
            Body.Children.Add(row);
        }

        TextBox? cond = null, thenBox = null, elseBox = null;
        if (step.Type is ActionTypes.If or ActionTypes.While)
        {
            cond = new TextBox { Text = step.Condition ?? "" };
            Body.Children.Add(Ui.Hint("Условие. Примеры: {hour} >= 22 · {battery} < 30 и {window} contains \"Chrome\" · {answer} == \"да\""));
            Body.Children.Add(cond);
        }
        if (step.Type is ActionTypes.If or ActionTypes.While or ActionTypes.Repeat)
        {
            thenBox = JsonBox(step.Then);
            Body.Children.Add(Ui.Hint(step.Type == ActionTypes.If ? "Тогда (шаги JSON):" : "Тело цикла (шаги JSON, переменная {i} — номер прохода):"));
            Body.Children.Add(thenBox);
            if (step.Type == ActionTypes.If)
            {
                elseBox = JsonBox(step.Else);
                Body.Children.Add(Ui.Hint("Иначе (шаги JSON):"));
                Body.Children.Add(elseBox);
            }
            Body.Children.Add(Ui.Hint("Пример шага: {\"type\":\"voice.say\",\"args\":{\"text\":\"Привет\"}}"));
        }

        var cont = new CheckBox { Content = "Продолжать сценарий при ошибке (текст ошибки — в {error})", IsChecked = step.ContinueOnError };
        Body.Children.Add(cont);
        var comment = new TextBox { Text = step.Comment ?? "" };
        Body.Children.Add(Ui.Hint("Комментарий"));
        Body.Children.Add(comment);

        Buttons(() =>
        {
            foreach (var kv in values) step.Args[kv.Key] = kv.Value();
            if (cond != null) step.Condition = cond.Text.Trim();
            if (thenBox != null) step.Then = ParseSteps(thenBox.Text);
            if (elseBox != null) step.Else = ParseSteps(elseBox.Text);
            step.ContinueOnError = cont.IsChecked == true;
            step.Comment = string.IsNullOrWhiteSpace(comment.Text) ? null : comment.Text;
            if (cond != null) Altair.Core.Engine.Expr.Eval(step.Condition!, new Altair.Core.Engine.VariableStore()); // проверка синтаксиса
            return true;
        });
    }

    private static TextBox JsonBox(List<ActionStep>? steps) => new()
    {
        Text = JsonSerializer.Serialize(steps ?? new List<ActionStep>(), Json.Options),
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 110, FontFamily = new System.Windows.Media.FontFamily("Cascadia Code, Consolas")
    };

    private static List<ActionStep> ParseSteps(string json)
    {
        var list = JsonSerializer.Deserialize<List<ActionStep>>(string.IsNullOrWhiteSpace(json) ? "[]" : json, Json.Options) ?? new();
        foreach (var s in list)
            if (ActionTypes.Find(s.Type) == null) throw new InvalidDataException($"Неизвестное действие «{s.Type}»");
        return list;
    }

    public static bool Edit(ActionStep step) => new ActionEditor(step).ShowDialog() == true;
}

public sealed class TriggerEditor : DialogBase
{
    private Trigger? _result;

    private TriggerEditor(TriggerKind kind) : base(kind switch { TriggerKind.Hotkey => "Горячая клавиша", TriggerKind.Schedule => "Расписание", _ => "Событие" })
    {
        var t = new Trigger { Kind = kind };
        switch (kind)
        {
            case TriggerKind.Hotkey:
            {
                var tb = new TextBox { Text = "Ctrl+Alt+1" };
                Body.Children.Add(Ui.Hint("Сочетание: Ctrl/Alt/Shift/Win + клавиша. Примеры: Ctrl+Alt+K · Ctrl+Shift+F5 · RightCtrl×2"));
                Body.Children.Add(tb);
                Buttons(() => { Altair.Windows.Sys.Hotkey.Parse(tb.Text); t.Hotkey = tb.Text.Trim(); _result = t; return true; });
                break;
            }
            case TriggerKind.Schedule:
            {
                var mode = new ComboBox();
                foreach (var (m, title) in new[] { (ScheduleMode.Daily, "Ежедневно"), (ScheduleMode.Weekdays, "По будням"), (ScheduleMode.DaysOfWeek, "По дням недели"), (ScheduleMode.EveryMinutes, "Каждые N минут"), (ScheduleMode.Once, "Однократно") })
                    mode.Items.Add(new ComboBoxItem { Content = title, Tag = m });
                mode.SelectedIndex = 0;
                var time = new TextBox { Text = "09:00" };
                var date = new DatePicker { SelectedDate = DateTime.Today };
                var every = new TextBox { Text = "60" };
                var days = new WrapPanel();
                var dayBoxes = new List<(DayOfWeek, CheckBox)>();
                foreach (var d in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
                {
                    var cb = new CheckBox { Content = CultureInfo.GetCultureInfo("ru-RU").DateTimeFormat.GetAbbreviatedDayName(d), Margin = new Thickness(0, 0, 10, 0) };
                    dayBoxes.Add((d, cb)); days.Children.Add(cb);
                }
                Body.Children.Add(Ui.Hint("Повтор")); Body.Children.Add(mode);
                Body.Children.Add(Ui.Hint("Время (ЧЧ:ММ)")); Body.Children.Add(time);
                Body.Children.Add(Ui.Hint("Дата (для «однократно»)")); Body.Children.Add(date);
                Body.Children.Add(Ui.Hint("Дни недели")); Body.Children.Add(days);
                Body.Children.Add(Ui.Hint("Интервал, минут (для «каждые N минут»)")); Body.Children.Add(every);
                Body.Children.Add(Ui.Hint("Пропущенный запуск выполняется, если Altair опоздал не больше чем на 10 минут."));
                Buttons(() =>
                {
                    t.Mode = (ScheduleMode)((ComboBoxItem)mode.SelectedItem).Tag;
                    t.Time = TimeSpan.ParseExact(time.Text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture);
                    t.EveryMinutes = int.Parse(every.Text.Trim());
                    t.Days = dayBoxes.Where(x => x.Item2.IsChecked == true).Select(x => x.Item1).ToList();
                    if (t.Mode == ScheduleMode.Once) t.At = (date.SelectedDate ?? DateTime.Today) + t.Time;
                    if (t.Mode == ScheduleMode.DaysOfWeek && t.Days.Count == 0) throw new InvalidOperationException("Выберите хотя бы один день");
                    _result = t; return true;
                });
                break;
            }
            default:
            {
                var titles = new Dictionary<string, string>
                {
                    [SystemEvents.AppStarted] = "Запущена программа", [SystemEvents.AppClosed] = "Закрыта программа",
                    [SystemEvents.HeadphonesConnected] = "Подключены наушники", [SystemEvents.HeadphonesDisconnected] = "Отключены наушники",
                    [SystemEvents.AudioDeviceChanged] = "Сменилось аудиоустройство", [SystemEvents.PowerPlugged] = "Подключена зарядка",
                    [SystemEvents.PowerUnplugged] = "Отключена зарядка", [SystemEvents.BatteryBelow] = "Заряд ниже порога",
                    [SystemEvents.UserAway] = "Отошёл от ПК", [SystemEvents.UserBack] = "Вернулся к ПК",
                    [SystemEvents.SessionLocked] = "Компьютер заблокирован", [SystemEvents.SessionUnlocked] = "Компьютер разблокирован",
                    [SystemEvents.InternetLost] = "Пропал интернет", [SystemEvents.InternetRestored] = "Появился интернет",
                    [SystemEvents.WifiConnected] = "Подключение к Wi-Fi", [SystemEvents.AssistantStarted] = "Запуск Altair",
                    [SystemEvents.Snapse] = "Snapse: щелчки пальцами (1/2/3)",
                };
                var evt = new ComboBox();
                foreach (var kv in titles) evt.Items.Add(new ComboBoxItem { Content = kv.Value, Tag = kv.Key });
                evt.SelectedIndex = 0;
                var arg = new TextBox();
                var cd = new TextBox { Text = "0" };
                Body.Children.Add(evt);
                Body.Children.Add(Ui.Hint("Уточнение: имя программы (chrome), имя сети Wi-Fi, порог батареи (%), число щелчков Snapse. Пусто — любое."));
                Body.Children.Add(arg);
                Body.Children.Add(Ui.Hint("Не чаще раза в N секунд"));
                Body.Children.Add(cd);
                Buttons(() =>
                {
                    t.Event = (string)((ComboBoxItem)evt.SelectedItem).Tag;
                    t.EventArg = string.IsNullOrWhiteSpace(arg.Text) ? null : arg.Text.Trim();
                    t.CooldownSec = int.TryParse(cd.Text, out var c) ? c : 0;
                    _result = t; return true;
                });
                break;
            }
        }
    }

    public static Trigger? Create(TriggerKind kind)
    {
        var d = new TriggerEditor(kind);
        return d.ShowDialog() == true ? d._result : null;
    }
}

/// <summary>Запись действий: набранный текст → «Напечатать», сочетания и спецклавиши → «Сочетание», паузы → «Пауза».</summary>
public sealed class MacroRecorder : DialogBase
{
    private readonly List<ActionStep> _steps = new();
    private readonly StringBuilder _text = new();
    private readonly HashSet<uint> _mods = new();
    private DateTime _last = DateTime.Now;
    private readonly TextBlock _log = Ui.Text("Нажимайте клавиши в любой программе…");

    private static readonly Dictionary<uint, string> Names = new()
    {
        [0x0D] = "Enter", [0x09] = "Tab", [0x1B] = "Esc", [0x08] = "Backspace", [0x2E] = "Delete", [0x20] = "Space",
        [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down", [0x24] = "Home", [0x23] = "End", [0x21] = "PageUp", [0x22] = "PageDown",
    };

    private MacroRecorder() : base("Запись действий", 420)
    {
        Topmost = true;
        Body.Children.Add(_log);
        Body.Children.Add(Ui.Hint("Окно записи можно отодвинуть. Нажмите «Стоп», когда закончите."));
        var stop = new Button { Content = "⏹ Стоп", Style = (Style)Application.Current.Resources["Primary"] };
        stop.Click += (_, _) => { Flush(); DialogResult = true; };
        Body.Children.Add(stop);
        var keys = AppHost.Current.Keys;
        if (keys != null) { keys.RawKey += OnKey; keys.Char += OnChar; }
        Closed += (_, _) => { if (keys != null) { keys.RawKey -= OnKey; keys.Char -= OnChar; } };
    }

    private static bool IsMod(uint vk) => vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;

    private void Gap()
    {
        var gap = (DateTime.Now - _last).TotalMilliseconds;
        _last = DateTime.Now;
        if (gap > 700 && (_steps.Count > 0 || _text.Length > 0)) { Flush(); _steps.Add(ActionStep.Of(ActionTypes.Wait, ("ms", ((int)Math.Min(gap, 10000)).ToString()))); }
    }

    private void OnChar(char c)
    {
        if (_mods.Any(m => m is not (0x10 or 0xA0 or 0xA1))) return; // сочетания обрабатывает OnKey
        if (char.IsControl(c)) return;
        Dispatcher.BeginInvoke(() => { Gap(); _text.Append(c); Show(); });
    }

    private void OnKey(uint vk, bool down)
    {
        if (IsMod(vk)) { if (down) _mods.Add(vk); else _mods.Remove(vk); return; }
        if (!down) return;
        bool combo = _mods.Any(m => m is not (0x10 or 0xA0 or 0xA1));
        if (!combo && !Names.ContainsKey(vk) && !(vk >= 0x70 && vk <= 0x87)) return; // обычные символы — через OnChar
        if (!combo && vk == 0x20) return; // пробел печатается текстом
        var parts = new List<string>();
        if (_mods.Overlaps(new uint[] { 0x11, 0xA2, 0xA3 })) parts.Add("Ctrl");
        if (_mods.Overlaps(new uint[] { 0x12, 0xA4, 0xA5 })) parts.Add("Alt");
        if (_mods.Overlaps(new uint[] { 0x10, 0xA0, 0xA1 })) parts.Add("Shift");
        if (_mods.Overlaps(new uint[] { 0x5B, 0x5C })) parts.Add("Win");
        string key = Names.TryGetValue(vk, out var n) ? n : vk is >= 0x70 and <= 0x87 ? "F" + (vk - 0x6F) : ((char)vk).ToString();
        parts.Add(key);
        var spec = string.Join('+', parts);
        Dispatcher.BeginInvoke(() =>
        {
            Gap(); Flush();
            _steps.Add(parts.Count == 1 ? ActionStep.Of(ActionTypes.KeyPress, ("key", key), ("times", "1")) : ActionStep.Of(ActionTypes.KeyCombo, ("combo", spec)));
            Show();
        });
    }

    private void Flush()
    {
        if (_text.Length == 0) return;
        _steps.Add(ActionStep.Of(ActionTypes.TypeText, ("text", _text.ToString()), ("human", "false")));
        _text.Clear();
    }

    private void Show() => _log.Text = $"Записано шагов: {_steps.Count}" + (_text.Length > 0 ? $"\nТекст: {_text}" : "");

    public static List<ActionStep> Record()
    {
        var r = new MacroRecorder();
        return r.ShowDialog() == true ? r._steps : new List<ActionStep>();
    }
}
