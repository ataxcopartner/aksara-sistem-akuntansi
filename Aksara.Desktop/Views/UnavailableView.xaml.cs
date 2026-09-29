using System.Windows;
using System.Windows.Controls;

namespace Aksara.Desktop.Views;

public partial class UnavailableView : UserControl
{
    public UnavailableView(string moduleName)
    {
        InitializeComponent();
        HeadingText.Text = moduleName;
    }

    public event EventHandler? CloseRequested;
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
