using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace Aksara.Desktop.Services;

internal enum PrintTemplate { Classic, Modern, Compact }
internal enum LetterheadKind { Text, Image }

internal sealed record PrintSignatureImage(string ImageBase64, string ImageName);

internal sealed record DocumentPrintFormat
{
    public int SchemaVersion { get; init; } = 1;
    public PrintTemplate Template { get; init; }
    public string CompanyName { get; init; } = ProgramConfigurationCache.PrintCompanyName;
    public LetterheadKind Letterhead { get; init; }
    public string? ImageBase64 { get; init; }
    public string? ImageName { get; init; }
    public int ImageHeight { get; init; } = 90;
    public int MinimumRows { get; init; } = 20;
    public PrintSignatureImage? Signature1Image { get; init; }
    public PrintSignatureImage? Signature2Image { get; init; }
    public PrintSignatureImage? Signature3Image { get; init; }
    public string? Signature1Name { get; init; }
    public string? Signature2Name { get; init; }
    public PrintHeaderLayout? HeaderLayout { get; init; }

    public static DocumentPrintFormat Default() => new()
    {
        MinimumRows = Math.Clamp(ProgramConfigurationCache.Current.MinimumPrintRows, 1, 60)
    };
}

internal sealed record PrintDocumentKind(string Key, string Group, string Label, string Title)
{
    public string DisplayName => $"{Group} / {Label}";
}

internal static class DocumentPrintFormats
{
    public static IReadOnlyList<PrintDocumentKind> Documents { get; } =
    [
        new("sales-invoice", "Penjualan", "Nota Penjualan", "Nota Penjualan"),
        new("sales-delivery", "Penjualan", "Surat Jalan", "Surat Jalan"),
        new("sales-receipt", "Penjualan", "Kwitansi", "Kwitansi"),
        new("sales-handover", "Penjualan", "Tanda Terima", "Tanda Terima"),
        new("sales-tax-simple", "Penjualan", "Faktur Pajak Sederhana", "Faktur Pajak Sederhana Penjualan"),
        new("sales-tax-standard", "Penjualan", "Faktur Pajak Standar", "Faktur Pajak Standar Penjualan"),
        new("sales-return", "Retur Penjualan", "Retur Penjualan", "Retur Penjualan"),
        new("purchase-invoice", "Pembelian", "Faktur Pembelian", "Faktur Pembelian"),
        new("purchase-delivery", "Pembelian", "Surat Jalan", "Surat Jalan Pembelian"),
        new("purchase-tax-simple", "Pembelian", "Faktur Pajak Sederhana", "Faktur Pajak Sederhana Pembelian"),
        new("purchase-tax-standard", "Pembelian", "Faktur Pajak Standar", "Faktur Pajak Standar Pembelian"),
        new("purchase-return", "Retur Pembelian", "Retur Pembelian", "Retur Pembelian"),
        new("cash-payment", "Kas / Bank", "Bukti Pengeluaran", "Bukti Pengeluaran Kas / Bank"),
        new("cash-receipt", "Kas / Bank", "Bukti Penerimaan", "Bukti Penerimaan Kas / Bank")
    ];

    public static PrintDocumentKind Get(string key) => Documents.FirstOrDefault(x => x.Key == key)
        ?? throw new ArgumentException("Jenis dokumen tidak dikenal.", nameof(key));

    // Use the original document title, before the configurable display title is applied.
    public static string ForTitle(string title) => Documents.FirstOrDefault(x =>
        string.Equals(x.Title, title.Trim(), StringComparison.OrdinalIgnoreCase))?.Key
        ?? (title.Equals("Nota Pembelian", StringComparison.OrdinalIgnoreCase) ? "purchase-invoice"
            : throw new ArgumentException($"Format dokumen belum terdaftar: {title}"));

    public static string ForCashBank(string? kind) => string.Equals(kind, "PAYMENT", StringComparison.OrdinalIgnoreCase)
        ? "cash-payment" : "cash-receipt";

    public static string TemplateName(PrintTemplate template) => template switch
    {
        PrintTemplate.Modern => "Modern",
        PrintTemplate.Compact => "Ringkas",
        _ => "Klasik"
    };
}

internal sealed class DocumentPrintFormatStore
{
    private const int MaximumImageBytes = 5 * 1024 * 1024;
    private const int MaximumProfileBytes = MaximumImageBytes * 8;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _directory;

    public static DocumentPrintFormatStore Shared { get; } = new(Path.Combine(
        AppBrand.LocalDataDirectory, "PrintFormats"));

    internal DocumentPrintFormatStore(string directory) => _directory = directory;

    public DocumentPrintFormat Load(string key) => Load(key, out _);

    public DocumentPrintFormat Load(string key, out string? warning)
    {
        var path = FilePath(key);
        warning = null;
        try
        {
            if (!File.Exists(path)) return DocumentPrintFormat.Default();
            if (new FileInfo(path).Length > MaximumProfileBytes) throw new InvalidDataException("File format terlalu besar.");
            var format = JsonSerializer.Deserialize<DocumentPrintFormat>(File.ReadAllText(path))
                ?? throw new InvalidDataException("File format kosong.");
            Validate(format);
            return format;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            warning = "Format tersimpan tidak dapat dibaca. Format bawaan ditampilkan; file lama tidak diubah.";
            return DocumentPrintFormat.Default();
        }
    }

    public void Save(string key, DocumentPrintFormat format)
    {
        var path = FilePath(key);
        Validate(format);
        Directory.CreateDirectory(_directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, format, JsonOptions);
                if (stream.Length > MaximumProfileBytes) throw new InvalidDataException("Ukuran gabungan gambar terlalu besar.");
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string FilePath(string key) => Path.Combine(_directory, DocumentPrintFormats.Get(key).Key + ".json");

    public static void Validate(DocumentPrintFormat format)
    {
        if (format.SchemaVersion != 1 || !Enum.IsDefined(format.Template) || !Enum.IsDefined(format.Letterhead))
            throw new InvalidDataException("Versi atau pilihan format tidak valid.");
        if (string.IsNullOrWhiteSpace(format.CompanyName) || format.CompanyName.Length > 160)
            throw new InvalidDataException("Nama perusahaan wajib diisi, maksimal 160 karakter.");
        if (format.MinimumRows is < 1 or > 60) throw new InvalidDataException("Minimal baris harus antara 1 dan 60.");
        if (format.ImageHeight is < 40 or > 160) throw new InvalidDataException("Tinggi kop harus antara 40 dan 160.");
        if (format.Letterhead == LetterheadKind.Image && string.IsNullOrEmpty(format.ImageBase64))
            throw new InvalidDataException("Pilih gambar kop terlebih dahulu.");
        if (!string.IsNullOrEmpty(format.ImageBase64)) _ = DecodeImage(format.ImageBase64);
        foreach (var signature in new[] { format.Signature1Image, format.Signature2Image, format.Signature3Image })
        {
            if (signature is null) continue;
            if (string.IsNullOrWhiteSpace(signature.ImageBase64)) throw new InvalidDataException("Gambar tanda tangan kosong atau tidak valid.");
            _ = DecodeImage(signature.ImageBase64);
        }
        foreach (var name in new[] { format.Signature1Name, format.Signature2Name })
            if (name?.Length > 80) throw new InvalidDataException("Nama penandatangan maksimal 80 karakter.");
        if (format.HeaderLayout is { } layout) PrintHeaderLayout.Validate(layout, format.ImageBase64);
    }

    public static string ImportImage(string path)
    {
        if (new FileInfo(path).Length > MaximumImageBytes) throw new InvalidDataException("Ukuran gambar maksimal 5 MB.");
        var data = Convert.ToBase64String(File.ReadAllBytes(path));
        _ = DecodeImage(data);
        return data;
    }

    public static BitmapSource DecodeImage(string data)
    {
        try
        {
            if (data.Length > MaximumImageBytes * 4 / 3 + 4) throw new InvalidDataException("Ukuran gambar maksimal 5 MB.");
            var bytes = Convert.FromBase64String(data);
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
            var frame = decoder.Frames[0];
            if (frame.PixelWidth < 1 || frame.PixelHeight < 1 || (long)frame.PixelWidth * frame.PixelHeight > 24_000_000)
                throw new InvalidDataException("Resolusi gambar maksimal 24 megapiksel.");
            stream.Position = 0;
            var loaded = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            loaded.Freeze();
            return loaded;
        }
        catch (Exception exception) when (exception is FormatException or NotSupportedException or ArgumentException)
        {
            throw new InvalidDataException("Gambar tidak valid. Gunakan PNG, JPG, atau BMP.", exception);
        }
    }
}
