using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Altair.Core.Engine;
using Altair.Core.Models;
using Altair.Core.Storage;
using Microsoft.Win32;

namespace Altair.App.Views;

/// <summary>Редактор команд без кода: коллекции → папки → команды; действия с условиями и циклами; триггеры; ИИ-создание.</summary>
public sealed class CommandsPage : UserControl
{
    private readonly AppHost _h = AppHost.Current;
    private readonly TreeView _tree = new() { MinWidth = 280 };
    private readonly ContentControl _editor = new();
    private readonly TextBox _search = new() { Margin = new Thickness(0, 0, 0, 8) };

    public CommandsPage()
    {
        _search.TextChanged += (_, _) => FillTree();
        var left = new DockPanel { Margin = new Thickness(0, 0, 16, 0), Width = 320 };
        var tools = Ui.Row(
            Ui.Btn("+ Коллекция", AddCollection),
            Ui.Btn("+ Папка", AddFolder),
            Ui.Btn("+ Команда", AddCommand, true),
            Ui.BtnAsync("✨ Создать ИИ", CreateWithAiAsync),
            Ui.Btn("Импорт", Import),
            Ui.Btn("Экспорт", Export),
            Ui.Btn("Удалить", DeleteSelected));
        DockPanel.SetDock(tools, Dock.Top);
        var searchBox = Ui.Stack(Ui.Hint("Поиск по командам и фразам"), _search);
        DockPanel.SetDock(searchBox, Dock.Top);
        left.Children.Add(tools);
        left.Children.Add(searchBox);
        left.Children.Add(_tree);

        _tree.SelectedItemChanged += (_, _) => ShowEditor();

        var grid = new Grid { Margin = new Thickness(28, 24, 16, 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        var title = Ui.H1("Команды");
        Grid.SetColumnSpan(title, 2);
        grid.Children.Add(title);
        Grid.SetRow(left, 1); grid.Children.Add(left);
        var scroll = new ScrollViewer { Content = _editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); Grid.SetColumn(scroll, 1); grid.Children.Add(scroll);
        Content = grid;
        FillTree();
        _editor.Content = Ui.Card("Как это работает",
            Ui.Text("Слева — коллекции (паки) и папки. Выберите команду, чтобы изменить фразы, действия и ответы."),
            Ui.Hint("Фразы поддерживают слоты: «громкость {число}», «найди {text}». Несколько команд в одной фразе: «открой ютуб и сделай громкость 30»."),
            Ui.Hint("Ответы — по одному в строке, можно с весом: «Есть, сэр! | 2». Свой аудиофайл: путь к .wav/.mp3."));
    }

    private object? Selected => (_tree.SelectedItem as TreeViewItem)?.Tag;

    private void FillTree()
    {
        var q = PhraseNormalizer.Normalize(_search.Text);
        _tree.Items.Clear();
        foreach (var c in _h.Store.Collections)
        {
            var ci = new TreeViewItem { Header = (c.Enabled ? "" : "⏸ ") + c.Name, Tag = c, IsExpanded = q.Length > 0 };
            foreach (var f in c.Folders)
            {
                var fi = new TreeViewItem { Header = (f.Enabled ? "📁 " : "⏸ ") + f.Name, Tag = f, IsExpanded = q.Length > 0 };
                foreach (var cmd in f.Commands)
                {
                    if (q.Length > 0 && !PhraseNormalizer.Normalize(cmd.Name + " " + string.Join(' ', cmd.Phrases)).Contains(q)) continue;
                    fi.Items.Add(new TreeViewItem { Header = (cmd.Enabled ? "" : "⏸ ") + cmd.Name, Tag = cmd });
                }
                if (q.Length == 0 || fi.Items.Count > 0) ci.Items.Add(fi);
            }
            if (q.Length == 0 || ci.Items.Count > 0) _tree.Items.Add(ci);
        }
    }

    private (CommandCollection? C, CommandFolder? F) Owner(object? o)
    {
        foreach (var c in _h.Store.Collections)
        {
            if (o == c) return (c, c.Folders.FirstOrDefault());
            foreach (var f in c.Folders)
                if (o == f || (o is VoiceCommand cmd && f.Commands.Contains(cmd))) return (c, f);
        }
        return (null, null);
    }

    private void AddCollection()
    {
        var c = new CommandCollection { Name = "Мои команды", Folders = { new CommandFolder { Name = "Общее" } } };
        _h.Store.Collections.Add(c); _h.Store.Save(c); FillTree();
    }

    private void AddFolder()
    {
        var (c, _) = Owner(Selected);
        c ??= _h.Store.Collections.FirstOrDefault(x => x.PackId == null) ?? _h.Store.Collections.First();
        c.Folders.Add(new CommandFolder { Name = "Новая папка" }); _h.Store.Save(c); FillTree();
    }

    private void AddCommand()
    {
        var (c, f) = Owner(Selected);
        if (c == null || f == null)
        {
            c = _h.Store.Collections.FirstOrDefault(x => x.PackId == null);
            if (c == null) { AddCollection(); c = _h.Store.Collections.Last(); }
            f = c.Folders.FirstOrDefault() ?? new CommandFolder { Name = "Общее" };
            if (!c.Folders.Contains(f)) c.Folders.Add(f);
        }
        var cmd = new VoiceCommand { Name = "Новая команда", Phrases = { "новая команда" }, Responses = { Items = { new WeightedResponse { Text = "Готово." } } } };
        f.Commands.Add(cmd); _h.Store.Save(c); FillTree(); Edit(cmd);
    }

    private async Task CreateWithAiAsync()
    {
        var desc = Prompt.Ask("Создать команду с помощью ИИ", "Опишите, что должна делать команда.\nНапример: «по фразе „рабочий режим“ открой VS Code и Telegram, громкость 20, разложи окна пополам».");
        if (string.IsNullOrWhiteSpace(desc)) return;
        var cmd = await _h.Builder.BuildAsync(desc);
        var c = _h.Store.Collections.FirstOrDefault(x => x.PackId == null);
        if (c == null) { AddCollection(); c = _h.Store.Collections.Last(); }
        var f = c.Folders.FirstOrDefault(x => x.Name == "Созданные ИИ") ?? new CommandFolder { Name = "Созданные ИИ" };
        if (!c.Folders.Contains(f)) c.Folders.Add(f);
        f.Commands.Add(cmd); _h.Store.Save(c); FillTree(); Edit(cmd);
    }

    private void Import()
    {
        var dlg = new OpenFileDialog { Filter = "Команды (*.json)|*.json", Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FileNames) _h.Store.Import(File.ReadAllText(f));
        FillTree();
    }

    private void Export()
    {
        var (c, _) = Owner(Selected);
        if (c == null) { Ui.Info("Выберите коллекцию или команду в ней"); return; }
        var dlg = new SaveFileDialog { Filter = "Команды (*.json)|*.json", FileName = c.Name + ".json" };
        if (dlg.ShowDialog() == true) File.WriteAllText(dlg.FileName, _h.Store.Export(c));
    }

    private void DeleteSelected()
    {
        var o = Selected; var (c, f) = Owner(o);
        if (o == null || c == null) return;
        if (!Ui.Ask("Удалить выбранное?")) return;
        switch (o)
        {
            case CommandCollection col: _h.Store.Delete(col); break;
            case CommandFolder fol: c.Folders.Remove(fol); _h.Store.Save(c); break;
            case VoiceCommand cmd: f!.Commands.Remove(cmd); _h.Store.Save(c); break;
        }
        FillTree(); _editor.Content = null;
    }

    private void ShowEditor()
    {
        switch (Selected)
        {
            case VoiceCommand cmd: Edit(cmd); break;
            case CommandFolder f:
                _editor.Content = Ui.Card("Папка",
                    Ui.Field("Название", f.Name, v => f.Name = v),
                    Ui.Toggle("Включена", f.Enabled, v => f.Enabled = v),
                    Ui.Btn("Сохранить", () => { _h.Store.Save(Owner(f).C!); FillTree(); }, true));
                break;
            case CommandCollection c:
                _editor.Content = Ui.Card("Коллекция" + (c.PackId != null ? " (встроенный пак)" : ""),
                    Ui.Field("Название", c.Name, v => c.Name = v),
                    Ui.Toggle("Включена", c.Enabled, v => c.Enabled = v),
                    Ui.Hint($"Команд: {c.Folders.Sum(x => x.Commands.Count)}"),
                    Ui.Btn("Сохранить", () => { _h.Store.Save(c); FillTree(); }, true));
                break;
        }
    }

    private void Edit(VoiceCommand cmd)
    {
        var actions = new ListBox { MinHeight = 160 };
        void RenderActions()
        {
            actions.Items.Clear();
            foreach (var a in cmd.Actions) actions.Items.Add(new ListBoxItem { Content = Describe(a, 0), Tag = a });
        }
        RenderActions();

        var actionPicker = new ComboBox { Width = 320 };
        foreach (var g in ActionTypes.Groups)
        {
            actionPicker.Items.Add(new ComboBoxItem { Content = "— " + g.Key + " —", IsEnabled = false });
            foreach (var a in g) actionPicker.Items.Add(new ComboBoxItem { Content = "   " + a.Title, Tag = a.Type });
        }

        var triggers = new ListBox { MinHeight = 60 };
        void RenderTriggers()
        {
            triggers.Items.Clear();
            foreach (var t in cmd.Triggers) triggers.Items.Add(new ListBoxItem { Content = DescribeTrigger(t), Tag = t });
        }
        RenderTriggers();

        var responses = string.Join("\n", cmd.Responses.Items.Select(r =>
            (r.AudioFile ?? r.Text ?? "") + (Math.Abs(r.Weight - 1) > 1e-9 ? " | " + r.Weight.ToString(CultureInfo.InvariantCulture) : "")));

        var result = Ui.Hint("");

        _editor.Content = Ui.Stack(
            Ui.Card("Команда",
                Ui.Field("Название", cmd.Name, v => cmd.Name = v),
                Ui.Toggle("Включена", cmd.Enabled, v => cmd.Enabled = v),
                Ui.Field("Фразы (по одной в строке)", string.Join("\n", cmd.Phrases), v => cmd.Phrases = Lines(v), true,
                    "Слоты: {число}, {text}. Нечёткое совпадение: окончания, слова-паразиты и оборванные фразы учитываются."),
                Ui.Field("Ответы (по одному в строке, «текст | вес», или путь к своему .wav/.mp3)", responses, v => cmd.Responses.Items = ParseResponses(v), true,
                    "Каждый раз выбирается случайный ответ с учётом веса; подряд не повторяется. Переменные: {time}, {date}, {address}, {число}, {text}."),
                Ui.Toggle("Не повторять ответ подряд", cmd.Responses.AvoidRepeat, v => cmd.Responses.AvoidRepeat = v)),
            Ui.Card("Действия",
                actions,
                Ui.Row(actionPicker,
                    Ui.Btn("Добавить", () =>
                    {
                        if (actionPicker.SelectedItem is not ComboBoxItem { Tag: string type }) return;
                        var step = new ActionStep { Type = type };
                        foreach (var a in ActionTypes.Find(type)!.Args.Where(x => x.Default != null)) step.Args[a.Key] = a.Default!;
                        if (type is ActionTypes.If or ActionTypes.While) { step.Condition = "{hour} >= 9"; step.Then = new(); step.Else = type == ActionTypes.If ? new() : null; }
                        if (type is ActionTypes.Repeat) step.Then = new();
                        if (ActionEditor.Edit(step)) { cmd.Actions.Add(step); RenderActions(); }
                    }, true)),
                Ui.Row(
                    Ui.Btn("Изменить", () => { if (actions.SelectedItem is ListBoxItem { Tag: ActionStep s } && ActionEditor.Edit(s)) RenderActions(); }),
                    Ui.Btn("↑", () => Move(cmd.Actions, actions.SelectedIndex, -1, RenderActions)),
                    Ui.Btn("↓", () => Move(cmd.Actions, actions.SelectedIndex, 1, RenderActions)),
                    Ui.Btn("Дублировать", () => { if (actions.SelectedItem is ListBoxItem { Tag: ActionStep s }) { cmd.Actions.Insert(actions.SelectedIndex + 1, Clone(s)); RenderActions(); } }),
                    Ui.Btn("Удалить", () => { if (actions.SelectedIndex >= 0) { cmd.Actions.RemoveAt(actions.SelectedIndex); RenderActions(); } }),
                    Ui.Btn("⏺ Записать клавиши", () => { var rec = MacroRecorder.Record(); cmd.Actions.AddRange(rec); RenderActions(); }))),
            Ui.Card("Запуск без голоса",
                triggers,
                Ui.Row(
                    Ui.Btn("+ Клавиша", () => { var t = TriggerEditor.Create(TriggerKind.Hotkey); if (t != null) { cmd.Triggers.Add(t); RenderTriggers(); } }),
                    Ui.Btn("+ Расписание", () => { var t = TriggerEditor.Create(TriggerKind.Schedule); if (t != null) { cmd.Triggers.Add(t); RenderTriggers(); } }),
                    Ui.Btn("+ Событие", () => { var t = TriggerEditor.Create(TriggerKind.Event); if (t != null) { cmd.Triggers.Add(t); RenderTriggers(); } }),
                    Ui.Btn("Удалить", () => { if (triggers.SelectedIndex >= 0) { cmd.Triggers.RemoveAt(triggers.SelectedIndex); RenderTriggers(); } })),
                Ui.Hint("Триггеры работают, пока Altair запущен (в трее).")),
            Ui.Card("Параметры",
                Ui.Toggle("Спрашивать подтверждение голосом («да / нет»)", cmd.RequireConfirmation, v => cmd.RequireConfirmation = v),
                Ui.Toggle("Показывать в меню Telegram-бота", cmd.ExposeToTelegram, v => cmd.ExposeToTelegram = v),
                Ui.Field("Описание", cmd.Description ?? "", v => cmd.Description = v)),
            Ui.Row(
                Ui.Btn("💾 Сохранить", () => { var (c, _) = Owner(cmd); if (c != null) { _h.Store.Save(c); _h.RebindHotkeys(); FillTree(); result.Text = "Сохранено " + DateTime.Now.ToString("HH:mm:ss"); } }, true),
                Ui.BtnAsync("▶ Проверить", async () => { result.Text = await _h.Assistant.ExecuteAsync(cmd, null, cmd.Phrases.FirstOrDefault() ?? cmd.Name, "ui"); }),
                Ui.Btn("Проверить фразу…", () =>
                {
                    var phrase = Prompt.Ask("Проверка распознавания", "Введите фразу так, как вы её скажете:");
                    if (string.IsNullOrWhiteSpace(phrase)) return;
                    var chain = _h.Assistant.Matcher.MatchChain(phrase);
                    result.Text = chain.Count == 0 ? "Ни одна команда не подошла — уйдёт в ИИ." :
                        string.Join(" → ", chain.Select(m => $"{m.Command.Name} ({m.Score:0.00}{(m.Slots.Count > 0 ? "; " + string.Join(", ", m.Slots.Select(kv => kv.Key + "=" + kv.Value)) : "")})"));
                })),
            result);
    }

    private static List<string> Lines(string v) => v.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

    private static List<WeightedResponse> ParseResponses(string v) => Lines(v).Select(l =>
    {
        var parts = l.Split('|');
        double w = parts.Length > 1 && double.TryParse(parts[^1].Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 1;
        var body = (parts.Length > 1 ? string.Join('|', parts[..^1]) : l).Trim();
        bool audio = body.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || body.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || body.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase);
        return audio ? new WeightedResponse { AudioFile = body, Weight = w } : new WeightedResponse { Text = body, Weight = w };
    }).ToList();

    private static void Move(List<ActionStep> list, int i, int d, Action render)
    {
        int j = i + d;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        render();
    }

    public static ActionStep Clone(ActionStep s) => JsonSerializer.Deserialize<ActionStep>(JsonSerializer.Serialize(s, Json.Options), Json.Options)!;

    public static string Describe(ActionStep a, int depth)
    {
        var info = ActionTypes.Find(a.Type);
        var pad = new string(' ', depth * 4);
        var args = string.Join(", ", a.Args.Select(kv => $"{kv.Key}={(kv.Value.Length > 40 ? kv.Value[..40] + "…" : kv.Value)}"));
        var line = $"{pad}{info?.Title ?? a.Type}" + (a.Condition != null ? $" [{a.Condition}]" : "") + (args.Length > 0 ? $" ({args})" : "");
        if (a.Then is { Count: > 0 }) line += "\n" + string.Join("\n", a.Then.Select(x => Describe(x, depth + 1)));
        if (a.Else is { Count: > 0 }) line += $"\n{pad}  иначе:\n" + string.Join("\n", a.Else.Select(x => Describe(x, depth + 1)));
        return line;
    }

    public static string DescribeTrigger(Trigger t) => t.Kind switch
    {
        TriggerKind.Hotkey => "⌨ " + t.Hotkey,
        TriggerKind.Schedule => "⏰ " + t.Mode switch
        {
            ScheduleMode.Once => $"однократно {t.At:dd.MM HH:mm}",
            ScheduleMode.EveryMinutes => $"каждые {t.EveryMinutes} мин",
            ScheduleMode.Weekdays => $"по будням в {t.Time:hh\\:mm}",
            ScheduleMode.DaysOfWeek => $"{string.Join(",", t.Days.Select(d => CultureInfo.GetCultureInfo("ru-RU").DateTimeFormat.GetAbbreviatedDayName(d)))} в {t.Time:hh\\:mm}",
            _ => $"ежедневно в {t.Time:hh\\:mm}",
        },
        _ => $"⚡ {t.Event}" + (string.IsNullOrEmpty(t.EventArg) ? "" : $" ({t.EventArg})") + (t.CooldownSec > 0 ? $", не чаще раза в {t.CooldownSec} с" : ""),
    };
}
