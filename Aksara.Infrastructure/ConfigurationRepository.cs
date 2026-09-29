using Aksara.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Aksara.Infrastructure;

public sealed class ConfigurationValidationException(string message) : InvalidOperationException(message);
public sealed class ConfigurationForbiddenException(string message) : InvalidOperationException(message);
public sealed class ConfigurationConflictException(string message) : InvalidOperationException(message);

public sealed class ConfigurationRepository(PostgresDatabase database)
{
    private static readonly (string Code, string Name)[] Modules =
    [
        ("LEDGER", "Ledger"),
        ("CASH_BANK", "Kas & Bank"),
        ("SUPPLIER", "Supplier"),
        ("PURCHASE", "Pembelian"),
        ("CUSTOMER", "Customer"),
        ("SALES", "Penjualan"),
        ("INVENTORY", "Barang & Persediaan"),
        ("MAINTENANCE", "Pemeliharaan"),
        ("CONFIGURATION", "Konfigurasi")
    ];

    public async Task<ProgramConfigurationDto> GetProgramAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        return await ReadProgramAsync(connection, null, cancellationToken);
    }

    public async Task<ProgramConfigurationDto> SaveProgramAsync(SaveProgramConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        ValidateProgram(request);
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            update program_configuration set
                company_name=@company, owner_name=@owner, address=@address, city=@city, phone=@phone,
                email=@email, tax_number=@taxNumber, tax_enabled=@taxEnabled, default_tax_percent=@taxPercent,
                transaction_detail_rows=@detailRows, minimum_print_rows=@printRows,
                sale_number_prefix=@salePrefix, purchase_number_prefix=@purchasePrefix,
                sale_return_number_prefix=@saleReturnPrefix, purchase_return_number_prefix=@purchaseReturnPrefix,
                cash_bank_number_prefix=@cashBankPrefix, customer_receipt_number_prefix=@receiptPrefix,
                supplier_payment_number_prefix=@paymentPrefix, check_giro_number_prefix=@checkGiroPrefix,
                show_print_date_time=@showDateTime, show_item_code=@showItemCode, show_printed_by=@showPrintedBy,
                print_header_mode=@headerMode, sale_title=@saleTitle, purchase_title=@purchaseTitle,
                sale_return_title=@saleReturnTitle, purchase_return_title=@purchaseReturnTitle,
                delivery_title=@deliveryTitle, cash_bank_title=@cashBankTitle,
                signature_1=@signature1, signature_2=@signature2, signature_3=@signature3,
                print_footer_note=@footer, updated_at=now(), updated_by_name=@user, computer_name=@computer,
                version=version+1
            where singleton and version=@version;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("company", Required(request.CompanyName, "Nama perusahaan", 150));
        command.Parameters.AddWithValue("owner", Text(request.OwnerName, 120));
        command.Parameters.AddWithValue("address", Text(request.Address, 2000));
        command.Parameters.AddWithValue("city", Text(request.City, 80));
        command.Parameters.AddWithValue("phone", Text(request.Phone, 40));
        command.Parameters.AddWithValue("email", Text(request.Email, 150));
        command.Parameters.AddWithValue("taxNumber", Text(request.TaxNumber, 50));
        command.Parameters.AddWithValue("taxEnabled", request.TaxEnabled);
        command.Parameters.AddWithValue("taxPercent", request.DefaultTaxPercent);
        command.Parameters.AddWithValue("detailRows", request.TransactionDetailRows);
        command.Parameters.AddWithValue("printRows", request.MinimumPrintRows);
        command.Parameters.AddWithValue("salePrefix", Prefix(request.SaleNumberPrefix, "Awalan penjualan"));
        command.Parameters.AddWithValue("purchasePrefix", Prefix(request.PurchaseNumberPrefix, "Awalan pembelian"));
        command.Parameters.AddWithValue("saleReturnPrefix", Prefix(request.SaleReturnNumberPrefix, "Awalan retur penjualan"));
        command.Parameters.AddWithValue("purchaseReturnPrefix", Prefix(request.PurchaseReturnNumberPrefix, "Awalan retur pembelian"));
        command.Parameters.AddWithValue("cashBankPrefix", Prefix(request.CashBankNumberPrefix, "Awalan kas/bank"));
        command.Parameters.AddWithValue("receiptPrefix", Prefix(request.CustomerReceiptNumberPrefix, "Awalan penerimaan piutang"));
        command.Parameters.AddWithValue("paymentPrefix", Prefix(request.SupplierPaymentNumberPrefix, "Awalan pembayaran utang"));
        command.Parameters.AddWithValue("checkGiroPrefix", Prefix(request.CheckGiroNumberPrefix, "Awalan cek/giro"));
        command.Parameters.AddWithValue("showDateTime", request.ShowPrintDateTime);
        command.Parameters.AddWithValue("showItemCode", request.ShowItemCode);
        command.Parameters.AddWithValue("showPrintedBy", request.ShowPrintedBy);
        command.Parameters.AddWithValue("headerMode", HeaderMode(request.PrintHeaderMode));
        command.Parameters.AddWithValue("saleTitle", Required(request.SaleTitle, "Judul penjualan", 80));
        command.Parameters.AddWithValue("purchaseTitle", Required(request.PurchaseTitle, "Judul pembelian", 80));
        command.Parameters.AddWithValue("saleReturnTitle", Required(request.SaleReturnTitle, "Judul retur penjualan", 80));
        command.Parameters.AddWithValue("purchaseReturnTitle", Required(request.PurchaseReturnTitle, "Judul retur pembelian", 80));
        command.Parameters.AddWithValue("deliveryTitle", Required(request.DeliveryTitle, "Judul surat jalan", 80));
        command.Parameters.AddWithValue("cashBankTitle", Required(request.CashBankTitle, "Judul kas/bank", 80));
        command.Parameters.AddWithValue("signature1", Text(request.Signature1, 80));
        command.Parameters.AddWithValue("signature2", Text(request.Signature2, 80));
        command.Parameters.AddWithValue("signature3", Text(request.Signature3, 80));
        command.Parameters.AddWithValue("footer", Text(request.PrintFooterNote, 2000));
        command.Parameters.AddWithValue("user", Db(request.UserName));
        command.Parameters.AddWithValue("computer", Db(request.ComputerName));
        command.Parameters.AddWithValue("version", request.ExpectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            throw new ConfigurationConflictException("Konfigurasi sudah berubah. Muat ulang lalu simpan kembali.");

        return await ReadProgramAsync(connection, null, cancellationToken);
    }

    public async Task<IReadOnlyList<AppRoleDto>> ListRolesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        const string sql = "select id, code, name, is_system, is_active, version from app_role order by case code when 'MASTER' then 0 when 'ADMIN' then 1 when 'MANAJEMEN' then 2 when 'STAFF' then 3 else 4 end, name;";
        var rows = new List<AppRoleDto>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetInt64(5)));
        return rows;
    }

    public async Task<AppRoleDto> SaveRoleAsync(SaveAppRoleRequest request, string actorRoleCode, CancellationToken cancellationToken = default)
    {
        EnsureAccessAdministrator(actorRoleCode);
        var code = RoleCode(request.Code);
        var name = Required(request.Name, "Nama grup", 100);
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        if (request.Id is Guid roleId)
        {
            var current = await ReadRoleByIdAsync(connection, roleId, cancellationToken)
                ?? throw new ConfigurationValidationException("Grup pengguna tidak ditemukan.");
            if (current.IsSystem)
                throw new ConfigurationForbiddenException("Nama dan status grup bawaan tidak dapat diubah. Atur hak aksesnya pada tabel izin.");
        }
        if (request.Id is null && await NameExistsAsync(connection, "app_role", "code", code, null, cancellationToken))
            throw new ConfigurationConflictException($"Kode grup {code} sudah digunakan.");
        try
        {
            if (request.Id is null)
            {
                const string insert = "insert into app_role(code,name,is_system,is_active) values(@code,@name,false,@active) returning id,code,name,is_system,is_active,version;";
                await using var command = new NpgsqlCommand(insert, connection);
                command.Parameters.AddWithValue("code", code);
                command.Parameters.AddWithValue("name", name);
                command.Parameters.AddWithValue("active", request.IsActive);
                return await ReadRoleAsync(command, cancellationToken);
            }

            const string update = """
                update app_role set name=@name, is_active=case when is_system then true else @active end, version=version+1
                where id=@id and version=@version
                returning id,code,name,is_system,is_active,version;
                """;
            await using var updateCommand = new NpgsqlCommand(update, connection);
            updateCommand.Parameters.AddWithValue("id", request.Id.Value);
            updateCommand.Parameters.AddWithValue("name", name);
            updateCommand.Parameters.AddWithValue("active", request.IsActive);
            updateCommand.Parameters.AddWithValue("version", request.ExpectedVersion ?? 0);
            return await ReadRoleAsync(updateCommand, cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ConfigurationConflictException($"Kode grup {code} sudah digunakan.");
        }
    }

    public async Task<IReadOnlyList<AppUserDto>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            select u.id,u.username,u.display_name,u.role_id,r.code,r.name,u.is_active,u.last_login_at,u.version
            from app_user u join app_role r on r.id=u.role_id
            order by u.is_active desc, u.display_name, u.username;
            """;
        var rows = new List<AppUserDto>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3), reader.GetString(4), reader.GetString(5), reader.GetBoolean(6), reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7), reader.GetInt64(8)));
        return rows;
    }

    public async Task<AppUserDto> SaveUserAsync(SaveAppUserRequest request, string actorRoleCode, CancellationToken cancellationToken = default)
    {
        EnsureAccessAdministrator(actorRoleCode);
        var userName = UserName(request.UserName);
        var displayName = Required(request.DisplayName, "Nama pengguna", 120);
        if (request.Id is null && string.IsNullOrWhiteSpace(request.Password))
            throw new ConfigurationValidationException("Password wajib diisi untuk pengguna baru.");
        ValidatePassword(request.Password, allowEmpty: request.Id is not null);

        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        if (await NameExistsAsync(connection, "app_user", "username", userName, request.Id, cancellationToken))
            throw new ConfigurationConflictException($"Username {userName} sudah digunakan.");
        var role = await ReadRoleByIdAsync(connection, request.RoleId, cancellationToken)
            ?? throw new ConfigurationValidationException("Grup pengguna tidak ditemukan atau sudah tidak aktif.");
        if (!role.IsActive) throw new ConfigurationValidationException("Grup pengguna sudah tidak aktif.");
        if (!IsSuperadmin(actorRoleCode) && role.Code.Equals("MASTER", StringComparison.OrdinalIgnoreCase))
            throw new ConfigurationForbiddenException("Admin tidak dapat menetapkan grup Superadmin.");
        if (request.Id is Guid targetUserId && !IsSuperadmin(actorRoleCode))
        {
            var current = await ReadUserAsync(connection, targetUserId, cancellationToken);
            if (current.RoleCode.Equals("MASTER", StringComparison.OrdinalIgnoreCase))
                throw new ConfigurationForbiddenException("Admin tidak dapat mengubah pengguna Superadmin.");
        }
        if (!request.IsActive && role.Code.Equals("MASTER", StringComparison.OrdinalIgnoreCase))
            throw new ConfigurationValidationException("Pengguna grup MASTER tidak dapat dinonaktifkan dari menu ini.");
        if (request.Id is Guid existingId)
            await EnsureMasterRemainsAsync(connection, existingId, role.Code, request.IsActive, cancellationToken);

        try
        {
            Guid id;
            if (request.Id is null)
            {
                const string insert = """
                    insert into app_user(username,display_name,password_hash,role_id,is_active)
                    values(@username,@display,@password,@role,@active) returning id;
                    """;
                await using var command = new NpgsqlCommand(insert, connection);
                command.Parameters.AddWithValue("username", userName);
                command.Parameters.AddWithValue("display", displayName);
                command.Parameters.AddWithValue("password", PasswordHasher.Hash(request.Password!));
                command.Parameters.AddWithValue("role", request.RoleId);
                command.Parameters.AddWithValue("active", request.IsActive);
                id = (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
            }
            else
            {
                const string update = """
                    update app_user set username=@username,display_name=@display,role_id=@role,is_active=@active,
                        password_hash=case when @password is null then password_hash else @password end,
                        version=version+1
                    where id=@id and version=@version returning id;
                    """;
                await using var command = new NpgsqlCommand(update, connection);
                command.Parameters.AddWithValue("username", userName);
                command.Parameters.AddWithValue("display", displayName);
                command.Parameters.AddWithValue("role", request.RoleId);
                command.Parameters.AddWithValue("active", request.IsActive);
                command.Parameters.Add(new NpgsqlParameter("password", NpgsqlDbType.Text)
                {
                    Value = string.IsNullOrWhiteSpace(request.Password) ? DBNull.Value : PasswordHasher.Hash(request.Password)
                });
                command.Parameters.AddWithValue("id", request.Id.Value);
                command.Parameters.AddWithValue("version", request.ExpectedVersion ?? 0);
                var value = await command.ExecuteScalarAsync(cancellationToken);
                if (value is null) throw new ConfigurationConflictException("Data pengguna sudah berubah. Muat ulang lalu coba lagi.");
                id = (Guid)value;
            }

            return await ReadUserAsync(connection, id, cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ConfigurationConflictException($"Username {userName} sudah digunakan.");
        }
    }

    public async Task<IReadOnlyList<RolePermissionDto>> GetRolePermissionsAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        var role = await ReadRoleByIdAsync(connection, roleId, cancellationToken)
            ?? throw new ConfigurationValidationException("Grup pengguna tidak ditemukan.");
        if (role.Code.Equals("MASTER", StringComparison.OrdinalIgnoreCase))
            return Modules.Select(x => new RolePermissionDto(x.Code, x.Name, true, true, true)).ToArray();

        const string sql = "select module,action from app_permission where role_id=@role and is_allowed;";
        var grants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = new NpgsqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue("role", roleId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) grants.Add($"{reader.GetString(0)}:{reader.GetString(1)}");
        }
        return Modules.Select(x => new RolePermissionDto(
            x.Code, x.Name,
            grants.Contains($"{x.Code}:VIEW"),
            grants.Contains($"{x.Code}:MANAGE"),
            grants.Contains($"{x.Code}:DELETE"))).ToArray();
    }

    public async Task<IReadOnlyList<RolePermissionDto>> SaveRolePermissionsAsync(Guid roleId, SaveRolePermissionsRequest request, string actorRoleCode, CancellationToken cancellationToken = default)
    {
        EnsureAccessAdministrator(actorRoleCode);
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        var role = await ReadRoleByIdAsync(connection, roleId, cancellationToken)
            ?? throw new ConfigurationValidationException("Grup pengguna tidak ditemukan.");
        if (role.Code.Equals("MASTER", StringComparison.OrdinalIgnoreCase))
            throw new ConfigurationValidationException("Hak akses MASTER selalu penuh dan tidak dapat dikurangi.");

        var known = Modules.Select(x => x.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (request.Permissions.Any(x => !known.Contains(x.Module)))
            throw new ConfigurationValidationException("Terdapat modul hak akses yang tidak dikenal.");

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var delete = new NpgsqlCommand("delete from app_permission where role_id=@role;", connection, transaction))
        {
            delete.Parameters.AddWithValue("role", roleId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var permission in request.Permissions)
        {
            foreach (var action in AllowedActions(permission))
            {
                const string insert = "insert into app_permission(role_id,module,action,is_allowed) values(@role,@module,@action,true);";
                await using var command = new NpgsqlCommand(insert, connection, transaction);
                command.Parameters.AddWithValue("role", roleId);
                command.Parameters.AddWithValue("module", permission.Module.ToUpperInvariant());
                command.Parameters.AddWithValue("action", action);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return await GetRolePermissionsAsync(roleId, cancellationToken);
    }

    private static bool IsSuperadmin(string roleCode) => roleCode.Equals("MASTER", StringComparison.OrdinalIgnoreCase);

    private static void EnsureAccessAdministrator(string roleCode)
    {
        if (!IsSuperadmin(roleCode) && !roleCode.Equals("ADMIN", StringComparison.OrdinalIgnoreCase))
            throw new ConfigurationForbiddenException("Hanya Superadmin dan Admin yang dapat mengubah pengguna dan hak akses.");
    }

    public async Task<AuthenticatedUserDto> ChangeCredentialsAsync(string currentUserName, ChangeCredentialsRequest request, CancellationToken cancellationToken = default)
    {
        var userName = UserName(request.UserName);
        var displayName = Required(request.DisplayName, "Nama pengguna", 120);
        if (string.IsNullOrWhiteSpace(request.CurrentPassword))
            throw new ConfigurationValidationException("Password saat ini wajib diisi.");
        ValidatePassword(request.NewPassword, allowEmpty: true);

        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        const string select = "select id,password_hash from app_user where lower(username)=lower(@username) and is_active limit 1;";
        Guid id;
        string hash;
        await using (var command = new NpgsqlCommand(select, connection))
        {
            command.Parameters.AddWithValue("username", currentUserName);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new ConfigurationValidationException("Pengguna aktif tidak ditemukan.");
            id = reader.GetGuid(0);
            hash = reader.GetString(1);
        }
        if (!PasswordHasher.Verify(hash, request.CurrentPassword))
            throw new ConfigurationValidationException("Password saat ini tidak sesuai.");
        if (await NameExistsAsync(connection, "app_user", "username", userName, id, cancellationToken))
            throw new ConfigurationConflictException($"Username {userName} sudah digunakan.");

        try
        {
            const string update = """
                update app_user set username=@username,display_name=@display,
                    password_hash=case when @password is null then password_hash else @password end,
                    version=version+1 where id=@id;
                """;
            await using var command = new NpgsqlCommand(update, connection);
            command.Parameters.AddWithValue("username", userName);
            command.Parameters.AddWithValue("display", displayName);
            command.Parameters.Add(new NpgsqlParameter("password", NpgsqlDbType.Text)
            {
                Value = string.IsNullOrWhiteSpace(request.NewPassword) ? DBNull.Value : PasswordHasher.Hash(request.NewPassword)
            });
            command.Parameters.AddWithValue("id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ConfigurationConflictException($"Username {userName} sudah digunakan.");
        }

        return await ReadAuthenticatedUserAsync(connection, id, cancellationToken);
    }

    public static async Task<string> GetDocumentPrefixAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string documentType, string fallback, CancellationToken cancellationToken)
    {
        var column = documentType.ToUpperInvariant() switch
        {
            "SALE" => "sale_number_prefix",
            "PURCHASE" => "purchase_number_prefix",
            "SALE_RETURN" => "sale_return_number_prefix",
            "PURCHASE_RETURN" => "purchase_return_number_prefix",
            "CASH_BANK" => "cash_bank_number_prefix",
            "CUSTOMER_RECEIPT" => "customer_receipt_number_prefix",
            "SUPPLIER_PAY" => "supplier_payment_number_prefix",
            "CUSTOMER_CHECK_GIRO" => "check_giro_number_prefix",
            _ => null
        };
        if (column is null) return fallback;
        await using var command = new NpgsqlCommand($"select {column} from program_configuration where singleton;", connection, transaction);
        var value = (await command.ExecuteScalarAsync(cancellationToken))?.ToString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? fallback : value.ToUpperInvariant();
    }

    private static async Task<ProgramConfigurationDto> ReadProgramAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            select company_name,owner_name,address,city,phone,email,tax_number,tax_enabled,default_tax_percent,
                transaction_detail_rows,minimum_print_rows,sale_number_prefix,purchase_number_prefix,
                sale_return_number_prefix,purchase_return_number_prefix,cash_bank_number_prefix,
                customer_receipt_number_prefix,supplier_payment_number_prefix,check_giro_number_prefix,
                show_print_date_time,show_item_code,show_printed_by,print_header_mode,
                sale_title,purchase_title,sale_return_title,purchase_return_title,delivery_title,cash_bank_title,
                signature_1,signature_2,signature_3,print_footer_note,updated_at,version
            from program_configuration where singleton;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new ConfigurationValidationException("Konfigurasi program belum tersedia.");
        return new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
            reader.GetBoolean(7), reader.GetDecimal(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetString(11), reader.GetString(12),
            reader.GetString(13), reader.GetString(14), reader.GetString(15), reader.GetString(16), reader.GetString(17), reader.GetString(18),
            reader.GetBoolean(19), reader.GetBoolean(20), reader.GetBoolean(21), reader.GetString(22), reader.GetString(23), reader.GetString(24),
            reader.GetString(25), reader.GetString(26), reader.GetString(27), reader.GetString(28), reader.GetString(29), reader.GetString(30),
            reader.GetString(31), reader.GetString(32), reader.GetFieldValue<DateTimeOffset>(33), reader.GetInt64(34));
    }

    private static async Task<AppRoleDto> ReadRoleAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new ConfigurationConflictException("Data grup sudah berubah. Muat ulang lalu coba lagi.");
        return new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetInt64(5));
    }

    private static async Task<AppRoleDto?> ReadRoleByIdAsync(NpgsqlConnection connection, Guid id, CancellationToken cancellationToken)
    {
        const string sql = "select id,code,name,is_system,is_active,version from app_role where id=@id;";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetInt64(5))
            : null;
    }

    private static async Task<AppUserDto> ReadUserAsync(NpgsqlConnection connection, Guid id, CancellationToken cancellationToken)
    {
        const string sql = """
            select u.id,u.username,u.display_name,u.role_id,r.code,r.name,u.is_active,u.last_login_at,u.version
            from app_user u join app_role r on r.id=u.role_id where u.id=@id;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new ConfigurationValidationException("Pengguna tidak ditemukan.");
        return new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3), reader.GetString(4), reader.GetString(5), reader.GetBoolean(6), reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7), reader.GetInt64(8));
    }

    private static async Task<AuthenticatedUserDto> ReadAuthenticatedUserAsync(NpgsqlConnection connection, Guid id, CancellationToken cancellationToken)
    {
        const string sql = "select u.username,u.display_name,r.code,r.name from app_user u join app_role r on r.id=u.role_id where u.id=@id;";
        string userName;
        string displayName;
        string roleCode;
        string roleName;
        await using (var command = new NpgsqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new ConfigurationValidationException("Pengguna tidak ditemukan.");
            userName = reader.GetString(0); displayName = reader.GetString(1); roleCode = reader.GetString(2); roleName = reader.GetString(3);
        }
        var permissions = await AuthenticationRepository.ReadPermissionsAsync(connection, roleCode, cancellationToken);
        return new(id, userName, displayName, roleCode, roleName, permissions);
    }

    private static async Task EnsureMasterRemainsAsync(NpgsqlConnection connection, Guid userId, string newRoleCode, bool isActive, CancellationToken cancellationToken)
    {
        const string roleSql = "select r.code from app_user u join app_role r on r.id=u.role_id where u.id=@id;";
        await using var roleCommand = new NpgsqlCommand(roleSql, connection);
        roleCommand.Parameters.AddWithValue("id", userId);
        var currentRole = (await roleCommand.ExecuteScalarAsync(cancellationToken))?.ToString();
        if (!string.Equals(currentRole, "MASTER", StringComparison.OrdinalIgnoreCase) ||
            (isActive && newRoleCode.Equals("MASTER", StringComparison.OrdinalIgnoreCase))) return;

        const string countSql = """
            select count(*) from app_user u join app_role r on r.id=u.role_id
            where u.id<>@id and u.is_active and r.is_active and lower(r.code)='master';
            """;
        await using var countCommand = new NpgsqlCommand(countSql, connection);
        countCommand.Parameters.AddWithValue("id", userId);
        if (Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken)) == 0)
            throw new ConfigurationValidationException("Minimal satu pengguna MASTER harus tetap aktif.");
    }

    private static IEnumerable<string> AllowedActions(RolePermissionDto permission)
    {
        if (permission.CanView) yield return "VIEW";
        if (permission.CanManage) yield return "MANAGE";
        if (permission.CanDelete) yield return "DELETE";
    }

    private static async Task<bool> NameExistsAsync(NpgsqlConnection connection, string table, string column, string value, Guid? excludedId, CancellationToken cancellationToken)
    {
        if ((table, column) is not (("app_role", "code") or ("app_user", "username")))
            throw new ArgumentOutOfRangeException(nameof(table));
        var sql = $"select exists(select 1 from {table} where lower({column})=lower(@value) and (cast(@id as uuid) is null or id<>cast(@id as uuid)));";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("value", value);
        command.Parameters.AddWithValue("id", excludedId is null ? DBNull.Value : excludedId.Value);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static void ValidateProgram(SaveProgramConfigurationRequest request)
    {
        if (!CompanyIdentityPolicy.IsFinalCompanyName(request.CompanyName))
            throw new ConfigurationValidationException("Nama perusahaan final wajib diisi dan tidak boleh memakai nama sementara Setup Data Awal.");
        if (request.DefaultTaxPercent is < 0 or > 100) throw new ConfigurationValidationException("PPN harus antara 0 sampai 100 persen.");
        if (request.TransactionDetailRows is < 1 or > 200) throw new ConfigurationValidationException("Jumlah baris transaksi harus antara 1 sampai 200.");
        if (request.MinimumPrintRows is < 1 or > 60) throw new ConfigurationValidationException("Minimal baris cetak harus antara 1 sampai 60.");
    }

    private static void ValidatePassword(string? password, bool allowEmpty)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            if (allowEmpty) return;
            throw new ConfigurationValidationException("Password wajib diisi.");
        }
        if (password.Length < 6) throw new ConfigurationValidationException("Password minimal 6 karakter.");
    }

    private static string UserName(string value)
    {
        var result = Required(value, "Username", 50);
        if (result.Any(char.IsWhiteSpace)) throw new ConfigurationValidationException("Username tidak boleh mengandung spasi.");
        return result;
    }

    private static string Prefix(string value, string label)
    {
        var result = Required(value, label, 10).ToUpperInvariant();
        if (result.Any(ch => !char.IsLetterOrDigit(ch))) throw new ConfigurationValidationException($"{label} hanya boleh berisi huruf dan angka.");
        return result;
    }

    private static string RoleCode(string value)
    {
        var result = Required(value, "Kode grup", 30).ToUpperInvariant();
        if (result.Any(ch => !char.IsLetterOrDigit(ch) && ch != '_'))
            throw new ConfigurationValidationException("Kode grup hanya boleh berisi huruf, angka, dan garis bawah.");
        return result;
    }

    private static string HeaderMode(string value) => value.Trim().ToUpperInvariant() switch
    {
        "NONE" => "NONE",
        "TEXT" => "TEXT",
        "LOGO_TEXT" => "LOGO_TEXT",
        _ => throw new ConfigurationValidationException("Mode kop bukti cetak tidak valid.")
    };

    private static string Required(string? value, string label, int maxLength)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0) throw new ConfigurationValidationException($"{label} wajib diisi.");
        if (text.Length > maxLength) throw new ConfigurationValidationException($"{label} maksimal {maxLength} karakter.");
        return text;
    }

    private static string Text(string? value, int maxLength)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length > maxLength) throw new ConfigurationValidationException($"Isi maksimal {maxLength} karakter.");
        return text;
    }

    private static object Db(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
}
