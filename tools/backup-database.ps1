[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$backupDirectory = Join-Path $projectRoot 'backups'
$timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$backupFileName = "SapDataSync_$timestamp.bak"
$backupFile = Join-Path $backupDirectory $backupFileName
$environmentFile = Join-Path $projectRoot '.env'

New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null

if (-not (Test-Path -LiteralPath $environmentFile)) {
    throw "Không tìm thấy file cấu hình .env. Hãy khởi động hệ thống bằng Launcher trước."
}

$passwordLine = Get-Content -LiteralPath $environmentFile | Where-Object {
    $_ -like 'MSSQL_SA_PASSWORD=*'
} | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($passwordLine)) {
    throw "Không tìm thấy MSSQL_SA_PASSWORD trong .env."
}
$databasePassword = $passwordLine.Substring('MSSQL_SA_PASSWORD='.Length)

$query = "BACKUP DATABASE [SapDataSync] TO DISK = N'/var/opt/mssql/backup/$backupFileName' WITH COPY_ONLY, CHECKSUM, INIT, STATS = 10; RESTORE VERIFYONLY FROM DISK = N'/var/opt/mssql/backup/$backupFileName' WITH CHECKSUM;"

Push-Location $projectRoot
try {
    docker compose exec -T sqlserver `
        /opt/mssql-tools18/bin/sqlcmd `
        -S localhost -U sa -P $databasePassword -C -b -Q $query
    if ($LASTEXITCODE -ne 0) {
        throw "SQL Server backup thất bại với exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

if (-not (Test-Path -LiteralPath $backupFile)) {
    throw "SQL Server báo thành công nhưng không tìm thấy file backup: $backupFile"
}

$file = Get-Item -LiteralPath $backupFile
$hash = Get-FileHash -LiteralPath $backupFile -Algorithm SHA256
Write-Host "Backup đã được tạo và VERIFYONLY thành công."
Write-Host "File: $($file.FullName)"
Write-Host "Dung lượng: $($file.Length) byte"
Write-Host "SHA256: $($hash.Hash)"
