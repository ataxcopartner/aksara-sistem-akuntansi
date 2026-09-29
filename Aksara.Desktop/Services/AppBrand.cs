using System.IO;

namespace Aksara.Desktop.Services;

public static class AppBrand
{
    public static string ProductName { get; } = Read("AKSARA_APP_NAME", "AKSARA");

    public static string EnvironmentLabel { get; } = Read("AKSARA_ENVIRONMENT_LABEL", "");

    public static string LocalDataDirectory { get; } = ResolveLocalDataDirectory();

    public static bool IsServerAddressLocked { get; } = ReadBoolean("AKSARA_SERVER_LOCKED", false);

    public static bool UpdatesEnabled { get; } = ReadBoolean("AKSARA_UPDATES_ENABLED", true);

    public static string MainTitle { get; } = WithEnvironmentLabel(Read(
        "AKSARA_APP_TITLE",
        ProductName + " - Sistem Akuntansi dan Administrasi"));

    public static string LoginTitle => WithEnvironmentLabel(ProductName + " - Login");

    public static string StatusPrefix => string.IsNullOrWhiteSpace(EnvironmentLabel)
        ? ProductName
        : ProductName + " | " + EnvironmentLabel;

    private static string Read(string key, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static bool ReadBoolean(string key, bool fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return bool.TryParse(value, out var result) ? result : fallback;
    }

    private static string ResolveLocalDataDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("AKSARA_PROFILE_DIRECTORY");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AKSARA")
            : Path.GetFullPath(configured.Trim());
    }

    private static string WithEnvironmentLabel(string title) =>
        string.IsNullOrWhiteSpace(EnvironmentLabel) || title.Contains(EnvironmentLabel, StringComparison.OrdinalIgnoreCase)
            ? title
            : title + " | " + EnvironmentLabel;
}
