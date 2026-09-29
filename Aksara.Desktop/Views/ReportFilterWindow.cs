using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Aksara.Desktop.Views;

public sealed record ReportFilterSelection(DateOnly From, DateOnly To, string? Query, bool IncludeZero = false);

public sealed class ReportFilterWindow : Window
{
    private readonly RadioButton _all = new() { Content = "Semua periode", GroupName = "ReportPeriod" };
    private readonly RadioButton _range = new() { Content = "Periode tanggal", GroupName = "ReportPeriod", IsChecked = true };
    private readonly DatePicker _from = new() { Width = 145, Height = 31, SelectedDateFormat = DatePickerFormat.Short };
    private readonly DatePicker _to = new() { Width = 145, Height = 31, SelectedDateFormat = DatePickerFormat.Short };
    private readonly CheckBox _includeZero = new() { Content = "Tampilkan data/perkiraan bernilai kosong" };
    public ReportFilterSelection? Selection { get; private set; }

    public ReportFilterWindow(string reportTitle, bool allowIncludeZero = false)
    {
        Title = reportTitle;
        Width = 680;
        Height = allowIncludeZero ? 400 : 365;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Tahoma");
        FontSize = 11.5;
        Background = PrintUi.CanvasBrush;
        SnapsToDevicePixels = true;
        _from.SelectedDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        _to.SelectedDate = DateTime.Today;
        _includeZero.Visibility = allowIncludeZero ? Visibility.Visible : Visibility.Collapsed;
        ConfigureControls();
        Content = Build(reportTitle);
        _all.Checked += (_, _) => UpdatePeriodState();
        _range.Checked += (_, _) => UpdatePeriodState();
        UpdatePeriodState();
    }

    private UIElement Build(string title)
    {
        var root = new Grid { Margin = new Thickness(14), Background = PrintUi.CanvasBrush };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var headingLayout = new Grid();
        headingLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        headingLayout.ColumnDefinitions.Add(new ColumnDefinition());
        headingLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headingLayout.Children.Add(PrintUi.Glyph("\uE9D2", 18, Brushes.White));
        var heading = new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(heading, 1);
        headingLayout.Children.Add(heading);
        var badge = new Border
        {
            Background = PrintUi.GoldSoftBrush,
            BorderBrush = PrintUi.GoldBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "LAPORAN",
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = PrintUi.MaroonDarkBrush
            }
        };
        Grid.SetColumn(badge, 2);
        headingLayout.Children.Add(badge);
        root.Children.Add(new Border
        {
            Height = 56,
            Background = PrintUi.MaroonBrush,
            BorderBrush = PrintUi.MaroonDarkBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(0, 0, 0, 10),
            Child = headingLayout
        });

        var presetLayout = new Grid();
        presetLayout.ColumnDefinitions.Add(new ColumnDefinition());
        presetLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var presetLabel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        presetLabel.Children.Add(PrintUi.Glyph("\uE787", 14, PrintUi.GoldBrush));
        presetLabel.Children.Add(new TextBlock
        {
            Text = "Periode cepat",
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            Foreground = PrintUi.MaroonDarkBrush
        });
        presetLayout.Children.Add(presetLabel);
        var presets = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        presets.Children.Add(PresetButton("Hari Ini", DateTime.Today, DateTime.Today));
        presets.Children.Add(PresetButton("Bulan Ini", new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), DateTime.Today));
        var standard = PresetButton("Standar", new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), DateTime.Today);
        standard.Margin = new Thickness(0);
        presets.Children.Add(standard);
        Grid.SetColumn(presets, 1);
        presetLayout.Children.Add(presets);
        var presetCard = PrintUi.Card(presetLayout, new Thickness(11, 7, 11, 7), new Thickness(0, 0, 0, 10));
        Grid.SetRow(presetCard, 1);
        root.Children.Add(presetCard);

        var panel = new Grid { Margin = new Thickness(18, 14, 18, 14) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        panel.Children.Add(SectionHeading("Periode Laporan"));
        var periodOptions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        _all.Margin = new Thickness(0, 0, 28, 0);
        periodOptions.Children.Add(_all);
        periodOptions.Children.Add(_range);
        Grid.SetRow(periodOptions, 1);
        panel.Children.Add(periodOptions);

        var dates = new Grid { Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        dates.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        dates.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dates.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });
        dates.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dates.Children.Add(new TextBlock { Text = "Dari", VerticalAlignment = VerticalAlignment.Center, Foreground = PrintUi.MutedBrush });
        Grid.SetColumn(_from, 1);
        dates.Children.Add(_from);
        var separator = new TextBlock { Text = "s/d", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = PrintUi.MutedBrush };
        Grid.SetColumn(separator, 2);
        dates.Children.Add(separator);
        Grid.SetColumn(_to, 3);
        dates.Children.Add(_to);
        Grid.SetRow(dates, 2);
        panel.Children.Add(dates);
        _includeZero.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(_includeZero, 3);
        panel.Children.Add(_includeZero);
        var filterPanel = new Border
        {
            Background = PrintUi.SurfaceBrush,
            BorderBrush = PrintUi.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 0, 10),
            Child = panel
        };
        Grid.SetRow(filterPanel, 2);
        root.Children.Add(filterPanel);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var process = PrintUi.Button("Proses", 112, Process, PrintButtonKind.Primary, "\uE768", "Proses laporan");
        process.IsDefault = true;
        var cancel = PrintUi.Button("Batal", 100, () => DialogResult = false, PrintButtonKind.Standard, "\uE711", "Batalkan");
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0);
        footer.Children.Add(process);
        footer.Children.Add(cancel);
        var footerBorder = new Border
        {
            BorderBrush = PrintUi.BorderBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 10, 0, 0),
            Child = footer
        };
        Grid.SetRow(footerBorder, 3);
        root.Children.Add(footerBorder);
        return root;
    }

    private Button PresetButton(string text, DateTime from, DateTime to)
    {
        return PrintUi.Button(text, 88, () =>
        {
            _range.IsChecked = true;
            _from.SelectedDate = from;
            _to.SelectedDate = to;
        });
    }

    private void Process()
    {
        var from = _all.IsChecked == true ? new DateOnly(2000, 1, 1) : DateOnly.FromDateTime(_from.SelectedDate ?? DateTime.Today);
        var to = _all.IsChecked == true ? DateOnly.FromDateTime(DateTime.Today) : DateOnly.FromDateTime(_to.SelectedDate ?? DateTime.Today);
        if (to < from) { MessageBox.Show("Tanggal akhir tidak boleh lebih awal dari tanggal awal.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        Selection = new(from, to, null, _includeZero.IsChecked == true);
        DialogResult = true;
    }

    private void ConfigureControls()
    {
        foreach (var option in new[] { _all, _range })
        {
            option.Foreground = PrintUi.TextBrush;
            option.FontWeight = FontWeights.SemiBold;
            option.VerticalAlignment = VerticalAlignment.Center;
        }
        foreach (var picker in new[] { _from, _to })
        {
            picker.Background = PrintUi.SurfaceBrush;
            picker.BorderBrush = PrintUi.BorderBrush;
            picker.BorderThickness = new Thickness(1);
            picker.VerticalContentAlignment = VerticalAlignment.Center;
        }
        _includeZero.Foreground = PrintUi.TextBrush;
    }

    private void UpdatePeriodState()
    {
        var enabled = _range.IsChecked == true;
        _from.IsEnabled = enabled;
        _to.IsEnabled = enabled;
        _from.Opacity = enabled ? 1 : 0.5;
        _to.Opacity = enabled ? 1 : 0.5;
    }

    private static TextBlock SectionHeading(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        FontWeight = FontWeights.Bold,
        Foreground = PrintUi.MaroonDarkBrush
    };

}
