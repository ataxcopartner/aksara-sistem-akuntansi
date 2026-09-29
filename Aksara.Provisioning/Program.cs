using System.Text.RegularExpressions;
using Aksara.Contracts;
using Aksara.Infrastructure;
using Npgsql;

return await Provisioner.RunAsync(args);

internal static partial class Provisioner
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Parse(args);
            if (options.ContainsKey("password"))
                throw new InvalidOperationException("Jangan kirim password melalui argumen. Gunakan AKSARA_BOOTSTRAP_PASSWORD atau input tersembunyi.");
            var technicalSuffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var databaseName = Value(options, "database", "AKSARA_NEW_DATABASE") ?? $"aksara_{technicalSuffix.ToLowerInvariant()}";
            var companyCode = (Value(options, "company-code", "AKSARA_NEW_COMPANY_CODE") ?? $"COMPANY-{technicalSuffix}").Trim().ToUpperInvariant();
            var suppliedCompanyName = Value(options, "company-name", "AKSARA_NEW_COMPANY_NAME");
            var companyName = CompanyIdentityPolicy.BootstrapCompanyName(suppliedCompanyName);
            var serverUrl = Required(options, "server-url", "AKSARA_NEW_SERVER_URL").Trim().TrimEnd('/');
            var updateManifestUrl = Value(options, "update-manifest-url", "AKSARA_NEW_UPDATE_MANIFEST_URL");
            var userName = Value(options, "superadmin-user", "AKSARA_BOOTSTRAP_USER") ?? "admin";
            var displayName = Value(options, "superadmin-name", "AKSARA_BOOTSTRAP_DISPLAY_NAME") ?? "Superadmin";
            var migrationDirectory = Value(options, "migrations", "AKSARA_MIGRATIONS_DIRECTORY")
                ?? Path.Combine(AppContext.BaseDirectory, "Database");

            Validate(databaseName, companyCode, userName, companyName, displayName, serverUrl, updateManifestUrl, migrationDirectory);
            if (Boolean(options, "plan"))
            {
                Console.WriteLine("DRY RUN - tidak ada koneksi atau perubahan database.");
                Console.WriteLine($"Kode teknis : {companyCode}");
                Console.WriteLine($"Database   : {databaseName} (database baru terisolasi)");
                Console.WriteLine($"Server     : {serverUrl}");
                Console.WriteLine($"Superadmin : {userName} - {displayName}");
                Console.WriteLine($"Nama awal  : {companyName}");
                Console.WriteLine(suppliedCompanyName is null
                    ? "Nama final  : wajib diisi pada Profil Perusahaan di Setup Data Awal."
                    : "Nama final  : akan dikonfirmasi pada Profil Perusahaan di Setup Data Awal.");
                Console.WriteLine("Data awal  : schema/migration + system seed + company identity + akun bootstrap; tanpa transaksi/master perusahaan lama.");
                return 0;
            }

            var adminConnection = Required(options, "admin-connection", "AKSARA_PROVISION_ADMIN_CONNECTION");
            var password = ReadBootstrapPassword();
            if (password.Length < 6) throw new InvalidOperationException("Password awal minimal 6 karakter.");
            if (password.Length < 10)
                Console.WriteLine("PERINGATAN: password bootstrap lebih pendek dari rekomendasi 10 karakter dan harus segera diganti setelah login.");
            Guid instanceId;
            try
            {
                instanceId = await ProvisionAsync(adminConnection, databaseName, companyCode, companyName, userName, displayName, password, migrationDirectory);
            }
            finally
            {
                password = string.Empty;
            }

            Console.WriteLine($"Provisioning perusahaan {companyCode} selesai. Database: {databaseName}.");
            Console.WriteLine($"Endpoint yang dikonfirmasi: {serverUrl}");
            Console.WriteLine("Nama perusahaan final wajib diisi atau dikonfirmasi pada Profil Perusahaan di Setup Data Awal sebelum aktivasi.");
            Console.WriteLine("Arahkan endpoint tersebut hanya ke database baru. Desktop tidak memerlukan profil perusahaan manual.");
            Console.WriteLine("Tambahkan rute berikut pada TenantDirectory server bootstrap utama:");
            Console.WriteLine($"  UserName={userName}; ServerUrl={serverUrl}; CompanyCode={companyCode}; CompanyInstanceId={instanceId}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Provisioning dibatalkan: " + exception.Message);
            return 1;
        }
    }

    private static async Task<Guid> ProvisionAsync(
        string adminConnection,
        string databaseName,
        string companyCode,
        string companyName,
        string userName,
        string displayName,
        string password,
        string migrationDirectory)
    {
        var adminBuilder = new NpgsqlConnectionStringBuilder(adminConnection);
        if (string.IsNullOrWhiteSpace(adminBuilder.Database)) adminBuilder.Database = "postgres";
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();

        var exists = await DatabaseExistsAsync(admin, databaseName);
        if (!exists)
        {
            await using var create = new NpgsqlCommand($"create database \"{databaseName}\";", admin);
            await create.ExecuteNonQueryAsync();
        }

        var targetBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString) { Database = databaseName };
        await using var target = new NpgsqlConnection(targetBuilder.ConnectionString);
        await target.OpenAsync();

        var hasGuard = await TableExistsAsync(target, "aksara_provisioning_guard");
        if (exists && !hasGuard && await PublicTableCountAsync(target) > 0)
            throw new InvalidOperationException("Database target sudah berisi tabel dan bukan database provisioning AKSARA baru. Database lama tidak disentuh.");

        if (!hasGuard)
        {
            await using var createGuard = new NpgsqlCommand("""
                create table aksara_provisioning_guard(
                    singleton boolean primary key default true check(singleton),
                    company_code varchar(40) not null,
                    database_name varchar(63) not null,
                    started_at timestamptz not null default now(),
                    completed_at timestamptz
                );
                insert into aksara_provisioning_guard(singleton,company_code,database_name)
                values(true,@code,@database);
                """, target);
            createGuard.Parameters.AddWithValue("code", companyCode);
            createGuard.Parameters.AddWithValue("database", databaseName);
            await createGuard.ExecuteNonQueryAsync();
        }
        else
        {
            await using var readGuard = new NpgsqlCommand("select company_code,database_name from aksara_provisioning_guard where singleton;", target);
            await using var reader = await readGuard.ExecuteReaderAsync();
            if (!await reader.ReadAsync() ||
                !reader.GetString(0).Equals(companyCode, StringComparison.OrdinalIgnoreCase) ||
                !reader.GetString(1).Equals(databaseName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Penanda provisioning tidak sesuai dengan perusahaan/database target. Proses ditolak.");
        }

        if (await TableExistsAsync(target, "company_instance"))
            await EnsureExistingIdentityIsSafeAsync(target, companyCode);

        foreach (var migrationPath in Directory.EnumerateFiles(migrationDirectory, "*.sql").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var sql = await File.ReadAllTextAsync(migrationPath);
            await using var transaction = await target.BeginTransactionAsync();
            await using var migration = new NpgsqlCommand(sql, target, transaction) { CommandTimeout = 180 };
            await migration.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        await EnsureExistingIdentityIsSafeAsync(target, companyCode, allowDefaultCode: true);
        await using var finalize = new NpgsqlCommand("""
            update company_instance
            set company_code=@code, company_name=@name, version=version+1
            where singleton;
            update program_configuration
            set company_name=@name, updated_at=now(), version=version+1
            where singleton;
            update initial_setup
            set profile_confirmed=false, version=version+1
            where singleton and status='DRAFT' and not legacy_protected;
            """, target);
        finalize.Parameters.AddWithValue("code", companyCode);
        finalize.Parameters.AddWithValue("name", companyName);
        await finalize.ExecuteNonQueryAsync();

        var existingRole = await ExistingUserRoleAsync(target, userName);
        if (existingRole is null)
        {
            await using var createUser = new NpgsqlCommand("""
                insert into app_user(username,display_name,password_hash,role_id,is_active)
                select @username,@display,@hash,id,true from app_role where code='MASTER';
                """, target);
            createUser.Parameters.AddWithValue("username", userName.Trim());
            createUser.Parameters.AddWithValue("display", displayName.Trim());
            createUser.Parameters.AddWithValue("hash", PasswordHasher.Hash(password));
            if (await createUser.ExecuteNonQueryAsync() != 1)
                throw new InvalidOperationException("Role Superadmin tidak tersedia setelah migrasi.");
        }
        else if (!existingRole.Equals("MASTER", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Username bootstrap sudah digunakan oleh akun non-Superadmin.");
        }

        await using var complete = new NpgsqlCommand("update aksara_provisioning_guard set completed_at=coalesce(completed_at,now()) where singleton;", target);
        await complete.ExecuteNonQueryAsync();
        await using var identity = new NpgsqlCommand("select instance_id from company_instance where singleton;", target);
        return (Guid)(await identity.ExecuteScalarAsync() ?? throw new InvalidOperationException("Instance ID perusahaan baru tidak tersedia."));
    }

    private static async Task EnsureExistingIdentityIsSafeAsync(NpgsqlConnection connection, string companyCode, bool allowDefaultCode = false)
    {
        await using var identity = new NpgsqlCommand("select company_code from company_instance where singleton;", connection);
        var existingCode = Convert.ToString(await identity.ExecuteScalarAsync());
        if (!string.IsNullOrWhiteSpace(existingCode) &&
            !existingCode.Equals(companyCode, StringComparison.OrdinalIgnoreCase) &&
            !(allowDefaultCode && existingCode.Equals("PERUSAHAAN-UTAMA", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Database sudah terikat ke perusahaan {existingCode}. Database lama tidak disentuh.");

        if (!await TableExistsAsync(connection, "initial_setup")) return;
        await using var status = new NpgsqlCommand("select status,legacy_protected from initial_setup where singleton;", connection);
        await using var reader = await status.ExecuteReaderAsync();
        if (await reader.ReadAsync() && (reader.GetBoolean(1) || !reader.GetString(0).Equals("DRAFT", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Database sudah aktif atau dilindungi sebagai data lama. Provisioning ditolak.");
    }

    private static async Task<string?> ExistingUserRoleAsync(NpgsqlConnection connection, string userName)
    {
        await using var command = new NpgsqlCommand("select r.code from app_user u join app_role r on r.id=u.role_id where lower(u.username)=lower(@username);", connection);
        command.Parameters.AddWithValue("username", userName.Trim());
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private static async Task<bool> DatabaseExistsAsync(NpgsqlConnection connection, string databaseName)
    {
        await using var command = new NpgsqlCommand("select exists(select 1 from pg_database where datname=@name);", connection);
        command.Parameters.AddWithValue("name", databaseName);
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string table)
    {
        await using var command = new NpgsqlCommand("select to_regclass('public.' || @name) is not null;", connection);
        command.Parameters.AddWithValue("name", table);
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
    }

    private static async Task<int> PublicTableCountAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("select count(*) from information_schema.tables where table_schema='public' and table_type='BASE TABLE';", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                throw new InvalidOperationException("Argumen harus memakai format --nama nilai.");
            result[args[index][2..]] = args[index + 1];
        }
        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string argument, string environment) =>
        Value(options, argument, environment) ?? throw new InvalidOperationException($"{argument} wajib diisi melalui --{argument} atau {environment}.");

    private static string? Value(IReadOnlyDictionary<string, string> options, string argument, string environment) =>
        options.TryGetValue(argument, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : Environment.GetEnvironmentVariable(environment)?.Trim() is { Length: > 0 } configured ? configured : null;

    private static bool Boolean(IReadOnlyDictionary<string, string> options, string argument) =>
        options.TryGetValue(argument, out var value) && bool.TryParse(value, out var parsed) && parsed;

    private static string ReadBootstrapPassword()
    {
        var configured = Environment.GetEnvironmentVariable("AKSARA_BOOTSTRAP_PASSWORD");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        if (Console.IsInputRedirected)
            throw new InvalidOperationException("AKSARA_BOOTSTRAP_PASSWORD wajib tersedia untuk eksekusi non-interaktif.");

        Console.Write("Password awal Superadmin: ");
        var characters = new List<char>();
        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace && characters.Count > 0) characters.RemoveAt(characters.Count - 1);
            else if (!char.IsControl(key.KeyChar)) characters.Add(key.KeyChar);
        }
        Console.WriteLine();
        return new string(characters.ToArray());
    }

    private static void Validate(string database, string companyCode, string userName, string companyName, string displayName, string serverUrl, string? updateManifestUrl, string migrations)
    {
        if (!Identifier().IsMatch(database)) throw new InvalidOperationException("Nama database hanya boleh berisi huruf, angka, dan underscore.");
        if (!Code().IsMatch(companyCode)) throw new InvalidOperationException("Kode perusahaan harus 2-40 karakter huruf, angka, '-' atau '_'.");
        if (!UserName().IsMatch(userName)) throw new InvalidOperationException("Username harus 3-50 karakter huruf, angka, titik, '-' atau '_'.");
        if (companyName.Length is < 2 or > 150 || displayName.Length is < 2 or > 120) throw new InvalidOperationException("Nama bootstrap perusahaan atau nama Superadmin tidak valid.");
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var server) || (server.Scheme != Uri.UriSchemeHttps && server.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException("Target server perusahaan baru wajib berupa URL HTTP(S) absolut.");
        if (!string.IsNullOrWhiteSpace(updateManifestUrl) && !Uri.TryCreate(updateManifestUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("Alamat manifest update tidak valid.");
        if (!Directory.Exists(migrations) || !Directory.EnumerateFiles(migrations, "*.sql").Any()) throw new InvalidOperationException("Direktori migrasi tidak ditemukan atau kosong.");
        var password = Environment.GetEnvironmentVariable("AKSARA_BOOTSTRAP_PASSWORD");
        if (!string.IsNullOrEmpty(password) && password.Length < 6) throw new InvalidOperationException("Password awal minimal 6 karakter.");
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{1,62}$")]
    private static partial Regex Identifier();
    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{1,39}$")]
    private static partial Regex Code();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{2,49}$")]
    private static partial Regex UserName();
}
