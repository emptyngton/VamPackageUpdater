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
    public IEnumerable<string> Files => FileLines.Keys;
    public int FileCount => FileLines.Count;
    public int OccurrenceCount { get; private set; }

    /// <summary>
    /// Most frequent non-Reference category seen; falls back to Reference if nothing else was seen.
    /// </summary>
    public PluginCategory PrimaryCategory
    {
        get
        {
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

    public void AddOccurrence(string version, string file, int line, PluginCategory category)
    {
        CurrentVersions.Add(version);
        if (!FileLines.TryGetValue(file, out var lines))
        {
            lines = new List<int>();
            FileLines[file] = lines;
        }
        lines.Add(line);
        CategoryCounts[category] = CategoryCounts.GetValueOrDefault(category) + 1;
        OccurrenceCount++;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record PluginUpdate(string PluginId, string NewVersion);
