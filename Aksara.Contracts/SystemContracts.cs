namespace Aksara.Contracts;

public sealed record HealthResponse(string Status, string Application, string Version, DateTimeOffset ServerTime);

public sealed record SystemInfoResponse(
    string ProductName,
    string ProductDescription,
    string DeploymentMode,
    string DatabaseProvider,
    CompanyIdentityDto? Company);
