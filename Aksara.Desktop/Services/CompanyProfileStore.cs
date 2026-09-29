using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aksara.Contracts;

namespace Aksara.Desktop.Services;

public sealed record CompanyConnectionProfile(
    Guid Id,
    string CompanyCode,
    string CompanyName,
    [property: JsonPropertyName("serverUrl")] string ServerAddress,
    string? UpdateManifestUrl,
    Guid? ExpectedInstanceId)
{
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(CompanyCode)
        ? CompanyName
        : $"{CompanyCode} - {CompanyName}";
}

public sealed record CompanyProfileSnapshot(
    IReadOnlyList<CompanyConnectionProfile> Profiles,
    string? LastSelectedCompanyCode);

public static class CompanyProfileStore
{
    private const int SchemaVersion = 2;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private static string FilePath => Path.Combine(AppBrand.LocalDataDirectory, "company-profiles.json");

    public static CompanyProfileSnapshot Load(string fallbackServer)
    {
        var fallbackManifest = ReadConfiguredManifest();
        foreach (var path in new[]
                 {
                     FilePath,
                     AksaraApiClient.LocalConfigurationPath,
                     Path.Combine(AppContext.BaseDirectory, "aksara-client.json")
                 }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(path)) continue;
                var snapshot = Parse(File.ReadAllText(path), fallbackServer);
                if (snapshot.Profiles.Count == 0) continue;
                snapshot = snapshot with
                {
                    Profiles = snapshot.Profiles
                        .Select(profile => string.IsNullOrWhiteSpace(profile.UpdateManifestUrl)
                            ? profile with { UpdateManifestUrl = fallbackManifest }
                            : profile)
                        .ToArray()
                };
                if (!path.Equals(FilePath, StringComparison.OrdinalIgnoreCase)) Save(snapshot.Profiles, snapshot.LastSelectedCompanyCode);
                return snapshot;
            }
            catch (JsonException) { }
            catch (IOException) { }
        }

        var fallback = CreateFallback(fallbackServer, fallbackManifest);
        Save([fallback], null);
        return new([fallback], null);
    }

    public static CompanyProfileSnapshot Parse(string json, string fallbackServer)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string? lastSelectedCode = null;
        var profiles = new List<CompanyConnectionProfile>();

        if (root.ValueKind == JsonValueKind.Array)
        {
            ReadProfiles(root, profiles);
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            lastSelectedCode = ReadString(root, "lastSelectedCompanyCode");
            if (TryGet(root, "profiles", out var configuredProfiles) && configuredProfiles.ValueKind == JsonValueKind.Array)
            {
                ReadProfiles(configuredProfiles, profiles);
            }
            else
            {
                var server = ReadString(root, "serverUrl") ?? fallbackServer;
                var manifest = ReadString(root, "updateManifestUrl");
                profiles.Add(CreateFallback(server, manifest));
            }
        }

        var normalized = profiles
            .Where(profile => Uri.TryCreate(profile.ServerAddress, UriKind.Absolute, out _))
            .GroupBy(profile => profile.Id)
            .Select(group => group.First())
            .ToList();
        return new(normalized, lastSelectedCode);
    }

    public static CompanyConnectionProfile BindVerifiedIdentity(CompanyConnectionProfile profile, CompanyIdentityDto company)
    {
        if (profile.ExpectedInstanceId is Guid expected && expected != company.InstanceId)
            throw new AksaraApiException($"Server ini milik {company.CompanyName}, bukan profil {profile.CompanyName}. Login dibatalkan untuk mencegah salah perusahaan.");
        if (!string.IsNullOrWhiteSpace(profile.CompanyCode) &&
            !profile.CompanyCode.Equals(company.CompanyCode, StringComparison.OrdinalIgnoreCase))
            throw new AksaraApiException($"Kode perusahaan server ({company.CompanyCode}) tidak sesuai profil {profile.CompanyCode}. Login dibatalkan.");

        return profile with
        {
            CompanyCode = company.CompanyCode,
            CompanyName = company.CompanyName,
            ExpectedInstanceId = company.InstanceId
        };
    }

    public static void Save(IReadOnlyList<CompanyConnectionProfile> profiles, string? lastSelectedCompanyCode)
    {
        var duplicateCode = profiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.CompanyCode))
            .GroupBy(profile => profile.CompanyCode, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateCode is not null)
            throw new InvalidOperationException($"Kode perusahaan {duplicateCode.Key} digunakan oleh lebih dari satu profil.");

        Directory.CreateDirectory(AppBrand.LocalDataDirectory);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, Serialize(profiles, lastSelectedCompanyCode));
        File.Move(temporary, FilePath, true);
    }

    public static string Serialize(IReadOnlyList<CompanyConnectionProfile> profiles, string? lastSelectedCompanyCode) =>
        JsonSerializer.Serialize(new CompanyProfilesFile(SchemaVersion, lastSelectedCompanyCode, profiles), Options);

    private static void ReadProfiles(JsonElement source, ICollection<CompanyConnectionProfile> target)
    {
        foreach (var row in source.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            var server = ReadString(row, "serverAddress") ?? ReadString(row, "serverUrl");
            if (string.IsNullOrWhiteSpace(server)) continue;
            var oldName = ReadString(row, "name");
            var companyName = ReadString(row, "companyName") ?? oldName ?? "Perusahaan Saat Ini";
            var companyCode = ReadString(row, "companyCode") ?? string.Empty;
            var manifest = ReadString(row, "updateManifestUrl");
            var id = ReadGuid(row, "id") ?? Guid.NewGuid();
            var instance = ReadGuid(row, "expectedInstanceId");
            target.Add(new(id, companyCode.Trim().ToUpperInvariant(), companyName.Trim(), server.Trim(), manifest?.Trim(), instance));
        }
    }

    private static CompanyConnectionProfile CreateFallback(string server, string? manifest) =>
        new(Guid.NewGuid(), string.Empty, "Perusahaan Saat Ini", server.Trim(), manifest?.Trim(), null);

    private static string? ReadConfiguredManifest()
    {
        foreach (var path in new[] { AksaraApiClient.LocalConfigurationPath, Path.Combine(AppContext.BaseDirectory, "aksara-client.json") })
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var value = ReadString(document.RootElement, "updateManifestUrl");
                if (Uri.TryCreate(value, UriKind.Absolute, out _)) return value;
            }
            catch (JsonException) { }
            catch (IOException) { }
        }
        return null;
    }

    private static bool TryGet(JsonElement source, string name, out JsonElement value)
    {
        foreach (var property in source.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    private static string? ReadString(JsonElement source, string name) =>
        TryGet(source, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Guid? ReadGuid(JsonElement source, string name) =>
        Guid.TryParse(ReadString(source, name), out var value) ? value : null;

    private sealed record CompanyProfilesFile(
        int SchemaVersion,
        string? LastSelectedCompanyCode,
        IReadOnlyList<CompanyConnectionProfile> Profiles);
}
