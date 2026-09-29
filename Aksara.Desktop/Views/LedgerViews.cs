using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Aksara.Contracts;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

public enum LedgerReportKind { AccountList, Journal, OpeningJournal, TransactionJournal, GeneralLedger, TrialBalance, IncomeStatement, BalanceSheetSkontro, BalanceSheetStaffel }

public sealed class LedgerAccountView : UserControl
{
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly TextBox _search = new() { Width = 250, Margin = new Thickness(4, 0, 8, 0) };
    private readonly CheckBox _inactive = new() { Content = "Tampilkan nonaktif", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single, RowHeight = 23 };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    public event EventHandler? CloseRequested;

    public LedgerAccountView()
    {
        BuildColumns();
        Content = Build();
        Loaded += async (_, _) => await ReloadAsync();
        _search.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await ReloadAsync(); } };
        _grid.MouseDoubleClick += async (_, _) => await EditAsync();
    }

    private UIElement Build()
    {
        var root = Shell("DAFTAR PERKIRAAN", _status, out var footer);
        var tools = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var add = Button("Tambah"); add.Click += async (_, _) => await OpenEditorAsync(null);
        var edit = Button("Ubah"); edit.Click += async (_, _) => await EditAsync();
        var refresh = Button("Muat Ulang"); refresh.Click += async (_, _) => await ReloadAsync();
        var print = Button("Cetak"); print.Click += (_, _) => PrintGrid(_grid, "Daftar Perkiraan");
        actions.Children.Add(add); actions.Children.Add(edit); actions.Children.Add(refresh); actions.Children.Add(print);
        DockPanel.SetDock(actions, Dock.Right); tools.Children.Add(actions);
        tools.Children.Add(Label("Cari")); tools.Children.Add(_search); tools.Children.Add(_inactive);
        Grid.SetRow(tools, 1); root.Children.Add(tools);
        Grid.SetRow(_grid, 2); root.Children.Add(_grid);
        footer.Children.Add(CloseButton(() => CloseRequested?.Invoke(this, EventArgs.Empty)));
        return root;
    }

    private void BuildColumns()
    {
        _grid.Columns.Add(Column("Kode", "Code", 95));
        _grid.Columns.Add(Column("Nama Perkiraan", "Name", new DataGridLength(1, DataGridLengthUnitType.Star)));
        _grid.Columns.Add(Column("Jenis", "AccountType", 95));
        _grid.Columns.Add(Column("D/K", "NormalBalance", 46));
        _grid.Columns.Add(Column("Induk", "ParentCode", 75));
        _grid.Columns.Add(CheckColumn("Posting", "IsPostable", 65));
        _grid.Columns.Add(CheckColumn("Kas/Bank", "IsCashBank", 70));
        _grid.Columns.Add(CheckColumn("Aktif", "IsActive", 55));
    }

    private async Task ReloadAsync(string? selectCode = null)
    {
        try
        {
            IsEnabled = false; _status.Text = "Mengambil daftar perkiraan...";
            var rows = (await _api.GetLedgerAccountsAsync(_search.Text, _inactive.IsChecked == true)).ToList();
            _grid.ItemsSource = rows;
            if (!string.IsNullOrWhiteSpace(selectCode)) _grid.SelectedItem = rows.FirstOrDefault(x => x.Code == selectCode);
            _status.Text = $"{rows.Count:N0} perkiraan | Kode ini dipakai oleh jurnal, mapping transaksi, kas, bank, biaya, dan penghasilan.";
        }
        catch (Exception exception) { _status.Text = exception.Message; ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Daftar Perkiraan"); }
        finally { IsEnabled = true; }
    }

    private async Task EditAsync()
    {
        if (_grid.SelectedItem is LedgerAccountDto row) await OpenEditorAsync(row);
    }

    private async Task OpenEditorAsync(LedgerAccountDto? row)
    {
        var editor = new LedgerAccountEditWindow(row) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() == true) await ReloadAsync(editor.SavedCode);
    }

    internal static Grid Shell(string title, TextBlock status, out StackPanel footerActions)
    {
        var root = new Grid { Margin = new Thickness(5), Background = Brushes.White };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new Border { Background = Brush("#DCE7F1"), BorderBrush = Brush("#A7B3BE"), BorderThickness = new Thickness(1), Padding = new Thickness(8, 5, 8, 5), Child = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Brush("#003A5C") } });
        var footer = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(status);
        footerActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(footerActions, 1); footer.Children.Add(footerActions);
        Grid.SetRow(footer, 3); root.Children.Add(footer);
        return root;
    }

    internal static Button Button(string text) => new() { Content = text, Width = 92, Height = 24, MinHeight = 0, Margin = new Thickness(5, 0, 0, 0) };
    internal static Button CloseButton(Action close) { var button = Button("Tutup"); button.Click += (_, _) => close(); return button; }
    internal static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 5, 2) };
    internal static DataGridTextColumn Column(string header, string property, double width, string? format = null) => new() { Header = header, Binding = new Binding(property) { StringFormat = format }, Width = width };
    internal static DataGridTextColumn Column(string header, string property, DataGridLength width, string? format = null) => new() { Header = header, Binding = new Binding(property) { StringFormat = format }, Width = width };
    internal static DataGridCheckBoxColumn CheckColumn(string header, string property, double width) => new() { Header = header, Binding = new Binding(property), Width = width };
    internal static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
    internal static Style RightAlignedStyle() { var style = new Style(typeof(TextBlock)); style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right)); return style; }
    internal static DataGridTextColumn NumberColumn(string header, string property, double width) => new() { Header = header, Binding = new Binding(property) { StringFormat = "N2" }, Width = width, ElementStyle = RightAlignedStyle() };
    internal static void PrintGrid(Visual visual, string title)
    {
        if (visual is DataGrid grid)
        {
            ReportGridTools.PrintDataGrid(grid, title);
            return;
        }
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() == true) dialog.PrintVisual(visual, title);
    }
}

internal sealed class LedgerAccountEditWindow : Window
{
    private readonly LedgerAccountDto? _row;
    private readonly TextBox _code = new();
    private readonly TextBox _name = new();
    private readonly ComboBox _type = new() { ItemsSource = new[] { "ASSET", "LIABILITY", "EQUITY", "REVENUE", "EXPENSE", "COGS" } };
    private readonly ComboBox _normal = new() { ItemsSource = new[] { "D", "C" } };
    private readonly TextBox _parent = new();
    private readonly TextBlock _parentName = new() { Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _postable = new() { Content = "Dapat diposting di jurnal", IsChecked = true };
    private readonly CheckBox _cashBank = new() { Content = "Akun kas/bank" };
    private readonly CheckBox _active = new() { Content = "Aktif", IsChecked = true };
    private readonly TextBlock _status = new() { Foreground = Brushes.Maroon, TextWrapping = TextWrapping.Wrap };
    public string? SavedCode { get; private set; }

    public LedgerAccountEditWindow(LedgerAccountDto? row)
    {
        _row = row;
        Title = row is null ? "Tambah Perkiraan" : "Ubah Perkiraan";
        Width = 560; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Content = Build();
        Populate();
        _type.SelectionChanged += (_, _) => SetDefaultNormal();
    }

    private UIElement Build()
    {
        var root = FormGrid(10);
        Add(root, "Kode Perkiraan", _code, 0);
        Add(root, "Nama Perkiraan", _name, 1);
        Add(root, "Jenis", _type, 2);
        Add(root, "Saldo Normal", _normal, 3);
        var parentPanel = new DockPanel();
        var lookup = new Button { Content = "...", Width = 34, Margin = new Thickness(5, 3, 0, 3) };
        lookup.Click += (_, _) => LookupParent();
        DockPanel.SetDock(lookup, Dock.Right); parentPanel.Children.Add(lookup); parentPanel.Children.Add(_parent);
        Add(root, "Kode Induk", parentPanel, 4);
        Add(root, "", _parentName, 5);
        AddControl(root, _postable, 6);
        AddControl(root, _cashBank, 7);
        AddControl(root, _active, 8);
        AddControl(root, _status, 9);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var save = new Button { Content = "Simpan", Width = 90, IsDefault = true, Margin = new Thickness(4) }; save.Click += Save_Click;
        buttons.Children.Add(save); buttons.Children.Add(new Button { Content = "Batal", Width = 90, IsCancel = true, Margin = new Thickness(4) });
        Grid.SetRow(buttons, 10); Grid.SetColumnSpan(buttons, 2); root.Children.Add(buttons);
        return root;
    }

    private void Populate()
    {
        _type.SelectedIndex = 0; _normal.SelectedIndex = 0;
        if (_row is null) return;
        _code.Text = _row.Code; _code.IsReadOnly = true; _name.Text = _row.Name; _type.SelectedItem = _row.AccountType;
        _normal.SelectedItem = _row.NormalBalance; _parent.Text = _row.ParentCode ?? string.Empty;
        _postable.IsChecked = _row.IsPostable; _cashBank.IsChecked = _row.IsCashBank; _active.IsChecked = _row.IsActive;
    }

    private void SetDefaultNormal()
    {
        var type = _type.SelectedItem?.ToString();
        _normal.SelectedItem = type is "ASSET" or "EXPENSE" or "COGS" ? "D" : "C";
    }

    private void LookupParent()
    {
        var lookup = new LedgerAccountLookupWindow(false) { Owner = this };
        if (lookup.ShowDialog() == true && lookup.SelectedAccount is not null)
        {
            _parent.Text = lookup.SelectedAccount.Code;
            _parentName.Text = lookup.SelectedAccount.Name;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            IsEnabled = false; _status.Text = "Menyimpan...";
            var saved = await AksaraApiClient.Shared.SaveLedgerAccountAsync(new SaveLedgerAccountRequest(
                _code.Text, _name.Text, _type.SelectedItem?.ToString() ?? "", _normal.SelectedItem?.ToString() ?? "",
                string.IsNullOrWhiteSpace(_parent.Text) ? null : _parent.Text, _postable.IsChecked == true, _cashBank.IsChecked == true,
                _active.IsChecked == true, _row?.Version, AksaraApiClient.Shared.CurrentUserName, Environment.MachineName));
            SavedCode = saved.Code;
            DialogResult = true;
        }
        catch (Exception exception) { _status.Text = exception.Message; ViewError.ShowWarningUnlessDatabaseUnavailable(exception, Title); }
        finally { IsEnabled = true; }
    }

    internal static Grid FormGrid(int rows)
    {
        var root = new Grid { Margin = new Thickness(12) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i <= rows; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        return root;
    }

    internal static void Add(Grid grid, string label, UIElement input, int row)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 8, 3) };
        Grid.SetRow(text, row); grid.Children.Add(text);
        if (input is FrameworkElement element) element.Margin = new Thickness(0, 3, 0, 3);
        Grid.SetRow(input, row); Grid.SetColumn(input, 1); grid.Children.Add(input);
    }

    internal static void AddControl(Grid grid, FrameworkElement control, int row)
    {
        control.Margin = new Thickness(0, 5, 0, 5); Grid.SetRow(control, row); Grid.SetColumn(control, 1); grid.Children.Add(control);
    }
}

public sealed class JournalEntryView : UserControl
{
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly TextBox _search = new() { Width = 260, Margin = new Thickness(4, 0, 8, 0) };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single, RowHeight = 23 };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    public event EventHandler? CloseRequested;

    public JournalEntryView()
    {
        BuildColumns();
        Content = Build();
        Loaded += async (_, _) => await ReloadAsync();
        _search.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await ReloadAsync(); } };
        _grid.MouseDoubleClick += async (_, _) => await DetailAsync();
    }

    private UIElement Build()
    {
        var root = LedgerAccountView.Shell("JURNAL UMUM", _status, out var footer);
        var tools = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var first = SmallButton("|<"); first.Click += (_, _) => MoveSelection(0);
        var previous = SmallButton("<"); previous.Click += (_, _) => MoveSelection(-1);
        var next = SmallButton(">"); next.Click += (_, _) => MoveSelection(1);
        var last = SmallButton(">|"); last.Click += (_, _) => MoveSelection(int.MaxValue);
        var add = LedgerAccountView.Button("Tambah"); add.Click += async (_, _) => await OpenEditorAsync();
        var detail = LedgerAccountView.Button("Rinci"); detail.Click += async (_, _) => await DetailAsync();
        var refresh = LedgerAccountView.Button("Muat Ulang"); refresh.Click += async (_, _) => await ReloadAsync();
        actions.Children.Add(first); actions.Children.Add(previous); actions.Children.Add(next); actions.Children.Add(last);
        actions.Children.Add(add); actions.Children.Add(detail); actions.Children.Add(refresh);
        DockPanel.SetDock(actions, Dock.Right); tools.Children.Add(actions);
        tools.Children.Add(LedgerAccountView.Label("Cari")); tools.Children.Add(_search);
        Grid.SetRow(tools, 1); root.Children.Add(tools);
        Grid.SetRow(_grid, 2); root.Children.Add(_grid);
        footer.Children.Add(LedgerAccountView.CloseButton(() => CloseRequested?.Invoke(this, EventArgs.Empty)));
        return root;
    }

    private void BuildColumns()
    {
        _grid.Columns.Add(LedgerAccountView.Column("Tanggal", "DocumentDate", 85));
        _grid.Columns.Add(LedgerAccountView.Column("No. Jurnal", "DocumentNumber", 125));
        _grid.Columns.Add(LedgerAccountView.Column("Keterangan", "Memo", new DataGridLength(1, DataGridLengthUnitType.Star)));
        _grid.Columns.Add(LedgerAccountView.Column("Sumber", "SourceModule", 90));
        _grid.Columns.Add(LedgerAccountView.Column("Status", "Status", 70));
        _grid.Columns.Add(LedgerAccountView.NumberColumn("Debit", "DebitTotal", 105));
        _grid.Columns.Add(LedgerAccountView.NumberColumn("Kredit", "CreditTotal", 105));
    }

    private async Task ReloadAsync(Guid? selectId = null)
    {
        try
        {
            IsEnabled = false; _status.Text = "Mengambil jurnal umum...";
            var rows = (await _api.GetJournalEntriesAsync(_search.Text)).ToList();
            _grid.ItemsSource = rows;
            if (selectId is not null) _grid.SelectedItem = rows.FirstOrDefault(x => x.Id == selectId.Value);
            _status.Text = $"{rows.Count:N0} jurnal | Jurnal dari Kas Bank ikut muncul di sini sebagai sumber CASH_BANK.";
        }
        catch (Exception exception) { _status.Text = exception.Message; ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Jurnal Umum"); }
        finally { IsEnabled = true; }
    }

    private async Task OpenEditorAsync()
    {
        var editor = new JournalEntryEditWindow { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() == true && editor.SavedId is not null) await ReloadAsync(editor.SavedId.Value);
    }

    private async Task DetailAsync()
    {
        if (_grid.SelectedItem is not JournalEntrySummaryDto row) return;
        try
        {
            var detail = await _api.GetJournalEntryAsync(row.Id);
            new JournalEntryDetailWindow(detail) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (Exception exception) { ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Rincian Jurnal"); }
    }

    private void MoveSelection(int delta)
    {
        if (_grid.Items.Count == 0) return;
        if (delta == int.MaxValue) { _grid.SelectedIndex = _grid.Items.Count - 1; _grid.ScrollIntoView(_grid.SelectedItem); return; }
        if (delta == 0) { _grid.SelectedIndex = 0; _grid.ScrollIntoView(_grid.SelectedItem); return; }
        _grid.SelectedIndex = Math.Clamp(_grid.SelectedIndex + delta, 0, _grid.Items.Count - 1);
        _grid.ScrollIntoView(_grid.SelectedItem);
    }

    private static Button SmallButton(string text) => new() { Content = text, Width = 32, Margin = new Thickness(3, 0, 0, 0) };
}

internal sealed class JournalEntryEditWindow : Window
{
    private readonly DatePicker _date = new() { SelectedDate = DateTime.Today, Width = 125 };
    private readonly TextBox _memo = new();
    private readonly ObservableCollection<JournalLineDraft> _lines = [];
    private readonly DataGrid _lineGrid = new() { AutoGenerateColumns = false, CanUserAddRows = false, RowHeight = 24, MinHeight = 210 };
    private readonly TextBlock _debit = new() { FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Right };
    private readonly TextBlock _credit = new() { FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Right };
    private readonly TextBlock _balance = new() { FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Right };
    private readonly TextBlock _status = new() { Foreground = Brushes.Maroon, TextWrapping = TextWrapping.Wrap };
    public Guid? SavedId { get; private set; }

    public JournalEntryEditWindow()
    {
        Title = "Tambah Jurnal Umum";
        Width = 820; Height = 560; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Content = Build();
        ConfigureColumns();
        _lines.Add(new JournalLineDraft());
        _lines.Add(new JournalLineDraft());
        _lineGrid.ItemsSource = _lines;
        _lineGrid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(UpdateTotals);
        UpdateTotals();
    }

    private UIElement Build()
    {
        var root = new Grid { Margin = new Thickness(10) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        var dateLabel = LedgerAccountView.Label("Tanggal");
        Grid.SetColumn(dateLabel, 0); header.Children.Add(dateLabel);
        Grid.SetColumn(_date, 1); header.Children.Add(_date);
        var memoLabel = LedgerAccountView.Label("Keterangan"); Grid.SetColumn(memoLabel, 2); header.Children.Add(memoLabel);
        Grid.SetColumn(_memo, 3); header.Children.Add(_memo);
        root.Children.Add(header);
        Grid.SetRow(_lineGrid, 1); _lineGrid.Margin = new Thickness(0, 8, 0, 8); root.Children.Add(_lineGrid);

        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        var add = LedgerAccountView.Button("Tambah Baris"); add.Click += (_, _) => { _lines.Add(new JournalLineDraft()); UpdateTotals(); };
        var lookup = LedgerAccountView.Button("Lookup Akun"); lookup.Click += (_, _) => LookupLineAccount();
        var remove = LedgerAccountView.Button("Hapus Baris"); remove.Click += (_, _) => { if (_lineGrid.SelectedItem is JournalLineDraft line) _lines.Remove(line); UpdateTotals(); };
        tools.Children.Add(add); tools.Children.Add(lookup); tools.Children.Add(remove);
        Grid.SetRow(tools, 2); root.Children.Add(tools);

        var footer = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(_status);
        AddTotal(footer, 1, "Debit", _debit);
        AddTotal(footer, 2, "Kredit", _credit);
        AddTotal(footer, 3, "Selisih", _balance);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "Simpan", Width = 90, IsDefault = true, Margin = new Thickness(6, 0, 0, 0) }; save.Click += Save_Click;
        buttons.Children.Add(save); buttons.Children.Add(new Button { Content = "Batal", Width = 90, IsCancel = true, Margin = new Thickness(6, 0, 0, 0) });
        Grid.SetColumn(buttons, 4); footer.Children.Add(buttons);
        Grid.SetRow(footer, 3); root.Children.Add(footer);
        return root;
    }

    private void ConfigureColumns()
    {
        _lineGrid.Columns.Add(new DataGridTextColumn { Header = "Kode Perkiraan", Binding = new Binding(nameof(JournalLineDraft.AccountCode)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 110 });
        _lineGrid.Columns.Add(new DataGridTextColumn { Header = "Nama Perkiraan", Binding = new Binding(nameof(JournalLineDraft.AccountName)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _lineGrid.Columns.Add(new DataGridTextColumn { Header = "Keterangan", Binding = new Binding(nameof(JournalLineDraft.Description)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 190 });
        _lineGrid.Columns.Add(new DataGridTextColumn { Header = "Debit", Binding = new Binding(nameof(JournalLineDraft.Debit)) { StringFormat = "N2", UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 110, ElementStyle = LedgerAccountView.RightAlignedStyle() });
        _lineGrid.Columns.Add(new DataGridTextColumn { Header = "Kredit", Binding = new Binding(nameof(JournalLineDraft.Credit)) { StringFormat = "N2", UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 110, ElementStyle = LedgerAccountView.RightAlignedStyle() });
    }

    private void LookupLineAccount()
    {
        CommitLineGridEdit();
        var line = _lineGrid.SelectedItem as JournalLineDraft;
        if (line is null)
        {
            line = new JournalLineDraft();
            _lines.Add(line);
            _lineGrid.SelectedItem = line;
        }
        var lookup = new LedgerAccountLookupWindow { Owner = this };
        if (lookup.ShowDialog() == true && lookup.SelectedAccount is not null)
        {
            line.AccountCode = lookup.SelectedAccount.Code;
            line.AccountName = lookup.SelectedAccount.Name;
            RefreshLineGridSafely();
        }
    }

    private void CommitLineGridEdit()
    {
        _lineGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        _lineGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private void RefreshLineGridSafely()
    {
        if (TryCommitLineGridEdit()) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            TryCommitLineGridEdit();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private bool TryCommitLineGridEdit()
    {
        try
        {
            CommitLineGridEdit();
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

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CommitLineGridEdit();
            var requestLines = _lines
                .Where(x => !string.IsNullOrWhiteSpace(x.AccountCode) && (x.Debit != 0 || x.Credit != 0))
                .Select(x => new JournalEntryLineRequest(x.AccountCode, string.IsNullOrWhiteSpace(x.Description) ? null : x.Description, x.Debit, x.Credit))
                .ToArray();
            IsEnabled = false; _status.Text = "Menyimpan jurnal...";
            var saved = await AksaraApiClient.Shared.CreateJournalEntryAsync(new CreateJournalEntryRequest(
                DateOnly.FromDateTime(_date.SelectedDate ?? DateTime.Today), _memo.Text, AksaraApiClient.Shared.CurrentUserName, Environment.MachineName, requestLines));
            SavedId = saved.Id;
            DialogResult = true;
        }
        catch (Exception exception) { _status.Text = exception.Message; ViewError.ShowWarningUnlessDatabaseUnavailable(exception, Title); }
        finally { IsEnabled = true; }
    }

    private void UpdateTotals()
    {
        var debit = _lines.Sum(x => x.Debit);
        var credit = _lines.Sum(x => x.Credit);
        _debit.Text = debit.ToString("N2");
        _credit.Text = credit.ToString("N2");
        _balance.Text = (debit - credit).ToString("N2");
        _balance.Foreground = debit == credit ? Brushes.DarkGreen : Brushes.Maroon;
    }

    private static void AddTotal(Grid grid, int column, string label, TextBlock value)
    {
        var panel = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.DimGray, FontSize = 10 });
        panel.Children.Add(value);
        Grid.SetColumn(panel, column); grid.Children.Add(panel);
    }
}

internal sealed class JournalLineDraft : INotifyPropertyChanged
{
    private string _accountCode = string.Empty;
    private string _accountName = string.Empty;
    private string? _description;
    private decimal _debit;
    private decimal _credit;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string AccountCode { get => _accountCode; set { _accountCode = value?.Trim().ToUpperInvariant() ?? string.Empty; Changed(nameof(AccountCode)); } }
    public string AccountName { get => _accountName; set { _accountName = value ?? string.Empty; Changed(nameof(AccountName)); } }
    public string? Description { get => _description; set { _description = value; Changed(nameof(Description)); } }
    public decimal Debit { get => _debit; set { _debit = Math.Max(0, value); if (_debit > 0) _credit = 0; Changed(nameof(Debit)); Changed(nameof(Credit)); } }
    public decimal Credit { get => _credit; set { _credit = Math.Max(0, value); if (_credit > 0) _debit = 0; Changed(nameof(Credit)); Changed(nameof(Debit)); } }
    private void Changed(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed class JournalEntryDetailWindow : Window
{
    public JournalEntryDetailWindow(JournalEntryDto journal)
    {
        Title = $"Rincian Jurnal {journal.DocumentNumber}";
        Width = 820; Height = 470; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var root = new Grid { Margin = new Thickness(10) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = $"{journal.DocumentDate:dd/MM/yyyy}  {journal.DocumentNumber}  {journal.Memo}", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) });
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, ItemsSource = journal.Lines, RowHeight = 24 };
        grid.Columns.Add(LedgerAccountView.Column("No", "LineNumber", 45));
        grid.Columns.Add(LedgerAccountView.Column("Kode", "AccountCode", 90));
        grid.Columns.Add(LedgerAccountView.Column("Nama Perkiraan", "AccountName", new DataGridLength(1, DataGridLengthUnitType.Star)));
        grid.Columns.Add(LedgerAccountView.Column("Keterangan", "Description", 190));
        grid.Columns.Add(LedgerAccountView.NumberColumn("Debit", "Debit", 110));
        grid.Columns.Add(LedgerAccountView.NumberColumn("Kredit", "Credit", 110));
        Grid.SetRow(grid, 1); root.Children.Add(grid);
        var close = new Button { Content = "Tutup", Width = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), IsCancel = true };
        Grid.SetRow(close, 2); root.Children.Add(close);
        Content = root;
    }
}

public sealed class LedgerMappingView : UserControl
{
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single, RowHeight = 23 };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    public event EventHandler? CloseRequested;

    public LedgerMappingView()
    {
        _grid.Columns.Add(LedgerAccountView.Column("Kode Transaksi", "MappingCode", 160));
        _grid.Columns.Add(LedgerAccountView.Column("Nama Transaksi", "MappingName", new DataGridLength(1, DataGridLengthUnitType.Star)));
        _grid.Columns.Add(LedgerAccountView.Column("Kode Perkiraan", "AccountCode", 115));
        _grid.Columns.Add(LedgerAccountView.Column("Nama Perkiraan", "AccountName", 220));
        Content = Build();
        Loaded += async (_, _) => await ReloadAsync();
        _grid.MouseDoubleClick += async (_, _) => await EditAsync();
    }

    private UIElement Build()
    {
        var root = LedgerAccountView.Shell("PENETAPAN KODE PERKIRAAN JURNAL TRANSAKSI", _status, out var footer);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 6) };
        var edit = LedgerAccountView.Button("Hubungkan"); edit.Click += async (_, _) => await EditAsync();
        var refresh = LedgerAccountView.Button("Muat Ulang"); refresh.Click += async (_, _) => await ReloadAsync();
        tools.Children.Add(edit); tools.Children.Add(refresh); Grid.SetRow(tools, 1); root.Children.Add(tools);
        Grid.SetRow(_grid, 2); root.Children.Add(_grid);
        footer.Children.Add(LedgerAccountView.CloseButton(() => CloseRequested?.Invoke(this, EventArgs.Empty)));
        return root;
    }

    private async Task ReloadAsync()
    {
        try
        {
            IsEnabled = false; _status.Text = "Mengambil mapping akun transaksi...";
            var rows = await _api.GetLedgerTransactionMappingsAsync();
            _grid.ItemsSource = rows;
            _status.Text = $"{rows.Count:N0} mapping | Dipakai sebagai jembatan transaksi penjualan, pembelian, persediaan, dan kas-bank.";
        }
        catch (Exception exception) { _status.Text = exception.Message; ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Mapping Jurnal"); }
        finally { IsEnabled = true; }
    }

    private async Task EditAsync()
    {
        if (_grid.SelectedItem is not LedgerTransactionMappingDto row) return;
        var lookup = new LedgerAccountLookupWindow { Owner = Window.GetWindow(this) };
        if (lookup.ShowDialog() != true || lookup.SelectedAccount is null) return;
        try
        {
            IsEnabled = false; _status.Text = "Menyimpan mapping...";
            await _api.SaveLedgerTransactionMappingAsync(new SaveLedgerTransactionMappingRequest(row.MappingCode, lookup.SelectedAccount.Code, row.Version, AksaraApiClient.Shared.CurrentUserName, Environment.MachineName));
            await ReloadAsync();
        }
        catch (Exception exception) { _status.Text = exception.Message; ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Mapping Jurnal"); }
        finally { IsEnabled = true; }
    }
}

public sealed class LedgerReportView : UserControl
{
    private static readonly CultureInfo IndonesianCulture = CultureInfo.GetCultureInfo("id-ID");
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly LedgerReportKind _kind;
    private readonly DatePicker _from = new() { SelectedDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), Width = 122 };
    private readonly DatePicker _to = new() { SelectedDate = DateTime.Today, Width = 122 };
    private readonly TextBox _query = new() { MinWidth = 210 };
    private readonly TextBox _account = new() { Width = 100 };
    private readonly CheckBox _includeZero = new() { Content = "Tampilkan nilai kosong", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
    private readonly FlowDocument _document = new()
    {
        FontFamily = new FontFamily("Tahoma"),
        FontSize = 9,
        PagePadding = new Thickness(28, 24, 28, 24),
        // Keep enough printable width for the widest ledger tables; the viewer
        // still exposes horizontal scrolling when the window is narrower.
        PageWidth = 900,
        ColumnWidth = double.PositiveInfinity,
        Background = Brushes.White
    };
    private readonly FlowDocumentScrollViewer _viewer = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Background = LedgerAccountView.Brush("#4A4A4A"),
        Zoom = 110
    };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private object? _lastReport;
    public event EventHandler? CloseRequested;
    public string ReportTitle => _kind switch
    {
        LedgerReportKind.AccountList => "Laporan Daftar Perkiraan",
        LedgerReportKind.Journal => "Laporan Jurnal Umum",
        LedgerReportKind.OpeningJournal => "Laporan Jurnal Awal",
        LedgerReportKind.TransactionJournal => "Laporan Jurnal Transaksi",
        LedgerReportKind.GeneralLedger => "Buku Besar",
        LedgerReportKind.TrialBalance => "Neraca Saldo",
        LedgerReportKind.IncomeStatement => "Laba Rugi",
        LedgerReportKind.BalanceSheetSkontro => "Neraca Skontro",
        _ => "Neraca Staffel"
    };

    public LedgerReportView(LedgerReportKind kind, ReportFilterSelection? initialFilter = null)
    {
        _kind = kind;
        if (initialFilter is not null)
        {
            _from.SelectedDate = initialFilter.From.ToDateTime(TimeOnly.MinValue);
            _to.SelectedDate = initialFilter.To.ToDateTime(TimeOnly.MinValue);
            _query.Text = initialFilter.Query ?? string.Empty;
            _includeZero.IsChecked = initialFilter.IncludeZero;
        }
        Content = Build();
        Loaded += async (_, _) => await ReloadAsync();
    }

    private UIElement Build()
    {
        var root = LedgerAccountView.Shell(ReportTitle.ToUpperInvariant(), _status, out var footer);
        var filters = new WrapPanel { Margin = new Thickness(0, 6, 0, 6), VerticalAlignment = VerticalAlignment.Center };
        filters.Children.Add(Text("Periode")); filters.Children.Add(_from); filters.Children.Add(Text("s/d")); filters.Children.Add(_to);
        filters.Children.Add(Text("Kode Akun")); filters.Children.Add(_account);
        filters.Children.Add(Text("Cari")); filters.Children.Add(_query);
        var lookup = new Button { Content = "...", Width = 34, Margin = new Thickness(4, 0, 4, 0) }; lookup.Click += (_, _) => LookupAccount();
        filters.Children.Add(lookup);
        filters.Children.Add(_includeZero);
        var show = LedgerAccountView.Button("Tampilkan"); show.Click += async (_, _) => await ReloadAsync();
        filters.Children.Add(show);
        _account.IsEnabled = _kind == LedgerReportKind.GeneralLedger;
        lookup.IsEnabled = _kind == LedgerReportKind.GeneralLedger;
        _from.IsEnabled = _kind != LedgerReportKind.AccountList;
        _to.IsEnabled = _kind != LedgerReportKind.AccountList;
        _includeZero.IsEnabled = _kind is LedgerReportKind.TrialBalance or LedgerReportKind.IncomeStatement or LedgerReportKind.BalanceSheetSkontro or LedgerReportKind.BalanceSheetStaffel;
        Grid.SetRow(filters, 1); root.Children.Add(filters);
        _viewer.Document = _document;
        Grid.SetRow(_viewer, 2); root.Children.Add(_viewer);
        var copy = LedgerAccountView.Button("Salin CSV"); copy.Click += (_, _) => CopyCsv();
        var excel = LedgerAccountView.Button("Excel"); excel.Click += (_, _) => ExportExcel();
        var print = LedgerAccountView.Button("Cetak"); print.Click += (_, _) => PrintReport();
        footer.Children.Add(copy);
        footer.Children.Add(excel);
        footer.Children.Add(print);
        footer.Children.Add(LedgerAccountView.CloseButton(() => CloseRequested?.Invoke(this, EventArgs.Empty)));
        return root;
    }

    private async Task ReloadAsync()
    {
        try
        {
            IsEnabled = false; _status.Text = "Mengambil laporan ledger...";
            var from = DateOnly.FromDateTime(_from.SelectedDate ?? DateTime.Today);
            var to = DateOnly.FromDateTime(_to.SelectedDate ?? DateTime.Today);
            var includeZero = _includeZero.IsChecked == true;
            object report = _kind switch
            {
                LedgerReportKind.AccountList => await _api.GetLedgerAccountReportAsync(_query.Text),
                LedgerReportKind.Journal => await _api.GetLedgerJournalReportAsync(from, to, _query.Text),
                LedgerReportKind.OpeningJournal => await _api.GetLedgerJournalReportAsync(from, to, _query.Text, "OPENING"),
                LedgerReportKind.TransactionJournal => await _api.GetLedgerJournalReportAsync(from, to, _query.Text, "TRANSACTION"),
                LedgerReportKind.GeneralLedger => await _api.GetGeneralLedgerReportAsync(from, to, _account.Text, _query.Text),
                LedgerReportKind.TrialBalance => await _api.GetTrialBalanceReportAsync(from, to, _query.Text, includeZero),
                LedgerReportKind.IncomeStatement => await _api.GetIncomeStatementReportAsync(from, to, _query.Text, includeZero),
                _ => await _api.GetBalanceSheetReportAsync(from, to, _query.Text, includeZero)
            };
            _lastReport = report;
            RenderReport(report, from, to);
            var totals = report.GetType().GetProperty("Totals")?.GetValue(report);
            _status.Text = FormatTotals(totals);
        }
        catch (Exception exception) { _status.Text = exception.Message; ViewError.ShowWarningUnlessDatabaseUnavailable(exception, $"AKSARA - {ReportTitle}"); }
        finally { IsEnabled = true; }
    }

    private void LookupAccount()
    {
        var lookup = new LedgerAccountLookupWindow { Owner = Window.GetWindow(this) };
        if (lookup.ShowDialog() == true && lookup.SelectedAccount is not null) _account.Text = lookup.SelectedAccount.Code;
    }

    private void CopyCsv()
    {
        if (_lastReport?.GetType().GetProperty("Rows")?.GetValue(_lastReport) is not System.Collections.IEnumerable rows) return;
        var items = rows.Cast<object>().ToList();
        if (items.Count == 0) return;
        var properties = items[0].GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(';', properties.Select(x => Csv(x.Name))));
        foreach (var row in items) csv.AppendLine(string.Join(';', properties.Select(x => Csv(Convert.ToString(x.GetValue(row), CultureInfo.CurrentCulture) ?? ""))));
        Clipboard.SetText(csv.ToString());
        _status.Text = $"{items.Count:N0} baris disalin sebagai CSV.";
    }

    private void ExportExcel()
    {
        if (_lastReport?.GetType().GetProperty("Rows")?.GetValue(_lastReport) is not System.Collections.IEnumerable rows) return;
        ReportGridTools.ExportObjectsToExcel(ReportTitle, rows.Cast<object>());
    }

    private void PrintReport()
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() == true)
            dialog.PrintDocument(((IDocumentPaginatorSource)_document).DocumentPaginator, ReportTitle);
    }

    private void RenderReport(object report, DateOnly from, DateOnly to)
    {
        _document.Blocks.Clear();
        AddReportHeader(from, to);
        switch (_kind)
        {
            case LedgerReportKind.AccountList:
                RenderAccountList(((LedgerReportDto<LedgerAccountDto>)report).Rows);
                break;
            case LedgerReportKind.Journal:
            case LedgerReportKind.OpeningJournal:
            case LedgerReportKind.TransactionJournal:
                RenderJournal(((LedgerReportDto<LedgerJournalReportRowDto>)report).Rows, ((LedgerReportDto<LedgerJournalReportRowDto>)report).Totals);
                break;
            case LedgerReportKind.GeneralLedger:
                RenderGeneralLedger(((LedgerReportDto<GeneralLedgerReportRowDto>)report).Rows);
                break;
            case LedgerReportKind.TrialBalance:
                RenderTrialBalance(((LedgerReportDto<TrialBalanceReportRowDto>)report).Rows, ((LedgerReportDto<TrialBalanceReportRowDto>)report).Totals);
                break;
            case LedgerReportKind.IncomeStatement:
                RenderIncomeStatement(((LedgerReportDto<IncomeStatementReportRowDto>)report).Rows, ((LedgerReportDto<IncomeStatementReportRowDto>)report).Totals);
                break;
            default:
                RenderBalanceSheet(((LedgerReportDto<BalanceSheetReportRowDto>)report).Rows, ((LedgerReportDto<BalanceSheetReportRowDto>)report).Totals);
                break;
        }
        AddReportFooter();
    }

    private void AddReportHeader(DateOnly from, DateOnly to)
    {
        _document.Blocks.Add(new Paragraph(new Run(ProgramConfigurationCache.PrintCompanyName))
        {
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4)
        });
        var header = new Table { CellSpacing = 0 };
        // Symmetrical side columns keep the report title centered on the paper
        // while leaving page numbering at the right edge.
        header.Columns.Add(new TableColumn { Width = new GridLength(80) });
        header.Columns.Add(new TableColumn { Width = new GridLength(540) });
        header.Columns.Add(new TableColumn { Width = new GridLength(80) });
        var group = new TableRowGroup();
        var row = new TableRow();
        var title = new Paragraph(new Run(ReportTitle.ToUpperInvariant()))
        {
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0)
        };
        var headingBorder = new Thickness(0, 0, 0, 2);
        row.Cells.Add(new TableCell(new Paragraph()) { BorderThickness = headingBorder, BorderBrush = Brushes.Black });
        row.Cells.Add(new TableCell(title) { BorderThickness = headingBorder, BorderBrush = Brushes.Black, Padding = new Thickness(0, 0, 0, 3) });
        row.Cells.Add(new TableCell(new Paragraph(new Run("Hal. : 1")) { TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 4, 0, 0) })
        { BorderThickness = headingBorder, BorderBrush = Brushes.Black });
        group.Rows.Add(row);
        var filter = _kind == LedgerReportKind.AccountList
            ? "Ket.Filter : Semua Perkiraan"
            : BuildReportPeriodCaption(ReportTitle, from, to);
        var financialStatement = IsPeriodEndingStatement(ReportTitle) || IsBalanceStatement(ReportTitle);
        var filterRow = new TableRow();
        filterRow.Cells.Add(new TableCell(new Paragraph(new Run(filter))
        {
            FontSize = financialStatement ? 10 : 8,
            FontWeight = financialStatement ? FontWeights.SemiBold : FontWeights.Normal,
            TextAlignment = financialStatement ? TextAlignment.Center : TextAlignment.Left,
            Margin = new Thickness(0, financialStatement ? 5 : 2, 0, 8)
        }) { ColumnSpan = 3 });
        group.Rows.Add(filterRow);
        header.RowGroups.Add(group);
        _document.Blocks.Add(header);
    }

    private void RenderAccountList(IEnumerable<LedgerAccountDto> rows)
    {
        var table = ReportTable(100, 340, 90, 50, 75, 75);
        AddHeader(table, "NO PERKIRAAN", "NAMA PERKIRAAN", "JENIS", "D/K", "INDUK", "POSTING");
        foreach (var row in rows)
            AddRow(table, false, row.Code, row.Name, AccountTypeLabel(row.AccountType), row.NormalBalance, row.ParentCode ?? "", row.IsPostable ? "Ya" : "Tidak");
        _document.Blocks.Add(table);
    }

    private void RenderJournal(IReadOnlyList<LedgerJournalReportRowDto> rows, LedgerReportTotalsDto totals)
    {
        var table = ReportTable(70, 95, 80, 165, 155, 80, 80);
        AddHeader(table, "TANGGAL", "NO. BUKTI", "NO PERK.", "NAMA PERKIRAAN", "KETERANGAN", "DEBET", "KREDIT");
        foreach (var row in rows)
            AddRow(table, false, Date(row.DocumentDate), row.DocumentNumber, row.AccountCode, row.AccountName, row.Description ?? "", Money(row.Debit), Money(row.Credit));
        AddRow(table, true, "", "", "", "TOTAL", "", Money(totals.Debit), Money(totals.Credit));
        _document.Blocks.Add(table);
    }

    private void RenderGeneralLedger(IReadOnlyList<GeneralLedgerReportRowDto> rows)
    {
        var table = ReportTable(65, 90, 75, 145, 150, 75, 75, 85);
        AddHeader(table, "TANGGAL", "NO. BUKTI", "NO PERK.", "NAMA PERKIRAAN", "KETERANGAN", "DEBET", "KREDIT", "SALDO");
        foreach (var row in rows)
            AddRow(table, false, Date(row.DocumentDate), row.DocumentNumber, row.AccountCode, row.AccountName, row.Description ?? "", Money(row.Debit), Money(row.Credit), Money(row.Balance));
        _document.Blocks.Add(table);
    }

    private void RenderTrialBalance(IReadOnlyList<TrialBalanceReportRowDto> rows, LedgerReportTotalsDto totals)
    {
        var table = ReportTable(110, 370, 110, 110);
        AddHeader(table, "NO PERKIRAAN", "NAMA PERKIRAAN", "DEBET", "KREDIT");
        foreach (var row in rows)
            AddRow(table, false, row.AccountCode, row.AccountName, Money(row.Debit), Money(row.Credit));
        AddRow(table, true, "", "Rp.", Money(totals.Debit), Money(totals.Credit));
        _document.Blocks.Add(table);
        if (totals.Debit != totals.Credit)
            _document.Blocks.Add(new Paragraph(new Run($"PERINGATAN: Neraca saldo belum balance sebesar {Money(totals.Debit - totals.Credit)}.")) { Foreground = Brushes.Maroon, FontWeight = FontWeights.Bold });
    }

    private void RenderIncomeStatement(IReadOnlyList<IncomeStatementReportRowDto> rows, LedgerReportTotalsDto totals)
    {
        var table = ReportTable(90, 330, 105, 105, 105);
        AddHeader(table, "NO PERK.", "NAMA PERKIRAAN", "PENDAPATAN", "BIAYA/HPP", "NETTO");
        foreach (var group in rows.GroupBy(x => x.AccountType).OrderBy(x => IncomeGroupOrder(x.Key)))
        {
            AddRow(table, true, "", AccountTypeLabel(group.Key).ToUpperInvariant(), "", "", "");
            foreach (var row in group)
                AddRow(table, false, row.AccountCode, row.AccountName, Money(row.Revenue), Money(row.Expense), Money(row.NetAmount));
        }
        AddRow(table, true, "", "TOTAL PENDAPATAN", Money(totals.Debit), "", "");
        AddRow(table, true, "", "TOTAL BIAYA/HPP", "", Money(totals.Credit), "");
        AddRow(table, true, "", totals.Balance >= 0 ? "LABA/RUGI BERSIH" : "RUGI BERSIH", "", "", Money(totals.Balance));
        _document.Blocks.Add(table);
    }

    private void RenderBalanceSheet(IReadOnlyList<BalanceSheetReportRowDto> rows, LedgerReportTotalsDto totals)
    {
        if (_kind == LedgerReportKind.BalanceSheetStaffel)
        {
            RenderBalanceSheetStaffel(rows, totals);
            return;
        }

        var left = BalanceLines(rows.Where(x => x.AccountType == "ASSET"), "AKTIVA", "TOTAL AKTIVA");
        var right = BalanceLines(rows.Where(x => x.AccountType == "LIABILITY"), "HUTANG", "TOTAL HUTANG")
            .Concat(BalanceLines(rows.Where(x => x.AccountType == "EQUITY"), "MODAL", "TOTAL MODAL"))
            .ToList();
        right.Add(new ReportLine("TOTAL HUTANG & MODAL", totals.Credit, true));
        var max = Math.Max(left.Count, right.Count);
        var table = ReportTable(250, 95, 250, 95);
        AddHeader(table, "AKTIVA", "JUMLAH", "HUTANG / MODAL", "JUMLAH");
        for (var i = 0; i < max; i++)
        {
            var l = i < left.Count ? left[i] : ReportLine.Empty;
            var r = i < right.Count ? right[i] : ReportLine.Empty;
            AddRow(table, l.Bold || r.Bold, l.Label, Money(l.Amount), r.Label, Money(r.Amount));
        }
        _document.Blocks.Add(table);
        AddBalanceWarning(totals);
    }

    private void RenderBalanceSheetStaffel(IReadOnlyList<BalanceSheetReportRowDto> rows, LedgerReportTotalsDto totals)
    {
        var table = ReportTable(100, 390, 120);
        AddHeader(table, "NO PERK.", "NAMA PERKIRAAN", "JUMLAH");
        foreach (var group in rows.GroupBy(x => x.AccountType).OrderBy(x => BalanceGroupOrder(x.Key)))
        {
            AddRow(table, true, "", AccountTypeLabel(group.Key).ToUpperInvariant(), "");
            foreach (var row in group.OrderBy(x => x.AccountCode))
                AddRow(table, false, row.AccountCode, row.AccountName, Money(row.Balance));
            AddRow(table, true, "", $"TOTAL {AccountTypeLabel(group.Key).ToUpperInvariant()}", Money(group.Sum(x => x.Balance)));
        }
        AddRow(table, true, "", "TOTAL AKTIVA", Money(totals.Debit));
        AddRow(table, true, "", "TOTAL HUTANG & MODAL", Money(totals.Credit));
        _document.Blocks.Add(table);
        AddBalanceWarning(totals);
    }

    private void AddBalanceWarning(LedgerReportTotalsDto totals)
    {
        if (totals.Balance != 0)
            _document.Blocks.Add(new Paragraph(new Run($"PERINGATAN: Aktiva dan Hutang+Modal selisih {Money(totals.Balance)}. Sumber jurnal tetap balance; cek klasifikasi akun/mapping CPS.")) { Foreground = Brushes.Maroon, FontWeight = FontWeights.Bold });
    }

    private static List<ReportLine> BalanceLines(IEnumerable<BalanceSheetReportRowDto> rows, string heading, string totalLabel)
    {
        var list = new List<ReportLine> { new(heading, 0, true) };
        var ordered = rows.OrderBy(x => x.AccountCode).ToList();
        list.AddRange(ordered.Select(x => new ReportLine($"{x.AccountCode}  {x.AccountName}", x.Balance, false)));
        list.Add(new(totalLabel, ordered.Sum(x => x.Balance), true));
        return list;
    }

    private void AddReportFooter()
    {
        var printed = $"Dicetak oleh : {AksaraApiClient.Shared.CurrentUserName}    Tanggal {DateTime.Now:dd/MM/yyyy HH:mm:ss}";
        _document.Blocks.Add(new Paragraph(new Run(printed)) { FontSize = 7, Margin = new Thickness(0, 14, 0, 0) });
    }

    private static Table ReportTable(params double[] widths)
    {
        var table = new Table { CellSpacing = 0, BorderBrush = Brushes.Black, BorderThickness = new Thickness(0, 1, 0, 1), Margin = new Thickness(0, 0, 0, 8) };
        foreach (var width in widths) table.Columns.Add(new TableColumn { Width = new GridLength(width) });
        table.RowGroups.Add(new TableRowGroup());
        return table;
    }

    private static void AddHeader(Table table, params string[] values) => AddRow(table, true, values);

    private static void AddRow(Table table, bool bold, params string[] values)
    {
        var row = new TableRow();
        foreach (var value in values)
        {
            var paragraph = new Paragraph(new Run(value)) { Margin = new Thickness(2, 1, 2, 1), TextAlignment = IsNumeric(value) ? TextAlignment.Right : TextAlignment.Left };
            row.Cells.Add(new TableCell(paragraph) { Padding = new Thickness(1), FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, BorderBrush = Brushes.Black, BorderThickness = bold ? new Thickness(0, 0, 0, 1) : new Thickness(0) });
        }
        table.RowGroups[0].Rows.Add(row);
    }

    private static string FormatTotals(object? totals)
    {
        if (totals is null) return "Laporan siap.";
        var type = totals.GetType();
        string Value(string name) => type.GetProperty(name)?.GetValue(totals) is decimal value ? value.ToString("N2") : type.GetProperty(name)?.GetValue(totals)?.ToString() ?? "0";
        return $"{Value("RowCount")} baris | Debit {Value("Debit")} | Kredit {Value("Credit")} | Saldo {Value("Balance")}";
    }

    private static int IncomeGroupOrder(string type) => type switch { "REVENUE" => 0, "COGS" => 1, "EXPENSE" => 2, _ => 9 };
    private static int BalanceGroupOrder(string type) => type switch { "ASSET" => 0, "LIABILITY" => 1, "EQUITY" => 2, _ => 9 };
    internal static string BuildReportPeriodCaption(string reportTitle, DateOnly from, DateOnly to)
    {
        var fromText = from.ToString("dd MMMM yyyy", IndonesianCulture);
        var toText = to.ToString("dd MMMM yyyy", IndonesianCulture);
        if (IsPeriodEndingStatement(reportTitle))
            return $"Untuk periode yang berakhir pada {toText}".ToUpper(IndonesianCulture);
        if (IsBalanceStatement(reportTitle))
            return $"Periode {fromText} s/d {toText}".ToUpper(IndonesianCulture);
        return $"Ket.Filter : Periode {from:dd/MM/yyyy} s/d {to:dd/MM/yyyy}";
    }
    private static bool IsPeriodEndingStatement(string title) =>
        title.Equals("Laba Rugi", StringComparison.OrdinalIgnoreCase)
        || title.Equals("Arus Kas", StringComparison.OrdinalIgnoreCase)
        || title.Equals("Perubahan Ekuitas", StringComparison.OrdinalIgnoreCase);
    private static bool IsBalanceStatement(string title) => title.StartsWith("Neraca", StringComparison.OrdinalIgnoreCase);
    private static string Date(DateOnly value) => value.ToString("dd/MM/yyyy", IndonesianCulture);
    private static string Money(decimal value) => value == 0 ? "0" : value.ToString("#,0.##;(#,0.##)", IndonesianCulture);
    private static bool IsNumeric(string value) => decimal.TryParse(value.Replace("(", "").Replace(")", ""), NumberStyles.Number, IndonesianCulture, out _);
    private static string AccountTypeLabel(string type) => type switch
    {
        "ASSET" => "Aktiva",
        "LIABILITY" => "Hutang",
        "EQUITY" => "Modal",
        "REVENUE" => "Pendapatan",
        "COGS" => "Harga Pokok",
        "EXPENSE" => "Biaya",
        _ => type
    };
    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    private static TextBlock Text(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 5, 0) };
    private sealed record ReportLine(string Label, decimal Amount, bool Bold)
    {
        public static ReportLine Empty { get; } = new("", 0, false);
    }
}

internal sealed class LedgerAccountLookupWindow : Window
{
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly bool _onlyPostable;
    private readonly string? _accountType;
    private readonly bool _cashBankOnly;
    private readonly TextBox _search = new() { Width = 250, Margin = new Thickness(4, 0, 8, 0) };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, RowHeight = 23, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBlock _status = new();
    public LedgerAccountDto? SelectedAccount { get; private set; }

    public LedgerAccountLookupWindow(bool onlyPostable = true, string? accountType = null, bool cashBankOnly = false)
    {
        _onlyPostable = onlyPostable; _accountType = accountType; _cashBankOnly = cashBankOnly;
        Title = "Cari Kode Perkiraan";
        Width = 690; Height = 470; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Content = Build();
        Loaded += async (_, _) => await ReloadAsync();
        _search.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await ReloadAsync(); } };
        _grid.MouseDoubleClick += (_, _) => Accept();
    }

    private UIElement Build()
    {
        _grid.Columns.Add(LedgerAccountView.Column("Kode", "Code", 95));
        _grid.Columns.Add(LedgerAccountView.Column("Nama Perkiraan", "Name", new DataGridLength(1, DataGridLengthUnitType.Star)));
        _grid.Columns.Add(LedgerAccountView.Column("Jenis", "AccountType", 90));
        _grid.Columns.Add(LedgerAccountView.Column("D/K", "NormalBalance", 45));
        var root = new Grid { Margin = new Thickness(10) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var tools = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
        var refresh = LedgerAccountView.Button("Cari"); refresh.Click += async (_, _) => await ReloadAsync();
        DockPanel.SetDock(refresh, Dock.Right); tools.Children.Add(refresh);
        tools.Children.Add(LedgerAccountView.Label("Kode/Nama")); tools.Children.Add(_search);
        root.Children.Add(tools);
        Grid.SetRow(_grid, 1); root.Children.Add(_grid);
        var footer = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "Pilih", Width = 90, IsDefault = true, Margin = new Thickness(4) }; ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok); buttons.Children.Add(new Button { Content = "Batal", Width = 90, IsCancel = true, Margin = new Thickness(4) });
        Grid.SetColumn(buttons, 1); footer.Children.Add(buttons);
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        return root;
    }

    private async Task ReloadAsync()
    {
        try
        {
            var rows = (await _api.GetLedgerAccountsAsync(_search.Text, true, _onlyPostable, _accountType)).Where(x => !_cashBankOnly || x.IsCashBank).ToList();
            _grid.ItemsSource = rows;
            if (rows.Count > 0) _grid.SelectedIndex = 0;
            _status.Text = $"{rows.Count:N0} perkiraan ditemukan.";
        }
        catch (Exception exception) { _status.Text = exception.Message; }
    }

    private void Accept()
    {
        if (_grid.SelectedItem is not LedgerAccountDto row) return;
        SelectedAccount = row;
        DialogResult = true;
    }
}
