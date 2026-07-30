param(
    [string]$BaseUri = "http://localhost:8080",
    [int]$TimeoutSeconds = 120,
    [string]$AdminPassword = "Stage5Validation!2026"
)

$ErrorActionPreference = "Stop"
$adminSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$adminHeaders = @{ "X-SapDataSync-Admin" = "1" }
$adminStatus = Invoke-RestMethod -Uri "$BaseUri/api/admin/status"
$authEndpoint = if ($adminStatus.setupRequired) { "setup" } else { "login" }
$authBody = @{ password = $AdminPassword } | ConvertTo-Json
$null = Invoke-RestMethod -Method Post -Uri "$BaseUri/api/admin/$authEndpoint" `
    -Headers $adminHeaders -ContentType "application/json" -Body $authBody -WebSession $adminSession

$before = Invoke-RestMethod -Uri "$BaseUri/api/imports/status"
if ($before.running) {
    Write-Host "An import is already running; waiting for it to finish."
}
else {
    $accepted = Invoke-RestMethod -Method Post -Uri "$BaseUri/api/imports/run" `
        -Headers $adminHeaders -WebSession $adminSession
    if (-not $accepted.running -or $accepted.trigger -ne "manual") {
        throw "The manual import request was not accepted."
    }
    Write-Host "Manual import request accepted."
}

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
do {
    Start-Sleep -Seconds 2
    $status = Invoke-RestMethod -Uri "$BaseUri/api/imports/status"
} while ($status.running -and (Get-Date) -lt $deadline)

if ($status.running) {
    throw "Manual import did not finish within $TimeoutSeconds seconds."
}
if ($status.exitCode -ne 0) {
    throw "Manual import finished with exit code $($status.exitCode): $($status.message)"
}

$health = Invoke-RestMethod -Uri "$BaseUri/api/health"
if ($health.status -ne "Healthy") {
    throw "Web/API health check failed after manual import."
}

Write-Host "Manual import verification passed."
Write-Host "Started: $($status.startedAt)"
Write-Host "Completed: $($status.completedAt)"
Write-Host "Result: $($status.message)"
