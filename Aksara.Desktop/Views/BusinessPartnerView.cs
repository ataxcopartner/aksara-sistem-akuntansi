using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Aksara.Contracts;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

public enum BusinessPartnerKind { Supplier, Customer, Salesman }

public sealed class BusinessPartnerView : UserControl
{
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly BusinessPartnerKind _kind;
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        CanUserAddRows = false,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.All,
        SelectionMode = DataGridSelectionMode.Single,
        SelectionUnit = DataGridSelectionUnit.FullRow,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        RowHeight = 23,
        ColumnHeaderHeight = 26,
        Background = Brushes.White,
        RowBackground = Brushes.White,
        AlternatingRowBackground = Brush("#F4F4F4"),
        BorderBrush = Brush("#9AA8B6"),
        BorderThickness = new Thickness(1)
    };
    private readonly TextBlock _modeText = new() { FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _dataCount = new() { FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _order = new() { Width = 170, Height = 24, Margin = new Thickness(6, 0, 10, 0) };
    private string _searchQuery = string.Empty;

    public event EventHandler? CloseRequested;
    public string ViewTitle => _kind switch
    {
        BusinessPartnerKind.Supplier => "Daftar Supplier",
        BusinessPartnerKind.Customer => "Daftar Customer",
        _ => "Daftar Salesman"
    };

    public BusinessPartnerView(BusinessPartnerKind kind)
    {
        _kind = kind;
        FontFamily = new FontFamily("Tahoma");
        FontSize = 12;
        BuildColumns();
        ConfigureOrderChoices();
        Content = Build();
        Loaded += async (_, _) => await ReloadAsync();
        _grid.MouseDoubleClick += async (_, _) => await EditAsync();
        _grid.KeyDown += async (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                args.Handled = true;
                await EditAsync();
            }
        };
        _order.SelectionChanged += async (_, _) =>
        {
            if (IsLoaded) await ReloadAsync(SelectedCode());
        };
    }

    private UIElement Build()
    {
        var root = new Grid { Margin = new Thickness(8), Background = Brush("#EFEFEF") };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(HeaderStrip());
        Grid.SetRow(_grid, 1);
        _grid.Margin = new Thickness(0, 8, 0, 8);
        root.Children.Add(_grid);

        var footer = BuildFooter();
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    private UIElement HeaderStrip()
    {
        var header = new Grid { Height = 40, Background = Brush("#F6F6F6") };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

        AddHeaderCell(header, new CheckBox { Content = "Exclusive Mode", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }, 0);
        AddHeaderCell(header, new TextBlock { Text = "Status:", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }, 1);
        _modeText.Text = "DAFTAR DATA";
        AddHeaderCell(header, _modeText, 2);
        AddHeaderCell(header, _dataCount, 3);
        var back = new Button { Content = "|||", FontWeight = FontWeights.Bold, FontSize = 16, Width = 40, Height = 28, Padding = new Thickness(0), ToolTip = "Kembali ke daftar awal" };
        back.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        AddHeaderCell(header, back, 4);
        return header;
    }

    private UIElement BuildFooter()
    {
        var footer = new Grid { Background = Brush("#EFEFEF") };
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var actions = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var orderPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        orderPanel.Children.Add(new TextBlock { Text = "Urutan", TextDecorations = TextDecorations.Underline, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        orderPanel.Children.Add(_order);
        actions.Children.Add(orderPanel);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var refresh = FooterButton("Refresh");
        refresh.Click += async (_, _) => await ReloadAsync(SelectedCode());
        var search = FooterButton("Cari");
        search.Click += async (_, _) => await PromptSearchAsync();
        var add = FooterButton("Tambah");
        add.Click += async (_, _) => await OpenEditorAsync(null);
        var edit = FooterButton("Edit");
        edit.Click += async (_, _) => await EditAsync();
        var delete = FooterButton("Hps./Btl.");
        delete.Click += async (_, _) => await ToggleActiveAsync();
        var close = FooterButton("Tutup");
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        foreach (var button in new[] { refresh, search, add, edit, delete, close }) buttons.Children.Add(button);
        Grid.SetColumn(buttons, 2);
        actions.Children.Add(buttons);
        Grid.SetRow(actions, 0);
        footer.Children.Add(actions);

        var statusPanel = new Border { Background = Brushes.White, Padding = new Thickness(8, 6, 8, 6), Child = _status };
        Grid.SetRow(statusPanel, 1);
        footer.Children.Add(statusPanel);
        return footer;
    }

    private void BuildColumns()
    {
        _grid.Columns.Clear();
        if (_kind == BusinessPartnerKind.Customer)
        {
            _grid.Columns.Add(Column("Kode", nameof(CustomerMasterDto.Code), 85));
            _grid.Columns.Add(Column("Nama Pemilik", nameof(CustomerMasterDto.OwnerName), 145));
            _grid.Columns.Add(Column("Toko/PT", nameof(CustomerMasterDto.CompanyName), 190));
            _grid.Columns.Add(Column("Alamat 1", nameof(CustomerMasterDto.Address1), 230));
            _grid.Columns.Add(Column("Alamat 2", nameof(CustomerMasterDto.Address2), 230));
            _grid.Columns.Add(Column("Kecamatan", nameof(CustomerMasterDto.District), 115));
            _grid.Columns.Add(Column("Kota", nameof(CustomerMasterDto.City), 105));
            _grid.Columns.Add(Column("Kode Pos", nameof(CustomerMasterDto.PostalCode), 75));
            _grid.Columns.Add(Column("Hp.(1)", nameof(CustomerMasterDto.Mobile1), 110));
            _grid.Columns.Add(Column("Hp.(2)", nameof(CustomerMasterDto.Mobile2), 110));
            _grid.Columns.Add(Column("Telp.(1)", nameof(CustomerMasterDto.Phone), 110));
            _grid.Columns.Add(Column("Telp.(2)", nameof(CustomerMasterDto.Phone2), 110));
            _grid.Columns.Add(Column("Fax", nameof(CustomerMasterDto.Fax), 110));
            _grid.Columns.Add(Column("Kode Sales", nameof(CustomerMasterDto.SalesCode), 90));
            _grid.Columns.Add(Column("Hrg.Jual", nameof(CustomerMasterDto.PriceGroup), 85));
            _grid.Columns.Add(Column("Kond.Trans.", nameof(CustomerMasterDto.TransactionCondition), 95));
            _grid.Columns.Add(Column("Plafon Piutang", nameof(CustomerMasterDto.CreditLimit), 120, "N0"));
            _grid.Columns.Add(Column("NPWP", nameof(CustomerMasterDto.TaxNumber), 145));
            _grid.Columns.Add(Column("NPPKP", nameof(CustomerMasterDto.TaxRegistrationNumber), 145));
            _grid.Columns.Add(CheckColumn("Tdk.A", nameof(CustomerMasterDto.IsInactive), 58));
            return;
        }

        if (_kind == BusinessPartnerKind.Salesman)
        {
            _grid.Columns.Add(Column("Kode", nameof(SalespersonMasterDto.Code), 85));
            _grid.Columns.Add(Column("Nama", nameof(SalespersonMasterDto.Name), 170));
            _grid.Columns.Add(Column("Alamat", nameof(SalespersonMasterDto.Address), 260));
            _grid.Columns.Add(Column("Kecamatan", nameof(SalespersonMasterDto.District), 115));
            _grid.Columns.Add(Column("Kota", nameof(SalespersonMasterDto.City), 110));
            _grid.Columns.Add(Column("Kode Pos", nameof(SalespersonMasterDto.PostalCode), 75));
            _grid.Columns.Add(Column("Telp.", nameof(SalespersonMasterDto.Phone), 120));
            _grid.Columns.Add(Column("Komisi", nameof(SalespersonMasterDto.CommissionPercent), 80, "N2"));
            _grid.Columns.Add(Column("Target Tghn.", nameof(SalespersonMasterDto.CollectionTarget), 120, "N0"));
            _grid.Columns.Add(Column("Target Omset", nameof(SalespersonMasterDto.SalesTarget), 120, "N0"));
            _grid.Columns.Add(Column("Gj.Pokok", nameof(SalespersonMasterDto.BaseSalary), 110, "N0"));
            _grid.Columns.Add(Column("Bns.Target", nameof(SalespersonMasterDto.TargetBonus), 110, "N0"));
            _grid.Columns.Add(CheckColumn("Tdk.A", nameof(SalespersonMasterDto.IsInactive), 58));
            return;
        }

        _grid.Columns.Add(Column("Kode", nameof(SupplierMasterDto.Code), 95));
        _grid.Columns.Add(Column("Nama Pemilik", nameof(SupplierMasterDto.OwnerName), 145));
        _grid.Columns.Add(Column("Toko/PT", nameof(SupplierMasterDto.CompanyName), 190));
        _grid.Columns.Add(Column("Alamat 1", nameof(SupplierMasterDto.Address1), 230));
        _grid.Columns.Add(Column("Alamat 2", nameof(SupplierMasterDto.Address2), 230));
        _grid.Columns.Add(Column("Kecamatan", nameof(SupplierMasterDto.District), 115));
        _grid.Columns.Add(Column("Kota", nameof(SupplierMasterDto.City), 105));
        _grid.Columns.Add(Column("Kode Pos", nameof(SupplierMasterDto.PostalCode), 75));
        _grid.Columns.Add(Column("Hp.(1)", nameof(SupplierMasterDto.Mobile1), 110));
        _grid.Columns.Add(Column("Hp.(2)", nameof(SupplierMasterDto.Mobile2), 110));
        _grid.Columns.Add(Column("Telp.(1)", nameof(SupplierMasterDto.Phone), 110));
        _grid.Columns.Add(Column("Telp.(2)", nameof(SupplierMasterDto.Phone2), 110));
        _grid.Columns.Add(Column("Fax", nameof(SupplierMasterDto.Fax), 110));
        _grid.Columns.Add(Column("NPWP", nameof(SupplierMasterDto.TaxNumber), 145));
        _grid.Columns.Add(Column("NPPKP", nameof(SupplierMasterDto.TaxRegistrationNumber), 145));
        _grid.Columns.Add(Column("Tgl. PPKP", nameof(SupplierMasterDto.TaxRegisteredDate), 90, "dd/MM/yyyy"));
        _grid.Columns.Add(CheckColumn("Tdk.Aktif", nameof(SupplierMasterDto.IsInactive), 70));
    }

    private void ConfigureOrderChoices()
    {
        _order.Items.Clear();
        var choices = _kind switch
        {
            BusinessPartnerKind.Customer => new[] { "Kode Customer", "Toko/PT", "Nama Pemilik", "Kota", "Kode Sales" },
            BusinessPartnerKind.Salesman => new[] { "Kode Sales", "Nama", "Kota" },
            _ => new[] { "Kode Supplier", "Toko/PT", "Kota" }
        };
        foreach (var choice in choices) _order.Items.Add(choice);
        _order.SelectedIndex = 0;
    }

    public async Task ReloadAsync(string? selectCode = null)
    {
        try
        {
            IsEnabled = false;
            _status.Text = "Mengambil data dari server...";
            var query = _searchQuery;
            IList rows = _kind switch
            {
                BusinessPartnerKind.Supplier => (await _api.GetSuppliersAsync(query, true)).ToList(),
                BusinessPartnerKind.Customer => (await _api.GetCustomersAsync(query, true)).ToList(),
                _ => (await _api.GetSalespeopleAsync(query, true)).ToList()
            };
            var sorted = SortRows(rows.Cast<object>()).ToList();
            _grid.ItemsSource = sorted;
            _dataCount.Text = $"TOTAL DATA {sorted.Count:N0}";
            if (!string.IsNullOrWhiteSpace(selectCode))
                _grid.SelectedItem = sorted.FirstOrDefault(x => string.Equals(PartnerCode(x), selectCode, StringComparison.OrdinalIgnoreCase));
            if (_grid.SelectedItem is null && sorted.Count > 0) _grid.SelectedIndex = 0;
            _status.Text = $"{sorted.Count:N0} data | Refresh, Cari, Tambah, Edit, Hps./Btl., dan Tutup sudah terhubung ke database AKSARA.";
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            MessageBox.Show(exception.Message, ViewTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { IsEnabled = true; }
    }

    private IEnumerable<object> SortRows(IEnumerable<object> rows)
    {
        var order = _order.SelectedItem?.ToString() ?? string.Empty;
        return (_kind, order) switch
        {
            (BusinessPartnerKind.Customer, "Toko/PT") => rows.OrderBy(x => ((CustomerMasterDto)x).CompanyName, StringComparer.OrdinalIgnoreCase),
            (BusinessPartnerKind.Customer, "Nama Pemilik") => rows.OrderBy(x => ((CustomerMasterDto)x).OwnerName, StringComparer.OrdinalIgnoreCase),
            (BusinessPartnerKind.Customer, "Kota") => rows.OrderBy(x => ((CustomerMasterDto)x).City, StringComparer.OrdinalIgnoreCase),
            (BusinessPartnerKind.Customer, "Kode Sales") => rows.OrderBy(x => ((CustomerMasterDto)x).SalesCode, StringComparer.OrdinalIgnoreCase),
            (BusinessPartnerKind.Salesman, "Nama") => rows.OrderBy(x => ((SalespersonMasterDto)x).Name, StringComparer.OrdinalIgnoreCase),
            (BusinessPartnerKind.Salesman, "Kota") => rows.OrderBy(x => ((SalespersonMasterDto)x).City, StringComparer.OrdinalIgnoreCase),
            (BusinessPartnerKind.Supplier, "Toko/PT") => rows.OrderBy(x => ((SupplierMasterDto)x).CompanyName, StringComparer.OrdinalIgnoreCase),
            (BusinessPartnerKind.Supplier, "Kota") => rows.OrderBy(x => ((SupplierMasterDto)x).City, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderBy(PartnerCode, StringComparer.OrdinalIgnoreCase)
        };
    }

    private async Task PromptSearchAsync()
    {
        var prompt = new TextPromptWindow("Cari", $"Cari {ViewTitle}", _searchQuery) { Owner = Window.GetWindow(this) };
        if (prompt.ShowDialog() != true) return;
        _searchQuery = prompt.Value.Trim();
        await ReloadAsync();
    }

    private async Task EditAsync()
    {
        if (_grid.SelectedItem is not null) await OpenEditorAsync(_grid.SelectedItem);
    }

    private async Task OpenEditorAsync(object? row)
    {
        var editor = new BusinessPartnerEditWindow(_kind, row) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() == true) await ReloadAsync(editor.SavedCode);
    }

    private async Task ToggleActiveAsync()
    {
        if (_grid.SelectedItem is null) return;
        var selected = _grid.SelectedItem;
        var active = PartnerIsActive(selected);
        var action = active ? "hapus/nonaktifkan" : "batalkan hapus/aktifkan kembali";
        if (MessageBox.Show($"Yakin {action} {PartnerCode(selected)}?", ViewTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            IsEnabled = false;
            switch (selected)
            {
                case CustomerMasterDto customer:
                    await _api.SaveCustomerAsync(CustomerRequest(customer, !customer.IsActive));
                    break;
                case SalespersonMasterDto salesperson:
                    await _api.SaveSalespersonAsync(SalespersonRequest(salesperson, !salesperson.IsActive));
                    break;
                case SupplierMasterDto supplier:
                    await _api.SaveSupplierAsync(SupplierRequest(supplier, !supplier.IsActive));
                    break;
            }
            await ReloadAsync(PartnerCode(selected));
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, ViewTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { IsEnabled = true; }
    }

    private string? SelectedCode() => _grid.SelectedItem is null ? null : PartnerCode(_grid.SelectedItem);

    private static SaveCustomerMasterRequest CustomerRequest(CustomerMasterDto row, bool active) => new(
        row.Code, row.CompanyName, row.ContactName, row.Address, row.City, row.Phone, row.Email, row.TaxNumber,
        row.CreditLimit, row.CreditDays, row.PriceGroup, row.SalesCode, active, row.Version, AksaraApiClient.Shared.CurrentUserName, Environment.MachineName,
        row.OwnerName, row.Address1, row.Address2, row.District, row.PostalCode, row.Address3, row.Address4,
        row.Mobile1, row.Mobile2, row.Phone2, row.Fax, row.TransactionCondition, row.ContactPhone1, row.ContactPhone2,
        row.ContactYahooId, row.TaxRegistered, row.TaxRegistrationNumber, row.TaxRegisteredDate, row.TaxInvoicePrefix);

    private static SaveSupplierMasterRequest SupplierRequest(SupplierMasterDto row, bool active) => new(
        row.Code, row.CompanyName, row.ContactName, row.Address, row.City, row.Phone, row.Email, row.TaxNumber,
        row.CreditDays, active, row.Version, AksaraApiClient.Shared.CurrentUserName, Environment.MachineName,
        row.OwnerName, row.Address1, row.Address2, row.District, row.PostalCode, row.Address3, row.Address4,
        row.Mobile1, row.Mobile2, row.Phone2, row.Fax, row.ContactPhone1, row.ContactPhone2, row.ContactYahooId,
        row.TaxRegistered, row.TaxRegistrationNumber, row.TaxRegisteredDate);

    private static SaveSalespersonMasterRequest SalespersonRequest(SalespersonMasterDto row, bool active) => new(
        row.Code, row.Name, row.Address, row.District, row.City, row.PostalCode, row.Phone, row.CommissionPercent,
        row.CollectionTarget, row.SalesTarget, row.BaseSalary, row.TargetBonus, active, row.Version,
        AksaraApiClient.Shared.CurrentUserName, Environment.MachineName, row.GroupCode, row.SupervisorCode);

    private static string? PartnerCode(object row) => row switch
    {
        SupplierMasterDto x => x.Code,
        CustomerMasterDto x => x.Code,
        SalespersonMasterDto x => x.Code,
        _ => null
    };

    private static bool PartnerIsActive(object row) => row switch
    {
        SupplierMasterDto x => x.IsActive,
        CustomerMasterDto x => x.IsActive,
        SalespersonMasterDto x => x.IsActive,
        _ => true
    };

    private static void AddHeaderCell(Grid header, UIElement child, int column)
    {
        var border = new Border { BorderBrush = Brush("#7E7E7E"), BorderThickness = new Thickness(1), Child = child };
        Grid.SetColumn(border, column);
        header.Children.Add(border);
    }

    private static Button FooterButton(string text, double width = 94) => new()
    {
        Content = text,
        Width = width,
        Height = 28,
        Margin = new Thickness(3, 0, 3, 0),
        Padding = new Thickness(4, 0, 4, 0)
    };

    private static DataGridTextColumn Column(string header, string property, double width, string? format = null) => new()
    {
        Header = header,
        Binding = new Binding(property) { StringFormat = format is null ? null : "{0:" + format + "}" },
        Width = width
    };

    private static DataGridCheckBoxColumn CheckColumn(string header, string property, double width) => new()
    {
        Header = header,
        Binding = new Binding(property) { Mode = BindingMode.OneWay },
        Width = width
    };

    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
}

internal sealed class BusinessPartnerEditWindow : Window
{
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly BusinessPartnerKind _kind;
    private readonly object? _row;
    private object? _currentRow;
    private long? _expectedVersion;
    private readonly TextBox _code = TextBox(120, upper: true);
    private readonly TextBox _company = TextBox(260);
    private readonly TextBox _contact = TextBox(180);
    private readonly TextBox _address = TextBox(360, multiline: true);
    private readonly TextBox _city = TextBox(160);
    private readonly TextBox _phone = TextBox(150);
    private readonly TextBox _email = TextBox(220);
    private readonly TextBox _tax = TextBox(220);
    private readonly TextBox _creditDays = TextBox(70);
    private readonly TextBox _creditLimit = TextBox(130);
    private readonly TextBox _priceGroup = TextBox(90, upper: true);
    private readonly TextBox _salesCode = TextBox(90, upper: true);
    private readonly TextBox _owner = TextBox(180);
    private readonly TextBox _address1 = TextBox(360);
    private readonly TextBox _address2 = TextBox(360);
    private readonly TextBox _address3 = TextBox(360);
    private readonly TextBox _address4 = TextBox(360);
    private readonly TextBox _district = TextBox(130);
    private readonly TextBox _postalCode = TextBox(80);
    private readonly TextBox _mobile1 = TextBox(130);
    private readonly TextBox _mobile2 = TextBox(130);
    private readonly TextBox _phone2 = TextBox(130);
    private readonly TextBox _fax = TextBox(130);
    private readonly TextBox _transactionCondition = TextBox(80, upper: true);
    private readonly TextBox _contactPhone1 = TextBox(130);
    private readonly TextBox _contactPhone2 = TextBox(130);
    private readonly TextBox _contactYahoo = TextBox(150);
    private readonly CheckBox _taxRegistered = new() { Content = "Pengusaha Kena Pajak (PKP)" };
    private readonly TextBox _taxRegistrationNumber = TextBox(190);
    private readonly DatePicker _taxRegisteredDate = new() { Width = 130, Height = 24 };
    private readonly TextBox _taxInvoicePrefix = TextBox(100);
    private readonly TextBox _salesName = TextBox(180);
    private readonly TextBox _salesAddress = TextBox(360);
    private readonly TextBox _salesDistrict = TextBox(130);
    private readonly TextBox _salesCity = TextBox(160);
    private readonly TextBox _salesPostalCode = TextBox(80);
    private readonly TextBox _salesPhone = TextBox(150);
    private readonly TextBox _commission = TextBox(90);
    private readonly TextBox _collectionTarget = TextBox(130);
    private readonly TextBox _salesTarget = TextBox(130);
    private readonly TextBox _baseSalary = TextBox(130);
    private readonly TextBox _targetBonus = TextBox(130);
    private readonly TextBox _groupCode = TextBox(90, upper: true);
    private readonly TextBox _supervisorCode = TextBox(90, upper: true);
    private readonly CheckBox _active = new() { Content = "Aktif", IsChecked = true };
    private readonly TextBlock _status = new() { Foreground = Brushes.Maroon, TextWrapping = TextWrapping.Wrap };
    private readonly Button _saveButton = FormButton("Simpan");
    private readonly Button _deleteButton = FormButton("Hapus");
    private readonly Button _restoreButton = FormButton("Btl.Hapus");

    public string? SavedCode { get; private set; }

    public BusinessPartnerEditWindow(BusinessPartnerKind kind, object? row)
    {
        _kind = kind;
        _row = row;
        _currentRow = row;
        Title = kind switch
        {
            BusinessPartnerKind.Supplier => "Master Supplier",
            BusinessPartnerKind.Customer => "Master Customer",
            _ => "Master Sales"
        };
        Width = kind switch
        {
            BusinessPartnerKind.Customer => 960,
            BusinessPartnerKind.Salesman => 840,
            _ => 960
        };
        Height = kind switch
        {
            BusinessPartnerKind.Customer => 640,
            BusinessPartnerKind.Salesman => 360,
            _ => 610
        };
        MinWidth = kind == BusinessPartnerKind.Salesman ? 800 : 900;
        MinHeight = kind == BusinessPartnerKind.Salesman ? 340 : 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Tahoma");
        FontSize = 12;
        Content = Build();
        Populate();
        _salesCode.MouseDoubleClick += async (_, _) => await LookupSalesAsync();
        _salesCode.PreviewKeyDown += async (_, args) =>
        {
            if (args.Key == Key.F4)
            {
                args.Handled = true;
                await LookupSalesAsync();
            }
        };
    }

    private UIElement Build()
    {
        return _kind switch
        {
            BusinessPartnerKind.Customer => BuildCustomer(),
            BusinessPartnerKind.Salesman => BuildSalesman(),
            _ => BuildSupplier()
        };
    }

    private UIElement BuildCustomer()
    {
        var root = RootGrid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(HeaderStrip(_row is null ? "TAMBAH DATA" : "EDIT DATA"));

        var body = new StackPanel { Margin = new Thickness(10), Background = Brush("#EFEFEF") };
        var top = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
        var left = new Grid { Margin = new Thickness(0, 0, 8, 0) };
        left.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
        left.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(245) });
        left.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(75) });
        left.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        for (var i = 0; i < 7; i++) left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(29) });
        Add(left, "Kode", _code, 0, 0);
        Add(left, "Nama Pemilik", _owner, 1, 0);
        Add(left, "Toko/PT", _company, 2, 0, span: 3);
        Add(left, "Alamat 1", _address1, 3, 0, span: 3);
        Add(left, "Alamat 2", _address2, 4, 0, span: 3);
        Add(left, "Kecamatan", _district, 5, 0);
        Add(left, "Kota", _city, 6, 0);
        Add(left, "Kode Pos", _postalCode, 6, 2);
        var right = new Grid();
        right.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(75) });
        right.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(165) });
        for (var i = 0; i < 6; i++) right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(29) });
        Add(right, "Hp.(1)", _mobile1, 0, 0);
        Add(right, "Hp.(2)", _mobile2, 1, 0);
        Add(right, "Telp.(1)", _phone, 2, 0);
        Add(right, "Telp.(2)", _phone2, 3, 0);
        Add(right, "Fax", _fax, 4, 0);
        Add(right, "Kode Sales", _salesCode, 5, 0);
        top.Children.Add(left);
        Grid.SetColumn(right, 1);
        top.Children.Add(right);
        body.Children.Add(top);

        var addressRows = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        addressRows.ColumnDefinitions.Add(new ColumnDefinition());
        addressRows.ColumnDefinitions.Add(new ColumnDefinition());
        _address3.Height = 86; _address4.Height = 86;
        addressRows.Children.Add(Group("Alamat 3", ("", _address3)));
        var address4 = Group("Alamat 4", ("", _address4));
        Grid.SetColumn(address4, 1); addressRows.Children.Add(address4);
        body.Children.Add(addressRows);

        var tempoRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        tempoRow.ColumnDefinitions.Add(new ColumnDefinition());
        tempoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        var tempo = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        tempo.Children.Add(new TextBlock { Text = "Tempo Pelunasan", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        tempo.Children.Add(_creditDays);
        Grid.SetColumn(tempo, 1); tempoRow.Children.Add(tempo); body.Children.Add(tempoRow);

        var groups = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        groups.ColumnDefinitions.Add(new ColumnDefinition());
        groups.ColumnDefinitions.Add(new ColumnDefinition());
        var contactGroup = Group("Contact Person", ("Nama", _contact), ("Email", _email), ("YM.ID", _contactYahoo), ("Telp.(1)", _contactPhone1), ("Telp.(2)", _contactPhone2));
        var taxGroup = Group("Informasi Pajak", ("NPWP", _tax), ("NPPKP", _taxRegistrationNumber), ("Tgl. PPKP", _taxRegisteredDate), ("Awln.No.FPS", _taxInvoicePrefix));
        ((StackPanel)taxGroup.Content).Children.Insert(0, _taxRegistered);
        groups.Children.Add(contactGroup); Grid.SetColumn(taxGroup, 1); groups.Children.Add(taxGroup); body.Children.Add(groups);

        var ending = new Grid();
        ending.ColumnDefinitions.Add(new ColumnDefinition());
        ending.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        ending.Children.Add(new TextBlock { Text = "Gunakan Harga Jual & Discount dari Kelompok Harga\nJika Jumlah Pengambilan Brg./Jasa kurang dari batasan kelompok harga ini.", Foreground = Brush("#444"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        var limit = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        limit.Children.Add(new TextBlock { Text = "Plafon Piutang", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        limit.Children.Add(_creditLimit); Grid.SetColumn(limit, 1); ending.Children.Add(limit); body.Children.Add(ending);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = body };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var footer = FormFooter();
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    private UIElement BuildSalesman()
    {
        var root = RootGrid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(HeaderStrip(_row is null ? "TAMBAH DATA" : "EDIT DATA"));

        _code.Width = 150;
        _salesName.Width = 180;
        _salesAddress.Width = 255;
        _salesCity.Width = 150;
        _salesPhone.Width = 180;
        _salesPostalCode.Width = 60;
        _commission.Width = 82;

        var body = new Grid { Margin = new Thickness(10, 12, 10, 10), Background = Brush("#EFEFEF") };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(205) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < 5; i++) body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });

        Add(body, "Kode", _code, 0, 0);
        Add(body, "Komisi", _commission, 0, 4);
        Add(body, "Nama", _salesName, 1, 0, span: 5);
        Add(body, "Alamat", _salesAddress, 2, 0, span: 5);
        Add(body, "Kota", _salesCity, 3, 0);
        Add(body, "K.Pos", _salesPostalCode, 3, 2);
        Add(body, "Telp", _salesPhone, 4, 0, span: 5);

        Grid.SetRow(body, 1);
        root.Children.Add(body);
        var footer = FormFooter();
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    private UIElement BuildSupplier()
    {
        var root = RootGrid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(HeaderStrip(_row is null ? "TAMBAH DATA" : "EDIT DATA"));

        _code.Width = 150;
        _company.Width = 430;
        _owner.Width = 260;
        _address1.Width = 645;
        _address2.Width = 645;
        _address3.Width = 360;
        _address4.Width = 360;
        _address3.Height = 46;
        _address4.Height = 46;
        _address3.AcceptsReturn = true;
        _address4.AcceptsReturn = true;
        _address3.TextWrapping = TextWrapping.Wrap;
        _address4.TextWrapping = TextWrapping.Wrap;

        var body = new StackPanel { Margin = new Thickness(10, 8, 10, 8), Background = Brush("#EFEFEF") };
        var top = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        var left = new Grid { Margin = new Thickness(0, 0, 8, 0) };
        left.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
        left.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(390) });
        left.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        left.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        for (var i = 0; i < 6; i++) left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(29) });
        Add(left, "Kode", _code, 0, 0);
        Add(left, "Nama Pemilik", _owner, 1, 0);
        Add(left, "Toko/PT", _company, 2, 0, span: 3);
        Add(left, "Alamat 1", _address1, 3, 0, span: 3);
        Add(left, "Alamat 2", _address2, 4, 0, span: 3);
        Add(left, "Kota", _city, 5, 0);
        Add(left, "Kode Pos", _postalCode, 5, 2);
        var right = new Grid();
        right.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(75) });
        right.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(165) });
        for (var i = 0; i < 5; i++) right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(29) });
        Add(right, "Hp.(1)", _mobile1, 0, 0);
        Add(right, "Hp.(2)", _mobile2, 1, 0);
        Add(right, "Telp.(1)", _phone, 2, 0);
        Add(right, "Telp.(2)", _phone2, 3, 0);
        Add(right, "Fax", _fax, 4, 0);
        top.Children.Add(left); Grid.SetColumn(right, 1); top.Children.Add(right); body.Children.Add(top);

        var extraAddress = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        extraAddress.ColumnDefinitions.Add(new ColumnDefinition());
        extraAddress.ColumnDefinitions.Add(new ColumnDefinition());
        var address3 = Group("Alamat 3", ("", _address3));
        var address4 = Group("Alamat 4", ("", _address4));
        extraAddress.Children.Add(address3);
        Grid.SetColumn(address4, 1);
        extraAddress.Children.Add(address4);
        body.Children.Add(extraAddress);

        var groups = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        groups.ColumnDefinitions.Add(new ColumnDefinition());
        groups.ColumnDefinitions.Add(new ColumnDefinition());
        var contactGroup = Group("Contact Person", ("Nama", _contact), ("Email", _email), ("YM.ID", _contactYahoo), ("Telp.(1)", _contactPhone1), ("Telp.(2)", _contactPhone2));
        var taxGroup = Group("Informasi Pajak", ("NPWP", _tax), ("NPPKP", _taxRegistrationNumber), ("Tgl. PPKP", _taxRegisteredDate));
        ((StackPanel)taxGroup.Content).Children.Insert(0, _taxRegistered);
        groups.Children.Add(contactGroup);
        Grid.SetColumn(taxGroup, 1);
        groups.Children.Add(taxGroup);
        body.Children.Add(groups);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = body };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        var footer = FormFooter();
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    private UIElement HeaderStrip(string mode)
    {
        var header = new Grid { Height = 38, Background = Brush("#F6F6F6"), Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        AddHeaderCell(header, new CheckBox { Content = "Exclusive Mode", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }, 0);
        AddHeaderCell(header, new TextBlock { Text = "Status:", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }, 1);
        AddHeaderCell(header, new TextBlock { Text = mode, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, 2);
        AddHeaderCell(header, new TextBlock { Text = "TOTAL DATA", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, 3);
        var back = new Button { Content = "|||", FontWeight = FontWeights.Bold, FontSize = 16, Width = 40, Height = 28, Padding = new Thickness(0), ToolTip = "Tutup form" };
        back.Click += (_, _) => Close();
        AddHeaderCell(header, back, 4);
        return header;
    }

    private UIElement FormFooter()
    {
        var footer = new Grid { Margin = new Thickness(8, 0, 8, 8) };
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nav = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var text in new[] { "<<", "<", "-", ">", ">>" })
        {
            var button = FormNavButton(text);
            button.IsEnabled = false;
            nav.Children.Add(button);
        }
        row.Children.Add(nav);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var search = FormButton("Cari");
        var add = FormButton("Tambah");
        var cancel = FormButton("Batal");
        var savePrint = FormButton("Smpn&Ctk");
        var close = FormButton("Tutup");
        search.Click += async (_, _) => await SearchCurrentAsync();
        add.Click += (_, _) => ClearForNew();
        cancel.Click += (_, _) => DialogResult = false;
        _saveButton.Click += async (_, _) => await SaveAsync();
        savePrint.IsEnabled = false;
        savePrint.ToolTip = "Simpan & Cetak tidak digunakan untuk data master";
        _deleteButton.Click += async (_, _) => await ToggleEditorActiveAsync(false);
        _restoreButton.Click += async (_, _) => await ToggleEditorActiveAsync(true);
        close.Click += (_, _) => DialogResult = false;
        foreach (var button in new[] { search, add, cancel, _saveButton, savePrint, _deleteButton, _restoreButton, close }) buttons.Children.Add(button);
        Grid.SetColumn(buttons, 2);
        row.Children.Add(buttons);
        footer.Children.Add(row);
        _status.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(_status, 1);
        footer.Children.Add(_status);
        UpdateActionButtons();
        return footer;
    }

    private void Populate()
    {
        _creditDays.Text = "0";
        _creditLimit.Text = "0";
        _commission.Text = "0,00%";
        _collectionTarget.Text = "0";
        _salesTarget.Text = "0";
        _baseSalary.Text = "0";
        _targetBonus.Text = "0";
        if (_currentRow is SupplierMasterDto supplier)
        {
            _expectedVersion = supplier.Version;
            _code.Text = supplier.Code;
            _company.Text = supplier.CompanyName;
            _contact.Text = supplier.ContactName ?? string.Empty;
            _address.Text = supplier.Address ?? string.Empty;
            _owner.Text = supplier.OwnerName ?? string.Empty;
            _address1.Text = supplier.Address1 ?? string.Empty;
            _address2.Text = supplier.Address2 ?? string.Empty;
            _address3.Text = supplier.Address3 ?? string.Empty;
            _address4.Text = supplier.Address4 ?? string.Empty;
            _district.Text = supplier.District ?? string.Empty;
            _city.Text = supplier.City ?? string.Empty;
            _postalCode.Text = supplier.PostalCode ?? string.Empty;
            _phone.Text = supplier.Phone ?? string.Empty;
            _phone2.Text = supplier.Phone2 ?? string.Empty;
            _mobile1.Text = supplier.Mobile1 ?? string.Empty;
            _mobile2.Text = supplier.Mobile2 ?? string.Empty;
            _fax.Text = supplier.Fax ?? string.Empty;
            _email.Text = supplier.Email ?? string.Empty;
            _tax.Text = supplier.TaxNumber ?? string.Empty;
            _contactPhone1.Text = supplier.ContactPhone1 ?? string.Empty;
            _contactPhone2.Text = supplier.ContactPhone2 ?? string.Empty;
            _contactYahoo.Text = supplier.ContactYahooId ?? string.Empty;
            _taxRegistered.IsChecked = supplier.TaxRegistered;
            _taxRegistrationNumber.Text = supplier.TaxRegistrationNumber ?? string.Empty;
            _taxRegisteredDate.SelectedDate = supplier.TaxRegisteredDate?.ToDateTime(TimeOnly.MinValue);
            _creditDays.Text = supplier.CreditDays.ToString(CultureInfo.CurrentCulture);
            _active.IsChecked = supplier.IsActive;
        }
        else if (_currentRow is CustomerMasterDto customer)
        {
            _expectedVersion = customer.Version;
            _code.Text = customer.Code;
            _company.Text = customer.CompanyName;
            _contact.Text = customer.ContactName ?? string.Empty;
            _owner.Text = customer.OwnerName ?? string.Empty;
            _address1.Text = customer.Address1 ?? string.Empty;
            _address2.Text = customer.Address2 ?? string.Empty;
            _address3.Text = customer.Address3 ?? string.Empty;
            _address4.Text = customer.Address4 ?? string.Empty;
            _district.Text = customer.District ?? string.Empty;
            _city.Text = customer.City ?? string.Empty;
            _postalCode.Text = customer.PostalCode ?? string.Empty;
            _phone.Text = customer.Phone ?? string.Empty;
            _phone2.Text = customer.Phone2 ?? string.Empty;
            _mobile1.Text = customer.Mobile1 ?? string.Empty;
            _mobile2.Text = customer.Mobile2 ?? string.Empty;
            _fax.Text = customer.Fax ?? string.Empty;
            _email.Text = customer.Email ?? string.Empty;
            _tax.Text = customer.TaxNumber ?? string.Empty;
            _creditLimit.Text = customer.CreditLimit.ToString(CultureInfo.CurrentCulture);
            _creditDays.Text = customer.CreditDays.ToString(CultureInfo.CurrentCulture);
            _priceGroup.Text = customer.PriceGroup ?? string.Empty;
            _salesCode.Text = customer.SalesCode ?? string.Empty;
            _transactionCondition.Text = customer.TransactionCondition ?? string.Empty;
            _contactPhone1.Text = customer.ContactPhone1 ?? string.Empty;
            _contactPhone2.Text = customer.ContactPhone2 ?? string.Empty;
            _contactYahoo.Text = customer.ContactYahooId ?? string.Empty;
            _taxRegistered.IsChecked = customer.TaxRegistered;
            _taxRegistrationNumber.Text = customer.TaxRegistrationNumber ?? string.Empty;
            _taxRegisteredDate.SelectedDate = customer.TaxRegisteredDate?.ToDateTime(TimeOnly.MinValue);
            _taxInvoicePrefix.Text = customer.TaxInvoicePrefix ?? string.Empty;
            _active.IsChecked = customer.IsActive;
        }
        else if (_currentRow is SalespersonMasterDto salesperson)
        {
            _expectedVersion = salesperson.Version;
            _code.Text = salesperson.Code;
            _salesName.Text = salesperson.Name;
            _salesAddress.Text = salesperson.Address ?? string.Empty;
            _salesDistrict.Text = salesperson.District ?? string.Empty;
            _salesCity.Text = salesperson.City ?? string.Empty;
            _salesPostalCode.Text = salesperson.PostalCode ?? string.Empty;
            _salesPhone.Text = salesperson.Phone ?? string.Empty;
            _commission.Text = salesperson.CommissionPercent.ToString("N2", CultureInfo.CurrentCulture) + "%";
            _collectionTarget.Text = salesperson.CollectionTarget.ToString(CultureInfo.CurrentCulture);
            _salesTarget.Text = salesperson.SalesTarget.ToString(CultureInfo.CurrentCulture);
            _baseSalary.Text = salesperson.BaseSalary.ToString(CultureInfo.CurrentCulture);
            _targetBonus.Text = salesperson.TargetBonus.ToString(CultureInfo.CurrentCulture);
            _groupCode.Text = salesperson.GroupCode ?? string.Empty;
            _supervisorCode.Text = salesperson.SupervisorCode ?? string.Empty;
            _active.IsChecked = salesperson.IsActive;
        }
        else
        {
            _expectedVersion = null;
        }
        _code.IsReadOnly = _currentRow is not null;
        UpdateActionButtons();
    }

    private async Task SaveAsync()
    {
        try
        {
            IsEnabled = false;
            _status.Text = "Menyimpan...";
            if (_kind == BusinessPartnerKind.Supplier)
            {
                var address = JoinText(_address1.Text, _address2.Text, _address3.Text, _address4.Text);
                var saved = await _api.SaveSupplierAsync(new SaveSupplierMasterRequest(_code.Text, _company.Text, Empty(_contact.Text), Empty(address), Empty(_city.Text), Empty(_phone.Text), Empty(_email.Text), Empty(_tax.Text), ParseInt(_creditDays.Text, "Tempo"), _active.IsChecked == true, _expectedVersion, AksaraApiClient.Shared.CurrentUserName, Environment.MachineName, Empty(_owner.Text), Empty(_address1.Text), Empty(_address2.Text), Empty(_district.Text), Empty(_postalCode.Text), Empty(_address3.Text), Empty(_address4.Text), Empty(_mobile1.Text), Empty(_mobile2.Text), Empty(_phone2.Text), Empty(_fax.Text), Empty(_contactPhone1.Text), Empty(_contactPhone2.Text), Empty(_contactYahoo.Text), _taxRegistered.IsChecked == true, Empty(_taxRegistrationNumber.Text), DateOnlyFromPicker(_taxRegisteredDate)));
                _currentRow = saved;
                _expectedVersion = saved.Version;
                SavedCode = saved.Code;
            }
            else if (_kind == BusinessPartnerKind.Customer)
            {
                var address = JoinText(_address1.Text, _address2.Text, _address3.Text, _address4.Text);
                var saved = await _api.SaveCustomerAsync(new SaveCustomerMasterRequest(_code.Text, _company.Text, Empty(_contact.Text), Empty(address), Empty(_city.Text), Empty(_phone.Text), Empty(_email.Text), Empty(_tax.Text), ParseDecimal(_creditLimit.Text, "Plafon Piutang"), ParseInt(_creditDays.Text, "Tempo Pelunasan"), Empty(_priceGroup.Text), Empty(_salesCode.Text), _active.IsChecked == true, _expectedVersion, AksaraApiClient.Shared.CurrentUserName, Environment.MachineName, Empty(_owner.Text), Empty(_address1.Text), Empty(_address2.Text), Empty(_district.Text), Empty(_postalCode.Text), Empty(_address3.Text), Empty(_address4.Text), Empty(_mobile1.Text), Empty(_mobile2.Text), Empty(_phone2.Text), Empty(_fax.Text), Empty(_transactionCondition.Text), Empty(_contactPhone1.Text), Empty(_contactPhone2.Text), Empty(_contactYahoo.Text), _taxRegistered.IsChecked == true, Empty(_taxRegistrationNumber.Text), DateOnlyFromPicker(_taxRegisteredDate), Empty(_taxInvoicePrefix.Text)));
                _currentRow = saved;
                _expectedVersion = saved.Version;
                SavedCode = saved.Code;
            }
            else
            {
                var saved = await _api.SaveSalespersonAsync(new SaveSalespersonMasterRequest(_code.Text, _salesName.Text, Empty(_salesAddress.Text), Empty(_salesDistrict.Text), Empty(_salesCity.Text), Empty(_salesPostalCode.Text), Empty(_salesPhone.Text), ParseDecimal(_commission.Text, "Komisi"), ParseDecimal(_collectionTarget.Text, "Target Tghn."), ParseDecimal(_salesTarget.Text, "Target Omset"), ParseDecimal(_baseSalary.Text, "Gj.Pokok"), ParseDecimal(_targetBonus.Text, "Bns.Target"), _active.IsChecked == true, _expectedVersion, AksaraApiClient.Shared.CurrentUserName, Environment.MachineName, Empty(_groupCode.Text), Empty(_supervisorCode.Text)));
                _currentRow = saved;
                _expectedVersion = saved.Version;
                SavedCode = saved.Code;
            }
            DialogResult = true;
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            MessageBox.Show(exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { IsEnabled = true; }
    }

    private async Task LookupSalesAsync()
    {
        var lookup = new SalespersonLookupWindow { Owner = this };
        if (lookup.ShowDialog() == true && lookup.SelectedSalesperson is not null)
            _salesCode.Text = lookup.SelectedSalesperson.Code;
        await Task.CompletedTask;
    }

    private void ClearForNew()
    {
        foreach (var box in AllTextBoxes()) box.Clear();
        _active.IsChecked = true;
        _taxRegistered.IsChecked = false;
        _taxRegisteredDate.SelectedDate = null;
        _currentRow = null;
        _expectedVersion = null;
        _creditDays.Text = "0";
        _creditLimit.Text = "0";
        _commission.Text = "0,00%";
        _collectionTarget.Text = "0";
        _salesTarget.Text = "0";
        _baseSalary.Text = "0";
        _targetBonus.Text = "0";
        _code.IsReadOnly = false;
        UpdateActionButtons();
        _code.Focus();
    }

    private async Task SearchCurrentAsync()
    {
        var prompt = new TextPromptWindow("Cari", $"Cari {Title}", _code.Text) { Owner = this };
        if (prompt.ShowDialog() != true) return;
        var query = prompt.Value.Trim();
        if (string.IsNullOrWhiteSpace(query)) return;

        try
        {
            IsEnabled = false;
            _status.Text = "Mencari data...";
            object? found = _kind switch
            {
                BusinessPartnerKind.Supplier => Pick(await _api.GetSuppliersAsync(query, true), query),
                BusinessPartnerKind.Customer => Pick(await _api.GetCustomersAsync(query, true), query),
                _ => Pick(await _api.GetSalespeopleAsync(query, true), query)
            };

            if (found is null)
            {
                MessageBox.Show("Data tidak ditemukan.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ClearInputs();
            _currentRow = found;
            Populate();
            _status.Text = $"Data {query} dimuat.";
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            MessageBox.Show(exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { IsEnabled = true; }
    }

    private async Task ToggleEditorActiveAsync(bool active)
    {
        if (_currentRow is null)
        {
            MessageBox.Show("Data harus disimpan terlebih dahulu.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (CurrentRowIsActive() == active) return;

        var question = active
            ? "Apakah anda ingin membatalkan hapus data?"
            : "Apakah anda ingin menandai hapus data ini?";
        if (MessageBox.Show(question, Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _active.IsChecked = active;
        await SaveAsync();
    }

    private void UpdateActionButtons()
    {
        var hasSavedRow = _currentRow is not null;
        var isActive = CurrentRowIsActive();
        _saveButton.IsEnabled = true;
        _deleteButton.IsEnabled = hasSavedRow && isActive;
        _restoreButton.IsEnabled = hasSavedRow && !isActive;
    }

    private bool CurrentRowIsActive() => _currentRow switch
    {
        SupplierMasterDto supplier => supplier.IsActive,
        CustomerMasterDto customer => customer.IsActive,
        SalespersonMasterDto salesperson => salesperson.IsActive,
        _ => true
    };

    private void ClearInputs()
    {
        foreach (var box in AllTextBoxes()) box.Clear();
        _taxRegistered.IsChecked = false;
        _taxRegisteredDate.SelectedDate = null;
        _active.IsChecked = true;
        _expectedVersion = null;
    }

    private static object? Pick<T>(IEnumerable<T> rows, string query) where T : class
    {
        var list = rows.Cast<object>().ToList();
        return list.FirstOrDefault(x => string.Equals(CodeOf(x), query, StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault();
    }

    private static string? CodeOf(object row) => row switch
    {
        SupplierMasterDto supplier => supplier.Code,
        CustomerMasterDto customer => customer.Code,
        SalespersonMasterDto salesperson => salesperson.Code,
        _ => null
    };

    private IEnumerable<TextBox> AllTextBoxes() => new[]
    {
        _code, _company, _contact, _address, _city, _phone, _email, _tax, _creditDays, _creditLimit,
        _priceGroup, _salesCode, _owner, _address1, _address2, _address3, _address4, _district,
        _postalCode, _mobile1, _mobile2, _phone2, _fax, _transactionCondition, _contactPhone1,
        _contactPhone2, _contactYahoo, _taxRegistrationNumber, _taxInvoicePrefix, _salesName,
        _salesAddress, _salesDistrict, _salesCity, _salesPostalCode, _salesPhone, _commission,
        _collectionTarget, _salesTarget, _baseSalary, _targetBonus, _groupCode, _supervisorCode
    };

    private static Grid RootGrid() => new() { Margin = new Thickness(8), Background = Brush("#EFEFEF") };

    private static GroupBox Group(string title, params (string Label, Control Control)[] fields)
    {
        var panel = new StackPanel { Margin = new Thickness(8, 4, 8, 8) };
        foreach (var (label, control) in fields)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = label, Width = string.IsNullOrWhiteSpace(label) ? 0 : 84, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(control);
            panel.Children.Add(row);
        }
        return new GroupBox { Header = title, Content = panel, Margin = new Thickness(4) };
    }

    private static void Add(Grid grid, string label, Control control, int row, int column, int span = 1)
    {
        var labelBlock = new TextBlock { Text = label, Margin = new Thickness(2, 4, 8, 4), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(labelBlock, row);
        Grid.SetColumn(labelBlock, column);
        grid.Children.Add(labelBlock);
        control.Margin = new Thickness(0, 3, 8, 3);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column + 1);
        Grid.SetColumnSpan(control, span);
        grid.Children.Add(control);
    }

    private static void AddControl(Grid grid, FrameworkElement control, int row, int column)
    {
        control.Margin = new Thickness(0, 4, 8, 4);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }

    private static void AddHeaderCell(Grid header, UIElement child, int column)
    {
        var border = new Border { BorderBrush = Brush("#7E7E7E"), BorderThickness = new Thickness(1), Child = child };
        Grid.SetColumn(border, column);
        header.Children.Add(border);
    }

    private static Button FormButton(string text) => new() { Content = text, Width = 66, Height = 28, Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(1, 0, 1, 0) };
    private static Button FormNavButton(string text) => new() { Content = text, Width = 30, Height = 28, Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(1, 0, 1, 0), FontWeight = FontWeights.Bold };

    private static TextBox TextBox(double width, bool upper = false, bool multiline = false) => new()
    {
        Width = width,
        Height = multiline ? 52 : 24,
        HorizontalAlignment = HorizontalAlignment.Left,
        CharacterCasing = upper ? CharacterCasing.Upper : CharacterCasing.Normal,
        AcceptsReturn = multiline,
        TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
        VerticalContentAlignment = VerticalAlignment.Center
    };

    private static int ParseInt(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value)) return value;
        throw new InvalidOperationException($"{label} harus berupa angka.");
    }

    private static decimal ParseDecimal(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var cleaned = text.Trim().TrimEnd('%').Trim();
        if (decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.CurrentCulture, out var value)) return value;
        if (decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out value)) return value;
        throw new InvalidOperationException($"{label} harus berupa angka.");
    }

    private static DateOnly? DateOnlyFromPicker(DatePicker picker) => picker.SelectedDate is null ? null : DateOnly.FromDateTime(picker.SelectedDate.Value);
    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string JoinText(params string[] values) => string.Join(", ", values.Select(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)));
    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
}

internal sealed class TextPromptWindow : Window
{
    private readonly TextBox _value = new() { Width = 320, Height = 24 };
    public string Value => _value.Text;

    public TextPromptWindow(string title, string label, string currentValue)
    {
        Title = title;
        Width = 430;
        Height = 150;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Tahoma");
        FontSize = 12;
        _value.Text = currentValue;

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
        Grid.SetRow(_value, 1);
        root.Children.Add(_value);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(4, 0, 0, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true, Margin = new Thickness(4, 0, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => { _value.SelectAll(); _value.Focus(); };
    }
}
