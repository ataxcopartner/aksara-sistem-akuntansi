using System.Windows;
using System.Windows.Controls;

namespace Aksara.Desktop.Views;

public partial class HomeView : UserControl
{
    public event EventHandler<string>? ModuleRequested;
    public HomeView() => InitializeComponent();
    private void OpenModule_Click(object sender, RoutedEventArgs e) => ModuleRequested?.Invoke(this, (sender as FrameworkElement)?.Tag?.ToString() ?? string.Empty);
}
