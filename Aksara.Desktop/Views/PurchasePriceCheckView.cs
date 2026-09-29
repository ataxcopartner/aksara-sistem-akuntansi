using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Aksara.Contracts;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

public sealed class PurchasePriceCheckView : UserControl
{
    private static readonly CultureInfo IndonesianCulture = CultureInfo.GetCultureInfo("id-ID");
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly bool _useSalesPermission;
    private readonly TextBox _selectedItemText = new()
    {
        IsReadOnly = true,
        Width = 430,
        Height = 28,
        VerticalContentAlignment = VerticalAlignment.Center,
        Cursor = Cursors.Hand,
        ToolTip = "Klik untuk mencari barang"
    };
    private readonly TextBlock _itemInfo = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        CanUserAddRows = false,
        SelectionMode = DataGridSelectionMode.Single,
        RowHeight = 25,
        ColumnHeaderHeight = 30,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.All,
        Margin = new Thickness(0, 6, 0, 6)
    };
    private readonly TextBlock _totals = new() { FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _priceRange = new() { FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _average = new();
    private readonly TextBlock _fifo = new();
    private readonly TextBlock _lifo = new();
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private InventoryItemSummaryDto? _selectedItem;

    public event EventHandler? CloseRequested;

    public PurchasePriceCheckView(bool useSalesPermission)
    {
        _useSalesPermission = useSalesPermission;
        ReportGridTools.ConfigureScrollableReportGrid(_grid);
        ConfigureColumns();
        _grid.ItemsSource = Array.Empty<PurchasePriceCheckRowDto>();
        _selectedItemText.Text = "Klik untuk memilih barang...";
        _status.Text = "Pilih barang untuk menampilkan riwayat pembelian dan pemakaian HPP.";
        ResetSummary();
        Content = Build();
        _selectedItemText.PreviewMouseLeftButtonDown += (_, args) => { args.Handled = true; OpenItemLookup(); };
    }

    private UIElement Build()
    {
        var root = new Grid { Margin = new Thickness(6), Background = Brushes.White };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new Border
        {
            Background = Brush("#E8EDF3"),
            BorderBrush = Brush("#B8C4CF"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            Child = new TextBlock
            {
                Text = "CEK HARGA BELI",
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#183B56")
            }
        };
        root.Children.Add(heading);

        var selector = new DockPanel { Margin = new Thickness(0, 7, 0, 0), LastChildFill = true };
        selector.Children.Add(new TextBlock { Text = "Barang", Width = 62, VerticalAlignment = VerticalAlignment.Center });
        var searchButton = Button("Cari Barang", 105);
        searchButton.Click += (_, _) => OpenItemLookup();
        DockPanel.SetDock(searchButton, Dock.Right);
        selector.Children.Add(searchButton);
        DockPanel.SetDock(_itemInfo, Dock.Right);
        selector.Children.Add(_itemInfo);
        selector.Children.Add(_selectedItemText);
        Grid.SetRow(selector, 1);
        root.Children.Add(selector);

        Grid.SetRow(_grid, 2);
        root.Children.Add(_grid);

        var summary = BuildSummary();
        Grid.SetRow(summary, 3);
        root.Children.Add(summary);

        var footer = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(_status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var reload = Button("Muat Ulang", 95); reload.Click += async (_, _) => await ReloadAsync();
        var excel = Button("Excel", 85); excel.Click += (_, _) => ReportGridTools.ExportDataGridToExcel(_grid, "Cek Harga Beli");
        var print = Button("Cetak", 85); print.Click += (_, _) => ReportGridTools.PrintDataGrid(_grid, "Cek Harga Beli");
        var close = Button("Tutup", 85); close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        actions.Children.Add(reload); actions.Children.Add(excel); actions.Children.Add(print); actions.Children.Add(close);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 4);
        root.Children.Add(footer);
        return root;
    }

    private UIElement BuildSummary()
    {
        var root = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var overview = new Grid();
        overview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.55, GridUnitType.Star) });
        overview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddSummaryCard(overview, CreateSummaryCard("RINGKASAN KUANTITAS", _totals, emphasized: true), 0);
        AddSummaryCard(overview, CreateSummaryCard("RENTANG HPP", _priceRange), 1, leftMargin: 6);
        root.Children.Add(overview);

        var methods = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        methods.ColumnDefinitions.Add(new ColumnDefinition());
        methods.ColumnDefinitions.Add(new ColumnDefinition());
        methods.ColumnDefinitions.Add(new ColumnDefinition());
        AddSummaryCard(methods, CreateSummaryCard("AVERAGE", _average), 0);
        AddSummaryCard(methods, CreateSummaryCard("FIFO", _fifo), 1, leftMargin: 6);
        AddSummaryCard(methods, CreateSummaryCard("LIFO", _lifo), 2, leftMargin: 6);
        Grid.SetRow(methods, 1);
        root.Children.Add(methods);

        return root;
    }

    private static Border CreateSummaryCard(string title, TextBlock value, bool emphasized = false)
    {
        value.TextWrapping = TextWrapping.Wrap;
        value.FontSize = 10.75;
        value.FontWeight = FontWeights.SemiBold;
        value.Foreground = Brush("#3E282C");
        value.LineHeight = 15;

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 9.5,
            FontWeight = FontWeights.Bold,
            Foreground = Brush("#7C1622"),
            Margin = new Thickness(0, 0, 0, 2)
        });
        panel.Children.Add(value);

        return new Border
        {
            Background = emphasized ? Brush("#FFF9EA") : Brush("#FFFDFD"),
            BorderBrush = emphasized ? Brush("#E4B33E") : Brush("#DDCFD2"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 5, 10, 6),
            Child = panel
        };
    }

    private static void AddSummaryCard(Grid parent, Border card, int column, double leftMargin = 0)
    {
        card.Margin = new Thickness(leftMargin, 0, 0, 0);
        Grid.SetColumn(card, column);
        parent.Children.Add(card);
    }

    private void ConfigureColumns()
    {
        _grid.Columns.Add(TextColumn("Tanggal", nameof(PurchasePriceCheckRowDto.PurchaseDate), 90, "dd/MM/yyyy"));
        _grid.Columns.Add(TextColumn("No. Pembelian", nameof(PurchasePriceCheckRowDto.PurchaseNumber), 125));
        _grid.Columns.Add(TextColumn("Supplier", nameof(PurchasePriceCheckRowDto.SupplierName), 210));
        _grid.Columns.Add(TextColumn("Gudang", nameof(PurchasePriceCheckRowDto.WarehouseCode), 80));
        _grid.Columns.Add(NumberColumn("Qty Beli", nameof(PurchasePriceCheckRowDto.PurchaseQuantity), 90));
        _grid.Columns.Add(NumberColumn("Qty Retur", nameof(PurchasePriceCheckRowDto.ReturnQuantity), 85));
        _grid.Columns.Add(NumberColumn("Qty Terjual/HPP", nameof(PurchasePriceCheckRowDto.SoldQuantity), 115));
        _grid.Columns.Add(NumberColumn("Sisa Qty", nameof(PurchasePriceCheckRowDto.RemainingQuantity), 90));
        _grid.Columns.Add(NumberColumn("Harga Beli", nameof(PurchasePriceCheckRowDto.UnitPurchaseCost), 115));
        _grid.Columns.Add(NumberColumn("Sisa Saldo", nameof(PurchasePriceCheckRowDto.RemainingValue), 125));
    }

    private void OpenItemLookup()
    {
        var dialog = new PurchasePriceCheckItemLookupWindow(_useSalesPermission) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.SelectedItem is null) return;
        _selectedItem = dialog.SelectedItem;
        _selectedItemText.Text = $"{_selectedItem.Code} - {_selectedItem.Name}";
        _ = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_selectedItem is null)
        {
            _status.Text = "Pilih barang terlebih dahulu.";
            return;
        }

        try
        {
            IsEnabled = false;
            _status.Text = "Mengambil seluruh riwayat pembelian dan penjualan barang...";
            var report = await _api.GetPurchasePriceCheckAsync(_selectedItem.Code, _useSalesPermission);
            _grid.ItemsSource = report.Rows;
            _itemInfo.Text = $"Sat. {report.Unit}   |   Stok saat ini {Quantity(report.CurrentStock)}";
            ApplySummary(report.Summary, report.Rows);
            _status.Text = report.Rows.Count == 0
                ? "Belum ada pembelian tersimpan untuk barang ini."
                : report.IsCompatibilityMode
                    ? $"{report.Rows.Count:N0} baris pembelian. Mode sementara cloud lama: alokasi FIFO dihitung gabungan semua gudang."
                    : $"{report.Rows.Count:N0} baris pembelian. Qty terjual/HPP dialokasikan dengan FIFO per gudang.";
            if (report.Summary.UnallocatedSoldQuantity > 0)
                _status.Text += $" {Quantity(report.Summary.UnallocatedSoldQuantity)} unit penjualan berasal dari saldo awal/non-pembelian.";
        }
        catch (Exception exception)
        {
            _grid.ItemsSource = Array.Empty<PurchasePriceCheckRowDto>();
            ResetSummary();
            _status.Text = exception.Message;
            ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Cek Harga Beli");
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void ApplySummary(PurchasePriceCheckSummaryDto summary, IReadOnlyList<PurchasePriceCheckRowDto> rows)
    {
        _totals.Text = $"Pembelian {Quantity(summary.TotalPurchaseQuantity)}   |   Retur {Quantity(summary.TotalReturnQuantity)}   |   Terjual {Quantity(summary.TotalSoldQuantity)}   |   Sisa {Quantity(summary.TotalRemainingQuantity)}";
        var pricedRows = rows.Where(row => row.PurchaseQuantity > 0 && row.UnitPurchaseCost > 0).ToArray();
        var lowestCost = pricedRows.Length == 0 ? 0 : pricedRows.Min(row => row.UnitPurchaseCost);
        var highestCost = pricedRows.Length == 0 ? 0 : pricedRows.Max(row => row.UnitPurchaseCost);
        _priceRange.Text = $"HPP terendah {Money(lowestCost)}   |   HPP tertinggi {Money(highestCost)}";
        _average.Text = MethodText(summary.Average);
        _fifo.Text = MethodText(summary.Fifo, NextLotRemainingQuantity(rows, fifo: true));
        _lifo.Text = MethodText(summary.Lifo, NextLotRemainingQuantity(rows, fifo: false));
    }

    private void ResetSummary()
    {
        _itemInfo.Text = string.Empty;
        _totals.Text = "Pembelian 0,00   |   Retur 0,00   |   Terjual 0,00   |   Sisa 0,00";
        _priceRange.Text = "HPP terendah 0,00   |   HPP tertinggi 0,00";
        _average.Text = "HPP/unit penjualan berikutnya 0,00";
        _fifo.Text = _lifo.Text = "HPP/unit penjualan berikutnya 0,00\nSisa unit pada lot ini 0,00";
    }

    private static string MethodText(PurchasePriceCostMethodDto method, decimal? remainingQuantity = null)
    {
        var text = $"HPP/unit penjualan berikutnya {Money(method.HppPerUnit)}";
        return remainingQuantity is null
            ? text
            : $"{text}\nSisa unit pada lot ini {Quantity(remainingQuantity.Value)}";
    }

    private static decimal NextLotRemainingQuantity(IReadOnlyList<PurchasePriceCheckRowDto> rows, bool fifo)
    {
        var remainingRows = rows.Where(row => row.RemainingQuantity > 0);
        var nextLot = fifo ? remainingRows.FirstOrDefault() : remainingRows.LastOrDefault();
        return nextLot?.RemainingQuantity ?? 0;
    }

    private static string Quantity(decimal value) => value.ToString("N2", IndonesianCulture);
    private static string Money(decimal value) => value.ToString("N2", IndonesianCulture);
    private static Button Button(string text, double width) => new() { Content = text, Width = width, Height = 29, Margin = new Thickness(6, 0, 0, 0) };
    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
    private static DataGridTextColumn TextColumn(string header, string property, double width, string? format = null) => new()
    {
        Header = header,
        Binding = new Binding(property) { StringFormat = format, ConverterCulture = IndonesianCulture },
        Width = width
    };
    private static DataGridTextColumn NumberColumn(string header, string property, double width) => TextColumn(header, property, width, "N2");
}

internal sealed class PurchasePriceCheckItemLookupWindow(bool useSalesPermission) : SalesLookupWindowBase<InventoryItemSummaryDto>("CARI BARANG - CEK HARGA BELI", 860, 540)
{
    public InventoryItemSummaryDto? SelectedItem { get; private set; }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Grid.Columns.Add(Column("Kode Barang", nameof(InventoryItemSummaryDto.Code), 135));
        Grid.Columns.Add(Column("Nama Barang", nameof(InventoryItemSummaryDto.Name), 360));
        Grid.Columns.Add(Column("Sat.", nameof(InventoryItemSummaryDto.Unit), 70));
        Grid.Columns.Add(NumberColumn("Stok", nameof(InventoryItemSummaryDto.TotalStock), 105));
        Grid.Columns.Add(NumberColumn("HPP Standar", nameof(InventoryItemSummaryDto.AverageCost), 120));
    }

    protected override Task<IReadOnlyList<InventoryItemSummaryDto>> LoadAsync(string query) =>
        Api.SearchPurchasePriceCheckItemsAsync(query, useSalesPermission);

    protected override void SetSelected(InventoryItemSummaryDto selected) => SelectedItem = selected;

    private static DataGridTextColumn NumberColumn(string header, string property, double width) => new()
    {
        Header = header,
        Binding = new Binding(property) { StringFormat = "N2", ConverterCulture = CultureInfo.GetCultureInfo("id-ID") },
        Width = width
    };
}
