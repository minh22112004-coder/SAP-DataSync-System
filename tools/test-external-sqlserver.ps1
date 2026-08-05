[CmdletBinding()]
param(
    [string]$Server = $(
        if ([string]::IsNullOrWhiteSpace($env:SQL_HOST)) { '127.0.0.1' }
        else { $env:SQL_HOST }
    ),

    [ValidateRange(1, 65535)]
    [int]$Port = $(
        if ($env:SQL_PORT -as [int]) { [int]$env:SQL_PORT }
        else { 1433 }
    ),

    [string]$Database = $(
        if ([string]::IsNullOrWhiteSpace($env:SQL_DATABASE)) { 'SapDataSync' }
        else { $env:SQL_DATABASE }
    ),

    [string]$User = $(
        if ([string]::IsNullOrWhiteSpace($env:SQL_USER)) { 'sa' }
        else { $env:SQL_USER }
    ),

    [Security.SecureString]$Password,

    [ValidateRange(1, 60)]
    [int]$TimeoutSeconds = 5
)

$ErrorActionPreference = 'Stop'

function Test-TcpEndpoint {
    param(
        [Parameter(Mandatory)]
        [string]$ComputerName,

        [Parameter(Mandatory)]
        [int]$TcpPort,

        [Parameter(Mandatory)]
        [int]$Timeout
    )

    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $pendingConnection = $client.BeginConnect($ComputerName, $TcpPort, $null, $null)
        if (-not $pendingConnection.AsyncWaitHandle.WaitOne([TimeSpan]::FromSeconds($Timeout))) {
            throw "Timed out while connecting to TCP endpoint ${ComputerName}:$TcpPort."
        }

        $client.EndConnect($pendingConnection)
    }
    finally {
        $client.Dispose()
    }
}

if ($null -eq $Password) {
    if (-not [string]::IsNullOrWhiteSpace($env:SQL_PASSWORD)) {
        $Password = ConvertTo-SecureString $env:SQL_PASSWORD -AsPlainText -Force
    }
    else {
        $Password = Read-Host "Enter the SQL password for login '$User'" -AsSecureString
    }
}

Write-Host "Checking TCP endpoint ${Server}:$Port..."
Test-TcpEndpoint -ComputerName $Server -TcpPort $Port -Timeout $TimeoutSeconds

$passwordPointer = [IntPtr]::Zero
$plainPassword = $null
$connection = $null

try {
    $passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
    $plainPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)

    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $builder['Data Source'] = "${Server},$Port"
    $builder['Initial Catalog'] = 'master'
    $builder['User ID'] = $User
    $builder['Password'] = $plainPassword
    $builder['Integrated Security'] = $false
    $builder['Encrypt'] = $true
    $builder['TrustServerCertificate'] = $true
    $builder['Connect Timeout'] = $TimeoutSeconds
    $builder['Persist Security Info'] = $false
    $builder['Application Name'] = 'SAP DataSync SQL Server Preflight'

    $connection = [System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
    $connection.Open()

    $command = $connection.CreateCommand()
    $command.CommandTimeout = $TimeoutSeconds
    $command.CommandText = @"
SET NOCOUNT ON;
SELECT
    CONVERT(int, SERVERPROPERTY('ProductMajorVersion')) AS ProductMajorVersion,
    CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')) AS ProductVersion,
    CONVERT(nvarchar(128), SERVERPROPERTY('Edition')) AS Edition,
    COALESCE(CONVERT(nvarchar(128), SERVERPROPERTY('InstanceName')), N'MSSQLSERVER') AS InstanceName,
    CONVERT(int, SERVERPROPERTY('IsIntegratedSecurityOnly')) AS WindowsAuthOnly,
    ORIGINAL_LOGIN() AS LoginName,
    ISNULL(IS_SRVROLEMEMBER(N'sysadmin'), 0) AS IsSysAdmin,
    ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, N'CREATE ANY DATABASE'), 0) AS CanCreateDatabase,
    CASE WHEN DB_ID(@DatabaseName) IS NULL THEN 0 ELSE 1 END AS DatabaseExists;
"@
    [void]$command.Parameters.Add('@DatabaseName', [System.Data.SqlDbType]::NVarChar, 128)
    $command.Parameters['@DatabaseName'].Value = $Database

    $reader = $command.ExecuteReader()
    if (-not $reader.Read()) {
        throw 'SQL Server did not return a preflight result.'
    }

    $result = [pscustomobject]@{
        Server = $Server
        Port = $Port
        ProductMajorVersion = $reader.GetInt32(0)
        ProductVersion = $reader.GetString(1)
        Edition = $reader.GetString(2)
        InstanceName = $reader.GetString(3)
        WindowsAuthOnly = $reader.GetInt32(4) -eq 1
        LoginName = $reader.GetString(5)
        IsSysAdmin = $reader.GetInt32(6) -eq 1
        CanCreateDatabase = $reader.GetInt32(7) -eq 1
        Database = $Database
        DatabaseExists = $reader.GetInt32(8) -eq 1
    }
    $reader.Close()

    if ($result.ProductMajorVersion -ne 16) {
        throw "SQL Server 2022 (major version 16) is required; the server returned major version $($result.ProductMajorVersion)."
    }
    if ($result.WindowsAuthOnly) {
        throw 'SQL Server is configured for Windows Authentication only; Mixed Mode is required.'
    }
    if (-not $result.LoginName.Equals($User, [StringComparison]::OrdinalIgnoreCase)) {
        throw "SQL login does not match the required account '$User'."
    }
    if (-not $result.DatabaseExists -and -not $result.CanCreateDatabase) {
        throw "Database '$Database' does not exist and login '$User' cannot create databases."
    }

    Write-Host 'SQL Server 2022 preflight passed.' -ForegroundColor Green
    $result
}
finally {
    if ($null -ne $connection) {
        $connection.Dispose()
    }
    $plainPassword = $null
    if ($passwordPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
    }
}
