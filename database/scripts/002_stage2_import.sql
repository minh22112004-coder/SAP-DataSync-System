USE [SapDataSync];
GO

IF COL_LENGTH(N'dbo.SapDataStaging', N'BusinessKeyHash') IS NULL
BEGIN
    ALTER TABLE dbo.SapDataStaging
        ADD BusinessKeyHash BINARY(32) NULL;
END;
GO

IF COL_LENGTH(N'dbo.SapDataStaging', N'RowHash') IS NULL
BEGIN
    ALTER TABLE dbo.SapDataStaging
        ADD RowHash BINARY(32) NULL;
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.SapDataStaging')
      AND name = N'IX_SapDataStaging_ImportBusinessKey'
)
BEGIN
    CREATE INDEX IX_SapDataStaging_ImportBusinessKey
        ON dbo.SapDataStaging(ImportLogId, BusinessKeyHash);
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.ImportLog')
      AND name = N'UX_ImportLog_CompletedFileHash'
)
BEGIN
    CREATE UNIQUE INDEX UX_ImportLog_CompletedFileHash
        ON dbo.ImportLog(FileHash)
        WHERE Status = N'Completed';
END;
GO
