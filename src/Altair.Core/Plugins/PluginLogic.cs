using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Altair.Core.Models;
using Altair.Core.Storage;

namespace Altair.Core.Plugins;

public sealed record PluginInfo(string Id, string Title, string Theme, string Description, Tier Tier, string? Hotkey = null);

public static class PluginCatalog
{
    public static readonly PluginInfo[] All =
    {
        // Голос и звук
        new("dictation", "Умная диктовка", "Голос и звук", "Диктуйте в любое поле; ИИ расставит знаки препинания", Tier.Free, "RightCtrl×2"),
        new("readAloud", "Чтение вслух", "Голос и звук", "Выделенный текст или страница — голосом", Tier.Plus, "Ctrl+Alt+R"),
        new("notifyVoice", "Озвучка уведомлений", "Голос и звук", "Читает уведомления Windows и мессенджеров", Tier.Free),
        new("subtitles", "Живые субтитры", "Голос и звук", "Субтитры поверх экрана для любого звука", Tier.Free),
        new("summary", "Конспект видео и созвонов", "Голос и звук", "Записывает и делает краткий пересказ", Tier.Free),
        // Текст и буфер
        new("textAssist", "Помощник текста", "Текст и буфер", "Выделили текст — исправить, улучшить, перевести", Tier.Plus, "Ctrl+Alt+W"),
        new("clipboard", "История буфера обмена", "Текст и буфер", "Последние 200 копирований с поиском", Tier.Free, "Win+Alt+V"),
        new("snippets", "Быстрые вставки", "Текст и буфер", "Набрали сокращение — подставился текст", Tier.Plus),
        new("docAsk", "Спросить документы", "Текст и буфер", "Найдёт файл по содержимому и ответит по нему", Tier.Plus),
        // Рабочий стол
        new("avatar", "Визуальный аватар", "Рабочий стол", "Анимированный ассистент реагирует на голос", Tier.Free),
        new("pet", "Питомец", "Рабочий стол", "Живёт на панели задач", Tier.Free),
        new("island", "Островок", "Рабочий стол", "Плашка у края экрана: что происходит сейчас", Tier.Plus, "Ctrl+Alt+I"),
        new("breaks", "Перерывы", "Рабочий стол", "Напоминает размяться и отдохнуть глазам", Tier.Free),
        // Управление ПК и дом
        new("snapse", "Snapse", "Управление ПК и дом", "Жесты рукой перед веб-камерой", Tier.Plus),
        new("smartHome", "Умный дом", "Управление ПК и дом", "Яндекс Дом и Home Assistant", Tier.Plus),
        new("remotePc", "Второй компьютер", "Управление ПК и дом", "Команды на другом ПК по сети", Tier.Plus),
        new("awayLock", "Блокировка при отходе", "Управление ПК и дом", "Блокирует ПК, когда телефон (Bluetooth) далеко", Tier.Free),
        new("pcControl", "PC Control", "Управление ПК и дом", "Компьютер из Telegram через своего бота", Tier.Free),
        // Файлы, экран, стримы
        new("converter", "Конвертер", "Файлы, экран и стримы", "Круг действий с файлами", Tier.Free, "Ctrl+Alt+X"),
        new("screenshots", "Скриншоты", "Файлы, экран и стримы", "Снимок с пометками и запись экрана", Tier.Plus, "Ctrl+Alt+A"),
        new("streamer", "Помощник стримера", "Файлы, экран и стримы", "OBS голосом и чат Twitch", Tier.Plus),
        new("cleanup", "Уборка загрузок", "Файлы, экран и стримы", "Раскладывает «Загрузки» по папкам", Tier.Plus),
        // Подключения
        new("connections", "Подключения (MCP)", "Подключения", "GitHub, Notion и любые MCP-серверы", Tier.Plus),
        new("gameMode", "Игровой режим", "Подключения", "Приглушает ответы и не мешает в играх", Tier.Free),
    };
}

/// <summary>Быстрые вставки: «;адр» → полный адрес. Поддерживает {date}, {time}, {clipboard}.</summary>
public sealed class TextExpander
{
    private readonly StringBuilder _buffer = new();
    public Dictionary<string, string> Snippets { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Подаём каждый набранный символ. Возвращает (сколько стереть, что вставить) при совпадении.</summary>
    public (int Backspaces, string Text)? Feed(char c, Func<string, string>? expand = null)
    {
        if (c == '\b') { if (_buffer.Length > 0) _buffer.Length--; return null; }
        if (char.IsControl(c)) { _buffer.Clear(); return null; }
        _buffer.Append(c);
        if (_buffer.Length > 64) _buffer.Remove(0, _buffer.Length - 64);
        var s = _buffer.ToString();
        foreach (var kv in Snippets.OrderByDescending(k => k.Key.Length))
        {
            if (kv.Key.Length == 0 || !s.EndsWith(kv.Key, StringComparison.Ordinal)) continue;
            // Сокращение должно начинаться с границы слова
            int start = s.Length - kv.Key.Length;
            if (start > 0 && char.IsLetterOrDigit(s[start - 1]) && char.IsLetterOrDigit(kv.Key[0])) continue;
            _buffer.Clear();
            return (kv.Key.Length, expand != null ? expand(kv.Value) : kv.Value);
        }
        return null;
    }

    public void Reset() => _buffer.Clear();
}

/// <summary>Уборка загрузок: правила «расширение → папка», безопасное перемещение без перезаписи.</summary>
public sealed class DownloadsCleaner
{
    public static readonly Dictionary<string, string[]> DefaultRules = new()
    {
        ["Изображения"] = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic", ".svg" },
        ["Видео"] = new[] { ".mp4", ".mkv", ".avi", ".mov", ".webm" },
        ["Музыка"] = new[] { ".mp3", ".wav", ".flac", ".ogg", ".m4a" },
        ["Документы"] = new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf", ".odt", ".csv" },
        ["Архивы"] = new[] { ".zip", ".rar", ".7z", ".tar", ".gz" },
        ["Программы"] = new[] { ".exe", ".msi", ".msix", ".appx" },
        ["Торренты"] = new[] { ".torrent" },
    };

    public Dictionary<string, string[]> Rules { get; set; } = DefaultRules;
    public int MinAgeMinutes { get; set; } = 10; // не трогаем файлы, которые ещё качаются

    public sealed record Move(string From, string To);

    public List<Move> Plan(string folder, DateTime? now = null)
    {
        var t = now ?? DateTime.Now;
        var moves = new List<Move>();
        foreach (var f in Directory.EnumerateFiles(folder))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".crdownload" or ".part" or ".tmp" or ".download") continue;
            if ((t - File.GetLastWriteTime(f)).TotalMinutes < MinAgeMinutes) continue;
            var target = Rules.FirstOrDefault(r => r.Value.Contains(ext)).Key;
            if (target == null) continue;
            moves.Add(new Move(f, Path.Combine(folder, target, Path.GetFileName(f))));
        }
        return moves;
    }

    public int Apply(IEnumerable<Move> moves)
    {
        int n = 0;
        foreach (var m in moves)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(m.To)!);
            var dest = Unique(m.To);
            try { File.Move(m.From, dest); n++; } catch (IOException) { /* занят другим процессом */ }
        }
        return n;
    }

    public static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!; var name = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path);
        for (int i = 2; ; i++) { var p = Path.Combine(dir, $"{name} ({i}){ext}"); if (!File.Exists(p)) return p; }
    }
}

/// <summary>«Спросить документы»: локальный BM25-индекс по txt/md/csv/json/log/docx/html; ответ — ИИ по найденным фрагментам.</summary>
public sealed partial class DocIndex
{
    public sealed record Chunk(string File, int Index, string Text, string[] Terms);
    private readonly List<Chunk> _chunks = new();
    private readonly Dictionary<string, int> _df = new();
    private double _avgLen;

    public static readonly string[] Extensions = { ".txt", ".md", ".csv", ".json", ".log", ".docx", ".html", ".htm", ".xml", ".ini", ".cs", ".py", ".js" };

    public int Count => _chunks.Count;

    [GeneratedRegex(@"[\p{L}\p{Nd}]{2,}")]
    private static partial Regex Word();

    public static string[] Terms(string text) =>
        Word().Matches(text.ToLowerInvariant().Replace('ё', 'е')).Select(m => Stem(m.Value)).ToArray();

    /// <summary>Грубый стеммер: отрезаем частые русские/английские окончания.</summary>
    public static string Stem(string w)
    {
        string[] endings = { "иями", "ями", "ами", "ого", "его", "ому", "ему", "ыми", "ими", "ия", "ие", "ий", "ой", "ый", "ая", "яя", "ое", "ее", "ах", "ях", "ов", "ев", "ам", "ям", "ом", "ем", "ы", "и", "а", "я", "о", "е", "у", "ю", "ing", "ed", "es", "s" };
        if (w.Length <= 4) return w;
        foreach (var e in endings) if (w.EndsWith(e, StringComparison.Ordinal) && w.Length - e.Length >= 3) return w[..^e.Length];
        return w;
    }

    public static string ExtractText(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".docx")
        {
            using var z = ZipFile.OpenRead(path);
            var e = z.GetEntry("word/document.xml");
            if (e == null) return "";
            using var s = e.Open();
            var doc = XDocument.Load(s);
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            return string.Join("\n", doc.Descendants(w + "p").Select(p => string.Concat(p.Descendants(w + "t").Select(t => t.Value))));
        }
        var text = File.ReadAllText(path);
        if (ext is ".html" or ".htm") text = Regex.Replace(text, "<[^>]+>", " ");
        return text;
    }

    public void AddFolder(string folder, int maxFiles = 5000, long maxBytes = 5_000_000)
    {
        int n = 0;
        foreach (var f in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            if (!Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
            try { if (new FileInfo(f).Length > maxBytes) continue; AddDocument(f, ExtractText(f)); }
            catch { continue; }
            if (++n >= maxFiles) break;
        }
    }

    public void AddDocument(string file, string text, int chunkChars = 1200)
    {
        for (int i = 0, idx = 0; i < text.Length; i += chunkChars, idx++)
        {
            var piece = text.Substring(i, Math.Min(chunkChars + 200, text.Length - i)); // перекрытие 200
            var terms = Terms(piece);
            if (terms.Length == 0) continue;
            _chunks.Add(new Chunk(file, idx, piece, terms));
            foreach (var t in terms.Distinct()) _df[t] = _df.GetValueOrDefault(t) + 1;
        }
        _avgLen = _chunks.Count == 0 ? 0 : _chunks.Average(c => c.Terms.Length);
    }

    public List<(Chunk Chunk, double Score)> Search(string query, int top = 5)
    {
        var q = Terms(query).Distinct().ToArray();
        int N = _chunks.Count; const double k1 = 1.4, b = 0.75;
        var res = new List<(Chunk, double)>();
        foreach (var c in _chunks)
        {
            double score = 0;
            foreach (var t in q)
            {
                int tf = c.Terms.Count(x => x == t);
                if (tf == 0) continue;
                int df = _df.GetValueOrDefault(t);
                double idf = Math.Log(1 + (N - df + 0.5) / (df + 0.5));
                score += idf * tf * (k1 + 1) / (tf + k1 * (1 - b + b * c.Terms.Length / Math.Max(1, _avgLen)));
            }
            if (score > 0) res.Add((c, score));
        }
        return res.OrderByDescending(r => r.Item2).Take(top).ToList();
    }
}

/// <summary>История буфера обмена: до N записей, без дублей, закреплённые не вытесняются.</summary>
public sealed class ClipboardHistory
{
    public sealed class Entry { public string Text { get; set; } = ""; public DateTime At { get; set; } = DateTime.Now; public bool Pinned { get; set; } }
    public List<Entry> Items { get; set; } = new();
    public int Capacity { get; set; } = 200;

    public void Add(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 100_000) return;
        var existing = Items.FirstOrDefault(i => i.Text == text);
        if (existing != null) { Items.Remove(existing); existing.At = DateTime.Now; Items.Insert(0, existing); return; }
        Items.Insert(0, new Entry { Text = text });
        while (Items.Count > Capacity)
        {
            var victim = Items.LastOrDefault(i => !i.Pinned);
            if (victim == null) break;
            Items.Remove(victim);
        }
    }

    public IEnumerable<Entry> Search(string q) =>
        string.IsNullOrWhiteSpace(q) ? Items : Items.Where(i => i.Text.Contains(q, StringComparison.OrdinalIgnoreCase));

    public void Save(string? path = null) => Json.Save(path ?? Path.Combine(AppPaths.Data, "clipboard.json"), Items);
    public void Load(string? path = null) => Items = Json.Load<List<Entry>>(path ?? Path.Combine(AppPaths.Data, "clipboard.json"));
}

/// <summary>Перерывы: правило 20-20-20 для глаз и разминка раз в N минут активной работы.</summary>
public sealed class BreakReminder
{
    public int WorkMinutes { get; set; } = 50;
    public int EyeMinutes { get; set; } = 20;
    private DateTime _workStart = DateTime.Now, _lastEye = DateTime.Now;

    public string? Check(DateTime now, bool userActive)
    {
        if (!userActive) { _workStart = now; _lastEye = now; return null; }
        if ((now - _workStart).TotalMinutes >= WorkMinutes) { _workStart = now; _lastEye = now; return "Пора размяться: встаньте и пройдитесь пару минут."; }
        if ((now - _lastEye).TotalMinutes >= EyeMinutes) { _lastEye = now; return "Отдых для глаз: посмотрите вдаль 20 секунд."; }
        return null;
    }
}
