using System;
using System.Windows;
using System.Windows.Threading;
using Aksara.Desktop.Services;
using Aksara.Desktop.Views;
using Aksara.Contracts;

namespace Aksara.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var login = new LoginWindow();
        if (login.ShowDialog() != true)
        {
            Shutdown();
            return;
        }

        InitialSetupDto? setup = null;
        while (setup is null)
        {
            try
            {
                setup = await AksaraApiClient.Shared.GetInitialSetupAsync();
            }
            catch (Exception exception)
            {
                var retry = MessageBox.Show(
                    $"Status Setup Data Awal tidak dapat diperiksa. Akses aplikasi dikunci demi keamanan data.\n\n{exception.Message}\n\nPilih Coba Lagi untuk mengulangi, atau Batal untuk logout.",
                    "AKSARA - Pemeriksaan Setup",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning,
                    MessageBoxResult.OK);
                if (retry == MessageBoxResult.OK) continue;
                AksaraApiClient.Shared.Logout();
                Shutdown();
                return;
            }
        }

        var mainWindow = new MainWindow(setup);
        MainWindow = mainWindow;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWindow.Show();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        if (IsBenignGridRefreshException(args.Exception))
        {
            args.Handled = true;
            return;
        }

        MessageBox.Show(args.Exception.Message, AppBrand.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
        args.Handled = true;
    }

    private static bool IsBenignGridRefreshException(Exception exception)
    {
        if (exception is not InvalidOperationException) return false;

        var message = exception.Message;
        return message.Contains("Refresh", StringComparison.OrdinalIgnoreCase)
            && (message.Contains("AddNew", StringComparison.OrdinalIgnoreCase)
                || message.Contains("EditItem", StringComparison.OrdinalIgnoreCase));
    }
}
