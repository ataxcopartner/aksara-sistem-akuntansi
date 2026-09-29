using Aksara.Contracts;
using Npgsql;

namespace Aksara.Infrastructure;

public sealed class AuthenticationRepository(PostgresDatabase database)
{
    public async Task<bool> UserNameExistsAsync(string? userName, CancellationToken cancellationToken = default)
    {
        var value = userName?.Trim() ?? string.Empty;
        if (value.Length == 0) return false;
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select exists(select 1 from app_user where lower(username)=lower(@username));", connection);
        command.Parameters.AddWithValue("username", value);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<AuthenticatedUserDto?> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var userName = request.UserName.Trim();
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(request.Password))
            return null;

        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            select u.id, u.username, u.display_name, u.password_hash, r.code, r.name
            from app_user u
            join app_role r on r.id = u.role_id
            where lower(u.username) = lower(@username)
              and u.is_active = true
              and r.is_active = true
            limit 1;
            """, connection);
        command.Parameters.AddWithValue("username", userName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var id = reader.GetGuid(0);
        var storedUserName = reader.GetString(1);
        var displayName = reader.GetString(2);
        var hash = reader.GetString(3);
        var roleCode = reader.GetString(4);
        var roleName = reader.GetString(5);
        await reader.DisposeAsync();

        if (!PasswordHasher.Verify(hash, request.Password))
            return null;

        await using var update = new NpgsqlCommand("update app_user set last_login_at = now(), version = version + 1 where id = @id;", connection);
        update.Parameters.AddWithValue("id", id);
        await update.ExecuteNonQueryAsync(cancellationToken);

        var permissions = await ReadPermissionsAsync(connection, roleCode, cancellationToken);
        return new AuthenticatedUserDto(id, storedUserName, displayName, roleCode, roleName, permissions);
    }

    public async Task<bool> IsAllowedAsync(string roleCode, string module, string action, CancellationToken cancellationToken = default)
    {
        if (roleCode.Equals("MASTER", StringComparison.OrdinalIgnoreCase)) return true;

        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            select coalesce((
                select p.is_allowed
                from app_permission p
                join app_role r on r.id = p.role_id
                where lower(r.code) = lower(@role) and r.is_active
                  and p.module = @module and p.action = @action
                limit 1
            ), false);
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("role", roleCode);
        command.Parameters.AddWithValue("module", module);
        command.Parameters.AddWithValue("action", action);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<bool> IsSessionActiveAsync(string userName, string roleCode, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            select exists(
                select 1 from app_user u join app_role r on r.id=u.role_id
                where lower(u.username)=lower(@username) and lower(r.code)=lower(@role)
                  and u.is_active and r.is_active
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("username", userName);
        command.Parameters.AddWithValue("role", roleCode);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    internal static async Task<IReadOnlyList<PermissionGrantDto>> ReadPermissionsAsync(NpgsqlConnection connection, string roleCode, CancellationToken cancellationToken)
    {
        if (roleCode.Equals("MASTER", StringComparison.OrdinalIgnoreCase)) return [];

        const string sql = """
            select p.module, p.action
            from app_permission p
            join app_role r on r.id = p.role_id
            where lower(r.code) = lower(@role) and r.is_active and p.is_allowed
            order by p.module, p.action;
            """;
        var result = new List<PermissionGrantDto>();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("role", roleCode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), reader.GetString(1)));
        return result;
    }
}
