using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Altair.Core.Engine;

/// <summary>Переменные сценария + встроенные ({time}, {date}, {hour}, {weekday}, {random}) + провайдеры платформы.</summary>
public sealed partial class VariableStore
{
    private readonly Dictionary<string, string> _vars = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<string>> _providers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Random Rng = new();

    public VariableStore()
    {
        _providers["time"] = () => DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
        _providers["date"] = () => DateTime.Now.ToString("d MMMM", CultureInfo.GetCultureInfo("ru-RU"));
        _providers["hour"] = () => DateTime.Now.Hour.ToString(CultureInfo.InvariantCulture);
        _providers["minute"] = () => DateTime.Now.Minute.ToString(CultureInfo.InvariantCulture);
        _providers["weekday"] = () => ((int)DateTime.Now.DayOfWeek == 0 ? 7 : (int)DateTime.Now.DayOfWeek).ToString(CultureInfo.InvariantCulture);
        _providers["random"] = () => Rng.Next(0, 100).ToString(CultureInfo.InvariantCulture);
    }

    public void RegisterProvider(string name, Func<string> provider) => _providers[name] = provider;

    public string? Get(string name) =>
        _vars.TryGetValue(name, out var v) ? v : _providers.TryGetValue(name, out var p) ? SafeCall(p) : null;

    public void Set(string name, string value) => _vars[name] = value;

    public IReadOnlyDictionary<string, string> Snapshot() => new Dictionary<string, string>(_vars);

    public VariableStore Fork()
    {
        var f = new VariableStore();
        foreach (var kv in _vars) f._vars[kv.Key] = kv.Value;
        foreach (var kv in _providers) f._providers[kv.Key] = kv.Value;
        return f;
    }

    private static string SafeCall(Func<string> p) { try { return p(); } catch { return ""; } }

    [GeneratedRegex(@"\{([\p{L}\p{Nd}_\.]+)\}")]
    private static partial Regex Placeholder();

    /// <summary>Подставляет {переменные} в строку. Неизвестные остаются как есть.</summary>
    public string Expand(string? template)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;
        return Placeholder().Replace(template, m => Get(m.Groups[1].Value) ?? m.Value);
    }
}

/// <summary>
/// Мини-язык условий и выражений: числа, "строки", {переменные}, + - * /, == != &gt; &lt; &gt;= &lt;=,
/// contains, startswith, &amp;&amp; || !, скобки. Пример: <c>{hour} &gt;= 22 &amp;&amp; {battery} &lt; 30</c>.
/// </summary>
public static class Expr
{
    public static bool IsTrue(string expression, VariableStore vars) => ToBool(Eval(expression, vars));

    public static object Eval(string expression, VariableStore vars)
    {
        var p = new Parser(Tokenize(expression), vars);
        var v = p.ParseOr();
        return v;
    }

    public static bool ToBool(object v) => v switch
    {
        bool b => b,
        double d => Math.Abs(d) > double.Epsilon,
        string s => s.Length > 0 && !s.Equals("false", StringComparison.OrdinalIgnoreCase) && s != "0" && !s.Equals("нет", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    public static string ToText(object v) => v switch
    {
        double d => d.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => v?.ToString() ?? ""
    };

    private enum K { Num, Str, Var, Op, LParen, RParen, Word, End }
    private readonly record struct Tok(K Kind, string Text);

    private static List<Tok> Tokenize(string s)
    {
        var list = new List<Tok>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '(') { list.Add(new(K.LParen, "(")); i++; continue; }
            if (c == ')') { list.Add(new(K.RParen, ")")); i++; continue; }
            if (c == '"' || c == '\'')
            {
                char q = c; var sb = new StringBuilder(); i++;
                while (i < s.Length && s[i] != q) { if (s[i] == '\\' && i + 1 < s.Length) i++; sb.Append(s[i]); i++; }
                i++; list.Add(new(K.Str, sb.ToString())); continue;
            }
            if (c == '{')
            {
                int end = s.IndexOf('}', i);
                if (end < 0) throw new FormatException("Не закрыта фигурная скобка");
                list.Add(new(K.Var, s[(i + 1)..end])); i = end + 1; continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
            {
                int st = i; while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                list.Add(new(K.Num, s[st..i])); continue;
            }
            string[] ops = { "&&", "||", "==", "!=", ">=", "<=", ">", "<", "!", "+", "-", "*", "/" };
            var op = ops.FirstOrDefault(o => string.CompareOrdinal(s, i, o, 0, o.Length) == 0);
            if (op != null) { list.Add(new(K.Op, op)); i += op.Length; continue; }
            if (char.IsLetter(c))
            {
                int st = i; while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                var w = s[st..i].ToLowerInvariant();
                list.Add(w switch
                {
                    "and" or "и" => new(K.Op, "&&"),
                    "or" or "или" => new(K.Op, "||"),
                    "not" or "не" => new(K.Op, "!"),
                    _ => new(K.Word, w)
                });
                continue;
            }
            throw new FormatException($"Неожиданный символ '{c}' в выражении");
        }
        list.Add(new(K.End, ""));
        return list;
    }

    private sealed class Parser
    {
        private readonly List<Tok> _t; private int _i; private readonly VariableStore _v;
        public Parser(List<Tok> t, VariableStore v) { _t = t; _v = v; }
        private Tok Peek => _t[_i];
        private bool IsOp(string op) => Peek.Kind == K.Op && Peek.Text == op;
        private bool IsWord(string w) => Peek.Kind == K.Word && Peek.Text == w;

        public object ParseOr()
        {
            var l = ParseAnd();
            while (IsOp("||")) { _i++; var r = ParseAnd(); l = ToBool(l) || ToBool(r); }
            return l;
        }
        private object ParseAnd()
        {
            var l = ParseNot();
            while (IsOp("&&")) { _i++; var r = ParseNot(); l = ToBool(l) && ToBool(r); }
            return l;
        }
        private object ParseNot()
        {
            if (IsOp("!")) { _i++; return !ToBool(ParseNot()); }
            return ParseCmp();
        }
        private object ParseCmp()
        {
            var l = ParseAdd();
            if (Peek.Kind == K.Op && Peek.Text is "==" or "!=" or ">" or "<" or ">=" or "<=")
            {
                var op = Peek.Text; _i++; var r = ParseAdd();
                return Compare(l, r, op);
            }
            if (IsWord("contains") || IsWord("содержит"))
            { _i++; var r = ParseAdd(); return ToText(l).Contains(ToText(r), StringComparison.OrdinalIgnoreCase); }
            if (IsWord("startswith") || IsWord("начинается"))
            { _i++; var r = ParseAdd(); return ToText(l).StartsWith(ToText(r), StringComparison.OrdinalIgnoreCase); }
            return l;
        }
        private object ParseAdd()
        {
            var l = ParseMul();
            while (IsOp("+") || IsOp("-"))
            {
                var op = Peek.Text; _i++; var r = ParseMul();
                if (op == "+" && (TryNum(l, out var a) && TryNum(r, out var b))) l = a + b;
                else if (op == "+") l = ToText(l) + ToText(r);
                else l = Num(l) - Num(r);
            }
            return l;
        }
        private object ParseMul()
        {
            var l = ParseUnary();
            while (IsOp("*") || IsOp("/"))
            {
                var op = Peek.Text; _i++; var r = ParseUnary();
                l = op == "*" ? Num(l) * Num(r) : Num(r) == 0 ? 0d : Num(l) / Num(r);
            }
            return l;
        }
        private object ParseUnary()
        {
            if (IsOp("-")) { _i++; return -Num(ParseUnary()); }
            return ParsePrimary();
        }
        private object ParsePrimary()
        {
            var t = Peek; _i++;
            switch (t.Kind)
            {
                case K.Num: return double.Parse(t.Text, CultureInfo.InvariantCulture);
                case K.Str: return t.Text;
                case K.Var:
                    var val = _v.Get(t.Text) ?? "";
                    return TryNum(val, out var d) ? d : val;
                case K.Word:
                    return t.Text switch { "true" or "да" => true, "false" or "нет" => false, _ => t.Text };
                case K.LParen:
                    var v = ParseOr();
                    if (Peek.Kind != K.RParen) throw new FormatException("Ожидалась ')'");
                    _i++; return v;
                default: throw new FormatException($"Неожиданный элемент '{t.Text}'");
            }
        }

        private static bool TryNum(object o, out double d)
        {
            switch (o)
            {
                case double x: d = x; return true;
                case bool: d = 0; return false;
                case string s: return double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d);
                default: d = 0; return false;
            }
        }
        private static double Num(object o) => TryNum(o, out var d) ? d : 0;

        private static bool Compare(object l, object r, string op)
        {
            if (TryNum(l, out var a) && TryNum(r, out var b))
                return op switch { "==" => Math.Abs(a - b) < 1e-9, "!=" => Math.Abs(a - b) >= 1e-9, ">" => a > b, "<" => a < b, ">=" => a >= b, _ => a <= b };
            int c = string.Compare(ToText(l), ToText(r), StringComparison.OrdinalIgnoreCase);
            return op switch { "==" => c == 0, "!=" => c != 0, ">" => c > 0, "<" => c < 0, ">=" => c >= 0, _ => c <= 0 };
        }
    }
}
