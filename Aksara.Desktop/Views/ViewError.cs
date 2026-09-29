using System.Windows;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

internal static class ViewError
{
    public static void ShowWarningUnlessDatabaseUnavailable(Exception exception, string title)
    {
        if (exception is AksaraApiException { Code: "DATABASE_NOT_CONFIGURED" }) return;
        MessageBox.Show(exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
