using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Aksara.Desktop.Views;

internal sealed record ReturnItemChoice(object SourceRow, string WarehouseCode, string ItemCode, string ItemName, decimal OriginalQuantity, decimal PreviouslyReturned, decimal RemainingQuantity, string Unit);

internal sealed class ReturnItemPickerWindow : Window
{
    private readonly IReadOnlyList<ReturnItemChoice> _source;
    private readonly TextBox _search = new() { MinWidth = 260 };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBlock _status = new();
    public ReturnItemChoice? SelectedItem { get; private set; }

    public ReturnItemPickerWindow(string sourceDocumentNumber, IReadOnlyList<ReturnItemChoice> source)
    {
        _source = source;
        Title = $"PILIH BARANG RETUR - {sourceDocumentNumber}";
        Width = 820; Height = 480; MinWidth = 650; MinHeight = 380; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        _grid.Columns.Add(Column("Lok.", nameof(ReturnItemChoice.WarehouseCode), 60));
        _grid.Columns.Add(Column("Kode Barang", nameof(ReturnItemChoice.ItemCode), 120));
        _grid.Columns.Add(Column("Nama Barang", nameof(ReturnItemChoice.ItemName), 280));
        _grid.Columns.Add(Column("Asal", nameof(ReturnItemChoice.OriginalQuantity), 75, "N2"));
        _grid.Columns.Add(Column("Sudah Retur", nameof(ReturnItemChoice.PreviouslyReturned), 85, "N2"));
        _grid.Columns.Add(Column("Sisa", nameof(ReturnItemChoice.RemainingQuantity), 75, "N2"));
        _grid.Columns.Add(Column("Sat.", nameof(ReturnItemChoice.Unit), 55));
        Content = Build();
        Loaded += (_, _) => { Reload(); _search.Focus(); };
        _search.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Reload(); } };
        _grid.MouseDoubleClick += (_, _) => Accept();
        _grid.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Accept(); } };
    }

    private UIElement Build()
    {
        var root = new Grid { Margin = new Thickness(8) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var search = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
        var button = new Button { Content = "Cari", Width = 78, Margin = new Thickness(6, 0, 0, 0) }; button.Click += (_, _) => Reload(); DockPanel.SetDock(button, Dock.Right); search.Children.Add(button);
        search.Children.Add(new TextBlock { Text = "Cari barang pada faktur", Width = 145, VerticalAlignment = VerticalAlignment.Center }); search.Children.Add(_search); root.Children.Add(search);
        Grid.SetRow(_grid, 1); root.Children.Add(_grid);
        var footer = new Grid { Margin = new Thickness(0, 7, 0, 0) }; footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); footer.Children.Add(_status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; var select = new Button { Content = "Pilih", Width = 85 }; select.Click += (_, _) => Accept(); var cancel = new Button { Content = "Batal", Width = 85, Margin = new Thickness(6, 0, 0, 0) }; cancel.Click += (_, _) => DialogResult = false; actions.Children.Add(select); actions.Children.Add(cancel); Grid.SetColumn(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 2); root.Children.Add(footer);
        return root;
    }

    private void Reload()
    {
        var query = _search.Text.Trim();
        var rows = _source.Where(x => query.Length == 0 || x.ItemCode.Contains(query, StringComparison.OrdinalIgnoreCase) || x.ItemName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        _grid.ItemsSource = rows; if (rows.Count > 0) _grid.SelectedIndex = 0; _status.Text = $"{rows.Count:N0} barang masih dapat diretur";
    }
    private void Accept() { if (_grid.SelectedItem is not ReturnItemChoice selected) return; SelectedItem = selected; DialogResult = true; }
    private static DataGridTextColumn Column(string header, string property, double width, string? format = null) => new() { Header = header, Binding = new Binding(property) { StringFormat = format }, Width = width };
}
