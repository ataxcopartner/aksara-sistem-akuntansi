using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Aksara.Contracts;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

public enum PurchaseReportKind { Detail, Header, ReturnDetail, ReturnHeader }

public sealed class PurchaseReportView : UserControl
{
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly PurchaseReportKind _kind;
    private readonly DatePicker _from = new() { SelectedDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), Width = 112 };
    private readonly DatePicker _to = new() { SelectedDate = DateTime.Today, Width = 112 };
    private readonly TextBox _query = new() { MinWidth = 220 };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, RowHeight = 23, ColumnHeaderHeight = 27 };
    private readonly TextBlock _gross = TotalText();
    private readonly TextBlock _returns = TotalText();
    private readonly TextBlock _net = TotalText(true);
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };

    public string ReportTitle => _kind switch
    {
        PurchaseReportKind.Detail => "Laporan Pembelian Rinci",
        PurchaseReportKind.Header => "Laporan Pembelian Rinci (Header)",
        PurchaseReportKind.ReturnDetail => "Laporan Retur Pembelian Rinci",
        _ => "Laporan Retur Pembelian Header"
    };
    public event EventHandler? CloseRequested;

    public PurchaseReportView(PurchaseReportKind kind, ReportFilterSelection? initialFilter = null)
    {
        _kind = kind;
        ReportGridTools.ConfigureScrollableReportGrid(_grid);
        Content = BuildLayout();
        if (initialFilter is not null)
        {
            _from.SelectedDate = initialFilter.From.ToDateTime(TimeOnly.MinValue);
            _to.SelectedDate = initialFilter.To.ToDateTime(TimeOnly.MinValue);
            _query.Text = initialFilter.Query ?? string.Empty;
        }
        ConfigureColumns();
        Loaded += async (_, _) => await ReloadAsync();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(8) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var title = new Border { BorderBrush = Brush(160, 160, 160), BorderThickness = new Thickness(1), Background = Brush(245, 245, 245), Padding = new Thickness(10, 7, 10, 7) };
        title.Child = new TextBlock { Text = ReportTitle, FontSize = 14, FontWeight = FontWeights.Bold };
        root.Children.Add(title);

        var filters = new Grid { Margin = new Thickness(0, 7, 0, 7) };
        filters.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); filters.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        filters.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); filters.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        filters.ColumnDefinitions.Add(new() { Width = new GridLength(65) }); filters.ColumnDefinitions.Add(new()); filters.ColumnDefinitions.Add(new() { Width = new GridLength(90) });
        Add(filters, Label("Dari"), 0); Add(filters, _from, 1, new Thickness(5, 0, 12, 0)); Add(filters, Label("Sampai"), 2); Add(filters, _to, 3, new Thickness(5, 0, 12, 0));
        Add(filters, Label("Cari"), 4); Add(filters, _query, 5, new Thickness(5, 0, 7, 0));
        var show = new Button { Content = "Tampilkan", Width = 88 }; show.Click += async (_, _) => await ReloadAsync(); Add(filters, show, 6);
        Grid.SetRow(filters, 1); root.Children.Add(filters);

        Grid.SetRow(_grid, 2); root.Children.Add(_grid);
        var totals = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        totals.ColumnDefinitions.Add(new()); totals.ColumnDefinitions.Add(new()); totals.ColumnDefinitions.Add(new());
        AddTotal(totals, 0, IsReturnReport ? "Nilai Retur" : "Pembelian", _gross);
        AddTotal(totals, 1, IsReturnReport ? "Dokumen" : "Retur Pembelian", _returns);
        AddTotal(totals, 2, IsReturnReport ? "Total Retur" : "Pembelian Neto", _net);
        Grid.SetRow(totals, 3); root.Children.Add(totals);

        var footer = new Grid { Margin = new Thickness(0, 7, 0, 0) }; footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.Children.Add(_status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var excel = new Button { Content = "Excel", Width = 90, Margin = new Thickness(5, 0, 0, 0) }; excel.Click += (_, _) => ReportGridTools.ExportDataGridToExcel(_grid, ReportTitle);
        var print = new Button { Content = "Cetak", Width = 90, Margin = new Thickness(5, 0, 0, 0) }; print.Click += (_, _) => ReportGridTools.PrintDataGrid(_grid, ReportTitle);
        var close = new Button { Content = "Tutup", Width = 90, Margin = new Thickness(5, 0, 0, 0) }; close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        actions.Children.Add(excel); actions.Children.Add(print); actions.Children.Add(close);
        Grid.SetColumn(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 4); root.Children.Add(footer);
        return root;
    }

    private void ConfigureColumns()
    {
        _grid.Columns.Add(Column(IsReturnReport ? "Tgl.Retur" : "Tanggal", "DocumentDate", 90));
        _grid.Columns.Add(Column(IsReturnReport ? "No. Retur" : "No. Faktur", "DocumentNumber", 115));
        if (IsReturnReport) _grid.Columns.Add(Column("No. Faktur", "SourceDocumentNumber", 115));
        _grid.Columns.Add(Column("Kode Supl.", "SupplierCode", 90)); _grid.Columns.Add(Column("Toko/PT", "SupplierName", 170));
        if (_kind is PurchaseReportKind.Detail or PurchaseReportKind.ReturnDetail)
        {
            _grid.Columns.Add(Column("Kode Barang", "ItemCode", 100)); _grid.Columns.Add(Column("Nama Barang", "ItemName", 260));
            if (_kind == PurchaseReportKind.Detail)
            {
                _grid.Columns.Add(NumberColumn("Beli", "PurchasedQuantity", 65)); _grid.Columns.Add(NumberColumn("Retur", "ReturnedQuantity", 65)); _grid.Columns.Add(NumberColumn("Neto", "NetQuantity", 65));
            }
            else _grid.Columns.Add(NumberColumn("Retur", "ReturnQuantity", 70));
            _grid.Columns.Add(Column("Sat.", "Unit", 42)); _grid.Columns.Add(NumberColumn("Harga", "UnitPrice", 90)); _grid.Columns.Add(NumberColumn("Disc.", "DiscountPercent", 55));
            _grid.Columns.Add(NumberColumn(IsReturnReport ? "Nilai Retur" : "Pembelian", IsReturnReport ? "ReturnAmount" : "GrossPurchase", 105));
            if (_kind == PurchaseReportKind.Detail) { _grid.Columns.Add(NumberColumn("Retur Rp.", "PurchaseReturn", 90)); _grid.Columns.Add(NumberColumn("Pembelian Neto", "NetPurchase", 110)); }
        }
        else
        {
            _grid.Columns.Add(Column("T/K", "TransactionType", 55)); _grid.Columns.Add(Column("Gudang", "WarehouseCode", 70));
            if (_kind == PurchaseReportKind.Header)
            {
                _grid.Columns.Add(Column("J.Tempo", "DueDate", 90)); _grid.Columns.Add(NumberColumn("Pembelian", "GrossPurchase", 110));
                _grid.Columns.Add(NumberColumn("Retur", "PurchaseReturn", 100)); _grid.Columns.Add(NumberColumn("Pembelian Neto", "NetPurchase", 115));
            }
            else
            {
                _grid.Columns.Add(NumberColumn("Subtotal", "Subtotal", 100)); _grid.Columns.Add(NumberColumn("Disc.", "DiscountAmount", 85));
                _grid.Columns.Add(NumberColumn("PPN", "TaxAmount", 85)); _grid.Columns.Add(NumberColumn("Nilai Retur", "ReturnAmount", 110));
            }
        }
    }

    private async Task ReloadAsync()
    {
        try
        {
            IsEnabled = false; _status.Text = "Mengambil data Pembelian, Retur, dan Persediaan...";
            var from = DateOnly.FromDateTime(_from.SelectedDate ?? DateTime.Today); var to = DateOnly.FromDateTime(_to.SelectedDate ?? DateTime.Today);
            if (_kind == PurchaseReportKind.Detail)
            {
                var report = await _api.GetPurchaseDetailReportAsync(from, to, _query.Text); _grid.ItemsSource = report.Rows; SetTotals(report.Totals); _status.Text = $"{report.Rows.Count:N0} rincian pembelian";
            }
            else if (_kind == PurchaseReportKind.Header)
            {
                var report = await _api.GetPurchaseHeaderReportAsync(from, to, _query.Text); _grid.ItemsSource = report.Rows; SetTotals(report.Totals); _status.Text = $"{report.Rows.Count:N0} faktur pembelian";
            }
            else if (_kind == PurchaseReportKind.ReturnDetail)
            {
                var report = await _api.GetPurchaseReturnDetailReportAsync(from, to, _query.Text); _grid.ItemsSource = report.Rows; SetReturnTotals(report.Totals.ReturnAmount, report.Rows.Count); _status.Text = $"{report.Rows.Count:N0} rincian retur pembelian";
            }
            else
            {
                var report = await _api.GetPurchaseReturnHeaderReportAsync(from, to, _query.Text); _grid.ItemsSource = report.Rows; SetReturnTotals(report.Totals.ReturnAmount, report.Rows.Count); _status.Text = $"{report.Rows.Count:N0} dokumen retur pembelian";
            }
            _status.Text += $" | Periode {from:dd/MM/yyyy} s/d {to:dd/MM/yyyy}";
        }
        catch (Exception ex) { _status.Text = "Laporan gagal dimuat"; MessageBox.Show(ex.Message, $"AKSARA - {ReportTitle}", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { IsEnabled = true; }
    }

    private void SetTotals(PurchaseReportTotalsDto totals) { _gross.Text = totals.GrossPurchase.ToString("N2"); _returns.Text = totals.PurchaseReturn.ToString("N2"); _net.Text = totals.NetPurchase.ToString("N2"); }
    private void SetReturnTotals(decimal total, int count) { _gross.Text = total.ToString("N2"); _returns.Text = count.ToString("N0"); _net.Text = total.ToString("N2"); }
    private bool IsReturnReport => _kind is PurchaseReportKind.ReturnDetail or PurchaseReportKind.ReturnHeader;
    private static DataGridTextColumn Column(string header, string property, DataGridLength width) => new() { Header = header, Binding = new Binding(property), Width = width };
    private static DataGridTextColumn NumberColumn(string header, string property, double width) => new() { Header = header, Binding = new Binding(property) { StringFormat = "N2" }, Width = width, ElementStyle = RightAlignedStyle() };
    private static Style RightAlignedStyle() { var style = new Style(typeof(TextBlock)); style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right)); return style; }
    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };
    private static TextBlock TotalText(bool bold = false) => new() { Text = "0,00", TextAlignment = TextAlignment.Right, FontWeight = bold ? FontWeights.Bold : FontWeights.SemiBold, FontSize = bold ? 13 : 11 };
    private static System.Windows.Media.SolidColorBrush Brush(byte r, byte g, byte b) => new(System.Windows.Media.Color.FromRgb(r, g, b));
    private static void Add(Grid grid, UIElement element, int column, Thickness? margin = null) { Grid.SetColumn(element, column); if (margin is not null && element is FrameworkElement f) f.Margin = margin.Value; grid.Children.Add(element); }
    private static void AddTotal(Grid grid, int column, string label, TextBlock value)
    {
        var border = new Border { BorderBrush = Brush(203, 213, 223), BorderThickness = new Thickness(1), Margin = new Thickness(column == 0 ? 0 : 3, 0, 0, 0), Padding = new Thickness(7, 4, 7, 4), Background = Brush(248, 250, 252) };
        var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = label, Foreground = Brush(96, 112, 128), FontSize = 9.5 }); panel.Children.Add(value); border.Child = panel;
        Grid.SetColumn(border, column); grid.Children.Add(border);
    }
}
