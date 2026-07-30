param(
    [string]$ComposeFile = "compose.yaml"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($env:MSSQL_SA_PASSWORD)) {
    throw "Set MSSQL_SA_PASSWORD before running the Stage 1 verification."
}

Write-Host "[1/5] Building .NET solution..."
dotnet build ".\SapDataSync.sln" --configuration Release --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }

Write-Host "[2/5] Validating Docker Compose..."
docker compose -f $ComposeFile config --quiet
if ($LASTEXITCODE -ne 0) { throw "docker compose config failed." }

Write-Host "[3/5] Checking API health..."
$health = Invoke-RestMethod -Uri "http://localhost:8080/api/health"
if ($health.status -ne "Healthy" -or $health.database -ne "SapDataSync") {
    throw "API or database health check failed."
}

Write-Host "[4/5] Checking the Excel mount is read-only..."
$inspectResult = docker inspect sap-datasync-etl-worker-1 | ConvertFrom-Json
$sourceMount = $inspectResult[0].Mounts | Where-Object { $_.Destination -eq "/data/source" }
if ($null -eq $sourceMount) {
    throw "The /data/source mount was not found."
}
if ($sourceMount.RW -ne $false) {
    throw "The /data/source mount is not read-only."
}

Write-Host "[5/5] Checking the schema contains 149 source columns..."
$schema = Get-Content -Raw -Encoding UTF8 -LiteralPath ".\database\scripts\001_initial_schema.sql"
$sourceBlock = [regex]::Match(
    $schema,
    "DECLARE @SourceColumns NVARCHAR\(MAX\) = N'(?<body>[\s\S]*?)';").Groups["body"].Value
$sourceColumnCount = [regex]::Matches($sourceBlock, "\[[^\]]+\] NVARCHAR\(MAX\) NULL").Count
if ($sourceColumnCount -ne 149) {
    throw "Expected 149 source columns but found $sourceColumnCount."
}

Write-Host "Stage 1 verification passed: build, Compose, health, read-only mount and 149-column schema are valid."
