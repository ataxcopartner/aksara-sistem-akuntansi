using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Aksara.Contracts;
using Aksara.Desktop.Controls;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

public partial class SalesView : UserControl, IWorkspaceCloseGuard
{
    private string LocalDraftKey => _serviceMode ? "service-sales" : "sales";
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly bool _serviceMode;
    private SalesCustomerDto? _selectedCustomer;
    private CashAccountDto? _selectedCashAccount;
    private SalespersonDto? _selectedSalesperson;
    private SaleDto? _currentSale;
    private bool _busy;
    private string _defaultWarehouse = "UTAMA";
    private IReadOnlyList<CashAccountDto> _cashAccounts = Array.Empty<CashAccountDto>();
    private IReadOnlyList<SaleDto> _salesHistory = Array.Empty<SaleDto>();
    private int _salesTotalCount;
    private int _historyIndex = -1;
    private bool _documentNumberManuallyEdited;
    private bool _suppressDocumentNumberChange;
    private bool _formattingMoneyText;
    private string? _suggestedDocumentNumber;
    private bool _draftChecked;

    public DateTime? DocumentDate { get; set; } = DateTime.Today;
    public DateTime? DueDate { get; set; } = DateTime.Today.AddDays(30);
    public ObservableCollection<SalesLineRow> Lines { get; } = [];
    public event EventHandler? CloseRequested;
    public event EventHandler? PurchasePriceCheckRequested;

    public SalesView(bool serviceMode = false)
    {
        _serviceMode = serviceMode;
        InitializeComponent();
        if (_serviceMode)
        {
            LinesGrid.Columns[1].Header = "Kode Jasa";
            LinesGrid.Columns[2].Header = "Nama Jasa";
            LinesGrid.Columns[3].Visibility = Visibility.Collapsed;
            HppGrid.Columns[0].Header = "Kode Jasa";
            HppGrid.Columns[1].Header = "Nama Jasa";
            CttnGrid.Columns[0].Header = "Kode Jasa";
            CttnGrid.Columns[1].Header = "Nama Jasa";
            HppTab.Header = "HPP Bahan";
            PurchasePriceCheckButton.Visibility = Visibility.Collapsed;
        }
        DataContext = this;
        Lines.CollectionChanged += Lines_CollectionChanged;
        DocumentDatePicker.SelectedDateChanged += async (_, _) =>
        {
            if (_currentSale is null && !_documentNumberManuallyEdited)
                await RefreshNewDocumentNumberAsync();
        };
        ResetForm();
        Loaded += async (_, _) =>
        {
            SetReadOnly(false);
            UpdateTotals();
            await RefreshServerStatusAsync();
            await LoadDefaultWarehouseAsync();
            await LoadSalesHistoryAsync();
            await RefreshNewDocumentNumberAsync();
            RestoreLocalDraft();
        };
    }

    private void Lines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (SalesLineRow line in e.NewItems) line.PropertyChanged += Line_PropertyChanged;
        if (e.OldItems is not null)
            foreach (SalesLineRow line in e.OldItems) line.PropertyChanged -= Line_PropertyChanged;
        UpdateTotals();
    }

    private void Line_PropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateTotals();

    private async Task RefreshServerStatusAsync()
    {
        var ready = await _api.IsDatabaseReadyAsync();
        ServerStatusText.Text = ready ? "DATA 0 / 0" : "SERVER BELUM SIAP";
        ServerStatusText.Foreground = ready ? Brushes.Black : new SolidColorBrush(Color.FromRgb(180, 35, 24));
        HelpText.Text = ready
            ? $"ⓘ Tekan F4 pada keyboard / DOUBLE CLICK tombol kiri mouse pada kolom ini untuk membuka form cari {(_serviceMode ? "JASA" : "BARANG")}"
            : $"ⓘ Server/database belum terhubung ({_api.ServerAddress}). Form tetap dapat dilihat tetapi belum dapat menyimpan.";
    }

    private async Task LoadDefaultWarehouseAsync()
    {
        try
        {
            var warehouses = await _api.GetInventoryWarehousesAsync(false);
            var selected = warehouses.FirstOrDefault(x => x.IsDefault) ?? warehouses.FirstOrDefault();
            if (selected is null) return;
            _defaultWarehouse = selected.Code;
        }
        catch (Exception exception) { HelpText.Text = $"Gudang default belum dapat dimuat: {exception.Message}"; }
    }

    private void ResetForm()
    {
        _currentSale = null;
        _selectedCustomer = null;
        DocumentDate = DateTime.Today;
        DueDate = DateTime.Today.AddDays(30);
        if (IsLoaded)
        {
            CustomerCodeBox.Clear();
            CustomerNameBox.Clear();
            CustomerAddressBox.Clear();
            _selectedCashAccount = null;
            _selectedSalesperson = null;
            _documentNumberManuallyEdited = false;
            _suggestedDocumentNumber = null;
            SetDocumentNumber(string.Empty);
            CashAccountBox.Clear();
            SalesCodeBox.Clear();
            DueDaysBox.Text = "30";
            DocumentDatePicker.SelectedDate = DocumentDate;
            DueDatePicker.SelectedDate = DueDate;
            NotesBox.Clear();
            DiscountPercentBox.Text = "0";
            DiscountAmountBox.Text = "0";
            TaxPercentBox.Text = (ProgramConfigurationCache.Current.TaxEnabled ? ProgramConfigurationCache.Current.DefaultTaxPercent : 0).ToString("0.####", CultureInfo.CurrentCulture);
            DownPaymentBox.Text = "0";
            TransactionType.SelectedIndex = 1;
        }

        Lines.Clear();
        for (var index = 0; index < Math.Clamp(ProgramConfigurationCache.Current.TransactionDetailRows, 1, 200); index++) Lines.Add(new SalesLineRow());
        SetReadOnly(false);
        ModeStatusText.Text = "TAMBAH DATA";
        _historyIndex = -1;
        UpdateTotals();
        UpdateSalesCounter();
        _ = RefreshNewDocumentNumberAsync();
    }

    private void SetReadOnly(bool readOnly)
    {
        if (!IsLoaded) return;
        CustomerCodeBox.IsReadOnly = readOnly;
        DocumentNumberBox.IsReadOnly = readOnly;
        SalesCodeBox.IsReadOnly = readOnly;
        NotesBox.IsReadOnly = readOnly;
        DiscountPercentBox.IsReadOnly = readOnly;
        DiscountAmountBox.IsReadOnly = readOnly;
        TaxPercentBox.IsReadOnly = readOnly;
        DownPaymentBox.IsReadOnly = readOnly;
        CashAccountBox.IsReadOnly = readOnly;
        CashAccountBox.IsEnabled = !readOnly && TransactionType.SelectedIndex == 0;
        TransactionType.IsEnabled = !readOnly;
        LinesGrid.IsReadOnly = readOnly;
        CttnGrid.IsReadOnly = readOnly;
        SaveButton.IsEnabled = !readOnly;
        SavePrintButton.IsEnabled = true;
        DeleteButton.IsEnabled = readOnly && string.Equals(_currentSale?.Status, "POSTED", StringComparison.OrdinalIgnoreCase);
        RestoreButton.IsEnabled = readOnly && string.Equals(_currentSale?.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase);
    }

    private async void CustomerCodeBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.F4 or Key.Enter)) return;
        e.Handled = true;
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(CustomerCodeBox.Text) && await TryResolveCustomerAsync(CustomerCodeBox.Text)) return;
        OpenCustomerLookup();
    }

    private void CustomerCodeBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenCustomerLookup();

    private async void SalesCodeBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.F4 or Key.Enter)) return;
        e.Handled = true;
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(SalesCodeBox.Text) && await TryResolveSalespersonAsync(SalesCodeBox.Text)) return;
        OpenSalespersonLookup();
    }

    private void SalesCodeBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSalespersonLookup();

    private void SalesCodeBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        HelpText.Text = "ⓘ Tekan F4 pada keyboard / DOUBLE CLICK tombol kiri mouse pada kolom ini untuk membuka form cari SALES";
    }

    private async void CashAccountBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (TransactionType.SelectedIndex != 0 || e.Key is not (Key.F4 or Key.Enter)) return;
        e.Handled = true;
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(CashAccountBox.Text) && await TryResolveCashAccountAsync(CashAccountBox.Text)) return;
        OpenCashAccountLookup();
    }

    private void CashAccountBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TransactionType.SelectedIndex == 0) OpenCashAccountLookup();
    }

    private void CashAccountBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        HelpText.Text = "ⓘ Tekan F4 pada keyboard / DOUBLE CLICK tombol kiri mouse pada kolom ini untuk membuka form cari Kas";
    }

    private void DocumentNumberBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressDocumentNumberChange || _currentSale is not null) return;
        _documentNumberManuallyEdited = !string.Equals(DocumentNumberBox.Text.Trim(), _suggestedDocumentNumber, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshNewDocumentNumberAsync()
    {
        if (!IsLoaded || _currentSale is not null || _documentNumberManuallyEdited) return;
        try
        {
            var date = DateOnly.FromDateTime(DocumentDatePicker.SelectedDate ?? DateTime.Today);
            SetDocumentNumber(await _api.GetNextSaleNumberAsync(date));
        }
        catch (Exception exception)
        {
            HelpText.Text = $"Nomor nota berikutnya belum dapat dimuat: {exception.Message}";
        }
    }

    private void SetDocumentNumber(string value)
    {
        _suppressDocumentNumberChange = true;
        try
        {
            DocumentNumberBox.Text = value;
            _suggestedDocumentNumber = value;
        }
        finally
        {
            _suppressDocumentNumberChange = false;
        }
    }

    private void OpenCustomerLookup()
    {
        var dialog = new CustomerLookupWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.SelectedCustomer is not null)
            ApplyCustomer(dialog.SelectedCustomer);
    }

    private void OpenCashAccountLookup()
    {
        if (_busy || _currentSale is not null || TransactionType.SelectedIndex != 0) return;
        var dialog = new CashAccountLookupWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.SelectedCashAccount is not null)
            ApplyCashAccount(dialog.SelectedCashAccount);
    }

    private void OpenSalespersonLookup()
    {
        if (_busy || _currentSale is not null) return;
        var dialog = new SalespersonLookupWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.SelectedSalesperson is not null)
            ApplySalesperson(dialog.SelectedSalesperson);
    }

    private async Task<bool> TryResolveCustomerAsync(string code)
    {
        try
        {
            var matches = await _api.SearchCustomersAsync(code);
            var customer = matches.FirstOrDefault(x => string.Equals(x.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));
            if (customer is null) return false;
            ApplyCustomer(customer);
            return true;
        }
        catch (Exception exception)
        {
            ShowError(exception);
            return false;
        }
    }

    private void ApplyCustomer(SalesCustomerDto customer)
    {
        _selectedCustomer = customer;
        CustomerCodeBox.Text = customer.Code;
        CustomerNameBox.Text = customer.CompanyName;
        CustomerAddressBox.Text = customer.Address ?? string.Empty;
        DueDaysBox.Text = customer.CreditDays.ToString(CultureInfo.CurrentCulture);
        DueDate = (DocumentDatePicker.SelectedDate ?? DateTime.Today).AddDays(customer.CreditDays);
        DueDatePicker.SelectedDate = DueDate;
    }

    private async Task<bool> TryResolveSalespersonAsync(string code)
    {
        try
        {
            var matches = await _api.SearchSalespeopleAsync(code);
            var salesperson = matches.FirstOrDefault(x => string.Equals(x.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));
            if (salesperson is null) return false;
            ApplySalesperson(salesperson);
            return true;
        }
        catch (Exception exception)
        {
            ShowError(exception);
            return false;
        }
    }

    private void ApplySalesperson(SalespersonDto salesperson)
    {
        _selectedSalesperson = salesperson;
        SalesCodeBox.Text = salesperson.Code;
        HelpText.Text = $"ⓘ Sales dipilih: {salesperson.Code} - {salesperson.Name}";
    }

    private async Task<IReadOnlyList<CashAccountDto>> LoadCashAccountsAsync()
    {
        if (_cashAccounts.Count == 0) _cashAccounts = await _api.GetCashAccountsAsync(false);
        return _cashAccounts;
    }

    private async Task EnsureDefaultCashAccountAsync()
    {
        if (!IsLoaded || TransactionType.SelectedIndex != 0 || _currentSale is not null) return;
        try
        {
            var accounts = await LoadCashAccountsAsync();
            if (accounts.Count == 0)
            {
                HelpText.Text = "ⓘ Daftar kas belum ada. Isi master Kas Bank dulu sebelum menyimpan transaksi tunai.";
                return;
            }

            var typed = CashAccountBox.Text.Trim();
            var selected = string.IsNullOrWhiteSpace(typed)
                ? null
                : accounts.FirstOrDefault(x => string.Equals(x.Code, typed, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                var mappings = await _api.GetLedgerTransactionMappingsAsync();
                var cashDefault = mappings.FirstOrDefault(x => string.Equals(x.MappingCode, "CASH_DEFAULT", StringComparison.OrdinalIgnoreCase))?.AccountCode;
                selected = accounts.FirstOrDefault(x => string.Equals(x.AccountCode, cashDefault, StringComparison.OrdinalIgnoreCase)) ?? accounts.FirstOrDefault();
            }

            if (selected is not null) ApplyCashAccount(selected);
        }
        catch (Exception exception)
        {
            HelpText.Text = $"ⓘ Kas default belum dapat dimuat: {exception.Message}";
        }
    }

    private async Task<bool> TryResolveCashAccountAsync(string code)
    {
        try
        {
            var accounts = await LoadCashAccountsAsync();
            var account = accounts.FirstOrDefault(x => string.Equals(x.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));
            if (account is null) return false;
            ApplyCashAccount(account);
            return true;
        }
        catch (Exception exception)
        {
            ShowError(exception);
            return false;
        }
    }

    private void ApplyCashAccount(CashAccountDto account)
    {
        _selectedCashAccount = account;
        CashAccountBox.Text = account.Code;
        HelpText.Text = $"ⓘ Kas dipilih: {account.Code} - {account.Name}";
    }

    private void LinesGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F4) return;
        e.Handled = true;
        var line = CurrentOrEmptyLine();
        if (LinesGrid.CurrentColumn?.DisplayIndex == 0) OpenWarehouseLookup(line);
        else if (LinesGrid.CurrentColumn?.DisplayIndex == 5) OpenUnitLookup(line);
        else OpenItemLookup(line);
    }

    private void LinesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LinesGrid.CurrentColumn?.DisplayIndex == 0) OpenWarehouseLookup(CurrentOrEmptyLine());
        else if (LinesGrid.CurrentColumn?.DisplayIndex == 1) OpenItemLookup(CurrentOrEmptyLine());
    }

    private void UnitLookup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SalesLineRow line }) OpenUnitLookup(line);
    }

    private void OpenUnitLookup(SalesLineRow line)
    {
        var dialog = new InventoryUnitLookupWindow(line.Unit) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.SelectedUnit)) return;

        if (string.IsNullOrWhiteSpace(line.ItemCode))
        {
            HelpText.Text = $"Pilih {(_serviceMode ? "jasa" : "barang")} dahulu. Satuan transaksi mengikuti satuan dasar di Master {(_serviceMode ? "Jasa" : "Barang")}.";
            return;
        }

        if (!string.Equals(dialog.SelectedUnit, line.Unit, StringComparison.OrdinalIgnoreCase))
        {
            HelpText.Text = $"Satuan {line.ItemCode} adalah {line.Unit}. Satuan lain memerlukan konversi di Master {(_serviceMode ? "Jasa" : "Barang")}.";
            return;
        }

        LinesGrid.SelectedItem = line;
    }

    private SalesLineRow CurrentOrEmptyLine()
    {
        if (LinesGrid.SelectedItem is SalesLineRow selected) return selected;
        return Lines.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.ItemCode)) ?? AddEmptyLine();
    }

    private SalesLineRow AddEmptyLine()
    {
        var line = new SalesLineRow();
        Lines.Add(line);
        return line;
    }

    private void OpenItemLookup(SalesLineRow line)
    {
        var dialog = new ItemLookupWindow(NormalizeWarehouse(line.WarehouseCode), serviceMode: _serviceMode) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.SelectedItem is not null)
            ApplyItem(line, dialog.SelectedItem);
    }

    private void OpenWarehouseLookup(SalesLineRow line)
    {
        var dialog = new WarehouseLookupWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.SelectedWarehouse is null) return;
        if (!string.Equals(line.WarehouseCode, dialog.SelectedWarehouse.Code, StringComparison.OrdinalIgnoreCase))
        {
            line.WarehouseCode = dialog.SelectedWarehouse.Code;
            line.ItemCode = string.Empty;
            line.ItemName = string.Empty;
            line.Unit = string.Empty;
            line.AvailableStock = 0;
            line.FifoUnitCost = 0;
        }
        LinesGrid.SelectedItem = line;
    }

    private void ApplyItem(SalesLineRow line, SalesItemDto item)
    {
        line.ItemCode = item.Code;
        line.ItemName = item.Name;
        line.Unit = item.Unit;
        line.WarehouseCode = item.WarehouseCode;
        line.AvailableStock = item.AvailableStock;
        line.UnitPrice = item.RetailPrice;
        if (line.Quantity <= 0) line.Quantity = 1;
        LinesGrid.SelectedItem = line;
    }

    private void Lines_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            if (e.Row.Item is SalesLineRow line && e.Column.DisplayIndex == 1 && !string.IsNullOrWhiteSpace(line.ItemCode))
                await TryResolveItemAsync(line);
            UpdateTotals();
        });
    }

    private async Task<bool> TryResolveItemAsync(SalesLineRow line)
    {
        try
        {
            var matches = await _api.SearchItemsAsync(line.ItemCode, NormalizeWarehouse(line.WarehouseCode), itemType: _serviceMode ? "SERVICE" : "GOODS");
            var item = matches.FirstOrDefault(x => string.Equals(x.Code, line.ItemCode.Trim(), StringComparison.OrdinalIgnoreCase));
            if (item is null) return false;
            ApplyItem(line, item);
            return true;
        }
        catch (Exception exception)
        {
            ShowError(exception);
            return false;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveSaleAsync();
    private async void SavePrint_Click(object sender, RoutedEventArgs e) => await SaveAndPrintSaleAsync();
    private void PurchasePriceCheck_Click(object sender, RoutedEventArgs e) => PurchasePriceCheckRequested?.Invoke(this, EventArgs.Empty);

    private async Task<SaleDto?> SaveSaleAsync(bool showMessage = true)
    {
        if (_busy) return null;
        if (_currentSale is not null) return _currentSale;
        LinesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        LinesGrid.CommitEdit(DataGridEditingUnit.Row, true);

        try
        {
            SetBusy(true, _serviceMode ? "MENYIMPAN PENJUALAN JASA & BAHAN..." : "MENYIMPAN & MENGURANGI STOK...");
            if (!await _api.IsDatabaseReadyAsync())
                throw new AksaraApiException($"Server atau database belum siap di {_api.ServerAddress}.");

            if (_selectedCustomer is null || !string.Equals(_selectedCustomer.Code, CustomerCodeBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                if (!await TryResolveCustomerAsync(CustomerCodeBox.Text))
                    throw new InvalidOperationException("Customer belum dipilih atau tidak ditemukan.");
            }

            var activeLines = Lines.Where(x => !string.IsNullOrWhiteSpace(x.ItemCode) || x.Quantity != 0 || x.UnitPrice != 0).ToArray();
            if (activeLines.Length == 0) throw new InvalidOperationException($"Minimal satu {(_serviceMode ? "jasa" : "barang")} harus diisi.");
            foreach (var line in activeLines)
            {
                if (string.IsNullOrWhiteSpace(line.ItemName) && !await TryResolveItemAsync(line))
                    throw new InvalidOperationException($"{(_serviceMode ? "Jasa" : "Barang")} {line.ItemCode} tidak ditemukan.");
                if (line.Quantity <= 0) throw new InvalidOperationException($"Jumlah {(_serviceMode ? "jasa" : "barang")} {line.ItemCode} harus lebih dari nol.");
                if (!_serviceMode && line.Quantity > line.AvailableStock)
                    throw new InvalidOperationException($"Stok {line.ItemCode} tidak cukup. Diminta {line.Quantity:N2}, tersedia {line.AvailableStock:N2}.");
            }

            var cashAccountCode = await CashAccountCodeForSaveAsync();
            var salesCode = await SalesCodeForSaveAsync();
            var request = new CreateSaleRequest(
                DateOnly.FromDateTime(DocumentDatePicker.SelectedDate ?? DateTime.Today),
                NullIfBlank(DocumentNumberBox.Text),
                _selectedCustomer!.Code,
                TransactionType.SelectedIndex == 0 ? "CASH" : "CREDIT",
                cashAccountCode,
                TransactionType.SelectedIndex == 0 ? null : DateOnly.FromDateTime(DueDatePicker.SelectedDate ?? DateTime.Today),
                salesCode,
                NormalizeHeaderWarehouse(activeLines),
                ParseDecimal(DiscountPercentBox.Text),
                ParseDecimal(DiscountAmountBox.Text),
                ParseDecimal(TaxPercentBox.Text),
                TransactionType.SelectedIndex == 0 ? 0m : ParseDecimal(DownPaymentBox.Text),
                NullIfBlank(NotesBox.Text),
                AksaraApiClient.Shared.CurrentUserName,
                Environment.MachineName,
                activeLines.Select(x => new CreateSaleLineRequest(x.ItemCode, x.Quantity, x.UnitPrice, x.DiscountPercent, NullIfBlank(x.WarehouseCode))).ToArray(),
                _serviceMode ? "SERVICE" : "GOODS");

            var saved = await _api.CreateSaleAsync(request);
            LocalTransactionDraftStore.Delete(LocalDraftKey);
            await LoadSaleAsync(saved);
            await LoadSalesHistoryAsync();
            if (showMessage)
                MessageBox.Show(_serviceMode
                    ? $"Nota jasa {saved.DocumentNumber} berhasil disimpan.\nPendapatan, kas/piutang, bahan penunjang, dan HPP telah diposting bersama."
                    : $"Nota {saved.DocumentNumber} berhasil disimpan.\nPersediaan berkurang dan kartu stok tercatat dalam transaksi yang sama.", "AKSARA", MessageBoxButton.OK, MessageBoxImage.Information);
            return saved;
        }
        catch (Exception exception) { ShowError(exception); }
        finally { SetBusy(false); }
        return null;
    }

    private async Task SaveAndPrintSaleAsync()
    {
        var sale = _currentSale ?? await SaveSaleAsync(showMessage: false);
        if (sale is null) return;
        await LoadSalesHistoryAsync();

        var dialog = new TransactionPrintOptionsWindow(
            _serviceMode ? "Cetak Nota Penjualan Jasa" : "Cetak Nota Penjualan",
            "No. Nota",
            sale.DocumentNumber,
            [
                new("sales-invoice", "Nota Penjualan"),
                new("sales-delivery", "Surat Jalan"),
                new("sales-receipt", "Kwitansi"),
                new("sales-handover", "Tanda Terima"),
                new("sales-tax-simple", "Faktur Pajak Sederhana"),
                new("sales-tax-standard", "Faktur Pajak Standar")
            ],
            _salesHistory.Select(x => new TransactionPrintHistoryEntry(x.DocumentDate, x.DocumentNumber, x.CustomerName, x.GrandTotal, x)).ToArray())
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true) return;

        var selected = await ResolveSaleForPrintAsync(dialog.SelectedNumber, sale);
        if (selected is null) return;
        var document = BuildSaleDocument(selected, dialog.SelectedOptionId);
        RunPrintAction(dialog.SelectedAction, document);
    }

    private async Task<SaleDto?> ResolveSaleForPrintAsync(string number, SaleDto fallback)
    {
        if (string.IsNullOrWhiteSpace(number) || string.Equals(number, fallback.DocumentNumber, StringComparison.OrdinalIgnoreCase)) return fallback;
        var selected = _salesHistory.FirstOrDefault(x => string.Equals(x.DocumentNumber, number, StringComparison.OrdinalIgnoreCase));
        if (selected is not null) return selected;
        try
        {
            return (await _api.SearchSalesAsync(number, documentKind: _serviceMode ? "SERVICE" : "GOODS")).FirstOrDefault(x => string.Equals(x.DocumentNumber, number, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            ShowError(exception);
            return null;
        }
    }

    private static PrintableTransaction BuildSaleDocument(SaleDto sale, string optionId)
    {
        var kind = sale.DocumentKind == "SERVICE" ? " Jasa" : string.Empty;
        var title = optionId switch
        {
            "sales-delivery" => $"Surat Jalan{kind}",
            "sales-receipt" => $"Kwitansi{kind}",
            "sales-handover" => $"Tanda Terima{kind}",
            "sales-tax-simple" => $"Faktur Pajak Sederhana Penjualan{kind}",
            "sales-tax-standard" => $"Faktur Pajak Standar Penjualan{kind}",
            _ => $"Nota Penjualan{kind}"
        };
        return new PrintableTransaction(
            title,
            "No. Nota",
            sale.DocumentNumber,
            sale.DocumentDate,
            "Customer",
            sale.CustomerCode,
            sale.CustomerName,
            null,
            sale.TransactionType,
            sale.WarehouseCode,
            sale.SalesCode,
            sale.DueDate,
            sale.Subtotal,
            sale.DiscountAmount,
            sale.TaxAmount,
            sale.GrandTotal,
            sale.Notes,
            sale.Lines.Select((line, index) => new PrintableTransactionLine(index + 1, line.ItemCode, line.ItemName, line.Quantity, line.Unit, line.UnitPrice, line.DiscountPercent, line.LineTotal)).ToArray());
    }

    private static void RunPrintAction(TransactionPrintAction action, PrintableTransaction document)
    {
        if (action == TransactionPrintAction.Preview) TransactionDocumentOutput.Preview(document);
        else if (action == TransactionPrintAction.ExportExcel) TransactionDocumentOutput.ExportExcel(document);
        else TransactionDocumentOutput.Print(document);
    }

    private async Task<string?> CashAccountCodeForSaveAsync()
    {
        if (TransactionType.SelectedIndex != 0) return null;
        if (_selectedCashAccount is null || !string.Equals(_selectedCashAccount.Code, CashAccountBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(CashAccountBox.Text) && await TryResolveCashAccountAsync(CashAccountBox.Text))
                return _selectedCashAccount?.Code;
            await EnsureDefaultCashAccountAsync();
        }

        return _selectedCashAccount?.Code
            ?? throw new InvalidOperationException("Kas belum dipilih. Tekan F4 atau double-click pada kolom Kas untuk memilih.");
    }

    private async Task<string?> SalesCodeForSaveAsync()
    {
        if (string.IsNullOrWhiteSpace(SalesCodeBox.Text)) return null;
        if (_selectedSalesperson is null || !string.Equals(_selectedSalesperson.Code, SalesCodeBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            if (!await TryResolveSalespersonAsync(SalesCodeBox.Text))
                throw new InvalidOperationException("Sales tidak ditemukan. Tekan F4 atau double-click pada kolom Sls. untuk memilih.");
        }

        return _selectedSalesperson?.Code;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSale is null && !ConfirmPendingDraft()) return;
        ResetForm();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSale is null && !ConfirmPendingDraft()) return;
        ResetForm();
    }

    public Task<bool> ConfirmCloseAsync() => Task.FromResult(_currentSale is not null || ConfirmPendingDraft());

    private bool ConfirmPendingDraft()
    {
        if (!HasMeaningfulDraftInput()) return true;

        var choice = LocalDraftPromptWindow.Show(Window.GetWindow(this), "Penjualan");
        if (choice == LocalDraftChoice.KeepEditing) return false;
        if (choice == LocalDraftChoice.SaveDraft)
        {
            try
            {
                SaveLocalDraft();
                return true;
            }
            catch (Exception exception)
            {
                ShowError(new InvalidOperationException($"Draft lokal tidak dapat disimpan: {exception.Message}", exception));
                return false;
            }
        }

        LocalTransactionDraftStore.Delete(LocalDraftKey);
        return true;
    }

    private bool HasMeaningfulDraftInput()
    {
        CommitPendingGridEdit();
        return !string.IsNullOrWhiteSpace(CustomerCodeBox.Text)
            || !string.IsNullOrWhiteSpace(SalesCodeBox.Text)
            || !string.IsNullOrWhiteSpace(CashAccountBox.Text)
            || !string.IsNullOrWhiteSpace(NotesBox.Text)
            || ParseDecimal(DiscountPercentBox.Text) != 0
            || ParseDecimal(DiscountAmountBox.Text) != 0
            || ParseDecimal(DownPaymentBox.Text) != 0
            || Lines.Any(line => !string.IsNullOrWhiteSpace(line.ItemCode)
                || line.Quantity != 0
                || line.UnitPrice != 0
                || !string.IsNullOrWhiteSpace(line.LineNote));
    }

    private void SaveLocalDraft()
    {
        CommitPendingGridEdit();
        var activeLines = Lines
            .Where(line => !string.IsNullOrWhiteSpace(line.ItemCode)
                || line.Quantity != 0
                || line.UnitPrice != 0
                || !string.IsNullOrWhiteSpace(line.LineNote))
            .Select(line => new SalesLocalDraftLine(
                line.WarehouseCode, line.ItemCode, line.ItemName, line.Quantity, line.Unit,
                line.AvailableStock, line.UnitPrice, line.DiscountPercent, line.FifoUnitCost,
                line.PackageQuantity, line.LineNote))
            .ToArray();
        LocalTransactionDraftStore.Save(LocalDraftKey, new SalesLocalDraft(
            DocumentDatePicker.SelectedDate,
            DocumentNumberBox.Text,
            CustomerCodeBox.Text,
            CustomerNameBox.Text,
            CustomerAddressBox.Text,
            TransactionType.SelectedIndex,
            DueDatePicker.SelectedDate,
            DueDaysBox.Text,
            SalesCodeBox.Text,
            CashAccountBox.Text,
            NotesBox.Text,
            DiscountPercentBox.Text,
            DiscountAmountBox.Text,
            TaxPercentBox.Text,
            DownPaymentBox.Text,
            activeLines));
        HelpText.Text = "Draft Penjualan tersimpan lokal dan akan dipulihkan saat form dibuka kembali.";
    }

    private void RestoreLocalDraft()
    {
        if (_draftChecked) return;
        _draftChecked = true;
        if (!LocalTransactionDraftStore.TryLoad<SalesLocalDraft>(LocalDraftKey, out var draft) || draft is null) return;

        _currentSale = null;
        _selectedCustomer = null;
        _selectedCashAccount = null;
        _selectedSalesperson = null;
        _documentNumberManuallyEdited = true;
        DocumentDatePicker.SelectedDate = draft.DocumentDate ?? DateTime.Today;
        SetDocumentNumber(draft.DocumentNumber);
        CustomerCodeBox.Text = draft.CustomerCode;
        CustomerNameBox.Text = draft.CustomerName;
        CustomerAddressBox.Text = draft.CustomerAddress;
        TransactionType.SelectedIndex = Math.Clamp(draft.TransactionTypeIndex, 0, 1);
        DueDatePicker.SelectedDate = draft.DueDate ?? DateTime.Today.AddDays(30);
        DueDaysBox.Text = draft.DueDays;
        SalesCodeBox.Text = draft.SalesCode;
        CashAccountBox.Text = draft.CashAccountCode;
        NotesBox.Text = draft.Notes;
        DiscountPercentBox.Text = draft.DiscountPercent;
        DiscountAmountBox.Text = draft.DiscountAmount;
        TaxPercentBox.Text = draft.TaxPercent;
        DownPaymentBox.Text = draft.DownPayment;
        Lines.Clear();
        foreach (var line in draft.Lines)
        {
            Lines.Add(new SalesLineRow
            {
                WarehouseCode = line.WarehouseCode,
                ItemCode = line.ItemCode,
                ItemName = line.ItemName,
                Quantity = line.Quantity,
                Unit = line.Unit,
                AvailableStock = line.AvailableStock,
                UnitPrice = line.UnitPrice,
                DiscountPercent = line.DiscountPercent,
                FifoUnitCost = line.FifoUnitCost,
                PackageQuantity = line.PackageQuantity,
                LineNote = line.LineNote
            });
        }
        AddBlankRows();
        SetReadOnly(false);
        ModeStatusText.Text = "DRAFT LOKAL";
        HelpText.Text = "Draft Penjualan dipulihkan. Tekan Simpan untuk menjadikannya transaksi.";
        UpdateTotals();
    }

    private void AddBlankRows()
    {
        var minimum = Math.Clamp(ProgramConfigurationCache.Current.TransactionDetailRows, 1, 200);
        while (Lines.Count < minimum) Lines.Add(new SalesLineRow());
    }

    private void CommitPendingGridEdit()
    {
        LinesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        LinesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        CttnGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        CttnGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaleLookupWindow(_serviceMode) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.SelectedSale is not null)
        {
            await LoadSaleAsync(dialog.SelectedSale);
            await LoadSalesHistoryAsync();
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSale is null || !string.Equals(_currentSale.Status, "POSTED", StringComparison.OrdinalIgnoreCase)) return;
        if (MessageBox.Show("Apakah anda ingin mendandai hapus data ini?", "AKSARA", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        try
        {
            SetBusy(true, "MENANDAI HAPUS DATA...");
            var cancelled = await _api.CancelSaleAsync(_currentSale.Id, new CancelSaleRequest("Ditandai hapus dari form penjualan.", AksaraApiClient.Shared.CurrentUserName, Environment.MachineName));
            await LoadSaleAsync(cancelled);
            await LoadSalesHistoryAsync();
        }
        catch (Exception exception) { ShowError(exception); }
        finally { SetBusy(false); }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSale is null || !string.Equals(_currentSale.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) return;
        if (MessageBox.Show("Apakah anda ingin membatalkan hapus data?", "AKSARA", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        try
        {
            SetBusy(true, "MEMBATALKAN TANDA HAPUS...");
            var restored = await _api.RestoreSaleAsync(_currentSale.Id, new RestoreSaleRequest(AksaraApiClient.Shared.CurrentUserName, Environment.MachineName));
            await LoadSaleAsync(restored);
            await LoadSalesHistoryAsync();
        }
        catch (Exception exception) { ShowError(exception); }
        finally { SetBusy(false); }
    }

    private async Task LoadSaleAsync(SaleDto sale)
    {
        _currentSale = sale;
        DocumentDate = sale.DocumentDate.ToDateTime(TimeOnly.MinValue);
        DueDate = sale.DueDate?.ToDateTime(TimeOnly.MinValue);
        DocumentDatePicker.SelectedDate = DocumentDate;
        DueDatePicker.SelectedDate = DueDate;
        _documentNumberManuallyEdited = false;
        SetDocumentNumber(sale.DocumentNumber);
        CustomerCodeBox.Text = sale.CustomerCode;
        CustomerNameBox.Text = sale.CustomerName;
        _selectedSalesperson = null;
        SalesCodeBox.Text = sale.SalesCode ?? string.Empty;
        TransactionType.SelectedIndex = sale.TransactionType == "CASH" ? 0 : 1;
        if (string.IsNullOrWhiteSpace(sale.CashAccountCode))
        {
            _selectedCashAccount = null;
            CashAccountBox.Clear();
        }
        else
        {
            ApplyCashAccount(new CashAccountDto(sale.CashAccountCode, sale.CashAccountName ?? sale.CashAccountCode, string.Empty, string.Empty, true, 0));
        }
        DiscountPercentBox.Text = "0";
        DiscountAmountBox.Text = FormatMoney(sale.DiscountAmount);
        TaxPercentBox.Text = sale.Subtotal - sale.DiscountAmount == 0 ? "0" : (sale.TaxAmount * 100m / (sale.Subtotal - sale.DiscountAmount)).ToString("0.####", CultureInfo.CurrentCulture);
        DownPaymentBox.Text = FormatMoney(sale.DownPayment);
        NotesBox.Text = sale.Notes ?? string.Empty;
        Lines.Clear();

        var currentItems = new List<SalesItemDto>();
        foreach (var warehouse in sale.Lines.Select(x => x.WarehouseCode).Distinct(StringComparer.OrdinalIgnoreCase))
            currentItems.AddRange(await _api.SearchItemsAsync(string.Empty, warehouse, itemType: _serviceMode ? "SERVICE" : "GOODS"));
        foreach (var line in sale.Lines)
        {
            var item = currentItems.FirstOrDefault(x => string.Equals(x.Code, line.ItemCode, StringComparison.OrdinalIgnoreCase) && string.Equals(x.WarehouseCode, line.WarehouseCode, StringComparison.OrdinalIgnoreCase));
            Lines.Add(new SalesLineRow
            {
                WarehouseCode = line.WarehouseCode,
                ItemCode = line.ItemCode,
                ItemName = line.ItemName,
                Quantity = line.Quantity,
                Unit = line.Unit,
                AvailableStock = item?.AvailableStock ?? 0m,
                UnitPrice = line.UnitPrice,
                DiscountPercent = line.DiscountPercent,
                FifoUnitCost = line.Quantity == 0 ? 0 : line.CostAmount / line.Quantity
            });
        }
        while (Lines.Count < 10) Lines.Add(new SalesLineRow());

        ModeStatusText.Text = sale.Status == "CANCELLED" ? "DITANDAI HAPUS" : "EDIT DATA";
        SyncHistoryIndex();
        UpdateSalesCounter();
        SetReadOnly(true);
        UpdateTotals();
    }

    private void DueDaysBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(DueDaysBox.Text, out var days))
        {
            DueDate = (DocumentDatePicker.SelectedDate ?? DateTime.Today).AddDays(Math.Max(0, days));
            DueDatePicker.SelectedDate = DueDate;
        }
    }

    private async void TransactionType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var cash = TransactionType.SelectedIndex == 0;
        if (DownPaymentBox is not null) DownPaymentBox.IsEnabled = !cash && _currentSale is null;
        if (CashAccountBox is not null)
        {
            CashAccountBox.IsEnabled = cash && _currentSale is null;
            if (!cash && _currentSale is null)
            {
                _selectedCashAccount = null;
                CashAccountBox.Clear();
            }
            else if (cash && _currentSale is null && string.IsNullOrWhiteSpace(CashAccountBox.Text))
            {
                await EnsureDefaultCashAccountAsync();
            }
            HelpText.Text = cash
                ? "ⓘ Tekan F4 pada keyboard / DOUBLE CLICK tombol kiri mouse pada kolom ini untuk membuka form cari Kas"
                : $"ⓘ Tekan F4 pada keyboard / DOUBLE CLICK tombol kiri mouse pada kolom ini untuk membuka form cari {(_serviceMode ? "JASA" : "BARANG")}";
        }
        UpdateTotals();
    }

    private void TotalsInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        FormatMoneyInput(sender as TextBox);
        UpdateTotals();
    }

    private async void DetailTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DetailTabs is null || LinesGrid is null || HppGrid is null || CttnGrid is null) return;
        LinesGrid.Visibility = DetailTabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        HppGrid.Visibility = DetailTabs.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        CttnGrid.Visibility = DetailTabs.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        if (DetailTabs.SelectedIndex == 1 && _currentSale is null) await RefreshFifoPreviewAsync();
    }

    private async void NavigateFirst_Click(object sender, RoutedEventArgs e) => await LoadSalesHistoryAtAsync(_salesHistory.Count - 1);
    private async void NavigatePrevious_Click(object sender, RoutedEventArgs e) => await MoveSalesHistoryAsync(1);
    private void NavigateLookup_Click(object sender, RoutedEventArgs e) => Search_Click(sender, e);
    private async void NavigateNext_Click(object sender, RoutedEventArgs e) => await MoveSalesHistoryAsync(-1);
    private async void NavigateLast_Click(object sender, RoutedEventArgs e) => await LoadSalesHistoryAtAsync(0);

    private async Task LoadSalesHistoryAsync()
    {
        try
        {
            _salesHistory = await _api.SearchSalesAsync(null, documentKind: _serviceMode ? "SERVICE" : "GOODS");
            _salesTotalCount = await _api.CountSalesAsync(null, documentKind: _serviceMode ? "SERVICE" : "GOODS");
            SyncHistoryIndex();
            UpdateSalesCounter();
        }
        catch (Exception exception)
        {
            HelpText.Text = $"Daftar penjualan belum dapat dimuat: {exception.Message}";
        }
    }

    private async Task MoveSalesHistoryAsync(int offset)
    {
        if (_salesHistory.Count == 0) await LoadSalesHistoryAsync();
        if (_salesHistory.Count == 0)
        {
            HelpText.Text = "Belum ada data penjualan tersimpan.";
            return;
        }

        var nextIndex = _historyIndex < 0
            ? offset > 0 ? _salesHistory.Count - 1 : 0
            : _historyIndex + offset;
        await LoadSalesHistoryAtAsync(nextIndex);
    }

    private async Task LoadSalesHistoryAtAsync(int index)
    {
        if (_salesHistory.Count == 0) await LoadSalesHistoryAsync();
        if (_salesHistory.Count == 0)
        {
            HelpText.Text = "Belum ada data penjualan tersimpan.";
            return;
        }

        index = Math.Clamp(index, 0, _salesHistory.Count - 1);
        _historyIndex = index;
        await LoadSaleAsync(_salesHistory[index]);
        HelpText.Text = $"Data {_historyIndex + 1:N0}/{_salesHistory.Count:N0}: {_salesHistory[index].DocumentNumber} | {_salesHistory[index].DocumentDate:dd/MM/yyyy}.";
    }

    private void SyncHistoryIndex()
    {
        _historyIndex = _currentSale is null
            ? -1
            : _salesHistory.ToList().FindIndex(x => x.Id == _currentSale.Id);
    }

    private void UpdateSalesCounter()
    {
        if (ServerStatusText is null) return;
        var total = _salesTotalCount > 0 ? _salesTotalCount : _salesHistory.Count;
        ServerStatusText.Text = _currentSale is null || _historyIndex < 0
            ? $"TOTAL DATA {total:N0}"
            : $"DATA {_historyIndex + 1:N0} / {total:N0}";
    }

    private async Task RefreshFifoPreviewAsync()
    {
        var active = Lines.Where(x => !string.IsNullOrWhiteSpace(x.ItemCode) && x.Quantity > 0).ToArray();
        if (active.Length == 0) return;
        try
        {
            var preview = await _api.PreviewFifoCostAsync(new FifoCostPreviewRequest(active
                .Select(x => new FifoCostPreviewLineRequest(x.ItemCode, NormalizeWarehouse(x.WarehouseCode), x.Quantity)).ToArray()));
            for (var index = 0; index < active.Length && index < preview.Lines.Count; index++)
                active[index].FifoUnitCost = preview.Lines[index].UnitCost;
            HelpText.Text = $"HPP FIFO sementara: {preview.TotalCost:N2}. Nilai final dikunci saat nota disimpan.";
        }
        catch (Exception exception) { HelpText.Text = $"HPP FIFO belum dapat dihitung: {exception.Message}"; }
    }

    private void UpdateTotals()
    {
        if (TotalText is null || BalanceText is null || DiscountTotalText is null || NetBeforeTaxText is null) return;
        var subtotal = Lines.Sum(x => x.Total);
        var discount = Math.Min(subtotal, Math.Round(subtotal * ParseDecimal(DiscountPercentBox?.Text) / 100m, 2) + ParseDecimal(DiscountAmountBox?.Text));
        var net = subtotal - discount;
        var tax = Math.Round(net * ParseDecimal(TaxPercentBox?.Text) / 100m, 2);
        var total = net + tax;
        var balance = TransactionType?.SelectedIndex == 0 ? 0m : Math.Max(0, total - ParseDecimal(DownPaymentBox?.Text));
        DiscountTotalText.Text = discount.ToString("N0");
        NetBeforeTaxText.Text = net.ToString("N0");
        TotalText.Text = total.ToString("N0");
        BalanceText.Text = balance.ToString("N0");
        UpdateTransactionBanner(balance);
    }

    private void UpdateTransactionBanner(decimal balance)
    {
        if (TransactionBanner is null || TransactionType is null) return;
        var cash = TransactionType.SelectedIndex == 0;
        if (_currentSale is null)
        {
            TransactionBanner.Text = cash ? "TUNAI" : "KREDIT";
        }
        else
        {
            var prefix = cash ? "T" : "K";
            TransactionBanner.Text = $"({prefix}) {(balance <= 0 ? "LUNAS" : "BELUM LUNAS")}";
        }

        TransactionBanner.Foreground = Brushes.Red;
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        if (!string.IsNullOrWhiteSpace(status)) ModeStatusText.Text = status;
        IsEnabled = !busy;
        if (!busy && _currentSale is null) ModeStatusText.Text = "TAMBAH DATA";
    }

    private string NormalizeWarehouse(string? value) => string.IsNullOrWhiteSpace(value) ? _defaultWarehouse : value.Trim().ToUpperInvariant();
    private string NormalizeHeaderWarehouse(IReadOnlyList<SalesLineRow> lines) =>
        lines.Count == 0 ? _defaultWarehouse : NormalizeWarehouse(lines[0].WarehouseCode);
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string FormatMoney(decimal value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private void FormatMoneyInput(TextBox? textBox)
    {
        if (_formattingMoneyText || textBox is null || !textBox.IsKeyboardFocusWithin) return;
        if (textBox != DiscountAmountBox && textBox != DownPaymentBox) return;

        var original = textBox.Text;
        var caret = textBox.CaretIndex;
        var digitsRight = original.Skip(Math.Min(caret, original.Length)).Count(char.IsDigit);
        var digits = new string(original.Where(char.IsDigit).ToArray());
        if (string.IsNullOrWhiteSpace(digits)) return;
        if (!decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) return;

        var formatted = FormatMoney(value);
        if (formatted == original) return;

        _formattingMoneyText = true;
        textBox.Text = formatted;
        textBox.CaretIndex = CaretFromDigitsRight(formatted, digitsRight);
        _formattingMoneyText = false;
    }

    private static int CaretFromDigitsRight(string text, int digitsRight)
    {
        if (digitsRight <= 0) return text.Length;
        var remaining = digitsRight;
        for (var index = text.Length - 1; index >= 0; index--)
        {
            if (!char.IsDigit(text[index])) continue;
            remaining--;
            if (remaining == 0) return index;
        }
        return 0;
    }

    private static decimal ParseDecimal(string? value)
    {
        var text = value?.Replace("%", string.Empty).Trim();
        if (string.IsNullOrEmpty(text)) return 0m;
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var current)) return current;
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.GetCultureInfo("id-ID"), out var indonesian)) return indonesian;
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var invariant)) return invariant;
        return 0m;
    }

    private static void ShowError(Exception exception)
    {
        var message = exception is HttpRequestException
            ? "Tidak dapat terhubung ke AKSARA Server. Pastikan server berjalan dan alamat koneksi benar."
            : exception.Message;
        MessageBox.Show(message, "AKSARA - Penjualan", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void Unavailable_Click(object sender, RoutedEventArgs e) => MessageBox.Show("Fitur tambahan ini akan diaktifkan setelah alur Penjualan dan Persediaan tervalidasi.", "AKSARA", MessageBoxButton.OK, MessageBoxImage.Information);
}

public sealed class SalesLineRow : INotifyPropertyChanged
{
    private string _warehouseCode = string.Empty;
    private string _itemCode = string.Empty;
    private string _itemName = string.Empty;
    private decimal _quantity;
    private string _unit = string.Empty;
    private decimal _availableStock;
    private decimal _unitPrice;
    private decimal _discountPercent;
    private decimal _fifoUnitCost;
    private decimal _packageQuantity;
    private string _lineNote = string.Empty;

    public string WarehouseCode { get => _warehouseCode; set => Set(ref _warehouseCode, value); }
    public string ItemCode { get => _itemCode; set => Set(ref _itemCode, value); }
    public string ItemName { get => _itemName; set => Set(ref _itemName, value); }
    public decimal Quantity { get => _quantity; set => Set(ref _quantity, value); }
    public string Unit { get => _unit; set => Set(ref _unit, value); }
    public decimal AvailableStock { get => _availableStock; set => Set(ref _availableStock, value); }
    public decimal UnitPrice { get => _unitPrice; set => Set(ref _unitPrice, value); }
    public decimal DiscountPercent { get => _discountPercent; set => Set(ref _discountPercent, value); }
    public decimal FifoUnitCost { get => _fifoUnitCost; set => Set(ref _fifoUnitCost, value); }
    public decimal PackageQuantity { get => _packageQuantity; set => Set(ref _packageQuantity, value); }
    public string LineNote { get => _lineNote; set => Set(ref _lineNote, value); }
    public decimal Total => Math.Round(Quantity * UnitPrice * (1m - DiscountPercent / 100m), 2);
    public decimal CostAmount => Math.Round(Quantity * FifoUnitCost, 5);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Total)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CostAmount)));
    }
}
