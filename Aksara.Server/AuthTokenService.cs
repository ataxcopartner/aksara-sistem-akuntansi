using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aksara.Contracts;

namespace Aksara.Server;

public sealed record AksaraTokenPrincipal(string UserName, string DisplayName, string RoleCode, Guid CompanyInstanceId, DateTimeOffset ExpiresAt);

public sealed class AuthTokenService
{
    private static readonly byte[] HeaderBytes = Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"AKSARA"}""");
    private readonly byte[] _key;
    private CompanyIdentityDto? _company;

    public AuthTokenService(string tokenKey, TimeSpan lifetime)
    {
        if (string.IsNullOrWhiteSpace(tokenKey))
            tokenKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        _key = Encoding.UTF8.GetBytes(tokenKey);
        Lifetime = lifetime;
    }

    public TimeSpan Lifetime { get; }

    public void SetCompanyIdentity(CompanyIdentityDto company) => _company = company;

    public LoginResponse CreateLoginResponse(AuthenticatedUserDto user)
    {
        var company = _company ?? throw new InvalidOperationException("Identitas perusahaan belum dimuat.");
        var expiresAt = DateTimeOffset.UtcNow.Add(Lifetime);
        var payload = new TokenPayload(user.UserName, user.DisplayName, user.RoleCode, company.InstanceId, expiresAt.ToUnixTimeSeconds());
        var token = CreateToken(payload);
        return new LoginResponse(user.UserName, user.DisplayName, user.RoleCode, user.RoleName, user.Permissions, company, token, expiresAt);
    }

    public bool TryValidate(string token, out AksaraTokenPrincipal? principal)
    {
        principal = null;
        var parts = token.Split('.');
        if (parts.Length != 3)
            return false;

        var signed = $"{parts[0]}.{parts[1]}";
        var expectedSignature = Sign(signed);
        if (!FixedTimeEquals(expectedSignature, parts[2]))
            return false;

        try
        {
            var payloadJson = Encoding.UTF8.GetString(DecodeBase64Url(parts[1]));
            var payload = JsonSerializer.Deserialize<TokenPayload>(payloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (payload is null || string.IsNullOrWhiteSpace(payload.UserName) || _company is null || payload.CompanyInstanceId != _company.InstanceId)
                return false;

            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAtUnix);
            if (expiresAt <= DateTimeOffset.UtcNow)
                return false;

            principal = new AksaraTokenPrincipal(payload.UserName, payload.DisplayName, payload.RoleCode, payload.CompanyInstanceId, expiresAt);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private string CreateToken(TokenPayload payload)
    {
        var header = EncodeBase64Url(HeaderBytes);
        var body = EncodeBase64Url(JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var signed = $"{header}.{body}";
        return $"{signed}.{Sign(signed)}";
    }

    private string Sign(string value)
    {
        using var hmac = new HMACSHA256(_key);
        return EncodeBase64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string EncodeBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }

    private sealed record TokenPayload(string UserName, string DisplayName, string RoleCode, Guid CompanyInstanceId, long ExpiresAtUnix);
}
