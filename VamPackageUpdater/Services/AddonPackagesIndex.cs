using System.IO;
using System.Text.RegularExpressions;
using VamPackageUpdater.Models;

namespace VamPackageUpdater.Services;

/// <summary>
/// Indexes .var files in a VaM AddonPackages folder so we can quickly answer
/// "is this dependency already installed?" for each entry in a scene's
/// meta.json dependencies tree.
///
/// Dependency names come in two forms:
///   - "Author.Package.N"        - exact version match required
///   - "Author.Package.latest"   - any installed version satisfies
///
/// A third edge case we tolerate:
///   - "Author.Package.min.N"    - any version >= N satisfies (rare, VaM syntax)
/// </summary>
public sealed class AddonPackagesIndex
{
    // Matches Author.Package.Version[.latest | .min] in a .var filename (without the extension).
    // VaM filenames always follow Author.Package.N pattern (Author/Package are word-ish).
    private static readonly Regex VarNamePattern = new(
        @"^(?<author>[^.]+)\.(?<package>.+)\.(?<version>\d+)$",
        RegexOptions.Compiled);

    // Lookup: "Author.Package" (case-insensitive) -> list of installed int versions.
    private readonly Dictionary<string, List<int>> _installedByPackage =
        new(StringComparer.OrdinalIgnoreCase);

    // For setting InstalledPath on matches: "Author.Package.N" -> absolute path.
    private readonly Dictionary<string, string> _pathByFullName =
        new(StringComparer.OrdinalIgnoreCase);

    public string AddonPackagesFolder { get; }

    public AddonPackagesIndex(string addonPackagesFolder)
    {
        AddonPackagesFolder = addonPackagesFolder;
        if (!Directory.Exists(addonPackagesFolder)) return;

        // Enumerate every .var in AddonPackages (recursive — VaM supports subfolders).
        foreach (var file in Directory.EnumerateFiles(addonPackagesFolder, "*.var", SearchOption.AllDirectories))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            var match = VarNamePattern.Match(stem);
            if (!match.Success) continue;

            var author = match.Groups["author"].Value;
            var package = match.Groups["package"].Value;
            if (!int.TryParse(match.Groups["version"].Value, out var version)) continue;

            var pkgKey = $"{author}.{package}";
            if (!_installedByPackage.TryGetValue(pkgKey, out var versions))
            {
                versions = new List<int>();
                _installedByPackage[pkgKey] = versions;
            }
            versions.Add(version);

            _pathByFullName[$"{pkgKey}.{version}"] = file;
        }
    }

    /// <summary>
    /// Populate Status + InstalledPath on each dependency based on what's in the folder.
    /// Leaves status as Missing when not found; caller can then push them through the Hub client.
    /// </summary>
    public void AnnotateStatus(IEnumerable<HubDependency> deps)
    {
        foreach (var dep in deps)
        {
            if (!TryParseDepName(dep.Name, out var pkgKey, out var wantedVersion, out var isLatest, out var isMinVersion))
            {
                dep.Status = HubDependencyStatus.Missing;
                continue;
            }

            if (!_installedByPackage.TryGetValue(pkgKey, out var installedVersions) || installedVersions.Count == 0)
            {
                dep.Status = HubDependencyStatus.Missing;
                continue;
            }

            int? satisfyingVersion = null;
            if (isLatest)
            {
                satisfyingVersion = installedVersions.Max();
            }
            else if (isMinVersion)
            {
                satisfyingVersion = installedVersions.Where(v => v >= wantedVersion).OrderByDescending(v => v).FirstOrDefault();
                if (satisfyingVersion == 0 && !installedVersions.Contains(0)) satisfyingVersion = null;
            }
            else
            {
                satisfyingVersion = installedVersions.Contains(wantedVersion) ? wantedVersion : (int?)null;
            }

            if (satisfyingVersion is int v)
            {
                dep.InstalledPath = _pathByFullName.TryGetValue($"{pkgKey}.{v}", out var p) ? p : null;
                dep.InstalledVersion = v;
                dep.Status = HubDependencyStatus.Installed;
            }
            else
            {
                dep.InstalledVersion = null;
                dep.Status = HubDependencyStatus.Missing;
            }
        }
    }

    /// <summary>
    /// Parse a dependency name like "Author.Package.287", "Author.Package.latest", or "Author.Package.min.5".
    /// Returns false if the name doesn't look like a valid VaM package reference.
    /// </summary>
    private static bool TryParseDepName(string name, out string pkgKey, out int version, out bool isLatest, out bool isMinVersion)
    {
        pkgKey = "";
        version = 0;
        isLatest = false;
        isMinVersion = false;

        if (string.IsNullOrWhiteSpace(name)) return false;

        var parts = name.Split('.');
        if (parts.Length < 3) return false;

        var last = parts[^1];
        if (string.Equals(last, "latest", StringComparison.OrdinalIgnoreCase))
        {
            isLatest = true;
            pkgKey = string.Join('.', parts, 0, parts.Length - 1);
            return !string.IsNullOrWhiteSpace(pkgKey);
        }

        if (int.TryParse(last, out version))
        {
            // Check for "...min.N" shape (VaM syntax for "at least N").
            if (parts.Length >= 4 && string.Equals(parts[^2], "min", StringComparison.OrdinalIgnoreCase))
            {
                isMinVersion = true;
                pkgKey = string.Join('.', parts, 0, parts.Length - 2);
                return !string.IsNullOrWhiteSpace(pkgKey);
            }

            pkgKey = string.Join('.', parts, 0, parts.Length - 1);
            return !string.IsNullOrWhiteSpace(pkgKey);
        }

        return false;
    }

    /// <summary>
    /// Resolve a dependency reference ("Author.Package.3", "Author.Package.latest", etc.)
    /// to an absolute .var path in this AddonPackages folder. For .latest we pick the
    /// highest installed version; for .min.N we pick the highest installed that's >= N.
    /// Returns null if nothing satisfies the ref.
    /// </summary>
    public string? TryGetInstalledVarPath(string depName)
    {
        if (!TryParseDepName(depName, out var pkgKey, out var version, out var isLatest, out var isMinVersion))
            return null;
        if (!_installedByPackage.TryGetValue(pkgKey, out var installed) || installed.Count == 0)
            return null;

        int? chosen = null;
        if (isLatest)
            chosen = installed.Max();
        else if (isMinVersion)
        {
            var candidates = installed.Where(v => v >= version).ToList();
            if (candidates.Count > 0) chosen = candidates.Max();
        }
        else if (installed.Contains(version))
            chosen = version;

        return chosen is int v && _pathByFullName.TryGetValue($"{pkgKey}.{v}", out var p) ? p : null;
    }

    /// <summary>True if the dependency name ends in ".latest" (case insensitive).</summary>
    public static bool IsLatestRef(string depName) =>
        !string.IsNullOrEmpty(depName) &&
        depName.EndsWith(".latest", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parse the requested version from a dependency reference like "Author.Package.3" → 3.
    /// Returns null for .latest / .min.N / malformed refs.
    /// </summary>
    public static int? ParseRequestedVersion(string depName)
    {
        if (TryParseDepName(depName, out _, out var v, out var isLatest, out var isMin) && !isLatest && !isMin)
            return v;
        return null;
    }

    /// <summary>
    /// Split a dep reference into its package-base and version-label parts.
    /// Example: "AcidBubbles.Timeline.latest" → ("AcidBubbles.Timeline", "latest").
    /// Example: "SPQR.Footsteps.3"            → ("SPQR.Footsteps", "3").
    /// Example: "Author.Package.min.5"        → ("Author.Package", "min.5").
    /// Falls back to (name, "") for names we can't parse.
    /// </summary>
    public static (string PackageBase, string VersionLabel) SplitNameParts(string depName)
    {
        if (!TryParseDepName(depName, out var pkgKey, out var version, out var isLatest, out var isMin))
            return (depName, "");

        var label = isLatest ? "latest"
            : isMin ? $"min.{version}"
            : version.ToString();
        return (pkgKey, label);
    }

    /// <summary>
    /// Parse the version from a Hub-returned filename like "AcidBubbles.Timeline.291.var" → 291.
    /// Returns null if the name doesn't match the expected pattern.
    /// </summary>
    public static int? ParseVersionFromFilename(string? filename)
    {
        if (string.IsNullOrEmpty(filename)) return null;
        var stem = Path.GetFileNameWithoutExtension(filename);
        var match = VarNamePattern.Match(stem);
        return match.Success && int.TryParse(match.Groups["version"].Value, out var v) ? v : (int?)null;
    }

    /// <summary>
    /// Best guess at the VaM AddonPackages folder based on where the loaded .var lives.
    /// If the .var sits directly inside a folder named "AddonPackages" (case-insensitive),
    /// that folder is returned. Otherwise the parent directory is returned as a fallback
    /// (user may need to override via a picker).
    /// </summary>
    public static string? InferAddonPackagesFolder(string loadedVarPath)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(loadedVarPath) ?? "");
        while (dir is not null)
        {
            if (string.Equals(dir.Name, "AddonPackages", StringComparison.OrdinalIgnoreCase))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Path.GetDirectoryName(loadedVarPath);
    }
}
