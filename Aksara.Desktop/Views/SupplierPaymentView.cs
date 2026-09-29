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

public sealed class SupplierPaymentView : UserControl, IWorkspaceCloseGuard
{
    private const string LocalDraftKey = "supplier-payment";
    private const int DefaultRows = 10;

    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly ObservableCollection<SupplierPaymentLineDraft> _lines = [];
    private readonly DatePicker _date = new() { SelectedDate = DateTime.Today, Width = 135 };
    private readonly TextBox _documentNumber = ClassicBox("OTOMATIS", 160);
    private readonly TextBox _supplierCode = ClassicBox(string.Empty, 135);
    private readonly TextBox _supplierName = ClassicBox(string.Empty, 360, readOnly: true);
    private readonly TextBox _supplierAddress = ClassicBox(string.Empty, 520, readOnly: true);
    private readonly TextBox _group = ClassicBox(string.Empty, 135);
    private readonly TextBox _searchNumber = ClassicBox(string.Empty, 180);
    private readonly TextBox _totalDebt = MoneyBox();
    private readonly TextBox _currentPayment = MoneyBox();
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
    private readonly DatePicker _payablesUntilDate = new() { SelectedDate = DateTime.Today, Width = 118 };
    private readonly TextBox _giroAmount = MoneyBox();
    private readonly CheckBox _autoCash = new() { Content = "Isi Kas Otomatis", Margin = new Thickness(6, 0, 0, 0) };
    private readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 56, BorderThickness = new Thickness(0), Background = Brushes.White };
    private readonly TextBox _summaryCashAmount = SummaryMoneyBox();
    private readonly TextBox _summaryBankAmount = SummaryMoneyBox();
    private readonly TextBox _summaryGiroAmount = SummaryMoneyBox();
    private readonly TextBox _summaryExpense = SummaryMoneyBox();
    private readonly TextBox _summaryAdjustment = SummaryMoneyBox();
    private readonly TextBox _summaryUnbalance = SummaryMoneyBox();
    private readonly ComboBox _summaryStatus = new() { Width = 70, Height = 24, MinHeight = 0 };
    private readonly Grid _contentHost = new();
    private readonly Grid _paymentMethodPanel = new();
    private readonly TextBlock _modeStatus = new() { Text = "TAMBAH DATA", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _totalData = new() { Text = "TOTAL DATA 0", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { Text = "PEMBAYARAN UTANG", Foreground = Brushes.Red, FontSize = 26, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hint = new() { Text = "Tekan F4 pada keyboard / DOUBLE CLICK tombol kiri mouse pada kolom ini untuk membuka form cari SUPPLIER / NO. BUKTI", VerticalAlignment = VerticalAlignment.Center };
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

    private PurchaseSupplierDto? _selectedSupplier;
    private SupplierPaymentDto? _currentPaymentRecord;
    private IReadOnlyList<CashAccountDto> _cashAccounts = [];
    private IReadOnlyList<BankAccountDto> _bankAccounts = [];
    private IReadOnlyList<SupplierPaymentDto> _paymentHistory = [];
    private TabControl? _detailTabs;
    private Grid? _payablesToolbar;
    private Button? _deleteButton;
    private Button? _restoreButton;
    private int _historyIndex = -1;
    private bool _loading;
    private bool _saving;
    private string _cleanDraftSignature = string.Empty;
    private bool _draftChecked;

    public event EventHandler? CloseRequested;

    public SupplierPaymentView()
    {
        _date.Language = XmlLanguage.GetLanguage("id-ID");
        _payablesUntilDate.Language = XmlLanguage.GetLanguage("id-ID");
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
        _supplierCode.MouseDoubleClick += (_, _) => OpenSupplierLookup();
        _supplierCode.PreviewKeyDown += SupplierCode_PreviewKeyDown;
        _supplierCode.LostFocus += async (_, _) => await TryResolveSupplierAsync(_supplierCode.Text);
        _searchNumber.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key is Key.Enter or Key.F4) { e.Handled = true; await LoadPayablesAsync(); }
        };
        _grid.MouseDoubleClick += (_, _) => FillSelectedLine();
        _grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(UpdateTotals);
        _grid.CurrentCellChanged += (_, _) => UpdateTotals();
        _autoCash.Checked += (_, _) => FillCashAutomatically();
        _autoCash.Unchecked += (_, _) => { };
        WireLookup(_cashCode, OpenCashLookup);
        WireLookup(_bankCode, () => OpenBankLookup(_bankCode, _bankName));
        WireLookup(_giroBankCode, () => OpenBankLookup(_giroBankCode, _giroBankName));
        _currentPayment.TextChanged += (_, _) => SyncMethodSummary();
        _cashAmount.TextChanged += (_, _) => SyncMethodSummary();
        _bankAmount.TextChanged += (_, _) => SyncMethodSummary();
        _giroAmount.TextChanged += (_, _) => SyncMethodSummary();
        _giroDueDate.LostKeyboardFocus += (_, _) => NormalizeGiroDueDateText();
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
        AddPaymentArea(root);
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
        _supplierCode.Width = 160;
        _supplierName.Width = 500;
        _supplierAddress.Width = 500;

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

        var supplierGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        supplierGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        supplierGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(520) });
        supplierGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        supplierGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        supplierGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        AddLabel(supplierGrid, "Kode Supl.", 0, 0); AddInput(supplierGrid, _supplierCode, 0, 1);
        AddLabel(supplierGrid, "Toko/PT", 1, 0); AddInput(supplierGrid, _supplierName, 1, 1);
        AddLabel(supplierGrid, "Alamat", 2, 0); AddInput(supplierGrid, _supplierAddress, 2, 1);
        Grid.SetColumn(supplierGrid, 2);
        Grid.SetRowSpan(supplierGrid, 3);
        grid.Children.Add(supplierGrid);

        border.Child = grid;
        Grid.SetRow(border, 1); root.Children.Add(border);
    }

    private void AddTitleAndSearch(Grid root)
    {
        var grid = new Grid { Margin = new Thickness(7, 0, 7, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _detailTabs = new TabControl { Height = 34, Padding = new Thickness(0), Background = Brush("#F0F0F0") };
        _detailTabs.Items.Add(new TabItem { Header = "Utang (Transaksi yang Dibayar) .1", IsSelected = true });
        _detailTabs.Items.Add(new TabItem { Header = "Pengeluaran Berupa (Uang untuk Membayar) .2" });
        _detailTabs.SelectionChanged += (_, _) => UpdateTabView();
        grid.Children.Add(_detailTabs);

        _payablesToolbar = new Grid { Height = 32, Margin = new Thickness(28, 0, 28, 0) };
        _payablesToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _payablesToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _payablesToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _payablesToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _payablesToolbar.ColumnDefinitions.Add(new ColumnDefinition());
        _payablesToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _payablesToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
        AddToolbarText(_payablesToolbar, "Bukti belum lunas s/d Tgl.", 0);
        AddToolbarInput(_payablesToolbar, _payablesUntilDate, 1);
        var addButton = ToolbarButton("Tambahkan pada Rincian", async () => await LoadPayablesAsync(), 140);
        var clearButton = ToolbarButton("Kosongkan Rincian", ClearAmounts, 130);
        Grid.SetColumn(addButton, 2); _payablesToolbar.Children.Add(addButton);
        Grid.SetColumn(clearButton, 3); _payablesToolbar.Children.Add(clearButton);
        AddToolbarText(_payablesToolbar, "Saldo", 5);
        AddToolbarInput(_payablesToolbar, _totalDebt, 6);
        Grid.SetRow(_payablesToolbar, 1);
        grid.Children.Add(_payablesToolbar);

        Grid.SetRow(grid, 2); root.Children.Add(grid);
    }

    private void AddContentArea(Grid root)
    {
        _grid.ItemsSource = _lines;
        _grid.Margin = new Thickness(7, 0, 7, 0);
        _contentHost.Children.Add(_grid);
        BuildPaymentMethodPanel();
        _contentHost.Children.Add(_paymentMethodPanel);
        Grid.SetRow(_contentHost, 3);
        root.Children.Add(_contentHost);
    }

    private void BuildPaymentMethodPanel()
    {
        _paymentMethodPanel.Width = 1080;
        _paymentMethodPanel.Margin = new Thickness(7, 0, 7, 0);
        _paymentMethodPanel.HorizontalAlignment = HorizontalAlignment.Center;
        _paymentMethodPanel.VerticalAlignment = VerticalAlignment.Top;
        _paymentMethodPanel.Background = Brush("#F0F0F0");
        _paymentMethodPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _paymentMethodPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _paymentMethodPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _cashCode.Width = 80;
        _cashName.Width = 245;
        _cashAmount.Width = 150;
        _bankCode.Width = 80;
        _bankName.Width = 245;
        _bankAmount.Width = 150;
        _giroBankCode.Width = 64;
        _giroBankName.Width = 292;
        _giroNumber.Width = 136;
        _giroDueDate.Width = 104;
        _giroAmount.Width = 150;

        AddMethodSection(_paymentMethodPanel, 0, "K A S",
            ("Kode", 85, _cashCode),
            ("Nama", 250, _cashName),
            ("Keterangan", 550, BlankMethodBox()),
            ("Jumlah", 155, _cashAmount));

        AddMethodSection(_paymentMethodPanel, 1, "B A N K",
            ("Kode", 85, _bankCode),
            ("Nama", 250, _bankName),
            ("No.C/G", 120, BlankMethodBox()),
            ("Jth.Tempo", 120, _giroDueDate),
            ("Typ.", 65, BlankMethodBox()),
            ("Keterangan", 245, BlankMethodBox()),
            ("Jumlah", 155, _bankAmount));

        var giroInfo = new Grid();
        giroInfo.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
        giroInfo.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(297) });
        Grid.SetColumn(_giroBankCode, 0); giroInfo.Children.Add(_giroBankCode);
        Grid.SetColumn(_giroBankName, 1); giroInfo.Children.Add(_giroBankName);

        AddMethodSection(_paymentMethodPanel, 2, "CEK / GIRO",
            ("No.Urut", 145, _giroNumber),
            ("Informasi Cek / Giro", 365, giroInfo),
            ("Keterangan", 375, BlankMethodBox()),
            ("Nilai", 155, _giroAmount));
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

    private void AddPaymentArea(Grid root)
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

        _currentPayment.Padding = new Thickness(2, 0, 5, 0);

        Grid.SetRow(_autoCash, 0);
        Grid.SetColumn(_autoCash, 0);
        Grid.SetColumnSpan(_autoCash, 2);
        grid.Children.Add(_autoCash);

        AddPaymentSummaryField(grid, "Bank", _summaryBankAmount, 0, 2, 3);
        AddPaymentSummaryField(grid, "Kas", _summaryCashAmount, 1, 0, 1);
        AddPaymentSummaryField(grid, "C/G", _summaryGiroAmount, 1, 2, 3);
        AddPaymentSummaryField(grid, "Utang dibayar", _currentPayment, 0, 4, 5);
        AddPaymentSummaryField(grid, "Pengeluaran", _summaryExpense, 1, 4, 5);
        AddPaymentSummaryField(grid, "Penyesuaian", _summaryAdjustment, 0, 6, 7);
        AddPaymentSummaryField(grid, "Unbalance", _summaryUnbalance, 1, 6, 7);
        AddPaymentSummaryField(grid, "Status", _summaryStatus, 0, 8, 9);

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
        AddNav(nav, "◀◀", async () => await GuardedAsync(() => LoadPaymentHistoryAtAsync(_paymentHistory.Count - 1)));
        AddNav(nav, "◀", async () => await GuardedAsync(() => MovePaymentHistoryAsync(1)));
        AddNav(nav, "●", async () => await GuardedAsync(ShowPaymentHistoryAsync));
        AddNav(nav, "▶", async () => await GuardedAsync(() => MovePaymentHistoryAsync(-1)));
        AddNav(nav, "▶▶", async () => await GuardedAsync(() => LoadPaymentHistoryAtAsync(0)));
        grid.Children.Add(nav);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(ActionButton("Cari", async () => await GuardedAsync(ShowPaymentHistoryAsync), 86));
        actions.Children.Add(ActionButton("Tambah", async () => await GuardedAsync(() => { ClearForm(); return Task.CompletedTask; }), 86));
        actions.Children.Add(ActionButton("Batal", CancelInputAsync, 86));
        actions.Children.Add(ActionButton("Simpan", async () => { await SaveAsync(); }, 92));
        actions.Children.Add(ActionButton("Smpn&Ctk", async () => { await SaveAsync(); }, 98));
        _deleteButton = ActionButton("Hapus", async () => await DeleteOrClearAsync(), 86);
        _restoreButton = ActionButton("Btl.Hapus", async () => await RestoreCurrentPaymentAsync(), 92);
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
        _grid.Columns.Add(LedgerAccountView.Column("Group", nameof(SupplierPaymentLineDraft.Group), 82));
        _grid.Columns.Add(LedgerAccountView.Column("Tgl.Bukti", nameof(SupplierPaymentLineDraft.DocumentDate), 105, "dd/MM/yyyy"));
        _grid.Columns.Add(LedgerAccountView.Column("No.Bukti", nameof(SupplierPaymentLineDraft.DocumentNumber), 125));
        _grid.Columns.Add(LedgerAccountView.NumberColumn("Nilai", nameof(SupplierPaymentLineDraft.OriginalValue), 125));
        _grid.Columns.Add(LedgerAccountView.NumberColumn("Sudah Bayar", nameof(SupplierPaymentLineDraft.PaidBefore), 125));
        _grid.Columns.Add(LedgerAccountView.NumberColumn("Sisa Bayar", nameof(SupplierPaymentLineDraft.RemainingBalance), 125));
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Skrg. Bayar",
            Binding = new Binding(nameof(SupplierPaymentLineDraft.PaymentText)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
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
                _documentNumber.Text = await _api.GetNextSupplierPaymentNumberAsync(date);
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
            _paymentHistory = await _api.SearchSupplierPaymentsAsync(null);
            SyncPaymentHistoryIndex();
            UpdatePaymentHistoryCounter();
        }
        catch
        {
            _paymentHistory = [];
            _historyIndex = -1;
            _totalData.Text = "TOTAL DATA 0";
        }
    }

    private async Task LoadPayablesAsync()
    {
        if (_selectedSupplier is null || !Same(_selectedSupplier.Code, _supplierCode.Text))
        {
            if (!await TryResolveSupplierAsync(_supplierCode.Text)) return;
        }

        try
        {
            _loading = true;
            IsEnabled = false;
            _status.Text = "Mengambil daftar nota utang...";
            var rows = (await _api.SearchSupplierPayablesAsync(_selectedSupplier!.Code, _searchNumber.Text)).ToList();
            _lines.Clear();
            foreach (var row in rows)
                _lines.Add(SupplierPaymentLineDraft.From(row));
            AddBlankRows();
            SetTotalDebt(rows.Sum(x => x.RemainingBalance));
            _status.Text = rows.Count == 0 ? "Tidak ada nota utang terbuka untuk supplier ini." : $"{rows.Count:N0} nota utang terbuka.";
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Pembayaran Utang");
        }
        finally
        {
            IsEnabled = true;
            _loading = false;
        }
    }

    private async void SupplierCode_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F4)
        {
            e.Handled = true;
            OpenSupplierLookup();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await TryResolveSupplierAsync(_supplierCode.Text);
        }
    }

    private void OpenSupplierLookup()
    {
        var dialog = new SupplierLookupWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.SelectedSupplier is not null)
        {
            ApplySupplier(dialog.SelectedSupplier);
            _ = LoadPayablesAsync();
        }
    }

    private async Task<bool> TryResolveSupplierAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        try
        {
            var rows = await _api.SearchSuppliersAsync(code);
            var supplier = rows.FirstOrDefault(x => Same(x.Code, code)) ?? rows.FirstOrDefault();
            if (supplier is null) return false;
            ApplySupplier(supplier);
            return true;
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            return false;
        }
    }

    private void ApplySupplier(PurchaseSupplierDto supplier)
    {
        _selectedSupplier = supplier;
        _supplierCode.Text = supplier.Code;
        _supplierName.Text = supplier.CompanyName;
        _supplierAddress.Text = supplier.Address ?? string.Empty;
    }

    private void FillSelectedLine()
    {
        if (_grid.SelectedItem is not SupplierPaymentLineDraft line || line.IsBlank) return;
        line.PaymentText = line.RemainingBalance.ToString("N0", CultureInfo.CurrentCulture);
        UpdateTotals();
        if (_autoCash.IsChecked == true) FillCashAutomatically();
    }

    private void UpdateTotals()
    {
        if (_loading) return;
        var selectedTotal = _lines.Where(x => !x.IsBlank).Sum(x => TryReadAmount(x.PaymentText, out var value) ? value : 0);
        _currentPayment.Text = selectedTotal.ToString("N0", CultureInfo.CurrentCulture);
        if (_autoCash.IsChecked == true) FillCashAutomatically();
    }

    private void FillCashAutomatically()
    {
        if (!TryReadAmount(_currentPayment.Text, out var amount)) amount = _lines.Where(x => !x.IsBlank).Sum(x => TryReadAmount(x.PaymentText, out var value) ? value : 0);
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
            if (_selectedSupplier is null || !Same(_selectedSupplier.Code, _supplierCode.Text))
                if (!await TryResolveSupplierAsync(_supplierCode.Text)) throw new InvalidOperationException("Supplier belum dipilih.");
            if (!TryReadAmount(_currentPayment.Text, out var paymentAmount) || paymentAmount <= 0) throw new InvalidOperationException("Skrg Bayar harus diisi.");
            var requestLines = _lines
                .Where(x => !x.IsBlank && TryReadAmount(x.PaymentText, out var amount) && amount > 0)
                .Select(x => new CreateSupplierPaymentLineRequest(x.PurchaseId, ParseAmount(x.PaymentText), x.SourceType))
                .ToArray();
            if (requestLines.Length == 0) throw new InvalidOperationException("Pilih nota utang dahulu.");

            var method = ResolveMethod(paymentAmount, out var cashCode, out var bankCode, out var giroNumber, out var giroDueDate);
            IsEnabled = false;
            _status.Text = "Menyimpan pembayaran utang dan jurnal...";
            var saved = await _api.CreateSupplierPaymentAsync(new CreateSupplierPaymentRequest(
                DateOnly.FromDateTime(_date.SelectedDate ?? DateTime.Today),
                _documentNumber.Text,
                _selectedSupplier!.Code,
                method,
                cashCode,
                bankCode,
                giroNumber,
                giroDueDate,
                paymentAmount,
                _notes.Text,
                AksaraApiClient.Shared.CurrentUserName,
                Environment.MachineName,
                requestLines));
            LocalTransactionDraftStore.Delete(LocalDraftKey);
            _status.Text = $"Pembayaran {saved.DocumentNumber} tersimpan. Jurnal: {saved.JournalNumber}.";
            await ReloadHistoryCountAsync();
            ClearAmounts();
            ClearPaymentMethods();
            await LoadPayablesAsync();
            _documentNumber.Clear();
            await PreviewNumberAsync();
            MarkClean();
            return true;
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Pembayaran Utang");
            return false;
        }
        finally
        {
            _saving = false;
            IsEnabled = true;
        }
    }

    private string ResolveMethod(decimal paymentAmount, out string? cashCode, out string? bankCode, out string? giroNumber, out DateOnly? giroDueDate)
    {
        var cash = ParseAmount(_cashAmount.Text);
        var bank = ParseAmount(_bankAmount.Text);
        var giro = ParseAmount(_giroAmount.Text);
        var filled = new[] { cash > 0, bank > 0, giro > 0 }.Count(x => x);
        if (filled == 0) throw new InvalidOperationException("Isi metode bayar Kas, Bank, atau C/G.");
        if (filled > 1) throw new InvalidOperationException("Untuk tahap ini satu pembayaran memakai satu metode bayar dulu.");
        if (cash + bank + giro != paymentAmount) throw new InvalidOperationException("Total metode bayar harus sama dengan Skrg Bayar.");
        cashCode = null; bankCode = null; giroNumber = null; giroDueDate = null;
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

        bankCode = _giroBankCode.Text;
        giroNumber = _giroNumber.Text;
        if (!TryReadOptionalDate(_giroDueDate.Text, out giroDueDate))
            throw new InvalidOperationException("Jth.Tempo tidak valid. Gunakan format dd/MM/yyyy.");
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

        var choice = LocalDraftPromptWindow.Show(Window.GetWindow(this), "Pembayaran Utang");
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
                    ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Draft Pembayaran Utang");
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
        NormalizeGiroDueDateText();
        return HasMeaningfulDraftInput() && !Same(CurrentDraftSignature(), _cleanDraftSignature);
    }

    private bool HasMeaningfulDraftInput()
    {
        if (!string.IsNullOrWhiteSpace(_notes.Text)) return true;
        if (ParseAmount(_currentPayment.Text) > 0 || ParseAmount(_cashAmount.Text) > 0 || ParseAmount(_bankAmount.Text) > 0 || ParseAmount(_giroAmount.Text) > 0) return true;
        if (!string.IsNullOrWhiteSpace(_cashCode.Text) || !string.IsNullOrWhiteSpace(_bankCode.Text) || !string.IsNullOrWhiteSpace(_giroBankCode.Text)) return true;
        if (!string.IsNullOrWhiteSpace(_giroNumber.Text) || !string.IsNullOrWhiteSpace(_giroDueDate.Text)) return true;
        return _lines.Any(line => !line.IsBlank && ParseAmount(line.PaymentText) > 0);
    }

    private string CurrentDraftSignature()
    {
        var lineSignature = string.Join("|", _lines
            .Where(line => !line.IsBlank)
            .Select(line => $"{line.PurchaseId:N}:{NormalizeForSignature(line.PaymentText)}"));
        return string.Join("\u001F",
            FormatDate(_date.SelectedDate is null ? null : DateOnly.FromDateTime(_date.SelectedDate.Value)),
            NormalizeForSignature(_documentNumber.Text),
            NormalizeForSignature(_supplierCode.Text),
            NormalizeForSignature(_currentPayment.Text),
            NormalizeForSignature(_cashCode.Text),
            NormalizeForSignature(_cashAmount.Text),
            NormalizeForSignature(_bankCode.Text),
            NormalizeForSignature(_bankAmount.Text),
            NormalizeForSignature(_giroBankCode.Text),
            NormalizeForSignature(_giroNumber.Text),
            NormalizeForSignature(_giroDueDate.Text),
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
        LocalTransactionDraftStore.Save(LocalDraftKey, new SupplierPaymentLocalDraft(
            _date.SelectedDate,
            _documentNumber.Text,
            _supplierCode.Text,
            _supplierName.Text,
            _supplierAddress.Text,
            _group.Text,
            _searchNumber.Text,
            _payablesUntilDate.SelectedDate,
            _currentPayment.Text,
            _cashCode.Text,
            _cashName.Text,
            _cashAmount.Text,
            _bankCode.Text,
            _bankName.Text,
            _bankAmount.Text,
            _giroBankCode.Text,
            _giroBankName.Text,
            _giroNumber.Text,
            _giroDueDate.Text,
            _giroAmount.Text,
            _autoCash.IsChecked == true,
            _notes.Text,
            _lines.Where(line => !line.IsBlank)
                .Select(line => new SupplierPaymentLineLocalDraft(
                    line.PurchaseId, line.Group, line.DocumentNumber, line.DocumentDate,
                    line.SupplierInvoiceNumber, line.DueDate, line.SupplierName,
                    line.OriginalValue, line.PaidBefore, line.RemainingBalance, line.PaymentText, line.SourceType))
                .ToArray()));
        _modeStatus.Text = "DRAFT LOKAL";
        _status.Text = "Draft Pembayaran Utang tersimpan lokal dan belum masuk jurnal.";
    }

    private void RestoreLocalDraft()
    {
        if (_draftChecked) return;
        _draftChecked = true;
        if (!LocalTransactionDraftStore.TryLoad<SupplierPaymentLocalDraft>(LocalDraftKey, out var draft) || draft is null) return;

        _currentPaymentRecord = null;
        _selectedSupplier = null;
        _date.SelectedDate = draft.DocumentDate ?? DateTime.Today;
        _documentNumber.Text = draft.DocumentNumber;
        _supplierCode.Text = draft.SupplierCode;
        _supplierName.Text = draft.SupplierName;
        _supplierAddress.Text = draft.SupplierAddress;
        _group.Text = draft.Group;
        _searchNumber.Text = draft.SearchNumber;
        _payablesUntilDate.SelectedDate = draft.PayablesUntilDate ?? DateTime.Today;
        _autoCash.IsChecked = false;
        _currentPayment.Text = draft.CurrentPayment;
        _cashCode.Text = draft.CashCode;
        _cashName.Text = draft.CashName;
        _cashAmount.Text = draft.CashAmount;
        _bankCode.Text = draft.BankCode;
        _bankName.Text = draft.BankName;
        _bankAmount.Text = draft.BankAmount;
        _giroBankCode.Text = draft.GiroBankCode;
        _giroBankName.Text = draft.GiroBankName;
        _giroNumber.Text = draft.GiroNumber;
        _giroDueDate.Text = draft.GiroDueDate;
        _giroAmount.Text = draft.GiroAmount;
        _notes.Text = draft.Notes;
        _lines.Clear();
        foreach (var line in draft.Lines)
        {
            _lines.Add(new SupplierPaymentLineDraft
            {
                PurchaseId = line.PurchaseId,
                Group = line.Group,
                DocumentNumber = line.DocumentNumber,
                DocumentDate = line.DocumentDate,
                SupplierInvoiceNumber = line.SupplierInvoiceNumber,
                DueDate = line.DueDate,
                SupplierName = line.SupplierName,
                OriginalValue = line.OriginalValue,
                PaidBefore = line.PaidBefore,
                RemainingBalance = line.RemainingBalance,
                PaymentText = line.PaymentText,
                SourceType = line.SourceType
            });
        }
        AddBlankRows();
        _autoCash.IsChecked = draft.AutoCash;
        SetTotalDebt(draft.Lines.Sum(line => line.RemainingBalance));
        _modeStatus.Text = "DRAFT LOKAL";
        _historyIndex = -1;
        UpdatePaymentHistoryCounter();
        UpdateCommandStates();
        UpdateTotals();
        _status.Text = "Draft Pembayaran Utang dipulihkan. Tekan Simpan untuk menjadikannya transaksi.";
    }

    private void CommitPendingGridEdit()
    {
        _grid.CommitEdit(DataGridEditingUnit.Cell, true);
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private static string NormalizeForSignature(string? text) => (text ?? string.Empty).Trim();

    private async Task ShowPaymentHistoryAsync()
    {
        try
        {
            _status.Text = "Membuka history pembayaran utang...";
            if (_paymentHistory.Count == 0) await ReloadHistoryCountAsync();
            if (_paymentHistory.Count == 0)
            {
                _status.Text = "Belum ada history pembayaran utang.";
                return;
            }

            var dialog = new SupplierPaymentLookupWindow { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true || dialog.SelectedPayment is null)
            {
                _status.Text = $"History tersedia: {_paymentHistory.Count:N0} data.";
                return;
            }

            await ApplyPaymentHistoryAsync(dialog.SelectedPayment);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - History Pembayaran Utang");
        }
    }

    private async Task MovePaymentHistoryAsync(int offset)
    {
        if (_paymentHistory.Count == 0) await ReloadHistoryCountAsync();
        if (_paymentHistory.Count == 0)
        {
            _status.Text = "Belum ada history pembayaran utang.";
            return;
        }

        var nextIndex = _historyIndex < 0
            ? offset > 0 ? _paymentHistory.Count - 1 : 0
            : _historyIndex + offset;
        await LoadPaymentHistoryAtAsync(nextIndex);
    }

    private async Task LoadPaymentHistoryAtAsync(int index)
    {
        if (_paymentHistory.Count == 0) await ReloadHistoryCountAsync();
        if (_paymentHistory.Count == 0)
        {
            _status.Text = "Belum ada history pembayaran utang.";
            return;
        }

        index = Math.Clamp(index, 0, _paymentHistory.Count - 1);
        _historyIndex = index;
        await ApplyPaymentHistoryAsync(_paymentHistory[index]);
    }

    private Task ApplyPaymentHistoryAsync(SupplierPaymentDto payment)
    {
        _currentPaymentRecord = payment;
        _documentNumber.Text = payment.DocumentNumber;
        _date.SelectedDate = payment.DocumentDate.ToDateTime(TimeOnly.MinValue);
        _supplierCode.Text = payment.SupplierCode;
        _supplierName.Text = payment.SupplierName;
        _currentPayment.Text = payment.PaymentAmount.ToString("N0", CultureInfo.CurrentCulture);
        _cashCode.Text = payment.CashAccountCode ?? string.Empty;
        _cashName.Text = payment.CashAccountName ?? string.Empty;
        _bankCode.Text = payment.BankAccountCode ?? string.Empty;
        _bankName.Text = payment.BankAccountName ?? string.Empty;
        _giroBankCode.Text = payment.Method == "CHECK_GIRO" ? payment.BankAccountCode ?? string.Empty : string.Empty;
        _giroBankName.Text = payment.Method == "CHECK_GIRO" ? payment.BankAccountName ?? string.Empty : string.Empty;
        _giroNumber.Text = payment.CheckGiroNumber ?? string.Empty;
        _giroDueDate.Text = FormatDate(payment.CheckGiroDueDate);
        _cashAmount.Text = payment.Method == "CASH" ? payment.PaymentAmount.ToString("N0", CultureInfo.CurrentCulture) : "0";
        _bankAmount.Text = payment.Method == "BANK" ? payment.PaymentAmount.ToString("N0", CultureInfo.CurrentCulture) : "0";
        _giroAmount.Text = payment.Method == "CHECK_GIRO" ? payment.PaymentAmount.ToString("N0", CultureInfo.CurrentCulture) : "0";
        _lines.Clear();
        foreach (var line in payment.Lines)
            _lines.Add(SupplierPaymentLineDraft.From(line, payment.SupplierName));
        AddBlankRows();
        _modeStatus.Text = payment.Status == "POSTED" ? "EDIT DATA" : "DITANDAI HAPUS";
        SyncPaymentHistoryIndex();
        UpdatePaymentHistoryCounter();
        UpdateCommandStates();
        _status.Text = _historyIndex >= 0
            ? $"Data {_historyIndex + 1:N0}/{_paymentHistory.Count:N0}: {payment.DocumentNumber} | {payment.DocumentDate:dd/MM/yyyy}."
            : $"Data {payment.DocumentNumber} dibuka. Simpan hanya untuk transaksi baru.";
        MarkClean();
        return Task.CompletedTask;
    }

    private void ClearForm()
    {
        _selectedSupplier = null;
        _currentPaymentRecord = null;
        _supplierCode.Clear();
        _supplierName.Clear();
        _supplierAddress.Clear();
        _group.Clear();
        _searchNumber.Clear();
        _notes.Clear();
        _documentNumber.Clear();
        ClearAmounts();
        ClearPaymentMethods();
        SetTotalDebt(0);
        _lines.Clear();
        AddBlankRows();
        _modeStatus.Text = "TAMBAH DATA";
        _historyIndex = -1;
        UpdatePaymentHistoryCounter();
        UpdateCommandStates();
        MarkClean();
        _ = PreviewNumberAsync();
    }

    private void ClearAmounts()
    {
        if (_currentPaymentRecord is not null)
        {
            _status.Text = "Data lama sedang dibuka. Klik Tambah untuk membuat pembayaran baru.";
            return;
        }

        foreach (var line in _lines) line.PaymentText = "0";
        _currentPayment.Text = "0";
        _cashAmount.Text = "0";
        _bankAmount.Text = "0";
        _giroAmount.Text = "0";
        SyncMethodSummary();
    }

    private async Task DeleteOrClearAsync()
    {
        if (_currentPaymentRecord is null)
        {
            ClearAmounts();
            return;
        }

        await DeleteCurrentPaymentAsync();
    }

    private async Task DeleteCurrentPaymentAsync()
    {
        if (_currentPaymentRecord is null || !Same(_currentPaymentRecord.Status, "POSTED")) return;
        var owner = Window.GetWindow(this);
        var result = owner is null
            ? MessageBox.Show("Apakah anda ingin mendandai hapus data ini?", "AKSARA", MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(owner, "Apakah anda ingin mendandai hapus data ini?", "AKSARA", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            IsEnabled = false;
            _status.Text = "Menandai hapus pembayaran utang...";
            var cancelled = await _api.CancelSupplierPaymentAsync(_currentPaymentRecord.Id, new("Ditandai hapus dari form pembayaran utang.", AksaraApiClient.Shared.CurrentUserName, Environment.MachineName));
            await ReloadHistoryCountAsync();
            await ApplyPaymentHistoryAsync(cancelled);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Pembayaran Utang");
        }
        finally
        {
            IsEnabled = true;
            UpdateCommandStates();
        }
    }

    private async Task RestoreCurrentPaymentAsync()
    {
        if (_currentPaymentRecord is null || !Same(_currentPaymentRecord.Status, "CANCELLED")) return;
        var owner = Window.GetWindow(this);
        var result = owner is null
            ? MessageBox.Show("Apakah anda ingin membatalkan hapus data?", "AKSARA", MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(owner, "Apakah anda ingin membatalkan hapus data?", "AKSARA", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            IsEnabled = false;
            _status.Text = "Membatalkan hapus pembayaran utang...";
            var restored = await _api.RestoreSupplierPaymentAsync(_currentPaymentRecord.Id, new(AksaraApiClient.Shared.CurrentUserName, Environment.MachineName));
            await ReloadHistoryCountAsync();
            await ApplyPaymentHistoryAsync(restored);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Pembayaran Utang");
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
            _deleteButton.IsEnabled = _currentPaymentRecord is null || Same(_currentPaymentRecord.Status, "POSTED");
        if (_restoreButton is not null)
            _restoreButton.IsEnabled = _currentPaymentRecord is not null && Same(_currentPaymentRecord.Status, "CANCELLED");
    }

    private void ClearPaymentMethods()
    {
        _cashCode.Clear();
        _cashName.Clear();
        _bankCode.Clear();
        _bankName.Clear();
        _giroBankCode.Clear();
        _giroBankName.Clear();
        _giroNumber.Clear();
        _giroDueDate.Clear();
    }

    private void SyncMethodSummary()
    {
        var cash = ParseAmount(_cashAmount.Text);
        var bank = ParseAmount(_bankAmount.Text);
        var giro = ParseAmount(_giroAmount.Text);
        var paid = ParseAmount(_currentPayment.Text);
        _summaryCashAmount.Text = cash.ToString("N0", CultureInfo.CurrentCulture);
        _summaryBankAmount.Text = bank.ToString("N0", CultureInfo.CurrentCulture);
        _summaryGiroAmount.Text = giro.ToString("N0", CultureInfo.CurrentCulture);
        _summaryExpense.Text = paid.ToString("N0", CultureInfo.CurrentCulture);
        _summaryAdjustment.Text = "0";
        _summaryUnbalance.Text = (paid - cash - bank - giro).ToString("N0", CultureInfo.CurrentCulture);
    }

    private void SetTotalDebt(decimal amount)
    {
        var formatted = amount.ToString("N0", CultureInfo.CurrentCulture);
        _totalDebt.Text = formatted;
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
        while (_lines.Count < DefaultRows) _lines.Add(SupplierPaymentLineDraft.Blank());
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
        _paymentMethodPanel.Visibility = methodTab ? Visibility.Visible : Visibility.Collapsed;
        if (_payablesToolbar is not null) _payablesToolbar.Visibility = selectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        _hint.Text = methodTab
            ? "Tekan F4 pada keyboard / DOUBLE CLICK tombol kiri mouse pada kolom ini untuk membuka form cari Kas / Bank / Cek-Giro"
            : "Tekan F4 pada keyboard / DOUBLE CLICK tombol kiri mouse pada kolom ini untuk membuka form cari SUPPLIER / NO. BUKTI";
    }

    private static void AddSummaryText(Grid grid, string text, int row, int column)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 10, 3) };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, column);
        grid.Children.Add(label);
    }

    private static void AddPaymentSummaryField(Grid grid, string labelText, UIElement input, int row, int labelColumn, int inputColumn)
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

    private void SyncPaymentHistoryIndex()
    {
        _historyIndex = string.IsNullOrWhiteSpace(_documentNumber.Text)
            ? -1
            : _paymentHistory.ToList().FindIndex(x => Same(x.DocumentNumber, _documentNumber.Text));
    }

    private void UpdatePaymentHistoryCounter()
    {
        _totalData.Text = _historyIndex < 0
            ? $"TOTAL DATA {_paymentHistory.Count:N0}"
            : $"DATA {_historyIndex + 1:N0} / {_paymentHistory.Count:N0}";
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

internal sealed class SupplierPaymentLookupWindow : SalesLookupWindowBase<SupplierPaymentDto>
{
    public SupplierPaymentDto? SelectedPayment { get; private set; }

    public SupplierPaymentLookupWindow() : base("FORM CARI PEMBAYARAN UTANG", 900, 540)
    {
        Grid.Columns.Add(Column("No. Bukti", nameof(SupplierPaymentDto.DocumentNumber), 130));
        Grid.Columns.Add(Column("Tanggal", nameof(SupplierPaymentDto.DocumentDate), 100));
        Grid.Columns.Add(Column("Kode Supplier", nameof(SupplierPaymentDto.SupplierCode), 110));
        Grid.Columns.Add(Column("Toko/PT", nameof(SupplierPaymentDto.SupplierName), 230));
        Grid.Columns.Add(Column("Cara", nameof(SupplierPaymentDto.Method), 100));
        Grid.Columns.Add(Column("Jumlah", nameof(SupplierPaymentDto.PaymentAmount), 120));
        Grid.Columns.Add(Column("Status", nameof(SupplierPaymentDto.Status), 90));
    }

    protected override Task<IReadOnlyList<SupplierPaymentDto>> LoadAsync(string query) => Api.SearchSupplierPaymentsAsync(query);
    protected override void SetSelected(SupplierPaymentDto selected) => SelectedPayment = selected;
}

internal sealed class SupplierPaymentLineDraft : INotifyPropertyChanged
{
    private string _paymentText = "0";

    public Guid PurchaseId { get; init; }
    public string SourceType { get; init; } = "PURCHASE";
    public string Group { get; init; } = string.Empty;
    public string DocumentNumber { get; init; } = string.Empty;
    public DateOnly? DocumentDate { get; init; }
    public string? SupplierInvoiceNumber { get; init; }
    public DateOnly? DueDate { get; init; }
    public string SupplierName { get; init; } = string.Empty;
    public decimal OriginalValue { get; init; }
    public decimal PaidBefore { get; init; }
    public decimal RemainingBalance { get; init; }
    public string PaymentText { get => _paymentText; set => SetField(ref _paymentText, value); }
    public bool IsBlank => PurchaseId == Guid.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public static SupplierPaymentLineDraft From(SupplierPayableDto row) => new()
    {
        PurchaseId = row.PurchaseId,
        SourceType = row.SourceType,
        Group = row.SourceType == "BALANCE_DOCUMENT" ? "SALDO" : "BDG",
        DocumentNumber = row.DocumentNumber,
        DocumentDate = row.DocumentDate,
        SupplierInvoiceNumber = row.SupplierInvoiceNumber,
        DueDate = row.DueDate,
        SupplierName = row.SupplierName,
        OriginalValue = row.BalanceDue,
        PaidBefore = row.PaidAmount,
        RemainingBalance = row.RemainingBalance,
        PaymentText = "0"
    };

    public static SupplierPaymentLineDraft From(SupplierPaymentLineDto row, string supplierName) => new()
    {
        PurchaseId = row.PurchaseId,
        SourceType = row.SourceType,
        Group = row.SourceType == "BALANCE_DOCUMENT" ? "SALDO" : "BDG",
        DocumentNumber = row.PurchaseNumber,
        DocumentDate = row.PurchaseDate,
        SupplierName = supplierName,
        OriginalValue = row.OriginalBalance,
        PaidBefore = row.PaidBefore,
        RemainingBalance = row.OriginalBalance - row.PaidBefore,
        PaymentText = row.Amount.ToString("N0", CultureInfo.CurrentCulture)
    };

    public static SupplierPaymentLineDraft Blank() => new();

    private void SetField(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        value ??= string.Empty;
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
