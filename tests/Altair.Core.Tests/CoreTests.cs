using Altair.Core.Agent;
using Altair.Core.Engine;
using Altair.Core.Integrations;
using Altair.Core.Models;
using Altair.Core.Plugins;
using Altair.Core.Storage;
using Altair.Core.Triggers;

namespace Altair.Core.Tests;

public class NormalizerTests
{
    [Theory]
    [InlineData("Громкость пятьдесят пять!", "громкость 55")]
    [InlineData("Открой Ёлку, пожалуйста", "открой елку пожалуйста")]
    [InlineData("сто двадцать три", "123")]
    [InlineData("двадцать", "20")]
    public void Normalizes(string input, string expected) => Assert.Equal(expected, PhraseNormalizer.Normalize(input));
}

public class MatcherTests
{
    private static CommandMatcher Matcher(out List<CommandCollection> cols)
    {
        cols = DefaultPacks.All();
        var local = cols;
        return new CommandMatcher(() => local);
    }

    [Theory]
    [InlineData("открой ютуб", "YouTube")]
    [InlineData("открыть ютуб", "YouTube")]            // другое окончание
    [InlineData("ну открой мне ютуб пожалуйста", "YouTube")] // слова-паразиты
    [InlineData("сделай громкость тридцать", "Громкость")]
    [InlineData("который час", "Время")]
    [InlineData("сверни все окна", "Свернуть всё")]
    public void FindsCommand(string phrase, string expected)
    {
        var m = Matcher(out _).MatchChain(phrase);
        Assert.NotEmpty(m);
        Assert.Equal(expected, m[0].Command.Name);
    }

    [Fact]
    public void ExtractsNumberSlot()
    {
        var m = Matcher(out _).MatchChain("громкость сорок два");
        Assert.Equal("42", m[0].Slots["число"]);
    }

    [Fact]
    public void ExtractsTextSlot()
    {
        var m = Matcher(out _).MatchChain("найди рецепт борща");
        Assert.Equal("Поиск в интернете", m[0].Command.Name);
        Assert.Equal("рецепт борща", m[0].Slots["text"]);
    }

    [Fact]
    public void ChainsCommands()
    {
        var m = Matcher(out _).MatchChain("открой ютуб и сделай громкость 20");
        Assert.Equal(2, m.Count);
        Assert.Equal("YouTube", m[0].Command.Name);
        Assert.Equal("Громкость", m[1].Command.Name);
    }

    [Fact]
    public void DoesNotMatchNoise()
    {
        Assert.Empty(Matcher(out _).MatchChain("сегодня хорошая погода на улице"));
    }

    [Fact]
    public void ToleratesCutPhrase()
    {
        // «установи громкость» без последнего слова-числа не должна сработать как Громкость (слот обязателен)
        var r = FuzzyMatcher.Match("открой проводник", "открой проводник");
        Assert.NotNull(r);
        var cut = FuzzyMatcher.Match("покажи рабочий", "покажи рабочий стол");
        Assert.NotNull(cut); // оборвано последнее слово
    }
}

public class ExpressionTests
{
    [Theory]
    [InlineData("{hour} >= 22 && {battery} < 30", true)]
    [InlineData("{hour} >= 22 и {battery} > 30", false)]
    [InlineData("{name} == \"Артём\"", true)]
    [InlineData("{name} contains \"рт\"", true)]
    [InlineData("!({battery} < 10)", true)]
    [InlineData("(1 + 2) * 3 == 9", true)]
    public void Evaluates(string expr, bool expected)
    {
        var v = new VariableStore();
        v.Set("hour", "23"); v.Set("battery", "15"); v.Set("name", "Артём");
        Assert.Equal(expected, Expr.IsTrue(expr, v));
    }

    [Fact]
    public void ExpandsVariables()
    {
        var v = new VariableStore(); v.Set("x", "мир");
        Assert.Equal("привет мир {нет}", v.Expand("привет {x} {нет}"));
    }
}

public class RunnerTests
{
    [Fact]
    public async Task RunsIfRepeatAndVars()
    {
        var reg = new ActionRegistry();
        var said = new List<string>();
        reg.Register(ActionTypes.Say, (s, c) => { said.Add(c.Arg(s, "text")); return Task.CompletedTask; });
        var runner = new ActionRunner(reg, _ => null);
        var steps = new List<ActionStep>
        {
            ActionStep.Of(ActionTypes.SetVar, ("name", "n"), ("value", "2 + 3")),
            new() { Type = ActionTypes.If, Condition = "{n} == 5",
                Then = new() { new ActionStep { Type = ActionTypes.Repeat, Args = { ["count"] = "3" }, Then = new() { ActionStep.Of(ActionTypes.Say, ("text", "шаг {i}")) } } },
                Else = new() { ActionStep.Of(ActionTypes.Say, ("text", "нет")) } }
        };
        await runner.RunAsync(steps, new RunContext { Vars = new VariableStore() });
        Assert.Equal(new[] { "шаг 1", "шаг 2", "шаг 3" }, said);
    }

    [Fact]
    public async Task ContinueOnErrorSetsErrorVar()
    {
        var reg = new ActionRegistry();
        reg.Register("x.fail", (_, _) => throw new InvalidOperationException("boom"));
        var vars = new VariableStore();
        var runner = new ActionRunner(reg, _ => null);
        await runner.RunAsync(new[] { new ActionStep { Type = "x.fail", ContinueOnError = true } }, new RunContext { Vars = vars });
        Assert.Equal("boom", vars.Get("error"));
    }
}

public class ResponseTests
{
    [Fact]
    public void AvoidsImmediateRepeat()
    {
        var pool = new ResponsePool { Items = { new() { Text = "А" }, new() { Text = "Б" } } };
        var p = new ResponsePicker(new Random(1));
        string? prev = null;
        for (int i = 0; i < 50; i++) { var r = p.Pick(pool)!.Text; Assert.NotEqual(prev, r); prev = r; }
    }
}

public class ScheduleTests
{
    [Fact]
    public void WeekdaysSkipWeekend()
    {
        var t = new Trigger { Kind = TriggerKind.Schedule, Mode = ScheduleMode.Weekdays, Time = new TimeSpan(9, 0, 0) };
        var fri = new DateTime(2026, 11, 6, 10, 0, 0); // пятница после 9:00
        Assert.Equal(new DateTime(2026, 11, 9, 9, 0, 0), ScheduleMath.Next(t, fri)); // понедельник
    }

    [Fact]
    public void CatchesUpWithinTenMinutes()
    {
        var cmd = new VoiceCommand { Name = "x" };
        var trg = new Trigger { Kind = TriggerKind.Schedule, Mode = ScheduleMode.Daily, Time = new TimeSpan(9, 0, 0) };
        cmd.Triggers.Add(trg);
        var s = new Scheduler(() => new[] { (cmd, trg) });
        int fired = 0; s.Fired += (_, _) => fired++;
        s.Rebuild(new DateTime(2026, 11, 3, 8, 0, 0));
        s.Tick(new DateTime(2026, 11, 3, 9, 7, 0)); // опоздали на 7 минут — догоняем
        Assert.Equal(1, fired);
        s.Tick(new DateTime(2026, 11, 3, 9, 8, 0));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void EventCooldown()
    {
        var cmd = new VoiceCommand();
        var trg = new Trigger { Kind = TriggerKind.Event, Event = SystemEvents.PowerPlugged, CooldownSec = 60 };
        var hub = new EventTriggerHub(() => new[] { (cmd, trg) });
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        Assert.Equal(1, hub.Raise(SystemEvents.PowerPlugged, null, t0));
        Assert.Equal(0, hub.Raise(SystemEvents.PowerPlugged, null, t0.AddSeconds(30)));
        Assert.Equal(1, hub.Raise(SystemEvents.PowerPlugged, null, t0.AddSeconds(61)));
    }
}

public class AssistantTests
{
    [Theory]
    [InlineData("альтаир открой ютуб", true, "открой ютуб")]
    [InlineData("эй альтаир громче", true, "громче")]
    [InlineData("алтаир", true, "")]
    [InlineData("открой ютуб", false, "открой ютуб")]
    public void StripsWakeWord(string input, bool wake, string rest)
    {
        var (w, r) = Assistant.StripWakeWord(PhraseNormalizer.Normalize(input), new[] { "альтаир" });
        Assert.Equal(wake, w);
        Assert.Equal(rest, r);
    }
}

public class AgentTests
{
    [Fact]
    public void ClassifiesRisk()
    {
        Assert.Equal(Risk.Safe, AgentRunner.Classify(ActionTypes.OpenUrl, new Dictionary<string, string> { ["url"] = "https://ya.ru" }));
        Assert.Equal(Risk.Important, AgentRunner.Classify(ActionTypes.BrowserClick, new Dictionary<string, string> { ["label"] = "Отправить" }));
        Assert.Equal(Risk.Critical, AgentRunner.Classify(ActionTypes.BrowserClick, new Dictionary<string, string> { ["label"] = "Оплатить заказ" }));
        Assert.True(AgentRunner.NeedsConfirm(ConfirmMode.FullAccess, Risk.Critical));
        Assert.False(AgentRunner.NeedsConfirm(ConfirmMode.Auto, Risk.Important));
    }

    [Fact]
    public void ParsesFencedJson()
    {
        var o = AgentRunner.ParseJson("```json\n{\"done\":true,\"answer\":\"ок\"}\n```");
        Assert.Equal("ок", o["answer"]!.ToString());
    }
}

public class PluginTests
{
    [Fact]
    public void ExpandsSnippet()
    {
        var x = new TextExpander { Snippets = { [";адр"] = "г. Алматы, ул. Абая 1" } };
        (int, string)? hit = null;
        foreach (var c in "мой ;адр") hit = x.Feed(c) ?? hit;
        Assert.Equal((4, "г. Алматы, ул. Абая 1"), hit);
    }

    [Fact]
    public void Bm25FindsRelevantChunk()
    {
        var idx = new DocIndex();
        idx.AddDocument("a.txt", "Договор аренды помещения на улице Абая, арендная плата 300 000 тенге.");
        idx.AddDocument("b.txt", "Рецепт плова: рис, морковь, баранина.");
        var r = idx.Search("сколько арендная плата");
        Assert.Equal("a.txt", r[0].Chunk.File);
    }

    [Fact]
    public void ClipboardDedupAndCapacity()
    {
        var h = new ClipboardHistory { Capacity = 3 };
        h.Add("1"); h.Add("2"); h.Add("1"); h.Add("3"); h.Add("4");
        Assert.Equal(new[] { "4", "3", "1" }, h.Items.Select(i => i.Text));
    }

    [Fact]
    public void ObsAuthIsDeterministic()
    {
        Assert.Equal(ObsClient.AuthString("p", "s", "c"), ObsClient.AuthString("p", "s", "c"));
    }

    [Fact]
    public void ParsesTwitchLine()
    {
        var m = TwitchChat.Parse(":vasya!vasya@vasya.tmi.twitch.tv PRIVMSG #chan :привет всем");
        Assert.Equal("vasya", m!.User);
        Assert.Equal("привет всем", m.Text);
    }
}

public class CatalogTests
{
    [Fact]
    public void CatalogHasUniqueTypesAndEnoughActions()
    {
        Assert.Equal(ActionTypes.Catalog.Count, ActionTypes.Catalog.Select(a => a.Type).Distinct().Count());
        Assert.True(ActionTypes.Catalog.Count >= 80);
    }

    [Fact]
    public void PacksUseOnlyKnownActions()
    {
        foreach (var cmd in DefaultPacks.All().SelectMany(c => c.Folders).SelectMany(f => f.Commands))
            foreach (var a in cmd.Actions)
                Assert.NotNull(ActionTypes.Find(a.Type));
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var s = new AppSettings { AddressAs = "босс" };
        Json.Save(path, s);
        Assert.Equal("босс", Json.Load<AppSettings>(path).AddressAs);
    }
}
