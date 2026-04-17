namespace VamPackageUpdater.Services;

public sealed class UpdaterOptions
{
    public string SourceVarPath { get; set; } = "";
    public string PluginName { get; set; } = "";
    public string NewVersion { get; set; } = "";
    public string? NewLicenseLine { get; set; }
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
