using Altair.Core.Models;

namespace Altair.Core.Engine;

/// <summary>Взвешенный случайный выбор ответа с защитой от повтора подряд.</summary>
public sealed class ResponsePicker
{
    private readonly Random _rng;
    private readonly Dictionary<ResponsePool, int> _last = new(ReferenceEqualityComparer.Instance);

    public ResponsePicker(Random? rng = null) => _rng = rng ?? new Random();

    public WeightedResponse? Pick(ResponsePool pool)
    {
        var items = pool.Items.Where(i => i.Weight > 0 && (!string.IsNullOrWhiteSpace(i.Text) || !string.IsNullOrWhiteSpace(i.AudioFile))).ToList();
        if (items.Count == 0) return null;
        if (items.Count == 1) return items[0];

        var candidates = Enumerable.Range(0, items.Count).ToList();
        if (pool.AvoidRepeat && _last.TryGetValue(pool, out var lastIdx) && lastIdx < items.Count)
            candidates.Remove(lastIdx);

        double total = candidates.Sum(i => items[i].Weight);
        double roll = _rng.NextDouble() * total;
        foreach (var i in candidates)
        {
            roll -= items[i].Weight;
            if (roll <= 0) { _last[pool] = i; return items[i]; }
        }
        _last[pool] = candidates[^1];
        return items[candidates[^1]];
    }
}
