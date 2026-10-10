# Runs the complete automated suite (unit, mock, rendering, source-audit). Touches no hardware.
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
dotnet test '.\KortyKeyboardStudio.sln' -c Release --nologo
exit $LASTEXITCODE
