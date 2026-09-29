using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aksara.Desktop.Services;

public static class LocalTransactionDraftStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static void Save<T>(string formKey, T draft)
    {
        var path = DraftPath(formKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(draft, JsonOptions), Encoding.UTF8);
        File.Move(temporaryPath, path, overwrite: true);
    }

    public static bool TryLoad<T>(string formKey, out T? draft)
    {
        draft = default;
        var path = DraftPath(formKey);
        if (!File.Exists(path)) return false;

        try
        {
            draft = JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), JsonOptions);
            return draft is not null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Delete(string formKey)
    {
        var path = DraftPath(formKey);
        if (File.Exists(path)) File.Delete(path);
    }

    private static string DraftPath(string formKey)
    {
        var user = string.IsNullOrWhiteSpace(AksaraApiClient.Shared.CurrentUserName)
            ? "anonymous"
            : AksaraApiClient.Shared.CurrentUserName.Trim().ToUpperInvariant();
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{user}\n{formKey}")))[..24];
        return Path.Combine(AppBrand.LocalDataDirectory, "Drafts", $"{identity}.json");
    }
}
