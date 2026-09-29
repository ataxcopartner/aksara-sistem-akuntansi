using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

internal sealed record PrintColumn(string Label, double Weight, bool Numeric = false);
internal sealed record PrintContent(
    string Key, string Title, IReadOnlyList<(string Label, string Value)> LeftInfo,
    IReadOnlyList<(string Label, string Value)> RightInfo, IReadOnlyList<PrintColumn> Columns,
    IReadOnlyList<string[]> Rows, IReadOnlyList<(string Label, string Value)> Totals, string? Notes);

internal static class DocumentPrintRenderer
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("id-ID");
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(126, 22, 39));

    public static PrintContent FromTransaction(PrintableTransaction document)
    {
        var showCode = ProgramConfigurationCache.Current.ShowItemCode;
        var columns = new List<PrintColumn> { new("No.", 30) };
        if (showCode) columns.Add(new("Kode", 78));
        columns.AddRange([new("Nama Barang", showCode ? 190 : 268), new("Banyak", 60, true),
            new("Sat.", 42), new("Harga", 94, true), new("Disc.", 50, true), new("Jumlah", 105, true)]);
        var rows = document.Lines.Select(line =>
        {
            var cells = new List<string> { line.No.ToString(Culture) };
            if (showCode) cells.Add(line.Code);
            cells.AddRange([line.Name, Money(line.Quantity), line.Unit, Money(line.UnitPrice), Money(line.DiscountPercent), Money(line.Total)]);
            return cells.ToArray();
        }).ToArray();
        return new(DocumentPrintFormats.ForTitle(document.Title), ProgramConfigurationCache.ResolveTitle(document.Title),
            [(document.NumberLabel, document.Number), ("Tanggal", document.Date.ToString("dd/MM/yyyy", Culture)),
                (document.PartnerLabel, $"{document.PartnerCode} {document.PartnerName}".Trim()), ("Alamat", document.Address ?? "")],
            [("Transaksi", document.TransactionType.ToUpperInvariant() switch { "CASH" => "Tunai", "CREDIT" => "Kredit", _ => document.TransactionType }),
                ("Gudang", document.WarehouseCode), ("Sales", document.SalesCode ?? ""),
                ("Jatuh Tempo", document.DueDate?.ToString("dd/MM/yyyy", Culture) ?? "")],
            columns, rows, [("Subtotal", Money(document.Subtotal)), ("Discount", Money(document.DiscountAmount)),
                ("PPN", Money(document.TaxAmount)), ("Total", Money(document.GrandTotal))], document.Notes);
    }

    public static PrintContent FromCashBank(CashBankReceipt receipt)
    {
        var key = DocumentPrintFormats.ForCashBank(receipt.TransactionKind);
        return new(key, ProgramConfigurationCache.Current.CashBankTitle,
            [("No. Bukti", receipt.DocumentNumber), ("Tanggal", receipt.DocumentDate.ToString("dd/MM/yyyy", Culture)),
                ("Jenis", key == "cash-payment" ? "Pengeluaran" : "Penerimaan")],
            [("Cara", receipt.Method switch { "BANK" => "Bank", "CHECK_GIRO" => "Cek / Giro", _ => "Kas" }),
                ("Akun", $"{receipt.SettlementLabel} {receipt.SettlementName}".Trim()), ("Jurnal", receipt.JournalNumber)],
            [new("No.", 34), new("Keterangan", 300), new("Group", 72), new("Perkiraan", 90), new("Jumlah", 118, true)],
            receipt.Lines.Select((line, index) => new[] { (index + 1).ToString(Culture),
                string.IsNullOrWhiteSpace(line.Description) ? line.AccountName : line.Description,
                line.GroupCode, line.AccountCode, Money(line.Amount) }).ToArray(), [("Total", Money(receipt.Total))], null);
    }

    public static PrintContent Sample(string key)
    {
        var kind = DocumentPrintFormats.Get(key);
        if (key.StartsWith("cash-", StringComparison.Ordinal))
            return FromCashBank(new("CONTOH-KB-001", new DateOnly(2026, 9, 7), key == "cash-payment" ? "PAYMENT" : "RECEIPT",
                "CASH", "KAS", "Kas Utama", "CONTOH-JR-001",
                [new("KAS", "Kas Utama", key == "cash-payment" ? "Pengeluaran kas untuk biaya transport" : "Penerimaan kas dari pelanggan",
                    "OPERASIONAL", "1101", "Kas Utama", 150000m)]));
        var purchase = key.StartsWith("purchase-", StringComparison.Ordinal);
        return FromTransaction(new(kind.Title, key.EndsWith("return", StringComparison.Ordinal) ? "No. Retur" : purchase ? "No. Faktur" : "No. Nota",
            "CONTOH-001", new DateOnly(2026, 9, 7), purchase ? "Supplier" : "Customer", "CONTOH", "Toko Contoh",
            "Jl. Contoh No. 10", "CREDIT", "UTAMA", "S01", new DateOnly(2026, 10, 7), 450000m, 0m, 0m, 450000m,
            "Dokumen contoh", [new(1, "BRG-001", "Barang Contoh A", 2m, "PCS", 125000m, 0m, 250000m),
                new(2, "BRG-002", "Barang Contoh B", 4m, "BOX", 50000m, 0m, 200000m)]));
    }

    public static FixedDocument Build(PrintContent content, double pageWidth = 793, double pageHeight = 1122,
        DocumentPrintFormat? format = null)
    {
        format ??= DocumentPrintFormatStore.Shared.Load(content.Key);
        DocumentPrintFormatStore.Validate(format);
        pageWidth = double.IsFinite(pageWidth) && pageWidth > 0 ? pageWidth : 793;
        pageHeight = double.IsFinite(pageHeight) && pageHeight > 0 ? pageHeight : 1122;
        var margin = Math.Min(38, pageWidth * .06);
        var width = pageWidth - margin * 2;
        var top = Math.Min(30, pageHeight * .04);
        var bottom = pageHeight - top - 20;
        var result = new FixedDocument();
        result.DocumentPaginator.PageSize = new Size(pageWidth, pageHeight);
        var rows = content.Rows.Cast<string[]?>().ToList();
        while (rows.Count < format.MinimumRows) rows.Add(null);
        var offset = 0;
        do
        {
            var page = new FixedPage { Width = pageWidth, Height = pageHeight, Background = Brushes.White };
            var header = Fit(Header(content, format, width), width, (bottom - top) * .35);
            Place(page, header, margin, top);
            var y = top + Height(header, width);
            var tableHeader = TableRow(content.Columns.Select(x => x.Label).ToArray(), content.Columns, width, format, true);
            Place(page, tableHeader, margin, y);
            y += Height(tableHeader, width);
            var ending = Fit(Ending(content, format, width), width, (bottom - top) * .4);
            var endingHeight = Height(ending, width);
            var remainingHeight = bottom - y;

            // Measure complete rows before allocating pages. Never crop rows or the signature block.
            var candidates = new List<(FrameworkElement Element, double Height)>();
            var sum = 0d;
            for (var i = offset; i < rows.Count; i++)
            {
                var row = Fit(TableRow(rows[i], content.Columns, width, format, false), width,
                    Math.Max(1, remainingHeight - endingHeight));
                var height = Height(row, width);
                if (candidates.Count > 0 && sum + height > remainingHeight + .1) break;
                candidates.Add((row, height));
                sum += height;
            }
            var lastPage = offset + candidates.Count == rows.Count && sum + endingHeight <= remainingHeight + .1;
            if (!lastPage && offset + candidates.Count == rows.Count && candidates.Count > 1)
            {
                // Keep at least one detail row with totals on the final page.
                candidates.RemoveAt(candidates.Count - 1);
            }
            foreach (var row in candidates)
            {
                Place(page, row.Element, margin, y);
                y += row.Height;
            }
            offset += candidates.Count;
            if (lastPage)
            {
                // Totals follow the table; signatures stay near the bottom of the last page.
                var totals = Fit(TotalsAndNotes(content, width, format), width, endingHeight);
                Place(page, totals, margin, y);
                var signatures = Fit(Signatures(width, format), width, Math.Max(1, bottom - y - Height(totals, width)));
                Place(page, signatures, margin, bottom - Height(signatures, width));
            }
            Place(page, Text($"{result.Pages.Count + 1}", 8, false, TextAlignment.Right, width), margin, pageHeight - top);
            page.Measure(new Size(pageWidth, pageHeight));
            page.Arrange(new Rect(0, 0, pageWidth, pageHeight));
            page.UpdateLayout();
            var pageContent = new PageContent();
            ((IAddChild)pageContent).AddChild(page);
            result.Pages.Add(pageContent);
        } while (offset < rows.Count);
        return result;
    }

    private static FrameworkElement Header(PrintContent content, DocumentPrintFormat format, double width)
    {
        if (format.HeaderLayout is { } layout) return PrintHeaderRenderer.Build(layout, content, format, width);
        var modern = format.Template == PrintTemplate.Modern;
        var compact = format.Template == PrintTemplate.Compact;
        var root = Panel(width, format);
        if (format.Letterhead == LetterheadKind.Image && format.ImageBase64 is not null)
            root.Children.Add(new Image { Source = DocumentPrintFormatStore.DecodeImage(format.ImageBase64),
                Width = Math.Min(width, 420), Height = format.ImageHeight, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) });
        else
        {
            root.Children.Add(Text(format.CompanyName, compact ? 13 : 16, true, modern ? TextAlignment.Left : TextAlignment.Center));
            var settings = ProgramConfigurationCache.Current;
            var address = string.Join(" | ", new[] { settings.Address, settings.City, settings.Phone }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (address.Length > 0) root.Children.Add(Text(address, 9, false, modern ? TextAlignment.Left : TextAlignment.Center));
        }
        var title = Text(content.Title.ToUpperInvariant(), compact ? 15 : 20, true, modern ? TextAlignment.Left : TextAlignment.Center);
        title.Margin = new Thickness(0, 10, 0, 12);
        if (modern) title.Foreground = Accent;
        root.Children.Add(title);
        if (modern) root.Children.Add(new Border { Height = 3, Background = Accent, Margin = new Thickness(0, 0, 0, 12) });
        var info = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        info.ColumnDefinitions.Add(new ColumnDefinition());
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        info.ColumnDefinitions.Add(new ColumnDefinition());
        var left = Info(content.LeftInfo, compact);
        var right = Info(content.RightInfo, compact);
        Grid.SetColumn(right, 2);
        info.Children.Add(left);
        info.Children.Add(right);
        root.Children.Add(info);
        return root;
    }

    private static Grid Info(IReadOnlyList<(string Label, string Value)> rows, bool compact)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = Text(rows[i].Label, compact ? 9 : 10);
            var value = Text(": " + rows[i].Value, compact ? 9 : 10, true);
            label.Margin = value.Margin = new Thickness(0, 0, 3, 4);
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }
        return grid;
    }

    private static Grid TableRow(string[]? cells, IReadOnlyList<PrintColumn> columns, double width,
        DocumentPrintFormat format, bool heading)
    {
        var compact = format.Template == PrintTemplate.Compact;
        var modern = format.Template == PrintTemplate.Modern;
        var grid = new Grid { Width = width, MinHeight = compact ? 19 : 24,
            Tag = heading ? "print-table-heading" : "print-table-row" };
        var totalWeight = columns.Sum(x => x.Weight);
        for (var i = 0; i < columns.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width * columns[i].Weight / totalWeight) });
            var text = Text(cells?[i] ?? "", compact ? 9 : 10, heading,
                heading ? TextAlignment.Center : columns[i].Numeric ? TextAlignment.Right : TextAlignment.Left);
            if (heading && modern) text.Foreground = Brushes.White;
            var border = new Border
            {
                BorderBrush = modern ? Accent : Brushes.Gray,
                BorderThickness = format.Template == PrintTemplate.Classic ? new Thickness(.5)
                    : compact ? new Thickness(.5, 0, .5, .5) : new Thickness(0, 0, 0, .5),
                Background = heading ? modern ? Accent : new SolidColorBrush(Color.FromRgb(238, 238, 238)) : Brushes.White,
                Padding = new Thickness(compact ? 3 : 4, compact ? 2 : 4, compact ? 3 : 4, compact ? 2 : 4),
                Child = text
            };
            Grid.SetColumn(border, i);
            grid.Children.Add(border);
        }
        return grid;
    }

    private static StackPanel Ending(PrintContent content, DocumentPrintFormat format, double width)
    {
        var panel = Panel(width, format);
        panel.Children.Add(TotalsAndNotes(content, width, format));
        panel.Children.Add(Signatures(width, format));
        return panel;
    }

    private static StackPanel TotalsAndNotes(PrintContent content, double width, DocumentPrintFormat format)
    {
        var panel = Panel(width, format);
        panel.Margin = new Thickness(0, 10, 0, 12);
        foreach (var (row, index) in content.Totals.Select((row, index) => (row, index)))
        {
            var total = new Grid { Width = Math.Min(285, width), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 2) };
            total.ColumnDefinitions.Add(new ColumnDefinition());
            total.ColumnDefinitions.Add(new ColumnDefinition());
            var bold = index == content.Totals.Count - 1;
            total.Children.Add(Text(row.Label, 11, bold));
            var amount = Text(row.Value, 11, bold, TextAlignment.Right);
            Grid.SetColumn(amount, 1);
            total.Children.Add(amount);
            panel.Children.Add(total);
        }
        if (!string.IsNullOrWhiteSpace(content.Notes))
        {
            var notes = Text("Catatan: " + content.Notes, 10);
            notes.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(notes);
        }
        return panel;
    }

    private static StackPanel Signatures(double width, DocumentPrintFormat format)
    {
        var panel = Panel(width, format);
        var settings = ProgramConfigurationCache.Current;
        var signatures = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        var labels = new[] { settings.Signature1, settings.Signature2, settings.Signature3 };
        var images = new[] { format.Signature1Image, format.Signature2Image, format.Signature3Image };
        var names = new[] { format.Signature1Name, format.Signature2Name, null };
        for (var i = 0; i < labels.Length; i++)
        {
            signatures.ColumnDefinitions.Add(new ColumnDefinition());
            var column = new StackPanel { Margin = new Thickness(12, 0, 12, 0) };
            column.Children.Add(Text(labels[i], 10, false, TextAlignment.Center));
            var signatureArea = new Border { Height = 58, BorderBrush = Brushes.Black, BorderThickness = new Thickness(0, 0, 0, .6) };
            if (images[i] is { } signature)
                signatureArea.Child = new Image
                {
                    Source = DocumentPrintFormatStore.DecodeImage(signature.ImageBase64),
                    Stretch = Stretch.Uniform, MaxHeight = 50, Margin = new Thickness(8, 3, 8, 3),
                    VerticalAlignment = VerticalAlignment.Center, Tag = $"signature-image-{i}"
                };
            column.Children.Add(signatureArea);
            var nameText = string.IsNullOrWhiteSpace(names[i]) ? "(                         )" : $"({names[i]!.Trim()})";
            var name = Text(nameText, 9, false, TextAlignment.Center);
            name.Margin = new Thickness(0, 4, 0, 0);
            column.Children.Add(name);
            Grid.SetColumn(column, i);
            signatures.Children.Add(column);
        }
        panel.Children.Add(signatures);
        var footer = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.PrintFooterNote)) footer.Add(settings.PrintFooterNote);
        if (settings.ShowPrintedBy) footer.Add($"Dicetak oleh: {AksaraApiClient.Shared.CurrentUserName}");
        if (settings.ShowPrintDateTime) footer.Add(DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", Culture));
        if (footer.Count > 0)
        {
            var text = Text(string.Join("    ", footer), 8);
            text.Margin = new Thickness(0, 12, 0, 0);
            panel.Children.Add(text);
        }
        return panel;
    }

    private static StackPanel Panel(double width, DocumentPrintFormat format)
    {
        var panel = new StackPanel { Width = width };
        TextElement.SetFontFamily(panel, new FontFamily(format.Template switch
        {
            PrintTemplate.Modern => "Segoe UI", PrintTemplate.Compact => "Arial", _ => "Tahoma"
        }));
        return panel;
    }

    private static TextBlock Text(string text, double size, bool bold = false, TextAlignment alignment = TextAlignment.Left, double width = double.NaN)
        => new() { Text = text, FontSize = size, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap, TextAlignment = alignment, Width = width, Foreground = Brushes.Black };

    private static double Height(FrameworkElement element, double width)
    {
        element.Measure(new Size(width, double.PositiveInfinity));
        return element.DesiredSize.Height;
    }

    private static FrameworkElement Fit(FrameworkElement element, double width, double maximumHeight)
    {
        var height = Height(element, width);
        if (height <= maximumHeight) return element;
        return new Viewbox { Width = width, Height = maximumHeight, Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly, Child = element };
    }

    private static void Place(FixedPage page, FrameworkElement element, double x, double y)
    {
        FixedPage.SetLeft(element, x);
        FixedPage.SetTop(element, y);
        page.Children.Add(element);
    }

    private static string Money(decimal value) => value.ToString("N2", Culture);
}
