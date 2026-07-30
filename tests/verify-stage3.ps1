param(
    [string]$BaseUri = "http://localhost:8080",
    [int]$ExpectedMinimumRows = 1
)

$ErrorActionPreference = "Stop"

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

$health = Invoke-RestMethod -Uri "$BaseUri/api/health"
Assert-True ($health.status -eq "Healthy") "Web/API health check failed."

$webResponse = Invoke-WebRequest -UseBasicParsing -Uri "$BaseUri/"
Assert-True ($webResponse.StatusCode -eq 200) "Web App did not return HTTP 200."
Assert-True ($webResponse.Content -match "Locate Flow") "Web App content was not found."
Assert-True ($webResponse.Content -match "Soft delete") "Soft Delete import column was not found in the Web App."
Assert-True ($webResponse.Content -match "Upload &amp; Import") "Upload & Import control was not found in the Web App."
$stylesResponse = Invoke-WebRequest -UseBasicParsing -Uri "$BaseUri/styles.css"
Assert-True ($stylesResponse.Content -match '\[hidden\]\s*\{\s*display:\s*none\s*!important') "Hidden UI states can be displayed by component CSS."

$dataDuration = Measure-Command {
    $data = Invoke-RestMethod -Uri (
        "$BaseUri/api/sap-data?page=1&pageSize=25" +
        "&product=12&salesOrganization=SG50" +
        "&businessScenario=PDO,PWS,SDS,SWS"
    )
}
Assert-True ($data.totalItems -ge $ExpectedMinimumRows) "SAP data endpoint returned too few rows."
Assert-True ($data.items.Count -le 25) "SAP data endpoint did not honor PageSize."
Assert-True ($data.page -eq 1) "SAP data endpoint returned an unexpected page."
Assert-True ($dataDuration.TotalSeconds -lt 10) "SAP data query took 10 seconds or longer."

$first = $data.items | Select-Object -First 1
Assert-True ($null -ne $first) "SAP data endpoint returned no item for detail verification."
$detail = Invoke-RestMethod -Uri "$BaseUri/api/sap-data/$($first.id)"
$fieldCount = @($detail.fields.PSObject.Properties).Count
Assert-True ($fieldCount -eq 149) "SAP detail returned $fieldCount source fields; expected 149."
Assert-True ($detail.fields.PSObject.Properties.Name -contains "SI Created on") "SI Created on is missing from detail."
Assert-True ($detail.fields.PSObject.Properties.Name -contains "OIL Sales") "OIL Sales is missing from detail."

$noMatch = Invoke-RestMethod -Uri "$BaseUri/api/sap-data?page=1&pageSize=10&product=NOT-A-PRODUCT&salesOrganization=SG50"
Assert-True ($noMatch.totalItems -eq 0) "Product metadata filter was not applied."

$imports = Invoke-RestMethod -Uri "$BaseUri/api/import-logs?page=1&pageSize=10"
Assert-True ($imports.totalItems -ge 1) "Import history endpoint returned no rows."
$firstImport = $imports.items | Select-Object -First 1
$importDetail = Invoke-RestMethod -Uri "$BaseUri/api/import-logs/$($firstImport.id)"
Assert-True ($importDetail.id -eq $firstImport.id) "Import detail endpoint returned an unexpected row."
Assert-True ($null -ne $importDetail.deletedRows) "Import detail does not include DeletedRows."
$changes = Invoke-RestMethod -Uri "$BaseUri/api/import-logs/$($firstImport.id)/changes?page=1&pageSize=10"
Assert-True ($changes.page -eq 1) "Import change endpoint returned an unexpected page."
Assert-True ($changes.items.Count -le 10) "Import change endpoint did not honor PageSize."

$manualStatus = Invoke-RestMethod -Uri "$BaseUri/api/imports/status"
Assert-True ($null -ne $manualStatus.running) "Manual import status endpoint returned an invalid response."

$invalidStatus = $null
try {
    Invoke-WebRequest -UseBasicParsing -Uri "$BaseUri/api/sap-data?page=1&pageSize=500" | Out-Null
    $invalidStatus = 200
}
catch {
    $invalidStatus = [int]$_.Exception.Response.StatusCode
}
Assert-True ($invalidStatus -eq 400) "Invalid PageSize did not return HTTP 400."

Write-Host "Stage 3 verification passed."
Write-Host "SAP rows available: $($data.totalItems)"
Write-Host "List query duration: $([math]::Round($dataDuration.TotalMilliseconds)) ms"
Write-Host "Detail source fields: $fieldCount"
Write-Host "Import history rows: $($imports.totalItems)"
