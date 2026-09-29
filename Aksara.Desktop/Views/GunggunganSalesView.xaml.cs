using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Aksara.Contracts;
using Aksara.Desktop.Controls;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

public partial class GunggunganSalesView : UserControl, IWorkspaceCloseGuard
{
    private const string LocalDraftKey = "sales-gunggungan";
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("id-ID");
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly ObservableCollection<GunggunganEntryLine> _lines = [];
    private GunggunganSaleResultDto? _savedResult;
    private GunggunganSaleReportDto? _report;
    private IReadOnlyList<Guid> _historySaleIds = [];
    private int _historyIndex = -1;
    private bool _initialized;
    private bool _busy;
    private bool _saved;

    public event EventHandler? CloseRequested;

    public GunggunganSalesView()
    {
        InitializeComponent();
        LinesGrid.ItemsSource = _lines;
        ReportGrid.SelectionChanged += (_, _) => UpdateHistoryActions();
        Loaded += async (_, _) => await InitializeAsync();
    }

    public Task<bool> ConfirmCloseAsync() => Task.FromResult(_saved || ConfirmPendingDraft());

    private async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        DocumentDatePicker.SelectedDate = DateTime.Today;
        FromDatePicker.SelectedDate = DateTime.Today;
        ToDatePicker.SelectedDate = DateTime.Today;

        try
        {
            SetBusy(true, "Memuat gudang, akun kas, dan nomor transaksi...");
            var warehousesTask = _api.GetInventoryWarehousesAsync(false);
            var cashAccountsTask = _api.GetCashAccountsAsync(false);
            await Task.WhenAll(warehousesTask, cashAccountsTask);

            var warehouses = await warehousesTask;
            var cashAccounts = await cashAccountsTask;
            WarehouseCombo.ItemsSource = warehouses;
            CashAccountCombo.ItemsSource = cashAccounts;
            WarehouseCombo.SelectedItem = warehouses.FirstOrDefault(x => x.IsDefault) ?? warehouses.FirstOrDefault();
            CashAccountCombo.SelectedItem = cashAccounts.FirstOrDefault();
            await ReloadNextNumberAsync();
            RestoreLocalDraft();
            await ReloadNextNumberAsync();
            await LoadReportAsync();
            ItemSearchBox.Focus();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DocumentDatePicker_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _saved || _busy) return;
        await ReloadNextNumberAsync();
    }

    private async Task ReloadNextNumberAsync()
    {
        var date = DateOnly.FromDateTime(DocumentDatePicker.SelectedDate ?? DateTime.Today);
        DocumentNumberBox.Text = await _api.GetNextSaleNumberAsync(date);
    }

    private async void FindItem_Click(object sender, RoutedEventArgs e) => await FindItemAsync();

    private async void ItemSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await FindItemAsync();
    }

    private async Task FindItemAsync()
    {
        if (_busy || _saved) return;
        var warehouse = SelectedWarehouse();
        if (warehouse is null)
        {
            MessageBox.Show("Pilih gudang terlebih dahulu.", "AKSARA", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var query = ItemSearchBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(query))
            {
                SetBusy(true, "Mencari barang...");
                var matches = await _api.SearchItemsAsync(query, warehouse.Code);
                var exact = matches.FirstOrDefault(item => string.Equals(item.Code, query, StringComparison.OrdinalIgnoreCase));
                if (exact is not null || matches.Count == 1)
                {
                    AddItem(exact ?? matches[0]);
                    return;
                }
            }

            var dialog = new ItemLookupWindow(warehouse.Code, query) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() == true && dialog.SelectedItem is not null) AddItem(dialog.SelectedItem);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            SetBusy(false);
            ItemSearchBox.SelectAll();
            ItemSearchBox.Focus();
        }
    }

    private void AddItem(SalesItemDto item)
    {
        if (item.AvailableStock <= 0)
        {
            MessageBox.Show($"Stok {item.Code} di gudang {item.WarehouseCode} sudah habis.", "AKSARA", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var existing = _lines.FirstOrDefault(line => string.Equals(line.ItemCode, item.Code, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (existing.Quantity + 1 > existing.AvailableStock)
            {
                MessageBox.Show($"Stok {item.Code} hanya {item.AvailableStock:N2}.", "AKSARA", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            existing.Quantity += 1;
        }
        else
        {
            var line = new GunggunganEntryLine(item.Code, item.Name, item.Unit, item.AvailableStock, item.RetailPrice);
            line.PropertyChanged += Line_PropertyChanged;
            _lines.Add(line);
            LinesGrid.SelectedItem = line;
        }

        WarehouseCombo.IsEnabled = false;
        ItemSearchBox.Clear();
        UpdateTotals();
    }

    private void Line_PropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateTotals();

    private void LinesGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(UpdateTotals);

    private void RemoveLine_Click(object sender, RoutedEventArgs e)
    {
        if (_saved || LinesGrid.SelectedItem is not GunggunganEntryLine line) return;
        line.PropertyChanged -= Line_PropertyChanged;
        _lines.Remove(line);
        WarehouseCombo.IsEnabled = _lines.Count == 0;
        UpdateTotals();
    }

    private void CashReceivedBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateTotals();

    private decimal Total => _lines.Sum(line => line.Total);

    private void UpdateTotals()
    {
        var total = Total;
        var paid = ParseDecimal(CashReceivedBox.Text);
        TotalBox.Text = total.ToString("N2", Culture);
        ChangeBox.Text = Math.Max(0, paid - total).ToString("N2", Culture);
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    private async Task<bool> SaveAsync()
    {
        if (_busy || _saved) return false;
        LinesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        LinesGrid.CommitEdit(DataGridEditingUnit.Row, true);

        try
        {
            var warehouse = SelectedWarehouse() ?? throw new InvalidOperationException("Gudang wajib dipilih.");
            var cashAccount = SelectedCashAccount() ?? throw new InvalidOperationException("Akun kas wajib dipilih.");
            if (_lines.Count == 0) throw new InvalidOperationException("Minimal satu barang harus dipilih.");
            if (_lines.Any(line => line.Quantity <= 0 || line.UnitPrice < 0))
                throw new InvalidOperationException("Quantity dan harga barang belum valid.");
            var overStock = _lines.FirstOrDefault(line => line.Quantity > line.AvailableStock);
            if (overStock is not null)
                throw new InvalidOperationException($"Stok {overStock.ItemCode} tidak cukup. Diminta {overStock.Quantity:N2}, tersedia {overStock.AvailableStock:N2}.");

            var cashReceived = ParseDecimal(CashReceivedBox.Text);
            if (cashReceived < Total)
                throw new InvalidOperationException($"Pembayaran kurang {Math.Max(0, Total - cashReceived).ToString("N2", Culture)}.");

            var request = new CreateGunggunganSaleRequest(
                DateOnly.FromDateTime(DocumentDatePicker.SelectedDate ?? DateTime.Today),
                NullIfBlank(DocumentNumberBox.Text),
                cashAccount.Code,
                warehouse.Code,
                cashReceived,
                NullIfBlank(NotesBox.Text),
                _api.CurrentUserName,
                Environment.MachineName,
                _lines.Select(line => new CreateSaleLineRequest(line.ItemCode, line.Quantity, line.UnitPrice, 0m, warehouse.Code)).ToArray());

            SetBusy(true, "Menyimpan, mengurangi stok, dan membentuk jurnal...");
            _savedResult = await _api.CreateGunggunganSaleAsync(request);
            _saved = true;
            LocalTransactionDraftStore.Delete(LocalDraftKey);
            SetEntryReadOnly(true);
            StatusText.Text = $"Nota {_savedResult.Sale.DocumentNumber} tersimpan. Stok, HPP, kas, dan jurnal sudah diperbarui.";
            await LoadReportAsync();
            MessageBox.Show(
                $"Nota {_savedResult.Sale.DocumentNumber} berhasil disimpan.\nKembalian {_savedResult.Change.ToString("N2", Culture)}.",
                "AKSARA - Penjualan Gunggungan",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return true;
        }
        catch (Exception exception)
        {
            ShowError(exception);
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Add_Click(object sender, RoutedEventArgs e) => await StartNewEntryAsync();

    private async void Cancel_Click(object sender, RoutedEventArgs e) => await StartNewEntryAsync();

    private async Task StartNewEntryAsync()
    {
        if (!_saved && !ConfirmPendingDraft()) return;

        foreach (var line in _lines) line.PropertyChanged -= Line_PropertyChanged;
        _lines.Clear();
        _savedResult = null;
        _saved = false;
        NotesBox.Clear();
        CashReceivedBox.Clear();
        SetEntryReadOnly(false);
        WarehouseCombo.IsEnabled = true;
        DocumentDatePicker.SelectedDate = DateTime.Today;
        _historyIndex = -1;
        MainTabs.SelectedIndex = 0;
        UpdateTotals();
        UpdateModeCounterAndActions();
        try { await ReloadNextNumberAsync(); }
        catch (Exception exception) { ShowError(exception); }
        ItemSearchBox.Focus();
    }

    private void PrintReceipt_Click(object sender, RoutedEventArgs e)
    {
        if (_savedResult is null) return;
        GunggunganPrintOutput.PreviewReceipt(_savedResult, Window.GetWindow(this));
    }

    private async void SavePrint_Click(object sender, RoutedEventArgs e)
    {
        if (!_saved && !await SaveAsync()) return;
        PrintReceipt_Click(sender, e);
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 1;
        StatusText.Text = "Pilih transaksi pada riwayat, lalu klik Buka Data atau klik dua kali barisnya.";
        ReportGrid.Focus();
    }

    private async void LoadReport_Click(object sender, RoutedEventArgs e) => await LoadReportAsync();

    private async Task LoadReportAsync()
    {
        try
        {
            var from = DateOnly.FromDateTime(FromDatePicker.SelectedDate ?? DateTime.Today);
            var to = DateOnly.FromDateTime(ToDatePicker.SelectedDate ?? DateTime.Today);
            if (from > to) throw new InvalidOperationException("Tanggal awal tidak boleh melewati tanggal akhir.");
            SetBusy(true, "Memuat riwayat Penjualan Gunggungan...");
            _report = await _api.GetGunggunganSalesAsync(from, to);
            ReportGrid.ItemsSource = _report.Rows.Select(row => new GunggunganReportDisplayRow(row)).ToArray();
            _historySaleIds = _report.Rows.Select(row => row.SaleId).Distinct().ToArray();
            SyncHistoryIndex();
            ReportTransactionCountText.Text = $"{_report.TransactionCount:N0} transaksi";
            ReportQuantityText.Text = $"{_report.TotalQuantity.ToString("N2", Culture)} unit";
            ReportSalesText.Text = $"Total {_report.TotalSales.ToString("N2", Culture)}";
            StatusText.Text = _report.Rows.Count == 0
                ? "Belum ada Penjualan Gunggungan pada periode tersebut."
                : $"{_report.Rows.Count:N0} rincian ditemukan. Transaksi bertanda hapus tidak masuk total.";
            UpdateHistoryActions();
            UpdateModeCounterAndActions();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void TodayPreset_Click(object sender, RoutedEventArgs e) => await ApplyPresetAsync(DateTime.Today, DateTime.Today);
    private async void SevenDaysPreset_Click(object sender, RoutedEventArgs e) => await ApplyPresetAsync(DateTime.Today.AddDays(-6), DateTime.Today);
    private async void OneMonthPreset_Click(object sender, RoutedEventArgs e) => await ApplyPresetAsync(DateTime.Today.AddMonths(-1), DateTime.Today);
    private async void ThisMonthPreset_Click(object sender, RoutedEventArgs e) => await ApplyPresetAsync(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), DateTime.Today);

    private async Task ApplyPresetAsync(DateTime from, DateTime to)
    {
        FromDatePicker.SelectedDate = from;
        ToDatePicker.SelectedDate = to;
        await LoadReportAsync();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_savedResult?.Sale is not { Status: "POSTED" } sale) return;
        var answer = MessageBox.Show("Apakah anda ingin menandai hapus data ini?", "AKSARA", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            SetBusy(true, "Menandai transaksi sebagai dihapus...");
            var cancelled = await _api.CancelSaleAsync(sale.Id, new CancelSaleRequest(
                "Ditandai hapus dari Penjualan Gunggungan.", _api.CurrentUserName, Environment.MachineName));
            _savedResult = _savedResult with { Sale = cancelled };
            await LoadReportAsync();
            UpdateModeCounterAndActions();
        }
        catch (Exception exception) { ShowError(exception); }
        finally { SetBusy(false); }
    }

    private async void OpenSelected_Click(object sender, RoutedEventArgs e) => await OpenSelectedAsync();

    private async void ReportGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await OpenSelectedAsync();

    private async Task OpenSelectedAsync()
    {
        if (ReportGrid.SelectedItem is not GunggunganReportDisplayRow row) return;
        await LoadHistorySaleAsync(row.SaleId);
    }

    private async void PrintSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ReportGrid.SelectedItem is not GunggunganReportDisplayRow { Status: "POSTED" } row) return;
        try
        {
            SetBusy(true, "Memuat nota terpilih...");
            var result = await _api.GetGunggunganSaleAsync(row.SaleId);
            GunggunganPrintOutput.PreviewReceipt(result, Window.GetWindow(this));
        }
        catch (Exception exception) { ShowError(exception); }
        finally { SetBusy(false); }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_savedResult?.Sale is not { Status: "CANCELLED" } sale) return;
        var answer = MessageBox.Show("Apakah anda ingin membatalkan hapus data?", "AKSARA", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            SetBusy(true, "Memulihkan transaksi...");
            var restored = await _api.RestoreSaleAsync(sale.Id, new RestoreSaleRequest(_api.CurrentUserName, Environment.MachineName));
            _savedResult = _savedResult with { Sale = restored };
            await LoadReportAsync();
            UpdateModeCounterAndActions();
        }
        catch (Exception exception) { ShowError(exception); }
        finally { SetBusy(false); }
    }

    private void PrintRange_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null || !_report.Rows.Any(row => row.Status == "POSTED"))
        {
            MessageBox.Show("Tidak ada transaksi aktif pada rentang ini untuk dicetak.", "AKSARA", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        GunggunganPrintOutput.PreviewRange(_report, Window.GetWindow(this));
    }

    private void ExportRange_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        var rows = _report.Rows.Where(row => row.Status == "POSTED").Select(row => (object)new GunggunganReportDisplayRow(row)).ToArray();
        ReportGridTools.ExportObjectsToExcel("Penjualan Gunggungan", rows);
    }

    private async void NavigateFirst_Click(object sender, RoutedEventArgs e) => await LoadHistoryAtAsync(0);
    private async void NavigatePrevious_Click(object sender, RoutedEventArgs e) => await MoveHistoryAsync(-1);
    private void NavigateLookup_Click(object sender, RoutedEventArgs e) => Search_Click(sender, e);
    private async void NavigateNext_Click(object sender, RoutedEventArgs e) => await MoveHistoryAsync(1);
    private async void NavigateLast_Click(object sender, RoutedEventArgs e) => await LoadHistoryAtAsync(_historySaleIds.Count - 1);

    private async Task MoveHistoryAsync(int offset)
    {
        if (_historySaleIds.Count == 0) await LoadReportAsync();
        if (_historySaleIds.Count == 0)
        {
            StatusText.Text = "Belum ada Penjualan Gunggungan pada periode riwayat yang dipilih.";
            return;
        }

        var nextIndex = _historyIndex < 0
            ? offset < 0 ? _historySaleIds.Count - 1 : 0
            : _historyIndex + offset;
        await LoadHistoryAtAsync(nextIndex);
    }

    private async Task LoadHistoryAtAsync(int index)
    {
        if (_historySaleIds.Count == 0) await LoadReportAsync();
        if (_historySaleIds.Count == 0)
        {
            StatusText.Text = "Belum ada Penjualan Gunggungan pada periode riwayat yang dipilih.";
            return;
        }

        index = Math.Clamp(index, 0, _historySaleIds.Count - 1);
        await LoadHistorySaleAsync(_historySaleIds[index]);
    }

    private async Task LoadHistorySaleAsync(Guid saleId)
    {
        if (!_saved && !ConfirmPendingDraft()) return;

        try
        {
            SetBusy(true, "Memuat data Penjualan Gunggungan...");
            var result = await _api.GetGunggunganSaleAsync(saleId);
            await ApplySavedResultAsync(result);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ApplySavedResultAsync(GunggunganSaleResultDto result)
    {
        _savedResult = result;
        _saved = true;
        DocumentDatePicker.SelectedDate = result.Sale.DocumentDate.ToDateTime(TimeOnly.MinValue);
        DocumentNumberBox.Text = result.Sale.DocumentNumber;
        WarehouseCombo.SelectedItem = WarehouseCombo.Items.Cast<InventoryWarehouseDto>()
            .FirstOrDefault(item => string.Equals(item.Code, result.Sale.WarehouseCode, StringComparison.OrdinalIgnoreCase));
        CashAccountCombo.SelectedItem = CashAccountCombo.Items.Cast<CashAccountDto>()
            .FirstOrDefault(item => string.Equals(item.Code, result.Sale.CashAccountCode, StringComparison.OrdinalIgnoreCase));
        CashReceivedBox.Text = result.CashReceived.ToString("N2", Culture);
        NotesBox.Text = result.Sale.Notes ?? string.Empty;

        foreach (var line in _lines) line.PropertyChanged -= Line_PropertyChanged;
        _lines.Clear();
        var currentItems = await _api.SearchItemsAsync(string.Empty, result.Sale.WarehouseCode);
        foreach (var source in result.Sale.Lines)
        {
            var item = currentItems.FirstOrDefault(candidate =>
                string.Equals(candidate.Code, source.ItemCode, StringComparison.OrdinalIgnoreCase));
            var line = new GunggunganEntryLine(
                source.ItemCode,
                source.ItemName,
                source.Unit,
                item?.AvailableStock ?? 0m,
                source.UnitPrice)
            {
                Quantity = source.Quantity
            };
            line.PropertyChanged += Line_PropertyChanged;
            _lines.Add(line);
        }

        SetEntryReadOnly(true);
        SyncHistoryIndex();
        UpdateTotals();
        UpdateModeCounterAndActions();
        MainTabs.SelectedIndex = 0;
        StatusText.Text = $"Data {_historyIndex + 1:N0}/{_historySaleIds.Count:N0}: {result.Sale.DocumentNumber} | {result.Sale.DocumentDate:dd/MM/yyyy}.";
    }

    private void SyncHistoryIndex()
    {
        _historyIndex = _savedResult is null
            ? -1
            : _historySaleIds.ToList().FindIndex(id => id == _savedResult.Sale.Id);
    }

    private void UpdateModeCounterAndActions()
    {
        ModeStatusText.Text = _savedResult?.Sale.Status == "CANCELLED"
            ? "DITANDAI HAPUS"
            : _savedResult is null ? "TAMBAH DATA" : "EDIT DATA";
        ServerStatusText.Text = _savedResult is null || _historyIndex < 0
            ? $"TOTAL DATA {_historySaleIds.Count:N0}"
            : $"DATA {_historyIndex + 1:N0} / {_historySaleIds.Count:N0}";
        SaveButton.IsEnabled = !_saved;
        SavePrintButton.IsEnabled = true;
        DeleteButton.IsEnabled = _savedResult?.Sale.Status == "POSTED";
        RestoreButton.IsEnabled = _savedResult?.Sale.Status == "CANCELLED";
    }

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmCloseAsync()) return;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateHistoryActions()
    {
        var selected = ReportGrid.SelectedItem as GunggunganReportDisplayRow;
        OpenSelectedButton.IsEnabled = selected is not null;
        PrintSelectedButton.IsEnabled = selected?.Status == "POSTED";
    }

    private bool ConfirmPendingDraft()
    {
        LinesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        LinesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (_lines.Count == 0 && string.IsNullOrWhiteSpace(NotesBox.Text) && ParseDecimal(CashReceivedBox.Text) == 0) return true;
        var choice = LocalDraftPromptWindow.Show(Window.GetWindow(this), "Penjualan Gunggungan");
        if (choice == LocalDraftChoice.KeepEditing) return false;
        if (choice == LocalDraftChoice.SaveDraft)
        {
            SaveLocalDraft();
            return true;
        }
        LocalTransactionDraftStore.Delete(LocalDraftKey);
        return true;
    }

    private void SaveLocalDraft()
    {
        LocalTransactionDraftStore.Save(LocalDraftKey, new GunggunganLocalDraft(
            DocumentDatePicker.SelectedDate,
            (WarehouseCombo.SelectedItem as InventoryWarehouseDto)?.Code,
            (CashAccountCombo.SelectedItem as CashAccountDto)?.Code,
            CashReceivedBox.Text,
            NotesBox.Text,
            _lines.Select(line => new GunggunganLocalDraftLine(
                line.ItemCode, line.ItemName, line.Unit, line.AvailableStock, line.Quantity, line.UnitPrice)).ToArray()));
    }

    private void RestoreLocalDraft()
    {
        if (!LocalTransactionDraftStore.TryLoad<GunggunganLocalDraft>(LocalDraftKey, out var draft) || draft is null) return;
        DocumentDatePicker.SelectedDate = draft.DocumentDate ?? DateTime.Today;
        WarehouseCombo.SelectedItem = WarehouseCombo.Items.Cast<InventoryWarehouseDto>()
            .FirstOrDefault(item => string.Equals(item.Code, draft.WarehouseCode, StringComparison.OrdinalIgnoreCase)) ?? WarehouseCombo.SelectedItem;
        CashAccountCombo.SelectedItem = CashAccountCombo.Items.Cast<CashAccountDto>()
            .FirstOrDefault(item => string.Equals(item.Code, draft.CashAccountCode, StringComparison.OrdinalIgnoreCase)) ?? CashAccountCombo.SelectedItem;
        CashReceivedBox.Text = draft.CashReceived;
        NotesBox.Text = draft.Notes;
        foreach (var item in draft.Lines)
        {
            var line = new GunggunganEntryLine(item.ItemCode, item.ItemName, item.Unit, item.AvailableStock, item.UnitPrice)
            {
                Quantity = item.Quantity
            };
            line.PropertyChanged += Line_PropertyChanged;
            _lines.Add(line);
        }
        WarehouseCombo.IsEnabled = _lines.Count == 0;
        StatusText.Text = "Draft lokal Penjualan Gunggungan dipulihkan.";
        UpdateTotals();
    }

    private void SetEntryReadOnly(bool value)
    {
        DocumentDatePicker.IsEnabled = !value;
        WarehouseCombo.IsEnabled = !value && _lines.Count == 0;
        CashAccountCombo.IsEnabled = !value;
        ItemSearchBox.IsEnabled = !value;
        LinesGrid.IsReadOnly = value;
        NotesBox.IsReadOnly = value;
        CashReceivedBox.IsReadOnly = value;
        UpdateModeCounterAndActions();
    }

    private void SetBusy(bool value, string? message = null)
    {
        _busy = value;
        if (!string.IsNullOrWhiteSpace(message)) StatusText.Text = message;
        if (value) ModeStatusText.Text = "MEMPROSES...";
        else UpdateModeCounterAndActions();
        Mouse.OverrideCursor = value ? Cursors.Wait : null;
    }

    private InventoryWarehouseDto? SelectedWarehouse() => WarehouseCombo.SelectedItem as InventoryWarehouseDto;
    private CashAccountDto? SelectedCashAccount() => CashAccountCombo.SelectedItem as CashAccountDto;

    private static decimal ParseDecimal(string? value)
    {
        if (decimal.TryParse(value, NumberStyles.Number, Culture, out var result)) return result;
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out result) ? result : 0m;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ShowError(Exception exception) =>
        ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Penjualan Gunggungan");

    private sealed record GunggunganLocalDraft(
        DateTime? DocumentDate,
        string? WarehouseCode,
        string? CashAccountCode,
        string CashReceived,
        string Notes,
        IReadOnlyList<GunggunganLocalDraftLine> Lines);

    private sealed record GunggunganLocalDraftLine(
        string ItemCode,
        string ItemName,
        string Unit,
        decimal AvailableStock,
        decimal Quantity,
        decimal UnitPrice);
}

internal sealed class GunggunganEntryLine : INotifyPropertyChanged
{
    private decimal _quantity = 1m;
    private decimal _unitPrice;

    public GunggunganEntryLine(string itemCode, string itemName, string unit, decimal availableStock, decimal unitPrice)
    {
        ItemCode = itemCode;
        ItemName = itemName;
        Unit = unit;
        AvailableStock = availableStock;
        _unitPrice = unitPrice;
    }

    public string ItemCode { get; }
    public string ItemName { get; }
    public string Unit { get; }
    public decimal AvailableStock { get; }
    public decimal Quantity { get => _quantity; set { if (_quantity == value) return; _quantity = value; Changed(); Changed(nameof(Total)); } }
    public decimal UnitPrice { get => _unitPrice; set { if (_unitPrice == value) return; _unitPrice = value; Changed(); Changed(nameof(Total)); } }
    public decimal Total => Math.Round(Quantity * UnitPrice, 2);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed record GunggunganReportDisplayRow(GunggunganSaleReportRowDto Source)
{
    public Guid SaleId => Source.SaleId;
    public DateOnly DocumentDate => Source.DocumentDate;
    public string DocumentNumber => Source.DocumentNumber;
    public string WarehouseCode => Source.WarehouseCode;
    public string CashAccountName => Source.CashAccountName;
    public string ItemCode => Source.ItemCode;
    public string ItemName => Source.ItemName;
    public decimal Quantity => Source.Quantity;
    public string Unit => Source.Unit;
    public decimal UnitPrice => Source.UnitPrice;
    public decimal LineTotal => Source.LineTotal;
    public string Status => Source.Status;
    public string StatusLabel => Status == "CANCELLED" ? "DITANDAI HAPUS" : "AKTIF";
}
