namespace VamPackageUpdater.Models;

public enum PluginCategory
{
    /// <summary>No path suffix seen — likely only appears in meta.json dependencies block.</summary>
    Reference,
    Plugin,
    Clothing,
    Hair,
    Appearance,
    Morph,
    Texture,
    Pose,
    Asset,
    Audio,
    Scene,
    Other
}

public static class PluginCategoryExtensions
{
    public static string ToLabel(this PluginCategory c) => c switch
    {
        PluginCategory.Plugin => "Plugins",
        PluginCategory.Clothing => "Clothing",
        PluginCategory.Hair => "Hair",
        PluginCategory.Appearance => "Appearances",
        PluginCategory.Morph => "Morphs",
        PluginCategory.Texture => "Textures",
        PluginCategory.Pose => "Poses",
        PluginCategory.Asset => "Assets",
        PluginCategory.Audio => "Audio",
        PluginCategory.Scene => "Scenes",
        PluginCategory.Reference => "Reference-only",
        _ => "Other"
    };
}
