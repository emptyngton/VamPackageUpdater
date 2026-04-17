using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using VamPackageUpdater.Services;

namespace VamPackageUpdater;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        foreach (var key in Licenses.All.Keys)
            LicenseCombo.Items.Add(key);
        LicenseCombo.SelectedIndex = 0;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        TryEnableDarkTitleBar(hwnd);
    }

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

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a .var package",
            Filter = "Virt-a-Mate Packages (*.var)|*.var|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            FilePathBox.Text = dialog.FileName;
            UpdateButton.IsEnabled = true;
            Log($"Selected file: {Path.GetFileName(dialog.FileName)}");
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        LogBox.Clear();

        var selectedLicense = LicenseCombo.SelectedItem as string ?? "Do not change license";
        var options = new UpdaterOptions
        {
            SourceVarPath = FilePathBox.Text,
            PluginName = PluginNameBox.Text.Trim(),
            NewVersion = NewVersionBox.Text.Trim(),
            NewLicenseLine = Licenses.All[selectedLicense]
        };

        var updater = new PackageUpdater(Log);

        try
        {
            var result = await updater.RunAsync(options);
            if (!result.Success)
                Log($"Error: {result.Error}");
        }
        catch (Exception ex)
        {
            Log($"Unexpected error: {ex.Message}");
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    private void Log(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText(message + Environment.NewLine);
            LogScroll.ScrollToEnd();
        });
    }
}
