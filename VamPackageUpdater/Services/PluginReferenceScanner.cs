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

    private const int SnippetContextLines = 5;
    private const int MaxLineLength = 400;
    private const int MatchLineLeftPadding = 100;

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
                var snippet = BuildSnippet(content, lineStarts, m.Index, m.Length, line);

                if (!groups.TryGetValue(id, out var group))
                {
                    group = new PluginReferenceGroup(id);
                    groups[id] = group;
                }
                group.AddOccurrence(new PluginReferenceOccurrence(
                    File: entry.FullName,
                    Line: line,
                    Column: snippet.MatchColumn,
                    MatchLength: m.Length,
                    Version: version,
                    Category: category,
                    SnippetLines: snippet.Lines,
                    SnippetMatchLineIndex: snippet.MatchLineIndex,
                    SnippetStartLine: snippet.StartLine));
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
        if (StartsWithI(path, "Custom/Sounds/"))                     return PluginCategory.Audio;
        if (StartsWithI(path, "Saves/scene/"))                       return PluginCategory.Scene;
        return PluginCategory.Other;
    }

    private static bool StartsWithI(ReadOnlySpan<char> span, string prefix) =>
        span.StartsWith(prefix.AsSpan(), StringComparison.OrdinalIgnoreCase);

    private readonly record struct Snippet(
        IReadOnlyList<string> Lines,
        int MatchColumn,
        int MatchLineIndex,
        int StartLine);

    /// <summary>
    /// Produces a list of up to (2*SnippetContextLines+1) context lines centered on the match,
    /// individually truncated to MaxLineLength. The match line is re-centered on the match so
    /// long morph/blob lines remain readable.
    /// </summary>
    private static Snippet BuildSnippet(
        string content, List<int> lineStarts, int matchStart, int matchLen, int matchLineOneBased)
    {
        var totalLines = lineStarts.Count;
        var matchLineIdx = matchLineOneBased - 1;
        var firstIdx = Math.Max(0, matchLineIdx - SnippetContextLines);
        var lastIdx = Math.Min(totalLines - 1, matchLineIdx + SnippetContextLines);

        var lines = new List<string>(lastIdx - firstIdx + 1);
        var matchColumn = 1;

        for (var i = firstIdx; i <= lastIdx; i++)
        {
            var start = lineStarts[i];
            var end = i + 1 < totalLines ? lineStarts[i + 1] : content.Length;
            while (end > start && (content[end - 1] == '\n' || content[end - 1] == '\r')) end--;
            var rawLen = end - start;

            if (i == matchLineIdx)
            {
                var matchColInLine = matchStart - start + 1;
                var (text, shiftedCol) = TruncateAroundMatch(content, start, rawLen, matchColInLine, matchLen);
                lines.Add(text);
                matchColumn = shiftedCol;
            }
            else
            {
                lines.Add(TruncateLine(content, start, rawLen));
            }
        }

        return new Snippet(lines, matchColumn, matchLineIdx - firstIdx, firstIdx + 1);
    }

    private static string TruncateLine(string content, int start, int len)
    {
        if (len <= MaxLineLength) return content.Substring(start, len);
        return content.Substring(start, MaxLineLength) + " …";
    }

    /// <summary>
    /// Builds the match line with a window centered on the match. Returns the truncated text
    /// and the adjusted 1-based column of the match within that text.
    /// </summary>
    private static (string Text, int Column) TruncateAroundMatch(
        string content, int lineStart, int lineLen, int matchColOneBased, int matchLen)
    {
        if (lineLen <= MaxLineLength)
            return (content.Substring(lineStart, lineLen), matchColOneBased);

        // Desired window: [matchCol - leftPad, matchCol - leftPad + MaxLineLength)
        var desiredStart = matchColOneBased - 1 - MatchLineLeftPadding;
        if (desiredStart < 0) desiredStart = 0;
        if (desiredStart + MaxLineLength > lineLen) desiredStart = Math.Max(0, lineLen - MaxLineLength);

        var windowLen = Math.Min(MaxLineLength, lineLen - desiredStart);
        var text = content.Substring(lineStart + desiredStart, windowLen);

        var leftEllipsis = desiredStart > 0;
        var rightEllipsis = desiredStart + windowLen < lineLen;

        var prefix = leftEllipsis ? "… " : "";
        var suffix = rightEllipsis ? " …" : "";

        // New column = prefix length + (original col - desiredStart)
        var shiftedCol = prefix.Length + (matchColOneBased - desiredStart);

        // Guard against the match itself being clipped by the window (shouldn't happen given leftPad).
        if (shiftedCol < 1) shiftedCol = 1;
        if (shiftedCol + matchLen - 1 > prefix.Length + windowLen)
            shiftedCol = Math.Max(1, prefix.Length + windowLen - matchLen + 1);

        return (prefix + text + suffix, shiftedCol);
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
