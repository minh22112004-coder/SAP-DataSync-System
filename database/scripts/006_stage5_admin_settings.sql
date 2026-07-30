USE [SapDataSync];
GO

IF OBJECT_ID(N'dbo.AdminAccount', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AdminAccount
    (
        Id TINYINT NOT NULL CONSTRAINT PK_AdminAccount PRIMARY KEY,
        PasswordHash VARBINARY(64) NOT NULL,
        PasswordSalt VARBINARY(32) NOT NULL,
        PasswordIterations INT NOT NULL,
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_AdminAccount_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_AdminAccount_UpdatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_AdminAccount_Singleton CHECK (Id = 1),
        CONSTRAINT CK_AdminAccount_Iterations CHECK (PasswordIterations >= 100000)
    );
END;
GO

IF OBJECT_ID(N'dbo.AppConfiguration', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AppConfiguration
    (
        [Key] NVARCHAR(100) NOT NULL CONSTRAINT PK_AppConfiguration PRIMARY KEY,
        ProtectedValue NVARCHAR(MAX) NULL,
        UpdatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_AppConfiguration_UpdatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedBy NVARCHAR(100) NOT NULL
    );
END;
GO

IF OBJECT_ID(N'dbo.AdminAuditLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AdminAuditLog
    (
        Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AdminAuditLog PRIMARY KEY,
        EventType NVARCHAR(50) NOT NULL,
        Detail NVARCHAR(500) NOT NULL,
        RemoteIp NVARCHAR(64) NULL,
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_AdminAuditLog_CreatedAt DEFAULT SYSUTCDATETIME()
    );

    CREATE INDEX IX_AdminAuditLog_CreatedAt ON dbo.AdminAuditLog(CreatedAt DESC);
END;
GO
