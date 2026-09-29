using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Aksara.Contracts;
using Aksara.Desktop.Services;
using Microsoft.Win32;

namespace Aksara.Desktop.Views;

public sealed class PeriodCloseView : UserControl
{
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly CultureInfo _culture = new("id-ID");
    private readonly DatePicker _cutoff = DateInput();
    private readonly DatePicker _opening = DateInput();
    private readonly TextBox _periodName = Box();
    private readonly TextBox _backupLocation = Box(true);
    private readonly TextBox _archiveLocation = Box(true);
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
    private readonly TextBlock _blockers = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick, LineHeight = 19 };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly DataGrid _history = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        CanUserAddRows = false,
        SelectionMode = DataGridSelectionMode.Single,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.All,
        RowHeight = 36,
        ColumnHeaderHeight = 32
    };
    private readonly Button _closePeriod = PartnerUi.Button("Tutup Buku", 112, PartnerButtonKind.Primary, "\uE73E", "Jalankan Tutup Buku setelah preflight berhasil");
    private PeriodClosePreflightDto? _lastPreflight;
    private string? _pendingIdempotencyKey;

    public event EventHandler? CloseRequested;

    public PeriodCloseView()
    {
        var today = DateTime.Today;
        var firstDay = new DateTime(today.Year, today.Month, 1);
        var cutoff = firstDay.AddDays(-1);
        _cutoff.SelectedDate = cutoff;
        _opening.SelectedDate = firstDay;
        _periodName.Text = cutoff.ToString("yy/MM", _culture);
        var outputRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AKSARA", "Tutup Buku");
        _backupLocation.Text = Path.Combine(outputRoot, $"_ttpbuku_{cutoff:yyyyMMdd}");
        _archiveLocation.Text = Path.Combine(outputRoot, $"_data_sd_{cutoff:yyyyMMdd}");
        _cutoff.Language = _opening.Language = System.Windows.Markup.XmlLanguage.GetLanguage("id-ID");
        _history.ColumnHeaderStyle = PartnerUi.ColumnHeaderStyle();
        _history.HorizontalGridLinesBrush = PartnerUi.GridBrush;
        _history.VerticalGridLinesBrush = PartnerUi.GridBrush;
        ConfigureHistory();
        Content = BuildLayout();
        _closePeriod.IsEnabled = _api.Can("MAINTENANCE", "MANAGE");
        Loaded += async (_, _) => await ReloadHistoryAsync();
    }

    private FrameworkElement BuildLayout()
    {
        var root = new Grid { Background = PartnerUi.CanvasBrush };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = "Tutup Buku",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = PartnerUi.MaroonBrush
        });
        heading.Children.Add(new TextBlock
        {
            Text = "Kunci transaksi lama dan simpan snapshot saldo yang dapat diaudit",
            Foreground = PartnerUi.MutedBrush,
            Margin = new Thickness(0, 3, 0, 0)
        });
        var headerCard = PartnerUi.Card(heading, new Thickness(14, 10, 14, 10), new Thickness(14, 10, 14, 7));
        headerCard.BorderThickness = new Thickness(4, 1, 1, 1);
        headerCard.BorderBrush = PartnerUi.MaroonBrush;
        root.Children.Add(headerCard);

        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(175) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var index = 0; index < 5; index++) form.RowDefinitions.Add(new RowDefinition { Height = new GridLength(index == 0 ? 27 : 35) });
        var section = PartnerUi.SectionTitle("PERIODE DAN BACKUP");
        Grid.SetColumnSpan(section, 4);
        form.Children.Add(section);
        AddField(form, "Ditutup sampai", _cutoff, 1, 0);
        AddField(form, "Tanggal saldo awal baru", _opening, 1, 2);
        AddField(form, "Nama periode", _periodName, 2, 0);

        var locationPanel = new Grid();
        locationPanel.ColumnDefinitions.Add(new ColumnDefinition());
        locationPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        locationPanel.Children.Add(_backupLocation);
        var browse = PartnerUi.Button("Pilih", 78, PartnerButtonKind.Standard, "\uE838", "Pilih direktori backup data total");
        browse.Margin = new Thickness(7, 0, 0, 0);
        browse.Click += (_, _) => BrowseLocation(_backupLocation, "Pilih direktori backup data total");
        Grid.SetColumn(browse, 1);
        locationPanel.Children.Add(browse);
        AddField(form, "Backup data total", locationPanel, 3, 0, 3);

        var archivePanel = new Grid();
        archivePanel.ColumnDefinitions.Add(new ColumnDefinition());
        archivePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        archivePanel.Children.Add(_archiveLocation);
        var browseArchive = PartnerUi.Button("Pilih", 78, PartnerButtonKind.Standard, "\uE838", "Pilih direktori arsip data sampai cutoff");
        browseArchive.Margin = new Thickness(7, 0, 0, 0);
        browseArchive.Click += (_, _) => BrowseLocation(_archiveLocation, "Pilih direktori arsip data sampai cutoff");
        Grid.SetColumn(browseArchive, 1);
        archivePanel.Children.Add(browseArchive);
        AddField(form, "Arsip s/d cutoff", archivePanel, 4, 0, 3);
        var formCard = PartnerUi.Card(form, new Thickness(14, 8, 14, 8), new Thickness(14, 0, 14, 7));
        Grid.SetRow(formCard, 1);
        root.Children.Add(formCard);

        var preflightPanel = new Grid();
        preflightPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        preflightPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var summaryBox = new StackPanel();
        summaryBox.Children.Add(PartnerUi.SectionTitle("RINGKASAN PREFLIGHT"));
        _summary.Margin = new Thickness(0, 7, 16, 0);
        summaryBox.Children.Add(_summary);
        preflightPanel.Children.Add(summaryBox);
        var blockerBox = new StackPanel();
        blockerBox.Children.Add(PartnerUi.SectionTitle("VALIDASI"));
        _blockers.Margin = new Thickness(0, 7, 0, 0);
        blockerBox.Children.Add(_blockers);
        Grid.SetColumn(blockerBox, 1);
        preflightPanel.Children.Add(blockerBox);
        var preflightCard = PartnerUi.Card(preflightPanel, new Thickness(14, 10, 14, 10), new Thickness(14, 0, 14, 7));
        Grid.SetRow(preflightCard, 2);
        root.Children.Add(preflightCard);

        var historyPanel = new Grid();
        historyPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(31) });
        historyPanel.RowDefinitions.Add(new RowDefinition());
        var historyTitle = PartnerUi.SectionTitle("HISTORI TUTUP BUKU");
        historyPanel.Children.Add(historyTitle);
        Grid.SetRow(_history, 1);
        historyPanel.Children.Add(_history);
        var historyCard = PartnerUi.Card(historyPanel, new Thickness(10, 7, 10, 10), new Thickness(14, 0, 14, 7));
        Grid.SetRow(historyCard, 3);
        root.Children.Add(historyCard);

        var commands = new Grid();
        commands.ColumnDefinitions.Add(new ColumnDefinition());
        commands.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var info = new TextBlock
        {
            Text = "Transaksi lama tidak dihapus. Setelah ditutup, tanggal tersebut tidak dapat dimutasi lagi.",
            Foreground = PartnerUi.MutedBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        commands.Children.Add(info);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var preflight = PartnerUi.Button("Preflight", 102, PartnerButtonKind.Gold, "\uE9D9", "Periksa kesiapan Tutup Buku");
        var export = PartnerUi.Button("Ekspor Ulang", 116, PartnerButtonKind.Standard, "\uE896", "Ekspor ulang snapshot yang dipilih");
        var refresh = PartnerUi.Button("Muat Ulang", 106, PartnerButtonKind.Standard, "\uE72C", "Muat ulang histori");
        var close = PartnerUi.Button("Tutup", 84, PartnerButtonKind.Standard, "\uE8BB", "Tutup form");
        preflight.Click += async (_, _) => await RunPreflightAsync(showErrorDialog: true);
        _closePeriod.Click += async (_, _) => await ClosePeriodAsync();
        export.Click += async (_, _) => await ExportSelectedAsync();
        refresh.Click += async (_, _) => await ReloadHistoryAsync();
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        actions.Children.Add(preflight);
        actions.Children.Add(_closePeriod);
        actions.Children.Add(export);
        actions.Children.Add(refresh);
        actions.Children.Add(close);
        Grid.SetColumn(actions, 1);
        commands.Children.Add(actions);
        var commandCard = PartnerUi.Card(commands, new Thickness(10, 8, 6, 8), new Thickness(14, 0, 14, 7));
        Grid.SetRow(commandCard, 4);
        root.Children.Add(commandCard);

        _status.Foreground = PartnerUi.MutedBrush;
        var status = new Border
        {
            Background = PartnerUi.SurfaceBrush,
            BorderBrush = PartnerUi.BorderBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 0, 14, 0),
            Child = _status
        };
        Grid.SetRow(status, 5);
        root.Children.Add(status);
        return root;
    }

    private void ConfigureHistory()
    {
        _history.Columns.Add(Column("Periode", nameof(PeriodCloseDto.PeriodName), 135));
        _history.Columns.Add(Column("Ditutup s/d", nameof(PeriodCloseDto.CutoffDate), 100, "dd/MM/yyyy"));
        _history.Columns.Add(Column("Status", nameof(PeriodCloseDto.Status), 90));
        _history.Columns.Add(Column("Oleh", nameof(PeriodCloseDto.CreatedBy), 100));
        _history.Columns.Add(Column("Waktu", nameof(PeriodCloseDto.CreatedAt), 140, "dd/MM/yyyy HH:mm"));
        _history.Columns.Add(Column("Deposting Oleh", nameof(PeriodCloseDto.ReopenedBy), 125));
        _history.Columns.Add(Column("Waktu Deposting", nameof(PeriodCloseDto.ReopenedAt), 170, "dd/MM/yyyy HH:mm"));
        _history.Columns.Add(new DepostingActionColumn(CanDepost, DepostPeriodAsync)
        {
            Header = "Aksi",
            Width = 112,
            IsReadOnly = true
        });
    }

    private async Task<PeriodClosePreflightDto?> RunPreflightAsync(bool showErrorDialog)
    {
        if (!TryReadSelection(out var request)) return null;
        try
        {
            SetBusy(true, "Memeriksa konsistensi transaksi...");
            var result = await _api.GetPeriodClosePreflightAsync(request);
            _lastPreflight = result;
            _pendingIdempotencyKey = null;
            ShowPreflight(result);
            _status.Text = result.CanClose ? "Preflight selesai. Periode siap ditutup." : "Preflight menemukan data yang harus diperbaiki.";
            return result;
        }
        catch (Exception exception)
        {
            _status.Text = "Preflight gagal.";
            if (showErrorDialog) MessageBox.Show(Window.GetWindow(this), exception.Message, "Tutup Buku", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        finally { SetBusy(false); }
    }

    private async Task ClosePeriodAsync()
    {
        if (!_api.Can("MAINTENANCE", "MANAGE"))
        {
            MessageBox.Show(Window.GetWindow(this), "Pengguna ini tidak memiliki hak untuk menjalankan Tutup Buku.", "Tutup Buku", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var preflight = await RunPreflightAsync(showErrorDialog: true);
        if (preflight is null || !preflight.CanClose) return;

        var confirmation = MessageBox.Show(
            Window.GetWindow(this),
            $"Tutup periode {preflight.PeriodName} sampai {preflight.CutoffDate:dd/MM/yyyy}?\n\n" +
            $"{preflight.Documents.Total:N0} dokumen akan dikunci.\n" +
            $"Saldo awal periode berikutnya: {preflight.OpeningDate:dd/MM/yyyy}.\n\n" +
            "Setelah diproses, transaksi pada periode ini tidak dapat dibuat, diubah, dihapus, dibatalkan, atau dipulihkan.",
            "Konfirmasi Tutup Buku",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes) return;

        try
        {
            SetBusy(true, "Menyimpan snapshot dan mengunci periode...");
            _pendingIdempotencyKey ??= Guid.NewGuid().ToString("N");
            var result = await _api.ClosePeriodAsync(new(
                preflight.CutoffDate,
                preflight.OpeningDate,
                preflight.PeriodName,
                _backupLocation.Text.Trim(),
                _archiveLocation.Text.Trim(),
                _pendingIdempotencyKey,
                Environment.MachineName));
            _pendingIdempotencyKey = null;

            ExportPaths? savedFiles = null;
            string? exportError = null;
            try { savedFiles = await SaveExportAsync(result); }
            catch (Exception exception) { exportError = exception.Message; }
            await ReloadHistoryAsync(result.Id);
            _status.Text = exportError is null
                ? $"Periode {result.PeriodName} berhasil ditutup. Dua file audit tersimpan."
                : $"Periode berhasil ditutup; file backup perlu diekspor ulang. {exportError}";
            MessageBox.Show(
                Window.GetWindow(this),
                BuildSuccessMessage(result, savedFiles, exportError),
                "Tutup Buku Berhasil",
                MessageBoxButton.OK,
                exportError is null ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            _status.Text = "Tutup Buku gagal. Tidak ada perubahan parsial yang disimpan.";
            MessageBox.Show(Window.GetWindow(this), exception.Message, "Tutup Buku Gagal", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    private async Task ExportSelectedAsync()
    {
        if (_history.SelectedItem is not PeriodCloseDto selected)
        {
            MessageBox.Show(Window.GetWindow(this), "Pilih histori Tutup Buku yang ingin diekspor.", "Ekspor Tutup Buku", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            SetBusy(true, "Mengekspor snapshot...");
            var paths = await SaveExportAsync(selected);
            _status.Text = "Backup total dan arsip sampai cutoff berhasil diekspor.";
            MessageBox.Show(Window.GetWindow(this), $"Dua file audit berhasil dibuat:\n\nBackup total:\n{paths.TotalBackup}\n\nArsip s/d cutoff:\n{paths.ThroughCutoffArchive}", "Ekspor Tutup Buku", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(Window.GetWindow(this), exception.Message, "Ekspor Tutup Buku", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    private async Task DepostPeriodAsync(PeriodCloseDto selected)
    {
        _history.SelectedItem = selected;
        if (!_api.Can("MAINTENANCE", "MANAGE"))
        {
            MessageBox.Show(Window.GetWindow(this), "Pengguna ini tidak memiliki hak untuk membatalkan Tutup Buku.", "Batal Tutup Buku", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!CanDepost(selected))
        {
            MessageBox.Show(Window.GetWindow(this), "Tutup Buku ini tidak dapat dideposting. Hanya periode aktif paling akhir yang dapat dibuka kembali.", "Deposting", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirmation = MessageBox.Show(
            Window.GetWindow(this),
            $"Apakah Anda yakin ingin membatalkan Tutup Buku periode sampai {selected.CutoffDate:dd/MM/yyyy}? Transaksi pada periode tersebut akan dapat diubah kembali.",
            "Konfirmasi Batal Tutup Buku",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes) return;

        try
        {
            SetBusy(true, "Membatalkan Tutup Buku paling akhir...");
            var reopened = await _api.ReopenPeriodCloseAsync(selected.Id, new(
                "Dibatalkan melalui form Tutup Buku.",
                Environment.MachineName));
            await ReloadHistoryAsync(reopened.Id);
            _status.Text = $"Tutup Buku sampai {reopened.CutoffDate:dd/MM/yyyy} berhasil dibatalkan. Periode tersebut dapat dimutasi kembali.";
            MessageBox.Show(
                Window.GetWindow(this),
                $"Tutup Buku periode sampai {reopened.CutoffDate:dd/MM/yyyy} berhasil dibatalkan.\n\nSnapshot dan arsip audit tetap tersimpan.",
                "Batal Tutup Buku Berhasil",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            _status.Text = "Batal Tutup Buku gagal. Tidak ada perubahan parsial yang disimpan.";
            MessageBox.Show(Window.GetWindow(this), exception.Message, "Batal Tutup Buku Gagal", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    private bool CanDepost(PeriodCloseDto periodClose) =>
        _api.Can("MAINTENANCE", "MANAGE")
        && periodClose.CanReopen
        && periodClose.Status.Equals("POSTED", StringComparison.OrdinalIgnoreCase);

    private async Task<ExportPaths> SaveExportAsync(PeriodCloseDto periodClose)
    {
        var bundle = await _api.GetPeriodCloseExportAsync(periodClose.Id);
        var backupDirectory = string.IsNullOrWhiteSpace(_backupLocation.Text) ? periodClose.BackupLocation : _backupLocation.Text.Trim();
        var archiveDirectory = string.IsNullOrWhiteSpace(_archiveLocation.Text) ? periodClose.ArchiveLocation : _archiveLocation.Text.Trim();
        var backupPath = await SaveVerifiedFileAsync(bundle.TotalBackup, backupDirectory, "backup total");
        var archivePath = await SaveVerifiedFileAsync(bundle.ThroughCutoffArchive, archiveDirectory, "arsip sampai cutoff");
        return new(backupPath, archivePath);
    }

    private static async Task<string> SaveVerifiedFileAsync(PeriodCloseExportFileDto export, string directory, string label)
    {
        if (Path.GetFileName(export.FileName) != export.FileName)
            throw new InvalidOperationException($"Nama file {label} dari server tidak valid.");
        var bytes = Encoding.UTF8.GetBytes(export.Content);
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!actualHash.Equals(export.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Hash file {label} tidak cocok dengan histori Tutup Buku.");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, export.FileName);
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, bytes);
        File.Move(temporary, path, true);
        return path;
    }

    private async Task ReloadHistoryAsync(Guid? selectedId = null)
    {
        try
        {
            selectedId ??= (_history.SelectedItem as PeriodCloseDto)?.Id;
            var rows = await _api.GetPeriodClosesAsync();
            _history.ItemsSource = rows;
            if (selectedId is Guid id) _history.SelectedItem = rows.FirstOrDefault(x => x.Id == id);
            _status.Text = rows.Count == 0 ? "Belum ada histori Tutup Buku." : $"{rows.Count:N0} periode tersimpan.";
        }
        catch (Exception exception)
        {
            _status.Text = "Histori Tutup Buku gagal dimuat.";
            MessageBox.Show(Window.GetWindow(this), exception.Message, "Tutup Buku", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowPreflight(PeriodClosePreflightDto result)
    {
        var d = result.Documents;
        var c = result.CarryForward;
        _summary.Text =
            $"Dokumen termasuk jurnal: {d.Total:N0}\n" +
            $"Jurnal {d.Journals:N0}  |  Kas/Bank {d.CashBankTransactions:N0}  |  Pembelian/Retur {d.Purchases + d.PurchaseReturns:N0}  |  Penjualan/Retur {d.Sales + d.SaleReturns:N0}\n" +
            $"Pembayaran/Penerimaan {d.SupplierPayments + d.CustomerReceipts:N0}  |  Cek/Giro {d.CheckGiros:N0}  |  Dokumen saldo {d.PartnerBalanceDocuments:N0}\n\n" +
            $"Akun ledger: {c.LedgerAccounts:N0}  |  Debit {c.LedgerDebit:N2}  |  Kredit {c.LedgerCredit:N2}\n" +
            $"Kas/Bank: {c.CashBankBalance:N2}\n" +
            $"Utang: {c.SupplierOutstanding:N2} ({c.SupplierBalances:N0} dokumen)\n" +
            $"Piutang: {c.CustomerOutstanding:N2} ({c.CustomerBalances:N0} dokumen)\n" +
            $"Stok: {c.InventoryQuantity:N3}  |  Nilai {c.InventoryValue:N2}  |  Layer FIFO {c.InventoryCostLayers:N0}";
        _blockers.Text = result.CanClose
            ? "LULUS\nJurnal seimbang, tidak ada draft penghambat, dan snapshot persediaan konsisten."
            : string.Join("\n", result.Blockers.Select(x => "- " + x));
        _blockers.Foreground = result.CanClose ? new SolidColorBrush(Color.FromRgb(35, 107, 62)) : Brushes.Firebrick;
    }

    private bool TryReadSelection(out PeriodClosePreflightRequest request)
    {
        request = default!;
        if (_cutoff.SelectedDate is not DateTime cutoff || _opening.SelectedDate is not DateTime opening)
        {
            MessageBox.Show(Window.GetWindow(this), "Tanggal akhir dan tanggal saldo awal baru wajib diisi.", "Tutup Buku", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        if (string.IsNullOrWhiteSpace(_periodName.Text) || string.IsNullOrWhiteSpace(_backupLocation.Text) || string.IsNullOrWhiteSpace(_archiveLocation.Text))
        {
            MessageBox.Show(Window.GetWindow(this), "Nama periode dan kedua lokasi output wajib diisi.", "Tutup Buku", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        request = new(DateOnly.FromDateTime(cutoff), DateOnly.FromDateTime(opening), _periodName.Text.Trim());
        return true;
    }

    private void BrowseLocation(TextBox target, string title)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            InitialDirectory = Directory.Exists(target.Text) ? target.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) target.Text = dialog.FolderName;
    }

    private void SetBusy(bool busy, string? status = null)
    {
        IsEnabled = !busy;
        if (!busy) _closePeriod.IsEnabled = _api.Can("MAINTENANCE", "MANAGE");
        if (!string.IsNullOrWhiteSpace(status)) _status.Text = status;
    }

    private static string BuildSuccessMessage(PeriodCloseDto result, ExportPaths? paths, string? exportError)
    {
        var c = result.CarryForward;
        var text =
            $"Periode: {result.PeriodName}\n" +
            $"Ditutup sampai: {result.CutoffDate:dd/MM/yyyy}\n" +
            $"Tanggal saldo awal baru: {result.OpeningDate:dd/MM/yyyy}\n\n" +
            $"Akun dibawa: {c.LedgerAccounts:N0}\n" +
            $"Utang: {c.SupplierOutstanding:N2}\n" +
            $"Piutang: {c.CustomerOutstanding:N2}\n" +
            $"Stok: {c.InventoryQuantity:N3} senilai {c.InventoryValue:N2}\n" +
            $"Hash: {result.SnapshotSha256}";
        if (paths is not null) return text + $"\n\nBackup total:\n{paths.TotalBackup}\n\nArsip s/d cutoff:\n{paths.ThroughCutoffArchive}";
        return text + $"\n\nPeriode sudah terkunci, tetapi file belum tersimpan:\n{exportError}\nGunakan tombol Ekspor Ulang.";
    }

    private sealed record ExportPaths(string TotalBackup, string ThroughCutoffArchive);

    private static TextBox Box(bool readOnly = false) => new()
    {
        Height = 26,
        MinWidth = 160,
        IsReadOnly = readOnly,
        Background = readOnly ? PartnerUi.ReadOnlyBrush : Brushes.White,
        BorderBrush = PartnerUi.BorderBrush,
        Padding = new Thickness(6, 2, 6, 2),
        VerticalContentAlignment = VerticalAlignment.Center
    };

    private static DatePicker DateInput() => new()
    {
        Height = 26,
        MinWidth = 150,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        BorderBrush = PartnerUi.BorderBrush
    };

    private static void AddField(Grid grid, string label, FrameworkElement control, int row, int column, int span = 1)
    {
        var text = new TextBlock
        {
            Text = label,
            Foreground = PartnerUi.TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetRow(text, row);
        Grid.SetColumn(text, column);
        grid.Children.Add(text);
        control.Margin = new Thickness(0, 3, column == 0 ? 24 : 0, 3);
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column + 1);
        Grid.SetColumnSpan(control, span);
        grid.Children.Add(control);
    }

    private static DataGridTextColumn Column(string header, string path, double width, string? format = null, Style? style = null) =>
        new() { Header = header, Binding = new Binding(path) { StringFormat = format }, Width = width, ElementStyle = style };

    private static DataGridTextColumn Column(string header, string path, DataGridLength width, string? format = null, Style? style = null) =>
        new() { Header = header, Binding = new Binding(path) { StringFormat = format }, Width = width, ElementStyle = style };

    private static Style RightStyle()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
        style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 5, 0)));
        return style;
    }

    private sealed class DepostingActionColumn : DataGridColumn
    {
        private readonly Func<PeriodCloseDto, bool> _canExecute;
        private readonly Func<PeriodCloseDto, Task> _execute;

        public DepostingActionColumn(
            Func<PeriodCloseDto, bool> canExecute,
            Func<PeriodCloseDto, Task> execute)
        {
            _canExecute = canExecute;
            _execute = execute;
        }

        protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem) =>
            CreateButton(dataItem);

        protected override FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem) =>
            CreateButton(dataItem);

        private FrameworkElement CreateButton(object dataItem)
        {
            if (dataItem is not PeriodCloseDto periodClose) return new Border();

            var button = PartnerUi.Button(
                "Deposting",
                94,
                PartnerButtonKind.Standard,
                tooltip: "Buka kembali periode Tutup Buku ini");
            button.Height = 28;
            button.Margin = new Thickness(4, 3, 4, 3);
            button.HorizontalAlignment = HorizontalAlignment.Center;
            button.VerticalAlignment = VerticalAlignment.Center;
            button.IsEnabled = _canExecute(periodClose);
            button.Click += async (_, _) => await _execute(periodClose);
            return button;
        }
    }
}
