param(
    [string]$Level = "Frogs",
    [int]$Seconds = 150,
    [string]$Tag = "run",
    [string]$HostRes = "640x360",
    [string]$JoinRes = "640x360",
    [switch]$Trace,
    [switch]$DeepTrace,
    [switch]$GodMode,
    [switch]$DamageTrace,
    [string]$HoldKeyOnFriend = "",
    [string]$FriendExtra = "",
    [int]$LagMs = 0,
    [int]$JitterMs = 0,
    [int]$KillFriendAfter = 0,
    [string]$FriendGameDir = ""
)
# Opens two local instances (host + friend over 127.0.0.1) with bot input,
# enters the level and compares the logs. Requires the mod installed in the game.
$ErrorActionPreference = "Stop"
$game = if ($env:CUPHEAD_DIR) { $env:CUPHEAD_DIR } else { "$env:USERPROFILE\Downloads\Cuphead\Cuphead" }
$out = Join-Path $PSScriptRoot "..\testlogs\$Tag"
New-Item -ItemType Directory $out -Force | Out-Null
Remove-Item "$out\*" -Force -ErrorAction SilentlyContinue

function Res([string]$r) { $w, $h = $r -split "x"; @("-screen-width", $w, "-screen-height", $h) }
$common = @("-screen-fullscreen", "0", "-netcode-port", "7778", "-netcode-autoload", "0", "-netcode-botinput")
if ($Trace) { $common += "-netcode-trace" }
if ($DeepTrace) { $common += "-netcode-deeptrace" }
if ($DamageTrace) { $common += "-netcode-dmgtrace" }
if ($GodMode) { $common += "-netcode-godmode" }
if ($LagMs -gt 0 -or $JitterMs -gt 0) { $common += @("-netcode-lag", "$LagMs", "-netcode-jitter", "$JitterMs") }
$friendGame = if ($FriendGameDir) { $FriendGameDir } else { $game }
$hostArgs = $common + (Res $HostRes) + @("-netcode-host", "-netcode-testlevel", $Level, "-onlinelog", "`"$out\host.log`"", "-logFile", "`"$out\host-unity.log`"")
$joinArgs = $common + (Res $JoinRes) + @("-netcode-join", "127.0.0.1", "-onlinelog", "`"$out\amigo.log`"", "-logFile", "`"$out\amigo-unity.log`"")
if ($FriendExtra) { $joinArgs += ($FriendExtra -split " ") }

$h = Start-Process "$game\Cuphead.exe" -ArgumentList $hostArgs -WorkingDirectory $game -PassThru
Start-Sleep -Seconds 3
$j = Start-Process "$friendGame\Cuphead.exe" -ArgumentList $joinArgs -WorkingDirectory $friendGame -PassThru
if ($HoldKeyOnFriend) {
    # Simulates the friend holding a key (default Z = jump) on their own PC: focus the
    # friend's window and keep the key down for the whole run.
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class KeyHold {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint type);
}
"@
    Start-Sleep -Seconds 8
    $j.Refresh()
    $vk = [byte][char]$HoldKeyOnFriend.ToUpper()
    $scan = [byte][KeyHold]::MapVirtualKey($vk, 0)
    $deadline = (Get-Date).AddSeconds($Seconds - 8)
    while ((Get-Date) -lt $deadline) {
        $j.Refresh()
        [void][KeyHold]::ShowWindow($j.MainWindowHandle, 5)
        [void][KeyHold]::SetForegroundWindow($j.MainWindowHandle)
        [KeyHold]::keybd_event($vk, $scan, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 500
    }
    [KeyHold]::keybd_event($vk, $scan, 2, [UIntPtr]::Zero)
} elseif ($KillFriendAfter -gt 0) {
    Start-Sleep -Seconds $KillFriendAfter
    Stop-Process -Id $j.Id -Force
    "amigo derrubado aos $KillFriendAfter s"
    Start-Sleep -Seconds ($Seconds - $KillFriendAfter)
    $h.Refresh()
    "host ainda rodando: $(-not $h.HasExited)  respondendo: $($h.Responding)"
} else {
    Start-Sleep -Seconds $Seconds
}
foreach ($p in @($h, $j)) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force } }
Start-Sleep -Seconds 1

$a = Get-Content "$out\amigo.log" -ErrorAction SilentlyContinue
$desyncs = @($a | Select-String "dessincronia no frame")
$snaps = @($a | Select-String "snap frame")
$lastFrame = 0
foreach ($s in $snaps) { if ($s -match "snap frame (\d+)") { $lastFrame = [Math]::Max($lastFrame, [int]$Matches[1]) } }
"frames: $lastFrame  dessincronias: $($desyncs.Count)  excecoes: $(@($a | Select-String '\[Exception\]').Count)"
$desyncs | Select-Object -First 3 | ForEach-Object { $_.Line }
"--- cenas"
$a | Select-String "carregando|barreira|lockstep" | Select-Object -First 20 | ForEach-Object { $_.Line }
