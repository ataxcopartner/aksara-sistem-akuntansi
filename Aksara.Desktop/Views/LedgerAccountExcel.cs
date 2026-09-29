using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Aksara.Desktop.Views;

internal sealed record LedgerAccountImportRow(
    int RowNumber,
    string Code,
    string Name,
    string AccountType,
    string NormalBalance,
    string? ParentCode,
    bool IsPostable,
    bool IsCashBank,
    bool IsActive);

internal static class LedgerAccountExcel
{
    private static readonly XNamespace Spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace OfficeRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static IReadOnlyList<LedgerAccountImportRow> Read(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("File Excel tidak ditemukan.", path);
        if (!Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("File impor harus menggunakan format Excel .xlsx.");

        using var archive = ZipFile.OpenRead(path);
        var worksheetPath = ResolveFirstWorksheetPath(archive);
        var worksheet = LoadXml(archive, worksheetPath);
        var sharedStrings = ReadSharedStrings(archive);
        var rows = worksheet.Descendants(Spreadsheet + "row")
            .Select(row => ReadRow(row, sharedStrings))
            .Where(row => row.Cells.Values.Any(value => !string.IsNullOrWhiteSpace(value)))
            .ToArray();
        if (rows.Length == 0) throw new InvalidOperationException("Workbook tidak memiliki tabel COA.");

        var headers = rows[0].Cells.ToDictionary(cell => NormalizeHeader(cell.Value), cell => cell.Key, StringComparer.OrdinalIgnoreCase);
        var codeColumn = RequiredHeader(headers, "KODEPERKIRAAN");
        var nameColumn = RequiredHeader(headers, "NAMAPERKIRAAN");
        var typeColumn = RequiredHeader(headers, "JENIS");
        headers.TryGetValue("SALDONORMAL", out var normalColumn);
        headers.TryGetValue("KODEINDUK", out var parentColumn);
        headers.TryGetValue("DAPATDIPOSTING", out var postableColumn);
        headers.TryGetValue("KASBANK", out var cashBankColumn);
        headers.TryGetValue("AKTIF", out var activeColumn);

        var result = new List<LedgerAccountImportRow>();
        foreach (var row in rows.Skip(1))
        {
            var code = Value(row, codeColumn).Trim();
            var name = Value(row, nameColumn).Trim();
            var rawType = Value(row, typeColumn).Trim();
            if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(rawType)) continue;
            if (string.IsNullOrWhiteSpace(code)) throw Error(row, "Kode Perkiraan wajib diisi.");
            if (string.IsNullOrWhiteSpace(name)) throw Error(row, "Nama Perkiraan wajib diisi.");

            var type = AccountType(rawType, row.RowNumber);
            var normal = string.IsNullOrWhiteSpace(Value(row, normalColumn))
                ? type is "ASSET" or "EXPENSE" or "COGS" ? "D" : "C"
                : NormalBalance(Value(row, normalColumn), row.RowNumber);
            var parent = Empty(Value(row, parentColumn));
            if (parent?.Equals(code, StringComparison.OrdinalIgnoreCase) == true)
                throw Error(row, "Kode Induk tidak boleh sama dengan Kode Perkiraan.");

            result.Add(new(
                row.RowNumber,
                code,
                name,
                type,
                normal,
                parent,
                Boolean(Value(row, postableColumn), defaultValue: true, row.RowNumber, "Dapat Diposting"),
                Boolean(Value(row, cashBankColumn), defaultValue: false, row.RowNumber, "Kas Bank"),
                Boolean(Value(row, activeColumn), defaultValue: true, row.RowNumber, "Aktif")));
        }

        if (result.Count == 0) throw new InvalidOperationException("Tidak ada baris COA yang dapat diimpor.");
        var duplicate = result.GroupBy(row => row.Code, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Kode Perkiraan {duplicate.Key} muncul lebih dari sekali pada template.");
        return result;
    }

    private static string ResolveFirstWorksheetPath(ZipArchive archive)
    {
        var workbook = LoadXml(archive, "xl/workbook.xml");
        var firstSheet = workbook.Descendants(Spreadsheet + "sheet").FirstOrDefault()
            ?? throw new InvalidOperationException("Workbook tidak memiliki worksheet.");
        var relationshipId = firstSheet.Attribute(OfficeRelationships + "id")?.Value
            ?? throw new InvalidOperationException("Worksheet pertama tidak memiliki relasi yang valid.");
        var relationships = LoadXml(archive, "xl/_rels/workbook.xml.rels");
        var target = relationships.Descendants(PackageRelationships + "Relationship")
            .FirstOrDefault(item => item.Attribute("Id")?.Value == relationshipId)
            ?.Attribute("Target")?.Value
            ?? throw new InvalidOperationException("Worksheet pertama tidak ditemukan dalam workbook.");
        return target.StartsWith('/') ? target.TrimStart('/') : $"xl/{target.TrimStart('/')}";
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return Array.Empty<string>();
        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        return document.Descendants(Spreadsheet + "si")
            .Select(item => string.Concat(item.Descendants(Spreadsheet + "t").Select(text => text.Value)))
            .ToArray();
    }

    private static ExcelRow ReadRow(XElement row, IReadOnlyList<string> sharedStrings)
    {
        var number = int.TryParse(row.Attribute("r")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        var cells = new Dictionary<int, string>();
        foreach (var cell in row.Elements(Spreadsheet + "c"))
        {
            var reference = cell.Attribute("r")?.Value ?? string.Empty;
            var column = ColumnIndex(reference);
            if (column <= 0) continue;
            var type = cell.Attribute("t")?.Value;
            var raw = cell.Element(Spreadsheet + "v")?.Value ?? string.Empty;
            var value = type switch
            {
                "s" when int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < sharedStrings.Count => sharedStrings[index],
                "inlineStr" => string.Concat(cell.Descendants(Spreadsheet + "t").Select(text => text.Value)),
                "b" => raw == "1" ? "YA" : "TIDAK",
                _ => raw
            };
            cells[column] = value;
        }
        return new(number, cells);
    }

    private static XDocument LoadXml(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName.Replace('\\', '/'))
            ?? throw new InvalidOperationException($"Bagian workbook {entryName} tidak ditemukan.");
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static int RequiredHeader(IReadOnlyDictionary<string, int> headers, string name) =>
        headers.TryGetValue(name, out var column)
            ? column
            : throw new InvalidOperationException($"Kolom wajib {HeaderCaption(name)} tidak ditemukan. Gunakan template COA dari AKSARA.");

    private static string Value(ExcelRow row, int column) => column > 0 && row.Cells.TryGetValue(column, out var value) ? value : string.Empty;

    private static string AccountType(string value, int row) => value.Trim().ToUpperInvariant() switch
    {
        "ASSET" or "AKTIVA" or "ASET" => "ASSET",
        "LIABILITY" or "KEWAJIBAN" or "LIABILITAS" or "HUTANG" or "UTANG" => "LIABILITY",
        "EQUITY" or "EKUITAS" or "MODAL" => "EQUITY",
        "REVENUE" or "PENDAPATAN" => "REVENUE",
        "EXPENSE" or "BEBAN" or "BIAYA" => "EXPENSE",
        "COGS" or "HPP" or "HARGAPOKOK" => "COGS",
        _ => throw new InvalidOperationException($"Baris {row}: Jenis akun '{value}' tidak valid. Gunakan ASSET, LIABILITY, EQUITY, REVENUE, EXPENSE, atau COGS.")
    };

    private static string NormalBalance(string value, int row) => value.Trim().ToUpperInvariant() switch
    {
        "D" or "DEBIT" => "D",
        "C" or "K" or "CREDIT" or "KREDIT" => "C",
        _ => throw new InvalidOperationException($"Baris {row}: Saldo Normal harus D atau C.")
    };

    private static bool Boolean(string value, bool defaultValue, int row, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        return value.Trim().ToUpperInvariant() switch
        {
            "YA" or "Y" or "YES" or "TRUE" or "1" or "AKTIF" => true,
            "TIDAK" or "T" or "N" or "NO" or "FALSE" or "0" or "NONAKTIF" => false,
            _ => throw new InvalidOperationException($"Baris {row}: {label} harus diisi YA atau TIDAK.")
        };
    }

    private static string NormalizeHeader(string value) => Regex.Replace(value.Trim().ToUpperInvariant(), "[^A-Z0-9]", string.Empty);
    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static InvalidOperationException Error(ExcelRow row, string message) => new($"Baris {row.RowNumber}: {message}");
    private static string HeaderCaption(string normalized) => normalized switch
    {
        "KODEPERKIRAAN" => "Kode Perkiraan",
        "NAMAPERKIRAAN" => "Nama Perkiraan",
        "JENIS" => "Jenis",
        _ => normalized
    };

    private static int ColumnIndex(string reference)
    {
        var result = 0;
        foreach (var character in reference)
        {
            if (!char.IsLetter(character)) break;
            result = result * 26 + char.ToUpperInvariant(character) - 'A' + 1;
        }
        return result;
    }

    private sealed record ExcelRow(int RowNumber, IReadOnlyDictionary<int, string> Cells);
}
