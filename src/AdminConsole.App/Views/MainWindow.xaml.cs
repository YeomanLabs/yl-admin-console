using Wpf.Ui.Controls;
using AdminConsole.App.ViewModels;

namespace AdminConsole.App.Views;

public sealed partial class MainWindow : FluentWindow
{
    public MainWindow(ShellViewModel shell)
    {
        DataContext = shell;
        InitializeComponent();
    }
}
