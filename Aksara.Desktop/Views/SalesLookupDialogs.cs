using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Aksara.Contracts;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

internal abstract class SalesLookupWindowBase<T> : Window where T : class
{
    protected readonly TextBox SearchBox = new() { MinWidth = 240, Margin = new Thickness(0, 0, 6, 0) };
    protected readonly DataGrid Grid = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        CanUserAddRows = false,
        SelectionMode = DataGridSelectionMode.Single,
        Margin = new Thickness(8, 0, 8, 8)
    };
    protected readonly TextBlock StatusText = new() { VerticalAlignment = VerticalAlignment.Center };
    protected readonly AksaraApiClient Api = AksaraApiClient.Shared;
    protected readonly StackPanel ActionButtons = new() { Orientation = Orientation.Horizontal };

    protected SalesLookupWindowBase(string title, double width, double height)
    {
        Title = title;
        Width = width;
        Height = height;
        MinWidth = width * .75;
        MinHeight = height * .7;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new Grid { Margin = new Thickness(4) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var searchPanel = new DockPanel { Margin = new Thickness(8) };
        searchPanel.Children.Add(new TextBlock { Text = "Cari", Width = 45, VerticalAlignment = VerticalAlignment.Center });
        var searchButton = new Button { Content = "Cari", Width = 75, Margin = new Thickness(6, 0, 0, 0) };
        searchButton.Click += async (_, _) => await ReloadAsync();
        DockPanel.SetDock(searchButton, Dock.Right);
        searchPanel.Children.Add(searchButton);
        searchPanel.Children.Add(SearchBox);
        System.Windows.Controls.Grid.SetRow(searchPanel, 0);
        root.Children.Add(searchPanel);

        System.Windows.Controls.Grid.SetRow(Grid, 1);
        root.Children.Add(Grid);

        var footer = new Grid { Margin = new Thickness(8, 0, 8, 8) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(StatusText);
        var selectButton = new Button { Content = "Pilih", Width = 82, Margin = new Thickness(6, 0, 0, 0) };
        selectButton.Click += (_, _) => AcceptSelection();
        var cancelButton = new Button { Content = "Batal", Width = 82, Margin = new Thickness(6, 0, 0, 0) };
        cancelButton.Click += (_, _) => DialogResult = false;
        ActionButtons.Children.Add(selectButton);
        ActionButtons.Children.Add(cancelButton);
        System.Windows.Controls.Grid.SetColumn(ActionButtons, 1);
        footer.Children.Add(ActionButtons);
        System.Windows.Controls.Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        SearchBox.KeyDown += async (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; await ReloadAsync(); } };
        Grid.MouseDoubleClick += (_, _) => AcceptSelection();
        Grid.KeyDown += (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; AcceptSelection(); } };
        Loaded += async (_, _) => { SearchBox.Focus(); await ReloadAsync(); };
    }

    protected abstract Task<IReadOnlyList<T>> LoadAsync(string query);
    protected abstract void SetSelected(T selected);

    protected async Task ReloadAsync()
    {
        try
        {
            IsEnabled = false;
            StatusText.Text = "Mengambil data dari server...";
            var items = await LoadAsync(SearchBox.Text);
            Grid.ItemsSource = items;
            if (items.Count > 0) Grid.SelectedIndex = 0;
            StatusText.Text = $"{items.Count:N0} data ditemukan";
        }
        catch (Exception exception)
        {
            StatusText.Text = "Gagal mengambil data";
            MessageBox.Show(exception.Message, "AKSARA", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { IsEnabled = true; SearchBox.Focus(); }
    }

    protected void AddLookupAction(string caption, Func<Task> action, double width = 92)
    {
        var button = new Button { Content = caption, Width = width, Margin = new Thickness(6, 0, 0, 0) };
        button.Click += async (_, _) => await action();
        ActionButtons.Children.Insert(Math.Max(0, ActionButtons.Children.Count - 2), button);
    }

    private void AcceptSelection()
    {
        if (Grid.SelectedItem is not T selected) return;
        SetSelected(selected);
        DialogResult = true;
    }

    protected static DataGridTextColumn Column(string header, string property, double width) => new()
    {
        Header = header,
        Binding = new Binding(property),
        Width = width
    };
}

internal sealed class CustomerLookupWindow : SalesLookupWindowBase<SalesCustomerDto>
{
    public SalesCustomerDto? SelectedCustomer { get; private set; }

    public CustomerLookupWindow() : base("FORM CARI CUSTOMER", 780, 500)
    {
        Grid.Columns.Add(Column("Kode", nameof(SalesCustomerDto.Code), 110));
        Grid.Columns.Add(Column("Toko/PT", nameof(SalesCustomerDto.CompanyName), 240));
        Grid.Columns.Add(Column("Alamat", nameof(SalesCustomerDto.Address), 280));
        Grid.Columns.Add(Column("Tempo", nameof(SalesCustomerDto.CreditDays), 65));
        AddLookupAction("Tambah", () => OpenEditorAsync(null));
        AddLookupAction("Ubah", EditSelectedAsync);
    }

    protected override Task<IReadOnlyList<SalesCustomerDto>> LoadAsync(string query) => Api.SearchCustomersAsync(query);
    protected override void SetSelected(SalesCustomerDto selected) => SelectedCustomer = selected;

    private async Task EditSelectedAsync()
    {
        if (Grid.SelectedItem is not SalesCustomerDto selected) return;
        var row = (await Api.GetCustomersAsync(selected.Code, true)).FirstOrDefault(x => string.Equals(x.Code, selected.Code, StringComparison.OrdinalIgnoreCase));
        await OpenEditorAsync(row);
    }

    private async Task OpenEditorAsync(CustomerMasterDto? row)
    {
        var editor = new BusinessPartnerEditWindow(BusinessPartnerKind.Customer, row) { Owner = this };
        if (editor.ShowDialog() != true) return;
        SearchBox.Text = editor.SavedCode ?? SearchBox.Text;
        await ReloadAsync();
    }
}

internal sealed class ItemLookupWindow(string warehouseCode, string? initialQuery = null, bool serviceMode = false) : SalesLookupWindowBase<SalesItemDto>(serviceMode ? "FORM CARI JASA" : "FORM CARI BARANG", 860, 540)
{
    public SalesItemDto? SelectedItem { get; private set; }

    protected override Task<IReadOnlyList<SalesItemDto>> LoadAsync(string query) => Api.SearchItemsAsync(query, warehouseCode, itemType: serviceMode ? "SERVICE" : "GOODS");
    protected override void SetSelected(SalesItemDto selected) => SelectedItem = selected;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        SearchBox.Text = initialQuery ?? string.Empty;
        Grid.Columns.Add(Column(serviceMode ? "Kode Jasa" : "Kode Barang", nameof(SalesItemDto.Code), 130));
        Grid.Columns.Add(Column(serviceMode ? "Nama Jasa" : "Nama Barang", nameof(SalesItemDto.Name), 310));
        if (!serviceMode) Grid.Columns.Add(Column("Stok", nameof(SalesItemDto.AvailableStock), 90));
        Grid.Columns.Add(Column("Sat.", nameof(SalesItemDto.Unit), 70));
        Grid.Columns.Add(Column("Harga Jual", nameof(SalesItemDto.RetailPrice), 110));
        Grid.Columns.Add(Column("Gudang", nameof(SalesItemDto.WarehouseCode), 90));
        AddLookupAction(serviceMode ? "Tambah Jasa" : "Tambah Barang", OpenItemEditorAsync, 108);
    }

    private async Task OpenItemEditorAsync()
    {
        var editor = new ItemDetailWindow(serviceMode: serviceMode) { Owner = this };
        if (editor.ShowDialog() == true) await ReloadAsync();
    }
}

internal sealed class WarehouseLookupWindow : SalesLookupWindowBase<InventoryWarehouseDto>
{
    public InventoryWarehouseDto? SelectedWarehouse { get; private set; }

    public WarehouseLookupWindow() : base("FORM CARI LOKASI STOK / GUDANG", 570, 430)
    {
        Grid.Columns.Add(Column("Kode", nameof(InventoryWarehouseDto.Code), 130));
        Grid.Columns.Add(Column("Nama Lokasi / Gudang", nameof(InventoryWarehouseDto.Name), 290));
        Grid.Columns.Add(Column("Default", nameof(InventoryWarehouseDto.IsDefault), 75));
        AddLookupAction("Daftar Gudang", OpenWarehouseMasterAsync, 110);
    }

    protected override async Task<IReadOnlyList<InventoryWarehouseDto>> LoadAsync(string query)
    {
        var rows = await Api.GetInventoryWarehousesAsync(false);
        if (string.IsNullOrWhiteSpace(query)) return rows;
        return rows.Where(x => x.Code.Contains(query, StringComparison.OrdinalIgnoreCase)
            || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    protected override void SetSelected(InventoryWarehouseDto selected) => SelectedWarehouse = selected;

    private async Task OpenWarehouseMasterAsync()
    {
        var view = new InventoryMasterView(InventoryMasterKind.Warehouses);
        var window = new Window
        {
            Title = view.ViewTitle,
            Width = 760,
            Height = 520,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = view,
            ShowInTaskbar = false
        };
        view.CloseRequested += (_, _) => window.Close();
        window.ShowDialog();
        await ReloadAsync();
    }
}

internal sealed class CashAccountLookupWindow : Window
{
    private readonly TextBox _codeBox = new() { Width = 230, Height = 22, Margin = new Thickness(4, 2, 8, 2), CharacterCasing = CharacterCasing.Upper };
    private readonly TextBox _nameBox = new() { Width = 230, Height = 22, Margin = new Thickness(4, 2, 8, 2), CharacterCasing = CharacterCasing.Upper };
    private readonly RadioButton _orderByCode = new() { Content = "Kode", IsChecked = true, Margin = new Thickness(5, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly RadioButton _orderByName = new() { Content = "Nama", Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        CanUserAddRows = false,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.All,
        SelectionMode = DataGridSelectionMode.Single,
        Background = new SolidColorBrush(Color.FromRgb(214, 237, 249)),
        RowBackground = new SolidColorBrush(Color.FromRgb(214, 237, 249)),
        AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(214, 237, 249)),
        BorderThickness = new Thickness(0),
        RowHeight = 24,
        ColumnHeaderHeight = 28
    };

    public CashAccountDto? SelectedCashAccount { get; private set; }

    public CashAccountLookupWindow()
    {
        Title = "FORM CARI KAS";
        Width = 530;
        Height = 720;
        MinWidth = 430;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Tahoma");
        FontSize = 12;

        var rowStyle = new Style(typeof(DataGridRow));
        rowStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(214, 237, 249))));
        rowStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
        var selectedTrigger = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(72, 0, 142))));
        selectedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        rowStyle.Triggers.Add(selectedTrigger);
        _grid.RowStyle = rowStyle;

        var root = new Grid { Background = Brushes.White };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Background = Brushes.Black, Margin = new Thickness(4, 4, 4, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.Children.Add(HeaderLabel("Kode", 0));
        header.Children.Add(HeaderLabel("Nama", 1));
        System.Windows.Controls.Grid.SetColumn(_codeBox, 1);
        System.Windows.Controls.Grid.SetColumn(_nameBox, 1);
        System.Windows.Controls.Grid.SetRow(_nameBox, 1);
        header.Children.Add(_codeBox);
        header.Children.Add(_nameBox);
        System.Windows.Controls.Grid.SetRow(header, 0);
        root.Children.Add(header);

        _grid.Columns.Add(Column("Kode", nameof(CashAccountDto.Code), 150));
        _grid.Columns.Add(Column("Nama", nameof(CashAccountDto.Name), 310));
        _grid.SelectionChanged += (_, _) =>
        {
            if (_grid.SelectedItem is not CashAccountDto selected) return;
            _codeBox.Text = selected.Code;
            _nameBox.Text = selected.Name;
        };
        _grid.MouseDoubleClick += (_, _) => AcceptSelection();
        _grid.KeyDown += (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; AcceptSelection(); } };
        System.Windows.Controls.Grid.SetRow(_grid, 1);
        root.Children.Add(_grid);

        var footer = new DockPanel { Margin = new Thickness(6, 3, 6, 6) };
        footer.Children.Add(new TextBlock { Text = "Urutan", VerticalAlignment = VerticalAlignment.Center, TextDecorations = TextDecorations.Underline });
        footer.Children.Add(_orderByCode);
        footer.Children.Add(_orderByName);
        var reload = new Button { Content = "R", Width = 24, Height = 24, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Muat ulang" };
        reload.Click += async (_, _) => await ReloadAsync();
        var search = new Button { Content = "S", Width = 24, Height = 24, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Cari" };
        search.Click += async (_, _) => await ReloadAsync();
        DockPanel.SetDock(reload, Dock.Right);
        DockPanel.SetDock(search, Dock.Right);
        footer.Children.Add(reload);
        footer.Children.Add(search);
        System.Windows.Controls.Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        _codeBox.KeyDown += async (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; await ReloadAsync(); } };
        _nameBox.KeyDown += async (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; await ReloadAsync(); } };
        Loaded += async (_, _) => { _codeBox.Focus(); await ReloadAsync(); };
    }

    private async Task ReloadAsync()
    {
        var code = _codeBox.Text.Trim();
        var name = _nameBox.Text.Trim();
        try
        {
            IsEnabled = false;
            var rows = await AksaraApiClient.Shared.GetCashAccountsAsync(false);
            var filtered = rows
                .Where(x => (string.IsNullOrWhiteSpace(code) || x.Code.Contains(code, StringComparison.OrdinalIgnoreCase))
                    && (string.IsNullOrWhiteSpace(name) || x.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(x => _orderByName.IsChecked == true ? x.Name : x.Code, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _grid.ItemsSource = filtered;
            if (filtered.Count > 0) _grid.SelectedIndex = 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "AKSARA - Form Cari Kas", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsEnabled = true;
            _codeBox.Focus();
        }
    }

    private void AcceptSelection()
    {
        if (_grid.SelectedItem is not CashAccountDto selected) return;
        SelectedCashAccount = selected;
        DialogResult = true;
    }

    private static TextBlock HeaderLabel(string text, int row)
    {
        var label = new TextBlock { Text = text, Foreground = Brushes.White, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Controls.Grid.SetRow(label, row);
        return label;
    }

    private static DataGridTextColumn Column(string header, string property, double width) => new()
    {
        Header = header,
        Binding = new Binding(property),
        Width = width
    };
}

internal sealed class SalespersonLookupWindow : Window
{
    private readonly TextBox _codeBox = new() { Width = 230, Height = 22, Margin = new Thickness(4, 2, 8, 2), CharacterCasing = CharacterCasing.Upper };
    private readonly TextBox _nameBox = new() { Width = 230, Height = 22, Margin = new Thickness(4, 2, 8, 2), CharacterCasing = CharacterCasing.Upper };
    private readonly RadioButton _orderByCode = new() { Content = "Kode", IsChecked = true, Margin = new Thickness(5, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly RadioButton _orderByName = new() { Content = "Nama", Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        CanUserAddRows = false,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.All,
        SelectionMode = DataGridSelectionMode.Single,
        Background = new SolidColorBrush(Color.FromRgb(214, 237, 249)),
        RowBackground = new SolidColorBrush(Color.FromRgb(214, 237, 249)),
        AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(214, 237, 249)),
        BorderThickness = new Thickness(0),
        RowHeight = 24,
        ColumnHeaderHeight = 28
    };

    public SalespersonDto? SelectedSalesperson { get; private set; }

    public SalespersonLookupWindow()
    {
        Title = "FORM CARI SALES";
        Width = 530;
        Height = 720;
        MinWidth = 430;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Tahoma");
        FontSize = 12;

        var rowStyle = new Style(typeof(DataGridRow));
        rowStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(214, 237, 249))));
        rowStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
        var selectedTrigger = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(72, 0, 142))));
        selectedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        rowStyle.Triggers.Add(selectedTrigger);
        _grid.RowStyle = rowStyle;

        var root = new Grid { Background = Brushes.White };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Background = Brushes.Black, Margin = new Thickness(4, 4, 4, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.Children.Add(HeaderLabel("Kode", 0));
        header.Children.Add(HeaderLabel("Nama", 1));
        System.Windows.Controls.Grid.SetColumn(_codeBox, 1);
        System.Windows.Controls.Grid.SetColumn(_nameBox, 1);
        System.Windows.Controls.Grid.SetRow(_nameBox, 1);
        header.Children.Add(_codeBox);
        header.Children.Add(_nameBox);
        System.Windows.Controls.Grid.SetRow(header, 0);
        root.Children.Add(header);

        _grid.Columns.Add(Column("Kode", nameof(SalespersonDto.Code), 150));
        _grid.Columns.Add(Column("Nama", nameof(SalespersonDto.Name), 310));
        _grid.SelectionChanged += (_, _) =>
        {
            if (_grid.SelectedItem is not SalespersonDto selected) return;
            _codeBox.Text = selected.Code;
            _nameBox.Text = selected.Name;
        };
        _grid.MouseDoubleClick += (_, _) => AcceptSelection();
        _grid.KeyDown += (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; AcceptSelection(); } };
        System.Windows.Controls.Grid.SetRow(_grid, 1);
        root.Children.Add(_grid);

        var footer = new DockPanel { Margin = new Thickness(6, 3, 6, 6) };
        footer.Children.Add(new TextBlock { Text = "Urutan", VerticalAlignment = VerticalAlignment.Center, TextDecorations = TextDecorations.Underline });
        footer.Children.Add(_orderByCode);
        footer.Children.Add(_orderByName);
        var reload = new Button { Content = "R", Width = 24, Height = 24, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Muat ulang" };
        reload.Click += async (_, _) => await ReloadAsync();
        var search = new Button { Content = "S", Width = 24, Height = 24, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Cari" };
        search.Click += async (_, _) => await ReloadAsync();
        DockPanel.SetDock(reload, Dock.Right);
        DockPanel.SetDock(search, Dock.Right);
        footer.Children.Add(reload);
        footer.Children.Add(search);
        System.Windows.Controls.Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        _codeBox.KeyDown += async (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; await ReloadAsync(); } };
        _nameBox.KeyDown += async (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; await ReloadAsync(); } };
        Loaded += async (_, _) => { _codeBox.Focus(); await ReloadAsync(); };
    }

    private async Task ReloadAsync()
    {
        var code = _codeBox.Text.Trim();
        var name = _nameBox.Text.Trim();
        try
        {
            IsEnabled = false;
            var rows = await AksaraApiClient.Shared.SearchSalespeopleAsync(string.Empty);
            var filtered = rows
                .Where(x => (string.IsNullOrWhiteSpace(code) || x.Code.Contains(code, StringComparison.OrdinalIgnoreCase))
                    && (string.IsNullOrWhiteSpace(name) || x.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(x => _orderByName.IsChecked == true ? x.Name : x.Code, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _grid.ItemsSource = filtered;
            if (filtered.Count > 0) _grid.SelectedIndex = 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "AKSARA - Form Cari Sales", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsEnabled = true;
            _codeBox.Focus();
        }
    }

    private void AcceptSelection()
    {
        if (_grid.SelectedItem is not SalespersonDto selected) return;
        SelectedSalesperson = selected;
        DialogResult = true;
    }

    private static TextBlock HeaderLabel(string text, int row)
    {
        var label = new TextBlock { Text = text, Foreground = Brushes.White, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Controls.Grid.SetRow(label, row);
        return label;
    }

    private static DataGridTextColumn Column(string header, string property, double width) => new()
    {
        Header = header,
        Binding = new Binding(property),
        Width = width
    };
}

internal sealed class SaleLookupWindow : SalesLookupWindowBase<SaleDto>
{
    private readonly bool _serviceMode;
    public SaleDto? SelectedSale { get; private set; }

    public SaleLookupWindow(bool serviceMode = false) : base(serviceMode ? "FORM CARI PENJUALAN JASA" : "FORM CARI PENJUALAN", 900, 540)
    {
        _serviceMode = serviceMode;
        Grid.Columns.Add(Column("No. Nota", nameof(SaleDto.DocumentNumber), 140));
        Grid.Columns.Add(Column("Tanggal", nameof(SaleDto.DocumentDate), 100));
        Grid.Columns.Add(Column("Kode Cust.", nameof(SaleDto.CustomerCode), 110));
        Grid.Columns.Add(Column("Toko/PT", nameof(SaleDto.CustomerName), 220));
        Grid.Columns.Add(Column("Total", nameof(SaleDto.GrandTotal), 115));
        Grid.Columns.Add(Column("Status", nameof(SaleDto.Status), 100));
    }

    protected override Task<IReadOnlyList<SaleDto>> LoadAsync(string query) => Api.SearchSalesAsync(query, documentKind: _serviceMode ? "SERVICE" : "GOODS");
    protected override void SetSelected(SaleDto selected) => SelectedSale = selected;
}

internal sealed class SaleReturnLookupWindow : SalesLookupWindowBase<SaleReturnDto>
{
    public SaleReturnDto? SelectedReturn { get; private set; }

    public SaleReturnLookupWindow() : base("FORM CARI RETUR PENJUALAN", 920, 540)
    {
        Grid.Columns.Add(Column("No. Retur", nameof(SaleReturnDto.DocumentNumber), 130));
        Grid.Columns.Add(Column("Tanggal", nameof(SaleReturnDto.DocumentDate), 100));
        Grid.Columns.Add(Column("No. Nota", nameof(SaleReturnDto.SourceDocumentNumber), 130));
        Grid.Columns.Add(Column("Kode Cust.", nameof(SaleReturnDto.CustomerCode), 100));
        Grid.Columns.Add(Column("Toko/PT", nameof(SaleReturnDto.CustomerName), 220));
        Grid.Columns.Add(Column("Total", nameof(SaleReturnDto.GrandTotal), 110));
        Grid.Columns.Add(Column("Status", nameof(SaleReturnDto.Status), 90));
    }

    protected override Task<IReadOnlyList<SaleReturnDto>> LoadAsync(string query) => Api.SearchSaleReturnsAsync(query);
    protected override void SetSelected(SaleReturnDto selected) => SelectedReturn = selected;
}

internal sealed class TextInputDialog : Window
{
    private readonly TextBox _textBox = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70 };
    public string Value => _textBox.Text.Trim();

    public TextInputDialog(string title, string prompt)
    {
        Title = title;
        Width = 430;
        Height = 220;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) });
        System.Windows.Controls.Grid.SetRow(_textBox, 1);
        root.Children.Add(_textBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var ok = new Button { Content = "OK", Width = 80 };
        ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(Value)) DialogResult = true; };
        var cancel = new Button { Content = "Batal", Width = 80, Margin = new Thickness(6, 0, 0, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        System.Windows.Controls.Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => _textBox.Focus();
    }
}
