using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

internal static class PrintHeaderRenderer
{
    public static PrintHeaderLayout CreateDefault(PrintContent content, DocumentPrintFormat format)
    {
        var elements = new List<PrintHeaderElement>();
        var compact = format.Template == PrintTemplate.Compact;
        var modern = format.Template == PrintTemplate.Modern;
        var align = modern ? PrintHeaderAlignment.Left : PrintHeaderAlignment.Center;
        var font = compact ? "Arial" : modern ? "Segoe UI" : "Tahoma";
        void Add(string text, double x, double y, double w, double h, double size, bool bold = false,
            PrintHeaderAlignment alignment = PrintHeaderAlignment.Left, string binding = "") => elements.Add(new()
        {
            Kind = binding.Length > 0 ? PrintHeaderElementKind.Field : PrintHeaderElementKind.Text,
            Text = text, Binding = binding, X = x, Y = y, Width = w, Height = h, FontSize = size,
            Bold = bold, Alignment = alignment, FontFamily = font
        });
        var y = 0d;
        if (format.Letterhead == LetterheadKind.Image && format.ImageBase64 is { } data)
        {
            var image = NewImage(data);
            elements.Add(image);
            y = image.Height + 10;
        }
        else
        {
            Add(format.CompanyName, 0, y, 717, 26, compact ? 13 : 16, true, align);
            y += 28;
            var settings = ProgramConfigurationCache.Current;
            foreach (var line in new[] { settings.Address, string.Join(" | ", new[] { settings.City, settings.Phone }.Where(s => !string.IsNullOrWhiteSpace(s))) })
            {
                // Keep an editable address box even when the configured address is empty.
                Add(line, 0, y, 717, 18, 9, alignment: align);
                y += 18;
            }
        }
        Add("", 0, y + 4, 717, 30, compact ? 15 : 20, true, align, "title");
        y += 44;
        if (modern)
        {
            elements.Add(new() { Kind = PrintHeaderElementKind.Line, Y = y - 5, Width = 717, Height = 2 });
            y += 6;
        }
        foreach (var (rows, side, x) in new[] { (content.LeftInfo, "left", 0d), (content.RightInfo, "right", 370d) })
        for (var index = 0; index < rows.Count; index++)
        {
            Add(rows[index].Label + " :", x, y + index * 26, 87, 24, compact ? 9 : 10);
            Add("", x + 90, y + index * 26, 257, 24, compact ? 9 : 10, true, binding: side + index);
        }
        return new() { Height = Math.Min(330, y + Math.Max(content.LeftInfo.Count, content.RightInfo.Count) * 26 + 12), Elements = elements.ToArray() };
    }

    public static PrintHeaderElement NewImage(string data)
    {
        var bitmap = DocumentPrintFormatStore.DecodeImage(data);
        var scale = Math.Min(1, Math.Min(420d / bitmap.PixelWidth, 80d / bitmap.PixelHeight));
        var width = Math.Max(8, bitmap.PixelWidth * scale);
        var height = Math.Max(2, bitmap.PixelHeight * scale);
        return new() { Kind = PrintHeaderElementKind.Image, X = (717 - width) / 2, Width = width, Height = height };
    }

    public static string Resolve(PrintHeaderElement element, PrintContent content)
    {
        if (element.Kind != PrintHeaderElementKind.Field) return element.Text;
        if (element.Binding == "title") return content.Title.ToUpperInvariant();
        var rows = element.Binding.StartsWith("left", StringComparison.Ordinal) ? content.LeftInfo : content.RightInfo;
        return int.TryParse(element.Binding[^1..], out var index) && index < rows.Count ? rows[index].Value : "";
    }

    public static string Name(PrintHeaderElement element, PrintContent content) => element.Kind switch
    {
        PrintHeaderElementKind.Image => "Gambar kop",
        PrintHeaderElementKind.Line => "Garis",
        PrintHeaderElementKind.Field => FieldName(element.Binding, content),
        _ => string.IsNullOrWhiteSpace(element.Text) ? "Teks kosong" : element.Text.ReplaceLineEndings(" ")
    };

    public static string FieldName(string binding, PrintContent content)
    {
        if (binding == "title") return "Judul dokumen";
        var rows = binding.StartsWith("left", StringComparison.Ordinal) ? content.LeftInfo : content.RightInfo;
        return int.TryParse(binding[^1..], out var index) && index < rows.Count ? rows[index].Label : binding;
    }

    public static FrameworkElement Element(PrintHeaderElement element, PrintContent content, DocumentPrintFormat format)
    {
        if (element.Kind == PrintHeaderElementKind.Image)
            return new Image { Source = DocumentPrintFormatStore.DecodeImage(format.ImageBase64!), Stretch = Stretch.Uniform };
        if (element.Kind == PrintHeaderElementKind.Line)
            return new Border { Background = format.Template == PrintTemplate.Modern ? Brushes.Maroon : Brushes.Black };
        var text = new TextBlock
        {
            Text = Resolve(element, content), FontSize = element.FontSize, FontFamily = new FontFamily(element.FontFamily),
            FontWeight = element.Bold ? FontWeights.Bold : FontWeights.Normal, Foreground = Brushes.Black,
            TextWrapping = TextWrapping.Wrap, Width = element.Width,
            TextAlignment = element.Alignment switch { PrintHeaderAlignment.Center => TextAlignment.Center,
                PrintHeaderAlignment.Right => TextAlignment.Right, _ => TextAlignment.Left }
        };
        // Downscale only overflowing content; dynamic transaction values cannot cover nearby boxes.
        text.Measure(new Size(element.Width, double.PositiveInfinity));
        if (text.DesiredSize.Height <= element.Height + .1) return text;
        return new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = text,
            HorizontalAlignment = element.Alignment switch { PrintHeaderAlignment.Center => HorizontalAlignment.Center,
                PrintHeaderAlignment.Right => HorizontalAlignment.Right, _ => HorizontalAlignment.Left } };
    }

    public static FrameworkElement Build(PrintHeaderLayout layout, PrintContent content, DocumentPrintFormat format, double width)
    {
        var canvas = new Canvas { Width = 717, Height = layout.Height, Background = Brushes.White };
        foreach (var element in layout.Elements)
        {
            var box = new Grid { Width = element.Width, Height = element.Height, Tag = "header-" + element.Id };
            box.Children.Add(Element(element, content, format));
            Canvas.SetLeft(box, element.X);
            Canvas.SetTop(box, element.Y);
            canvas.Children.Add(box);
        }
        return new Viewbox { Width = width, Height = layout.Height * width / 717, Stretch = Stretch.Uniform, Child = canvas };
    }
}
