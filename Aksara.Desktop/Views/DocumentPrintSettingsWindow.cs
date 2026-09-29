using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Aksara.Desktop.Services;
using Microsoft.Win32;

namespace Aksara.Desktop.Views;

internal enum PrintButtonKind { Standard, Primary, Gold, Danger, Tool }

internal sealed class DocumentPrintSettingsWindow : Window
{
    private readonly ComboBox _documents = new() { Height = 30, DisplayMemberPath = nameof(PrintDocumentKind.DisplayName) };
    private readonly TextBox _company = new() { Height = 28, MaxLength = 160, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ComboBox _letterhead = new() { Height = 28, ItemsSource = new[] { "Teks perusahaan", "Gambar kop" } };
    private readonly TextBox _minimumRows = new() { Width = 68, Height = 28, MaxLength = 2, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Slider _imageHeight = new() { Minimum = 40, Maximum = 160, TickFrequency = 10, IsSnapToTickEnabled = true };
    private readonly TextBlock _imageName = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontSize = 11 };
    private readonly TextBlock _heightValue = new() { Width = 42, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    private readonly DocumentViewer _preview = new() { Background = PrintUi.WorkspaceBrush };
    private readonly Dictionary<PrintTemplate, (RadioButton Button, Image Image, Border Card)> _templates = [];
    private readonly TabControl _settingsTabs = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly PrintSignatureImage?[] _signatures = new PrintSignatureImage?[3];
    private readonly Image[] _signaturePreviews = new Image[3];
    private readonly TextBlock[] _signatureNames = new TextBlock[3];
    private readonly Button[] _signatureRemoveButtons = new Button[3];
    private readonly TextBox[] _signatureSignerNames =
    [
        new() { Height = 28, MaxLength = 80, VerticalContentAlignment = VerticalAlignment.Center },
        new() { Height = 28, MaxLength = 80, VerticalContentAlignment = VerticalAlignment.Center }
    ];
    private readonly DocumentPrintFormatStore _store;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private DocumentPrintFormat _saved = DocumentPrintFormat.Default();
    private string _key;
    private string? _imageData;
    private string? _imageFileName;
    private PrintHeaderLayout? _headerLayout;
    private readonly StackPanel _imageButtons = new() { Orientation = Orientation.Horizontal };
    private bool _loading;

    public DocumentPrintSettingsWindow(string? documentKey = null, DocumentPrintFormatStore? store = null)
    {
        _store = store ?? DocumentPrintFormatStore.Shared;
        _key = documentKey ?? "sales-invoice";
        _ = DocumentPrintFormats.Get(_key);
        Title = "Format Cetak Dokumen";
        Width = 1120;
        Height = 780;
        MinWidth = 900;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Tahoma");
        FontSize = 11.5;
        Background = PrintUi.CanvasBrush;
        Icon = new BitmapImage(new Uri("pack://application:,,,/AKSARA;component/Assets/aksara.ico"));

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.Children.Add(PrintUi.Glyph("\uE749", 18, Brushes.White));
        var title = new TextBlock
        {
            Text = "FORMAT CETAK DOKUMEN",
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 1);
        heading.Children.Add(title);
        root.Children.Add(new Border
        {
            Height = 50,
            Background = PrintUi.MaroonBrush,
            BorderBrush = PrintUi.MaroonDarkBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(0, 0, 0, 12),
            Child = heading
        });

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(350) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        var settingsPanel = new Grid();
        settingsPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        settingsPanel.RowDefinitions.Add(new RowDefinition());
        body.Children.Add(PrintUi.Card(settingsPanel, new Thickness(12)));
        var documentPicker = new StackPanel();
        Label(documentPicker, "Jenis Dokumen");
        _documents.ItemsSource = DocumentPrintFormats.Documents;
        documentPicker.Children.Add(_documents);
        settingsPanel.Children.Add(documentPicker);
        Grid.SetRow(_settingsTabs, 1);
        _settingsTabs.ItemContainerStyle = PrintUi.TabItemStyle;
        settingsPanel.Children.Add(_settingsTabs);

        var form = new StackPanel { Margin = new Thickness(3, 9, 3, 6) };
        var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _settingsTabs.Items.Add(new TabItem { Header = "Dokumen", Content = scroll });
        Label(form, "Template");
        var templates = new UniformGrid { Columns = 3, Height = 140 };
        foreach (var template in Enum.GetValues<PrintTemplate>())
        {
            var image = new Image { Width = 68, Height = 96, Stretch = Stretch.Uniform };
            var radioContent = new StackPanel();
            radioContent.Children.Add(image);
            radioContent.Children.Add(new TextBlock { Text = DocumentPrintFormats.TemplateName(template), HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0), FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center });
            var card = PrintUi.Card(radioContent, new Thickness(5), background: PrintUi.SurfaceBrush);
            var radio = new RadioButton { GroupName = "Template", Content = card, Tag = template, Margin = new Thickness(3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top,
                ToolTip = $"Gunakan template {DocumentPrintFormats.TemplateName(template)}" };
            radio.Checked += (_, _) => { UpdateTemplateCards(); Changed(); };
            _templates.Add(template, (radio, image, card));
            templates.Children.Add(radio);
        }
        form.Children.Add(templates);

        Label(form, "Kop Dokumen", 8);
        form.Children.Add(_letterhead);
        Label(form, "Nama Perusahaan", 10);
        form.Children.Add(_company);
        Label(form, "Gambar Kop (PNG / JPG / BMP)", 10);
        var imageButtons = _imageButtons;
        imageButtons.Children.Add(Button("Pilih Gambar", 132, ImportImage, glyph: "\uE91B", tooltip: "Pilih gambar kop"));
        imageButtons.Children.Add(Button("Hapus", 92, () =>
        {
            _imageData = null;
            _imageFileName = null;
            _letterhead.SelectedIndex = 0;
            Changed();
        }, PrintButtonKind.Danger, "\uE74D", "Hapus gambar kop"));
        form.Children.Add(imageButtons);
        form.Children.Add(_imageName);
        Label(form, "Tinggi Kop", 10);
        var height = new DockPanel();
        DockPanel.SetDock(_heightValue, Dock.Right);
        height.Children.Add(_heightValue);
        height.Children.Add(_imageHeight);
        form.Children.Add(height);
        Label(form, "Minimal Baris Rincian", 10);
        _minimumRows.HorizontalAlignment = HorizontalAlignment.Left;
        form.Children.Add(_minimumRows);

        var signatures = new StackPanel { Margin = new Thickness(3, 9, 3, 6) };
        var settings = ProgramConfigurationCache.Current;
        var signatureLabels = new[] { settings.Signature1, settings.Signature2, settings.Signature3 };
        for (var index = 0; index < _signatures.Length; index++)
        {
            var slot = index;
            var label = string.IsNullOrWhiteSpace(signatureLabels[index]) ? $"Tanda Tangan {index + 1}" : signatureLabels[index];
            var signaturePanel = new StackPanel();
            Label(signaturePanel, label);
            if (slot < _signatureSignerNames.Length)
            {
                signaturePanel.Children.Add(new TextBlock { Text = "Nama Penandatangan", FontSize = 10.5,
                    Foreground = PrintUi.MutedBrush, Margin = new Thickness(0, 0, 0, 4) });
                _signatureSignerNames[slot].Margin = new Thickness(0, 0, 0, 8);
                _signatureSignerNames[slot].TextChanged += (_, _) => Changed();
                signaturePanel.Children.Add(_signatureSignerNames[slot]);
            }
            else
            {
                signaturePanel.Children.Add(new TextBlock { Text = "Nama penerima diisi manual.", FontSize = 10.5,
                    FontStyle = FontStyles.Italic, Foreground = PrintUi.MutedBrush, Margin = new Thickness(0, 0, 0, 8) });
            }
            _signaturePreviews[index] = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(5) };
            signaturePanel.Children.Add(new Border { Height = 68, Background = PrintUi.PreviewBrush, BorderBrush = PrintUi.BorderBrush,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = _signaturePreviews[index] });
            _signatureNames[index] = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11,
                Foreground = PrintUi.MutedBrush, Margin = new Thickness(0, 5, 0, 7) };
            signaturePanel.Children.Add(_signatureNames[index]);
            var commandsRow = new StackPanel { Orientation = Orientation.Horizontal };
            commandsRow.Children.Add(Button("Pilih Gambar", 132, () => ImportSignatureImage(slot), glyph: "\uE91B", tooltip: $"Pilih gambar {label}"));
            _signatureRemoveButtons[index] = Button("Hapus", 92, () => SetSignatureImage(slot, null), PrintButtonKind.Danger, "\uE74D", $"Hapus gambar {label}");
            commandsRow.Children.Add(_signatureRemoveButtons[index]);
            signaturePanel.Children.Add(commandsRow);
            signatures.Children.Add(PrintUi.Card(signaturePanel, new Thickness(9), new Thickness(0, 0, 0, 8), PrintUi.SurfaceBrush));
        }
        _settingsTabs.Items.Add(new TabItem { Header = "Tanda Tangan", Content = new ScrollViewer { Content = signatures,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } });

        var previewPanel = new Grid();
        previewPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        previewPanel.RowDefinitions.Add(new RowDefinition());
        var previewHeader = new Grid();
        previewHeader.ColumnDefinitions.Add(new ColumnDefinition());
        previewHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        previewHeader.Children.Add(new TextBlock { Text = "PRATINJAU DATA CONTOH", FontWeight = FontWeights.Bold,
            Foreground = PrintUi.MaroonBrush, VerticalAlignment = VerticalAlignment.Center });
        var editHeader = Button("Edit Kop & Tata Letak", 178, EditHeader, PrintButtonKind.Gold, "\uE70F", "Buka editor kop dan tata letak");
        Grid.SetColumn(editHeader, 1);
        previewHeader.Children.Add(editHeader);
        previewPanel.Children.Add(PrintUi.Card(previewHeader, new Thickness(10, 7, 10, 7), new Thickness(0, 0, 0, 8)));
        var previewFrame = new Border { Background = PrintUi.WorkspaceBrush, BorderBrush = PrintUi.BorderBrush,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(4), Child = _preview };
        Grid.SetRow(previewFrame, 1);
        previewPanel.Children.Add(previewFrame);
        Grid.SetColumn(previewPanel, 2);
        body.Children.Add(previewPanel);

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var commands = new StackPanel { Orientation = Orientation.Horizontal };
        commands.Children.Add(Button("Bawaan", 98, () => Apply(DocumentPrintFormat.Default()), glyph: "\uE777", tooltip: "Kembalikan ke pengaturan bawaan"));
        commands.Children.Add(Button("Simpan", 104, () => Save(), PrintButtonKind.Primary, "\uE74E", "Simpan format dokumen"));
        commands.Children.Add(Button("Tutup", 92, Close));
        DockPanel.SetDock(commands, Dock.Right);
        footer.Children.Add(commands);
        footer.Children.Add(_status);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;

        _documents.SelectionChanged += SelectDocument;
        _settingsTabs.SelectionChanged += (_, args) =>
        {
            if (_loading || !ReferenceEquals(args.Source, _settingsTabs)) return;
            RefreshPreview();
            if (_settingsTabs.SelectedIndex == 0) _preview.FitToWidth();
        };
        _company.TextChanged += (_, _) => Changed();
        _minimumRows.TextChanged += (_, _) => Changed();
        _letterhead.SelectionChanged += (_, _) => Changed();
        _imageHeight.ValueChanged += (_, _) => Changed();
        _refresh.Tick += (_, _) => { _refresh.Stop(); RefreshPreview(); };
        Closing += OnClosing;
        Closed += (_, _) => _refresh.Stop();
        LoadDocument();
        Loaded += (_, _) => { RefreshPreview(); _preview.FitToWidth(); };
    }

    private void LoadDocument()
    {
        _saved = _store.Load(_key, out var warning);
        _loading = true;
        _documents.SelectedItem = DocumentPrintFormats.Get(_key);
        _loading = false;
        Apply(_saved);
        Status(warning ?? "Pengaturan tersimpan di perangkat ini.", warning is not null);
    }

    private void Apply(DocumentPrintFormat format)
    {
        _loading = true;
        _company.Text = format.CompanyName;
        _letterhead.SelectedIndex = (int)format.Letterhead;
        _minimumRows.Text = format.MinimumRows.ToString();
        _imageHeight.Value = format.ImageHeight;
        _imageData = format.ImageBase64;
        _imageFileName = format.ImageName;
        _headerLayout = format.HeaderLayout;
        _signatureSignerNames[0].Text = format.Signature1Name ?? "";
        _signatureSignerNames[1].Text = format.Signature2Name ?? "";
        SetSignatureImage(0, format.Signature1Image);
        SetSignatureImage(1, format.Signature2Image);
        SetSignatureImage(2, format.Signature3Image);
        _templates[format.Template].Button.IsChecked = true;
        _loading = false;
        Changed();
    }

    private DocumentPrintFormat Read()
    {
        if (!int.TryParse(_minimumRows.Text, out var rows)) throw new InvalidDataException("Minimal baris harus berupa angka 1 sampai 60.");
        return new()
        {
            Template = _templates.First(x => x.Value.Button.IsChecked == true).Key,
            CompanyName = _company.Text.Trim(), Letterhead = (LetterheadKind)_letterhead.SelectedIndex,
            MinimumRows = rows, ImageHeight = (int)_imageHeight.Value,
            ImageBase64 = _imageData, ImageName = _imageFileName,
            Signature1Image = _signatures[0], Signature2Image = _signatures[1], Signature3Image = _signatures[2],
            Signature1Name = string.IsNullOrWhiteSpace(_signatureSignerNames[0].Text) ? null : _signatureSignerNames[0].Text.Trim(),
            Signature2Name = string.IsNullOrWhiteSpace(_signatureSignerNames[1].Text) ? null : _signatureSignerNames[1].Text.Trim(),
            HeaderLayout = _headerLayout
        };
    }

    private bool Dirty()
    {
        try { return Read() != _saved; }
        catch (InvalidDataException) { return true; }
    }

    private void Changed()
    {
        if (_loading) return;
        UpdateTemplateCards();
        _company.IsEnabled = _headerLayout is null && _letterhead.SelectedIndex == 0;
        _imageHeight.IsEnabled = _headerLayout is null && _letterhead.SelectedIndex == 1;
        _letterhead.IsEnabled = _imageButtons.IsEnabled = _headerLayout is null;
        _imageName.Text = _imageFileName ?? "Belum ada gambar";
        _heightValue.Text = ((int)_imageHeight.Value).ToString();
        _refresh.Stop();
        _refresh.Start();
        Status(Dirty() ? "Perubahan belum disimpan." : "Pengaturan tersimpan di perangkat ini.");
    }

    private void UpdateTemplateCards()
    {
        foreach (var controls in _templates.Values)
        {
            var selected = controls.Button.IsChecked == true;
            controls.Card.Background = selected ? PrintUi.GoldSoftBrush : PrintUi.SurfaceBrush;
            controls.Card.BorderBrush = selected ? PrintUi.GoldBrush : PrintUi.BorderBrush;
            controls.Card.BorderThickness = new Thickness(selected ? 1.5 : 1);
        }
    }

    private void RefreshPreview()
    {
        try
        {
            var format = Read();
            DocumentPrintFormatStore.Validate(format);
            var sample = DocumentPrintRenderer.Sample(_key);
            _preview.Document = DocumentPrintRenderer.Build(sample, format: format);
            foreach (var (template, controls) in _templates)
            {
                var document = DocumentPrintRenderer.Build(sample, format: format with { Template = template });
                var page = document.Pages[0].Child;
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen())
                    context.DrawRectangle(new VisualBrush(page) { Stretch = Stretch.Fill }, null, new Rect(0, 0, 180, 254));
                var bitmap = new RenderTargetBitmap(180, 254, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(drawing);
                bitmap.Freeze();
                controls.Image.Source = bitmap;
            }
            if (_settingsTabs.SelectedIndex == 1)
            {
                _preview.FitToHeight();
                _preview.LastPage();
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException or NotSupportedException)
        {
            _preview.Document = null;
            Status(exception.Message, true);
        }
    }

    private void ImportImage()
    {
        var picker = new OpenFileDialog { Title = "Pilih Gambar Kop", Filter = "Gambar kop|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var data = DocumentPrintFormatStore.ImportImage(picker.FileName);
            _imageData = data;
            _imageFileName = Path.GetFileName(picker.FileName);
            _imageHeight.Value = 80;
            _letterhead.SelectedIndex = 1;
            Changed();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            MessageBox.Show(this, exception.Message, "Gambar Kop", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void EditHeader()
    {
        try
        {
            var format = Read();
            DocumentPrintFormatStore.Validate(format);
            var editor = new PrintHeaderEditorWindow(DocumentPrintRenderer.Sample(_key), format) { Owner = this };
            editor.ShowDialog();
            if (editor.Result is { } edited) Apply(edited);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException or NotSupportedException)
        {
            Status(exception.Message, true);
        }
    }

    private void ImportSignatureImage(int slot)
    {
        var picker = new OpenFileDialog { Title = $"Pilih Gambar Tanda Tangan {slot + 1}",
            Filter = "Gambar tanda tangan|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            SetSignatureImage(slot, new PrintSignatureImage(DocumentPrintFormatStore.ImportImage(picker.FileName), Path.GetFileName(picker.FileName)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            MessageBox.Show(this, exception.Message, "Gambar Tanda Tangan", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SetSignatureImage(int slot, PrintSignatureImage? signature)
    {
        var preview = signature is null ? null : DocumentPrintFormatStore.DecodeImage(signature.ImageBase64);
        _signatures[slot] = signature;
        _signaturePreviews[slot].Source = preview;
        _signatureNames[slot].Text = signature?.ImageName ?? "Tanpa gambar";
        _signatureNames[slot].ToolTip = signature?.ImageName;
        _signatureRemoveButtons[slot].IsEnabled = signature is not null;
        Changed();
    }

    private bool Save()
    {
        try
        {
            var format = Read();
            _store.Save(_key, format);
            _saved = format;
            Status($"Format {DocumentPrintFormats.Get(_key).Label} disimpan di perangkat ini.");
            RefreshPreview();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            Status(exception.Message, true);
            MessageBox.Show(this, exception.Message, "Format Belum Disimpan", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private bool CanLeave()
    {
        if (!Dirty()) return true;
        var choice = MessageBox.Show(this, "Simpan perubahan format dokumen ini?\nYa: simpan. Tidak: buang perubahan. Batal: lanjut mengedit.",
            "Format Cetak Dokumen", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return choice == MessageBoxResult.No || choice == MessageBoxResult.Yes && Save();
    }

    private void SelectDocument(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _documents.SelectedItem is not PrintDocumentKind kind || kind.Key == _key) return;
        if (!CanLeave())
        {
            _loading = true;
            _documents.SelectedItem = DocumentPrintFormats.Get(_key);
            _loading = false;
            return;
        }
        _key = kind.Key;
        LoadDocument();
    }

    private void OnClosing(object? sender, CancelEventArgs e) => e.Cancel = !CanLeave();

    private void Status(string message, bool error = false)
    {
        _status.Text = message;
        _status.Foreground = error ? Brushes.Firebrick : Brushes.DimGray;
    }

    private static void Label(Panel panel, string text, double top = 0) => panel.Children.Add(new TextBlock
    {
        Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 6)
    });

    private static Button Button(string caption, double width, Action action, PrintButtonKind kind = PrintButtonKind.Standard,
        string? glyph = null, string? tooltip = null) => PrintUi.Button(caption, width, action, kind, glyph, tooltip);
}

internal static class PrintUi
{
    public static readonly SolidColorBrush MaroonDarkBrush = Brush("#5C1018");
    public static readonly SolidColorBrush MaroonBrush = Brush("#7C1622");
    public static readonly SolidColorBrush GoldBrush = Brush("#D6A22E");
    public static readonly SolidColorBrush GoldSoftBrush = Brush("#FFF4D2");
    public static readonly SolidColorBrush CanvasBrush = Brush("#F5F1F2");
    public static readonly SolidColorBrush SurfaceBrush = Brush("#FFFDFD");
    public static readonly SolidColorBrush WorkspaceBrush = Brush("#E8E4E5");
    public static readonly SolidColorBrush PreviewBrush = Brush("#FAF7F8");
    public static readonly SolidColorBrush BorderBrush = Brush("#D8C9CC");
    public static readonly SolidColorBrush TextBrush = Brush("#2C2022");
    public static readonly SolidColorBrush MutedBrush = Brush("#705D61");

    public static readonly Style TabItemStyle = CreateTabItemStyle();
    private static readonly Style StandardButtonStyle = CreateButtonStyle(SurfaceBrush, BorderBrush, MaroonDarkBrush, GoldSoftBrush, GoldBrush);
    private static readonly Style PrimaryButtonStyle = CreateButtonStyle(MaroonBrush, MaroonDarkBrush, Brushes.White, Brush("#941E2E"), MaroonDarkBrush);
    private static readonly Style GoldButtonStyle = CreateButtonStyle(GoldSoftBrush, GoldBrush, MaroonDarkBrush, Brush("#FBE6A8"), Brush("#B98516"));
    private static readonly Style DangerButtonStyle = CreateButtonStyle(Brush("#FFF7F7"), Brush("#E4B9BF"), Brush("#A6192E"), Brush("#FBE8EA"), Brush("#C76A77"));
    private static readonly Style ToolButtonStyle = CreateButtonStyle(SurfaceBrush, BorderBrush, MaroonDarkBrush, GoldSoftBrush, GoldBrush, 4);

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

    public static TextBlock Glyph(string glyph, double size = 15, Brush? foreground = null) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        FontSize = size,
        Foreground = foreground ?? MaroonDarkBrush,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    public static Border ToolbarSeparator() => new()
    {
        Width = 1,
        Height = 20,
        Background = BorderBrush,
        Margin = new Thickness(3, 4, 9, 4)
    };

    public static Button Button(string caption, double width, Action action, PrintButtonKind kind = PrintButtonKind.Standard,
        string? glyph = null, string? tooltip = null)
    {
        var button = new Button
        {
            Width = width,
            Height = kind == PrintButtonKind.Tool ? 30 : 30,
            MinWidth = width,
            Margin = new Thickness(0, 0, 7, 0),
            Padding = kind == PrintButtonKind.Tool ? new Thickness(0) : new Thickness(9, 0, 9, 0),
            Style = kind switch
            {
                PrintButtonKind.Primary => PrimaryButtonStyle,
                PrintButtonKind.Gold => GoldButtonStyle,
                PrintButtonKind.Danger => DangerButtonStyle,
                PrintButtonKind.Tool => ToolButtonStyle,
                _ => StandardButtonStyle
            },
            ToolTip = tooltip
        };
        if (glyph is null)
        {
            button.Content = caption;
        }
        else if (string.IsNullOrWhiteSpace(caption))
        {
            button.Content = Glyph(glyph, 15, button.Foreground);
        }
        else
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(Glyph(glyph, 14, button.Foreground));
            content.Children.Add(new TextBlock
            {
                Text = caption,
                Foreground = button.Foreground,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            });
            button.Content = content;
        }
        button.Click += (_, _) => action();
        return button;
    }

    private static Style CreateButtonStyle(Brush background, Brush border, Brush foreground, Brush hover, Brush pressed, double radius = 4)
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, background));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, border));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Tahoma")));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 11d));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));

        var template = new ControlTemplate(typeof(Button));
        var chrome = new FrameworkElementFactory(typeof(Border), "ButtonChrome");
        chrome.SetBinding(Border.BackgroundProperty, TemplateBinding("Background"));
        chrome.SetBinding(Border.BorderBrushProperty, TemplateBinding("BorderBrush"));
        chrome.SetBinding(Border.BorderThicknessProperty, TemplateBinding("BorderThickness"));
        chrome.SetBinding(Border.PaddingProperty, TemplateBinding("Padding"));
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
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

    private static Style CreateTabItemStyle()
    {
        var style = new Style(typeof(TabItem));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#F1EBED")));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, BorderBrush));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, MutedBrush));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 6, 12, 6)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 5, 0)));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));

        var template = new ControlTemplate(typeof(TabItem));
        var chrome = new FrameworkElementFactory(typeof(Border), "TabChrome");
        chrome.SetBinding(Border.BackgroundProperty, TemplateBinding("Background"));
        chrome.SetBinding(Border.BorderBrushProperty, TemplateBinding("BorderBrush"));
        chrome.SetBinding(Border.BorderThicknessProperty, TemplateBinding("BorderThickness"));
        chrome.SetBinding(Border.PaddingProperty, TemplateBinding("Padding"));
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(ContentPresenter.ContentProperty, TemplateBinding("Header"));
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        chrome.AppendChild(presenter);
        template.VisualTree = chrome;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, GoldSoftBrush));
        hover.Setters.Add(new Setter(Control.BorderBrushProperty, GoldBrush));
        template.Triggers.Add(hover);
        var selected = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, MaroonBrush));
        selected.Setters.Add(new Setter(Control.BorderBrushProperty, MaroonDarkBrush));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        template.Triggers.Add(selected);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        template.Triggers.Add(disabled);
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
