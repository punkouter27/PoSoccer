# Unattended queue: trains every character that has no brain yet, from scratch,
# a few at a time, and grades each against the benchmark bot (2026-11-08).
#
#   pwsh -ExecutionPolicy Bypass -Command "& ./scripts/train-roster.ps1"
#   pwsh -ExecutionPolicy Bypass -Command "& ./scripts/train-roster.ps1 -Profiles ALAN,BAILY -BatchSize 2"
#
# Per batch (one brain per character, never initialised from another's - AGENTS.md):
#   1. p50     30M steps from scratch, the p38 scheduled curriculum   (config TRAIN_<NAME>_p50.yaml)
#   2. p51bot  10M steps against bot v1 at full strength, from its own p50 (TRAIN_<NAME>_p51bot.yaml)
#   3. deploy (update-model, done by each worker), assign any first brain headless,
#      rebuild the player, grade every character of the batch over -GradeEpisodes.
#
# Wall clock: ~770 s per million steps with three trainers running, so ~8.5 h per batch
# of three, ~38 h for thirteen. Logs to docs/<yyyyMMdd>/train-roster.log and state to
# train-roster.json; grades are copied to docs/<yyyyMMdd>/grades/. A batch whose state
# says it finished is skipped on a re-run, so the queue resumes where it stopped.
#
# The editor must be closed (CLAUDE.md: runs over 30 min, and the headless steps need
# the project). TensorBoard is started by train-parallel.ps1.
param(
    [ValidatePattern('^[A-Z]+$')]
    [string[]]$Profiles = @(),
    [ValidateRange(1, 4)]
    [int]$BatchSize = 3,
    [int]$GradeEpisodes = 350,
    [string]$ScratchTag = "p50",
    [string]$ContinueTag = "p51bot"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot\lib-profile.ps1"

$day = Get-Date -Format "yyyyMMdd"
$docDir = Join-Path $root "docs\$day"
New-Item -ItemType Directory -Force -Path (Join-Path $docDir "grades") | Out-Null
$logPath = Join-Path $docDir "train-roster.log"
$statePath = Join-Path $docDir "train-roster.json"

function Log([string]$message) {
    $line = "{0}  {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $message
    Add-Content -Path $logPath -Value $line
    Write-Host $line
}

function Save-State($state) {
    $state | ConvertTo-Json -Depth 6 | Set-Content -Path $statePath -Encoding utf8
}

function Test-HasBrain([string]$name) {
    $asset = Join-Path $root "Assets\Agents\$(Get-ProfileFolder $name)\Reward_$name.asset"
    if (-not (Test-Path $asset)) { return $false }
    $line = Select-String -Path $asset -Pattern '^\s*brainModel:' | Select-Object -First 1
    return $line -and ($line.Line -notmatch 'fileID: 0\b')
}

# Waits for this tag's worker shells (train-parallel re-invokes itself per profile).
function Wait-Workers([string]$tag) {
    Start-Sleep -Seconds 20
    $lastNote = Get-Date
    while ($true) {
        $workers = @(Get-CimInstance Win32_Process -Filter "Name='pwsh.exe'" |
            Where-Object { $_.CommandLine -match '-WorkerProfile' -and $_.CommandLine -match "-Tag $tag\b" })
        if ($workers.Count -eq 0) { break }
        if (((Get-Date) - $lastNote).TotalMinutes -ge 60) {
            foreach ($file in Get-ChildItem (Join-Path $root "results") -Filter "train_soccer_${tag}_*.log") {
                $step = Select-String -Path $file.FullName -Pattern 'Step: \d+' | Select-Object -Last 1
                if ($step) { Log ("   [$tag] " + $step.Line.Trim()) }
            }
            $lastNote = Get-Date
        }
        Start-Sleep -Seconds 60
    }
    & "$PSScriptRoot\cleanup-training.ps1" | Out-Null
}

function Invoke-Round([string]$tag, [string[]]$batch, [string]$initFrom) {
    $arguments = @{ Tag = $tag; Profiles = $batch; AllowStale = $true }
    if ($initFrom) { $arguments.InitFromTag = $initFrom }
    Log "round $tag : $($batch -join ',')$(if ($initFrom) { " (from own $initFrom)" } else { ' (from scratch)' })"
    & "$PSScriptRoot\train-parallel.ps1" @arguments | Out-Null
    Wait-Workers $tag
    foreach ($name in $batch) {
        $runLog = Join-Path $root "results\train_soccer_${tag}_$($name.ToLower()).log"
        if (-not (Select-String -Path $runLog -Pattern "OK: soccer_${tag}_" -Quiet -ErrorAction SilentlyContinue) -and
            -not (Select-String -Path $runLog -Pattern "NEW slot created" -Quiet -ErrorAction SilentlyContinue)) {
            Log "WARNING: soccer_${tag}_$($name.ToLower()) did not deploy - see $runLog"
        }
    }
}

# ---- Who to train -------------------------------------------------------------
if ($Profiles.Count -eq 0) {
    $Profiles = @($PoSoccerProfiles.Keys | Where-Object { $_ -ne "STANDARD" -and -not (Test-HasBrain $_) })
}
foreach ($name in $Profiles) { Assert-Profile $name }
if ($Profiles.Count -eq 0) { Log "nothing to train: every character has a brain"; exit 0 }

$editor = Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" |
    Where-Object { $_.CommandLine -match [regex]::Escape($root) -and $_.CommandLine -notmatch '-batchmode' }
if ($editor) { throw "The Unity editor has this project open (PID $($editor.ProcessId)). Close it first." }

$batches = @()
for ($i = 0; $i -lt $Profiles.Count; $i += $BatchSize) {
    $batches += , @($Profiles[$i..([Math]::Min($i + $BatchSize, $Profiles.Count) - 1)])
}

$state = if (Test-Path $statePath) { Get-Content $statePath -Raw | ConvertFrom-Json -AsHashtable } else { @{} }
if (-not $state.ContainsKey("done")) { $state.done = @() }
if (-not $state.ContainsKey("grades")) { $state.grades = @{} }
$state.started = if ($state.ContainsKey("started")) { $state.started } else { (Get-Date).ToString("o") }
$state.queue = $Profiles
Save-State $state

$hours = [Math]::Round(($batches.Count * 40 * 770 / 3600.0), 1)
Log "roster queue start: $($Profiles -join ',') in $($batches.Count) batch(es) of up to $BatchSize, estimated ~$hours h"

# Make sure the player carries the current roster, reward tables and training wiring.
Log "build player"
& "$PSScriptRoot\build-headless.ps1" | Out-Null

foreach ($batch in $batches) {
    $todo = @($batch | Where-Object { $state.done -notcontains $_ })
    if ($todo.Count -eq 0) { continue }

    Invoke-Round $ScratchTag $todo ""
    # A first brain lands while three workers finish at once; their headless
    # assignments can collide on the project lock, so assign once more here.
    & "$PSScriptRoot\unity-exec.ps1" -Method PoSoccer.EditorTools.Editor_CreateCharacters.AssignBrains | Out-Null
    Invoke-Round $ContinueTag $todo $ScratchTag
    & "$PSScriptRoot\unity-exec.ps1" -Method PoSoccer.EditorTools.Editor_CreateCharacters.AssignBrains | Out-Null

    Log "rebuild player and grade $($todo -join ',') over $GradeEpisodes episodes"
    & "$PSScriptRoot\build-headless.ps1" | Out-Null
    foreach ($name in $todo) {
        $runId = "soccer_${ContinueTag}_$($name.ToLower())"
        & "$PSScriptRoot\evaluate.ps1" -RunId $runId -Profile $name -Episodes $GradeEpisodes *>&1 |
            Select-Object -Last 3 | ForEach-Object { Log "   $_" }
        $json = Join-Path $root "results\eval\$runId.json"
        if (Test-Path $json) {
            Copy-Item $json (Join-Path $docDir "grades\${runId}_$GradeEpisodes.json") -Force
            $grade = Get-Content $json -Raw | ConvertFrom-Json
            $win = [double]$grade.blueWins / [Math]::Max(1, $grade.episodes)
            $stale = [double]$grade.stalemates / [Math]::Max(1, $grade.episodes)
            $state.grades[$name] = @{ run = $runId; episodes = $grade.episodes; win = [Math]::Round($win, 4); stale = [Math]::Round($stale, 4)
                                      pass = ($win -ge 0.8 -and $stale -le 0.1) }
            Log ("   {0}: {1:P1} wins, {2:P1} stale -> {3}" -f $name, $win, $stale, $(if ($state.grades[$name].pass) { "PASS" } else { "below bar" }))
        }
        else {
            Log "WARNING: no grade for $runId"
        }
        $state.done += $name
        Save-State $state
    }
}

$state.finished = (Get-Date).ToString("o")
Save-State $state
Log "roster queue done"
