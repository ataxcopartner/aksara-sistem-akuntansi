using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Aksara.Contracts;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

public sealed class PartnerOutstandingReportView : UserControl
{
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly string _partnerType;
    private readonly TextBox _query = new() { MinWidth = 300, Height = 28, Padding = new Thickness(6, 1, 6, 1) };
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        CanUserAddRows = false,
        RowHeight = 25,
        ColumnHeaderHeight = 29,
        GridLinesVisibility = DataGridGridLinesVisibility.All
    };
    private readonly TextBlock _original = TotalText();
    private readonly TextBlock _correction = TotalText();
    private readonly TextBlock _settled = TotalText();
    private readonly TextBlock _remaining = TotalText(true);
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };

    public string ReportTitle => _partnerType == "SUPPLIER" ? "Laporan Utang Berjalan" : "Laporan Piutang Berjalan";
    public event EventHandler? CloseRequested;

    public PartnerOutstandingReportView(string partnerType)
    {
        _partnerType = string.Equals(partnerType, "SUPPLIER", StringComparison.OrdinalIgnoreCase) ? "SUPPLIER" : "CUSTOMER";
        Background = PartnerUi.CanvasBrush;
        ReportGridTools.ConfigureScrollableReportGrid(_grid);
        _grid.EnableColumnVirtualization = false;
        _grid.HorizontalGridLinesBrush = PartnerUi.GridBrush;
        _grid.VerticalGridLinesBrush = PartnerUi.GridBrush;
        _grid.ColumnHeaderStyle = PartnerUi.ColumnHeaderStyle();
        ConfigureColumns();
        Content = BuildLayout();
        Loaded += async (_, _) => await ReloadAsync();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Background = PartnerUi.CanvasBrush };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });

        var titlePanel = new Grid();
        titlePanel.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        titlePanel.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new TextBlock { Text = ReportTitle, FontSize = 17, FontWeight = FontWeights.Bold,
            Foreground = PartnerUi.MaroonBrush });
        heading.Children.Add(new TextBlock { Text = _partnerType == "SUPPLIER" ? "SUPPLIER" : "CUSTOMER",
            FontSize = 10.5, Foreground = PartnerUi.MutedBrush, Margin = new Thickness(0, 2, 0, 0) });
        titlePanel.Children.Add(heading);
        var badge = new Border { Background = PartnerUi.GoldSoftBrush, BorderBrush = PartnerUi.GoldBrush,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(11, 5, 11, 5),
            Child = new TextBlock { Text = "SALDO TERBUKA", FontWeight = FontWeights.Bold, Foreground = PartnerUi.MaroonDarkBrush } };
        Grid.SetColumn(badge, 1);
        titlePanel.Children.Add(badge);
        var title = PartnerUi.Card(titlePanel, new Thickness(14, 8, 14, 8), new Thickness(12, 10, 12, 8));
        title.BorderThickness = new Thickness(4, 1, 1, 1);
        title.BorderBrush = PartnerUi.MaroonBrush;
        root.Children.Add(title);

        var filters = new Grid();
        filters.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        filters.ColumnDefinitions.Add(new());
        filters.ColumnDefinitions.Add(new() { Width = new GridLength(112) });
        var label = new TextBlock { Text = "Cari Partner / Bukti", Foreground = PartnerUi.MutedBrush,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        filters.Children.Add(label);
        Grid.SetColumn(_query, 1);
        _query.Margin = new Thickness(0, 0, 7, 0);
        _query.HorizontalAlignment = HorizontalAlignment.Stretch;
        filters.Children.Add(_query);
        var show = PartnerUi.Button("Tampilkan", 106, PartnerButtonKind.Primary, "\uE721", "Tampilkan laporan");
        show.Margin = new Thickness(0);
        show.Click += async (_, _) => await ReloadAsync();
        Grid.SetColumn(show, 2);
        filters.Children.Add(show);
        var filterCard = PartnerUi.Card(filters, new Thickness(10, 7, 10, 7), new Thickness(12, 0, 12, 8));
        Grid.SetRow(filterCard, 1);
        root.Children.Add(filterCard);

        Grid.SetRow(_grid, 2);
        _grid.Margin = new Thickness(12, 0, 12, 8);
        root.Children.Add(_grid);

        var totals = new Grid { Margin = new Thickness(12, 0, 12, 8) };
        for (var i = 0; i < 4; i++) totals.ColumnDefinitions.Add(new());
        AddTotal(totals, 0, "Nilai Awal", _original);
        AddTotal(totals, 1, "Koreksi Pengurang", _correction);
        AddTotal(totals, 2, _partnerType == "SUPPLIER" ? "Sudah Dibayar" : "Sudah Diterima", _settled);
        AddTotal(totals, 3, "Sisa Saldo", _remaining);
        Grid.SetRow(totals, 3);
        root.Children.Add(totals);

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new());
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _status.Foreground = PartnerUi.MutedBrush;
        footer.Children.Add(_status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var excel = PartnerUi.Button("Excel", 88, PartnerButtonKind.Standard, "\uE896", "Ekspor ke Excel");
        excel.Click += (_, _) => ReportGridTools.ExportDataGridToExcel(_grid, ReportTitle);
        var print = PartnerUi.Button("Cetak", 88, PartnerButtonKind.Gold, "\uE749", "Cetak laporan");
        print.Click += (_, _) => ReportGridTools.PrintDataGrid(_grid, ReportTitle);
        var close = PartnerUi.Button("Tutup", 86, PartnerButtonKind.Standard, "\uE8BB", "Tutup laporan");
        close.Margin = new Thickness(0);
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        actions.Children.Add(excel);
        actions.Children.Add(print);
        actions.Children.Add(close);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        var footerCard = PartnerUi.Card(footer, new Thickness(8), new Thickness(12, 0, 12, 10));
        Grid.SetRow(footerCard, 4);
        root.Children.Add(footerCard);
        return root;
    }

    private void ConfigureColumns()
    {
        _grid.Columns.Add(Column("Tanggal", nameof(OutstandingRow.DocumentDate), 90, "dd/MM/yyyy"));
        _grid.Columns.Add(Column("No. Bukti", nameof(OutstandingRow.DocumentNumber), 115));
        _grid.Columns.Add(Column("Sumber", nameof(OutstandingRow.SourceLabel), 105));
        _grid.Columns.Add(Column(_partnerType == "SUPPLIER" ? "Kode Supl." : "Kode Cust.", nameof(OutstandingRow.PartnerCode), 90));
        _grid.Columns.Add(Column("Toko/PT", nameof(OutstandingRow.PartnerName), 215));
        _grid.Columns.Add(Column("J.Tempo", nameof(OutstandingRow.DueDate), 90, "dd/MM/yyyy"));
        _grid.Columns.Add(NumberColumn("Nilai Awal", nameof(OutstandingRow.OriginalAmount), 105));
        _grid.Columns.Add(NumberColumn("Koreksi", nameof(OutstandingRow.CorrectionAmount), 95));
        _grid.Columns.Add(NumberColumn(_partnerType == "SUPPLIER" ? "Dibayar" : "Diterima", nameof(OutstandingRow.SettledAmount), 100));
        _grid.Columns.Add(NumberColumn("Sisa Saldo", nameof(OutstandingRow.RemainingAmount), 110));
    }

    private async Task ReloadAsync()
    {
        try
        {
            IsEnabled = false;
            _status.Text = "Mengambil saldo berjalan...";
            IReadOnlyList<OutstandingRow> rows;
            if (_partnerType == "SUPPLIER")
            {
                var data = await _api.GetSupplierOutstandingReportAsync(_query.Text);
                rows = data.Select(x => new OutstandingRow(x.DocumentDate, x.DocumentNumber, x.SourceLabel, x.SupplierCode,
                    x.SupplierName, x.DueDate, x.BalanceDue + x.CorrectionAmount, x.CorrectionAmount,
                    x.PaidAmount, x.RemainingBalance)).ToArray();
            }
            else
            {
                var data = await _api.GetCustomerOutstandingReportAsync(_query.Text);
                rows = data.Select(x => new OutstandingRow(x.DocumentDate, x.DocumentNumber, x.SourceLabel, x.CustomerCode,
                    x.CustomerName, x.DueDate, x.BalanceDue + x.CorrectionAmount, x.CorrectionAmount,
                    x.PaidAmount, x.RemainingBalance)).ToArray();
            }

            _grid.ItemsSource = rows;
            _original.Text = rows.Sum(x => x.OriginalAmount).ToString("N2");
            _correction.Text = rows.Sum(x => x.CorrectionAmount).ToString("N2");
            _settled.Text = rows.Sum(x => x.SettledAmount).ToString("N2");
            _remaining.Text = rows.Sum(x => x.RemainingAmount).ToString("N2");
            _status.Text = $"{rows.Count:N0} saldo terbuka | Data lunas tidak ditampilkan";
        }
        catch (Exception exception)
        {
            _status.Text = "Laporan gagal dimuat";
            MessageBox.Show(exception.Message, $"AKSARA - {ReportTitle}", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private static DataGridTextColumn Column(string header, string property, DataGridLength width, string? format = null) =>
        new() { Header = header, Binding = new Binding(property) { StringFormat = format }, Width = width };

    private static DataGridTextColumn NumberColumn(string header, string property, double width) =>
        new()
        {
            Header = header,
            Binding = new Binding(property) { StringFormat = "N2" },
            Width = width,
            ElementStyle = RightAlignedStyle()
        };

    private static Style RightAlignedStyle()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
        return style;
    }

    private static TextBlock TotalText(bool bold = false) => new()
    {
        Text = "0,00",
        TextAlignment = TextAlignment.Right,
        FontWeight = bold ? FontWeights.Bold : FontWeights.SemiBold,
        FontSize = bold ? 13 : 11,
        Foreground = PartnerUi.MaroonDarkBrush
    };

    private static void AddTotal(Grid grid, int column, string label, TextBlock value)
    {
        var border = new Border
        {
            BorderBrush = column == 3 ? PartnerUi.GoldBrush : PartnerUi.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(column == 0 ? 0 : 6, 0, 0, 0),
            Padding = new Thickness(9, 6, 9, 6),
            Background = column == 3 ? PartnerUi.GoldSoftBrush : PartnerUi.SurfaceBrush
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label, Foreground = PartnerUi.MutedBrush, FontSize = 9.5,
            FontWeight = FontWeights.SemiBold });
        panel.Children.Add(value);
        border.Child = panel;
        Grid.SetColumn(border, column);
        grid.Children.Add(border);
    }

    private sealed record OutstandingRow(
        DateOnly DocumentDate,
        string DocumentNumber,
        string SourceLabel,
        string PartnerCode,
        string PartnerName,
        DateOnly? DueDate,
        decimal OriginalAmount,
        decimal CorrectionAmount,
        decimal SettledAmount,
        decimal RemainingAmount);
}
