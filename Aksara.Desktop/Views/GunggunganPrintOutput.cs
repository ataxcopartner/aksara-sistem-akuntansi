using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Aksara.Contracts;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

internal static class GunggunganPrintOutput
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("id-ID");

    public static void PreviewReceipt(GunggunganSaleResultDto result, Window? owner) =>
        ShowPreview(
            $"Nota {result.Sale.DocumentNumber}",
            (width, height) => BuildReceipt(result, width, height),
            owner);

    public static void PreviewRange(GunggunganSaleReportDto report, Window? owner) =>
        ShowPreview(
            "Rekap Penjualan Gunggungan",
            (width, height) => BuildRange(report, width, height),
            owner);

    private static void ShowPreview(string title, Func<double, double, FlowDocument> build, Window? owner)
    {
        var viewer = new DocumentViewer
        {
            Document = build(793, 1122),
            Margin = new Thickness(8),
            Background = new SolidColorBrush(Color.FromRgb(232, 228, 229))
        };
        var print = new Button { Content = "Cetak...", Width = 104, Height = 31 };
        var close = new Button { Content = "Tutup", Width = 92, Height = 31, Margin = new Thickness(8, 0, 0, 0) };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8)
        };
        actions.Children.Add(print);
        actions.Children.Add(close);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(viewer);
        Grid.SetRow(actions, 1);
        root.Children.Add(actions);

        var window = new Window
        {
            Title = $"AKSARA - {title}",
            Width = 980,
            Height = 720,
            MinWidth = 760,
            MinHeight = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root,
            ShowInTaskbar = false
        };
        if (owner is { IsVisible: true }) window.Owner = owner;
        close.Click += (_, _) => window.Close();
        print.Click += (_, _) =>
        {
            var dialog = new PrintDialog();
            if (dialog.ShowDialog() != true) return;
            var document = build(
                dialog.PrintableAreaWidth > 0 ? dialog.PrintableAreaWidth : 793,
                dialog.PrintableAreaHeight > 0 ? dialog.PrintableAreaHeight : 1122);
            dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, title);
        };
        window.ShowDialog();
    }

    internal static FlowDocument BuildReceipt(GunggunganSaleResultDto result, double width, double height)
    {
        var sale = result.Sale;
        var document = CreateDocument(width, height);
        AddHeading(document, "NOTA PENJUALAN GUNGGUNGAN");
        document.Blocks.Add(new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 8),
            Inlines =
            {
                new Run($"No. Nota: {sale.DocumentNumber}\n") { FontWeight = FontWeights.Bold },
                new Run($"Tanggal: {sale.DocumentDate:dd/MM/yyyy}\n"),
                new Run($"Gudang: {sale.WarehouseCode}    Kas: {sale.CashAccountName ?? sale.CashAccountCode ?? "-"}\n"),
                new Run("Pembayaran: TUNAI")
            }
        });

        document.Blocks.Add(Table(
            ["No.", "Kode", "Nama Barang", "Qty", "Sat.", "Harga", "Jumlah"],
            sale.Lines.Select((line, index) => new[]
            {
                (index + 1).ToString(Culture), line.ItemCode, line.ItemName,
                Number(line.Quantity), line.Unit, Money(line.UnitPrice), Money(line.LineTotal)
            }),
            [28, 76, 230, 58, 45, 90, 100],
            [3, 5, 6]));

        document.Blocks.Add(Summary(
            ("Total", Money(sale.GrandTotal)),
            ("Bayar tunai", Money(result.CashReceived)),
            ("Kembalian", Money(result.Change))));
        if (!string.IsNullOrWhiteSpace(sale.Notes))
            document.Blocks.Add(new Paragraph(new Run($"Catatan: {sale.Notes}")) { Margin = new Thickness(0, 12, 0, 0) });
        AddFooter(document);
        return document;
    }

    internal static FlowDocument BuildRange(GunggunganSaleReportDto report, double width, double height)
    {
        var rows = report.Rows.Where(row => row.Status == "POSTED").ToArray();
        var document = CreateDocument(width, height);
        AddHeading(document, "REKAP PENJUALAN GUNGGUNGAN");
        document.Blocks.Add(new Paragraph(new Run(
            $"Periode {report.From:dd/MM/yyyy} s/d {report.To:dd/MM/yyyy}"))
        {
            TextAlignment = TextAlignment.Center,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 10)
        });

        document.Blocks.Add(Table(
            ["Tanggal", "No. Nota", "Kode", "Nama Barang", "Qty", "Sat.", "Harga", "Jumlah"],
            rows.Select(row => new[]
            {
                row.DocumentDate.ToString("dd/MM/yyyy", Culture), row.DocumentNumber, row.ItemCode, row.ItemName,
                Number(row.Quantity), row.Unit, Money(row.UnitPrice), Money(row.LineTotal)
            }),
            [64, 82, 72, 190, 52, 42, 82, 92],
            [4, 6, 7]));

        document.Blocks.Add(Summary(
            ("Jumlah transaksi", report.TransactionCount.ToString("N0", Culture)),
            ("Jumlah unit", Number(report.TotalQuantity)),
            ("Total penjualan", Money(report.TotalSales))));
        AddFooter(document);
        return document;
    }

    private static FlowDocument CreateDocument(double width, double height) => new()
    {
        FontFamily = new FontFamily("Tahoma"),
        FontSize = 9,
        PagePadding = new Thickness(34),
        PageWidth = width,
        PageHeight = height,
        ColumnWidth = double.PositiveInfinity,
        Background = Brushes.White
    };

    private static void AddHeading(FlowDocument document, string title)
    {
        document.Blocks.Add(new Paragraph(new Run(ProgramConfigurationCache.PrintCompanyName))
        {
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 2)
        });
        var settings = ProgramConfigurationCache.Current;
        var address = string.Join(" | ", new[] { settings.Address, settings.City, settings.Phone }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (!string.IsNullOrWhiteSpace(address))
            document.Blocks.Add(new Paragraph(new Run(address)) { TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 5) });
        document.Blocks.Add(new Paragraph(new Run(title))
        {
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 5),
            Margin = new Thickness(0, 0, 0, 10)
        });
    }

    private static Table Table(
        IReadOnlyList<string> headers,
        IEnumerable<string[]> rows,
        IReadOnlyList<double> widths,
        IReadOnlyCollection<int> numericColumns)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var value in widths) table.Columns.Add(new TableColumn { Width = new GridLength(value) });
        var group = new TableRowGroup();
        var header = new TableRow { Background = new SolidColorBrush(Color.FromRgb(239, 225, 228)), FontWeight = FontWeights.Bold };
        for (var index = 0; index < headers.Count; index++) header.Cells.Add(Cell(headers[index], false));
        group.Rows.Add(header);
        foreach (var values in rows)
        {
            var row = new TableRow();
            for (var index = 0; index < values.Length; index++) row.Cells.Add(Cell(values[index], numericColumns.Contains(index)));
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    private static TableCell Cell(string value, bool numeric) => new(new Paragraph(new Run(value))
    {
        Margin = new Thickness(1),
        TextAlignment = numeric ? TextAlignment.Right : TextAlignment.Left
    })
    {
        Padding = new Thickness(3, 2, 3, 2),
        BorderBrush = new SolidColorBrush(Color.FromRgb(170, 170, 170)),
        BorderThickness = new Thickness(.5)
    };

    private static Table Summary(params (string Label, string Value)[] values)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(360, 2, 0, 0) };
        table.Columns.Add(new TableColumn { Width = new GridLength(120) });
        table.Columns.Add(new TableColumn { Width = new GridLength(120) });
        var group = new TableRowGroup();
        foreach (var value in values)
        {
            var row = new TableRow();
            row.Cells.Add(new TableCell(new Paragraph(new Run(value.Label))) { Padding = new Thickness(3, 2, 3, 2) });
            var amount = new Paragraph(new Run(value.Value))
            {
                Margin = new Thickness(0),
                TextAlignment = TextAlignment.Right,
                FontWeight = FontWeights.Bold
            };
            row.Cells.Add(new TableCell(amount) { Padding = new Thickness(3, 2, 3, 2) });
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    private static void AddFooter(FlowDocument document)
    {
        var note = ProgramConfigurationCache.Current.PrintFooterNote;
        document.Blocks.Add(new Paragraph(new Run(string.IsNullOrWhiteSpace(note) ? "Terima kasih." : note))
        {
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 22, 0, 0),
            FontStyle = FontStyles.Italic
        });
    }

    private static string Number(decimal value) => value.ToString("N2", Culture);
    private static string Money(decimal value) => value.ToString("N2", Culture);
}
