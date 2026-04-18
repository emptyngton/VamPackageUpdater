using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace VamPackageUpdater.Models;

public enum VoxtaResourceStatus
{
    /// <summary>Referenced by a scene and already bundled in the .var.</summary>
    Bundled,
    /// <summary>Referenced by a scene but no matching PNG is bundled. User action needed.</summary>
    Missing,
    /// <summary>Missing, but user has selected a PNG to attach on the next apply.</summary>
    Attached,
    /// <summary>Bundled in the .var but not referenced by any scene — probably leftover. Informational only.</summary>
    Orphan
}

public sealed class VoxtaResourceRef : INotifyPropertyChanged
{
    public VoxtaResourceKind Kind { get; init; }
    public string Id { get; init; } = "";

    /// <summary>Human-friendly name parsed from the scene JSON (e.g. "Elara Meadowlight"), if present.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Scene file that referenced this resource (first one found).</summary>
    public string? SceneFile { get; set; }

    /// <summary>Path inside the .var if the resource PNG is already bundled.</summary>
    public string? BundledPath { get; set; }

    private VoxtaResourceStatus _status;
    public VoxtaResourceStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusLabel));
        }
    }

    private string? _attachedSourcePath;
    /// <summary>Absolute path on disk to the PNG the user wants to attach.</summary>
    public string? AttachedSourcePath
    {
        get => _attachedSourcePath;
        set
        {
            if (_attachedSourcePath == value) return;
            _attachedSourcePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AttachedFileName));
        }
    }

    public string? AttachedFileName =>
        string.IsNullOrEmpty(AttachedSourcePath) ? null : Path.GetFileName(AttachedSourcePath);

    public string StatusLabel => Status switch
    {
        VoxtaResourceStatus.Bundled => "Bundled",
        VoxtaResourceStatus.Missing => "Missing",
        VoxtaResourceStatus.Attached => "Attached",
        VoxtaResourceStatus.Orphan => "Orphan",
        _ => Status.ToString()
    };

    public string KindLabel => Kind.ToLabel();

    /// <summary>Short display form: "Elara Meadowlight" or just the UUID if no name known.</summary>
    public string NameOrId => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A staged attachment that PackageUpdater will copy into the temp dir before re-zipping.</summary>
public sealed record VoxtaResourceAttachment(VoxtaResourceKind Kind, string Id, string SourcePngPath);
