using System.IO;
using System.Text.Json;

namespace Aksara.Desktop.Services;

public static class InventoryUnitCatalog
{
    private static readonly string[] CpsUnits =
    [
        "BIJI", "BOTOL", "BOX", "BUAH", "CM", "DENAH", "DM", "DOS", "DRUM", "DUS", "DZ", "FILE",
        "FOLIO", "FOTO", "GMBR", "GRAM", "GROSS", "HARI", "HELAI", "INCH", "JAM", "KAMAR", "KANTG",
        "KG", "KILO", "KM", "KMPTR", "KODI", "KOLI", "KOMP", "KRNG", "KTK", "KURSI", "LEMBA", "LUSIN",
        "MEJA", "MENIT", "MESIN", "METER", "MILI", "MM", "MOBIL", "MOTOR", "ONS", "ONZ", "ORANG", "PACK",
        "PCS", "PIECE", "PLANO", "PLY", "PORSI", "PPS", "RIM", "ROL", "ROLL", "RUMAH", "SAK", "SET", "TAS",
        "TITIK", "UNIT", "ZAK"
    ];

    private static readonly string CatalogPath = Path.Combine(AppBrand.LocalDataDirectory, "inventory-units.json");

    public static IReadOnlyList<string> Load()
    {
        var units = new SortedSet<string>(CpsUnits, StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(CatalogPath))
            {
                var customUnits = JsonSerializer.Deserialize<string[]>(File.ReadAllText(CatalogPath)) ?? [];
                foreach (var unit in customUnits.Where(x => !string.IsNullOrWhiteSpace(x)))
                    units.Add(Normalize(unit));
            }
        }
        catch
        {
            // Daftar bawaan tetap tersedia jika konfigurasi lokal rusak.
        }

        return units.ToArray();
    }

    public static string Add(string value)
    {
        var unit = Normalize(value);
        if (unit.Length > 10) throw new InvalidOperationException("Nama satuan maksimal 10 karakter.");

        var units = new SortedSet<string>(Load(), StringComparer.OrdinalIgnoreCase) { unit };
        Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath)!);
        File.WriteAllText(CatalogPath, JsonSerializer.Serialize(units.ToArray(), new JsonSerializerOptions { WriteIndented = true }));
        return unit;
    }

    private static string Normalize(string value)
    {
        var unit = value.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(unit)) throw new InvalidOperationException("Nama satuan wajib diisi.");
        return unit;
    }
}
