param([string]$BaseUri = "http://localhost:8080")

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $projectRoot ".env"

function Get-LocalSetting {
    param([string]$Name)
    if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) { return $null }
    $line = Get-Content -LiteralPath $envFile |
        Where-Object { $_ -match "^$([regex]::Escape($Name))=" } |
        Select-Object -First 1
    if (-not $line) { return $null }
    return $line.Substring($line.IndexOf('=') + 1).Trim()
}

function Wait-Healthy {
    $deadline = (Get-Date).AddSeconds(90)
    do {
        try {
            $health = Invoke-RestMethod -Uri "$BaseUri/api/health"
            if ($health.status -eq "Healthy") { return }
        }
        catch { Start-Sleep -Seconds 2 }
    } while ((Get-Date) -lt $deadline)
    throw "Web API did not become healthy."
}

function Invoke-JsonRequest {
    param([string]$Path, [hashtable]$Body)
    $json = $Body | ConvertTo-Json -Depth 8
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    return Invoke-RestMethod -Uri "$BaseUri$Path" -Method Post `
        -ContentType "application/json; charset=utf-8" -Body $bytes
}

function Get-SourceHashes {
    $result = @{}
    Get-ChildItem -LiteralPath (Join-Path $projectRoot "data/source") -Filter "*.xlsx" -File |
        ForEach-Object { $result[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    return $result
}

function Get-DatabaseFingerprint {
    $password = if (-not [string]::IsNullOrWhiteSpace($env:MSSQL_SA_PASSWORD)) {
        $env:MSSQL_SA_PASSWORD
    } else {
        Get-LocalSetting "MSSQL_SA_PASSWORD"
    }
    if ([string]::IsNullOrWhiteSpace($password)) {
        throw "MSSQL_SA_PASSWORD is required to verify database immutability."
    }

    $query = @"
SET NOCOUNT ON;
SELECT CONCAT(
  (SELECT COUNT_BIG(*) FROM dbo.SapDataStaging), '|',
  COALESCE((SELECT CHECKSUM_AGG(CHECKSUM(RowHash)) FROM dbo.SapDataStaging), 0), '|',
  (SELECT COUNT_BIG(*) FROM dbo.SapData), '|',
  COALESCE((SELECT CHECKSUM_AGG(CHECKSUM(RowHash)) FROM dbo.SapData), 0), '|',
  (SELECT COUNT_BIG(*) FROM dbo.ImportLog), '|',
  (SELECT COUNT_BIG(*) FROM dbo.SapDataChangeLog));
"@
    $output = docker compose --project-directory $projectRoot exec -T sqlserver `
        /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $password -C `
        -d SapDataSync -Q $query -h -1 -W
    if ($LASTEXITCODE -ne 0) { throw "Could not fingerprint the database." }
    $line = $output | Where-Object { $_ -match '^\d+\|-?\d+\|\d+\|-?\d+\|\d+\|\d+$' } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($line)) { throw "Unexpected database fingerprint output." }
    return $line.Trim()
}

$aiEnabled = Get-LocalSetting "AI_ENABLED"
$aiKey = Get-LocalSetting "AI_API_KEY"
if ($aiEnabled -notmatch '^(?i:true|1|yes)$' -or [string]::IsNullOrWhiteSpace($aiKey)) {
    throw "Set AI_ENABLED=true and a non-empty AI_API_KEY in the local .env file."
}
$aiKey = $null

docker compose --project-directory $projectRoot up -d --build --force-recreate web-api
if ($LASTEXITCODE -ne 0) { throw "Could not recreate web-api with the local AI configuration." }
Wait-Healthy

$sourceHashesBefore = Get-SourceHashes
$databaseBefore = Get-DatabaseFingerprint

$status = Invoke-RestMethod -Uri "$BaseUri/api/ai/status"
if (-not $status.enabled) { throw "AI status is disabled after loading the local .env file." }

$sample = Invoke-RestMethod -Uri "$BaseUri/api/sap-data?page=1&pageSize=10&product=12&salesOrganization=SG50"
$sampleItem = $sample.items | Select-Object -First 1
if (-not $sampleItem -or [string]::IsNullOrWhiteSpace($sampleItem.shippingInstructionsId)) {
    throw "Could not find one Shipping Instruction for the minimal live AI test."
}

$plan = Invoke-JsonRequest -Path "/api/ai/plans" -Body @{
    goal = "Tạo một kế hoạch kiểm tra ngắn, chỉ dựa trên bản ghi được cung cấp."
    query = @{
        page = 1
        pageSize = 10
        product = "12"
        salesOrganization = "SG50"
        siId = $sampleItem.shippingInstructionsId
    }
}
if ([string]::IsNullOrWhiteSpace($plan.plan.title) -or $plan.analyzedRecords -lt 1) {
    throw "The live AI provider did not return a valid plan."
}

$filter = Invoke-JsonRequest -Path "/api/ai/filters" -Body @{
    question = "Lọc Product có mã chính xác là 12, Sales Organization có mã chính xác là SG50 và sắp xếp Created Date mới nhất."
}
if (-not $filter.requiresConfirmation -or
    $filter.query.product -ne "12" -or
    $filter.query.salesOrganization -ne "SG50" -or
    $filter.query.sortDirection -ne "desc") {
    throw "The live AI provider did not return the expected safe filter draft."
}

$databaseAfter = Get-DatabaseFingerprint
if ($databaseBefore -ne $databaseAfter) {
    throw "Live AI verification changed database state."
}
$sourceHashesAfter = Get-SourceHashes
if ($sourceHashesBefore.Count -ne $sourceHashesAfter.Count) {
    throw "Live AI verification changed the source file set."
}
foreach ($path in $sourceHashesBefore.Keys) {
    if ($sourceHashesAfter[$path] -ne $sourceHashesBefore[$path]) {
        throw "Live AI verification changed source file: $path"
    }
}

Write-Host "Stage 4 live Groq verification passed."
Write-Host "Provider: $($plan.provider); model: $($plan.model); analyzed records: $($plan.analyzedRecords)."
Write-Host "Plan JSON and confirmed filter draft are valid."
Write-Host "Database and source Excel files remained unchanged."
