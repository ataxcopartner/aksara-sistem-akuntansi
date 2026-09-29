using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Aksara.Contracts;
using Aksara.Desktop.Controls;
using Aksara.Desktop.Services;
using Microsoft.Win32;

namespace Aksara.Desktop.Views;

public sealed class InitialSetupView : UserControl, IWorkspaceCloseGuard
{
    private static readonly SolidColorBrush SuccessBrush = Brush("#246B3F");
    private static readonly SolidColorBrush SuccessSoftBrush = Brush("#EAF6EE");
    private static readonly SolidColorBrush WarningBrush = Brush("#965C00");

    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly DatePicker _start = DateInput();
    private readonly DatePicker _cutoff = DateInput();
    private readonly TextBox _profileCompany = ProfileInput();
    private readonly TextBox _profileOwner = ProfileInput();
    private readonly TextBox _profileAddress = ProfileInput(multiline: true);
    private readonly TextBox _profileCity = ProfileInput();
    private readonly TextBox _profilePhone = ProfileInput();
    private readonly TextBox _profileEmail = ProfileInput();
    private readonly TextBox _profileTaxNumber = ProfileInput();
    private readonly TextBlock _setupBadgeText = new() { FontWeight = FontWeights.Bold };
    private readonly Border _setupBadge = new() { CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 5, 12, 5) };
    private readonly TextBlock _stageSummary = new() { Foreground = PartnerUi.MutedBrush, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _openingSummary = new() { Foreground = PartnerUi.MutedBrush, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _preflightSummary = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _activationSummary = new() { Foreground = PartnerUi.MutedBrush, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _operationStatus = new()
    {
        Foreground = PartnerUi.MutedBrush,
        FontWeight = FontWeights.SemiBold,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly ProgressBar _busyIndicator = new()
    {
        Width = 104,
        Height = 4,
        IsIndeterminate = true,
        Foreground = PartnerUi.GoldBrush,
        Background = PartnerUi.ReadOnlyBrush,
        Visibility = Visibility.Collapsed,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly WrapPanel _stepRail = new() { Orientation = Orientation.Horizontal };
    private readonly ContentControl _wizardContent = new();
    private readonly TextBlock _wizardStep = new()
    {
        Foreground = PartnerUi.MaroonBrush,
        FontWeight = FontWeights.Bold
    };
    private readonly TextBlock _wizardTitle = new()
    {
        FontSize = 19,
        FontWeight = FontWeights.Bold,
        Foreground = PartnerUi.MaroonDarkBrush,
        Margin = new Thickness(0, 3, 0, 0)
    };
    private readonly TextBlock _wizardDetail = new()
    {
        Foreground = PartnerUi.MutedBrush,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 0)
    };
    private readonly ListBox _errors = ErrorList();
    private readonly DataGrid _inventoryGrid = SetupGrid(readOnly: false);
    private readonly DataGrid _partnerGrid = SetupGrid(readOnly: false);
    private readonly DataGrid _ledgerGrid = SetupGrid(readOnly: false);
    private readonly DataGrid _accountGrid = SetupGrid(readOnly: true);
    private readonly TextBox _accountSearch = new() { Width = 240, Height = 29, Margin = new Thickness(5, 0, 8, 0) };
    private readonly TextBlock _accountStatus = new() { Foreground = PartnerUi.MutedBrush, VerticalAlignment = VerticalAlignment.Center };
    private readonly ObservableCollection<InventoryDraftRow> _inventory = [];
    private readonly ObservableCollection<PartnerDraftRow> _partners = [];
    private readonly ObservableCollection<LedgerDraftRow> _ledger = [];
    private readonly List<Button> _draftEditButtons = [];
    private readonly List<Button> _accountEditButtons = [];
    private readonly Button _save = PartnerUi.Button("Simpan Draft", 118, PartnerButtonKind.Gold, "\uE74E", "Simpan perubahan saldo awal sebagai draft");
    private readonly Button _activate = PartnerUi.Button("Aktifkan Setup", 128, PartnerButtonKind.Primary, "\uE73E", "Posting seluruh saldo awal setelah preflight valid");
    private readonly Button _reopen = PartnerUi.Button("Buka Kembali", 124, PartnerButtonKind.Danger, "\uE72B", "Buka kembali setup yang sudah aktif");
    private readonly Button _preflight = PartnerUi.Button("Periksa Ulang", 118, PartnerButtonKind.Gold, "\uE9D9", "Jalankan ulang rekonsiliasi dan preflight");
    private readonly Button _previous = PartnerUi.Button("Kembali", 104, PartnerButtonKind.Standard, "\uE72B", "Kembali ke tahap sebelumnya");
    private readonly Button _next = PartnerUi.Button("Simpan & Lanjut", 146, PartnerButtonKind.Primary, "\uE72A", "Simpan bila diperlukan dan lanjut ke tahap berikutnya");
    private readonly Button _close = PartnerUi.Button("Tutup", 88, PartnerButtonKind.Standard, "\uE8BB", "Tutup Setup Data Awal");
    private readonly List<FrameworkElement> _stagePages = [];
    private StageDisplayRow[] _visibleStages = [];
    private InitialSetupDto? _current;
    private ProgramConfigurationDto? _programConfiguration;
    private int _currentStageIndex;
    private bool _initialFocusApplied;
    private bool _loaded;

    public event EventHandler? CloseRequested;
    public event EventHandler<string>? OpenRouteRequested;
    public event Action<InitialSetupDto>? StateChanged;
    public event EventHandler? LogoutRequested;

    public InitialSetupView(InitialSetupDto? initialState = null)
    {
        ConfigureDraftGrids();
        ConfigureAccountGrid();

        _preflight.Click += async (_, _) => await LoadAsync();
        _save.Click += async (_, _) => await SaveAsync();
        _activate.Click += async (_, _) => await ActivateAsync();
        _reopen.Click += async (_, _) => await ReopenAsync();
        _previous.Click += (_, _) => NavigateToStage(_currentStageIndex - 1);
        _next.Click += async (_, _) => await ContinueAsync();
        _accountSearch.KeyDown += async (_, eventArgs) =>
        {
            if (eventArgs.Key != System.Windows.Input.Key.Enter) return;
            eventArgs.Handled = true;
            await ReloadAccountsAsync();
        };
        _accountGrid.MouseDoubleClick += async (_, _) => await EditAccountAsync();

        Content = BuildLayout();
        if (initialState is not null)
        {
            _current = initialState;
            ApplyState(initialState);
        }
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            await LoadAsync();
        };
    }

    public Task<bool> ConfirmCloseAsync()
    {
        if (InitialSetupFlowPolicy.CanCloseSetup(_current)) return Task.FromResult(true);
        var result = MessageBox.Show(
            Window.GetWindow(this),
            "Setup Data Awal belum selesai dan aplikasi transaksi masih dikunci.\n\nPilih Ya untuk kembali ke setup, atau Tidak untuk logout dan keluar dari AKSARA.",
            "Setup Data Awal Wajib",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);
        if (result == MessageBoxResult.No) LogoutRequested?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(false);
    }

    public Task RefreshFromServerAsync() => LoadAsync();

    private FrameworkElement BuildLayout()
    {
        var root = new Grid { Background = PartnerUi.CanvasBrush };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(27) });

        root.Children.Add(BuildHeader());

        var progress = BuildWizardProgress();
        Grid.SetRow(progress, 1);
        root.Children.Add(progress);

        _stagePages.Add(BuildCompanyProfile());
        _stagePages.Add(BuildOpeningDates());
        _stagePages.Add(BuildAccounts());
        _stagePages.Add(BuildMasterData());
        _stagePages.Add(BuildOpeningBalances());
        _stagePages.Add(BuildPreflight());
        _stagePages.Add(BuildActivation());

        var workspace = BuildWizardWorkspace();
        Grid.SetRow(workspace, 2);
        root.Children.Add(workspace);

        var commands = BuildCommands();
        Grid.SetRow(commands, 3);
        root.Children.Add(commands);

        var statusLayout = new Grid();
        statusLayout.ColumnDefinitions.Add(new ColumnDefinition());
        statusLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusLayout.Children.Add(_operationStatus);
        Grid.SetColumn(_busyIndicator, 1);
        statusLayout.Children.Add(_busyIndicator);

        var status = new Border
        {
            Background = PartnerUi.SurfaceBrush,
            BorderBrush = PartnerUi.BorderBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 0, 14, 0),
            Child = statusLayout
        };
        Grid.SetRow(status, 4);
        root.Children.Add(status);
        return root;
    }

    private FrameworkElement BuildHeader()
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock
        {
            Text = "\uE713",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 22,
            Foreground = PartnerUi.MaroonBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 11, 0)
        });
        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = "Setup Data Awal",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = PartnerUi.MaroonBrush
        });
        text.Children.Add(new TextBlock
        {
            Text = "Selesaikan tujuh tahap persiapan sebelum transaksi operasional dibuka",
            Foreground = PartnerUi.MutedBrush,
            Margin = new Thickness(0, 2, 0, 0)
        });
        title.Children.Add(text);
        header.Children.Add(title);

        _setupBadge.Child = _setupBadgeText;
        Grid.SetColumn(_setupBadge, 1);
        header.Children.Add(_setupBadge);

        var card = PartnerUi.Card(header, new Thickness(14, 9, 14, 9), new Thickness(12, 10, 12, 8));
        card.BorderThickness = new Thickness(4, 1, 1, 1);
        card.BorderBrush = PartnerUi.MaroonBrush;
        return card;
    }

    private FrameworkElement BuildWizardProgress()
    {
        var panel = new Grid { Margin = new Thickness(16, 5, 16, 7) };
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _stepRail.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(_stepRail);
        Grid.SetColumn(_stageSummary, 1);
        panel.Children.Add(_stageSummary);
        return panel;
    }

    private FrameworkElement BuildWizardWorkspace()
    {
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        heading.Children.Add(_wizardStep);
        heading.Children.Add(_wizardTitle);
        heading.Children.Add(_wizardDetail);
        layout.Children.Add(heading);

        _wizardContent.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _wizardContent.VerticalContentAlignment = VerticalAlignment.Stretch;
        var stageScroll = new ScrollViewer
        {
            Content = _wizardContent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            PanningMode = PanningMode.VerticalOnly,
            Padding = new Thickness(0, 0, 5, 6)
        };
        Grid.SetRow(stageScroll, 1);
        layout.Children.Add(stageScroll);

        var card = PartnerUi.Card(layout, new Thickness(22, 18, 22, 18), new Thickness(12, 0, 12, 6));
        card.MinHeight = 470;
        return card;
    }

    private FrameworkElement BuildCompanyProfile()
    {
        var fields = new Grid
        {
            Margin = new Thickness(0, 0, 12, 0)
        };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        fields.ColumnDefinitions.Add(new ColumnDefinition());
        AddProfileField(fields, "Nama Perusahaan", _profileCompany);
        AddProfileField(fields, "Nama Pemilik", _profileOwner);
        AddProfileField(fields, "Alamat", _profileAddress, 78);
        AddProfileField(fields, "Kota", _profileCity);
        AddProfileField(fields, "Telepon", _profilePhone);
        AddProfileField(fields, "Email", _profileEmail);
        AddProfileField(fields, "NPWP", _profileTaxNumber);

        return fields;
    }

    private FrameworkElement BuildOpeningDates()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Tanggal cutoff adalah posisi saldo awal. Transaksi operasional dimulai setelah tanggal tersebut.",
            Foreground = PartnerUi.TextBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        var dates = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        dates.ColumnDefinitions.Add(new ColumnDefinition());
        dates.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        dates.ColumnDefinitions.Add(new ColumnDefinition());
        dates.Children.Add(BuildDateField("Cutoff saldo awal", _cutoff));
        var startDate = BuildDateField("Mulai transaksi", _start);
        Grid.SetColumn(startDate, 2);
        dates.Children.Add(startDate);
        panel.Children.Add(dates);
        return panel;
    }

    private FrameworkElement BuildAccounts()
    {
        var accounts = new Grid { MinHeight = 370 };
        accounts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        accounts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        accounts.RowDefinitions.Add(new RowDefinition { Height = new GridLength(300) });
        accounts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        accounts.Children.Add(new TextBlock
        {
            Text = "Daftar perkiraan perusahaan. Klik ganda pada akun untuk mengubahnya, atau impor COA dari template Excel.",
            Foreground = PartnerUi.TextBrush,
            TextWrapping = TextWrapping.Wrap
        });

        var tools = new DockPanel { Margin = new Thickness(0, 10, 0, 8), LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var add = AccountToolbarButton("Tambah", 96, PartnerButtonKind.Standard, "\uE710", "Tambah akun perkiraan");
        var edit = AccountToolbarButton("Ubah", 92, PartnerButtonKind.Standard, "\uE70F", "Ubah akun yang dipilih");
        var import = AccountToolbarButton("Impor Excel", 126, PartnerButtonKind.Standard, "\uE8B5", "Impor COA dari Excel");
        var template = AccountToolbarButton("Unduh Template", 140, PartnerButtonKind.Standard, "\uE896", "Unduh template impor COA");
        var mapping = AccountToolbarButton("Hubungkan Akun", 150, PartnerButtonKind.Gold, "\uE71B", "Hubungkan akun ke transaksi otomatis");
        add.Click += async (_, _) => await OpenAccountEditorAsync(null);
        edit.Click += async (_, _) => await EditAccountAsync();
        import.Click += async (_, _) => await ImportAccountsAsync();
        template.Click += (_, _) => ReportGridTools.ExportLedgerAccountTemplate();
        mapping.Click += (_, _) => OpenRouteRequested?.Invoke(this, "LEDGER_MAPPING");
        _accountEditButtons.AddRange([add, edit, import]);
        actions.Children.Add(add);
        actions.Children.Add(edit);
        actions.Children.Add(import);
        actions.Children.Add(template);
        actions.Children.Add(mapping);
        DockPanel.SetDock(actions, Dock.Right);
        tools.Children.Add(actions);
        tools.Children.Add(new TextBlock { Text = "Cari", VerticalAlignment = VerticalAlignment.Center });
        tools.Children.Add(_accountSearch);
        Grid.SetRow(tools, 1);
        accounts.Children.Add(tools);

        Grid.SetRow(_accountGrid, 2);
        accounts.Children.Add(_accountGrid);
        _accountStatus.Margin = new Thickness(0, 7, 0, 0);
        Grid.SetRow(_accountStatus, 3);
        accounts.Children.Add(_accountStatus);
        return accounts;
    }

    private static Button AccountToolbarButton(string caption, double width, PartnerButtonKind kind, string glyph, string tooltip)
    {
        var button = PartnerUi.Button(caption, width, kind, tooltip: tooltip);
        button.Height = 34;

        var iconForeground = kind == PartnerButtonKind.Gold ? PartnerUi.MaroonDarkBrush : PartnerUi.MaroonBrush;
        var icon = new Border
        {
            Width = 21,
            Height = 21,
            CornerRadius = new CornerRadius(4),
            Background = kind == PartnerButtonKind.Gold ? Brush("#F3D675") : PartnerUi.ReadOnlyBrush,
            BorderBrush = kind == PartnerButtonKind.Gold ? PartnerUi.GoldBrush : PartnerUi.BorderBrush,
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 12.5,
                Foreground = iconForeground,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(icon);
        content.Children.Add(new TextBlock
        {
            Text = caption,
            Foreground = button.Foreground,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0)
        });
        button.Content = content;
        return button;
    }

    private FrameworkElement BuildMasterData()
    {
        var masters = new StackPanel();
        masters.Children.Add(new TextBlock
        {
            Text = "Lengkapi master data yang akan dipakai saat transaksi. Anda dapat kembali ke tahap ini sebelum aktivasi.",
            Foreground = PartnerUi.TextBrush,
            TextWrapping = TextWrapping.Wrap
        });
        var masterButtons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        AddRouteButton(masterButtons, "Gudang", "WAREHOUSES", 96, "\uE7B8");
        AddRouteButton(masterButtons, "Satuan", "UNITS", 92, "\uE8CB");
        AddRouteButton(masterButtons, "Kelompok", "CATEGORIES", 104, "\uE8EC");
        AddRouteButton(masterButtons, "Barang", "ITEMS", 94, "\uE719");
        AddRouteButton(masterButtons, "Supplier", "SUPPLIERS", 102, "\uE77B");
        AddRouteButton(masterButtons, "Customer", "CUSTOMERS", 104, "\uE716");
        AddRouteButton(masterButtons, "Salesman", "SALESMEN", 104, "\uE13D");
        AddRouteButton(masterButtons, "Kas", "CASH", 82, "\uE8C7");
        AddRouteButton(masterButtons, "Bank", "BANK", 86, "\uE825");
        AddRouteButton(masterButtons, "Biaya/Penghasilan", "CATEGORIES_CASH", 146, "\uE9D2");
        masters.Children.Add(masterButtons);
        return masters;
    }

    private FrameworkElement BuildOpeningBalances()
    {
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(29) });
        panel.RowDefinitions.Add(new RowDefinition());
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.Children.Add(new TextBlock
        {
            Text = "Masukkan saldo awal sesuai hasil cutoff.",
            Foreground = PartnerUi.TextBrush,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(_openingSummary, 1);
        panel.Children.Add(_openingSummary);

        var tabs = new TabControl { MinHeight = 330 };
        tabs.Items.Add(Tab("Stok per Gudang + HPP", BuildInventory()));
        tabs.Items.Add(Tab("Utang & Piutang", BuildPartners()));
        tabs.Items.Add(Tab("Kas/Bank & Akun Lain", BuildLedger()));
        Grid.SetRow(tabs, 1);
        Grid.SetColumnSpan(tabs, 2);
        panel.Children.Add(tabs);
        return panel;
    }

    private FrameworkElement BuildPreflight()
    {
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        panel.RowDefinitions.Add(new RowDefinition());
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock
        {
            Text = "Hasil pemeriksaan kesiapan data",
            Foreground = PartnerUi.TextBrush,
            VerticalAlignment = VerticalAlignment.Center
        });
        _preflightSummary.Margin = new Thickness(14, 0, 0, 0);
        title.Children.Add(_preflightSummary);
        panel.Children.Add(title);
        Grid.SetColumn(_preflight, 1);
        panel.Children.Add(_preflight);

        var errorsBorder = new Border
        {
            Background = Brushes.White,
            BorderBrush = PartnerUi.BorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5),
            Child = _errors
        };
        Grid.SetRow(errorsBorder, 1);
        Grid.SetColumnSpan(errorsBorder, 2);
        panel.Children.Add(errorsBorder);
        return panel;
    }

    private FrameworkElement BuildActivation()
    {
        var panel = new StackPanel { MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock
        {
            Text = "Setup siap diaktifkan",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = PartnerUi.MaroonDarkBrush
        });
        _activationSummary.Margin = new Thickness(0, 8, 0, 18);
        panel.Children.Add(_activationSummary);
        panel.Children.Add(new TextBlock
        {
            Text = "Aktivasi akan memposting seluruh saldo awal secara atomik dan membuka menu operasional sesuai hak akses pengguna.",
            Foreground = PartnerUi.TextBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18)
        });
        _activate.HorizontalAlignment = HorizontalAlignment.Left;
        _activate.Margin = new Thickness(0);
        panel.Children.Add(_activate);
        _reopen.HorizontalAlignment = HorizontalAlignment.Left;
        _reopen.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(_reopen);
        return panel;
    }

    private FrameworkElement BuildCommands()
    {
        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _close.Margin = new Thickness(0);
        _close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(_save);
        actions.Children.Add(_previous);
        actions.Children.Add(_next);
        actions.Children.Add(_close);
        Grid.SetColumn(actions, 1);
        panel.Children.Add(actions);
        return PartnerUi.Card(panel, new Thickness(12, 8, 8, 8), new Thickness(12, 0, 12, 8));
    }

    private UIElement BuildInventory()
    {
        _inventoryGrid.ItemsSource = _inventory;
        return WithRowButtons(_inventoryGrid, () => _inventory.Add(new()), () => Remove(_inventoryGrid, _inventory));
    }

    private UIElement BuildPartners()
    {
        _partnerGrid.ItemsSource = _partners;
        return WithRowButtons(_partnerGrid, () => _partners.Add(new()), () => Remove(_partnerGrid, _partners));
    }

    private UIElement BuildLedger()
    {
        _ledgerGrid.ItemsSource = _ledger;
        return WithRowButtons(_ledgerGrid, () => _ledger.Add(new()), () => Remove(_ledgerGrid, _ledger));
    }

    private void ConfigureDraftGrids()
    {
        _inventoryGrid.Columns.Add(Text("Gudang", nameof(InventoryDraftRow.WarehouseCode), 125));
        var itemCode = Text("Kode Barang", nameof(InventoryDraftRow.ItemCode), new DataGridLength(1, DataGridLengthUnitType.Star));
        itemCode.MinWidth = 340;
        _inventoryGrid.Columns.Add(itemCode);
        _inventoryGrid.Columns.Add(Text("Kuantitas", nameof(InventoryDraftRow.Quantity), 145, rightAligned: true));
        _inventoryGrid.Columns.Add(Text("HPP / Unit", nameof(InventoryDraftRow.UnitCost), 165, rightAligned: true));

        _partnerGrid.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "Jenis",
            ItemsSource = new[] { "SUPPLIER", "CUSTOMER" },
            SelectedItemBinding = new Binding(nameof(PartnerDraftRow.PartnerType)),
            Width = 125
        });
        _partnerGrid.Columns.Add(Text("Kode Partner", nameof(PartnerDraftRow.PartnerCode), 140));
        _partnerGrid.Columns.Add(Text("No. Dokumen", nameof(PartnerDraftRow.DocumentNumber), 155));
        _partnerGrid.Columns.Add(Text("Jatuh Tempo", nameof(PartnerDraftRow.DueDate), 135));
        _partnerGrid.Columns.Add(Text("Saldo", nameof(PartnerDraftRow.Amount), 155, rightAligned: true));
        var partnerNotes = Text("Catatan", nameof(PartnerDraftRow.Notes), new DataGridLength(1, DataGridLengthUnitType.Star));
        partnerNotes.MinWidth = 240;
        _partnerGrid.Columns.Add(partnerNotes);

        _ledgerGrid.Columns.Add(Text("Kode Perkiraan", nameof(LedgerDraftRow.AccountCode), 160));
        var ledgerDescription = Text("Keterangan", nameof(LedgerDraftRow.Description), new DataGridLength(1, DataGridLengthUnitType.Star));
        ledgerDescription.MinWidth = 400;
        _ledgerGrid.Columns.Add(ledgerDescription);
        _ledgerGrid.Columns.Add(Text("Debit", nameof(LedgerDraftRow.Debit), 155, rightAligned: true));
        _ledgerGrid.Columns.Add(Text("Kredit", nameof(LedgerDraftRow.Credit), 155, rightAligned: true));
    }

    private void ConfigureAccountGrid()
    {
        _accountGrid.RowHeight = 28;
        _accountGrid.ColumnHeaderHeight = 31;
        _accountGrid.SelectionMode = DataGridSelectionMode.Single;
        _accountGrid.Columns.Add(Text("Kode", nameof(LedgerAccountDto.Code), 135));
        var name = Text("Nama Perkiraan", nameof(LedgerAccountDto.Name), new DataGridLength(1, DataGridLengthUnitType.Star));
        name.MinWidth = 280;
        _accountGrid.Columns.Add(name);
        _accountGrid.Columns.Add(Text("Jenis", nameof(LedgerAccountDto.AccountType), 110));
        _accountGrid.Columns.Add(Text("D/K", nameof(LedgerAccountDto.NormalBalance), 55, centered: true));
        _accountGrid.Columns.Add(Text("Induk", nameof(LedgerAccountDto.ParentCode), 115));
        _accountGrid.Columns.Add(Check("Posting", nameof(LedgerAccountDto.IsPostable), 72));
        _accountGrid.Columns.Add(Check("Kas/Bank", nameof(LedgerAccountDto.IsCashBank), 76));
        _accountGrid.Columns.Add(Check("Aktif", nameof(LedgerAccountDto.IsActive), 60));
    }

    private async Task LoadAsync()
    {
        var retry = false;
        try
        {
            SetBusy(true, "Memuat status Setup Data Awal...");
            var setupTask = _api.GetInitialSetupAsync();
            var configurationTask = _api.GetProgramConfigurationAsync();
            var accountsTask = _api.GetLedgerAccountsAsync(_accountSearch.Text, includeInactive: true);
            await Task.WhenAll(setupTask, configurationTask, accountsTask);
            _current = await setupTask;
            _programConfiguration = await configurationTask;
            ProgramConfigurationCache.Set(_programConfiguration);
            ApplyProgramProfile(_programConfiguration);
            ApplyAccounts(await accountsTask);
            ApplyState(_current);
            StateChanged?.Invoke(_current);
            SetOperationStatus($"Setup Data Awal dimuat. Versi data {_current.Version:N0}.");
        }
        catch (Exception exception)
        {
            SetOperationStatus("Status setup gagal diperiksa. Akses operasional tetap dikunci.", PartnerUi.MaroonBrush);
            var result = MessageBox.Show(
                Window.GetWindow(this),
                $"Status setup tidak dapat diperiksa. Akses normal tetap dikunci.\n\n{exception.Message}\n\nPilih Coba Lagi untuk mengulangi, atau Batal untuk logout.",
                "Setup Data Awal",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.OK);
            retry = result == MessageBoxResult.OK;
            if (!retry) LogoutRequested?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            SetBusy(false);
        }
        if (retry) await LoadAsync();
    }

    private async Task<bool> SaveCompanyProfileAsync()
    {
        if (_programConfiguration is null)
        {
            MessageBox.Show(Window.GetWindow(this), "Profil usaha belum selesai dimuat. Silakan coba lagi.", "Profil Usaha", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        if (string.IsNullOrWhiteSpace(_profileCompany.Text))
        {
            MessageBox.Show(Window.GetWindow(this), "Nama perusahaan wajib diisi.", "Profil Usaha", MessageBoxButton.OK, MessageBoxImage.Information);
            _profileCompany.Focus();
            return false;
        }

        try
        {
            SetBusy(true, "Menyimpan Profil Usaha...");
            var request = CreateProfileSaveRequest(_programConfiguration);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    _programConfiguration = await _api.SaveProgramConfigurationAsync(request);
                    break;
                }
                catch (AksaraApiException exception) when (
                    attempt == 0 &&
                    exception.Code?.Equals("CONFIGURATION_CONFLICT", StringComparison.OrdinalIgnoreCase) == true)
                {
                    SetOperationStatus("Menyinkronkan perubahan Profil Usaha...");
                    var latest = await _api.GetProgramConfigurationAsync();
                    request = CreateProfileSaveRequest(latest);
                }
            }

            ProgramConfigurationCache.Set(_programConfiguration);
            ApplyProgramProfile(_programConfiguration);
            _current = await _api.GetInitialSetupAsync();
            ApplyState(_current);
            StateChanged?.Invoke(_current);
            SetOperationStatus("Profil Usaha tersimpan.", SuccessBrush);
            return true;
        }
        catch (Exception exception)
        {
            SetOperationStatus("Profil Usaha gagal disimpan.", PartnerUi.MaroonBrush);
            MessageBox.Show(Window.GetWindow(this), exception.Message, "Profil Usaha", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private SaveProgramConfigurationRequest CreateProfileSaveRequest(ProgramConfigurationDto current) => new(
        _profileCompany.Text.Trim(),
        Empty(_profileOwner.Text),
        Empty(_profileAddress.Text),
        Empty(_profileCity.Text),
        Empty(_profilePhone.Text),
        Empty(_profileEmail.Text),
        Empty(_profileTaxNumber.Text),
        current.TaxEnabled,
        current.DefaultTaxPercent,
        current.TransactionDetailRows,
        current.MinimumPrintRows,
        current.SaleNumberPrefix,
        current.PurchaseNumberPrefix,
        current.SaleReturnNumberPrefix,
        current.PurchaseReturnNumberPrefix,
        current.CashBankNumberPrefix,
        current.CustomerReceiptNumberPrefix,
        current.SupplierPaymentNumberPrefix,
        current.CheckGiroNumberPrefix,
        current.ShowPrintDateTime,
        current.ShowItemCode,
        current.ShowPrintedBy,
        current.PrintHeaderMode,
        current.SaleTitle,
        current.PurchaseTitle,
        current.SaleReturnTitle,
        current.PurchaseReturnTitle,
        current.DeliveryTitle,
        current.CashBankTitle,
        current.Signature1,
        current.Signature2,
        current.Signature3,
        current.PrintFooterNote,
        current.Version,
        _api.CurrentUserName,
        Environment.MachineName);

    private void ApplyProgramProfile(ProgramConfigurationDto current)
    {
        _profileCompany.Text = current.CompanyName;
        _profileOwner.Text = current.OwnerName;
        _profileAddress.Text = current.Address;
        _profileCity.Text = current.City;
        _profilePhone.Text = current.Phone;
        _profileEmail.Text = current.Email;
        _profileTaxNumber.Text = current.TaxNumber;
    }

    private void ApplyAccounts(IReadOnlyList<LedgerAccountDto> accounts)
    {
        var rows = accounts.OrderBy(account => account.Code, StringComparer.OrdinalIgnoreCase).ToArray();
        _accountGrid.ItemsSource = rows;
        _accountStatus.Text = $"{rows.Length:N0} akun perkiraan  |  Klik ganda untuk mengubah akun.";
    }

    private async Task ReloadAccountsAsync()
    {
        try
        {
            SetBusy(true, "Memuat daftar perkiraan...");
            ApplyAccounts(await _api.GetLedgerAccountsAsync(_accountSearch.Text, includeInactive: true));
            SetOperationStatus("Daftar perkiraan diperbarui.", SuccessBrush);
        }
        catch (Exception exception)
        {
            SetOperationStatus("Daftar perkiraan gagal dimuat.", PartnerUi.MaroonBrush);
            ViewError.ShowWarningUnlessDatabaseUnavailable(exception, "AKSARA - Daftar Perkiraan");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task EditAccountAsync()
    {
        if (_accountGrid.SelectedItem is LedgerAccountDto account)
            await OpenAccountEditorAsync(account);
    }

    private async Task OpenAccountEditorAsync(LedgerAccountDto? account)
    {
        var editor = new LedgerAccountEditWindow(account) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() == true) await ReloadAccountsAsync();
    }

    private async Task ImportAccountsAsync()
    {
        var picker = new OpenFileDialog
        {
            Title = "Impor COA dari Excel",
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;

        var imported = 0;
        try
        {
            SetBusy(true, "Membaca dan mengimpor COA...");
            var rows = LedgerAccountExcel.Read(picker.FileName);
            var existing = (await _api.GetLedgerAccountsAsync(null, includeInactive: true))
                .ToDictionary(account => account.Code, StringComparer.OrdinalIgnoreCase);
            var availableCodes = existing.Keys.Concat(rows.Select(row => row.Code)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingParents = rows
                .Where(row => !string.IsNullOrWhiteSpace(row.ParentCode) && !availableCodes.Contains(row.ParentCode))
                .Select(row => $"{row.Code} -> {row.ParentCode}")
                .ToArray();
            if (missingParents.Length > 0)
                throw new InvalidOperationException($"Kode induk berikut tidak ditemukan: {string.Join(", ", missingParents)}.");

            var pending = rows.ToList();
            while (pending.Count > 0)
            {
                var ready = pending
                    .Where(row => string.IsNullOrWhiteSpace(row.ParentCode) || existing.ContainsKey(row.ParentCode))
                    .ToArray();
                if (ready.Length == 0)
                    throw new InvalidOperationException("Hubungan kode induk pada template membentuk siklus dan tidak dapat diimpor.");

                foreach (var row in ready)
                {
                    existing.TryGetValue(row.Code, out var current);
                    var saved = await _api.SaveLedgerAccountAsync(new SaveLedgerAccountRequest(
                        row.Code,
                        row.Name,
                        row.AccountType,
                        row.NormalBalance,
                        row.ParentCode,
                        row.IsPostable,
                        row.IsCashBank,
                        row.IsActive,
                        current?.Version,
                        _api.CurrentUserName,
                        Environment.MachineName));
                    existing[saved.Code] = saved;
                    pending.Remove(row);
                    imported++;
                    _accountStatus.Text = $"Mengimpor {imported:N0} dari {rows.Count:N0} akun...";
                }
            }

            ApplyAccounts(existing.Values.ToArray());
            SetOperationStatus($"Impor COA selesai: {imported:N0} akun.", SuccessBrush);
            MessageBox.Show(
                Window.GetWindow(this),
                $"Impor COA selesai.\n\n{imported:N0} akun berhasil ditambahkan atau diperbarui.",
                "Impor COA",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            SetOperationStatus("Impor COA gagal.", PartnerUi.MaroonBrush);
            var prefix = imported > 0 ? $"{imported:N0} akun sudah berhasil diproses sebelum impor berhenti.\n\n" : string.Empty;
            MessageBox.Show(Window.GetWindow(this), prefix + exception.Message, "Impor COA", MessageBoxButton.OK, MessageBoxImage.Warning);
            await ReloadAccountsAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ApplyState(InitialSetupDto current)
    {
        _cutoff.SelectedDate = current.CutoffDate?.ToDateTime(TimeOnly.MinValue);
        _start.SelectedDate = current.StartDate?.ToDateTime(TimeOnly.MinValue);

        var serverStages = current.Stages
            .Where(IsVisibleStage)
            .Select((stage, index) => new StageDisplayRow(
                index + 1,
                stage.Code,
                stage.Title,
                NormalizeStageStatus(stage.Status),
                stage.Detail,
                stage.Route))
            .ToArray();
        var combinedProfile = serverStages.FirstOrDefault(stage => stage.Code.Equals("PROFILE", StringComparison.OrdinalIgnoreCase));
        var activeSetup = current.Status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase);
        var profileReady = activeSetup || IsComplete(combinedProfile) || IsCompanyProfileReadyForWizard(current.Company.CompanyName);
        var datesReady = activeSetup || IsComplete(combinedProfile);
        var splitStages = new List<StageDisplayRow>
        {
            new(
                1,
                "COMPANY_PROFILE",
                "Profil usaha",
                activeSetup ? "Selesai" : profileReady ? "Valid" : "Perlu dilengkapi",
                profileReady
                    ? $"Profil {current.Company.CompanyName} telah disimpan."
                    : "Lengkapi identitas usaha pada formulir, lalu pilih Simpan & Lanjut.",
                null),
            new(
                2,
                "OPENING_DATES",
                "Tanggal pembukuan",
                activeSetup ? "Selesai" : datesReady ? "Valid" : current.StartDate is null ? "Belum mulai" : "Perlu dilengkapi",
                datesReady
                    ? $"Mulai {current.StartDate:dd/MM/yyyy}; cutoff {current.CutoffDate:dd/MM/yyyy}."
                    : "Tentukan cutoff saldo awal dan tanggal mulai transaksi.",
                null)
        };
        splitStages.AddRange(serverStages
            .Where(stage => !stage.Code.Equals("PROFILE", StringComparison.OrdinalIgnoreCase))
            .Select((stage, index) => stage with { Number = index + 3 }));
        _visibleStages = splitStages.ToArray();
        var ready = _visibleStages.Count(IsComplete);
        _stageSummary.Text = $"{ready:N0} dari {_visibleStages.Length:N0} tahap siap";

        _inventory.Clear();
        foreach (var row in current.Inventory)
            _inventory.Add(new InventoryDraftRow { WarehouseCode = row.WarehouseCode, ItemCode = row.ItemCode, Quantity = row.Quantity, UnitCost = row.UnitCost });
        _partners.Clear();
        foreach (var row in current.Partners)
            _partners.Add(new PartnerDraftRow
            {
                PartnerType = row.PartnerType,
                PartnerCode = row.PartnerCode,
                DocumentNumber = row.DocumentNumber,
                DueDate = row.DueDate?.ToString("yyyy-MM-dd") ?? string.Empty,
                Amount = row.Amount,
                Notes = row.Notes ?? string.Empty
            });
        _ledger.Clear();
        foreach (var row in current.Ledger)
            _ledger.Add(new LedgerDraftRow { AccountCode = row.AccountCode, Description = row.Description ?? string.Empty, Debit = row.Debit, Credit = row.Credit });
        _openingSummary.Text = $"{_inventory.Count:N0} stok  |  {_partners.Count:N0} utang/piutang  |  {_ledger.Count:N0} akun jurnal";

        var noErrors = current.Errors.Count == 0;
        _errors.ItemsSource = noErrors ? new[] { "Tidak ada masalah preflight." } : current.Errors;
        _errors.Foreground = noErrors ? SuccessBrush : PartnerUi.MaroonDarkBrush;
        _preflightSummary.Text = current.Status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase)
            ? "Selesai"
            : noErrors ? "Valid" : $"Perlu dilengkapi ({current.Errors.Count:N0})";
        _preflightSummary.Foreground = noErrors ? SuccessBrush : WarningBrush;

        var active = current.Status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase);
        var setupRequired = InitialSetupFlowPolicy.RequiresSetup(current);
        var status = current.LegacyProtected
            ? "DATA LAMA DILINDUNGI"
            : active ? "AKTIF" : "WAJIB DISELESAIKAN";
        ApplyBadge(_setupBadge, _setupBadgeText, status, active || current.LegacyProtected);

        var activation = _visibleStages.FirstOrDefault(x => x.Code.Equals("ACTIVATE", StringComparison.OrdinalIgnoreCase));
        _activationSummary.Text = current.LegacyProtected
            ? "Setup tidak dapat diubah karena data perusahaan lama dilindungi."
            : activation?.Detail ?? "Aktivasi tersedia setelah seluruh preflight valid.";

        var canManage = _api.Can("CONFIGURATION", "MANAGE");
        var canEdit = current.Status.Equals("DRAFT", StringComparison.OrdinalIgnoreCase) && !current.LegacyProtected && canManage;
        _save.IsEnabled = canEdit;
        _activate.IsEnabled = canEdit && noErrors;
        _reopen.IsEnabled = current.Status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) && !current.LegacyProtected && canManage;
        _start.IsEnabled = canEdit;
        _cutoff.IsEnabled = canEdit;
        foreach (var input in ProfileInputs()) input.IsEnabled = canEdit;
        _inventoryGrid.IsReadOnly = !canEdit;
        _partnerGrid.IsReadOnly = !canEdit;
        _ledgerGrid.IsReadOnly = !canEdit;
        foreach (var button in _draftEditButtons) button.IsEnabled = canEdit;
        foreach (var button in _accountEditButtons) button.IsEnabled = canEdit;

        _close.ToolTip = setupRequired
            ? "Kembali ke setup atau logout dari AKSARA"
            : "Tutup Setup Data Awal";
        SetOperationStatus(setupRequired
            ? "Mode wajib: selesaikan Setup Data Awal untuk membuka transaksi. Tombol Tutup menawarkan logout."
            : current.LegacyProtected
                ? "Data perusahaan lama dilindungi. Setup tersedia dalam mode tinjau."
                : "Setup Data Awal aktif. Menu operasional tersedia sesuai hak akses.",
            setupRequired ? PartnerUi.MaroonDarkBrush : SuccessBrush);
        ScheduleInitialFocus(_visibleStages, setupRequired);
        RenderWizard();
    }

    private async Task<bool> SaveAsync(bool showConfirmation = true)
    {
        if (_current is null) return false;
        if (_start.SelectedDate is not DateTime start || _cutoff.SelectedDate is not DateTime cutoff)
        {
            MessageBox.Show(Window.GetWindow(this), "Tanggal cutoff saldo awal dan tanggal mulai transaksi wajib diisi.", "Setup Data Awal", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        Commit(_inventoryGrid);
        Commit(_partnerGrid);
        Commit(_ledgerGrid);
        try
        {
            SetBusy(true, "Menyimpan draft Setup Data Awal...");
            var partners = new List<InitialSetupPartnerDraftDto>();
            foreach (var row in _partners)
            {
                DateOnly? dueDate = null;
                if (!string.IsNullOrWhiteSpace(row.DueDate))
                {
                    if (!DateOnly.TryParse(row.DueDate, out var parsed))
                        throw new InvalidOperationException($"Tanggal jatuh tempo {row.DueDate} tidak valid. Gunakan yyyy-MM-dd.");
                    dueDate = parsed;
                }
                partners.Add(new(null, row.PartnerType, row.PartnerCode, string.Empty, row.DocumentNumber, dueDate, row.Amount, row.Notes));
            }

            var request = new SaveInitialSetupRequest(
                DateOnly.FromDateTime(start),
                DateOnly.FromDateTime(cutoff),
                _current.Version,
                _api.CurrentUserName,
                Environment.MachineName,
                _inventory.Select(x => new InitialSetupInventoryDraftDto(null, x.WarehouseCode, x.ItemCode, string.Empty, string.Empty, x.Quantity, x.UnitCost)).ToArray(),
                partners,
                _ledger.Select(x => new InitialSetupLedgerDraftDto(null, x.AccountCode, string.Empty, x.Description, x.Debit, x.Credit)).ToArray());
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    _current = await _api.SaveInitialSetupAsync(request);
                    break;
                }
                catch (AksaraApiException exception) when (
                    attempt == 0 &&
                    exception.Code?.Equals("INITIAL_SETUP_CONFLICT", StringComparison.OrdinalIgnoreCase) == true)
                {
                    SetOperationStatus("Menyinkronkan perubahan Profil Perusahaan...");
                    var latest = await _api.GetInitialSetupAsync();
                    request = request with { ExpectedVersion = latest.Version };
                }
            }
            ApplyState(_current);
            StateChanged?.Invoke(_current);
            SetOperationStatus("Draft Setup Data Awal tersimpan.", SuccessBrush);
            if (showConfirmation)
                MessageBox.Show(Window.GetWindow(this), "Draft Setup Data Awal tersimpan. Draft belum masuk laporan resmi.", "Setup Data Awal", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        catch (Exception exception)
        {
            SetOperationStatus("Draft Setup Data Awal gagal disimpan.", PartnerUi.MaroonBrush);
            MessageBox.Show(Window.GetWindow(this), exception.Message, "Setup Data Awal", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ActivateAsync()
    {
        if (_current is null) return;
        if (MessageBox.Show(
                Window.GetWindow(this),
                "Aktivasi akan memposting seluruh saldo awal secara atomik. Lanjutkan?",
                "Aktivasi Setup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            SetBusy(true, "Mengaktifkan dan memposting saldo awal...");
            _current = await _api.ActivateInitialSetupAsync(new(_current.Version, _api.CurrentUserName, Environment.MachineName));
            ApplyState(_current);
            SetOperationStatus("Setup Data Awal berhasil diaktifkan.", SuccessBrush);
            MessageBox.Show(Window.GetWindow(this), "Setup Data Awal berhasil diaktifkan.", "Setup Data Awal", MessageBoxButton.OK, MessageBoxImage.Information);
            StateChanged?.Invoke(_current);
        }
        catch (Exception exception)
        {
            SetOperationStatus("Aktivasi Setup Data Awal gagal.", PartnerUi.MaroonBrush);
            MessageBox.Show(Window.GetWindow(this), exception.Message, "Setup Data Awal", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ReopenAsync()
    {
        if (_current is null) return;
        var reason = PromptReason(Window.GetWindow(this));
        if (string.IsNullOrWhiteSpace(reason)) return;
        try
        {
            SetBusy(true, "Membuka kembali Setup Data Awal...");
            _current = await _api.ReopenInitialSetupAsync(new(_current.Version, reason, _api.CurrentUserName, Environment.MachineName));
            ApplyState(_current);
            StateChanged?.Invoke(_current);
            SetOperationStatus("Setup Data Awal dibuka kembali sebagai draft.", WarningBrush);
        }
        catch (Exception exception)
        {
            SetOperationStatus("Setup Data Awal gagal dibuka kembali.", PartnerUi.MaroonBrush);
            MessageBox.Show(Window.GetWindow(this), exception.Message, "Buka Kembali Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ContinueAsync()
    {
        if (_current is null || _visibleStages.Length == 0) return;
        if (_currentStageIndex >= Math.Min(_visibleStages.Length, _stagePages.Count) - 1) return;

        if (_currentStageIndex == 0)
        {
            if (!await SaveCompanyProfileAsync()) return;
        }
        else if (_currentStageIndex is 1 or 4)
        {
            if (!await SaveAsync(showConfirmation: false)) return;
        }
        else
        {
            await LoadAsync();
        }

        if (_currentStageIndex >= _visibleStages.Length) return;
        var stage = _visibleStages[_currentStageIndex];
        if (!IsComplete(stage))
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"Tahap ini belum siap dilanjutkan.\n\n{stage.Detail}",
                "Lengkapi Tahap Setup",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        NavigateToStage(_currentStageIndex + 1);
    }

    private void NavigateToStage(int index)
    {
        if (_visibleStages.Length == 0 || _stagePages.Count == 0) return;
        var maximum = Math.Min(_visibleStages.Length, _stagePages.Count) - 1;
        index = Math.Clamp(index, 0, maximum);
        if (index > HighestUnlockedStage()) return;
        _currentStageIndex = index;
        RenderWizard();
    }

    private int HighestUnlockedStage()
    {
        if (_visibleStages.Length == 0) return 0;
        if (_current?.Status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) == true)
            return Math.Min(_visibleStages.Length, _stagePages.Count) - 1;

        var firstIncomplete = Array.FindIndex(_visibleStages, stage => !IsComplete(stage));
        return firstIncomplete < 0
            ? Math.Min(_visibleStages.Length, _stagePages.Count) - 1
            : Math.Min(firstIncomplete, _stagePages.Count - 1);
    }

    private void RenderWizard()
    {
        _stepRail.Children.Clear();
        if (_visibleStages.Length == 0 || _stagePages.Count == 0)
        {
            _wizardStep.Text = "Memuat tahap setup...";
            _wizardTitle.Text = string.Empty;
            _wizardDetail.Text = string.Empty;
            _wizardContent.Content = null;
            return;
        }

        var count = Math.Min(_visibleStages.Length, _stagePages.Count);
        _currentStageIndex = Math.Clamp(_currentStageIndex, 0, count - 1);
        var highestUnlocked = HighestUnlockedStage();
        for (var index = 0; index < count; index++)
        {
            var stage = _visibleStages[index];
            var selected = index == _currentStageIndex;
            var complete = IsComplete(stage);
            var number = new Border
            {
                Width = 23,
                Height = 23,
                CornerRadius = new CornerRadius(12),
                Background = selected ? PartnerUi.MaroonBrush : complete ? SuccessBrush : PartnerUi.ReadOnlyBrush,
                Child = new TextBlock
                {
                    Text = stage.Number.ToString("N0"),
                    Foreground = selected || complete ? Brushes.White : PartnerUi.MutedBrush,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                }
            };
            var caption = new TextBlock
            {
                Text = stage.Title,
                Foreground = selected ? PartnerUi.MaroonDarkBrush : complete ? SuccessBrush : PartnerUi.MutedBrush,
                FontWeight = selected ? FontWeights.Bold : FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(7, 0, 0, 0)
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(number);
            content.Children.Add(caption);
            var stepIndex = index;
            var button = new Button
            {
                Content = content,
                MinWidth = 118,
                Height = 38,
                Padding = new Thickness(7, 4, 7, 4),
                Margin = new Thickness(0, 0, 4, 0),
                Background = selected ? PartnerUi.GoldSoftBrush : Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                IsEnabled = index <= highestUnlocked,
                ToolTip = $"{stage.Title}: {stage.Status}"
            };
            button.Click += (_, _) => NavigateToStage(stepIndex);
            _stepRail.Children.Add(button);
        }

        var activeStage = _visibleStages[_currentStageIndex];
        _wizardStep.Text = $"TAHAP {activeStage.Number:N0} DARI {count:N0}  |  {activeStage.Status.ToUpperInvariant()}";
        _wizardTitle.Text = activeStage.Title;
        _wizardDetail.Text = activeStage.Detail;
        _wizardContent.Content = _stagePages[_currentStageIndex];

        _previous.IsEnabled = _currentStageIndex > 0;
        _previous.Visibility = _currentStageIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
        _next.Visibility = _currentStageIndex < count - 1 ? Visibility.Visible : Visibility.Collapsed;
        _save.Visibility = _current?.Status.Equals("DRAFT", StringComparison.OrdinalIgnoreCase) == true
            && _currentStageIndex is 1 or 4
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SetBusy(bool busy, string? message = null)
    {
        IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
        _busyIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(message)) SetOperationStatus(message);
    }

    private void SetOperationStatus(string message, Brush? foreground = null)
    {
        _operationStatus.Text = message;
        _operationStatus.Foreground = foreground ?? PartnerUi.MutedBrush;
    }

    private void ScheduleInitialFocus(IReadOnlyList<StageDisplayRow> stages, bool setupRequired)
    {
        if (_initialFocusApplied || stages.Count == 0) return;
        _initialFocusApplied = true;
        var nextIndex = setupRequired
            ? Math.Max(0, stages.ToList().FindIndex(stage => !IsComplete(stage)))
            : Math.Min(stages.Count, _stagePages.Count) - 1;
        _currentStageIndex = Math.Max(0, nextIndex);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_currentStageIndex == 0 && _profileCompany.IsEnabled) _profileCompany.Focus();
            else if (_currentStageIndex == 1 && _cutoff.IsEnabled) _cutoff.Focus();
            else _wizardContent.Focus();
        });
    }

    private void AddRouteButton(Panel panel, string text, string route, double width, string glyph)
    {
        var button = PartnerUi.Button(text, width, PartnerButtonKind.Standard, glyph, $"Buka {text}");
        button.Margin = new Thickness(0, 0, 6, 6);
        button.Click += (_, _) => OpenRouteRequested?.Invoke(this, route);
        panel.Children.Add(button);
    }

    private UIElement WithRowButtons(DataGrid grid, Action add, Action remove)
    {
        var root = new Grid { Margin = new Thickness(4) };
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        root.Children.Add(grid);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var plus = PartnerUi.Button("Tambah", 92, PartnerButtonKind.Standard, "\uE710", "Tambah baris saldo awal");
        var minus = PartnerUi.Button("Hapus", 88, PartnerButtonKind.Danger, "\uE74D", "Hapus baris terpilih");
        plus.Click += (_, _) => add();
        minus.Click += (_, _) => remove();
        _draftEditButtons.Add(plus);
        _draftEditButtons.Add(minus);
        buttons.Children.Add(plus);
        buttons.Children.Add(minus);
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);
        return root;
    }

    private static bool IsVisibleStage(InitialSetupStageDto stage)
    {
        var combined = $"{stage.Code} {stage.Title} {stage.Route}";
        return !combined.Contains("USER", StringComparison.OrdinalIgnoreCase)
            && !combined.Contains("PENGGUNA", StringComparison.OrdinalIgnoreCase)
            && !combined.Contains("ACCESS", StringComparison.OrdinalIgnoreCase)
            && !combined.Contains("HAK AKSES", StringComparison.OrdinalIgnoreCase)
            && !combined.Contains("PERMISSION", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeStageStatus(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "DRAFT" or "BELUM MULAI" or "NOT_STARTED" or "PENDING" => "Belum mulai",
        "PERLU DILENGKAPI" or "INCOMPLETE" or "INVALID" => "Perlu dilengkapi",
        "VALID" or "READY" => "Valid",
        "SELESAI" or "ACTIVE" or "COMPLETED" or "DONE" => "Selesai",
        _ => string.IsNullOrWhiteSpace(value) ? "Belum mulai" : value.Trim()
    };

    private static bool IsComplete(StageDisplayRow? stage) => stage?.Status is "Valid" or "Selesai";

    private static bool IsCompanyProfileReadyForWizard(string? companyName)
    {
        var value = companyName?.Trim();
        return !string.IsNullOrWhiteSpace(value)
            && !value.Contains("PERUSAHAAN BARU", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("BELUM SETUP", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeIsolation(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "DATABASE_PER_COMPANY" or "DATABASE" => "database terpisah",
        "SCHEMA_PER_COMPANY" or "SCHEMA" => "schema terpisah",
        _ => string.IsNullOrWhiteSpace(value) ? "terpisah" : value.Trim()
    };

    private static void ApplyBadge(Border badge, TextBlock text, string caption, bool success)
    {
        text.Text = caption;
        text.Foreground = success ? SuccessBrush : PartnerUi.MaroonDarkBrush;
        badge.Background = success ? SuccessSoftBrush : PartnerUi.GoldSoftBrush;
        badge.BorderBrush = success ? SuccessBrush : PartnerUi.GoldBrush;
        badge.BorderThickness = new Thickness(1);
    }

    private IEnumerable<TextBox> ProfileInputs()
    {
        yield return _profileCompany;
        yield return _profileOwner;
        yield return _profileAddress;
        yield return _profileCity;
        yield return _profilePhone;
        yield return _profileEmail;
        yield return _profileTaxNumber;
    }

    private static TextBox ProfileInput(bool multiline = false) => new()
    {
        Height = multiline ? 68 : 30,
        MinWidth = 260,
        Padding = new Thickness(7, multiline ? 5 : 2, 7, 2),
        AcceptsReturn = multiline,
        TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
        VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Left,
        TextAlignment = TextAlignment.Left,
        BorderBrush = PartnerUi.BorderBrush,
        Background = Brushes.White
    };

    private static void AddProfileField(Grid grid, string label, FrameworkElement input, double height = 42)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) });
        var caption = new TextBlock
        {
            Text = label,
            Foreground = PartnerUi.TextBrush,
            VerticalAlignment = height > 50 ? VerticalAlignment.Top : VerticalAlignment.Center,
            Margin = height > 50 ? new Thickness(0, 7, 18, 0) : new Thickness(0, 0, 18, 0)
        };
        Grid.SetRow(caption, row);
        grid.Children.Add(caption);
        Grid.SetRow(input, row);
        Grid.SetColumn(input, 1);
        input.HorizontalAlignment = HorizontalAlignment.Stretch;
        input.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(input);
    }

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static FrameworkElement BuildDateField(string label, FrameworkElement input)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        var text = new TextBlock
        {
            Text = label,
            Foreground = PartnerUi.TextBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.Children.Add(text);
        Grid.SetColumn(input, 1);
        input.HorizontalAlignment = HorizontalAlignment.Stretch;
        input.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(input);
        return grid;
    }

    private static void Remove<T>(DataGrid grid, ObservableCollection<T> rows)
    {
        if (grid.SelectedItem is T row) rows.Remove(row);
    }

    private static void Commit(DataGrid grid)
    {
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private static TabItem Tab(string header, UIElement content) => new() { Header = header, Content = content };

    private static DatePicker DateInput()
    {
        var picker = new DatePicker
        {
            Width = 190,
            Height = 29,
            HorizontalAlignment = HorizontalAlignment.Left,
            BorderBrush = PartnerUi.BorderBrush,
            Language = System.Windows.Markup.XmlLanguage.GetLanguage("id-ID")
        };
        picker.Loaded += (_, _) =>
        {
            if (picker.Template.FindName("PART_TextBox", picker) is not DatePickerTextBox textBox) return;
            textBox.ApplyTemplate();
            if (textBox.Template.FindName("PART_Watermark", textBox) is ContentControl watermark)
                watermark.Content = "Pilih tanggal";
        };
        return picker;
    }

    private static DataGrid SetupGrid(bool readOnly)
    {
        return new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            IsReadOnly = readOnly,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.All,
            HorizontalGridLinesBrush = PartnerUi.GridBrush,
            VerticalGridLinesBrush = PartnerUi.GridBrush,
            ColumnHeaderStyle = PartnerUi.ColumnHeaderStyle(),
            RowHeight = 30,
            ColumnHeaderHeight = 31,
            BorderBrush = PartnerUi.BorderBrush,
            Background = Brushes.White,
            Margin = new Thickness(0)
        };
    }

    private static ListBox ErrorList()
    {
        var list = new ListBox
        {
            MinHeight = 68,
            MaxHeight = 150,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(2)
        };
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 3, 4, 3)));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        list.ItemContainerStyle = itemStyle;
        var template = new DataTemplate();
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding("."));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        template.VisualTree = text;
        list.ItemTemplate = template;
        return list;
    }

    private static DataGridTextColumn Text(
        string header,
        string path,
        double width,
        bool centered = false,
        bool rightAligned = false) =>
        Text(header, path, new DataGridLength(width), centered, rightAligned);

    private static DataGridCheckBoxColumn Check(string header, string path, double width) => new()
    {
        Header = header,
        Binding = new Binding(path),
        Width = width,
        IsReadOnly = true
    };

    private static DataGridTextColumn Text(
        string header,
        string path,
        DataGridLength width,
        bool centered = false,
        bool rightAligned = false)
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(5, 0, 5, 0)));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        if (centered) style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
        if (rightAligned) style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
        return new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = width,
            ElementStyle = style
        };
    }

    private static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }

    private static string? PromptReason(Window? owner)
    {
        var box = new TextBox
        {
            MinWidth = 350,
            MinHeight = 64,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 12),
            Padding = new Thickness(6),
            BorderBrush = PartnerUi.BorderBrush
        };
        var window = new Window
        {
            Owner = owner,
            Title = "Alasan Buka Kembali Setup",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = PartnerUi.CanvasBrush
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = "Alasan buka kembali wajib dicatat:", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(box);
        var ok = PartnerUi.Button("Lanjutkan", 106, PartnerButtonKind.Primary, "\uE72A", "Lanjutkan buka kembali setup");
        ok.HorizontalAlignment = HorizontalAlignment.Right;
        ok.Margin = new Thickness(0);
        ok.Click += (_, _) => window.DialogResult = true;
        panel.Children.Add(ok);
        window.Content = panel;
        return window.ShowDialog() == true ? box.Text.Trim() : null;
    }

    private sealed record StageDisplayRow(int Number, string Code, string Title, string Status, string Detail, string? Route);

    private sealed class InventoryDraftRow
    {
        public string WarehouseCode { get; set; } = string.Empty;
        public string ItemCode { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal UnitCost { get; set; }
    }

    private sealed class PartnerDraftRow
    {
        public string PartnerType { get; set; } = "SUPPLIER";
        public string PartnerCode { get; set; } = string.Empty;
        public string DocumentNumber { get; set; } = string.Empty;
        public string DueDate { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Notes { get; set; } = string.Empty;
    }

    private sealed class LedgerDraftRow
    {
        public string AccountCode { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public decimal Debit { get; set; }
        public decimal Credit { get; set; }
    }
}
