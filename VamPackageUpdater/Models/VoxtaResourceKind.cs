namespace VamPackageUpdater.Models;

public enum VoxtaResourceKind
{
    Character,
    Scenario,
    MemoryBook,
    Package
}

public static class VoxtaResourceKindExtensions
{
    /// <summary>
    /// Folder name inside Saves/PluginData/Voxta/ for this kind (plural).
    /// Matches the Voxta VaM plugin's TryLoadResource path (Voxta.cs:1204).
    /// </summary>
    public static string FolderName(this VoxtaResourceKind kind) => kind switch
    {
        VoxtaResourceKind.Character => "Characters",
        VoxtaResourceKind.Scenario => "Scenarios",
        VoxtaResourceKind.MemoryBook => "MemoryBooks",
        VoxtaResourceKind.Package => "Packages",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>
    /// Lowercase suffix used in bundled filenames, e.g. "{uuid}.character.png".
    /// </summary>
    public static string FilenameSuffix(this VoxtaResourceKind kind) => kind switch
    {
        VoxtaResourceKind.Character => "character",
        VoxtaResourceKind.Scenario => "scenario",
        VoxtaResourceKind.MemoryBook => "memorybook",
        VoxtaResourceKind.Package => "package",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string ToLabel(this VoxtaResourceKind kind) => kind switch
    {
        VoxtaResourceKind.Character => "Character",
        VoxtaResourceKind.Scenario => "Scenario",
        VoxtaResourceKind.MemoryBook => "Memory Book",
        VoxtaResourceKind.Package => "Package",
        _ => kind.ToString()
    };

    /// <summary>
    /// Build the path inside a .var where the bundled resource PNG should live.
    /// Example: "Saves/PluginData/Voxta/Characters/916a705b-....character.png"
    /// </summary>
    public static string BuildBundledPath(this VoxtaResourceKind kind, string uuid) =>
        $"Saves/PluginData/Voxta/{kind.FolderName()}/{uuid}.{kind.FilenameSuffix()}.png";
}
