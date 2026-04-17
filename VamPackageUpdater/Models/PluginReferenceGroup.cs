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
    public IEnumerable<string> Files => FileLines.Keys;
    public int FileCount => FileLines.Count;
    public int OccurrenceCount { get; private set; }

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

    public void AddOccurrence(string version, string file, int line)
    {
        CurrentVersions.Add(version);
        if (!FileLines.TryGetValue(file, out var lines))
        {
            lines = new List<int>();
            FileLines[file] = lines;
        }
        lines.Add(line);
        OccurrenceCount++;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record PluginUpdate(string PluginId, string NewVersion);
