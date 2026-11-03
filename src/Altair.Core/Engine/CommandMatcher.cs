using Altair.Core.Models;

namespace Altair.Core.Engine;

public sealed record CommandMatch(VoiceCommand Command, double Score, Dictionary<string, string> Slots, string Utterance);

/// <summary>Сопоставляет фразу со всеми включёнными командами; поддерживает связки «и / затем / потом».</summary>
public sealed class CommandMatcher
{
    private static readonly string[] Connectors = { " а потом ", " и затем ", " затем ", " потом ", " после этого ", " и ", " then ", " and " };

    public double Threshold { get; set; } = 0.70;

    private readonly Func<IEnumerable<CommandCollection>> _source;

    public CommandMatcher(Func<IEnumerable<CommandCollection>> source) => _source = source;

    public IEnumerable<VoiceCommand> EnabledCommands() =>
        _source().Where(c => c.Enabled)
            .SelectMany(c => c.Folders.Where(f => f.Enabled))
            .SelectMany(f => f.Commands.Where(cmd => cmd.Enabled));

    public CommandMatch? Best(string normalized)
    {
        CommandMatch? best = null;
        foreach (var cmd in EnabledCommands())
        {
            foreach (var phrase in cmd.Phrases)
            {
                var r = FuzzyMatcher.Match(normalized, phrase);
                if (r is null || r.Score < Threshold) continue;
                if (best is null || r.Score > best.Score ||
                    (Math.Abs(r.Score - best.Score) < 1e-6 && phrase.Length > best.Command.Phrases.Max(p => p.Length)))
                    best = new CommandMatch(cmd, r.Score, r.Slots, normalized);
            }
        }
        return best;
    }

    /// <summary>Возвращает цепочку команд. Если вся фраза — одна команда с высоким баллом, цепочку не режем.</summary>
    public IReadOnlyList<CommandMatch> MatchChain(string rawText)
    {
        var normalized = PhraseNormalizer.Normalize(rawText);
        if (normalized.Length == 0) return Array.Empty<CommandMatch>();

        var whole = Best(normalized);
        var parts = Split(" " + normalized + " ");
        if (parts.Count <= 1) return whole is null ? Array.Empty<CommandMatch>() : new[] { whole };

        var chain = new List<CommandMatch>();
        foreach (var p in parts)
        {
            var m = Best(p);
            if (m is null) { chain.Clear(); break; }
            chain.Add(m);
        }

        if (chain.Count > 1 && (whole is null || chain.Average(c => c.Score) >= whole.Score - 0.02))
            return chain;
        return whole is null ? Array.Empty<CommandMatch>() : new[] { whole };
    }

    private static List<string> Split(string padded)
    {
        var pieces = new List<string> { padded };
        foreach (var c in Connectors)
        {
            pieces = pieces.SelectMany(p => p.Split(c, StringSplitOptions.RemoveEmptyEntries)).ToList();
        }
        return pieces.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }
}
