using System.Data;
using System.Text.Json;
using Aksara.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Aksara.Infrastructure;

public sealed class PartnerValidationException(string message) : InvalidOperationException(message);
public sealed class PartnerConflictException(string message) : InvalidOperationException(message);

public sealed class BusinessPartnerRepository(PostgresDatabase database)
{
    public async Task<IReadOnlyList<SupplierMasterDto>> ListSuppliersAsync(string? query, bool includeInactive, int limit = 1000, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select id, code, company_name, contact_name, address, city, phone, email, tax_number,
                   credit_days, is_active, version,
                   owner_name, address1, address2, district, postal_code, address3, address4,
                   mobile1, mobile2, phone2, fax, contact_phone1, contact_phone2, contact_yahoo_id,
                   tax_registered, tax_registration_number, tax_registered_date
            from supplier
            where (@includeInactive or is_active)
              and (@query = '' or code ilike '%' || @query || '%' or company_name ilike '%' || @query || '%'
                   or coalesce(contact_name, '') ilike '%' || @query || '%' or coalesce(owner_name, '') ilike '%' || @query || '%'
                   or coalesce(city, '') ilike '%' || @query || '%' or coalesce(phone, '') ilike '%' || @query || '%')
            order by code
            limit @limit;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("includeInactive", includeInactive);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 5000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<SupplierMasterDto>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadSupplier(reader));
        return result;
    }

    public async Task<IReadOnlyList<CustomerMasterDto>> ListCustomersAsync(string? query, bool includeInactive, int limit = 1000, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select id, code, company_name, contact_name, address, city, phone, email, tax_number,
                   credit_limit, credit_days, price_group, sales_code, is_active, version,
                   owner_name, address1, address2, district, postal_code, address3, address4,
                   mobile1, mobile2, phone2, fax, transaction_condition,
                   contact_phone1, contact_phone2, contact_yahoo_id,
                   tax_registered, tax_registration_number, tax_registered_date, tax_invoice_prefix
            from customer
            where (@includeInactive or is_active)
              and code <> 'GUNGGUNGAN'
              and (@query = '' or code ilike '%' || @query || '%' or company_name ilike '%' || @query || '%'
                   or coalesce(contact_name, '') ilike '%' || @query || '%' or coalesce(owner_name, '') ilike '%' || @query || '%'
                   or coalesce(city, '') ilike '%' || @query || '%' or coalesce(phone, '') ilike '%' || @query || '%')
            order by code
            limit @limit;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("includeInactive", includeInactive);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 5000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<CustomerMasterDto>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadCustomer(reader));
        return result;
    }

    public async Task<IReadOnlyList<SalespersonMasterDto>> ListSalespeopleAsync(string? query, bool includeInactive, int limit = 1000, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select id, code, name, address, district, city, postal_code, phone,
                   commission_percent, collection_target, sales_target, base_salary, target_bonus,
                   is_active, version, group_code, supervisor_code
            from sales_person
            where (@includeInactive or is_active)
              and (@query = '' or code ilike '%' || @query || '%' or name ilike '%' || @query || '%'
                   or coalesce(city, '') ilike '%' || @query || '%')
            order by code
            limit @limit;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("includeInactive", includeInactive);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 5000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<SalespersonMasterDto>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadSalesperson(reader));
        return result;
    }

    public async Task<SupplierMasterDto> SaveSupplierAsync(SaveSupplierMasterRequest request, CancellationToken cancellationToken = default)
    {
        var code = Code(request.Code, "Kode Supplier");
        var company = Required(request.CompanyName, "Toko/PT", 150);
        ValidateCredit(request.CreditDays, 0, "Tempo Supplier");
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var version = await ReadVersionAsync(connection, transaction, "supplier", code, cancellationToken);
            ValidateVersion(version, request.ExpectedVersion, $"Supplier {code}");
            const string sql = """
                insert into supplier(code, company_name, contact_name, address, city, phone, email, tax_number,
                                     credit_days, is_active, owner_name, address1, address2, district, postal_code,
                                     address3, address4, mobile1, mobile2, phone2, fax, contact_phone1, contact_phone2,
                                     contact_yahoo_id, tax_registered, tax_registration_number, tax_registered_date,
                                     updated_at, version)
                values (@code, @company, @contact, @address, @city, @phone, @email, @tax,
                        @creditDays, @active, @ownerName, @address1, @address2, @district, @postalCode,
                        @address3, @address4, @mobile1, @mobile2, @phone2, @fax, @contactPhone1, @contactPhone2,
                        @contactYahooId, @taxRegistered, @taxRegistrationNumber, @taxRegisteredDate,
                        now(), 1)
                on conflict (code) do update set
                    company_name = excluded.company_name, contact_name = excluded.contact_name, address = excluded.address,
                    city = excluded.city, phone = excluded.phone, email = excluded.email, tax_number = excluded.tax_number,
                    credit_days = excluded.credit_days, is_active = excluded.is_active, owner_name = excluded.owner_name,
                    address1 = excluded.address1, address2 = excluded.address2, district = excluded.district,
                    postal_code = excluded.postal_code, address3 = excluded.address3, address4 = excluded.address4,
                    mobile1 = excluded.mobile1, mobile2 = excluded.mobile2, phone2 = excluded.phone2, fax = excluded.fax,
                    contact_phone1 = excluded.contact_phone1, contact_phone2 = excluded.contact_phone2,
                    contact_yahoo_id = excluded.contact_yahoo_id, tax_registered = excluded.tax_registered,
                    tax_registration_number = excluded.tax_registration_number,
                    tax_registered_date = excluded.tax_registered_date,
                    updated_at = now(), version = supplier.version + 1
                returning id, code, company_name, contact_name, address, city, phone, email, tax_number,
                          credit_days, is_active, version, owner_name, address1, address2, district, postal_code,
                          address3, address4, mobile1, mobile2, phone2, fax, contact_phone1, contact_phone2,
                          contact_yahoo_id, tax_registered, tax_registration_number, tax_registered_date;
                """;
            SupplierMasterDto saved;
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                AddCommon(command, code, company, request.ContactName, request.Address, request.City, request.Phone, request.Email, request.TaxNumber, request.IsActive);
                command.Parameters.AddWithValue("creditDays", request.CreditDays);
                AddSupplierDetail(command, request);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                saved = ReadSupplier(reader);
            }
            await AuditAsync(connection, transaction, "SUPPLIER", code, version is null ? "CREATE" : "UPDATE", request.UserName, request.ComputerName, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return saved;
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PartnerConflictException($"Supplier {code} berubah atau kodenya sudah digunakan. Muat ulang lalu coba kembali.");
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    public async Task<CustomerMasterDto> SaveCustomerAsync(SaveCustomerMasterRequest request, CancellationToken cancellationToken = default)
    {
        var code = Code(request.Code, "Kode Customer");
        if (code == "GUNGGUNGAN")
            throw new PartnerValidationException("Kode GUNGGUNGAN dipakai sistem untuk Penjualan Gunggungan dan tidak dapat diubah.");
        var company = Required(request.CompanyName, "Toko/PT", 150);
        ValidateCredit(request.CreditDays, request.CreditLimit, "Batas kredit Customer");
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var version = await ReadVersionAsync(connection, transaction, "customer", code, cancellationToken);
            ValidateVersion(version, request.ExpectedVersion, $"Customer {code}");
            await ValidateSalespersonAsync(connection, transaction, request.SalesCode, cancellationToken);
            const string sql = """
                insert into customer(code, company_name, contact_name, address, city, phone, email, tax_number,
                                     credit_limit, credit_days, price_group, sales_code, is_active,
                                     owner_name, address1, address2, district, postal_code, address3, address4,
                                     mobile1, mobile2, phone2, fax, transaction_condition,
                                     contact_phone1, contact_phone2, contact_yahoo_id,
                                     tax_registered, tax_registration_number, tax_registered_date, tax_invoice_prefix,
                                     updated_at, version)
                values (@code, @company, @contact, @address, @city, @phone, @email, @tax,
                        @creditLimit, @creditDays, @priceGroup, @salesCode, @active,
                        @ownerName, @address1, @address2, @district, @postalCode, @address3, @address4,
                        @mobile1, @mobile2, @phone2, @fax, @transactionCondition,
                        @contactPhone1, @contactPhone2, @contactYahooId,
                        @taxRegistered, @taxRegistrationNumber, @taxRegisteredDate, @taxInvoicePrefix,
                        now(), 1)
                on conflict (code) do update set
                    company_name = excluded.company_name, contact_name = excluded.contact_name, address = excluded.address,
                    city = excluded.city, phone = excluded.phone, email = excluded.email, tax_number = excluded.tax_number,
                    credit_limit = excluded.credit_limit, credit_days = excluded.credit_days,
                    price_group = excluded.price_group, sales_code = excluded.sales_code, is_active = excluded.is_active,
                    owner_name = excluded.owner_name, address1 = excluded.address1, address2 = excluded.address2,
                    district = excluded.district, postal_code = excluded.postal_code, address3 = excluded.address3,
                    address4 = excluded.address4, mobile1 = excluded.mobile1, mobile2 = excluded.mobile2,
                    phone2 = excluded.phone2, fax = excluded.fax, transaction_condition = excluded.transaction_condition,
                    contact_phone1 = excluded.contact_phone1, contact_phone2 = excluded.contact_phone2,
                    contact_yahoo_id = excluded.contact_yahoo_id, tax_registered = excluded.tax_registered,
                    tax_registration_number = excluded.tax_registration_number,
                    tax_registered_date = excluded.tax_registered_date, tax_invoice_prefix = excluded.tax_invoice_prefix,
                    updated_at = now(), version = customer.version + 1
                returning id, code, company_name, contact_name, address, city, phone, email, tax_number,
                          credit_limit, credit_days, price_group, sales_code, is_active, version,
                          owner_name, address1, address2, district, postal_code, address3, address4,
                          mobile1, mobile2, phone2, fax, transaction_condition,
                          contact_phone1, contact_phone2, contact_yahoo_id,
                          tax_registered, tax_registration_number, tax_registered_date, tax_invoice_prefix;
                """;
            CustomerMasterDto saved;
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                AddCommon(command, code, company, request.ContactName, request.Address, request.City, request.Phone, request.Email, request.TaxNumber, request.IsActive);
                command.Parameters.AddWithValue("creditLimit", request.CreditLimit);
                command.Parameters.AddWithValue("creditDays", request.CreditDays);
                command.Parameters.AddWithValue("priceGroup", Db(request.PriceGroup));
                command.Parameters.AddWithValue("salesCode", Db(request.SalesCode));
                AddCustomerDetail(command, request);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                saved = ReadCustomer(reader);
            }
            await AuditAsync(connection, transaction, "CUSTOMER", code, version is null ? "CREATE" : "UPDATE", request.UserName, request.ComputerName, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return saved;
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PartnerConflictException($"Customer {code} berubah atau kodenya sudah digunakan. Muat ulang lalu coba kembali.");
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    public async Task<SalespersonMasterDto> SaveSalespersonAsync(SaveSalespersonMasterRequest request, CancellationToken cancellationToken = default)
    {
        var code = Code(request.Code, "Kode Sales");
        var name = Required(request.Name, "Nama Sales", 100);
        ValidateNonNegative(request.CommissionPercent, "Komisi");
        ValidateNonNegative(request.CollectionTarget, "Target tagihan");
        ValidateNonNegative(request.SalesTarget, "Target omset");
        ValidateNonNegative(request.BaseSalary, "Gaji pokok");
        ValidateNonNegative(request.TargetBonus, "Bonus target");
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var version = await ReadVersionAsync(connection, transaction, "sales_person", code, cancellationToken);
            ValidateVersion(version, request.ExpectedVersion, $"Sales {code}");
            const string sql = """
                insert into sales_person(code, name, group_code, supervisor_code, address, district, city, postal_code, phone,
                                         commission_percent, collection_target, sales_target, base_salary, target_bonus,
                                         is_active, updated_at, version)
                values (@code, @name, @groupCode, @supervisorCode, @address, @district, @city, @postalCode, @phone,
                        @commissionPercent, @collectionTarget, @salesTarget, @baseSalary, @targetBonus,
                        @active, now(), 1)
                on conflict (code) do update set
                    name = excluded.name, group_code = excluded.group_code, supervisor_code = excluded.supervisor_code,
                    address = excluded.address, district = excluded.district, city = excluded.city,
                    postal_code = excluded.postal_code, phone = excluded.phone,
                    commission_percent = excluded.commission_percent, collection_target = excluded.collection_target,
                    sales_target = excluded.sales_target, base_salary = excluded.base_salary,
                    target_bonus = excluded.target_bonus, is_active = excluded.is_active,
                    updated_at = now(), version = sales_person.version + 1
                returning id, code, name, address, district, city, postal_code, phone,
                          commission_percent, collection_target, sales_target, base_salary, target_bonus,
                          is_active, version, group_code, supervisor_code;
                """;
            SalespersonMasterDto saved;
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue("code", code);
                command.Parameters.AddWithValue("name", name);
                command.Parameters.AddWithValue("groupCode", Db(request.GroupCode));
                command.Parameters.AddWithValue("supervisorCode", Db(request.SupervisorCode));
                command.Parameters.AddWithValue("address", Db(request.Address));
                command.Parameters.AddWithValue("district", Db(request.District));
                command.Parameters.AddWithValue("city", Db(request.City));
                command.Parameters.AddWithValue("postalCode", Db(request.PostalCode));
                command.Parameters.AddWithValue("phone", Db(request.Phone));
                command.Parameters.AddWithValue("commissionPercent", request.CommissionPercent);
                command.Parameters.AddWithValue("collectionTarget", request.CollectionTarget);
                command.Parameters.AddWithValue("salesTarget", request.SalesTarget);
                command.Parameters.AddWithValue("baseSalary", request.BaseSalary);
                command.Parameters.AddWithValue("targetBonus", request.TargetBonus);
                command.Parameters.AddWithValue("active", request.IsActive);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                saved = ReadSalesperson(reader);
            }
            await AuditAsync(connection, transaction, "SALESPERSON", code, version is null ? "CREATE" : "UPDATE", request.UserName, request.ComputerName, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return saved;
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PartnerConflictException($"Sales {code} berubah atau kodenya sudah digunakan. Muat ulang lalu coba kembali.");
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    private static SupplierMasterDto ReadSupplier(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2), Text(reader, 3), Text(reader, 4), Text(reader, 5),
        Text(reader, 6), Text(reader, 7), Text(reader, 8), reader.GetInt32(9), reader.GetBoolean(10), reader.GetInt64(11),
        Text(reader, 12), Text(reader, 13), Text(reader, 14), Text(reader, 15), Text(reader, 16), Text(reader, 17),
        Text(reader, 18), Text(reader, 19), Text(reader, 20), Text(reader, 21), Text(reader, 22), Text(reader, 23),
        Text(reader, 24), Text(reader, 25), reader.GetBoolean(26), Text(reader, 27),
        reader.IsDBNull(28) ? null : DateOnly.FromDateTime(reader.GetDateTime(28)));

    private static CustomerMasterDto ReadCustomer(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2), Text(reader, 3), Text(reader, 4), Text(reader, 5),
        Text(reader, 6), Text(reader, 7), Text(reader, 8), reader.GetDecimal(9), reader.GetInt32(10), Text(reader, 11),
        Text(reader, 12), reader.GetBoolean(13), reader.GetInt64(14), Text(reader, 15), Text(reader, 16), Text(reader, 17),
        Text(reader, 18), Text(reader, 19), Text(reader, 20), Text(reader, 21), Text(reader, 22), Text(reader, 23),
        Text(reader, 24), Text(reader, 25), Text(reader, 26), Text(reader, 27), Text(reader, 28), Text(reader, 29),
        reader.GetBoolean(30), Text(reader, 31), reader.IsDBNull(32) ? null : DateOnly.FromDateTime(reader.GetDateTime(32)),
        Text(reader, 33));

    private static SalespersonMasterDto ReadSalesperson(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2), Text(reader, 3), Text(reader, 4), Text(reader, 5),
        Text(reader, 6), Text(reader, 7), reader.GetDecimal(8), reader.GetDecimal(9), reader.GetDecimal(10),
        reader.GetDecimal(11), reader.GetDecimal(12), reader.GetBoolean(13), reader.GetInt64(14), Text(reader, 15), Text(reader, 16));

    private static void AddCommon(NpgsqlCommand command, string code, string company, string? contact, string? address, string? city, string? phone, string? email, string? tax, bool active)
    {
        command.Parameters.AddWithValue("code", code); command.Parameters.AddWithValue("company", company);
        command.Parameters.AddWithValue("contact", Db(contact)); command.Parameters.AddWithValue("address", Db(address));
        command.Parameters.AddWithValue("city", Db(city)); command.Parameters.AddWithValue("phone", Db(phone));
        command.Parameters.AddWithValue("email", Db(email)); command.Parameters.AddWithValue("tax", Db(tax));
        command.Parameters.AddWithValue("active", active);
    }

    private static void AddCustomerDetail(NpgsqlCommand command, SaveCustomerMasterRequest request)
    {
        command.Parameters.AddWithValue("ownerName", Db(request.OwnerName));
        command.Parameters.AddWithValue("address1", Db(request.Address1));
        command.Parameters.AddWithValue("address2", Db(request.Address2));
        command.Parameters.AddWithValue("district", Db(request.District));
        command.Parameters.AddWithValue("postalCode", Db(request.PostalCode));
        command.Parameters.AddWithValue("address3", Db(request.Address3));
        command.Parameters.AddWithValue("address4", Db(request.Address4));
        command.Parameters.AddWithValue("mobile1", Db(request.Mobile1));
        command.Parameters.AddWithValue("mobile2", Db(request.Mobile2));
        command.Parameters.AddWithValue("phone2", Db(request.Phone2));
        command.Parameters.AddWithValue("fax", Db(request.Fax));
        command.Parameters.AddWithValue("transactionCondition", Db(request.TransactionCondition));
        command.Parameters.AddWithValue("contactPhone1", Db(request.ContactPhone1));
        command.Parameters.AddWithValue("contactPhone2", Db(request.ContactPhone2));
        command.Parameters.AddWithValue("contactYahooId", Db(request.ContactYahooId));
        command.Parameters.AddWithValue("taxRegistered", request.TaxRegistered);
        command.Parameters.AddWithValue("taxRegistrationNumber", Db(request.TaxRegistrationNumber));
        command.Parameters.Add(new NpgsqlParameter("taxRegisteredDate", NpgsqlDbType.Date) { Value = request.TaxRegisteredDate.HasValue ? (object)request.TaxRegisteredDate.Value : DBNull.Value });
        command.Parameters.AddWithValue("taxInvoicePrefix", Db(request.TaxInvoicePrefix));
    }

    private static void AddSupplierDetail(NpgsqlCommand command, SaveSupplierMasterRequest request)
    {
        command.Parameters.AddWithValue("ownerName", Db(request.OwnerName));
        command.Parameters.AddWithValue("address1", Db(request.Address1));
        command.Parameters.AddWithValue("address2", Db(request.Address2));
        command.Parameters.AddWithValue("district", Db(request.District));
        command.Parameters.AddWithValue("postalCode", Db(request.PostalCode));
        command.Parameters.AddWithValue("address3", Db(request.Address3));
        command.Parameters.AddWithValue("address4", Db(request.Address4));
        command.Parameters.AddWithValue("mobile1", Db(request.Mobile1));
        command.Parameters.AddWithValue("mobile2", Db(request.Mobile2));
        command.Parameters.AddWithValue("phone2", Db(request.Phone2));
        command.Parameters.AddWithValue("fax", Db(request.Fax));
        command.Parameters.AddWithValue("contactPhone1", Db(request.ContactPhone1));
        command.Parameters.AddWithValue("contactPhone2", Db(request.ContactPhone2));
        command.Parameters.AddWithValue("contactYahooId", Db(request.ContactYahooId));
        command.Parameters.AddWithValue("taxRegistered", request.TaxRegistered);
        command.Parameters.AddWithValue("taxRegistrationNumber", Db(request.TaxRegistrationNumber));
        command.Parameters.Add(new NpgsqlParameter("taxRegisteredDate", NpgsqlDbType.Date) { Value = request.TaxRegisteredDate.HasValue ? (object)request.TaxRegisteredDate.Value : DBNull.Value });
    }

    private static async Task<long?> ReadVersionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string table, string code, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"select version from {table} where code = @code for update;", connection, transaction);
        command.Parameters.AddWithValue("code", code);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null ? null : Convert.ToInt64(result);
    }

    private static async Task ValidateSalespersonAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string? code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        await using var command = new NpgsqlCommand("select exists(select 1 from sales_person where code = @code);", connection, transaction);
        command.Parameters.AddWithValue("code", code.Trim().ToUpperInvariant());
        var exists = Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
        if (!exists) throw new PartnerValidationException($"Kode Sales {code.Trim().ToUpperInvariant()} tidak ada di daftar sales.");
    }

    private static void ValidateVersion(long? actual, long? expected, string label)
    {
        if (actual is null && expected is not null) throw new PartnerConflictException($"{label} sudah tidak tersedia.");
        if (actual is not null && expected is null) throw new PartnerConflictException($"{label} sudah ada. Gunakan menu Ubah.");
        if (actual != expected) throw new PartnerConflictException($"{label} telah diubah pengguna lain. Muat ulang data.");
    }

    private static async Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string entity, string code, string action, string user, string computer, object data, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into audit_log(computer_name, module, action, entity_type, entity_id, after_data)
            values (@computer, 'MASTER DATA', @action, @entity, @code, @data);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("computer", Db(computer)); command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("entity", entity); command.Parameters.AddWithValue("code", code);
        command.Parameters.Add(new NpgsqlParameter("data", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(new { userName = user, data }) });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Code(string value, string label)
    {
        var result = Required(value, label, 20).ToUpperInvariant();
        if (result.Any(char.IsWhiteSpace)) throw new PartnerValidationException($"{label} tidak boleh mengandung spasi.");
        return result;
    }
    private static string Required(string? value, string label, int max)
    {
        var result = value?.Trim() ?? string.Empty;
        if (result.Length == 0) throw new PartnerValidationException($"{label} wajib diisi.");
        if (result.Length > max) throw new PartnerValidationException($"{label} maksimal {max} karakter.");
        return result;
    }
    private static void ValidateCredit(int days, decimal amount, string label)
    {
        if (days is < 0 or > 3650) throw new PartnerValidationException("Tempo harus antara 0 dan 3650 hari.");
        if (amount < 0) throw new PartnerValidationException($"{label} tidak boleh negatif.");
    }
    private static void ValidateNonNegative(decimal amount, string label)
    {
        if (amount < 0) throw new PartnerValidationException($"{label} tidak boleh negatif.");
    }
    private static string? Text(NpgsqlDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static object Db(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
}
