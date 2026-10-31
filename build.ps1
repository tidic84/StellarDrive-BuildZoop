param(
    [string]$GamePath = "C:\Program Files (x86)\Steam\steamapps\common\StellarDrive Demo"
)
$ErrorActionPreference = "Stop"
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetPath = if ($dotnetCommand) { $dotnetCommand.Source } else { Join-Path $env:USERPROFILE '.dotnet/dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnetPath)) { throw 'The .NET SDK is required.' }

& $dotnetPath build "$PSScriptRoot/src/BuildZoop.csproj" -c Release -t:Rebuild '-p:DefineConstants=' "-p:GamePath=$GamePath" -nologo -v:q
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$distPath = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force $distPath | Out-Null
$archivePath = Join-Path $distPath 'StellarDrive-BuildZoop.zip'
$archiveStream = [System.IO.File]::Open($archivePath, [System.IO.FileMode]::Create)
$archive = [System.IO.Compression.ZipArchive]::new($archiveStream, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $files = @(
        @{ Source = "$PSScriptRoot/src/bin/Release/BuildZoop.dll"; Entry = 'Mods/BuildZoop.dll' },
        @{ Source = "$PSScriptRoot/Mods/mod.json"; Entry = 'mod.json' },
        @{ Source = "$PSScriptRoot/Mods/Thumbnail.png"; Entry = 'Thumbnail.png' },
        @{ Source = "$PSScriptRoot/LICENSE"; Entry = 'LICENSE' }
    )
    foreach ($image in Get-ChildItem -LiteralPath "$PSScriptRoot/Mods/images" -File | Sort-Object Name) {
        $files += @{ Source = $image.FullName; Entry = "images/$($image.Name)" }
    }
    foreach ($file in $files) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.Source, $file.Entry) | Out-Null
    }
} finally {
    $archive.Dispose()
    $archiveStream.Dispose()
}
Write-Host "Package: $archivePath"
