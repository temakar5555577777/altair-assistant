using System.Globalization;
using Altair.Core.Models;

namespace Altair.Core.Triggers;

public static class ScheduleMath
{
    /// <summary>Ближайшее время срабатывания строго после <paramref name="after"/>; null — больше не сработает.</summary>
    public static DateTime? Next(Trigger t, DateTime after)
    {
        switch (t.Mode)
        {
            case ScheduleMode.Once:
                return t.At is { } at && at > after ? at : null;

            case ScheduleMode.EveryMinutes:
            {
                int m = Math.Max(1, t.EveryMinutes);
                var anchor = after.Date;
                var elapsed = (after - anchor).TotalMinutes;
                var k = Math.Floor(elapsed / m) + 1;
                return anchor.AddMinutes(k * m);
            }

            default:
                for (int d = 0; d <= 8; d++)
                {
                    var day = after.Date.AddDays(d);
                    var candidate = day + t.Time;
                    if (candidate <= after) continue;
                    if (DayAllowed(t, day.DayOfWeek)) return candidate;
                }
                return null;
        }
    }

    private static bool DayAllowed(Trigger t, DayOfWeek dow) => t.Mode switch
    {
        ScheduleMode.Daily => true,
        ScheduleMode.Weekdays => dow is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
        ScheduleMode.DaysOfWeek => t.Days.Contains(dow),
        _ => true
    };
}

/// <summary>Расписание: тикает раз в ~15 с; пропущенный запуск догоняется, если опоздание ≤ 10 минут.</summary>
public sealed class Scheduler
{
    public static readonly TimeSpan CatchUpWindow = TimeSpan.FromMinutes(10);

    private readonly Dictionary<Trigger, DateTime?> _due = new(ReferenceEqualityComparer.Instance);
    private readonly Func<IEnumerable<(VoiceCommand Cmd, Trigger Trg)>> _source;
    public event Action<VoiceCommand, Trigger>? Fired;

    public Scheduler(Func<IEnumerable<(VoiceCommand, Trigger)>> source) => _source = source;

    /// <summary>Вызывается при старте/изменении команд; <paramref name="lastSeen"/> — время последней работы (для догонялки).</summary>
    public void Rebuild(DateTime now, DateTime? lastSeen = null)
    {
        var fresh = new Dictionary<Trigger, DateTime?>(ReferenceEqualityComparer.Instance);
        foreach (var (_, trg) in _source())
        {
            if (trg.Kind != TriggerKind.Schedule || !trg.Enabled) continue;
            fresh[trg] = _due.TryGetValue(trg, out var known) ? known : ScheduleMath.Next(trg, lastSeen ?? now);
        }
        _due.Clear();
        foreach (var kv in fresh) _due[kv.Key] = kv.Value;
    }

    public int Tick(DateTime now)
    {
        int fired = 0;
        foreach (var (cmd, trg) in _source().ToList())
        {
            if (trg.Kind != TriggerKind.Schedule || !trg.Enabled) continue;
            if (!_due.TryGetValue(trg, out var due)) { _due[trg] = ScheduleMath.Next(trg, now); continue; }
            if (due is null || now < due.Value) continue;

            if (now - due.Value <= CatchUpWindow)
            {
                Fired?.Invoke(cmd, trg);
                trg.LastFired = now;
                fired++;
            }
            _due[trg] = ScheduleMath.Next(trg, now);
        }
        return fired;
    }

    public DateTime? DueOf(Trigger t) => _due.TryGetValue(t, out var d) ? d : null;
}

/// <summary>Событийные триггеры с ограничением частоты.</summary>
public sealed class EventTriggerHub
{
    private readonly Func<IEnumerable<(VoiceCommand Cmd, Trigger Trg)>> _source;
    public event Action<VoiceCommand, Trigger, string?>? Fired;

    public EventTriggerHub(Func<IEnumerable<(VoiceCommand, Trigger)>> source) => _source = source;

    public int Raise(string eventName, string? arg = null, DateTime? now = null)
    {
        var t = now ?? DateTime.Now;
        int count = 0;
        foreach (var (cmd, trg) in _source().ToList())
        {
            if (trg.Kind != TriggerKind.Event || !trg.Enabled || trg.Event != eventName) continue;
            if (!ArgMatches(eventName, trg.EventArg, arg)) continue;
            if (trg.CooldownSec > 0 && (t - trg.LastFired).TotalSeconds < trg.CooldownSec) continue;
            trg.LastFired = t;
            Fired?.Invoke(cmd, trg, arg);
            count++;
        }
        return count;
    }

    private static bool ArgMatches(string evt, string? expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true;
        if (actual is null) return false;
        if (evt == SystemEvents.BatteryBelow || evt == SystemEvents.UserAway)
        {
            // Порог: событие приходит с текущим значением, срабатываем при пересечении (платформа шлёт один раз)
            return double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var e)
                && double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                && (evt == SystemEvents.BatteryBelow ? a <= e : a >= e);
        }
        return actual.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }
}
