using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Aksara.Contracts;
using Aksara.Desktop.Controls;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

public partial class SalesReturnView : UserControl, IWorkspaceCloseGuard
{
    private const string LocalDraftKey = "sales-return";
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private SaleReturnSourceDto? _source;
    private SaleReturnDto? _currentReturn;
    private bool _busy;
    private bool _returnNumberManuallyEdited;
    private bool _suppressReturnNumberChange;
    private string? _suggestedReturnNumber;
    private bool _draftChecked;

    public DateTime? ReturnDate { get; set; } = DateTime.Today;
    public ObservableCollection<SalesReturnRow> Lines { get; } = [];
    public event EventHandler? CloseRequested;

    public SalesReturnView()
    {
        InitializeComponent();
        DataContext = this;
        Lines.CollectionChanged += (_, args) =>
        {
            if (args.NewItems is not null)
                foreach (SalesReturnRow row in args.NewItems) row.PropertyChanged += (_, _) =>
                {
                    UpdateTotals();
                    UpdateCommandStates();
                };
            UpdateTotals();
            UpdateCommandStates();
        };
        ReturnDatePicker.SelectedDateChanged += (_, _) =>
        {
            if (_currentReturn is null && !_returnNumberManuallyEdited) _ = RefreshNewReturnNumberAsync();
        };
        Loaded += async (_, _) =>
        {
            ResetForm();
            RestoreLocalDraft();
            SourceNumberBox.Focus();
            await RefreshServerStatusAsync();
            UpdateCommandStates();
        };
    }

    private async Task RefreshServerStatusAsync()
    {
        var ready = await _api.IsDatabaseReadyAsync();
        ServerStatusText.Text = ready ? "SERVER & STOK SIAP" : "SERVER BELUM SIAP";
        ServerStatusText.Foreground = new SolidColorBrush(ready ? Color.FromRgb(23, 107, 100) : Color.FromRgb(180, 35, 24));
    }

    private void ResetForm()
    {
        _source = null;
        _currentReturn = null;
        Lines.Clear();
        if (!IsLoaded) return;
        ReturnDatePicker.SelectedDate = DateTime.Today;
        _returnNumberManuallyEdited = false;
        _suggestedReturnNumber = null;
        SetReturnNumber(string.Empty);
        _ = RefreshNewReturnNumberAsync();
        SourceNumberBox.Clear();
        CustomerCodeBox.Clear();
        CustomerNameBox.Clear();
        AddressBox.Clear();
        TransactionTypeBox.Clear();
        SalesCodeBox.Clear();
        WarehouseBox.Text = "UTAMA";
        NotesBox.Clear();
        DiscountPercentBox.Text = "0";
        DiscountAmountBox.Text = "0";
        TaxPercentBox.Text = "0";
        ModeStatusText.Text = "TRANSAKSI BARU";
        SetReadOnly(false);
        UpdateTotals();
        UpdateCommandStates();
    }

    private void SetReadOnly(bool readOnly)
    {
        if (!IsLoaded) return;
        ReturnDatePicker.IsEnabled = !readOnly;
        ReturnNumberBox.IsReadOnly = readOnly;
        SourceNumberBox.IsReadOnly = readOnly;
        LinesGrid.IsReadOnly = readOnly;
        NotesBox.IsReadOnly = readOnly;
        DiscountPercentBox.IsReadOnly = readOnly;
        DiscountAmountBox.IsReadOnly = readOnly;
        TaxPercentBox.IsReadOnly = readOnly;
        SaveButton.IsEnabled = !readOnly;
        SavePrintButton.IsEnabled = !_busy;
        DeleteButton.IsEnabled = readOnly && string.Equals(_currentReturn?.Status, "POSTED", StringComparison.OrdinalIgnoreCase);
        RestoreButton.IsEnabled = readOnly && string.Equals(_currentReturn?.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase);
        UpdateCommandStates();
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
            var number = await _api.GetNextSaleReturnNumberAsync(date);
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
        if (e.Key is not (Key.F4 or Key.Enter)) return;
        e.Handled = true;
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(SourceNumberBox.Text))
            await GuardedAsync(() => ResolveSourceNumberAsync(SourceNumberBox.Text));
        else
            await GuardedAsync(OpenSourceLookupAsync);
    }

    private async void SourceNumberBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        await GuardedAsync(OpenSourceLookupAsync);
    }

    private async void SearchSource_Click(object sender, RoutedEventArgs e)
    {
        await GuardedAsync(OpenSourceLookupAsync);
    }

    private async Task OpenSourceLookupAsync()
    {
        if (_busy || _currentReturn is not null) return;
        var dialog = new SaleLookupWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.SelectedSale is not null)
            await ApplySourceSaleAsync(dialog.SelectedSale);
    }

    private async Task ResolveSourceNumberAsync(string number)
    {
        try
        {
            var matches = await _api.SearchSalesAsync(number);
            var sale = matches.FirstOrDefault(x => string.Equals(x.DocumentNumber, number.Trim(), StringComparison.OrdinalIgnoreCase));
            if (sale is null) throw new InvalidOperationException("Nota Penjualan tidak ditemukan.");
            await ApplySourceSaleAsync(sale);
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async Task ApplySourceSaleAsync(SaleDto sale)
    {
        try
        {
            SetBusy(true, "MEMUAT NOTA SUMBER...");
            var source = await _api.GetSaleReturnSourceAsync(sale.Id);
            _source = source;
            SourceNumberBox.Text = source.DocumentNumber;
            CustomerCodeBox.Text = source.CustomerCode;
            CustomerNameBox.Text = source.CustomerName;
            AddressBox.Text = source.CustomerAddress ?? string.Empty;
            TransactionTypeBox.Text = source.TransactionType == "CASH" ? "T TUNAI" : "K KREDIT";
            SalesCodeBox.Text = source.SalesCode ?? string.Empty;
            WarehouseBox.Text = source.WarehouseCode;
            Lines.Clear();
            foreach (var line in source.Lines)
            {
                Lines.Add(new SalesReturnRow
                {
                    SaleLineId = line.SaleLineId,
                    SourceDocumentNumber = source.DocumentNumber,
                    WarehouseCode = line.WarehouseCode,
                    ItemCode = line.ItemCode,
                    ItemName = line.ItemName,
                    SoldQuantity = line.SoldQuantity,
                    PreviouslyReturned = line.ReturnedQuantity,
                    RemainingQuantity = line.RemainingQuantity,
                    Unit = line.Unit,
                    UnitPrice = line.UnitPrice,
                    DiscountPercent = line.DiscountPercent,
                    UnitCost = line.UnitCost
                });
            }
            ModeStatusText.Text = source.Lines.Any(x => x.RemainingQuantity > 0) ? "NOTA SIAP DIRETUR" : "SELURUH BARANG SUDAH DIRETUR";
            FocusReturnQuantityCell(Lines.FirstOrDefault(x => x.RemainingQuantity > 0));
        }
        catch (Exception exception) { ShowError(exception); }
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
            LinesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            LinesGrid.CommitEdit(DataGridEditingUnit.Row, true);
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

        if (LinesGrid.SelectedItem is SalesReturnRow row && row.RemainingQuantity > 0)
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
            ShowError(new InvalidOperationException("Pilih Nota Penjualan sumber terlebih dahulu."));
            return;
        }
        if (_currentReturn is not null) return;
        var choices = Lines.Where(x => x.RemainingQuantity > 0)
            .Select(x => new ReturnItemChoice(x, x.WarehouseCode, x.ItemCode, x.ItemName, x.SoldQuantity, x.PreviouslyReturned, x.RemainingQuantity, x.Unit)).ToList();
        var dialog = new ReturnItemPickerWindow(_source.DocumentNumber, choices) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.SelectedItem?.SourceRow is not SalesReturnRow row) return;
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
        if (DiscountTotalBox is null || NetBox is null || GrandTotalBox is null) return;
        var subtotal = Lines.Sum(x => x.Total);
        var discount = Math.Min(subtotal, Math.Round(subtotal * ParseDecimal(DiscountPercentBox?.Text) / 100m, 2) + ParseDecimal(DiscountAmountBox?.Text));
        var net = subtotal - discount;
        var tax = Math.Round(net * ParseDecimal(TaxPercentBox?.Text) / 100m, 2);
        DiscountTotalBox.Text = discount.ToString("N2");
        NetBox.Text = net.ToString("N2");
        GrandTotalBox.Text = (net + tax).ToString("N2");
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
            SetBusy(true, "MENYIMPAN RETUR & MENAMBAH STOK...");
            if (_source is null) throw new InvalidOperationException("Pilih Nota Penjualan sumber terlebih dahulu.");
            var active = Lines.Where(x => x.ReturnQuantity > 0).ToArray();
            if (active.Length == 0) throw new InvalidOperationException("Isi jumlah retur minimal pada satu barang.");
            foreach (var row in active)
                if (row.ReturnQuantity > row.RemainingQuantity)
                    throw new InvalidOperationException($"Jumlah retur {row.ItemCode} melebihi sisa {row.RemainingQuantity:N3}.");

            var request = new CreateSaleReturnRequest(
                _source.SaleId,
                DateOnly.FromDateTime(ReturnDatePicker.SelectedDate ?? DateTime.Today),
                NullIfBlank(ReturnNumberBox.Text),
                ParseDecimal(DiscountPercentBox.Text),
                ParseDecimal(DiscountAmountBox.Text),
                ParseDecimal(TaxPercentBox.Text),
                string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text.Trim(),
                AksaraApiClient.Shared.CurrentUserName,
                Environment.MachineName,
                active.Select(x => new CreateSaleReturnLineRequest(x.SaleLineId, x.ReturnQuantity)).ToArray());
            var saved = await _api.CreateSaleReturnAsync(request);
            LocalTransactionDraftStore.Delete(LocalDraftKey);
            await LoadReturnAsync(saved);
            if (showMessage)
                MessageBox.Show($"Retur {saved.DocumentNumber} berhasil disimpan.\nStok bertambah dan laporan Penjualan/Laba langsung diperbarui.", "AKSARA", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        catch (Exception exception)
        {
            ShowError(exception);
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
            "Cetak Bukti Retur Penjualan",
            "No. Retur",
            _currentReturn.DocumentNumber,
            [new("sales-return", "Retur Penjualan")],
            history.Select(x => new TransactionPrintHistoryEntry(x.DocumentDate, x.DocumentNumber, x.CustomerName, x.GrandTotal, x)).ToArray())
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true) return;

        var selected = await ResolveReturnForPrintAsync(dialog.SelectedNumber, _currentReturn, history);
        if (selected is null) return;
        var document = BuildReturnDocument(selected);
        RunPrintAction(dialog.SelectedAction, document);
    }

    private async Task<IReadOnlyList<SaleReturnDto>> LoadPrintHistoryAsync()
    {
        try
        {
            return await _api.SearchSaleReturnsAsync(null);
        }
        catch (Exception exception)
        {
            ShowError(exception);
            return [];
        }
    }

    private async Task<SaleReturnDto?> ResolveReturnForPrintAsync(string number, SaleReturnDto fallback, IReadOnlyList<SaleReturnDto> history)
    {
        if (string.IsNullOrWhiteSpace(number) || string.Equals(number, fallback.DocumentNumber, StringComparison.OrdinalIgnoreCase)) return fallback;
        var selected = history.FirstOrDefault(x => string.Equals(x.DocumentNumber, number, StringComparison.OrdinalIgnoreCase));
        if (selected is not null) return selected;
        try
        {
            return (await _api.SearchSaleReturnsAsync(number)).FirstOrDefault(x => string.Equals(x.DocumentNumber, number, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            ShowError(exception);
            return null;
        }
    }

    private static PrintableTransaction BuildReturnDocument(SaleReturnDto item) => new(
        "Retur Penjualan",
        "No. Retur",
        item.DocumentNumber,
        item.DocumentDate,
        "Customer",
        item.CustomerCode,
        item.CustomerName,
        item.CustomerAddress,
        item.TransactionType,
        item.WarehouseCode,
        item.SalesCode,
        null,
        item.Subtotal,
        item.DiscountAmount,
        item.TaxAmount,
        item.GrandTotal,
        string.IsNullOrWhiteSpace(item.Notes) ? $"No. Nota: {item.SourceDocumentNumber}" : $"No. Nota: {item.SourceDocumentNumber}. {item.Notes}",
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
        var dialog = new SaleReturnLookupWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.SelectedReturn is not null)
            await LoadReturnAsync(dialog.SelectedReturn);
    }

    private async Task LoadReturnAsync(SaleReturnDto item)
    {
        _currentReturn = item;
        _source = await _api.GetSaleReturnSourceAsync(item.SaleId);
        ReturnDatePicker.SelectedDate = item.DocumentDate.ToDateTime(TimeOnly.MinValue);
        _returnNumberManuallyEdited = false;
        SetReturnNumber(item.DocumentNumber);
        SourceNumberBox.Text = item.SourceDocumentNumber;
        CustomerCodeBox.Text = item.CustomerCode;
        CustomerNameBox.Text = item.CustomerName;
        AddressBox.Text = item.CustomerAddress ?? string.Empty;
        TransactionTypeBox.Text = item.TransactionType == "CASH" ? "T TUNAI" : "K KREDIT";
        SalesCodeBox.Text = item.SalesCode ?? string.Empty;
        WarehouseBox.Text = item.WarehouseCode;
        NotesBox.Text = item.Notes ?? string.Empty;
        DiscountAmountBox.Text = item.DiscountAmount.ToString(CultureInfo.CurrentCulture);
        TaxPercentBox.Text = item.Subtotal - item.DiscountAmount == 0 ? "0" : (item.TaxAmount * 100m / (item.Subtotal - item.DiscountAmount)).ToString("0.####", CultureInfo.CurrentCulture);
        Lines.Clear();
        foreach (var line in item.Lines)
        {
            var sourceLine = _source.Lines.FirstOrDefault(x => x.SaleLineId == line.SaleLineId);
            Lines.Add(new SalesReturnRow
            {
                SaleLineId = line.SaleLineId,
                SourceDocumentNumber = line.SourceDocumentNumber,
                WarehouseCode = line.WarehouseCode,
                ItemCode = line.ItemCode,
                ItemName = line.ItemName,
                SoldQuantity = sourceLine?.SoldQuantity ?? line.Quantity,
                PreviouslyReturned = Math.Max(0, (sourceLine?.ReturnedQuantity ?? line.Quantity) - line.Quantity),
                RemainingQuantity = sourceLine?.RemainingQuantity ?? 0,
                ReturnQuantity = line.Quantity,
                Unit = line.Unit,
                UnitPrice = line.UnitPrice,
                DiscountPercent = line.DiscountPercent,
                UnitCost = line.Quantity == 0 ? 0 : line.CostAmount / line.Quantity
            });
        }
        ModeStatusText.Text = item.Status == "CANCELLED" ? "DITANDAI HAPUS" : "DATA RETUR TERSIMPAN";
        SetReadOnly(true);
        UpdateTotals();
        UpdateCommandStates();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentReturn is null || _currentReturn.Status != "POSTED") return;
        if (MessageBox.Show("Apakah anda ingin menandai hapus data ini?", "AKSARA", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var reasonDialog = new TextInputDialog("Alasan Hapus Retur", "Masukkan alasan hapus retur:") { Owner = Window.GetWindow(this) };
        if (reasonDialog.ShowDialog() != true) return;
        try
        {
            SetBusy(true, "MENANDAI HAPUS RETUR...");
            var cancelled = await _api.CancelSaleReturnAsync(_currentReturn.Id, new CancelSaleReturnRequest(reasonDialog.Value, AksaraApiClient.Shared.CurrentUserName, Environment.MachineName));
            await LoadReturnAsync(cancelled);
            MessageBox.Show("Retur sudah ditandai hapus. Stok dan laporan sudah disesuaikan.", "AKSARA", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) { ShowError(exception); }
        finally { SetBusy(false); }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_currentReturn is null || _currentReturn.Status != "CANCELLED") return;
        if (MessageBox.Show("Apakah anda ingin membatalkan hapus data?", "AKSARA", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            SetBusy(true, "MEMBATALKAN TANDA HAPUS RETUR...");
            var restored = await _api.RestoreSaleReturnAsync(_currentReturn.Id, new RestoreSaleReturnRequest(AksaraApiClient.Shared.CurrentUserName, Environment.MachineName));
            await LoadReturnAsync(restored);
            MessageBox.Show("Tanda hapus retur sudah dibatalkan. Data kembali diperhitungkan.", "AKSARA", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) { ShowError(exception); }
        finally { SetBusy(false); }
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmPendingChangesAsync(discardOnNo: true))
            ResetForm();
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmPendingChangesAsync(discardOnNo: true))
            ResetForm();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        if (!string.IsNullOrWhiteSpace(status)) ModeStatusText.Text = status;
        IsEnabled = !busy;
        if (!busy && _currentReturn is null && _source is null) ModeStatusText.Text = "TRANSAKSI BARU";
        UpdateCommandStates();
    }

    public async Task<bool> ConfirmCloseAsync() => await ConfirmPendingChangesAsync(discardOnNo: true);

    private async Task GuardedAsync(Func<Task> action)
    {
        if (await ConfirmPendingChangesAsync(discardOnNo: true))
            await action();
    }

    private Task<bool> ConfirmPendingChangesAsync(bool discardOnNo)
    {
        if (!HasUnsavedChanges()) return Task.FromResult(true);
        var choice = LocalDraftPromptWindow.Show(Window.GetWindow(this), "Retur Penjualan");
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
        LocalTransactionDraftStore.Save(LocalDraftKey, new SalesReturnLocalDraft(
            ReturnDatePicker.SelectedDate,
            ReturnNumberBox.Text,
            SourceNumberBox.Text,
            CustomerCodeBox.Text,
            CustomerNameBox.Text,
            AddressBox.Text,
            TransactionTypeBox.Text,
            SalesCodeBox.Text,
            WarehouseBox.Text,
            _source,
            NotesBox.Text,
            DiscountPercentBox.Text,
            DiscountAmountBox.Text,
            TaxPercentBox.Text,
            Lines.Where(line => line.ReturnQuantity > 0)
                .Select(line => new ReturnQuantityLocalDraft(line.SaleLineId, line.ReturnQuantity))
                .ToArray()));
        ModeStatusText.Text = "DRAFT LOKAL";
        HelpText.Text = "Draft Retur Penjualan tersimpan lokal dan belum memengaruhi stok atau laporan.";
    }

    private void RestoreLocalDraft()
    {
        if (_draftChecked) return;
        _draftChecked = true;
        if (!LocalTransactionDraftStore.TryLoad<SalesReturnLocalDraft>(LocalDraftKey, out var draft) || draft is null) return;

        _currentReturn = null;
        _source = draft.Source;
        ReturnDatePicker.SelectedDate = draft.ReturnDate ?? DateTime.Today;
        _returnNumberManuallyEdited = true;
        SetReturnNumber(draft.ReturnNumber);
        SourceNumberBox.Text = draft.SourceNumber;
        CustomerCodeBox.Text = draft.CustomerCode;
        CustomerNameBox.Text = draft.CustomerName;
        AddressBox.Text = draft.CustomerAddress;
        TransactionTypeBox.Text = draft.TransactionType;
        SalesCodeBox.Text = draft.SalesCode;
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
                Lines.Add(new SalesReturnRow
                {
                    SaleLineId = line.SaleLineId,
                    SourceDocumentNumber = _source.DocumentNumber,
                    WarehouseCode = line.WarehouseCode,
                    ItemCode = line.ItemCode,
                    ItemName = line.ItemName,
                    SoldQuantity = line.SoldQuantity,
                    PreviouslyReturned = line.ReturnedQuantity,
                    RemainingQuantity = line.RemainingQuantity,
                    Unit = line.Unit,
                    UnitPrice = line.UnitPrice,
                    DiscountPercent = line.DiscountPercent,
                    UnitCost = line.UnitCost,
                    ReturnQuantity = quantities.GetValueOrDefault(line.SaleLineId)
                });
            }
        }
        SetReadOnly(false);
        ModeStatusText.Text = "DRAFT LOKAL";
        HelpText.Text = "Draft Retur Penjualan dipulihkan. Tekan Simpan untuk menjadikannya transaksi.";
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
            || ParseDecimal(DiscountPercentBox.Text) > 0m
            || ParseDecimal(DiscountAmountBox.Text) > 0m
            || ParseDecimal(TaxPercentBox.Text) > 0m
            || (_returnNumberManuallyEdited && !string.IsNullOrWhiteSpace(ReturnNumberBox.Text));
    }

    private void UpdateCommandStates()
    {
        if (!IsLoaded || SaveButton is null) return;

        var viewingSavedReturn = _currentReturn is not null;
        var hasSource = _source is not null;
        var hasReturnLines = Lines.Any(x => x.ReturnQuantity > 0);

        SourceButton.IsEnabled = !_busy && !viewingSavedReturn;
        ReturnSearchButton.IsEnabled = !_busy;
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
            HelpText.Text = "Klik Cari Nota / tekan F4 / double-click No.Nota untuk memilih nota sumber. Setelah itu isi Banyak retur di tabel.";
        else if (!hasReturnLines && !viewingSavedReturn)
            HelpText.Text = "Isi kolom Banyak, double-click baris barang, atau tekan F4 untuk memilih barang yang akan diretur.";
        else if (!viewingSavedReturn)
            HelpText.Text = "Tekan Enter untuk pindah baris. Klik Simpan untuk menambah stok dan memperbarui laporan.";
        else
            HelpText.Text = string.Equals(_currentReturn?.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)
                ? "Data retur sedang ditandai hapus. Klik Btl.Hapus untuk mengaktifkan kembali, atau Tambah untuk input retur baru."
                : "Data retur tersimpan. Klik Hapus untuk menandai hapus, atau Tambah untuk input retur baru.";
    }

    private void FocusReturnQuantityCell(SalesReturnRow? row)
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

        var current = LinesGrid.SelectedItem as SalesReturnRow;
        var currentIndex = current is null ? -1 : Lines.IndexOf(current);
        var next = Lines.Skip(currentIndex + 1).FirstOrDefault(x => x.RemainingQuantity > 0)
            ?? Lines.FirstOrDefault(x => x.RemainingQuantity > 0);
        FocusReturnQuantityCell(next);
    }

    private void CommitPendingGridEdit()
    {
        if (!IsLoaded || LinesGrid is null) return;
        LinesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        LinesGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private static decimal ParseDecimal(string? value)
    {
        var text = value?.Replace("%", string.Empty).Trim();
        if (string.IsNullOrEmpty(text)) return 0m;
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var current)) return current;
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.GetCultureInfo("id-ID"), out var id)) return id;
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var invariant)) return invariant;
        return 0m;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ShowError(Exception exception) =>
        MessageBox.Show(exception.Message, "AKSARA - Retur Penjualan", MessageBoxButton.OK, MessageBoxImage.Warning);
}

public sealed class SalesReturnRow : INotifyPropertyChanged
{
    private decimal _returnQuantity;
    public Guid SaleLineId { get; init; }
    public string SourceDocumentNumber { get; init; } = string.Empty;
    public string WarehouseCode { get; init; } = string.Empty;
    public string ItemCode { get; init; } = string.Empty;
    public string ItemName { get; init; } = string.Empty;
    public decimal SoldQuantity { get; init; }
    public decimal PreviouslyReturned { get; init; }
    public decimal RemainingQuantity { get; init; }
    public string Unit { get; init; } = string.Empty;
    public decimal UnitPrice { get; init; }
    public decimal DiscountPercent { get; init; }
    public decimal UnitCost { get; init; }
    public decimal ReturnQuantity { get => _returnQuantity; set { _returnQuantity = value; OnPropertyChanged(); OnPropertyChanged(nameof(Total)); OnPropertyChanged(nameof(CostTotal)); } }
    public decimal Total => Math.Round(ReturnQuantity * UnitPrice * (1m - DiscountPercent / 100m), 2);
    public decimal CostTotal => Math.Round(ReturnQuantity * UnitCost, 5);
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
