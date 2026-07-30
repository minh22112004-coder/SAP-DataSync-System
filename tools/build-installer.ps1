[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publishScript = Join-Path $PSScriptRoot 'publish-launcher.ps1'
$installerScript = Join-Path $projectRoot 'installer\SapDataSync.iss'

& $publishScript -Runtime win-x64
if ($LASTEXITCODE -ne 0) {
    throw "Publish Launcher thất bại với exit code $LASTEXITCODE."
}

$isccCommand = Get-Command iscc.exe -ErrorAction SilentlyContinue
$isccCandidates = @(
    if ($isccCommand) { $isccCommand.Source }
    (Join-Path $projectRoot '.tools\InnoSetup\ISCC.exe')
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    'C:\ProgramData\chocolatey\bin\iscc.exe'
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

$iscc = $isccCandidates | Select-Object -First 1
if (-not $iscc) {
    throw 'Không tìm thấy Inno Setup 6. Cài bằng: choco install innosetup -y'
}

& $iscc "/DAppVersion=$Version" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Build Setup.exe thất bại với exit code $LASTEXITCODE."
}

$setupFile = Join-Path $projectRoot "artifacts\installer\SapDataSync-Setup-$Version.exe"
if (-not (Test-Path -LiteralPath $setupFile)) {
    throw "Không tìm thấy Setup.exe sau khi build: $setupFile"
}

Write-Host "Bộ cài đặt đã sẵn sàng: $setupFile"
