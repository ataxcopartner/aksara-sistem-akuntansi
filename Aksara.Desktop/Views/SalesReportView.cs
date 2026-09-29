using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Aksara.Contracts;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

public enum SalesReportKind { Detail, Header, ReturnDetail, ReturnHeader, ProfitDetail, ProfitHeader }

public sealed class SalesReportView : UserControl
{
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly SalesReportKind _kind;
    private readonly DatePicker _from = new() { SelectedDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), Width = 125 };
    private readonly DatePicker _to = new() { SelectedDate = DateTime.Today, Width = 125 };
    private readonly TextBox _query = new() { MinWidth = 220 };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, RowHeight = 23, ColumnHeaderHeight = 27 };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _gross = TotalText();
    private readonly TextBlock _returns = TotalText();
    private readonly TextBlock _net = TotalText();
    private readonly TextBlock _cost = TotalText();
    private readonly TextBlock _profit = TotalText(true);

    public event EventHandler? CloseRequested;
    public string ReportTitle { get; }

    public SalesReportView(SalesReportKind kind, ReportFilterSelection? initialFilter = null)
    {
        _kind = kind;
        ReportGridTools.ConfigureScrollableReportGrid(_grid);
        ReportTitle = kind switch
        {
            SalesReportKind.Detail => "Laporan Penjualan Rinci",
            SalesReportKind.Header => "Laporan Penjualan Rinci (Header)",
            SalesReportKind.ReturnDetail => "Laporan Retur Penjualan Rinci",
            SalesReportKind.ReturnHeader => "Laporan Retur Penjualan Header",
            SalesReportKind.ProfitDetail => "Laporan Laba Penjualan Rinci",
            _ => "Laporan Laba Penjualan Header"
        };
        Content = BuildContent();
        if (initialFilter is not null)
        {
            _from.SelectedDate = initialFilter.From.ToDateTime(TimeOnly.MinValue);
            _to.SelectedDate = initialFilter.To.ToDateTime(TimeOnly.MinValue);
            _query.Text = initialFilter.Query ?? string.Empty;
        }
        ConfigureColumns();
        Loaded += async (_, _) => await ReloadAsync();
    }

    private UIElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(5), Background = System.Windows.Media.Brushes.White };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new Border { Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(232, 237, 243)), BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(184, 196, 207)), BorderThickness = new Thickness(1), Padding = new Thickness(10, 8, 10, 8) };
        title.Child = new TextBlock { Text = ReportTitle, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 59, 86)) };
        root.Children.Add(title);

        var filter = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        filter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filter.ColumnDefinitions.Add(new ColumnDefinition());
        filter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filter.Children.Add(Label("Tanggal", 0));
        Add(filter, _from, 1, new Thickness(5, 0, 12, 0));
        Add(filter, Label("s/d", 0), 2);
        Add(filter, _to, 3, new Thickness(5, 0, 12, 0));
        Add(filter, Label("Cari", 0), 4);
        Add(filter, _query, 5, new Thickness(5, 0, 8, 0));
        var show = new Button { Content = "Tampilkan", Width = 90, Style = (Style)Application.Current.Resources["PrimaryButtonStyle"] };
        show.Click += async (_, _) => await ReloadAsync();
        Add(filter, show, 6);
        Grid.SetRow(filter, 1);
        root.Children.Add(filter);

        Grid.SetRow(_grid, 2);
        root.Children.Add(_grid);

        var totals = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        if (IsReturnReport)
        {
            for (var i = 0; i < 3; i++) totals.ColumnDefinitions.Add(new ColumnDefinition());
            AddTotal(totals, 0, "Retur Penjualan", _returns);
            AddTotal(totals, 1, "HPP Retur", _cost);
            AddTotal(totals, 2, "Koreksi Laba", _profit);
        }
        else
        {
            for (var i = 0; i < 5; i++) totals.ColumnDefinitions.Add(new ColumnDefinition());
            AddTotal(totals, 0, "Penjualan Bruto", _gross);
            AddTotal(totals, 1, "Retur Penjualan", _returns);
            AddTotal(totals, 2, "Penjualan Neto", _net);
            AddTotal(totals, 3, "HPP Neto", _cost);
            AddTotal(totals, 4, "Laba Kotor", _profit);
        }
        Grid.SetRow(totals, 3);
        root.Children.Add(totals);

        var footer = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(_status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var excel = new Button { Content = "Excel", Width = 90, Margin = new Thickness(5, 0, 0, 0) };
        excel.Click += (_, _) => ReportGridTools.ExportDataGridToExcel(_grid, ReportTitle);
        var print = new Button { Content = "Cetak", Width = 90, Margin = new Thickness(5, 0, 0, 0) };
        print.Click += (_, _) => ReportGridTools.PrintDataGrid(_grid, ReportTitle);
        var close = new Button { Content = "Tutup", Width = 90, Margin = new Thickness(5, 0, 0, 0) };
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        actions.Children.Add(excel);
        actions.Children.Add(print);
        actions.Children.Add(close);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 4);
        root.Children.Add(footer);
        return root;
    }

    private void ConfigureColumns()
    {
        if (IsReturnReport)
        {
            _grid.Columns.Add(Column("Tgl.Retur", "DocumentDate", 90));
            _grid.Columns.Add(Column("No. Retur", "DocumentNumber", 110));
            _grid.Columns.Add(Column("No. Nota", "SourceDocumentNumber", 110));
            _grid.Columns.Add(Column("Kode Cust.", "CustomerCode", 90));
            _grid.Columns.Add(Column("Toko/PT", "CustomerName", 165));
            if (_kind == SalesReportKind.ReturnDetail)
            {
                _grid.Columns.Add(Column("Kode Barang", "ItemCode", 95));
                _grid.Columns.Add(Column("Nama Barang", "ItemName", 260));
                _grid.Columns.Add(NumberColumn("Banyak", "ReturnQuantity", 70));
                _grid.Columns.Add(Column("Sat.", "Unit", 42));
                _grid.Columns.Add(NumberColumn("Harga", "UnitPrice", 85));
                _grid.Columns.Add(NumberColumn("Disc.", "DiscountPercent", 55));
            }
            else
            {
                _grid.Columns.Add(Column("T/K", "TransactionType", 55));
                _grid.Columns.Add(Column("Sales", "SalesCode", 70));
                _grid.Columns.Add(Column("Gudang", "WarehouseCode", 70));
                _grid.Columns.Add(NumberColumn("Subtotal", "Subtotal", 100));
                _grid.Columns.Add(NumberColumn("Disc.", "DiscountAmount", 85));
                _grid.Columns.Add(NumberColumn("PPN", "TaxAmount", 85));
            }
            _grid.Columns.Add(NumberColumn("Retur", "ReturnAmount", 100));
            _grid.Columns.Add(NumberColumn("HPP Retur", "ReturnCost", 100));
            _grid.Columns.Add(NumberColumn("Koreksi Laba", "ProfitReduction", 105));
            return;
        }

        _grid.Columns.Add(Column("Tanggal", "DocumentDate", 90));
        _grid.Columns.Add(Column("No. Nota", "DocumentNumber", 110));
        _grid.Columns.Add(Column("Kode Cust.", "CustomerCode", 90));
        _grid.Columns.Add(Column("Toko/PT", "CustomerName", 160));
        if (_kind is SalesReportKind.Detail or SalesReportKind.ProfitDetail)
        {
            _grid.Columns.Add(Column("Kode Barang", "ItemCode", 95));
            _grid.Columns.Add(Column("Nama Barang", "ItemName", 260));
            _grid.Columns.Add(NumberColumn("Jual", "SoldQuantity", 65));
            _grid.Columns.Add(NumberColumn("Retur", "ReturnedQuantity", 65));
            _grid.Columns.Add(NumberColumn("Neto", "NetQuantity", 65));
            if (_kind == SalesReportKind.Detail)
            {
                _grid.Columns.Add(NumberColumn("Harga", "UnitPrice", 85));
                _grid.Columns.Add(NumberColumn("Penjualan", "GrossSales", 100));
                _grid.Columns.Add(NumberColumn("Retur Rp.", "SalesReturn", 90));
            }
            _grid.Columns.Add(NumberColumn("Penjualan Neto", "NetSales", 105));
            if (_kind == SalesReportKind.ProfitDetail)
            {
                _grid.Columns.Add(NumberColumn("HPP Neto", "NetCost", 100));
                _grid.Columns.Add(NumberColumn("Laba", "GrossProfit", 100));
            }
        }
        else
        {
            _grid.Columns.Add(Column("T/K", "TransactionType", 55));
            _grid.Columns.Add(Column("J.Tempo", "DueDate", 90));
            _grid.Columns.Add(Column("Sales", "SalesCode", 70));
            _grid.Columns.Add(NumberColumn("Penjualan", "GrossSales", 110));
            _grid.Columns.Add(NumberColumn("Retur", "SalesReturn", 100));
            _grid.Columns.Add(NumberColumn("Penjualan Neto", "NetSales", 115));
            if (_kind == SalesReportKind.ProfitHeader)
            {
                _grid.Columns.Add(NumberColumn("HPP Neto", "NetCost", 105));
                _grid.Columns.Add(NumberColumn("Laba", "GrossProfit", 105));
            }
        }
    }

    private async Task ReloadAsync()
    {
        try
        {
            IsEnabled = false;
            _status.Text = IsReturnReport ? "Mengambil data Retur Penjualan dan HPP..." : "Mengambil data Penjualan, Retur, Stok, dan HPP...";
            var from = DateOnly.FromDateTime(_from.SelectedDate ?? DateTime.Today);
            var to = DateOnly.FromDateTime(_to.SelectedDate ?? DateTime.Today);
            if (_kind == SalesReportKind.ReturnDetail)
            {
                var report = await _api.GetSalesReturnDetailReportAsync(from, to, _query.Text);
                _grid.ItemsSource = report.Rows;
                _returns.Text = report.Totals.ReturnAmount.ToString("N2");
                _cost.Text = report.Totals.ReturnCost.ToString("N2");
                _profit.Text = report.Totals.ProfitReduction.ToString("N2");
                _status.Text = $"{report.Rows.Count:N0} rincian retur | Periode {from:dd/MM/yyyy} s/d {to:dd/MM/yyyy}";
                return;
            }
            if (_kind == SalesReportKind.ReturnHeader)
            {
                var report = await _api.GetSalesReturnHeaderReportAsync(from, to, _query.Text);
                _grid.ItemsSource = report.Rows;
                _returns.Text = report.Totals.ReturnAmount.ToString("N2");
                _cost.Text = report.Totals.ReturnCost.ToString("N2");
                _profit.Text = report.Totals.ProfitReduction.ToString("N2");
                _status.Text = $"{report.Rows.Count:N0} nota retur | Periode {from:dd/MM/yyyy} s/d {to:dd/MM/yyyy}";
                return;
            }

            SalesReportTotalsDto totals;
            if (_kind is SalesReportKind.Detail or SalesReportKind.ProfitDetail)
            {
                var report = await _api.GetSalesDetailReportAsync(from, to, _query.Text, _kind == SalesReportKind.ProfitDetail);
                _grid.ItemsSource = report.Rows;
                totals = report.Totals;
                _status.Text = $"{report.Rows.Count:N0} rincian | Periode {from:dd/MM/yyyy} s/d {to:dd/MM/yyyy}";
            }
            else
            {
                var report = await _api.GetSalesHeaderReportAsync(from, to, _query.Text, _kind == SalesReportKind.ProfitHeader);
                _grid.ItemsSource = report.Rows;
                totals = report.Totals;
                _status.Text = $"{report.Rows.Count:N0} nota | Periode {from:dd/MM/yyyy} s/d {to:dd/MM/yyyy}";
            }
            _gross.Text = totals.GrossSales.ToString("N2");
            _returns.Text = totals.SalesReturn.ToString("N2");
            _net.Text = totals.NetSales.ToString("N2");
            _cost.Text = totals.NetCost.ToString("N2");
            _profit.Text = totals.GrossProfit.ToString("N2");
        }
        catch (Exception exception)
        {
            _status.Text = "Laporan gagal dimuat";
            MessageBox.Show(exception.Message, $"AKSARA - {ReportTitle}", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { IsEnabled = true; }
    }

    private static DataGridTextColumn Column(string header, string property, DataGridLength width) => new() { Header = header, Binding = new Binding(property), Width = width };
    private static DataGridTextColumn NumberColumn(string header, string property, double width) => new() { Header = header, Binding = new Binding(property) { StringFormat = "N2" }, Width = width, ElementStyle = RightAlignedStyle() };
    private static Style RightAlignedStyle() { var style = new Style(typeof(TextBlock)); style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right)); return style; }
    private static TextBlock Label(string text, int _) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };
    private static TextBlock TotalText(bool bold = false) => new() { Text = "0,00", TextAlignment = TextAlignment.Right, FontWeight = bold ? FontWeights.Bold : FontWeights.SemiBold, FontSize = bold ? 13 : 11 };
    private static void Add(Grid grid, UIElement element, int column, Thickness? margin = null) { Grid.SetColumn(element, column); if (margin.HasValue && element is FrameworkElement framework) framework.Margin = margin.Value; grid.Children.Add(element); }
    private static void AddTotal(Grid grid, int column, string label, TextBlock value)
    {
        var border = new Border { BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(203, 213, 223)), BorderThickness = new Thickness(1), Margin = new Thickness(column == 0 ? 0 : 3, 0, 0, 0), Padding = new Thickness(7, 4, 7, 4), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252)) };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(96, 112, 128)), FontSize = 9.5 });
        panel.Children.Add(value);
        border.Child = panel;
        Grid.SetColumn(border, column);
        grid.Children.Add(border);
    }

    private bool IsReturnReport => _kind is SalesReportKind.ReturnDetail or SalesReportKind.ReturnHeader;
}
