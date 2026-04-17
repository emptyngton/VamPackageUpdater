using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using VamPackageUpdater.Models;

namespace VamPackageUpdater.Services;

public sealed class PluginReferenceScanner
{
    private static readonly Regex PluginRefPattern = new(
        @"\b(?<id>[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*)\.(?<version>\d+|latest)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> ScannableExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".json", ".vap", ".vam", ".vaj", ".vab" };

    public Task<List<PluginReferenceGroup>> ScanAsync(string varPath, CancellationToken ct = default) =>
        Task.Run(() => Scan(varPath, ct), ct);

    public List<PluginReferenceGroup> Scan(string varPath, CancellationToken ct = default)
    {
        var groups = new Dictionary<string, PluginReferenceGroup>(StringComparer.Ordinal);

        using var archive = ZipFile.OpenRead(varPath);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/')) continue;
            var ext = Path.GetExtension(entry.FullName);
            if (!ScannableExtensions.Contains(ext)) continue;

            string content;
            try
            {
                using var reader = new StreamReader(entry.Open());
                content = reader.ReadToEnd();
            }
            catch
            {
                continue;
            }

            var matches = PluginRefPattern.Matches(content);
            if (matches.Count == 0) continue;

            var lineStarts = BuildLineStarts(content);
            foreach (Match m in matches)
            {
                var id = m.Groups["id"].Value;
                var version = m.Groups["version"].Value;
                var line = LineFromOffset(lineStarts, m.Index);

                if (!groups.TryGetValue(id, out var group))
                {
                    group = new PluginReferenceGroup(id);
                    groups[id] = group;
                }
                group.AddOccurrence(version, entry.FullName, line);
            }
        }

        return groups.Values.OrderBy(g => g.PluginId, StringComparer.Ordinal).ToList();
    }

    private static List<int> BuildLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') starts.Add(i + 1);
        return starts;
    }

    private static int LineFromOffset(List<int> lineStarts, int offset)
    {
        var lo = 0;
        var hi = lineStarts.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (lineStarts[mid] <= offset) lo = mid;
            else hi = mid - 1;
        }
        return lo + 1;
    }
}
