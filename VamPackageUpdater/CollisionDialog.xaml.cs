using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using VamPackageUpdater.Services;

namespace VamPackageUpdater;

public partial class CollisionDialog : Window
{
    public CollisionChoice Choice { get; private set; } = CollisionChoice.Cancel;

    public CollisionDialog(string existingPath, string nextFreeName)
    {
        InitializeComponent();
        ExistingFileRun.Text = Path.GetFileName(existingPath);
        NextFreeButton.Content = $"Save as  {nextFreeName}";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TryEnableDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    private void OverwriteButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = CollisionChoice.Overwrite;
        DialogResult = true;
    }

    private void NextFreeButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = CollisionChoice.NextFree;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = CollisionChoice.Cancel;
        DialogResult = false;
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
}
