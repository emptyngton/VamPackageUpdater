using System.IO;
using System.IO.Compression;
using System.Text.Json;
using VamPackageUpdater.Models;

namespace VamPackageUpdater.Services;

/// <summary>
/// Reads meta.json from a .var and flattens its (nested) dependencies tree into
/// a unique list of HubDependency entries. The tree is already transitive as
/// written by VaM's Package Builder, so we just walk every nested "dependencies"
/// object and collect keys.
/// </summary>
public sealed class MetaJsonDependencyScanner
{
    public Task<List<HubDependency>> ScanAsync(string varPath, CancellationToken ct = default) =>
        Task.Run(() => Scan(varPath, ct), ct);

    public List<HubDependency> Scan(string varPath, CancellationToken ct = default)
    {
        using var archive = ZipFile.OpenRead(varPath);
        var metaEntry = archive.GetEntry("meta.json");
        if (metaEntry is null) return new List<HubDependency>();

        string json;
        try
        {
            using var reader = new StreamReader(metaEntry.Open());
            json = reader.ReadToEnd();
        }
        catch
        {
            return new List<HubDependency>();
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return new List<HubDependency>(); }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("dependencies", out var depsRoot) ||
                depsRoot.ValueKind != JsonValueKind.Object)
                return new List<HubDependency>();

            var collected = new Dictionary<string, HubDependency>(StringComparer.OrdinalIgnoreCase);
            WalkDeps(depsRoot, collected, ct);
            return collected.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    private static void WalkDeps(JsonElement depsObj, IDictionary<string, HubDependency> collected, CancellationToken ct)
    {
        foreach (var prop in depsObj.EnumerateObject())
        {
            ct.ThrowIfCancellationRequested();
            var name = prop.Name;

            string? license = null;
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                if (prop.Value.TryGetProperty("licenseType", out var lic) && lic.ValueKind == JsonValueKind.String)
                    license = lic.GetString();

                // Recurse into nested "dependencies" blocks (transitive tree).
                if (prop.Value.TryGetProperty("dependencies", out var nested) && nested.ValueKind == JsonValueKind.Object)
                    WalkDeps(nested, collected, ct);
            }

            if (!collected.ContainsKey(name))
            {
                collected[name] = new HubDependency
                {
                    Name = name,
                    License = license,
                    Status = HubDependencyStatus.Missing // actual status filled in by AddonPackagesIndex pass
                };
            }
        }
    }
}
