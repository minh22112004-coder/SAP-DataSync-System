USE [SapDataSync];
GO

CREATE OR ALTER PROCEDURE dbo.SyncSapData
    @ImportLogId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS
    (
        SELECT 1
        FROM dbo.ImportLog
        WHERE Id = @ImportLogId
          AND Status = N'Processing'
    )
    BEGIN
        THROW 50010, 'ImportLog must exist with Processing status before synchronization.', 1;
    END;

    IF EXISTS
    (
        SELECT 1
        FROM dbo.SapDataStaging
        WHERE ImportLogId = @ImportLogId
          AND BusinessKeyHash IS NULL
    )
    BEGIN
        THROW 50011, 'Staging contains a row without BusinessKeyHash.', 1;
    END;

    IF EXISTS
    (
        SELECT BusinessKeyHash
        FROM dbo.SapDataStaging
        WHERE ImportLogId = @ImportLogId
        GROUP BY BusinessKeyHash
        HAVING COUNT_BIG(*) > 1
    )
    BEGIN
        THROW 50012, 'Staging contains duplicate business keys for this import.', 1;
    END;

    DECLARE @Assignments NVARCHAR(MAX);
    DECLARE @QuotedColumns NVARCHAR(MAX);
    DECLARE @SelectedColumns NVARCHAR(MAX);
    DECLARE @TargetJsonColumns NVARCHAR(MAX);
    DECLARE @SourceJsonColumns NVARCHAR(MAX);

    SELECT
        @Assignments = STRING_AGG(
            CONVERT(NVARCHAR(MAX), N'target.' + QUOTENAME([name]) + N' = source.' + QUOTENAME([name])),
            N',' + CHAR(10) + N'                ')
            WITHIN GROUP (ORDER BY column_id),
        @QuotedColumns = STRING_AGG(
            CONVERT(NVARCHAR(MAX), QUOTENAME([name])),
            N', ')
            WITHIN GROUP (ORDER BY column_id),
        @SelectedColumns = STRING_AGG(
            CONVERT(NVARCHAR(MAX), N'source.' + QUOTENAME([name])),
            N', ')
            WITHIN GROUP (ORDER BY column_id),
        @TargetJsonColumns = STRING_AGG(
            CONVERT(NVARCHAR(MAX),
                N'(N''' + REPLACE([name], N'''', N'''''') +
                N''', CONVERT(NVARCHAR(MAX), target.' + QUOTENAME([name]) + N'))'),
            N', ')
            WITHIN GROUP (ORDER BY column_id),
        @SourceJsonColumns = STRING_AGG(
            CONVERT(NVARCHAR(MAX),
                N'(N''' + REPLACE([name], N'''', N'''''') +
                N''', CONVERT(NVARCHAR(MAX), source.' + QUOTENAME([name]) + N'))'),
            N', ')
            WITHIN GROUP (ORDER BY column_id)
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.SapDataStaging')
      AND [name] NOT IN
          (N'StagingId', N'ImportLogId', N'SourceRowNumber', N'BusinessKeyHash', N'RowHash', N'LoadedAt');

    IF @Assignments IS NULL OR @QuotedColumns IS NULL OR @SelectedColumns IS NULL
       OR @TargetJsonColumns IS NULL OR @SourceJsonColumns IS NULL
    BEGIN
        THROW 50013, 'Could not discover the SAP source columns.', 1;
    END;

    DECLARE @Sql NVARCHAR(MAX) = N'
        INSERT INTO dbo.SapDataChangeLog
            (ImportLogId, SapDataId, SourceRowNumber, BusinessKeyHash,
             ShippingInstructionsId, UniqueNumber, ChangeType,
             OldValuesJson, NewValuesJson)
        SELECT @ImportLogId,
               target.Id,
               source.SourceRowNumber,
               target.BusinessKeyHash,
               CONVERT(NVARCHAR(500), source.[Shipping Instructions ID]),
               CONVERT(NVARCHAR(500), source.[Unique Number]),
               N''Update'',
               (SELECT jsonValue.Field, jsonValue.Value
                FROM (VALUES ' + @TargetJsonColumns + N',
                      (N''IsDeleted'', CASE WHEN target.IsDeleted = 1 THEN N''True'' ELSE N''False'' END))
                    AS jsonValue(Field, Value)
                FOR JSON PATH, INCLUDE_NULL_VALUES),
               (SELECT jsonValue.Field, jsonValue.Value
                FROM (VALUES ' + @SourceJsonColumns + N',
                      (N''IsDeleted'', N''False''))
                    AS jsonValue(Field, Value)
                FOR JSON PATH, INCLUDE_NULL_VALUES)
        FROM dbo.SapData AS target
        INNER JOIN dbo.SapDataStaging AS source
            ON source.ImportLogId = @ImportLogId
           AND source.BusinessKeyHash = target.BusinessKeyHash
        WHERE target.IsDeleted = 1
           OR target.RowHash IS NULL
           OR target.RowHash <> source.RowHash;

        DECLARE @AuditedUpdatedRows INT = @@ROWCOUNT;

        UPDATE target
        SET target.ImportLogId = source.ImportLogId,
            target.SourceRowNumber = source.SourceRowNumber,
            target.RowHash = source.RowHash,
            target.IsDeleted = 0,
            target.DeletedAt = NULL,
            target.UpdatedAt = SYSUTCDATETIME(),
            ' + @Assignments + N'
        FROM dbo.SapData AS target
        INNER JOIN dbo.SapDataStaging AS source
            ON source.ImportLogId = @ImportLogId
           AND source.BusinessKeyHash = target.BusinessKeyHash
        WHERE target.IsDeleted = 1
           OR target.RowHash IS NULL
           OR target.RowHash <> source.RowHash;

        DECLARE @UpdatedRows INT = @@ROWCOUNT;

        IF @AuditedUpdatedRows <> @UpdatedRows
            THROW 50014, ''Update audit count does not match synchronized rows.'', 1;

        DECLARE @InsertedRecords TABLE
        (
            SapDataId BIGINT NOT NULL,
            SourceRowNumber INT NOT NULL,
            BusinessKeyHash BINARY(32) NOT NULL
        );

        INSERT INTO dbo.SapData
            (ImportLogId, SourceRowNumber, BusinessKeyHash, RowHash, ' + @QuotedColumns + N')
        OUTPUT inserted.Id, inserted.SourceRowNumber, inserted.BusinessKeyHash
            INTO @InsertedRecords(SapDataId, SourceRowNumber, BusinessKeyHash)
        SELECT source.ImportLogId,
               source.SourceRowNumber,
               source.BusinessKeyHash,
               source.RowHash,
               ' + @SelectedColumns + N'
        FROM dbo.SapDataStaging AS source
        WHERE source.ImportLogId = @ImportLogId
          AND NOT EXISTS
          (
              SELECT 1
              FROM dbo.SapData AS target
              WHERE target.BusinessKeyHash = source.BusinessKeyHash
          );

        DECLARE @InsertedRows INT = (SELECT COUNT(*) FROM @InsertedRecords);

        INSERT INTO dbo.SapDataChangeLog
            (ImportLogId, SapDataId, SourceRowNumber, BusinessKeyHash,
             ShippingInstructionsId, UniqueNumber, ChangeType,
             OldValuesJson, NewValuesJson)
        SELECT @ImportLogId,
               target.Id,
               insertedRecord.SourceRowNumber,
               target.BusinessKeyHash,
               CONVERT(NVARCHAR(500), target.[Shipping Instructions ID]),
               CONVERT(NVARCHAR(500), target.[Unique Number]),
               N''Insert'',
               NULL,
               (SELECT jsonValue.Field, jsonValue.Value
                FROM (VALUES ' + @TargetJsonColumns + N',
                      (N''IsDeleted'', CASE WHEN target.IsDeleted = 1 THEN N''True'' ELSE N''False'' END))
                    AS jsonValue(Field, Value)
                WHERE jsonValue.Value IS NOT NULL
                FOR JSON PATH, INCLUDE_NULL_VALUES)
        FROM @InsertedRecords AS insertedRecord
        INNER JOIN dbo.SapData AS target
            ON target.Id = insertedRecord.SapDataId;

        IF @@ROWCOUNT <> @InsertedRows
            THROW 50015, ''Insert audit count does not match synchronized rows.'', 1;

        INSERT INTO dbo.SapDataChangeLog
            (ImportLogId, SapDataId, SourceRowNumber, BusinessKeyHash,
             ShippingInstructionsId, UniqueNumber, ChangeType,
             OldValuesJson, NewValuesJson)
        SELECT @ImportLogId,
               target.Id,
               NULL,
               target.BusinessKeyHash,
               CONVERT(NVARCHAR(500), target.[Shipping Instructions ID]),
               CONVERT(NVARCHAR(500), target.[Unique Number]),
               N''Delete'',
               (SELECT jsonValue.Field, jsonValue.Value
                FROM (VALUES ' + @TargetJsonColumns + N',
                      (N''IsDeleted'', CASE WHEN target.IsDeleted = 1 THEN N''True'' ELSE N''False'' END))
                    AS jsonValue(Field, Value)
                FOR JSON PATH, INCLUDE_NULL_VALUES),
               (SELECT jsonValue.Field, jsonValue.Value
                FROM (VALUES ' + @TargetJsonColumns + N',
                      (N''IsDeleted'', N''True''))
                    AS jsonValue(Field, Value)
                FOR JSON PATH, INCLUDE_NULL_VALUES)
        FROM dbo.SapData AS target
        INNER JOIN dbo.ImportLog AS targetImport
            ON targetImport.Id = target.ImportLogId
        INNER JOIN dbo.ImportLog AS currentImport
            ON currentImport.Id = @ImportLogId
        WHERE target.IsDeleted = 0
          AND currentImport.SoftDeleteEnabled = 1
          AND targetImport.Product = currentImport.Product
          AND targetImport.SalesOrganization = currentImport.SalesOrganization
          AND NOT EXISTS
          (
              SELECT 1
              FROM dbo.SapDataStaging AS source
              WHERE source.ImportLogId = @ImportLogId
                AND source.BusinessKeyHash = target.BusinessKeyHash
          );

        DECLARE @AuditedDeletedRows INT = @@ROWCOUNT;

        UPDATE target
        SET target.ImportLogId = @ImportLogId,
            target.IsDeleted = 1,
            target.DeletedAt = SYSUTCDATETIME(),
            target.UpdatedAt = SYSUTCDATETIME()
        FROM dbo.SapData AS target
        INNER JOIN dbo.ImportLog AS targetImport
            ON targetImport.Id = target.ImportLogId
        INNER JOIN dbo.ImportLog AS currentImport
            ON currentImport.Id = @ImportLogId
        WHERE target.IsDeleted = 0
          AND currentImport.SoftDeleteEnabled = 1
          AND targetImport.Product = currentImport.Product
          AND targetImport.SalesOrganization = currentImport.SalesOrganization
          AND NOT EXISTS
          (
              SELECT 1
              FROM dbo.SapDataStaging AS source
              WHERE source.ImportLogId = @ImportLogId
                AND source.BusinessKeyHash = target.BusinessKeyHash
          );

        DECLARE @DeletedRows INT = @@ROWCOUNT;

        IF @AuditedDeletedRows <> @DeletedRows
            THROW 50016, ''Delete audit count does not match synchronized rows.'', 1;

        DECLARE @TotalRows INT =
        (
            SELECT COUNT(*)
            FROM dbo.SapDataStaging
            WHERE ImportLogId = @ImportLogId
        );

        SELECT @TotalRows AS TotalRows,
               @InsertedRows AS InsertedRows,
               @UpdatedRows AS UpdatedRows,
               @TotalRows - @InsertedRows - @UpdatedRows AS UnchangedRows,
               @DeletedRows AS DeletedRows;';

    EXEC sys.sp_executesql
        @Sql,
        N'@ImportLogId UNIQUEIDENTIFIER',
        @ImportLogId = @ImportLogId;
END;
GO
