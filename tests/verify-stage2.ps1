param(
    [string]$ExcelPath = ".\data\source\export.xlsx",
    [int]$ExpectedRows = 9726
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($env:MSSQL_SA_PASSWORD)) {
    throw "Set MSSQL_SA_PASSWORD in this PowerShell session before running the test."
}

if (-not (Test-Path -LiteralPath $ExcelPath -PathType Leaf)) {
    throw "Excel test file not found: $ExcelPath"
}

$hashBefore = (Get-FileHash -LiteralPath $ExcelPath -Algorithm SHA256).Hash

dotnet build .\SapDataSync.sln --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "The .NET build failed."
}

docker compose run --rm --no-deps --entrypoint python `
    -e PYTHONPYCACHEPREFIX=/tmp/pycache `
    etl-worker -m compileall -q /app/etl_worker
if ($LASTEXITCODE -ne 0) {
    throw "The Python ETL syntax check failed."
}

docker compose config --quiet
if ($LASTEXITCODE -ne 0) {
    throw "Docker Compose configuration is invalid."
}

$health = Invoke-RestMethod -Uri "http://localhost:8080/api/health"
if ($health.status -ne "Healthy" -or $health.database -ne "SapDataSync") {
    throw "Web/API or database health check failed."
}

$query = @"
SET NOCOUNT ON;
DECLARE @LatestImportId UNIQUEIDENTIFIER =
(
    SELECT TOP (1) Id
    FROM dbo.ImportLog
    WHERE Status = N'Completed' AND TotalRows = $ExpectedRows
    ORDER BY StartedAt DESC
);
SELECT CONCAT(
    (SELECT COUNT(*) FROM dbo.SapDataStaging), '|',
    (SELECT COUNT(*) FROM dbo.SapData), '|',
    (SELECT COUNT(*) FROM sys.columns
     WHERE object_id = OBJECT_ID(N'dbo.SapDataStaging')
       AND [name] NOT IN
           (N'StagingId', N'ImportLogId', N'SourceRowNumber', N'BusinessKeyHash', N'RowHash', N'LoadedAt')), '|',
    (SELECT COUNT(*) FROM dbo.ImportLog
     WHERE Status = N'Completed' AND TotalRows = $ExpectedRows), '|',
    (SELECT COUNT(*)
     FROM dbo.SapDataStaging AS source
     LEFT JOIN dbo.SapData AS target
       ON target.BusinessKeyHash = source.BusinessKeyHash
     WHERE source.ImportLogId = @LatestImportId
       AND (target.Id IS NULL OR target.RowHash <> source.RowHash)));
"@

$countsOutput = docker compose exec -T sqlserver `
    /opt/mssql-tools18/bin/sqlcmd `
    -S localhost -U sa -P $env:MSSQL_SA_PASSWORD -C `
    -d SapDataSync -Q $query -h -1 -W
if ($LASTEXITCODE -ne 0) {
    throw "Could not query Stage 2 database state."
}

$countsLine = ($countsOutput | Where-Object { $_ -match '^\d+\|\d+\|\d+\|\d+\|\d+$' } | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($countsLine)) {
    throw "Unexpected SQL count output: $($countsOutput -join ' ')"
}

$counts = $countsLine.Split('|') | ForEach-Object { [int]$_ }
if ($counts[0] -lt $ExpectedRows) {
    throw "Staging contains $($counts[0]) rows; expected at least $ExpectedRows."
}
if ($counts[1] -ne $ExpectedRows) {
    throw "SapData contains $($counts[1]) rows; expected exactly $ExpectedRows."
}
if ($counts[2] -ne 149) {
    throw "SapDataStaging contains $($counts[2]) source columns; expected 149."
}
if ($counts[3] -lt 1) {
    throw "No completed ImportLog with $ExpectedRows rows was found."
}
if ($counts[4] -ne 0) {
    throw "Found $($counts[4]) rows whose Staging and SapData hashes do not match."
}

$updateQuery = @"
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
BEGIN TRANSACTION;
DECLARE @ImportLogId UNIQUEIDENTIFIER =
(
    SELECT TOP (1) Id
    FROM dbo.ImportLog
    WHERE Status = N'Completed' AND TotalRows = $ExpectedRows
    ORDER BY StartedAt DESC
);
UPDATE dbo.ImportLog SET Status = N'Processing', SoftDeleteEnabled = 1 WHERE Id = @ImportLogId;
DECLARE @StagingId BIGINT =
(
    SELECT MIN(StagingId)
    FROM dbo.SapDataStaging
    WHERE ImportLogId = @ImportLogId
);
UPDATE dbo.SapDataStaging
SET [SI Status] = CONCAT(COALESCE([SI Status], N''), N'__AUDIT_TEST__'),
    RowHash = 0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF
WHERE StagingId = @StagingId;
DECLARE @SyncCounts TABLE
(
    TotalRows INT,
    InsertedRows INT,
    UpdatedRows INT,
    UnchangedRows INT,
    DeletedRows INT
);
INSERT INTO @SyncCounts
EXEC dbo.SyncSapData @ImportLogId = @ImportLogId;
SELECT CONCAT(TotalRows, '|', InsertedRows, '|', UpdatedRows, '|', UnchangedRows, '|', DeletedRows, '|',
    (SELECT COUNT(*) FROM dbo.SapDataChangeLog WHERE ImportLogId = @ImportLogId AND ChangeType = N'Update'), '|',
    (SELECT COUNT(*)
     FROM dbo.SapDataChangeLog AS changeLog
     CROSS APPLY OPENJSON(changeLog.NewValuesJson)
         WITH (Field NVARCHAR(500) '$.Field', Value NVARCHAR(MAX) '$.Value') AS jsonValue
     WHERE changeLog.ImportLogId = @ImportLogId
       AND changeLog.ChangeType = N'Update'
       AND jsonValue.Field = N'SI Status'
       AND jsonValue.Value LIKE N'%__AUDIT_TEST__'))
FROM @SyncCounts;
ROLLBACK TRANSACTION;
"@

$updateOutput = docker compose exec -T sqlserver `
    /opt/mssql-tools18/bin/sqlcmd `
    -S localhost -U sa -P $env:MSSQL_SA_PASSWORD -C `
    -d SapDataSync -Q $updateQuery -h -1 -W -b
if ($LASTEXITCODE -ne 0) {
    throw "The rollback-only Update test failed."
}
$updateLine = ($updateOutput | Where-Object { $_ -match '^\d+\|\d+\|\d+\|\d+\|\d+\|\d+\|\d+$' } | Select-Object -First 1)
if ($updateLine -ne "$ExpectedRows|0|1|$($ExpectedRows - 1)|0|1|1") {
    throw "Unexpected Update test result: $($updateOutput -join ' ')"
}

$softDeleteQuery = @"
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
BEGIN TRANSACTION;
DECLARE @ImportLogId UNIQUEIDENTIFIER =
(
    SELECT TOP (1) Id FROM dbo.ImportLog
    WHERE Status = N'Completed' AND TotalRows = $ExpectedRows
    ORDER BY StartedAt DESC
);
DECLARE @StagingId BIGINT =
(
    SELECT MIN(StagingId) FROM dbo.SapDataStaging WHERE ImportLogId = @ImportLogId
);
DECLARE @BusinessKeyHash BINARY(32) =
(
    SELECT BusinessKeyHash FROM dbo.SapDataStaging WHERE StagingId = @StagingId
);
DECLARE @SapDataId BIGINT =
(
    SELECT Id FROM dbo.SapData WHERE BusinessKeyHash = @BusinessKeyHash
);
UPDATE dbo.ImportLog SET Status = N'Processing', SoftDeleteEnabled = 1 WHERE Id = @ImportLogId;
DELETE dbo.SapDataStaging WHERE StagingId = @StagingId;
DECLARE @SyncCounts TABLE
(
    TotalRows INT, InsertedRows INT, UpdatedRows INT, UnchangedRows INT, DeletedRows INT
);
INSERT INTO @SyncCounts EXEC dbo.SyncSapData @ImportLogId = @ImportLogId;
SELECT CONCAT(TotalRows, '|', InsertedRows, '|', UpdatedRows, '|', UnchangedRows, '|', DeletedRows, '|',
    (SELECT COUNT(*) FROM dbo.SapDataChangeLog WHERE ImportLogId = @ImportLogId AND ChangeType = N'Delete'), '|',
    (SELECT CONVERT(INT, IsDeleted) FROM dbo.SapData WHERE Id = @SapDataId))
FROM @SyncCounts;
ROLLBACK TRANSACTION;
"@

$softDeleteOutput = docker compose exec -T sqlserver `
    /opt/mssql-tools18/bin/sqlcmd `
    -S localhost -U sa -P $env:MSSQL_SA_PASSWORD -C `
    -d SapDataSync -Q $softDeleteQuery -h -1 -W -b
if ($LASTEXITCODE -ne 0) {
    throw "The rollback-only Soft Delete test failed."
}
$softDeleteLine = ($softDeleteOutput | Where-Object { $_ -match '^\d+\|\d+\|\d+\|\d+\|\d+\|\d+\|\d+$' } | Select-Object -First 1)
if ($softDeleteLine -ne "$($ExpectedRows - 1)|0|0|$($ExpectedRows - 1)|1|1|1") {
    throw "Unexpected Soft Delete test result: $($softDeleteOutput -join ' ')"
}

$insertQuery = @"
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
BEGIN TRANSACTION;
DECLARE @ImportLogId UNIQUEIDENTIFIER =
(
    SELECT TOP (1) Id FROM dbo.ImportLog
    WHERE Status = N'Completed' AND TotalRows = $ExpectedRows
    ORDER BY StartedAt DESC
);
DECLARE @StagingId BIGINT =
(
    SELECT MIN(StagingId) FROM dbo.SapDataStaging WHERE ImportLogId = @ImportLogId
);
UPDATE dbo.ImportLog SET Status = N'Processing', SoftDeleteEnabled = 1 WHERE Id = @ImportLogId;
UPDATE dbo.SapDataStaging
SET BusinessKeyHash = HASHBYTES('SHA2_256', CONVERT(NVARCHAR(36), NEWID()))
WHERE StagingId = @StagingId;
DECLARE @SyncCounts TABLE
(
    TotalRows INT, InsertedRows INT, UpdatedRows INT, UnchangedRows INT, DeletedRows INT
);
INSERT INTO @SyncCounts EXEC dbo.SyncSapData @ImportLogId = @ImportLogId;
SELECT CONCAT(TotalRows, '|', InsertedRows, '|', UpdatedRows, '|', UnchangedRows, '|', DeletedRows, '|',
    (SELECT COUNT(*) FROM dbo.SapDataChangeLog WHERE ImportLogId = @ImportLogId AND ChangeType = N'Insert'), '|',
    (SELECT COUNT(*) FROM dbo.SapDataChangeLog WHERE ImportLogId = @ImportLogId AND ChangeType = N'Delete'))
FROM @SyncCounts;
ROLLBACK TRANSACTION;
"@

$insertOutput = docker compose exec -T sqlserver `
    /opt/mssql-tools18/bin/sqlcmd `
    -S localhost -U sa -P $env:MSSQL_SA_PASSWORD -C `
    -d SapDataSync -Q $insertQuery -h -1 -W -b
if ($LASTEXITCODE -ne 0) {
    throw "The rollback-only Insert audit test failed."
}
$insertLine = ($insertOutput | Where-Object { $_ -match '^\d+\|\d+\|\d+\|\d+\|\d+\|\d+\|\d+$' } | Select-Object -First 1)
if ($insertLine -ne "$ExpectedRows|1|0|$($ExpectedRows - 1)|1|1|1") {
    throw "Unexpected Insert audit test result: $($insertOutput -join ' ')"
}

$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = "Continue"
$repeatOutput = docker compose run --rm --no-deps `
    -e ETL_RUN_ONCE=true `
    -e ETL_MIN_FILE_AGE_SECONDS=0 `
    etl-worker 2>&1
$repeatExitCode = $LASTEXITCODE
$ErrorActionPreference = $previousErrorActionPreference
if ($repeatExitCode -ne 0 -or ($repeatOutput -join "`n") -notmatch 'AlreadyCompleted') {
    throw "Re-importing the same file was not skipped as expected: $($repeatOutput -join ' ')"
}

$hashAfter = (Get-FileHash -LiteralPath $ExcelPath -Algorithm SHA256).Hash
if ($hashBefore -ne $hashAfter) {
    throw "The Excel source file changed during Stage 2 verification."
}

$archiveSnapshots = @(
    Get-ChildItem -LiteralPath ".\data\archive" -Filter "*_$hashAfter.xlsx" -File
)
if ($archiveSnapshots.Count -ne 1) {
    throw "Expected exactly one archive snapshot for SHA-256 $hashAfter; found $($archiveSnapshots.Count)."
}
$archiveHash = (Get-FileHash -LiteralPath $archiveSnapshots[0].FullName -Algorithm SHA256).Hash
if ($archiveHash -ne $hashAfter) {
    throw "The archive snapshot hash does not match the Excel source hash."
}

Write-Host "Stage 2 verification passed."
Write-Host "Excel SHA-256: $hashAfter"
Write-Host "Archive snapshot: $($archiveSnapshots[0].Name)"
Write-Host "Staging rows: $($counts[0])"
Write-Host "SapData rows: $($counts[1])"
Write-Host "Source columns: $($counts[2])"
Write-Host "Staging/Main hash mismatches: $($counts[4])"
Write-Host "Changed-row behavior: Update audit includes changed field, transaction rolled back"
Write-Host "Insert behavior: Insert audit includes SapData record, transaction rolled back"
Write-Host "Delete behavior: missing row is soft-deleted with audit, transaction rolled back"
Write-Host "Duplicate file behavior: skipped"
