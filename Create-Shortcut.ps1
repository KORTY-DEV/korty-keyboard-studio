# Creates "KORTY Keyboard Studio" shortcuts on the Desktop and in the Start Menu, using the icon embedded in the EXE.
#   .\Create-Shortcut.ps1                      (EXE next to this script)
#   .\Create-Shortcut.ps1 -Exe C:\Tools\KORTY.KeyboardStudio.exe
param([string]$Exe = (Join-Path $PSScriptRoot 'KORTY.KeyboardStudio.exe'))
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Exe)) { throw "EXE not found: $Exe" }
$Exe = (Resolve-Path -LiteralPath $Exe).Path
$shell = New-Object -ComObject WScript.Shell
$targets = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'KORTY Keyboard Studio.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'KORTY Keyboard Studio.lnk')
)
foreach ($t in $targets) {
    $l = $shell.CreateShortcut($t)
    $l.TargetPath = $Exe
    $l.WorkingDirectory = Split-Path $Exe
    $l.IconLocation = "$Exe,0"
    $l.Description = 'KORTY Keyboard Studio — AJAZZ AK820 Pro'
    $l.Save()
    "created: $t"
}
