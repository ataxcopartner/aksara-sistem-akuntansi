using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Aksara.Contracts;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

public enum InventoryTransactionKind { Transfer, Adjustment, StockCount, MbapSend, MbapReceive }

public sealed class InventoryTransactionView : UserControl
{
    private readonly AksaraApiClient _api = AksaraApiClient.Shared; private readonly InventoryTransactionKind _kind;
    private readonly TextBox _query = new() { MinWidth = 230 }; private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, RowHeight = 25, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    public event EventHandler? CloseRequested;
    public string ViewTitle => _kind switch { InventoryTransactionKind.Transfer => "Mutasi Barang", InventoryTransactionKind.Adjustment => "Penyesuaian Persediaan", InventoryTransactionKind.StockCount => "Stok Fisik", InventoryTransactionKind.MbapSend => "MBAP Kirim", _ => "MBAP Terima" };
    private string TypeCode => _kind switch { InventoryTransactionKind.Transfer => "TRANSFER", InventoryTransactionKind.Adjustment => "ADJUSTMENT", InventoryTransactionKind.StockCount => "STOCK_COUNT", InventoryTransactionKind.MbapSend => "MBAP_SEND", _ => "MBAP_RECEIVE" };

    public InventoryTransactionView(InventoryTransactionKind kind) { _kind = kind; Content = Build(); ConfigureColumns(); Loaded += async (_, _) => await ReloadAsync(); _grid.MouseDoubleClick += async (_, _) => await ViewAsync(); _grid.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; await ViewAsync(); } }; }
    private UIElement Build()
    {
        var root = new Grid { Margin = new Thickness(6), Background = Brushes.White }; root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid { Height = 43, Background = Brush("#ECECEC"), Margin = new Thickness(0, 0, 0, 1) }; header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(205) }); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(165) }); header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
        header.Children.Add(new CheckBox { Content = "Exclusive Mode", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) }); var headerStatus = new TextBlock { Text = "Status:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) }; Grid.SetColumn(headerStatus, 1); header.Children.Add(headerStatus); var mode = new TextBlock { Text = "TAMBAH DATA", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(mode, 2); header.Children.Add(mode); var back = new Button { Content = "|||", Width = 42, Height = 30, Margin = new Thickness(8, 6, 8, 6), Padding = new Thickness(0), ToolTip = "Kembali ke tampilan awal" }; back.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty); Grid.SetColumn(back, 3); header.Children.Add(back); root.Children.Add(header);
        var tools = new Grid { Margin = new Thickness(0, 6, 0, 6) }; tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); tools.ColumnDefinitions.Add(new ColumnDefinition()); for (var i = 0; i < 5; i++) tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); tools.Children.Add(new TextBlock { Text = "Cari No. Bukti", VerticalAlignment = VerticalAlignment.Center }); _query.Margin = new Thickness(8, 0, 8, 0); Grid.SetColumn(_query, 1); tools.Children.Add(_query);
        var find = Button("Cari"); find.Click += async (_, _) => await ReloadAsync(); Add(tools, find, 2); var add = Button("Tambah"); add.Click += async (_, _) => await AddAsync(); Add(tools, add, 3); var cancel = Button("Btl./Hapus"); cancel.Click += async (_, _) => await CancelAsync(); Add(tools, cancel, 4); var refresh = Button("Refresh"); refresh.Click += async (_, _) => await ReloadAsync(); Add(tools, refresh, 5); Grid.SetRow(tools, 1); root.Children.Add(tools);
        Grid.SetRow(_grid, 2); root.Children.Add(_grid); var footer = new Grid { Margin = new Thickness(0, 6, 0, 0) }; footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); footer.Children.Add(_status); var close = Button("Tutup"); close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty); Grid.SetColumn(close, 1); footer.Children.Add(close); Grid.SetRow(footer, 3); root.Children.Add(footer); return root;
    }
    private void ConfigureColumns() { _grid.Columns.Add(Column("Tgl. Bukti", "DocumentDate", 110)); _grid.Columns.Add(Column("No. Bukti", "DocumentNumber", 145)); _grid.Columns.Add(Column("Status", "Status", 100)); _grid.Columns.Add(Column("Gudang Asal", "SourceWarehouseCode", 145)); _grid.Columns.Add(Column("Gudang Tujuan", "DestinationWarehouseCode", 145)); _grid.Columns.Add(Column("Keterangan", "Counterparty", 260)); _grid.Columns.Add(NumberColumn("Jumlah", "TotalQuantity", 120)); _grid.Columns.Add(NumberColumn("Nilai", "TotalAmount", 150)); }
    private async Task ReloadAsync() { try { IsEnabled = false; var rows = await _api.SearchInventoryDocumentsAsync(TypeCode, _query.Text); _grid.ItemsSource = rows; _status.Text = $"{rows.Count:N0} dokumen {ViewTitle}."; } catch (Exception exception) { _status.Text = exception.Message; } finally { IsEnabled = true; } }
    private async Task AddAsync() { var editor = new InventoryDocumentEditorWindow(_kind) { Owner = Window.GetWindow(this) }; if (editor.ShowDialog() == true) await ReloadAsync(); }
    private async Task ViewAsync() { if (_grid.SelectedItem is not InventoryDocumentSummaryDto selected) return; try { IsEnabled = false; var document = await GetDocumentAsync(selected.Id); new InventoryDocumentDetailWindow(ViewTitle, document) { Owner = Window.GetWindow(this) }.ShowDialog(); } catch (Exception exception) { MessageBox.Show(exception.Message, ViewTitle, MessageBoxButton.OK, MessageBoxImage.Error); } finally { IsEnabled = true; } }
    private async Task CancelAsync()
    {
        if (_grid.SelectedItem is not InventoryDocumentSummaryDto selected || selected.Status == "CANCELLED") return; var reason = InventoryTextPrompt.Show("Alasan pembatalan", "Batalkan Dokumen", Window.GetWindow(this)); if (string.IsNullOrWhiteSpace(reason)) return;
        try { IsEnabled = false; var request = new CancelInventoryDocumentRequest(reason, AksaraApiClient.Shared.CurrentUserName, Environment.MachineName); switch (_kind) { case InventoryTransactionKind.Transfer: await _api.CancelInventoryTransferAsync(selected.Id, request); break; case InventoryTransactionKind.Adjustment: await _api.CancelInventoryAdjustmentAsync(selected.Id, request); break; case InventoryTransactionKind.StockCount: await _api.CancelInventoryStockCountAsync(selected.Id, request); break; case InventoryTransactionKind.MbapSend: await _api.CancelInventoryMbapSendAsync(selected.Id, request); break; default: await _api.CancelInventoryMbapReceiveAsync(selected.Id, request); break; } await ReloadAsync(); }
        catch (Exception exception) { MessageBox.Show(exception.Message, ViewTitle, MessageBoxButton.OK, MessageBoxImage.Error); } finally { IsEnabled = true; }
    }
    private Task<object> GetDocumentAsync(Guid id) => _kind switch { InventoryTransactionKind.Transfer => Cast(_api.GetInventoryTransferAsync(id)), InventoryTransactionKind.Adjustment => Cast(_api.GetInventoryAdjustmentAsync(id)), InventoryTransactionKind.StockCount => Cast(_api.GetInventoryStockCountAsync(id)), InventoryTransactionKind.MbapSend => Cast(_api.GetInventoryMbapSendAsync(id)), _ => Cast(_api.GetInventoryMbapReceiveAsync(id)) };
    private static async Task<object> Cast<T>(Task<T> task) => (await task)!;
    private static void Add(Grid grid, UIElement child, int column) { Grid.SetColumn(child, column); grid.Children.Add(child); }
    private static Button Button(string text) => new() { Content = text, Width = 92, Margin = new Thickness(3, 0, 0, 0) };
    private static DataGridTextColumn Column(string title, string path, DataGridLength width) => new() { Header = title, Binding = new Binding(path), Width = width };
    private static DataGridTextColumn NumberColumn(string title, string path, double width) => new() { Header = title, Binding = new Binding(path) { StringFormat = "N2" }, Width = width, ElementStyle = RightStyle() };
    internal static Style RightStyle() => new(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right) } };
    internal static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
}

internal sealed class InventoryDocumentEditorWindow : Window
{
    private readonly AksaraApiClient _api = AksaraApiClient.Shared; private readonly InventoryTransactionKind _kind;
    private readonly DatePicker _date = new() { SelectedDate = DateTime.Today }; private readonly ComboBox _source = new() { DisplayMemberPath = "Name", SelectedValuePath = "Code" }; private readonly ComboBox _destination = new() { DisplayMemberPath = "Name", SelectedValuePath = "Code" }; private readonly ComboBox _adjustment = new() { DisplayMemberPath = "Name", SelectedValuePath = "Code" }; private readonly ComboBox _businessUnit = new() { DisplayMemberPath = "Name", SelectedValuePath = "Code" }; private readonly ComboBox _mbapSource = new() { DisplayMemberPath = "DocumentNumber", SelectedValuePath = "Id" };
    private readonly DatePicker _settlement = new(); private readonly TextBox _bank = new(); private readonly TextBox _notes = new() { AcceptsReturn = true, Height = 50 }; private readonly TextBox _documentNumber = new() { Text = "OTOMATIS", IsReadOnly = true }; private readonly TextBlock _totalQuantity = new() { Text = "0", TextAlignment = TextAlignment.Right }; private readonly TextBlock _totalAmount = new() { Text = "0", TextAlignment = TextAlignment.Right }; private readonly DataGrid _linesGrid = new() { AutoGenerateColumns = false, CanUserAddRows = true, RowHeight = 25 };
    private DataGridComboBoxColumn? _itemColumn;
    private IReadOnlyDictionary<string, InventoryItemChoice> _itemChoices = new Dictionary<string, InventoryItemChoice>(StringComparer.OrdinalIgnoreCase); private readonly TextBlock _status = new(); public ObservableCollection<InventoryEntryLine> Lines { get; } = [];
    public InventoryDocumentEditorWindow(InventoryTransactionKind kind) { _kind = kind; Title = $"Tambah {TitleFor(kind)} - AKSARA"; Width = kind == InventoryTransactionKind.Transfer ? 1120 : 920; Height = kind == InventoryTransactionKind.Transfer ? 720 : 650; MinWidth = 780; MinHeight = 560; WindowStartupLocation = WindowStartupLocation.CenterOwner; Content = Build(); Loaded += async (_, _) => await LoadLookupsAsync(); }
    private UIElement Build()
    {
        if (_kind == InventoryTransactionKind.Transfer) return BuildTransfer();
        var root = new Grid { Margin = new Thickness(8) }; root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(232, 237, 243)), Padding = new Thickness(10, 7, 10, 7), Child = new TextBlock { Text = TitleFor(_kind).ToUpperInvariant(), FontSize = 15, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(24, 59, 86)) } });
        var header = new Grid { Margin = new Thickness(0, 7, 0, 7) }; for (var i = 0; i < 4; i++) header.ColumnDefinitions.Add(new ColumnDefinition { Width = i % 2 == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star) }); for (var i = 0; i < 4; i++) header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddField(header, "Tgl. Bukti", _date, 0, 0); var row = 1;
        if (_kind == InventoryTransactionKind.Transfer) { AddField(header, "Gudang Asal", _source, row, 0); AddField(header, "Gudang Tujuan", _destination, row, 2); row++; }
        else if (_kind == InventoryTransactionKind.Adjustment) { AddField(header, "Jenis", _adjustment, row, 0); AddField(header, "Gudang", _source, row, 2); row++; }
        else if (_kind == InventoryTransactionKind.StockCount) { AddField(header, "Gudang", _source, row, 0); row++; }
        else if (_kind == InventoryTransactionKind.MbapSend) { AddField(header, "Gudang Asal", _source, row, 0); AddField(header, "Ke Wil. Usaha", _businessUnit, row, 2); row++; AddField(header, "Tgl. Lunas", _settlement, row, 0); AddField(header, "Kode Bank", _bank, row, 2); row++; }
        else { AddField(header, "Sumber MBAP Kirim", _mbapSource, row, 0); AddField(header, "Gudang Penerima", _destination, row, 2); row++; _mbapSource.SelectionChanged += async (_, _) => await LoadMbapSourceAsync(); }
        AddField(header, "Catatan", _notes, row, 0, span: 3); Grid.SetRow(header, 1); root.Children.Add(header);
        ConfigureLines(); _linesGrid.ItemsSource = Lines; Grid.SetRow(_linesGrid, 2); root.Children.Add(_linesGrid);
        var lineTools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 5, 0, 0) }; if (_kind != InventoryTransactionKind.MbapReceive) { var add = new Button { Content = "Tambah Baris", Width = 105, Margin = new Thickness(4) }; add.Click += (_, _) => Lines.Add(new()); lineTools.Children.Add(add); } var remove = new Button { Content = "Hapus Baris", Width = 105, Margin = new Thickness(4) }; remove.Click += (_, _) => { if (_linesGrid.SelectedItem is InventoryEntryLine line) Lines.Remove(line); }; lineTools.Children.Add(remove); Grid.SetRow(lineTools, 3); root.Children.Add(lineTools);
        var footer = new Grid { Margin = new Thickness(0, 7, 0, 0) }; footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); footer.Children.Add(_status); var save = new Button { Content = "Simpan", Width = 105, Margin = new Thickness(4) }; save.Click += async (_, _) => await SaveAsync(); Grid.SetColumn(save, 1); footer.Children.Add(save); var close = new Button { Content = "Tutup", Width = 105, Margin = new Thickness(4) }; close.Click += (_, _) => Close(); Grid.SetColumn(close, 2); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); footer.Children.Add(close); Grid.SetRow(footer, 4); root.Children.Add(footer); return root;
    }
    private UIElement BuildTransfer()
    {
        var root = new Grid { Margin = new Thickness(6), Background = Brushes.White };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid { Height = 43, Background = InventoryTransactionView.Brush("#ECECEC"), Margin = new Thickness(0, 0, 0, 1) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(205) }); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(165) }); header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
        header.Children.Add(new CheckBox { Content = "Exclusive Mode", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
        var headerStatus = new TextBlock { Text = "Status:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) }; Grid.SetColumn(headerStatus, 1); header.Children.Add(headerStatus);
        var mode = new TextBlock { Text = "TAMBAH DATA", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(mode, 2); header.Children.Add(mode);
        var back = new Button { Content = "|||", Width = 42, Height = 30, Margin = new Thickness(8, 6, 8, 6), Padding = new Thickness(0), ToolTip = "Kembali" }; back.Click += (_, _) => Close(); Grid.SetColumn(back, 3); header.Children.Add(back); root.Children.Add(header);

        var fields = new Grid { Margin = new Thickness(12, 9, 12, 7) }; fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) }); fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) }); fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) }); fields.ColumnDefinitions.Add(new ColumnDefinition()); fields.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) }); fields.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) }); fields.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        _notes.Height = 28; AddField(fields, "Tgl. Bukti", _date, 0, 0); AddField(fields, "No. Bukti", _documentNumber, 0, 2); AddField(fields, "Lok.(Dari)", _source, 1, 0); AddField(fields, "Lok.(Ke)", _destination, 1, 2); AddField(fields, "Keterangan", _notes, 2, 0, 2); Grid.SetRow(fields, 1); root.Children.Add(fields);

        var tabs = new Grid { Margin = new Thickness(0, 0, 0, 4) }; tabs.ColumnDefinitions.Add(new ColumnDefinition()); tabs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) }); var tab = new TabControl { Height = 40 }; tab.Items.Add(new TabItem { Header = "Rincian" }); tabs.Children.Add(tab); var heading = new Border { BorderBrush = InventoryTransactionView.Brush("#999"), BorderThickness = new Thickness(1), Margin = new Thickness(8, 0, 0, 0), Child = new TextBlock { Text = "MUTASI", Foreground = Brushes.Red, FontSize = 24, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } }; Grid.SetColumn(heading, 1); tabs.Children.Add(heading); Grid.SetRow(tabs, 2); root.Children.Add(tabs);

        ConfigureLines(); _linesGrid.ItemsSource = Lines; _linesGrid.Margin = new Thickness(0, 0, 0, 4); _linesGrid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(RecalculateTotals); Grid.SetRow(_linesGrid, 3); root.Children.Add(_linesGrid);
        var totals = new Grid { Background = InventoryTransactionView.Brush("#F2F2F2"), Margin = new Thickness(0, 0, 0, 4), Height = 40 }; totals.ColumnDefinitions.Add(new ColumnDefinition()); totals.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) }); totals.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) }); totals.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) }); totals.Children.Add(new TextBlock { Text = "", VerticalAlignment = VerticalAlignment.Center }); totals.Children.Add(new TextBlock { Text = "Total Banyak", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right }); Grid.SetColumn(_totalQuantity, 2); totals.Children.Add(_totalQuantity); totals.Children.Add(new TextBlock { Text = "Total Nilai", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0, 0, 0) }); Grid.SetColumn(_totalAmount, 3); totals.Children.Add(_totalAmount); Grid.SetRow(totals, 4); root.Children.Add(totals);
        var footer = new Grid { Margin = new Thickness(0, 2, 0, 0) }; footer.ColumnDefinitions.Add(new ColumnDefinition()); for (var i = 0; i < 8; i++) footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); var nav = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var glyph in new[] { "<<", "<", "•", ">", ">>" }) nav.Children.Add(new Button { Content = glyph, Width = 42, Margin = new Thickness(2), IsEnabled = false }); footer.Children.Add(nav);
        var add = new Button { Content = "Tambah", Width = 86, Margin = new Thickness(3) }; add.Click += (_, _) => ResetTransferDraft(); Grid.SetColumn(add, 1); footer.Children.Add(add); var cancel = new Button { Content = "Batal", Width = 86, Margin = new Thickness(3) }; cancel.Click += (_, _) => Close(); Grid.SetColumn(cancel, 2); footer.Children.Add(cancel); var save = new Button { Content = "Simpan", Width = 86, Margin = new Thickness(3) }; save.Click += async (_, _) => await SaveAsync(); Grid.SetColumn(save, 3); footer.Children.Add(save); var saveContinue = new Button { Content = "Smnp&Ctk", Width = 100, Margin = new Thickness(3), ToolTip = "Simpan dokumen" }; saveContinue.Click += async (_, _) => await SaveAsync(); Grid.SetColumn(saveContinue, 4); footer.Children.Add(saveContinue); var remove = new Button { Content = "Hapus Baris", Width = 100, Margin = new Thickness(3) }; remove.Click += (_, _) => { if (_linesGrid.SelectedItem is InventoryEntryLine line) { Lines.Remove(line); RecalculateTotals(); } }; Grid.SetColumn(remove, 5); footer.Children.Add(remove); var close = new Button { Content = "Tutup", Width = 86, Margin = new Thickness(3) }; close.Click += (_, _) => Close(); Grid.SetColumn(close, 6); footer.Children.Add(close); Grid.SetRow(footer, 5); root.Children.Add(footer); return root;
    }
    private void ResetTransferDraft()
    {
        _date.SelectedDate = DateTime.Today; _notes.Clear(); _documentNumber.Text = "OTOMATIS"; Lines.Clear(); for (var i = 0; i < 8; i++) Lines.Add(new() { WarehouseCode = _source.SelectedValue?.ToString() ?? "" }); RecalculateTotals(); _status.Text = "Form baru siap diisi.";
    }
    private async Task LoadLookupsAsync()
    {
        try { IsEnabled = false; var warehouses = await _api.GetInventoryWarehousesAsync(false); _source.ItemsSource = warehouses; _destination.ItemsSource = warehouses; _source.SelectedIndex = 0; _destination.SelectedIndex = Math.Min(1, warehouses.Count - 1); if (_kind == InventoryTransactionKind.Transfer) _source.SelectionChanged += (_, _) => { foreach (var line in Lines) line.WarehouseCode = _source.SelectedValue?.ToString() ?? ""; RefreshLinesGridSafely(); }; if (_itemColumn is not null) { var items = await _api.SearchInventoryItemsAsync(null, includeInactive: false); _itemChoices = items.ToDictionary(x => x.Code, x => new InventoryItemChoice(x.Code, $"{x.Code} - {x.Name}", x.Name, x.Unit, x.AverageCost), StringComparer.OrdinalIgnoreCase); _itemColumn.ItemsSource = _itemChoices.Values.ToList(); } if (_kind == InventoryTransactionKind.Adjustment) { _adjustment.ItemsSource = await _api.GetInventoryAdjustmentTypesAsync(false); _adjustment.SelectedIndex = 0; } if (_kind == InventoryTransactionKind.MbapSend) { _businessUnit.ItemsSource = await _api.GetInventoryBusinessUnitsAsync(); _businessUnit.SelectedIndex = 0; } if (_kind == InventoryTransactionKind.MbapReceive) { var docs = (await _api.SearchInventoryDocumentsAsync("MBAP_SEND", null)).Where(x => x.Status is "POSTED" or "PARTIAL").ToList(); _mbapSource.ItemsSource = docs; _mbapSource.SelectedIndex = docs.Count > 0 ? 0 : -1; } if (_kind != InventoryTransactionKind.MbapReceive) for (var i = 0; i < 8; i++) Lines.Add(new() { WarehouseCode = _source.SelectedValue?.ToString() ?? "" }); RecalculateTotals(); _status.Text = "No. bukti dibuat otomatis saat disimpan."; }
        catch (Exception exception) { _status.Text = exception.Message; }
        finally { IsEnabled = true; }
    }
    private async Task LoadMbapSourceAsync()
    {
        if (_mbapSource.SelectedValue is not Guid id) return; try { var source = await _api.GetInventoryMbapReceiveSourceAsync(id); Lines.Clear(); foreach (var line in source.Lines) Lines.Add(new() { SendLineId = line.SendLineId, ItemCode = line.ItemCode, ItemName = line.ItemName, Quantity = line.RemainingQuantity, RemainingQuantity = line.RemainingQuantity, UnitCost = line.UnitCost }); _status.Text = $"Sumber {source.SendNumber}, sisa {source.Lines.Sum(x => x.RemainingQuantity):N3}."; } catch (Exception exception) { _status.Text = exception.Message; }
    }
    private void ConfigureLines()
    {
        if (_kind == InventoryTransactionKind.Transfer) { _itemColumn = new DataGridComboBoxColumn { Header = "Kode Barang", SelectedValueBinding = new Binding(nameof(InventoryEntryLine.ItemCode)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, SelectedValuePath = nameof(InventoryItemChoice.Code), DisplayMemberPath = nameof(InventoryItemChoice.Display), Width = 145 }; _linesGrid.Columns.Add(new DataGridTextColumn { Header = "Lok.(Dari)", Binding = new Binding(nameof(InventoryEntryLine.WarehouseCode)), Width = 95, IsReadOnly = true }); _linesGrid.Columns.Add(_itemColumn); _linesGrid.Columns.Add(new DataGridTextColumn { Header = "Nama Barang", Binding = new Binding(nameof(InventoryEntryLine.ItemName)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true }); _linesGrid.Columns.Add(NumberEditableColumn("Byk.Koli", nameof(InventoryEntryLine.KoliQuantity), 95)); _linesGrid.Columns.Add(NumberEditableColumn("Banyak", nameof(InventoryEntryLine.Quantity), 95)); _linesGrid.Columns.Add(new DataGridTextColumn { Header = "Sat.", Binding = new Binding(nameof(InventoryEntryLine.Unit)), Width = 70, IsReadOnly = true }); _linesGrid.Columns.Add(NumberEditableColumn("HPP", nameof(InventoryEntryLine.UnitCost), 115)); _linesGrid.Columns.Add(NumberColumn("Jumlah", nameof(InventoryEntryLine.Amount), 125)); _linesGrid.AddHandler(ComboBox.SelectionChangedEvent, new SelectionChangedEventHandler(ItemSelectionChanged)); return; }
        if(_kind==InventoryTransactionKind.MbapReceive){_linesGrid.Columns.Add(new DataGridTextColumn { Header = "Kode Barang", Binding = new Binding(nameof(InventoryEntryLine.ItemCode)), Width = 130, IsReadOnly = true });_linesGrid.Columns.Add(new DataGridTextColumn { Header = "Nama Barang", Binding = new Binding(nameof(InventoryEntryLine.ItemName)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true });}
        else { _itemColumn=new DataGridComboBoxColumn { Header="Barang",SelectedValueBinding=new Binding(nameof(InventoryEntryLine.ItemCode)){UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged},SelectedValuePath=nameof(InventoryItemChoice.Code),DisplayMemberPath=nameof(InventoryItemChoice.Display),Width=new DataGridLength(2,DataGridLengthUnitType.Star)};_linesGrid.Columns.Add(_itemColumn); }
        if (_kind == InventoryTransactionKind.MbapReceive) _linesGrid.Columns.Add(new DataGridTextColumn { Header = "Sisa Kirim", Binding = new Binding(nameof(InventoryEntryLine.RemainingQuantity)) { StringFormat = "N3" }, Width = 105, IsReadOnly = true, ElementStyle = InventoryTransactionView.RightStyle() });
        _linesGrid.Columns.Add(new DataGridTextColumn { Header = _kind == InventoryTransactionKind.StockCount ? "Stok Fisik" : "Banyak", Binding = new Binding(nameof(InventoryEntryLine.Quantity)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 110, ElementStyle = InventoryTransactionView.RightStyle() }); _linesGrid.Columns.Add(new DataGridTextColumn { Header = "HPP / Harga", Binding = new Binding(nameof(InventoryEntryLine.UnitCost)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 125, ElementStyle = InventoryTransactionView.RightStyle() });
    }
    private DataGridTextColumn NumberEditableColumn(string title, string path, double width) => new() { Header = title, Binding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, StringFormat = "N2" }, Width = width, ElementStyle = InventoryTransactionView.RightStyle() };
    private DataGridTextColumn NumberColumn(string title, string path, double width) => new() { Header = title, Binding = new Binding(path) { StringFormat = "N2" }, Width = width, IsReadOnly = true, ElementStyle = InventoryTransactionView.RightStyle() };
    private void ItemSelectionChanged(object sender, SelectionChangedEventArgs e) { if (e.OriginalSource is not ComboBox combo || combo.DataContext is not InventoryEntryLine line || combo.SelectedValue is not string code || !_itemChoices.TryGetValue(code, out var item)) return; line.ItemName = item.Name; line.Unit = item.Unit; if (line.UnitCost == 0) line.UnitCost = item.AverageCost; RefreshLinesGridSafely(); RecalculateTotals(); }
    private void RecalculateTotals() { _totalQuantity.Text = Lines.Sum(x => x.Quantity).ToString("N2"); _totalAmount.Text = Lines.Sum(x => x.Amount).ToString("N2"); }
    private void CommitLinesGridEdit()
    {
        _linesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        _linesGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }
    private void RefreshLinesGridSafely()
    {
        if (TryCommitLinesGridEdit()) return;
        Dispatcher.BeginInvoke(new Action(() => { TryCommitLinesGridEdit(); }), System.Windows.Threading.DispatcherPriority.Background);
    }
    private bool TryCommitLinesGridEdit()
    {
        try
        {
            CommitLinesGridEdit();
            return true;
        }
        catch (InvalidOperationException exception) when (IsRefreshDuringGridTransaction(exception))
        {
            return false;
        }
    }
    private static bool IsRefreshDuringGridTransaction(InvalidOperationException exception)
    {
        var message = exception.Message;
        return message.Contains("Refresh", StringComparison.OrdinalIgnoreCase)
            && (message.Contains("AddNew", StringComparison.OrdinalIgnoreCase)
                || message.Contains("EditItem", StringComparison.OrdinalIgnoreCase));
    }
    private async Task SaveAsync()
    {
        try { CommitLinesGridEdit(); var date = DateOnly.FromDateTime(_date.SelectedDate ?? DateTime.Today); var user = AksaraApiClient.Shared.CurrentUserName; var computer = Environment.MachineName; var valid = Lines.Where(x => !string.IsNullOrWhiteSpace(x.ItemCode) && (_kind == InventoryTransactionKind.StockCount || x.Quantity != 0)).ToList(); if (valid.Count == 0) throw new InvalidOperationException("Minimal satu rincian barang wajib diisi."); IsEnabled = false; object saved = _kind switch { InventoryTransactionKind.Transfer => await _api.CreateInventoryTransferAsync(new(date, Code(_source), Code(_destination), Empty(_notes.Text), user, computer, valid.Select(x => x.ToRequest()).ToList())), InventoryTransactionKind.Adjustment => await _api.CreateInventoryAdjustmentAsync(new(date, Code(_adjustment), Code(_source), Empty(_notes.Text), user, computer, valid.Select(x => x.ToRequest()).ToList())), InventoryTransactionKind.StockCount => await _api.CreateInventoryStockCountAsync(new(date, Code(_source), Empty(_notes.Text), user, computer, valid.Select(x => new InventoryStockCountLineRequest(x.ItemCode, x.Quantity, x.UnitCost)).ToList())), InventoryTransactionKind.MbapSend => await _api.CreateInventoryMbapSendAsync(new(date, Code(_source), Code(_businessUnit), _settlement.SelectedDate is null ? null : DateOnly.FromDateTime(_settlement.SelectedDate.Value), Empty(_bank.Text), Empty(_notes.Text), user, computer, valid.Select(x => x.ToRequest()).ToList())), _ => await _api.CreateInventoryMbapReceiveAsync(new((Guid)(_mbapSource.SelectedValue ?? throw new InvalidOperationException("Pilih sumber MBAP Kirim.")), date, Code(_destination), Empty(_notes.Text), user, computer, valid.Select(x => new InventoryMbapReceiveLineRequest(x.SendLineId ?? Guid.Empty, x.Quantity)).ToList())) }; var number = saved.GetType().GetProperty("DocumentNumber")?.GetValue(saved)?.ToString(); MessageBox.Show($"Dokumen {number} berhasil disimpan dan stok langsung diperbarui.", TitleFor(_kind), MessageBoxButton.OK, MessageBoxImage.Information); DialogResult = true; }
        catch (Exception exception) { MessageBox.Show(exception.Message, TitleFor(_kind), MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { IsEnabled = true; }
    }
    private static string Code(ComboBox selector) => selector.SelectedValue?.ToString() ?? throw new InvalidOperationException("Pilihan master belum diisi."); private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string TitleFor(InventoryTransactionKind kind) => kind switch { InventoryTransactionKind.Transfer => "Mutasi Barang", InventoryTransactionKind.Adjustment => "Penyesuaian Persediaan", InventoryTransactionKind.StockCount => "Stok Fisik", InventoryTransactionKind.MbapSend => "MBAP Kirim", _ => "MBAP Terima" };
    private static void AddField(Grid grid, string label, Control control, int row, int column, int span = 1) { var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 6, 3) }; Grid.SetRow(text, row); Grid.SetColumn(text, column); grid.Children.Add(text); control.Margin = new Thickness(0, 3, 12, 3); Grid.SetRow(control, row); Grid.SetColumn(control, column + 1); Grid.SetColumnSpan(control, span); grid.Children.Add(control); }
}

public sealed class InventoryEntryLine
{
    public Guid? SendLineId { get; set; } public string ItemCode { get; set; } = ""; public string ItemName { get; set; } = ""; public string WarehouseCode { get; set; } = ""; public string Unit { get; set; } = ""; public decimal KoliQuantity { get; set; } public decimal Quantity { get; set; } public decimal RemainingQuantity { get; set; } public decimal UnitCost { get; set; } public decimal Amount => Quantity * UnitCost;
    public InventoryDocumentLineRequest ToRequest() => new(ItemCode.Trim(), Quantity, UnitCost);
}

public sealed record InventoryItemChoice(string Code, string Display, string Name = "", string Unit = "", decimal AverageCost = 0);

internal sealed class InventoryDocumentDetailWindow : Window
{
    public InventoryDocumentDetailWindow(string title, object document)
    {
        Title = $"{title} - AKSARA";
        Width = 1040;
        Height = 650;
        MinWidth = 860;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = Build(title, document);
    }

    private UIElement Build(string title, object document)
    {
        var root = new Grid { Margin = new Thickness(6), Background = Brush("#F0F0F0") };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Height = 43, Background = Brush("#ECECEC"), Margin = new Thickness(0, 0, 0, 1) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(205) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(165) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
        header.Children.Add(new TextBlock { Text = "DATA TRANSAKSI", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), FontWeight = FontWeights.Bold });
        var status = new TextBlock { Text = $"Status: {Value(document, "Status")}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(status, 1); header.Children.Add(status);
        var mode = new TextBlock { Text = "EDIT DATA", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(mode, 2); header.Children.Add(mode);
        var back = new Button { Content = "|||", Width = 42, Height = 30, Margin = new Thickness(8, 6, 8, 6), Padding = new Thickness(0), ToolTip = "Tutup rincian transaksi" };
        back.Click += (_, _) => Close(); Grid.SetColumn(back, 3); header.Children.Add(back);
        root.Children.Add(header);

        var body = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition());
        var fields = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
        fields.ColumnDefinitions.Add(new ColumnDefinition());
        fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddValue(fields, "Tgl. Bukti", FormatValue(document, "DocumentDate"), 0, 0);
        AddValue(fields, "No. Bukti", Value(document, "DocumentNumber"), 1, 0);
        AddValue(fields, "Gudang Asal", Value(document, "SourceWarehouseCode"), 0, 2);
        AddValue(fields, "Gudang Tujuan", Value(document, "DestinationWarehouseCode"), 1, 2);
        AddValue(fields, "Catatan", Value(document, "Notes"), 0, 4);
        Grid.SetRow(fields, 0); body.Children.Add(fields);

        var titleBand = new Border { BorderBrush = Brush("#6F7F8F"), BorderThickness = new Thickness(1), Background = Brushes.White, Height = 42, Margin = new Thickness(0, 0, 0, 6), Child = new TextBlock { Text = title.ToUpperInvariant(), FontSize = 19, FontWeight = FontWeights.Bold, Foreground = Brushes.Red, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        Grid.SetRow(titleBand, 1); body.Children.Add(titleBand);

        var lines = document.GetType().GetProperty("Lines")?.GetValue(document) as System.Collections.IEnumerable;
        var grid = new DataGrid { ItemsSource = lines, AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, RowHeight = 25, HeadersVisibility = DataGridHeadersVisibility.Column, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        grid.Columns.Add(Column("No.", nameof(InventoryDocumentLineDto.LineNumber), 55));
        grid.Columns.Add(Column("Kode Barang", nameof(InventoryDocumentLineDto.ItemCode), 145));
        grid.Columns.Add(Column("Nama Barang", nameof(InventoryDocumentLineDto.ItemName), new DataGridLength(250)));
        grid.Columns.Add(Column("Gudang", nameof(InventoryDocumentLineDto.WarehouseCode), 125));
        grid.Columns.Add(NumberColumn("Banyak", nameof(InventoryDocumentLineDto.Quantity), 105));
        grid.Columns.Add(Column("Sat.", nameof(InventoryDocumentLineDto.Unit), 70));
        grid.Columns.Add(NumberColumn("HPP / Harga", nameof(InventoryDocumentLineDto.UnitCost), 125));
        grid.Columns.Add(NumberColumn("Jumlah", nameof(InventoryDocumentLineDto.Amount), 140));
        Grid.SetRow(grid, 2); body.Children.Add(grid);
        Grid.SetRow(body, 1); root.Children.Add(body);

        var footer = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(135) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        footer.Children.Add(new TextBlock { Text = $"Total Qty: {FormatValue(document, "TotalQuantity")}    Total Nilai: {FormatValue(document, "TotalAmount")}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        var close = new Button { Content = "Tutup", Width = 95, Height = 30, Margin = new Thickness(4, 0, 0, 0) };
        close.Click += (_, _) => Close(); Grid.SetColumn(close, 2); footer.Children.Add(close);
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        return root;
    }

    private static void AddValue(Grid grid, string label, string value, int row, int column, int span = 1)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 4, 6, 4) };
        Grid.SetRow(text, row); Grid.SetColumn(text, column); grid.Children.Add(text);
        var box = new TextBlock { Text = string.IsNullOrWhiteSpace(value) ? "" : value, Background = Brushes.White, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 2, 10, 2), TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetRow(box, row); Grid.SetColumn(box, column + 1); Grid.SetColumnSpan(box, span * 2 - 1); grid.Children.Add(box);
    }

    private static DataGridTextColumn Column(string title, string path, double width) => Column(title, path, new DataGridLength(width));
    private static DataGridTextColumn Column(string title, string path, DataGridLength width) => new() { Header = title, Binding = new Binding(path), Width = width };
    private static DataGridTextColumn NumberColumn(string title, string path, double width) => new() { Header = title, Binding = new Binding(path) { StringFormat = "N2" }, Width = width, ElementStyle = InventoryTransactionView.RightStyle() };
    private static string Value(object value, string property) => value.GetType().GetProperty(property)?.GetValue(value)?.ToString() ?? "";
    private static string FormatValue(object value, string property)
    {
        var raw = value.GetType().GetProperty(property)?.GetValue(value);
        return raw switch { DateOnly date => date.ToString("dd/MM/yyyy"), decimal amount => amount.ToString("N2"), _ => raw?.ToString() ?? "" };
    }
    internal static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
}

internal static class InventoryTextPrompt
{
    public static string? Show(string label, string title, Window? owner) { var box = new TextBox { MinWidth = 340, Margin = new Thickness(0, 6, 0, 10) }; var window = new Window { Owner = owner, Title = title, Width = 400, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner }; var panel = new StackPanel { Margin = new Thickness(12) }; panel.Children.Add(new TextBlock { Text = label }); panel.Children.Add(box); var ok = new Button { Content = "OK", Width = 90, HorizontalAlignment = HorizontalAlignment.Right }; ok.Click += (_, _) => window.DialogResult = true; panel.Children.Add(ok); window.Content = panel; return window.ShowDialog() == true ? box.Text : null; }
}
