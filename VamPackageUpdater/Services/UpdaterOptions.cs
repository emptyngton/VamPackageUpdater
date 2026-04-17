using VamPackageUpdater.Models;

namespace VamPackageUpdater.Services;

public sealed class UpdaterOptions
{
    public string SourceVarPath { get; set; } = "";
    public List<PluginUpdate> PluginUpdates { get; set; } = new();
    public string? NewLicenseLine { get; set; }
    public bool DryRun { get; set; }
    public Func<string, CollisionChoice>? OnCollision { get; set; }
}

public enum CollisionChoice
{
    Overwrite,
    NextFree,
    Cancel
}

public static class Licenses
{
    public static readonly IReadOnlyDictionary<string, string?> All = new Dictionary<string, string?>
    {
        ["Do not change license"] = null,
        ["Creative Commons (CC BY-NC-SA 4.0)"] = "\"licenseType\" : \"CC BY-NC-SA 4.0\",",
        ["Creative Commons (CC BY-SA 4.0)"]    = "\"licenseType\" : \"CC BY-SA 4.0\",",
        ["Creative Commons (CC BY 4.0)"]       = "\"licenseType\" : \"CC BY 4.0\",",
        ["No Rights Reserved (CC0)"]           = "\"licenseType\" : \"CC0\",",
        ["PC (Protected Content)"]             = "\"licenseType\" : \"PC\","
    };
}
