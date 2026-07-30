USE [SapDataSync];
GO

IF COL_LENGTH(N'dbo.ImportLog', N'DeletedRows') IS NULL
BEGIN
    ALTER TABLE dbo.ImportLog
        ADD DeletedRows INT NOT NULL
            CONSTRAINT DF_ImportLog_DeletedRows DEFAULT 0 WITH VALUES;
END;
GO

IF COL_LENGTH(N'dbo.ImportLog', N'SoftDeleteEnabled') IS NULL
BEGIN
    ALTER TABLE dbo.ImportLog
        ADD SoftDeleteEnabled BIT NOT NULL
            CONSTRAINT DF_ImportLog_SoftDeleteEnabled DEFAULT 0 WITH VALUES;
END;
GO

IF COL_LENGTH(N'dbo.SapData', N'IsDeleted') IS NULL
BEGIN
    ALTER TABLE dbo.SapData
        ADD IsDeleted BIT NOT NULL
            CONSTRAINT DF_SapData_IsDeleted DEFAULT 0 WITH VALUES;
END;
GO

IF COL_LENGTH(N'dbo.SapData', N'DeletedAt') IS NULL
BEGIN
    ALTER TABLE dbo.SapData ADD DeletedAt DATETIME2(0) NULL;
END;
GO

IF OBJECT_ID(N'dbo.SapDataChangeLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SapDataChangeLog
    (
        Id BIGINT IDENTITY(1, 1) NOT NULL
            CONSTRAINT PK_SapDataChangeLog PRIMARY KEY,
        ImportLogId UNIQUEIDENTIFIER NOT NULL,
        SapDataId BIGINT NOT NULL,
        SourceRowNumber INT NULL,
        BusinessKeyHash BINARY(32) NOT NULL,
        ShippingInstructionsId NVARCHAR(500) NULL,
        UniqueNumber NVARCHAR(500) NULL,
        ChangeType NVARCHAR(10) NOT NULL,
        OldValuesJson NVARCHAR(MAX) NULL,
        NewValuesJson NVARCHAR(MAX) NULL,
        CreatedAt DATETIME2(0) NOT NULL
            CONSTRAINT DF_SapDataChangeLog_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_SapDataChangeLog_ImportLog
            FOREIGN KEY (ImportLogId) REFERENCES dbo.ImportLog(Id),
        CONSTRAINT CK_SapDataChangeLog_ChangeType
            CHECK (ChangeType IN (N'Insert', N'Update', N'Delete')),
        CONSTRAINT CK_SapDataChangeLog_OldValuesJson
            CHECK (OldValuesJson IS NULL OR ISJSON(OldValuesJson) = 1),
        CONSTRAINT CK_SapDataChangeLog_NewValuesJson
            CHECK (NewValuesJson IS NULL OR ISJSON(NewValuesJson) = 1),
        CONSTRAINT CK_SapDataChangeLog_HasValues
            CHECK (OldValuesJson IS NOT NULL OR NewValuesJson IS NOT NULL),
        CONSTRAINT UX_SapDataChangeLog_ImportRecordType
            UNIQUE (ImportLogId, SapDataId, ChangeType)
    );

    CREATE INDEX IX_SapDataChangeLog_ImportLogId
        ON dbo.SapDataChangeLog(ImportLogId, Id)
        INCLUDE (SapDataId, SourceRowNumber, ChangeType,
                 ShippingInstructionsId, UniqueNumber, CreatedAt);
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.SapData')
      AND name = N'IX_SapData_IsDeleted_ImportLogId'
)
BEGIN
    CREATE INDEX IX_SapData_IsDeleted_ImportLogId
        ON dbo.SapData(IsDeleted, ImportLogId);
END;
GO
