using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace VamPackageUpdater.Models;

public sealed class PluginReferenceGroup : INotifyPropertyChanged
{
    public string PluginId { get; }
    public HashSet<string> CurrentVersions { get; } = new();
    public Dictionary<string, List<int>> FileLines { get; } = new(StringComparer.Ordinal);
    public Dictionary<PluginCategory, int> CategoryCounts { get; } = new();
    private readonly List<PluginReferenceOccurrence> _occurrences = new();
    public IReadOnlyList<PluginReferenceOccurrence> Occurrences => _occurrences;
    public IEnumerable<string> Files => FileLines.Keys;
    public int FileCount => FileLines.Count;
    public int OccurrenceCount { get; private set; }

    /// <summary>
    /// Group-level category used by the main-grid tab filter.
    /// Plugin outranks everything: if any occurrence is a Plugin ref, the package is classified
    /// as Plugin even when other asset refs (audio/clothing/etc.) are more numerous — a script
    /// is the defining feature of a package, bundled assets are supplementary.
    /// Otherwise: most frequent non-Reference category, falling back to Reference.
    /// </summary>
    public PluginCategory PrimaryCategory
    {
        get
        {
            if (CategoryCounts.TryGetValue(PluginCategory.Plugin, out var pluginCount) && pluginCount > 0)
                return PluginCategory.Plugin;

            var best = PluginCategory.Reference;
            var bestCount = -1;
            foreach (var kv in CategoryCounts)
            {
                if (kv.Key == PluginCategory.Reference) continue;
                if (kv.Value > bestCount)
                {
                    best = kv.Key;
                    bestCount = kv.Value;
                }
            }
            return bestCount >= 0 ? best : PluginCategory.Reference;
        }
    }

    private string _newVersion = "";
    public string NewVersion
    {
        get => _newVersion;
        set
        {
            if (_newVersion == value) return;
            _newVersion = value;
            OnPropertyChanged();
        }
    }

    public string CurrentVersionsDisplay =>
        CurrentVersions.Count == 1
            ? CurrentVersions.First()
            : string.Join(", ", CurrentVersions.OrderBy(v => v));

    public PluginReferenceGroup(string id) => PluginId = id;

    public void AddOccurrence(PluginReferenceOccurrence occurrence)
    {
        CurrentVersions.Add(occurrence.Version);
        if (!FileLines.TryGetValue(occurrence.File, out var lines))
        {
            lines = new List<int>();
            FileLines[occurrence.File] = lines;
        }
        lines.Add(occurrence.Line);
        CategoryCounts[occurrence.Category] = CategoryCounts.GetValueOrDefault(occurrence.Category) + 1;
        _occurrences.Add(occurrence);
        OccurrenceCount++;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record PluginUpdate(string PluginId, string NewVersion);

/// <summary>
/// A single occurrence of a plugin id inside a .var content file.
/// Captured at scan time so the "view occurrences" dialog doesn't need to reopen the archive.
/// </summary>
/// <param name="File">Zip-relative path of the file that contains the match.</param>
/// <param name="Line">1-based line number of the match.</param>
/// <param name="Column">1-based column of the match start within <see cref="SnippetLines"/>[<see cref="SnippetMatchLineIndex"/>].</param>
/// <param name="MatchLength">Length (chars) of the matched token in the snippet's match line.</param>
/// <param name="Version">Version token captured by the regex (e.g. "3" or "latest").</param>
/// <param name="Category">Heuristic classification from the path suffix after the match.</param>
/// <param name="SnippetLines">Context lines around the match (typically ±5). Lines may be individually truncated with "… " / " …" markers.</param>
/// <param name="SnippetMatchLineIndex">Index into <see cref="SnippetLines"/> of the line that contains the match.</param>
/// <param name="SnippetStartLine">1-based line number of <see cref="SnippetLines"/>[0] in the original file.</param>
public sealed record PluginReferenceOccurrence(
    string File,
    int Line,
    int Column,
    int MatchLength,
    string Version,
    PluginCategory Category,
    IReadOnlyList<string> SnippetLines,
    int SnippetMatchLineIndex,
    int SnippetStartLine);
