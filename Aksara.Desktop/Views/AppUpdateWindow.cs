using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

public sealed class AppUpdateWindow : Window
{
    private readonly bool _allowInstall;
    private readonly TextBlock _currentVersion = Value();
    private readonly TextBlock _latestVersion = Value();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _notes = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Padding = new Thickness(7),
        MinHeight = 90
    };
    private readonly ProgressBar _progress = new() { Height = 7, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed };
    private readonly Button _checkButton = Button("Cek Ulang", 95);
    private readonly Button _installButton = Button("Unduh & Pasang", 125);
    private AppVersionCheckResult? _result;

    public string? DownloadedPackagePath { get; private set; }

    public AppUpdateWindow(bool allowInstall)
    {
        _allowInstall = allowInstall;
        Title = allowInstall ? "AKSARA - Update" : "AKSARA - Cek Versi";
        Width = 540;
        Height = 390;
        MinWidth = 500;
        MinHeight = 350;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        Content = BuildContent();
        _installButton.Visibility = allowInstall ? Visibility.Visible : Visibility.Collapsed;
        _installButton.IsEnabled = false;
        _checkButton.Click += async (_, _) => await CheckAsync();
        _installButton.Click += async (_, _) => await DownloadAsync();
        Loaded += async (_, _) => await CheckAsync();
    }

    private UIElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = _allowInstall ? "Update AKSARA" : "Cek Versi AKSARA",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = Brush("#74101C"),
            Margin = new Thickness(0, 0, 0, 16)
        };
        root.Children.Add(heading);

        AddVersionRow(root, 1, "Versi saat ini", _currentVersion);
        AddVersionRow(root, 2, "Versi terbaru", _latestVersion);

        _status.Margin = new Thickness(0, 14, 0, 10);
        Grid.SetRow(_status, 3);
        root.Children.Add(_status);

        var notesPanel = new Grid();
        notesPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        notesPanel.RowDefinitions.Add(new RowDefinition());
        notesPanel.Children.Add(new TextBlock { Text = "Catatan versi", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) });
        Grid.SetRow(_notes, 1);
        notesPanel.Children.Add(_notes);
        Grid.SetRow(notesPanel, 4);
        root.Children.Add(notesPanel);

        _progress.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(_progress, 5);
        root.Children.Add(_progress);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var closeButton = Button("Tutup", 90);
        closeButton.Click += (_, _) => Close();
        actions.Children.Add(_checkButton);
        actions.Children.Add(_installButton);
        actions.Children.Add(closeButton);
        Grid.SetRow(actions, 6);
        root.Children.Add(actions);
        return root;
    }

    private async Task CheckAsync()
    {
        SetBusy(true);
        _currentVersion.Text = AppUpdateService.CurrentVersion;
        _latestVersion.Text = "Memeriksa...";
        _notes.Text = string.Empty;
        _status.Text = "Mengambil informasi versi terbaru...";
        _status.Foreground = Brushes.Black;
        try
        {
            _result = await AppUpdateService.CheckAsync();
            _latestVersion.Text = _result.LatestVersion;
            _notes.Text = string.IsNullOrWhiteSpace(_result.Manifest.ReleaseNotes)
                ? "Tidak ada catatan perubahan."
                : _result.Manifest.ReleaseNotes.Trim();
            if (_result.IsUpdateAvailable)
            {
                _status.Text = "Versi baru tersedia dan siap diunduh.";
                _status.Foreground = Brush("#176B35");
                _installButton.IsEnabled = _allowInstall;
            }
            else
            {
                _status.Text = "AKSARA sudah menggunakan versi terbaru.";
                _status.Foreground = Brush("#176B35");
                _installButton.IsEnabled = false;
            }
        }
        catch (Exception exception)
        {
            _result = null;
            _latestVersion.Text = "Belum tersedia";
            _notes.Text = string.Empty;
            _status.Text = exception.Message;
            _status.Foreground = Brush("#B42318");
            _installButton.IsEnabled = false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task DownloadAsync()
    {
        if (_result is not { IsUpdateAvailable: true }) return;
        SetBusy(true);
        _progress.Visibility = Visibility.Visible;
        _progress.Value = 0;
        _status.Text = "Mengunduh dan memeriksa paket pembaruan...";
        _status.Foreground = Brushes.Black;
        try
        {
            var progress = new Progress<double>(value => _progress.Value = Math.Clamp(value, 0, 1));
            DownloadedPackagePath = await AppUpdateService.DownloadPackageAsync(_result.Manifest, progress);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            _status.Foreground = Brush("#B42318");
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _checkButton.IsEnabled = !busy;
        _installButton.IsEnabled = !busy && _allowInstall && _result?.IsUpdateAvailable == true;
    }

    private static void AddVersionRow(Grid root, int row, string label, UIElement value)
    {
        var panel = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(value, 1);
        panel.Children.Add(value);
        Grid.SetRow(panel, row);
        root.Children.Add(panel);
    }

    private static TextBlock Value() => new() { FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
    private static Button Button(string text, double width) => new() { Content = text, Width = width, Height = 29, Margin = new Thickness(7, 0, 0, 0) };
    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
}
