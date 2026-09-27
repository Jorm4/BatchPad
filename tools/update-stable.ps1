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
$apps = @()
try {
    # Lets the BatchPad that started this record the run as finished before it closes.
    Start-Sleep -Seconds 3
    # Command-line runs (started by batchpad.com) are left to finish; only the app windows are closed.
    $apps = @(Get-Process BatchPad -ErrorAction SilentlyContinue | Where-Object {
        $parentId = (Get-CimInstance Win32_Process -Filter "ProcessId=$($_.Id)").ParentProcessId
        $parent = Get-Process -Id $parentId -ErrorAction SilentlyContinue
        $_.Path -eq $exe -and -not ($parent -and $parent.Path -like '*.com')
    })
    foreach ($app in $apps) {
        try {
            # Exits as the tray's Exit does, even while hidden to the tray; older builds only answer a window close.
            [System.Threading.EventWaitHandle]::OpenExisting("BatchPad-Exit-$($app.Id)").Set() | Out-Null
        }
        catch {
            $app.CloseMainWindow() | Out-Null
        }
    }
    # Leaves time to answer "runs are still running" before forcing it.
    $apps | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
    $apps | Where-Object { -not $_.HasExited } | Stop-Process -Force
    $apps | Wait-Process -ErrorAction SilentlyContinue

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
}
finally {
    # Started in its own folder, BatchPad opens the most recent workspace rather than the one around this script.
    if ($apps) { Start-Process $exe -WorkingDirectory $stable }
    Stop-Transcript | Out-Null
}
