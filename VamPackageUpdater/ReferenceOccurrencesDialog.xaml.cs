using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using VamPackageUpdater.Models;

namespace VamPackageUpdater;

public partial class ReferenceOccurrencesDialog : Window
{
    // Soft amber — readable on the dark grid, doesn't fight the theme. ARGB: 40% alpha over #FFC107.
    private static readonly Brush HighlightBackground = Freeze(0x66, 0xFF, 0xC1, 0x07);
    private static readonly Brush HighlightForeground = Freeze(0xFF, 0xFF, 0xE9, 0xB3);
    private static readonly Brush LineNumberBrush = Freeze(0x64, 0x64, 0x64);
    private static readonly Brush MatchLineNumberBrush = Freeze(0xDC, 0xDC, 0xAA);
    private static readonly Brush CodeBrush = Freeze(0xDC, 0xDC, 0xDC);

    public ReferenceOccurrencesDialog(PluginReferenceGroup group)
    {
        InitializeComponent();

        HeaderPluginRun.Text = group.PluginId;
        HeaderCountRun.Text = $"   ({group.OccurrenceCount} occurrence{(group.OccurrenceCount == 1 ? "" : "s")} across {group.FileCount} file{(group.FileCount == 1 ? "" : "s")})";

        var items = group.Occurrences
            .OrderBy(o => o.File, StringComparer.Ordinal)
            .ThenBy(o => o.Line)
            .Select(o => new OccurrenceItem(o))
            .ToList();

        OccurrenceList.ItemsSource = items;
        if (items.Count > 0)
            OccurrenceList.SelectedIndex = 0;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TryEnableDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    private void OccurrenceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OccurrenceList.SelectedItem is OccurrenceItem item)
            RenderSnippet(item);
    }

    private void RenderSnippet(OccurrenceItem item)
    {
        DetailFileRun.Text = $"{item.File}  ·  line {item.Line}  ·  {item.CategoryLabel}";
        SnippetBlock.Inlines.Clear();

        var o = item.Occurrence;
        var lineNumberWidth = (o.SnippetStartLine + o.SnippetLines.Count - 1).ToString().Length;

        for (var i = 0; i < o.SnippetLines.Count; i++)
        {
            var lineNumber = o.SnippetStartLine + i;
            var isMatchLine = i == o.SnippetMatchLineIndex;

            SnippetBlock.Inlines.Add(new Run(lineNumber.ToString().PadLeft(lineNumberWidth) + "  ")
            {
                Foreground = isMatchLine ? MatchLineNumberBrush : LineNumberBrush
            });

            var lineText = o.SnippetLines[i];

            if (isMatchLine)
            {
                var colIdx = Math.Max(0, Math.Min(o.Column - 1, lineText.Length));
                var matchEnd = Math.Min(colIdx + o.MatchLength, lineText.Length);

                if (colIdx > 0)
                    SnippetBlock.Inlines.Add(new Run(lineText.Substring(0, colIdx)) { Foreground = CodeBrush });

                if (matchEnd > colIdx)
                {
                    SnippetBlock.Inlines.Add(new Run(lineText.Substring(colIdx, matchEnd - colIdx))
                    {
                        Background = HighlightBackground,
                        Foreground = HighlightForeground,
                        FontWeight = FontWeights.SemiBold
                    });
                }

                if (matchEnd < lineText.Length)
                    SnippetBlock.Inlines.Add(new Run(lineText.Substring(matchEnd)) { Foreground = CodeBrush });
            }
            else
            {
                SnippetBlock.Inlines.Add(new Run(lineText) { Foreground = CodeBrush });
            }

            if (i < o.SnippetLines.Count - 1)
                SnippetBlock.Inlines.Add(new LineBreak());
        }
    }

    private void CopyLocationButton_Click(object sender, RoutedEventArgs e)
    {
        if (OccurrenceList.SelectedItem is OccurrenceItem item)
        {
            try { Clipboard.SetText($"{item.File}:{item.Line}"); }
            catch { /* clipboard can occasionally be contested — no-op */ }
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static Brush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Brush Freeze(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
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

    /// <summary>
    /// View-model row wrapping a single occurrence for the list box.
    /// </summary>
    private sealed class OccurrenceItem
    {
        public PluginReferenceOccurrence Occurrence { get; }
        public string File => Occurrence.File;
        public int Line => Occurrence.Line;
        public string Version => Occurrence.Version;
        public string CategoryLabel => Occurrence.Category.ToLabel();

        public OccurrenceItem(PluginReferenceOccurrence occurrence) => Occurrence = occurrence;
    }
}
