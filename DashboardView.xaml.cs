using System.Windows.Controls;
using System.Windows;
using LiveCharts;
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
}
