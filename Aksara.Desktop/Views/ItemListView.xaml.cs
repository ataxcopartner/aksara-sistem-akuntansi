using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Aksara.Contracts;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

public partial class ItemListView : UserControl
{
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly bool _serviceMode;
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private CancellationTokenSource? _reloadCancellation;
    private UserControl? _editorHost;
    private ItemDetailWindow? _embeddedEditor;
    private bool _loaded;
    public ObservableCollection<InventoryItemSummaryDto> Items { get; } = [];
    public event EventHandler? CloseRequested;

    public ItemListView(bool serviceMode = false)
    {
        _serviceMode = serviceMode;
        InitializeComponent();
        DataContext = this;
        if (_serviceMode)
        {
            ListTitleText.Text = "DAFTAR JASA";
            AddNewButton.Content = "Tambah Jasa Baru";
            ItemGrid.Columns[0].Header = "Kel. Jasa";
            ItemGrid.Columns[1].Header = "Kode Jasa";
            ItemGrid.Columns[2].Header = "Nama Jasa";
            ItemGrid.Columns[3].Visibility = Visibility.Collapsed;
            ItemGrid.Columns[7].Visibility = Visibility.Collapsed;
            ItemGrid.Columns[8].Visibility = Visibility.Collapsed;
            ItemGrid.Columns[11].Visibility = Visibility.Collapsed;
            ItemGrid.Columns[12].Visibility = Visibility.Collapsed;
        }
        _searchDelay.Tick += async (_, _) =>
        {
            _searchDelay.Stop();
            await ReloadAsync();
        };
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            await ReloadAsync();
        };
    }

    private async Task ReloadAsync()
    {
        _reloadCancellation?.Cancel();
        _reloadCancellation?.Dispose();
        _reloadCancellation = new CancellationTokenSource();
        var cancellationToken = _reloadCancellation.Token;
        try
        {
            ServerStatusText.Text = _serviceMode ? "Mengambil master Jasa dari server..." : "Mengambil master dan saldo Barang dari server...";
            var query = SearchBox.Text;
            var rowsTask = _api.SearchInventoryItemsAsync(query, cancellationToken: cancellationToken, itemType: _serviceMode ? "SERVICE" : "GOODS");
            var costsTask = _serviceMode
                ? Task.FromResult(new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase))
                : LoadLatestPurchaseCostsAsync(query, cancellationToken);
            await Task.WhenAll(rowsTask, costsTask);
            cancellationToken.ThrowIfCancellationRequested();
            var rows = SortRows(rowsTask.Result);
            var latestPurchaseCosts = costsTask.Result;
            var recoveredCostCount = 0;
            Items.Clear();
            foreach (var row in rows)
            {
                if (row.AverageCost <= 0 && latestPurchaseCosts.TryGetValue(row.Code, out var purchaseCost) && purchaseCost > 0)
                {
                    Items.Add(row with { AverageCost = purchaseCost });
                    recoveredCostCount++;
                }
                else
                {
                    Items.Add(row);
                }
            }
            DataCountText.Text = $"DATA {Items.Count:N0} / {Items.Count:N0}";
            ServerStatusText.Text = _serviceMode
                ? $"{Items.Count:N0} jasa terintegrasi | kelompok dan tarif terbaru"
                : recoveredCostCount > 0
                ? $"{Items.Count:N0} barang terintegrasi | HPP per unit tersedia, {recoveredCostCount:N0} dipulihkan dari pembelian"
                : $"{Items.Count:N0} barang terintegrasi | stok dan HPP per unit terbaru";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            ServerStatusText.Text = $"Server belum siap: {exception.Message}";
        }
    }

    private async Task<Dictionary<string, decimal>> LoadLatestPurchaseCostsAsync(string? query, CancellationToken cancellationToken)
    {
        try
        {
            var history = await _api.GetInventoryPriceHistoryAsync(
                "PURCHASE",
                new DateOnly(1900, 1, 1),
                DateOnly.FromDateTime(DateTime.Today),
                query,
                cancellationToken);
            var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in history.Rows)
            {
                var cost = Math.Round(row.UnitPrice * (1m - row.DiscountPercent / 100m), 5);
                if (cost > 0) result.TryAdd(row.ItemCode, cost);
            }
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded) return;
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || Items.Count == 0) return;
        var selectedCode = (ItemGrid.SelectedItem as InventoryItemSummaryDto)?.Code;
        var sorted = SortRows(Items).ToArray();
        Items.Clear();
        foreach (var row in sorted) Items.Add(row);
        if (!string.IsNullOrWhiteSpace(selectedCode))
            ItemGrid.SelectedItem = Items.FirstOrDefault(x => x.Code.Equals(selectedCode, StringComparison.OrdinalIgnoreCase));
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) { SearchBox.Clear(); await ReloadAsync(); }
    private async void Reload_Click(object sender, RoutedEventArgs e) => await ReloadAsync();
    private void FocusSearch_Click(object sender, RoutedEventArgs e) => SearchBox.Focus();
    private async void Add_Click(object sender, RoutedEventArgs e) => await OpenEditorAsync(null);
    private async void Edit_Click(object sender, RoutedEventArgs e) => await OpenEditorAsync((ItemGrid.SelectedItem as InventoryItemSummaryDto)?.Code);
    private async void ItemGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await OpenEditorAsync((ItemGrid.SelectedItem as InventoryItemSummaryDto)?.Code);

    private Task OpenEditorAsync(string? code)
    {
        if (_editorHost is not null) return Task.CompletedTask;

        var saved = false;
        var editor = new ItemDetailWindow(code, _serviceMode);
        var host = editor.CreateEmbeddedView();
        _embeddedEditor = editor;
        _editorHost = host;
        Grid.SetRowSpan(host, 5);
        Panel.SetZIndex(host, 100);
        editor.Saved += (_, _) => saved = true;
        editor.CloseRequested += async (_, _) =>
        {
            RootGrid.Children.Remove(host);
            _editorHost = null;
            _embeddedEditor = null;
            if (saved) await ReloadAsync();
            ItemGrid.Focus();
        };
        RootGrid.Children.Add(host);
        return Task.CompletedTask;
    }

    private IEnumerable<InventoryItemSummaryDto> SortRows(IEnumerable<InventoryItemSummaryDto> rows) => SortBox.SelectedIndex switch
    {
        1 => rows.OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase),
        2 => rows.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
        _ => rows.OrderBy(x => x.CategoryCode ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
    };

    private async void Deactivate_Click(object sender, RoutedEventArgs e)
    {
        if (ItemGrid.SelectedItem is not InventoryItemSummaryDto selected) return;
        if (MessageBox.Show($"Nonaktifkan {(_serviceMode ? "jasa" : "barang")} {selected.Code} - {selected.Name}?", "AKSARA", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var item = await _api.GetInventoryItemAsync(selected.Code);
            await _api.SaveInventoryItemAsync(ItemDetailWindow.ToSaveRequest(item, isActive: false));
            await ReloadAsync();
        }
        catch (Exception exception) { MessageBox.Show(exception.Message, _serviceMode ? "Jasa" : "Barang", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Price_Click(object sender, RoutedEventArgs e)
    {
        if (ItemGrid.SelectedItem is not InventoryItemSummaryDto selected) return;
        var view = new InventoryPriceWindow(selected.Code) { Owner = Window.GetWindow(this) };
        view.ShowDialog();
    }

    private void Unavailable_Click(object sender, RoutedEventArgs e) => MessageBox.Show("Gunakan menu Barang untuk master, gambar, harga, histori, dan laporan lengkap.", "AKSARA", MessageBoxButton.OK, MessageBoxImage.Information);
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
