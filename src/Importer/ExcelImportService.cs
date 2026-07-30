using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ExcelDataReader;
using Microsoft.Data.SqlClient;

namespace SapDataSync.Importer;

internal sealed class ExcelImportService(string connectionString, int batchSize)
{
    private const int ExpectedSourceColumnCount = 149;
    private const string FirstBusinessKeyColumn = "Shipping Instructions ID";
    private const string SecondBusinessKeyColumn = "Unique Number";

    public async Task<ImportResult> ImportAsync(
        string filePath,
        string fileHash,
        string? worksheetName,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (await IsAlreadyCompletedAsync(connection, fileHash, cancellationToken))
        {
            return new ImportResult(ImportStatus.AlreadyCompleted, null, 0, 0, 0, 0);
        }

        var sourceColumns = await LoadSourceColumnsAsync(connection, cancellationToken);
        ValidateDatabaseSchema(sourceColumns);

        var importLogId = await CreateImportLogAsync(
            connection,
            Path.GetFileName(filePath),
            fileHash,
            cancellationToken);

        try
        {
            var counts = await ImportAndSynchronizeAsync(
                connection,
                filePath,
                fileHash,
                worksheetName,
                importLogId,
                sourceColumns,
                cancellationToken);

            return new ImportResult(
                ImportStatus.Completed,
                importLogId,
                counts.Total,
                counts.Inserted,
                counts.Updated,
                counts.Unchanged);
        }
        catch (Exception exception)
        {
            try
            {
                await MarkFailedAsync(connection, importLogId, exception, CancellationToken.None);
            }
            catch (Exception logException)
            {
                Console.Error.WriteLine(
                    "[{0:O}] Could not mark ImportLog {1} as failed: {2}",
                    DateTimeOffset.UtcNow,
                    importLogId,
                    logException.Message);
            }

            throw;
        }
    }

    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 64,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private async Task<ImportCounts> ImportAndSynchronizeAsync(
        SqlConnection connection,
        string filePath,
        string expectedFileHash,
        string? worksheetName,
        Guid importLogId,
        IReadOnlyList<string> sourceColumns,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var totalRows = await LoadStagingAsync(
                connection,
                transaction,
                filePath,
                worksheetName,
                importLogId,
                sourceColumns,
                cancellationToken);

            var hashAfterRead = await ComputeSha256Async(filePath, cancellationToken);
            if (!string.Equals(hashAfterRead, expectedFileHash, StringComparison.Ordinal))
            {
                throw new IOException("The Excel file changed while it was being imported. The transaction was rolled back.");
            }

            var updatedRows = await UpdateChangedRowsAsync(
                connection,
                transaction,
                importLogId,
                sourceColumns,
                cancellationToken);
            var insertedRows = await InsertNewRowsAsync(
                connection,
                transaction,
                importLogId,
                sourceColumns,
                cancellationToken);
            var unchangedRows = totalRows - insertedRows - updatedRows;

            if (unchangedRows < 0)
            {
                throw new InvalidOperationException("Synchronization counts are inconsistent.");
            }

            await MarkCompletedAsync(
                connection,
                transaction,
                importLogId,
                totalRows,
                insertedRows,
                updatedRows,
                unchangedRows,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new ImportCounts(totalRows, insertedRows, updatedRows, unchangedRows);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<int> LoadStagingAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string filePath,
        string? worksheetName,
        Guid importLogId,
        IReadOnlyList<string> sourceColumns,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = ExcelReaderFactory.CreateReader(stream);

        SelectWorksheet(reader, worksheetName);

        if (!reader.Read())
        {
            throw new InvalidDataException($"Worksheet '{reader.Name}' is empty.");
        }

        ValidateHeader(reader, sourceColumns);

        var firstKeyIndex = sourceColumns.IndexOf(FirstBusinessKeyColumn);
        var secondKeyIndex = sourceColumns.IndexOf(SecondBusinessKeyColumn);
        var seenBusinessKeys = new HashSet<string>(StringComparer.Ordinal);
        var batch = CreateBatchTable(sourceColumns);
        var totalRows = 0;
        var excelRowNumber = 1;

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            excelRowNumber++;

            var values = new string?[sourceColumns.Count];
            var hasValue = false;

            for (var index = 0; index < sourceColumns.Count; index++)
            {
                values[index] = ConvertCellValue(reader.GetValue(index));
                hasValue |= values[index] is not null;
            }

            if (!hasValue)
            {
                continue;
            }

            var firstKey = values[firstKeyIndex];
            var secondKey = values[secondKeyIndex];
            if (string.IsNullOrEmpty(firstKey) && string.IsNullOrEmpty(secondKey))
            {
                throw new InvalidDataException(
                    $"Excel row {excelRowNumber} has no business key. At least one of '{FirstBusinessKeyColumn}' or '{SecondBusinessKeyColumn}' is required.");
            }

            var businessKeyHash = ComputeValueHash([firstKey, secondKey]);
            var businessKeyHex = Convert.ToHexString(businessKeyHash);
            if (!seenBusinessKeys.Add(businessKeyHex))
            {
                throw new InvalidDataException(
                    $"Excel row {excelRowNumber} has a duplicate business key ({FirstBusinessKeyColumn} + {SecondBusinessKeyColumn}).");
            }

            var row = batch.NewRow();
            row["ImportLogId"] = importLogId;
            row["SourceRowNumber"] = excelRowNumber;
            row["BusinessKeyHash"] = businessKeyHash;
            row["RowHash"] = ComputeValueHash(values);

            for (var index = 0; index < sourceColumns.Count; index++)
            {
                row[sourceColumns[index]] = values[index] is null ? DBNull.Value : values[index]!;
            }

            batch.Rows.Add(row);
            totalRows++;

            if (batch.Rows.Count >= batchSize)
            {
                await WriteBatchAsync(connection, transaction, batch, cancellationToken);
                batch.Clear();
            }
        }

        if (batch.Rows.Count > 0)
        {
            await WriteBatchAsync(connection, transaction, batch, cancellationToken);
        }

        if (totalRows == 0)
        {
            throw new InvalidDataException($"Worksheet '{reader.Name}' contains no data rows.");
        }

        return totalRows;
    }

    private static void SelectWorksheet(IExcelDataReader reader, string? worksheetName)
    {
        if (worksheetName is null)
        {
            return;
        }

        do
        {
            if (string.Equals(reader.Name, worksheetName, StringComparison.Ordinal))
            {
                return;
            }
        }
        while (reader.NextResult());

        throw new InvalidDataException($"Worksheet '{worksheetName}' was not found in the workbook.");
    }

    private static void ValidateHeader(IExcelDataReader reader, IReadOnlyList<string> sourceColumns)
    {
        if (reader.FieldCount != sourceColumns.Count)
        {
            throw new InvalidDataException(
                $"Worksheet '{reader.Name}' has {reader.FieldCount} columns; expected exactly {sourceColumns.Count}.");
        }

        for (var index = 0; index < sourceColumns.Count; index++)
        {
            var actual = Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? string.Empty;
            var expected = sourceColumns[index];
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Invalid header at Excel column {index + 1}: found '{actual}', expected '{expected}'.");
            }
        }
    }

    private static DataTable CreateBatchTable(IReadOnlyList<string> sourceColumns)
    {
        var table = new DataTable { Locale = CultureInfo.InvariantCulture };
        table.Columns.Add("ImportLogId", typeof(Guid));
        table.Columns.Add("SourceRowNumber", typeof(int));
        table.Columns.Add("BusinessKeyHash", typeof(byte[]));
        table.Columns.Add("RowHash", typeof(byte[]));

        foreach (var sourceColumn in sourceColumns)
        {
            table.Columns.Add(sourceColumn, typeof(string));
        }

        return table;
    }

    private static async Task WriteBatchAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DataTable batch,
        CancellationToken cancellationToken)
    {
        using var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, transaction)
        {
            DestinationTableName = "dbo.SapDataStaging",
            BatchSize = batch.Rows.Count,
            BulkCopyTimeout = 300
        };

        foreach (DataColumn column in batch.Columns)
        {
            bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        await bulkCopy.WriteToServerAsync(batch, cancellationToken);
    }

    private static async Task<int> UpdateChangedRowsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid importLogId,
        IReadOnlyList<string> sourceColumns,
        CancellationToken cancellationToken)
    {
        var assignments = string.Join(",\n            ", sourceColumns.Select(
            column => $"target.{QuoteIdentifier(column)} = source.{QuoteIdentifier(column)}"));

        var sql = $"""
            UPDATE target
            SET target.ImportLogId = source.ImportLogId,
                target.SourceRowNumber = source.SourceRowNumber,
                target.RowHash = source.RowHash,
                target.UpdatedAt = SYSUTCDATETIME(),
                {assignments}
            FROM dbo.SapData AS target
            INNER JOIN dbo.SapDataStaging AS source
                ON source.ImportLogId = @ImportLogId
               AND source.BusinessKeyHash = target.BusinessKeyHash
            WHERE target.RowHash IS NULL OR target.RowHash <> source.RowHash;
            SELECT @@ROWCOUNT;
            """;

        return await ExecuteCountAsync(connection, transaction, sql, importLogId, cancellationToken);
    }

    private static async Task<int> InsertNewRowsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid importLogId,
        IReadOnlyList<string> sourceColumns,
        CancellationToken cancellationToken)
    {
        var quotedColumns = string.Join(", ", sourceColumns.Select(QuoteIdentifier));
        var selectedColumns = string.Join(", ", sourceColumns.Select(column => $"source.{QuoteIdentifier(column)}"));

        var sql = $"""
            INSERT INTO dbo.SapData
                (ImportLogId, SourceRowNumber, BusinessKeyHash, RowHash, {quotedColumns})
            SELECT source.ImportLogId,
                   source.SourceRowNumber,
                   source.BusinessKeyHash,
                   source.RowHash,
                   {selectedColumns}
            FROM dbo.SapDataStaging AS source
            WHERE source.ImportLogId = @ImportLogId
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM dbo.SapData AS target
                  WHERE target.BusinessKeyHash = source.BusinessKeyHash
              );
            SELECT @@ROWCOUNT;
            """;

        return await ExecuteCountAsync(connection, transaction, sql, importLogId, cancellationToken);
    }

    private static async Task<int> ExecuteCountAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        Guid importLogId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = 300;
        command.Parameters.Add(new SqlParameter("@ImportLogId", SqlDbType.UniqueIdentifier) { Value = importLogId });
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<bool> IsAlreadyCompletedAsync(
        SqlConnection connection,
        string fileHash,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT CASE WHEN EXISTS
            (
                SELECT 1
                FROM dbo.ImportLog
                WHERE FileHash = @FileHash AND Status = N'Completed'
            ) THEN 1 ELSE 0 END;
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new SqlParameter("@FileHash", SqlDbType.Char, 64) { Value = fileHash });
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<List<string>> LoadSourceColumnsAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT [name]
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'dbo.SapDataStaging')
              AND [name] NOT IN
                  (N'StagingId', N'ImportLogId', N'SourceRowNumber', N'BusinessKeyHash', N'RowHash', N'LoadedAt')
            ORDER BY column_id;
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var columns = new List<string>(ExpectedSourceColumnCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static void ValidateDatabaseSchema(IReadOnlyList<string> sourceColumns)
    {
        if (sourceColumns.Count != ExpectedSourceColumnCount)
        {
            throw new InvalidOperationException(
                $"SapDataStaging has {sourceColumns.Count} source columns; expected {ExpectedSourceColumnCount}.");
        }

        if (!sourceColumns.Contains(FirstBusinessKeyColumn, StringComparer.Ordinal)
            || !sourceColumns.Contains(SecondBusinessKeyColumn, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("SapDataStaging does not contain the configured business key columns.");
        }
    }

    private static async Task<Guid> CreateImportLogAsync(
        SqlConnection connection,
        string fileName,
        string fileHash,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.ImportLog (FileName, FileHash, Status)
            OUTPUT INSERTED.Id
            VALUES (@FileName, @FileHash, N'Processing');
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new SqlParameter("@FileName", SqlDbType.NVarChar, 260) { Value = fileName });
        command.Parameters.Add(new SqlParameter("@FileHash", SqlDbType.Char, 64) { Value = fileHash });
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("ImportLog did not return an identifier."));
    }

    private static async Task MarkCompletedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid importLogId,
        int totalRows,
        int insertedRows,
        int updatedRows,
        int unchangedRows,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.ImportLog
            SET Status = N'Completed',
                CompletedAt = SYSUTCDATETIME(),
                TotalRows = @TotalRows,
                InsertedRows = @InsertedRows,
                UpdatedRows = @UpdatedRows,
                UnchangedRows = @UnchangedRows,
                ErrorRows = 0,
                ErrorMessage = NULL
            WHERE Id = @ImportLogId;
            """;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.Add(new SqlParameter("@ImportLogId", SqlDbType.UniqueIdentifier) { Value = importLogId });
        command.Parameters.Add(new SqlParameter("@TotalRows", SqlDbType.Int) { Value = totalRows });
        command.Parameters.Add(new SqlParameter("@InsertedRows", SqlDbType.Int) { Value = insertedRows });
        command.Parameters.Add(new SqlParameter("@UpdatedRows", SqlDbType.Int) { Value = updatedRows });
        command.Parameters.Add(new SqlParameter("@UnchangedRows", SqlDbType.Int) { Value = unchangedRows });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkFailedAsync(
        SqlConnection connection,
        Guid importLogId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.ImportLog
            SET Status = N'Failed',
                CompletedAt = SYSUTCDATETIME(),
                ErrorRows = 1,
                ErrorMessage = @ErrorMessage
            WHERE Id = @ImportLogId;
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new SqlParameter("@ImportLogId", SqlDbType.UniqueIdentifier) { Value = importLogId });
        command.Parameters.Add(new SqlParameter("@ErrorMessage", SqlDbType.NVarChar, -1)
        {
            Value = exception.ToString()
        });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? ConvertCellValue(object? value) => value switch
    {
        null => null,
        string text => text,
        DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture),
        TimeSpan timeSpan => timeSpan.ToString("c", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    private static byte[] ComputeValueHash(IEnumerable<string?> values)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var value in values)
            {
                if (value is null)
                {
                    writer.Write(-1);
                    continue;
                }

                var bytes = Encoding.UTF8.GetBytes(value);
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
        }

        return SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
    }

    private static string QuoteIdentifier(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private sealed record ImportCounts(int Total, int Inserted, int Updated, int Unchanged);
}

internal static class ReadOnlyListExtensions
{
    public static int IndexOf(this IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }
}
