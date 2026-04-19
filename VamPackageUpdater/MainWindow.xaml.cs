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
    private readonly PluginReferenceScanner _scanner = new();
    private readonly VoxtaResourceScanner _voxtaScanner = new();
    private readonly VoxtaPngInspector _pngInspector = new();
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
        RescanButton.IsEnabled = false;
        UpdateButton.IsEnabled = false;
        SetAllLatestButton.IsEnabled = false;
        ClearAllButton.IsEnabled = false;
        ClearAllAttachmentsButton.IsEnabled = false;
        LogParts(("Scanning ", SubduedBrush), ($"'{Path.GetFileName(FilePathBox.Text)}'", PathBrush), ("...", SubduedBrush));

        try
        {
            var metaTask = _metaReader.ReadAsync(FilePathBox.Text);
            var voxtaTask = _voxtaScanner.ScanAsync(FilePathBox.Text);
            var results = await _scanner.ScanAsync(FilePathBox.Text);
            var meta = await metaTask;
            var voxtaResults = await voxtaTask;
            ShowMetaPanel(meta);

            foreach (var g in results)
                _plugins.Add(g);
            foreach (var r in voxtaResults)
                _voxtaResources.Add(r);

            LogPluginScanResults();
            LogVoxtaScanResults();

            var hasPlugins = _plugins.Count > 0;
            var hasVoxta = _voxtaResources.Count > 0;

            UpdateButton.IsEnabled = hasPlugins || hasVoxta;
            SetAllLatestButton.IsEnabled = hasPlugins;
            ClearAllButton.IsEnabled = hasPlugins;
            ClearAllAttachmentsButton.IsEnabled = hasVoxta;
            RescanButton.IsEnabled = true;
            UpdateTabCounts();
            UpdateSectionTabLabels();

            // Auto-jump to Voxta tab if there are missing resources — they're the actionable ones.
            if (_voxtaResources.Any(r => r.Status == VoxtaResourceStatus.Missing))
                SwitchSection("Voxta");
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

    private void SectionTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb) return;
        var section = tb.Tag as string ?? "Plugins";
        SwitchSection(section);
    }

    private void SwitchSection(string section)
    {
        var wantVoxta = section == "Voxta";
        SectionTabPlugins.IsChecked = !wantVoxta;
        SectionTabVoxta.IsChecked = wantVoxta;
        PluginsSection.Visibility = wantVoxta ? Visibility.Collapsed : Visibility.Visible;
        VoxtaSection.Visibility = wantVoxta ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSectionTabLabels()
    {
        var pluginsCount = _plugins.Count;
        var missing = _voxtaResources.Count(r => r.Status == VoxtaResourceStatus.Missing);
        var voxtaTotal = _voxtaResources.Count;

        SectionTabPlugins.Content = pluginsCount > 0 ? $"Plugin references ({pluginsCount})" : "Plugin references";
        SectionTabVoxta.Content = voxtaTotal == 0
            ? "Voxta resources"
            : missing > 0
                ? $"Voxta resources ({missing} missing)"
                : $"Voxta resources ({voxtaTotal})";
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
