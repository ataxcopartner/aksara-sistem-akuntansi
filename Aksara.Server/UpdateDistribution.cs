using System.Text.Json;
using Aksara.Contracts;

namespace Aksara.Server;

public static class UpdateDistribution
{
    private const string ManifestFileName = "update-manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/update/manifest", GetManifestAsync);
        app.MapGet("/api/update/packages/{fileName}", GetPackageAsync);
    }

    private static async Task<IResult> GetManifestAsync(
        HttpContext context,
        IWebHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store, max-age=0";
        var updateDirectory = GetUpdateDirectory(environment);
        var manifestPath = Path.Combine(updateDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
            return CurrentVersion();

        UpdateReleaseDescriptor? descriptor;
        try
        {
            await using var stream = File.OpenRead(manifestPath);
            descriptor = await JsonSerializer.DeserializeAsync<UpdateReleaseDescriptor>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return Error(StatusCodes.Status500InternalServerError, "UPDATE_MANIFEST_INVALID", "Manifest pembaruan pada server tidak valid.");
        }

        if (!IsValidDescriptor(descriptor))
            return Error(StatusCodes.Status500InternalServerError, "UPDATE_MANIFEST_INVALID", "Manifest pembaruan pada server tidak lengkap.");

        var packagePath = Path.Combine(updateDirectory, descriptor!.PackageFile);
        if (!File.Exists(packagePath))
            return Error(StatusCodes.Status503ServiceUnavailable, "UPDATE_PACKAGE_MISSING", "Paket pembaruan belum tersedia pada server.");

        return Results.Ok(new
        {
            version = descriptor.Version.Trim(),
            packageUrl = BuildPackageUrl(context.Request, descriptor.PackageFile),
            sha256 = descriptor.Sha256.Trim().ToUpperInvariant(),
            releaseNotes = descriptor.ReleaseNotes?.Trim() ?? string.Empty,
            publishedAt = descriptor.PublishedAt
        });
    }

    private static async Task GetPackageAsync(
        string fileName,
        HttpContext context,
        IWebHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (!IsSafePackageFileName(fileName))
        {
            await Error(StatusCodes.Status400BadRequest, "UPDATE_PACKAGE_INVALID", "Nama paket pembaruan tidak valid.")
                .ExecuteAsync(context);
            return;
        }

        var packagePath = Path.Combine(GetUpdateDirectory(environment), fileName);
        if (!File.Exists(packagePath))
        {
            await Error(StatusCodes.Status404NotFound, "UPDATE_PACKAGE_NOT_FOUND", "Paket pembaruan tidak ditemukan.")
                .ExecuteAsync(context);
            return;
        }

        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        if (context.Request.Headers.ContainsKey("Range"))
        {
            await Results.File(packagePath, "application/zip", fileName, enableRangeProcessing: true)
                .ExecuteAsync(context);
            return;
        }

        // Cloud Run limits buffered HTTP/1 responses to 32 MiB. Leaving Content-Length unset
        // makes Kestrel stream the portable package with chunked transfer encoding.
        context.Response.ContentType = "application/zip";
        context.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
        await using var package = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        await package.CopyToAsync(context.Response.Body, 1024 * 1024, cancellationToken);
    }

    private static bool IsValidDescriptor(UpdateReleaseDescriptor? descriptor) =>
        descriptor is not null &&
        Version.TryParse(descriptor.Version?.Trim(), out _) &&
        IsSafePackageFileName(descriptor.PackageFile) &&
        descriptor.Sha256 is { Length: 64 } hash &&
        hash.All(Uri.IsHexDigit);

    private static bool IsSafePackageFileName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) &&
        fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private static string GetUpdateDirectory(IWebHostEnvironment environment) =>
        Path.Combine(environment.ContentRootPath, "Updates");

    private static string BuildPackageUrl(HttpRequest request, string packageFile)
    {
        var forwardedScheme = request.Headers["X-Forwarded-Proto"].ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        var scheme = string.Equals(forwardedScheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? Uri.UriSchemeHttps
            : request.Scheme;
        return $"{scheme}://{request.Host}{request.PathBase}/api/update/packages/{Uri.EscapeDataString(packageFile)}";
    }

    private static IResult CurrentVersion()
    {
        var version = typeof(UpdateDistribution).Assembly.GetName().Version ?? new Version(0, 0, 0);
        return Results.Ok(new
        {
            version = $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}",
            packageUrl = (string?)null,
            sha256 = (string?)null,
            releaseNotes = "Belum ada pembaruan baru yang diterbitkan.",
            publishedAt = (DateTimeOffset?)null
        });
    }

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new ApiError(code, message), statusCode: statusCode);

    private sealed record UpdateReleaseDescriptor(
        string Version,
        string PackageFile,
        string Sha256,
        string? ReleaseNotes,
        DateTimeOffset? PublishedAt);
}
