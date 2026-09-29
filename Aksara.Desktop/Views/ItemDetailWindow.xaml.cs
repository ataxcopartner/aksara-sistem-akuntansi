using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Aksara.Contracts;
using Aksara.Desktop.Services;
using Microsoft.Win32;

namespace Aksara.Desktop.Views;

public partial class ItemDetailWindow : Window
{
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly string? _itemCode;
    private readonly bool _serviceMode;
    private InventoryItemDto? _item;
    private string? _imageType;
    private string? _imageBase64;
    private UserControl? _embeddedHost;
    private bool _isEmbedded;
    private bool _dataLoaded;

    public ObservableCollection<EditablePriceRow> Prices { get; } = [];
    public ObservableCollection<EditablePackageRow> Packages { get; } = [];
    public ObservableCollection<EditableFormulaRow> Formula { get; } = [];
    public ObservableCollection<InventoryItemBalanceDto> Balances { get; } = [];
    public event EventHandler? Saved;
    public event EventHandler? CloseRequested;

    public ItemDetailWindow(string? itemCode = null, bool serviceMode = false)
    {
        _itemCode = itemCode;
        _serviceMode = serviceMode;
        InitializeComponent();
        if (_serviceMode)
        {
            ConfigureServiceMode();
        }
        DataContext = this;
        SeedPrices();
        Loaded += async (_, _) => await EnsureLoadedAsync();
    }

    public UserControl CreateEmbeddedView()
    {
        if (_embeddedHost is not null) return _embeddedHost;
        if (Content is not UIElement content) throw new InvalidOperationException($"Tampilan Master {(_serviceMode ? "Jasa" : "Barang")} tidak tersedia.");

        _isEmbedded = true;
        Content = null;
        if (content is FrameworkElement element) element.DataContext = this;
        _embeddedHost = new UserControl
        {
            Content = content,
            DataContext = this,
            Background = System.Windows.Media.Brushes.WhiteSmoke
        };
        _embeddedHost.Loaded += async (_, _) => await EnsureLoadedAsync();
        return _embeddedHost;
    }

    private async Task EnsureLoadedAsync()
    {
        if (_dataLoaded) return;
        _dataLoaded = true;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            IsEnabled = false;
            CategoryBox.ItemsSource = await _api.GetInventoryCategoriesAsync(false, _serviceMode ? "SERVICE" : "GOODS");
            if (string.IsNullOrWhiteSpace(_itemCode)) return;
            _item = await _api.GetInventoryItemAsync(_itemCode);
            ModeText.Text = "EDIT DATA";
            CodeBox.Text = _item.Code; CodeBox.IsReadOnly = true; NameBox.Text = _item.Name; UnitBox.Text = _item.Unit;
            CategoryBox.SelectedValue = _item.CategoryCode; BarcodeBox.Text = _item.Barcode; RackBox.Text = _item.Rack; BrandBox.Text = _item.Brand;
            PurchasePriceBox.Text = _item.PurchasePrice.ToString(CultureInfo.CurrentCulture); AverageCostBox.Text = _item.AverageCost.ToString(CultureInfo.CurrentCulture);
            MinimumStockBox.Text = _item.MinimumStock.ToString(CultureInfo.CurrentCulture); MaximumStockBox.Text = _item.MaximumStock.ToString(CultureInfo.CurrentCulture);
            CheckStockBox.IsChecked = _item.CheckStock; ActiveBox.IsChecked = _item.IsActive; NotesBox.Text = _item.Notes; SpecificationBox.Text = _item.Specification;
            Prices.Clear(); foreach (var row in _item.Prices) Prices.Add(new(row));
            Packages.Clear(); foreach (var row in _item.Packages) Packages.Add(new(row));
            var package = Packages.FirstOrDefault();
            PackageNameBox.Text = package?.PackageName ?? "";
            PackageSizeBox.Text = package?.PackageSize.ToString(CultureInfo.CurrentCulture) ?? "0";
            Formula.Clear(); foreach (var row in _item.Formula) Formula.Add(new(row));
            Balances.Clear(); foreach (var row in _item.Balances) Balances.Add(row);
            _imageType = _item.ImageContentType; _imageBase64 = _item.ImageBase64; ShowImage();
            StatusText.Text = _serviceMode
                ? $"Versi data {_item.Version:N0} | master jasa"
                : $"Versi data {_item.Version:N0} | saldo total {_item.Balances.Sum(x => x.Quantity):N3}";
        }
        catch (Exception exception) { MessageBox.Show(exception.Message, _serviceMode ? "Master Jasa" : "Master Barang", MessageBoxButton.OK, MessageBoxImage.Error); RequestClose(); }
        finally { IsEnabled = true; }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_serviceMode) CommitPackageFields();
            CommitGrids();
            IsEnabled = false;
            var request = new SaveInventoryItemRequest(
                CodeBox.Text, NameBox.Text, _serviceMode ? "JASA" : UnitBox.Text, CategoryBox.SelectedValue?.ToString(), Empty(BarcodeBox.Text), Empty(RackBox.Text), Empty(BrandBox.Text),
                _serviceMode ? 0m : Number(PurchasePriceBox.Text, "Harga beli"), _serviceMode ? 0m : Number(AverageCostBox.Text, "HPP Standar"), _serviceMode ? 0m : Number(MinimumStockBox.Text, "Minimum stok"), _serviceMode ? 0m : Number(MaximumStockBox.Text, "Maximum stok"),
                !_serviceMode && CheckStockBox.IsChecked == true, Empty(NotesBox.Text), Empty(SpecificationBox.Text), _imageType, _imageBase64, ActiveBox.IsChecked == true, _item?.Version,
                Prices.Select(x => x.ToDto()).ToList(), _serviceMode ? [] : Packages.Where(x => !string.IsNullOrWhiteSpace(x.PackageName)).Select((x, index) => x.ToDto(index)).ToList(),
                _serviceMode ? [] : Formula.Where(x => !string.IsNullOrWhiteSpace(x.ComponentItemCode)).Select(x => x.ToDto()).ToList(), AksaraApiClient.Shared.CurrentUserName, Environment.MachineName,
                _serviceMode ? "SERVICE" : "GOODS");
            _item = await _api.SaveInventoryItemAsync(request);
            if (_isEmbedded)
            {
                Saved?.Invoke(this, EventArgs.Empty);
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                DialogResult = true;
            }
        }
        catch (Exception exception) { MessageBox.Show(exception.Message, _serviceMode ? "Master Jasa" : "Master Barang", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            IsEnabled = true;
        }
    }

    public static SaveInventoryItemRequest ToSaveRequest(InventoryItemDto item, bool? isActive = null) => new(
        item.Code, item.Name, item.Unit, item.CategoryCode, item.Barcode, item.Rack, item.Brand, item.PurchasePrice, item.ItemType == "SERVICE" ? 0m : item.AverageCost, item.MinimumStock, item.MaximumStock,
        item.CheckStock, item.Notes, item.Specification, item.ImageContentType, item.ImageBase64, isActive ?? item.IsActive, item.Version, item.Prices, item.Packages, item.Formula,
        AksaraApiClient.Shared.CurrentUserName, Environment.MachineName, item.ItemType);

    private void LoadImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Gambar|*.png;*.jpg;*.jpeg;*.webp", CheckFileExists = true };
        var owner = _isEmbedded ? Window.GetWindow(_embeddedHost) : this;
        var accepted = owner is not null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (accepted != true) return;
        var bytes = File.ReadAllBytes(dialog.FileName);
        if (bytes.Length > 5 * 1024 * 1024) { MessageBox.Show("Ukuran gambar maksimum 5 MB.", "Gambar", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        _imageType = Path.GetExtension(dialog.FileName).ToLowerInvariant() switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };
        _imageBase64 = Convert.ToBase64String(bytes); ShowImage();
    }
    private void RemoveImage_Click(object sender, RoutedEventArgs e) { _imageType = null; _imageBase64 = null; ItemImage.Source = null; }
    private void ShowImage()
    {
        if (string.IsNullOrWhiteSpace(_imageBase64)) { ItemImage.Source = null; return; }
        var bitmap = new BitmapImage(); using var stream = new MemoryStream(Convert.FromBase64String(_imageBase64)); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); ItemImage.Source = bitmap;
    }
    private void RemovePackage_Click(object sender, RoutedEventArgs e)
    {
        Packages.Clear();
        PackageNameBox.Clear();
        PackageSizeBox.Text = "0";
    }
    private void RemoveFormula_Click(object sender, RoutedEventArgs e) { if (FormulaGrid.SelectedItem is EditableFormulaRow row) Formula.Remove(row); }
    private async void Deactivate_Click(object sender, RoutedEventArgs e) => await SetActiveAsync(false);
    private async void Reactivate_Click(object sender, RoutedEventArgs e) => await SetActiveAsync(true);
    private async Task SetActiveAsync(bool active)
    {
        if (_item is null)
        {
            var entityName = _serviceMode ? "jasa" : "barang";
            MessageBox.Show($"Simpan data {entityName} terlebih dahulu sebelum mengubah status.", _serviceMode ? "Master Jasa" : "Master Barang", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_item.IsActive == active) return;
        try
        {
            IsEnabled = false;
            _item = await _api.SaveInventoryItemAsync(ToSaveRequest(_item, active));
            ActiveBox.IsChecked = _item.IsActive;
            var entityName = _serviceMode ? "Jasa" : "Barang";
            StatusText.Text = active ? $"{entityName} diaktifkan kembali." : $"{entityName} dinonaktifkan.";
        }
        catch (Exception exception) { MessageBox.Show(exception.Message, _serviceMode ? "Master Jasa" : "Master Barang", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            IsEnabled = true;
        }
    }
    private void UnitLookup_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is TextBox target) ShowUnitLookup(target);
    }

    private void UnitLookup_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.F4 or Key.Enter or Key.Space)) return;
        e.Handled = true;
        if (sender is TextBox target) ShowUnitLookup(target);
    }

    private void ShowUnitLookup(TextBox target)
    {
        var dialog = new InventoryUnitLookupWindow(target.Text);
        var owner = _isEmbedded ? Window.GetWindow(_embeddedHost) : this;
        if (owner is not null && owner.IsVisible) dialog.Owner = owner;
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.SelectedUnit))
            target.Text = dialog.SelectedUnit;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => RequestClose();

    private void RequestClose()
    {
        if (_isEmbedded) CloseRequested?.Invoke(this, EventArgs.Empty);
        else Close();
    }
    private void CommitPackageFields()
    {
        Packages.Clear();
        if (string.IsNullOrWhiteSpace(PackageNameBox.Text)) return;
        Packages.Add(new EditablePackageRow
        {
            PackageName = PackageNameBox.Text.Trim(),
            PackageSize = Number(PackageSizeBox.Text, "Isi koli")
        });
    }

    private void ConfigureServiceMode()
    {
        Title = "Master Jasa";
        CategoryLabel.Text = "Kelompok Jasa";
        UnitLabel.Visibility = Visibility.Collapsed;
        UnitBox.Visibility = Visibility.Collapsed;
        UnitBox.Text = "JASA";
        PurchasePriceLabel.Visibility = Visibility.Collapsed;
        PurchasePriceBox.Visibility = Visibility.Collapsed;
        AverageCostLabel.Visibility = Visibility.Collapsed;
        AverageCostBox.Visibility = Visibility.Collapsed;
        CostRow.Height = new GridLength(0);
        CheckStockBox.IsChecked = false;
        CheckStockBox.Visibility = Visibility.Collapsed;

        PricingGroup.Header = "Tarif Jasa & Diskon Menurut Jumlah";
        PriceColumnLabel.Text = "Tarif";
        DiscountColumnLabel.Text = "Diskon";
        QuantityColumnLabel.Text = "Minimal";
        PricingHintText.Text = "Minimal menentukan jumlah pemakaian tarif pada setiap tingkat.";
        FooterHelpText.Text = "Isi data jasa lalu tekan Simpan.";

        MaterialsTab.Visibility = Visibility.Collapsed;
        WorkforceTab.Visibility = Visibility.Collapsed;
        OverheadTab.Visibility = Visibility.Collapsed;
        ItemDetailsTabs.SelectedItem = NotesTab;

        InventorySidePanel.Visibility = Visibility.Collapsed;
        Grid.SetColumnSpan(MainDetailPanel, 2);
        MainDetailPanel.Margin = new Thickness(0);
    }

    private void CommitGrids() { PriceGrid.CommitEdit(); PackageGrid.CommitEdit(); FormulaGrid.CommitEdit(); }
    private void SeedPrices() { Prices.Clear(); foreach (var row in new[] { ("RETAIL", "Eceran"), ("WHOLESALE", "Grosir"), ("BULK", "Partai"), ("SPECIAL1", "Khusus 1"), ("SPECIAL2", "Khusus 2"), ("SPECIAL3", "Khusus 3") }) Prices.Add(new() { TierCode = row.Item1, TierName = row.Item2 }); }
    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static decimal Number(string value, string name) => decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out var result) ? result : throw new InvalidOperationException($"{name} tidak valid.");
}

public sealed class EditablePriceRow
{
    public string TierCode { get; set; } = ""; public string TierName { get; set; } = ""; public decimal Price { get; set; } public decimal DiscountPercent { get; set; } public decimal MinimumQuantity { get; set; }
    public EditablePriceRow() { } public EditablePriceRow(InventoryPriceTierDto row) { TierCode = row.TierCode; TierName = row.TierName; Price = row.Price; DiscountPercent = row.DiscountPercent; MinimumQuantity = row.MinimumQuantity; }
    public InventoryPriceTierDto ToDto() => new(TierCode, TierName, Price, DiscountPercent, MinimumQuantity);
}
public sealed class EditablePackageRow
{
    public string PackageName { get; set; } = ""; public decimal PackageSize { get; set; } public string? Barcode { get; set; }
    public EditablePackageRow() { } public EditablePackageRow(InventoryPackageDto row) { PackageName = row.PackageName; PackageSize = row.PackageSize; Barcode = row.Barcode; }
    public InventoryPackageDto ToDto(int index) => new(PackageName.Trim(), PackageSize, string.IsNullOrWhiteSpace(Barcode) ? null : Barcode.Trim(), index + 1);
}
public sealed class EditableFormulaRow
{
    public string ComponentItemCode { get; set; } = ""; public string ComponentItemName { get; set; } = ""; public decimal Quantity { get; set; } public string Unit { get; set; } = "PCS";
    public EditableFormulaRow() { } public EditableFormulaRow(InventoryFormulaLineDto row) { ComponentItemCode = row.ComponentItemCode; ComponentItemName = row.ComponentItemName; Quantity = row.Quantity; Unit = row.Unit; }
    public InventoryFormulaLineDto ToDto() => new(ComponentItemCode.Trim(), ComponentItemName.Trim(), Quantity, Unit.Trim());
}
