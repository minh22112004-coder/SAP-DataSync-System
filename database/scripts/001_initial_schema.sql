SET NOCOUNT ON;

IF DB_ID(N'SapDataSync') IS NULL
BEGIN
    CREATE DATABASE [SapDataSync];
END;
GO

USE [SapDataSync];
GO

IF OBJECT_ID(N'dbo.ImportLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ImportLog
    (
        Id UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT PK_ImportLog PRIMARY KEY
            CONSTRAINT DF_ImportLog_Id DEFAULT NEWSEQUENTIALID(),
        FileName NVARCHAR(260) NOT NULL,
        FileHash CHAR(64) NOT NULL,
        Status NVARCHAR(20) NOT NULL,
        StartedAt DATETIME2(0) NOT NULL
            CONSTRAINT DF_ImportLog_StartedAt DEFAULT SYSUTCDATETIME(),
        CompletedAt DATETIME2(0) NULL,
        TotalRows INT NOT NULL CONSTRAINT DF_ImportLog_TotalRows DEFAULT 0,
        InsertedRows INT NOT NULL CONSTRAINT DF_ImportLog_InsertedRows DEFAULT 0,
        UpdatedRows INT NOT NULL CONSTRAINT DF_ImportLog_UpdatedRows DEFAULT 0,
        UnchangedRows INT NOT NULL CONSTRAINT DF_ImportLog_UnchangedRows DEFAULT 0,
        ErrorRows INT NOT NULL CONSTRAINT DF_ImportLog_ErrorRows DEFAULT 0,
        ErrorMessage NVARCHAR(MAX) NULL,
        CreatedAt DATETIME2(0) NOT NULL
            CONSTRAINT DF_ImportLog_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_ImportLog_Status
            CHECK (Status IN (N'Pending', N'Processing', N'Completed', N'Failed'))
    );

    CREATE INDEX IX_ImportLog_FileHash ON dbo.ImportLog(FileHash);
    CREATE INDEX IX_ImportLog_StartedAt ON dbo.ImportLog(StartedAt DESC);
END;
GO

DECLARE @SourceColumns NVARCHAR(MAX) = N'
        [Shipping Instructions ID] NVARCHAR(MAX) NULL,
        [SI Status] NVARCHAR(MAX) NULL,
        [Merge ID] NVARCHAR(MAX) NULL,
        [Merge Qty] NVARCHAR(MAX) NULL,
        [Merge Open Qty] NVARCHAR(MAX) NULL,
        [Business Scenario] NVARCHAR(MAX) NULL,
        [Marketing Office] NVARCHAR(MAX) NULL,
        [SHIPMENT TYPE] NVARCHAR(MAX) NULL,
        [Customer Name] NVARCHAR(MAX) NULL,
        [Ship to party name] NVARCHAR(MAX) NULL,
        [Selling Plant] NVARCHAR(MAX) NULL,
        [Origin] NVARCHAR(MAX) NULL,
        [Inco Term] NVARCHAR(MAX) NULL,
        [Payment Term] NVARCHAR(MAX) NULL,
        [Payment Type] NVARCHAR(MAX) NULL,
        [SI Quantity (SI Qty for Line Item)] NVARCHAR(MAX) NULL,
        [UOM] NVARCHAR(MAX) NULL,
        [OIL Sales] NVARCHAR(MAX) NULL,
        [OIL SO] NVARCHAR(MAX) NULL,
        [OIL Purchase] NVARCHAR(MAX) NULL,
        [OIL GRN] NVARCHAR(MAX) NULL,
        [OIL IR] NVARCHAR(MAX) NULL,
        [Origin Sales] NVARCHAR(MAX) NULL,
        [Origin SO] NVARCHAR(MAX) NULL,
        [Origin OBD] NVARCHAR(MAX) NULL,
        [Origin Invoice] NVARCHAR(MAX) NULL,
        [Unique Number] NVARCHAR(MAX) NULL,
        [Unique Lot] NVARCHAR(MAX) NULL,
        [Grade] NVARCHAR(MAX) NULL,
        [Grade Description] NVARCHAR(MAX) NULL,
        [Customer Item Code] NVARCHAR(MAX) NULL,
        [Customer Item Desc] NVARCHAR(MAX) NULL,
        [Origin Item Code] NVARCHAR(MAX) NULL,
        [Origin Item Desc] NVARCHAR(MAX) NULL,
        [Sales Office Description] NVARCHAR(MAX) NULL,
        [Trader] NVARCHAR(MAX) NULL,
        [Customer OPS] NVARCHAR(MAX) NULL,
        [Shipment Month] NVARCHAR(MAX) NULL,
        [Shipment Year] NVARCHAR(MAX) NULL,
        [Delivery Month] NVARCHAR(MAX) NULL,
        [Delivery Year] NVARCHAR(MAX) NULL,
        [Customer Requested Ship date] NVARCHAR(MAX) NULL,
        [Customer Requested Delivery date] NVARCHAR(MAX) NULL,
        [Olam Ship date] NVARCHAR(MAX) NULL,
        [Olam Delivery date] NVARCHAR(MAX) NULL,
        [First Committed Ship Date] NVARCHAR(MAX) NULL,
        [First Committed Delivery Date] NVARCHAR(MAX) NULL,
        [Estimated Time of Departure (Date)] NVARCHAR(MAX) NULL,
        [Estimated Time of Arrival (Date)] NVARCHAR(MAX) NULL,
        [POS Country] NVARCHAR(MAX) NULL,
        [POL] NVARCHAR(MAX) NULL,
        [POD Country] NVARCHAR(MAX) NULL,
        [POD] NVARCHAR(MAX) NULL,
        [Customer PO#] NVARCHAR(MAX) NULL,
        [Customer PO Date] NVARCHAR(MAX) NULL,
        [Product Origin] NVARCHAR(MAX) NULL,
        [SAMPLE CODE] NVARCHAR(MAX) NULL,
        [Packaging] NVARCHAR(MAX) NULL,
        [Package Count] NVARCHAR(MAX) NULL,
        [Pallet Requires] NVARCHAR(MAX) NULL,
        [Send Samples To] NVARCHAR(MAX) NULL,
        [Samples To Be Sent By] NVARCHAR(MAX) NULL,
        [PSS Sent Date] NVARCHAR(MAX) NULL,
        [Date of action] NVARCHAR(MAX) NULL,
        [PSS Sent] NVARCHAR(MAX) NULL,
        [Sample Status(Customer)] NVARCHAR(MAX) NULL,
        [Lab Type] NVARCHAR(MAX) NULL,
        [Type of Sample] NVARCHAR(MAX) NULL,
        [Tested Parameter] NVARCHAR(MAX) NULL,
        [Sample Sent Date(Labs)] NVARCHAR(MAX) NULL,
        [Sample Reached Date(Labs)] NVARCHAR(MAX) NULL,
        [Expected Date of Result(Labs)] NVARCHAR(MAX) NULL,
        [Response Received Date(Labs)] NVARCHAR(MAX) NULL,
        [Sample Status(Labs)] NVARCHAR(MAX) NULL,
        [Revised Sample Sent Date(Customer)] NVARCHAR(MAX) NULL,
        [Revised Response Received Date(Customer)] NVARCHAR(MAX) NULL,
        [Revised Sample Status(Customer)] NVARCHAR(MAX) NULL,
        [Revised Sample Sent Date(Labs)] NVARCHAR(MAX) NULL,
        [Revised Response Received Date(Labs)] NVARCHAR(MAX) NULL,
        [Revised Sample Status(Labs)] NVARCHAR(MAX) NULL,
        [Quality Test(Internal)] NVARCHAR(MAX) NULL,
        [RFA Certified] NVARCHAR(MAX) NULL,
        [Samples Managed By] NVARCHAR(MAX) NULL,
        [Sample Sent from Lot No] NVARCHAR(MAX) NULL,
        [Sample sent Quantity] NVARCHAR(MAX) NULL,
        [Sample sent UOM] NVARCHAR(MAX) NULL,
        [Courier Name] NVARCHAR(MAX) NULL,
        [Courier ref#] NVARCHAR(MAX) NULL,
        [Stuffing Date] NVARCHAR(MAX) NULL,
        [Booking Number] NVARCHAR(MAX) NULL,
        [Booking date] NVARCHAR(MAX) NULL,
        [Ship Line / Air Line] NVARCHAR(MAX) NULL,
        [MBL] NVARCHAR(MAX) NULL,
        [BL date] NVARCHAR(MAX) NULL,
        [Vessel Name Text] NVARCHAR(MAX) NULL,
        [Container Number] NVARCHAR(MAX) NULL,
        [Container Size] NVARCHAR(MAX) NULL,
        [Seal No] NVARCHAR(MAX) NULL,
        [No Of Packing] NVARCHAR(MAX) NULL,
        [Quantity Per Pac] NVARCHAR(MAX) NULL,
        [Packing UOM] NVARCHAR(MAX) NULL,
        [Packing Type] NVARCHAR(MAX) NULL,
        [Gross Weight] NVARCHAR(MAX) NULL,
        [Net Weight] NVARCHAR(MAX) NULL,
        [Document Upload Status] NVARCHAR(MAX) NULL,
        [Revise Reason] NVARCHAR(MAX) NULL,
        [Revise Remarks] NVARCHAR(MAX) NULL,
        [Revised From] NVARCHAR(MAX) NULL,
        [SI Created on] NVARCHAR(MAX) NULL,
        [SI Send to OE Date] NVARCHAR(MAX) NULL,
        [SI Assign to Plant Date] NVARCHAR(MAX) NULL,
        [SI Send to DE] NVARCHAR(MAX) NULL,
        [DES Invoice] NVARCHAR(MAX) NULL,
        [DES Invoice Date] NVARCHAR(MAX) NULL,
        [DES GR/IR] NVARCHAR(MAX) NULL,
        [Ori Del Picking Status] NVARCHAR(MAX) NULL,
        [Ori Del PGI Status] NVARCHAR(MAX) NULL,
        [Ori Inv Accounting Status] NVARCHAR(MAX) NULL,
        [Supplier Invoice] NVARCHAR(MAX) NULL,
        [Supplier Invoice Date] NVARCHAR(MAX) NULL,
        [OIL Invoice Number] NVARCHAR(MAX) NULL,
        [OIL Invoice Amount(USD)] NVARCHAR(MAX) NULL,
        [OIL Invoice Quantity (MT)] NVARCHAR(MAX) NULL,
        [OIL Invoice Rate] NVARCHAR(MAX) NULL,
        [Supplier Invoice Status] NVARCHAR(MAX) NULL,
        [Freight Invoice Number] NVARCHAR(MAX) NULL,
        [Freight Invoice Value] NVARCHAR(MAX) NULL,
        [Freight Invoice Status] NVARCHAR(MAX) NULL,
        [AWB Tracking] NVARCHAR(MAX) NULL,
        [Payment Status] NVARCHAR(MAX) NULL,
        [Material Group Desc.] NVARCHAR(MAX) NULL,
        [Comments] NVARCHAR(MAX) NULL,
        [Process Status] NVARCHAR(MAX) NULL,
        [SI Created By] NVARCHAR(MAX) NULL,
        [Supplier Name] NVARCHAR(MAX) NULL,
        [Valuation Type] NVARCHAR(MAX) NULL,
        [Production Date] NVARCHAR(MAX) NULL,
        [Best Before Date] NVARCHAR(MAX) NULL,
        [PlantCode] NVARCHAR(MAX) NULL,
        [Sales Office] NVARCHAR(MAX) NULL,
        [Revised By] NVARCHAR(MAX) NULL,
        [Revised Date] NVARCHAR(MAX) NULL,
        [Revised Aging] NVARCHAR(MAX) NULL,
        [Batch No] NVARCHAR(MAX) NULL,
        [Customer Reference No] NVARCHAR(MAX) NULL,
        [Origin Remarks] NVARCHAR(MAX) NULL,
        [Origin remarks updated by] NVARCHAR(MAX) NULL,
        [Origin remarks updated date] NVARCHAR(MAX) NULL,
        [Origin remarks updated time] NVARCHAR(MAX) NULL';

IF OBJECT_ID(N'dbo.SapDataStaging', N'U') IS NULL
BEGIN
    DECLARE @CreateStaging NVARCHAR(MAX) = N'
    CREATE TABLE dbo.SapDataStaging
    (
        StagingId BIGINT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_SapDataStaging PRIMARY KEY,
        ImportLogId UNIQUEIDENTIFIER NOT NULL,
        SourceRowNumber INT NOT NULL,
' + @SourceColumns + N',
        LoadedAt DATETIME2(0) NOT NULL
            CONSTRAINT DF_SapDataStaging_LoadedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_SapDataStaging_ImportLog
            FOREIGN KEY (ImportLogId) REFERENCES dbo.ImportLog(Id),
        CONSTRAINT UQ_SapDataStaging_ImportRow
            UNIQUE (ImportLogId, SourceRowNumber)
    );';

    EXEC sys.sp_executesql @CreateStaging;
    CREATE INDEX IX_SapDataStaging_ImportLogId ON dbo.SapDataStaging(ImportLogId);
END;

IF OBJECT_ID(N'dbo.SapData', N'U') IS NULL
BEGIN
    DECLARE @CreateMain NVARCHAR(MAX) = N'
    CREATE TABLE dbo.SapData
    (
        Id BIGINT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_SapData PRIMARY KEY,
        ImportLogId UNIQUEIDENTIFIER NOT NULL,
        SourceRowNumber INT NOT NULL,
        BusinessKeyHash BINARY(32) NULL,
        RowHash BINARY(32) NULL,
' + @SourceColumns + N',
        CreatedAt DATETIME2(0) NOT NULL
            CONSTRAINT DF_SapData_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIME2(0) NOT NULL
            CONSTRAINT DF_SapData_UpdatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_SapData_ImportLog
            FOREIGN KEY (ImportLogId) REFERENCES dbo.ImportLog(Id)
    );';

    EXEC sys.sp_executesql @CreateMain;
    CREATE UNIQUE INDEX UX_SapData_BusinessKeyHash
        ON dbo.SapData(BusinessKeyHash)
        WHERE BusinessKeyHash IS NOT NULL;
    CREATE INDEX IX_SapData_ImportLogId ON dbo.SapData(ImportLogId);
END;
GO

