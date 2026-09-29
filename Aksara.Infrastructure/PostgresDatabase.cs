using Npgsql;

namespace Aksara.Infrastructure;

public sealed class PostgresDatabase(string connectionString) : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(connectionString);

    internal NpgsqlDataSource DataSource => _dataSource;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var databaseDirectory = Path.Combine(AppContext.BaseDirectory, "Database");
        foreach (var sqlPath in Directory.EnumerateFiles(databaseDirectory, "*.sql").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var sql = await File.ReadAllTextAsync(sqlPath, cancellationToken);
            await using var command = _dataSource.CreateCommand(sql);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("select 1", connection);
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
        }
        catch
        {
            return false;
        }
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
