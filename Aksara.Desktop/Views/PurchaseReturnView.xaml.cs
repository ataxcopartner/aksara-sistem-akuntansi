using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Aksara.Contracts;
using Aksara.Desktop.Controls;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

public partial class PurchaseReturnView : UserControl, IWorkspaceCloseGuard
{
    private const string LocalDraftKey = "purchase-return";
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private PurchaseReturnSourceDto? _source;
    private PurchaseReturnDto? _currentReturn;
    private bool _busy;
    private bool _returnNumberManuallyEdited;
    private bool _suppressReturnNumberChange;
    private string? _suggestedReturnNumber;
    private bool _draftChecked;
    public DateTime? ReturnDate { get; set; } = DateTime.Today;
    public ObservableCollection<PurchaseReturnRow> Lines { get; } = [];
    public event EventHandler? CloseRequested;

    public PurchaseReturnView()
    {
        InitializeComponent(); DataContext = this; ResetForm();
        ReturnDatePicker.SelectedDateChanged += (_, _) =>
        {
            if (_currentReturn is null && !_returnNumberManuallyEdited) _ = RefreshNewReturnNumberAsync();
        };
        Lines.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (PurchaseReturnRow line in e.NewItems) line.PropertyChanged += (_, _) =>
                {
                    UpdateTotals();
                    UpdateCommandStates();
                };
            UpdateTotals();
            UpdateCommandStates();
        };
        Loaded += (_, _) =>
        {
            ResetForm();
            RestoreLocalDraft();
            SourceNumberBox.Focus();
        };
    }

    private void ResetForm()
    {
        _source = null; _currentReturn = null; Lines.Clear();
        if (IsLoaded)
        {
            ReturnDatePicker.SelectedDate = DateTime.Today; _returnNumberManuallyEdited = false; _suggestedReturnNumber = null; SetReturnNumber(string.Empty); _ = RefreshNewReturnNumberAsync(); SourceNumberBox.Clear(); SupplierCodeBox.Clear(); SupplierNameBox.Clear(); AddressBox.Clear();
            TransactionTypeBox.Clear(); WarehouseBox.Clear(); NotesBox.Clear(); DiscountPercentBox.Text = "0"; DiscountAmountBox.Text = "0"; TaxPercentBox.Text = "0";
        }
        SetReadOnly(false); if (ModeStatusText is not null) ModeStatusText.Text = "TRANSAKSI BARU"; UpdateTotals(); UpdateCommandStates();
    }

    private void SetReadOnly(bool readOnly)
    {
        if (!IsLoaded) return;
        ReturnDatePicker.IsEnabled = !readOnly; ReturnNumberBox.IsReadOnly = readOnly; SourceNumberBox.IsReadOnly = readOnly; NotesBox.IsReadOnly = readOnly; DiscountPercentBox.IsReadOnly = readOnly; DiscountAmountBox.IsReadOnly = readOnly; TaxPercentBox.IsReadOnly = readOnly;
        LinesGrid.IsReadOnly = readOnly; SaveButton.IsEnabled = !readOnly; SavePrintButton.IsEnabled = !_busy; DeleteButton.IsEnabled = readOnly && _currentReturn?.Status == "POSTED"; RestoreButton.IsEnabled = readOnly && _currentReturn?.Status == "CANCELLED"; UpdateCommandStates();
    }

    private void ReturnNumberBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressReturnNumberChange) return;
        _returnNumberManuallyEdited = !string.Equals(ReturnNumberBox.Text.Trim(), _suggestedReturnNumber, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshNewReturnNumberAsync()
    {
        if (!IsLoaded || _currentReturn is not null) return;
        try
        {
            var date = DateOnly.FromDateTime(ReturnDatePicker.SelectedDate ?? DateTime.Today);
            var number = await _api.GetNextPurchaseReturnNumberAsync(date);
            _suggestedReturnNumber = number;
            if (!_returnNumberManuallyEdited) SetReturnNumber(number);
        }
        catch
        {
            if (!_returnNumberManuallyEdited) SetReturnNumber(string.Empty);
        }
    }

    private void SetReturnNumber(string value)
    {
        _suppressReturnNumberChange = true;
        try
        {
            ReturnNumberBox.Text = value;
        }
        finally
        {
            _suppressReturnNumberChange = false;
        }
    }

    private async void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F4) return;
        e.Handled = true;
        if (_currentReturn is not null)
        {
            await OpenReturnLookupAsync();
            return;
        }

        if (_source is null || Keyboard.FocusedElement == SourceNumberBox)
            await GuardedAsync(OpenSourceLookupAsync);
        else
            OpenReturnItemPicker();
    }

    private async void SourceNumberBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.F4 or Key.Enter)) return; e.Handled = true;
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(SourceNumberBox.Text))
            await GuardedAsync(() => ResolveSourceNumberAsync(SourceNumberBox.Text));
        else
            await GuardedAsync(OpenSourceLookupAsync);
    }
    private async void SourceNumberBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await GuardedAsync(OpenSourceLookupAsync);
    private async void SearchSource_Click(object sender, RoutedEventArgs e) => await GuardedAsync(OpenSourceLookupAsync);
    private async Task OpenSourceLookupAsync()
    {
        if (_busy || _currentReturn is not null) return;
        var dialog = new PurchaseLookupWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.SelectedPurchase is not null)
            await ApplySourceAsync(dialog.SelectedPurchase);
    }
    private async Task ResolveSourceNumberAsync(string number)
    {
        try
        {
            var matches = await _api.SearchPurchasesAsync(number);
            var purchase = matches.FirstOrDefault(x => string.Equals(x.DocumentNumber, number.Trim(), StringComparison.OrdinalIgnoreCase));
            if (purchase is null) throw new InvalidOperationException("Faktur Pembelian tidak ditemukan.");
            await ApplySourceAsync(purchase);
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async Task ApplySourceAsync(PurchaseDto purchase)
    {
        try
        {
            SetBusy(true, "MEMUAT FAKTUR SUMBER...");
            var source = await _api.GetPurchaseReturnSourceAsync(purchase.Id); _source = source;
            SourceNumberBox.Text = source.DocumentNumber; SupplierCodeBox.Text = source.SupplierCode; SupplierNameBox.Text = source.SupplierName; AddressBox.Text = source.SupplierAddress ?? string.Empty;
            TransactionTypeBox.Text = source.TransactionType == "CASH" ? "T TUNAI" : "K KREDIT"; WarehouseBox.Text = source.WarehouseCode;
            Lines.Clear();
            foreach (var line in source.Lines) Lines.Add(new() { PurchaseLineId = line.PurchaseLineId, SourceDocumentNumber = source.DocumentNumber, WarehouseCode = line.WarehouseCode,
                ItemCode = line.ItemCode, ItemName = line.ItemName, PurchasedQuantity = line.PurchasedQuantity, PreviouslyReturned = line.ReturnedQuantity,
                RemainingQuantity = line.RemainingQuantity, Unit = line.Unit, UnitPrice = line.UnitPrice, DiscountPercent = line.DiscountPercent });
            ModeStatusText.Text = source.Lines.Any(x => x.RemainingQuantity > 0) ? "FAKTUR SIAP DIRETUR" : "SELURUH BARANG SUDAH DIRETUR";
            FocusReturnQuantityCell(Lines.FirstOrDefault(x => x.RemainingQuantity > 0));
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private void LinesGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e) => Dispatcher.BeginInvoke(UpdateTotals);
    private void LinesGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (_currentReturn is not null || _source is null || e.Column.DisplayIndex != 6)
            e.Cancel = true;
    }

    private void LinesGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitPendingGridEdit();
            MoveToNextReturnRow();
            UpdateTotals();
            UpdateCommandStates();
            return;
        }

        if (e.Key != Key.F4) return;
        e.Handled = true;
        if (_source is null) _ = GuardedAsync(OpenSourceLookupAsync);
        else OpenReturnItemPicker();
    }

    private async void LinesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_currentReturn is not null) return;
        if (_source is null)
        {
            await GuardedAsync(OpenSourceLookupAsync);
            return;
        }

        if (LinesGrid.SelectedItem is PurchaseReturnRow row && row.RemainingQuantity > 0)
        {
            if (row.ReturnQuantity <= 0)
                row.ReturnQuantity = Math.Min(1m, row.RemainingQuantity);
            FocusReturnQuantityCell(row);
            UpdateTotals();
            UpdateCommandStates();
            return;
        }

        OpenReturnItemPicker();
    }

    private async void SelectReturnItem_Click(object sender, RoutedEventArgs e)
    {
        if (_source is null)
        {
            await GuardedAsync(OpenSourceLookupAsync);
            return;
        }

        OpenReturnItemPicker();
    }
    private void OpenReturnItemPicker()
    {
        if (_source is null)
        {
            ShowError(new InvalidOperationException("Pilih Faktur Pembelian sumber terlebih dahulu."));
            return;
        }
        if (_currentReturn is not null) return;
        var choices = Lines.Where(x => x.RemainingQuantity > 0)
            .Select(x => new ReturnItemChoice(x, x.WarehouseCode, x.ItemCode, x.ItemName, x.PurchasedQuantity, x.PreviouslyReturned, x.RemainingQuantity, x.Unit)).ToList();
        var dialog = new ReturnItemPickerWindow(_source.DocumentNumber, choices) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.SelectedItem?.SourceRow is not PurchaseReturnRow row) return;
        row.ReturnQuantity = Math.Min(row.RemainingQuantity, row.ReturnQuantity > 0 ? row.ReturnQuantity : 1m);
        FocusReturnQuantityCell(row);
        UpdateTotals();
        UpdateCommandStates();
    }
    private void TotalsInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateTotals();
        UpdateCommandStates();
    }
    private void UpdateTotals()
    {
        if (GrandTotalBox is null) return;
        var subtotal = Lines.Sum(x => x.Total); var discount = Math.Min(subtotal, Math.Round(subtotal * PurchaseView.ParseDecimal(DiscountPercentBox?.Text) / 100m, 2) + PurchaseView.ParseDecimal(DiscountAmountBox?.Text));
        var net = subtotal - discount; var total = net + Math.Round(net * PurchaseView.ParseDecimal(TaxPercentBox?.Text) / 100m, 2);
        DiscountTotalBox.Text = discount.ToString("N2"); NetBox.Text = net.ToString("N2"); GrandTotalBox.Text = total.ToString("N2");
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();
    private async void SavePrint_Click(object sender, RoutedEventArgs e) => await SaveAndPrintAsync();

    private async Task<bool> SaveAsync(bool showMessage = true)
    {
        if (_busy) return false;
        if (_currentReturn is not null) return true;
        CommitPendingGridEdit();
        try
        {
            SetBusy(true, "MENYIMPAN RETUR & MENGURANGI STOK...");
            if (_source is null) throw new InvalidOperationException("Pilih Faktur Pembelian sumber terlebih dahulu.");
            var active = Lines.Where(x => x.ReturnQuantity > 0).ToArray(); if (active.Length == 0) throw new InvalidOperationException("Isi jumlah retur minimal pada satu barang.");
            foreach (var row in active) if (row.ReturnQuantity > row.RemainingQuantity) throw new InvalidOperationException($"Jumlah retur {row.ItemCode} melebihi sisa {row.RemainingQuantity:N3}.");
            var request = new CreatePurchaseReturnRequest(_source.PurchaseId, DateOnly.FromDateTime(ReturnDatePicker.SelectedDate ?? DateTime.Today),
                NullIfBlank(ReturnNumberBox.Text),
                PurchaseView.ParseDecimal(DiscountPercentBox.Text), PurchaseView.ParseDecimal(DiscountAmountBox.Text), PurchaseView.ParseDecimal(TaxPercentBox.Text),
                string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text.Trim(), AksaraApiClient.Shared.CurrentUserName, Environment.MachineName,
                active.Select(x => new CreatePurchaseReturnLineRequest(x.PurchaseLineId, x.ReturnQuantity)).ToArray());
            var saved = await _api.CreatePurchaseReturnAsync(request);
            LocalTransactionDraftStore.Delete(LocalDraftKey);
            await LoadReturnAsync(saved);
            if (showMessage)
                MessageBox.Show($"Retur {saved.DocumentNumber} berhasil disimpan.\nStok berkurang dan laporan Pembelian langsung diperbarui.", "AKSARA", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return false;
        }
        finally { SetBusy(false); }
    }

    private async Task SaveAndPrintAsync()
    {
        if (_currentReturn is null && !await SaveAsync(showMessage: false)) return;
        if (_currentReturn is null) return;

        var history = await LoadPrintHistoryAsync();
        var dialog = new TransactionPrintOptionsWindow(
            "Cetak Bukti Retur Pembelian",
            "No. Retur",
            _currentReturn.DocumentNumber,
            [new("purchase-return", "Retur Pembelian")],
            history.Select(x => new TransactionPrintHistoryEntry(x.DocumentDate, x.DocumentNumber, x.SupplierName, x.GrandTotal, x)).ToArray())
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true) return;

        var selected = await ResolveReturnForPrintAsync(dialog.SelectedNumber, _currentReturn, history);
        if (selected is null) return;
        var document = BuildReturnDocument(selected);
        RunPrintAction(dialog.SelectedAction, document);
    }

    private async Task<IReadOnlyList<PurchaseReturnDto>> LoadPrintHistoryAsync()
    {
        try
        {
            return await _api.SearchPurchaseReturnsAsync(null);
        }
        catch (Exception exception)
        {
            ShowError(exception);
            return [];
        }
    }

    private async Task<PurchaseReturnDto?> ResolveReturnForPrintAsync(string number, PurchaseReturnDto fallback, IReadOnlyList<PurchaseReturnDto> history)
    {
        if (string.IsNullOrWhiteSpace(number) || string.Equals(number, fallback.DocumentNumber, StringComparison.OrdinalIgnoreCase)) return fallback;
        var selected = history.FirstOrDefault(x => string.Equals(x.DocumentNumber, number, StringComparison.OrdinalIgnoreCase));
        if (selected is not null) return selected;
        try
        {
            return (await _api.SearchPurchaseReturnsAsync(number)).FirstOrDefault(x => string.Equals(x.DocumentNumber, number, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            ShowError(exception);
            return null;
        }
    }

    private static PrintableTransaction BuildReturnDocument(PurchaseReturnDto item) => new(
        "Retur Pembelian",
        "No. Retur",
        item.DocumentNumber,
        item.DocumentDate,
        "Supplier",
        item.SupplierCode,
        item.SupplierName,
        item.SupplierAddress,
        item.TransactionType,
        item.WarehouseCode,
        null,
        null,
        item.Subtotal,
        item.DiscountAmount,
        item.TaxAmount,
        item.GrandTotal,
        string.IsNullOrWhiteSpace(item.Notes) ? $"No. Faktur: {item.SourceDocumentNumber}" : $"No. Faktur: {item.SourceDocumentNumber}. {item.Notes}",
        item.Lines.Select((line, index) => new PrintableTransactionLine(index + 1, line.ItemCode, line.ItemName, line.Quantity, line.Unit, line.UnitPrice, line.DiscountPercent, line.LineTotal)).ToArray());

    private static void RunPrintAction(TransactionPrintAction action, PrintableTransaction document)
    {
        if (action == TransactionPrintAction.Preview) TransactionDocumentOutput.Preview(document);
        else if (action == TransactionPrintAction.ExportExcel) TransactionDocumentOutput.ExportExcel(document);
        else TransactionDocumentOutput.Print(document);
    }

    private async void SearchReturn_Click(object sender, RoutedEventArgs e) => await GuardedAsync(OpenReturnLookupAsync);

    private async Task OpenReturnLookupAsync()
    {
        var dialog = new PurchaseReturnLookupWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.SelectedReturn is not null)
            await LoadReturnAsync(dialog.SelectedReturn);
    }
    private async Task LoadReturnAsync(PurchaseReturnDto item)
    {
        _currentReturn = item; _source = await _api.GetPurchaseReturnSourceAsync(item.PurchaseId);
        ReturnDatePicker.SelectedDate = item.DocumentDate.ToDateTime(TimeOnly.MinValue); _returnNumberManuallyEdited = false; SetReturnNumber(item.DocumentNumber); SourceNumberBox.Text = item.SourceDocumentNumber;
        SupplierCodeBox.Text = item.SupplierCode; SupplierNameBox.Text = item.SupplierName; AddressBox.Text = item.SupplierAddress ?? string.Empty;
        TransactionTypeBox.Text = item.TransactionType == "CASH" ? "T TUNAI" : "K KREDIT"; WarehouseBox.Text = item.WarehouseCode; NotesBox.Text = item.Notes ?? string.Empty;
        DiscountAmountBox.Text = item.DiscountAmount.ToString(CultureInfo.CurrentCulture); TaxPercentBox.Text = item.Subtotal - item.DiscountAmount == 0 ? "0" : (item.TaxAmount * 100m / (item.Subtotal - item.DiscountAmount)).ToString("0.####", CultureInfo.CurrentCulture);
        Lines.Clear();
        foreach (var line in item.Lines)
        {
            var sourceLine = _source.Lines.FirstOrDefault(x => x.PurchaseLineId == line.PurchaseLineId);
            Lines.Add(new() { PurchaseLineId = line.PurchaseLineId, SourceDocumentNumber = line.SourceDocumentNumber, WarehouseCode = line.WarehouseCode,
                ItemCode = line.ItemCode, ItemName = line.ItemName, PurchasedQuantity = sourceLine?.PurchasedQuantity ?? line.Quantity,
                PreviouslyReturned = Math.Max(0, (sourceLine?.ReturnedQuantity ?? line.Quantity) - line.Quantity), RemainingQuantity = sourceLine?.RemainingQuantity ?? 0,
                ReturnQuantity = line.Quantity, Unit = line.Unit, UnitPrice = line.UnitPrice, DiscountPercent = line.DiscountPercent });
        }
        ModeStatusText.Text = item.Status == "CANCELLED" ? "DITANDAI HAPUS" : "DATA RETUR TERSIMPAN"; SetReadOnly(true); UpdateTotals(); UpdateCommandStates();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentReturn is null || _currentReturn.Status != "POSTED") return;
        if (MessageBox.Show("Apakah anda ingin menandai hapus data ini?", "AKSARA", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var dialog = new TextInputDialog("Alasan Hapus Retur", "Masukkan alasan hapus retur:") { Owner = Window.GetWindow(this) }; if (dialog.ShowDialog() != true) return;
        try
        {
            SetBusy(true, "MENANDAI HAPUS RETUR...");
            var cancelled = await _api.CancelPurchaseReturnAsync(_currentReturn.Id, new(dialog.Value, AksaraApiClient.Shared.CurrentUserName, Environment.MachineName)); await LoadReturnAsync(cancelled);
            MessageBox.Show("Retur sudah ditandai hapus. Stok dan laporan sudah disesuaikan.", "AKSARA", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_currentReturn is null || _currentReturn.Status != "CANCELLED") return;
        if (MessageBox.Show("Apakah anda ingin membatalkan hapus data?", "AKSARA", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            SetBusy(true, "MEMBATALKAN TANDA HAPUS RETUR...");
            var restored = await _api.RestorePurchaseReturnAsync(_currentReturn.Id, new RestorePurchaseReturnRequest(AksaraApiClient.Shared.CurrentUserName, Environment.MachineName));
            await LoadReturnAsync(restored);
            MessageBox.Show("Tanda hapus retur sudah dibatalkan. Data kembali diperhitungkan.", "AKSARA", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async void Add_Click(object sender, RoutedEventArgs e) { if (await ConfirmPendingChangesAsync(discardOnNo: true)) ResetForm(); }
    private async void Reset_Click(object sender, RoutedEventArgs e) { if (await ConfirmPendingChangesAsync(discardOnNo: true)) ResetForm(); }
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void SetBusy(bool busy, string? status = null) { _busy = busy; if (!string.IsNullOrWhiteSpace(status)) ModeStatusText.Text = status; IsEnabled = !busy; if (!busy && _currentReturn is null && _source is null) ModeStatusText.Text = "TRANSAKSI BARU"; UpdateCommandStates(); }

    public async Task<bool> ConfirmCloseAsync() => await ConfirmPendingChangesAsync(discardOnNo: true);

    private async Task GuardedAsync(Func<Task> action)
    {
        if (await ConfirmPendingChangesAsync(discardOnNo: true))
            await action();
    }

    private Task<bool> ConfirmPendingChangesAsync(bool discardOnNo)
    {
        if (!HasUnsavedChanges()) return Task.FromResult(true);
        var choice = LocalDraftPromptWindow.Show(Window.GetWindow(this), "Retur Pembelian");
        if (choice == LocalDraftChoice.KeepEditing)
        {
            ModeStatusText.Text = "AKSI DIBATALKAN";
            HelpText.Text = "Data belum disimpan tetap di form.";
            return Task.FromResult(false);
        }

        if (choice == LocalDraftChoice.SaveDraft)
        {
            try
            {
                SaveLocalDraft();
                return Task.FromResult(true);
            }
            catch (Exception exception)
            {
                ShowError(new InvalidOperationException($"Draft lokal tidak dapat disimpan: {exception.Message}", exception));
                return Task.FromResult(false);
            }
        }

        LocalTransactionDraftStore.Delete(LocalDraftKey);
        return Task.FromResult(true);
    }

    private void SaveLocalDraft()
    {
        CommitPendingGridEdit();
        LocalTransactionDraftStore.Save(LocalDraftKey, new PurchaseReturnLocalDraft(
            ReturnDatePicker.SelectedDate,
            ReturnNumberBox.Text,
            SourceNumberBox.Text,
            SupplierCodeBox.Text,
            SupplierNameBox.Text,
            AddressBox.Text,
            TransactionTypeBox.Text,
            WarehouseBox.Text,
            _source,
            NotesBox.Text,
            DiscountPercentBox.Text,
            DiscountAmountBox.Text,
            TaxPercentBox.Text,
            Lines.Where(line => line.ReturnQuantity > 0)
                .Select(line => new ReturnQuantityLocalDraft(line.PurchaseLineId, line.ReturnQuantity))
                .ToArray()));
        ModeStatusText.Text = "DRAFT LOKAL";
        HelpText.Text = "Draft Retur Pembelian tersimpan lokal dan belum memengaruhi stok atau laporan.";
    }

    private void RestoreLocalDraft()
    {
        if (_draftChecked) return;
        _draftChecked = true;
        if (!LocalTransactionDraftStore.TryLoad<PurchaseReturnLocalDraft>(LocalDraftKey, out var draft) || draft is null) return;

        _currentReturn = null;
        _source = draft.Source;
        ReturnDatePicker.SelectedDate = draft.ReturnDate ?? DateTime.Today;
        _returnNumberManuallyEdited = true;
        SetReturnNumber(draft.ReturnNumber);
        SourceNumberBox.Text = draft.SourceNumber;
        SupplierCodeBox.Text = draft.SupplierCode;
        SupplierNameBox.Text = draft.SupplierName;
        AddressBox.Text = draft.SupplierAddress;
        TransactionTypeBox.Text = draft.TransactionType;
        WarehouseBox.Text = draft.Warehouse;
        NotesBox.Text = draft.Notes;
        DiscountPercentBox.Text = draft.DiscountPercent;
        DiscountAmountBox.Text = draft.DiscountAmount;
        TaxPercentBox.Text = draft.TaxPercent;
        Lines.Clear();
        if (_source is not null)
        {
            var quantities = draft.Lines.ToDictionary(line => line.SourceLineId, line => line.Quantity);
            foreach (var line in _source.Lines)
            {
                Lines.Add(new PurchaseReturnRow
                {
                    PurchaseLineId = line.PurchaseLineId,
                    SourceDocumentNumber = _source.DocumentNumber,
                    WarehouseCode = line.WarehouseCode,
                    ItemCode = line.ItemCode,
                    ItemName = line.ItemName,
                    PurchasedQuantity = line.PurchasedQuantity,
                    PreviouslyReturned = line.ReturnedQuantity,
                    RemainingQuantity = line.RemainingQuantity,
                    Unit = line.Unit,
                    UnitPrice = line.UnitPrice,
                    DiscountPercent = line.DiscountPercent,
                    ReturnQuantity = quantities.GetValueOrDefault(line.PurchaseLineId)
                });
            }
        }
        SetReadOnly(false);
        ModeStatusText.Text = "DRAFT LOKAL";
        HelpText.Text = "Draft Retur Pembelian dipulihkan. Tekan Simpan untuk menjadikannya transaksi.";
        UpdateTotals();
        UpdateCommandStates();
    }

    private bool HasUnsavedChanges()
    {
        if (_busy || _currentReturn is not null || !IsLoaded) return false;
        CommitPendingGridEdit();
        return _source is not null
            || Lines.Any(x => x.ReturnQuantity > 0)
            || !string.IsNullOrWhiteSpace(NotesBox.Text)
            || PurchaseView.ParseDecimal(DiscountPercentBox.Text) > 0m
            || PurchaseView.ParseDecimal(DiscountAmountBox.Text) > 0m
            || PurchaseView.ParseDecimal(TaxPercentBox.Text) > 0m
            || (_returnNumberManuallyEdited && !string.IsNullOrWhiteSpace(ReturnNumberBox.Text));
    }

    private void UpdateCommandStates()
    {
        if (!IsLoaded || SaveButton is null) return;
        var viewingSavedReturn = _currentReturn is not null;
        var hasSource = _source is not null;
        var hasReturnLines = Lines.Any(x => x.ReturnQuantity > 0);
        ReturnSearchButton.IsEnabled = !_busy;
        SourceButton.IsEnabled = !_busy && !viewingSavedReturn;
        ItemButton.IsEnabled = !_busy && !viewingSavedReturn && hasSource;
        AddButton.IsEnabled = !_busy;
        ResetButton.IsEnabled = !_busy && (viewingSavedReturn || hasSource || hasReturnLines);
        SaveButton.IsEnabled = !_busy && !viewingSavedReturn && hasSource && hasReturnLines;
        SavePrintButton.IsEnabled = !_busy && (viewingSavedReturn || (hasSource && hasReturnLines));
        DeleteButton.IsEnabled = !_busy && viewingSavedReturn && string.Equals(_currentReturn?.Status, "POSTED", StringComparison.OrdinalIgnoreCase);
        RestoreButton.IsEnabled = !_busy && viewingSavedReturn && string.Equals(_currentReturn?.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase);
        CloseButton.IsEnabled = !_busy;
        LinesGrid.IsReadOnly = _busy || viewingSavedReturn || !hasSource;
        if (!hasSource && !viewingSavedReturn)
            HelpText.Text = "Pilih Faktur / tekan F4 / double-click No.Faktur untuk memilih faktur sumber.";
        else if (!hasReturnLines && !viewingSavedReturn)
            HelpText.Text = "Isi kolom Retur, double-click baris barang, atau tekan F4 untuk memilih barang yang akan diretur.";
        else if (!viewingSavedReturn)
            HelpText.Text = "Tekan Enter untuk pindah baris. Klik Simpan untuk mengurangi stok dan memperbarui laporan.";
        else
            HelpText.Text = string.Equals(_currentReturn?.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)
                ? "Data retur sedang ditandai hapus. Klik Btl.Hapus untuk mengaktifkan kembali, atau Tambah untuk input retur baru."
                : "Data retur tersimpan. Klik Hapus untuk menandai hapus, atau Tambah untuk input retur baru.";
    }

    private void FocusReturnQuantityCell(PurchaseReturnRow? row)
    {
        if (row is null || LinesGrid is null || LinesGrid.Columns.Count <= 6) return;
        var column = LinesGrid.Columns[6];
        LinesGrid.SelectedItem = row;
        LinesGrid.CurrentCell = new DataGridCellInfo(row, column);
        LinesGrid.ScrollIntoView(row, column);
        LinesGrid.Focus();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            LinesGrid.SelectedItem = row;
            LinesGrid.CurrentCell = new DataGridCellInfo(row, column);
            LinesGrid.BeginEdit();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void MoveToNextReturnRow()
    {
        if (_source is null || Lines.Count == 0) return;
        var current = LinesGrid.SelectedItem as PurchaseReturnRow;
        var currentIndex = current is null ? -1 : Lines.IndexOf(current);
        var next = Lines.Skip(currentIndex + 1).FirstOrDefault(x => x.RemainingQuantity > 0) ?? Lines.FirstOrDefault(x => x.RemainingQuantity > 0);
        FocusReturnQuantityCell(next);
    }

    private void CommitPendingGridEdit()
    {
        if (!IsLoaded || LinesGrid is null) return;
        LinesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        LinesGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static void ShowError(Exception ex) => MessageBox.Show(ex.Message, "AKSARA - Retur Pembelian", MessageBoxButton.OK, MessageBoxImage.Warning);
}

public sealed class PurchaseReturnRow : INotifyPropertyChanged
{
    private decimal _returnQuantity;
    public Guid PurchaseLineId { get; init; } public string SourceDocumentNumber { get; init; } = ""; public string WarehouseCode { get; init; } = "";
    public string ItemCode { get; init; } = ""; public string ItemName { get; init; } = ""; public decimal PurchasedQuantity { get; init; }
    public decimal PreviouslyReturned { get; init; } public decimal RemainingQuantity { get; init; } public string Unit { get; init; } = "";
    public decimal UnitPrice { get; init; } public decimal DiscountPercent { get; init; }
    public decimal ReturnQuantity { get => _returnQuantity; set { _returnQuantity = value; OnPropertyChanged(); OnPropertyChanged(nameof(Total)); } }
    public decimal Total => Math.Round(ReturnQuantity * UnitPrice * (1m - DiscountPercent / 100m), 2);
    public event PropertyChangedEventHandler? PropertyChanged; private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
