using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using VamPackageUpdater.Models;
using VamPackageUpdater.Services;

namespace VamPackageUpdater;

public partial class MainWindow : Window
{
    private static readonly Brush DefaultBrush = Freeze(0xC0, 0xC0, 0xC0);
    private static readonly Brush SubduedBrush = Freeze(0x80, 0x80, 0x80);
    private static readonly Brush HeaderBrush  = Freeze(0xE0, 0xE0, 0xE0);
    private static readonly Brush PluginBrush  = Freeze(0x4E, 0xC9, 0xB0);
    private static readonly Brush VersionBrush = Freeze(0xB5, 0xCE, 0xA8);
    private static readonly Brush NumberBrush  = Freeze(0xCE, 0x91, 0x78);
    private static readonly Brush PathBrush    = Freeze(0x9C, 0xDC, 0xFE);
    private static readonly Brush LineBrush    = Freeze(0xDC, 0xDC, 0xAA);
    private static readonly Brush ErrorBrush   = Freeze(0xF4, 0x87, 0x71);
    private static readonly Brush SuccessBrush = Freeze(0x6A, 0x99, 0x55);

    private readonly ObservableCollection<PluginReferenceGroup> _plugins = new();
    private readonly ObservableCollection<VoxtaResourceRef> _voxtaResources = new();
    private readonly ObservableCollection<HubDependency> _hubDeps = new();
    private readonly PluginReferenceScanner _scanner = new();
    private readonly VoxtaResourceScanner _voxtaScanner = new();
    private readonly VoxtaPngInspector _pngInspector = new();
    private readonly MetaJsonDependencyScanner _depScanner = new();
    private readonly MetaReader _metaReader = new();
    private Paragraph _logParagraph = null!;
    private PluginCategory? _activeFilter;
    private ICollectionView _pluginsView = null!;

    public MainWindow()
    {
        InitializeComponent();

        foreach (var key in Licenses.All.Keys)
            LicenseCombo.Items.Add(key);
        LicenseCombo.SelectedIndex = 0;

        _pluginsView = CollectionViewSource.GetDefaultView(_plugins);
        _pluginsView.Filter = PluginFilter;
        PluginsGrid.ItemsSource = _pluginsView;

        VoxtaGrid.ItemsSource = _voxtaResources;
        HubDepsGrid.ItemsSource = _hubDeps;

        InitLogDocument();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TryEnableDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a .var package",
            Filter = "Virt-a-Mate Packages (*.var)|*.var|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            FilePathBox.Text = dialog.FileName;
            LogParts(("Selected file: ", SubduedBrush), (Path.GetFileName(dialog.FileName), PathBrush));
            await ScanAsync();
        }
    }

    private async void RescanButton_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private void SetRowLatestButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is PluginReferenceGroup g)
            g.NewVersion = "latest";
    }

    private void RefsHyperlink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Hyperlink link) return;
        if (link.DataContext is not PluginReferenceGroup group) return;
        if (group.OccurrenceCount == 0) return;

        var dlg = new ReferenceOccurrencesDialog(group) { Owner = this };
        dlg.ShowDialog();
    }

    private void SetAllLatestButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in VisiblePlugins())
            p.NewVersion = "latest";
    }

    private void ClearAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in VisiblePlugins())
            p.NewVersion = "";
    }

    private IEnumerable<PluginReferenceGroup> VisiblePlugins() =>
        _pluginsView.Cast<PluginReferenceGroup>();

    private bool PluginFilter(object obj)
    {
        if (obj is not PluginReferenceGroup g) return false;
        return _activeFilter is null || g.PrimaryCategory == _activeFilter;
    }

    private void FilterTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;

        // Uncheck the others (radio-group behavior)
        foreach (var tab in FilterTabStrip.Children.OfType<ToggleButton>())
            tab.IsChecked = tab == clicked;

        _activeFilter = clicked.Tag is string s && Enum.TryParse<PluginCategory>(s, out var cat)
            ? cat
            : (PluginCategory?)null;

        _pluginsView.Refresh();
    }

    private void UpdateTabCounts()
    {
        foreach (var tab in FilterTabStrip.Children.OfType<ToggleButton>())
        {
            var baseLabel = tab.Tag is string s && Enum.TryParse<PluginCategory>(s, out var cat)
                ? cat.ToLabel()
                : "All";
            var count = tab.Tag is string s2 && Enum.TryParse<PluginCategory>(s2, out var cat2)
                ? _plugins.Count(p => p.PrimaryCategory == cat2)
                : _plugins.Count;
            tab.Content = $"{baseLabel} ({count})";
        }
    }

    private void VersionTextBox_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is TextBox tb && !tb.IsKeyboardFocusWithin)
        {
            tb.Focus();
            e.Handled = true;
        }
    }

    private void VersionTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
            tb.SelectAll();
    }

    private void ShowMetaPanel(PackageMeta meta)
    {
        MetaCreator.Text = string.IsNullOrWhiteSpace(meta.CreatorName) ? "(unknown)" : meta.CreatorName;
        MetaLicense.Text = string.IsNullOrWhiteSpace(meta.LicenseType) ? "(not specified)" : meta.LicenseType;
        MetaSize.Text = FormatSize(meta.FileSize);
        MetaFileCount.Text = meta.FileCount.ToString("N0");
        var desc = string.IsNullOrWhiteSpace(meta.Description) ? "(no description)" : meta.Description!.Trim();
        MetaDescription.Text = desc;
        MetaPanel.Visibility = Visibility.Visible;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    private CollisionChoice PromptCollision(string existingPath)
    {
        return Dispatcher.Invoke(() =>
        {
            var outDir = Path.GetDirectoryName(existingPath)!;
            var sourceStem = Path.GetFileNameWithoutExtension(FilePathBox.Text);
            var nextFree = Path.GetFileName(PackageUpdater.FindNextFreePath(outDir, sourceStem));
            var dlg = new CollisionDialog(existingPath, nextFree) { Owner = this };
            dlg.ShowDialog();
            return dlg.Choice;
        });
    }

    private async Task ScanAsync()
    {
        if (string.IsNullOrEmpty(FilePathBox.Text)) return;

        _plugins.Clear();
        _voxtaResources.Clear();
        _hubDeps.Clear();
        RescanButton.IsEnabled = false;
        UpdateButton.IsEnabled = false;
        SetAllLatestButton.IsEnabled = false;
        ClearAllButton.IsEnabled = false;
        ClearAllAttachmentsButton.IsEnabled = false;
        DownloadAllMissingButton.IsEnabled = false;
        LogParts(("Scanning ", SubduedBrush), ($"'{Path.GetFileName(FilePathBox.Text)}'", PathBrush), ("...", SubduedBrush));

        try
        {
            var metaTask = _metaReader.ReadAsync(FilePathBox.Text);
            var voxtaTask = _voxtaScanner.ScanAsync(FilePathBox.Text);
            var depsTask = _depScanner.ScanAsync(FilePathBox.Text);
            var results = await _scanner.ScanAsync(FilePathBox.Text);
            var meta = await metaTask;
            var voxtaResults = await voxtaTask;
            var depResults = await depsTask;
            ShowMetaPanel(meta);

            foreach (var g in results)
                _plugins.Add(g);
            foreach (var r in voxtaResults)
                _voxtaResources.Add(r);

            // Check which deps are already in the user's AddonPackages folder.
            var addonPackagesFolder = AddonPackagesIndex.InferAddonPackagesFolder(FilePathBox.Text);
            if (!string.IsNullOrEmpty(addonPackagesFolder))
            {
                var index = await Task.Run(() => new AddonPackagesIndex(addonPackagesFolder));
                index.AnnotateStatus(depResults);
            }

            // Ask the Hub upfront about availability for each dep:
            //   - Non-installed → classify Missing vs NotOnHub based on Hub response
            //   - .latest + installed → flag UpdateAvailable if Hub's latest version > installed version
            await AnnotateWithHubAsync(depResults);

            foreach (var d in depResults)
                _hubDeps.Add(d);

            LogPluginScanResults();
            LogVoxtaScanResults();
            LogHubDepsScanResults();

            var hasPlugins = _plugins.Count > 0;
            var hasVoxta = _voxtaResources.Count > 0;
            var hasActionableDeps = _hubDeps.Any(d =>
                d.Status == HubDependencyStatus.Missing ||
                d.Status == HubDependencyStatus.UpdateAvailable);

            UpdateButton.IsEnabled = hasPlugins || hasVoxta;
            SetAllLatestButton.IsEnabled = hasPlugins;
            ClearAllButton.IsEnabled = hasPlugins;
            ClearAllAttachmentsButton.IsEnabled = hasVoxta;
            DownloadAllMissingButton.IsEnabled = hasActionableDeps;
            RescanButton.IsEnabled = true;
            UpdateTabCounts();
            UpdateSectionTabLabels();

            // Auto-jump priority: Missing Voxta resources > actionable hub deps.
            if (_voxtaResources.Any(r => r.Status == VoxtaResourceStatus.Missing))
                SwitchSection("Voxta");
            else if (hasActionableDeps)
                SwitchSection("HubDeps");
        }
        catch (Exception ex)
        {
            Log($"Scan failed: {ex.Message}", ErrorBrush);
            UpdateButton.IsEnabled = false;
            RescanButton.IsEnabled = !string.IsNullOrEmpty(FilePathBox.Text);
        }
    }

    private void LogPluginScanResults()
    {
        if (_plugins.Count == 0)
        {
            Log("No plugin references found in the package.", SubduedBrush);
            return;
        }

        var totalRefs = _plugins.Sum(p => p.OccurrenceCount);
        var totalFiles = _plugins.SelectMany(p => p.Files).Distinct(StringComparer.Ordinal).Count();

        LogParts(
            ("Found ", HeaderBrush),
            (_plugins.Count.ToString(), NumberBrush),
            (" unique plugin(s), ", HeaderBrush),
            (totalRefs.ToString(), NumberBrush),
            (" reference(s) across ", HeaderBrush),
            (totalFiles.ToString(), NumberBrush),
            (" file(s):", HeaderBrush));
        Log("");

        foreach (var p in _plugins)
        {
            LogParts(
                ("  ", DefaultBrush),
                (p.PluginId, PluginBrush),
                ("  @ ", SubduedBrush),
                (p.CurrentVersionsDisplay, VersionBrush),
                ("    (", SubduedBrush),
                (p.OccurrenceCount.ToString(), NumberBrush),
                (" ref(s) in ", SubduedBrush),
                (p.FileCount.ToString(), NumberBrush),
                (" file(s))", SubduedBrush));

            foreach (var fc in p.FileLines.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var label = fc.Value.Count == 1 ? "line " : "lines ";
                LogParts(
                    ("      ", DefaultBrush),
                    (fc.Key, PathBrush),
                    ("  (", SubduedBrush),
                    (label, SubduedBrush),
                    (string.Join(", ", fc.Value), LineBrush),
                    (")", SubduedBrush));
            }
        }

        Log("");
        Log("Edit the New Version column to queue updates, then click Update Package.", SubduedBrush);
    }

    private void LogVoxtaScanResults()
    {
        if (_voxtaResources.Count == 0) return;

        var missing = _voxtaResources.Count(r => r.Status == VoxtaResourceStatus.Missing);
        var bundled = _voxtaResources.Count(r => r.Status == VoxtaResourceStatus.Bundled);
        var orphans = _voxtaResources.Count(r => r.Status == VoxtaResourceStatus.Orphan);

        Log("");
        LogParts(
            ("Voxta resources: ", HeaderBrush),
            (bundled.ToString(), NumberBrush),
            (" bundled, ", HeaderBrush),
            (missing.ToString(), missing > 0 ? ErrorBrush : NumberBrush),
            (" missing", HeaderBrush),
            (orphans > 0 ? $", {orphans} orphan" : "", SubduedBrush),
            (".", HeaderBrush));

        foreach (var r in _voxtaResources)
        {
            var statusBrush = r.Status switch
            {
                VoxtaResourceStatus.Bundled => SuccessBrush,
                VoxtaResourceStatus.Missing => ErrorBrush,
                VoxtaResourceStatus.Attached => PathBrush,
                VoxtaResourceStatus.Orphan => SubduedBrush,
                _ => DefaultBrush
            };
            LogParts(
                ("  ", DefaultBrush),
                ($"[{r.StatusLabel}] ", statusBrush),
                ($"{r.KindLabel} ", PluginBrush),
                (r.NameOrId, VersionBrush),
                (string.IsNullOrWhiteSpace(r.DisplayName) ? "" : $"  ({r.Id})", SubduedBrush));
        }

        if (missing > 0)
        {
            Log("");
            Log("Open the 'Voxta resources' tab and attach PNGs for the missing entries.", SubduedBrush);
        }
    }

    private async Task AnnotateWithHubAsync(IList<HubDependency> deps)
    {
        // Deps to query:
        //   - anything Missing (classify: Missing-downloadable vs NotOnHub)
        //   - .latest refs that are Installed (check for newer version on Hub)
        var toQuery = deps
            .Where(d =>
                d.Status == HubDependencyStatus.Missing ||
                (d.Status == HubDependencyStatus.Installed && AddonPackagesIndex.IsLatestRef(d.Name)))
            .ToList();

        if (toQuery.Count == 0) return;

        Dictionary<string, HubPackageInfo> lookup;
        try
        {
            using var hub = new HubClient();
            lookup = await hub.FindPackagesAsync(toQuery.Select(d => d.Name));
        }
        catch (Exception ex)
        {
            Log($"Warning: Hub availability check failed ({ex.Message}). 'Not on Hub' / 'Update avail' statuses are unknown until you can reach the Hub.", ErrorBrush);
            return;
        }

        foreach (var dep in toQuery)
        {
            // Record what the scene asks for, so the UI can show both sides on mismatch.
            dep.RequestedVersion = AddonPackagesIndex.ParseRequestedVersion(dep.Name);

            if (!lookup.TryGetValue(dep.Name, out var info) || !info.HasUsableDownloadUrl)
            {
                // Hub won't serve this. Only mark NotOnHub if not locally installed —
                // an installed package we can't verify on Hub is still fine to use.
                if (dep.Status == HubDependencyStatus.Missing)
                    dep.Status = HubDependencyStatus.NotOnHub;
                continue;
            }

            // Cache Hub's resolved download info so DownloadAll doesn't re-query.
            dep.ResolvedFilename = info.Filename;
            dep.DownloadUrl = info.DownloadUrl;

            var hubVersion = AddonPackagesIndex.ParseVersionFromFilename(info.Filename);
            dep.HubLatestVersion = hubVersion;

            // Exact-version ref that Hub substitutes with a different version (e.g. scene
            // wants SPQR.Footsteps.3 but Hub only has .2). Silently downloading .2 wouldn't
            // satisfy VaM's .3 ref — flag it so the user can resolve manually instead.
            if (dep.Status == HubDependencyStatus.Missing &&
                dep.RequestedVersion is int requestedV &&
                hubVersion is int hubSubV &&
                hubSubV != requestedV)
            {
                dep.Status = HubDependencyStatus.VersionMismatch;
                continue;
            }

            if (dep.Status == HubDependencyStatus.Installed &&
                AddonPackagesIndex.IsLatestRef(dep.Name) &&
                dep.InstalledVersion is int installedV &&
                hubVersion is int hubV &&
                hubV > installedV)
            {
                dep.Status = HubDependencyStatus.UpdateAvailable;
            }
        }
    }

    private void LogHubDepsScanResults()
    {
        if (_hubDeps.Count == 0) return;

        var installed = _hubDeps.Count(d => d.Status == HubDependencyStatus.Installed);
        var missing = _hubDeps.Count(d => d.Status == HubDependencyStatus.Missing);
        var notOnHub = _hubDeps.Count(d => d.Status == HubDependencyStatus.NotOnHub);
        var updatable = _hubDeps.Count(d => d.Status == HubDependencyStatus.UpdateAvailable);
        var mismatched = _hubDeps.Count(d => d.Status == HubDependencyStatus.VersionMismatch);
        var total = _hubDeps.Count;

        Log("");
        LogParts(
            ("Hub dependencies: ", HeaderBrush),
            (installed.ToString(), NumberBrush),
            (" installed, ", HeaderBrush),
            (missing.ToString(), missing > 0 ? ErrorBrush : NumberBrush),
            (" missing, ", HeaderBrush),
            (notOnHub.ToString(), notOnHub > 0 ? ErrorBrush : NumberBrush),
            (" not on Hub, ", HeaderBrush),
            (mismatched.ToString(), mismatched > 0 ? LineBrush : NumberBrush),
            (" wrong version, ", HeaderBrush),
            (updatable.ToString(), updatable > 0 ? LineBrush : NumberBrush),
            (" update available (of ", HeaderBrush),
            (total.ToString(), NumberBrush),
            (" total).", HeaderBrush));

        if (missing > 0)
        {
            Log("");
            Log("Open the 'Download deps' tab and click 'Download all' to fetch them from hub.virtamate.com.", SubduedBrush);
        }
        if (notOnHub > 0)
        {
            Log("Some packages aren't on the Hub and must be fetched manually from their creator (Patreon, etc.).", SubduedBrush);
        }
        if (mismatched > 0)
        {
            foreach (var d in _hubDeps.Where(d => d.Status == HubDependencyStatus.VersionMismatch))
            {
                LogParts(
                    ("  Wrong version: ", ErrorBrush),
                    (d.Name, PluginBrush),
                    (" — Hub only has v", SubduedBrush),
                    (d.HubLatestVersion?.ToString() ?? "?", NumberBrush));
            }
            Log("Scene asks for specific versions that Hub doesn't serve. Either grab the exact version from the creator's Patreon, or use the Plugin references tab to rewrite those refs to .latest.", SubduedBrush);
        }
    }

    private async void DownloadAllMissingButton_Click(object sender, RoutedEventArgs e)
    {
        // "Download all" handles both Missing (never-installed) and UpdateAvailable
        // (installed but .latest resolves to a newer version on Hub). Both get fetched
        // into AddonPackages; VaM's .latest resolver picks the newest version present.
        var missing = _hubDeps
            .Where(d => d.Status == HubDependencyStatus.Missing || d.Status == HubDependencyStatus.UpdateAvailable)
            .ToList();
        if (missing.Count == 0) return;

        var updatingCount = missing.Count(d => d.Status == HubDependencyStatus.UpdateAvailable);
        var missingCount = missing.Count - updatingCount;

        var addonPackagesFolder = AddonPackagesIndex.InferAddonPackagesFolder(FilePathBox.Text);
        if (string.IsNullOrEmpty(addonPackagesFolder) || !Directory.Exists(addonPackagesFolder))
        {
            Log("Could not determine the AddonPackages folder from the loaded .var path.", ErrorBrush);
            return;
        }

        DownloadAllMissingButton.IsEnabled = false;
        RescanButton.IsEnabled = false;
        UpdateButton.IsEnabled = false;

        try
        {
            foreach (var dep in missing)
                dep.Status = HubDependencyStatus.Queued;

            if (updatingCount > 0 && missingCount > 0)
                LogParts(
                    ("Querying Hub for ", HeaderBrush),
                    (missingCount.ToString(), NumberBrush),
                    (" missing + ", HeaderBrush),
                    (updatingCount.ToString(), NumberBrush),
                    (" outdated package(s)...", HeaderBrush));
            else if (updatingCount > 0)
                LogParts(
                    ("Querying Hub for ", HeaderBrush),
                    (updatingCount.ToString(), NumberBrush),
                    (" outdated package(s)...", HeaderBrush));
            else
                LogParts(
                    ("Querying Hub for ", HeaderBrush),
                    (missingCount.ToString(), NumberBrush),
                    (" missing package(s)...", HeaderBrush));

            using var hub = new HubClient();
            Dictionary<string, HubPackageInfo> lookup;
            try
            {
                lookup = await hub.FindPackagesAsync(missing.Select(d => d.Name));
            }
            catch (Exception ex)
            {
                Log($"Hub query failed: {ex.Message}", ErrorBrush);
                foreach (var dep in missing)
                {
                    dep.Status = HubDependencyStatus.Error;
                    dep.ErrorMessage = "Hub query failed";
                }
                return;
            }

            // Some packages get a "...?file=" truncated URL back and need a second query —
            // VamToolbox's workaround.
            var needsRetry = missing
                .Where(d =>
                    lookup.TryGetValue(d.Name, out var info) &&
                    !string.IsNullOrEmpty(info.DownloadUrl) &&
                    info.DownloadUrl!.EndsWith("?file=", StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Name)
                .ToList();
            if (needsRetry.Count > 0)
            {
                try
                {
                    var retry = await hub.FindPackagesAsync(needsRetry);
                    foreach (var kv in retry)
                        lookup[kv.Key] = kv.Value;
                }
                catch { /* best-effort retry */ }
            }

            var downloaded = 0;
            var skipped = 0;
            foreach (var dep in missing)
            {
                if (!lookup.TryGetValue(dep.Name, out var info) || !info.HasUsableDownloadUrl)
                {
                    dep.Status = HubDependencyStatus.NotOnHub;
                    dep.ErrorMessage = "Hub returned no download URL (package is paid-only, removed, or never uploaded to Hub).";
                    LogParts(
                        ("  - ", DefaultBrush),
                        ("[Not on Hub] ", ErrorBrush),
                        (dep.Name, PluginBrush));
                    skipped++;
                    continue;
                }

                dep.ResolvedFilename = info.Filename;
                dep.DownloadUrl = info.DownloadUrl;
                var ok = await DownloadSingleDepAsync(dep, hub, addonPackagesFolder);
                if (ok) downloaded++; else skipped++;
            }

            Log("");
            LogParts(
                ("Done. Downloaded ", HeaderBrush),
                (downloaded.ToString(), NumberBrush),
                (skipped > 0 ? $", skipped {skipped} (paid/removed/error)." : ".", HeaderBrush));

            UpdateSectionTabLabels();
        }
        finally
        {
            DownloadAllMissingButton.IsEnabled = _hubDeps.Any(d =>
                d.Status == HubDependencyStatus.Missing ||
                d.Status == HubDependencyStatus.UpdateAvailable);
            RescanButton.IsEnabled = true;
            var hasPlugins = _plugins.Count > 0;
            var hasVoxta = _voxtaResources.Count > 0;
            UpdateButton.IsEnabled = hasPlugins || hasVoxta;
        }
    }

    /// <summary>
    /// Download a single dep using its already-resolved DownloadUrl + ResolvedFilename.
    /// Handles the existing-file short-circuit, logs transitions, updates Status/ErrorMessage.
    /// Returns true if the file ended up present in AddonPackages (fresh download or already there).
    /// </summary>
    private async Task<bool> DownloadSingleDepAsync(HubDependency dep, HubClient hub, string addonPackagesFolder)
    {
        if (string.IsNullOrEmpty(dep.DownloadUrl) || string.IsNullOrEmpty(dep.ResolvedFilename))
        {
            dep.Status = HubDependencyStatus.Error;
            dep.ErrorMessage = "No resolved Hub download info — run Rescan first.";
            LogParts(
                ("  - ", DefaultBrush),
                ("[No Hub info] ", ErrorBrush),
                (dep.Name, PluginBrush));
            return false;
        }

        var destination = Path.Combine(addonPackagesFolder, dep.ResolvedFilename);
        if (File.Exists(destination))
        {
            dep.InstalledPath = destination;
            dep.Status = HubDependencyStatus.Installed;
            LogParts(
                ("  - ", DefaultBrush),
                ("[Already present] ", SuccessBrush),
                (dep.ResolvedFilename, PluginBrush));
            return true;
        }

        dep.Status = HubDependencyStatus.Downloading;
        LogParts(
            ("  - ", DefaultBrush),
            ("Downloading ", HeaderBrush),
            (dep.ResolvedFilename, PathBrush),
            ("...", SubduedBrush));

        HubDownloadResult result;
        try
        {
            result = await hub.DownloadAsync(dep.DownloadUrl, destination);
        }
        catch (Exception ex)
        {
            result = HubDownloadResult.Fail(ex.Message);
        }

        if (result.Success)
        {
            dep.InstalledPath = destination;
            dep.Status = HubDependencyStatus.Downloaded;
            LogParts(
                ("     ", DefaultBrush),
                ($"-> {FormatBytes(result.Bytes)}", SubduedBrush));
            return true;
        }

        dep.Status = HubDependencyStatus.Error;
        dep.ErrorMessage = result.Error;
        LogParts(
            ("     ", DefaultBrush),
            ("-> Error: ", ErrorBrush),
            (result.Error ?? "unknown", ErrorBrush));
        return false;
    }

    private async void DepActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not HubDependency dep) return;

        // Branch by status. Installed / Downloaded → destructive Delete. Missing /
        // UpdateAvailable / Error → install via Hub. VersionMismatch → confirm then
        // install Hub's substitute version. Other states are presented as disabled
        // buttons in the XAML, so they shouldn't fire the click event.
        if (dep.Status == HubDependencyStatus.Installed || dep.Status == HubDependencyStatus.Downloaded)
        {
            DeleteInstalledDep(dep);
            return;
        }

        if (dep.Status == HubDependencyStatus.VersionMismatch)
        {
            // If the mismatched file is already force-installed on disk, the action
            // is Delete — remove the substitute .var. Otherwise the action is the
            // force-install confirm dialog below.
            if (dep.HasLocalFile)
            {
                DeleteInstalledDep(dep);
                return;
            }

            var requested = dep.RequestedVersion?.ToString() ?? "?";
            var hubHas = dep.HubLatestVersion?.ToString() ?? "?";
            var confirm = MessageBox.Show(
                this,
                $"Scene asks for {dep.PackageBaseName} v{requested} exactly, but Hub only serves v{hubHas}.\n\n" +
                $"Install v{hubHas} anyway?\n\n" +
                $"Note: this won't satisfy the scene's v{requested} reference — VaM will still complain about the missing exact version. " +
                $"To actually fix the scene, either grab v{requested} manually from the creator's Patreon, or rewrite the ref to .latest on the Plugin references tab.",
                "Install anyway?",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;

            await ForceInstallMismatchedDepAsync(dep);
            return;
        }

        if (dep.Status != HubDependencyStatus.Missing &&
            dep.Status != HubDependencyStatus.UpdateAvailable &&
            dep.Status != HubDependencyStatus.Error)
            return;

        var addonPackagesFolder = AddonPackagesIndex.InferAddonPackagesFolder(FilePathBox.Text);
        if (string.IsNullOrEmpty(addonPackagesFolder) || !Directory.Exists(addonPackagesFolder))
        {
            Log("Could not determine the AddonPackages folder from the loaded .var path.", ErrorBrush);
            return;
        }

        DownloadAllMissingButton.IsEnabled = false;
        RescanButton.IsEnabled = false;
        UpdateButton.IsEnabled = false;

        try
        {
            using var hub = new HubClient();

            // If scan already cached DownloadUrl we reuse it. Otherwise (user clicked Retry
            // on an Error row, or cache got cleared somehow) query Hub for just this one.
            if (string.IsNullOrEmpty(dep.DownloadUrl) || string.IsNullOrEmpty(dep.ResolvedFilename))
            {
                try
                {
                    var lookup = await hub.FindPackagesAsync(new[] { dep.Name });
                    if (lookup.TryGetValue(dep.Name, out var info) && info.HasUsableDownloadUrl)
                    {
                        dep.ResolvedFilename = info.Filename;
                        dep.DownloadUrl = info.DownloadUrl;
                    }
                    else
                    {
                        dep.Status = HubDependencyStatus.NotOnHub;
                        dep.ErrorMessage = "Hub returned no download URL (paid-only/removed/never uploaded).";
                        LogParts(
                            ("  - ", DefaultBrush),
                            ("[Not on Hub] ", ErrorBrush),
                            (dep.Name, PluginBrush));
                        return;
                    }
                }
                catch (Exception ex)
                {
                    dep.Status = HubDependencyStatus.Error;
                    dep.ErrorMessage = ex.Message;
                    Log($"Hub query failed for {dep.Name}: {ex.Message}", ErrorBrush);
                    return;
                }
            }

            LogParts(
                ("Installing ", HeaderBrush),
                (dep.Name, PluginBrush),
                ("...", SubduedBrush));

            await DownloadSingleDepAsync(dep, hub, addonPackagesFolder);
            UpdateSectionTabLabels();
        }
        finally
        {
            DownloadAllMissingButton.IsEnabled = _hubDeps.Any(d =>
                d.Status == HubDependencyStatus.Missing ||
                d.Status == HubDependencyStatus.UpdateAvailable);
            RescanButton.IsEnabled = true;
            var hasPlugins = _plugins.Count > 0;
            var hasVoxta = _voxtaResources.Count > 0;
            UpdateButton.IsEnabled = hasPlugins || hasVoxta;
        }
    }

    /// <summary>
    /// Force-install a VersionMismatch dep: download whatever version Hub is serving
    /// into AddonPackages, but KEEP the status as VersionMismatch afterward so the UI
    /// stays honest about the scene's exact-version ref still being unsatisfied.
    /// </summary>
    private async Task ForceInstallMismatchedDepAsync(HubDependency dep)
    {
        var addonPackagesFolder = AddonPackagesIndex.InferAddonPackagesFolder(FilePathBox.Text);
        if (string.IsNullOrEmpty(addonPackagesFolder) || !Directory.Exists(addonPackagesFolder))
        {
            Log("Could not determine the AddonPackages folder from the loaded .var path.", ErrorBrush);
            return;
        }

        DownloadAllMissingButton.IsEnabled = false;
        RescanButton.IsEnabled = false;
        UpdateButton.IsEnabled = false;

        try
        {
            using var hub = new HubClient();
            LogParts(
                ("Force-installing ", HeaderBrush),
                (dep.PackageBaseName, PluginBrush),
                (" v", SubduedBrush),
                (dep.HubLatestVersion?.ToString() ?? "?", NumberBrush),
                ($" (scene asks for v{dep.RequestedVersion?.ToString() ?? "?"})...", SubduedBrush));

            var ok = await DownloadSingleDepAsync(dep, hub, addonPackagesFolder);

            // Even on successful download, keep flagging as VersionMismatch so the user
            // remembers the scene's exact-version ref still isn't satisfied.
            if (ok)
            {
                dep.InstalledVersion = dep.HubLatestVersion;
                dep.Status = HubDependencyStatus.VersionMismatch;
                LogParts(
                    ("     ", DefaultBrush),
                    ("(scene still references v", SubduedBrush),
                    (dep.RequestedVersion?.ToString() ?? "?", NumberBrush),
                    (" — update the ref to .latest or grab v", SubduedBrush),
                    (dep.RequestedVersion?.ToString() ?? "?", NumberBrush),
                    (" manually to fully resolve)", SubduedBrush));
            }

            UpdateSectionTabLabels();
        }
        finally
        {
            DownloadAllMissingButton.IsEnabled = _hubDeps.Any(d =>
                d.Status == HubDependencyStatus.Missing ||
                d.Status == HubDependencyStatus.UpdateAvailable);
            RescanButton.IsEnabled = true;
            var hasPlugins = _plugins.Count > 0;
            var hasVoxta = _voxtaResources.Count > 0;
            UpdateButton.IsEnabled = hasPlugins || hasVoxta;
        }
    }

    private void DeleteInstalledDep(HubDependency dep)
    {
        var path = dep.InstalledPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            // Already gone somehow — just sync the status.
            dep.InstalledPath = null;
            dep.InstalledVersion = null;
            dep.Status = HubDependencyStatus.Missing;
            UpdateSectionTabLabels();
            return;
        }

        var filename = Path.GetFileName(path);
        var sizeMb = new FileInfo(path).Length / (1024.0 * 1024);
        var confirm = MessageBox.Show(
            this,
            $"Delete this .var from your AddonPackages folder?\n\n" +
            $"File:   {filename}\n" +
            $"Size:   {sizeMb:F1} MB\n\n" +
            $"This removes the file from disk. You can re-download it via 'Install' after.",
            "Confirm delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            File.Delete(path);
            dep.InstalledPath = null;
            dep.InstalledVersion = null;

            // If the dep was a VersionMismatch (scene asks for vX, Hub only has vY)
            // that we force-installed, restore that status instead of flipping to Missing —
            // the scene's exact-version ref is still genuinely unsatisfiable by Hub.
            var isUnresolvableMismatch =
                dep.RequestedVersion.HasValue &&
                dep.HubLatestVersion.HasValue &&
                dep.RequestedVersion != dep.HubLatestVersion;

            dep.Status = isUnresolvableMismatch
                ? HubDependencyStatus.VersionMismatch
                : HubDependencyStatus.Missing;

            LogParts(
                ("Deleted ", HeaderBrush),
                (filename, PathBrush),
                (" from AddonPackages.", HeaderBrush));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Could not delete '{filename}':\n\n{ex.Message}",
                "Delete failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Log($"Delete failed for {filename}: {ex.Message}", ErrorBrush);
        }

        UpdateSectionTabLabels();

        // Re-enable Download All if we just created a new missing slot.
        DownloadAllMissingButton.IsEnabled = _hubDeps.Any(d =>
            d.Status == HubDependencyStatus.Missing ||
            d.Status == HubDependencyStatus.UpdateAvailable);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    private void SectionTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb) return;
        var section = tb.Tag as string ?? "Plugins";
        SwitchSection(section);
    }

    private void SwitchSection(string section)
    {
        SectionTabPlugins.IsChecked = section == "Plugins";
        SectionTabVoxta.IsChecked = section == "Voxta";
        SectionTabHubDeps.IsChecked = section == "HubDeps";

        PluginsSection.Visibility = section == "Plugins" ? Visibility.Visible : Visibility.Collapsed;
        VoxtaSection.Visibility = section == "Voxta" ? Visibility.Visible : Visibility.Collapsed;
        HubDepsSection.Visibility = section == "HubDeps" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSectionTabLabels()
    {
        var pluginsCount = _plugins.Count;
        var missing = _voxtaResources.Count(r => r.Status == VoxtaResourceStatus.Missing);
        var voxtaTotal = _voxtaResources.Count;
        var depsMissing = _hubDeps.Count(d => d.Status == HubDependencyStatus.Missing);
        var depsTotal = _hubDeps.Count;

        SectionTabPlugins.Content = pluginsCount > 0 ? $"Plugin references ({pluginsCount})" : "Plugin references";
        SectionTabVoxta.Content = voxtaTotal == 0
            ? "Voxta resources"
            : missing > 0
                ? $"Voxta resources ({missing} missing)"
                : $"Voxta resources ({voxtaTotal})";
        var depsNotOnHub = _hubDeps.Count(d => d.Status == HubDependencyStatus.NotOnHub);
        var depsUpdateAvail = _hubDeps.Count(d => d.Status == HubDependencyStatus.UpdateAvailable);
        var depsMismatched = _hubDeps.Count(d => d.Status == HubDependencyStatus.VersionMismatch);
        if (depsTotal == 0)
        {
            SectionTabHubDeps.Content = "Download deps";
        }
        else if (depsMissing > 0 || depsNotOnHub > 0 || depsUpdateAvail > 0 || depsMismatched > 0)
        {
            var parts = new List<string>();
            if (depsMissing > 0) parts.Add($"{depsMissing} missing");
            if (depsUpdateAvail > 0) parts.Add($"{depsUpdateAvail} update available");
            if (depsMismatched > 0) parts.Add($"{depsMismatched} wrong version");
            if (depsNotOnHub > 0) parts.Add($"{depsNotOnHub} not on Hub");
            SectionTabHubDeps.Content = $"Download deps ({string.Join(", ", parts)})";
        }
        else
        {
            SectionTabHubDeps.Content = $"Download deps ({depsTotal})";
        }
    }

    private void AttachResourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not VoxtaResourceRef r) return;

        var dialog = new OpenFileDialog
        {
            Title = $"Attach PNG for {r.KindLabel} {r.NameOrId}",
            Filter = "PNG files (*.png)|*.png|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        if (!IsPngFile(dialog.FileName))
        {
            Log($"Not a valid PNG file: {Path.GetFileName(dialog.FileName)}", ErrorBrush);
            return;
        }

        // Verify the PNG actually contains the character (or scenario etc.) we're attaching it for.
        // Voxta-exported PNGs embed the resource data in a tEXt chunk; we peek at it.
        var verdict = _pngInspector.CheckMatch(dialog.FileName, r.Kind, r.Id, out var inspection);
        switch (verdict)
        {
            case VoxtaPngInspector.MatchVerdict.KindMismatch:
            {
                var actual = inspection.EmbeddedKind?.ToLabel() ?? "unknown";
                MessageBox.Show(this,
                    $"This PNG contains a Voxta {actual}, but you're attaching it to a {r.KindLabel} slot.\n\n" +
                    $"File:   {Path.GetFileName(dialog.FileName)}\n" +
                    $"Expected kind: {r.KindLabel}\n" +
                    $"Actual kind:   {actual}",
                    "Wrong resource kind",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Log($"Refused attachment — kind mismatch ({actual} PNG for {r.KindLabel} slot): {Path.GetFileName(dialog.FileName)}", ErrorBrush);
                return;
            }
            case VoxtaPngInspector.MatchVerdict.UuidMismatch:
            {
                var found = inspection.EmbeddedUuids.Count > 0 ? string.Join(", ", inspection.EmbeddedUuids) : "(none)";
                MessageBox.Show(this,
                    $"This PNG doesn't contain the {r.KindLabel} you expected.\n\n" +
                    $"File:          {Path.GetFileName(dialog.FileName)}\n" +
                    $"Expected UUID: {r.Id}\n" +
                    $"PNG contains:  {found}\n\n" +
                    "Pick the PNG exported from Voxta Studio that matches the UUID above.",
                    "Wrong character",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Log($"Refused attachment — UUID mismatch (expected {r.Id}, PNG has {found}): {Path.GetFileName(dialog.FileName)}", ErrorBrush);
                return;
            }
            case VoxtaPngInspector.MatchVerdict.NoVoxtaMetadata:
                // Permissive: warn but allow. User might know something we don't.
                Log($"Warning: '{Path.GetFileName(dialog.FileName)}' has no Voxta metadata chunk. Attaching anyway.", ErrorBrush);
                break;
            case VoxtaPngInspector.MatchVerdict.Match:
                // Silent success
                break;
        }

        var wasBundled = r.Status == VoxtaResourceStatus.Bundled || r.Status == VoxtaResourceStatus.Replacing;
        r.AttachedSourcePath = dialog.FileName;
        r.Status = wasBundled ? VoxtaResourceStatus.Replacing : VoxtaResourceStatus.Attached;

        var verb = wasBundled ? "Replacing" : "Attached";
        LogParts(
            ($"{verb} ", HeaderBrush),
            ($"{r.KindLabel} ", PluginBrush),
            (r.NameOrId, VersionBrush),
            ("  <-  ", SubduedBrush),
            (Path.GetFileName(dialog.FileName), PathBrush));
        LogParts(
            ("   will embed as: ", SubduedBrush),
            (r.TargetEmbedPath, PathBrush));

        UpdateSectionTabLabels();
    }

    private void DetachResourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not VoxtaResourceRef r) return;
        if (string.IsNullOrEmpty(r.AttachedSourcePath)) return;
        RevertAttachment(r);
        UpdateSectionTabLabels();
    }

    private void ClearAllAttachmentsButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _voxtaResources)
        {
            if (!string.IsNullOrEmpty(r.AttachedSourcePath))
                RevertAttachment(r);
        }
        UpdateSectionTabLabels();
    }

    private static void RevertAttachment(VoxtaResourceRef r)
    {
        r.AttachedSourcePath = null;
        if (r.Status is VoxtaResourceStatus.Attached or VoxtaResourceStatus.Replacing)
        {
            r.Status = string.IsNullOrEmpty(r.BundledPath)
                ? VoxtaResourceStatus.Missing
                : VoxtaResourceStatus.Bundled;
        }
    }

    private static bool IsPngFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> header = stackalloc byte[8];
            if (fs.Read(header) != 8) return false;
            // PNG signature: 89 50 4E 47 0D 0A 1A 0A
            return header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
                && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A;
        }
        catch
        {
            return false;
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        PluginsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        PluginsGrid.CommitEdit(DataGridEditingUnit.Row, true);

        UpdateButton.IsEnabled = false;
        RescanButton.IsEnabled = false;
        SetAllLatestButton.IsEnabled = false;
        ClearAllButton.IsEnabled = false;
        LogClear();

        var updates = _plugins
            .Where(p => !string.IsNullOrWhiteSpace(p.NewVersion))
            .Select(p => new PluginUpdate(p.PluginId, p.NewVersion.Trim()))
            .ToList();

        var attachments = _voxtaResources
            .Where(r => !string.IsNullOrEmpty(r.AttachedSourcePath))
            .Select(r => new VoxtaResourceAttachment(r.Kind, r.Id, r.AttachedSourcePath!))
            .ToList();

        var selectedLicense = LicenseCombo.SelectedItem as string ?? "Do not change license";

        var options = new UpdaterOptions
        {
            SourceVarPath = FilePathBox.Text,
            PluginUpdates = updates,
            VoxtaAttachments = attachments,
            NewLicenseLine = Licenses.All[selectedLicense],
            DryRun = DryRunCheck.IsChecked == true,
            OnCollision = PromptCollision
        };

        var updater = new PackageUpdater(s => Log(s));

        try
        {
            var result = await updater.RunAsync(options);
            if (!result.Success)
                Log($"Error: {result.Error}", ErrorBrush);
        }
        catch (Exception ex)
        {
            Log($"Unexpected error: {ex.Message}", ErrorBrush);
        }
        finally
        {
            var hasPlugins = _plugins.Count > 0;
            var hasVoxta = _voxtaResources.Count > 0;
            UpdateButton.IsEnabled = hasPlugins || hasVoxta;
            SetAllLatestButton.IsEnabled = hasPlugins;
            ClearAllButton.IsEnabled = hasPlugins;
            ClearAllAttachmentsButton.IsEnabled = hasVoxta;
            RescanButton.IsEnabled = true;
        }
    }

    // --- Log helpers ---

    private void InitLogDocument()
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = LogBox.FontFamily,
            FontSize = LogBox.FontSize
        };
        _logParagraph = new Paragraph { Margin = new Thickness(0) };
        doc.Blocks.Add(_logParagraph);
        LogBox.Document = doc;
    }

    private void LogClear() => Dispatcher.Invoke(() =>
    {
        _logParagraph.Inlines.Clear();
    });

    private void Log(string text) => Log(text, DefaultBrush);

    private void Log(string text, Brush brush) => Dispatcher.Invoke(() =>
    {
        if (text.Length > 0) _logParagraph.Inlines.Add(new Run(text) { Foreground = brush });
        _logParagraph.Inlines.Add(new LineBreak());
        LogBox.ScrollToEnd();
    });

    private void LogParts(params (string text, Brush brush)[] parts) => Dispatcher.Invoke(() =>
    {
        foreach (var (text, brush) in parts)
            if (text.Length > 0) _logParagraph.Inlines.Add(new Run(text) { Foreground = brush });
        _logParagraph.Inlines.Add(new LineBreak());
        LogBox.ScrollToEnd();
    });

    // --- Dark title bar ---

    private static void TryEnableDarkTitleBar(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        var useDark = 1;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref useDark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private static Brush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
