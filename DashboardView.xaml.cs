using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LiveCharts;
using Microsoft.Win32;
using SterilizationGenie.ViewModels;

namespace SterilizationGenie.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
    }

    private void MainChart_OnDataClick(object sender, ChartPoint chartPoint)
    {
        if (DataContext is SterilizationDashboardViewModel vm)
        {
            vm.OnChartDataClick(chartPoint);
        }
    }

    private void ExportSavePngButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new SaveFileDialog
            {
                Filter = "PNG image (*.png)|*.png",
                FileName = BuildSuggestedFileName("png")
            };
            if (dlg.ShowDialog() != true) return;

            var target = (DataContext as SterilizationDashboardViewModel)?.IsOnline == true
                ? OnlineMainChart
                : MainChart;
            if (target == null || target.ActualWidth < 1 || target.ActualHeight < 1) return;

            var width = (int)Math.Ceiling(target.ActualWidth);
            var height = (int)Math.Ceiling(target.ActualHeight);
            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);

            // Force a fresh layout pass before rendering so any pending updates are captured.
            target.Measure(new Size(width, height));
            target.Arrange(new Rect(new Size(width, height)));
            rtb.Render(target);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var stream = File.Create(dlg.FileName);
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not save chart image:\n" + ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExportSaveDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not SterilizationDashboardViewModel vm) return;

            var dlg = new SaveFileDialog
            {
                Filter = "Text file (*.txt)|*.txt",
                FileName = BuildSuggestedFileName("txt")
            };
            if (dlg.ShowDialog() != true) return;

            File.WriteAllText(dlg.FileName, vm.ExportPanelSummary);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not save details:\n" + ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string BuildSuggestedFileName(string extension)
    {
        var rep = (DataContext as SterilizationDashboardViewModel)?.SelectedRepresentationDisplayName ?? "view";
        foreach (var ch in Path.GetInvalidFileNameChars())
        {
            rep = rep.Replace(ch, '_');
        }
        return $"SterilizationGenie_{rep}_{DateTime.Now:yyyyMMdd_HHmmss}.{extension}";
    }
}
