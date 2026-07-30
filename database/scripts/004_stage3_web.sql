USE [SapDataSync];
GO

IF COL_LENGTH(N'dbo.ImportLog', N'Product') IS NULL
BEGIN
    ALTER TABLE dbo.ImportLog
        ADD Product NVARCHAR(50) NOT NULL
            CONSTRAINT DF_ImportLog_Product DEFAULT N'12' WITH VALUES;
END;
GO

IF COL_LENGTH(N'dbo.ImportLog', N'SalesOrganization') IS NULL
BEGIN
    ALTER TABLE dbo.ImportLog
        ADD SalesOrganization NVARCHAR(50) NOT NULL
            CONSTRAINT DF_ImportLog_SalesOrganization DEFAULT N'SG50' WITH VALUES;
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.ImportLog')
      AND name = N'IX_ImportLog_ProductSalesOrganization'
)
BEGIN
    CREATE INDEX IX_ImportLog_ProductSalesOrganization
        ON dbo.ImportLog(Product, SalesOrganization, StartedAt DESC);
END;
GO

IF COL_LENGTH(N'dbo.SapData', N'WebSiCreatedDate') IS NULL
BEGIN
    ALTER TABLE dbo.SapData
        ADD WebSiCreatedDate AS
            CONVERT(NVARCHAR(10), LEFT([SI Created on], 10)) PERSISTED;
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.SapData')
      AND name = N'IX_SapData_WebSiCreatedDate'
)
BEGIN
    CREATE INDEX IX_SapData_WebSiCreatedDate
        ON dbo.SapData(WebSiCreatedDate DESC, Id DESC);
END;
GO
