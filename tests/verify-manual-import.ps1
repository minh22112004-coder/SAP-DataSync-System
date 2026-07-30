param(
    [string]$BaseUri = "http://localhost:8080",
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"

$before = Invoke-RestMethod -Uri "$BaseUri/api/imports/status"
if ($before.running) {
    Write-Host "An import is already running; waiting for it to finish."
}
else {
    $accepted = Invoke-RestMethod -Method Post -Uri "$BaseUri/api/imports/run"
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
