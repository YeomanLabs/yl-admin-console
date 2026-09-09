using Wpf.Ui.Controls;
using YeomanLabs.AdminConsole.App.ViewModels;

namespace YeomanLabs.AdminConsole.App.Views;

public sealed partial class MainWindow : FluentWindow
{
    public MainWindow(ShellViewModel shell)
    {
        DataContext = shell;
        InitializeComponent();
    }
}
