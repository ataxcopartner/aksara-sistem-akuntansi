using System.Globalization;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Aksara.Contracts;
using System.Windows.Threading;
using Aksara.Desktop.Controls;
using Aksara.Desktop.Services;
using Aksara.Desktop.Views;

namespace Aksara.Desktop;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _menuCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private static readonly TimeSpan ServerSessionCheckInterval = TimeSpan.FromSeconds(30);
    private readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly CancellationTokenSource _sessionMonitorCancellation = new();
    private readonly List<MdiChild> _children = [];
    private readonly CultureInfo _idCulture = new("id-ID");
    private IReadOnlyList<DashboardPoint> _profitPoints = [];
    private IReadOnlyList<DashboardPoint> _salesPoints = [];
    private IReadOnlyList<DashboardPoint> _salespersonPoints = [];
    private IReadOnlyList<DashboardPoint> _cashFlowPoints = [];
    private IReadOnlyList<DashboardPoint> _cashAvailabilityPoints = [];
    private IReadOnlyList<SalesHeaderReportRowDto> _dashboardSalesRows = [];
    private DateOnly? _dashboardSalesRowsFrom;
    private DateOnly? _dashboardSalesRowsTo;
    private readonly HashSet<string> _dashboardRefreshingCharts = new(StringComparer.OrdinalIgnoreCase);
    private MdiChild? _activeChild;
    private MenuItem? _pinnedTopMenu;
    private bool _dashboardReady;
    private bool _sessionExpiryHandled;
    private DateTimeOffset _lastServerSessionCheckAt = DateTimeOffset.MinValue;
    private int _sessionCheckInProgress;
    private int _setupStatusCheckInProgress;
    private int _zIndex;
    private int _cascadeIndex;
    private readonly InitialSetupDto _startupInitialSetup;
    private bool _setupRequired;

    public MainWindow(InitialSetupDto initialSetup)
    {
        _startupInitialSetup = initialSetup;
        _setupRequired = InitialSetupFlowPolicy.RequiresSetup(initialSetup);
        InitializeComponent();
        ConfigureMainMenuBehavior();
        Title = AppBrand.MainTitle;
        HomeBrandNameText.Text = AppBrand.ProductName;
        HomeEnvironmentBadgeText.Text = AppBrand.EnvironmentLabel;
        HomeEnvironmentBadgeText.Visibility = string.IsNullOrWhiteSpace(AppBrand.EnvironmentLabel)
            ? Visibility.Collapsed
            : Visibility.Visible;
        ConnectionConfigurationMenu.Visibility = AppBrand.IsServerAddressLocked ? Visibility.Collapsed : Visibility.Visible;
        UpdateApplicationMenu.Visibility = AppBrand.UpdatesEnabled ? Visibility.Visible : Visibility.Collapsed;
        CheckApplicationVersionMenu.Visibility = AppBrand.UpdatesEnabled ? Visibility.Visible : Visibility.Collapsed;
        MaintenanceUpdateSeparator.Visibility = AppBrand.UpdatesEnabled ? Visibility.Visible : Visibility.Collapsed;
        UserText.Text = $"Pengguna: {AksaraApiClient.Shared.CurrentDisplayName}";
        ApplyMenuPermissions();
        _clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("dd MMM yyyy  HH:mm:ss");
        _menuCloseTimer.Tick += MenuCloseTimer_Tick;
        _sessionTimer.Tick += SessionTimer_Tick;
        AksaraApiClient.Shared.SessionExpired += ApiClient_SessionExpired;
        AksaraApiClient.Shared.InitialSetupRequired += ApiClient_InitialSetupRequired;
        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
        _clock.Start();
        _sessionTimer.Start();
        ClockText.Text = DateTime.Now.ToString("dd MMM yyyy  HH:mm:ss");
        Loaded += MainWindow_Loaded;
    }

    private async void SessionTimer_Tick(object? sender, EventArgs e) =>
        await ValidateCurrentSessionAsync();

    private async void MainWindow_Activated(object? sender, EventArgs e) =>
        await ValidateCurrentSessionAsync(forceServerCheck: true);

    private async Task ValidateCurrentSessionAsync(bool forceServerCheck = false)
    {
        if (AksaraApiClient.Shared.CheckSessionExpiration()) return;
        if (!forceServerCheck && DateTimeOffset.UtcNow - _lastServerSessionCheckAt < ServerSessionCheckInterval) return;
        if (_sessionExpiryHandled || Interlocked.Exchange(ref _sessionCheckInProgress, 1) != 0) return;

        try
        {
            if (AksaraApiClient.Shared.CheckSessionExpiration()) return;
            if (!forceServerCheck && DateTimeOffset.UtcNow - _lastServerSessionCheckAt < ServerSessionCheckInterval) return;

            _lastServerSessionCheckAt = DateTimeOffset.UtcNow;
            await AksaraApiClient.Shared.ValidateCurrentSessionAsync(_sessionMonitorCancellation.Token);
        }
        catch (OperationCanceledException) when (_sessionMonitorCancellation.IsCancellationRequested)
        {
        }
        catch (HttpRequestException)
        {
            // Server yang sedang tidak dapat dijangkau bukan berarti sesi pengguna berakhir.
        }
        catch (TaskCanceledException)
        {
            // Timeout heartbeat tidak boleh mengganggu pekerjaan pengguna.
        }
        catch (AksaraApiException exception) when (exception.StatusCode != System.Net.HttpStatusCode.Unauthorized)
        {
            // Gangguan server selain 401 ditangani oleh permintaan normal berikutnya.
        }
        finally
        {
            Interlocked.Exchange(ref _sessionCheckInProgress, 0);
        }
    }

    private void ApiClient_SessionExpired(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => HandleSessionExpired());
            return;
        }

        HandleSessionExpired();
    }

    private void HandleSessionExpired()
    {
        if (_sessionExpiryHandled) return;
        _sessionExpiryHandled = true;
        _sessionTimer.Stop();

        MessageBox.Show(
            this,
            "Sesi telah berakhir.\n\nSilakan buka kembali AKSARA dan login ulang.",
            "AKSARA - Sesi Berakhir",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        Application.Current.Shutdown();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _sessionTimer.Stop();
        _sessionMonitorCancellation.Cancel();
        AksaraApiClient.Shared.SessionExpired -= ApiClient_SessionExpired;
        AksaraApiClient.Shared.InitialSetupRequired -= ApiClient_InitialSetupRequired;
    }

    private void ApiClient_InitialSetupRequired(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(async () => await EnforceSetupFromServerAsync());
            return;
        }
        _ = EnforceSetupFromServerAsync();
    }

    private async Task EnforceSetupFromServerAsync()
    {
        if (Interlocked.Exchange(ref _setupStatusCheckInProgress, 1) != 0) return;
        try
        {
            var state = await AksaraApiClient.Shared.GetInitialSetupAsync();
            if (!InitialSetupFlowPolicy.RequiresSetup(state))
            {
                var wasRequired = _setupRequired;
                _setupRequired = false;
                ApplyMenuPermissions();
                if (wasRequired)
                {
                    ShowHome();
                    if (_dashboardReady) await RefreshDashboardAsync();
                    else await InitializeDashboardAsync();
                }
                return;
            }
            _setupRequired = true;
            ApplyMenuPermissions();
            foreach (var child in _children.ToArray()) CloseChildNow(child);
            ShowInitialSetup(state);
            HomeBackdrop.Visibility = Visibility.Collapsed;
            StatusText.Text = "Setup Data Awal dibuka kembali. Operasi normal dikunci.";
        }
        catch (Exception exception)
        {
            _setupRequired = true;
            ApplyMenuPermissions();
            foreach (var child in _children.ToArray()) CloseChildNow(child);
            var retry = MessageBox.Show(
                this,
                $"Status Setup Data Awal tidak dapat diperiksa. Akses normal tetap dikunci.\n\n{exception.Message}\n\nPilih Coba Lagi untuk mengulangi, atau Batal untuk logout.",
                "AKSARA - Pemeriksaan Setup",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.OK);
            if (retry == MessageBoxResult.OK)
                _ = Dispatcher.BeginInvoke(async () => await EnforceSetupFromServerAsync());
            else
                LogoutAndExit();
        }
        finally
        {
            Interlocked.Exchange(ref _setupStatusCheckInProgress, 0);
        }
    }

    private void ConfigureMainMenuBehavior()
    {
        MainMenu.MouseLeave += MainMenu_MouseLeave;
        MouseMove += MainWindow_MouseMoveForMenu;
        foreach (var item in MainMenu.Items.OfType<MenuItem>())
        {
            item.MouseEnter += TopMenu_MouseEnter;
            item.MouseLeave += TopMenu_MouseLeave;
            item.PreviewMouseLeftButtonDown += TopMenu_PreviewMouseLeftButtonDown;
            item.SubmenuClosed += TopMenu_SubmenuClosed;
            AttachDropdownHoverHandlers(item);
        }
    }

    private void AttachDropdownHoverHandlers(MenuItem parent)
    {
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            item.MouseEnter += DropdownMenu_MouseEnter;
            item.MouseLeave += DropdownMenu_MouseLeave;
            AttachDropdownHoverHandlers(item);
        }
    }

    private void TopMenu_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not MenuItem { HasItems: true } item) return;
        _menuCloseTimer.Stop();
        if (_pinnedTopMenu is not null && _pinnedTopMenu != item) _pinnedTopMenu.IsSubmenuOpen = false;
        item.IsSubmenuOpen = true;
    }

    private void TopMenu_MouseLeave(object sender, MouseEventArgs e)
    {
        StartMenuCloseTimer();
    }

    private void DropdownMenu_MouseEnter(object sender, MouseEventArgs e)
    {
        _menuCloseTimer.Stop();
    }

    private void DropdownMenu_MouseLeave(object sender, MouseEventArgs e)
    {
        StartMenuCloseTimer();
    }

    private void TopMenu_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not MenuItem { HasItems: true } item) return;
        _menuCloseTimer.Stop();
        if (_pinnedTopMenu is not null && _pinnedTopMenu != item) _pinnedTopMenu.IsSubmenuOpen = false;

        _pinnedTopMenu = item;
        item.IsSubmenuOpen = true;
        e.Handled = true;
    }

    private void TopMenu_SubmenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender == _pinnedTopMenu) _pinnedTopMenu = null;
    }

    private void MainMenu_MouseLeave(object sender, MouseEventArgs e)
    {
        StartMenuCloseTimer();
    }

    private void MainWindow_MouseMoveForMenu(object sender, MouseEventArgs e)
    {
        if (!MainMenu.Items.OfType<MenuItem>().Any(item => item.IsSubmenuOpen)) return;
        if (IsPointerOverMainMenuOrDropdown()) _menuCloseTimer.Stop();
        else StartMenuCloseTimer();
    }

    private void MenuCloseTimer_Tick(object? sender, EventArgs e)
    {
        _menuCloseTimer.Stop();
        if (IsPointerOverMainMenuOrDropdown()) return;
        CloseOpenMainMenuDropdowns();
    }

    private void StartMenuCloseTimer()
    {
        if (!_menuCloseTimer.IsEnabled) _menuCloseTimer.Start();
    }

    private void CloseOpenMainMenuDropdowns()
    {
        _pinnedTopMenu = null;
        foreach (var item in MainMenu.Items.OfType<MenuItem>())
        {
            item.IsSubmenuOpen = false;
        }
    }

    private bool IsPointerOverMainMenuOrDropdown()
    {
        var position = Mouse.GetPosition(MainMenu);
        if (position.X >= 0
            && position.Y >= 0
            && position.X <= MainMenu.ActualWidth
            && position.Y <= MainMenu.ActualHeight)
        {
            return true;
        }

        foreach (var item in MainMenu.Items.OfType<MenuItem>())
        {
            if (IsMouseOverMenuBranch(item)) return true;
        }

        var current = Mouse.DirectlyOver as DependencyObject;
        while (current is not null)
        {
            if (current == MainMenu) return true;
            if (current is MenuItem menuItem && IsMainMenuItem(menuItem)) return true;
            current = GetVisualOrLogicalParent(current);
        }

        return false;
    }

    private static bool IsMouseOverMenuBranch(MenuItem item)
    {
        if (item.IsMouseOver) return true;
        foreach (var child in item.Items.OfType<MenuItem>())
        {
            if (IsMouseOverMenuBranch(child)) return true;
        }

        return false;
    }

    private bool IsMainMenuItem(MenuItem menuItem)
    {
        foreach (var topMenu in MainMenu.Items.OfType<MenuItem>())
        {
            if (topMenu == menuItem || ContainsMenuItem(topMenu, menuItem)) return true;
        }

        return false;
    }

    private static bool ContainsMenuItem(MenuItem parent, MenuItem child)
    {
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            if (item == child || ContainsMenuItem(item, child)) return true;
        }

        return false;
    }

    private static DependencyObject? GetVisualOrLogicalParent(DependencyObject node)
    {
        if (node is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            return VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node);
        }

        return LogicalTreeHelper.GetParent(node);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await ValidateCurrentSessionAsync(forceServerCheck: true);
        if (_sessionExpiryHandled) return;

        if (_setupRequired)
        {
            ShowInitialSetup(_startupInitialSetup);
            HomeBackdrop.Visibility = Visibility.Collapsed;
            StatusText.Text = "Setup Data Awal wajib diselesaikan sebelum menggunakan transaksi.";
            return;
        }

        await ProgramConfigurationCache.RefreshAsync();
        ShowHome();
        await InitializeDashboardAsync();
    }

    private void ModuleButton_Click(object sender, RoutedEventArgs e) => ShowModule((sender as FrameworkElement)?.Tag?.ToString());
    private void ModuleMenu_Click(object sender, RoutedEventArgs e) => ShowModule((sender as FrameworkElement)?.Tag?.ToString());
    private void Home_Click(object sender, RoutedEventArgs e) => ShowHome();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void CloseActive_Click(object sender, RoutedEventArgs e) => CloseActive();
    private void Unavailable_Click(object sender, RoutedEventArgs e) => ShowUnavailable(GetActionName(sender));
    private void ContactWebsite_Click(object sender, RoutedEventArgs e) => OpenExternalTarget("https://andriawantaxconsult.com/");
    private void ContactWhatsapp_Click(object sender, RoutedEventArgs e) => OpenExternalTarget("https://wa.me/6282227573012");
    private void Manualbook_Click(object sender, RoutedEventArgs e) => OpenManualbook();
    private void ProgramConfiguration_Click(object sender, RoutedEventArgs e) => ShowProgramConfiguration();
    private void InitialSetup_Click(object sender, RoutedEventArgs e) => ShowInitialSetup();
    private void DocumentPrintSettings_Click(object sender, RoutedEventArgs e)
        => new DocumentPrintSettingsWindow { Owner = this }.ShowDialog();
    private void UserManagement_Click(object sender, RoutedEventArgs e) => ShowUserManagement();
    private void AccessConfiguration_Click(object sender, RoutedEventArgs e) => ShowAccessConfiguration();
    private void ChangeCredentials_Click(object sender, RoutedEventArgs e) => ShowChangeCredentials();
    private void ConnectionConfiguration_Click(object sender, RoutedEventArgs e) => ShowConnectionConfiguration();
    private void PeriodClose_Click(object sender, RoutedEventArgs e) => ShowPeriodClose();
    private void CheckApplicationVersion_Click(object sender, RoutedEventArgs e) =>
        new AppUpdateWindow(allowInstall: false) { Owner = this }.ShowDialog();
    private async void UpdateApplication_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AppUpdateWindow(allowInstall: true) { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.DownloadedPackagePath)) return;

        foreach (var child in _children.ToArray())
        {
            if (child.Body is IWorkspaceCloseGuard guard && !await guard.ConfirmCloseAsync()) return;
        }

        var confirmation = MessageBox.Show(
            "AKSARA akan ditutup, diperbarui, lalu dibuka kembali. Pastikan semua transaksi sudah disimpan.\n\nLanjutkan pemasangan?",
            "AKSARA - Update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes) return;

        try
        {
            AppUpdateService.BeginInstall(dialog.DownloadedPackagePath);
            Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            MessageBox.Show("Pembaruan belum dapat dipasang.\n\n" + exception.Message, "AKSARA - Update", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private void LedgerAccount_Click(object sender, RoutedEventArgs e) => ShowLedgerAccounts();
    private void JournalEntry_Click(object sender, RoutedEventArgs e) => ShowJournalEntries();
    private void LedgerMapping_Click(object sender, RoutedEventArgs e) => ShowLedgerMapping();
    private void CashBankTransaction_Click(object sender, RoutedEventArgs e) => ShowCashBankTransaction((sender as FrameworkElement)?.Tag?.ToString());
    private void SupplierPayment_Click(object sender, RoutedEventArgs e) => ShowSupplierPayment();
    private void CustomerReceipt_Click(object sender, RoutedEventArgs e) => ShowCustomerReceipt();
    private void SupplierOpeningBalance_Click(object sender, RoutedEventArgs e) => ShowPartnerBalance("SUPPLIER", "OPENING");
    private void SupplierCorrection_Click(object sender, RoutedEventArgs e) => ShowPartnerBalance("SUPPLIER", "CORRECTION");
    private void CustomerOpeningBalance_Click(object sender, RoutedEventArgs e) => ShowPartnerBalance("CUSTOMER", "OPENING");
    private void CustomerCorrection_Click(object sender, RoutedEventArgs e) => ShowPartnerBalance("CUSTOMER", "CORRECTION");
    private void PartnerOutstandingReport_Click(object sender, RoutedEventArgs e) =>
        ShowPartnerOutstandingReport((sender as FrameworkElement)?.Tag?.ToString() ?? "SUPPLIER");
    private void CustomerCheckGiro_Click(object sender, RoutedEventArgs e) => ShowCustomerCheckGiro();
    private void GunggunganSales_Click(object sender, RoutedEventArgs e) => ShowGunggunganSales();
    private void SalesReturn_Click(object sender, RoutedEventArgs e) => ShowSalesReturn();
    private void PurchaseReturn_Click(object sender, RoutedEventArgs e) => ShowPurchaseReturn();
    private void EditCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: not null } element || Keyboard.FocusedElement is not IInputElement target) return;
        var command = element.Tag.ToString() switch
        {
            "Cut" => ApplicationCommands.Cut,
            "Copy" => ApplicationCommands.Copy,
            "Paste" => ApplicationCommands.Paste,
            "SelectAll" => ApplicationCommands.SelectAll,
            _ => null
        };
        if (command is not null && command.CanExecute(null, target)) command.Execute(null, target);
    }
    private void LedgerReport_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: not null } element && Enum.TryParse<LedgerReportKind>(element.Tag.ToString(), out var kind))
        {
            if (kind == LedgerReportKind.AccountList) { ShowLedgerReport(kind, null); return; }
            var title = LedgerReportTitle(kind);
            var filter = new ReportFilterWindow(title, kind is LedgerReportKind.TrialBalance or LedgerReportKind.IncomeStatement or LedgerReportKind.BalanceSheetSkontro or LedgerReportKind.BalanceSheetStaffel) { Owner = this };
            if (filter.ShowDialog() == true) ShowLedgerReport(kind, filter.Selection);
        }
    }
    private void CashBankMaster_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: not null } element && Enum.TryParse<CashBankMasterKind>(element.Tag.ToString(), out var kind)) ShowCashBankMaster(kind);
    }
    private void PurchaseReport_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: not null } element && Enum.TryParse<PurchaseReportKind>(element.Tag.ToString(), out var kind))
        {
            var title = PurchaseReportTitle(kind);
            var filter = new ReportFilterWindow(title) { Owner = this };
            if (filter.ShowDialog() == true && filter.Selection is not null) ShowPurchaseReport(kind, filter.Selection);
        }
    }
    private void SalesReport_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: not null } element && Enum.TryParse<SalesReportKind>(element.Tag.ToString(), out var kind))
        {
            var title = SalesReportTitle(kind);
            var filter = new ReportFilterWindow(title) { Owner = this };
            if (filter.ShowDialog() == true && filter.Selection is not null) ShowSalesReport(kind, filter.Selection);
        }
    }
    private void InventoryMaster_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: not null } element && Enum.TryParse<InventoryMasterKind>(element.Tag.ToString(), out var kind)) ShowInventoryMaster(kind);
    }
    private void InventoryCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: not null } element && Enum.TryParse<InventoryCatalogKind>(element.Tag.ToString(), out var kind)) ShowInventoryCatalog(kind);
    }
    private void InventoryTransaction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: not null } element && Enum.TryParse<InventoryTransactionKind>(element.Tag.ToString(), out var kind)) ShowInventoryTransaction(kind);
    }
    private void InventoryReport_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: not null } element && Enum.TryParse<InventoryReportKind>(element.Tag.ToString(), out var kind)) ShowInventoryReport(kind);
    }
    private void PurchasePriceCheck_Click(object sender, RoutedEventArgs e) =>
        ShowPurchasePriceCheck(string.Equals((sender as FrameworkElement)?.Tag?.ToString(), "Sales", StringComparison.OrdinalIgnoreCase));
    private void BusinessPartner_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: not null } element && Enum.TryParse<BusinessPartnerKind>(element.Tag.ToString(), out var kind)) ShowBusinessPartner(kind);
    }

    private static void OpenExternalTarget(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show("Tautan belum bisa dibuka.\n\n" + exception.Message, "AKSARA", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void OpenManualbook()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            System.IO.Path.Combine(baseDirectory, "manualbook.pdf"),
            System.IO.Path.Combine(baseDirectory, "manualbook.html"),
            System.IO.Path.Combine(baseDirectory, "Manualbook", "index.html")
        };
        var manualbook = candidates.FirstOrDefault(System.IO.File.Exists);
        if (manualbook is not null)
        {
            OpenExternalTarget(manualbook);
            return;
        }

        MessageBox.Show("Manualbook belum tersedia. Menu ini sudah disiapkan untuk file manualbook nanti.", "AKSARA", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowHome()
    {
        var existing = _children.FirstOrDefault(x => x.WindowKey == "home");
        if (existing is not null) CloseChildNow(existing);
        HomeBackdrop.Visibility = Visibility.Visible;
        StatusText.Text = AppBrand.StatusPrefix + " siap | Tampilan awal aktif.";
        if (_dashboardReady) _ = RefreshDashboardAsync();
    }

    private async Task InitializeDashboardAsync()
    {
        if (_dashboardReady) return;

        var today = DateTime.Today;
        var firstDay = new DateTime(today.Year, 1, 1);
        SetDashboardPeriod(ProfitFromPicker, ProfitToPicker, firstDay, today);
        SetDashboardPeriod(SalesFromPicker, SalesToPicker, firstDay, today);
        SetDashboardPeriod(SalespersonFromPicker, SalespersonToPicker, firstDay, today);
        SetDashboardPeriod(CashFlowFromPicker, CashFlowToPicker, firstDay, today);
        SetDashboardPeriod(CashAvailabilityFromPicker, CashAvailabilityToPicker, firstDay, today);
        _dashboardReady = true;
        await RefreshDashboardAsync();
    }

    private static void SetDashboardPeriod(DatePicker fromPicker, DatePicker toPicker, DateTime from, DateTime to)
    {
        fromPicker.SelectedDate = from;
        toPicker.SelectedDate = to;
    }

    private async void DashboardRefresh_Click(object sender, RoutedEventArgs e)
    {
        var key = (sender as FrameworkElement)?.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(key)) await RefreshDashboardAsync();
        else await RefreshDashboardCardAsync(key);
    }

    private void DashboardPeriod_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_dashboardReady) return;
        var key = (sender as FrameworkElement)?.Tag?.ToString();
        if (!string.IsNullOrWhiteSpace(key)) _ = RefreshDashboardCardAsync(key);
    }

    private void DashboardChart_Click(object sender, MouseButtonEventArgs e)
    {
        var key = (sender as FrameworkElement)?.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(key) || !TryGetDashboardPeriod(key, out var from, out var to)) return;

        var filter = new ReportFilterSelection(from, to, null, IncludeZero: false);
        switch (key)
        {
            case "Profit":
                ShowLedgerReport(LedgerReportKind.IncomeStatement, filter);
                break;
            case "Sales":
            case "Salesman":
                ShowSalesReport(SalesReportKind.Header, filter);
                break;
            case "CashFlow":
            case "CashAvailability":
                ShowLedgerReport(LedgerReportKind.GeneralLedger, filter);
                break;
        }
    }

    private async Task RefreshDashboardAsync()
    {
        if (!_dashboardReady) return;

        var succeeded = 0;
        if (await RefreshDashboardCardAsync("Profit")) succeeded++;
        if (await RefreshDashboardCardAsync("Sales")) succeeded++;
        if (await RefreshDashboardCardAsync("Salesman")) succeeded++;
        if (await RefreshDashboardCardAsync("CashFlow")) succeeded++;

        if (succeeded == 4)
        {
            SetDashboardUpdatedText();
            return;
        }

        var failed = 4 - succeeded;
        DashboardHeaderUpdatedText.Text = $"{failed} grafik gagal dimuat";
        DashboardLastUpdatedText.Text = "Sebagian dashboard gagal dimuat. Klik Refresh pada grafik untuk mencoba kembali.";
    }

    private async Task<bool> RefreshDashboardCardAsync(string key)
    {
        if (!_dashboardReady || _dashboardRefreshingCharts.Contains(key)) return false;
        if (!TryGetDashboardPeriod(key, out var from, out var to)) return false;
        if (from > to)
        {
            SetDashboardCardError(key, "Periode awal > akhir");
            DashboardLastUpdatedText.Text = "Periode awal tidak boleh melewati periode akhir.";
            DashboardHeaderUpdatedText.Text = "Periode tidak valid";
            return false;
        }

        _dashboardRefreshingCharts.Add(key);
        SetDashboardCardLoading(key);

        try
        {
            var buckets = BuildDashboardBuckets(from, to);
            switch (key)
            {
                case "Profit":
                    _profitPoints = await LoadProfitSeriesAsync(buckets);
                    ProfitValueText.Text = Money(_profitPoints.Sum(x => x.Value));
                    ProfitInfoText.Text = FormatPeriod(from, to);
                    DrawDashboardChart(key);
                    break;
                case "Sales":
                {
                    var report = await AksaraApiClient.Shared.GetSalesHeaderReportAsync(from, to, null, profit: false);
                    _dashboardSalesRows = report.Rows;
                    _dashboardSalesRowsFrom = from;
                    _dashboardSalesRowsTo = to;
                    _salesPoints = BuildSalesPoints(report.Rows, buckets);
                    SalesValueText.Text = Money(_salesPoints.Sum(x => x.Value));
                    SalesInfoText.Text = FormatPeriod(from, to);
                    DrawDashboardChart(key);
                    break;
                }
                case "Salesman":
                {
                    var salespeopleTask = AksaraApiClient.Shared.GetSalespeopleAsync(null, includeInactive: true);
                    IReadOnlyList<SalesHeaderReportRowDto> salesRows;
                    if (_dashboardSalesRowsFrom == from && _dashboardSalesRowsTo == to)
                    {
                        salesRows = _dashboardSalesRows;
                    }
                    else
                    {
                        var salesReport = await AksaraApiClient.Shared.GetSalesHeaderReportAsync(from, to, null, profit: false);
                        salesRows = salesReport.Rows;
                        _dashboardSalesRows = salesRows;
                        _dashboardSalesRowsFrom = from;
                        _dashboardSalesRowsTo = to;
                    }

                    var salespeople = await salespeopleTask;
                    _salespersonPoints = BuildSalespersonPoints(salesRows, salespeople);
                    SalespersonValueText.Text = Money(_salespersonPoints.Sum(x => x.Value));
                    SalespersonInfoText.Text = FormatPeriod(from, to);
                    DrawDashboardChart(key);
                    break;
                }
                case "CashFlow":
                {
                    var accountsTask = AksaraApiClient.Shared.GetLedgerAccountsAsync(null, includeInactive: true, onlyPostable: true);
                    var journalTask = AksaraApiClient.Shared.GetLedgerJournalReportAsync(from, to, null);
                    await Task.WhenAll(accountsTask, journalTask);
                    var cashAccountCodes = accountsTask.Result
                        .Where(x => x.IsActive && x.IsCashBank)
                        .Select(x => x.Code)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    _cashFlowPoints = BuildLedgerCashFlowPoints(journalTask.Result.Rows, buckets, cashAccountCodes);
                    CashFlowValueText.Text = Money(_cashFlowPoints.Sum(x => x.Value));
                    CashFlowInfoText.Text = FormatPeriod(from, to);
                    DrawDashboardChart(key);
                    break;
                }
                case "CashAvailability":
                {
                    var accounts = await AksaraApiClient.Shared.GetLedgerAccountsAsync(null, includeInactive: true, onlyPostable: true);
                    var cashAccountCodes = accounts
                        .Where(x => x.IsActive && x.IsCashBank)
                        .Select(x => x.Code)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    _cashAvailabilityPoints = cashAccountCodes.Count == 0
                        ? BuildRunningCashPoints(BuildCashFlowPoints(await AksaraApiClient.Shared.GetCashBankTransactionsAsync(null, 5000), buckets))
                        : await LoadCashAvailabilitySeriesAsync(buckets, cashAccountCodes);
                    var endingCash = _cashAvailabilityPoints.LastOrDefault()?.Value ?? 0;
                    CashAvailabilityValueText.Text = Money(endingCash);
                    CashAvailabilityInfoText.Text = FormatPeriod(from, to);
                    DrawDashboardChart(key);
                    break;
                }
            }

            SetDashboardUpdatedText();
            return true;
        }
        catch (Exception ex)
        {
            SetDashboardCardError(key, DashboardCardErrorMessage(ex));
            DashboardLastUpdatedText.Text = $"Gagal memuat dashboard: {ex.Message}";
            DashboardHeaderUpdatedText.Text = "Gagal memuat";
            return false;
        }
        finally
        {
            _dashboardRefreshingCharts.Remove(key);
        }
    }

    private bool TryGetDashboardPeriod(string key, out DateOnly from, out DateOnly to)
    {
        (DatePicker From, DatePicker To)? pickers = key switch
        {
            "Profit" => (ProfitFromPicker, ProfitToPicker),
            "Sales" => (SalesFromPicker, SalesToPicker),
            "Salesman" => (SalespersonFromPicker, SalespersonToPicker),
            "CashFlow" => (CashFlowFromPicker, CashFlowToPicker),
            "CashAvailability" => (CashAvailabilityFromPicker, CashAvailabilityToPicker),
            _ => null
        };

        if (pickers is null)
        {
            from = default;
            to = default;
            return false;
        }

        from = DateOnlyFromPicker(pickers.Value.From);
        to = DateOnlyFromPicker(pickers.Value.To);
        return true;
    }

    private void SetDashboardUpdatedText()
    {
        var text = $"Diperbarui {DateTime.Now:HH:mm:ss}";
        DashboardLastUpdatedText.Text = text;
        DashboardHeaderUpdatedText.Text = text;
    }

    private void SetDashboardCardLoading(string key)
    {
        switch (key)
        {
            case "Profit":
                ProfitValueText.Text = "Memuat...";
                SetChartMessage(ProfitCanvas, "Memuat data");
                break;
            case "Sales":
                SalesValueText.Text = "Memuat...";
                SetChartMessage(SalesCanvas, "Memuat data");
                break;
            case "Salesman":
                SalespersonValueText.Text = "Memuat...";
                SetChartMessage(SalespersonCanvas, "Memuat data");
                break;
            case "CashFlow":
                CashFlowValueText.Text = "Memuat...";
                SetChartMessage(CashFlowCanvas, "Memuat data");
                break;
            case "CashAvailability":
                CashAvailabilityValueText.Text = "Memuat...";
                SetChartMessage(CashAvailabilityCanvas, "Memuat data");
                break;
        }
    }

    private void SetDashboardCardError(string key, string message)
    {
        switch (key)
        {
            case "Profit":
                ProfitValueText.Text = "Rp 0";
                _profitPoints = [];
                SetChartMessage(ProfitCanvas, message);
                break;
            case "Sales":
                SalesValueText.Text = "Rp 0";
                _salesPoints = [];
                SetChartMessage(SalesCanvas, message);
                break;
            case "Salesman":
                SalespersonValueText.Text = "Rp 0";
                _salespersonPoints = [];
                SetChartMessage(SalespersonCanvas, message);
                break;
            case "CashFlow":
                CashFlowValueText.Text = "Rp 0";
                _cashFlowPoints = [];
                SetChartMessage(CashFlowCanvas, message);
                break;
            case "CashAvailability":
                CashAvailabilityValueText.Text = "Rp 0";
                _cashAvailabilityPoints = [];
                SetChartMessage(CashAvailabilityCanvas, message);
                break;
        }
    }

    private void DrawDashboardChart(string key)
    {
        switch (key)
        {
            case "Profit":
                DrawLineChart(ProfitCanvas, _profitPoints, "Laba/Rugi", "#A6192E");
                break;
            case "Sales":
                DrawLineChart(SalesCanvas, _salesPoints, "Penjualan", "#C02A3B");
                break;
            case "Salesman":
                DrawBarChart(SalespersonCanvas, _salespersonPoints, "Penjualan Oleh Salesman", "#D6A22E");
                break;
            case "CashFlow":
                DrawLineChart(CashFlowCanvas, _cashFlowPoints, "Arus Kas", "#8F1724");
                break;
            case "CashAvailability":
                DrawLineChart(CashAvailabilityCanvas, _cashAvailabilityPoints, "Ketersediaan Arus Kas", "#6F1018");
                break;
        }
    }

    private void SetChartMessage(Canvas canvas, string message)
    {
        canvas.Children.Clear();
        AddCenteredChartText(canvas, message);
    }

    private static DateOnly DateOnlyFromPicker(DatePicker picker) =>
        DateOnly.FromDateTime(picker.SelectedDate ?? DateTime.Today);

    private List<DashboardBucket> BuildDashboardBuckets(DateOnly from, DateOnly to)
    {
        var result = new List<DashboardBucket>();
        var days = to.DayNumber - from.DayNumber + 1;

        if (days <= 14)
        {
            for (var day = from; day <= to; day = day.AddDays(1))
            {
                result.Add(new DashboardBucket(day, day, day.ToString("dd MMM", _idCulture)));
            }

            return result;
        }

        if (days <= 90)
        {
            for (var start = from; start <= to;)
            {
                var candidateEnd = start.AddDays(6);
                var end = candidateEnd > to ? to : candidateEnd;
                result.Add(new DashboardBucket(start, end, $"{start:dd MMM}-{end:dd MMM}"));
                start = end.AddDays(1);
            }

            return result;
        }

        for (var month = new DateOnly(from.Year, from.Month, 1); month <= to; month = month.AddMonths(1))
        {
            var monthEnd = month.AddMonths(1).AddDays(-1);
            var start = month < from ? from : month;
            var end = monthEnd > to ? to : monthEnd;
            result.Add(new DashboardBucket(start, end, start.ToString("MMM yy", _idCulture)));
        }

        return result;
    }

    private async Task<IReadOnlyList<DashboardPoint>> LoadProfitSeriesAsync(IReadOnlyList<DashboardBucket> buckets)
    {
        var tasks = buckets.Select(async bucket =>
        {
            var report = await AksaraApiClient.Shared.GetIncomeStatementReportAsync(bucket.Start, bucket.End, null, includeZero: false);
            return new DashboardPoint(bucket.Label, report.Totals.Balance);
        }).ToArray();

        return await Task.WhenAll(tasks);
    }

    private static IReadOnlyList<DashboardPoint> BuildSalesPoints(
        IReadOnlyList<SalesHeaderReportRowDto> rows,
        IReadOnlyList<DashboardBucket> buckets) =>
        buckets
            .Select(bucket => new DashboardPoint(
                bucket.Label,
                rows.Where(x => x.DocumentDate >= bucket.Start && x.DocumentDate <= bucket.End).Sum(x => x.NetSales)))
            .ToList();

    private async Task<IReadOnlyList<DashboardPoint>> LoadCashAvailabilitySeriesAsync(IReadOnlyList<DashboardBucket> buckets, HashSet<string> cashAccountCodes)
    {
        if (buckets.Count == 0) return [];

        var first = buckets[0].Start;
        var tasks = buckets.Select(async bucket =>
        {
            var report = await AksaraApiClient.Shared.GetBalanceSheetReportAsync(first, bucket.End, null, includeZero: false);
            var value = report.Rows
                .Where(x => cashAccountCodes.Contains(x.AccountCode))
                .Sum(x => x.Balance);
            return new DashboardPoint(bucket.Label, value);
        }).ToArray();

        return await Task.WhenAll(tasks);
    }

    private IReadOnlyList<DashboardPoint> BuildSalespersonPoints(IReadOnlyList<SalesHeaderReportRowDto> rows, IReadOnlyList<SalespersonMasterDto> salespeople)
    {
        var salesByCode = rows
            .Where(x => !string.IsNullOrWhiteSpace(x.SalesCode))
            .GroupBy(x => x.SalesCode!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Sum(row => row.NetSales), StringComparer.OrdinalIgnoreCase);

        var points = salespeople
            .Where(x => x.IsActive && !string.IsNullOrWhiteSpace(x.Code))
            .GroupBy(x => x.Code.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var salesperson = group.First();
                var code = salesperson.Code.Trim().ToUpperInvariant();
                var label = !string.IsNullOrWhiteSpace(salesperson.Name)
                    ? $"{code} - {salesperson.Name}"
                    : code;
                return new DashboardPoint(label, salesByCode.GetValueOrDefault(salesperson.Code.Trim()));
            })
            .ToList();

        var knownCodes = salespeople
            .Where(x => !string.IsNullOrWhiteSpace(x.Code))
            .Select(x => x.Code.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        points.AddRange(salesByCode
            .Where(x => !knownCodes.Contains(x.Key))
            .Select(x => new DashboardPoint(x.Key.ToUpperInvariant(), x.Value)));

        return points
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
    }

    private static IReadOnlyList<DashboardPoint> BuildCashFlowPoints(IReadOnlyList<CashBankTransactionDto> rows, IReadOnlyList<DashboardBucket> buckets) =>
        buckets
            .Select(bucket => new DashboardPoint(
                bucket.Label,
                rows.Where(x => x.DocumentDate >= bucket.Start && x.DocumentDate <= bucket.End).Sum(SignedCashAmount)))
            .ToList();

    private static IReadOnlyList<DashboardPoint> BuildLedgerCashFlowPoints(
        IReadOnlyList<LedgerJournalReportRowDto> rows,
        IReadOnlyList<DashboardBucket> buckets,
        HashSet<string> cashAccountCodes) =>
        buckets
            .Select(bucket => new DashboardPoint(
                bucket.Label,
                rows.Where(x => cashAccountCodes.Contains(x.AccountCode) &&
                                x.DocumentDate >= bucket.Start && x.DocumentDate <= bucket.End)
                    .Sum(x => x.Debit - x.Credit)))
            .ToList();

    private static string DashboardCardErrorMessage(Exception exception) => exception switch
    {
        TaskCanceledException => "Server terlalu lama merespons",
        AksaraApiException { StatusCode: System.Net.HttpStatusCode.Forbidden } => "Akses data ditolak",
        AksaraApiException { StatusCode: System.Net.HttpStatusCode.Unauthorized } => "Sesi login berakhir",
        _ => "Gagal memuat"
    };

    private static IReadOnlyList<DashboardPoint> BuildRunningCashPoints(IReadOnlyList<DashboardPoint> cashFlowPoints)
    {
        var running = 0m;
        var result = new List<DashboardPoint>();
        foreach (var point in cashFlowPoints)
        {
            running += point.Value;
            result.Add(point with { Value = running });
        }

        return result;
    }

    private static decimal SignedCashAmount(CashBankTransactionDto row)
    {
        var kind = row.TransactionKind.ToUpperInvariant();
        if (kind.Contains("KELUAR") || kind.Contains("PENGELUARAN") || kind.Contains("PAYMENT") || kind.Contains("OUT")) return -row.Amount;
        return row.Amount;
    }

    private void UpdateDashboardCards(DateOnly from, DateOnly to)
    {
        ProfitValueText.Text = Money(_profitPoints.Sum(x => x.Value));
        ProfitInfoText.Text = $"Laba/rugi {FormatPeriod(from, to)}";

        SalesValueText.Text = Money(_salesPoints.Sum(x => x.Value));
        SalesInfoText.Text = $"Penjualan bersih {FormatPeriod(from, to)}";

        SalespersonValueText.Text = Money(_salespersonPoints.Sum(x => x.Value));
        SalespersonInfoText.Text = _salespersonPoints.Count == 0
            ? "Belum ada penjualan dengan salesman."
            : $"{_salespersonPoints.Count} salesman dengan penjualan.";

        CashFlowValueText.Text = Money(_cashFlowPoints.Sum(x => x.Value));
        CashFlowInfoText.Text = $"Mutasi kas/bank {FormatPeriod(from, to)}";

        var endingCash = _cashAvailabilityPoints.LastOrDefault()?.Value ?? 0;
        CashAvailabilityValueText.Text = Money(endingCash);
        CashAvailabilityInfoText.Text = "Saldo akhir kas/bank periode.";
    }

    private void SetDashboardLoading()
    {
        DashboardLastUpdatedText.Text = "Memuat dashboard...";
        foreach (var text in DashboardValueBlocks()) text.Text = "Memuat...";
        foreach (var text in DashboardInfoBlocks()) text.Text = "Mengambil data dari database...";
    }

    private void SetDashboardError(string message)
    {
        DashboardLastUpdatedText.Text = message;
        _profitPoints = [];
        _salesPoints = [];
        _salespersonPoints = [];
        _cashFlowPoints = [];
        _cashAvailabilityPoints = [];
        foreach (var text in DashboardValueBlocks()) text.Text = "Rp 0";
        foreach (var text in DashboardInfoBlocks()) text.Text = message;
        DrawDashboardCharts();
    }

    private IEnumerable<TextBlock> DashboardValueBlocks()
    {
        yield return ProfitValueText;
        yield return SalesValueText;
        yield return SalespersonValueText;
        yield return CashFlowValueText;
        yield return CashAvailabilityValueText;
    }

    private IEnumerable<TextBlock> DashboardInfoBlocks()
    {
        yield return ProfitInfoText;
        yield return SalesInfoText;
        yield return SalespersonInfoText;
        yield return CashFlowInfoText;
        yield return CashAvailabilityInfoText;
    }

    private string Money(decimal value) => $"Rp {FormatDashboardNumber(value)}";
    private string FormatDashboardNumber(decimal value) => value.ToString("#,0;(#,0);0", _idCulture);
    private string FormatPeriod(DateOnly from, DateOnly to) => $"{from:dd/MM/yyyy} s/d {to:dd/MM/yyyy}";

    private void DashboardChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawDashboardCharts();

    private void DrawDashboardCharts()
    {
        DrawLineChart(ProfitCanvas, _profitPoints, "Laba/Rugi", "#A6192E");
        DrawLineChart(SalesCanvas, _salesPoints, "Penjualan", "#C02A3B");
        DrawBarChart(SalespersonCanvas, _salespersonPoints, "Penjualan Oleh Salesman", "#D6A22E");
        DrawLineChart(CashFlowCanvas, _cashFlowPoints, "Arus Kas", "#8F1724");
        DrawLineChart(CashAvailabilityCanvas, _cashAvailabilityPoints, "Ketersediaan Arus Kas", "#6F1018");
    }

    private void DrawLineChart(Canvas canvas, IReadOnlyList<DashboardPoint> points, string title, string color)
    {
        canvas.Children.Clear();
        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        if (width < 120 || height < 70) return;

        if (points.Count == 0)
        {
            AddCenteredChartText(canvas, "Belum ada data");
            return;
        }

        const double left = 44;
        const double top = 10;
        const double right = 12;
        const double bottom = 22;
        var plotWidth = Math.Max(10, width - left - right);
        var plotHeight = Math.Max(10, height - top - bottom);

        var min = (double)points.Min(x => x.Value);
        var max = (double)points.Max(x => x.Value);
        if (min > 0) min = 0;
        if (max < 0) max = 0;
        if (Math.Abs(max - min) < 0.01)
        {
            max += 1;
            min -= 1;
        }

        for (var i = 0; i <= 4; i++)
        {
            var y = top + plotHeight * i / 4;
            AddChartLine(canvas, left, y, left + plotWidth, y, "#E7D8DB", 0.8);
            var value = max - (max - min) * i / 4;
            AddChartText(canvas, AbbreviateNumber((decimal)value), 0, y - 8, 38, TextAlignment.Right, "#6A7A86", 9);
        }

        var zeroY = ValueToY(0, min, max, top, plotHeight);
        AddChartLine(canvas, left, zeroY, left + plotWidth, zeroY, "#D8C4C8", 1);

        var chartPoints = new PointCollection();
        for (var i = 0; i < points.Count; i++)
        {
            var x = left + (plotWidth * i / Math.Max(1, points.Count - 1));
            var y = ValueToY((double)points[i].Value, min, max, top, plotHeight);
            chartPoints.Add(new Point(x, y));
        }

        var areaPoints = new PointCollection(chartPoints);
        areaPoints.Add(new Point(left + plotWidth, zeroY));
        areaPoints.Add(new Point(left, zeroY));
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        var fillColor = ((SolidColorBrush)brush).Color;
        canvas.Children.Add(new Polygon
        {
            Points = areaPoints,
            Fill = new SolidColorBrush(Color.FromArgb(30, fillColor.R, fillColor.G, fillColor.B)),
            StrokeThickness = 0
        });

        canvas.Children.Add(new Polyline
        {
            Points = chartPoints,
            Stroke = brush,
            StrokeThickness = 2.4,
            StrokeLineJoin = PenLineJoin.Round
        });

        for (var i = 0; i < chartPoints.Count; i++)
        {
            var point = chartPoints[i];
            var hitTarget = new Ellipse
            {
                Width = 22,
                Height = 22,
                Cursor = Cursors.Hand,
                Fill = Brushes.Transparent,
                Stroke = Brushes.Transparent
            };
            SetDashboardToolTip(hitTarget, points[i].Value);
            Canvas.SetLeft(hitTarget, point.X - 11);
            Canvas.SetTop(hitTarget, point.Y - 11);
            canvas.Children.Add(hitTarget);

            var marker = new Ellipse
            {
                Width = 8,
                Height = 8,
                Cursor = Cursors.Hand,
                Fill = Brushes.White,
                Stroke = brush,
                StrokeThickness = 2
            };
            SetDashboardToolTip(marker, points[i].Value);
            Canvas.SetLeft(marker, point.X - 4.5);
            Canvas.SetTop(marker, point.Y - 4.5);
            canvas.Children.Add(marker);
            if (points.Count <= 8 || i == 0 || i == points.Count - 1 || i % 2 == 0)
                AddChartText(canvas, ShortLabel(points[i].Label), point.X - 28, height - 17, 56, TextAlignment.Center, "#5A6872", 9);
        }
    }

    private void DrawBarChart(Canvas canvas, IReadOnlyList<DashboardPoint> points, string title, string color)
    {
        canvas.Children.Clear();
        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        if (width < 120 || height < 70) return;

        if (points.Count == 0)
        {
            AddCenteredChartText(canvas, "Belum ada data");
            return;
        }

        const double left = 44;
        const double top = 10;
        const double right = 12;
        const double bottom = 24;
        var plotWidth = Math.Max(10, width - left - right);
        var plotHeight = Math.Max(10, height - top - bottom);
        var max = Math.Max(1, (double)points.Max(x => x.Value));
        var maxBarWidth = points.Count switch
        {
            1 => 110d,
            2 => 88d,
            3 => 74d,
            4 => 64d,
            _ => 56d
        };
        var barGap = points.Count switch
        {
            1 => 0d,
            2 => 24d,
            3 => 18d,
            4 => 14d,
            _ => 10d
        };
        var barWidth = Math.Min(maxBarWidth, Math.Max(16, (plotWidth - barGap * (points.Count - 1)) / points.Count));
        var totalBarsWidth = points.Count * barWidth + (points.Count - 1) * barGap;
        var startX = left + Math.Max(0, (plotWidth - totalBarsWidth) / 2);

        var plotBackdrop = new Border
        {
            Width = plotWidth,
            Height = plotHeight,
            Background = (Brush)new BrushConverter().ConvertFromString("#FFFBFA")!,
            BorderBrush = (Brush)new BrushConverter().ConvertFromString("#F0E2E5")!,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5)
        };
        Canvas.SetLeft(plotBackdrop, left);
        Canvas.SetTop(plotBackdrop, top);
        canvas.Children.Add(plotBackdrop);

        for (var i = 0; i <= 4; i++)
        {
            var y = top + plotHeight * i / 4;
            AddChartLine(canvas, left + 1, y, left + plotWidth - 1, y, "#E8D8DB", 0.8);
            var value = max - max * i / 4;
            AddChartText(canvas, AbbreviateNumber((decimal)value), 0, y - 8, 38, TextAlignment.Right, "#715D61", 9);
        }

        AddChartLine(canvas, left + 1, top + plotHeight, left + plotWidth - 1, top + plotHeight, "#CBB7BB", 1.1);

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var value = Math.Max(0, (double)point.Value);
            var barHeight = value / max * plotHeight;
            var displayHeight = Math.Max(3, barHeight);
            var x = startX + i * (barWidth + barGap);
            var y = top + plotHeight - displayHeight;
            var barBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop((Color)ColorConverter.ConvertFromString("#F2D06C"), 0),
                    new GradientStop((Color)ColorConverter.ConvertFromString(color), 1)
                }
            };
            var bar = new Border
            {
                Width = barWidth,
                Height = displayHeight,
                Background = barBrush,
                BorderBrush = (Brush)new BrushConverter().ConvertFromString("#C99B2A")!,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5, 5, 2, 2),
                Cursor = Cursors.Hand
            };
            SetDashboardToolTip(bar, point.Value);
            Canvas.SetLeft(bar, x);
            Canvas.SetTop(bar, y);
            canvas.Children.Add(bar);

            var hitTarget = new Rectangle
            {
                Width = barWidth,
                Height = plotHeight,
                Fill = Brushes.Transparent,
                Stroke = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            SetDashboardToolTip(hitTarget, point.Value);
            Canvas.SetLeft(hitTarget, x);
            Canvas.SetTop(hitTarget, top);
            canvas.Children.Add(hitTarget);
            AddChartText(canvas, ShortLabel(point.Label), x - 8, height - 18, barWidth + 16, TextAlignment.Center, "#4F4044", 9);
        }
    }

    private void SetDashboardToolTip(FrameworkElement element, decimal value)
    {
        element.ToolTip = DashboardNumberToolTip(value);
        ToolTipService.SetInitialShowDelay(element, 120);
        ToolTipService.SetBetweenShowDelay(element, 0);
        ToolTipService.SetShowDuration(element, 12000);
    }

    private ToolTip DashboardNumberToolTip(decimal value) => new()
    {
        Content = new TextBlock
        {
            Text = FormatDashboardNumber(value),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(37, 49, 59)),
            Margin = new Thickness(4, 2, 4, 2)
        },
        Background = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.FromRgb(122, 174, 224)),
        BorderThickness = new Thickness(1)
    };

    private static string ShortLabel(string value)
    {
        var dash = value.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) return value[..dash];
        return value.Length > 10 ? value[..10] : value;
    }

    private void AddCenteredChartText(Canvas canvas, string text)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(106, 122, 134)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            Width = Math.Max(80, canvas.ActualWidth)
        };
        Canvas.SetLeft(block, 0);
        Canvas.SetTop(block, Math.Max(8, canvas.ActualHeight / 2 - 9));
        canvas.Children.Add(block);
    }

    private static void AddChartLine(Canvas canvas, double x1, double y1, double x2, double y2, string color, double thickness)
    {
        canvas.Children.Add(new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = (Brush)new BrushConverter().ConvertFromString(color)!,
            StrokeThickness = thickness
        });
    }

    private static void AddChartText(Canvas canvas, string text, double x, double y, double width, TextAlignment alignment, string color, double size)
    {
        var block = new TextBlock
        {
            Text = text,
            Width = width,
            Foreground = (Brush)new BrushConverter().ConvertFromString(color)!,
            FontSize = size,
            TextAlignment = alignment
        };
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        canvas.Children.Add(block);
    }

    private static double ValueToY(double value, double min, double max, double top, double height)
    {
        var ratio = (value - min) / (max - min);
        return top + height - ratio * height;
    }

    private string AbbreviateNumber(decimal value)
    {
        var abs = Math.Abs(value);
        if (abs >= 1_000_000_000) return $"{(value / 1_000_000_000).ToString("0.#", _idCulture)}M";
        if (abs >= 1_000_000) return $"{(value / 1_000_000).ToString("0.#", _idCulture)}Jt";
        if (abs >= 1_000) return $"{(value / 1_000).ToString("0.#", _idCulture)}Rb";
        return value.ToString("0", _idCulture);
    }

    private void ShowModule(string? module)
    {
        if (module == "Supplier")
        {
            ShowBusinessPartner(BusinessPartnerKind.Supplier);
            return;
        }

        if (module == "Customer")
        {
            ShowBusinessPartner(BusinessPartnerKind.Customer);
            return;
        }

        if (module == "Kas Bank")
        {
            ShowCashBankTransaction("Cash");
            return;
        }

        if (module is "Barang" or "Persediaan" or "Master Data")
        {
            if (ActivateExisting("barang")) return;
            var view = new ItemListView();
            var child = CreateChild("barang", "Daftar Barang", view, 1100, 700);
            view.CloseRequested += (_, _) => CloseChild(child);
            return;
        }

        if (module == "Jasa")
        {
            if (ActivateExisting("jasa")) return;
            var view = new ItemListView(serviceMode: true);
            var child = CreateChild("jasa", "Daftar Jasa", view, 1100, 700);
            view.CloseRequested += (_, _) => CloseChild(child);
            return;
        }

        if (module == "Pembelian")
        {
            if (ActivateExisting("pembelian")) return;
            var view = new PurchaseView();
            var child = CreateChild("pembelian", "Pembelian", view, 1125, 810, centered: true);
            view.CloseRequested += (_, _) => CloseChild(child);
            return;
        }

        if (module == "Penjualan")
        {
            if (ActivateExisting("penjualan")) return;
            var view = new SalesView();
            var child = CreateChild("penjualan", "Penjualan", view, 1125, 810, centered: true);
            view.CloseRequested += (_, _) => CloseChild(child);
            view.PurchasePriceCheckRequested += (_, _) => ShowPurchasePriceCheck(true);
            return;
        }

        if (module == "Penjualan Jasa")
        {
            if (ActivateExisting("penjualan-jasa")) return;
            var view = new ServiceSalesView();
            var child = CreateChild("penjualan-jasa", "Penjualan Jasa", view, 1125, 810, centered: true);
            view.CloseRequested += (_, _) => CloseChild(child);
            return;
        }

        ShowUnavailable(module);
    }

    private void ShowLedgerAccounts()
    {
        if (ActivateExisting("ledger-accounts")) return;
        var view = new LedgerAccountView();
        var child = CreateChild("ledger-accounts", "Daftar Perkiraan", view, 1060, 650, preferredLeft: 55, preferredTop: 35);
        view.CloseRequested += (_, _) => CloseChild(child);
    }

    private void ShowJournalEntries()
    {
        if (ActivateExisting("ledger-journals")) return;
        var view = new JournalEntryView();
        var child = CreateChild("ledger-journals", "Jurnal Umum", view, 1040, 650, preferredLeft: 70, preferredTop: 45);
        view.CloseRequested += (_, _) => CloseChild(child);
    }

    private void ShowLedgerMapping()
    {
        if (ActivateExisting("ledger-mapping")) return;
        var view = new LedgerMappingView();
        var child = CreateChild("ledger-mapping", "Penetapan Kode Perkiraan Jurnal Transaksi", view, 930, 560, preferredLeft: 90, preferredTop: 60);
        view.CloseRequested += (_, _) => CloseChild(child);
    }

    private void ShowLedgerReport(LedgerReportKind kind, ReportFilterSelection? filter)
    {
        var key = $"ledger-report:{kind}:{Guid.NewGuid():N}";
        var view = new LedgerReportView(kind, filter);
        var child = CreateChild(key, view.ReportTitle, view, 1080, 650, preferredLeft: 100, preferredTop: 60);
        view.CloseRequested += (_, _) => CloseChild(child);
    }

    private void ShowCashBankMaster(CashBankMasterKind kind)
    {
        var key = $"cash-bank-master:{kind}";
        if (ActivateExisting(key)) return;
        var view = new CashBankMasterView(kind);
        var child = CreateChild(key, view.ViewTitle, view, 960, 610, preferredLeft: 75, preferredTop: 45);
        view.CloseRequested += (_, _) => CloseChild(child);
    }

    private void ShowCashBankTransaction(string? initialMethod = null)
    {
        if (ActivateExisting("cash-bank-transaction")) return;
        var initialTab = initialMethod switch
        {
            "Bank" => 1,
            "Giro" => 2,
            _ => 0
        };
        var view = new CashBankTransactionView(initialTab);
        var child = CreateChild("cash-bank-transaction", "Kas & Bank", view, 1120, 670, centered: true);
        view.CloseRequested += (_, _) => CloseChild(child);
    }

    private void ShowSupplierPayment()
    {
        if (ActivateExisting("supplier-payment")) return;
        var view = new SupplierPaymentView();
        var child = CreateChild("supplier-payment", "Pembayaran Utang", view, 1120, 670, centered: true);
        view.CloseRequested += (_, _) => CloseChild(child);
    }

    private void ShowCustomerReceipt()
    {
        if (ActivateExisting("customer-receipt")) return;
        var view = new CustomerReceiptView();
        var child = CreateChild("customer-receipt", "Penerimaan Piutang", view, 1120, 670, centered: true);
        view.CloseRequested += (_, _) => CloseChild(child);
    }

    private void ShowPartnerBalance(string partnerType, string kind)
    {
        var key = $"partner-balance:{partnerType}:{kind}".ToLowerInvariant();
        if (ActivateExisting(key)) return;
        var view = new PartnerBalanceView(partnerType, kind);
        var title = (partnerType, kind) switch
        {
            ("SUPPLIER", "OPENING") => "Utang Awal",
            ("SUPPLIER", _) => "Koreksi Utang",
            ("CUSTOMER", "OPENING") => "Piutang Awal",
            _ => "Koreksi Piutang"
        };
        var child = CreateChild(key, title, view, 1120, 680, centered: true);
        view.CloseRequested += (_, _) => CloseChild(child);
    }

    private void ShowPartnerOutstandingReport(string partnerType)
    {
        partnerType = string.Equals(partnerType, "CUSTOMER", StringComparison.OrdinalIgnoreCase) ? "CUSTOMER" : "SUPPLIER";
        var key = $"partner-outstanding:{partnerType}".ToLowerInvariant();
        if (ActivateExisting(key)) return;
        var view = new PartnerOutstandingReportView(partnerType);
        var child = CreateChild(key, view.ReportTitle, view, 1160, 670, centered: true);
        view.CloseRequested += (_, _) => CloseChild(child);
    }

    private void ShowCustomerCheckGiro()
    {
        var dialog = new CustomerCheckGiroEditWindow { Owner = this };
        dialog.ShowDialog();
    }

    private void ShowSalesReturn()
    {
        if (ActivateExisting("retur-penjualan")) return;
        var view = new SalesReturnView();
        var child = CreateChild("retur-penjualan", "Retur Penjualan", view, 880, 595, centered: true);
        view.CloseRequested += (_, _) => CloseChild(child);
    }

    private void ShowPurchaseReturn()
    {
        if (ActivateExisting("retur-pembelian")) return;
        var view = new PurchaseReturnView();
        var child = CreateChild("retur-pembelian", "Retur Pembelian", view, 900, 595, centered: true);
        view.CloseRequested += (_, _) => CloseChild(child);
    }

    private void ShowPurchaseReport(PurchaseReportKind kind, ReportFilterSelection filter)
    {
        var key = $"laporan-pembelian:{kind}:{Guid.NewGuid():N}";
        var view = new PurchaseReportView(kind, filter);
        var child = CreateChild(key, view.ReportTitle, view, 1080, 650, preferredLeft: 110, preferredTop: 65);
        view.CloseRequested += (_, _) => CloseChild(child);
    }

    private void ShowSalesReport(SalesReportKind kind, ReportFilterSelection filter)
    {
        var key = $"laporan-penjualan:{kind}:{Guid.NewGuid():N}";
        var view = new SalesReportView(kind, filter);
        var child = CreateChild(key, view.ReportTitle, view, 1080, 650, preferredLeft: 100, preferredTop: 60);
        view.CloseRequested += (_, _) => CloseChild(child);
    }

    private static string SalesReportTitle(SalesReportKind kind) => kind switch
    {
        SalesReportKind.Detail => "Laporan Penjualan Rinci",
        SalesReportKind.Header => "Laporan Penjualan Rinci (Header)",
        SalesReportKind.ReturnDetail => "Laporan Retur Penjualan Rinci",
        SalesReportKind.ReturnHeader => "Laporan Retur Penjualan Header",
        SalesReportKind.ProfitDetail => "Laporan Laba Penjualan Rinci",
        _ => "Laporan Laba Penjualan Header"
    };

    private static string PurchaseReportTitle(PurchaseReportKind kind) => kind switch
    {
        PurchaseReportKind.Detail => "Laporan Pembelian Rinci",
        PurchaseReportKind.Header => "Laporan Pembelian Rinci (Header)",
        PurchaseReportKind.ReturnDetail => "Laporan Retur Pembelian Rinci",
        _ => "Laporan Retur Pembelian Header"
    };

    private static string LedgerReportTitle(LedgerReportKind kind) => kind switch
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

    private void ShowInventoryMaster(InventoryMasterKind kind)
    {
        var key = $"master-barang:{kind}"; if (ActivateExisting(key)) return; var view = new InventoryMasterView(kind); var child = CreateChild(key, view.ViewTitle, view, 920, 590, preferredLeft: 65, preferredTop: 40); view.CloseRequested += (_, _) => CloseChild(child);
    }

    private void ShowBusinessPartner(BusinessPartnerKind kind)
    {
        var key = $"master-partner:{kind}";
        if (ActivateExisting(key)) return;
        var view = new BusinessPartnerView(kind);
        var child = CreateChild(key, view.ViewTitle, view, 1080, 650, preferredLeft: 65, preferredTop: 40);
        view.CloseRequested += (_, _) => CloseChild(child);
    }

    private void ShowInventoryCatalog(InventoryCatalogKind kind)
    {
        var key = $"katalog-barang:{kind}"; if (ActivateExisting(key)) return; var view = new InventoryCatalogView(kind); var child = CreateChild(key, view.ViewTitle, view, 1100, 650, preferredLeft: 75, preferredTop: 45); view.CloseRequested += (_, _) => CloseChild(child);
    }

    private void ShowInventoryTransaction(InventoryTransactionKind kind)
    {
        var key = $"transaksi-barang:{kind}"; if (ActivateExisting(key)) return; var view = new InventoryTransactionView(kind); var child = CreateChild(key, view.ViewTitle, view, 1080, 640, centered: true); view.CloseRequested += (_, _) => CloseChild(child);
    }

    private void ShowInventoryReport(InventoryReportKind kind)
    {
        var key = $"laporan-barang:{kind}"; if (ActivateExisting(key)) return; var view = new InventoryReportView(kind); var child = CreateChild(key, view.ReportTitle, view, 1140, 670, preferredLeft: 95, preferredTop: 55); view.CloseRequested += (_, _) => CloseChild(child);
    }

    private void ShowPurchasePriceCheck(bool useSalesPermission)
    {
        const string key = "laporan:cek-harga-beli";
        if (ActivateExisting(key)) return;
        var view = new PurchasePriceCheckView(useSalesPermission);
        var child = CreateChild(key, "Cek Harga Beli", view, 1180, 690, preferredLeft: 75, preferredTop: 45);
        view.CloseRequested += (_, _) => CloseChild(child);
    }

    private void ShowGunggunganSales()
    {
        if (ActivateExisting("penjualan-gunggungan")) return;
        var view = new GunggunganSalesView();
        var child = CreateChild("penjualan-gunggungan", "Penjualan Gunggungan", view, 1180, 760, centered: true);
        view.CloseRequested += (_, _) => CloseChild(child);
    }

    private void ShowProgramConfiguration(Action? afterSaved = null)
    {
        const string key = "configuration:program";
        if (ActivateExisting(key)) return;
        var view = new ProgramConfigurationView();
        if (afterSaved is not null) view.Saved += (_, _) => afterSaved();
        var child = CreateChild(key, "Konfigurasi Program", view, 900, 650, centered: true);
        view.CloseRequested += (_, _) => CloseChild(child);
    }

    private void ShowInitialSetup(InitialSetupDto? initialState = null)
    {
        const string key = "configuration:initial-setup";
        if (ActivateExisting(key)) return;
        var view = new InitialSetupView(initialState);
        var child = CreateChild(key, "Setup Data Awal", view, 1260, 700, centered: true);
        if (_setupRequired && !child.IsMaximized) child.ToggleMaximize();
        view.CloseRequested += (_, _) => CloseChild(child);
        view.OpenRouteRequested += (_, route) => OpenInitialSetupRoute(route, view);
        view.StateChanged += state => Dispatcher.BeginInvoke(async () => await ApplyInitialSetupStateAsync(state, child));
        view.LogoutRequested += (_, _) => LogoutAndExit();
    }

    private async Task ApplyInitialSetupStateAsync(InitialSetupDto state, MdiChild setupChild)
    {
        var wasRequired = _setupRequired;
        _setupRequired = InitialSetupFlowPolicy.RequiresSetup(state);
        ApplyMenuPermissions();

        if (_setupRequired)
        {
            foreach (var child in _children.Where(child => child != setupChild).ToArray()) CloseChildNow(child);
            HomeBackdrop.Visibility = Visibility.Collapsed;
            StatusText.Text = "Setup Data Awal wajib diselesaikan sebelum menggunakan transaksi.";
            return;
        }

        if (!wasRequired) return;
        CloseChildNow(setupChild);
        await ProgramConfigurationCache.RefreshAsync();
        ShowHome();
        if (_dashboardReady) await RefreshDashboardAsync();
        else await InitializeDashboardAsync();
        StatusText.Text = "Setup Data Awal selesai. Seluruh menu sesuai hak akses sudah dibuka.";
    }

    private static void LogoutAndExit()
    {
        AksaraApiClient.Shared.Logout();
        Application.Current.Shutdown();
    }

    private void OpenInitialSetupRoute(string route, InitialSetupView setupView)
    {
        switch (route)
        {
            case "PROGRAM_CONFIGURATION": ShowProgramConfiguration(() => _ = setupView.RefreshFromServerAsync()); break;
            case "LEDGER_ACCOUNTS": ShowLedgerAccounts(); break;
            case "LEDGER_MAPPING": ShowLedgerMapping(); break;
            case "WAREHOUSES": ShowInventoryMaster(InventoryMasterKind.Warehouses); break;
            case "UNITS": new InventoryUnitLookupWindow { Owner = this }.ShowDialog(); break;
            case "CATEGORIES": ShowInventoryMaster(InventoryMasterKind.Categories); break;
            case "ITEMS": ShowModule("Barang"); break;
            case "SUPPLIERS": ShowBusinessPartner(BusinessPartnerKind.Supplier); break;
            case "CUSTOMERS": ShowBusinessPartner(BusinessPartnerKind.Customer); break;
            case "SALESMEN": ShowBusinessPartner(BusinessPartnerKind.Salesman); break;
            case "CASH": ShowCashBankMaster(CashBankMasterKind.Cash); break;
            case "BANK": ShowCashBankMaster(CashBankMasterKind.Bank); break;
            case "CATEGORIES_CASH": ShowCashBankMaster(CashBankMasterKind.Expense); break;
        }
    }

    private void ShowPeriodClose()
    {
        const string key = "maintenance:period-close";
        if (ActivateExisting(key)) return;
        var view = new PeriodCloseView();
        var child = CreateChild(key, "Tutup Buku", view, 1120, 720, centered: true);
        view.CloseRequested += (_, _) => CloseChild(child);
    }

    private void ShowUserManagement()
    {
        const string key = "configuration:users";
        if (ActivateExisting(key)) return;
        var view = new UserManagementView();
        var child = CreateChild(key, "Manajemen Pengguna", view, 900, 590, centered: true);
        view.CloseRequested += (_, _) => CloseChild(child);
    }

    private void ShowAccessConfiguration()
    {
        const string key = "configuration:access";
        if (ActivateExisting(key)) return;
        var view = new AccessConfigurationView();
        var child = CreateChild(key, "Grup & Hak Akses", view, 900, 610, centered: true);
        view.CloseRequested += (_, _) => CloseChild(child);
    }

    private void ShowChangeCredentials()
    {
        const string key = "configuration:credentials";
        if (ActivateExisting(key)) return;
        var view = new ChangeCredentialsView();
        var child = CreateChild(key, "Ubah Nama / Password", view, 650, 430, centered: true);
        view.CloseRequested += (_, _) => CloseChild(child);
        view.CredentialsChanged += (_, _) => UserText.Text = $"Pengguna: {AksaraApiClient.Shared.CurrentDisplayName}";
    }

    private void ShowConnectionConfiguration()
    {
        const string key = "configuration:connection";
        if (ActivateExisting(key)) return;
        var view = new ConnectionConfigurationView();
        var child = CreateChild(key, "Konfigurasi Koneksi Server", view, 650, 330, centered: true);
        view.CloseRequested += (_, _) => CloseChild(child);
    }

    private void ApplyMenuPermissions()
    {
        MainMenu.Visibility = _setupRequired ? Visibility.Collapsed : Visibility.Visible;
        MainMenuRow.Height = _setupRequired ? new GridLength(0) : new GridLength(30);

        foreach (var item in MainMenu.Items.OfType<MenuItem>())
        {
            item.Visibility = Visibility.Visible;
            item.IsEnabled = true;
        }

        var modules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ledger"] = "LEDGER", ["Kas Bank"] = "CASH_BANK", ["Supplier"] = "SUPPLIER",
            ["Pembelian"] = "PURCHASE", ["Customer"] = "CUSTOMER", ["Penjualan"] = "SALES",
            ["Barang"] = "INVENTORY"
        };
        foreach (var item in MainMenu.Items.OfType<MenuItem>())
        {
            var header = item.Header?.ToString() ?? string.Empty;
            if (!modules.TryGetValue(header, out var module)) continue;
            SetMenuAccess(item, AksaraApiClient.Shared.Can(module, "VIEW"));
        }

        var canViewServiceMaster = AksaraApiClient.Shared.Can("INVENTORY", "VIEW");
        var canViewServiceSales = AksaraApiClient.Shared.Can("SALES", "VIEW");
        SetMenuAccess(ServiceCategoryMenu, canViewServiceMaster);
        SetMenuAccess(ServiceMasterMenu, canViewServiceMaster);
        SetMenuAccess(ServiceSalesMenu, canViewServiceSales);
        SetMenuAccess(ServiceMenu, canViewServiceMaster || canViewServiceSales);

        var canConfigure = AksaraApiClient.Shared.Can("CONFIGURATION", "VIEW");
        SetMenuAccess(InitialSetupMenu, canConfigure);
        SetMenuAccess(ProgramConfigurationMenu, canConfigure);
        SetMenuAccess(DocumentPrintSettingsMenu, canConfigure);

        var roleCode = AksaraApiClient.Shared.CurrentRoleCode;
        var canAdministerAccess = canConfigure &&
            (roleCode.Equals("MASTER", StringComparison.OrdinalIgnoreCase) ||
             roleCode.Equals("ADMIN", StringComparison.OrdinalIgnoreCase));
        SetMenuAccess(UserManagementMenu, canAdministerAccess);
        SetMenuAccess(AccessConfigurationMenu, canAdministerAccess);
        SetMenuAccess(UserAccessMenu, canAdministerAccess);

        var canMaintain = AksaraApiClient.Shared.Can("MAINTENANCE", "VIEW");
        SetMenuAccess(PeriodCloseMenu, canMaintain);
        MaintenanceMenu.Visibility = canMaintain || AppBrand.UpdatesEnabled ? Visibility.Visible : Visibility.Collapsed;

        if (_setupRequired)
        {
            foreach (var item in MainMenu.Items.OfType<MenuItem>())
                item.Visibility = item == FileMenu || item == ConfigurationMenu ? Visibility.Visible : Visibility.Collapsed;
            SetMenuAccess(InitialSetupMenu, canConfigure);
            SetMenuAccess(ProgramConfigurationMenu, canConfigure);
            DocumentPrintSettingsMenu.Visibility = Visibility.Collapsed;
            UserAccessMenu.Visibility = Visibility.Collapsed;
            ChangeCredentialsMenu.Visibility = Visibility.Collapsed;
            ConfigurationConnectionSeparator.Visibility = Visibility.Collapsed;
            ConnectionConfigurationMenu.Visibility = Visibility.Collapsed;
            SidebarPanel.Visibility = Visibility.Collapsed;
            SidebarColumn.Width = new GridLength(0);
        }
        else
        {
            FileMenu.Visibility = Visibility.Visible;
            ConfigurationMenu.Visibility = Visibility.Visible;
            ChangeCredentialsMenu.Visibility = Visibility.Visible;
            ConfigurationConnectionSeparator.Visibility = Visibility.Visible;
            ConnectionConfigurationMenu.Visibility = AppBrand.IsServerAddressLocked ? Visibility.Collapsed : Visibility.Visible;
            SidebarPanel.Visibility = Visibility.Visible;
            SidebarColumn.Width = new GridLength(174);
        }

        foreach (var item in MainMenu.Items.OfType<MenuItem>())
            HideEmptyMenuBranches(item);
    }

    private static void SetMenuAccess(MenuItem item, bool allowed)
    {
        item.IsSubmenuOpen = false;
        item.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        item.IsEnabled = true;
    }

    private static bool HideEmptyMenuBranches(MenuItem parent)
    {
        foreach (var child in parent.Items.OfType<MenuItem>())
            HideEmptyMenuBranches(child);

        var visibleItems = parent.Items
            .OfType<MenuItem>()
            .Where(item => item.Visibility == Visibility.Visible)
            .ToArray();
        if (parent.Items.OfType<MenuItem>().Any() && visibleItems.Length == 0)
            parent.Visibility = Visibility.Collapsed;

        var hasVisibleItemBefore = false;
        foreach (var entry in parent.Items)
        {
            if (entry is MenuItem menuItem && menuItem.Visibility == Visibility.Visible)
            {
                hasVisibleItemBefore = true;
                continue;
            }

            if (entry is Separator separator)
                separator.Visibility = hasVisibleItemBefore && HasVisibleMenuItemAfter(parent, separator)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        return parent.Visibility == Visibility.Visible;
    }

    private static bool HasVisibleMenuItemAfter(MenuItem parent, Separator separator)
    {
        var foundSeparator = false;
        foreach (var entry in parent.Items)
        {
            if (ReferenceEquals(entry, separator))
            {
                foundSeparator = true;
                continue;
            }

            if (!foundSeparator) continue;
            if (entry is Separator) return false;
            if (entry is MenuItem menuItem && menuItem.Visibility == Visibility.Visible) return true;
        }

        return false;
    }

    private void ShowUnavailable(string? module)
    {
        var name = string.IsNullOrWhiteSpace(module) ? "Fitur CPS" : module;
        var key = $"pending:{name.ToLowerInvariant()}";
        StatusText.Text = $"{name} sudah masuk inventarisasi CPS dan sedang disusun untuk tahap paritas berikutnya.";
        if (ActivateExisting(key)) return;
        var view = new UnavailableView(name);
        var child = CreateChild(key, name, view, 650, 390);
        view.CloseRequested += (_, _) => CloseChild(child);
    }

    private MdiChild CreateChild(string key, string title, FrameworkElement view, double preferredWidth, double preferredHeight, bool centered = false, double? preferredLeft = null, double? preferredTop = null)
    {
        var workspaceWidth = Math.Max(760, MdiCanvas.ActualWidth);
        var workspaceHeight = Math.Max(520, MdiCanvas.ActualHeight);
        var cascade = _cascadeIndex++ % 9;
        var left = preferredLeft ?? (centered ? Math.Max(4, (workspaceWidth - preferredWidth) / 2) : 8 + cascade * 42);
        var top = preferredTop ?? (centered ? Math.Max(4, (workspaceHeight - preferredHeight) / 2) : 8 + cascade * 34);
        left = Math.Clamp(left, 0, Math.Max(0, workspaceWidth - 440));
        top = Math.Clamp(top, 0, Math.Max(0, workspaceHeight - 31));
        var width = Math.Min(preferredWidth, Math.Max(560, workspaceWidth - left - 8));
        var height = Math.Min(preferredHeight, Math.Max(360, workspaceHeight - top - 8));

        var child = new MdiChild(key, title, view)
        {
            Width = width,
            Height = height
        };
        child.Activated += (_, _) => ActivateChild(child);
        child.CloseRequested += (_, _) => CloseChild(child);
        _children.Add(child);
        MdiCanvas.Children.Add(child);
        HomeBackdrop.Visibility = Visibility.Visible;
        Canvas.SetLeft(child, left);
        Canvas.SetTop(child, top);
        ActivateChild(child);
        StatusText.Text = $"{title} dibuka sebagai jendela kerja. {_children.Count} jendela aktif.";
        return child;
    }

    private bool ActivateExisting(string key)
    {
        var child = _children.FirstOrDefault(x => x.WindowKey == key);
        if (child is null) return false;
        child.RestoreIfMinimized();
        ActivateChild(child);
        return true;
    }

    private void ActivateChild(MdiChild child)
    {
        _activeChild = child;
        foreach (var openChild in _children) openChild.SetActive(openChild == child);
        Panel.SetZIndex(child, ++_zIndex);
        StatusText.Text = $"Jendela aktif: {child.Title} | {_children.Count} jendela terbuka.";
    }

    private async void CloseChild(MdiChild child) => await CloseChildAsync(child);

    private async Task CloseChildAsync(MdiChild child)
    {
        if (child.Body is IWorkspaceCloseGuard guard && !await guard.ConfirmCloseAsync()) return;
        CloseChildNow(child);
    }

    private void CloseChildNow(MdiChild child)
    {
        if (!_children.Remove(child)) return;
        MdiCanvas.Children.Remove(child);
        _activeChild = _children.OrderByDescending(Panel.GetZIndex).FirstOrDefault();
        if (_activeChild is not null) ActivateChild(_activeChild);
        else
        {
            HomeBackdrop.Visibility = Visibility.Visible;
            StatusText.Text = AppBrand.StatusPrefix + " siap | Tidak ada jendela kerja terbuka.";
        }
    }

    private void CloseActive()
    {
        if (_activeChild is not null) CloseChild(_activeChild);
    }

    private static string? GetActionName(object sender)
    {
        if (sender is FrameworkElement element && element.Tag is not null) return element.Tag.ToString();
        if (sender is MenuItem menuItem) return menuItem.Header?.ToString()?.Replace("_", string.Empty);
        if (sender is Button button) return button.ToolTip?.ToString() ?? button.Content?.ToString();
        return null;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F4 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            CloseActive();
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _children.Count > 1)
        {
            var ordered = _children.OrderByDescending(Panel.GetZIndex).ToList();
            var next = ordered[1];
            next.RestoreIfMinimized();
            ActivateChild(next);
            e.Handled = true;
        }
    }

    private void MdiCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        foreach (var child in _children) child.FitToWorkspace(e.NewSize);
    }

    private void Cascade_Click(object sender, RoutedEventArgs e)
    {
        var visible = _children.Where(x => !x.IsMinimized).ToList();
        if (visible.Count == 0) return;
        var width = Math.Max(560, MdiCanvas.ActualWidth - Math.Min(260, visible.Count * 34) - 10);
        var height = Math.Max(360, MdiCanvas.ActualHeight - Math.Min(210, visible.Count * 30) - 10);
        for (var index = 0; index < visible.Count; index++)
        {
            var child = visible[index];
            if (child.IsMaximized) child.ToggleMaximize();
            child.Width = width;
            child.Height = height;
            Canvas.SetLeft(child, 8 + index * 34);
            Canvas.SetTop(child, 8 + index * 28);
            Panel.SetZIndex(child, ++_zIndex);
        }
        ActivateChild(visible[^1]);
    }

    private void TileHorizontal_Click(object sender, RoutedEventArgs e) => Tile(horizontal: true);
    private void TileVertical_Click(object sender, RoutedEventArgs e) => Tile(horizontal: false);

    private void Tile(bool horizontal)
    {
        var visible = _children.Where(x => !x.IsMinimized).ToList();
        if (visible.Count == 0) return;
        for (var index = 0; index < visible.Count; index++)
        {
            var child = visible[index];
            if (child.IsMaximized) child.ToggleMaximize();
            if (horizontal)
            {
                var height = MdiCanvas.ActualHeight / visible.Count;
                child.Width = MdiCanvas.ActualWidth;
                child.Height = height;
                Canvas.SetLeft(child, 0);
                Canvas.SetTop(child, index * height);
            }
            else
            {
                var width = MdiCanvas.ActualWidth / visible.Count;
                child.Width = Math.Max(child.MinWidth, width);
                child.Height = MdiCanvas.ActualHeight;
                Canvas.SetLeft(child, index * width);
                Canvas.SetTop(child, 0);
            }
        }
        ActivateChild(visible[^1]);
    }

    private void ArrangeIcons_Click(object sender, RoutedEventArgs e)
    {
        var minimized = _children.Where(x => x.IsMinimized).ToList();
        for (var index = 0; index < minimized.Count; index++)
        {
            Canvas.SetLeft(minimized[index], 8 + index * 268);
            Canvas.SetTop(minimized[index], Math.Max(0, MdiCanvas.ActualHeight - 33));
        }
    }

    private async void CloseAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var child in _children.ToList())
            await CloseChildAsync(child);
    }

    private void WindowMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        while (WindowMenu.Items.Count > WindowMenu.Items.IndexOf(WindowListSeparator) + 1)
            WindowMenu.Items.RemoveAt(WindowMenu.Items.Count - 1);

        foreach (var child in _children.OrderBy(Panel.GetZIndex))
        {
            var item = new MenuItem { Header = child.Title, IsChecked = child == _activeChild, Tag = child };
            item.Click += (_, _) =>
            {
                child.RestoreIfMinimized();
                ActivateChild(child);
            };
            WindowMenu.Items.Add(item);
        }
    }

    private sealed record DashboardPoint(string Label, decimal Value);

    private sealed record DashboardBucket(DateOnly Start, DateOnly End, string Label);
}
