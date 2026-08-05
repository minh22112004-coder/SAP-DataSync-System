param(
    [string]$BaseUri = "http://localhost:8080",
    [string]$ExcelPath = ".\data\source\export.xlsx",
    [string]$AdminPassword = "Stage5Validation!2026"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

if (-not (Test-Path -LiteralPath $ExcelPath -PathType Leaf)) {
    throw "Excel test file not found: $ExcelPath"
}

$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.UseCookies = $true
$client = [System.Net.Http.HttpClient]::new($handler)
$client.DefaultRequestHeaders.Add("X-SapDataSync-Admin", "1")

function Send-AdminAuth([string]$Endpoint) {
    $json = @{ password = $AdminPassword } | ConvertTo-Json
    $content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, "application/json")
    try {
        $response = $client.PostAsync("$BaseUri/api/admin/$Endpoint", $content).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw "Admin authentication failed with HTTP $([int]$response.StatusCode): $body"
        }
    }
    finally {
        $content.Dispose()
    }
}

$statusJson = $client.GetStringAsync("$BaseUri/api/admin/status").GetAwaiter().GetResult() | ConvertFrom-Json
Send-AdminAuth $(if ($statusJson.setupRequired) { "setup" } else { "login" })

function Send-ExcelUpload([string]$Path) {
    $form = [System.Net.Http.MultipartFormDataContent]::new()
    $stream = [System.IO.File]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
    $fileContent = [System.Net.Http.StreamContent]::new($stream)
    $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse(
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
    $form.Add($fileContent, "file", [System.IO.Path]::GetFileName($Path))
    try {
        $response = $client.PostAsync("$BaseUri/api/uploads", $form).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw "Upload failed with HTTP $([int]$response.StatusCode): $body"
        }
        return $body | ConvertFrom-Json
    }
    finally {
        $form.Dispose()
        $stream.Dispose()
    }
}

$sourceHash = (Get-FileHash -LiteralPath $ExcelPath -Algorithm SHA256).Hash
$first = Send-ExcelUpload $ExcelPath
if ($first.sha256 -ne $sourceHash) {
    throw "Uploaded SHA-256 does not match the source file."
}

$containerPath = "/data/uploads/$($first.storedFileName)"
docker compose exec -T web-api test -f $containerPath
if ($LASTEXITCODE -ne 0) {
    throw "Uploaded file was not persisted in the uploads_data volume."
}
$storedHash = (docker compose exec -T web-api sha256sum $containerPath).Split(' ')[0].Trim().ToUpperInvariant()
if ($LASTEXITCODE -ne 0 -or $storedHash -ne $sourceHash) {
    throw "Persisted upload hash does not match the source file."
}

$second = Send-ExcelUpload $ExcelPath
if (-not $second.alreadyExisted -or $second.storedFileName -ne $first.storedFileName) {
    throw "Duplicate upload was not deduplicated by SHA-256."
}

Write-Host "Upload verification passed."
Write-Host "Stored file: $($first.storedFileName)"
Write-Host "SHA-256: $sourceHash"
Write-Host "Duplicate behavior: reused existing upload"

$client.Dispose()
$handler.Dispose()
