using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using VamPackageUpdater.Models;

namespace VamPackageUpdater.Services;

/// <summary>
/// Finds Voxta resources referenced by scene JSONs inside a .var (Character/Scenario/MemoryBook/Package UUIDs)
/// and correlates them with bundled PNG files under Saves/PluginData/Voxta/{Kind}s/.
/// </summary>
public sealed class VoxtaResourceScanner
{
    // Matches fields in the Voxta plugin's own storable block, e.g.:
    //   "Character ID" : "916a..."
    //   "Character ID 2" : "..."
    //   "Scenario ID" : "..."
    private static readonly Regex ResourceIdPattern = new(
        @"""(?<key>Character|Scenario|MemoryBook|Package)\s+ID\s*(?<idx>\d*)""\s*:\s*""(?<uuid>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})""",
        RegexOptions.Compiled);

    // Matches VaM trigger actions that target the Voxta plugin, e.g. buttons that
    // switch characters at runtime. Shape:
    //   "receiver" : "plugin#3_Voxta",
    //   "receiverTargetName" : "Character ID",
    //   "stringChooserValue" : "916a..."
    // The [^}]*? sections keep the match inside a single action object so we don't
    // accidentally stitch fields across siblings.
    private static readonly Regex VoxtaTriggerActionPattern = new(
        @"""receiver""\s*:\s*""[^""]*Voxta[^""]*""[^}]*?""receiverTargetName""\s*:\s*""(?<key>Character|Scenario|MemoryBook|Package)\s+ID\s*(?<idx>\d*)""[^}]*?""stringChooserValue""\s*:\s*""(?<uuid>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})""",
        RegexOptions.Compiled);

    private static readonly Regex ResourceNamePattern = new(
        @"""(?<key>Character|Scenario|MemoryBook|Package)\s+Name\s*(?<idx>\d*)""\s*:\s*""(?<name>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex BundledResourcePattern = new(
        @"^Saves/PluginData/Voxta/(?<kindFolder>Characters|Scenarios|MemoryBooks|Packages)/(?<uuid>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\.(?<kindSuffix>character|scenario|memorybook|package)(?:\.[^/]+)?\.(?:png|json|zip|vxz|voxpkg)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Task<List<VoxtaResourceRef>> ScanAsync(string varPath, CancellationToken ct = default) =>
        Task.Run(() => Scan(varPath, ct), ct);

    public List<VoxtaResourceRef> Scan(string varPath, CancellationToken ct = default)
    {
        var referenced = new Dictionary<(VoxtaResourceKind Kind, string Id), VoxtaResourceRef>();
        var bundled = new Dictionary<(VoxtaResourceKind Kind, string Id), string>();

        using var archive = ZipFile.OpenRead(varPath);

        // Pass 1: find bundled PNGs / data files
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/')) continue;

            var path = entry.FullName.Replace('\\', '/');
            var m = BundledResourcePattern.Match(path);
            if (!m.Success) continue;

            if (!TryParseKindFromFolder(m.Groups["kindFolder"].Value, out var kind)) continue;
            var uuid = m.Groups["uuid"].Value.ToLowerInvariant();
            bundled.TryAdd((kind, uuid), entry.FullName);
        }

        // Pass 2: find referenced resources in scene JSONs (and other .json files for completeness)
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/')) continue;
            if (!Path.GetExtension(entry.FullName).Equals(".json", StringComparison.OrdinalIgnoreCase)) continue;

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

            // Fast skip: the fields we care about only exist in files that mention Voxta
            if (content.IndexOf("Voxta", StringComparison.OrdinalIgnoreCase) < 0) continue;

            // Gather names indexed by (kind, idx) so we can pair them with IDs from the same plugin block
            var names = new Dictionary<(VoxtaResourceKind, string), string>();
            foreach (Match nm in ResourceNamePattern.Matches(content))
            {
                if (!TryParseKindFromKeyword(nm.Groups["key"].Value, out var nkind)) continue;
                var idx = nm.Groups["idx"].Value;
                names[(nkind, idx)] = nm.Groups["name"].Value.Trim();
            }

            foreach (Match im in ResourceIdPattern.Matches(content))
            {
                if (!TryParseKindFromKeyword(im.Groups["key"].Value, out var kind)) continue;
                var idx = im.Groups["idx"].Value;
                var uuid = im.Groups["uuid"].Value.ToLowerInvariant();
                RecordReference(referenced, kind, uuid, entry.FullName, idx, names);
            }

            // Trigger actions (e.g. VaM buttons that switch character) that write
            // to the Voxta plugin's Character ID / Scenario ID / etc.
            foreach (Match am in VoxtaTriggerActionPattern.Matches(content))
            {
                if (!TryParseKindFromKeyword(am.Groups["key"].Value, out var kind)) continue;
                var uuid = am.Groups["uuid"].Value.ToLowerInvariant();
                // Trigger actions don't carry a paired "Name" field, so no name lookup.
                RecordReference(referenced, kind, uuid, entry.FullName, idx: "", names: null);
            }
        }

        // Correlate status
        foreach (var kv in referenced)
        {
            if (bundled.TryGetValue(kv.Key, out var bundledPath))
            {
                kv.Value.BundledPath = bundledPath;
                kv.Value.Status = VoxtaResourceStatus.Bundled;
            }
            else
            {
                kv.Value.Status = VoxtaResourceStatus.Missing;
            }
        }

        // Surface orphans (bundled but not referenced) — usually nothing, but useful to see
        foreach (var kv in bundled)
        {
            if (referenced.ContainsKey(kv.Key)) continue;
            referenced[kv.Key] = new VoxtaResourceRef
            {
                Kind = kv.Key.Kind,
                Id = kv.Key.Id,
                BundledPath = kv.Value,
                Status = VoxtaResourceStatus.Orphan
            };
        }

        // Backfill DisplayName from the bundled PNG's embedded Voxta metadata for any
        // resource the scene-JSON pairing didn't name (e.g. characters only referenced
        // via a VaM button trigger, which carries a UUID but no name field).
        foreach (var kv in referenced)
        {
            var rref = kv.Value;
            if (!string.IsNullOrWhiteSpace(rref.DisplayName)) continue;
            if (string.IsNullOrEmpty(rref.BundledPath)) continue;

            ct.ThrowIfCancellationRequested();
            var entry = archive.GetEntry(rref.BundledPath);
            if (entry is null) continue;

            try
            {
                using var es = entry.Open();
                var name = VoxtaPngInspector.TryReadResourceName(es);
                if (!string.IsNullOrWhiteSpace(name))
                    rref.DisplayName = name;
            }
            catch (InvalidDataException) { /* skip — malformed PNG */ }
            catch (IOException)          { /* skip — couldn't read entry */ }
        }

        return referenced.Values
            .OrderBy(r => r.Status) // Missing first (lowest enum), then Bundled, then Orphan
            .ThenBy(r => (int)r.Kind)
            .ThenBy(r => r.NameOrId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void RecordReference(
        Dictionary<(VoxtaResourceKind Kind, string Id), VoxtaResourceRef> referenced,
        VoxtaResourceKind kind,
        string uuid,
        string sceneFile,
        string idx,
        Dictionary<(VoxtaResourceKind, string), string>? names)
    {
        var key = (kind, uuid);
        if (!referenced.TryGetValue(key, out var rref))
        {
            rref = new VoxtaResourceRef
            {
                Kind = kind,
                Id = uuid,
                SceneFile = sceneFile
            };
            referenced[key] = rref;
        }

        if (rref.DisplayName is null && names is not null)
        {
            // Try exact idx first, then fall back to idx="1" when id idx=""
            // (VaM Voxta plugin names the primary character "Character Name 1" but
            //  the primary ID is just "Character ID" with no numeric suffix).
            if (names.TryGetValue((kind, idx), out var n))
                rref.DisplayName = n;
            else if (idx.Length == 0 && names.TryGetValue((kind, "1"), out var n1))
                rref.DisplayName = n1;
            else if (idx == "1" && names.TryGetValue((kind, ""), out var n0))
                rref.DisplayName = n0;
        }
    }

    private static bool TryParseKindFromKeyword(string keyword, out VoxtaResourceKind kind)
    {
        switch (keyword)
        {
            case "Character":  kind = VoxtaResourceKind.Character;  return true;
            case "Scenario":   kind = VoxtaResourceKind.Scenario;   return true;
            case "MemoryBook": kind = VoxtaResourceKind.MemoryBook; return true;
            case "Package":    kind = VoxtaResourceKind.Package;    return true;
            default:           kind = default;                      return false;
        }
    }

    private static bool TryParseKindFromFolder(string folder, out VoxtaResourceKind kind)
    {
        switch (folder)
        {
            case "Characters":  kind = VoxtaResourceKind.Character;  return true;
            case "Scenarios":   kind = VoxtaResourceKind.Scenario;   return true;
            case "MemoryBooks": kind = VoxtaResourceKind.MemoryBook; return true;
            case "Packages":    kind = VoxtaResourceKind.Package;    return true;
            default:            kind = default;                      return false;
        }
    }
}
