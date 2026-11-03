using Altair.Core.Models;
using A = Altair.Core.Models.ActionTypes;

namespace Altair.Core.Storage;

/// <summary>Встроенные паки команд. Создаются при первом запуске; пользователь может включать/выключать их.</summary>
public static class DefaultPacks
{
    private static VoiceCommand C(string name, string[] phrases, string[] replies, params ActionStep[] actions)
    {
        var c = new VoiceCommand { Name = name, Phrases = phrases.ToList(), Actions = actions.ToList() };
        foreach (var r in replies) c.Responses.Items.Add(new WeightedResponse { Text = r, Weight = 1 });
        return c;
    }

    private static ActionStep S(string type, params (string, string)[] args) => ActionStep.Of(type, args);

    private static readonly string[] Ok = { "Выполняю.", "Есть, {address}.", "Готово.", "Сделано." };
    private static readonly string[] Opening = { "Открываю.", "Запускаю, {address}.", "Секунду." };

    private static CommandCollection Pack(string id, string name, params (string Folder, VoiceCommand[] Cmds)[] folders) => new()
    {
        Id = "pack-" + id, PackId = id, Name = name,
        Folders = folders.Select((f, i) => new CommandFolder { Id = $"pack-{id}-{i}", Name = f.Folder, Commands = f.Cmds.Select((c, j) => { c.Id = $"pack-{id}-{i}-{j}"; return c; }).ToList() }).ToList()
    };

    private static VoiceCommand Site(string name, string url, params string[] phrases) =>
        C(name, phrases, Opening, S(A.OpenUrl, ("url", url)));

    private static VoiceCommand App(string name, string path, params string[] phrases) =>
        C(name, phrases, Opening, S(A.OpenApp, ("path", path)));

    public static List<CommandCollection> All() => new()
    {
        Pack("base", "Базовые",
            ("Браузер и сайты", new[]
            {
                App("Браузер", "browser", "открой браузер", "запусти браузер", "открой хром"),
                Site("YouTube", "https://youtube.com", "открой ютуб", "запусти youtube", "включи ютуб"),
                Site("Почта", "https://mail.google.com", "открой почту", "проверь почту"),
                Site("Карты", "https://yandex.ru/maps", "открой карты", "покажи карту"),
                Site("Переводчик", "https://translate.yandex.ru", "открой переводчик"),
                C("Поиск в интернете", new[] { "найди {text}", "найди в интернете {text}", "поищи {text}", "загугли {text}" }, new[] { "Ищу.", "Вот что нашлось." },
                    S(A.WebSearch, ("query", "{text}"), ("engine", "yandex"))),
                C("Поиск на YouTube", new[] { "найди на ютубе {text}", "включи на ютубе {text}" }, new[] { "Ищу на YouTube." },
                    S(A.OpenUrl, ("url", "https://www.youtube.com/results?search_query={text}"))),
            }),
            ("Звук и музыка", new[]
            {
                C("Громкость", new[] { "громкость {число}", "сделай громкость {число}", "установи громкость {число}" }, new[] { "Громкость {число}." }, S(A.VolumeSet, ("value", "{число}"))),
                C("Громче", new[] { "громче", "сделай громче", "прибавь звук" }, Array.Empty<string>(), S(A.VolumeUp, ("step", "10"))),
                C("Тише", new[] { "тише", "сделай тише", "убавь звук" }, Array.Empty<string>(), S(A.VolumeDown, ("step", "10"))),
                C("Без звука", new[] { "выключи звук", "отключи звук", "mute" }, Array.Empty<string>(), S(A.Mute)),
                C("Пауза", new[] { "пауза", "поставь на паузу", "продолжи", "играй" }, Array.Empty<string>(), S(A.MediaPlayPause)),
                C("Следующий трек", new[] { "следующий трек", "следующая песня", "дальше" }, Array.Empty<string>(), S(A.MediaNext)),
                C("Предыдущий трек", new[] { "предыдущий трек", "предыдущая песня", "назад трек" }, Array.Empty<string>(), S(A.MediaPrev)),
            }),
            ("Окна", new[]
            {
                C("Свернуть всё", new[] { "сверни все", "покажи рабочий стол" }, Ok, S(A.MinimizeAll)),
                C("Свернуть окно", new[] { "сверни окно", "сверни" }, Array.Empty<string>(), S(A.WindowMinimize, ("window", "active"))),
                C("Развернуть окно", new[] { "разверни окно", "на весь экран" }, Array.Empty<string>(), S(A.WindowMaximize, ("window", "active"))),
                C("Закрыть окно", new[] { "закрой окно", "закрой это" }, Ok, S(A.WindowClose, ("window", "active"))),
                C("Переключить окно", new[] { "переключись на {text}", "перейди в {text}" }, Array.Empty<string>(), S(A.WindowFocus, ("window", "{text}"))),
                C("Окно влево", new[] { "окно влево", "прижми влево" }, Array.Empty<string>(), S(A.WindowSnap, ("side", "left"))),
                C("Окно вправо", new[] { "окно вправо", "прижми вправо" }, Array.Empty<string>(), S(A.WindowSnap, ("side", "right"))),
            }),
            ("Система", new[]
            {
                C("Время", new[] { "сколько времени", "который час", "время" }, new[] { "Сейчас {time}.", "{time}, {address}." }),
                C("Дата", new[] { "какое сегодня число", "какая дата", "какой сегодня день" }, new[] { "Сегодня {date}." }),
                C("Заблокировать", new[] { "заблокируй компьютер", "заблокируй экран" }, Ok, S(A.Lock)),
                C("Сон", new[] { "спящий режим", "переведи в сон" }, new[] { "Спокойной ночи." }, S(A.Sleep)),
                new VoiceCommand { Name = "Выключить ПК", Phrases = { "выключи компьютер", "заверши работу" }, RequireConfirmation = true,
                    Actions = { S(A.Shutdown, ("delay", "10")) }, Responses = { Items = { new() { Text = "Выключаю через десять секунд." } } } },
                new VoiceCommand { Name = "Перезагрузка", Phrases = { "перезагрузи компьютер", "перезагрузка" }, RequireConfirmation = true,
                    Actions = { S(A.Restart) }, Responses = { Items = { new() { Text = "Перезагружаю." } } } },
                C("Сводка системы", new[] { "как дела у компьютера", "состояние системы", "нагрузка" }, Array.Empty<string>(), S(A.SystemInfo, ("what", "all"))),
                C("Скриншот", new[] { "сделай скриншот", "скриншот", "сфоткай экран" }, new[] { "Снимок готов." }, S(A.Screenshot, ("region", "full"))),
                C("Очистить корзину", new[] { "очисти корзину" }, Ok, S(A.EmptyRecycleBin)),
                C("Диспетчер задач", new[] { "открой диспетчер задач", "диспетчер задач" }, Opening, S(A.KeyCombo, ("combo", "Ctrl+Shift+Esc"))),
                C("Проводник", new[] { "открой проводник", "мой компьютер" }, Opening, S(A.KeyCombo, ("combo", "Win+E"))),
                C("Параметры", new[] { "открой параметры", "настройки windows" }, Opening, S(A.KeyCombo, ("combo", "Win+I"))),
                C("Загрузки", new[] { "открой загрузки", "папка загрузки" }, Opening, S(A.OpenFolder, ("path", "%USERPROFILE%\\Downloads"))),
                C("Яркость", new[] { "яркость {число}" }, new[] { "Яркость {число}." }, S(A.Brightness, ("value", "{число}"))),
            }),
            ("Нейросеть", new[]
            {
                C("Перескажи буфер", new[] { "перескажи текст", "кратко перескажи", "что тут написано" }, Array.Empty<string>(), S(A.AiSummarizeClipboard)),
                C("Переведи выделенное", new[] { "переведи на английский", "переведи текст" }, Array.Empty<string>(), S(A.Translate, ("to", "en"))),
                C("Вопрос ИИ", new[] { "вопрос {text}", "спроси нейросеть {text}" }, Array.Empty<string>(), S(A.AiAsk, ("prompt", "{text}"), ("say", "true"))),
                C("Что на экране", new[] { "что на экране", "прочитай экран" }, Array.Empty<string>(),
                    S(A.ReadScreen, ("var", "screen")), S(A.AiAsk, ("prompt", "Кратко опиши, что на экране, по распознанному тексту: {screen}"), ("say", "true"))),
            }),
            ("Пример сценария", new[]
            {
                new VoiceCommand
                {
                    Name = "Доброе утро", Phrases = { "доброе утро", "я проснулся" },
                    Actions =
                    {
                        new ActionStep { Type = A.If, Condition = "{hour} < 12",
                            Then = new() { S(A.Say, ("text", "Доброе утро, {address}. Сейчас {time}.")), S(A.OpenUrl, ("url", "https://yandex.ru/pogoda")) },
                            Else = new() { S(A.Say, ("text", "Уже {time}, {address}. Утро давно прошло.")) } },
                    }
                },
            })),

        Pack("dev", "Разработчик",
            ("Инструменты", new[]
            {
                App("VS Code", "code", "открой код", "открой vs code", "запусти редактор"),
                App("Терминал", "wt", "открой терминал", "запусти терминал"),
                Site("GitHub", "https://github.com", "открой гитхаб"),
                C("Мой IP", new[] { "мой ip", "какой у меня ip" }, new[] { "Ваш адрес: {ip}" }, S(A.RunPowerShell, ("script", "(Invoke-RestMethod https://api.ipify.org)"), ("var", "ip"))),
                C("Пинг", new[] { "проверь интернет", "пинг" }, new[] { "{ping}" },
                    S(A.RunPowerShell, ("script", "if (Test-Connection 8.8.8.8 -Count 2 -Quiet) { 'Интернет работает' } else { 'Интернета нет' }"), ("var", "ping"))),
            })),

        Pack("games", "Игры",
            ("Steam", new[]
            {
                App("Steam", "steam://open/main", "открой стим", "запусти steam"),
                App("Библиотека Steam", "steam://open/games", "открой библиотеку игр"),
                C("Игровой режим", new[] { "игровой режим", "режим игры" }, new[] { "Игровой режим включён." }, S(A.KeyCombo, ("combo", "Win+G"))),
                App("Discord", "discord", "открой дискорд"),
            })),

        Pack("office", "Офис",
            ("Документы", new[]
            {
                App("Word", "winword", "открой ворд", "открой word"),
                App("Excel", "excel", "открой эксель", "открой excel"),
                App("Блокнот", "notepad", "открой блокнот"),
                App("Калькулятор", "calc", "открой калькулятор", "калькулятор"),
                C("Заметка", new[] { "запиши {text}", "заметка {text}" }, new[] { "Записал." },
                    S(A.FileWrite, ("path", "%USERPROFILE%\\Documents\\Заметки Altair.txt"), ("text", "{date} {time}: {text}"), ("append", "true"))),
            })),

        Pack("messengers", "Мессенджеры",
            ("Связь", new[]
            {
                App("Telegram", "telegram", "открой телеграм", "открой telegram"),
                App("WhatsApp", "whatsapp:", "открой ватсап", "открой whatsapp"),
                Site("ВКонтакте", "https://vk.com", "открой вконтакте", "открой вк"),
            })),

        Pack("creative", "Творчество",
            ("Программы", new[]
            {
                App("Photoshop", "photoshop", "открой фотошоп"),
                App("OBS", "obs64", "открой обс", "запусти obs"),
                App("Paint", "mspaint", "открой пейнт"),
                Site("Figma", "https://figma.com", "открой фигму"),
            })),
    };
}
