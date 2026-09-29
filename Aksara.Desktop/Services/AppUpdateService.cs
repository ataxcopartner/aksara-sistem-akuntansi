using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Aksara.Desktop.Services;

public sealed record AppUpdateManifest(
    string Version,
    string? PackageUrl,
    string? Sha256,
    string? ReleaseNotes,
    DateTimeOffset? PublishedAt);

public sealed record AppVersionCheckResult(
    string CurrentVersion,
    string LatestVersion,
    bool IsUpdateAvailable,
    AppUpdateManifest Manifest);

public static class AppUpdateService
{
    private const string DefaultManifestUrl = "https://aksara-web-202608281007-209855272730.asia-southeast2.run.app/api/update/manifest";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private static string? _profileManifestUrl;

    public static string CurrentVersion => FormatVersion(NormalizeVersion(
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0)));

    public static void UseCompanyProfile(CompanyConnectionProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.UpdateManifestUrl) &&
            !Uri.TryCreate(profile.UpdateManifestUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("Alamat manifest pembaruan profil perusahaan tidak valid.");
        Volatile.Write(ref _profileManifestUrl, profile.UpdateManifestUrl?.Trim());
    }

    public static async Task<AppVersionCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        EnsureUpdatesEnabled();
        var manifestUri = ResolveManifestUri();
        AppUpdateManifest? manifest;

        if (manifestUri.IsFile)
        {
            await using var stream = File.OpenRead(manifestUri.LocalPath);
            manifest = await JsonSerializer.DeserializeAsync<AppUpdateManifest>(stream, JsonOptions, cancellationToken);
        }
        else
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var response = await client.GetAsync(manifestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Informasi versi terbaru belum dapat diakses ({(int)response.StatusCode}).");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            manifest = await JsonSerializer.DeserializeAsync<AppUpdateManifest>(stream, JsonOptions, cancellationToken);
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidOperationException("Informasi versi terbaru tidak valid.");

        manifest = ResolvePackageUrl(manifest, manifestUri);

        var current = NormalizeVersion(ParseVersion(CurrentVersion));
        var latest = NormalizeVersion(ParseVersion(manifest.Version));
        return new(CurrentVersion, FormatVersion(latest), latest > current, manifest);
    }

    public static async Task<string> DownloadPackageAsync(
        AppUpdateManifest manifest,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureUpdatesEnabled();
        if (!Uri.TryCreate(manifest.PackageUrl, UriKind.Absolute, out var packageUri))
            throw new InvalidOperationException("Alamat paket pembaruan belum tersedia.");
        if (!packageUri.IsFile && packageUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Paket pembaruan wajib menggunakan koneksi HTTPS.");
        if (string.IsNullOrWhiteSpace(manifest.Sha256)
            || manifest.Sha256.Length != 64
            || manifest.Sha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("Paket pembaruan belum memiliki checksum SHA-256 yang valid.");

        var updateDirectory = Path.Combine(AppBrand.LocalDataDirectory, "Updates", manifest.Version.Trim());
        Directory.CreateDirectory(updateDirectory);
        var fileName = Path.GetFileName(packageUri.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "AKSARA-update.zip";
        var destination = Path.Combine(updateDirectory, fileName);
        var partial = destination + ".partial";

        if (packageUri.IsFile)
        {
            File.Copy(packageUri.LocalPath, partial, overwrite: true);
            progress?.Report(1);
        }
        else
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            using var response = await client.GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Paket pembaruan belum dapat diunduh ({(int)response.StatusCode}).");

            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long copied = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                copied += count;
                if (total > 0) progress?.Report((double)copied / total.Value);
            }
        }

        await using (var package = File.OpenRead(partial))
        {
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(package, cancellationToken));
            if (!actualHash.Equals(manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Checksum paket pembaruan tidak sesuai. Pemasangan dibatalkan.");
        }

        File.Move(partial, destination, overwrite: true);
        return destination;
    }

    public static void BeginInstall(string packagePath)
    {
        EnsureUpdatesEnabled();
        if (!File.Exists(packagePath)) throw new FileNotFoundException("Paket pembaruan tidak ditemukan.", packagePath);
        var executablePath = Environment.ProcessPath ?? throw new InvalidOperationException("Lokasi AKSARA.exe tidak ditemukan.");
        var targetDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var workDirectory = Path.Combine(
            AppBrand.LocalDataDirectory,
            "Updates", "install-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(workDirectory);
        var scriptPath = Path.Combine(workDirectory, "install-update.ps1");
        var progressScriptPath = Path.Combine(workDirectory, "show-progress.ps1");
        var logPath = Path.Combine(workDirectory, "update.log");
        var statusPath = Path.Combine(workDirectory, "update-status.txt");
        File.WriteAllText(scriptPath, InstallerScript);
        File.WriteAllText(progressScriptPath, ProgressScript);

        var progressStartInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-File", progressScriptPath,
                     "-StatusPath", statusPath,
                     "-LogPath", logPath
                 })
            progressStartInfo.ArgumentList.Add(argument);

        var progressProcess = Process.Start(progressStartInfo)
            ?? throw new InvalidOperationException("Jendela progres pembaruan tidak dapat dibuka.");

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
                     "-ProcessId", Environment.ProcessId.ToString(),
                     "-PackagePath", packagePath,
                     "-TargetDirectory", targetDirectory,
                     "-ExecutableName", Path.GetFileName(executablePath),
                     "-WorkDirectory", workDirectory,
                     "-LogPath", logPath,
                     "-StatusPath", statusPath
                 })
            startInfo.ArgumentList.Add(argument);

        try
        {
            if (Process.Start(startInfo) is null)
                throw new InvalidOperationException("Program pemasang pembaruan tidak dapat dijalankan.");
        }
        catch
        {
            if (!progressProcess.HasExited) progressProcess.Kill();
            throw;
        }
    }

    private static Uri ResolveManifestUri()
    {
        var environmentValue = Environment.GetEnvironmentVariable("AKSARA_UPDATE_MANIFEST_URL");
        if (Uri.TryCreate(environmentValue, UriKind.Absolute, out var environmentUri)) return environmentUri;

        var profileValue = Volatile.Read(ref _profileManifestUrl);
        if (Uri.TryCreate(profileValue, UriKind.Absolute, out var profileUri)) return profileUri;

        var paths = new[]
        {
            AksaraApiClient.LocalConfigurationPath,
            Path.Combine(AppContext.BaseDirectory, "aksara-client.json")
        };
        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("updateManifestUrl", out var value)
                    && Uri.TryCreate(value.GetString(), UriKind.Absolute, out var configuredUri))
                    return configuredUri;
            }
            catch (JsonException)
            {
                // Konfigurasi koneksi yang rusak ditangani oleh layar konfigurasi server.
            }
        }

        return new Uri(DefaultManifestUrl);
    }

    private static AppUpdateManifest ResolvePackageUrl(AppUpdateManifest manifest, Uri manifestUri)
    {
        if (string.IsNullOrWhiteSpace(manifest.PackageUrl) ||
            !Uri.TryCreate(manifest.PackageUrl, UriKind.RelativeOrAbsolute, out var packageUri))
            return manifest;

        var absolutePackageUri = packageUri.IsAbsoluteUri ? packageUri : new Uri(manifestUri, packageUri);
        return manifest with { PackageUrl = absolutePackageUri.AbsoluteUri };
    }

    private static void EnsureUpdatesEnabled()
    {
        if (!AppBrand.UpdatesEnabled)
            throw new InvalidOperationException("Pembaruan dinonaktifkan untuk profil aplikasi ini.");
    }

    private static Version ParseVersion(string value)
    {
        var normalized = value.Trim().TrimStart('v', 'V').Split('-', 2)[0];
        if (!Version.TryParse(normalized, out var version))
            throw new InvalidOperationException($"Nomor versi '{value}' tidak valid.");
        return version;
    }

    private static Version NormalizeVersion(Version version) => new(
        Math.Max(0, version.Major),
        Math.Max(0, version.Minor),
        Math.Max(0, version.Build),
        Math.Max(0, version.Revision));

    private static string FormatVersion(Version version) => $"{version.Major}.{version.Minor}.{version.Build}";

    private const string InstallerScript = """
        param(
            [int]$ProcessId,
            [string]$PackagePath,
            [string]$TargetDirectory,
            [string]$ExecutableName,
            [string]$WorkDirectory,
            [string]$LogPath,
            [string]$StatusPath
        )

        $ErrorActionPreference = 'Stop'
        $stageDirectory = Join-Path $WorkDirectory 'staging'
        $backupDirectory = Join-Path $WorkDirectory 'backup'
        try {
            'WAITING' | Set-Content -LiteralPath $StatusPath
            Wait-Process -Id $ProcessId -Timeout 120 -ErrorAction SilentlyContinue
            if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
                throw 'AKSARA belum berhasil ditutup. Pembaruan dibatalkan.'
            }
            'EXTRACTING' | Set-Content -LiteralPath $StatusPath
            New-Item -ItemType Directory -Path $stageDirectory -Force | Out-Null
            New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
            Expand-Archive -LiteralPath $PackagePath -DestinationPath $stageDirectory -Force

            $newExecutable = Get-ChildItem -LiteralPath $stageDirectory -Filter $ExecutableName -File -Recurse | Select-Object -First 1
            if ($null -eq $newExecutable) { throw "Paket tidak berisi $ExecutableName." }
            $sourceDirectory = $newExecutable.Directory.FullName

            'BACKUP' | Set-Content -LiteralPath $StatusPath
            Get-ChildItem -LiteralPath $TargetDirectory -File | Copy-Item -Destination $backupDirectory -Force
            'INSTALLING' | Set-Content -LiteralPath $StatusPath
            Get-ChildItem -LiteralPath $sourceDirectory -File | Copy-Item -Destination $TargetDirectory -Force
            Get-ChildItem -LiteralPath $sourceDirectory -Directory | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination $TargetDirectory -Recurse -Force
            }

            'RESTARTING' | Set-Content -LiteralPath $StatusPath
            Start-Process -FilePath (Join-Path $TargetDirectory $ExecutableName) -WorkingDirectory $TargetDirectory
            "Pembaruan selesai pada $(Get-Date -Format o)." | Set-Content -LiteralPath $LogPath
            'COMPLETE' | Set-Content -LiteralPath $StatusPath
        }
        catch {
            ($_ | Out-String) | Set-Content -LiteralPath $LogPath
            try {
                if (Test-Path -LiteralPath $backupDirectory) {
                    Get-ChildItem -LiteralPath $backupDirectory -File | Copy-Item -Destination $TargetDirectory -Force
                }
                $currentExecutable = Join-Path $TargetDirectory $ExecutableName
                if (Test-Path -LiteralPath $currentExecutable) {
                    Start-Process -FilePath $currentExecutable -WorkingDirectory $TargetDirectory
                }
            }
            finally {
                'FAILED' | Set-Content -LiteralPath $StatusPath
            }
        }
        """;

    private const string ProgressScript = """
        param(
            [string]$StatusPath,
            [string]$LogPath
        )

        Add-Type -AssemblyName PresentationFramework
        $window = [System.Windows.Window]::new()
        $window.Title = 'AKSARA - Update'
        $window.Width = 440
        $window.Height = 190
        $window.MinWidth = 440
        $window.MinHeight = 190
        $window.ResizeMode = [System.Windows.ResizeMode]::NoResize
        $window.WindowStartupLocation = [System.Windows.WindowStartupLocation]::CenterScreen
        $window.Topmost = $true
        $window.Background = [System.Windows.Media.Brushes]::White

        $content = [System.Windows.Controls.StackPanel]::new()
        $content.Margin = [System.Windows.Thickness]::new(20)
        $heading = [System.Windows.Controls.TextBlock]::new()
        $heading.Text = 'Memperbarui AKSARA'
        $heading.FontSize = 18
        $heading.FontWeight = [System.Windows.FontWeights]::Bold
        $heading.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#74101C')
        $content.Children.Add($heading) | Out-Null

        $message = [System.Windows.Controls.TextBlock]::new()
        $message.Text = 'Menunggu AKSARA ditutup...'
        $message.Margin = [System.Windows.Thickness]::new(0, 14, 0, 12)
        $message.TextWrapping = [System.Windows.TextWrapping]::Wrap
        $content.Children.Add($message) | Out-Null

        $progress = [System.Windows.Controls.ProgressBar]::new()
        $progress.Height = 12
        $progress.IsIndeterminate = $true
        $content.Children.Add($progress) | Out-Null

        $closeButton = [System.Windows.Controls.Button]::new()
        $closeButton.Content = 'Tutup'
        $closeButton.Width = 90
        $closeButton.Height = 29
        $closeButton.HorizontalAlignment = [System.Windows.HorizontalAlignment]::Right
        $closeButton.Margin = [System.Windows.Thickness]::new(0, 12, 0, 0)
        $closeButton.Visibility = [System.Windows.Visibility]::Collapsed
        $closeButton.Add_Click({ $window.Close() })
        $content.Children.Add($closeButton) | Out-Null
        $window.Content = $content

        $script:completedAt = $null
        $startedAt = Get-Date
        $timer = [System.Windows.Threading.DispatcherTimer]::new()
        $timer.Interval = [TimeSpan]::FromMilliseconds(350)
        $timer.Add_Tick({
            try {
                $phase = if (Test-Path -LiteralPath $StatusPath) {
                    (Get-Content -LiteralPath $StatusPath -Raw).Trim()
                } else { 'WAITING' }
            } catch { return }

            switch ($phase) {
                'WAITING' { $message.Text = 'Menunggu AKSARA ditutup...' }
                'EXTRACTING' { $message.Text = 'Menyiapkan paket pembaruan...' }
                'BACKUP' { $message.Text = 'Menyimpan cadangan aplikasi...' }
                'INSTALLING' { $message.Text = 'Memasang pembaruan...' }
                'RESTARTING' { $message.Text = 'Membuka kembali AKSARA...' }
                'COMPLETE' {
                    $message.Text = 'Pembaruan selesai. AKSARA sudah dibuka kembali.'
                    $progress.IsIndeterminate = $false
                    $progress.Value = 100
                    if ($null -eq $script:completedAt) { $script:completedAt = Get-Date }
                    if ((Get-Date) - $script:completedAt -gt [TimeSpan]::FromSeconds(2)) {
                        $timer.Stop()
                        $window.Close()
                    }
                }
                'FAILED' {
                    $message.Text = 'Pembaruan gagal. AKSARA lama akan dibuka kembali.'
                    $progress.IsIndeterminate = $false
                    $closeButton.Visibility = [System.Windows.Visibility]::Visible
                    $timer.Stop()
                }
            }
            if ((Get-Date) - $startedAt -gt [TimeSpan]::FromMinutes(5)) {
                $message.Text = "Pembaruan belum selesai. Periksa log: $LogPath"
                $progress.IsIndeterminate = $false
                $closeButton.Visibility = [System.Windows.Visibility]::Visible
                $timer.Stop()
            }
        })
        $timer.Start()
        $window.ShowDialog() | Out-Null
        """;
}
