param([switch]$NoInstall)
# Builds src\Assembly-CSharp.csproj and installs it in the game (keeps the original as .original).
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$game = if ($env:CUPHEAD_DIR) { $env:CUPHEAD_DIR } else { "$env:USERPROFILE\Downloads\Cuphead\Cuphead" }
$managed = "$game\Cuphead_Data\Managed"
$dotnet = if ($env:DOTNET_EXE) { $env:DOTNET_EXE } else { "dotnet" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

& $dotnet build "$root\src\Assembly-CSharp.csproj" -c Release -nologo -v q -clp:ErrorsOnly "-p:GameManaged=$managed"
if ($LASTEXITCODE -ne 0) { throw "build falhou" }
$dll = "$root\src\bin\Release\net35\Assembly-CSharp.dll"
New-Item -ItemType Directory "$root\dist" -Force | Out-Null
Copy-Item $dll "$root\dist\Assembly-CSharp.dll" -Force
"build ok: $((Get-Item $dll).Length) bytes"

# Launcher (embeds dist\Assembly-CSharp.dll and installs it in the friend's game)
& $dotnet build "$root\launcher\Cuphead-Online.csproj" -c Release -nologo -v q -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw "build do launcher falhou" }
Copy-Item (Get-ChildItem "$root\launcher\bin\Release" -Recurse -Filter "Cuphead Online.exe" | Select-Object -First 1).FullName "$root\dist\Cuphead-Online.exe" -Force
"launcher ok: dist\Cuphead-Online.exe"
if ($NoInstall) { return }
if (Get-Process Cuphead -ErrorAction SilentlyContinue) { throw "feche o Cuphead antes de instalar" }
if (-not (Test-Path "$managed\Assembly-CSharp.dll.original")) { throw "falta Assembly-CSharp.dll.original (backup do jogo limpo)" }
Copy-Item $dll "$managed\Assembly-CSharp.dll" -Force
"instalado em $managed"
