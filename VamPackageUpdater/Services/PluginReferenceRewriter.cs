using System.Text.RegularExpressions;
using VamPackageUpdater.Models;

namespace VamPackageUpdater.Services;

/// <summary>
/// Rewrites "Author.Package.N" / ".latest" / ".minN" references to the queued versions. Shared by the
/// .var updater and the loose scene updater so both apply exactly the same rule.
/// </summary>
public sealed class PluginReferenceRewriter
{
    private readonly List<(PluginUpdate Update, Regex Regex, string Replacement)> _patterns;

    public PluginReferenceRewriter(IEnumerable<PluginUpdate> updates)
    {
        _patterns = updates
            .Where(u => !string.IsNullOrWhiteSpace(u.NewVersion))
            .Select(u => (
                u,
                new Regex($@"{Regex.Escape(u.PluginId)}\.(?:\d+|latest|min\d+)\b", RegexOptions.IgnoreCase),
                $"{u.PluginId}.{u.NewVersion.Trim()}"))
            .ToList();
    }

    public IReadOnlyList<PluginUpdate> Updates => _patterns.Select(p => p.Update).ToList();

    public bool IsEmpty => _patterns.Count == 0;

    /// <summary>Returns the rewritten text and how many references changed, adding per-plugin counts.</summary>
    public (string Text, int Count) Rewrite(string text, IDictionary<string, int> perPluginCounts)
    {
        var total = 0;
        foreach (var (update, regex, replacement) in _patterns)
        {
            var count = 0;
            text = regex.Replace(text, _ => { count++; return replacement; });
            if (count == 0) continue;
            total += count;
            perPluginCounts[update.PluginId] = perPluginCounts.TryGetValue(update.PluginId, out var n) ? n + count : count;
        }
        return (text, total);
    }
}
