using System.IO;
using System.Text.Json;

namespace VamPackageUpdater.Services;

/// <summary>
/// After the plugin-version regex pass rewrites meta.json, multiple dependency
/// entries that collapsed to the same key (e.g. both "AcidBubbles.Timeline.283"
/// and "AcidBubbles.Timeline.287" rewritten to "AcidBubbles.Timeline.latest")
/// end up as duplicate JSON keys inside the dependencies object. VaM accepts
/// that (last-wins) but it's technically invalid JSON. This helper detects and
/// collapses the duplicates.
///
/// Strategy:
///   1. Parse meta.json with JsonDocument (preserves duplicate keys on iterate).
///   2. Walk the tree — if any object has duplicate keys at any depth, we know
///      we need to rewrite.
///   3. If duplicates exist, rebuild the tree as Dictionary&lt;string, object&gt;
///      (last-wins on collisions) and re-serialize with indented output.
///   4. If no duplicates, leave the file untouched so the original VaM-style
///      formatting is preserved.
/// </summary>
public static class MetaJsonDeduplicator
{
    public enum DedupResult { NotNeeded, Deduplicated, ParseFailed }

    public static DedupResult DedupIfNeeded(string metaJsonPath)
    {
        string original;
        try { original = File.ReadAllText(metaJsonPath); }
        catch (IOException) { return DedupResult.ParseFailed; }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(original); }
        catch (JsonException) { return DedupResult.ParseFailed; }

        using (doc)
        {
            if (!HasDuplicateKeys(doc.RootElement))
                return DedupResult.NotNeeded;

            var cleaned = BuildDedupedTree(doc.RootElement);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                // Match VaM's 3-space indent where System.Text.Json supports it (net9+).
                IndentCharacter = ' ',
                IndentSize = 3
            };
            var json = JsonSerializer.Serialize(cleaned, options);
            File.WriteAllText(metaJsonPath, json);
            return DedupResult.Deduplicated;
        }
    }

    private static bool HasDuplicateKeys(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var prop in element.EnumerateObject())
                {
                    if (!seen.Add(prop.Name)) return true;
                    if (HasDuplicateKeys(prop.Value)) return true;
                }
                return false;
            }
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (HasDuplicateKeys(item)) return true;
                return false;
            default:
                return false;
        }
    }

    private static object? BuildDedupedTree(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                // Dictionary<string, object?> naturally last-wins on duplicate keys.
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var prop in element.EnumerateObject())
                    dict[prop.Name] = BuildDedupedTree(prop.Value);
                return dict;
            }
            case JsonValueKind.Array:
            {
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                    list.Add(BuildDedupedTree(item));
                return list;
            }
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var l)) return l;
                return element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
            default:
                return null;
        }
    }
}
