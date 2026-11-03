using System.Text.Json.Serialization;

namespace Altair.Core.Models;

/// <summary>Коллекция → папки → команды. Сериализуется в JSON (импорт/экспорт, паки).</summary>
public sealed class CommandCollection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Мои команды";
    public string? PackId { get; set; }
    public bool Enabled { get; set; } = true;
    public List<CommandFolder> Folders { get; set; } = new();
}

public sealed class CommandFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Папка";
    public bool Enabled { get; set; } = true;
    public List<VoiceCommand> Commands { get; set; } = new();
}

public sealed class VoiceCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Новая команда";
    public bool Enabled { get; set; } = true;

    /// <summary>Фразы-активаторы. Поддерживаются слоты: «открой {сайт}», «громкость {число}».</summary>
    public List<string> Phrases { get; set; } = new();

    public List<ActionStep> Actions { get; set; } = new();

    /// <summary>Пул ответов с весами — случайный выбор (запрос пользователей: «Выполняю!» / «Есть, сэр!»).</summary>
    public ResponsePool Responses { get; set; } = new();

    public List<Trigger> Triggers { get; set; } = new();

    /// <summary>Спрашивать подтверждение перед выполнением (голосом «да/нет», таймаут).</summary>
    public bool RequireConfirmation { get; set; }
    public int ConfirmationTimeoutSec { get; set; } = 30;

    /// <summary>Доступна в меню Telegram-бота.</summary>
    public bool ExposeToTelegram { get; set; }

    /// <summary>Требует прав администратора — выполняется через заранее одобренную задачу планировщика.</summary>
    public bool RunElevated { get; set; }

    public string? Description { get; set; }
}

public sealed class ResponsePool
{
    public List<WeightedResponse> Items { get; set; } = new();
    /// <summary>Не повторять предыдущий ответ подряд, если вариантов больше одного.</summary>
    public bool AvoidRepeat { get; set; } = true;
}

public sealed class WeightedResponse
{
    public string? Text { get; set; }
    /// <summary>Путь к своему аудиофайлу (wav/mp3) — бесплатно, без облака.</summary>
    public string? AudioFile { get; set; }
    public double Weight { get; set; } = 1.0;
}

/// <summary>Шаг сценария. Управляющие шаги (If/Loop) содержат вложенные шаги.</summary>
public sealed class ActionStep
{
    public string Type { get; set; } = ActionTypes.Say;
    public Dictionary<string, string> Args { get; set; } = new();

    /// <summary>Для If: условие; для While/Repeat: условие или количество.</summary>
    public string? Condition { get; set; }
    public List<ActionStep>? Then { get; set; }
    public List<ActionStep>? Else { get; set; }

    public bool ContinueOnError { get; set; }
    public string? Comment { get; set; }

    public string Arg(string key, string fallback = "") =>
        Args.TryGetValue(key, out var v) ? v : fallback;

    public static ActionStep Of(string type, params (string Key, string Value)[] args)
    {
        var s = new ActionStep { Type = type };
        foreach (var (k, v) in args) s.Args[k] = v;
        return s;
    }
}

public enum TriggerKind { Hotkey, Schedule, Event }

public enum ScheduleMode { Once, Daily, Weekdays, DaysOfWeek, EveryMinutes }

public sealed class Trigger
{
    public TriggerKind Kind { get; set; }
    public bool Enabled { get; set; } = true;

    // Hotkey: "Ctrl+Alt+K"
    public string? Hotkey { get; set; }

    // Schedule
    public ScheduleMode Mode { get; set; } = ScheduleMode.Daily;
    public DateTime? At { get; set; }
    public TimeSpan Time { get; set; } = new(9, 0, 0);
    public List<DayOfWeek> Days { get; set; } = new();
    public int EveryMinutes { get; set; } = 60;

    // Event: см. SystemEvents
    public string? Event { get; set; }
    /// <summary>Параметр события: имя процесса, SSID, порог батареи, минуты отсутствия.</summary>
    public string? EventArg { get; set; }
    /// <summary>Не чаще раза в N секунд.</summary>
    public int CooldownSec { get; set; }

    [JsonIgnore] public DateTime LastFired { get; set; } = DateTime.MinValue;
}

public static class SystemEvents
{
    public const string AppStarted = "app.started";
    public const string AppClosed = "app.closed";
    public const string HeadphonesConnected = "audio.headphones.on";
    public const string HeadphonesDisconnected = "audio.headphones.off";
    public const string AudioDeviceChanged = "audio.device.changed";
    public const string PowerPlugged = "power.plugged";
    public const string PowerUnplugged = "power.unplugged";
    public const string BatteryBelow = "power.battery.below";
    public const string UserAway = "user.away";
    public const string UserBack = "user.back";
    public const string SessionLocked = "session.locked";
    public const string SessionUnlocked = "session.unlocked";
    public const string InternetLost = "net.lost";
    public const string InternetRestored = "net.restored";
    public const string WifiConnected = "net.wifi";
    public const string AssistantStarted = "assistant.started";
    public const string Snapse = "snapse"; // аргумент — число щелчков (1/2/3)
    public const string Gesture = "hotkey.double";

    public static readonly string[] All =
    {
        AppStarted, AppClosed, HeadphonesConnected, HeadphonesDisconnected, AudioDeviceChanged,
        PowerPlugged, PowerUnplugged, BatteryBelow, UserAway, UserBack, SessionLocked, SessionUnlocked,
        InternetLost, InternetRestored, WifiConnected, AssistantStarted, Snapse
    };
}
