using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using SapDataSync.WebApi.Models;

namespace SapDataSync.WebApi.Data;

public sealed class SapDataQueryService(IConfiguration configuration, IMemoryCache cache)
{
    private const int CommandTimeoutSeconds = 30;
    private static readonly HashSet<string> InternalColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id", "ImportLogId", "SourceRowNumber", "BusinessKeyHash", "RowHash", "IsDeleted", "DeletedAt", "CreatedAt", "UpdatedAt"
    };

    private string ConnectionString
    {
        get
        {
            var configured = configuration.GetConnectionString("SqlServer")
                ?? throw new InvalidOperationException("ConnectionStrings:SqlServer is required.");
            var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "SapDataSync" };
            return builder.ConnectionString;
        }
    }

    public async Task<PagedResponse<SapDataListItem>> GetSapDataAsync(
        SapDataQuery query,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;

        var conditions = new List<string> { "d.IsDeleted = 0" };
        AddExactFilter(command, conditions, "l.[Product]", query.Product, "@Product");
        AddExactFilter(command, conditions, "l.[SalesOrganization]", query.SalesOrganization, "@SalesOrganization");
        AddCsvFilter(command, conditions, "d.[Business Scenario]", NormalizeScenarioCsv(query.BusinessScenario), "Scenario");
        AddCsvFilter(command, conditions, "d.[SI Status]", query.SiStatus, "Status");
        AddExactFilter(command, conditions, "d.[Sales Office]", query.SalesOffice, "@SalesOffice");
        AddExactFilter(command, conditions, "d.[PlantCode]", query.PlantCode, "@PlantCode");
        AddContainsFilter(command, conditions, "d.[Shipping Instructions ID]", query.SiId, "@SiId");
        AddContainsFilter(command, conditions, "d.[Customer Name]", query.Customer, "@Customer");
        AddContainsFilter(command, conditions, "d.[OIL Sales]", query.OilSc, "@OilSc");
        AddContainsFilter(command, conditions, "d.[OIL SO]", query.OilSo, "@OilSo");
        AddContainsFilter(command, conditions, "d.[OIL Purchase]", query.OilPo, "@OilPo");

        if (query.CreatedFrom.HasValue)
        {
            conditions.Add("d.[WebSiCreatedDate] >= @CreatedFrom");
            command.Parameters.Add(new SqlParameter("@CreatedFrom", SqlDbType.NVarChar, 10)
            {
                Value = query.CreatedFrom.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            });
        }

        if (query.CreatedTo.HasValue)
        {
            conditions.Add("d.[WebSiCreatedDate] <= @CreatedTo");
            command.Parameters.Add(new SqlParameter("@CreatedTo", SqlDbType.NVarChar, 10)
            {
                Value = query.CreatedTo.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            });
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            conditions.Add(
                "(d.[Shipping Instructions ID] LIKE @Search ESCAPE '\\' " +
                "OR d.[Unique Number] LIKE @Search ESCAPE '\\' " +
                "OR d.[Customer Name] LIKE @Search ESCAPE '\\' " +
                "OR d.[OIL Sales] LIKE @Search ESCAPE '\\' " +
                "OR d.[OIL SO] LIKE @Search ESCAPE '\\' " +
                "OR d.[OIL Purchase] LIKE @Search ESCAPE '\\' " +
                "OR d.[Container Number] LIKE @Search ESCAPE '\\' " +
                "OR d.[Booking Number] LIKE @Search ESCAPE '\\')");
            command.Parameters.Add(new SqlParameter("@Search", SqlDbType.NVarChar, 204)
            {
                Value = $"%{EscapeLike(query.Search.Trim())}%"
            });
        }

        var where = conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions);
        var orderBy = ResolveOrderBy(query.SortBy, query.SortDirection);
        var offset = checked((query.Page - 1) * query.PageSize);
        command.Parameters.Add(new SqlParameter("@Offset", SqlDbType.Int) { Value = offset });
        command.Parameters.Add(new SqlParameter("@PageSize", SqlDbType.Int) { Value = query.PageSize });

        command.CommandText = $"""
            SELECT COUNT_BIG(*)
            FROM dbo.SapData AS d
            INNER JOIN dbo.ImportLog AS l ON l.Id = d.ImportLogId
            {where};

            SELECT d.Id,
                   l.Product,
                   l.SalesOrganization,
                   d.[Shipping Instructions ID],
                   d.[Unique Number],
                   d.[SI Status],
                   d.[Business Scenario],
                   d.[Customer Name],
                   d.[Selling Plant],
                   d.[PlantCode],
                   d.[Sales Office],
                   d.[Grade Description],
                   d.[SI Quantity (SI Qty for Line Item)],
                   d.[UOM],
                   d.[OIL Sales],
                   d.[OIL SO],
                   d.[OIL Purchase],
                   d.[First Committed Ship Date],
                   d.[Estimated Time of Departure (Date)],
                   d.[Estimated Time of Arrival (Date)],
                   d.[SI Created on],
                   d.UpdatedAt
            FROM dbo.SapData AS d
            INNER JOIN dbo.ImportLog AS l ON l.Id = d.ImportLogId
            {where}
            ORDER BY {orderBy}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var totalItems = reader.GetInt64(0);
        await reader.NextResultAsync(cancellationToken);

        var items = new List<SapDataListItem>(query.PageSize);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new SapDataListItem(
                reader.GetInt64(0),
                GetNullableString(reader, 1),
                GetNullableString(reader, 2),
                GetNullableString(reader, 3),
                GetNullableString(reader, 4),
                GetNullableString(reader, 5),
                GetNullableString(reader, 6),
                GetNullableString(reader, 7),
                GetNullableString(reader, 8),
                GetNullableString(reader, 9),
                GetNullableString(reader, 10),
                GetNullableString(reader, 11),
                GetNullableString(reader, 12),
                GetNullableString(reader, 13),
                GetNullableString(reader, 14),
                GetNullableString(reader, 15),
                GetNullableString(reader, 16),
                GetNullableString(reader, 17),
                GetNullableString(reader, 18),
                GetNullableString(reader, 19),
                GetNullableString(reader, 20),
                reader.GetDateTime(21)));
        }

        var totalPages = totalItems == 0 ? 0 : (int)Math.Ceiling(totalItems / (double)query.PageSize);
        return new PagedResponse<SapDataListItem>(items, query.Page, query.PageSize, totalItems, totalPages);
    }

    public async Task<FilterOptions> GetFilterOptionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = """
            SELECT DISTINCT Product FROM dbo.ImportLog WHERE Product IS NOT NULL ORDER BY Product;
            SELECT DISTINCT SalesOrganization FROM dbo.ImportLog WHERE SalesOrganization IS NOT NULL ORDER BY SalesOrganization;
            SELECT DISTINCT [Business Scenario] FROM dbo.SapData WHERE IsDeleted = 0 AND NULLIF(LTRIM(RTRIM([Business Scenario])), N'') IS NOT NULL ORDER BY [Business Scenario];
            SELECT DISTINCT [SI Status] FROM dbo.SapData WHERE IsDeleted = 0 AND NULLIF(LTRIM(RTRIM([SI Status])), N'') IS NOT NULL ORDER BY [SI Status];
            SELECT DISTINCT [Sales Office] FROM dbo.SapData WHERE IsDeleted = 0 AND NULLIF(LTRIM(RTRIM([Sales Office])), N'') IS NOT NULL ORDER BY [Sales Office];
            SELECT DISTINCT [PlantCode] FROM dbo.SapData WHERE IsDeleted = 0 AND NULLIF(LTRIM(RTRIM([PlantCode])), N'') IS NOT NULL ORDER BY [PlantCode];
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var products = await ReadStringListAsync(reader, cancellationToken);
        await reader.NextResultAsync(cancellationToken);
        var salesOrganizations = await ReadStringListAsync(reader, cancellationToken);
        await reader.NextResultAsync(cancellationToken);
        var scenarios = await ReadStringListAsync(reader, cancellationToken);
        await reader.NextResultAsync(cancellationToken);
        var statuses = await ReadStringListAsync(reader, cancellationToken);
        await reader.NextResultAsync(cancellationToken);
        var salesOffices = await ReadStringListAsync(reader, cancellationToken);
        await reader.NextResultAsync(cancellationToken);
        var plantCodes = await ReadStringListAsync(reader, cancellationToken);

        if (products.Count == 0) products.Add("12");
        if (salesOrganizations.Count == 0) salesOrganizations.Add("SG50");
        if (scenarios.Count == 0) scenarios.AddRange(["PDO", "PWS", "SDS", "SWS"]);

        return new FilterOptions(products, salesOrganizations, scenarios, statuses, salesOffices, plantCodes);
    }

    public async Task<SapDataDetail?> GetSapDataDetailAsync(long id, CancellationToken cancellationToken)
    {
        var sourceColumns = await GetSourceColumnNamesAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.Parameters.Add(new SqlParameter("@Id", SqlDbType.BigInt) { Value = id });
        command.CommandText = $"""
            SELECT d.Id, d.ImportLogId, d.SourceRowNumber, l.Product, l.SalesOrganization,
                   d.CreatedAt, d.UpdatedAt,
                   {string.Join(", ", sourceColumns.Select(column => "d." + QuoteIdentifier(column)))}
            FROM dbo.SapData AS d
            INNER JOIN dbo.ImportLog AS l ON l.Id = d.ImportLogId
            WHERE d.Id = @Id;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var fields = new Dictionary<string, string?>(sourceColumns.Count, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < sourceColumns.Count; index++)
        {
            fields[sourceColumns[index]] = GetNullableString(reader, index + 7);
        }

        return new SapDataDetail(
            reader.GetInt64(0),
            reader.GetGuid(1),
            reader.GetInt32(2),
            GetNullableString(reader, 3),
            GetNullableString(reader, 4),
            reader.GetDateTime(5),
            reader.GetDateTime(6),
            fields);
    }

    public async Task<PagedResponse<ImportLogListItem>> GetImportLogsAsync(
        ImportLogQuery query,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        var conditions = new List<string>();
        AddExactFilter(command, conditions, "Status", query.Status, "@Status");
        AddContainsFilter(command, conditions, "FileName", query.Search, "@Search");
        var where = conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions);
        var offset = checked((query.Page - 1) * query.PageSize);
        command.Parameters.Add(new SqlParameter("@Offset", SqlDbType.Int) { Value = offset });
        command.Parameters.Add(new SqlParameter("@PageSize", SqlDbType.Int) { Value = query.PageSize });
        command.CommandText = $"""
            SELECT COUNT_BIG(*) FROM dbo.ImportLog {where};
            SELECT Id, FileName, Status, Product, SalesOrganization, StartedAt, CompletedAt,
                   TotalRows, InsertedRows, UpdatedRows, DeletedRows, UnchangedRows, ErrorRows
            FROM dbo.ImportLog
            {where}
            ORDER BY StartedAt DESC
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var totalItems = reader.GetInt64(0);
        await reader.NextResultAsync(cancellationToken);
        var items = new List<ImportLogListItem>(query.PageSize);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ImportLogListItem(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                GetNullableString(reader, 3),
                GetNullableString(reader, 4),
                reader.GetDateTime(5),
                reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetInt32(10),
                reader.GetInt32(11),
                reader.GetInt32(12)));
        }

        var totalPages = totalItems == 0 ? 0 : (int)Math.Ceiling(totalItems / (double)query.PageSize);
        return new PagedResponse<ImportLogListItem>(items, query.Page, query.PageSize, totalItems, totalPages);
    }

    public async Task<ImportLogDetail?> GetImportLogDetailAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = """
            SELECT Id, FileName, FileHash, Status, Product, SalesOrganization, StartedAt, CompletedAt,
                   TotalRows, InsertedRows, UpdatedRows, DeletedRows, UnchangedRows, ErrorRows,
                   SoftDeleteEnabled, ErrorMessage, CreatedAt
            FROM dbo.ImportLog
            WHERE Id = @Id;
            """;
        command.Parameters.Add(new SqlParameter("@Id", SqlDbType.UniqueIdentifier) { Value = id });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new ImportLogDetail(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            GetNullableString(reader, 4),
            GetNullableString(reader, 5),
            reader.GetDateTime(6),
            reader.IsDBNull(7) ? null : reader.GetDateTime(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetInt32(12),
            reader.GetInt32(13),
            reader.GetBoolean(14),
            GetNullableString(reader, 15),
            reader.GetDateTime(16));
    }

    public async Task<PagedResponse<SapDataChangeListItem>> GetImportChangesAsync(
        Guid importLogId,
        ChangeLogQuery query,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.Parameters.Add(new SqlParameter("@ImportLogId", SqlDbType.UniqueIdentifier) { Value = importLogId });
        command.Parameters.Add(new SqlParameter("@Offset", SqlDbType.Int)
        {
            Value = checked((query.Page - 1) * query.PageSize)
        });
        command.Parameters.Add(new SqlParameter("@PageSize", SqlDbType.Int) { Value = query.PageSize });

        var typeFilter = string.Empty;
        if (!string.IsNullOrWhiteSpace(query.ChangeType))
        {
            typeFilter = " AND ChangeType = @ChangeType";
            command.Parameters.Add(new SqlParameter("@ChangeType", SqlDbType.NVarChar, 10)
            {
                Value = query.ChangeType
            });
        }

        command.CommandText = $"""
            SELECT COUNT_BIG(*)
            FROM dbo.SapDataChangeLog
            WHERE ImportLogId = @ImportLogId{typeFilter};

            SELECT Id, SapDataId, SourceRowNumber, ShippingInstructionsId,
                   UniqueNumber, ChangeType, OldValuesJson, NewValuesJson, CreatedAt
            FROM dbo.SapDataChangeLog
            WHERE ImportLogId = @ImportLogId{typeFilter}
            ORDER BY Id
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var totalItems = reader.GetInt64(0);
        await reader.NextResultAsync(cancellationToken);

        var items = new List<SapDataChangeListItem>(query.PageSize);
        while (await reader.ReadAsync(cancellationToken))
        {
            var fields = BuildFieldChanges(
                GetNullableString(reader, 6),
                GetNullableString(reader, 7));
            items.Add(new SapDataChangeListItem(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                GetNullableString(reader, 3),
                GetNullableString(reader, 4),
                reader.GetString(5),
                reader.GetDateTime(8),
                fields.Count,
                fields));
        }

        var totalPages = totalItems == 0 ? 0 : (int)Math.Ceiling(totalItems / (double)query.PageSize);
        return new PagedResponse<SapDataChangeListItem>(
            items, query.Page, query.PageSize, totalItems, totalPages);
    }

    private static IReadOnlyList<FieldChange> BuildFieldChanges(
        string? oldValuesJson,
        string? newValuesJson)
    {
        var oldValues = ReadJsonValues(oldValuesJson);
        var newValues = ReadJsonValues(newValuesJson);
        var fields = new List<FieldChange>();

        foreach (var field in oldValues.Keys.Concat(newValues.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            oldValues.TryGetValue(field, out var oldValue);
            newValues.TryGetValue(field, out var newValue);
            if (oldValuesJson is null)
            {
                if (!string.IsNullOrEmpty(newValue)) fields.Add(new FieldChange(field, null, newValue));
            }
            else if (newValuesJson is null || !string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                fields.Add(new FieldChange(field, oldValue, newValue));
            }
        }

        return fields;
    }

    private static Dictionary<string, string?> ReadJsonValues(string? json)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return values;

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("Field", out var fieldElement) ||
                    fieldElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                item.TryGetProperty("Value", out var valueElement);
                values[fieldElement.GetString()!] = ReadJsonValue(valueElement);
            }
        }
        else
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                values[property.Name] = ReadJsonValue(property.Value);
            }
        }

        return values;
    }

    private static string? ReadJsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        _ => value.GetRawText()
    };

    private async Task<List<string>> GetSourceColumnNamesAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue<List<string>>("SapDataSourceColumns", out var cached) && cached is not null)
        {
            return cached;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [name]
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'dbo.SapData')
              AND is_computed = 0
            ORDER BY column_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new List<string>(149);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            if (!InternalColumns.Contains(name)) columns.Add(name);
        }

        cache.Set("SapDataSourceColumns", columns, TimeSpan.FromHours(1));
        return columns;
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static void AddExactFilter(
        SqlCommand command,
        ICollection<string> conditions,
        string column,
        string? value,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        conditions.Add($"{column} = {parameterName}");
        command.Parameters.Add(new SqlParameter(parameterName, SqlDbType.NVarChar, 200) { Value = value.Trim() });
    }

    private static void AddContainsFilter(
        SqlCommand command,
        ICollection<string> conditions,
        string column,
        string? value,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        conditions.Add($"{column} LIKE {parameterName} ESCAPE '\\'");
        command.Parameters.Add(new SqlParameter(parameterName, SqlDbType.NVarChar, 500)
        {
            Value = $"%{EscapeLike(value.Trim())}%"
        });
    }

    private static void AddCsvFilter(
        SqlCommand command,
        ICollection<string> conditions,
        string column,
        string? csv,
        string parameterPrefix)
    {
        if (string.IsNullOrWhiteSpace(csv)) return;
        var values = csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
        if (values.Length == 0) return;

        var parameterNames = new List<string>(values.Length);
        for (var index = 0; index < values.Length; index++)
        {
            var parameterName = $"@{parameterPrefix}{index}";
            parameterNames.Add(parameterName);
            command.Parameters.Add(new SqlParameter(parameterName, SqlDbType.NVarChar, 200) { Value = values[index] });
        }

        conditions.Add($"{column} IN ({string.Join(", ", parameterNames)})");
    }

    private static string ResolveOrderBy(string? sortBy, string? sortDirection)
    {
        var column = sortBy?.Trim().ToLowerInvariant() switch
        {
            "siid" => "d.[Shipping Instructions ID]",
            "customer" => "d.[Customer Name]",
            "status" => "d.[SI Status]",
            "updatedat" => "d.UpdatedAt",
            _ => "d.[WebSiCreatedDate]"
        };
        var direction = string.Equals(sortDirection, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
        return $"{column} {direction}, d.Id DESC";
    }

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal);

    private static string? NormalizeScenarioCsv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return csv;
        var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PDO"] = "DEST-OIL Purchase",
            ["PWS"] = "Purchase Safety Stock",
            ["SDS"] = "Sales Direct Shipment",
            ["SWS"] = "Sales Warehouse Shipment"
        };
        return string.Join(',', csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => mapping.TryGetValue(value, out var expanded) ? expanded : value));
    }

    private static string QuoteIdentifier(string identifier) =>
        $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string? GetNullableString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static async Task<List<string>> ReadStringListAsync(
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var values = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0)) values.Add(reader.GetString(0));
        }

        return values;
    }
}
