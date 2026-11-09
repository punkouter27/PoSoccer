# Watches a train-roster.ps1 queue for a fixed window (2026-11-08): every hour it records a
# progress snapshot and re-renders the HTML report; at the end of the window it renders the
# final report. It only reads; the queue keeps training after the window closes.
#
#   pwsh -ExecutionPolicy Bypass -File scripts\watch-session.ps1 -Hours 8
#
# Output: docs/<queue day>/session-status.json, roster-report.html, and lines in train-roster.log.
param(
    [double]$Hours = 8,
    [int]$EveryMinutes = 60,
    [string]$Day = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if (-not $Day) {
    $Day = (Get-ChildItem (Join-Path $root "docs") -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName "train-roster.json") } |
        Sort-Object Name | Select-Object -Last 1).Name
}
$docDir = Join-Path $root "docs\$Day"
$statePath = Join-Path $docDir "train-roster.json"
$statusPath = Join-Path $docDir "session-status.json"
$logPath = Join-Path $docDir "train-roster.log"
$reportPath = Join-Path $docDir "roster-report.html"
$python = Join-Path $root ".venv\Scripts\python.exe"
$deadline = (Get-Date).AddHours($Hours)

function Snapshot {
    $state = Get-Content $statePath -Raw | ConvertFrom-Json
    $total = 0L
    foreach ($name in $state.queue) {
        foreach ($tag in "p50", "p51bot") {
            $log = Join-Path $root "results\train_soccer_${tag}_$($name.ToLower()).log"
            if (-not (Test-Path $log)) { continue }
            $line = Select-String -Path $log -Pattern 'Step: (\d+)' | Select-Object -Last 1
            if ($line) { $total += [long]$line.Matches[0].Groups[1].Value }
        }
    }
    $stage = (Get-Content $logPath -Tail 30 | Where-Object { $_ -match 'round |rebuild|grade|PASS|below bar|done' } |
        Select-Object -Last 1) -replace '^\S+ \S+\s+', ''
    $rows = @(if (Test-Path $statusPath) { Get-Content $statusPath -Raw | ConvertFrom-Json })
    $rows += [pscustomobject]@{ time = (Get-Date).ToString("o"); totalSteps = $total; stage = "$stage" }
    ConvertTo-Json -InputObject $rows -Depth 4 | Set-Content $statusPath -Encoding utf8
    Add-Content $logPath ("{0}  [session] {1:N1}M steps trained so far; {2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), ($total / 1e6), $stage)
    & $python (Join-Path $root "scripts\make-roster-report.py") --state $statePath --out $reportPath `
        --status $statusPath --hours $Hours | Out-Null
}

Add-Content $logPath ("{0}  [session] {1} h watch window until {2:ddd HH:mm}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Hours, $deadline)
while ((Get-Date) -lt $deadline) {
    Snapshot
    $wait = [Math]::Min($EveryMinutes * 60, [Math]::Max(1, ($deadline - (Get-Date)).TotalSeconds))
    Start-Sleep -Seconds $wait
}
Snapshot
Add-Content $logPath ("{0}  [session] window closed; final report {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $reportPath)
