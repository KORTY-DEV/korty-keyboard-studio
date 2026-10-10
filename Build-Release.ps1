# Builds the distributable: a self-contained single-file EXE (no .NET install needed) plus a ZIP.
#   .\Build-Release.ps1            run tests, then publish
#   .\Build-Release.ps1 -SkipTests
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$ver = ([xml](Get-Content .\Directory.Build.props)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $SkipTests) { dotnet test .\KortyKeyboardStudio.sln -c Release --nologo; if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' } }
$name = "KORTY-Keyboard-Studio-$ver-win-x64"
$out = Join-Path $PSScriptRoot "release\$name"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish .\src\Korty.App\Korty.App.csproj -c Release -r win-x64 --self-contained true -o $out --nologo `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
foreach ($f in 'README.md', 'README.en.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'THIRD_PARTY_LICENSE_kabelklaus.txt', 'CHANGELOG.md', 'CHANGELOG.en.md', 'Create-Shortcut.ps1') { Copy-Item $f $out }
$zip = Join-Path $PSScriptRoot "release\$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip
"EXE: $out\KORTY.KeyboardStudio.exe"
"ZIP: $zip"
