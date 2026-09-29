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

public partial class PurchaseView : UserControl, IWorkspaceCloseGuard
{
    private string LocalDraftKey => _serviceMode ? "service-purchase" : "purchase";
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly bool _serviceMode;
    private PurchaseSupplierDto? _selectedSupplier;
    private PurchaseDto? _currentPurchase;
    private bool _busy;
    private bool _uiReady;
    private string _defaultWarehouse = "UTAMA";
    private IReadOnlyList<PurchaseDto> _purchaseHistory = Array.Empty<PurchaseDto>();
    private int _purchaseTotalCount;
    private int _historyIndex = -1;
    private bool _documentNumberManuallyEdited;
    private bool _suppressDocumentNumberChange;
    private bool _formattingMoneyText;
    private string? _suggestedDocumentNumber;
    private bool _draftChecked;

    public DateTime? DocumentDate { get; set; } = DateTime.Today;
    public DateTime? DueDate { get; set; } = DateTime.Today.AddDays(30);
    public ObservableCollection<PurchaseLineRow> Lines { get; } = [];
    public event EventHandler? CloseRequested;

    public PurchaseView(bool serviceMode = false)
    {
        _serviceMode = serviceMode;
        InitializeComponent();
        if (_serviceMode)
        {
            LinesGrid.Columns[1].Header = "Kode Jasa";
            LinesGrid.Columns[2].Header = "Nama Jasa";
            LinesGrid.Columns[3].Visibility = Visibility.Collapsed;
            LineNotesGrid.Columns[0].Header = "Kode Jasa";
            LineNotesGrid.Columns[1].Header = "Nama Jasa";
        }
        _uiReady = true;
        DataContext = this;
        Lines.CollectionChanged += Lines_CollectionChanged;
        DocumentDatePicker.SelectedDateChanged += async (_, _) =>
        {
            if (_currentPurchase is null && !_documentNumberManuallyEdited)
                await RefreshNewDocumentNumberAsync();
        };
        ResetForm();
        Loaded += async (_, _) =>
        {
            SetReadOnly(false);
            UpdateTotals();
            await RefreshServerStatusAsync();
            await LoadDefaultWarehouseAsync();
            await LoadPurchaseHistoryAsync();
            await RefreshNewDocumentNumberAsync();
            RestoreLocalDraft();
        };
    }

    private void Lines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null) foreach (PurchaseLineRow line in e.NewItems) line.PropertyChanged += (_, _) => UpdateTotals();
        UpdateTotals();
    }

    private async Task RefreshServerStatusAsync()
    {
        var ready = await _api.IsDatabaseReadyAsync();
        ServerStatusText.Text = ready ? "TOTAL DATA 0" : "SERVER BELUM SIAP";
        ServerStatusText.Foreground = ready ? Brushes.Black : new SolidColorBrush(Color.FromRgb(180, 35, 24));
        HelpText.Text = ready
            ? $"Tekan F4 pada keyboard / DOUBLE CLICK tombol kiri mouse pada kolom ini untuk membuka form cari {(_serviceMode ? "JASA" : "BARANG")}"
            : $"ⓘ Server/database belum terhubung ({_api.ServerAddress}). Form dapat dilihat tetapi belum dapat menyimpan.";
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
        _currentPurchase = null;
        _selectedSupplier = null;
        DocumentDate = DateTime.Today;
        DueDate = DateTime.Today.AddDays(30);
        if (IsLoaded)
        {
            SupplierCodeBox.Clear(); SupplierNameBox.Clear(); SupplierAddressBox.Clear();
            _documentNumberManuallyEdited = false;
            _suggestedDocumentNumber = null;
            SetDocumentNumber(string.Empty);
            DueDaysBox.Text = "30";
            DocumentDatePicker.SelectedDate = DocumentDate; DueDatePicker.SelectedDate = DueDate;
            NotesBox.Clear(); DiscountPercentBox.Text = "0"; DiscountAmountBox.Text = "0";
            TaxPercentBox.Text = (ProgramConfigurationCache.Current.TaxEnabled ? ProgramConfigurationCache.Current.DefaultTaxPercent : 0).ToString("0.####", CultureInfo.CurrentCulture);
            DownPaymentBox.Text = "0";
            TransactionType.SelectedIndex = 1;
        }
        Lines.Clear();
        for (var i = 0; i < Math.Clamp(ProgramConfigurationCache.Current.TransactionDetailRows, 1, 200); i++) Lines.Add(new());
        SetReadOnly(false);
        if (ModeStatusText is not null) ModeStatusText.Text = "TAMBAH DATA";
        _historyIndex = -1;
        UpdateTotals();
        UpdatePurchaseCounter();
        _ = RefreshNewDocumentNumberAsync();
    }

    private void SetReadOnly(bool readOnly)
    {
        if (!IsLoaded) return;
        SupplierCodeBox.IsReadOnly = readOnly; DocumentNumberBox.IsReadOnly = readOnly; NotesBox.IsReadOnly = readOnly;
        DiscountPercentBox.IsReadOnly = readOnly; DiscountAmountBox.IsReadOnly = readOnly; TaxPercentBox.IsReadOnly = readOnly; DownPaymentBox.IsReadOnly = readOnly;
        TransactionType.IsEnabled = !readOnly; LinesGrid.IsReadOnly = readOnly; LineNotesGrid.IsReadOnly = readOnly; SaveButton.IsEnabled = !readOnly; SavePrintButton.IsEnabled = true;
        DeleteButton.IsEnabled = readOnly && _currentPurchase?.Status == "POSTED";
        RestoreButton.IsEnabled = readOnly && _currentPurchase?.Status == "CANCELLED";
    }

    private async void SupplierCodeBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.F4 or Key.Enter)) return;
        e.Handled = true;
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(SupplierCodeBox.Text) && await TryResolveSupplierAsync(SupplierCodeBox.Text)) return;
        OpenSupplierLookup();
    }

    private void SupplierCodeBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSupplierLookup();

    private void DocumentNumberBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressDocumentNumberChange || _currentPurchase is not null) return;
        _documentNumberManuallyEdited = !string.Equals(DocumentNumberBox.Text.Trim(), _suggestedDocumentNumber, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshNewDocumentNumberAsync()
    {
        if (!IsLoaded || _currentPurchase is not null || _documentNumberManuallyEdited) return;
        try
        {
            var date = DateOnly.FromDateTime(DocumentDatePicker.SelectedDate ?? DateTime.Today);
            SetDocumentNumber(await _api.GetNextPurchaseNumberAsync(date));
        }
        catch (Exception exception)
        {
            HelpText.Text = $"Nomor faktur berikutnya belum dapat dimuat: {exception.Message}";
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

    private void OpenSupplierLookup()
    {
        var dialog = new SupplierLookupWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.SelectedSupplier is not null) ApplySupplier(dialog.SelectedSupplier);
    }

    private async Task<bool> TryResolveSupplierAsync(string code)
    {
        try
        {
            var matches = await _api.SearchSuppliersAsync(code);
            var item = matches.FirstOrDefault(x => string.Equals(x.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));
            if (item is null) return false;
            ApplySupplier(item);
            return true;
        }
        catch (Exception exception) { ShowError(exception); return false; }
    }

    private void ApplySupplier(PurchaseSupplierDto supplier)
    {
        _selectedSupplier = supplier;
        SupplierCodeBox.Text = supplier.Code; SupplierNameBox.Text = supplier.CompanyName; SupplierAddressBox.Text = supplier.Address ?? string.Empty;
        DueDaysBox.Text = supplier.CreditDays.ToString(CultureInfo.CurrentCulture);
        DueDatePicker.SelectedDate = (DocumentDatePicker.SelectedDate ?? DateTime.Today).AddDays(supplier.CreditDays);
    }

    private void LinesGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F4) return;
        e.Handled = true;
        var line = CurrentOrEmptyLine();
        if (LinesGrid.CurrentColumn?.DisplayIndex == 0) OpenWarehouseLookup(line); else OpenItemLookup(line);
    }

    private void LinesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LinesGrid.CurrentColumn?.DisplayIndex == 0) OpenWarehouseLookup(CurrentOrEmptyLine());
        else if (LinesGrid.CurrentColumn?.DisplayIndex == 1) OpenItemLookup(CurrentOrEmptyLine());
    }

    private PurchaseLineRow CurrentOrEmptyLine() => LinesGrid.SelectedItem as PurchaseLineRow ?? Lines.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.ItemCode)) ?? AddEmptyLine();
    private PurchaseLineRow AddEmptyLine() { var line = new PurchaseLineRow(); Lines.Add(line); return line; }

    private void OpenItemLookup(PurchaseLineRow line)
    {
        var dialog = new PurchaseItemLookupWindow(NormalizeWarehouse(line.WarehouseCode), _serviceMode) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.SelectedItem is not null) ApplyItem(line, dialog.SelectedItem);
    }

    private void OpenWarehouseLookup(PurchaseLineRow line)
    {
        var dialog = new WarehouseLookupWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.SelectedWarehouse is null) return;
        if (!string.Equals(line.WarehouseCode, dialog.SelectedWarehouse.Code, StringComparison.OrdinalIgnoreCase))
        {
            line.WarehouseCode = dialog.SelectedWarehouse.Code;
            line.ItemCode = string.Empty; line.ItemName = string.Empty; line.Unit = string.Empty; line.AvailableStock = 0;
        }
        LinesGrid.SelectedItem = line;
    }

    private void ApplyItem(PurchaseLineRow line, PurchaseItemDto item)
    {
        line.ItemCode = item.Code; line.ItemName = item.Name; line.Unit = item.Unit; line.WarehouseCode = item.WarehouseCode;
        line.AvailableStock = item.AvailableStock; line.UnitPrice = item.PurchasePrice;
        if (line.Quantity <= 0) line.Quantity = 1;
        LinesGrid.SelectedItem = line;
    }

    private void Lines_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e) => Dispatcher.BeginInvoke(async () =>
    {
        if (e.Row.Item is PurchaseLineRow line && e.Column.DisplayIndex == 1 && !string.IsNullOrWhiteSpace(line.ItemCode)) await TryResolveItemAsync(line);
        UpdateTotals();
    });

    private async Task<bool> TryResolveItemAsync(PurchaseLineRow line)
    {
        try
        {
            var matches = await _api.SearchPurchaseItemsAsync(line.ItemCode, NormalizeWarehouse(line.WarehouseCode), itemType: _serviceMode ? "SERVICE" : "GOODS");
            var item = matches.FirstOrDefault(x => string.Equals(x.Code, line.ItemCode.Trim(), StringComparison.OrdinalIgnoreCase));
            if (item is null) return false;
            ApplyItem(line, item); return true;
        }
        catch (Exception exception) { ShowError(exception); return false; }
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SavePurchaseAsync();
    private async void SavePrint_Click(object sender, RoutedEventArgs e) => await SaveAndPrintPurchaseAsync();

    private async Task<PurchaseDto?> SavePurchaseAsync(bool showMessage = true)
    {
        if (_busy) return null;
        if (_currentPurchase is not null) return _currentPurchase;
        LinesGrid.CommitEdit(DataGridEditingUnit.Cell, true); LinesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        try
        {
            SetBusy(true, _serviceMode ? "MENYIMPAN PEMBELIAN JASA..." : "MENYIMPAN & MENAMBAH STOK...");
            if (!await _api.IsDatabaseReadyAsync()) throw new AksaraApiException($"Server atau database belum siap di {_api.ServerAddress}.");
            if (_selectedSupplier is null || !string.Equals(_selectedSupplier.Code, SupplierCodeBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                if (!await TryResolveSupplierAsync(SupplierCodeBox.Text)) throw new InvalidOperationException("Supplier belum dipilih atau tidak ditemukan.");

            var active = Lines.Where(x => !string.IsNullOrWhiteSpace(x.ItemCode) || x.Quantity != 0 || x.UnitPrice != 0).ToArray();
            if (active.Length == 0) throw new InvalidOperationException($"Minimal satu {(_serviceMode ? "jasa" : "barang")} harus diisi.");
            foreach (var line in active)
            {
                if (string.IsNullOrWhiteSpace(line.ItemName) && !await TryResolveItemAsync(line)) throw new InvalidOperationException($"Barang {line.ItemCode} tidak ditemukan.");
                if (line.Quantity <= 0) throw new InvalidOperationException($"Jumlah {line.ItemCode} harus lebih dari nol.");
            }
            var request = new CreatePurchaseRequest(
                DateOnly.FromDateTime(DocumentDatePicker.SelectedDate ?? DateTime.Today), NullIfBlank(DocumentNumberBox.Text), _selectedSupplier!.Code, null,
                TransactionType.SelectedIndex == 0 ? "CASH" : "CREDIT",
                TransactionType.SelectedIndex == 0 ? null : DateOnly.FromDateTime(DueDatePicker.SelectedDate ?? DateTime.Today),
                NormalizeHeaderWarehouse(active), ParseDecimal(DiscountPercentBox.Text), ParseDecimal(DiscountAmountBox.Text), ParseDecimal(TaxPercentBox.Text),
                TransactionType.SelectedIndex == 0 ? 0 : ParseDecimal(DownPaymentBox.Text), NullIfBlank(NotesBox.Text), AksaraApiClient.Shared.CurrentUserName, Environment.MachineName,
                active.Select(x => new CreatePurchaseLineRequest(x.ItemCode, x.Quantity, x.UnitPrice, x.DiscountPercent, NormalizeWarehouse(x.WarehouseCode), NullIfBlank(x.LineNote))).ToArray(),
                _serviceMode ? "SERVICE" : "GOODS");
            var saved = await _api.CreatePurchaseAsync(request);
            LocalTransactionDraftStore.Delete(LocalDraftKey);
            await LoadPurchaseAsync(saved);
            await LoadPurchaseHistoryAsync();
            if (showMessage)
                MessageBox.Show(_serviceMode
                    ? $"Faktur jasa {saved.DocumentNumber} berhasil disimpan.\nBeban jasa dan kas/utang telah diposting dalam transaksi yang sama."
                    : $"Faktur {saved.DocumentNumber} berhasil disimpan.\nPersediaan bertambah, harga beli dan rata-rata stok diperbarui dalam transaksi yang sama.", "AKSARA", MessageBoxButton.OK, MessageBoxImage.Information);
            return saved;
        }
        catch (Exception exception) { ShowError(exception); }
        finally { SetBusy(false); }
        return null;
    }

    private async Task SaveAndPrintPurchaseAsync()
    {
        var purchase = _currentPurchase ?? await SavePurchaseAsync(showMessage: false);
        if (purchase is null) return;
        await LoadPurchaseHistoryAsync();

        var dialog = new TransactionPrintOptionsWindow(
            _serviceMode ? "Cetak Faktur Pembelian Jasa" : "Cetak Faktur Pembelian",
            "No. Faktur",
            purchase.DocumentNumber,
            [
                new("purchase-tax-simple", "Faktur Pajak Sederhana"),
                new("purchase-tax-standard", "Faktur Pajak Standar"),
                new("purchase-invoice", "Faktur Pembelian"),
                new("purchase-delivery", "Surat Jalan")
            ],
            _purchaseHistory.Select(x => new TransactionPrintHistoryEntry(x.DocumentDate, x.DocumentNumber, x.SupplierName, x.GrandTotal, x)).ToArray())
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true) return;

        var selected = await ResolvePurchaseForPrintAsync(dialog.SelectedNumber, purchase);
        if (selected is null) return;
        var document = BuildPurchaseDocument(selected, dialog.SelectedOptionId);
        RunPrintAction(dialog.SelectedAction, document);
    }

    private async Task<PurchaseDto?> ResolvePurchaseForPrintAsync(string number, PurchaseDto fallback)
    {
        if (string.IsNullOrWhiteSpace(number) || string.Equals(number, fallback.DocumentNumber, StringComparison.OrdinalIgnoreCase)) return fallback;
        var selected = _purchaseHistory.FirstOrDefault(x => string.Equals(x.DocumentNumber, number, StringComparison.OrdinalIgnoreCase));
        if (selected is not null) return selected;
        try
        {
            return (await _api.SearchPurchasesAsync(number, documentKind: _serviceMode ? "SERVICE" : "GOODS")).FirstOrDefault(x => string.Equals(x.DocumentNumber, number, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            ShowError(exception);
            return null;
        }
    }

    private static PrintableTransaction BuildPurchaseDocument(PurchaseDto purchase, string optionId)
    {
        var kind = purchase.DocumentKind == "SERVICE" ? " Jasa" : string.Empty;
        var title = optionId switch
        {
            "purchase-tax-simple" => $"Faktur Pajak Sederhana Pembelian{kind}",
            "purchase-tax-standard" => $"Faktur Pajak Standar Pembelian{kind}",
            "purchase-delivery" => $"Surat Jalan Pembelian{kind}",
            _ => $"Faktur Pembelian{kind}"
        };
        return new PrintableTransaction(
            title,
            "No. Faktur",
            purchase.DocumentNumber,
            purchase.DocumentDate,
            "Supplier",
            purchase.SupplierCode,
            purchase.SupplierName,
            purchase.SupplierAddress,
            purchase.TransactionType,
            purchase.WarehouseCode,
            null,
            purchase.DueDate,
            purchase.Subtotal,
            purchase.DiscountAmount,
            purchase.TaxAmount,
            purchase.GrandTotal,
            purchase.Notes,
            purchase.Lines.Select((line, index) => new PrintableTransactionLine(index + 1, line.ItemCode, line.ItemName, line.Quantity, line.Unit, line.UnitPrice, line.DiscountPercent, line.LineTotal)).ToArray());
    }

    private static void RunPrintAction(TransactionPrintAction action, PrintableTransaction document)
    {
        if (action == TransactionPrintAction.Preview) TransactionDocumentOutput.Preview(document);
        else if (action == TransactionPrintAction.ExportExcel) TransactionDocumentOutput.ExportExcel(document);
        else TransactionDocumentOutput.Print(document);
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PurchaseLookupWindow(_serviceMode) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.SelectedPurchase is not null)
        {
            await LoadPurchaseAsync(dialog.SelectedPurchase);
            await LoadPurchaseHistoryAsync();
        }
    }

    private async Task LoadPurchaseAsync(PurchaseDto item)
    {
        _currentPurchase = item;
        DocumentDatePicker.SelectedDate = item.DocumentDate.ToDateTime(TimeOnly.MinValue); DueDatePicker.SelectedDate = item.DueDate?.ToDateTime(TimeOnly.MinValue);
        _documentNumberManuallyEdited = false;
        SetDocumentNumber(item.DocumentNumber); SupplierCodeBox.Text = item.SupplierCode; SupplierNameBox.Text = item.SupplierName; SupplierAddressBox.Text = item.SupplierAddress ?? string.Empty;
        TransactionType.SelectedIndex = item.TransactionType == "CASH" ? 0 : 1;
        DiscountPercentBox.Text = item.DiscountPercent.ToString(CultureInfo.CurrentCulture); DiscountAmountBox.Text = FormatMoney(item.DiscountAmount);
        TaxPercentBox.Text = item.TaxPercent.ToString(CultureInfo.CurrentCulture); DownPaymentBox.Text = FormatMoney(item.DownPayment); NotesBox.Text = item.Notes ?? string.Empty;
        Lines.Clear();
        var current = new List<PurchaseItemDto>();
        foreach (var warehouse in item.Lines.Select(x => x.WarehouseCode).Distinct(StringComparer.OrdinalIgnoreCase))
            current.AddRange(await _api.SearchPurchaseItemsAsync(string.Empty, warehouse, itemType: _serviceMode ? "SERVICE" : "GOODS"));
        foreach (var line in item.Lines)
        {
            var stock = current.FirstOrDefault(x => string.Equals(x.Code, line.ItemCode, StringComparison.OrdinalIgnoreCase) && string.Equals(x.WarehouseCode, line.WarehouseCode, StringComparison.OrdinalIgnoreCase));
            Lines.Add(new() { WarehouseCode = line.WarehouseCode, ItemCode = line.ItemCode, ItemName = line.ItemName, Quantity = line.Quantity, Unit = line.Unit,
                AvailableStock = stock?.AvailableStock ?? 0, UnitPrice = line.UnitPrice, DiscountPercent = line.DiscountPercent, LineNote = line.LineNote ?? string.Empty });
        }
        while (Lines.Count < 10) Lines.Add(new());
        ModeStatusText.Text = item.Status == "CANCELLED" ? "DITANDAI HAPUS" : "EDIT DATA";
        SyncHistoryIndex();
        UpdatePurchaseCounter();
        SetReadOnly(true); UpdateTotals();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPurchase is null || _currentPurchase.Status != "POSTED") return;
        if (MessageBox.Show("Apakah anda ingin mendandai hapus data ini?", "AKSARA", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            SetBusy(true, "MENANDAI HAPUS DATA...");
            var cancelled = await _api.CancelPurchaseAsync(_currentPurchase.Id, new("Ditandai hapus dari form pembelian.", AksaraApiClient.Shared.CurrentUserName, Environment.MachineName));
            await LoadPurchaseAsync(cancelled);
            await LoadPurchaseHistoryAsync();
        }
        catch (Exception exception) { ShowError(exception); }
        finally { SetBusy(false); }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPurchase is null || _currentPurchase.Status != "CANCELLED") return;
        if (MessageBox.Show("Apakah anda ingin membatalkan hapus data?", "AKSARA", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            SetBusy(true, "MEMBATALKAN TANDA HAPUS...");
            var restored = await _api.RestorePurchaseAsync(_currentPurchase.Id, new(AksaraApiClient.Shared.CurrentUserName, Environment.MachineName));
            await LoadPurchaseAsync(restored);
            await LoadPurchaseHistoryAsync();
        }
        catch (Exception exception) { ShowError(exception); }
        finally { SetBusy(false); }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPurchase is null && !ConfirmPendingDraft()) return;
        ResetForm();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPurchase is null && !ConfirmPendingDraft()) return;
        ResetForm();
    }
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    public Task<bool> ConfirmCloseAsync() => Task.FromResult(_currentPurchase is not null || ConfirmPendingDraft());

    private bool ConfirmPendingDraft()
    {
        if (!HasMeaningfulDraftInput()) return true;

        var choice = LocalDraftPromptWindow.Show(Window.GetWindow(this), "Pembelian");
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
        return !string.IsNullOrWhiteSpace(SupplierCodeBox.Text)
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
            .Select(line => new PurchaseLocalDraftLine(
                line.WarehouseCode, line.ItemCode, line.ItemName, line.Quantity, line.Unit,
                line.AvailableStock, line.UnitPrice, line.DiscountPercent, line.PackageQuantity, line.LineNote))
            .ToArray();
        LocalTransactionDraftStore.Save(LocalDraftKey, new PurchaseLocalDraft(
            DocumentDatePicker.SelectedDate,
            DocumentNumberBox.Text,
            SupplierCodeBox.Text,
            SupplierNameBox.Text,
            SupplierAddressBox.Text,
            TransactionType.SelectedIndex,
            DueDatePicker.SelectedDate,
            DueDaysBox.Text,
            NotesBox.Text,
            DiscountPercentBox.Text,
            DiscountAmountBox.Text,
            TaxPercentBox.Text,
            DownPaymentBox.Text,
            activeLines));
        HelpText.Text = "Draft Pembelian tersimpan lokal dan akan dipulihkan saat form dibuka kembali.";
    }

    private void RestoreLocalDraft()
    {
        if (_draftChecked) return;
        _draftChecked = true;
        if (!LocalTransactionDraftStore.TryLoad<PurchaseLocalDraft>(LocalDraftKey, out var draft) || draft is null) return;

        _currentPurchase = null;
        _selectedSupplier = null;
        _documentNumberManuallyEdited = true;
        DocumentDatePicker.SelectedDate = draft.DocumentDate ?? DateTime.Today;
        SetDocumentNumber(draft.DocumentNumber);
        SupplierCodeBox.Text = draft.SupplierCode;
        SupplierNameBox.Text = draft.SupplierName;
        SupplierAddressBox.Text = draft.SupplierAddress;
        TransactionType.SelectedIndex = Math.Clamp(draft.TransactionTypeIndex, 0, 1);
        DueDatePicker.SelectedDate = draft.DueDate ?? DateTime.Today.AddDays(30);
        DueDaysBox.Text = draft.DueDays;
        NotesBox.Text = draft.Notes;
        DiscountPercentBox.Text = draft.DiscountPercent;
        DiscountAmountBox.Text = draft.DiscountAmount;
        TaxPercentBox.Text = draft.TaxPercent;
        DownPaymentBox.Text = draft.DownPayment;
        Lines.Clear();
        foreach (var line in draft.Lines)
        {
            Lines.Add(new PurchaseLineRow
            {
                WarehouseCode = line.WarehouseCode,
                ItemCode = line.ItemCode,
                ItemName = line.ItemName,
                Quantity = line.Quantity,
                Unit = line.Unit,
                AvailableStock = line.AvailableStock,
                UnitPrice = line.UnitPrice,
                DiscountPercent = line.DiscountPercent,
                PackageQuantity = line.PackageQuantity,
                LineNote = line.LineNote
            });
        }
        AddBlankRows();
        SetReadOnly(false);
        ModeStatusText.Text = "DRAFT LOKAL";
        HelpText.Text = "Draft Pembelian dipulihkan. Tekan Simpan untuk menjadikannya transaksi.";
        UpdateTotals();
    }

    private void AddBlankRows()
    {
        var minimum = Math.Clamp(ProgramConfigurationCache.Current.TransactionDetailRows, 1, 200);
        while (Lines.Count < minimum) Lines.Add(new PurchaseLineRow());
    }

    private void CommitPendingGridEdit()
    {
        LinesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        LinesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        LineNotesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        LineNotesGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private async void NavigateFirst_Click(object sender, RoutedEventArgs e) => await LoadPurchaseHistoryAtAsync(_purchaseHistory.Count - 1);
    private async void NavigatePrevious_Click(object sender, RoutedEventArgs e) => await MovePurchaseHistoryAsync(1);
    private void NavigateLookup_Click(object sender, RoutedEventArgs e) => Search_Click(sender, e);
    private async void NavigateNext_Click(object sender, RoutedEventArgs e) => await MovePurchaseHistoryAsync(-1);
    private async void NavigateLast_Click(object sender, RoutedEventArgs e) => await LoadPurchaseHistoryAtAsync(0);

    private async Task LoadPurchaseHistoryAsync()
    {
        try
        {
            _purchaseHistory = await _api.SearchPurchasesAsync(null, documentKind: _serviceMode ? "SERVICE" : "GOODS");
            _purchaseTotalCount = await _api.CountPurchasesAsync(null, documentKind: _serviceMode ? "SERVICE" : "GOODS");
            SyncHistoryIndex();
            UpdatePurchaseCounter();
        }
        catch (Exception exception)
        {
            HelpText.Text = $"Daftar pembelian belum dapat dimuat: {exception.Message}";
        }
    }

    private async Task MovePurchaseHistoryAsync(int offset)
    {
        if (_purchaseHistory.Count == 0) await LoadPurchaseHistoryAsync();
        if (_purchaseHistory.Count == 0)
        {
            HelpText.Text = "Belum ada data pembelian tersimpan.";
            return;
        }

        var nextIndex = _historyIndex < 0
            ? offset > 0 ? _purchaseHistory.Count - 1 : 0
            : _historyIndex + offset;
        await LoadPurchaseHistoryAtAsync(nextIndex);
    }

    private async Task LoadPurchaseHistoryAtAsync(int index)
    {
        if (_purchaseHistory.Count == 0) await LoadPurchaseHistoryAsync();
        if (_purchaseHistory.Count == 0)
        {
            HelpText.Text = "Belum ada data pembelian tersimpan.";
            return;
        }

        index = Math.Clamp(index, 0, _purchaseHistory.Count - 1);
        _historyIndex = index;
        await LoadPurchaseAsync(_purchaseHistory[index]);
        HelpText.Text = $"Data {_historyIndex + 1:N0}/{_purchaseHistory.Count:N0}: {_purchaseHistory[index].DocumentNumber} | {_purchaseHistory[index].DocumentDate:dd/MM/yyyy}.";
    }

    private void SyncHistoryIndex()
    {
        _historyIndex = _currentPurchase is null
            ? -1
            : _purchaseHistory.ToList().FindIndex(x => x.Id == _currentPurchase.Id);
    }

    private void UpdatePurchaseCounter()
    {
        if (ServerStatusText is null) return;
        var total = _purchaseTotalCount > 0 ? _purchaseTotalCount : _purchaseHistory.Count;
        ServerStatusText.Text = _currentPurchase is null || _historyIndex < 0
            ? $"TOTAL DATA {total:N0}"
            : $"DATA {_historyIndex + 1:N0} / {total:N0}";
    }

    private void DueDaysBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(DueDaysBox.Text, out var days)) DueDatePicker.SelectedDate = (DocumentDatePicker.SelectedDate ?? DateTime.Today).AddDays(Math.Max(0, days));
    }

    private void TransactionType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TransactionBanner is null || TransactionType is null) return;
        var cash = TransactionType.SelectedIndex == 0;
        TransactionBanner.Text = cash ? "TUNAI" : "KREDIT";
        TransactionBanner.Foreground = Brushes.Red;
        if (DownPaymentBox is not null) DownPaymentBox.IsEnabled = !cash;
        UpdateTotals();
    }

    private void DetailTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DetailTabs is null || LinesGrid is null || LineNotesGrid is null) return;
        LinesGrid.Visibility = DetailTabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        LineNotesGrid.Visibility = DetailTabs.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TotalsInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        FormatMoneyInput(sender as TextBox);
        UpdateTotals();
    }
    private void UpdateTotals()
    {
        if (!_uiReady) return;
        var subtotal = Lines.Sum(x => x.Total);
        var discount = Math.Min(subtotal, Math.Round(subtotal * ParseDecimal(DiscountPercentBox?.Text) / 100m, 2) + ParseDecimal(DiscountAmountBox?.Text));
        var net = subtotal - discount; var total = net + Math.Round(net * ParseDecimal(TaxPercentBox?.Text) / 100m, 2);
        var balance = TransactionType?.SelectedIndex == 0 ? 0 : Math.Max(0, total - ParseDecimal(DownPaymentBox?.Text));
        DiscountTotalText.Text = discount.ToString("N0");
        NetBeforeTaxText.Text = net.ToString("N0");
        TotalText.Text = total.ToString("N0");
        BalanceText.Text = balance.ToString("N0");
    }

    private void SetBusy(bool busy, string? status = null) { _busy = busy; if (!string.IsNullOrWhiteSpace(status)) ModeStatusText.Text = status; IsEnabled = !busy; if (!busy && _currentPurchase is null) ModeStatusText.Text = "TAMBAH DATA"; }
    private string NormalizeWarehouse(string? value) => string.IsNullOrWhiteSpace(value) ? _defaultWarehouse : value.Trim().ToUpperInvariant();
    private string NormalizeHeaderWarehouse(IReadOnlyList<PurchaseLineRow> lines) => lines.Count == 0 ? _defaultWarehouse : NormalizeWarehouse(lines[0].WarehouseCode);
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

    internal static decimal ParseDecimal(string? value)
    {
        var text = value?.Replace("%", string.Empty).Trim(); if (string.IsNullOrEmpty(text)) return 0;
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var current)) return current;
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.GetCultureInfo("id-ID"), out var id)) return id;
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var invariant) ? invariant : 0;
    }
    private static void ShowError(Exception exception) => MessageBox.Show(exception is HttpRequestException ? "Tidak dapat terhubung ke AKSARA Server." : exception.Message, "AKSARA - Pembelian", MessageBoxButton.OK, MessageBoxImage.Warning);
}

public sealed class PurchaseLineRow : INotifyPropertyChanged
{
    private string _warehouseCode = string.Empty, _itemCode = "", _itemName = "", _unit = "";
    private string _lineNote = string.Empty;
    private decimal _quantity, _availableStock, _unitPrice, _discountPercent, _packageQuantity;
    public string WarehouseCode { get => _warehouseCode; set => Set(ref _warehouseCode, value); }
    public string ItemCode { get => _itemCode; set => Set(ref _itemCode, value); }
    public string ItemName { get => _itemName; set => Set(ref _itemName, value); }
    public decimal Quantity { get => _quantity; set => Set(ref _quantity, value); }
    public string Unit { get => _unit; set => Set(ref _unit, value); }
    public decimal AvailableStock { get => _availableStock; set => Set(ref _availableStock, value); }
    public decimal UnitPrice { get => _unitPrice; set => Set(ref _unitPrice, value); }
    public decimal DiscountPercent { get => _discountPercent; set => Set(ref _discountPercent, value); }
    public decimal PackageQuantity { get => _packageQuantity; set => Set(ref _packageQuantity, value); }
    public string LineNote { get => _lineNote; set => Set(ref _lineNote, value); }
    public decimal Total => Math.Round(Quantity * UnitPrice * (1m - DiscountPercent / 100m), 2);
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; PropertyChanged?.Invoke(this, new(name)); PropertyChanged?.Invoke(this, new(nameof(Total))); }
}
