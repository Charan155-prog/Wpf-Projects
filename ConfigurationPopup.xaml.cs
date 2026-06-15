using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SterilizationGenie.Views;

public partial class ConfigurationPopup : System.Windows.Controls.UserControl
{
    public ConfigurationPopup()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            Content = BuildErrorFallback(ex);
        }
    }

    private static UIElement BuildErrorFallback(Exception ex)
    {
        return new Border
        {
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(190, 60, 60)),
            BorderThickness = new Thickness(2),
            Padding = new Thickness(24),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "Configuration popup failed to load.",
                        FontSize = 20,
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(120, 20, 20))
                    },
                    new TextBlock
                    {
                        Margin = new Thickness(0, 12, 0, 0),
                        Text = ex.Message,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = System.Windows.Media.Brushes.Black
                    },
                    new TextBlock
                    {
                        Margin = new Thickness(0, 12, 0, 0),
                        Text = ex.InnerException?.Message ?? string.Empty,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(80, 80, 80))
                    }
                }
            }
        };
    }
}
