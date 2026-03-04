param(
    [string]$ValheimDir = "C:\Program Files (x86)\Steam\steamapps\common\Valheim"
)

$ErrorActionPreference = "Stop"

$pluginRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir = Join-Path $pluginRoot "src"
$project = Join-Path $srcDir "PlaytimeTimers.csproj"

if (-not (Test-Path $project)) {
    throw "Project file not found: $project"
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
$dllTarget = Join-Path $pluginRoot "PlaytimeTimers.dll"

if (-not (Test-Path $dllSource)) {
    throw "Build completed but DLL not found at: $dllSource"
}

Copy-Item $dllSource $dllTarget -Force
Write-Host "Built and copied: $dllTarget"
