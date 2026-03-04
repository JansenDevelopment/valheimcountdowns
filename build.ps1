param(
    [string]$ValheimDir = "C:\Program Files (x86)\Steam\steamapps\common\Valheim"
)

$ErrorActionPreference = "Stop"

$pluginRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir = Join-Path $pluginRoot "src"
$project = Join-Path $srcDir "PlaytimeTimers.csproj"
$infoFile = Join-Path $srcDir "info"

if (-not (Test-Path $project)) {
    throw "Project file not found: $project"
}

$buildDirectory = $pluginRoot
if (Test-Path $infoFile) {
    $infoContent = Get-Content $infoFile -Raw
    $buildMatch = [System.Text.RegularExpressions.Regex]::Match($infoContent, '(?im)^BuildDirectory=(.*)$')
    if ($buildMatch.Success) {
        $buildFromInfo = $buildMatch.Groups[1].Value.Trim()
        if (-not [string]::IsNullOrWhiteSpace($buildFromInfo)) {
            $buildDirectory = $buildFromInfo
        }
    }
}

$managedDir = Join-Path $ValheimDir "valheim_Data\Managed"
if (-not (Test-Path $managedDir)) {
    throw "Could not find Valheim managed assemblies at: $managedDir"
}

Push-Location $srcDir
try {
    dotnet build .\PlaytimeTimers.csproj -c Release /p:ValheimDir="$ValheimDir"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

$dllSource = Join-Path $srcDir "bin\Release\net472\PlaytimeTimers.dll"
$dllTarget = Join-Path $buildDirectory "PlaytimeTimers.dll"

if (-not (Test-Path $dllSource)) {
    throw "Build completed but DLL not found at: $dllSource"
}

if (-not (Test-Path $buildDirectory)) {
    New-Item -ItemType Directory -Path $buildDirectory -Force | Out-Null
}

Copy-Item $dllSource $dllTarget -Force
Write-Host "Built and copied: $dllTarget"
