using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VamPackageUpdater.Models;

public enum HubDependencyStatus
{
    /// <summary>Already present in AddonPackages.</summary>
    Installed,
    /// <summary>Not present — will be queued for download.</summary>
    Missing,
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

    /// <summary>Absolute path on disk if found in AddonPackages.</summary>
    public string? InstalledPath { get; set; }

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
        HubDependencyStatus.Installed   => "Installed",
        HubDependencyStatus.Missing     => "Missing",
        HubDependencyStatus.Queued      => "Queued",
        HubDependencyStatus.Downloading => "Downloading",
        HubDependencyStatus.Downloaded  => "Downloaded",
        HubDependencyStatus.NotOnHub    => "Not on Hub",
        HubDependencyStatus.Error       => "Error",
        _ => Status.ToString()
    };

    public string LicenseLabel => string.IsNullOrWhiteSpace(License) ? "" : License;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
