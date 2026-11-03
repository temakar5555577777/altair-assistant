namespace Altair.Core.Models;

public enum Tier { Free, Plus }

public sealed record ArgSpec(string Key, string Title, string Kind = "text", string? Default = null, bool Optional = false);

public sealed record ActionInfo(string Type, string Group, string Title, Tier Tier, params ArgSpec[] Args);

/// <summary>Каталог действий редактора. 80 действий в 16 группах (+ управляющие конструкции).</summary>
public static class ActionTypes
{
    // Управление сценарием (нет у образца: условия, переменные, циклы)
    public const string If = "flow.if";
    public const string Repeat = "flow.repeat";
    public const string While = "flow.while";
    public const string SetVar = "flow.setvar";
    public const string Wait = "flow.wait";
    public const string Stop = "flow.stop";
    public const string RunCommand = "flow.runcommand";
    public const string AskUser = "flow.ask";

    // Запуск
    public const string OpenApp = "launch.app";
    public const string OpenUrl = "launch.url";
    public const string OpenFile = "launch.file";
    public const string OpenFolder = "launch.folder";
    public const string CloseApp = "launch.close";
    public const string WebSearch = "launch.search";

    // Клавиши
    public const string KeyPress = "keys.press";
    public const string KeyCombo = "keys.combo";
    public const string KeyHold = "keys.hold";
    public const string TypeText = "keys.type";

    // Мышь
    public const string MouseMove = "mouse.move";
    public const string MouseClick = "mouse.click";
    public const string MouseScroll = "mouse.scroll";
    public const string MouseDrag = "mouse.drag";

    // Экран
    public const string ClickText = "screen.clicktext";
    public const string WaitForText = "screen.waittext";
    public const string ReadScreen = "screen.read";
    public const string Screenshot = "screen.shot";
    public const string ClickElement = "screen.clickelement";

    // Окна
    public const string WindowFocus = "window.focus";
    public const string WindowMinimize = "window.min";
    public const string WindowMaximize = "window.max";
    public const string WindowClose = "window.close";
    public const string WindowLayout = "window.layout";
    public const string WindowSnap = "window.snap";
    public const string MinimizeAll = "window.minall";

    // Озвучка
    public const string Say = "voice.say";
    public const string PlaySound = "voice.play";
    public const string Notify = "voice.notify";

    // Звук
    public const string VolumeSet = "sound.set";
    public const string VolumeUp = "sound.up";
    public const string VolumeDown = "sound.down";
    public const string Mute = "sound.mute";
    public const string MediaPlayPause = "sound.playpause";
    public const string MediaNext = "sound.next";
    public const string MediaPrev = "sound.prev";
    public const string AudioDevice = "sound.device";
    public const string MicMute = "sound.micmute";

    // Система
    public const string Shutdown = "sys.shutdown";
    public const string Restart = "sys.restart";
    public const string Sleep = "sys.sleep";
    public const string Lock = "sys.lock";
    public const string Brightness = "sys.brightness";
    public const string RunCmd = "sys.cmd";
    public const string RunPowerShell = "sys.ps";
    public const string RunPython = "sys.python";
    public const string KillProcess = "sys.kill";
    public const string SystemInfo = "sys.info";
    public const string EmptyRecycleBin = "sys.recycle";
    public const string PowerPlan = "sys.powerplan";

    // Браузер
    public const string BrowserNewTab = "browser.newtab";
    public const string BrowserCloseTab = "browser.closetab";
    public const string BrowserClick = "browser.click";
    public const string BrowserFill = "browser.fill";
    public const string BrowserReadPage = "browser.read";

    // Текст
    public const string ClipboardSet = "text.clipset";
    public const string ClipboardGet = "text.clipget";
    public const string Paste = "text.paste";
    public const string Dictate = "text.dictate";
    public const string Translate = "text.translate";

    // Файлы
    public const string FileCopy = "file.copy";
    public const string FileMove = "file.move";
    public const string FileDelete = "file.delete";
    public const string FileWrite = "file.write";
    public const string FindFile = "file.find";

    // Нейросеть
    public const string AiAsk = "ai.ask";
    public const string AiAgent = "ai.agent";
    public const string AiSummarizeClipboard = "ai.summarize";

    // Умный дом
    public const string HomeDevice = "home.device";
    public const string HomeScene = "home.scene";

    // Второй ПК
    public const string RemoteRun = "remote.run";
    public const string RemoteSay = "remote.say";

    // Интеграции
    public const string Telegram = "net.telegram";
    public const string HttpRequest = "net.http";
    public const string McpTool = "net.mcp";
    public const string ObsAction = "stream.obs";

    public static readonly IReadOnlyList<ActionInfo> Catalog = new List<ActionInfo>
    {
        new(If, "Сценарий", "Если … иначе", Tier.Free, new ArgSpec("—", "Условие задаётся в поле Condition")),
        new(Repeat, "Сценарий", "Повторить N раз", Tier.Free, new ArgSpec("count", "Сколько раз", "number", "3")),
        new(While, "Сценарий", "Пока условие", Tier.Free, new ArgSpec("max", "Предел итераций", "number", "100")),
        new(SetVar, "Сценарий", "Задать переменную", Tier.Free, new ArgSpec("name", "Имя"), new ArgSpec("value", "Значение / выражение")),
        new(Wait, "Сценарий", "Пауза", Tier.Free, new ArgSpec("ms", "Миллисекунды", "number", "500")),
        new(Stop, "Сценарий", "Остановить сценарий", Tier.Free),
        new(RunCommand, "Сценарий", "Выполнить другую команду", Tier.Free, new ArgSpec("command", "Имя команды")),
        new(AskUser, "Сценарий", "Спросить голосом", Tier.Free, new ArgSpec("question", "Вопрос"), new ArgSpec("var", "В переменную", Default: "answer")),

        new(OpenApp, "Запуск", "Открыть программу", Tier.Free, new ArgSpec("path", "Путь или имя"), new ArgSpec("args", "Аргументы", Optional: true)),
        new(OpenUrl, "Запуск", "Открыть сайт", Tier.Free, new ArgSpec("url", "Адрес")),
        new(OpenFile, "Запуск", "Открыть файл", Tier.Free, new ArgSpec("path", "Файл", "file")),
        new(OpenFolder, "Запуск", "Открыть папку", Tier.Free, new ArgSpec("path", "Папка", "folder")),
        new(CloseApp, "Запуск", "Закрыть программу", Tier.Free, new ArgSpec("process", "Имя процесса")),
        new(WebSearch, "Запуск", "Поиск в интернете", Tier.Free, new ArgSpec("query", "Запрос", Default: "{text}"), new ArgSpec("engine", "Поисковик", Default: "yandex")),

        new(KeyPress, "Клавиши", "Нажать клавишу", Tier.Free, new ArgSpec("key", "Клавиша", Default: "Enter"), new ArgSpec("times", "Раз", "number", "1")),
        new(KeyCombo, "Клавиши", "Сочетание клавиш", Tier.Free, new ArgSpec("combo", "Сочетание", Default: "Ctrl+C")),
        new(KeyHold, "Клавиши", "Удерживать клавишу", Tier.Free, new ArgSpec("key", "Клавиша"), new ArgSpec("ms", "Мс", "number", "1000")),
        new(TypeText, "Клавиши", "Напечатать текст", Tier.Free, new ArgSpec("text", "Текст"), new ArgSpec("human", "Как человек (паузы)", "bool", "false")),

        new(MouseMove, "Мышь", "Переместить курсор", Tier.Free, new ArgSpec("x", "X", "number"), new ArgSpec("y", "Y", "number"), new ArgSpec("smooth", "Плавно", "bool", "true"), new ArgSpec("ms", "Длительность", "number", "350")),
        new(MouseClick, "Мышь", "Клик", Tier.Free, new ArgSpec("button", "Кнопка", Default: "left"), new ArgSpec("double", "Двойной", "bool", "false")),
        new(MouseScroll, "Мышь", "Прокрутка", Tier.Free, new ArgSpec("delta", "Шаги (+вверх/−вниз)", "number", "-3")),
        new(MouseDrag, "Мышь", "Перетащить", Tier.Free, new ArgSpec("x1", "X1", "number"), new ArgSpec("y1", "Y1", "number"), new ArgSpec("x2", "X2", "number"), new ArgSpec("y2", "Y2", "number")),

        new(ClickText, "Экран", "Нажать по тексту на экране", Tier.Plus, new ArgSpec("text", "Текст")),
        new(WaitForText, "Экран", "Ждать текст на экране", Tier.Plus, new ArgSpec("text", "Текст"), new ArgSpec("timeout", "Сек", "number", "10")),
        new(ReadScreen, "Экран", "Прочитать экран (OCR)", Tier.Plus, new ArgSpec("var", "В переменную", Default: "screen")),
        new(Screenshot, "Экран", "Скриншот", Tier.Free, new ArgSpec("region", "full/window/select", Default: "full")),
        new(ClickElement, "Экран", "Нажать элемент по подписи (UI Automation)", Tier.Plus, new ArgSpec("name", "Подпись")),

        new(WindowFocus, "Окна", "Переключиться на окно", Tier.Free, new ArgSpec("window", "Заголовок или процесс")),
        new(WindowMinimize, "Окна", "Свернуть окно", Tier.Free, new ArgSpec("window", "Окно", Default: "active")),
        new(WindowMaximize, "Окна", "Развернуть окно", Tier.Free, new ArgSpec("window", "Окно", Default: "active")),
        new(WindowClose, "Окна", "Закрыть окно", Tier.Free, new ArgSpec("window", "Окно", Default: "active")),
        new(WindowLayout, "Окна", "Раскладка окон", Tier.Free, new ArgSpec("layout", "Имя раскладки")),
        new(WindowSnap, "Окна", "Прижать окно", Tier.Free, new ArgSpec("side", "left/right/top/bottom")),
        new(MinimizeAll, "Окна", "Свернуть всё", Tier.Free),

        new(Say, "Озвучка", "Произнести", Tier.Free, new ArgSpec("text", "Текст")),
        new(PlaySound, "Озвучка", "Проиграть звук", Tier.Free, new ArgSpec("file", "Файл", "file")),
        new(Notify, "Озвучка", "Уведомление", Tier.Free, new ArgSpec("title", "Заголовок"), new ArgSpec("text", "Текст")),

        new(VolumeSet, "Звук", "Громкость, %", Tier.Free, new ArgSpec("value", "0–100", "number", "{число}")),
        new(VolumeUp, "Звук", "Громче", Tier.Free, new ArgSpec("step", "Шаг", "number", "10")),
        new(VolumeDown, "Звук", "Тише", Tier.Free, new ArgSpec("step", "Шаг", "number", "10")),
        new(Mute, "Звук", "Выключить звук", Tier.Free),
        new(MediaPlayPause, "Звук", "Пауза/плей", Tier.Free),
        new(MediaNext, "Звук", "Следующий трек", Tier.Free),
        new(MediaPrev, "Звук", "Предыдущий трек", Tier.Free),
        new(AudioDevice, "Звук", "Устройство вывода", Tier.Free, new ArgSpec("name", "Часть имени")),
        new(MicMute, "Звук", "Микрофон вкл/выкл", Tier.Free),

        new(Shutdown, "Система", "Выключить ПК", Tier.Free, new ArgSpec("delay", "Через, сек", "number", "0")),
        new(Restart, "Система", "Перезагрузить", Tier.Free),
        new(Sleep, "Система", "Сон", Tier.Free),
        new(Lock, "Система", "Заблокировать", Tier.Free),
        new(Brightness, "Система", "Яркость", Tier.Free, new ArgSpec("value", "0–100", "number", "{число}")),
        new(RunCmd, "Система", "CMD", Tier.Free, new ArgSpec("script", "Команда"), new ArgSpec("keep", "Оставить окно", "bool", "false"), new ArgSpec("var", "Вывод в переменную", Optional: true)),
        new(RunPowerShell, "Система", "PowerShell", Tier.Free, new ArgSpec("script", "Скрипт"), new ArgSpec("var", "Вывод в переменную", Optional: true)),
        new(RunPython, "Система", "Python-скрипт", Tier.Free, new ArgSpec("script", "Код или путь .py"), new ArgSpec("var", "Вывод в переменную", Optional: true)),
        new(KillProcess, "Система", "Завершить процесс", Tier.Free, new ArgSpec("process", "Имя")),
        new(SystemInfo, "Система", "Сводка о системе", Tier.Free, new ArgSpec("what", "cpu/ram/battery/disk/all", Default: "all")),
        new(EmptyRecycleBin, "Система", "Очистить корзину", Tier.Free),
        new(PowerPlan, "Система", "Схема питания", Tier.Free, new ArgSpec("plan", "balanced/high/saver")),

        new(BrowserNewTab, "Браузер", "Новая вкладка", Tier.Free, new ArgSpec("url", "Адрес", Optional: true)),
        new(BrowserCloseTab, "Браузер", "Закрыть вкладку", Tier.Free),
        new(BrowserClick, "Браузер", "Нажать на странице", Tier.Plus, new ArgSpec("label", "Подпись элемента")),
        new(BrowserFill, "Браузер", "Заполнить поле", Tier.Plus, new ArgSpec("label", "Поле"), new ArgSpec("value", "Значение")),
        new(BrowserReadPage, "Браузер", "Прочитать страницу", Tier.Plus, new ArgSpec("var", "В переменную", Default: "page")),

        new(ClipboardSet, "Текст", "Записать в буфер", Tier.Free, new ArgSpec("text", "Текст")),
        new(ClipboardGet, "Текст", "Буфер → переменная", Tier.Free, new ArgSpec("var", "Переменная", Default: "clipboard")),
        new(Paste, "Текст", "Вставить текст", Tier.Free, new ArgSpec("text", "Текст")),
        new(Dictate, "Текст", "Диктовка в поле", Tier.Free),
        new(Translate, "Текст", "Перевести выделенное", Tier.Plus, new ArgSpec("to", "Язык", Default: "en")),

        new(FileCopy, "Файлы", "Копировать", Tier.Free, new ArgSpec("from", "Откуда", "file"), new ArgSpec("to", "Куда", "folder")),
        new(FileMove, "Файлы", "Переместить", Tier.Free, new ArgSpec("from", "Откуда", "file"), new ArgSpec("to", "Куда", "folder")),
        new(FileDelete, "Файлы", "Удалить в корзину", Tier.Free, new ArgSpec("path", "Путь", "file")),
        new(FileWrite, "Файлы", "Записать в файл", Tier.Free, new ArgSpec("path", "Файл", "file"), new ArgSpec("text", "Текст"), new ArgSpec("append", "Дописать", "bool", "true")),
        new(FindFile, "Файлы", "Найти файл", Tier.Free, new ArgSpec("name", "Имя/маска"), new ArgSpec("var", "В переменную", Default: "found")),

        new(AiAsk, "Нейросеть", "Спросить ИИ", Tier.Free, new ArgSpec("prompt", "Запрос", Default: "{text}"), new ArgSpec("var", "Ответ в переменную", Default: "ai"), new ArgSpec("say", "Озвучить", "bool", "true")),
        new(AiAgent, "Нейросеть", "Задание ИИ-агенту", Tier.Plus, new ArgSpec("task", "Задание", Default: "{text}")),
        new(AiSummarizeClipboard, "Нейросеть", "Кратко пересказать буфер", Tier.Free),

        new(HomeDevice, "Умный дом", "Устройство", Tier.Plus, new ArgSpec("device", "Устройство"), new ArgSpec("state", "on/off/toggle/значение")),
        new(HomeScene, "Умный дом", "Сценарий дома", Tier.Plus, new ArgSpec("scene", "Сценарий")),

        new(RemoteRun, "Второй ПК", "Команда на втором ПК", Tier.Plus, new ArgSpec("pc", "Компьютер"), new ArgSpec("command", "Команда")),
        new(RemoteSay, "Второй ПК", "Сказать на втором ПК", Tier.Plus, new ArgSpec("pc", "Компьютер"), new ArgSpec("text", "Текст")),

        new(Telegram, "Интеграции", "Сообщение в Telegram", Tier.Free, new ArgSpec("text", "Текст")),
        new(HttpRequest, "Интеграции", "HTTP-запрос / вебхук", Tier.Free, new ArgSpec("method", "Метод", Default: "GET"), new ArgSpec("url", "URL"), new ArgSpec("body", "Тело", Optional: true), new ArgSpec("var", "Ответ в переменную", Optional: true)),
        new(McpTool, "Интеграции", "Инструмент подключения (MCP)", Tier.Plus, new ArgSpec("server", "Подключение"), new ArgSpec("tool", "Инструмент"), new ArgSpec("json", "Аргументы JSON", Default: "{}")),
        new(ObsAction, "Интеграции", "OBS", Tier.Plus, new ArgSpec("op", "scene/record/stream/mute"), new ArgSpec("value", "Значение", Optional: true)),
    };

    public static ActionInfo? Find(string type) => Catalog.FirstOrDefault(a => a.Type == type);

    public static IEnumerable<IGrouping<string, ActionInfo>> Groups => Catalog.GroupBy(a => a.Group);
}
