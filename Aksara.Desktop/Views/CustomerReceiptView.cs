using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Aksara.Contracts;
using Aksara.Desktop.Controls;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

public sealed class CustomerReceiptView : UserControl, IWorkspaceCloseGuard
{
    private const string LocalDraftKey = "customer-receipt";
    private const int DefaultRows = 10;

    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly ObservableCollection<CustomerReceiptLineDraft> _lines = [];
    private readonly DatePicker _date = new() { SelectedDate = DateTime.Today, Width = 135 };
    private readonly TextBox _documentNumber = ClassicBox("OTOMATIS", 160);
    private readonly TextBox _customerCode = ClassicBox(string.Empty, 135);
    private readonly TextBox _customerName = ClassicBox(string.Empty, 360, readOnly: true);
    private readonly TextBox _customerAddress = ClassicBox(string.Empty, 520, readOnly: true);
    private readonly TextBox _collectorCode = ClassicBox(string.Empty, 100);
    private readonly TextBox _group = ClassicBox(string.Empty, 135);
    private readonly TextBox _searchNumber = ClassicBox(string.Empty, 180);
    private readonly TextBox _totalReceivable = MoneyBox();
    private readonly TextBox _currentReceipt = MoneyBox();
    private readonly TextBox _cashCode = ClassicBox(string.Empty, 95);
    private readonly TextBox _cashName = ClassicBox(string.Empty, 175, readOnly: true);
    private readonly TextBox _cashAmount = MoneyBox();
    private readonly TextBox _bankCode = ClassicBox(string.Empty, 95);
    private readonly TextBox _bankName = ClassicBox(string.Empty, 175, readOnly: true);
    private readonly TextBox _bankAmount = MoneyBox();
    private readonly TextBox _giroBankCode = ClassicBox(string.Empty, 95);
    private readonly TextBox _giroBankName = ClassicBox(string.Empty, 175, readOnly: true);
    private readonly TextBox _giroNumber = ClassicBox(string.Empty, 125);
    private readonly TextBox _giroDueDate = ClassicBox(string.Empty, 135);
    private readonly DatePicker _receivablesUntilDate = new() { SelectedDate = DateTime.Today, Width = 118 };
    private readonly TextBox _giroAmount = MoneyBox();
    private readonly CheckBox _autoCash = new() { Content = "Isi Kas Otomatis", Margin = new Thickness(6, 0, 0, 0) };
    private readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 56, BorderThickness = new Thickness(0), Background = Brushes.White };
    private readonly TextBox _summaryCashAmount = SummaryMoneyBox();
    private readonly TextBox _summaryBankAmount = SummaryMoneyBox();
    private readonly TextBox _summaryGiroAmount = SummaryMoneyBox();
    private readonly TextBox _summaryReceipt = SummaryMoneyBox();
    private readonly TextBox _summaryAdjustment = SummaryMoneyBox();
    private readonly TextBox _summaryUnbalance = SummaryMoneyBox();
    private readonly ComboBox _summaryStatus = new() { Width = 70, Height = 24, MinHeight = 0 };
    private readonly Grid _contentHost = new();
    private readonly Grid _receiptMethodPanel = new();
    private readonly TextBlock _modeStatus = new() { Text = "TAMBAH DATA", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _totalData = new() { Text = "TOTAL DATA 0", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { Text = "PENERIMAAN PIUTANG", Foreground = Brushes.Red, FontSize = 26, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hint = new() { Text = "Tekan F4 pada keyboard / DOUBLE CLICK tombol kiri mouse pada kolom ini untuk membuka form cari CUSTOMER / NO. BUKTI", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        CanUserAddRows = false,
        CanUserDeleteRows = false,
        CanUserSortColumns = false,
        RowHeight = 24,
        ColumnHeaderHeight = 24,
        SelectionMode = DataGridSelectionMode.Single,
        SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
        Background = Brushes.White
    };

    private SalesCustomerDto? _selectedCustomer;
    private CustomerCheckGiroDto? _selectedCheckGiro;
    private CustomerReceiptDto? _currentReceiptRecord;
    private IReadOnlyList<CashAccountDto> _cashAccounts = [];
    private IReadOnlyList<BankAccountDto> _bankAccounts = [];
    private IReadOnlyList<CustomerReceiptDto> _receiptHistory = [];
    private TabControl? _detailTabs;
    private Grid? _receivablesToolbar;
    private Button? _deleteButton;
    private Button? _restoreButton;
    private int _historyIndex = -1;
    private bool _loading;
    private bool _saving;
    private string _cleanDraftSignature = string.Empty;
    private bool _draftChecked;

    public event EventHandler? CloseRequested;

    public CustomerReceiptView()
    {
        _date.Language = XmlLanguage.GetLanguage("id-ID");
        _receivablesUntilDate.Language = XmlLanguage.GetLanguage("id-ID");
        BuildColumns();
        Content = Build();
        AddBlankRows();

        Loaded += async (_, _) =>
        {
            await LoadMastersAsync();
            await PreviewNumberAsync();
            await ReloadHistoryCountAsync();
            RestoreLocalDraft();
            MarkClean();
        };
        _date.SelectedDateChanged += async (_, _) => await PreviewNumberAsync();
        _customerCode.MouseDoubleClick += (_, _) => OpenCustomerLookup();
        _customerCode.PreviewKeyDown += CustomerCode_PreviewKeyDown;
        _customerCode.LostFocus += async (_, _) => await TryResolveCustomerAsync(_customerCode.Text);
        _searchNumber.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key is Key.Enter or Key.F4) { e.Handled = true; await LoadReceivablesAsync(); }
        };
        _grid.MouseDoubleClick += (_, _) => FillSelectedLine();
        _grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(UpdateTotals);
        _grid.CurrentCellChanged += (_, _) => UpdateTotals();
        _autoCash.Checked += (_, _) => FillCashAutomatically();
        _autoCash.Unchecked += (_, _) => { };
        WireLookup(_cashCode, OpenCashLookup);
        WireLookup(_bankCode, () => OpenBankLookup(_bankCode, _bankName));
        WireLookup(_giroNumber, OpenCheckGiroLookup);
        WireLookup(_collectorCode, OpenCollectorLookup);
        _currentReceipt.TextChanged += (_, _) => SyncMethodSummary();
        _cashAmount.TextChanged += (_, _) => SyncMethodSummary();
        _bankAmount.TextChanged += (_, _) => SyncMethodSummary();
        _giroAmount.TextChanged += (_, _) => SyncMethodSummary();
    }

    private UIElement Build()
    {
        var root = new Grid { Margin = new Thickness(7), Background = Brush("#F0F0F0") };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddStatusStrip(root);
        AddHeader(root);
        AddTitleAndSearch(root);
        AddContentArea(root);
        AddReceiptArea(root);
        AddCommandBar(root);
        AddHintBar(root);
        UpdateTabView();
        return root;
    }

    private void AddStatusStrip(Grid root)
    {
        var strip = new Grid { Height = 34, Margin = new Thickness(7, 0, 7, 0) };
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        strip.ColumnDefinitions.Add(new ColumnDefinition());
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(238) });
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        AddStripCell(strip, new CheckBox { Content = "Exclusive Mode", IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) }, 0);
        AddStripCell(strip, new TextBlock { Text = "Status:", Margin = new Thickness(15, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }, 1);
        AddStripCell(strip, _modeStatus, 2);
        AddStripCell(strip, _totalData, 3);
        AddStripCell(strip, new TextBlock { Text = "▥", FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, 4);
        Grid.SetRow(strip, 0);
        root.Children.Add(strip);
    }

    private void AddHeader(Grid root)
    {
        var border = new Border { Margin = new Thickness(7, 0, 7, 8), BorderBrush = Brushes.Silver, BorderThickness = new Thickness(1), Background = Brush("#F4F4F4"), Padding = new Thickness(7, 4, 7, 4) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(640) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });

        _date.Width = 125;
        _documentNumber.Width = 145;
        _customerCode.Width = 160;
        _customerName.Width = 500;
        _customerAddress.Width = 500;
        _collectorCode.Width = 95;

        var documentGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        documentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        documentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(155) });
        documentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        documentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        AddLabel(documentGrid, "Tgl.Bukti", 0, 0); AddInput(documentGrid, _date, 0, 1);
        AddLabel(documentGrid, "No.Bukti", 1, 0); AddInput(documentGrid, _documentNumber, 1, 1);
        Grid.SetColumn(documentGrid, 0);
        Grid.SetRowSpan(documentGrid, 3);
        grid.Children.Add(documentGrid);

        var customerGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        customerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        customerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(520) });
        customerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        customerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        customerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        AddLabel(customerGrid, "Kode Cust.", 0, 0); AddInput(customerGrid, _customerCode, 0, 1);
        AddLabel(customerGrid, "Toko/PT", 1, 0); AddInput(customerGrid, _customerName, 1, 1);
        AddLabel(customerGrid, "Alamat", 2, 0); AddInput(customerGrid, _customerAddress, 2, 1);
        Grid.SetColumn(customerGrid, 2);
        Grid.SetRowSpan(customerGrid, 3);
        grid.Children.Add(customerGrid);

        var collectorGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Right };
        collectorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        collectorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(102) });
        collectorGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        AddLabel(collectorGrid, "Penagih", 0, 0); AddInput(collectorGrid, _collectorCode, 0, 1);
        Grid.SetColumn(collectorGrid, 3);
        grid.Children.Add(collectorGrid);

        border.Child = grid;
        Grid.SetRow(border, 1); root.Children.Add(border);
    }

    private void AddTitleAndSearch(Grid root)
    {
        var grid = new Grid { Margin = new Thickness(7, 0, 7, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _detailTabs = new TabControl { Height = 34, Padding = new Thickness(0), Background = Brush("#F0F0F0") };
        _detailTabs.Items.Add(new TabItem { Header = "Piutang (Transaksi yang Dilunasi) .1", IsSelected = true });
        _detailTabs.Items.Add(new TabItem { Header = "Penerimaan Berupa (Uang yang Diterima) .2" });
        _detailTabs.SelectionChanged += (_, _) => UpdateTabView();
        grid.Children.Add(_detailTabs);

        _receivablesToolbar = new Grid { Height = 32, Margin = new Thickness(28, 0, 28, 0) };
        _receivablesToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _receivablesToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _receivablesToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _receivablesToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _receivablesToolbar.ColumnDefinitions.Add(new ColumnDefinition());
        _receivablesToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _receivablesToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
        AddToolbarText(_receivablesToolbar, "Bukti belum lunas s/d Tgl.", 0);
        AddToolbarInput(_receivablesToolbar, _receivablesUntilDate, 1);
        var addButton = ToolbarButton("Tambahkan pada Rincian", async () => await LoadReceivablesAsync(), 140);
        var clearButton = ToolbarButton("Kosongkan Rincian", ClearAmounts, 130);
        Grid.SetColumn(addButton, 2); _receivablesToolbar.Children.Add(addButton);
        Grid.SetColumn(clearButton, 3); _receivablesToolbar.Children.Add(clearButton);
        AddToolbarText(_receivablesToolbar, "Saldo", 5);
        AddToolbarInput(_receivablesToolbar, _totalReceivable, 6);
        Grid.SetRow(_receivablesToolbar, 1);
        grid.Children.Add(_receivablesToolbar);

        Grid.SetRow(grid, 2); root.Children.Add(grid);
    }

    private void AddContentArea(Grid root)
    {
        _grid.ItemsSource = _lines;
        _grid.Margin = new Thickness(7, 0, 7, 0);
        _contentHost.Children.Add(_grid);
        BuildReceiptMethodPanel();
        _contentHost.Children.Add(_receiptMethodPanel);
        Grid.SetRow(_contentHost, 3);
        root.Children.Add(_contentHost);
    }

    private void BuildReceiptMethodPanel()
    {
        _receiptMethodPanel.Width = 1080;
        _receiptMethodPanel.Margin = new Thickness(7, 0, 7, 0);
        _receiptMethodPanel.HorizontalAlignment = HorizontalAlignment.Center;
        _receiptMethodPanel.VerticalAlignment = VerticalAlignment.Top;
        _receiptMethodPanel.Background = Brush("#F0F0F0");
        _receiptMethodPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _receiptMethodPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _receiptMethodPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _cashCode.Width = 80;
        _cashName.Width = 245;
        _cashAmount.Width = 150;
        _bankCode.Width = 80;
        _bankName.Width = 245;
        _bankAmount.Width = 150;
        _giroBankCode.Width = 115;
        _giroBankName.Width = 235;
        _giroNumber.Width = 136;
        _giroAmount.Width = 150;

        AddMethodSection(_receiptMethodPanel, 0, "K A S",
            ("Kode", 85, _cashCode),
            ("Nama", 250, _cashName),
            ("Keterangan", 550, BlankMethodBox()),
            ("Jumlah", 155, _cashAmount));

        AddMethodSection(_receiptMethodPanel, 1, "B A N K",
            ("Kode", 85, _bankCode),
            ("Nama", 250, _bankName),
            ("Keterangan", 550, BlankMethodBox()),
            ("Jumlah", 155, _bankAmount));

        var giroInfo = new Grid();
        giroInfo.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        giroInfo.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(245) });
        Grid.SetColumn(_giroBankCode, 0); giroInfo.Children.Add(_giroBankCode);
        Grid.SetColumn(_giroBankName, 1); giroInfo.Children.Add(_giroBankName);

        AddMethodSection(_receiptMethodPanel, 2, "CEK / GIRO",
            ("No.Urut", 145, _giroNumber),
            ("Informasi Cek / Giro", 365, giroInfo),
            ("Keterangan", 375, BlankMethodBox()),
            ("Nilai", 155, _giroAmount));

        var newCheckGiroButton = ToolbarButton("Tambah Data Baru - Penerimaan Cek/Giro", async () => await OpenCheckGiroEditorAsync(), 280);
        newCheckGiroButton.HorizontalAlignment = HorizontalAlignment.Right;
        newCheckGiroButton.VerticalAlignment = VerticalAlignment.Top;
        newCheckGiroButton.Margin = new Thickness(0, 0, 38, 0);
        Grid.SetRow(newCheckGiroButton, 2);
        _receiptMethodPanel.Children.Add(newCheckGiroButton);
    }

    private void AddMethodSection(Grid parent, int row, string title, params (string Header, double Width, UIElement Child)[] columns)
    {
        var totalWidth = columns.Sum(column => column.Width);
        var section = new StackPanel
        {
            Width = totalWidth,
            Margin = new Thickness(20, row == 0 ? 0 : 5, 20, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        section.Children.Add(new Border
        {
            Width = 155,
            Height = 19,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brush("#F8F8F8"),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = title,
                Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold
            }
        });

        var table = new Grid { Width = totalWidth };
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        foreach (var column in columns)
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(column.Width) });

        for (var index = 0; index < columns.Length; index++)
        {
            AddMethodHeaderCell(table, columns[index].Header, 0, index);
            AddMethodInputCell(table, columns[index].Child, 1, index);
            WireMethodInput(columns[index].Child, table, columns, columns[index].Header);
            var blankInput = CreateMethodRowInput(columns[index].Header);
            AddMethodInputCell(table, blankInput, 2, index);
            WireMethodInput(blankInput, table, columns, columns[index].Header);
        }

        section.Children.Add(table);
        Grid.SetRow(section, row);
        parent.Children.Add(section);
    }

    private void AddReceiptArea(Grid root)
    {
        var grid = new Grid
        {
            Width = 1080,
            Height = 68,
            Margin = new Thickness(7, 8, 7, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = Brush("#F0F0F0")
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        foreach (var width in new[] { 160d, 130d, 55d, 130d, 110d, 125d, 110d, 125d, 60d, 75d })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });

        _currentReceipt.Padding = new Thickness(2, 0, 5, 0);

        Grid.SetRow(_autoCash, 0);
        Grid.SetColumn(_autoCash, 0);
        Grid.SetColumnSpan(_autoCash, 2);
        grid.Children.Add(_autoCash);

        AddReceiptSummaryField(grid, "Bank", _summaryBankAmount, 0, 2, 3);
        AddReceiptSummaryField(grid, "Kas", _summaryCashAmount, 1, 0, 1);
        AddReceiptSummaryField(grid, "C/G", _summaryGiroAmount, 1, 2, 3);
        AddReceiptSummaryField(grid, "Piut.dibayar", _currentReceipt, 0, 4, 5);
        AddReceiptSummaryField(grid, "Penerimaan", _summaryReceipt, 1, 4, 5);
        AddReceiptSummaryField(grid, "Penyesuaian", _summaryAdjustment, 0, 6, 7);
        AddReceiptSummaryField(grid, "Unbalance", _summaryUnbalance, 1, 6, 7);
        AddReceiptSummaryField(grid, "Status", _summaryStatus, 0, 8, 9);

        Grid.SetRow(grid, 4);
        root.Children.Add(grid);
        SyncMethodSummary();
    }

    private void AddCommandBar(Grid root)
    {
        var grid = new Grid { Margin = new Thickness(7, 8, 7, 0), Height = 32 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var nav = new StackPanel { Orientation = Orientation.Horizontal };
        AddNav(nav, "◀◀", async () => await GuardedAsync(() => LoadReceiptHistoryAtAsync(_receiptHistory.Count - 1)));
        AddNav(nav, "◀", async () => await GuardedAsync(() => MoveReceiptHistoryAsync(1)));
        AddNav(nav, "●", async () => await GuardedAsync(ShowReceiptHistoryAsync));
        AddNav(nav, "▶", async () => await GuardedAsync(() => MoveReceiptHistoryAsync(-1)));
        AddNav(nav, "▶▶", async () => await GuardedAsync(() => LoadReceiptHistoryAtAsync(0)));
        grid.Children.Add(nav);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(ActionButton("Cari", async () => await GuardedAsync(ShowReceiptHistoryAsync), 86));
        actions.Children.Add(ActionButton("Tambah", async () => await GuardedAsync(() => { ClearForm(); return Task.CompletedTask; }), 86));
        actions.Children.Add(ActionButton("Batal", CancelInputAsync, 86));
        actions.Children.Add(ActionButton("Simpan", async () => { await SaveAsync(); }, 92));
        actions.Children.Add(ActionButton("Smpn&Ctk", async () => { await SaveAsync(); }, 98));
        _deleteButton = ActionButton("Hapus", async () => await DeleteOrClearAsync(), 86);
        _restoreButton = ActionButton("Btl.Hapus", async () => await RestoreCurrentReceiptAsync(), 92);
        actions.Children.Add(_deleteButton);
        actions.Children.Add(_restoreButton);
        actions.Children.Add(ActionButton("Tutup", () => CloseRequested?.Invoke(this, EventArgs.Empty), 86));
        Grid.SetColumn(actions, 1); grid.Children.Add(actions);
        Grid.SetRow(grid, 5); root.Children.Add(grid);
        UpdateCommandStates();
    }

    private void AddHintBar(Grid root)
    {
        var grid = new Grid { Height = 38, Margin = new Thickness(7, 2, 7, 0), Background = Brushes.White };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Brush("#F4A322"), BorderBrush = Brush("#C87900"), BorderThickness = new Thickness(1), Child = new TextBlock { Text = "i", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        Grid.SetColumn(icon, 0); grid.Children.Add(icon);
        Grid.SetColumn(_hint, 1); grid.Children.Add(_hint);
        Grid.SetRow(grid, 6); root.Children.Add(grid);
        Grid.SetRow(_status, 7); root.Children.Add(_status);
    }

    private void BuildColumns()
    {
        _grid.Columns.Add(LedgerAccountView.Column("Group", nameof(CustomerReceiptLineDraft.Group), 82));
        _grid.Columns.Add(LedgerAccountView.Column("Tgl.Bukti", nameof(CustomerReceiptLineDraft.DocumentDate), 105, "dd/MM/yyyy"));
        _grid.Columns.Add(LedgerAccountView.Column("No.Bukti", nameof(CustomerReceiptLineDraft.DocumentNumber), 125));
        _grid.Columns.Add(LedgerAccountView.Column("Sales", nameof(CustomerReceiptLineDraft.SalesCode), 72));
        _grid.Columns.Add(LedgerAccountView.NumberColumn("Nilai", nameof(CustomerReceiptLineDraft.OriginalValue), 125));
        _grid.Columns.Add(LedgerAccountView.NumberColumn("Sudah Bayar", nameof(CustomerReceiptLineDraft.PaidBefore), 125));
        _grid.Columns.Add(LedgerAccountView.NumberColumn("Sisa Bayar", nameof(CustomerReceiptLineDraft.RemainingBalance), 125));
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Skrg. Bayar",
            Binding = new Binding(nameof(CustomerReceiptLineDraft.ReceiptText)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            ElementStyle = LedgerAccountView.RightAlignedStyle()
        });
    }

    private async Task LoadMastersAsync()
    {
        try
        {
            _cashAccounts = await _api.GetCashAccountsAsync(false);
            _bankAccounts = await _api.GetBankAccountsAsync(false);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
        }
    }

    private async Task PreviewNumberAsync()
    {
        if (_loading) return;
        try
        {
            var date = DateOnly.FromDateTime(_date.SelectedDate ?? DateTime.Today);
            if (string.IsNullOrWhiteSpace(_documentNumber.Text) || _documentNumber.Text == "OTOMATIS")
                _documentNumber.Text = await _api.GetNextCustomerReceiptNumberAsync(date);
        }
        catch
        {
            if (string.IsNullOrWhiteSpace(_documentNumber.Text)) _documentNumber.Text = "OTOMATIS";
        }
    }

    private async Task ReloadHistoryCountAsync()
    {
        try
        {
            _receiptHistory = await _api.SearchCustomerReceiptsAsync(null);
            SyncReceiptHistoryIndex();
            UpdateReceiptHistoryCounter();
        }
        catch
        {
            _receiptHistory = [];
            _historyIndex = -1;
            _totalData.Text = "TOTAL DATA 0";
        }
    }

    private async Task LoadReceivablesAsync()
    {
        if (_selectedCustomer is null || !Same(_selectedCustomer.Code, _customerCode.Text))
        {
            if (!await TryResolveCustomerAsync(_customerCode.Text)) return;
        }

        try
        {
            _loading = true;
            IsEnabled = false;
            _status.Text = "Mengambil daftar nota piutang...";
            var rows = (await _api.SearchCustomerReceivablesAsync(_selectedCustomer!.Code, _searchNumber.Text)).ToList();
            _lines.Clear();
            foreach (var row in rows)
                _lines.Add(CustomerReceiptLineDraft.From(row));
            AddBlankRows();
            SetTotalReceivable(rows.Sum(x => x.RemainingBalance));
            _status.Text = rows.Count == 0 ? "Tidak ada nota piutang terbuka untuk customer ini." : $"{rows.Count:N0} nota piutang terbuka.";
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Penerimaan Piutang");
        }
        finally
        {
            IsEnabled = true;
            _loading = false;
        }
    }

    private async void CustomerCode_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F4)
        {
            e.Handled = true;
            OpenCustomerLookup();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await TryResolveCustomerAsync(_customerCode.Text);
        }
    }

    private void OpenCustomerLookup()
    {
        var dialog = new CustomerLookupWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.SelectedCustomer is not null)
        {
            ApplyCustomer(dialog.SelectedCustomer);
            _ = LoadReceivablesAsync();
        }
    }

    private async Task<bool> TryResolveCustomerAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        try
        {
            var rows = await _api.SearchCustomersAsync(code);
            var customer = rows.FirstOrDefault(x => Same(x.Code, code)) ?? rows.FirstOrDefault();
            if (customer is null) return false;
            ApplyCustomer(customer);
            return true;
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            return false;
        }
    }

    private void ApplyCustomer(SalesCustomerDto customer)
    {
        _selectedCustomer = customer;
        _customerCode.Text = customer.Code;
        _customerName.Text = customer.CompanyName;
        _customerAddress.Text = customer.Address ?? string.Empty;
    }

    private void FillSelectedLine()
    {
        if (_grid.SelectedItem is not CustomerReceiptLineDraft line || line.IsBlank) return;
        line.ReceiptText = line.RemainingBalance.ToString("N0", CultureInfo.CurrentCulture);
        UpdateTotals();
        if (_autoCash.IsChecked == true) FillCashAutomatically();
    }

    private void UpdateTotals()
    {
        if (_loading) return;
        var selectedTotal = _lines.Where(x => !x.IsBlank).Sum(x => TryReadAmount(x.ReceiptText, out var value) ? value : 0);
        _currentReceipt.Text = selectedTotal.ToString("N0", CultureInfo.CurrentCulture);
        if (_autoCash.IsChecked == true) FillCashAutomatically();
    }

    private void FillCashAutomatically()
    {
        if (!TryReadAmount(_currentReceipt.Text, out var amount)) amount = _lines.Where(x => !x.IsBlank).Sum(x => TryReadAmount(x.ReceiptText, out var value) ? value : 0);
        if (amount <= 0) return;
        if (string.IsNullOrWhiteSpace(_cashCode.Text) && _cashAccounts.Count > 0) ApplyCash(_cashAccounts[0]);
        _cashAmount.Text = amount.ToString("N0", CultureInfo.CurrentCulture);
        _bankAmount.Text = "0";
        _giroAmount.Text = "0";
    }

    private async Task<bool> SaveAsync()
    {
        if (_saving) return false;
        try
        {
            _saving = true;
            CommitPendingGridEdit();
            if (_selectedCustomer is null || !Same(_selectedCustomer.Code, _customerCode.Text))
                if (!await TryResolveCustomerAsync(_customerCode.Text)) throw new InvalidOperationException("Customer belum dipilih.");
            if (!TryReadAmount(_currentReceipt.Text, out var receiptAmount) || receiptAmount <= 0) throw new InvalidOperationException("Skrg Bayar harus diisi.");
            var requestLines = _lines
                .Where(x => !x.IsBlank && TryReadAmount(x.ReceiptText, out var amount) && amount > 0)
                .Select(x => new CreateCustomerReceiptLineRequest(x.SaleId, ParseAmount(x.ReceiptText), x.SourceType))
                .ToArray();
            if (requestLines.Length == 0) throw new InvalidOperationException("Pilih nota piutang dahulu.");

            var method = ResolveMethod(receiptAmount, out var cashCode, out var bankCode, out var checkGiroId);
            IsEnabled = false;
            _status.Text = "Menyimpan penerimaan piutang dan jurnal...";
            var saved = await _api.CreateCustomerReceiptAsync(new CreateCustomerReceiptRequest(
                DateOnly.FromDateTime(_date.SelectedDate ?? DateTime.Today),
                _documentNumber.Text,
                _selectedCustomer!.Code,
                string.IsNullOrWhiteSpace(_collectorCode.Text) ? null : _collectorCode.Text.Trim().ToUpperInvariant(),
                method,
                cashCode,
                bankCode,
                checkGiroId,
                receiptAmount,
                _notes.Text,
                AksaraApiClient.Shared.CurrentUserName,
                Environment.MachineName,
                requestLines));
            LocalTransactionDraftStore.Delete(LocalDraftKey);
            _status.Text = $"Penerimaan {saved.DocumentNumber} tersimpan. Jurnal: {saved.JournalNumber}.";
            await ReloadHistoryCountAsync();
            ClearAmounts();
            ClearReceiptMethods();
            await LoadReceivablesAsync();
            _documentNumber.Clear();
            await PreviewNumberAsync();
            MarkClean();
            return true;
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Penerimaan Piutang");
            return false;
        }
        finally
        {
            _saving = false;
            IsEnabled = true;
        }
    }

    private string ResolveMethod(decimal receiptAmount, out string? cashCode, out string? bankCode, out Guid? checkGiroId)
    {
        var cash = ParseAmount(_cashAmount.Text);
        var bank = ParseAmount(_bankAmount.Text);
        var giro = ParseAmount(_giroAmount.Text);
        var filled = new[] { cash > 0, bank > 0, giro > 0 }.Count(x => x);
        if (filled == 0) throw new InvalidOperationException("Isi metode terima Kas, Bank, atau C/G.");
        if (filled > 1) throw new InvalidOperationException("Untuk tahap ini satu penerimaan memakai satu metode terima dulu.");
        if (cash + bank + giro != receiptAmount) throw new InvalidOperationException("Total metode terima harus sama dengan Skrg Bayar.");
        cashCode = null; bankCode = null; checkGiroId = null;
        if (cash > 0)
        {
            cashCode = _cashCode.Text;
            return "CASH";
        }
        if (bank > 0)
        {
            bankCode = _bankCode.Text;
            return "BANK";
        }

        if (_selectedCheckGiro is null || !Same(_selectedCheckGiro.SequenceNumber, _giroNumber.Text))
            throw new InvalidOperationException("Pilih data C/G milik customer ini terlebih dahulu.");
        checkGiroId = _selectedCheckGiro.Id;
        return "CHECK_GIRO";
    }

    public async Task<bool> ConfirmCloseAsync() => await ConfirmPendingChangesAsync(discardOnNo: true);

    private async Task GuardedAsync(Func<Task> action)
    {
        if (!await ConfirmPendingChangesAsync(discardOnNo: false)) return;
        await action();
    }

    private async Task<bool> ConfirmPendingChangesAsync(bool discardOnNo)
    {
        if (!HasUnsavedChanges()) return true;

        var choice = LocalDraftPromptWindow.Show(Window.GetWindow(this), "Penerimaan Piutang");
        switch (choice)
        {
            case LocalDraftChoice.SaveDraft:
                try
                {
                    SaveLocalDraft();
                    MarkClean();
                    return true;
                }
                catch (Exception exception)
                {
                    ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Draft Penerimaan Piutang");
                    return false;
                }
            case LocalDraftChoice.Discard:
                LocalTransactionDraftStore.Delete(LocalDraftKey);
                if (discardOnNo) MarkClean();
                return true;
            default:
                _status.Text = "Aksi dibatalkan. Data belum disimpan tetap di form.";
                return false;
        }
    }

    private async Task CancelInputAsync()
    {
        if (!await ConfirmPendingChangesAsync(discardOnNo: true)) return;
        ClearForm();
    }

    private bool HasUnsavedChanges()
    {
        CommitPendingGridEdit();
        return HasMeaningfulDraftInput() && !Same(CurrentDraftSignature(), _cleanDraftSignature);
    }

    private bool HasMeaningfulDraftInput()
    {
        if (!string.IsNullOrWhiteSpace(_notes.Text)) return true;
        if (ParseAmount(_currentReceipt.Text) > 0 || ParseAmount(_cashAmount.Text) > 0 || ParseAmount(_bankAmount.Text) > 0 || ParseAmount(_giroAmount.Text) > 0) return true;
        if (!string.IsNullOrWhiteSpace(_cashCode.Text) || !string.IsNullOrWhiteSpace(_bankCode.Text) || !string.IsNullOrWhiteSpace(_giroBankCode.Text)) return true;
        if (!string.IsNullOrWhiteSpace(_giroNumber.Text) || !string.IsNullOrWhiteSpace(_collectorCode.Text)) return true;
        return _lines.Any(line => !line.IsBlank && ParseAmount(line.ReceiptText) > 0);
    }

    private string CurrentDraftSignature()
    {
        var lineSignature = string.Join("|", _lines
            .Where(line => !line.IsBlank)
            .Select(line => $"{line.SaleId:N}:{NormalizeForSignature(line.ReceiptText)}"));
        return string.Join("\u001F",
            FormatDate(_date.SelectedDate is null ? null : DateOnly.FromDateTime(_date.SelectedDate.Value)),
            NormalizeForSignature(_documentNumber.Text),
            NormalizeForSignature(_customerCode.Text),
            NormalizeForSignature(_collectorCode.Text),
            NormalizeForSignature(_currentReceipt.Text),
            NormalizeForSignature(_cashCode.Text),
            NormalizeForSignature(_cashAmount.Text),
            NormalizeForSignature(_bankCode.Text),
            NormalizeForSignature(_bankAmount.Text),
            NormalizeForSignature(_giroBankCode.Text),
            NormalizeForSignature(_giroNumber.Text),
            _selectedCheckGiro?.Id.ToString("N") ?? string.Empty,
            NormalizeForSignature(_giroAmount.Text),
            NormalizeForSignature(_notes.Text),
            lineSignature);
    }

    private void MarkClean()
    {
        CommitPendingGridEdit();
        _cleanDraftSignature = CurrentDraftSignature();
    }

    private void SaveLocalDraft()
    {
        CommitPendingGridEdit();
        LocalTransactionDraftStore.Save(LocalDraftKey, new CustomerReceiptLocalDraft(
            _date.SelectedDate,
            _documentNumber.Text,
            _customerCode.Text,
            _customerName.Text,
            _customerAddress.Text,
            _collectorCode.Text,
            _group.Text,
            _searchNumber.Text,
            _receivablesUntilDate.SelectedDate,
            _currentReceipt.Text,
            _cashCode.Text,
            _cashName.Text,
            _cashAmount.Text,
            _bankCode.Text,
            _bankName.Text,
            _bankAmount.Text,
            _giroBankCode.Text,
            _giroBankName.Text,
            _giroNumber.Text,
            _giroAmount.Text,
            _autoCash.IsChecked == true,
            _notes.Text,
            _selectedCheckGiro,
            _lines.Where(line => !line.IsBlank)
                .Select(line => new CustomerReceiptLineLocalDraft(
                    line.SaleId, line.Group, line.DocumentNumber, line.DocumentDate,
                    line.SalesCode, line.DueDate, line.CustomerName,
                    line.OriginalValue, line.PaidBefore, line.RemainingBalance, line.ReceiptText, line.SourceType))
                .ToArray()));
        _modeStatus.Text = "DRAFT LOKAL";
        _status.Text = "Draft Penerimaan Piutang tersimpan lokal dan belum masuk jurnal.";
    }

    private void RestoreLocalDraft()
    {
        if (_draftChecked) return;
        _draftChecked = true;
        if (!LocalTransactionDraftStore.TryLoad<CustomerReceiptLocalDraft>(LocalDraftKey, out var draft) || draft is null) return;

        _currentReceiptRecord = null;
        _selectedCustomer = null;
        _date.SelectedDate = draft.DocumentDate ?? DateTime.Today;
        _documentNumber.Text = draft.DocumentNumber;
        _customerCode.Text = draft.CustomerCode;
        _customerName.Text = draft.CustomerName;
        _customerAddress.Text = draft.CustomerAddress;
        _collectorCode.Text = draft.CollectorCode;
        _group.Text = draft.Group;
        _searchNumber.Text = draft.SearchNumber;
        _receivablesUntilDate.SelectedDate = draft.ReceivablesUntilDate ?? DateTime.Today;
        _autoCash.IsChecked = false;
        _currentReceipt.Text = draft.CurrentReceipt;
        _cashCode.Text = draft.CashCode;
        _cashName.Text = draft.CashName;
        _cashAmount.Text = draft.CashAmount;
        _bankCode.Text = draft.BankCode;
        _bankName.Text = draft.BankName;
        _bankAmount.Text = draft.BankAmount;
        _giroBankCode.Text = draft.GiroBankCode;
        _giroBankName.Text = draft.GiroBankName;
        _giroNumber.Text = draft.GiroNumber;
        _giroAmount.Text = draft.GiroAmount;
        _selectedCheckGiro = draft.SelectedCheckGiro;
        _notes.Text = draft.Notes;
        _lines.Clear();
        foreach (var line in draft.Lines)
        {
            _lines.Add(new CustomerReceiptLineDraft
            {
                SaleId = line.SaleId,
                Group = line.Group,
                DocumentNumber = line.DocumentNumber,
                DocumentDate = line.DocumentDate,
                SalesCode = line.SalesCode,
                DueDate = line.DueDate,
                CustomerName = line.CustomerName,
                OriginalValue = line.OriginalValue,
                PaidBefore = line.PaidBefore,
                RemainingBalance = line.RemainingBalance,
                ReceiptText = line.ReceiptText,
                SourceType = line.SourceType
            });
        }
        AddBlankRows();
        _autoCash.IsChecked = draft.AutoCash;
        SetTotalReceivable(draft.Lines.Sum(line => line.RemainingBalance));
        _modeStatus.Text = "DRAFT LOKAL";
        _historyIndex = -1;
        UpdateReceiptHistoryCounter();
        UpdateCommandStates();
        UpdateTotals();
        _status.Text = "Draft Penerimaan Piutang dipulihkan. Tekan Simpan untuk menjadikannya transaksi.";
    }

    private void CommitPendingGridEdit()
    {
        _grid.CommitEdit(DataGridEditingUnit.Cell, true);
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private static string NormalizeForSignature(string? text) => (text ?? string.Empty).Trim();

    private async Task ShowReceiptHistoryAsync()
    {
        try
        {
            _status.Text = "Membuka history penerimaan piutang...";
            if (_receiptHistory.Count == 0) await ReloadHistoryCountAsync();
            if (_receiptHistory.Count == 0)
            {
                _status.Text = "Belum ada history penerimaan piutang.";
                return;
            }

            var dialog = new CustomerReceiptLookupWindow { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true || dialog.SelectedReceipt is null)
            {
                _status.Text = $"History tersedia: {_receiptHistory.Count:N0} data.";
                return;
            }

            await ApplyReceiptHistoryAsync(dialog.SelectedReceipt);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - History Penerimaan Piutang");
        }
    }

    private async Task MoveReceiptHistoryAsync(int offset)
    {
        if (_receiptHistory.Count == 0) await ReloadHistoryCountAsync();
        if (_receiptHistory.Count == 0)
        {
            _status.Text = "Belum ada history penerimaan piutang.";
            return;
        }

        var nextIndex = _historyIndex < 0
            ? offset > 0 ? _receiptHistory.Count - 1 : 0
            : _historyIndex + offset;
        await LoadReceiptHistoryAtAsync(nextIndex);
    }

    private async Task LoadReceiptHistoryAtAsync(int index)
    {
        if (_receiptHistory.Count == 0) await ReloadHistoryCountAsync();
        if (_receiptHistory.Count == 0)
        {
            _status.Text = "Belum ada history penerimaan piutang.";
            return;
        }

        index = Math.Clamp(index, 0, _receiptHistory.Count - 1);
        _historyIndex = index;
        await ApplyReceiptHistoryAsync(_receiptHistory[index]);
    }

    private Task ApplyReceiptHistoryAsync(CustomerReceiptDto receipt)
    {
        _currentReceiptRecord = receipt;
        _documentNumber.Text = receipt.DocumentNumber;
        _date.SelectedDate = receipt.DocumentDate.ToDateTime(TimeOnly.MinValue);
        _customerCode.Text = receipt.CustomerCode;
        _customerName.Text = receipt.CustomerName;
        _collectorCode.Text = receipt.CollectorCode ?? string.Empty;
        _currentReceipt.Text = receipt.ReceiptAmount.ToString("N0", CultureInfo.CurrentCulture);
        _cashCode.Text = receipt.CashAccountCode ?? string.Empty;
        _cashName.Text = receipt.CashAccountName ?? string.Empty;
        _bankCode.Text = receipt.BankAccountCode ?? string.Empty;
        _bankName.Text = receipt.BankAccountName ?? string.Empty;
        _selectedCheckGiro = receipt.CheckGiro;
        _giroNumber.Text = receipt.CheckGiro?.SequenceNumber ?? string.Empty;
        _giroBankCode.Text = receipt.CheckGiro?.BankName ?? string.Empty;
        _giroBankName.Text = receipt.CheckGiro?.CheckGiroNumber ?? string.Empty;
        _cashAmount.Text = receipt.Method == "CASH" ? receipt.ReceiptAmount.ToString("N0", CultureInfo.CurrentCulture) : "0";
        _bankAmount.Text = receipt.Method == "BANK" ? receipt.ReceiptAmount.ToString("N0", CultureInfo.CurrentCulture) : "0";
        _giroAmount.Text = receipt.Method == "CHECK_GIRO" ? receipt.ReceiptAmount.ToString("N0", CultureInfo.CurrentCulture) : "0";
        _lines.Clear();
        foreach (var line in receipt.Lines)
            _lines.Add(CustomerReceiptLineDraft.From(line, receipt.CustomerName));
        AddBlankRows();
        _modeStatus.Text = receipt.Status == "POSTED" ? "EDIT DATA" : "DITANDAI HAPUS";
        SyncReceiptHistoryIndex();
        UpdateReceiptHistoryCounter();
        UpdateCommandStates();
        _status.Text = _historyIndex >= 0
            ? $"Data {_historyIndex + 1:N0}/{_receiptHistory.Count:N0}: {receipt.DocumentNumber} | {receipt.DocumentDate:dd/MM/yyyy}."
            : $"Data {receipt.DocumentNumber} dibuka. Simpan hanya untuk transaksi baru.";
        MarkClean();
        return Task.CompletedTask;
    }

    private void ClearForm()
    {
        _selectedCustomer = null;
        _customerCode.Clear();
        _currentReceiptRecord = null;
        _customerName.Clear();
        _customerAddress.Clear();
        _collectorCode.Clear();
        _group.Clear();
        _searchNumber.Clear();
        _notes.Clear();
        _documentNumber.Clear();
        ClearAmounts();
        ClearReceiptMethods();
        SetTotalReceivable(0);
        _lines.Clear();
        AddBlankRows();
        _modeStatus.Text = "TAMBAH DATA";
        _historyIndex = -1;
        UpdateReceiptHistoryCounter();
        UpdateCommandStates();
        MarkClean();
        _ = PreviewNumberAsync();
    }

    private void ClearAmounts()
    {
        if (_currentReceiptRecord is not null)
        {
            _status.Text = "Data lama sedang dibuka. Klik Tambah untuk membuat penerimaan baru.";
            return;
        }

        foreach (var line in _lines) line.ReceiptText = "0";
        _currentReceipt.Text = "0";
        _cashAmount.Text = "0";
        _bankAmount.Text = "0";
        _giroAmount.Text = "0";
        SyncMethodSummary();
    }

    private async Task DeleteOrClearAsync()
    {
        if (_currentReceiptRecord is null)
        {
            ClearAmounts();
            return;
        }

        await DeleteCurrentReceiptAsync();
    }

    private async Task DeleteCurrentReceiptAsync()
    {
        if (_currentReceiptRecord is null || !Same(_currentReceiptRecord.Status, "POSTED")) return;
        var owner = Window.GetWindow(this);
        var result = owner is null
            ? MessageBox.Show("Apakah anda ingin mendandai hapus data ini?", "AKSARA", MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(owner, "Apakah anda ingin mendandai hapus data ini?", "AKSARA", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            IsEnabled = false;
            _status.Text = "Menandai hapus penerimaan piutang...";
            var cancelled = await _api.CancelCustomerReceiptAsync(_currentReceiptRecord.Id, new("Ditandai hapus dari form penerimaan piutang.", AksaraApiClient.Shared.CurrentUserName, Environment.MachineName));
            await ReloadHistoryCountAsync();
            await ApplyReceiptHistoryAsync(cancelled);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Penerimaan Piutang");
        }
        finally
        {
            IsEnabled = true;
            UpdateCommandStates();
        }
    }

    private async Task RestoreCurrentReceiptAsync()
    {
        if (_currentReceiptRecord is null || !Same(_currentReceiptRecord.Status, "CANCELLED")) return;
        var owner = Window.GetWindow(this);
        var result = owner is null
            ? MessageBox.Show("Apakah anda ingin membatalkan hapus data?", "AKSARA", MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(owner, "Apakah anda ingin membatalkan hapus data?", "AKSARA", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            IsEnabled = false;
            _status.Text = "Membatalkan hapus penerimaan piutang...";
            var restored = await _api.RestoreCustomerReceiptAsync(_currentReceiptRecord.Id, new(AksaraApiClient.Shared.CurrentUserName, Environment.MachineName));
            await ReloadHistoryCountAsync();
            await ApplyReceiptHistoryAsync(restored);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Penerimaan Piutang");
        }
        finally
        {
            IsEnabled = true;
            UpdateCommandStates();
        }
    }

    private void UpdateCommandStates()
    {
        if (_deleteButton is not null)
            _deleteButton.IsEnabled = _currentReceiptRecord is null || Same(_currentReceiptRecord.Status, "POSTED");
        if (_restoreButton is not null)
            _restoreButton.IsEnabled = _currentReceiptRecord is not null && Same(_currentReceiptRecord.Status, "CANCELLED");
    }

    private void ClearReceiptMethods()
    {
        _cashCode.Clear();
        _cashName.Clear();
        _bankCode.Clear();
        _bankName.Clear();
        _giroBankCode.Clear();
        _giroBankName.Clear();
        _giroNumber.Clear();
        _giroDueDate.Clear();
        _selectedCheckGiro = null;
    }

    private void SyncMethodSummary()
    {
        var cash = ParseAmount(_cashAmount.Text);
        var bank = ParseAmount(_bankAmount.Text);
        var giro = ParseAmount(_giroAmount.Text);
        var paid = ParseAmount(_currentReceipt.Text);
        _summaryCashAmount.Text = cash.ToString("N0", CultureInfo.CurrentCulture);
        _summaryBankAmount.Text = bank.ToString("N0", CultureInfo.CurrentCulture);
        _summaryGiroAmount.Text = giro.ToString("N0", CultureInfo.CurrentCulture);
        _summaryReceipt.Text = paid.ToString("N0", CultureInfo.CurrentCulture);
        _summaryAdjustment.Text = "0";
        _summaryUnbalance.Text = (paid - cash - bank - giro).ToString("N0", CultureInfo.CurrentCulture);
    }

    private void SetTotalReceivable(decimal amount)
    {
        var formatted = amount.ToString("N0", CultureInfo.CurrentCulture);
        _totalReceivable.Text = formatted;
    }

    private void OpenCashLookup()
    {
        var dialog = new CashAccountLookupWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.SelectedCashAccount is not null) ApplyCash(dialog.SelectedCashAccount);
    }

    private void ApplyCash(CashAccountDto cash)
    {
        _cashCode.Text = cash.Code;
        _cashName.Text = cash.Name;
    }

    private void OpenCollectorLookup()
    {
        var dialog = new SalespersonLookupWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.SelectedSalesperson is not null)
            _collectorCode.Text = dialog.SelectedSalesperson.Code;
    }

    private async Task<bool> EnsureCustomerSelectedAsync()
    {
        if (_selectedCustomer is not null && Same(_selectedCustomer.Code, _customerCode.Text)) return true;
        if (string.IsNullOrWhiteSpace(_customerCode.Text))
        {
            _status.Text = "Pilih customer terlebih dahulu sebelum membuka C/G.";
            MessageBox.Show(Window.GetWindow(this), "Pilih customer terlebih dahulu sebelum membuka C/G.", "AKSARA - Penerimaan Piutang", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var resolved = await TryResolveCustomerAsync(_customerCode.Text);
        if (resolved) return true;
        _status.Text = "Customer belum ditemukan.";
        MessageBox.Show(Window.GetWindow(this), "Customer belum ditemukan.", "AKSARA - Penerimaan Piutang", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private async void OpenCheckGiroLookup()
    {
        if (!await EnsureCustomerSelectedAsync()) return;
        var dialog = new CustomerCheckGiroLookupWindow(_selectedCustomer!.Code) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.SelectedCheckGiro is not null)
            ApplyCheckGiro(dialog.SelectedCheckGiro);
    }

    private async Task OpenCheckGiroEditorAsync()
    {
        if (!await EnsureCustomerSelectedAsync()) return;
        var dialog = new CustomerCheckGiroEditWindow(_selectedCustomer!.Code, _selectedCustomer.CompanyName, _selectedCustomer.Address) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.SavedCheckGiro is not null)
        {
            ApplyCheckGiro(dialog.SavedCheckGiro);
            _status.Text = $"C/G {dialog.SavedCheckGiro.SequenceNumber} dibuat untuk {_selectedCustomer.CompanyName}.";
        }
    }

    private void ApplyCheckGiro(CustomerCheckGiroDto checkGiro)
    {
        _selectedCheckGiro = checkGiro;
        _giroNumber.Text = checkGiro.SequenceNumber;
        _giroBankCode.Text = checkGiro.BankName;
        _giroBankName.Text = checkGiro.CheckGiroNumber;
        _giroAmount.Text = checkGiro.Amount.ToString("N0", CultureInfo.CurrentCulture);
        _cashAmount.Text = "0";
        _bankAmount.Text = "0";
        if (ParseAmount(_currentReceipt.Text) <= 0)
            _currentReceipt.Text = checkGiro.Amount.ToString("N0", CultureInfo.CurrentCulture);
        SyncMethodSummary();
    }

    private void OpenBankLookup(TextBox codeBox, TextBox nameBox)
    {
        var choices = _bankAccounts.Select(x => new CashBankLookupChoice(x.Code, x.BankName, x.AccountCode, x.AccountName));
        var dialog = new CashBankChoiceLookupWindow("FORM CARI BANK", choices) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.SelectedChoice is not null)
        {
            codeBox.Text = dialog.SelectedChoice.Code;
            nameBox.Text = dialog.SelectedChoice.Name;
        }
    }

    private static void ApplyBank(BankAccountDto bank, TextBox codeBox, TextBox nameBox)
    {
        codeBox.Text = bank.Code;
        nameBox.Text = bank.BankName;
    }

    private void AddBlankRows()
    {
        while (_lines.Count < DefaultRows) _lines.Add(CustomerReceiptLineDraft.Blank());
    }

    private static void WireLookup(TextBox box, Action action)
    {
        box.MouseDoubleClick += (_, _) => action();
        box.PreviewKeyDown += (_, e) => { if (e.Key == Key.F4) { e.Handled = true; action(); } };
    }

    private static decimal ParseAmount(string? text) => TryReadAmount(text, out var amount) ? amount : 0;

    private static bool TryReadAmount(string? text, out decimal amount)
    {
        var raw = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw)) { amount = 0; return true; }
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.CurrentCulture, out amount)
            || decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
    }

    private void NormalizeGiroDueDateText()
    {
        if (TryReadOptionalDate(_giroDueDate.Text, out var date))
            _giroDueDate.Text = FormatDate(date);
    }

    private void WireDateLookup(TextBox box)
    {
        box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (box.IsKeyboardFocusWithin) return;
            e.Handled = true;
            box.Focus();
            OpenDateLookup(box);
        };
        box.MouseDoubleClick += (_, _) => OpenDateLookup(box);
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.F4) return;
            e.Handled = true;
            OpenDateLookup(box);
        };
    }

    private void OpenDateLookup(TextBox target)
    {
        var currentDate = TryReadOptionalDate(target.Text, out var parsed) && parsed is not null
            ? parsed.Value.ToDateTime(TimeOnly.MinValue)
            : DateTime.Today;
        var calendar = new System.Windows.Controls.Calendar
        {
            SelectedDate = currentDate,
            DisplayDate = currentDate,
            SelectionMode = CalendarSelectionMode.SingleDate,
            Margin = new Thickness(6)
        };
        var popup = new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = new Border
            {
                Background = Brushes.White,
                BorderBrush = Brush("#8CA1B7"),
                BorderThickness = new Thickness(1),
                Child = calendar
            }
        };

        calendar.SelectedDatesChanged += (_, _) =>
        {
            if (calendar.SelectedDate is null) return;
            target.Text = calendar.SelectedDate.Value.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture);
            popup.IsOpen = false;
            target.Focus();
            target.CaretIndex = target.Text.Length;
        };

        popup.IsOpen = true;
    }

    private static bool TryReadOptionalDate(string? text, out DateOnly? date)
    {
        var raw = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw) || raw.Replace(" ", string.Empty) == "//")
        {
            date = null;
            return true;
        }

        var formats = new[] { "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "ddMMyyyy", "yyyy-MM-dd" };
        if (DateTime.TryParseExact(raw, formats, CultureInfo.CurrentCulture, DateTimeStyles.None, out var exact)
            || DateTime.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out exact)
            || DateTime.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.None, out exact))
        {
            date = DateOnly.FromDateTime(exact);
            return true;
        }

        date = null;
        return false;
    }

    private static string FormatDate(DateOnly? date) => date?.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture) ?? string.Empty;

    private static TextBox ClassicBox(string text, double width, bool readOnly = false) => new()
    {
        Text = text,
        Width = width,
        Height = 23,
        MinHeight = 0,
        Padding = new Thickness(2, 0, 2, 0),
        BorderThickness = new Thickness(0),
        Background = Brushes.White,
        IsReadOnly = readOnly
    };

    private static TextBox MoneyBox() => new()
    {
        Text = "0",
        Height = 23,
        MinHeight = 0,
        Padding = new Thickness(2, 0, 2, 0),
        BorderThickness = new Thickness(0),
        Background = Brushes.White,
        TextAlignment = TextAlignment.Right
    };

    private static TextBox SummaryMoneyBox()
    {
        var box = MoneyBox();
        box.IsReadOnly = true;
        box.Width = 120;
        box.Padding = new Thickness(2, 0, 5, 0);
        return box;
    }

    private static GroupBox GroupBox(string header, UIElement child) => new()
    {
        Header = header,
        Margin = new Thickness(0),
        Padding = new Thickness(6),
        BorderBrush = Brushes.Silver,
        Content = child
    };

    private void UpdateTabView()
    {
        var selectedIndex = _detailTabs?.SelectedIndex ?? 0;
        var methodTab = selectedIndex == 1;
        _grid.Visibility = methodTab ? Visibility.Collapsed : Visibility.Visible;
        _receiptMethodPanel.Visibility = methodTab ? Visibility.Visible : Visibility.Collapsed;
        if (_receivablesToolbar is not null) _receivablesToolbar.Visibility = selectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        _hint.Text = methodTab
            ? "Tekan F4 pada keyboard / DOUBLE CLICK tombol kiri mouse pada kolom ini untuk membuka form cari Kas / Bank / Cek-Giro"
            : "Tekan F4 pada keyboard / DOUBLE CLICK tombol kiri mouse pada kolom ini untuk membuka form cari CUSTOMER / NO. BUKTI";
    }

    private static void AddSummaryText(Grid grid, string text, int row, int column)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 10, 3) };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, column);
        grid.Children.Add(label);
    }

    private static void AddReceiptSummaryField(Grid grid, string labelText, UIElement input, int row, int labelColumn, int inputColumn)
    {
        var label = new TextBlock { Text = labelText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 6, 3) };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, labelColumn);
        grid.Children.Add(label);

        if (input is FrameworkElement element)
        {
            element.Width = double.NaN;
            element.Margin = new Thickness(0, 3, 0, 3);
            element.HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        Grid.SetRow(input, row);
        Grid.SetColumn(input, inputColumn);
        grid.Children.Add(input);
    }

    private static TextBox BlankMethodBox() => new()
    {
        Height = 23,
        MinHeight = 0,
        BorderThickness = new Thickness(0),
        Background = Brushes.White
    };

    private static void AddMethodHeaderCell(Grid grid, string text, int row, int column)
    {
        var border = new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            Background = Brush("#F8F8F8"),
            Child = new TextBlock { Text = text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        grid.Children.Add(border);
    }

    private static void AddMethodInputCell(Grid grid, UIElement child, int row, int column)
    {
        if (child is FrameworkElement element) element.Margin = new Thickness(0);
        var border = new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1, 0, 1, 1),
            Background = Brushes.White,
            Child = child
        };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        grid.Children.Add(border);
    }

    private void WireMethodInput(UIElement element, Grid table, (string Header, double Width, UIElement Child)[] columns, string header)
    {
        if (element is TextBox textBox)
        {
            if (Same(header, "Jth.Tempo"))
                WireDateLookup(textBox);

            textBox.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                if (!FocusNextMethodRow(table, textBox))
                    AddEditableMethodRow(table, columns);
            };
            return;
        }

        if (element is Panel panel)
        {
            foreach (UIElement child in panel.Children)
                WireMethodInput(child, table, columns, header);
        }
    }

    private void AddEditableMethodRow(Grid table, (string Header, double Width, UIElement Child)[] columns)
    {
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        var row = table.RowDefinitions.Count - 1;
        TextBox? firstBox = null;

        for (var index = 0; index < columns.Length; index++)
        {
            var child = CreateMethodRowInput(columns[index].Header);
            firstBox ??= child;
            AddMethodInputCell(table, child, row, index);
            WireMethodInput(child, table, columns, columns[index].Header);
        }

        table.Dispatcher.BeginInvoke(() =>
        {
            firstBox?.Focus();
            firstBox?.SelectAll();
        });
    }

    private static TextBox CreateMethodRowInput(string header)
    {
        if (Same(header, "Jumlah") || Same(header, "Nilai"))
            return MoneyBox();

        return BlankMethodBox();
    }

    private static bool FocusNextMethodRow(Grid table, TextBox current)
    {
        var currentRow = FindMethodRow(table, current);
        if (currentRow < 0 || currentRow + 1 >= table.RowDefinitions.Count) return false;
        var nextBox = FindFirstTextBoxInMethodRow(table, currentRow + 1);
        if (nextBox is null) return false;

        table.Dispatcher.BeginInvoke(() =>
        {
            nextBox.Focus();
            nextBox.SelectAll();
        });
        return true;
    }

    private static int FindMethodRow(Grid table, DependencyObject target)
    {
        foreach (UIElement child in table.Children)
        {
            if (ContainsDependencyObject(child, target))
                return Grid.GetRow(child);
        }

        return -1;
    }

    private static TextBox? FindFirstTextBoxInMethodRow(Grid table, int row)
    {
        foreach (UIElement child in table.Children)
        {
            if (Grid.GetRow(child) != row) continue;
            var textBox = FindNestedTextBox(child);
            if (textBox is not null) return textBox;
        }

        return null;
    }

    private static TextBox? FindNestedTextBox(DependencyObject root)
    {
        if (root is TextBox textBox) return textBox;
        if (root is Border { Child: DependencyObject borderChild }) return FindNestedTextBox(borderChild);
        if (root is Panel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                var nestedTextBox = FindNestedTextBox(child);
                if (nestedTextBox is not null) return nestedTextBox;
            }
        }

        return null;
    }

    private static bool ContainsDependencyObject(DependencyObject root, DependencyObject target)
    {
        if (ReferenceEquals(root, target)) return true;
        if (root is Border { Child: DependencyObject borderChild } && ContainsDependencyObject(borderChild, target)) return true;
        if (root is Panel panel)
        {
            foreach (UIElement child in panel.Children)
                if (ContainsDependencyObject(child, target)) return true;
        }

        return false;
    }

    private static Button ActionButton(string text, Action action, double width)
    {
        var button = new Button { Content = text, Width = width, Height = 25, MinHeight = 0, Margin = new Thickness(4, 0, 0, 0) };
        WireButtonAction(button, () =>
        {
            action();
            return Task.CompletedTask;
        });
        return button;
    }

    private static Button ActionButton(string text, Func<Task> action, double width)
    {
        var button = new Button { Content = text, Width = width, Height = 25, MinHeight = 0, Margin = new Thickness(4, 0, 0, 0) };
        WireButtonAction(button, action);
        return button;
    }

    private static void AddNav(StackPanel panel, string text, Func<Task> action)
    {
        var button = new Button { Content = text, Width = 34, Height = 25, MinHeight = 0, FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(0) };
        WireButtonAction(button, action);
        panel.Children.Add(button);
    }

    private static void WireButtonAction(Button button, Func<Task> action)
    {
        var running = false;
        async Task RunAsync()
        {
            if (running) return;
            try
            {
                running = true;
                await action();
            }
            finally
            {
                running = false;
            }
        }

        button.Click += async (_, _) => await RunAsync();
        button.PreviewMouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            await RunAsync();
        };
    }

    private void SyncReceiptHistoryIndex()
    {
        _historyIndex = string.IsNullOrWhiteSpace(_documentNumber.Text)
            ? -1
            : _receiptHistory.ToList().FindIndex(x => Same(x.DocumentNumber, _documentNumber.Text));
    }

    private void UpdateReceiptHistoryCounter()
    {
        _totalData.Text = _historyIndex < 0
            ? $"TOTAL DATA {_receiptHistory.Count:N0}"
            : $"DATA {_historyIndex + 1:N0} / {_receiptHistory.Count:N0}";
    }

    private static void AddStripCell(Grid grid, UIElement child, int column)
    {
        var border = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Background = Brush("#F8F8F8"), Child = child };
        Grid.SetColumn(border, column);
        grid.Children.Add(border);
    }

    private static void AddLabel(Grid grid, string text, int row, int column)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 7, 3) };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, column);
        grid.Children.Add(label);
    }

    private static void AddInput(Grid grid, UIElement input, int row, int column)
    {
        if (input is FrameworkElement element)
        {
            element.Margin = new Thickness(0, 3, 7, 3);
            element.HorizontalAlignment = HorizontalAlignment.Left;
        }
        Grid.SetRow(input, row);
        Grid.SetColumn(input, column);
        grid.Children.Add(input);
    }

    private static void AddInputSpan(Grid grid, UIElement input, int row, int column, int columnSpan)
    {
        AddInput(grid, input, row, column);
        Grid.SetColumnSpan(input, columnSpan);
    }

    private static void AddToolbarText(Grid grid, string text, int column)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        Grid.SetColumn(label, column);
        grid.Children.Add(label);
    }

    private static void AddToolbarInput(Grid grid, UIElement input, int column)
    {
        if (input is FrameworkElement element) element.Margin = new Thickness(0, 2, 8, 2);
        Grid.SetColumn(input, column);
        grid.Children.Add(input);
    }

    private static Button ToolbarButton(string text, Action action, double width)
    {
        var button = new Button { Content = text, Width = width, Height = 24, MinHeight = 0, Margin = new Thickness(4, 2, 4, 2), Padding = new Thickness(2, 0, 2, 0) };
        button.Click += (_, _) => action();
        return button;
    }

    private static Button ToolbarButton(string text, Func<Task> action, double width)
    {
        var button = new Button { Content = text, Width = width, Height = 24, MinHeight = 0, Margin = new Thickness(4, 2, 4, 2), Padding = new Thickness(2, 0, 2, 0) };
        button.Click += async (_, _) => await action();
        return button;
    }

    private static bool Same(string? left, string? right) => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
}

internal sealed class CustomerReceiptLookupWindow : SalesLookupWindowBase<CustomerReceiptDto>
{
    public CustomerReceiptDto? SelectedReceipt { get; private set; }

    public CustomerReceiptLookupWindow() : base("FORM CARI PENERIMAAN PIUTANG", 900, 540)
    {
        Grid.Columns.Add(Column("No. Bukti", nameof(CustomerReceiptDto.DocumentNumber), 130));
        Grid.Columns.Add(Column("Tanggal", nameof(CustomerReceiptDto.DocumentDate), 100));
        Grid.Columns.Add(Column("Kode Customer", nameof(CustomerReceiptDto.CustomerCode), 110));
        Grid.Columns.Add(Column("Toko/PT", nameof(CustomerReceiptDto.CustomerName), 230));
        Grid.Columns.Add(Column("Cara", nameof(CustomerReceiptDto.Method), 100));
        Grid.Columns.Add(Column("Jumlah", nameof(CustomerReceiptDto.ReceiptAmount), 120));
        Grid.Columns.Add(Column("Status", nameof(CustomerReceiptDto.Status), 90));
    }

    protected override Task<IReadOnlyList<CustomerReceiptDto>> LoadAsync(string query) => Api.SearchCustomerReceiptsAsync(query);
    protected override void SetSelected(CustomerReceiptDto selected) => SelectedReceipt = selected;
}

internal sealed class CustomerCheckGiroLookupWindow : Window
{
    private readonly string? _customerCode;
    private readonly bool _openOnly;
    private readonly TextBox _numberBox = LookupTextBox();
    private readonly TextBox _dateBox = LookupTextBox();
    private readonly TextBox _customerBox = LookupTextBox();
    private readonly RadioButton _orderByNumber = new() { Content = "No. Urut", IsChecked = true, Margin = new Thickness(5, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly RadioButton _orderByDate = new() { Content = "Tgl. Terima", Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly RadioButton _orderByCustomer = new() { Content = "Toko/PT", Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        CanUserAddRows = false,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.All,
        SelectionMode = DataGridSelectionMode.Single,
        Background = Brush("#D6EDF9"),
        RowBackground = Brush("#D6EDF9"),
        AlternatingRowBackground = Brush("#D6EDF9"),
        BorderThickness = new Thickness(0),
        RowHeight = 24,
        ColumnHeaderHeight = 24
    };

    public CustomerCheckGiroDto? SelectedCheckGiro { get; private set; }

    public CustomerCheckGiroLookupWindow(string? customerCode = null, bool openOnly = true)
    {
        _customerCode = customerCode;
        _openOnly = openOnly;
        Title = "FORM CARI PENERIMAAN CEK / GIRO";
        Width = 760;
        Height = 560;
        MinWidth = 500;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Tahoma");
        FontSize = 12;

        var rowStyle = new Style(typeof(DataGridRow));
        rowStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#D6EDF9")));
        rowStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
        var selectedTrigger = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#4B008E")));
        selectedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        rowStyle.Triggers.Add(selectedTrigger);
        _grid.RowStyle = rowStyle;

        _grid.Columns.Add(Column("No. Urut", nameof(CustomerCheckGiroDto.SequenceNumber), 120));
        _grid.Columns.Add(Column("Tgl. Terima", nameof(CustomerCheckGiroDto.DocumentDate), 105, "dd/MM/yyyy"));
        _grid.Columns.Add(Column("Bank", nameof(CustomerCheckGiroDto.BankName), 120));
        _grid.Columns.Add(Column("No. C/G", nameof(CustomerCheckGiroDto.CheckGiroNumber), 130));
        _grid.Columns.Add(Column("", nameof(CustomerCheckGiroDto.CheckGiroType), 28));
        _grid.Columns.Add(Column("J.Tempo", nameof(CustomerCheckGiroDto.DueDate), 95, "dd/MM/yyyy"));
        _grid.Columns.Add(NumberColumn("Nilai", nameof(CustomerCheckGiroDto.Amount), 120));
        _grid.Columns.Add(Column("Toko/PT", nameof(CustomerCheckGiroDto.CustomerName), 170));
        _grid.SelectionChanged += (_, _) => SyncHeaderFromSelection();

        var root = new Grid { Background = Brush("#D6EDF9") };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Background = Brushes.Black, Margin = new Thickness(0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        header.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        header.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        AddLookupLabel(header, "No. Urut", 0); AddLookupInput(header, _numberBox, 0);
        AddLookupLabel(header, "Tgl. Terima", 1); AddLookupInput(header, _dateBox, 1);
        AddLookupLabel(header, "Toko/PT", 2); AddLookupInput(header, _customerBox, 2);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        Grid.SetRow(_grid, 1);
        root.Children.Add(_grid);

        var footer = new Grid { Margin = new Thickness(6, 3, 6, 6) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var order = new StackPanel { Orientation = Orientation.Horizontal };
        order.Children.Add(new TextBlock { Text = "Urutan", TextDecorations = TextDecorations.Underline, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        order.Children.Add(_orderByNumber);
        order.Children.Add(_orderByDate);
        order.Children.Add(_orderByCustomer);
        order.Children.Add(_status);
        footer.Children.Add(order);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var selectButton = new Button { Content = "S", Width = 24, Height = 24, Margin = new Thickness(4, 0, 0, 0) };
        selectButton.Click += (_, _) => AcceptSelection();
        var cancelButton = new Button { Content = "R", Width = 24, Height = 24, Margin = new Thickness(4, 0, 0, 0) };
        cancelButton.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(selectButton);
        buttons.Children.Add(cancelButton);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        Loaded += async (_, _) => { _numberBox.Focus(); await ReloadAsync(); };
        _numberBox.KeyDown += SearchBox_KeyDown;
        _dateBox.KeyDown += SearchBox_KeyDown;
        _customerBox.KeyDown += SearchBox_KeyDown;
        _grid.MouseDoubleClick += (_, _) => AcceptSelection();
        _grid.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; AcceptSelection(); } };
    }

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.F4) return;
        e.Handled = true;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            IsEnabled = false;
            var query = CurrentQuery();
            var rows = await AksaraApiClient.Shared.SearchCustomerCheckGirosAsync(_customerCode, query, _openOnly);
            rows = Sorted(rows).ToArray();
            _grid.ItemsSource = rows;
            if (rows.Count > 0) _grid.SelectedIndex = 0;
            _status.Text = $"{rows.Count:N0} data";
        }
        catch (Exception exception)
        {
            _status.Text = "Gagal";
            MessageBox.Show(this, exception.Message, "AKSARA - Cek/Giro", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private string CurrentQuery()
    {
        if (_orderByDate.IsChecked == true) return _dateBox.Text;
        if (_orderByCustomer.IsChecked == true) return _customerBox.Text;
        return _numberBox.Text;
    }

    private IEnumerable<CustomerCheckGiroDto> Sorted(IEnumerable<CustomerCheckGiroDto> rows)
    {
        if (_orderByDate.IsChecked == true) return rows.OrderBy(x => x.DocumentDate).ThenBy(x => x.SequenceNumber);
        if (_orderByCustomer.IsChecked == true) return rows.OrderBy(x => x.CustomerName).ThenBy(x => x.SequenceNumber);
        return rows.OrderBy(x => x.SequenceNumber);
    }

    private void SyncHeaderFromSelection()
    {
        if (_grid.SelectedItem is not CustomerCheckGiroDto selected) return;
        _numberBox.Text = selected.SequenceNumber;
        _dateBox.Text = selected.DocumentDate.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture);
        _customerBox.Text = selected.CustomerName;
    }

    private void AcceptSelection()
    {
        if (_grid.SelectedItem is not CustomerCheckGiroDto selected) return;
        SelectedCheckGiro = selected;
        DialogResult = true;
    }

    private static DataGridTextColumn Column(string header, string property, double width, string? stringFormat = null) => new()
    {
        Header = header,
        Binding = new Binding(property) { StringFormat = stringFormat },
        Width = width
    };

    private static DataGridTextColumn NumberColumn(string header, string property, double width) => new()
    {
        Header = header,
        Binding = new Binding(property) { StringFormat = "N2" },
        Width = width,
        ElementStyle = LedgerAccountView.RightAlignedStyle()
    };

    private static void AddLookupLabel(Grid grid, string text, int row)
    {
        var label = new TextBlock { Text = text, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);
    }

    private static void AddLookupInput(Grid grid, TextBox input, int row)
    {
        Grid.SetRow(input, row);
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);
    }

    private static TextBox LookupTextBox() => new()
    {
        Height = 23,
        BorderThickness = new Thickness(1),
        Background = Brushes.White,
        CharacterCasing = CharacterCasing.Upper
    };

    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
}

internal sealed class CustomerCheckGiroEditWindow : Window
{
    private readonly string _defaultCustomerCode;
    private readonly string _defaultCustomerName;
    private readonly string _defaultCustomerAddress;
    private readonly bool _fixedCustomer;
    private readonly DatePicker _documentDate = DateInput();
    private readonly TextBox _sequenceNumber = ClassicBox(string.Empty, 150);
    private readonly TextBox _customerCodeBox = ClassicBox(string.Empty, 145, readOnly: true);
    private readonly TextBox _customerName = ClassicBox(string.Empty, 250, readOnly: true);
    private readonly TextBox _customerAddress = ClassicBox(string.Empty, 520, readOnly: true);
    private readonly TextBox _bankName = ClassicBox(string.Empty, 240);
    private readonly TextBox _checkNumber = ClassicBox(string.Empty, 145);
    private readonly DatePicker _dueDate = DateInput();
    private readonly TextBox _faceAmount = MoneyBox(150);
    private readonly TextBox _notes = ClassicBox(string.Empty, 760);
    private readonly RadioButton _originLocal = new() { Content = "Dlm.Kota", IsChecked = true, Margin = new Thickness(4, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly RadioButton _originOut = new() { Content = "Luar Kota", Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly RadioButton _typeCheck = new() { Content = "Cek", IsChecked = true, Margin = new Thickness(4, 0, 35, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly RadioButton _typeGiro = new() { Content = "Giro", Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _initialBalance = new() { Content = "Saldo Awal", VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _isRejected = new() { Content = "CekGiro Ditolak / Kosong", VerticalAlignment = VerticalAlignment.Center };
    private readonly DatePicker _rejectedDate = DateInput(95);
    private readonly TextBox _additionalAmount = MoneyBox(115);
    private readonly TextBox _totalAmount = MoneyBox(115);
    private readonly DatePicker _settlementDate = DateInput(95);
    private readonly TextBlock _modeStatus = new() { Text = "TAMBAH DATA", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _totalData = new() { Text = "TOTAL DATA 0", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private IReadOnlyList<CustomerCheckGiroDto> _history = [];
    private Button? _deleteButton;
    private Button? _restoreButton;
    private int _historyIndex = -1;
    private bool _saving;
    private string _cleanSignature = string.Empty;

    public CustomerCheckGiroDto? SavedCheckGiro { get; private set; }

    public CustomerCheckGiroEditWindow(string? customerCode = null, string? customerName = null, string? customerAddress = null)
    {
        _defaultCustomerCode = customerCode ?? string.Empty;
        _defaultCustomerName = customerName ?? string.Empty;
        _defaultCustomerAddress = customerAddress ?? string.Empty;
        _fixedCustomer = !string.IsNullOrWhiteSpace(customerCode);
        Title = "Penerimaan Cek / Giro";
        Width = 930;
        Height = 430;
        MinWidth = 850;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Tahoma");
        FontSize = 12;

        _documentDate.Language = XmlLanguage.GetLanguage("id-ID");
        _dueDate.Language = XmlLanguage.GetLanguage("id-ID");
        _rejectedDate.Language = XmlLanguage.GetLanguage("id-ID");
        _settlementDate.Language = XmlLanguage.GetLanguage("id-ID");
        _customerCodeBox.Text = _defaultCustomerCode;
        _customerName.Text = _defaultCustomerName;
        _customerAddress.Text = _defaultCustomerAddress;
        _customerCodeBox.IsReadOnly = _fixedCustomer;
        _documentDate.SelectedDate = DateTime.Today;
        _dueDate.SelectedDate = DateTime.Today;
        _totalAmount.IsReadOnly = true;

        _faceAmount.TextChanged += (_, _) => UpdateTotal();
        _additionalAmount.TextChanged += (_, _) => UpdateTotal();
        _customerCodeBox.TextChanged += (_, _) =>
        {
            if (_fixedCustomer) return;
            _customerName.Clear();
            _customerAddress.Clear();
        };
        _customerCodeBox.MouseDoubleClick += async (_, _) => await OpenCustomerLookupAsync();
        _customerCodeBox.PreviewKeyDown += async (_, e) =>
        {
            if (_fixedCustomer) return;
            if (e.Key == Key.F4)
            {
                e.Handled = true;
                await OpenCustomerLookupAsync();
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await ResolveCustomerAsync(_customerCodeBox.Text);
            }
        };
        _customerCodeBox.LostFocus += async (_, _) =>
        {
            if (!_fixedCustomer && !string.IsNullOrWhiteSpace(_customerCodeBox.Text))
                await ResolveCustomerAsync(_customerCodeBox.Text);
        };

        var root = new Grid { Margin = new Thickness(7), Background = Brush("#F0F0F0") };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddStatusStrip(root);
        var form = BuildForm();
        Grid.SetRow(form, 1);
        root.Children.Add(form);

        var commands = BuildCommandBar();
        Grid.SetRow(commands, 2);
        root.Children.Add(commands);

        var hint = new Grid { Height = 34, Background = Brushes.White, Margin = new Thickness(0, 4, 0, 0) };
        hint.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        hint.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new Border { Width = 27, Height = 27, CornerRadius = new CornerRadius(14), Background = Brush("#F4A322"), BorderBrush = Brush("#C87900"), BorderThickness = new Thickness(1), Child = new TextBlock { Text = "i", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        Grid.SetColumn(icon, 0); hint.Children.Add(icon);
        Grid.SetColumn(_status, 1); hint.Children.Add(_status);
        Grid.SetRow(hint, 3);
        root.Children.Add(hint);

        Content = root;
        Loaded += async (_, _) =>
        {
            await PreviewNumberAsync();
            await ReloadHistoryAsync();
            MarkClean();
        };
        Closing += (sender, e) =>
        {
            if (!HasUnsavedChanges()) return;
            var result = MessageBox.Show(this, "Simpan Data ?", "Centerpoint Software", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                _status.Text = "Aksi dibatalkan. Data C/G masih di form.";
                return;
            }

            if (result == MessageBoxResult.No)
            {
                MarkClean();
                return;
            }

            e.Cancel = true;
            _ = SaveAndCloseAsync();
        };
    }

    private void AddStatusStrip(Grid root)
    {
        var strip = new Grid { Height = 34 };
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        strip.ColumnDefinitions.Add(new ColumnDefinition());
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(205) });
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        AddStripCell(strip, new CheckBox { Content = "Exclusive Mode", IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) }, 0);
        AddStripCell(strip, new TextBlock { Text = "Status:", Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }, 1);
        AddStripCell(strip, _modeStatus, 2);
        AddStripCell(strip, _totalData, 3);
        AddStripCell(strip, new TextBlock { Text = "▥", FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, 4);
        Grid.SetRow(strip, 0);
        root.Children.Add(strip);
    }

    private Border BuildForm()
    {
        var frame = new Border { BorderBrush = Brushes.Silver, BorderThickness = new Thickness(1), Background = Brush("#F4F4F4"), Padding = new Thickness(8, 6, 8, 6) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var index = 0; index < 9; index++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });

        AddField(grid, "Tgl. Bukti", _documentDate, 0, 0, 82, 160);
        AddField(grid, "No. Bukti", _sequenceNumber, 1, 0, 82, 160);
        AddField(grid, "Kode Cust.", _customerCodeBox, 2, 0, 82, 160);
        AddField(grid, "Alamat", _customerAddress, 3, 0, 82, 280);
        AddField(grid, "Bank", _bankName, 4, 0, 82, 240);
        AddField(grid, "No. C/G", _checkNumber, 5, 0, 82, 160);
        AddField(grid, "Jth.Tempo", _dueDate, 6, 0, 82, 160);
        AddField(grid, "Senilai", _faceAmount, 7, 0, 82, 160);
        AddField(grid, "Keterangan", _notes, 8, 0, 82, 290);

        var center = new Grid { Margin = new Thickness(0, 54, 0, 0) };
        center.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        center.ColumnDefinitions.Add(new ColumnDefinition());
        center.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        center.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        center.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        AddLabel(center, "Toko/PT", 0, 0); AddInput(center, _customerName, 0, 1);
        AddRadioRow(center, "Asal C/G", 1, _originLocal, _originOut);
        AddRadioRow(center, "Type C/G", 2, _typeCheck, _typeGiro);
        Grid.SetColumn(center, 1);
        Grid.SetRowSpan(center, 9);
        grid.Children.Add(center);

        frame.Child = grid;
        return frame;
    }

    private Grid BuildCommandBar()
    {
        var grid = new Grid { Height = 40, Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var nav = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        AddNav(nav, "◀◀", async () => await LoadHistoryAtAsync(_history.Count - 1));
        AddNav(nav, "◀", async () => await MoveHistoryAsync(1));
        AddNav(nav, "●", async () => await ShowHistoryAsync());
        AddNav(nav, "▶", async () => await MoveHistoryAsync(-1));
        AddNav(nav, "▶▶", async () => await LoadHistoryAtAsync(0));
        grid.Children.Add(nav);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(ActionButton("Cari", async () => await ShowHistoryAsync(), 80));
        actions.Children.Add(ActionButton("Tambah", async () => await NewAsync(), 80));
        actions.Children.Add(ActionButton("Batal", async () => await CancelAsync(), 80));
        actions.Children.Add(ActionButton("Simpan", async () => await SaveAsync(closeAfterSave: true), 88));
        actions.Children.Add(ActionButton("Smpn&Ctk", async () => await SaveAsync(closeAfterSave: true), 96));
        _deleteButton = ActionButton("Hapus", async () => await DeleteCurrentCheckGiroAsync(), 80);
        _restoreButton = ActionButton("Btl.Hapus", async () => await RestoreCurrentCheckGiroAsync(), 88);
        actions.Children.Add(_deleteButton);
        actions.Children.Add(_restoreButton);
        actions.Children.Add(ActionButton("Tutup", async () => await CloseWithGuardAsync(), 80));
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        UpdateCommandStates();
        return grid;
    }

    private async Task OpenCustomerLookupAsync()
    {
        if (_fixedCustomer) return;
        var dialog = new CustomerLookupWindow { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SelectedCustomer is not null)
        {
            ApplyCustomer(dialog.SelectedCustomer);
            await ReloadHistoryAsync();
            _bankName.Focus();
        }
    }

    private async Task<bool> ResolveCustomerAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        try
        {
            var rows = await AksaraApiClient.Shared.SearchCustomersAsync(code);
            var customer = rows.FirstOrDefault(x => Same(x.Code, code)) ?? rows.FirstOrDefault();
            if (customer is null) return false;
            ApplyCustomer(customer);
            return true;
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            return false;
        }
    }

    private void ApplyCustomer(SalesCustomerDto customer)
    {
        _customerCodeBox.Text = customer.Code;
        _customerName.Text = customer.CompanyName;
        _customerAddress.Text = customer.Address ?? string.Empty;
    }

    private async Task SaveAsync(bool closeAfterSave)
    {
        try
        {
            if (SavedCheckGiro is not null && _historyIndex >= 0)
            {
                _status.Text = "Data C/G lama sedang dibuka. Klik Tambah untuk membuat C/G baru.";
                return;
            }

            if (string.IsNullOrWhiteSpace(_bankName.Text)) throw new InvalidOperationException("Bank wajib diisi.");
            if (string.IsNullOrWhiteSpace(_checkNumber.Text)) throw new InvalidOperationException("No. C/G wajib diisi.");
            if (string.IsNullOrWhiteSpace(_customerCodeBox.Text)) throw new InvalidOperationException("Kode customer wajib diisi.");
            if (string.IsNullOrWhiteSpace(_customerName.Text) && !await ResolveCustomerAsync(_customerCodeBox.Text))
                throw new InvalidOperationException("Customer tidak ditemukan di database CPS.");
            if (!TryReadAmount(_faceAmount.Text, out var faceAmount) || faceAmount <= 0) throw new InvalidOperationException("Senilai C/G wajib lebih dari nol.");
            var additional = ParseAmount(_additionalAmount.Text);
            var total = Math.Round(faceAmount + additional, 2);
            var customerCode = _customerCodeBox.Text.Trim().ToUpperInvariant();
            if (total <= 0) throw new InvalidOperationException("Jumlah C/G wajib lebih dari nol.");

            _saving = true;
            IsEnabled = false;
            _status.Text = "Menyimpan C/G...";
            SavedCheckGiro = await AksaraApiClient.Shared.CreateCustomerCheckGiroAsync(new CreateCustomerCheckGiroRequest(
                customerCode,
                string.IsNullOrWhiteSpace(_sequenceNumber.Text) ? null : _sequenceNumber.Text.Trim().ToUpperInvariant(),
                DateOnly.FromDateTime(_documentDate.SelectedDate ?? DateTime.Today),
                _bankName.Text.Trim().ToUpperInvariant(),
                _checkNumber.Text.Trim().ToUpperInvariant(),
                _typeCheck.IsChecked == true ? "C" : "G",
                _dueDate.SelectedDate is null ? null : DateOnly.FromDateTime(_dueDate.SelectedDate.Value),
                faceAmount,
                additional,
                total,
                _notes.Text,
                AksaraApiClient.Shared.CurrentUserName,
                Environment.MachineName,
                _originOut.IsChecked == true ? "OUT_OF_TOWN" : "LOCAL",
                _initialBalance.IsChecked == true,
                _isRejected.IsChecked == true,
                _rejectedDate.SelectedDate is null ? null : DateOnly.FromDateTime(_rejectedDate.SelectedDate.Value),
                _settlementDate.SelectedDate is null ? null : DateOnly.FromDateTime(_settlementDate.SelectedDate.Value)));
            _status.Text = $"C/G {SavedCheckGiro.SequenceNumber} tersimpan.";
            await ReloadHistoryAsync();
            UpdateCommandStates();
            MarkClean();
            if (closeAfterSave) DialogResult = true;
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            MessageBox.Show(this, exception.Message, "AKSARA - Penerimaan Cek/Giro", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _saving = false;
            IsEnabled = true;
            UpdateCommandStates();
        }
    }

    private async Task NewAsync()
    {
        if (!await ConfirmPendingChangesAsync(discardOnNo: true)) return;
        ClearForm();
        await PreviewNumberAsync();
        UpdateCommandStates();
        MarkClean();
        if (_fixedCustomer) _bankName.Focus();
        else _customerCodeBox.Focus();
    }

    private async Task CancelAsync()
    {
        if (!await ConfirmPendingChangesAsync(discardOnNo: true)) return;
        ClearForm();
        await PreviewNumberAsync();
        UpdateCommandStates();
        MarkClean();
    }

    private async Task DeleteCurrentCheckGiroAsync()
    {
        if (SavedCheckGiro is null || SavedCheckGiro.Status != "OPEN") return;
        var result = MessageBox.Show(this, "Apakah anda ingin mendandai hapus data ini?", "AKSARA", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            IsEnabled = false;
            _status.Text = "Menandai hapus C/G...";
            var cancelled = await AksaraApiClient.Shared.CancelCustomerCheckGiroAsync(SavedCheckGiro.Id, new("Ditandai hapus dari form penerimaan C/G.", AksaraApiClient.Shared.CurrentUserName, Environment.MachineName));
            await ReloadHistoryAsync();
            ApplyExisting(cancelled);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            MessageBox.Show(this, exception.Message, "AKSARA - Penerimaan Cek/Giro", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsEnabled = true;
            UpdateCommandStates();
        }
    }

    private async Task RestoreCurrentCheckGiroAsync()
    {
        if (SavedCheckGiro is null || SavedCheckGiro.Status != "CANCELLED" || SavedCheckGiro.IsRejected) return;
        var result = MessageBox.Show(this, "Apakah anda ingin membatalkan hapus data?", "AKSARA", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            IsEnabled = false;
            _status.Text = "Membatalkan hapus C/G...";
            var restored = await AksaraApiClient.Shared.RestoreCustomerCheckGiroAsync(SavedCheckGiro.Id, new(AksaraApiClient.Shared.CurrentUserName, Environment.MachineName));
            await ReloadHistoryAsync();
            ApplyExisting(restored);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            MessageBox.Show(this, exception.Message, "AKSARA - Penerimaan Cek/Giro", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsEnabled = true;
            UpdateCommandStates();
        }
    }

    private async Task CloseWithGuardAsync()
    {
        if (!HasUnsavedChanges())
        {
            DialogResult = false;
            return;
        }

        var result = MessageBox.Show(this, "Simpan Data ?", "Centerpoint Software", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel)
        {
            _status.Text = "Aksi dibatalkan. Data C/G masih di form.";
            return;
        }

        if (result == MessageBoxResult.No)
        {
            MarkClean();
            DialogResult = false;
            return;
        }

        await SaveAsync(closeAfterSave: false);
        if (!HasUnsavedChanges()) DialogResult = SavedCheckGiro is not null;
    }

    private async Task SaveAndCloseAsync()
    {
        await SaveAsync(closeAfterSave: false);
        if (!HasUnsavedChanges()) DialogResult = true;
    }

    private async Task<bool> ConfirmPendingChangesAsync(bool discardOnNo)
    {
        if (!HasUnsavedChanges()) return true;
        var result = MessageBox.Show(this, "Simpan Data ?", "Centerpoint Software", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        switch (result)
        {
            case MessageBoxResult.Yes:
                await SaveAsync(closeAfterSave: false);
                return !HasUnsavedChanges();
            case MessageBoxResult.No:
                if (discardOnNo) MarkClean();
                return true;
            default:
                _status.Text = "Aksi dibatalkan. Data C/G masih di form.";
                return false;
        }
    }

    private bool HasUnsavedChanges() => !_saving && !Same(CurrentSignature(), _cleanSignature);

    private string CurrentSignature() => string.Join("\u001F",
        _documentDate.SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
        _sequenceNumber.Text.Trim(),
        _customerCodeBox.Text.Trim(),
        _customerName.Text.Trim(),
        _bankName.Text.Trim(),
        _checkNumber.Text.Trim(),
        _dueDate.SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
        NormalizeNumberText(_faceAmount.Text),
        NormalizeNumberText(_additionalAmount.Text),
        _notes.Text.Trim(),
        _originOut.IsChecked == true ? "OUT" : "LOCAL",
        _typeCheck.IsChecked == true ? "C" : "G",
        (_initialBalance.IsChecked == true).ToString(),
        (_isRejected.IsChecked == true).ToString(),
        _rejectedDate.SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
        _settlementDate.SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty);

    private void MarkClean() => _cleanSignature = CurrentSignature();

    private async Task PreviewNumberAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_sequenceNumber.Text))
                _sequenceNumber.Text = await AksaraApiClient.Shared.GetNextCustomerCheckGiroNumberAsync();
        }
        catch
        {
            if (string.IsNullOrWhiteSpace(_sequenceNumber.Text)) _sequenceNumber.Text = "CG00000000";
        }
    }

    private async Task ReloadHistoryAsync()
    {
        try
        {
            _history = await AksaraApiClient.Shared.SearchCustomerCheckGirosAsync(null, null, openOnly: false);
            _totalData.Text = $"TOTAL DATA {_history.Count:N0}";
            SyncHistoryIndex();
            UpdateCommandStates();
        }
        catch
        {
            _history = [];
            _historyIndex = -1;
            _totalData.Text = "TOTAL DATA 0";
            UpdateCommandStates();
        }
    }

    private async Task ShowHistoryAsync()
    {
        await ReloadHistoryAsync();
        var dialog = new CustomerCheckGiroLookupWindow(null, openOnly: false) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SelectedCheckGiro is not null)
            ApplyExisting(dialog.SelectedCheckGiro);
    }

    private async Task MoveHistoryAsync(int offset)
    {
        if (_history.Count == 0) await ReloadHistoryAsync();
        if (_history.Count == 0) return;
        var next = _historyIndex < 0 ? 0 : _historyIndex + offset;
        await LoadHistoryAtAsync(next);
    }

    private async Task LoadHistoryAtAsync(int index)
    {
        if (_history.Count == 0) await ReloadHistoryAsync();
        if (_history.Count == 0) return;
        index = Math.Clamp(index, 0, _history.Count - 1);
        ApplyExisting(_history[index]);
    }

    private void ApplyExisting(CustomerCheckGiroDto giro)
    {
        SavedCheckGiro = giro;
        _documentDate.SelectedDate = giro.DocumentDate.ToDateTime(TimeOnly.MinValue);
        _sequenceNumber.Text = giro.SequenceNumber;
        _customerCodeBox.Text = giro.CustomerCode;
        _customerName.Text = giro.CustomerName;
        _customerAddress.Text = giro.CustomerAddress ?? string.Empty;
        _bankName.Text = giro.BankName;
        _checkNumber.Text = giro.CheckGiroNumber;
        _dueDate.SelectedDate = giro.DueDate?.ToDateTime(TimeOnly.MinValue);
        _faceAmount.Text = giro.FaceAmount.ToString("N0", CultureInfo.CurrentCulture);
        _additionalAmount.Text = giro.AdditionalAmount.ToString("N0", CultureInfo.CurrentCulture);
        _notes.Text = giro.Notes ?? string.Empty;
        _originLocal.IsChecked = giro.OriginArea != "OUT_OF_TOWN";
        _originOut.IsChecked = giro.OriginArea == "OUT_OF_TOWN";
        _typeCheck.IsChecked = !Same(giro.CheckGiroType, "G");
        _typeGiro.IsChecked = Same(giro.CheckGiroType, "G");
        _initialBalance.IsChecked = giro.InitialBalance;
        _isRejected.IsChecked = giro.IsRejected;
        _rejectedDate.SelectedDate = giro.RejectedDate?.ToDateTime(TimeOnly.MinValue);
        _settlementDate.SelectedDate = giro.SettlementDate?.ToDateTime(TimeOnly.MinValue);
        UpdateTotal();
        _modeStatus.Text = giro.Status == "OPEN"
            ? "EDIT DATA"
            : giro.Status == "CANCELLED" && !giro.IsRejected ? "DITANDAI HAPUS" : giro.Status;
        SyncHistoryIndex();
        UpdateCommandStates();
        MarkClean();
        _status.Text = _historyIndex >= 0 ? $"Record: {_historyIndex + 1:N0}/{_history.Count:N0}" : $"C/G {giro.SequenceNumber} dibuka.";
    }

    private void ClearForm()
    {
        _documentDate.SelectedDate = DateTime.Today;
        _sequenceNumber.Clear();
        _customerCodeBox.Text = _defaultCustomerCode;
        _customerName.Text = _defaultCustomerName;
        _customerAddress.Text = _defaultCustomerAddress;
        _bankName.Clear();
        _checkNumber.Clear();
        _dueDate.SelectedDate = DateTime.Today;
        _faceAmount.Text = "0";
        _additionalAmount.Text = "0";
        _notes.Clear();
        _originLocal.IsChecked = true;
        _typeCheck.IsChecked = true;
        _initialBalance.IsChecked = false;
        _isRejected.IsChecked = false;
        _rejectedDate.SelectedDate = null;
        _settlementDate.SelectedDate = null;
        _historyIndex = -1;
        SavedCheckGiro = null;
        _modeStatus.Text = "TAMBAH DATA";
        UpdateTotal();
        UpdateCommandStates();
        _status.Text = "Tekan F4 pada keyboard / DOUBLE CLICK tombol kiri mouse pada kolom ini untuk membuka form cari BANK / C/G";
    }

    private void SyncHistoryIndex()
    {
        _historyIndex = SavedCheckGiro is null ? -1 : _history.ToList().FindIndex(x => x.Id == SavedCheckGiro.Id);
    }

    private void UpdateTotal()
    {
        var total = ParseAmount(_faceAmount.Text) + ParseAmount(_additionalAmount.Text);
        _totalAmount.Text = total.ToString("N0", CultureInfo.CurrentCulture);
    }

    private void UpdateCommandStates()
    {
        if (_deleteButton is not null)
            _deleteButton.IsEnabled = SavedCheckGiro is not null && SavedCheckGiro.Status == "OPEN";
        if (_restoreButton is not null)
            _restoreButton.IsEnabled = SavedCheckGiro is not null && SavedCheckGiro.Status == "CANCELLED" && !SavedCheckGiro.IsRejected;
    }

    private static void AddStripCell(Grid grid, UIElement child, int column)
    {
        var border = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Background = Brush("#F8F8F8"), Child = child };
        Grid.SetColumn(border, column);
        grid.Children.Add(border);
    }

    private static void AddField(Grid grid, string labelText, UIElement input, int row, int column, double labelWidth, double inputWidth)
    {
        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(inputWidth) });
        AddLabel(inner, labelText, 0, 0);
        AddInput(inner, input, 0, 1);
        Grid.SetRow(inner, row);
        Grid.SetColumn(inner, column);
        grid.Children.Add(inner);
    }

    private static void AddRadioRow(Grid grid, string labelText, int row, params RadioButton[] radios)
    {
        AddLabel(grid, labelText, row, 0);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var radio in radios) panel.Children.Add(radio);
        Grid.SetRow(panel, row);
        Grid.SetColumn(panel, 1);
        grid.Children.Add(panel);
    }

    private static void AddLabel(Grid grid, string text, int row, int column)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, column);
        grid.Children.Add(label);
    }

    private static void AddInput(Grid grid, UIElement input, int row, int column)
    {
        if (input is FrameworkElement element)
        {
            element.HorizontalAlignment = HorizontalAlignment.Left;
            element.VerticalAlignment = VerticalAlignment.Center;
            element.Margin = new Thickness(0, 2, 4, 2);
        }

        Grid.SetRow(input, row);
        Grid.SetColumn(input, column);
        grid.Children.Add(input);
    }

    private static Button ActionButton(string text, Action action, double width)
    {
        var button = BaseButton(text, width);
        button.Click += (_, _) => action();
        return button;
    }

    private static Button ActionButton(string text, Func<Task> action, double width)
    {
        var button = BaseButton(text, width);
        button.Click += async (_, _) => await action();
        return button;
    }

    private static void AddNav(StackPanel panel, string text, Func<Task> action)
    {
        var button = BaseButton(text, 38);
        button.Margin = new Thickness(0, 0, 5, 0);
        button.Click += async (_, _) => await action();
        panel.Children.Add(button);
    }

    private static Button BaseButton(string text, double width) => new()
    {
        Content = text,
        Width = width,
        Height = 26,
        MinHeight = 0,
        Padding = new Thickness(2, 0, 2, 0),
        Margin = new Thickness(4, 0, 0, 0)
    };

    private static TextBox ClassicBox(string text, double width, bool readOnly = false) => new()
    {
        Text = text,
        Width = width,
        Height = 23,
        MinHeight = 0,
        Padding = new Thickness(2, 0, 2, 0),
        BorderThickness = new Thickness(0),
        Background = Brushes.White,
        IsReadOnly = readOnly,
        CharacterCasing = CharacterCasing.Upper
    };

    private static DatePicker DateInput(double width = 120) => new()
    {
        Width = width,
        Height = 23,
        MinHeight = 0,
        SelectedDateFormat = DatePickerFormat.Short
    };

    private static TextBox MoneyBox(double width = 155) => new()
    {
        Text = "0",
        Width = width,
        Height = 23,
        MinHeight = 0,
        Padding = new Thickness(2, 0, 5, 0),
        BorderThickness = new Thickness(0),
        Background = Brushes.White,
        TextAlignment = TextAlignment.Right
    };

    private static decimal ParseAmount(string? text) => TryReadAmount(text, out var amount) ? amount : 0;

    private static string NormalizeNumberText(string? text) => ParseAmount(text).ToString("0.##", CultureInfo.InvariantCulture);

    private static bool TryReadAmount(string? text, out decimal amount)
    {
        var raw = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw)) { amount = 0; return true; }
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.CurrentCulture, out amount)
            || decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
    }

    private static bool Same(string? left, string? right) => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
}

internal sealed class CustomerReceiptLineDraft : INotifyPropertyChanged
{
    private string _receiptText = "0";

    public Guid SaleId { get; init; }
    public string SourceType { get; init; } = "SALE";
    public string Group { get; init; } = string.Empty;
    public string DocumentNumber { get; init; } = string.Empty;
    public DateOnly? DocumentDate { get; init; }
    public string? SalesCode { get; init; }
    public DateOnly? DueDate { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public decimal OriginalValue { get; init; }
    public decimal PaidBefore { get; init; }
    public decimal RemainingBalance { get; init; }
    public string ReceiptText { get => _receiptText; set => SetField(ref _receiptText, value); }
    public bool IsBlank => SaleId == Guid.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public static CustomerReceiptLineDraft From(CustomerReceivableDto row) => new()
    {
        SaleId = row.SaleId,
        SourceType = row.SourceType,
        Group = row.SourceType == "BALANCE_DOCUMENT" ? "SALDO" : string.Empty,
        DocumentNumber = row.DocumentNumber,
        DocumentDate = row.DocumentDate,
        SalesCode = row.SalesCode,
        DueDate = row.DueDate,
        CustomerName = row.CustomerName,
        OriginalValue = row.BalanceDue,
        PaidBefore = row.PaidAmount,
        RemainingBalance = row.RemainingBalance,
        ReceiptText = "0"
    };

    public static CustomerReceiptLineDraft From(CustomerReceiptLineDto row, string customerName) => new()
    {
        SaleId = row.SaleId,
        SourceType = row.SourceType,
        Group = row.SourceType == "BALANCE_DOCUMENT" ? "SALDO" : string.Empty,
        DocumentNumber = row.SaleNumber,
        DocumentDate = row.SaleDate,
        CustomerName = customerName,
        OriginalValue = row.OriginalBalance,
        PaidBefore = row.PaidBefore,
        RemainingBalance = row.OriginalBalance - row.PaidBefore,
        ReceiptText = row.Amount.ToString("N0", CultureInfo.CurrentCulture)
    };

    public static CustomerReceiptLineDraft Blank() => new();

    private void SetField(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        value ??= string.Empty;
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

