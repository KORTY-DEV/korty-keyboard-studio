# Read-only HID descriptor diagnostics. Sends NOTHING to any device.
#   .\Diag-ReadOnly.ps1 scan
#   .\Diag-ReadOnly.ps1 snapshot "A only cable" .\snapA.json
#   .\Diag-ReadOnly.ps1 diff .\snapA.json .\snapB.json
#   .\Diag-ReadOnly.ps1 report .\report.json
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
dotnet build '.\src\Korty.Diag\Korty.Diag.csproj' -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
dotnet '.\src\Korty.Diag\bin\Release\net8.0-windows\korty-diag.dll' @args
