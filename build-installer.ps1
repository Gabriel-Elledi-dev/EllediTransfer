<#
  Builds the Elledi Transfer installer.

    .\build-installer.ps1                 version taken from EllediTransfer.Gui.csproj
    .\build-installer.ps1 -Version 1.1.0  overrides it

  Steps: publish the graphical app and the command-line tool as self-contained win-x64
  (they run without .NET installed) into publish\app, then compile installer\EllediTransfer.iss
  with Inno Setup. The result is dist\EllediTransfer-Setup-<version>.exe.
#>
param(
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not $Version) {
    $Version = ([xml](Get-Content "$root\EllediTransfer.Gui\EllediTransfer.Gui.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw 'Inno Setup 6 non trovato. Installalo da https://jrsoftware.org/isdl.php e riprova.'
}

$publish = Join-Path $root 'publish\app'
if (Test-Path $publish) {
    Remove-Item $publish -Recurse -Force
}

foreach ($project in 'EllediTransfer.Gui\EllediTransfer.Gui.csproj', 'EllediTransfer\EllediTransfer.csproj') {
    Write-Host "Pubblicazione di $project ..."
    dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained true `
        -p:Version=$Version -p:DebugType=none -o $publish
    if ($LASTEXITCODE -ne 0) {
        throw "Pubblicazione di $project non riuscita."
    }
}

Write-Host "Creazione dell'installer $Version ..."
& $iscc "/DAppVersion=$Version" (Join-Path $root 'installer\EllediTransfer.iss')
if ($LASTEXITCODE -ne 0) {
    throw 'Inno Setup non è riuscito a creare l''installer.'
}

$setup = Join-Path $root "dist\EllediTransfer-Setup-$Version.exe"
Write-Host "Installer pronto: $setup"
