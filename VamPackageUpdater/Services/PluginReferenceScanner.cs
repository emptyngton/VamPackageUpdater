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
                var category = ClassifyAt(content, m.Index + m.Length);

                if (!groups.TryGetValue(id, out var group))
                {
                    group = new PluginReferenceGroup(id);
                    groups[id] = group;
                }
                group.AddOccurrence(version, entry.FullName, line, category);
            }
        }

        return groups.Values.OrderBy(g => g.PluginId, StringComparer.Ordinal).ToList();
    }

    private static PluginCategory ClassifyAt(string content, int afterMatchIdx)
    {
        // Pattern: <Author.Pkg.Ver>:/<Path>
        if (afterMatchIdx >= content.Length || content[afterMatchIdx] != ':')
            return PluginCategory.Reference;

        var pathStart = afterMatchIdx + 1;
        if (pathStart < content.Length && content[pathStart] == '/') pathStart++;

        // Read until quote, newline, or control char
        var end = pathStart;
        while (end < content.Length)
        {
            var ch = content[end];
            if (ch == '"' || ch == '\n' || ch == '\r' || ch == '<') break;
            end++;
        }

        if (end == pathStart) return PluginCategory.Reference;

        // Use first ~80 chars only — more than enough to match the prefix
        var len = Math.Min(end - pathStart, 80);
        var path = content.AsSpan(pathStart, len);

        if (StartsWithI(path, "Custom/Scripts/"))                    return PluginCategory.Plugin;
        if (StartsWithI(path, "Custom/Clothing/"))                   return PluginCategory.Clothing;
        if (StartsWithI(path, "Custom/Hair/"))                       return PluginCategory.Hair;
        if (StartsWithI(path, "Custom/Atom/Person/Appearance/"))     return PluginCategory.Appearance;
        if (StartsWithI(path, "Custom/Atom/Person/Morphs/"))         return PluginCategory.Morph;
        if (StartsWithI(path, "Custom/Atom/Person/Textures/"))       return PluginCategory.Texture;
        if (StartsWithI(path, "Custom/Atom/Person/Pose/"))           return PluginCategory.Pose;
        if (StartsWithI(path, "Custom/Assets/"))                     return PluginCategory.Asset;
        if (StartsWithI(path, "Saves/scene/"))                       return PluginCategory.Scene;
        return PluginCategory.Other;
    }

    private static bool StartsWithI(ReadOnlySpan<char> span, string prefix) =>
        span.StartsWith(prefix.AsSpan(), StringComparison.OrdinalIgnoreCase);

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
