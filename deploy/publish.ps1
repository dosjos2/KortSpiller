# Lager ferdige pakker i .\dist
#   .\deploy\publish.ps1                    -> Pi (linux-arm64) + mapper for Windows og Pi
#   .\deploy\publish.ps1 -PiRid linux-arm   (32-bit Raspberry Pi OS)
param([string]$PiRid = 'linux-arm64')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish "$root\src\KortSpiller.Player" -c Release -r $PiRid --self-contained -p:PublishSingleFile=true -p:DebugType=none -o "$dist\pi\player"
Copy-Item "$PSScriptRoot\install.sh", "$PSScriptRoot\kortspiller-player.service" "$dist\pi"
dotnet publish "$root\src\KortSpiller.Mapper" -c Release -r win-x64 --self-contained -p:DebugType=none -o "$dist\mapper-win-x64"
dotnet publish "$root\src\KortSpiller.Mapper" -c Release -r $PiRid --self-contained -p:DebugType=none -o "$dist\mapper-$PiRid"

Write-Host "`nFerdig. Kopier dist\pi til Pi-en:  scp -r dist\pi <bruker>@kortspiller.local:~/kortspiller-install"
