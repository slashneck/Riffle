# Builds a standalone Drift.exe (no .NET install needed) into dist\Drift
param([string]$Runtime = "win-x64")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "dist\Drift"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish "$root\src\Drift\Drift.csproj" -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# nothing but the app itself goes out
Get-ChildItem $out -Recurse -Include *.pdb, *.xml | Remove-Item -Force

$exe = Join-Path $out "Drift.exe"
Write-Host ("Built {0} ({1:N1} MB)" -f $exe, ((Get-Item $exe).Length / 1MB))
