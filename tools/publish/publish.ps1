# Publikacja ZetPDF do dist\: jeden ZetPDF.exe (self-contained, single-file) + README.txt + LICENSES\.
# Uruchomienie z katalogu repozytorium:  pwsh tools\publish\publish.ps1
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Set-Location $root
if (Test-Path dist) { Remove-Item dist -Recurse -Force }
dotnet publish src\ZetPDF.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o dist
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
New-Item -ItemType Directory -Force dist\LICENSES | Out-Null
Copy-Item src\ZetPDF.App\Licenses\*.txt dist\LICENSES\
Copy-Item src\ZetPDF.Ocr\tessdata\LICENSE dist\LICENSES\tessdata-LICENSE.txt
Copy-Item (Join-Path $env:USERPROFILE '.nuget\packages\fluent.ribbon\11.0.2\license\License.txt') dist\LICENSES\Fluent.Ribbon-License.txt
Copy-Item tools\publish\zlib.txt dist\LICENSES\zlib.txt
Copy-Item tools\publish\THIRD-PARTY-NOTICES.txt dist\LICENSES\THIRD-PARTY-NOTICES.txt
# README w UTF-8 z BOM (Notatnik w starszych Windows)
$readme = Get-Content tools\publish\README.txt -Raw -Encoding utf8
[System.IO.File]::WriteAllText((Join-Path $root 'dist\README.txt'), $readme, [System.Text.UTF8Encoding]::new($true))
Get-ChildItem dist -Recurse | Select-Object FullName, Length
