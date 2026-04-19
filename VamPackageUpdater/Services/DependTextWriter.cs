using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace VamPackageUpdater.Services;

/// <summary>
/// Regenerates the sidecar <c>{varname}.var.depend.txt</c> that VaM's
/// Add-On Package Builder writes next to every published .var. The file
/// is a human-readable listing of every dependency with its license and
/// (when available) the creator and promotional link, pulled from each
/// dependency's own meta.json in AddonPackages.
///
/// Format mirrors VaM's own output:
/// <code>
/// AcidBubbles.Glance.23    By: AcidBubbles    License: CC BY-SA    Link: https://...
/// </code>
///
/// Creator/Link are lifted from each dep's installed .var's meta.json.
/// If a dep isn't installed locally we still emit the package-key and
/// license (the license comes from our own scene's meta.json), just
/// without the creator/link fields — that's better than nothing.
/// </summary>
public sealed class DependTextWriter
{
    private const int PackageColumnWidth = 46;
    private const int CreatorColumnWidth = 25;   // "By: {name}" padded to this width
    private const int LicenseColumnWidth = 25;   // "License: {lic}" padded to this width

    private readonly MetaReader _metaReader = new();

    /// <summary>
    /// Produce the text content for {varPath}.depend.txt by walking the
    /// var's meta.json dependencies tree and cross-referencing AddonPackages
    /// (and optionally an existing sidecar) for creator/link info.
    /// </summary>
    /// <param name="varPath">The .var whose deps we're listing (the NEW output).</param>
    /// <param name="index">Optional AddonPackages lookup for meta.json creator/link.</param>
    /// <param name="existingSidecarPath">
    /// Optional path to an existing .depend.txt (typically the source var's sidecar).
    /// Used as a second source for creator/link when a dep isn't installed locally
    /// and when dep version changed between source and output (e.g. Glance.23 → Glance.latest).
    /// </param>
    public async Task<string> BuildAsync(
        string varPath,
        AddonPackagesIndex? index,
        string? existingSidecarPath = null,
        CancellationToken ct = default)
    {
        var entries = ReadDependencyEntries(varPath);
        var sidecar = !string.IsNullOrEmpty(existingSidecarPath) && File.Exists(existingSidecarPath)
            ? SidecarLookup.FromFile(existingSidecarPath)
            : null;

        var sb = new StringBuilder();
        foreach (var entry in entries.OrderBy(e => e.PackageKey, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            string creator = "";
            string link = "";

            // Source 1 (best): dep's own meta.json in AddonPackages.
            var installedPath = index?.TryGetInstalledVarPath(entry.PackageKey);
            if (!string.IsNullOrEmpty(installedPath) && File.Exists(installedPath))
            {
                try
                {
                    var meta = await _metaReader.ReadAsync(installedPath, ct);
                    creator = meta.CreatorName ?? "";
                    link    = meta.PromotionalLink ?? "";
                }
                catch (IOException) { /* skip — best-effort */ }
                catch (InvalidDataException) { /* skip — malformed .var */ }
            }

            // Source 2 (fallback): existing sidecar from the source var. Handles
            // the common case where the dep .var isn't installed on this machine
            // but VaM already wrote its info when the scene was originally packaged.
            if (sidecar is not null && (string.IsNullOrEmpty(creator) || string.IsNullOrEmpty(link)))
            {
                var (sidecarCreator, sidecarLink) = sidecar.Find(entry.PackageKey);
                if (string.IsNullOrEmpty(creator)) creator = sidecarCreator;
                if (string.IsNullOrEmpty(link)) link = sidecarLink;
            }

            sb.Append(entry.PackageKey.PadRight(PackageColumnWidth));
            // Long package names overflow the padding; one space separator is enough.
            if (entry.PackageKey.Length >= PackageColumnWidth) sb.Append(' ');

            var byField = string.IsNullOrEmpty(creator) ? "" : $"By: {creator}";
            sb.Append(byField.PadRight(CreatorColumnWidth));

            var licenseField = $"License: {entry.License}";
            sb.Append(licenseField.PadRight(LicenseColumnWidth));

            if (!string.IsNullOrEmpty(link))
                sb.Append("Link: ").Append(link);

            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Produce the .depend.txt and write it as "{varPath}.depend.txt".
    /// Always overwrites. Does nothing (and returns false) if the .var has no deps.
    /// </summary>
    public async Task<bool> WriteAsync(
        string varPath,
        AddonPackagesIndex? index,
        string? existingSidecarPath = null,
        CancellationToken ct = default)
    {
        var text = await BuildAsync(varPath, index, existingSidecarPath, ct);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var dependPath = varPath + ".depend.txt";
        await File.WriteAllTextAsync(dependPath, text, ct);
        return true;
    }

    /// <summary>
    /// Parse an existing .depend.txt into a lookup that supports both exact-key and
    /// base-name ("Author.Package" without version) matching. Base-name fallback lets
    /// us recover creator/link info after we rewrite refs to .latest / a different
    /// version, where the exact packageKey no longer matches.
    /// </summary>
    private sealed class SidecarLookup
    {
        private readonly Dictionary<string, (string Creator, string Link)> _byExactKey =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string Creator, string Link)> _byPackageBase =
            new(StringComparer.OrdinalIgnoreCase);

        public static SidecarLookup FromFile(string path)
        {
            var lookup = new SidecarLookup();
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (IOException) { return lookup; }

            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!TryParseLine(line, out var key, out var creator, out var link)) continue;

                var value = (creator, link);
                if (!lookup._byExactKey.ContainsKey(key))
                    lookup._byExactKey[key] = value;
                var baseName = AddonPackagesIndex.SplitNameParts(key).PackageBase;
                if (!string.IsNullOrEmpty(baseName) && !lookup._byPackageBase.ContainsKey(baseName))
                    lookup._byPackageBase[baseName] = value;
            }
            return lookup;
        }

        public (string Creator, string Link) Find(string depKey)
        {
            if (_byExactKey.TryGetValue(depKey, out var exact)) return exact;
            var baseName = AddonPackagesIndex.SplitNameParts(depKey).PackageBase;
            if (!string.IsNullOrEmpty(baseName) && _byPackageBase.TryGetValue(baseName, out var fuzzy))
                return fuzzy;
            return ("", "");
        }

        /// <summary>
        /// Parse one line of a .depend.txt like:
        ///   AcidBubbles.Glance.23    By: AcidBubbles    License: CC BY-SA    Link: https://...
        /// Uses "License:" / "Link:" as anchors so that licenses containing spaces
        /// ("CC BY-NC-SA", "PC EA") and multi-word creator names parse correctly.
        /// </summary>
        private static bool TryParseLine(string line, out string packageKey, out string creator, out string link)
        {
            packageKey = ""; creator = ""; link = "";
            var licenseIdx = line.IndexOf("License:", StringComparison.Ordinal);
            if (licenseIdx < 0) return false;

            var linkIdx = line.IndexOf("Link:", licenseIdx, StringComparison.Ordinal);
            if (linkIdx > 0) link = line.Substring(linkIdx + "Link:".Length).Trim();

            // Everything before "License:" is "packageKey + optional By: creator".
            var prefix = line.Substring(0, licenseIdx).TrimEnd();
            var byMarker = prefix.IndexOf(" By:", StringComparison.Ordinal);
            if (byMarker > 0)
            {
                packageKey = prefix.Substring(0, byMarker).TrimEnd();
                creator = prefix.Substring(byMarker + " By:".Length).Trim();
            }
            else
            {
                packageKey = prefix.TrimEnd();
            }
            return !string.IsNullOrEmpty(packageKey);
        }
    }

    /// <summary>
    /// Walk the .var's meta.json dependencies tree (transitive — nested
    /// "dependencies" objects are flattened) and return unique (packageKey, license)
    /// entries in the order first seen.
    /// </summary>
    private static List<DependEntry> ReadDependencyEntries(string varPath)
    {
        var result = new List<DependEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var archive = ZipFile.OpenRead(varPath);
        var metaEntry = archive.GetEntry("meta.json");
        if (metaEntry is null) return result;

        string json;
        try
        {
            using var stream = metaEntry.Open();
            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }
        catch { return result; }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return result; }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("dependencies", out var depsRoot) ||
                depsRoot.ValueKind != JsonValueKind.Object)
                return result;

            Walk(depsRoot, result, seen);
        }
        return result;
    }

    private static void Walk(JsonElement depsObj, List<DependEntry> result, HashSet<string> seen)
    {
        foreach (var prop in depsObj.EnumerateObject())
        {
            var key = prop.Name;
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                if (seen.Add(key))
                {
                    string license = "";
                    if (prop.Value.TryGetProperty("licenseType", out var lic) && lic.ValueKind == JsonValueKind.String)
                        license = lic.GetString() ?? "";
                    result.Add(new DependEntry(key, license));
                }
                if (prop.Value.TryGetProperty("dependencies", out var nested) && nested.ValueKind == JsonValueKind.Object)
                    Walk(nested, result, seen);
            }
        }
    }

    private sealed record DependEntry(string PackageKey, string License);
}
