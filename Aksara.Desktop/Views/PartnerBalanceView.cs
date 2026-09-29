using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Aksara.Contracts;
using Aksara.Desktop.Controls;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

internal enum PartnerButtonKind { Standard, Primary, Gold, Danger, Tool }

public sealed class PartnerBalanceView : UserControl, IWorkspaceCloseGuard
{
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly string _partnerType;
    private readonly string _kind;
    private readonly CultureInfo _culture = new("id-ID");
    private readonly DatePicker _date = DateInput();
    private readonly TextBox _documentNumber = Box(170);
    private readonly TextBox _partnerCode = Box(130);
    private readonly TextBox _partnerName = Box(330, true);
    private readonly TextBox _partnerAddress = Box(500, true);
    private readonly DatePicker _dueDate = DateInput();
    private readonly TextBox _amount = MoneyBox();
    private readonly TextBox _notes = Box(500);
    private readonly ComboBox _direction = new() { Width = 190, Height = 25, SelectedValuePath = nameof(DirectionChoice.Code), DisplayMemberPath = nameof(DirectionChoice.Name) };
    private readonly ComboBox _target = new() { Width = 500, Height = 25, DisplayMemberPath = nameof(OutstandingChoice.Display) };
    private readonly TextBox _search = Box(230);
    private readonly DataGrid _history = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
    private readonly TextBlock _mode = new() { FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _counter = new() { FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _save = CommandButton("Simpan", 94, PartnerButtonKind.Primary, "\uE74E", "Simpan dokumen");
    private readonly Button _delete = CommandButton("Hapus", 90, PartnerButtonKind.Danger, "\uE74D", "Tandai hapus dokumen");
    private readonly Button _restore = CommandButton("Btl.Hapus", 96, PartnerButtonKind.Gold, "\uE777", "Batalkan status hapus");
    private PartnerBalanceDocumentDto? _current;
    private string _cleanSignature = string.Empty;
    private bool _loading;

    public event EventHandler? CloseRequested;

    public PartnerBalanceView(string partnerType, string kind)
    {
        _partnerType = partnerType.ToUpperInvariant();
        _kind = kind.ToUpperInvariant();
        _direction.ItemsSource = new[] { new DirectionChoice("INCREASE", "Tambah saldo"), new DirectionChoice("DECREASE", "Kurangi saldo") };
        _direction.SelectedValue = "INCREASE";
        _date.SelectedDate = DateTime.Today;
        _dueDate.SelectedDate = DateTime.Today;
        _date.Language = _dueDate.Language = System.Windows.Markup.XmlLanguage.GetLanguage("id-ID");
        Content = BuildLayout();
        ConfigureHistory();
        WireEvents();
        MarkClean();
        Loaded += async (_, _) =>
        {
            await NewAsync();
            await ReloadHistoryAsync();
        };
    }

    public Task<bool> ConfirmCloseAsync()
    {
        if (!HasChanges()) return Task.FromResult(true);
        var result = MessageBox.Show(Window.GetWindow(this), "Data pada form belum disimpan. Yakin ingin menutup dan membuang perubahan?", $"AKSARA - {TitleText}", MessageBoxButton.YesNo, MessageBoxImage.Question);
        return Task.FromResult(result == MessageBoxResult.Yes);
    }

    private string TitleText => (_partnerType, _kind) switch
    {
        ("SUPPLIER", "OPENING") => "Utang Awal",
        ("SUPPLIER", _) => "Koreksi Utang",
        ("CUSTOMER", "OPENING") => "Piutang Awal",
        _ => "Koreksi Piutang"
    };

    private FrameworkElement BuildLayout()
    {
        var root = new Grid { Background = PartnerUi.CanvasBrush };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new TextBlock { Text = TitleText, FontSize = 17, FontWeight = FontWeights.Bold,
            Foreground = PartnerUi.MaroonBrush });
        heading.Children.Add(new TextBlock { Text = PartnerLabel, FontSize = 10.5, Foreground = PartnerUi.MutedBrush,
            Margin = new Thickness(0, 2, 0, 0) });
        header.Children.Add(heading);
        var modeBadge = new Border { MinWidth = 126, Height = 28, Background = PartnerUi.GoldSoftBrush,
            BorderBrush = PartnerUi.GoldBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 0, 10, 0), Child = _mode };
        _mode.Foreground = PartnerUi.MaroonDarkBrush;
        Grid.SetColumn(modeBadge, 1);
        header.Children.Add(modeBadge);
        _counter.Foreground = PartnerUi.MaroonDarkBrush;
        Grid.SetColumn(_counter, 2);
        header.Children.Add(_counter);
        var headerCard = PartnerUi.Card(header, new Thickness(14, 8, 14, 8), new Thickness(14, 10, 14, 0));
        headerCard.BorderThickness = new Thickness(4, 1, 1, 1);
        headerCard.BorderBrush = PartnerUi.MaroonBrush;
        root.Children.Add(headerCard);

        SetStretch(_documentNumber, _partnerName, _partnerAddress, _notes, _target);
        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.7, GridUnitType.Star) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        form.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
        for (var i = 0; i < (_kind == "CORRECTION" ? 6 : 5); i++) form.RowDefinitions.Add(new RowDefinition { Height = new GridLength(33) });
        var formTitle = PartnerUi.SectionTitle("DATA DOKUMEN");
        Grid.SetColumnSpan(formTitle, 4);
        form.Children.Add(formTitle);
        AddField(form, "Tgl. Bukti", _date, 1, 0);
        AddField(form, "No. Bukti", _documentNumber, 1, 2, stretch: true);
        AddField(form, PartnerLabel, PartnerSelector(), 2, 0);
        AddField(form, "Jumlah", _amount, 2, 2);
        AddField(form, "Nama", _partnerName, 3, 0, stretch: true);
        AddField(form, "Jatuh Tempo", _dueDate, 3, 2);
        AddField(form, "Alamat", _partnerAddress, 4, 0, 3, true);
        var noteRow = _kind == "CORRECTION" ? 6 : 5;
        if (_kind == "CORRECTION")
        {
            AddField(form, "Jenis Koreksi", _direction, 5, 0);
            AddField(form, "Bukti Dikoreksi", _target, 5, 2, stretch: true);
        }
        AddField(form, "Keterangan", _notes, noteRow, 0, 3, true);
        var formCard = PartnerUi.Card(form, new Thickness(14, 9, 14, 9), new Thickness(14, 8, 14, 8));
        Grid.SetRow(formCard, 1);
        root.Children.Add(formCard);

        var historyPanel = new Grid();
        historyPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        historyPanel.RowDefinitions.Add(new RowDefinition());
        var toolbar = new Grid { Margin = new Thickness(10, 5, 7, 5) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(PartnerUi.SectionTitle("RIWAYAT DOKUMEN"));
        var searchLabel = new TextBlock { Text = "Cari", Foreground = PartnerUi.MutedBrush,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(searchLabel, 1);
        toolbar.Children.Add(searchLabel);
        _search.Margin = new Thickness(0, 0, 7, 0);
        _search.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(_search, 2);
        toolbar.Children.Add(_search);
        var find = CommandButton("Cari", 78, PartnerButtonKind.Standard, "\uE721", "Cari riwayat");
        find.Margin = new Thickness(0);
        find.Click += async (_, _) => await ReloadHistoryAsync();
        Grid.SetColumn(find, 3);
        toolbar.Children.Add(find);
        Grid.SetRow(toolbar, 0); historyPanel.Children.Add(toolbar);
        _history.GridLinesVisibility = DataGridGridLinesVisibility.All;
        _history.HorizontalGridLinesBrush = PartnerUi.GridBrush;
        _history.VerticalGridLinesBrush = PartnerUi.GridBrush;
        _history.RowHeight = 25;
        _history.ColumnHeaderHeight = 29;
        _history.ColumnHeaderStyle = PartnerUi.ColumnHeaderStyle();
        Grid.SetRow(_history, 1); historyPanel.Children.Add(_history);
        var historyCard = PartnerUi.Card(historyPanel, new Thickness(0), new Thickness(14, 0, 14, 8));
        Grid.SetRow(historyCard, 2);
        root.Children.Add(historyCard);

        var commands = new Grid();
        commands.ColumnDefinitions.Add(new ColumnDefinition());
        commands.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var nav = new StackPanel { Orientation = Orientation.Horizontal };
        var first = CommandButton("", 32, PartnerButtonKind.Tool, "\uE892", "Data pertama");
        var previous = CommandButton("", 32, PartnerButtonKind.Tool, "\uE76B", "Data sebelumnya");
        var next = CommandButton("", 32, PartnerButtonKind.Tool, "\uE76C", "Data berikutnya");
        var last = CommandButton("", 32, PartnerButtonKind.Tool, "\uE893", "Data terakhir");
        first.Click += (_, _) => SelectHistory(0); previous.Click += (_, _) => MoveHistory(-1); next.Click += (_, _) => MoveHistory(1); last.Click += (_, _) => SelectHistory(_history.Items.Count - 1);
        nav.Children.Add(first); nav.Children.Add(previous); nav.Children.Add(next); nav.Children.Add(last);
        commands.Children.Add(nav);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var add = CommandButton("Tambah", 90, PartnerButtonKind.Standard, "\uE710", "Buat dokumen baru");
        var cancel = CommandButton("Batal", 86, PartnerButtonKind.Standard, "\uE711", "Batalkan perubahan");
        var close = CommandButton("Tutup", 86, PartnerButtonKind.Standard, "\uE8BB", "Tutup form");
        add.Click += async (_, _) => await NewAsync(); cancel.Click += async (_, _) => await CancelEditAsync(); _save.Click += async (_, _) => await SaveAsync();
        _delete.Click += async (_, _) => await DeleteAsync(); _restore.Click += async (_, _) => await RestoreAsync(); close.Click += async (_, _) => { if (await ConfirmCloseAsync()) CloseRequested?.Invoke(this, EventArgs.Empty); };
        actions.Children.Add(add); actions.Children.Add(cancel); actions.Children.Add(_save); actions.Children.Add(_delete); actions.Children.Add(_restore); actions.Children.Add(close);
        Grid.SetColumn(actions, 1); commands.Children.Add(actions);
        var commandCard = PartnerUi.Card(commands, new Thickness(8), new Thickness(14, 0, 14, 8));
        Grid.SetRow(commandCard, 3);
        root.Children.Add(commandCard);

        _status.Foreground = PartnerUi.MutedBrush;
        var statusBorder = new Border { BorderBrush = PartnerUi.BorderBrush, BorderThickness = new Thickness(0, 1, 0, 0),
            Background = PartnerUi.SurfaceBrush, Padding = new Thickness(14, 0, 14, 0), Child = _status };
        Grid.SetRow(statusBorder, 4); root.Children.Add(statusBorder);
        return root;
    }

    private FrameworkElement PartnerSelector()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(_partnerCode);
        var button = CommandButton("", 32, PartnerButtonKind.Tool, "\uE721", $"Cari {PartnerLabel.ToLowerInvariant()}");
        button.Margin = new Thickness(6, 0, 0, 0);
        button.Click += (_, _) => OpenPartnerLookup();
        panel.Children.Add(button);
        return panel;
    }

    private string PartnerLabel => _partnerType == "SUPPLIER" ? "Supplier" : "Customer";

    private void ConfigureHistory()
    {
        _history.Columns.Add(Column("Tgl. Bukti", nameof(PartnerBalanceDocumentDto.DocumentDate), 90, "dd/MM/yyyy"));
        _history.Columns.Add(Column("No. Bukti", nameof(PartnerBalanceDocumentDto.DocumentNumber), 120));
        _history.Columns.Add(Column($"Kode {PartnerLabel}", nameof(PartnerBalanceDocumentDto.PartnerCode), 105));
        _history.Columns.Add(Column("Nama", nameof(PartnerBalanceDocumentDto.PartnerName), 220));
        if (_kind == "CORRECTION") _history.Columns.Add(Column("Jenis", nameof(PartnerBalanceDocumentDto.Direction), 85, elementStyle: CenterStyle()));
        _history.Columns.Add(NumberColumn("Jumlah", nameof(PartnerBalanceDocumentDto.Amount), 120));
        _history.Columns.Add(NumberColumn("Terpakai", nameof(PartnerBalanceDocumentDto.SettledAmount), 105));
        _history.Columns.Add(NumberColumn("Sisa", nameof(PartnerBalanceDocumentDto.RemainingBalance), 110));
        _history.Columns.Add(Column("Status", nameof(PartnerBalanceDocumentDto.Status), 90, elementStyle: CenterStyle()));
        _history.MouseDoubleClick += (_, _) => ApplySelectedHistory();
        _history.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; ApplySelectedHistory(); } };
    }

    private void WireEvents()
    {
        _date.SelectedDateChanged += async (_, _) => { if (!_loading && _current is null) await PreviewNumberAsync(); };
        _partnerCode.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.F4) { e.Handled = true; OpenPartnerLookup(); }
            else if (e.Key == Key.Enter) { e.Handled = true; await ResolvePartnerAsync(); }
        };
        _partnerCode.MouseDoubleClick += (_, _) => OpenPartnerLookup();
        _direction.SelectionChanged += async (_, _) =>
        {
            if (_loading) return;
            _target.IsEnabled = IsDecrease;
            if (IsDecrease) await LoadTargetsAsync(); else { _target.ItemsSource = null; _target.SelectedItem = null; }
        };
        _search.PreviewKeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await ReloadHistoryAsync(); } };
    }

    private bool IsDecrease => _kind == "CORRECTION" && Equals(_direction.SelectedValue, "DECREASE");

    private async Task NewAsync()
    {
        if (HasChanges() && MessageBox.Show(Window.GetWindow(this), "Perubahan belum disimpan. Buang perubahan dan buat data baru?", $"AKSARA - {TitleText}", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _loading = true;
        _current = null;
        _date.SelectedDate = DateTime.Today; _documentNumber.Clear(); _partnerCode.Clear(); _partnerName.Clear(); _partnerAddress.Clear();
        _dueDate.SelectedDate = DateTime.Today; _amount.Text = "0"; _notes.Clear(); _direction.SelectedValue = "INCREASE"; _target.ItemsSource = null; _target.IsEnabled = false;
        _mode.Text = "TAMBAH DATA"; _status.Text = "Isi data lalu tekan Simpan.";
        _loading = false;
        await PreviewNumberAsync();
        UpdateCommandStates();
        MarkClean();
    }

    private async Task CancelEditAsync()
    {
        if (_current is not null) { ApplyDocument(_current); return; }
        await NewAsync();
    }

    private async Task PreviewNumberAsync()
    {
        try { _documentNumber.Text = await _api.GetNextPartnerBalanceNumberAsync(_partnerType, _kind, DateOnly.FromDateTime(_date.SelectedDate ?? DateTime.Today)); }
        catch { if (string.IsNullOrWhiteSpace(_documentNumber.Text)) _documentNumber.Text = "OTOMATIS"; }
    }

    private async Task ResolvePartnerAsync()
    {
        if (string.IsNullOrWhiteSpace(_partnerCode.Text)) return;
        try
        {
            if (_partnerType == "SUPPLIER")
            {
                var row = (await _api.SearchSuppliersAsync(_partnerCode.Text)).FirstOrDefault(x => Same(x.Code, _partnerCode.Text));
                if (row is null) throw new InvalidOperationException("Supplier tidak ditemukan.");
                ApplyPartner(row.Code, row.CompanyName, row.Address, row.CreditDays);
            }
            else
            {
                var row = (await _api.SearchCustomersAsync(_partnerCode.Text)).FirstOrDefault(x => Same(x.Code, _partnerCode.Text));
                if (row is null) throw new InvalidOperationException("Customer tidak ditemukan.");
                ApplyPartner(row.Code, row.CompanyName, row.Address, row.CreditDays);
            }
            if (IsDecrease) await LoadTargetsAsync();
        }
        catch (Exception exception) { _status.Text = exception.Message; ViewError.ShowWarningUnlessDatabaseUnavailable(exception, $"AKSARA - {TitleText}"); }
    }

    private void OpenPartnerLookup()
    {
        if (_partnerType == "SUPPLIER")
        {
            var dialog = new SupplierLookupWindow { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() == true && dialog.SelectedSupplier is { } row) ApplyPartner(row.Code, row.CompanyName, row.Address, row.CreditDays);
        }
        else
        {
            var dialog = new CustomerLookupWindow { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() == true && dialog.SelectedCustomer is { } row) ApplyPartner(row.Code, row.CompanyName, row.Address, row.CreditDays);
        }
        if (IsDecrease) _ = LoadTargetsAsync();
    }

    private void ApplyPartner(string code, string name, string? address, int creditDays)
    {
        _partnerCode.Text = code; _partnerName.Text = name; _partnerAddress.Text = address ?? string.Empty;
        if (_current is null) _dueDate.SelectedDate = (_date.SelectedDate ?? DateTime.Today).AddDays(Math.Max(0, creditDays));
    }

    private async Task LoadTargetsAsync(bool markCleanAfter = false)
    {
        if (!IsDecrease || string.IsNullOrWhiteSpace(_partnerCode.Text)) return;
        try
        {
            IReadOnlyList<OutstandingChoice> choices;
            if (_partnerType == "SUPPLIER")
                choices = (await _api.SearchSupplierBalanceSourcesAsync(_partnerCode.Text)).Select(x => new OutstandingChoice(x.SourceType, x.PurchaseId, x.DocumentNumber, x.RemainingBalance)).ToArray();
            else
                choices = (await _api.SearchCustomerBalanceSourcesAsync(_partnerCode.Text)).Select(x => new OutstandingChoice(x.SourceType, x.SaleId, x.DocumentNumber, x.RemainingBalance)).ToArray();
            var selectedId = (_target.SelectedItem as OutstandingChoice)?.Id ?? _current?.TargetSourceId;
            var list = choices.ToList();
            if (_current?.TargetSourceId is Guid currentTarget && list.All(x => x.Id != currentTarget))
                list.Insert(0, new(_current.TargetSourceType ?? (_partnerType == "SUPPLIER" ? "PURCHASE" : "SALE"), currentTarget, _current.TargetDocumentNumber ?? "Bukti sumber", _current.Amount));
            _target.ItemsSource = list;
            _target.SelectedItem = list.FirstOrDefault(x => x.Id == selectedId);
            if (markCleanAfter) MarkClean();
        }
        catch (Exception exception) { _status.Text = exception.Message; }
    }

    private async Task SaveAsync()
    {
        try
        {
            if (!await EnsurePartnerResolvedAsync()) return;
            if (!TryAmount(_amount.Text, out var amount) || amount <= 0) throw new InvalidOperationException("Jumlah harus lebih dari nol.");
            var target = _target.SelectedItem as OutstandingChoice;
            if (IsDecrease && target is null) throw new InvalidOperationException("Pilih bukti yang akan dikoreksi.");
            IsEnabled = false; _status.Text = "Menyimpan dokumen dan jurnal...";
            var saved = await _api.SavePartnerBalanceAsync(new(
                _current?.Id, _partnerType, _kind, _kind == "OPENING" ? "INCREASE" : (_direction.SelectedValue?.ToString() ?? "INCREASE"),
                DateOnly.FromDateTime(_date.SelectedDate ?? DateTime.Today), _documentNumber.Text, _partnerCode.Text,
                _dueDate.SelectedDate is null ? null : DateOnly.FromDateTime(_dueDate.SelectedDate.Value), amount,
                IsDecrease ? target?.SourceType : null, IsDecrease ? target?.Id : null, _notes.Text, _current?.Version,
                _api.CurrentUserName, Environment.MachineName));
            _current = saved; _status.Text = $"{saved.DocumentNumber} tersimpan. Jurnal: {saved.JournalNumber}.";
            await ReloadHistoryAsync(); ApplyDocument(saved);
        }
        catch (Exception exception) { _status.Text = exception.Message; ViewError.ShowWarningUnlessDatabaseUnavailable(exception, $"AKSARA - {TitleText}"); }
        finally { IsEnabled = true; }
    }

    private async Task<bool> EnsurePartnerResolvedAsync()
    {
        await ResolvePartnerAsync();
        return !string.IsNullOrWhiteSpace(_partnerName.Text);
    }

    private async Task DeleteAsync()
    {
        if (_current is null || _current.Status != "POSTED") return;
        if (MessageBox.Show(Window.GetWindow(this), "Apakah Anda ingin menandai hapus data ini?", $"AKSARA - {TitleText}", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            IsEnabled = false;
            var saved = await _api.CancelPartnerBalanceAsync(_partnerType, _current.Id, new(_current.Version, "Ditandai hapus dari form.", _api.CurrentUserName, Environment.MachineName));
            _current = saved; await ReloadHistoryAsync(); ApplyDocument(saved); _status.Text = $"{saved.DocumentNumber} ditandai hapus dan tidak diperhitungkan.";
        }
        catch (Exception exception) { ViewError.ShowWarningUnlessDatabaseUnavailable(exception, $"AKSARA - {TitleText}"); }
        finally { IsEnabled = true; }
    }

    private async Task RestoreAsync()
    {
        if (_current is null || _current.Status != "CANCELLED") return;
        if (MessageBox.Show(Window.GetWindow(this), "Apakah Anda ingin membatalkan hapus data?", $"AKSARA - {TitleText}", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            IsEnabled = false;
            var saved = await _api.RestorePartnerBalanceAsync(_partnerType, _current.Id, new(_current.Version, _api.CurrentUserName, Environment.MachineName));
            _current = saved; await ReloadHistoryAsync(); ApplyDocument(saved); _status.Text = $"{saved.DocumentNumber} kembali diperhitungkan.";
        }
        catch (Exception exception) { ViewError.ShowWarningUnlessDatabaseUnavailable(exception, $"AKSARA - {TitleText}"); }
        finally { IsEnabled = true; }
    }

    private async Task ReloadHistoryAsync()
    {
        try
        {
            var rows = await _api.SearchPartnerBalancesAsync(_partnerType, _kind, _search.Text, true);
            _history.ItemsSource = rows; _counter.Text = $"TOTAL DATA {rows.Count:N0}";
        }
        catch (Exception exception) { _history.ItemsSource = Array.Empty<PartnerBalanceDocumentDto>(); _counter.Text = "TOTAL DATA 0"; _status.Text = exception.Message; }
    }

    private void ApplySelectedHistory()
    {
        if (_history.SelectedItem is PartnerBalanceDocumentDto selected) ApplyDocument(selected);
    }

    private void SelectHistory(int index)
    {
        if (_history.Items.Count == 0) return;
        index = Math.Clamp(index, 0, _history.Items.Count - 1); _history.SelectedIndex = index; _history.ScrollIntoView(_history.SelectedItem); ApplySelectedHistory();
    }

    private void MoveHistory(int offset) => SelectHistory((_history.SelectedIndex < 0 ? 0 : _history.SelectedIndex) + offset);

    private void ApplyDocument(PartnerBalanceDocumentDto row)
    {
        _loading = true; _current = row;
        _date.SelectedDate = row.DocumentDate.ToDateTime(TimeOnly.MinValue); _documentNumber.Text = row.DocumentNumber;
        _partnerCode.Text = row.PartnerCode; _partnerName.Text = row.PartnerName; _partnerAddress.Text = row.PartnerAddress ?? string.Empty;
        _dueDate.SelectedDate = row.DueDate?.ToDateTime(TimeOnly.MinValue); _amount.Text = row.Amount.ToString("N2", _culture); _notes.Text = row.Notes ?? string.Empty;
        _direction.SelectedValue = row.Direction; _target.IsEnabled = IsDecrease;
        _mode.Text = row.Status == "CANCELLED" ? "DITANDAI HAPUS" : "EDIT DATA";
        _loading = false;
        if (IsDecrease) _ = LoadTargetsAsync(markCleanAfter: true); else _target.ItemsSource = null;
        UpdateCommandStates(); MarkClean();
    }

    private void UpdateCommandStates()
    {
        var module = _partnerType == "SUPPLIER" ? "SUPPLIER" : "CUSTOMER";
        _save.IsEnabled = _api.Can(module, "MANAGE") && (_current is null || _current.Status == "POSTED");
        _delete.IsEnabled = _api.Can(module, "DELETE") && _current?.Status == "POSTED";
        _restore.IsEnabled = _api.Can(module, "DELETE") && _current?.Status == "CANCELLED";
    }

    private bool HasChanges() => CurrentSignature() != _cleanSignature;
    private void MarkClean() => _cleanSignature = CurrentSignature();
    private string CurrentSignature() => string.Join("|", _current?.Id, _date.SelectedDate, _documentNumber.Text.Trim(), _partnerCode.Text.Trim(), _dueDate.SelectedDate, _amount.Text.Trim(), _direction.SelectedValue, (_target.SelectedItem as OutstandingChoice)?.Id, _notes.Text.Trim());

    private static void AddField(Grid grid, string label, UIElement input, int row, int column, int span = 1, bool stretch = false)
    {
        var text = new TextBlock { Text = label, Foreground = PartnerUi.MutedBrush,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        Grid.SetRow(text, row); Grid.SetColumn(text, column); grid.Children.Add(text);
        if (input is FrameworkElement element)
        {
            element.HorizontalAlignment = stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            if (column == 0 && span == 1)
                element.Margin = new Thickness(element.Margin.Left, element.Margin.Top, Math.Max(18, element.Margin.Right), element.Margin.Bottom);
        }
        Grid.SetRow(input, row); Grid.SetColumn(input, column + 1); Grid.SetColumnSpan(input, span); grid.Children.Add(input);
    }
    private static void SetStretch(params FrameworkElement[] elements)
    {
        foreach (var element in elements) { element.Width = double.NaN; element.HorizontalAlignment = HorizontalAlignment.Stretch; }
    }
    private static TextBox Box(double width, bool readOnly = false) => new() { Width = width, Height = 26, IsReadOnly = readOnly,
        Padding = new Thickness(6, 1, 6, 1), Background = readOnly ? PartnerUi.ReadOnlyBrush : Brushes.White,
        Foreground = readOnly ? PartnerUi.MutedBrush : PartnerUi.TextBrush };
    private static TextBox MoneyBox() => new() { Width = 185, Height = 25, Text = "0", TextAlignment = TextAlignment.Right, Padding = new Thickness(4, 1, 5, 1), Background = Brushes.White };
    private static DatePicker DateInput() => new() { Width = 150, Height = 25, SelectedDateFormat = DatePickerFormat.Short };
    private static Button CommandButton(string text, double width, PartnerButtonKind kind = PartnerButtonKind.Standard,
        string? glyph = null, string? tooltip = null) => PartnerUi.Button(text, width, kind, glyph, tooltip);
    private static DataGridTextColumn Column(string header, string path, DataGridLength width, string? format = null, Style? elementStyle = null) =>
        new() { Header = header, Binding = new Binding(path) { StringFormat = format }, Width = width, ElementStyle = elementStyle };
    private static DataGridTextColumn NumberColumn(string header, string path, double width) =>
        new() { Header = header, Binding = new Binding(path) { StringFormat = "N2" }, Width = width, ElementStyle = RightStyle() };
    private static Style RightStyle()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
        style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
        return style;
    }
    private static Style CenterStyle()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
        style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
        return style;
    }
    private static bool TryAmount(string? text, out decimal value) => decimal.TryParse(text, NumberStyles.Number, new CultureInfo("id-ID"), out value) || decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    private static bool Same(string? left, string? right) => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
    private sealed record DirectionChoice(string Code, string Name);
    private sealed record OutstandingChoice(string SourceType, Guid Id, string DocumentNumber, decimal Remaining)
    {
        public string Display => $"{DocumentNumber} | Sisa {Remaining:N2}";
    }
}

internal static class PartnerUi
{
    public static readonly SolidColorBrush MaroonDarkBrush = Brush("#5C1018");
    public static readonly SolidColorBrush MaroonBrush = Brush("#7C1622");
    public static readonly SolidColorBrush GoldBrush = Brush("#D6A22E");
    public static readonly SolidColorBrush GoldSoftBrush = Brush("#FFF4D2");
    public static readonly SolidColorBrush CanvasBrush = Brush("#F5F1F2");
    public static readonly SolidColorBrush SurfaceBrush = Brush("#FFFDFD");
    public static readonly SolidColorBrush ReadOnlyBrush = Brush("#F3EFF0");
    public static readonly SolidColorBrush BorderBrush = Brush("#D8C9CC");
    public static readonly SolidColorBrush GridBrush = Brush("#E2D8DA");
    public static readonly SolidColorBrush TextBrush = Brush("#2C2022");
    public static readonly SolidColorBrush MutedBrush = Brush("#705D61");

    private static readonly Style StandardButtonStyle = CreateButtonStyle(SurfaceBrush, BorderBrush, MaroonDarkBrush, GoldSoftBrush, GoldBrush);
    private static readonly Style PrimaryButtonStyle = CreateButtonStyle(MaroonBrush, MaroonDarkBrush, Brushes.White, Brush("#941E2E"), MaroonDarkBrush);
    private static readonly Style GoldButtonStyle = CreateButtonStyle(GoldSoftBrush, GoldBrush, MaroonDarkBrush, Brush("#FBE6A8"), Brush("#B98516"));
    private static readonly Style DangerButtonStyle = CreateButtonStyle(Brush("#FFF7F7"), Brush("#E4B9BF"), Brush("#A6192E"), Brush("#FBE8EA"), Brush("#C76A77"));
    private static readonly Style ToolButtonStyle = CreateButtonStyle(SurfaceBrush, BorderBrush, MaroonDarkBrush, GoldSoftBrush, GoldBrush);

    public static Border Card(UIElement child, Thickness padding, Thickness? margin = null, Brush? background = null) => new()
    {
        Background = background ?? SurfaceBrush,
        BorderBrush = BorderBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = padding,
        Margin = margin ?? new Thickness(0),
        Child = child
    };

    public static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 10.5,
        FontWeight = FontWeights.Bold,
        Foreground = MaroonBrush,
        VerticalAlignment = VerticalAlignment.Center
    };

    public static Button Button(string caption, double width, PartnerButtonKind kind = PartnerButtonKind.Standard,
        string? glyph = null, string? tooltip = null)
    {
        var button = new Button
        {
            Width = width,
            MinWidth = width,
            Height = 30,
            MinHeight = 0,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = kind == PartnerButtonKind.Tool ? new Thickness(0) : new Thickness(8, 0, 8, 0),
            Style = kind switch
            {
                PartnerButtonKind.Primary => PrimaryButtonStyle,
                PartnerButtonKind.Gold => GoldButtonStyle,
                PartnerButtonKind.Danger => DangerButtonStyle,
                PartnerButtonKind.Tool => ToolButtonStyle,
                _ => StandardButtonStyle
            },
            ToolTip = tooltip
        };
        ToolTipService.SetInitialShowDelay(button, 150);
        ToolTipService.SetShowDuration(button, 12000);
        if (glyph is null)
        {
            button.Content = caption;
            return button;
        }

        if (string.IsNullOrWhiteSpace(caption))
        {
            button.Content = Icon(glyph, 14, button.Foreground);
            return button;
        }

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(Icon(glyph, 13, button.Foreground));
        content.Children.Add(new TextBlock { Text = caption, Foreground = button.Foreground,
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        button.Content = content;
        return button;
    }

    public static Style ColumnHeaderStyle()
    {
        var style = new Style(typeof(DataGridColumnHeader));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#EFE1E4")));
        style.Setters.Add(new Setter(Control.ForegroundProperty, MaroonDarkBrush));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, BorderBrush));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(5, 3, 5, 3)));
        return style;
    }

    private static TextBlock Icon(string glyph, double size, Brush foreground) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        FontSize = size,
        Foreground = foreground,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static Style CreateButtonStyle(Brush background, Brush border, Brush foreground, Brush hover, Brush pressed)
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, background));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, border));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));

        var template = new ControlTemplate(typeof(Button));
        var chrome = new FrameworkElementFactory(typeof(Border));
        chrome.SetBinding(Border.BackgroundProperty, TemplateBinding("Background"));
        chrome.SetBinding(Border.BorderBrushProperty, TemplateBinding("BorderBrush"));
        chrome.SetBinding(Border.BorderThicknessProperty, TemplateBinding("BorderThickness"));
        chrome.SetBinding(Border.PaddingProperty, TemplateBinding("Padding"));
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(ContentPresenter.ContentProperty, TemplateBinding("Content"));
        presenter.SetBinding(ContentPresenter.ContentTemplateProperty, TemplateBinding("ContentTemplate"));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        chrome.AppendChild(presenter);
        template.VisualTree = chrome;

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, hover));
        hoverTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, GoldBrush));
        template.Triggers.Add(hoverTrigger);
        var pressedTrigger = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, pressed));
        template.Triggers.Add(pressedTrigger);
        var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabledTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        template.Triggers.Add(disabledTrigger);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static Binding TemplateBinding(string property) => new(property)
    {
        RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
    };

    private static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}
