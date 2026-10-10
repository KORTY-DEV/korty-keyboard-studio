# Builds (if needed) and starts KORTY Keyboard Studio.
# No elevation, no downloads, no driver changes. The app only READS HID descriptors; writing to the keyboard is blocked.
#   .\Run-Studio.ps1              normal start (reads real device descriptors, writes blocked)
#   .\Run-Studio.ps1 -Simulate    start with the software keyboard (touches no hardware at all)
param([switch]$Simulate)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDK 8+ is required (dotnet not found).' }
if (Get-Process -Name 'KORTY.KeyboardStudio' -ErrorAction SilentlyContinue) { throw 'KORTY Keyboard Studio уже запущена: закройте её окно и повторите (иначе сборка не сможет обновить файлы).' }
dotnet build '.\src\Korty.App\Korty.App.csproj' -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$exe = Join-Path $PSScriptRoot 'src\Korty.App\bin\Release\net8.0-windows\KORTY.KeyboardStudio.exe'
if ($Simulate) { & $exe '--simulate' } else { & $exe }
