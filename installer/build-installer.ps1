# Buduje instalator ZetPDF z zawartości dist\ (najpierw: pwsh tools\publish\publish.ps1).
# Uruchomienie z katalogu repozytorium:  pwsh installer\build-installer.ps1
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
if (-not (Test-Path (Join-Path $root 'dist\ZetPDF.exe'))) { throw "Brak dist\ZetPDF.exe – uruchom najpierw tools\publish\publish.ps1" }
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Nie znaleziono Inno Setup 6 (ISCC.exe)" }
& $iscc (Join-Path $PSScriptRoot 'ZetPDF.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC zakończył się błędem $LASTEXITCODE" }
Get-ChildItem (Join-Path $PSScriptRoot 'Output') | Select-Object Name, Length
