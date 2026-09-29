using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

public sealed class InventoryUnitLookupWindow : Window
{
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly ObservableCollection<string> _units = [];
    private readonly ICollectionView _view;
    private readonly TextBox _search = new() { Height = 28, Margin = new Thickness(8, 8, 8, 6), Padding = new Thickness(6, 3, 6, 3) };
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        CanUserAddRows = false,
        IsReadOnly = true,
        SelectionMode = DataGridSelectionMode.Single,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        RowHeight = 27,
        Margin = new Thickness(8, 0, 8, 8)
    };
    private readonly TextBox _newUnit = new() { Height = 28, MinWidth = 180, CharacterCasing = CharacterCasing.Upper, MaxLength = 10 };

    public string? SelectedUnit { get; private set; }

    public InventoryUnitLookupWindow(string? currentUnit = null)
    {
        Title = "Daftar Satuan";
        Width = 440;
        Height = 570;
        MinWidth = 380;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        Background = System.Windows.Media.Brushes.WhiteSmoke;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/aksara.ico"));

        foreach (var unit in InventoryUnitCatalog.Load()) _units.Add(unit);
        if (!string.IsNullOrWhiteSpace(currentUnit) && !_units.Contains(currentUnit.Trim(), StringComparer.OrdinalIgnoreCase))
            _units.Add(currentUnit.Trim().ToUpperInvariant());
        _view = CollectionViewSource.GetDefaultView(_units);
        _view.Filter = FilterUnit;

        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Satuan",
            Binding = new Binding("."),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });
        _grid.ItemsSource = _view;
        _grid.MouseDoubleClick += (_, _) => SelectCurrent();
        _grid.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            SelectCurrent();
        };
        _search.TextChanged += (_, _) => _view.Refresh();

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var searchPanel = new Grid();
        searchPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        searchPanel.ColumnDefinitions.Add(new ColumnDefinition());
        searchPanel.Children.Add(new TextBlock { Text = "Cari", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_search, 1);
        searchPanel.Children.Add(_search);
        root.Children.Add(searchPanel);

        Grid.SetRow(_grid, 1);
        root.Children.Add(_grid);

        var addPanel = new Grid { Margin = new Thickness(8, 0, 8, 8) };
        addPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        addPanel.ColumnDefinitions.Add(new ColumnDefinition());
        addPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
        addPanel.Children.Add(new TextBlock { Text = "Nama satuan", VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_newUnit, 1);
        addPanel.Children.Add(_newUnit);
        var addButton = new Button { Content = "Tambah", Height = 28, Margin = new Thickness(8, 0, 0, 0) };
        addButton.Click += async (_, _) => await AddUnitAsync();
        Grid.SetColumn(addButton, 2);
        addPanel.Children.Add(addButton);
        Grid.SetRow(addPanel, 2);
        root.Children.Add(addPanel);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0, 8, 8) };
        var selectButton = new Button { Content = "Pilih", Width = 92, Height = 30, Margin = new Thickness(0, 0, 7, 0), IsDefault = true };
        var cancelButton = new Button { Content = "Batal", Width = 92, Height = 30, IsCancel = true };
        selectButton.Click += (_, _) => SelectCurrent();
        buttons.Children.Add(selectButton);
        buttons.Children.Add(cancelButton);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        Content = root;
        Loaded += async (_, _) =>
        {
            await LoadServerUnitsAsync();
            if (!string.IsNullOrWhiteSpace(currentUnit))
                _grid.SelectedItem = _units.FirstOrDefault(x => x.Equals(currentUnit, StringComparison.OrdinalIgnoreCase));
            _search.Focus();
        };
    }

    private bool FilterUnit(object value) => value is string unit &&
        (string.IsNullOrWhiteSpace(_search.Text) || unit.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase));

    private async Task LoadServerUnitsAsync()
    {
        try
        {
            var serverUnits = await _api.GetInventoryUnitsAsync();
            foreach (var row in serverUnits)
            {
                if (!_units.Contains(row.Name, StringComparer.OrdinalIgnoreCase)) _units.Add(row.Name);
            }
            SortUnits();
            _view.Refresh();
        }
        catch (AksaraApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Server lama tetap memakai katalog bawaan dan penyimpanan lokal.
        }
        catch
        {
            // Pemilih satuan harus tetap dapat dipakai ketika koneksi cloud terganggu.
        }
    }

    private async Task AddUnitAsync()
    {
        try
        {
            var unit = InventoryUnitCatalog.Add(_newUnit.Text);
            try
            {
                var saved = await _api.SaveInventoryUnitAsync(new(
                    unit,
                    AksaraApiClient.Shared.CurrentUserName,
                    Environment.MachineName));
                unit = saved.Name;
            }
            catch (AksaraApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Sinkronisasi cloud aktif setelah backend yang mendukung master satuan dirilis.
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, $"Satuan tersimpan di perangkat ini, tetapi belum tersinkron ke cloud.\n\n{exception.Message}", "Tambah Satuan", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            if (!_units.Contains(unit, StringComparer.OrdinalIgnoreCase)) _units.Add(unit);
            SortUnits();
            _view.Refresh();
            _grid.SelectedItem = unit;
            _grid.ScrollIntoView(unit);
            _newUnit.Clear();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Tambah Satuan", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SortUnits()
    {
        var sorted = _units.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        _units.Clear();
        foreach (var unit in sorted) _units.Add(unit);
    }

    private void SelectCurrent()
    {
        if (_grid.SelectedItem is not string unit) return;
        SelectedUnit = unit;
        DialogResult = true;
    }
}
