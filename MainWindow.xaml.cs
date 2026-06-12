using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SterilizationGenie.ViewModels;

namespace SterilizationGenie;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_OnLoaded;
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_OnLoaded;
        Dispatcher.BeginInvoke(() =>
        {
            DataContext ??= new MainViewModel();
        }, DispatcherPriority.Background);
    }

    private void LoginPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        // Keep password handling local while typing to avoid unnecessary view-model churn.
    }

    private void LoginPasswordBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (DataContext is MainViewModel viewModel && sender is PasswordBox passwordBox && viewModel.LoginCommand.CanExecute(null))
        {
            viewModel.UpdateLoginPassword(passwordBox.Password);
            viewModel.LoginCommand.Execute(null);
            e.Handled = true;
        }
    }
}
