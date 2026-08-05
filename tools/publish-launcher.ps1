[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.1.1'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectFile = Join-Path $projectRoot 'src\Launcher\SapDataSync.Launcher.csproj'
$outputDirectory = Join-Path $projectRoot "artifacts\launcher\$Runtime"

dotnet publish $projectFile `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:Version=$Version `
    -p:AssemblyVersion="$Version.0" `
    -p:FileVersion="$Version.0" `
    --output $outputDirectory

if ($LASTEXITCODE -ne 0) {
    throw "Publish Launcher thất bại với exit code $LASTEXITCODE."
}

$executable = Join-Path $outputDirectory 'SapDataSync.Launcher.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    throw "Không tìm thấy file Launcher sau khi publish: $executable"
}

Write-Host "Launcher đã sẵn sàng: $executable"
