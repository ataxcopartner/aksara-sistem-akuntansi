using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Aksara.Contracts;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

internal enum CashBankPrintAction
{
    Print,
    Preview,
    ExportExcel
}

internal sealed class CashBankPrintOptionsWindow : Window
{
    private readonly IReadOnlyList<CashBankTransactionDto> _transactions;
    private readonly TextBox _numberBox;
    private readonly TextBlock _detailCount;

    public string DocumentNumber => _numberBox.Text.Trim();
    public CashBankTransactionDto? SelectedTransaction { get; private set; }
    public CashBankPrintAction SelectedAction { get; private set; } = CashBankPrintAction.Print;

    public CashBankPrintOptionsWindow(string documentNumber, IEnumerable<CashBankTransactionDto> transactions)
    {
        _transactions = transactions.ToList();
        SelectedTransaction = _transactions.FirstOrDefault(x => SameDocument(x.DocumentNumber, documentNumber));
        Title = "Cetak Bukti Kas Bank";
        Width = 620;
        Height = 360;
        MinWidth = 620;
        MinHeight = 360;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.WhiteSmoke;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = "Cetak Bukti Kas Bank",
            FontSize = 23,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.Black,
            Margin = new Thickness(0, 0, 0, 14)
        };
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var body = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(224, 224, 224)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(24, 20, 24, 20),
            Margin = new Thickness(0, 0, 0, 12)
        };

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddLabel(content, "No.", 0, 0, new Thickness(6, 2, 8, 0));
        _numberBox = new TextBox
        {
            Text = SelectedTransaction?.DocumentNumber ?? documentNumber,
            Height = 26,
            Width = 220,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _numberBox.PreviewMouseLeftButtonDown += NumberBox_PreviewMouseLeftButtonDown;
        _numberBox.KeyDown += NumberBox_KeyDown;
        Grid.SetRow(_numberBox, 0);
        Grid.SetColumn(_numberBox, 1);
        content.Children.Add(_numberBox);

        var standardCheck = new CheckBox
        {
            Content = "Standar",
            IsChecked = true,
            Margin = new Thickness(8, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetRow(standardCheck, 0);
        Grid.SetColumn(standardCheck, 3);
        content.Children.Add(standardCheck);

        AddLabel(content, "Jumlah Rincian", 1, 0, new Thickness(6, 24, 8, 0));
        _detailCount = new TextBlock
        {
            Text = "1",
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 24, 6, 0)
        };
        Grid.SetRow(_detailCount, 1);
        Grid.SetColumn(_detailCount, 1);
        content.Children.Add(_detailCount);

        var hint = new TextBlock
        {
            Text = "Klik kotak nomor atau tekan F4 untuk memilih transaksi kas-bank.",
            Foreground = Brushes.DimGray,
            FontSize = 12,
            Margin = new Thickness(6, 22, 0, 0)
        };
        Grid.SetRow(hint, 2);
        Grid.SetColumn(hint, 0);
        Grid.SetColumnSpan(hint, 4);
        content.Children.Add(hint);

        body.Child = content;
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var format = new Button { Content = "Setting Format", Width = 120, Height = 28, Margin = new Thickness(4, 0, 0, 0) };
        format.Click += (_, _) =>
        {
            SelectTypedDocument();
            new DocumentPrintSettingsWindow(DocumentPrintFormats.ForCashBank(SelectedTransaction?.TransactionKind)) { Owner = this }.ShowDialog();
        };
        buttons.Children.Add(format);
        buttons.Children.Add(CommandButton("Preview", CashBankPrintAction.Preview, 92));
        buttons.Children.Add(CommandButton("Ekspor Excel", CashBankPrintAction.ExportExcel, 112));
        buttons.Children.Add(CommandButton("Cetak", CashBankPrintAction.Print, 92, true));

        var cancel = new Button
        {
            Content = "Batal",
            Width = 92,
            Height = 28,
            Margin = new Thickness(4, 0, 0, 0),
            IsCancel = true
        };
        cancel.Click += (_, _) =>
        {
            DialogResult = false;
            Close();
        };

        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        Content = root;
        RefreshDetailCount();
    }

    private Button CommandButton(string text, CashBankPrintAction action, double width, bool isDefault = false)
    {
        var button = new Button
        {
            Content = text,
            Width = width,
            Height = 28,
            Margin = new Thickness(4, 0, 0, 0),
            IsDefault = isDefault
        };
        button.Click += (_, _) =>
        {
            SelectedAction = action;
            SelectTypedDocument();
            DialogResult = true;
            Close();
        };
        return button;
    }

    private void NumberBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F4 && e.Key != Key.Enter) return;
        e.Handled = true;
        ShowTransactionLookup();
    }

    private void NumberBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_transactions.Count == 0) return;
        e.Handled = true;
        _numberBox.Focus();
        _numberBox.SelectAll();
        ShowTransactionLookup();
    }

    private void ShowTransactionLookup()
    {
        if (_transactions.Count == 0)
        {
            MessageBox.Show("Belum ada transaksi kas-bank yang bisa dipilih.", "AKSARA - Kas Bank", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var lookup = new CashBankTransactionHistoryWindow(_transactions)
        {
            Owner = this,
            InitialDocumentNumber = _numberBox.Text
        };
        if (lookup.ShowDialog() == true && lookup.SelectedTransaction is not null)
        {
            SelectedTransaction = lookup.SelectedTransaction;
            _numberBox.Text = lookup.SelectedTransaction.DocumentNumber;
            RefreshDetailCount();
        }
    }

    private void SelectTypedDocument()
    {
        SelectedTransaction = _transactions.FirstOrDefault(x => SameDocument(x.DocumentNumber, _numberBox.Text)) ?? SelectedTransaction;
        RefreshDetailCount();
    }

    private void RefreshDetailCount()
    {
        _detailCount.Text = SelectedTransaction is null ? "1" : "1";
    }

    private static void AddLabel(Grid grid, string text, int row, int column, Thickness margin)
    {
        var label = new TextBlock
        {
            Text = text,
            Margin = margin,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = Brushes.Black
        };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, column);
        grid.Children.Add(label);
    }

    private static bool SameDocument(string? left, string? right) =>
        string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
}

internal sealed record CashBankReceiptLine(
    string SettlementCode,
    string SettlementName,
    string Description,
    string GroupCode,
    string AccountCode,
    string AccountName,
    decimal Amount);

internal sealed record CashBankReceipt(
    string DocumentNumber,
    DateOnly DocumentDate,
    string TransactionKind,
    string Method,
    string SettlementLabel,
    string SettlementName,
    string JournalNumber,
    IReadOnlyList<CashBankReceiptLine> Lines)
{
    public decimal Total => Lines.Sum(x => x.Amount);
}

internal static class CashBankReceiptOutput
{
    public static void Preview(CashBankReceipt receipt, Window? owner)
    {
        ShowPreviewWindow(receipt, owner, allowPrint: false);
    }

    public static void Print(CashBankReceipt receipt)
    {
        ShowPreviewWindow(receipt, null, allowPrint: true);
    }

    public static void ExportExcel(CashBankReceipt receipt)
    {
        var rows = receipt.Lines.Select((line, index) => new CashBankReceiptExportRow(
            index + 1,
            receipt.DocumentNumber,
            receipt.DocumentDate.ToString("dd/MM/yyyy"),
            receipt.TransactionKind,
            receipt.Method,
            line.SettlementCode,
            line.SettlementName,
            line.Description,
            line.GroupCode,
            line.AccountCode,
            line.AccountName,
            line.Amount)).Cast<object>().ToList();

        ReportGridTools.ExportObjectsToExcel($"Bukti Kas Bank {receipt.DocumentNumber}", rows);
    }

    private static void ShowPreviewWindow(CashBankReceipt receipt, Window? owner, bool allowPrint)
    {
        var viewer = new DocumentViewer
        {
            Document = BuildDocument(receipt, 794, 1123),
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
            print.Click += (_, _) => PrintAfterPreview(receipt);
            close.Click += (_, _) => Window.GetWindow(close)?.Close();
            buttons.Children.Add(print);
            buttons.Children.Add(close);
            Grid.SetRow(buttons, 1);
            root.Children.Add(buttons);
        }

        var window = new Window
        {
            Title = allowPrint ? $"Cetak Bukti Kas Bank - {receipt.DocumentNumber}" : $"Preview Bukti Kas Bank - {receipt.DocumentNumber}",
            Width = 840,
            Height = 720,
            MinWidth = 720,
            MinHeight = 540,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };
        window.ShowDialog();
    }

    private static void PrintAfterPreview(CashBankReceipt receipt)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;

        var document = BuildDocument(receipt, dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
        dialog.PrintDocument(document.DocumentPaginator, $"Bukti Kas Bank {receipt.DocumentNumber}");
    }

    internal static FixedDocument BuildDocument(CashBankReceipt receipt, double printableWidth, double printableHeight)
        => DocumentPrintRenderer.Build(DocumentPrintRenderer.FromCashBank(receipt), printableWidth, printableHeight);
}

internal sealed record CashBankReceiptExportRow(
    int No,
    string NomorBukti,
    string Tanggal,
    string Jenis,
    string Cara,
    string KodeKasBank,
    string NamaKasBank,
    string Keterangan,
    string Group,
    string KodePerkiraan,
    string NamaPerkiraan,
    decimal Jumlah);
