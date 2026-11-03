using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Altair.Core.Engine;

/// <summary>Нормализация распознанной речи: регистр, «ё», пунктуация, числительные → цифры.</summary>
public static partial class PhraseNormalizer
{
    private static readonly Dictionary<string, int> Units = new()
    {
        ["ноль"] = 0, ["один"] = 1, ["одна"] = 1, ["одну"] = 1, ["два"] = 2, ["две"] = 2, ["три"] = 3, ["четыре"] = 4,
        ["пять"] = 5, ["шесть"] = 6, ["семь"] = 7, ["восемь"] = 8, ["девять"] = 9, ["десять"] = 10,
        ["одиннадцать"] = 11, ["двенадцать"] = 12, ["тринадцать"] = 13, ["четырнадцать"] = 14, ["пятнадцать"] = 15,
        ["шестнадцать"] = 16, ["семнадцать"] = 17, ["восемнадцать"] = 18, ["девятнадцать"] = 19,
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
        ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13,
        ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
    };

    private static readonly Dictionary<string, int> Tens = new()
    {
        ["двадцать"] = 20, ["тридцать"] = 30, ["сорок"] = 40, ["пятьдесят"] = 50, ["шестьдесят"] = 60,
        ["семьдесят"] = 70, ["восемьдесят"] = 80, ["девяносто"] = 90,
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70,
        ["eighty"] = 80, ["ninety"] = 90,
    };

    private static readonly Dictionary<string, int> Hundreds = new()
    {
        ["сто"] = 100, ["двести"] = 200, ["триста"] = 300, ["четыреста"] = 400, ["пятьсот"] = 500,
        ["hundred"] = 100,
    };

    [GeneratedRegex(@"[^\p{L}\p{Nd}\s\.\-+%]", RegexOptions.CultureInvariant)]
    private static partial Regex Junk();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var s = text.ToLower(CultureInfo.GetCultureInfo("ru-RU")).Replace('ё', 'е');
        s = Junk().Replace(s, " ");
        s = s.Replace(" .", " ").Replace(". ", " ").Trim('.', ' ');
        s = Spaces().Replace(s, " ").Trim();
        return WordsToNumbers(s);
    }

    /// <summary>«громкость пятьдесят пять» → «громкость 55».</summary>
    public static string WordsToNumbers(string s)
    {
        var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        int i = 0;
        while (i < tokens.Length)
        {
            int value = 0; bool any = false; int j = i;
            if (j < tokens.Length && Hundreds.TryGetValue(tokens[j], out var h)) { value += h; any = true; j++; }
            if (j < tokens.Length && Tens.TryGetValue(tokens[j], out var t)) { value += t; any = true; j++; }
            if (j < tokens.Length && Units.TryGetValue(tokens[j], out var u))
            {
                // «двадцать одиннадцать» не бывает — единицы >9 только сами по себе
                if (!any || u < 10) { value += u; any = true; j++; }
            }
            if (any)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(value.ToString(CultureInfo.InvariantCulture));
                i = j;
            }
            else
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(tokens[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    public static string[] Tokens(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
