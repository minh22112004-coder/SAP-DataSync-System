param(
    [string]$BaseUri = "http://localhost:8080",
    [string]$ExcelPath = ".\data\source\export.xlsx"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

if (-not (Test-Path -LiteralPath $ExcelPath -PathType Leaf)) {
    throw "Excel test file not found: $ExcelPath"
}

function Send-ExcelUpload([string]$Path) {
    $client = [System.Net.Http.HttpClient]::new()
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
        $client.Dispose()
    }
}

$sourceHash = (Get-FileHash -LiteralPath $ExcelPath -Algorithm SHA256).Hash
$first = Send-ExcelUpload $ExcelPath
if ($first.sha256 -ne $sourceHash) {
    throw "Uploaded SHA-256 does not match the source file."
}

$storedPath = Join-Path ".\data\uploads" $first.storedFileName
if (-not (Test-Path -LiteralPath $storedPath -PathType Leaf)) {
    throw "Uploaded file was not persisted in data/uploads."
}
if ((Get-FileHash -LiteralPath $storedPath -Algorithm SHA256).Hash -ne $sourceHash) {
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
