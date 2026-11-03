namespace Altair.Core.Engine;

/// <summary>
/// Нечёткое сопоставление фразы с шаблоном. Терпимо к оборванным фразам, лишним словам,
/// окончаниям («открой/открыть/открою») и ошибкам распознавания.
/// </summary>
public static class FuzzyMatcher
{
    public sealed record Result(double Score, Dictionary<string, string> Slots);

    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        "пожалуйста", "please", "ну", "давай", "а", "ка", "-ка", "мне", "ты", "можешь", "можно", "так", "вот", "это", "the", "a"
    };

    public static double TokenSimilarity(string a, string b)
    {
        if (a == b) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0;
        // Общая основа (рус. морфология): первые 4+ символа совпадают
        int common = 0;
        while (common < a.Length && common < b.Length && a[common] == b[common]) common++;
        if (common >= 4 && common >= Math.Min(a.Length, b.Length) - 3) return 0.88;
        int dist = Levenshtein(a, b);
        return 1.0 - (double)dist / Math.Max(a.Length, b.Length);
    }

    public static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    private static bool IsSlot(string t) => t.Length > 2 && t[0] == '{' && t[^1] == '}';

    private static bool IsNumericSlot(string slotName) =>
        slotName is "число" or "number" or "n" or "процент" or "минуты" or "секунды";

    /// <summary>Сопоставляет нормализованный ввод с шаблоном (шаблон тоже нормализуется, кроме слотов).</summary>
    public static Result? Match(string normalizedInput, string template, double tokenThreshold = 0.72)
    {
        var input = PhraseNormalizer.Tokens(normalizedInput).Where(t => !Fillers.Contains(t)).ToArray();
        var tpl = TokenizeTemplate(template);
        if (input.Length == 0 || tpl.Count == 0) return null;

        var slots = new Dictionary<string, string>();
        int pos = 0;
        double literalScore = 0; int literalCount = 0; int missing = 0;
        string? pendingSlot = null; int slotStart = 0;

        for (int k = 0; k < tpl.Count; k++)
        {
            var t = tpl[k];
            if (IsSlot(t))
            {
                pendingSlot = t[1..^1];
                slotStart = pos;
                // Числовой слот, за которым нет литерала — забираем ближайшее число
                continue;
            }

            literalCount++;
            // Ищем лучший токен впереди (слот может «съесть» промежуток; без слота — окно 2 лишних слова)
            int window = pendingSlot != null ? input.Length - pos : Math.Min(3, input.Length - pos);
            int bestIdx = -1; double best = 0;
            for (int i = pos; i < pos + window && i < input.Length; i++)
            {
                double sim = TokenSimilarity(input[i], t);
                if (sim > best) { best = sim; bestIdx = i; }
                if (sim >= 0.999) break;
            }

            if (bestIdx >= 0 && best >= tokenThreshold)
            {
                if (pendingSlot != null)
                {
                    if (bestIdx == slotStart) return null; // пустой слот
                    if (!AssignSlot(slots, pendingSlot, input, slotStart, bestIdx)) return null;
                    pendingSlot = null;
                }
                literalScore += best;
                pos = bestIdx + 1;
            }
            else
            {
                missing++;
                // Оборванная фраза: допускаем пропуск одного литерала в шаблонах от 3 слов
            }
        }

        if (pendingSlot != null)
        {
            if (slotStart >= input.Length) return null;
            if (!AssignSlot(slots, pendingSlot, input, slotStart, input.Length)) return null;
            pos = input.Length;
        }

        if (literalCount == 0)
            return new Result(0.6, slots); // шаблон из одного слота — низкий приоритет

        int allowedMissing = literalCount >= 3 ? 1 : 0;
        if (missing > allowedMissing) return null;

        double avg = literalScore / Math.Max(1, literalCount - missing);
        double coverage = (double)(literalCount - missing) / literalCount;
        int extra = input.Length - pos; // лишние слова в конце
        double extraPenalty = extra > 0 ? Math.Min(0.25, 0.08 * extra) : 0;
        double score = avg * (0.75 + 0.25 * coverage) - extraPenalty;
        return score <= 0 ? null : new Result(Math.Round(score, 4), slots);
    }

    private static bool AssignSlot(Dictionary<string, string> slots, string name, string[] input, int from, int to)
    {
        var value = string.Join(' ', input[from..to]);
        if (IsNumericSlot(name))
        {
            var num = input[from..to].FirstOrDefault(x => double.TryParse(x.TrimEnd('%'),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _));
            if (num is null) return false;
            value = num.TrimEnd('%');
        }
        slots[name] = value;
        return true;
    }

    private static List<string> TokenizeTemplate(string template)
    {
        var result = new List<string>();
        foreach (var raw in template.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsSlot(raw)) result.Add(raw.ToLowerInvariant());
            else
            {
                var n = PhraseNormalizer.Normalize(raw);
                if (n.Length > 0 && !Fillers.Contains(n)) result.Add(n);
            }
        }
        return result;
    }
}
