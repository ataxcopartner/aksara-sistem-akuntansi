using Aksara.Contracts;
using Npgsql;

namespace Aksara.Infrastructure;

public sealed class CompanyIdentityRepository(PostgresDatabase database)
{
    public async Task<CompanyIdentityDto> GetAsync(CancellationToken cancellationToken = default)
    {
        const string sql = "select instance_id,company_code,company_name,isolation_mode from company_instance where singleton;";
        await using var command = database.DataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Identitas perusahaan belum tersedia.");
        return new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }

    internal static async Task<CompanyIdentityDto> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select instance_id,company_code,company_name,isolation_mode from company_instance where singleton;", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Identitas perusahaan belum tersedia.");
        return new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }
}
