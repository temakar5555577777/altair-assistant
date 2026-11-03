using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Altair.Core.Agent;
using Altair.Core.AI;
using Altair.Core.Models;
using Altair.Core.Storage;
using Altair.Windows.Screen;

namespace Altair.App.Views;

/// <summary>Нейросеть: чат (бесплатно с любым ключом или локальной моделью), провайдеры, прозрачный расход.</summary>
public sealed class AiPage : UserControl
{
    private readonly AppHost _h = AppHost.Current;
    private readonly StackPanel _messages = new();
    private readonly ScrollViewer _scroll;

    public AiPage()
    {
        var s = _h.Settings;
        _scroll = new ScrollViewer { Content = _messages, Height = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        foreach (var m in _h.Chat.History) AddBubble(m.Role == Role.User, m.Text);

        var input = new TextBox { AcceptsReturn = false, MinHeight = 34 };
        var withScreen = new CheckBox { Content = "Показать нейросети экран" };
        async Task Send()
        {
            var text = input.Text.Trim(); if (text.Length == 0) return;
            input.Text = ""; AddBubble(true, text);
            var thinking = AddBubble(false, "…");
            try
            {
                IEnumerable<ImagePart>? images = null;
                if (withScreen.IsChecked == true) { using var bmp = ScreenCapture.Capture(); images = new[] { new ImagePart(ScreenCapture.JpegForAi(bmp), "image/jpeg") }; }
                thinking.Text = await _h.Chat.Ask(text, default, images);
            }
            catch (Exception ex) { thinking.Text = "⚠ " + ex.Message; thinking.Foreground = Ui.Res("Danger"); }
        }
        input.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) await Send(); };

        var providers = new StackPanel();
        foreach (var p in s.Providers) providers.Children.Add(ProviderCard(p));

        var usage = Ui.Text("");
        void RenderUsage()
        {
            var d = _h.Usage.Today;
            usage.Text = d.Requests.Count == 0 ? "Сегодня запросов не было." :
                string.Join("\n", d.Requests.Select(kv => $"{s.Provider(kv.Key)?.Name ?? kv.Key}: {kv.Value} запросов, {d.TokensIn.GetValueOrDefault(kv.Key) + d.TokensOut.GetValueOrDefault(kv.Key):N0} токенов"));
        }
        RenderUsage();

        Content = Ui.Page(
            Ui.H1("Нейросеть"),
            Ui.Card("Чат",
                _scroll,
                Ui.Columns((input, 1), (Ui.BtnAsync("Отправить", Send, true), 0)),
                Ui.Row(withScreen, Ui.Btn("Очистить диалог", () => { _h.Chat.ClearHistory(); _messages.Children.Clear(); }),
                    Ui.Btn("Озвучить последний ответ", () => { var last = _h.Chat.History.LastOrDefault(m => m.Role == Role.Assistant); if (last != null) _ = _h.Tts.SayAsync(last.Text); }))),
            Ui.Card("Поведение",
                Ui.Combo("Основной провайдер", s.Providers, s.Provider() ?? s.Providers[0], p => { s.ActiveProvider = p.Id; _h.SaveSettings(); }, p => p.Name),
                Ui.Toggle("Нераспознанные фразы отправлять в ИИ", s.AiFallbackForUnknown, v => { s.AiFallbackForUnknown = v; _h.SaveSettings(); }),
                Ui.Field("Характер ассистента (системный промпт). {address} — обращение", s.SystemPrompt, v => { s.SystemPrompt = v; _h.SaveSettings(); }, true),
                Ui.SliderField("Память диалога, сообщений", 2, 60, s.ChatHistoryLimit, v => { s.ChatHistoryLimit = (int)v; _h.SaveSettings(); }),
                Ui.Field("Свой дневной лимит запросов (0 — без ограничений)", s.DailyRequestSoftLimit.ToString(), v => { if (int.TryParse(v, out var n)) { s.DailyRequestSoftLimit = n; _h.SaveSettings(); } })),
            Ui.Card("Расход сегодня", usage, Ui.Hint("Счётчик честный: каждый запрос и токены по провайдерам. При ошибке основного провайдера запрос автоматически уходит в следующий включённый."), Ui.Btn("Обновить", RenderUsage)),
            Ui.H2("Провайдеры"),
            Ui.Hint("Свой ключ — без лимитов Altair. Локальные Ollama и LM Studio работают бесплатно и без интернета."),
            providers);
    }

    private TextBlock AddBubble(bool user, string text)
    {
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 640 };
        var b = new Border
        {
            Child = tb, CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        b.SetResourceReference(Border.BackgroundProperty, user ? "Accent" : "Panel");
        if (user) tb.SetResourceReference(TextBlock.ForegroundProperty, "AccentFg");
        _messages.Children.Add(b);
        _scroll.ScrollToEnd();
        return tb;
    }

    private Border ProviderCard(AiProviderConfig p)
    {
        var status = Ui.Hint("");
        var card = Ui.Card(p.Name,
            Ui.Toggle("Включён", p.Enabled, v => { p.Enabled = v; _h.SaveSettings(); }),
            Ui.Secret(p.Kind == "gigachat" ? "Ключ авторизации (Base64)" : "API-ключ", p.ApiKey, v => { p.ApiKey = v.Trim(); _h.SaveSettings(); }),
            Ui.Field("Модель", p.Model, v => { p.Model = v.Trim(); _h.SaveSettings(); }),
            Ui.Field("Модель для изображений (пусто — нет зрения)", p.VisionModel ?? "", v => { p.VisionModel = string.IsNullOrWhiteSpace(v) ? null : v.Trim(); _h.SaveSettings(); }),
            Ui.Field("Адрес API", p.BaseUrl, v => { p.BaseUrl = v.Trim(); _h.SaveSettings(); }, hint: "ProxyAPI: https://api.proxyapi.ru/openai/v1 · AITUNNEL: https://api.aitunnel.ru/v1"),
            p.Kind == "yandex" ? Ui.Field("Folder ID (Yandex Cloud)", p.FolderId ?? "", v => { p.FolderId = v.Trim(); _h.SaveSettings(); }) : new StackPanel(),
            Ui.Row(Ui.BtnAsync("Проверить", async () =>
            {
                status.Text = "Проверяю…";
                _h.Chat.InvalidateProviders();
                var r = await ProviderFactory.Create(p).CompleteAsync(new[] { ChatMessage.User("Ответь одним словом: работает?") }, new ChatOptions(MaxTokens: 20), default);
                status.Text = $"✓ {r.Model}: {r.Text}";
            })),
            status);
        return card;
    }
}

/// <summary>ИИ-агент: план, шаги, подтверждения, остановка/пропуск/уточнение, сохранение сценария как команды.</summary>
public sealed class AgentPage : UserControl
{
    private readonly AppHost _h = AppHost.Current;

    public AgentPage()
    {
        var s = _h.Settings;
        var task = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, Text = "" };
        var plan = new ListBox { MinHeight = 90 };
        var steps = new ListBox { MinHeight = 160 };
        var answer = Ui.Text("");
        var extra = new TextBox();

        Action<IReadOnlyList<string>> onPlan = p => Dispatcher.BeginInvoke(() => { plan.Items.Clear(); for (int i = 0; i < p.Count; i++) plan.Items.Add($"{i + 1}. {p[i]}"); });
        Action<AgentStep, int, int> onStart = (st, i, n) => Dispatcher.BeginInvoke(() => steps.Items.Add($"▶ Шаг {i}: {AgentRunner.Describe(st)}  — {st.Thought}"));
        Action<AgentStep> onDone = st => Dispatcher.BeginInvoke(() =>
        {
            if (steps.Items.Count > 0) steps.Items[^1] = $"{(st.Skipped ? "⤼" : st.Ok ? "✓" : "✗")} Шаг {st.Index}: {AgentRunner.Describe(st)}\n    {st.Result}";
        });
        Action<string> onFinish = r => Dispatcher.BeginInvoke(() => answer.Text = r);
        _h.Agent.PlanUpdated += onPlan; _h.Agent.StepStarted += onStart; _h.Agent.StepFinished += onDone; _h.Agent.Finished += onFinish;
        Unloaded += (_, _) => { _h.Agent.PlanUpdated -= onPlan; _h.Agent.StepStarted -= onStart; _h.Agent.StepFinished -= onDone; _h.Agent.Finished -= onFinish; };

        Content = Ui.Page(
            Ui.H1("ИИ-агент"),
            Ui.Hint("Агент сам работает в программах: смотрит на экран (скриншот, распознанный текст и элементы окна), составляет план и выполняет его по шагам. Перед важными действиями спрашивает разрешения."),
            Ui.Card("Задание",
                task,
                Ui.Row(
                    Ui.BtnAsync("▶ Выполнить", async () =>
                    {
                        plan.Items.Clear(); steps.Items.Clear(); answer.Text = "Работаю…";
                        answer.Text = await _h.RunAgentAsync(task.Text);
                    }, true),
                    Ui.Btn("⏹ Стоп", () => _h.Agent.Stop()),
                    Ui.Btn("⤼ Пропустить шаг", () => _h.Agent.SkipCurrentStep())),
                Ui.Columns((extra, 1), (Ui.Btn("Добавить указание", () => { if (extra.Text.Length > 0) { _h.Agent.AddInstruction(extra.Text); extra.Text = ""; } }), 0)),
                answer),
            Ui.Card("План", plan),
            Ui.Card("Шаги", steps,
                Ui.Btn("💾 Сохранить как команду", () =>
                {
                    if (_h.Agent.Steps.Count(x => x.Ok) == 0) { Ui.Info("Нет успешных шагов для сохранения"); return; }
                    var phrase = Prompt.Ask("Команда агента", "Фраза, по которой запускать этот сценарий без нейросети:", task.Text.Length < 40 ? task.Text : "");
                    if (string.IsNullOrWhiteSpace(phrase)) return;
                    var cmd = _h.Agent.ToCommand(phrase, phrase);
                    var col = _h.Store.Collections.FirstOrDefault(c => c.Id == "agent-commands") ?? new CommandCollection { Id = "agent-commands", Name = "Команды агента", Folders = { new CommandFolder { Name = "Сценарии" } } };
                    if (!_h.Store.Collections.Contains(col)) _h.Store.Collections.Add(col);
                    col.Folders[0].Commands.Add(cmd);
                    _h.Store.Save(col);
                    Ui.Info("Сохранено в «Команды агента».");
                })),
            Ui.Card("Подтверждения",
                Ui.Combo("Режим", new[] { ConfirmMode.All, ConfirmMode.Important, ConfirmMode.Auto, ConfirmMode.FullAccess }, s.AgentConfirm, v => { s.AgentConfirm = v; _h.SaveSettings(); },
                    v => v switch { ConfirmMode.All => "Спрашивать всё", ConfirmMode.Important => "Только важное (рекомендуется)", ConfirmMode.Auto => "Авто", _ => "Полный доступ" }),
                Ui.Hint("Оплата и ввод паролей подтверждаются всегда и только кнопкой — голосом их можно лишь отклонить."),
                Ui.SliderField("Предел шагов", 5, 60, s.AgentMaxSteps, v => { s.AgentMaxSteps = (int)v; _h.SaveSettings(); }),
                Ui.Toggle("Отправлять скриншот экрана (нужна модель с поддержкой изображений)", s.AgentScreenVision, v => { s.AgentScreenVision = v; _h.SaveSettings(); }),
                Ui.Hint(_h.Browser.Connected ? $"Расширение браузера подключено ({_h.Browser.BrowserName})" : "Расширение браузера не подключено — агент работает с сайтами через экран. Установка: «Расширения → Браузер»."))
        );
    }
}
