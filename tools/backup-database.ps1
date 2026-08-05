[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$environmentFile = Join-Path $projectRoot '.env'

if (-not (Test-Path -LiteralPath $environmentFile)) {
    throw 'Khong tim thay file .env. Hay cau hinh ket noi SQL Server truoc.'
}

$settings = @{}
foreach ($rawLine in Get-Content -LiteralPath $environmentFile) {
    $line = $rawLine.Trim()
    if (-not $line -or $line.StartsWith('#')) { continue }
    $separator = $line.IndexOf('=')
    if ($separator -le 0) { continue }
    $settings[$line.Substring(0, $separator).Trim()] = $line.Substring($separator + 1).Trim()
}

# Explicit process environment values override .env for validation/automation.
foreach ($name in @(
    'SQL_HOST', 'SQL_PORT', 'SQL_DATABASE', 'SQL_USER', 'SQL_PASSWORD',
    'SQL_ENCRYPT', 'SQL_TRUST_SERVER_CERTIFICATE')) {
    $environmentValue = [Environment]::GetEnvironmentVariable($name)
    if (-not [string]::IsNullOrWhiteSpace($environmentValue)) {
        $settings[$name] = $environmentValue
    }
}

foreach ($required in @('SQL_HOST', 'SQL_DATABASE', 'SQL_USER', 'SQL_PASSWORD')) {
    if (-not $settings.ContainsKey($required) -or [string]::IsNullOrWhiteSpace($settings[$required])) {
        throw "Thieu cau hinh $required trong .env."
    }
}

$sqlHost = $settings['SQL_HOST']
if ($sqlHost -ieq 'host.docker.internal') { $sqlHost = '127.0.0.1' }
$sqlPort = if ($settings['SQL_PORT'] -as [int]) { [int]$settings['SQL_PORT'] } else { 1433 }
$database = $settings['SQL_DATABASE']
if ($database -notmatch '^[A-Za-z0-9_]+$') {
    throw 'SQL_DATABASE chi duoc chua chu cai, chu so va dau gach duoi.'
}

$builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
$builder['Data Source'] = "${sqlHost},$sqlPort"
$builder['Initial Catalog'] = 'master'
$builder['User ID'] = $settings['SQL_USER']
$builder['Password'] = $settings['SQL_PASSWORD']
$builder['Integrated Security'] = $false
$builder['Encrypt'] = if ($settings.ContainsKey('SQL_ENCRYPT')) { [bool]::Parse($settings['SQL_ENCRYPT']) } else { $true }
$builder['TrustServerCertificate'] = if ($settings.ContainsKey('SQL_TRUST_SERVER_CERTIFICATE')) { [bool]::Parse($settings['SQL_TRUST_SERVER_CERTIFICATE']) } else { $true }
$builder['Connect Timeout'] = 10
$builder['Persist Security Info'] = $false
$builder['Application Name'] = 'SAP DataSync Backup'

$connection = [System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
try {
    $connection.Open()

    $pathCommand = $connection.CreateCommand()
    $pathCommand.CommandText = "SELECT CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultBackupPath'));"
    $defaultBackupPath = [string]$pathCommand.ExecuteScalar()
    if ([string]::IsNullOrWhiteSpace($defaultBackupPath)) {
        throw 'SQL Server khong tra ve thu muc backup mac dinh.'
    }

    $timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $backupFileName = "${database}_${timestamp}.bak"
    $backupFile = if ($defaultBackupPath.Contains('/') -and -not $defaultBackupPath.Contains('\')) {
        "$($defaultBackupPath.TrimEnd('/'))/$backupFileName"
    }
    else {
        [System.IO.Path]::Combine($defaultBackupPath, $backupFileName)
    }

    $backupCommand = $connection.CreateCommand()
    $backupCommand.CommandTimeout = 0
    $backupCommand.CommandText = @"
DECLARE @BackupPath nvarchar(4000) = @Path;
BACKUP DATABASE [$database]
    TO DISK = @BackupPath
    WITH COPY_ONLY, CHECKSUM, INIT, STATS = 10;
RESTORE VERIFYONLY
    FROM DISK = @BackupPath
    WITH CHECKSUM;
"@
    [void]$backupCommand.Parameters.Add('@Path', [System.Data.SqlDbType]::NVarChar, 4000)
    $backupCommand.Parameters['@Path'].Value = $backupFile
    [void]$backupCommand.ExecuteNonQuery()

    Write-Host 'Backup va RESTORE VERIFYONLY thanh cong.' -ForegroundColor Green
    Write-Host "SQL Server backup file: $backupFile"

    if (Test-Path -LiteralPath $backupFile) {
        $file = Get-Item -LiteralPath $backupFile
        $hash = Get-FileHash -LiteralPath $backupFile -Algorithm SHA256
        Write-Host "Size: $($file.Length) bytes"
        Write-Host "SHA256: $($hash.Hash)"
    }
    else {
        Write-Warning 'Backup nam trong thu muc cua SQL Server service; tai khoan hien tai khong doc truc tiep duoc file de tinh SHA256.'
    }
}
finally {
    $connection.Dispose()
}
