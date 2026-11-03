# Builds everything for a GitHub release into .\dist\release:
#   RiffleSetup-<version>.exe              the installer, with Riffle inside
#   Riffle-<version>-win-x64.zip           the program alone, which the auto updater downloads
#   Riffle-<version>-win-x64.zip.sha256    its checksum, which the updater verifies
#   Riffle-<version>-source.zip            the source code without build output
#
# Needs the .NET 8 SDK. The version comes from src\Riffle\Riffle.csproj.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# Zip entries always use forward slashes, whatever the .NET version behind PowerShell does by default.
function New-Zip([string]$Folder, [string]$Destination, [string]$Prefix = '') {
    $archive = [IO.Compression.ZipFile]::Open($Destination, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $Folder -Recurse -File) {
            $name = $Prefix + $file.FullName.Substring($Folder.Length + 1).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally {
        $archive.Dispose()
    }
}

[xml]$project = Get-Content 'src\Riffle\Riffle.csproj'
$version = @($project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
Write-Host "Building Riffle $version"

# 1. The program
$dist = Join-Path $PSScriptRoot 'dist\release'
$app = Join-Path $dist 'app'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
dotnet publish 'src\Riffle\Riffle.csproj' -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false -o $app --nologo
if ($LASTEXITCODE) { throw 'Publishing Riffle failed' }
Get-ChildItem $app -Recurse -Include *.pdb, *.xml | Remove-Item
Copy-Item 'LICENSE', 'THIRD-PARTY-NOTICES.md' $app

# 2. The file list. Updates and uninstalling only ever touch the files named here.
$files = Get-ChildItem $app -Recurse -File | ForEach-Object { $_.FullName.Substring($app.Length + 1) } | Sort-Object
[IO.File]::WriteAllLines((Join-Path $app 'riffle-files.txt'), [string[]]$files, (New-Object Text.UTF8Encoding $false))

# 3. Zip and checksum
$zipName = "Riffle-$version-win-x64.zip"
$zip = Join-Path $dist $zipName
New-Zip $app $zip
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$sha  $zipName`n")

# 4. Setup
$setupOut = Join-Path $dist 'setup'
dotnet build 'src\Riffle.Setup\Riffle.Setup.csproj' -c Release "-p:Version=$version" "-p:PayloadZip=$zip" -o $setupOut --nologo
if ($LASTEXITCODE) { throw 'Building the setup failed' }
Move-Item (Join-Path $setupOut 'RiffleSetup.exe') (Join-Path $dist "RiffleSetup-$version.exe")
Remove-Item $setupOut, $app -Recurse -Force

# 5. Source code, without build output
$source = Join-Path $dist "Riffle-$version-source"
New-Item -ItemType Directory $source | Out-Null
Copy-Item '.gitignore', '.gitattributes', 'LICENSE', 'README.md', 'THIRD-PARTY-NOTICES.md', 'Riffle.sln', 'build-release.ps1' $source
foreach ($dir in 'src', 'docs') {
    robocopy $dir (Join-Path $source $dir) /E /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying $dir failed" }
}
$global:LASTEXITCODE = 0
New-Zip $source (Join-Path $dist "Riffle-$version-source.zip") "Riffle-$version-source/"
Remove-Item $source -Recurse -Force

Write-Host ''
Write-Host "Done. Release files are in $dist"
Get-ChildItem $dist | Format-Table Name, @{ Name = 'MB'; Expression = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
