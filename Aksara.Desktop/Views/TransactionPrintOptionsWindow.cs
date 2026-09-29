using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

internal enum TransactionPrintAction
{
    Preview,
    Print,
    ExportExcel
}

internal sealed record TransactionPrintOption(string Id, string Label);

internal sealed record TransactionPrintHistoryEntry(DateOnly Date, string Number, string Partner, decimal Total, object Source);

internal sealed class TransactionPrintOptionsWindow : Window
{
    private readonly TextBox _numberBox = new() { MinWidth = 130, MaxWidth = 230, Height = 28, CharacterCasing = CharacterCasing.Upper };
    private readonly IReadOnlyList<TransactionPrintHistoryEntry> _history;
    private readonly List<RadioButton> _optionButtons = [];

    public string SelectedNumber => _numberBox.Text.Trim();
    public string SelectedOptionId { get; private set; }
    public TransactionPrintAction SelectedAction { get; private set; }

    public TransactionPrintOptionsWindow(
        string title,
        string numberLabel,
        string currentNumber,
        IReadOnlyList<TransactionPrintOption> options,
        IReadOnlyList<TransactionPrintHistoryEntry> history)
    {
        if (options.Count == 0) throw new ArgumentException("Minimal satu opsi cetak harus tersedia.", nameof(options));

        _history = history;
        SelectedOptionId = options[0].Id;
        Title = title;
        Width = 760;
        Height = 520;
        MinWidth = 620;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Tahoma");
        FontSize = 13;

        var root = new Grid { Background = new SolidColorBrush(Color.FromRgb(245, 245, 245)), Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 20)
        });

        var body = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(24, 34, 24, 34)
        };
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(85) });
        form.ColumnDefinitions.Add(new ColumnDefinition());
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        form.ColumnDefinitions.Add(new ColumnDefinition());
        body.Child = form;

        form.Children.Add(new TextBlock
        {
            Text = numberLabel,
            VerticalAlignment = VerticalAlignment.Center
        });

        _numberBox.Text = currentNumber;
        _numberBox.ToolTip = "Klik, tekan F4, atau double-click untuk membuka daftar transaksi tersimpan.";
        _numberBox.PreviewMouseLeftButtonDown += (_, args) =>
        {
            args.Handled = true;
            OpenHistory();
        };
        _numberBox.MouseDoubleClick += (_, _) => OpenHistory();
        _numberBox.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.F4)
            {
                args.Handled = true;
                OpenHistory();
            }
        };
        Grid.SetColumn(_numberBox, 1);
        form.Children.Add(_numberBox);

        var optionsPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16, 0, 0, 0) };
        Grid.SetColumn(optionsPanel, 3);
        foreach (var option in options)
        {
            var radio = new RadioButton
            {
                Content = option.Label,
                Tag = option.Id,
                GroupName = "PrintOption",
                IsChecked = _optionButtons.Count == 0,
                Margin = new Thickness(0, 0, 0, 12)
            };
            radio.Checked += (_, _) => SelectedOptionId = (string)radio.Tag;
            _optionButtons.Add(radio);
            optionsPanel.Children.Add(radio);
        }
        form.Children.Add(optionsPanel);

        var buttons = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        Grid.SetRow(buttons, 2);
        var format = new Button { Content = "Setting Format", Width = 120, Height = 32, Margin = new Thickness(8, 0, 0, 0) };
        format.Click += (_, _) => new DocumentPrintSettingsWindow(SelectedOptionId) { Owner = this }.ShowDialog();
        buttons.Children.Add(format);
        AddActionButton(buttons, "Preview", TransactionPrintAction.Preview, 104);
        AddActionButton(buttons, "Ekspor Excel", TransactionPrintAction.ExportExcel, 112);
        AddActionButton(buttons, "Cetak", TransactionPrintAction.Print, 104);
        var cancel = new Button { Content = "Batal", Width = 104, Height = 32, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) => _numberBox.Focus();
    }

    private void AddActionButton(Panel target, string text, TransactionPrintAction action, double width)
    {
        var button = new Button { Content = text, Width = width, Height = 32, Margin = new Thickness(8, 0, 0, 0) };
        button.Click += (_, _) =>
        {
            SelectedAction = action;
            DialogResult = true;
        };
        target.Children.Add(button);
    }

    private void OpenHistory()
    {
        if (_history.Count == 0)
        {
            MessageBox.Show("Belum ada transaksi tersimpan untuk dipilih.", "AKSARA", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new TransactionPrintHistoryWindow(_history, SelectedNumber) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SelectedEntry is not null)
            _numberBox.Text = dialog.SelectedEntry.Number;
    }
}

internal sealed class TransactionPrintHistoryWindow : Window
{
    private readonly TextBox _search = new() { Height = 26, Margin = new Thickness(0, 0, 0, 10) };
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        CanUserAddRows = false,
        SelectionMode = DataGridSelectionMode.Single,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.All,
        RowHeight = 25,
        ColumnHeaderHeight = 28
    };
    private readonly IReadOnlyList<TransactionPrintHistoryEntry> _all;

    public TransactionPrintHistoryEntry? SelectedEntry { get; private set; }

    public TransactionPrintHistoryWindow(IReadOnlyList<TransactionPrintHistoryEntry> entries, string currentNumber)
    {
        _all = entries;
        Title = "History Transaksi";
        Width = 900;
        Height = 560;
        MinWidth = 720;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Tahoma");
        FontSize = 12;

        var root = new Grid { Margin = new Thickness(8) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_search);

        _grid.Columns.Add(Column("Tanggal", nameof(TransactionHistoryRow.Date), 120));
        _grid.Columns.Add(Column("No. Bukti", nameof(TransactionHistoryRow.Number), 160));
        _grid.Columns.Add(Column("Relasi", nameof(TransactionHistoryRow.Partner), 360));
        _grid.Columns.Add(Column("Jumlah", nameof(TransactionHistoryRow.Total), 150));
        _grid.MouseDoubleClick += (_, _) => Accept();
        _grid.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                args.Handled = true;
                Accept();
            }
        };
        Grid.SetRow(_grid, 1);
        root.Children.Add(_grid);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var choose = new Button { Content = "Pilih", Width = 104, Height = 31 };
        choose.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Batal", Width = 104, Height = 31, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        footer.Children.Add(choose);
        footer.Children.Add(cancel);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        _search.TextChanged += (_, _) => Reload();
        Loaded += (_, _) =>
        {
            Reload();
            if (!string.IsNullOrWhiteSpace(currentNumber))
            {
                var rows = _grid.ItemsSource as IReadOnlyList<TransactionHistoryRow>;
                var index = rows?.ToList().FindIndex(x => string.Equals(x.Number, currentNumber, StringComparison.OrdinalIgnoreCase)) ?? -1;
                if (index >= 0) _grid.SelectedIndex = index;
            }
            _search.Focus();
        };
    }

    private void Reload()
    {
        var query = _search.Text.Trim();
        var rows = _all
            .Where(x => string.IsNullOrWhiteSpace(query)
                || x.Number.Contains(query, StringComparison.OrdinalIgnoreCase)
                || x.Partner.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(x => new TransactionHistoryRow(x, x.Date.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("id-ID")), x.Number, x.Partner, x.Total.ToString("N2", CultureInfo.GetCultureInfo("id-ID"))))
            .ToList();
        _grid.ItemsSource = rows;
        if (rows.Count > 0) _grid.SelectedIndex = 0;
    }

    private void Accept()
    {
        if (_grid.SelectedItem is not TransactionHistoryRow row) return;
        SelectedEntry = row.Entry;
        DialogResult = true;
    }

    private static DataGridTextColumn Column(string header, string path, double width) => new()
    {
        Header = header,
        Binding = new System.Windows.Data.Binding(path),
        Width = width
    };

    private sealed record TransactionHistoryRow(TransactionPrintHistoryEntry Entry, string Date, string Number, string Partner, string Total);
}

internal sealed record PrintableTransactionLine(int No, string Code, string Name, decimal Quantity, string Unit, decimal UnitPrice, decimal DiscountPercent, decimal Total);

internal sealed record PrintableTransaction(
    string Title,
    string NumberLabel,
    string Number,
    DateOnly Date,
    string PartnerLabel,
    string PartnerCode,
    string PartnerName,
    string? Address,
    string TransactionType,
    string WarehouseCode,
    string? SalesCode,
    DateOnly? DueDate,
    decimal Subtotal,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal GrandTotal,
    string? Notes,
    IReadOnlyList<PrintableTransactionLine> Lines);

internal static class TransactionDocumentOutput
{
    private static readonly CultureInfo IndonesianCulture = CultureInfo.GetCultureInfo("id-ID");

    public static void Preview(PrintableTransaction document)
    {
        ShowPreviewWindow(document, allowPrint: false);
    }

    public static void Print(PrintableTransaction document)
    {
        ShowPreviewWindow(document, allowPrint: true);
    }

    public static void ExportExcel(PrintableTransaction document)
    {
        var rows = document.Lines.Select(line => new TransactionExportRow(
            document.Title,
            document.Number,
            document.Date,
            document.PartnerCode,
            document.PartnerName,
            line.No,
            line.Code,
            line.Name,
            line.Quantity,
            line.Unit,
            line.UnitPrice,
            line.DiscountPercent,
            line.Total)).Cast<object>().ToList();
        if (rows.Count == 0)
            rows.Add(new TransactionExportRow(document.Title, document.Number, document.Date, document.PartnerCode, document.PartnerName, 0, "", "", 0, "", 0, 0, document.GrandTotal));
        ReportGridTools.ExportObjectsToExcel(document.Title, rows);
    }

    private static void ShowPreviewWindow(PrintableTransaction document, bool allowPrint)
    {
        var viewer = new DocumentViewer
        {
            Document = BuildDocument(document, 793, 1122),
            Margin = new Thickness(8)
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(viewer);

        if (allowPrint)
        {
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8)
            };
            var print = new Button { Content = "Cetak...", Width = 104, Height = 31 };
            var close = new Button { Content = "Tutup", Width = 104, Height = 31, Margin = new Thickness(8, 0, 0, 0) };
            buttons.Children.Add(print);
            buttons.Children.Add(close);
            Grid.SetRow(buttons, 1);
            root.Children.Add(buttons);

            print.Click += (_, _) => PrintAfterPreview(document);
            close.Click += (_, _) => Window.GetWindow(close)?.Close();
        }

        var window = new Window
        {
            Title = allowPrint ? $"Cetak - {document.Title}" : $"Preview - {document.Title}",
            Width = 980,
            Height = 720,
            MinWidth = 760,
            MinHeight = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };
        window.ShowDialog();
    }

    private static void PrintAfterPreview(PrintableTransaction document)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;
        var fixedDocument = BuildDocument(
            document,
            dialog.PrintableAreaWidth > 0 ? dialog.PrintableAreaWidth : 793,
            dialog.PrintableAreaHeight > 0 ? dialog.PrintableAreaHeight : 1122);
        dialog.PrintDocument(fixedDocument.DocumentPaginator, document.Title);
    }

    internal static FixedDocument BuildDocument(PrintableTransaction document, double pageWidth, double pageHeight)
        => DocumentPrintRenderer.Build(DocumentPrintRenderer.FromTransaction(document), pageWidth, pageHeight);

    private sealed record TransactionExportRow(
        string Dokumen,
        string Nomor,
        DateOnly Tanggal,
        string KodeRelasi,
        string NamaRelasi,
        int No,
        string KodeBarang,
        string NamaBarang,
        decimal Banyak,
        string Satuan,
        decimal Harga,
        decimal DiskonPersen,
        decimal Jumlah);
}
