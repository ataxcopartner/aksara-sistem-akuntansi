using System.IO;

namespace Aksara.Desktop.Services;

internal enum PrintHeaderElementKind { Text, Field, Image, Line }
internal enum PrintHeaderAlignment { Left, Center, Right }

internal sealed record PrintHeaderElement
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public PrintHeaderElementKind Kind { get; init; }
    public string Text { get; init; } = "";
    public string Binding { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; } = 180;
    public double Height { get; init; } = 24;
    public double FontSize { get; init; } = 11;
    public string FontFamily { get; init; } = "Tahoma";
    public bool Bold { get; init; }
    public PrintHeaderAlignment Alignment { get; init; }
}

internal sealed record PrintHeaderLayout
{
    public const double PageWidth = 717;
    public const double MaximumHeight = 330;
    public double Height { get; init; } = 250;
    public PrintHeaderElement[] Elements { get; init; } = [];
    public static string[] Fonts { get; } = ["Tahoma", "Arial", "Segoe UI", "Times New Roman"];
    public static string[] Bindings { get; } = ["title", "left0", "left1", "left2", "left3", "right0", "right1", "right2", "right3"];

    public static void Validate(PrintHeaderLayout layout, string? image)
    {
        if (!double.IsFinite(layout.Height) || layout.Height is < 60 or > MaximumHeight || layout.Elements is null || layout.Elements.Length > 60)
            throw new InvalidDataException("Area kop tidak valid (tinggi 60-330, maksimal 60 elemen).");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in layout.Elements)
        {
            if (element is null || string.IsNullOrEmpty(element.Id) || element.Id.Length > 64 || !ids.Add(element.Id)
                || !Enum.IsDefined(element.Kind) || !Enum.IsDefined(element.Alignment))
                throw new InvalidDataException("Elemen kop tidak valid.");
            if (!new[] { element.X, element.Y, element.Width, element.Height, element.FontSize }.All(double.IsFinite)
                || element.X < 0 || element.Y < 0 || element.Width < 8 || element.Height < 2
                || element.X + element.Width > PageWidth + .01 || element.Y + element.Height > layout.Height + .01)
                throw new InvalidDataException("Elemen kop harus berada di dalam area atas tabel.");
            if (element.FontSize is < 6 or > 48 || !Fonts.Contains(element.FontFamily)
                || element.Text is null || element.Text.Length > 2000 || element.Binding is null)
                throw new InvalidDataException("Teks atau font kop tidak valid.");
            if (element.Kind == PrintHeaderElementKind.Field && !Bindings.Contains(element.Binding))
                throw new InvalidDataException("Sumber data kop tidak dikenal.");
            if (element.Kind == PrintHeaderElementKind.Image && string.IsNullOrEmpty(image))
                throw new InvalidDataException("Gambar kop belum dipilih.");
        }
        if (layout.Elements.Count(e => e.Kind == PrintHeaderElementKind.Image) > 1)
            throw new InvalidDataException("Gunakan satu gambar kop; teks dapat ditambahkan terpisah.");
    }

    public static PrintHeaderElement Move(PrintHeaderElement element, double x, double y, double height) => element with
    {
        X = Math.Clamp(x, 0, PageWidth - element.Width), Y = Math.Clamp(y, 0, height - element.Height)
    };

    public static PrintHeaderElement Resize(PrintHeaderElement element, double width, double height, double areaHeight, double? imageRatio = null)
    {
        width = Math.Clamp(width, 8, PageWidth - element.X);
        height = Math.Clamp(height, 2, areaHeight - element.Y);
        if (imageRatio is > 0)
        {
            var scale = Math.Min(width / imageRatio.Value, height);
            width = scale * imageRatio.Value;
            height = scale;
            if (width < 8 || height < 2) return element;
        }
        return element with { Width = width, Height = height };
    }
}
