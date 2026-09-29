using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Aksara.Contracts;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

public enum InventoryMasterKind { Warehouses, AdjustmentTypes, Categories, ServiceCategories }

public sealed class InventoryMasterView : UserControl
{
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly InventoryMasterKind _kind;
    private readonly TextBox _query = new() { MinWidth = 220 };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = false, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single, RowHeight = 25, HeadersVisibility = DataGridHeadersVisibility.Column, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _categorySort = new() { Width = 230, ItemsSource = new[] { "Kode Kel. Barang", "Nama Kel. Barang", "Nom.Poin" }, SelectedIndex = 0 };
    private readonly CheckBox _filterCondition = new() { Content = "Kondisi Filter dijalankan", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private DataGridCheckBoxColumn? _defaultColumn;
    private bool _busy;
    private bool IsCategoryMaster => _kind is InventoryMasterKind.Categories or InventoryMasterKind.ServiceCategories;
    private bool IsServiceCategory => _kind == InventoryMasterKind.ServiceCategories;

    public event EventHandler? CloseRequested;
    public string ViewTitle => _kind switch
    {
        InventoryMasterKind.Warehouses => "Daftar Lokasi Stok / Gudang Barang",
        InventoryMasterKind.AdjustmentTypes => "Daftar Jenis Penyesuaian Persediaan",
        InventoryMasterKind.ServiceCategories => "Daftar Kelompok Jasa",
        _ => "Daftar Kelompok Barang"
    };

    public InventoryMasterView(InventoryMasterKind kind)
    {
        _kind = kind;
        if (IsServiceCategory)
            _categorySort.ItemsSource = new[] { "Kode Kel. Jasa", "Nama Kel. Jasa", "Jumlah Jasa" };
        ConfigureColumns();
        Content = Build();
        Loaded += async (_, _) => await ReloadAsync();
        _grid.MouseDoubleClick += async (_, _) => await EditAsync();
        _grid.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; await EditAsync(); } };
        _grid.CellEditEnding += Grid_CellEditEnding;
    }

    private UIElement Build() => IsCategoryMaster ? BuildCategoryList() : BuildGeneralList();

    private UIElement BuildCategoryList()
    {
        _grid.Margin = new Thickness(0);
        _grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        var root = new Grid { Margin = new Thickness(6), Background = Brush("#F0F0F0") };
        for (var i = 0; i < 5; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

        var header = new Grid { Height = 43, Background = Brush("#ECECEC"), Margin = new Thickness(0, 0, 0, 1) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(205) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(155) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
        header.Children.Add(new CheckBox { Content = "Exclusive Mode", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
        var status = new TextBlock { Text = "Status:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) }; Grid.SetColumn(status, 1); header.Children.Add(status);
        var mode = new TextBlock { Text = "TAMBAH DATA", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(mode, 2); header.Children.Add(mode);
        var total = new TextBlock { Text = "TOTAL DATA", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(total, 3); header.Children.Add(total);
        _count.FontWeight = FontWeights.Bold; _count.HorizontalAlignment = HorizontalAlignment.Right; _count.Margin = new Thickness(0, 0, 8, 0); Grid.SetColumn(_count, 4); header.Children.Add(_count);
        var back = new Button { Content = "|||", Width = 42, Height = 30, Margin = new Thickness(8, 6, 8, 6), Padding = new Thickness(0), ToolTip = "Kembali ke tampilan awal" }; back.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty); Grid.SetColumn(back, 5); header.Children.Add(back);
        root.Children.Add(header);

        Grid.SetRow(_grid, 1); root.Children.Add(_grid);

        var statusBar = new Border { Background = Brush("#F7F7F7"), BorderBrush = Brush("#B5B5B5"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(8, 4, 8, 4), Child = _status }; Grid.SetRow(statusBar, 2); root.Children.Add(statusBar);

        var filter = new Grid { Background = Brush("#666666"), Margin = new Thickness(0, 4, 0, 0) };
        filter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); filter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); filter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); filter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); filter.ColumnDefinitions.Add(new ColumnDefinition()); filter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var filterButton = Button("Filter", 90); filterButton.Click += (_, _) => { _query.Visibility = _query.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; if (_query.Visibility == Visibility.Visible) _query.Focus(); }; Grid.SetColumn(filterButton, 0); filter.Children.Add(filterButton);
        _filterCondition.Foreground = Brushes.White; _filterCondition.Margin = new Thickness(8, 0, 12, 0); Grid.SetColumn(_filterCondition, 1); filter.Children.Add(_filterCondition);
        var order = new TextBlock { Text = "Urutan", Foreground = Brushes.White, TextDecorations = TextDecorations.Underline, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }; Grid.SetColumn(order, 2); filter.Children.Add(order);
        Grid.SetColumn(_categorySort, 3); _categorySort.Margin = new Thickness(0, 3, 8, 3); filter.Children.Add(_categorySort);
        var info = Button("Info/Set.", 78); info.Click += (_, _) => MessageBox.Show(IsServiceCategory
            ? "Kelompok Jasa adalah daftar khusus untuk mengelompokkan master jasa. Daftar ini terpisah dari Kelompok Barang."
            : "Kelompok Barang memakai field dan data master dari CPS. Analisa menampilkan jumlah kode barang yang memakai kelompok ini.", "Info/Set.", MessageBoxButton.OK, MessageBoxImage.Information); Grid.SetColumn(info, 5); filter.Children.Add(info);
        Grid.SetRow(filter, 3); root.Children.Add(filter);

        var actions = new Grid { Margin = new Thickness(0, 4, 0, 0) }; actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); for (var i = 1; i < 8; i++) actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        var refresh = Button("Refresh", 88); refresh.Click += async (_, _) => { _query.Clear(); await ReloadAsync(); }; Add(actions, refresh, 1);
        var find = Button("Cari", 88); find.Click += (_, _) => { _query.Visibility = Visibility.Visible; _query.Focus(); }; Add(actions, find, 2);
        var add = Button("Tambah", 88); add.Click += async (_, _) => await OpenEditorAsync(null); Add(actions, add, 3);
        var edit = Button("Edit", 88); edit.Click += async (_, _) => await EditAsync(); Add(actions, edit, 4);
        var toggle = Button("Hps./Btl.", 88); toggle.Click += async (_, _) => await ToggleActiveAsync(); Add(actions, toggle, 5);
        var print = Button("Cetak", 88); print.Click += (_, _) => ReportGridTools.PrintDataGrid(_grid, ViewTitle); Add(actions, print, 6);
        var close = Button("Tutup", 88); close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty); Add(actions, close, 7);
        Grid.SetRow(actions, 4); root.Children.Add(actions);
        _query.Visibility = Visibility.Collapsed;
        return root;
    }

    private UIElement BuildGeneralList()
    {
        var root = new Grid { Margin = new Thickness(6), Background = Brush("#F0F0F0") };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Height = 43, Background = Brush("#ECECEC"), Margin = new Thickness(0, 0, 0, 1) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(205) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(165) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
        header.Children.Add(new CheckBox { Content = "Exclusive Mode", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
        var status = new TextBlock { Text = "Status:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(status, 1); header.Children.Add(status);
        var mode = new TextBlock { Text = "TAMBAH DATA", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(mode, 2); header.Children.Add(mode);
        var back = new Button { Content = "|||", Width = 42, Height = 30, Margin = new Thickness(8, 6, 8, 6), Padding = new Thickness(0), ToolTip = "Kembali ke tampilan awal" };
        back.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty); Grid.SetColumn(back, 3); header.Children.Add(back);
        root.Children.Add(header);

        var tools = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(225) });
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools.ColumnDefinitions.Add(new ColumnDefinition());
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(135) });
        _query.Margin = new Thickness(0, 0, 5, 0); Grid.SetColumn(_query, 0); tools.Children.Add(_query);
        var find = Button("Cari", 82); find.Click += async (_, _) => await ReloadAsync(); Grid.SetColumn(find, 1); tools.Children.Add(find);
        var hint = new TextBlock { Text = "Ketik kode atau nama lalu Cari", Foreground = Brush("#555"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(hint, 2); tools.Children.Add(hint);
        var totalLabel = new TextBlock { Text = "TOTAL DATA", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(totalLabel, 3); tools.Children.Add(totalLabel);
        _count.FontWeight = FontWeights.Bold; Grid.SetColumn(_count, 4); tools.Children.Add(_count);
        Grid.SetRow(tools, 1); root.Children.Add(tools);

        _grid.Margin = new Thickness(0);
        Grid.SetRow(_grid, 2); root.Children.Add(_grid);

        var statusBar = new Border { Background = Brush("#F7F7F7"), BorderBrush = Brush("#B5B5B5"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(8, 4, 8, 4), Child = _status };
        Grid.SetRow(statusBar, 3); root.Children.Add(statusBar);

        var actions = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        for (var i = 0; i < 7; i++) actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(i == 0 ? 1 : 96) });
        var refresh = Button("Refresh", 88); refresh.Click += async (_, _) => { _query.Clear(); await ReloadAsync(); }; Add(actions, refresh, 1);
        var add = Button("Tambah", 88); add.Click += async (_, _) => await OpenEditorAsync(null); Add(actions, add, 2);
        var edit = Button("Edit", 88); edit.Click += async (_, _) => await EditAsync(); Add(actions, edit, 3);
        var toggle = Button("Hps./Btl.", 88); toggle.Click += async (_, _) => await ToggleActiveAsync(); Add(actions, toggle, 4);
        var close = Button("Tutup", 88); close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty); Add(actions, close, 5);
        Grid.SetRow(actions, 4); root.Children.Add(actions);
        return root;
    }

    private void ConfigureColumns()
    {
        if (_kind == InventoryMasterKind.Warehouses)
        {
            _grid.Columns.Add(TextColumn("Kode Lokasi", nameof(InventoryWarehouseDto.Code), 145));
            _grid.Columns.Add(TextColumn("Nama Lokasi", nameof(InventoryWarehouseDto.Name), new DataGridLength(300)));
            _defaultColumn = new DataGridCheckBoxColumn { Header = "Pil.Default", Width = 100, Binding = new Binding(nameof(InventoryWarehouseDto.IsDefault)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged } };
            _grid.Columns.Add(_defaultColumn);
            _grid.Columns.Add(TextColumn("Status", nameof(InventoryWarehouseDto.IsActive), 90));
        }
        else if (IsCategoryMaster)
        {
            _grid.Columns.Add(TextColumn(IsServiceCategory ? "Kode Kel. Jasa" : "Kode Kel. Barang", nameof(InventoryCategoryDto.Code), 145));
            _grid.Columns.Add(TextColumn(IsServiceCategory ? "Nama Kel. Jasa" : "Nama Kel. Barang", nameof(InventoryCategoryDto.Name), 300));
            if (!IsServiceCategory)
            {
                _grid.Columns.Add(NumberColumn("Mov.R-1 Qty", nameof(InventoryCategoryDto.MovR1Qty), 120));
                _grid.Columns.Add(NumberColumn("Mov.R-2 Qty", nameof(InventoryCategoryDto.MovR2Qty), 120));
                _grid.Columns.Add(NumberColumn("Mov.R-3 Qty", nameof(InventoryCategoryDto.MovR3Qty), 120));
                _grid.Columns.Add(NumberColumn("Nom.Poin", nameof(InventoryCategoryDto.PointValue), 110));
            }
            _grid.Columns.Add(TextColumn("Analisa", nameof(InventoryCategoryDto.Analysis), new DataGridLength(1, DataGridLengthUnitType.Star)));
            if (IsServiceCategory)
                _grid.Columns.Add(TextColumn("Status", nameof(InventoryCategoryDto.IsActive), 90));
        }
        else
        {
            _grid.Columns.Add(TextColumn("Kode", nameof(InventoryAdjustmentTypeDto.Code), 150));
            _grid.Columns.Add(TextColumn("Nama", nameof(InventoryAdjustmentTypeDto.Name), new DataGridLength(1, DataGridLengthUnitType.Star)));
            _grid.Columns.Add(TextColumn("Arah Stok", nameof(InventoryAdjustmentTypeDto.Direction), 110));
            _grid.Columns.Add(TextColumn("Nilai/HPP", nameof(InventoryAdjustmentTypeDto.RequiresValue), 100));
            _grid.Columns.Add(TextColumn("Saldo Awal", nameof(InventoryAdjustmentTypeDto.IsOpeningBalance), 100));
            _grid.Columns.Add(TextColumn("Status", nameof(InventoryAdjustmentTypeDto.IsActive), 90));
        }
    }

    private async Task ReloadAsync()
    {
        if (_busy) return;
        try
        {
            _busy = true; IsEnabled = false;
            IEnumerable rows = _kind switch
            {
                InventoryMasterKind.Warehouses => await _api.GetInventoryWarehousesAsync(true),
                InventoryMasterKind.AdjustmentTypes => await _api.GetInventoryAdjustmentTypesAsync(true),
                InventoryMasterKind.ServiceCategories => await _api.GetInventoryCategoriesAsync(true, "SERVICE"),
                _ => await _api.GetInventoryCategoriesAsync(true, "GOODS")
            };
            var query = _query.Text.Trim();
            var filtered = rows.Cast<object>().Where(row => string.IsNullOrWhiteSpace(query) || SearchText(row).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            var selectedCode = CodeOf(_grid.SelectedItem);
            _grid.ItemsSource = filtered;
            _grid.SelectedItem = filtered.FirstOrDefault(row => string.Equals(CodeOf(row), selectedCode, StringComparison.OrdinalIgnoreCase)) ?? filtered.FirstOrDefault();
            _count.Text = filtered.Count.ToString("N0");
            _status.Text = $"{filtered.Count:N0} data | terhubung ke database AKSARA.";
        }
        catch (Exception exception) { _status.Text = exception.Message; }
        finally { _busy = false; IsEnabled = true; }
    }

    private async void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (_kind != InventoryMasterKind.Warehouses || e.EditAction != DataGridEditAction.Commit || e.Column != _defaultColumn || e.Row.Item is not InventoryWarehouseDto row || e.EditingElement is not CheckBox checkBox) return;
        try
        {
            _busy = true; IsEnabled = false;
            await _api.SaveInventoryWarehouseAsync(new(row.Code, row.Name, checkBox.IsChecked == true, row.IsActive, row.Version, AksaraApiClient.Shared.CurrentUserName, Environment.MachineName, row.Address1, row.Address2, row.District, row.City, row.PostalCode, row.Phone1, row.Phone2, row.Fax));
            _busy = false;
            await ReloadAsync();
        }
        catch (Exception exception) { MessageBox.Show(exception.Message, ViewTitle, MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _busy = false; IsEnabled = true; }
    }

    private async Task EditAsync() { if (_grid.SelectedItem is not null) await OpenEditorAsync(_grid.SelectedItem); }

    private async Task ToggleActiveAsync()
    {
        if (_grid.SelectedItem is not { } selected) return;
        var (code, name, active) = selected switch
        {
            InventoryWarehouseDto x => (x.Code, x.Name, x.IsActive),
            InventoryCategoryDto x => (x.Code, x.Name, x.IsActive),
            InventoryAdjustmentTypeDto x => (x.Code, x.Name, x.IsActive),
            _ => ("", "", true)
        };
        if (string.IsNullOrWhiteSpace(code)) return;
        var action = active ? "menonaktifkan" : "mengaktifkan";
        if (MessageBox.Show($"Yakin {action} {code} - {name}?", ViewTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            _busy = true; IsEnabled = false;
            switch (selected)
            {
                case InventoryWarehouseDto x: await _api.SaveInventoryWarehouseAsync(new(x.Code, x.Name, x.IsDefault, !x.IsActive, x.Version, AksaraApiClient.Shared.CurrentUserName, Environment.MachineName, x.Address1, x.Address2, x.District, x.City, x.PostalCode, x.Phone1, x.Phone2, x.Fax)); break;
                case InventoryCategoryDto x: await _api.SaveInventoryCategoryAsync(new(x.Code, x.Name, x.ParentCode, !x.IsActive, x.Version, AksaraApiClient.Shared.CurrentUserName, Environment.MachineName,
                    x.UsesIdPerTransaction, x.IsProductionMaterial, x.IsProduct, x.IsNotTradeGoods, x.IsNotServiceSupport,
                    x.MovR1Qty, x.MovR2Qty, x.MovR3Qty, x.PointValue, x.CategoryType)); break;
                case InventoryAdjustmentTypeDto x: await _api.SaveInventoryAdjustmentTypeAsync(new(x.Code, x.Name, x.Direction, x.RequiresValue, x.IsOpeningBalance, !x.IsActive, x.Version, AksaraApiClient.Shared.CurrentUserName, Environment.MachineName)); break;
            }
            _busy = false;
            await ReloadAsync();
        }
        catch (Exception exception) { MessageBox.Show(exception.Message, ViewTitle, MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _busy = false; IsEnabled = true; }
    }

    private async Task OpenEditorAsync(object? row)
    {
        var editor = new InventoryMasterEditWindow(_kind, row) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() != true || editor.Request is null) return;
        try
        {
            _busy = true; IsEnabled = false;
            switch (editor.Request)
            {
                case SaveInventoryWarehouseRequest request: await _api.SaveInventoryWarehouseAsync(request); break;
                case SaveInventoryAdjustmentTypeRequest request: await _api.SaveInventoryAdjustmentTypeAsync(request); break;
                case SaveInventoryCategoryRequest request: await _api.SaveInventoryCategoryAsync(request); break;
            }
            _busy = false;
            await ReloadAsync();
        }
        catch (Exception exception) { MessageBox.Show(exception.Message, ViewTitle, MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _busy = false; IsEnabled = true; }
    }

    private static string SearchText(object row) => row switch
    {
        InventoryWarehouseDto x => $"{x.Code} {x.Name}",
        InventoryCategoryDto x => $"{x.Code} {x.Name} {x.Analysis}",
        InventoryAdjustmentTypeDto x => $"{x.Code} {x.Name}",
        _ => row.ToString() ?? ""
    };

    private static DataGridTextColumn TextColumn(string title, string path, double width) => TextColumn(title, path, new DataGridLength(width));
    private static DataGridTextColumn TextColumn(string title, string path, DataGridLength width) => new() { Header = title, Binding = new Binding(path), Width = width, IsReadOnly = true };
    private static DataGridTextColumn NumberColumn(string title, string path, double width) => new() { Header = title, Binding = new Binding(path) { StringFormat = "N0" }, Width = width, IsReadOnly = true, ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right) } } };
    private static Button Button(string text, double width) => new() { Content = text, Width = width, Height = 30, Margin = new Thickness(4, 0, 0, 0) };
    private static void Add(Grid grid, UIElement child, int column) { Grid.SetColumn(child, column); grid.Children.Add(child); }
    private static string? CodeOf(object? row) => row switch { InventoryWarehouseDto x => x.Code, InventoryCategoryDto x => x.Code, InventoryAdjustmentTypeDto x => x.Code, _ => null };
    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
}

internal sealed class InventoryMasterEditWindow : Window
{
    private readonly InventoryMasterKind _kind;
    private readonly object? _row;
    private readonly TextBox _code = new();
    private readonly TextBox _name = new();
    private readonly TextBox _address1 = new();
    private readonly TextBox _address2 = new();
    private readonly TextBox _district = new();
    private readonly TextBox _city = new();
    private readonly TextBox _postalCode = new();
    private readonly TextBox _phone1 = new();
    private readonly TextBox _phone2 = new();
    private readonly TextBox _fax = new();
    private readonly TextBox _parent = new();
    private readonly CheckBox _usesId = new() { Content = "Menggunakan ID pada setiap transaksi" };
    private readonly CheckBox _isProduct = new() { Content = "Produk" };
    private readonly CheckBox _isNotTradeGoods = new() { Content = "Barang Dagang" };
    private readonly CheckBox _isNotServiceSupport = new() { Content = "Barang Penunjang Jasa" };
    private readonly ComboBox _direction = new() { ItemsSource = new[] { "IN", "OUT", "BOTH" }, SelectedIndex = 0 };
    private readonly CheckBox _default = new() { Content = "Sebagai pilihan default" };
    private readonly CheckBox _value = new() { Content = "Memerlukan nilai / HPP" };
    private readonly CheckBox _opening = new() { Content = "Jenis saldo awal" };
    private readonly CheckBox _active = new() { Content = "Aktif", IsChecked = true };
    private bool IsServiceCategory => _kind == InventoryMasterKind.ServiceCategories;
    public object? Request { get; private set; }

    public InventoryMasterEditWindow(InventoryMasterKind kind, object? row)
    {
        _kind = kind; _row = row;
        Title = kind switch { InventoryMasterKind.Warehouses => "Lokasi Stok / Gudang Barang", InventoryMasterKind.Categories => "Kelompok Barang", InventoryMasterKind.ServiceCategories => "Kelompok Jasa", _ => "Jenis Penyesuaian Persediaan" };
        Width = kind == InventoryMasterKind.Categories ? 1100 : 820; Height = kind switch { InventoryMasterKind.Warehouses => 520, InventoryMasterKind.Categories => 365, InventoryMasterKind.ServiceCategories => 320, _ => 450 }; MinWidth = kind == InventoryMasterKind.Categories ? 900 : 700; WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Brush("#F0F0F0");
        Content = Build(); Populate();
    }

    private UIElement Build()
    {
        var root = new Grid { Margin = new Thickness(8) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid { Height = 43, Background = Brush("#ECECEC") };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(205) }); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) }); header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
        header.Children.Add(new CheckBox { Content = "Exclusive Mode", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
        var status = new TextBlock { Text = "Status:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) }; Grid.SetColumn(status, 1); header.Children.Add(status);
        var mode = new TextBlock { Text = _row is null ? "TAMBAH DATA" : "EDIT DATA", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(mode, 2); header.Children.Add(mode);
        var back = new Button { Content = "|||", Width = 42, Height = 30, Margin = new Thickness(8, 6, 8, 6), Padding = new Thickness(0) }; back.Click += (_, _) => Close(); Grid.SetColumn(back, 3); header.Children.Add(back);
        root.Children.Add(header);

        var fields = new Grid { Margin = new Thickness(12, 12, 12, 8) };
        if (_kind == InventoryMasterKind.Warehouses)
        {
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) });
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(75) });
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(155) });
            for (var i = 0; i < 6; i++) fields.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });

            AddField(fields, "Kode", _code, 0, 0);
            Grid.SetRow(_default, 0); Grid.SetColumn(_default, 4); Grid.SetColumnSpan(_default, 2); fields.Children.Add(_default);
            AddField(fields, "Nama", _name, 1, 0, 3);
            AddField(fields, "Alamat 1", _address1, 2, 0, 2); AddField(fields, "Telp.(1)", _phone1, 2, 4);
            AddField(fields, "Alamat 2", _address2, 3, 0, 2); AddField(fields, "Telp.(2)", _phone2, 3, 4);
            AddField(fields, "Kecamatan", _district, 4, 0); AddField(fields, "Fax", _fax, 4, 4);
            AddField(fields, "Kota", _city, 5, 0); AddField(fields, "Kode Pos", _postalCode, 5, 2);
        }
        else if (_kind != InventoryMasterKind.Categories)
        {
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) }); fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(245) }); fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) }); fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(245) });
            for (var i = 0; i < 6; i++) fields.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
            AddField(fields, "Kode", _code, 0, 0); AddField(fields, "Nama", _name, 1, 0, 1);
            if (IsServiceCategory)
            {
                Grid.SetRow(_active, 2); Grid.SetColumn(_active, 1); fields.Children.Add(_active);
            }
        }
        if (_kind == InventoryMasterKind.Categories)
        {
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) }); fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(440) }); fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) }); fields.ColumnDefinitions.Add(new ColumnDefinition());
            for (var i = 0; i < 7; i++) fields.RowDefinitions.Add(new RowDefinition { Height = new GridLength(i == 2 ? 30 : 34) });
            AddField(fields, "Kode", _code, 0, 0, 1); AddField(fields, "Nama", _name, 1, 0, 1);
            var rightTitle = new TextBlock { Text = "Untuk semua barang yang menggunakan Kelompok Barang ini :", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }; Grid.SetRow(rightTitle, 0); Grid.SetColumn(rightTitle, 2); Grid.SetColumnSpan(rightTitle, 2); fields.Children.Add(rightTitle);
            Grid.SetRow(_usesId, 1); Grid.SetColumn(_usesId, 2); Grid.SetColumnSpan(_usesId, 2); fields.Children.Add(_usesId);
            var note = new TextBlock { Text = "(Aktifkan jika kelompok barang digunakan untuk barang seperti HANDPHONE, yang menggunakan IMEI setiap transaksinya)", Foreground = Brush("#777777"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }; Grid.SetRow(note, 2); Grid.SetColumn(note, 2); Grid.SetColumnSpan(note, 2); fields.Children.Add(note);
            var asLabel = new TextBlock { Text = "Adalah sebagai :", VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(asLabel, 3); Grid.SetColumn(asLabel, 2); fields.Children.Add(asLabel);
            var notLabel = new TextBlock { Text = "Dan BUKAN sebagai :", VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(notLabel, 3); Grid.SetColumn(notLabel, 3); fields.Children.Add(notLabel);
            Grid.SetRow(_isNotTradeGoods, 4); Grid.SetColumn(_isNotTradeGoods, 3); fields.Children.Add(_isNotTradeGoods);
            Grid.SetRow(_isProduct, 4); Grid.SetColumn(_isProduct, 2); fields.Children.Add(_isProduct);
            Grid.SetRow(_isNotServiceSupport, 5); Grid.SetColumn(_isNotServiceSupport, 3); fields.Children.Add(_isNotServiceSupport);
        }
        else if (_kind == InventoryMasterKind.AdjustmentTypes)
        {
            AddField(fields, "Arah Stok", _direction, 2, 0); Grid.SetRow(_value, 3); Grid.SetColumn(_value, 1); fields.Children.Add(_value); Grid.SetRow(_opening, 4); Grid.SetColumn(_opening, 1); fields.Children.Add(_opening);
        }
        if (_kind == InventoryMasterKind.AdjustmentTypes)
        {
            Grid.SetRow(_active, 5); Grid.SetColumn(_active, 1); fields.Children.Add(_active);
        }
        Grid.SetRow(fields, 1); root.Children.Add(fields);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        if (_kind == InventoryMasterKind.Categories)
        {
            foreach (var text in new[] { "<<", "<", "•", ">", ">>" }) actions.Children.Add(new Button { Content = text, Width = 42, Height = 30, Margin = new Thickness(2), IsEnabled = false });
            actions.Children.Add(new Button { Content = "Cari", Width = 78, Height = 30, Margin = new Thickness(3), IsEnabled = false });
            actions.Children.Add(new Button { Content = "Tambah", Width = 82, Height = 30, Margin = new Thickness(3), IsEnabled = false });
            var cancel = new Button { Content = "Batal", Width = 82, Height = 30, Margin = new Thickness(3) }; cancel.Click += (_, _) => { Request = null; DialogResult = false; }; actions.Children.Add(cancel);
            var save = new Button { Content = "Simpan", Width = 82, Height = 30, Margin = new Thickness(3) }; save.Click += Save_Click; actions.Children.Add(save);
            var saveClose = new Button { Content = "Smnp&Ctk", Width = 92, Height = 30, Margin = new Thickness(3) }; saveClose.Click += Save_Click; actions.Children.Add(saveClose);
            var delete = new Button { Content = "Hapus", Width = 82, Height = 30, Margin = new Thickness(3), IsEnabled = _row is not null }; delete.Click += (_, _) => { _active.IsChecked = false; Save_Click(this, new RoutedEventArgs()); }; actions.Children.Add(delete);
            var restore = new Button { Content = "Btl.Hapus", Width = 88, Height = 30, Margin = new Thickness(3), IsEnabled = _row is not null }; restore.Click += (_, _) => { _active.IsChecked = true; Save_Click(this, new RoutedEventArgs()); }; actions.Children.Add(restore);
            var close = new Button { Content = "Tutup", Width = 82, Height = 30, Margin = new Thickness(3) }; close.Click += (_, _) => { Request = null; DialogResult = false; }; actions.Children.Add(close);
        }
        else
        {
            var save = new Button { Content = "Simpan", Width = 100, Height = 30, Margin = new Thickness(4) }; save.Click += Save_Click;
            var cancel = new Button { Content = "Batal", Width = 100, Height = 30, Margin = new Thickness(4) }; cancel.Click += (_, _) => Close();
            var close = new Button { Content = "Tutup", Width = 100, Height = 30, Margin = new Thickness(4) }; close.Click += (_, _) => Close();
            actions.Children.Add(save); actions.Children.Add(cancel); actions.Children.Add(close);
        }
        Grid.SetRow(actions, 2); root.Children.Add(actions); return root;
    }

    private void Populate()
    {
        switch (_row)
        {
            case InventoryWarehouseDto x:
                _code.Text = x.Code; _name.Text = x.Name; _address1.Text = x.Address1; _address2.Text = x.Address2; _district.Text = x.District; _city.Text = x.City; _postalCode.Text = x.PostalCode; _phone1.Text = x.Phone1; _phone2.Text = x.Phone2; _fax.Text = x.Fax; _default.IsChecked = x.IsDefault; _active.IsChecked = x.IsActive; break;
            case InventoryAdjustmentTypeDto x: _code.Text = x.Code; _name.Text = x.Name; _direction.SelectedItem = x.Direction; _value.IsChecked = x.RequiresValue; _opening.IsChecked = x.IsOpeningBalance; _active.IsChecked = x.IsActive; break;
            case InventoryCategoryDto x:
                _code.Text = x.Code; _name.Text = x.Name; _parent.Text = x.ParentCode; _active.IsChecked = x.IsActive;
                _usesId.IsChecked = x.UsesIdPerTransaction; _isProduct.IsChecked = x.IsProduct;
                _isNotTradeGoods.IsChecked = x.IsNotTradeGoods; _isNotServiceSupport.IsChecked = x.IsNotServiceSupport;
                break;
        }
        _code.IsReadOnly = _row is not null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_code.Text) || string.IsNullOrWhiteSpace(_name.Text)) { MessageBox.Show("Kode dan nama wajib diisi.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var user = AksaraApiClient.Shared.CurrentUserName; var machine = Environment.MachineName;
        Request = _kind switch
        {
            InventoryMasterKind.Warehouses => new SaveInventoryWarehouseRequest(_code.Text, _name.Text, _default.IsChecked == true, _active.IsChecked == true, (_row as InventoryWarehouseDto)?.Version, user, machine, Empty(_address1.Text), Empty(_address2.Text), Empty(_district.Text), Empty(_city.Text), Empty(_postalCode.Text), Empty(_phone1.Text), Empty(_phone2.Text), Empty(_fax.Text)),
            InventoryMasterKind.AdjustmentTypes => new SaveInventoryAdjustmentTypeRequest(_code.Text, _name.Text, _direction.SelectedItem?.ToString() ?? "IN", _value.IsChecked == true, _opening.IsChecked == true, _active.IsChecked == true, (_row as InventoryAdjustmentTypeDto)?.Version, user, machine),
            _ => new SaveInventoryCategoryRequest(_code.Text, _name.Text, null, _active.IsChecked == true, (_row as InventoryCategoryDto)?.Version, user, machine,
                _usesId.IsChecked == true, false, _isProduct.IsChecked == true,
                _isNotTradeGoods.IsChecked == true, _isNotServiceSupport.IsChecked == true,
                CategoryType: IsServiceCategory ? "SERVICE" : "GOODS")
        };
        DialogResult = true;
    }

    private static void AddField(Grid grid, string label, Control control, int row, int labelColumn, int span = 1)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }; Grid.SetRow(text, row); Grid.SetColumn(text, labelColumn); grid.Children.Add(text);
        control.Margin = new Thickness(0, 2, 12, 2); Grid.SetRow(control, row); Grid.SetColumn(control, labelColumn + 1); Grid.SetColumnSpan(control, span * 2 - 1); grid.Children.Add(control);
    }

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
}
