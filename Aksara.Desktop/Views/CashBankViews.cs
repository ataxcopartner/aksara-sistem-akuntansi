using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Aksara.Contracts;
using Aksara.Desktop.Controls;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

public enum CashBankMasterKind { Cash, Bank, Expense, Income }

public sealed class CashBankMasterView : UserControl
{
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly CashBankMasterKind _kind;
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, RowHeight = 23, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    public event EventHandler? CloseRequested;
    public string ViewTitle => _kind switch
    {
        CashBankMasterKind.Cash => "Daftar Kas",
        CashBankMasterKind.Bank => "Daftar Bank",
        CashBankMasterKind.Expense => "Daftar Biaya",
        _ => "Daftar Penghasilan Lain"
    };

    public CashBankMasterView(CashBankMasterKind kind)
    {
        _kind = kind;
        BuildColumns();
        Content = Build();
        Loaded += async (_, _) => await ReloadAsync();
        _grid.MouseDoubleClick += async (_, _) => await EditAsync();
    }

    private UIElement Build()
    {
        var root = LedgerAccountView.Shell(ViewTitle.ToUpperInvariant(), _status, out var footer);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 6) };
        var add = LedgerAccountView.Button("Tambah"); add.Click += async (_, _) => await OpenEditorAsync(null);
        var edit = LedgerAccountView.Button("Ubah"); edit.Click += async (_, _) => await EditAsync();
        var delete = LedgerAccountView.Button(_kind is CashBankMasterKind.Expense or CashBankMasterKind.Income ? "Hapus" : "Nonaktif");
        delete.Click += async (_, _) => await DeleteOrDeactivateAsync();
        var refresh = LedgerAccountView.Button("Muat Ulang"); refresh.Click += async (_, _) => await ReloadAsync();
        var print = LedgerAccountView.Button("Cetak"); print.Click += (_, _) => LedgerAccountView.PrintGrid(_grid, ViewTitle);
        tools.Children.Add(add); tools.Children.Add(edit); tools.Children.Add(delete); tools.Children.Add(refresh); tools.Children.Add(print);
        Grid.SetRow(tools, 1); root.Children.Add(tools);
        if (_kind is CashBankMasterKind.Expense or CashBankMasterKind.Income)
        {
            var style = new Style(typeof(DataGridRow));
            var trigger = new DataTrigger { Binding = new Binding("IsDeleted"), Value = true };
            trigger.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Gainsboro));
            trigger.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.DimGray));
            style.Triggers.Add(trigger);
            _grid.RowStyle = style;
        }
        Grid.SetRow(_grid, 2); root.Children.Add(_grid);
        footer.Children.Add(LedgerAccountView.CloseButton(() => CloseRequested?.Invoke(this, EventArgs.Empty)));
        return root;
    }

    private void BuildColumns()
    {
        _grid.Columns.Add(LedgerAccountView.Column("Kode", "Code", 95));
        _grid.Columns.Add(LedgerAccountView.Column(_kind == CashBankMasterKind.Bank ? "Nama Bank" : "Nama", _kind == CashBankMasterKind.Bank ? "BankName" : "Name", new DataGridLength(1, DataGridLengthUnitType.Star)));
        if (_kind == CashBankMasterKind.Bank)
        {
            _grid.Columns.Add(LedgerAccountView.Column("No. Rekening", "AccountNumber", 120));
            _grid.Columns.Add(LedgerAccountView.Column("Pemilik", "AccountHolder", 140));
        }
        _grid.Columns.Add(LedgerAccountView.Column("Kode Perkiraan", "AccountCode", 110));
        _grid.Columns.Add(LedgerAccountView.Column("Nama Perkiraan", "AccountName", 190));
        if (_kind is CashBankMasterKind.Expense or CashBankMasterKind.Income) _grid.Columns.Add(LedgerAccountView.CheckColumn("Dihapus", "IsDeleted", 70));
        _grid.Columns.Add(LedgerAccountView.CheckColumn("Aktif", "IsActive", 55));
    }

    private async Task ReloadAsync(string? selectCode = null)
    {
        try
        {
            IsEnabled = false; _status.Text = $"Mengambil {ViewTitle.ToLowerInvariant()}...";
            System.Collections.IList rows = _kind switch
            {
                CashBankMasterKind.Cash => (System.Collections.IList)(await _api.GetCashAccountsAsync()).ToList(),
                CashBankMasterKind.Bank => (System.Collections.IList)(await _api.GetBankAccountsAsync()).ToList(),
                CashBankMasterKind.Expense => (System.Collections.IList)(await _api.GetCashBankCategoriesAsync("EXPENSE")).ToList(),
                _ => (System.Collections.IList)(await _api.GetCashBankCategoriesAsync("INCOME")).ToList()
            };
            _grid.ItemsSource = rows;
            if (!string.IsNullOrWhiteSpace(selectCode)) _grid.SelectedItem = rows.Cast<object>().FirstOrDefault(x => CodeOf(x) == selectCode);
            _status.Text = $"{rows.Count:N0} data | Kode perkiraan di sini dipakai otomatis pada transaksi kas-bank.";
        }
        catch (Exception exception) { _status.Text = exception.Message; ViewError.ShowWarningUnlessDatabaseUnavailable(exception, $"AKSARA - {ViewTitle}"); }
        finally { IsEnabled = true; }
    }

    private async Task EditAsync()
    {
        if (_grid.SelectedItem is not null) await OpenEditorAsync(_grid.SelectedItem);
    }

    private async Task OpenEditorAsync(object? row)
    {
        var editor = new CashBankMasterEditWindow(_kind, row) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() == true) await ReloadAsync(editor.SavedCode);
    }

    private async Task DeleteOrDeactivateAsync()
    {
        if (_grid.SelectedItem is null) return;
        try
        {
            IsEnabled = false; _status.Text = "Menyimpan perubahan...";
            if (_grid.SelectedItem is CashBankCategoryDto category)
            {
                await _api.SaveCashBankCategoryAsync(new SaveCashBankCategoryRequest(category.CategoryType, category.Code, category.Name, category.AccountCode, true, false, category.Version, AksaraApiClient.Shared.CurrentUserName, Environment.MachineName));
                await ReloadAsync(category.Code);
            }
            else if (_grid.SelectedItem is CashAccountDto cash)
            {
                await _api.SaveCashAccountAsync(new SaveCashAccountRequest(cash.Code, cash.Name, cash.AccountCode, false, cash.Version, AksaraApiClient.Shared.CurrentUserName, Environment.MachineName));
                await ReloadAsync(cash.Code);
            }
            else if (_grid.SelectedItem is BankAccountDto bank)
            {
                await _api.SaveBankAccountAsync(new SaveBankAccountRequest(bank.Code, bank.BankName, bank.AccountNumber, bank.AccountHolder, bank.AccountCode, false, bank.Version, AksaraApiClient.Shared.CurrentUserName, Environment.MachineName));
                await ReloadAsync(bank.Code);
            }
        }
        catch (Exception exception) { _status.Text = exception.Message; ViewError.ShowWarningUnlessDatabaseUnavailable(exception, $"AKSARA - {ViewTitle}"); }
        finally { IsEnabled = true; }
    }

    private static string? CodeOf(object row) => row switch
    {
        CashAccountDto cash => cash.Code,
        BankAccountDto bank => bank.Code,
        CashBankCategoryDto category => category.Code,
        _ => null
    };
}

internal sealed class CashBankMasterEditWindow : Window
{
    private readonly CashBankMasterKind _kind;
    private readonly object? _row;
    private readonly TextBox _code = new();
    private readonly TextBox _name = new();
    private readonly TextBox _accountNumber = new();
    private readonly TextBox _accountHolder = new();
    private readonly TextBox _accountCode = new();
    private readonly TextBlock _accountName = new() { Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _active = new() { Content = "Aktif dan dapat dipilih pada transaksi", IsChecked = true };
    private readonly CheckBox _deleted = new() { Content = "Tandai dihapus" };
    private readonly TextBlock _status = new() { Foreground = Brushes.Maroon, TextWrapping = TextWrapping.Wrap };
    public string? SavedCode { get; private set; }

    public CashBankMasterEditWindow(CashBankMasterKind kind, object? row)
    {
        _kind = kind; _row = row;
        Title = $"{(row is null ? "Tambah" : "Ubah")} {TitleFor(kind)}";
        Width = 580; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Content = Build();
        Populate();
    }

    private UIElement Build()
    {
        var root = LedgerAccountEditWindow.FormGrid(10);
        var row = 0;
        LedgerAccountEditWindow.Add(root, "Kode", _code, row++);
        LedgerAccountEditWindow.Add(root, _kind == CashBankMasterKind.Bank ? "Nama Bank" : "Nama", _name, row++);
        if (_kind == CashBankMasterKind.Bank)
        {
            LedgerAccountEditWindow.Add(root, "No. Rekening", _accountNumber, row++);
            LedgerAccountEditWindow.Add(root, "Atas Nama", _accountHolder, row++);
        }
        var accountPanel = new DockPanel();
        var lookup = new Button { Content = "...", Width = 34, Margin = new Thickness(5, 3, 0, 3) };
        lookup.Click += (_, _) => LookupAccount();
        DockPanel.SetDock(lookup, Dock.Right); accountPanel.Children.Add(lookup); accountPanel.Children.Add(_accountCode);
        LedgerAccountEditWindow.Add(root, "Kode Perkiraan", accountPanel, row++);
        LedgerAccountEditWindow.Add(root, "", _accountName, row++);
        if (_kind is CashBankMasterKind.Expense or CashBankMasterKind.Income) LedgerAccountEditWindow.AddControl(root, _deleted, row++);
        LedgerAccountEditWindow.AddControl(root, _active, row++);
        LedgerAccountEditWindow.AddControl(root, _status, row++);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var save = new Button { Content = "Simpan", Width = 90, IsDefault = true, Margin = new Thickness(4) }; save.Click += Save_Click;
        buttons.Children.Add(save); buttons.Children.Add(new Button { Content = "Batal", Width = 90, IsCancel = true, Margin = new Thickness(4) });
        Grid.SetRow(buttons, row); Grid.SetColumnSpan(buttons, 2); root.Children.Add(buttons);
        return root;
    }

    private void Populate()
    {
        switch (_row)
        {
            case CashAccountDto cash:
                _code.Text = cash.Code; _name.Text = cash.Name; _accountCode.Text = cash.AccountCode; _accountName.Text = cash.AccountName; _active.IsChecked = cash.IsActive; break;
            case BankAccountDto bank:
                _code.Text = bank.Code; _name.Text = bank.BankName; _accountNumber.Text = bank.AccountNumber; _accountHolder.Text = bank.AccountHolder;
                _accountCode.Text = bank.AccountCode; _accountName.Text = bank.AccountName; _active.IsChecked = bank.IsActive; break;
            case CashBankCategoryDto category:
                _code.Text = category.Code; _name.Text = category.Name; _accountCode.Text = category.AccountCode; _accountName.Text = category.AccountName;
                _deleted.IsChecked = category.IsDeleted; _active.IsChecked = category.IsActive; break;
        }
        _code.IsReadOnly = _row is not null;
    }

    private void LookupAccount()
    {
        var lookup = _kind switch
        {
            CashBankMasterKind.Cash or CashBankMasterKind.Bank => new LedgerAccountLookupWindow(true, "ASSET", true),
            CashBankMasterKind.Expense => new LedgerAccountLookupWindow(true, "EXPENSE"),
            _ => new LedgerAccountLookupWindow(true, "REVENUE")
        };
        lookup.Owner = this;
        if (lookup.ShowDialog() == true && lookup.SelectedAccount is not null)
        {
            _accountCode.Text = lookup.SelectedAccount.Code;
            _accountName.Text = lookup.SelectedAccount.Name;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            IsEnabled = false; _status.Text = "Menyimpan...";
            if (_kind == CashBankMasterKind.Cash)
            {
                var current = _row as CashAccountDto;
                var saved = await AksaraApiClient.Shared.SaveCashAccountAsync(new SaveCashAccountRequest(_code.Text, _name.Text, _accountCode.Text, _active.IsChecked == true, current?.Version, AksaraApiClient.Shared.CurrentUserName, Environment.MachineName));
                SavedCode = saved.Code;
            }
            else if (_kind == CashBankMasterKind.Bank)
            {
                var current = _row as BankAccountDto;
                var saved = await AksaraApiClient.Shared.SaveBankAccountAsync(new SaveBankAccountRequest(_code.Text, _name.Text, _accountNumber.Text, _accountHolder.Text, _accountCode.Text, _active.IsChecked == true, current?.Version, AksaraApiClient.Shared.CurrentUserName, Environment.MachineName));
                SavedCode = saved.Code;
            }
            else
            {
                var current = _row as CashBankCategoryDto;
                var type = _kind == CashBankMasterKind.Expense ? "EXPENSE" : "INCOME";
                var saved = await AksaraApiClient.Shared.SaveCashBankCategoryAsync(new SaveCashBankCategoryRequest(type, _code.Text, _name.Text, _accountCode.Text, _deleted.IsChecked == true, _active.IsChecked == true, current?.Version, AksaraApiClient.Shared.CurrentUserName, Environment.MachineName));
                SavedCode = saved.Code;
            }
            DialogResult = true;
        }
        catch (Exception exception) { _status.Text = exception.Message; ViewError.ShowWarningUnlessDatabaseUnavailable(exception, Title); }
        finally { IsEnabled = true; }
    }

    private static string TitleFor(CashBankMasterKind kind) => kind switch
    {
        CashBankMasterKind.Cash => "Kas",
        CashBankMasterKind.Bank => "Bank",
        CashBankMasterKind.Expense => "Biaya",
        _ => "Penghasilan Lain"
    };
}

public sealed class CashBankTransactionView : UserControl, IWorkspaceCloseGuard
{
    private const string LocalDraftKey = "cash-bank";
    private const int DefaultRows = 13;

    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly ObservableCollection<CashBankEntryDraft> _lines = [];
    private readonly CheckBox _exclusiveMode = new() { Content = "Exclusive Mode", IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _modeStatus = new() { Text = "TAMBAH DATA", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _totalData = new() { Text = "TOTAL DATA 0", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly DatePicker _date = new() { SelectedDate = DateTime.Today, Width = 120 };
    private readonly TextBox _documentNumber = new() { Text = "KSB2608001", Width = 138, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.White, FontWeight = FontWeights.Bold };
    private readonly RadioButton _receipt = new() { Content = "Trans.Penerimaan", IsChecked = true, Margin = new Thickness(0, 0, 0, 4), FontWeight = FontWeights.SemiBold };
    private readonly RadioButton _payment = new() { Content = "Trans.Pengeluaran", Margin = new Thickness(0, 0, 0, 0) };
    private readonly TabControl _methodTabs = new() { Height = 40 };
    private readonly TextBlock _title = new() { Text = "PENERIMAAN", Foreground = Brushes.Red, FontSize = 27, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _flowHeader = new() { Text = "Penerimaan", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly DataGrid _entryGrid = new()
    {
        AutoGenerateColumns = false,
        CanUserAddRows = false,
        CanUserDeleteRows = false,
        CanUserSortColumns = false,
        RowHeight = 23,
        ColumnHeaderHeight = 23,
        SelectionMode = DataGridSelectionMode.Single,
        SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
        MinHeight = 260
    };
    private readonly TextBlock _cashTotal = TotalValue();
    private readonly TextBlock _giroTotal = TotalValue();
    private readonly TextBlock _bankTotal = TotalValue();
    private readonly TextBlock _grandTotal = TotalValue();
    private readonly TextBlock _hint = new()
    {
        Text = "Tekan F4 pada keyboard / DOUBLE CLICK tombol kiri mouse pada kolom ini untuk membuka form cari TANGGAL",
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = 12
    };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Style _rightTextStyle = LedgerAccountView.RightAlignedStyle();

    private IReadOnlyList<CashAccountDto> _cashAccounts = [];
    private IReadOnlyList<BankAccountDto> _bankAccounts = [];
    private IReadOnlyList<CashBankCategoryDto> _categories = [];
    private IReadOnlyList<LedgerAccountDto> _accounts = [];
    private IReadOnlyList<CashBankTransactionDto> _history = [];
    private CashBankTransactionDto? _currentTransaction;
    private DataGridTextColumn? _settlementCodeColumn;
    private DataGridTextColumn? _settlementNameColumn;
    private Button? _deleteButton;
    private Button? _restoreButton;
    private int _historyIndex = -1;
    private bool _browseMode;
    private bool _lookupDialogOpen;
    private bool _draftChecked;
    private DateTime _ignoreLookupUntil = DateTime.MinValue;

    public event EventHandler? CloseRequested;

    public CashBankTransactionView(int initialMethodTab = 0)
    {
        _date.Language = XmlLanguage.GetLanguage("id-ID");
        BuildColumns();
        Content = Build();
        _methodTabs.SelectedIndex = Math.Clamp(initialMethodTab, 0, 2);
        AddBlankRows(DefaultRows);

        Loaded += async (_, _) =>
        {
            await LoadMastersAsync();
            await ReloadAsync();
            RestoreLocalDraft();
        };
        _receipt.Checked += (_, _) => UpdateState();
        _payment.Checked += (_, _) => UpdateState();
        _date.SelectedDateChanged += (_, _) => { if (!_browseMode) UpdateDocumentPreview(); };
        _methodTabs.SelectionChanged += (_, e) => { if (e.Source == _methodTabs) UpdateState(); };
        _entryGrid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(UpdateDraftNamesAndTotals);
        _entryGrid.CurrentCellChanged += (_, _) => UpdateDraftNamesAndTotals();
        _entryGrid.PreviewMouseLeftButtonDown += EntryGrid_PreviewMouseLeftButtonDown;
        _entryGrid.PreparingCellForEdit += (_, e) =>
        {
            if (e.EditingElement is TextBox editor)
                editor.Dispatcher.BeginInvoke(new Action(() => { editor.Focus(); editor.SelectAll(); }));
        };
        _entryGrid.PreviewTextInput += EntryGrid_PreviewTextInput;
        _entryGrid.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(EntryGrid_PreviewKeyDown), true);
        UpdateState();
    }

    private UIElement Build()
    {
        var root = new Grid { Margin = new Thickness(7), Background = LedgerAccountView.Brush("#F0F0F0") };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var statusStrip = BuildStatusStrip();
        Grid.SetRow(statusStrip, 0); root.Children.Add(statusStrip);
        var headerPanel = BuildHeaderPanel();
        Grid.SetRow(headerPanel, 1); root.Children.Add(headerPanel);
        var tabTitleRow = BuildTabTitleRow();
        Grid.SetRow(tabTitleRow, 2); root.Children.Add(tabTitleRow);
        var groupHeader = BuildGroupHeader();
        Grid.SetRow(groupHeader, 3); root.Children.Add(groupHeader);

        _entryGrid.ItemsSource = _lines;
        _entryGrid.Margin = new Thickness(7, 0, 7, 0);
        Grid.SetRow(_entryGrid, 4); root.Children.Add(_entryGrid);

        var totalsBar = BuildTotalsBar();
        Grid.SetRow(totalsBar, 5); root.Children.Add(totalsBar);
        var commandBar = BuildCommandBar();
        Grid.SetRow(commandBar, 6); root.Children.Add(commandBar);
        var hintBar = BuildHintBar();
        Grid.SetRow(hintBar, 7); root.Children.Add(hintBar);
        return root;
    }

    private UIElement BuildStatusStrip()
    {
        var grid = new Grid { Height = 34, Margin = new Thickness(7, 0, 7, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(238) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

        AddStripCell(grid, _exclusiveMode, 0);
        AddStripCell(grid, new TextBlock { Text = "Status:", Margin = new Thickness(15, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }, 1);
        AddStripCell(grid, _modeStatus, 2);
        AddStripCell(grid, _totalData, 3);
        AddStripCell(grid, new TextBlock { Text = "▥", FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, 4);
        return grid;
    }

    private UIElement BuildHeaderPanel()
    {
        var border = new Border
        {
            Margin = new Thickness(7, 0, 7, 8),
            BorderBrush = Brushes.Silver,
            BorderThickness = new Thickness(1),
            Background = LedgerAccountView.Brush("#F4F4F4"),
            Padding = new Thickness(7, 6, 7, 6)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddHeaderText(grid, "Tgl. Bukti", 0, 0);
        Grid.SetRow(_date, 0); Grid.SetColumn(_date, 1); grid.Children.Add(_date);
        AddHeaderText(grid, "No. Bukti", 1, 0);
        Grid.SetRow(_documentNumber, 1); Grid.SetColumn(_documentNumber, 1); grid.Children.Add(_documentNumber);

        var radioPanel = new StackPanel { Margin = new Thickness(0, 0, 12, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        radioPanel.Children.Add(_receipt);
        radioPanel.Children.Add(_payment);
        Grid.SetRow(radioPanel, 0); Grid.SetRowSpan(radioPanel, 2); Grid.SetColumn(radioPanel, 3); grid.Children.Add(radioPanel);

        border.Child = grid;
        return border;
    }

    private UIElement BuildTabTitleRow()
    {
        if (_methodTabs.Items.Count == 0)
        {
            _methodTabs.Items.Add(new TabItem { Header = "Kas .1" });
            _methodTabs.Items.Add(new TabItem { Header = "Bank .2" });
            _methodTabs.Items.Add(new TabItem { Header = "Cek / Giro .3" });
            _methodTabs.SelectedIndex = 0;
        }

        var grid = new Grid { Margin = new Thickness(7, 0, 7, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(420) });
        Grid.SetColumn(_methodTabs, 0); grid.Children.Add(_methodTabs);

        var titleBorder = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Background = Brushes.White, Margin = new Thickness(6, 3, 0, 0), Child = _title };
        Grid.SetColumn(titleBorder, 1); grid.Children.Add(titleBorder);
        return grid;
    }

    private UIElement BuildGroupHeader()
    {
        var grid = new Grid { Margin = new Thickness(7, 0, 7, 0), Height = 26, Background = Brushes.Black };
        AddEntryColumns(grid);
        Grid.SetColumn(_flowHeader, 0); Grid.SetColumnSpan(_flowHeader, 4); grid.Children.Add(_flowHeader);
        var source = new TextBlock { Text = "Dari", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(source, 4); Grid.SetColumnSpan(source, 3); grid.Children.Add(source);
        return grid;
    }

    private UIElement BuildTotalsBar()
    {
        var grid = new Grid { Margin = new Thickness(7, 8, 7, 0), Height = 40, Background = LedgerAccountView.Brush("#F5F5F5") };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(165) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(165) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(165) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(165) });

        AddTotalLabel(grid, "Kas", 0); AddTotalBox(grid, _cashTotal, 1);
        AddTotalLabel(grid, "C/G", 2); AddTotalBox(grid, _giroTotal, 3);
        AddTotalLabel(grid, "Bank", 4); AddTotalBox(grid, _bankTotal, 5);
        AddTotalLabel(grid, "Grand Total", 7); AddTotalBox(grid, _grandTotal, 8);
        return grid;
    }

    private UIElement BuildCommandBar()
    {
        var grid = new Grid { Margin = new Thickness(7, 6, 7, 0), Height = 31 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nav = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        AddNav(nav, "◀◀", async () => await LoadBoundaryHistoryAsync(false));
        AddNav(nav, "◀", async () => await MoveHistoryAsync(1));
        AddNav(nav, "●", ShowHistoryAsync);
        AddNav(nav, "▶", async () => await MoveHistoryAsync(-1));
        AddNav(nav, "▶▶", async () => await LoadBoundaryHistoryAsync(true));
        Grid.SetColumn(nav, 0); grid.Children.Add(nav);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(ActionButton("Cari", ShowHistoryAsync, 78));
        actions.Children.Add(ActionButton("Tambah", AddEntryLine, 86));
        actions.Children.Add(ActionButton("Batal", CancelInput, 86));
        actions.Children.Add(ActionButton("Simpan", async () => await SaveDraftsAsync(), 92));
        actions.Children.Add(ActionButton("Smpn&Ctk", async () => await SaveAndPrintWithOptionsAsync(), 98));
        _deleteButton = ActionButton("Hapus", async () => await DeleteOrClearAsync(), 86);
        _restoreButton = ActionButton("Btl.Hapus", async () => await RestoreCurrentTransactionAsync(), 92);
        actions.Children.Add(_deleteButton);
        actions.Children.Add(_restoreButton);
        actions.Children.Add(ActionButton("Tutup", () => CloseRequested?.Invoke(this, EventArgs.Empty), 86));
        Grid.SetColumn(actions, 1); grid.Children.Add(actions);
        UpdateCommandStates();
        return grid;
    }

    private UIElement BuildHintBar()
    {
        var grid = new Grid { Height = 38, Margin = new Thickness(7, 2, 7, 0), Background = Brushes.White };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Background = LedgerAccountView.Brush("#F0A22A"),
            BorderBrush = LedgerAccountView.Brush("#C87600"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "i", Foreground = Brushes.White, FontSize = 22, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        grid.Children.Add(icon);
        Grid.SetColumn(_hint, 1); grid.Children.Add(_hint);
        return grid;
    }

    private void BuildColumns()
    {
        _settlementCodeColumn = new DataGridTextColumn { Header = "Kas", Binding = DraftBinding(nameof(CashBankEntryDraft.SettlementCode)), Width = 55 };
        _settlementNameColumn = new DataGridTextColumn { Header = "Nama Kas", Binding = new Binding(nameof(CashBankEntryDraft.SettlementName)), IsReadOnly = true, Width = 130 };
        _entryGrid.Columns.Add(_settlementCodeColumn);
        _entryGrid.Columns.Add(_settlementNameColumn);
        _entryGrid.Columns.Add(new DataGridTextColumn { Header = "Keterangan", Binding = DraftBinding(nameof(CashBankEntryDraft.Description)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _entryGrid.Columns.Add(new DataGridTextColumn { Header = "Jumlah", Binding = DraftBinding(nameof(CashBankEntryDraft.AmountText)), Width = 135, ElementStyle = _rightTextStyle });
        _entryGrid.Columns.Add(new DataGridTextColumn { Header = "Grp.", Binding = DraftBinding(nameof(CashBankEntryDraft.GroupCode)), Width = 48 });
        _entryGrid.Columns.Add(new DataGridTextColumn { Header = "K/B/P", Binding = DraftBinding(nameof(CashBankEntryDraft.AccountCode)), Width = 60 });
        _entryGrid.Columns.Add(new DataGridTextColumn { Header = "Nama K/B/P", Binding = new Binding(nameof(CashBankEntryDraft.AccountName)), IsReadOnly = true, Width = 220 });
    }

    private async Task LoadMastersAsync()
    {
        try
        {
            _status.Text = "Mengambil daftar kas, bank, group, dan kode perkiraan...";
            var cashTask = _api.GetCashAccountsAsync(false);
            var bankTask = _api.GetBankAccountsAsync(false);
            var categoryTask = _api.GetCashBankCategoriesAsync(null, false);
            var accountTask = _api.GetLedgerAccountsAsync(null, true, true);

            _cashAccounts = await cashTask;
            _bankAccounts = await bankTask;
            _categories = await categoryTask;
            _accounts = await accountTask;
            UpdateDraftNamesAndTotals();
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Kas & Bank");
        }
    }

    private async Task ReloadAsync()
    {
        try
        {
            _history = await _api.GetCashBankTransactionsAsync(null, 5000);
            _historyIndex = ResolveHistoryIndex();
            _totalData.Text = $"TOTAL DATA {_history.Count:N0}";
            _status.Text = _browseMode
                ? $"History dimuat ulang. {_history.Count:N0} transaksi tersedia."
                : "Mode tambah data. Input transaksi langsung pada tabel.";
            if (!_browseMode) UpdateDocumentPreview();
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Kas & Bank");
        }
    }

    private async Task MoveHistoryAsync(int offset)
    {
        await ReloadAsync();
        if (_history.Count == 0)
        {
            _status.Text = "Belum ada history transaksi kas-bank.";
            return;
        }

        var currentIndex = ResolveHistoryIndex();
        var nextIndex = currentIndex < 0
            ? offset > 0 ? _history.Count - 1 : 0
            : currentIndex + offset;
        await LoadHistoryAtAsync(nextIndex);
    }

    private async Task LoadBoundaryHistoryAsync(bool newest)
    {
        await ReloadAsync();
        if (_history.Count == 0)
        {
            _status.Text = "Belum ada history transaksi kas-bank.";
            return;
        }

        await LoadHistoryAtAsync(newest ? 0 : _history.Count - 1);
    }

    private async Task LoadHistoryAtAsync(int index)
    {
        try
        {
            if (_history.Count == 0) await ReloadAsync();
            if (_history.Count == 0)
            {
                _status.Text = "Belum ada history transaksi kas-bank.";
                return;
            }

            index = Math.Clamp(index, 0, _history.Count - 1);
            var transaction = _history[index];
            var detailLines = await _api.GetCashBankTransactionLinesAsync(transaction.Id);

            _browseMode = true;
            _currentTransaction = transaction;
            _historyIndex = index;
            _entryGrid.IsReadOnly = true;
            _date.IsEnabled = false;
            _receipt.IsEnabled = false;
            _payment.IsEnabled = false;
            _methodTabs.IsEnabled = false;
            _modeStatus.Text = SameCode(transaction.Status, "CANCELLED") ? "DITANDAI HAPUS" : "LIHAT DATA";

            _date.SelectedDate = transaction.DocumentDate.ToDateTime(TimeOnly.MinValue);
            _documentNumber.Text = transaction.DocumentNumber;
            if (SameCode(transaction.TransactionKind, "PAYMENT")) _payment.IsChecked = true;
            else _receipt.IsChecked = true;
            SetMethodTab(transaction.Method);

            _lines.Clear();
            foreach (var line in detailLines) _lines.Add(DraftFromHistoryLine(line));
            if (_lines.Count == 0) _lines.Add(DraftFromHistorySummary(transaction));
            AddBlankRows(Math.Max(DefaultRows, _lines.Count));

            UpdateDraftNamesAndTotals();
            RefreshEntryGridSafely();
            SelectLine(0);
            UpdateCommandStates();
            _status.Text = $"Data {index + 1:N0}/{_history.Count:N0}: {transaction.DocumentNumber} | {transaction.DocumentDate:dd/MM/yyyy}.";
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Kas & Bank");
        }
    }

    private async Task ShowHistoryAsync()
    {
        try
        {
            await ReloadAsync();
            if (_history.Count == 0)
            {
                _status.Text = "Belum ada history transaksi kas-bank.";
                return;
            }

            var lookup = new CashBankTransactionHistoryWindow(_history) { Owner = Window.GetWindow(this) };
            if (lookup.ShowDialog() == true && lookup.SelectedTransaction is not null)
            {
                var index = _history.ToList().FindIndex(x => x.Id == lookup.SelectedTransaction.Id);
                await LoadHistoryAtAsync(index < 0 ? 0 : index);
            }
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - History Kas & Bank");
        }
    }

    private int ResolveHistoryIndex()
    {
        if (!_browseMode) return -1;

        var documentNumber = Normalize(_documentNumber.Text);
        if (!string.IsNullOrWhiteSpace(documentNumber))
        {
            var byDocument = _history.ToList().FindIndex(x => SameCode(x.DocumentNumber, documentNumber));
            if (byDocument >= 0) return byDocument;
        }

        return _historyIndex >= 0 && _historyIndex < _history.Count ? _historyIndex : -1;
    }

    private CashBankEntryDraft DraftFromHistoryLine(CashBankTransactionLineDto line)
    {
        var settlementCode = line.SettlementCode ?? line.CheckGiroNumber ?? string.Empty;
        return new CashBankEntryDraft
        {
            SettlementMethod = line.SettlementMethod,
            SettlementCode = settlementCode,
            SettlementName = SettlementName(line.SettlementMethod, settlementCode),
            Description = line.Notes ?? string.Empty,
            AmountText = line.Amount.ToString("N0", CultureInfo.CurrentCulture),
            GroupCode = line.CounterpartyGroup ?? string.Empty,
            AccountCode = line.CounterpartyCode ?? line.AccountCode,
            AccountName = line.CounterpartyName ?? line.AccountName
        };
    }

    private CashBankEntryDraft DraftFromHistorySummary(CashBankTransactionDto transaction)
    {
        var settlementCode = transaction.Method == "CASH"
            ? transaction.CashAccountCode ?? string.Empty
            : transaction.BankAccountCode ?? transaction.CheckGiroNumber ?? string.Empty;
        return new CashBankEntryDraft
        {
            SettlementMethod = transaction.Method,
            SettlementCode = settlementCode,
            SettlementName = SettlementName(transaction.Method, settlementCode),
            Description = transaction.Notes ?? string.Empty,
            AmountText = transaction.Amount.ToString("N0", CultureInfo.CurrentCulture),
            GroupCode = transaction.CategoryCode ?? string.Empty,
            AccountCode = transaction.CategoryCode ?? transaction.AccountCode,
            AccountName = transaction.Counterparty ?? transaction.AccountName
        };
    }

    private async Task SaveAndPrintWithOptionsAsync()
    {
        await ReloadAsync();
        var dialog = new CashBankPrintOptionsWindow(Normalize(_documentNumber.Text), _history);
        var owner = Window.GetWindow(this);
        if (owner is not null) dialog.Owner = owner;

        if (dialog.ShowDialog() != true) return;

        if (!string.IsNullOrWhiteSpace(dialog.DocumentNumber))
            _documentNumber.Text = dialog.DocumentNumber;

        try
        {
            var receipt = await ResolveReceiptForOutputAsync(dialog);
            switch (dialog.SelectedAction)
            {
                case CashBankPrintAction.Preview:
                    CashBankReceiptOutput.Preview(receipt, owner);
                    _status.Text = "Preview bukti kas-bank sudah dibuka.";
                    break;
                case CashBankPrintAction.ExportExcel:
                    CashBankReceiptOutput.ExportExcel(receipt);
                    _status.Text = "Bukti kas-bank siap diekspor ke Excel.";
                    break;
                default:
                    CashBankReceiptOutput.Print(receipt);
                    _status.Text = "Bukti kas-bank siap dicetak.";
                    break;
            }
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Cetak Bukti Kas Bank");
        }
    }

    private async Task<IReadOnlyList<CashBankTransactionDto>> SaveDraftsAsync()
    {
        try
        {
            if (_browseMode)
            {
                _status.Text = "Sedang melihat data lama. Klik Tambah dulu untuk membuat transaksi baru.";
                return [];
            }

            CommitEntryGridEdit();
            UpdateDraftNamesAndTotals();

            var filled = _lines.Where(x => !x.IsBlank).ToList();
            if (filled.Count == 0) throw new InvalidOperationException("Isi minimal satu baris transaksi pada tabel.");

            IsEnabled = false;
            _status.Text = "Menyimpan transaksi kas-bank dan membentuk jurnal...";
            var saved = 0;
            var savedTransactions = new List<CashBankTransactionDto>();
            foreach (var line in filled)
            {
                if (!TryReadAmount(line.AmountText, out var amount) || amount <= 0)
                    throw new InvalidOperationException("Jumlah harus lebih dari nol pada setiap baris yang diisi.");

                var method = SelectedMethod();
                var settlementCode = Normalize(line.SettlementCode);
                if (string.IsNullOrWhiteSpace(settlementCode)) settlementCode = DefaultSettlementCode(method);
                if (string.IsNullOrWhiteSpace(settlementCode))
                    throw new InvalidOperationException(method == "CASH" ? "Kode kas wajib diisi." : "Kode bank wajib diisi.");

                var category = FindCategory(line.GroupCode);
                var accountCode = Normalize(line.AccountCode);
                if (string.IsNullOrWhiteSpace(accountCode) && category is not null) accountCode = category.AccountCode;
                if (string.IsNullOrWhiteSpace(accountCode))
                    throw new InvalidOperationException("Kolom K/B/P wajib diisi atau pilih Grp. yang sudah punya kode perkiraan.");

                var savedTransaction = await _api.CreateCashBankTransactionAsync(new CreateCashBankTransactionRequest(
                    DateOnly.FromDateTime(_date.SelectedDate.GetValueOrDefault(DateTime.Today)),
                    _payment.IsChecked == true ? "PAYMENT" : "RECEIPT",
                    method,
                    method == "CASH" ? settlementCode : null,
                    method == "CASH" ? null : settlementCode,
                    method == "CHECK_GIRO" ? _documentNumber.Text : null,
                    null,
                    line.AccountName,
                    category?.CategoryType,
                    category?.Code,
                    accountCode,
                    amount,
                    line.Description,
                    AksaraApiClient.Shared.CurrentUserName,
                    Environment.MachineName));
                savedTransactions.Add(savedTransaction);
                saved++;
            }

            LocalTransactionDraftStore.Delete(LocalDraftKey);
            ClearDrafts();
            await ReloadAsync();
            _status.Text = $"{saved:N0} baris transaksi tersimpan dan sudah masuk Jurnal Umum.";
            return savedTransactions;
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Kas & Bank");
            return [];
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async Task<CashBankReceipt> ResolveReceiptForOutputAsync(CashBankPrintOptionsWindow dialog)
    {
        if (dialog.SelectedTransaction is not null)
            return await BuildReceiptFromTransactionAsync(dialog.SelectedTransaction);

        var typed = Normalize(dialog.DocumentNumber);
        var matchingHistory = _history.FirstOrDefault(x => SameCode(x.DocumentNumber, typed));
        if (matchingHistory is not null)
            return await BuildReceiptFromTransactionAsync(matchingHistory);

        if (_browseMode)
        {
            var current = _history.FirstOrDefault(x => SameCode(x.DocumentNumber, _documentNumber.Text));
            if (current is not null) return BuildReceiptFromCurrentRows(current);
        }

        if (dialog.SelectedAction == CashBankPrintAction.Preview)
            return BuildReceiptFromDraftRows(string.IsNullOrWhiteSpace(typed) ? Normalize(_documentNumber.Text) : typed);

        var saved = await SaveDraftsAsync();
        if (saved.Count == 0) throw new InvalidOperationException("Tidak ada transaksi kas-bank yang bisa diproses.");
        return await BuildReceiptFromTransactionAsync(saved[0]);
    }

    private async Task<CashBankReceipt> BuildReceiptFromTransactionAsync(CashBankTransactionDto transaction)
    {
        var detailLines = await _api.GetCashBankTransactionLinesAsync(transaction.Id);
        var lines = detailLines.Count > 0
            ? detailLines.Select(line => new CashBankReceiptLine(
                line.SettlementCode ?? line.CheckGiroNumber ?? string.Empty,
                SettlementName(line.SettlementMethod, line.SettlementCode ?? string.Empty),
                line.Notes ?? string.Empty,
                line.CounterpartyGroup ?? string.Empty,
                line.AccountCode,
                line.AccountName,
                line.Amount)).ToList()
            : new List<CashBankReceiptLine>
            {
                new(
                    transaction.Method == "CASH" ? transaction.CashAccountCode ?? string.Empty : transaction.BankAccountCode ?? transaction.CheckGiroNumber ?? string.Empty,
                    transaction.Method == "CASH" ? transaction.CashAccountName ?? string.Empty : transaction.BankAccountName ?? string.Empty,
                    transaction.Notes ?? string.Empty,
                    transaction.CategoryCode ?? string.Empty,
                    transaction.AccountCode,
                    transaction.AccountName,
                    transaction.Amount)
            };

        return new CashBankReceipt(
            transaction.DocumentNumber,
            transaction.DocumentDate,
            transaction.TransactionKind,
            transaction.Method,
            transaction.Method == "CASH" ? transaction.CashAccountCode ?? string.Empty : transaction.BankAccountCode ?? transaction.CheckGiroNumber ?? string.Empty,
            transaction.Method == "CASH" ? transaction.CashAccountName ?? string.Empty : transaction.BankAccountName ?? string.Empty,
            transaction.JournalNumber,
            lines);
    }

    private CashBankReceipt BuildReceiptFromCurrentRows(CashBankTransactionDto transaction)
    {
        var lines = FilledReceiptLines().ToList();
        if (lines.Count == 0)
        {
            lines.Add(new CashBankReceiptLine(
                transaction.Method == "CASH" ? transaction.CashAccountCode ?? string.Empty : transaction.BankAccountCode ?? transaction.CheckGiroNumber ?? string.Empty,
                transaction.Method == "CASH" ? transaction.CashAccountName ?? string.Empty : transaction.BankAccountName ?? string.Empty,
                transaction.Notes ?? string.Empty,
                transaction.CategoryCode ?? string.Empty,
                transaction.AccountCode,
                transaction.AccountName,
                transaction.Amount));
        }

        return new CashBankReceipt(
            transaction.DocumentNumber,
            transaction.DocumentDate,
            transaction.TransactionKind,
            transaction.Method,
            transaction.Method == "CASH" ? transaction.CashAccountCode ?? string.Empty : transaction.BankAccountCode ?? transaction.CheckGiroNumber ?? string.Empty,
            transaction.Method == "CASH" ? transaction.CashAccountName ?? string.Empty : transaction.BankAccountName ?? string.Empty,
            transaction.JournalNumber,
            lines);
    }

    private CashBankReceipt BuildReceiptFromDraftRows(string documentNumber)
    {
        CommitEntryGridEdit();
        UpdateDraftNamesAndTotals();
        var lines = FilledReceiptLines().ToList();
        if (lines.Count == 0) throw new InvalidOperationException("Isi minimal satu baris transaksi untuk preview.");
        var method = SelectedMethod();
        var settlementCode = Normalize(lines[0].SettlementCode);
        return new CashBankReceipt(
            documentNumber,
            DateOnly.FromDateTime(_date.SelectedDate.GetValueOrDefault(DateTime.Today)),
            _payment.IsChecked == true ? "PAYMENT" : "RECEIPT",
            method,
            settlementCode,
            SettlementName(method, settlementCode),
            "-",
            lines);
    }

    private IEnumerable<CashBankReceiptLine> FilledReceiptLines()
    {
        foreach (var line in _lines.Where(x => !x.IsBlank))
        {
            if (!TryReadAmount(line.AmountText, out var amount) || amount <= 0) continue;
            yield return new CashBankReceiptLine(
                Normalize(line.SettlementCode),
                line.SettlementName,
                line.Description,
                Normalize(line.GroupCode),
                Normalize(line.AccountCode),
                line.AccountName,
                amount);
        }
    }

    private void UpdateState()
    {
        var isReceipt = _receipt.IsChecked == true;
        _title.Text = isReceipt ? "PENERIMAAN" : "PENGELUARAN";
        _flowHeader.Text = isReceipt ? "Penerimaan" : "Pengeluaran";
        _title.Foreground = Brushes.Red;
        if (!_browseMode) _modeStatus.Text = "TAMBAH DATA";

        if (_settlementCodeColumn is not null && _settlementNameColumn is not null)
        {
            var method = SelectedMethod();
            _settlementCodeColumn.Header = method == "CASH" ? "Kas" : method == "BANK" ? "Bank" : "C/G";
            _settlementNameColumn.Header = method == "CASH" ? "Nama Kas" : method == "BANK" ? "Nama Bank" : "Nama C/G";
        }

        if (!_browseMode) UpdateDocumentPreview();
        UpdateDraftNamesAndTotals();
        UpdateCommandStates();
    }

    private void UpdateDocumentPreview()
    {
        if (_browseMode) return;
        var date = _date.SelectedDate.GetValueOrDefault(DateTime.Today);
        var prefix = ProgramConfigurationCache.Current.CashBankNumberPrefix;
        _documentNumber.Text = $"{prefix}-{date:yyyyMM}-{Math.Max(1, _history.Count + 1):0000}";
    }

    private void UpdateDraftNamesAndTotals()
    {
        var method = SelectedMethod();
        if (_browseMode)
        {
            decimal cashTotal = 0;
            decimal giroTotal = 0;
            decimal bankTotal = 0;
            foreach (var line in _lines)
            {
                if (!TryReadAmount(line.AmountText, out var amount) || amount <= 0) continue;
                switch (Normalize(line.SettlementMethod))
                {
                    case "CASH":
                        cashTotal += amount;
                        break;
                    case "CHECK_GIRO":
                        giroTotal += amount;
                        break;
                    default:
                        bankTotal += amount;
                        break;
                }
            }

            _cashTotal.Text = cashTotal.ToString("N0");
            _giroTotal.Text = giroTotal.ToString("N0");
            _bankTotal.Text = bankTotal.ToString("N0");
            _grandTotal.Text = (cashTotal + giroTotal + bankTotal).ToString("N0");
            return;
        }

        decimal total = 0;
        foreach (var line in _lines)
        {
            line.SettlementMethod = method;
            line.SettlementCode = Normalize(line.SettlementCode);
            line.SettlementName = SettlementName(method, line.SettlementCode);

            var category = FindCategory(line.GroupCode);
            if (category is not null)
            {
                line.GroupCode = category.Code;
                if (string.IsNullOrWhiteSpace(line.AccountCode)) line.AccountCode = category.AccountCode;
            }

            line.AccountCode = Normalize(line.AccountCode);
            line.AccountName = AccountName(line.AccountCode);
            if (TryReadAmount(line.AmountText, out var amount) && amount > 0) total += amount;
        }

        _cashTotal.Text = method == "CASH" ? total.ToString("N0") : "0";
        _giroTotal.Text = method == "CHECK_GIRO" ? total.ToString("N0") : "0";
        _bankTotal.Text = method == "BANK" ? total.ToString("N0") : "0";
        _grandTotal.Text = total.ToString("N0");
    }

    private void EntryGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F4 && e.SystemKey != Key.F4) return;
        e.Handled = true;
        OpenLookupForCurrentCell();
    }

    private void EntryGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_browseMode || _entryGrid.IsReadOnly || e.OriginalSource is not DependencyObject source) return;
        var cell = FindVisualParent<DataGridCell>(source);
        if (cell is null) return;

        _entryGrid.CurrentCell = new DataGridCellInfo(cell.DataContext, cell.Column);
        if (e.ClickCount >= 2)
        {
            e.Handled = true;
            cell.Focus();
            OpenLookupForCurrentCell();
            return;
        }

        if (cell.Column.IsReadOnly) return;

        if (cell.IsKeyboardFocusWithin)
        {
            BeginCurrentCellEdit();
            return;
        }

        e.Handled = true;
        cell.Focus();
        BeginCurrentCellEdit();
    }

    private void EntryGrid_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_browseMode || _entryGrid.IsReadOnly || _entryGrid.CurrentColumn?.IsReadOnly == true) return;
        _entryGrid.BeginEdit(e);
    }

    private void OpenLookupForCurrentCell()
    {
        if (_lookupDialogOpen || DateTime.UtcNow < _ignoreLookupUntil) return;
        _lookupDialogOpen = true;
        try
        {
            LookupCurrentCell();
        }
        finally
        {
            _lookupDialogOpen = false;
            _ignoreLookupUntil = DateTime.UtcNow.AddMilliseconds(350);
        }
    }

    private void LookupCurrentCell()
    {
        if (_browseMode)
        {
            _status.Text = "Mode lihat data lama. Klik Tambah untuk input transaksi baru.";
            return;
        }

        if (CurrentDraftLine() is not { } line) return;
        var header = _entryGrid.CurrentColumn?.Header?.ToString() ?? string.Empty;
        if (header is "Kas" or "Bank" or "C/G" or "Nama Kas" or "Nama Bank" or "Nama C/G")
        {
            LookupSettlement(line);
            return;
        }
        if (header.StartsWith("Grp.", StringComparison.OrdinalIgnoreCase))
        {
            LookupCategory(line);
            return;
        }
        if (header is "K/B/P" or "Nama K/B/P")
        {
            LookupAccount(line);
            return;
        }

        BeginCurrentCellEdit();
    }

    private CashBankEntryDraft? CurrentDraftLine()
    {
        if (_entryGrid.CurrentCell.Item is CashBankEntryDraft current) return current;
        if (_entryGrid.SelectedItem is CashBankEntryDraft selected) return selected;
        if (_entryGrid.SelectedCells.Count > 0 && _entryGrid.SelectedCells[0].Item is CashBankEntryDraft selectedCell) return selectedCell;
        return null;
    }

    private void LookupSettlement(CashBankEntryDraft line)
    {
        CommitEntryGridEdit();
        var method = SelectedMethod();
        var choices = method == "CASH"
            ? _cashAccounts.Select(x => new CashBankLookupChoice(x.Code, x.Name))
            : _bankAccounts.Select(x => new CashBankLookupChoice(x.Code, x.BankName));
        var lookup = new CashBankChoiceLookupWindow(method == "CASH" ? "Cari Kas" : "Cari Bank", choices) { Owner = Window.GetWindow(this) };
        if (lookup.ShowDialog() == true && lookup.SelectedChoice is not null)
        {
            line.SettlementCode = lookup.SelectedChoice.Code;
            line.SettlementName = lookup.SelectedChoice.Name;
            UpdateDraftNamesAndTotals();
            RefreshEntryGridSafely();
        }
    }

    private void LookupCategory(CashBankEntryDraft line)
    {
        CommitEntryGridEdit();
        var type = _payment.IsChecked == true ? "EXPENSE" : "INCOME";
        var choices = _categories
            .Where(x => x.CategoryType == type && !x.IsDeleted && x.IsActive)
            .Select(x => new CashBankLookupChoice(x.Code, x.Name, x.AccountCode, x.AccountName));
        var lookup = new CashBankChoiceLookupWindow(type == "EXPENSE" ? "Cari Biaya" : "Cari Penghasilan", choices) { Owner = Window.GetWindow(this) };
        if (lookup.ShowDialog() == true && lookup.SelectedChoice is not null)
        {
            line.GroupCode = lookup.SelectedChoice.Code;
            if (!string.IsNullOrWhiteSpace(lookup.SelectedChoice.AccountCode)) line.AccountCode = lookup.SelectedChoice.AccountCode;
            line.AccountName = lookup.SelectedChoice.AccountName ?? string.Empty;
            UpdateDraftNamesAndTotals();
            RefreshEntryGridSafely();
        }
    }

    private void LookupAccount(CashBankEntryDraft line)
    {
        CommitEntryGridEdit();
        var lookup = _payment.IsChecked == true ? new LedgerAccountLookupWindow(true, "EXPENSE") : new LedgerAccountLookupWindow(true, "REVENUE");
        lookup.Owner = Window.GetWindow(this);
        if (lookup.ShowDialog() == true && lookup.SelectedAccount is not null)
        {
            line.AccountCode = lookup.SelectedAccount.Code;
            line.AccountName = lookup.SelectedAccount.Name;
            UpdateDraftNamesAndTotals();
            RefreshEntryGridSafely();
        }
    }

    private void AddEntryLine()
    {
        if (_browseMode)
        {
            EnterAddMode("Form kembali ke tambah data. Input transaksi langsung pada tabel.");
            return;
        }

        var index = _lines.ToList().FindIndex(x => x.IsBlank);
        if (index < 0)
        {
            _lines.Add(new CashBankEntryDraft());
            index = _lines.Count - 1;
        }

        SelectLine(index);
        BeginCurrentCellEdit();
        _status.Text = "Isi transaksi langsung pada tabel. Tekan F4 atau double click untuk membuka daftar pilihan.";
        UpdateDraftNamesAndTotals();
    }

    private void ClearDrafts()
    {
        EnterAddMode("Input dibatalkan. Form kembali ke tambah data.");
    }

    private void CancelInput()
    {
        if (!ConfirmPendingDraft()) return;
        ClearDrafts();
    }

    public Task<bool> ConfirmCloseAsync() => Task.FromResult(ConfirmPendingDraft());

    private bool ConfirmPendingDraft()
    {
        if (_browseMode || !HasMeaningfulDraftInput()) return true;
        var choice = LocalDraftPromptWindow.Show(Window.GetWindow(this), "Kas & Bank");
        if (choice == LocalDraftChoice.KeepEditing)
        {
            _status.Text = "Aksi dibatalkan. Data belum disimpan tetap di form.";
            return false;
        }

        if (choice == LocalDraftChoice.SaveDraft)
        {
            try
            {
                SaveLocalDraft();
                return true;
            }
            catch (Exception exception)
            {
                ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Draft Kas & Bank");
                return false;
            }
        }

        LocalTransactionDraftStore.Delete(LocalDraftKey);
        return true;
    }

    private bool HasMeaningfulDraftInput()
    {
        CommitEntryGridEdit();
        return _lines.Any(line => !line.IsBlank);
    }

    private void SaveLocalDraft()
    {
        CommitEntryGridEdit();
        LocalTransactionDraftStore.Save(LocalDraftKey, new CashBankLocalDraft(
            _date.SelectedDate,
            _payment.IsChecked == true,
            _methodTabs.SelectedIndex,
            _lines.Where(line => !line.IsBlank)
                .Select(line => new CashBankLineLocalDraft(
                    line.SettlementMethod,
                    line.SettlementCode,
                    line.SettlementName,
                    line.Description,
                    line.AmountText,
                    line.GroupCode,
                    line.AccountCode,
                    line.AccountName))
                .ToArray()));
        _modeStatus.Text = "DRAFT LOKAL";
        _status.Text = "Draft Kas & Bank tersimpan lokal dan belum masuk jurnal.";
    }

    private void RestoreLocalDraft()
    {
        if (_draftChecked) return;
        _draftChecked = true;
        if (!LocalTransactionDraftStore.TryLoad<CashBankLocalDraft>(LocalDraftKey, out var draft) || draft is null) return;

        _browseMode = false;
        _currentTransaction = null;
        _historyIndex = -1;
        _date.SelectedDate = draft.DocumentDate ?? DateTime.Today;
        _payment.IsChecked = draft.IsPayment;
        _receipt.IsChecked = !draft.IsPayment;
        _methodTabs.SelectedIndex = Math.Clamp(draft.MethodIndex, 0, 2);
        _lines.Clear();
        foreach (var line in draft.Lines)
        {
            _lines.Add(new CashBankEntryDraft
            {
                SettlementMethod = line.SettlementMethod,
                SettlementCode = line.SettlementCode,
                SettlementName = line.SettlementName,
                Description = line.Description,
                AmountText = line.AmountText,
                GroupCode = line.GroupCode,
                AccountCode = line.AccountCode,
                AccountName = line.AccountName
            });
        }
        AddBlankRows(DefaultRows);
        _entryGrid.IsReadOnly = false;
        _date.IsEnabled = true;
        _receipt.IsEnabled = true;
        _payment.IsEnabled = true;
        _methodTabs.IsEnabled = true;
        UpdateState();
        UpdateDraftNamesAndTotals();
        UpdateCommandStates();
        _modeStatus.Text = "DRAFT LOKAL";
        _status.Text = "Draft Kas & Bank dipulihkan. Tekan Simpan untuk menjadikannya transaksi.";
    }

    private void EnterAddMode(string statusText)
    {
        _browseMode = false;
        _currentTransaction = null;
        _historyIndex = -1;
        _entryGrid.IsReadOnly = false;
        _date.IsEnabled = true;
        _receipt.IsEnabled = true;
        _payment.IsEnabled = true;
        _methodTabs.IsEnabled = true;
        _modeStatus.Text = "TAMBAH DATA";
        _lines.Clear();
        AddBlankRows(DefaultRows);
        UpdateDocumentPreview();
        UpdateDraftNamesAndTotals();
        RefreshEntryGridSafely();
        SelectFirstEmptyLine();
        UpdateCommandStates();
        _status.Text = statusText;
    }

    private async Task DeleteOrClearAsync()
    {
        if (_browseMode)
        {
            await DeleteCurrentTransactionAsync();
            return;
        }

        ClearSelectedLine();
    }

    private async Task DeleteCurrentTransactionAsync()
    {
        if (_currentTransaction is null || !SameCode(_currentTransaction.Status, "POSTED")) return;
        var owner = Window.GetWindow(this);
        var result = owner is null
            ? MessageBox.Show("Apakah anda ingin mendandai hapus data ini?", "AKSARA", MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(owner, "Apakah anda ingin mendandai hapus data ini?", "AKSARA", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            IsEnabled = false;
            _status.Text = "Menandai hapus transaksi kas-bank...";
            var cancelled = await _api.CancelCashBankTransactionAsync(_currentTransaction.Id, new("Ditandai hapus dari form kas-bank.", AksaraApiClient.Shared.CurrentUserName, Environment.MachineName));
            await ReloadAsync();
            var index = _history.ToList().FindIndex(x => x.Id == cancelled.Id);
            await LoadHistoryAtAsync(index < 0 ? 0 : index);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Kas & Bank");
        }
        finally
        {
            IsEnabled = true;
            UpdateCommandStates();
        }
    }

    private async Task RestoreCurrentTransactionAsync()
    {
        if (_currentTransaction is null || !SameCode(_currentTransaction.Status, "CANCELLED")) return;
        var owner = Window.GetWindow(this);
        var result = owner is null
            ? MessageBox.Show("Apakah anda ingin membatalkan hapus data?", "AKSARA", MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(owner, "Apakah anda ingin membatalkan hapus data?", "AKSARA", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            IsEnabled = false;
            _status.Text = "Membatalkan hapus transaksi kas-bank...";
            var restored = await _api.RestoreCashBankTransactionAsync(_currentTransaction.Id, new(AksaraApiClient.Shared.CurrentUserName, Environment.MachineName));
            await ReloadAsync();
            var index = _history.ToList().FindIndex(x => x.Id == restored.Id);
            await LoadHistoryAtAsync(index < 0 ? 0 : index);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Kas & Bank");
        }
        finally
        {
            IsEnabled = true;
            UpdateCommandStates();
        }
    }

    private void ClearSelectedLine()
    {
        if (_browseMode)
        {
            _status.Text = "Data lama sedang dibuka. Klik Tambah untuk membuat transaksi baru.";
            return;
        }

        if (_entryGrid.SelectedItem is CashBankEntryDraft line) line.Clear();
        UpdateDraftNamesAndTotals();
        RefreshEntryGridSafely();
    }

    private void UpdateCommandStates()
    {
        if (_deleteButton is not null)
            _deleteButton.IsEnabled = !_browseMode || SameCode(_currentTransaction?.Status, "POSTED");
        if (_restoreButton is not null)
            _restoreButton.IsEnabled = _browseMode && SameCode(_currentTransaction?.Status, "CANCELLED");
    }

    private void SelectFirstEmptyLine()
    {
        var index = _lines.ToList().FindIndex(x => x.IsBlank);
        SelectLine(index < 0 ? _lines.Count - 1 : index);
    }

    private void SelectLine(int index)
    {
        if (_lines.Count == 0) return;
        index = Math.Clamp(index, 0, _lines.Count - 1);
        _entryGrid.SelectedIndex = index;
        if (_entryGrid.Columns.Count > 0) _entryGrid.CurrentCell = new DataGridCellInfo(_lines[index], _entryGrid.Columns[0]);
        _entryGrid.ScrollIntoView(_lines[index]);
        _entryGrid.Focus();
    }

    private void BeginCurrentCellEdit()
    {
        if (_browseMode || _entryGrid.IsReadOnly || _entryGrid.CurrentColumn?.IsReadOnly == true) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_browseMode && !_entryGrid.IsReadOnly && _entryGrid.CurrentColumn?.IsReadOnly != true)
                _entryGrid.BeginEdit();
        }));
    }

    private void CommitEntryGridEdit()
    {
        _entryGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        _entryGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private void RefreshEntryGridSafely()
    {
        if (TryCommitEntryGridEdit()) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            TryCommitEntryGridEdit();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private bool TryCommitEntryGridEdit()
    {
        try
        {
            CommitEntryGridEdit();
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

    private void AddBlankRows(int count)
    {
        while (_lines.Count < count) _lines.Add(new CashBankEntryDraft());
    }

    private string SelectedMethod() => _methodTabs.SelectedIndex switch
    {
        1 => "BANK",
        2 => "CHECK_GIRO",
        _ => "CASH"
    };

    private void SetMethodTab(string method) => _methodTabs.SelectedIndex = Normalize(method) switch
    {
        "BANK" => 1,
        "CHECK_GIRO" => 2,
        _ => 0
    };

    private string? DefaultSettlementCode(string method)
    {
        if (method == "CASH") return _cashAccounts.FirstOrDefault()?.Code;
        return _bankAccounts.FirstOrDefault()?.Code;
    }

    private string SettlementName(string method, string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;
        if (method == "CASH") return _cashAccounts.FirstOrDefault(x => SameCode(x.Code, code))?.Name ?? string.Empty;
        return _bankAccounts.FirstOrDefault(x => SameCode(x.Code, code))?.BankName ?? string.Empty;
    }

    private CashBankCategoryDto? FindCategory(string code)
    {
        var type = _payment.IsChecked == true ? "EXPENSE" : "INCOME";
        return _categories.FirstOrDefault(x => x.CategoryType == type && SameCode(x.Code, code) && !x.IsDeleted && x.IsActive);
    }

    private string AccountName(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;
        return _accounts.FirstOrDefault(x => SameCode(x.Code, code))?.Name ?? string.Empty;
    }

    private static Binding DraftBinding(string property) => new(property) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };

    private static bool SameCode(string? left, string? right) => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static bool TryReadAmount(string? text, out decimal amount)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            amount = 0;
            return true;
        }
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out amount)
            || decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
    }

    private static void AddEntryColumns(Grid grid)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(135) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
    }

    private static void AddStripCell(Grid grid, UIElement child, int column)
    {
        var border = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(column == 0 ? 1 : 0, 1, 1, 1), Background = LedgerAccountView.Brush("#F7F7F7"), Child = child };
        Grid.SetColumn(border, column);
        grid.Children.Add(border);
    }

    private static void AddHeaderText(Grid grid, string text, int row, int column)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(5, 2, 8, 2) };
        Grid.SetRow(label, row); Grid.SetColumn(label, column); grid.Children.Add(label);
    }

    private static void AddTotalLabel(Grid grid, string text, int column)
    {
        var label = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 4, 0) };
        Grid.SetColumn(label, column); grid.Children.Add(label);
    }

    private static void AddTotalBox(Grid grid, TextBlock value, int column)
    {
        var border = new Border { Background = Brushes.White, BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(0, 7, 8, 7), Padding = new Thickness(4, 0, 4, 0), Child = value };
        Grid.SetColumn(border, column); grid.Children.Add(border);
    }

    private static TextBlock TotalValue() => new() { Text = "0", TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };

    private static Button ActionButton(string text, Action action, double width)
    {
        var button = new Button { Content = text, Width = width, Height = 25, MinHeight = 0, Margin = new Thickness(4, 0, 0, 0) };
        button.Click += (_, _) => action();
        return button;
    }

    private static Button ActionButton(string text, Func<Task> action, double width)
    {
        var button = new Button { Content = text, Width = width, Height = 25, MinHeight = 0, Margin = new Thickness(4, 0, 0, 0) };
        button.Click += async (_, _) => await action();
        return button;
    }

    private static void AddNav(StackPanel panel, string text, Func<Task> action)
    {
        var button = new Button { Content = text, Width = 34, Height = 25, MinHeight = 0, FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(0) };
        button.Click += async (_, _) => await action();
        panel.Children.Add(button);
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }
}

internal sealed class CashBankEntryDraft : INotifyPropertyChanged
{
    private string _settlementMethod = string.Empty;
    private string _settlementCode = string.Empty;
    private string _settlementName = string.Empty;
    private string _description = string.Empty;
    private string _amountText = "0";
    private string _groupCode = string.Empty;
    private string _accountCode = string.Empty;
    private string _accountName = string.Empty;

    public string SettlementMethod { get => _settlementMethod; set => SetField(ref _settlementMethod, value); }
    public string SettlementCode { get => _settlementCode; set => SetField(ref _settlementCode, value); }
    public string SettlementName { get => _settlementName; set => SetField(ref _settlementName, value); }
    public string Description { get => _description; set => SetField(ref _description, value); }
    public string AmountText { get => _amountText; set => SetField(ref _amountText, value); }
    public string GroupCode { get => _groupCode; set => SetField(ref _groupCode, value); }
    public string AccountCode { get => _accountCode; set => SetField(ref _accountCode, value); }
    public string AccountName { get => _accountName; set => SetField(ref _accountName, value); }

    public bool IsBlank =>
        string.IsNullOrWhiteSpace(SettlementCode)
        && string.IsNullOrWhiteSpace(Description)
        && string.IsNullOrWhiteSpace(GroupCode)
        && string.IsNullOrWhiteSpace(AccountCode)
        && (string.IsNullOrWhiteSpace(AmountText) || AmountText.Trim() == "0");

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Clear()
    {
        SettlementMethod = string.Empty;
        SettlementCode = string.Empty;
        SettlementName = string.Empty;
        Description = string.Empty;
        AmountText = "0";
        GroupCode = string.Empty;
        AccountCode = string.Empty;
        AccountName = string.Empty;
    }

    private void SetField(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        value ??= string.Empty;
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal sealed record CashBankLookupChoice(string Code, string Name, string? AccountCode = null, string? AccountName = null);

internal sealed class CashBankTransactionHistoryWindow : Window
{
    private readonly IReadOnlyList<CashBankTransactionDto> _transactions;
    private readonly TextBox _search = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, RowHeight = 23, SelectionMode = DataGridSelectionMode.Single };
    private IReadOnlyList<CashBankTransactionDto> _visibleRows = [];

    public CashBankTransactionDto? SelectedTransaction { get; private set; }
    public string SearchText { get => _search.Text; set => _search.Text = value ?? string.Empty; }
    public string InitialDocumentNumber { get; set; } = string.Empty;

    public CashBankTransactionHistoryWindow(IEnumerable<CashBankTransactionDto> transactions)
    {
        _transactions = transactions.ToList();
        Title = "History Kas & Bank";
        Width = 760;
        Height = 470;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Content = Build();
        _search.TextChanged += (_, _) => ApplyFilter();
        _grid.MouseDoubleClick += (_, _) => Accept();
        _grid.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            args.Handled = true;
            Accept();
        };
        Loaded += (_, _) =>
        {
            ApplyFilter();
            _search.Focus();
        };
    }

    private UIElement Build()
    {
        _grid.Columns.Add(LedgerAccountView.Column("Tanggal", nameof(CashBankHistoryRow.Tanggal), 88));
        _grid.Columns.Add(LedgerAccountView.Column("No. Bukti", "DocumentNumber", 115));
        _grid.Columns.Add(LedgerAccountView.Column("Jenis", nameof(CashBankHistoryRow.Jenis), 95));
        _grid.Columns.Add(LedgerAccountView.Column("Cara", nameof(CashBankHistoryRow.Cara), 90));
        _grid.Columns.Add(LedgerAccountView.Column("Relasi", "Counterparty", new DataGridLength(1, DataGridLengthUnitType.Star)));
        _grid.Columns.Add(LedgerAccountView.NumberColumn("Jumlah", "Amount", 125));
        _grid.Columns.Add(LedgerAccountView.Column("Status", "Status", 92));

        var root = new Grid { Margin = new Thickness(8) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_search);
        Grid.SetRow(_grid, 1); root.Children.Add(_grid);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 7, 0, 0) };
        var ok = new Button { Content = "Pilih", Width = 86, IsDefault = true, Margin = new Thickness(4, 0, 0, 0) };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Batal", Width = 86, IsCancel = true, Margin = new Thickness(4, 0, 0, 0) });
        Grid.SetRow(buttons, 2); root.Children.Add(buttons);
        return root;
    }

    private void ApplyFilter()
    {
        var query = _search.Text.Trim();
        _visibleRows = string.IsNullOrWhiteSpace(query)
            ? _transactions
            : _transactions
                .Where(x => Contains(x.DocumentNumber, query)
                    || Contains(x.Counterparty, query)
                    || Contains(x.Notes, query)
                    || Contains(x.AccountCode, query)
                    || Contains(x.AccountName, query))
                .ToList();
        var rows = _visibleRows.Select(CashBankHistoryRow.From).ToList();
        _grid.ItemsSource = rows;
        if (rows.Count == 0) return;

        var selectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(InitialDocumentNumber))
        {
            var index = _visibleRows.ToList().FindIndex(x => SameDocument(x.DocumentNumber, InitialDocumentNumber));
            if (index >= 0) selectedIndex = index;
        }

        _grid.SelectedIndex = selectedIndex;
        _grid.ScrollIntoView(rows[selectedIndex]);
    }

    private void Accept()
    {
        if (_grid.SelectedItem is not CashBankHistoryRow row) return;
        var transaction = _transactions.FirstOrDefault(x => x.Id == row.Id);
        if (transaction is null) return;
        SelectedTransaction = transaction;
        DialogResult = true;
    }

    private static bool Contains(string? value, string query) =>
        value?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool SameDocument(string? left, string? right) =>
        string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
}

internal sealed record CashBankHistoryRow(
    Guid Id,
    string Tanggal,
    string DocumentNumber,
    string Jenis,
    string Cara,
    string? Counterparty,
    decimal Amount,
    string Status)
{
    public static CashBankHistoryRow From(CashBankTransactionDto transaction) => new(
        transaction.Id,
        transaction.DocumentDate.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("id-ID")),
        transaction.DocumentNumber,
        Same(transaction.TransactionKind, "PAYMENT") ? "Pengeluaran" : "Penerimaan",
        transaction.Method switch
        {
            "BANK" => "Bank",
            "CHECK_GIRO" => "Cek/Giro",
            _ => "Kas"
        },
        transaction.Counterparty,
        transaction.Amount,
        transaction.Status);

    private static bool Same(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

internal sealed class CashBankChoiceLookupWindow : Window
{
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, RowHeight = 23, SelectionMode = DataGridSelectionMode.Single };
    private readonly IReadOnlyList<CashBankLookupChoice> _choices;
    public CashBankLookupChoice? SelectedChoice { get; private set; }

    public CashBankChoiceLookupWindow(string title, IEnumerable<CashBankLookupChoice> choices)
    {
        _choices = choices.ToList();
        Title = title;
        Width = 520;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Content = Build();
        _grid.ItemsSource = _choices;
        Loaded += (_, _) => { if (_choices.Count > 0) _grid.SelectedIndex = 0; };
        _grid.MouseDoubleClick += (_, _) => Accept();
    }

    private UIElement Build()
    {
        _grid.Columns.Add(LedgerAccountView.Column("Kode", "Code", 90));
        _grid.Columns.Add(LedgerAccountView.Column("Nama", "Name", new DataGridLength(1, DataGridLengthUnitType.Star)));
        _grid.Columns.Add(LedgerAccountView.Column("Perkiraan", "AccountCode", 100));

        var root = new Grid { Margin = new Thickness(8) };
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_grid);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 7, 0, 0) };
        var ok = new Button { Content = "Pilih", Width = 86, IsDefault = true, Margin = new Thickness(4, 0, 0, 0) };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Batal", Width = 86, IsCancel = true, Margin = new Thickness(4, 0, 0, 0) });
        Grid.SetRow(buttons, 1); root.Children.Add(buttons);
        return root;
    }

    private void Accept()
    {
        if (_grid.SelectedItem is not CashBankLookupChoice choice) return;
        SelectedChoice = choice;
        DialogResult = true;
    }
}

internal sealed class CashBankTransactionViewLegacy : UserControl
{
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly RadioButton _receipt = new() { Content = "Penerimaan", IsChecked = true, Margin = new Thickness(0, 0, 12, 0) };
    private readonly RadioButton _payment = new() { Content = "Pengeluaran", Margin = new Thickness(0, 0, 12, 0) };
    private readonly RadioButton _cash = new() { Content = "Kas", IsChecked = true, Margin = new Thickness(0, 0, 12, 0) };
    private readonly RadioButton _bank = new() { Content = "Bank", Margin = new Thickness(0, 0, 12, 0) };
    private readonly RadioButton _giro = new() { Content = "Cek / Giro", Margin = new Thickness(0, 0, 12, 0) };
    private readonly DatePicker _date = new() { SelectedDate = DateTime.Today, Width = 125 };
    private readonly ComboBox _cashAccounts = new() { DisplayMemberPath = "Name", SelectedValuePath = "Code" };
    private readonly ComboBox _bankAccounts = new() { DisplayMemberPath = "BankName", SelectedValuePath = "Code" };
    private readonly ComboBox _category = new() { DisplayMemberPath = "Name", SelectedValuePath = "Code" };
    private readonly TextBox _checkNumber = new();
    private readonly DatePicker _checkDueDate = new() { Width = 125 };
    private readonly TextBox _counterparty = new();
    private readonly TextBox _accountCode = new();
    private readonly TextBlock _accountName = new() { Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _amount = new() { Text = "0" };
    private readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 42 };
    private readonly TextBlock _title = new() { Text = "PENERIMAAN", Foreground = Brushes.Red, FontSize = 19, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Right };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly DataGrid _history = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, RowHeight = 23, SelectionMode = DataGridSelectionMode.Single };
    private IReadOnlyList<CashBankCategoryDto> _categories = [];
    public event EventHandler? CloseRequested;

    public CashBankTransactionViewLegacy()
    {
        Content = Build();
        BuildColumns();
        Loaded += async (_, _) => { await LoadMastersAsync(); await ReloadAsync(); };
        _receipt.Checked += (_, _) => UpdateState();
        _payment.Checked += (_, _) => UpdateState();
        _cash.Checked += (_, _) => UpdateState();
        _bank.Checked += (_, _) => UpdateState();
        _giro.Checked += (_, _) => UpdateState();
        _category.SelectionChanged += (_, _) => FillAccountFromCategory();
    }

    private UIElement Build()
    {
        var root = LedgerAccountView.Shell("KAS BANK", _status, out var footer);
        var form = new Grid { Margin = new Thickness(0, 6, 0, 8) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        form.ColumnDefinitions.Add(new ColumnDefinition());
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        form.ColumnDefinitions.Add(new ColumnDefinition());
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        for (var i = 0; i < 8; i++) form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var kind = new StackPanel { Orientation = Orientation.Horizontal };
        kind.Children.Add(_receipt); kind.Children.Add(_payment);
        Add(form, "Jenis", kind, 0, 0);
        Grid.SetColumn(_title, 4); Grid.SetRow(_title, 0); form.Children.Add(_title);
        var methods = new StackPanel { Orientation = Orientation.Horizontal };
        methods.Children.Add(_cash); methods.Children.Add(_bank); methods.Children.Add(_giro);
        Add(form, "Cara", methods, 1, 0);
        Add(form, "Tanggal", _date, 0, 2);
        Add(form, "Kas", _cashAccounts, 1, 2);
        Add(form, "Bank", _bankAccounts, 2, 2);
        Add(form, "No. Cek/Giro", _checkNumber, 3, 0);
        Add(form, "Jatuh Tempo", _checkDueDate, 3, 2);
        Add(form, "Dari/Kepada", _counterparty, 4, 0);
        Add(form, "Group", _category, 4, 2);
        var accountPanel = new DockPanel();
        var lookup = new Button { Content = "...", Width = 34, Margin = new Thickness(5, 3, 0, 3) }; lookup.Click += (_, _) => LookupAccount();
        DockPanel.SetDock(lookup, Dock.Right); accountPanel.Children.Add(lookup); accountPanel.Children.Add(_accountCode);
        Add(form, "K/B/P", accountPanel, 5, 0);
        Add(form, "", _accountName, 5, 2);
        Add(form, "Jumlah", _amount, 6, 0);
        Add(form, "Catatan", _notes, 7, 0, 3);
        var save = new Button { Content = "Simpan", Width = 95, Height = 26, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(6, 3, 0, 3) };
        save.Click += Save_Click; Grid.SetColumn(save, 4); Grid.SetRow(save, 7); form.Children.Add(save);
        Grid.SetRow(form, 1); root.Children.Add(form);

        Grid.SetRow(_history, 2); root.Children.Add(_history);
        var refresh = LedgerAccountView.Button("Muat Ulang"); refresh.Click += async (_, _) => await ReloadAsync();
        footer.Children.Add(refresh);
        footer.Children.Add(LedgerAccountView.CloseButton(() => CloseRequested?.Invoke(this, EventArgs.Empty)));
        return root;
    }

    private void BuildColumns()
    {
        _history.Columns.Add(LedgerAccountView.Column("Tanggal", "DocumentDate", 85));
        _history.Columns.Add(LedgerAccountView.Column("No. Bukti", "DocumentNumber", 110));
        _history.Columns.Add(LedgerAccountView.Column("Jenis", "TransactionKind", 90));
        _history.Columns.Add(LedgerAccountView.Column("Cara", "Method", 85));
        _history.Columns.Add(LedgerAccountView.Column("Relasi", "Counterparty", 150));
        _history.Columns.Add(LedgerAccountView.Column("Kode Akun", "AccountCode", 95));
        _history.Columns.Add(LedgerAccountView.Column("Nama Akun", "AccountName", new DataGridLength(1, DataGridLengthUnitType.Star)));
        _history.Columns.Add(LedgerAccountView.NumberColumn("Jumlah", "Amount", 115));
        _history.Columns.Add(LedgerAccountView.Column("No. Jurnal", "JournalNumber", 115));
    }

    private async Task LoadMastersAsync()
    {
        try
        {
            var cash = (await _api.GetCashAccountsAsync(false)).ToList();
            var bank = (await _api.GetBankAccountsAsync(false)).ToList();
            _categories = await _api.GetCashBankCategoriesAsync(null, false);
            _cashAccounts.ItemsSource = cash; if (cash.Count > 0) _cashAccounts.SelectedIndex = 0;
            _bankAccounts.ItemsSource = bank; if (bank.Count > 0) _bankAccounts.SelectedIndex = 0;
            UpdateState();
        }
        catch (Exception exception) { _status.Text = exception.Message; }
    }

    private async Task ReloadAsync()
    {
        try
        {
            IsEnabled = false; _status.Text = "Mengambil histori kas-bank...";
            var rows = (await _api.GetCashBankTransactionsAsync(null, 5000)).ToList();
            _history.ItemsSource = rows;
            _status.Text = $"{rows.Count:N0} transaksi | Setiap simpan membentuk jurnal dan langsung tampil di Jurnal Umum.";
        }
        catch (Exception exception) { _status.Text = exception.Message; ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Kas Bank"); }
        finally { IsEnabled = true; }
    }

    private void UpdateState()
    {
        var isReceipt = _receipt.IsChecked == true;
        _title.Text = isReceipt ? "PENERIMAAN" : "PENGELUARAN";
        _title.Foreground = isReceipt ? Brushes.Red : Brushes.DarkRed;
        _cashAccounts.IsEnabled = _cash.IsChecked == true;
        _bankAccounts.IsEnabled = _bank.IsChecked == true || _giro.IsChecked == true;
        _checkNumber.IsEnabled = _giro.IsChecked == true;
        _checkDueDate.IsEnabled = _giro.IsChecked == true;
        var type = isReceipt ? "INCOME" : "EXPENSE";
        var filtered = _categories.Where(x => x.CategoryType == type && !x.IsDeleted && x.IsActive).ToList();
        _category.ItemsSource = filtered;
        if (filtered.Count > 0) _category.SelectedIndex = 0;
        FillAccountFromCategory();
    }

    private void FillAccountFromCategory()
    {
        if (_category.SelectedItem is not CashBankCategoryDto category) return;
        _accountCode.Text = category.AccountCode;
        _accountName.Text = category.AccountName;
    }

    private void LookupAccount()
    {
        var lookup = _payment.IsChecked == true ? new LedgerAccountLookupWindow(true, "EXPENSE") : new LedgerAccountLookupWindow(true, "REVENUE");
        lookup.Owner = Window.GetWindow(this);
        if (lookup.ShowDialog() == true && lookup.SelectedAccount is not null)
        {
            _accountCode.Text = lookup.SelectedAccount.Code;
            _accountName.Text = lookup.SelectedAccount.Name;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!decimal.TryParse(_amount.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var amount))
                throw new InvalidOperationException("Jumlah harus berupa angka.");
            var method = _giro.IsChecked == true ? "CHECK_GIRO" : _bank.IsChecked == true ? "BANK" : "CASH";
            var category = _category.SelectedItem as CashBankCategoryDto;
            IsEnabled = false; _status.Text = "Menyimpan transaksi dan jurnal...";
            await _api.CreateCashBankTransactionAsync(new CreateCashBankTransactionRequest(
                DateOnly.FromDateTime(_date.SelectedDate ?? DateTime.Today),
                _receipt.IsChecked == true ? "RECEIPT" : "PAYMENT",
                method,
                method == "CASH" ? _cashAccounts.SelectedValue?.ToString() : null,
                method == "CASH" ? null : _bankAccounts.SelectedValue?.ToString(),
                _giro.IsChecked == true ? _checkNumber.Text : null,
                _giro.IsChecked == true && _checkDueDate.SelectedDate is not null ? DateOnly.FromDateTime(_checkDueDate.SelectedDate.Value) : null,
                _counterparty.Text,
                category?.CategoryType,
                category?.Code,
                _accountCode.Text,
                amount,
                _notes.Text,
                AksaraApiClient.Shared.CurrentUserName,
                Environment.MachineName));
            _amount.Text = "0"; _counterparty.Clear(); _notes.Clear(); _checkNumber.Clear();
            await ReloadAsync();
        }
        catch (Exception exception) { _status.Text = exception.Message; ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Kas Bank"); }
        finally { IsEnabled = true; }
    }

    private static void Add(Grid grid, string label, UIElement input, int row, int column, int columnSpan = 1)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(column == 0 ? 0 : 10, 3, 8, 3) };
        Grid.SetRow(text, row); Grid.SetColumn(text, column); grid.Children.Add(text);
        if (input is FrameworkElement element) element.Margin = new Thickness(0, 3, 0, 3);
        Grid.SetRow(input, row); Grid.SetColumn(input, column + 1); Grid.SetColumnSpan(input, columnSpan); grid.Children.Add(input);
    }
}
