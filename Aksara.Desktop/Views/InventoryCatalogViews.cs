using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aksara.Contracts;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

public enum InventoryCatalogKind { Images, Prices }

public sealed class InventoryCatalogView : UserControl
{
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly InventoryCatalogKind _kind;
    private readonly TextBox _query = new() { MinWidth = 240 };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, RowHeight = 25, SelectionMode = DataGridSelectionMode.Single };
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    public event EventHandler? CloseRequested;
    public string ViewTitle => _kind == InventoryCatalogKind.Images ? "Tampilan Gambar Barang" : "Tampilan Harga Jual Barang";

    public InventoryCatalogView(InventoryCatalogKind kind, string? initialQuery = null)
    {
        _kind = kind; _query.Text = initialQuery ?? ""; Content = Build(); ConfigureColumns();
        Loaded += async (_, _) => await ReloadAsync();
        _grid.SelectionChanged += async (_, _) => { if (_kind == InventoryCatalogKind.Images && _grid.SelectedItem is InventoryItemSummaryDto row) await LoadImageAsync(row.Code); };
    }

    private UIElement Build()
    {
        var root = new Grid { Margin = new Thickness(6), Background = Brushes.White };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Border { Background = Brush("#E8EDF3"), BorderBrush = Brush("#B8C4CF"), BorderThickness = new Thickness(1), Padding = new Thickness(10, 7, 10, 7), Child = new TextBlock { Text = ViewTitle.ToUpperInvariant(), FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Brush("#183B56") } }; root.Children.Add(header);
        var filter = new Grid { Margin = new Thickness(0, 6, 0, 6) }; filter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); filter.ColumnDefinitions.Add(new ColumnDefinition()); filter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filter.Children.Add(new TextBlock { Text = "Cari Barang", VerticalAlignment = VerticalAlignment.Center }); Grid.SetColumn(_query, 1); _query.Margin = new Thickness(8, 0, 8, 0); filter.Children.Add(_query);
        var show = new Button { Content = "Tampilkan", Width = 95 }; show.Click += async (_, _) => await ReloadAsync(); Grid.SetColumn(show, 2); filter.Children.Add(show); Grid.SetRow(filter, 1); root.Children.Add(filter);
        if (_kind == InventoryCatalogKind.Images)
        {
            var split = new Grid(); split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) }); split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) }); split.Children.Add(_grid);
            var imageBorder = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Background = Brushes.White, Margin = new Thickness(7, 0, 0, 0), Child = _image }; Grid.SetColumn(imageBorder, 1); split.Children.Add(imageBorder); Grid.SetRow(split, 2); root.Children.Add(split);
        }
        else { Grid.SetRow(_grid, 2); root.Children.Add(_grid); }
        var footer = new Grid { Margin = new Thickness(0, 6, 0, 0) }; footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); footer.Children.Add(_status); var close = new Button { Content = "Tutup", Width = 90 }; close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty); Grid.SetColumn(close, 1); footer.Children.Add(close); Grid.SetRow(footer, 3); root.Children.Add(footer); return root;
    }

    private void ConfigureColumns()
    {
        if (_kind == InventoryCatalogKind.Images)
        {
            _grid.Columns.Add(Column("Kode", "Code", 120)); _grid.Columns.Add(Column("Nama Barang", "Name", new DataGridLength(1, DataGridLengthUnitType.Star))); _grid.Columns.Add(Column("Kelompok", "CategoryCode", 100));
            return;
        }
        _grid.Columns.Add(Column("Kelompok", "CategoryCode", 90)); _grid.Columns.Add(Column("Kode", "ItemCode", 110)); _grid.Columns.Add(Column("Nama Barang", "ItemName", new DataGridLength(1, DataGridLengthUnitType.Star))); _grid.Columns.Add(Column("Sat.", "Unit", 55));
        foreach (var field in new[] { ("Harga Beli", "PurchasePrice"), ("HPP", "AverageCost"), ("Eceran", "RetailPrice"), ("Grosir", "WholesalePrice"), ("Partai", "BulkPrice"), ("Khusus 1", "SpecialPrice1"), ("Khusus 2", "SpecialPrice2"), ("Margin %", "RetailMarginPercent"), ("Stok", "TotalStock") }) _grid.Columns.Add(NumberColumn(field.Item1, field.Item2));
    }

    private async Task ReloadAsync()
    {
        try { IsEnabled = false; if (_kind == InventoryCatalogKind.Images) { var rows = await _api.SearchInventoryItemsAsync(_query.Text, includeInactive: false); _grid.ItemsSource = rows; _status.Text = $"{rows.Count:N0} barang"; } else { var report = await _api.GetInventoryPriceReportAsync(_query.Text); _grid.ItemsSource = report.Rows; _status.Text = $"{report.Rows.Count:N0} barang | nilai stok {report.Totals.InventoryValue:N2}"; } }
        catch (Exception exception) { _status.Text = exception.Message; }
        finally { IsEnabled = true; }
    }

    private async Task LoadImageAsync(string code)
    {
        try { var item = await _api.GetInventoryItemAsync(code); if (string.IsNullOrWhiteSpace(item.ImageBase64)) { _image.Source = null; _status.Text = $"{item.Code} belum memiliki gambar."; return; } var bitmap = new BitmapImage(); using var stream = new MemoryStream(Convert.FromBase64String(item.ImageBase64)); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); _image.Source = bitmap; _status.Text = $"{item.Code} - {item.Name}"; } catch (Exception exception) { _status.Text = exception.Message; }
    }
    private static DataGridTextColumn Column(string title, string path, DataGridLength width) => new() { Header = title, Binding = new Binding(path), Width = width };
    private static DataGridTextColumn NumberColumn(string title, string path) => new() { Header = title, Binding = new Binding(path) { StringFormat = "N2" }, Width = 100, ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right) } } };
    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
}

public sealed class InventoryPriceWindow : Window
{
    public InventoryPriceWindow(string itemCode)
    {
        Title = "Harga Jual Barang - AKSARA"; Width = 1050; Height = 600; MinWidth = 850; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var view = new InventoryCatalogView(InventoryCatalogKind.Prices, itemCode); view.CloseRequested += (_, _) => Close(); Content = view;
    }
}
