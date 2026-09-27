# Publishes BatchPad into dist\, then installs it into stable\ (the copy to pin) and restarts a stable BatchPad that was open.
param([switch]$Swap)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $repo 'dist'
$stable = Join-Path $repo 'stable'
$exe = Join-Path $stable 'BatchPad.exe'

if (-not $Swap) {
    & (Join-Path $PSScriptRoot 'publish.bat')
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    # Closing BatchPad breaks this run's output pipe, so a detached copy does the swap after this run ends.
    $shell = (Get-Process -Id $PID).Path
    Start-Process $shell -WindowStyle Hidden -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Swap'
    Write-Host "Published. stable\ updates in a few seconds; a stable BatchPad that is open restarts. Log: $stable\update.log"
    exit 0
}

New-Item -ItemType Directory -Force $stable | Out-Null
Start-Transcript -Path (Join-Path $stable 'update.log') | Out-Null
try {
    # Lets the BatchPad that started this record the run as finished before it closes.
    Start-Sleep -Seconds 3
    $running = @(Get-Process BatchPad -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
    $closing = @($running | Where-Object { $_.CloseMainWindow() })
    $closing | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    $running | Where-Object { -not $_.HasExited } | Stop-Process -Force
    $running | Wait-Process -ErrorAction SilentlyContinue

    foreach ($attempt in 1..10) {
        try {
            Copy-Item (Join-Path $dist 'BatchPad.exe'), (Join-Path $dist 'batchpad.com') $stable -Force
            break
        }
        catch {
            if ($attempt -eq 10) { throw }
            Start-Sleep -Seconds 1
        }
    }
    Write-Host "Installed $exe"
    if ($running) { Start-Process $exe }
}
finally {
    Stop-Transcript | Out-Null
}
