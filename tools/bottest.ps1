param(
    [string]$Level = "Slime",
    [int]$Seconds = 150,
    [string]$Tag = "bot",
    [switch]$P1God,
    [switch]$Trace
)
# Offline: loads slot 1 (menu), turns the P2 bot on, enters the level and reports the bot's life.
$ErrorActionPreference = "Stop"
$game = if ($env:CUPHEAD_DIR) { $env:CUPHEAD_DIR } else { "$env:USERPROFILE\Downloads\Cuphead\Cuphead" }
$out = Join-Path $PSScriptRoot "..\testlogs\$Tag"
New-Item -ItemType Directory $out -Force | Out-Null
Remove-Item "$out\*" -Force -ErrorAction SilentlyContinue
$args2 = @("-screen-fullscreen", "0", "-screen-width", "1280", "-screen-height", "720", "-netcode-autoload", "0", "-netcode-testlevel", $Level, "-helperbot", "-onlinelog", "`"$out\bot.log`"")
if ($P1God) { $args2 += "-netcode-godmode-p1" }
if ($Trace) { $args2 += "-helperbot-trace" }
$p = Start-Process "$game\Cuphead.exe" -ArgumentList $args2 -WorkingDirectory $game -PassThru
Start-Sleep -Seconds $Seconds
if (-not $p.HasExited) { $p.Kill() }
Start-Sleep -Seconds 1
$lines = @(Select-String "$out\bot.log" -Pattern "\[Bot\]" | ForEach-Object { $_.Line.Substring(13) })
$hits = @($lines | Where-Object { $_ -match "vida \d+ -> \d+" -and $_ -notmatch "vida -1" })
"hits recebidos: $($hits.Count)"
$hits
$lines | Where-Object { $_ -notmatch "vida" } | Select-Object -Last 3
