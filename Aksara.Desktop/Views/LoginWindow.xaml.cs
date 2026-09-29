using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        Title = AppBrand.LoginTitle;
        LoginBrandNameText.Text = AppBrand.ProductName;
        EnvironmentBadgeText.Text = AppBrand.EnvironmentLabel;
        EnvironmentBadge.Visibility = string.IsNullOrWhiteSpace(AppBrand.EnvironmentLabel)
            ? Visibility.Collapsed
            : Visibility.Visible;
        StatusText.Text = "Perusahaan ditentukan otomatis dari nama pengguna.";
        Loaded += (_, _) => UserNameText.Focus();
    }

    private async void Login_Click(object sender, RoutedEventArgs e) => await TryLoginAsync();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void Login_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await TryLoginAsync();
        }
    }

    private async Task TryLoginAsync()
    {
        ErrorText.Text = "";
        StatusText.Text = "Menghubungkan ke server...";

        try
        {
            SetBusy(true);
            var userName = UserNameText.Text.Trim();
            if (userName.Length == 0 || string.IsNullOrWhiteSpace(PasswordText.Password))
                throw new AksaraApiException("Nama pengguna dan password wajib diisi.");

            StatusText.Text = "Menentukan perusahaan...";
            var route = await AksaraApiClient.Shared.ResolveLoginRouteAsync(userName);
            AksaraApiClient.Shared.ConfigureRoutedServerAddress(route.ServerUrl);
            var info = await AksaraApiClient.Shared.GetSystemInfoAsync();
            if (info.Company is null) throw new AksaraApiException("Server belum memiliki identitas perusahaan. Jalankan migrasi server terlebih dahulu.");
            if (route.ExpectedCompanyInstanceId is Guid expectedInstance && expectedInstance != info.Company.InstanceId)
                throw new AksaraApiException("Identitas server perusahaan tidak sesuai dengan direktori login.");
            if (!string.IsNullOrWhiteSpace(route.CompanyCode) &&
                !route.CompanyCode.Equals(info.Company.CompanyCode, StringComparison.OrdinalIgnoreCase))
                throw new AksaraApiException("Kode perusahaan pada server tidak sesuai dengan direktori login.");

            StatusText.Text = $"Menghubungkan ke {info.Company.CompanyName}...";
            var session = await AksaraApiClient.Shared.LoginAsync(userName, PasswordText.Password);
            if (session.Company.InstanceId != info.Company.InstanceId)
            {
                AksaraApiClient.Shared.Logout();
                throw new AksaraApiException("Identitas perusahaan berubah saat login. Silakan periksa server.");
            }
            if (!string.IsNullOrWhiteSpace(session.ServerUrl) &&
                !new Uri(session.ServerUrl.TrimEnd('/') + "/").Equals(AksaraApiClient.Shared.ServerAddress))
            {
                AksaraApiClient.Shared.Logout();
                throw new AksaraApiException("Server login mengembalikan rute yang tidak sesuai.");
            }
            StatusText.Text = $"Masuk sebagai {session.DisplayName} pada {session.Company.CompanyName}";
            DialogResult = true;
            Close();
        }
        catch (UriFormatException)
        {
            ErrorText.Text = "Alamat server tidak valid.";
        }
        catch (AksaraApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            ErrorText.Text = "Username atau password tidak sesuai.";
            PasswordText.SelectAll();
            PasswordText.Focus();
        }
        catch (AksaraApiException exception)
        {
            ErrorText.Text = exception.Message;
        }
        catch (HttpRequestException)
        {
            ErrorText.Text = "Server " + AppBrand.ProductName + " belum bisa dihubungi.";
        }
        catch (TaskCanceledException)
        {
            ErrorText.Text = "Koneksi ke server terlalu lama.";
        }
        finally
        {
            SetBusy(false);
            if (DialogResult != true) StatusText.Text = "Perusahaan ditentukan otomatis dari nama pengguna.";
        }
    }

    private void SetBusy(bool busy)
    {
        Cursor = busy ? Cursors.Wait : null;
        LoginProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UserNameText.IsEnabled = !busy;
        PasswordText.IsEnabled = !busy;
        LoginButton.IsEnabled = !busy;
        CloseButton.IsEnabled = !busy;
    }
}

internal sealed class CompanyProfileEditorWindow : Window
{
    private readonly TextBox _code = Input();
    private readonly TextBox _name = Input();
    private readonly TextBox _server = Input();
    private readonly TextBox _manifest = Input();

    public CompanyConnectionProfile? Profile { get; private set; }

    public CompanyProfileEditorWindow()
    {
        Title = "Tambah Profil Perusahaan";
        Width = 650;
        Height = 410;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = PartnerUi.CanvasBrush;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(68) });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(62) });

        var heading = new Border
        {
            Background = PartnerUi.MaroonBrush,
            Padding = new Thickness(18, 11, 18, 11),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "Tambah Profil Perusahaan",
                        FontSize = 16,
                        FontWeight = FontWeights.Bold,
                        Foreground = Brushes.White
                    },
                    new TextBlock
                    {
                        Text = "Simpan koneksi perusahaan pada komputer ini",
                        Margin = new Thickness(0, 3, 0, 0),
                        Foreground = new SolidColorBrush(Color.FromRgb(243, 222, 225))
                    }
                }
            }
        };
        root.Children.Add(heading);

        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(135) });
        form.ColumnDefinitions.Add(new ColumnDefinition());
        for (var index = 0; index < 4; index++) form.RowDefinitions.Add(new RowDefinition { Height = new GridLength(43) });
        form.RowDefinitions.Add(new RowDefinition());
        AddField(form, "Kode perusahaan", _code, 0);
        AddField(form, "Nama perusahaan", _name, 1);
        AddField(form, "Alamat server", _server, 2);
        AddField(form, "Manifest update", _manifest, 3);
        var identityInfo = new Border
        {
            Background = PartnerUi.GoldSoftBrush,
            BorderBrush = PartnerUi.GoldBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(9, 6, 9, 6),
            Margin = new Thickness(0, 5, 0, 0),
            Child = new TextBlock
            {
                Text = "Instance ID dikunci otomatis setelah identitas server dan login berhasil diverifikasi.",
                Foreground = PartnerUi.MaroonDarkBrush,
                TextWrapping = TextWrapping.Wrap
            }
        };
        Grid.SetRow(identityInfo, 4);
        Grid.SetColumnSpan(identityInfo, 2);
        form.Children.Add(identityInfo);
        var formCard = PartnerUi.Card(form, new Thickness(14, 11, 14, 11), new Thickness(14, 12, 14, 12));
        Grid.SetRow(formCard, 1);
        root.Children.Add(formCard);

        var footer = new Border
        {
            Background = PartnerUi.SurfaceBrush,
            BorderBrush = PartnerUi.BorderBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 10, 8, 10)
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = PartnerUi.Button("Simpan", 104, PartnerButtonKind.Primary, "\uE74E", "Simpan profil perusahaan");
        var cancel = PartnerUi.Button("Batal", 94, PartnerButtonKind.Standard, "\uE711", "Batalkan penambahan profil");
        cancel.Margin = new Thickness(0);
        save.Click += (_, _) => SaveProfile();
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        footer.Child = buttons;
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;

        Loaded += (_, _) => _code.Focus();
    }

    private void SaveProfile()
    {
        var code = _code.Text.Trim().ToUpperInvariant();
        var manifest = _manifest.Text.Trim();
        var serverValid = Uri.TryCreate(_server.Text.Trim(), UriKind.Absolute, out var serverUri)
            && (serverUri.Scheme == Uri.UriSchemeHttps || serverUri.Scheme == Uri.UriSchemeHttp);
        var manifestValid = string.IsNullOrWhiteSpace(manifest) || Uri.TryCreate(manifest, UriKind.Absolute, out _);
        if (code.Length is < 2 or > 40
            || code.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_')
            || string.IsNullOrWhiteSpace(_name.Text)
            || !serverValid
            || !manifestValid)
        {
            MessageBox.Show(
                this,
                "Kode (huruf/angka/-/_), nama, alamat server HTTP(S), dan manifest yang valid wajib diisi.",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Profile = new(
            Guid.NewGuid(),
            code,
            _name.Text.Trim(),
            _server.Text.Trim(),
            string.IsNullOrWhiteSpace(manifest) ? null : manifest,
            null);
        DialogResult = true;
    }

    private static TextBox Input() => new()
    {
        Height = 29,
        MinWidth = 390,
        BorderBrush = PartnerUi.BorderBrush,
        VerticalContentAlignment = VerticalAlignment.Center
    };

    private static void AddField(Grid grid, string label, Control input, int row)
    {
        var text = new TextBlock
        {
            Text = label,
            Foreground = PartnerUi.TextBrush,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(text, row);
        grid.Children.Add(text);
        Grid.SetRow(input, row);
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);
    }
}
