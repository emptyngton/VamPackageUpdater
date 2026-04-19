using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VamPackageUpdater.Models;

public enum HubDependencyStatus
{
    /// <summary>Already present in AddonPackages at a version that satisfies the ref.</summary>
    Installed,
    /// <summary>Installed via a .latest ref, but Hub has a newer version available.</summary>
    UpdateAvailable,
    /// <summary>Not present — will be queued for download.</summary>
    Missing,
    /// <summary>
    /// Scene asks for an exact version (e.g. .3) but Hub only serves a different version (e.g. .2).
    /// Auto-download won't satisfy the reference; listed so the user knows to resolve it manually.
    /// </summary>
    VersionMismatch,
    /// <summary>Queued to download but not started yet.</summary>
    Queued,
    /// <summary>Downloading right now.</summary>
    Downloading,
    /// <summary>Just downloaded this run.</summary>
    Downloaded,
    /// <summary>Hub couldn't resolve this package (not hub-hosted, removed, or paid-only).</summary>
    NotOnHub,
    /// <summary>Network / server error while downloading.</summary>
    Error
}

public sealed class HubDependency : INotifyPropertyChanged
{
    /// <summary>E.g. "AcidBubbles.Timeline.287" or "AcidBubbles.Voxta.latest".</summary>
    public string Name { get; init; } = "";

    /// <summary>License reported in meta.json (CC BY, PC, FC, etc.).</summary>
    public string? License { get; init; }

    /// <summary>Filename resolved by Hub on query (e.g. AcidBubbles.Voxta.86.var for a .latest ref).</summary>
    public string? ResolvedFilename { get; set; }

    /// <summary>Direct download URL returned by Hub.</summary>
    public string? DownloadUrl { get; set; }

    private string? _installedPath;
    /// <summary>Absolute path on disk if found in AddonPackages.</summary>
    public string? InstalledPath
    {
        get => _installedPath;
        set
        {
            if (_installedPath == value) return;
            _installedPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasLocalFile));
        }
    }

    /// <summary>True when a matching .var exists on disk — either the scene's version, or a Hub substitute we force-installed.</summary>
    public bool HasLocalFile => !string.IsNullOrEmpty(InstalledPath);

    /// <summary>Version number of the local .var satisfying this ref (null if not installed).</summary>
    public int? InstalledVersion { get; set; }

    /// <summary>Newest version Hub reports for this package (null if Hub wasn't queried or has nothing).</summary>
    public int? HubLatestVersion { get; set; }

    /// <summary>Version number the scene explicitly asks for (null for .latest refs or unparseable names).</summary>
    public int? RequestedVersion { get; set; }

    private HubDependencyStatus _status;
    public HubDependencyStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(StatusTooltip));
        }
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (_errorMessage == value) return;
            _errorMessage = value;
            OnPropertyChanged();
        }
    }

    public string StatusLabel => Status switch
    {
        HubDependencyStatus.Installed        => "Installed",
        HubDependencyStatus.UpdateAvailable  => "Update available",
        HubDependencyStatus.Missing          => "Missing",
        HubDependencyStatus.VersionMismatch  => "Wrong version",
        HubDependencyStatus.Queued           => "Queued",
        HubDependencyStatus.Downloading      => "Downloading",
        HubDependencyStatus.Downloaded       => "Downloaded",
        HubDependencyStatus.NotOnHub         => "Not on Hub",
        HubDependencyStatus.Error            => "Error",
        _ => Status.ToString()
    };

    /// <summary>Context-specific tooltip surfaced on the status cell.</summary>
    public string? StatusTooltip => Status switch
    {
        HubDependencyStatus.UpdateAvailable when InstalledVersion.HasValue && HubLatestVersion.HasValue =>
            $"Installed: v{InstalledVersion} · Hub latest: v{HubLatestVersion}. Scene's .latest ref still resolves via the installed version, but Hub has a newer one.",
        HubDependencyStatus.VersionMismatch when RequestedVersion.HasValue && HubLatestVersion.HasValue =>
            $"Scene asks for v{RequestedVersion} exactly, but Hub only serves v{HubLatestVersion}. Auto-downloading won't satisfy this reference — grab v{RequestedVersion} from the creator's Patreon, or change the ref to .latest in the Plugin references tab.",
        HubDependencyStatus.NotOnHub =>
            "Hub has no download URL for this package — it's paid-only, has been removed, or was never uploaded to Hub. Grab it manually from the creator's Patreon/etc.",
        HubDependencyStatus.Error => ErrorMessage,
        _ => null
    };

    public string LicenseLabel => string.IsNullOrWhiteSpace(License) ? "" : License;

    /// <summary>"Author.Package" portion of the ref, without the version suffix.</summary>
    public string PackageBaseName => Services.AddonPackagesIndex.SplitNameParts(Name).PackageBase;

    /// <summary>"latest" / "3" / "min.5" — the version the scene is requesting.</summary>
    public string RequestedVersionLabel => Services.AddonPackagesIndex.SplitNameParts(Name).VersionLabel;

    /// <summary>Version number Hub serves for this package, as a display string. "—" when unknown.</summary>
    public string HubLatestVersionLabel => HubLatestVersion?.ToString() ?? "—";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
