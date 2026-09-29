using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Aksara.Desktop.Services;
using Microsoft.Win32;

namespace Aksara.Desktop.Views;

internal sealed class PrintHeaderEditorWindow : Window
{
    private sealed record EditorState(PrintHeaderLayout Layout, string? Image, string? ImageName);
    private readonly PrintContent _sample;
    private readonly DocumentPrintFormat _original;
    private EditorState _state;
    private readonly Stack<EditorState> _undo = [];
    private readonly Stack<EditorState> _redo = [];
    private readonly Canvas _page = new() { Width = 793, Height = 1122, Background = Brushes.White };
    private readonly Canvas _canvas = new() { Width = 717, Background = Brushes.White };
    private readonly Border _paper = new() { Width = 793, Height = 1122 };
    private readonly ListBox _elements = new() { Height = 148, DisplayMemberPath = "Label", BorderBrush = PrintUi.BorderBrush };
    private readonly TextBox _text = new() { Height = 64, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 2000,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _source = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, FontSize = 11 };
    private readonly TextBox _x = Number(), _y = Number(), _width = Number(), _height = Number(), _size = Number();
    private readonly ComboBox _font = new() { ItemsSource = PrintHeaderLayout.Fonts, Height = 28 };
    private readonly ComboBox _alignment = new() { ItemsSource = new[] { "Kiri", "Tengah", "Kanan" }, Height = 28 };
    private readonly CheckBox _bold = new() { Content = "Tebal", Margin = new Thickness(0, 6, 0, 6) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _areaHeight = Number();
    private readonly ComboBox _fields = new() { Height = 28, DisplayMemberPath = "Label" };
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly Slider _zoom = new() { Minimum = 40, Maximum = 130, Value = 80, Width = 120, ToolTip = "Zoom" };
    private readonly Button _undoButton, _redoButton, _deleteButton;
    private string? _selected;
    private bool _loading, _accepted, _previewOnly;
    private TextBox? _inline;
    private EditorState? _gestureStart;
    private PrintHeaderElement? _gestureElement;
    public DocumentPrintFormat? Result { get; private set; }

    public PrintHeaderEditorWindow(PrintContent sample, DocumentPrintFormat format)
    {
        _sample = sample;
        _original = format;
        _state = new(format.HeaderLayout ?? PrintHeaderRenderer.CreateDefault(sample, format), format.ImageBase64, format.ImageName);
        Title = "Edit Kop & Tata Letak";
        Width = 1200; Height = 800; MinWidth = 900; MinHeight = 620;
        Background = PrintUi.CanvasBrush; FontFamily = new FontFamily("Tahoma"); FontSize = 11.5;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Icon = new BitmapImage(new Uri("pack://application:,,,/AKSARA;component/Assets/aksara.ico"));
        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });

        var toolbar = new DockPanel();
        var zoomValue = new TextBlock { Width = 42, TextAlignment = TextAlignment.Right, Foreground = PrintUi.MutedBrush,
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var zoom = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        zoom.Children.Add(PrintUi.Glyph("\uE71E", 14, PrintUi.MutedBrush));
        zoom.Children.Add(new TextBlock { Text = "Zoom", Margin = new Thickness(6, 0, 8, 0), Foreground = PrintUi.MutedBrush,
            VerticalAlignment = VerticalAlignment.Center });
        zoom.Children.Add(_zoom);
        zoom.Children.Add(zoomValue);
        DockPanel.SetDock(zoom, Dock.Right);
        toolbar.Children.Add(zoom);
        var commands = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        _undoButton = ToolButton("\uE7A7", "Urungkan (Ctrl+Z)", Undo);
        _redoButton = ToolButton("\uE7A6", "Ulangi (Ctrl+Y)", Redo);
        _deleteButton = ToolButton("\uE74D", "Hapus elemen", DeleteSelected);
        commands.Children.Add(_undoButton); commands.Children.Add(_redoButton);
        commands.Children.Add(PrintUi.ToolbarSeparator());
        commands.Children.Add(Command("Teks", AddText, PrintButtonKind.Tool, "\uE710", "Tambah teks"));
        commands.Children.Add(ToolButton("\uE91B", "Pilih atau ganti gambar kop", ImportImage));
        commands.Children.Add(ToolButton("\uE738", "Tambah garis", () => AddElement(new() { Kind = PrintHeaderElementKind.Line, X = 20, Y = 15, Width = 220, Height = 2 })));
        commands.Children.Add(PrintUi.ToolbarSeparator());
        commands.Children.Add(_deleteButton);
        commands.Children.Add(Command("Atur Ulang", ResetLayout, PrintButtonKind.Tool, "\uE777", "Kembalikan tata letak bawaan"));
        var modeContent = new StackPanel { Orientation = Orientation.Horizontal };
        modeContent.Children.Add(PrintUi.Glyph("\uE890", 14, PrintUi.MaroonDarkBrush));
        modeContent.Children.Add(new TextBlock { Text = "Pratinjau Cetak", Margin = new Thickness(6, 0, 0, 0),
            Foreground = PrintUi.MaroonDarkBrush, VerticalAlignment = VerticalAlignment.Center });
        var mode = new CheckBox { Content = modeContent, Margin = new Thickness(9, 5, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        mode.Checked += (_, _) => { FinishInline(); _previewOnly = true; Draw(); };
        mode.Unchecked += (_, _) => { _previewOnly = false; Draw(); };
        commands.Children.Add(mode);
        toolbar.Children.Add(commands);
        root.Children.Add(PrintUi.Card(toolbar, new Thickness(10, 7, 10, 7), new Thickness(0, 0, 0, 10)));

        var body = new Grid();
        body.ColumnDefinitions.Add(new() { Width = new GridLength(292) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(10) });
        body.ColumnDefinitions.Add(new());
        Grid.SetRow(body, 1); root.Children.Add(body);
        var inspector = new StackPanel();
        SectionLabel(inspector, "Elemen Kop");
        inspector.Children.Add(_elements);
        _fields.ItemsSource = PrintHeaderLayout.Bindings.Where(b => b == "title" ||
            int.Parse(b[^1..], CultureInfo.InvariantCulture) < (b.StartsWith("left", StringComparison.Ordinal) ? sample.LeftInfo.Count : sample.RightInfo.Count))
            .Select(b => new FieldChoice(b, PrintHeaderRenderer.FieldName(b, sample))).ToArray();
        _fields.SelectedIndex = 0;
        SectionLabel(inspector, "Data Transaksi", 12);
        var fieldRow = new Grid();
        fieldRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        fieldRow.ColumnDefinitions.Add(new() { Width = new GridLength(38) });
        fieldRow.Children.Add(_fields);
        var addField = ToolButton("\uE710", "Tambahkan data transaksi", AddField);
        addField.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(addField, 1);
        fieldRow.Children.Add(addField);
        inspector.Children.Add(fieldRow);
        SectionLabel(inspector, "Isi Teks", 12);
        inspector.Children.Add(_text);
        inspector.Children.Add(_source);
        SectionLabel(inspector, "Posisi & Ukuran", 12);
        var positions = new UniformGrid { Columns = 2 };
        foreach (var (caption, input) in new[] { ("Posisi X", _x), ("Posisi Y", _y), ("Lebar", _width), ("Tinggi", _height) })
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 7) };
            Label(panel, caption);
            panel.Children.Add(input);
            positions.Children.Add(panel);
        }
        inspector.Children.Add(positions);
        SectionLabel(inspector, "Tipografi", 6);
        Label(inspector, "Font");
        inspector.Children.Add(_font);
        var fontRow = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        fontRow.ColumnDefinitions.Add(new() { Width = new GridLength(78) });
        fontRow.ColumnDefinitions.Add(new() { Width = new GridLength(8) });
        fontRow.ColumnDefinitions.Add(new());
        var sizePanel = new StackPanel();
        Label(sizePanel, "Ukuran");
        sizePanel.Children.Add(_size);
        fontRow.Children.Add(sizePanel);
        var alignmentPanel = new StackPanel();
        Label(alignmentPanel, "Perataan");
        alignmentPanel.Children.Add(_alignment);
        Grid.SetColumn(alignmentPanel, 2);
        fontRow.Children.Add(alignmentPanel);
        inspector.Children.Add(fontRow);
        inspector.Children.Add(_bold);
        SectionLabel(inspector, "Area Kop", 7);
        Label(inspector, "Tinggi Area Kop");
        inspector.Children.Add(_areaHeight);
        var inspectorScroll = new ScrollViewer { Content = inspector, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        body.Children.Add(PrintUi.Card(inspectorScroll, new Thickness(12)));

        _page.Children.Add(_paper); Canvas.SetLeft(_canvas, 38); Canvas.SetTop(_canvas, 30); _page.Children.Add(_canvas);
        var pageFrame = new Border { Child = _page, Margin = new Thickness(24), Background = Brushes.White,
            BorderBrush = PrintUi.BorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
            Effect = new DropShadowEffect { BlurRadius = 13, ShadowDepth = 2, Opacity = 0.18, Color = PrintUi.MaroonDarkBrush.Color } };
        var scroll = new ScrollViewer { Content = pageFrame, Background = PrintUi.WorkspaceBrush,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        var canvasPanel = new Grid();
        canvasPanel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        canvasPanel.RowDefinitions.Add(new());
        var canvasHeading = new DockPanel();
        var paperBadge = new Border { Background = PrintUi.GoldSoftBrush, BorderBrush = PrintUi.GoldBrush,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(9, 4, 9, 4),
            Child = new TextBlock { Text = "A4", FontWeight = FontWeights.Bold, Foreground = PrintUi.MaroonDarkBrush } };
        DockPanel.SetDock(paperBadge, Dock.Right);
        canvasHeading.Children.Add(paperBadge);
        canvasHeading.Children.Add(new TextBlock { Text = "KANVAS DOKUMEN", FontWeight = FontWeights.Bold,
            Foreground = PrintUi.MaroonBrush, VerticalAlignment = VerticalAlignment.Center });
        canvasPanel.Children.Add(PrintUi.Card(canvasHeading, new Thickness(10, 6, 10, 6), new Thickness(0, 0, 0, 8)));
        Grid.SetRow(scroll, 1);
        canvasPanel.Children.Add(scroll);
        Grid.SetColumn(canvasPanel, 2);
        body.Children.Add(canvasPanel);
        void Zoom()
        {
            _page.LayoutTransform = new ScaleTransform(_zoom.Value / 100, _zoom.Value / 100);
            zoomValue.Text = $"{_zoom.Value:0}%";
        }
        _zoom.ValueChanged += (_, _) => Zoom(); Zoom();
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var footerCommands = new StackPanel { Orientation = Orientation.Horizontal };
        footerCommands.Children.Add(Command("Terapkan", Accept, PrintButtonKind.Primary, "\uE73E", "Terapkan perubahan"));
        footerCommands.Children.Add(Command("Batal", Close));
        DockPanel.SetDock(footerCommands, Dock.Right); footer.Children.Add(footerCommands); footer.Children.Add(_status);
        Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        _elements.SelectionChanged += (_, _) => { if (!_loading && _elements.SelectedItem is ElementChoice choice) Select(choice.Id); };
        _text.TextChanged += (_, _) =>
        {
            if (_loading || Selected() is not { Kind: PrintHeaderElementKind.Text } element) return;
            ChangeElement(element with { Text = _text.Text }, refreshInspector: false);
        };
        foreach (var number in new[] { _x, _y, _width, _height, _size })
        {
            number.LostKeyboardFocus += (_, _) => { if (!_loading) CommitProperties(number); };
            number.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitProperties(number); e.Handled = true; } };
        }
        _font.SelectionChanged += (_, _) => { if (!_loading) CommitProperties(); };
        _alignment.SelectionChanged += (_, _) => { if (!_loading) CommitProperties(); };
        _bold.Click += (_, _) => CommitProperties();
        _areaHeight.LostKeyboardFocus += (_, _) => CommitAreaHeight();
        _areaHeight.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitAreaHeight(); e.Handled = true; } };
        PreviewKeyDown += HandleKey;
        _refresh.Tick += (_, _) => { _refresh.Stop(); RenderPaper(); };
        Closing += (_, e) =>
        {
            FinishInline();
            if (!_accepted && _undo.Count > 0 && MessageBox.Show(this, "Buang perubahan kop yang belum diterapkan?", "Edit Kop",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) e.Cancel = true;
        };
        Closed += (_, _) => _refresh.Stop();
        _selected = _state.Layout.Elements.FirstOrDefault()?.Id;
        Draw(); RenderPaper();
    }

    private sealed record ElementChoice(string Id, string Label);
    private sealed record FieldChoice(string Binding, string Label);
    private PrintHeaderElement? Selected() => _state.Layout.Elements.FirstOrDefault(e => e.Id == _selected);
    private DocumentPrintFormat CurrentFormat() => _original with { HeaderLayout = _state.Layout, ImageBase64 = _state.Image, ImageName = _state.ImageName };

    private void Record(EditorState state)
    {
        if (_state == state) return;
        _undo.Push(_state); if (_undo.Count > 100) TrimUndo();
        _redo.Clear(); _state = state; _status.Text = "Perubahan belum diterapkan.";
    }

    private void TrimUndo()
    {
        var recent = _undo.Take(100).Reverse().ToArray(); _undo.Clear(); foreach (var state in recent) _undo.Push(state);
    }

    private void ChangeElement(PrintHeaderElement element, bool refreshInspector = true)
    {
        if (element == Selected()) return;
        Record(_state with { Layout = _state.Layout with { Elements = _state.Layout.Elements.Select(e => e.Id == element.Id ? element : e).ToArray() } });
        Draw(refreshInspector);
    }

    private void Select(string id)
    {
        FinishInline(); _selected = id; Draw();
    }

    private void Draw(bool refreshInspector = true)
    {
        _canvas.Children.Clear(); _canvas.Height = _state.Layout.Height; _canvas.Visibility = _previewOnly ? Visibility.Hidden : Visibility.Visible;
        foreach (var element in _state.Layout.Elements)
        {
            var box = new Border { Width = element.Width, Height = element.Height, Background = Brushes.Transparent,
                BorderBrush = element.Id == _selected ? Brushes.SteelBlue : Brushes.Transparent, BorderThickness = new Thickness(0) };
            var layer = new Grid(); layer.Children.Add(PrintHeaderRenderer.Element(element, _sample, CurrentFormat())); box.Child = layer;
            box.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2 && element.Kind == PrintHeaderElementKind.Text) { BeginInline(element.Id); e.Handled = true; }
                else if (_selected != element.Id) { Select(element.Id); e.Handled = true; }
            };
            if (element.Id == _selected)
            {
                layer.Children.Add(new Border { BorderBrush = Brushes.SteelBlue, BorderThickness = new Thickness(1), IsHitTestVisible = false });
                var move = Handle(Cursors.SizeAll, "Geser elemen", 26, 8);
                move.HorizontalAlignment = HorizontalAlignment.Center; move.VerticalAlignment = VerticalAlignment.Top;
                layer.Children.Add(move); WireGesture(move, element, box, false);
                var resize = Handle(Cursors.SizeNWSE, "Ubah ukuran", 9, 9);
                resize.HorizontalAlignment = HorizontalAlignment.Right; resize.VerticalAlignment = VerticalAlignment.Bottom;
                layer.Children.Add(resize); WireGesture(resize, element, box, true);
            }
            Canvas.SetLeft(box, element.X); Canvas.SetTop(box, element.Y); _canvas.Children.Add(box);
        }
        _undoButton.IsEnabled = _undo.Count > 0; _redoButton.IsEnabled = _redo.Count > 0; _deleteButton.IsEnabled = Selected() is not null;
        _loading = true;
        _elements.ItemsSource = _state.Layout.Elements.Select(e => new ElementChoice(e.Id, PrintHeaderRenderer.Name(e, _sample))).ToArray();
        _elements.SelectedItem = _elements.Items.Cast<ElementChoice>().FirstOrDefault(e => e.Id == _selected);
        if (refreshInspector) PopulateInspector();
        _loading = false;
        _refresh.Stop(); _refresh.Start();
    }

    private void PopulateInspector()
    {
        var element = Selected();
        _text.IsEnabled = element?.Kind == PrintHeaderElementKind.Text;
        _text.Text = element is null ? "" : PrintHeaderRenderer.Resolve(element, _sample);
        _source.Text = element?.Kind == PrintHeaderElementKind.Field ? "Data transaksi: " + PrintHeaderRenderer.Name(element, _sample)
            : element?.Kind == PrintHeaderElementKind.Image ? "Proporsi gambar terkunci" : "";
        foreach (var (input, value) in new[] { (_x, element?.X ?? 0), (_y, element?.Y ?? 0), (_width, element?.Width ?? 0),
            (_height, element?.Height ?? 0), (_size, element?.FontSize ?? 11) })
        { input.Text = value.ToString("0.##", CultureInfo.CurrentCulture); input.IsEnabled = element is not null; }
        var isText = element?.Kind is PrintHeaderElementKind.Text or PrintHeaderElementKind.Field;
        _font.IsEnabled = _alignment.IsEnabled = _bold.IsEnabled = _size.IsEnabled = isText;
        _font.SelectedItem = element?.FontFamily ?? "Tahoma"; _alignment.SelectedIndex = (int)(element?.Alignment ?? PrintHeaderAlignment.Left);
        _bold.IsChecked = element?.Bold ?? false; _areaHeight.Text = _state.Layout.Height.ToString("0.##", CultureInfo.CurrentCulture);
    }

    private bool CommitProperties(TextBox? changed = null)
    {
        if (_loading || Selected() is not { } element) return true;
        if (!TryNumber(_x, out var x) || !TryNumber(_y, out var y) || !TryNumber(_width, out var width) || !TryNumber(_height, out var height)
            || !TryNumber(_size, out var size) || width < 8 || height < 2 || size is < 6 or > 48)
        { _status.Text = "Isi posisi dan ukuran yang valid; ukuran font 6-48."; return false; }
        double? ratio = ImageRatio(element);
        if (ratio.HasValue)
        {
            if (changed == _height) width = height * ratio.Value;
            else height = width / ratio.Value;
        }
        var resized = PrintHeaderLayout.Resize(element, width, height, _state.Layout.Height, ratio);
        var moved = PrintHeaderLayout.Move(resized, x, y, _state.Layout.Height);
        ChangeElement(moved with { FontSize = size, FontFamily = _font.SelectedItem as string ?? "Tahoma",
            Alignment = (PrintHeaderAlignment)Math.Max(0, _alignment.SelectedIndex), Bold = _bold.IsChecked == true });
        return true;
    }

    private bool CommitAreaHeight()
    {
        if (_loading) return true;
        var minimum = Math.Max(60, _state.Layout.Elements.Select(e => e.Y + e.Height).DefaultIfEmpty(0).Max());
        if (!TryNumber(_areaHeight, out var height) || height < minimum || height > 330)
        { _status.Text = $"Tinggi kop harus {minimum:0.##}-330 agar seluruh elemen tetap berada di atas tabel."; return false; }
        if (Math.Abs(_state.Layout.Height - height) > .001)
        { Record(_state with { Layout = _state.Layout with { Height = height } }); Draw(); }
        return true;
    }

    private double? ImageRatio(PrintHeaderElement element)
    {
        if (element.Kind != PrintHeaderElementKind.Image || _state.Image is null) return null;
        var image = DocumentPrintFormatStore.DecodeImage(_state.Image); return (double)image.PixelWidth / image.PixelHeight;
    }

    private void WireGesture(Thumb thumb, PrintHeaderElement element, Border box, bool resize)
    {
        var dx = 0d; var dy = 0d;
        thumb.DragStarted += (_, _) => { _gestureStart = _state; _gestureElement = element; dx = dy = 0; };
        thumb.DragDelta += (_, e) =>
        {
            dx += e.HorizontalChange; dy += e.VerticalChange;
            var next = resize ? PrintHeaderLayout.Resize(element, element.Width + dx, element.Height + dy, _state.Layout.Height, ImageRatio(element))
                : PrintHeaderLayout.Move(element, element.X + dx, element.Y + dy, _state.Layout.Height);
            _gestureElement = next;
            _state = _state with { Layout = _state.Layout with { Elements = _state.Layout.Elements.Select(item => item.Id == next.Id ? next : item).ToArray() } };
            box.Width = next.Width; box.Height = next.Height; Canvas.SetLeft(box, next.X); Canvas.SetTop(box, next.Y);
            _loading = true; PopulateInspector(); _loading = false;
        };
        thumb.DragCompleted += (_, e) =>
        {
            if (_gestureStart is { } before)
            {
                var after = _state; _state = before;
                if (!e.Canceled && _gestureElement != element) Record(after);
            }
            _gestureStart = null; _gestureElement = null; Draw();
        };
    }

    private void BeginInline(string id)
    {
        Select(id);
        if (Selected() is not { Kind: PrintHeaderElementKind.Text } element) return;
        var input = new TextBox { Text = element.Text, Width = element.Width, Height = Math.Max(24, element.Height),
            FontSize = element.FontSize, FontFamily = new FontFamily(element.FontFamily), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MaxLength = 2000, Background = Brushes.White, BorderBrush = Brushes.SteelBlue, BorderThickness = new Thickness(1),
            Padding = new Thickness(0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _inline = input; Canvas.SetLeft(input, element.X); Canvas.SetTop(input, element.Y); Panel.SetZIndex(input, 100); _canvas.Children.Add(input);
        input.LostKeyboardFocus += (_, _) => FinishInline();
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _inline = null; Draw(); e.Handled = true; }
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { FinishInline(); e.Handled = true; }
        };
        input.Focus(); input.SelectAll();
    }

    private void FinishInline()
    {
        if (_inline is not { } input) return;
        _inline = null;
        if (Selected() is { Kind: PrintHeaderElementKind.Text } element) ChangeElement(element with { Text = input.Text });
        _canvas.Children.Remove(input);
    }

    private void AddText() => AddElement(new() { Text = "Teks baru", X = 20, Y = 15 });
    private void AddField()
    {
        if (_fields.SelectedItem is FieldChoice field) AddElement(new() { Kind = PrintHeaderElementKind.Field, Binding = field.Binding, X = 20, Y = 15 });
    }
    private void AddElement(PrintHeaderElement element)
    {
        FinishInline();
        if (_state.Layout.Elements.Length >= 60) { _status.Text = "Maksimal 60 elemen."; return; }
        var bottom = _state.Layout.Elements.Select(e => e.Y + e.Height).DefaultIfEmpty(0).Max();
        element = element with { Y = bottom + 6 };
        if (element.Y + element.Height + 6 > 330)
        { _status.Text = "Area kop penuh. Kurangi tinggi atau hapus elemen sebelum menambah."; return; }
        Record(_state with { Layout = _state.Layout with { Height = Math.Max(_state.Layout.Height, element.Y + element.Height + 6),
            Elements = [.. _state.Layout.Elements, element] } }); _selected = element.Id; Draw();
    }
    private void DeleteSelected()
    {
        FinishInline(); if (Selected() is null) return;
        Record(_state with { Layout = _state.Layout with { Elements = _state.Layout.Elements.Where(e => e.Id != _selected).ToArray() } });
        _selected = _state.Layout.Elements.LastOrDefault()?.Id; Draw();
    }
    private void Undo()
    {
        FinishInline(); if (_undo.Count == 0) return; _redo.Push(_state); _state = _undo.Pop(); Draw();
    }
    private void Redo()
    {
        FinishInline(); if (_redo.Count == 0) return; _undo.Push(_state); _state = _redo.Pop(); Draw();
    }
    private void ResetLayout()
    {
        FinishInline();
        if (MessageBox.Show(this, "Atur ulang posisi dan teks kop ke template bawaan?", "Kop Dokumen", MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Record(_state with { Layout = PrintHeaderRenderer.CreateDefault(_sample, CurrentFormat() with { HeaderLayout = null }) });
        _selected = _state.Layout.Elements.FirstOrDefault()?.Id; Draw();
    }
    private void ImportImage()
    {
        var picker = new OpenFileDialog { Title = "Pilih Gambar Kop", Filter = "Gambar|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        try { SetImage(DocumentPrintFormatStore.ImportImage(picker.FileName), Path.GetFileName(picker.FileName)); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { _status.Text = e.Message; }
    }
    private void SetImage(string data, string name)
    {
        FinishInline();
        var previous = _state.Layout.Elements.FirstOrDefault(e => e.Kind == PrintHeaderElementKind.Image);
        if (previous is null && _state.Layout.Elements.Length >= 60) { _status.Text = "Maksimal 60 elemen."; return; }
        var image = PrintHeaderRenderer.NewImage(data);
        var bitmap = DocumentPrintFormatStore.DecodeImage(data);
        image = PrintHeaderLayout.Resize(image, image.Width, image.Height, _state.Layout.Height, (double)bitmap.PixelWidth / bitmap.PixelHeight);
        if (previous is not null) image = PrintHeaderLayout.Move(image with { Id = previous.Id }, previous.X, previous.Y, _state.Layout.Height);
        var layout = _state.Layout;
        if (previous is null)
        {
            // Reserve a separate strip above the text so importing a logo never covers the company name.
            var extra = image.Height + 10;
            var occupiedHeight = layout.Elements.Select(e => e.Y + e.Height).DefaultIfEmpty(0).Max();
            var neededHeight = occupiedHeight + extra + 12;
            if (neededHeight > 330)
            { _status.Text = "Ruang kop tidak cukup untuk gambar. Kurangi tinggi area/elemen terlebih dahulu."; return; }
            layout = layout with { Height = Math.Max(layout.Height, neededHeight), Elements = layout.Elements.Select(e => e with { Y = e.Y + extra }).ToArray() };
        }
        Record(_state with { Image = data, ImageName = name, Layout = layout with
        { Elements = previous is null ? [.. layout.Elements, image] : layout.Elements.Select(e => e.Id == previous.Id ? image : e).ToArray() } });
        _selected = image.Id; Draw();
    }

    private void RenderPaper()
    {
        try
        {
            var format = CurrentFormat(); DocumentPrintFormatStore.Validate(format);
            var page = DocumentPrintRenderer.Build(_sample, format: format).Pages[0].Child;
            _paper.Background = new VisualBrush(page) { Stretch = Stretch.Fill };
        }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or NotSupportedException) { _status.Text = e.Message; }
    }
    private void Accept()
    {
        FinishInline(); if (!CommitProperties() || !CommitAreaHeight()) return;
        try { var format = CurrentFormat(); DocumentPrintFormatStore.Validate(format); Result = format; _accepted = true; Close(); }
        catch (InvalidDataException e) { _status.Text = e.Message; }
    }
    private void HandleKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { Undo(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y) { Redo(); e.Handled = true; }
        else if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.None && Selected() is { } element && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            ChangeElement(PrintHeaderLayout.Move(element, element.X + (e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0),
                element.Y + (e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0), _state.Layout.Height)); e.Handled = true;
        }
    }
    private static bool TryNumber(TextBox input, out double value) => double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value);
    private static TextBox Number() => new() { Height = 28, VerticalContentAlignment = VerticalAlignment.Center };
    private static void Label(Panel panel, string text, double top = 0) => panel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 5) });
    private static void SectionLabel(Panel panel, string text, double top = 0) => panel.Children.Add(new TextBlock
    {
        Text = text.ToUpperInvariant(),
        FontSize = 10.5,
        FontWeight = FontWeights.Bold,
        Foreground = PrintUi.MaroonBrush,
        Margin = new Thickness(0, top, 0, 6)
    });
    private static Button Command(string text, Action action, PrintButtonKind kind = PrintButtonKind.Standard,
        string? glyph = null, string? tooltip = null) => PrintUi.Button(text, text switch
        {
            "Teks" => 72,
            "Terapkan" => 112,
            "Batal" => 88,
            "Atur Ulang" => 112,
            _ => 96
        }, action, kind, glyph, tooltip);
    private static Button ToolButton(string glyph, string tooltip, Action action) =>
        PrintUi.Button("", 30, action, PrintButtonKind.Tool, glyph, tooltip);
    private static Thumb Handle(Cursor cursor, string tooltip, double width, double height)
    {
        var visual = new FrameworkElementFactory(typeof(Border)); visual.SetValue(Border.BackgroundProperty, Brushes.SteelBlue);
        return new Thumb { Width = width, Height = height, Cursor = cursor, ToolTip = tooltip,
            Template = new ControlTemplate(typeof(Thumb)) { VisualTree = visual } };
    }
}
