# Unattended keep-best training loop over several profiles, to a wall-clock deadline.
#
#   .\scripts\train-loop.ps1 -Hours 8 -Profiles MATT,KIM,NICK -AdoptTag p39bot -BaselineTag p38
#
# Each round:  train (train-parallel.ps1, each profile continuing from ITS OWN best run)
#           -> rebuild the player -> grade every new run (evaluate.ps1, 350 episodes)
#           -> keep-best: a profile whose new grade beats its best is kept (already
#              deployed by the worker); otherwise its best run is redeployed in place.
# Priority: profiles that fail the bar (>=80% wins, <=10% stalemates) train first; once
# all pass, all of them refine. Rounds are sized to fit the deadline, leaving time for a
# final 1000-episode grade of the production weights and the HTML report
# (scripts/make-loop-report.py).
#
# "Better" is the bar margin, min(win - 0.80, 0.10 - stale): the distance from failing on
# whichever criterion is closer. Mean reward is never a decision variable (CLAUDE.md).
#
# Every step is logged to docs/<yyyyMMdd>/train-loop.log and the state to
# train-loop.json, so a later agent can pick up where the loop left off.
param(
    [double]$Hours = 8,
    [ValidateSet("STANDARD", "MATT", "KIM", "NICK")]
    [string[]]$Profiles = @("MATT", "KIM", "NICK"),
    # A round already training when the loop starts: wait for it and grade it as round 1.
    [string]$AdoptTag = "",
    # Graded runs that are the starting "best" for each profile (results/eval/soccer_<tag>_<name>.json).
    [string]$BaselineTag = "p38",
    # Recipe every round copies (config/TRAIN_<NAME>_<ConfigTag>.yaml), with max_steps resized.
    [string]$ConfigTag = "p39bot",
    [int]$FirstRound = 40,
    [int]$GradeEpisodes = 350,
    [int]$FinalEpisodes = 1000,
    [double]$MaxRoundMSteps = 10,
    [double]$MinRoundMSteps = 3,
    [double]$GradeHours = 0.5,
    [double]$FinalHours = 1.15
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$start = Get-Date
$deadline = $start.AddHours($Hours)
$dir = Join-Path $root ("docs\" + $start.ToString("yyyyMMdd"))
New-Item -ItemType Directory -Force $dir, (Join-Path $dir "grades") | Out-Null
$logPath = Join-Path $dir "train-loop.log"
$statePath = Join-Path $dir "train-loop.json"

function Log([string]$msg) {
    $line = "{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $msg
    Add-Content -Path $logPath -Value $line
    Write-Host $line
}

# Measured on this machine (p38, 2026-11-07): ~770 s per million steps with three
# trainers side by side; fewer trainers run a little faster each.
function SecondsPerMillion([int]$n) { @{ 1 = 560; 2 = 660; 3 = 770; 4 = 900 }[[Math]::Min([Math]::Max($n, 1), 4)] }

function Read-Grade([string]$runId) {
    $path = Join-Path $root "results\eval\$runId.json"
    if (-not (Test-Path $path)) { return $null }
    $j = Get-Content $path -Raw | ConvertFrom-Json
    if ($j.invalid -or $j.episodes -le 0) { return $null }
    $win = $j.blueWins / $j.episodes
    $stale = $j.stalemates / $j.episodes
    return [pscustomobject]@{
        run = $runId; episodes = [int]$j.episodes; win = [Math]::Round($win, 4); stale = [Math]::Round($stale, 4)
        loss = [Math]::Round($j.redWins / $j.episodes, 4)
        pass = ($win -ge 0.80 -and $stale -le 0.10)
        margin = [Math]::Round([Math]::Min($win - 0.80, 0.10 - $stale), 4)
    }
}

$state = [ordered]@{ started = $start.ToString("o"); deadline = $deadline.ToString("o"); profiles = $Profiles
                     best = [ordered]@{}; rounds = @(); final = $null; finished = $null }
function Save-State { $state | ConvertTo-Json -Depth 8 | Set-Content -Path $statePath -Encoding utf8 }

foreach ($p in $Profiles) {
    $g = Read-Grade "soccer_${BaselineTag}_$($p.ToLower())"
    if (-not $g) { throw "No baseline grade for $p (results/eval/soccer_${BaselineTag}_$($p.ToLower()).json). Grade it first." }
    $state.best[$p] = $g
}
$state.rounds += [ordered]@{ tag = $BaselineTag; profiles = $Profiles; steps = $null
                             grades = [ordered]@{}; kept = [ordered]@{} }
foreach ($p in $Profiles) { $state.rounds[0].grades[$p] = $state.best[$p]; $state.rounds[0].kept[$p] = $true }
Save-State
Log "loop start: deadline $($deadline.ToString('HH:mm')), profiles $($Profiles -join ','), baseline $BaselineTag"
foreach ($p in $Profiles) { $b = $state.best[$p]; Log ("  {0}: {1:P1} wins, {2:P1} stale, pass={3}" -f $p, $b.win, $b.stale, $b.pass) }

function Worker-Alive([string]$tag) {
    $procs = Get-CimInstance Win32_Process -Filter "Name='pwsh.exe'" -ErrorAction SilentlyContinue |
             Where-Object { $_.CommandLine -match "-WorkerProfile" -and $_.CommandLine -match "-Tag $tag\b" }
    return @($procs).Count
}

function Wait-Round([string]$tag, [string[]]$names) {
    $lastReport = Get-Date
    while ((Worker-Alive $tag) -gt 0) {
        Start-Sleep -Seconds 60
        if (((Get-Date) - $lastReport).TotalMinutes -ge 60) {
            $lastReport = Get-Date
            foreach ($p in $names) {
                $log = Join-Path $root "results\train_soccer_${tag}_$($p.ToLower()).log"
                $step = Select-String -Path $log -Pattern "Step:" -ErrorAction SilentlyContinue | Select-Object -Last 1
                if ($step) { Log ("  [$tag] " + ($step.Line -replace '^\[INFO\]\s*', '')) }
            }
        }
    }
    # Crash check: a worker that did not reach update-model leaves no "OK:" line.
    $ok = @()
    foreach ($p in $names) {
        $log = Join-Path $root "results\train_soccer_${tag}_$($p.ToLower()).log"
        if (Select-String -Path $log -Pattern "^OK: soccer_${tag}_" -Quiet -ErrorAction SilentlyContinue) { $ok += $p }
        else { Log "  WARNING: $p in $tag did not deploy (crash or interrupted) - see $log" }
    }
    & "$root\scripts\cleanup-training.ps1" | Out-Null
    return $ok
}

function Build-Player {
    Log "rebuild player"
    & "$root\scripts\build-headless.ps1" | Out-Null
}

function Grade-Runs([hashtable]$runs, [int]$episodes) {
    # $runs: profile -> runId. All graded in parallel (each eval writes its own JSON).
    $procs = @()
    foreach ($p in $runs.Keys) {
        $evalLog = Join-Path $dir "grades\eval_$($runs[$p])_$episodes.log"
        $procs += Start-Process pwsh -PassThru -WindowStyle Hidden -WorkingDirectory $root `
            -RedirectStandardOutput $evalLog -RedirectStandardError "$evalLog.err" `
            -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "$root\scripts\evaluate.ps1",
                            '-RunId', $runs[$p], '-Profile', $p, '-Episodes', "$episodes")
    }
    $procs | Wait-Process
    $out = @{}
    foreach ($p in $runs.Keys) {
        $g = Read-Grade $runs[$p]
        $src = Join-Path $root "results\eval\$($runs[$p]).json"
        if (Test-Path $src) { Copy-Item $src (Join-Path $dir "grades\$($runs[$p])_$episodes.json") -Force }
        $out[$p] = $g
    }
    return $out
}

function Deploy([string]$p, [string]$runId) {
    & "$root\scripts\update-model.ps1" -RunId $runId -Profile $p | Out-Null
}

function Grade-And-Keep([string]$tag, [string[]]$names, $round) {
    Build-Player
    $runs = @{}; foreach ($p in $names) { $runs[$p] = "soccer_${tag}_$($p.ToLower())" }
    $grades = Grade-Runs $runs $GradeEpisodes
    $restored = $false
    foreach ($p in $names) {
        $g = $grades[$p]; $b = $state.best[$p]
        $round.grades[$p] = $g
        if ($g -and $g.margin -gt $b.margin) {
            $state.best[$p] = $g; $round.kept[$p] = $true
            Log ("  {0}: {1:P1} wins, {2:P1} stale -> NEW BEST (margin {3:+0.0%;-0.0%} vs {4:+0.0%;-0.0%})" -f $p, $g.win, $g.stale, $g.margin, $b.margin)
        } else {
            $round.kept[$p] = $false
            if ($g) { Log ("  {0}: {1:P1} wins, {2:P1} stale -> worse than {3}; restoring it" -f $p, $g.win, $g.stale, $b.run) }
            else { Log "  ${p}: no valid grade; restoring $($b.run)" }
            Deploy $p $b.run; $restored = $true
        }
    }
    Save-State
    return $restored
}

function New-RoundConfig([string]$p, [string]$tag, [long]$steps, [string]$from) {
    $src = Join-Path $root "config\TRAIN_${p}_${ConfigTag}.yaml"
    $dst = Join-Path $root "config\TRAIN_${p}_${tag}.yaml"
    $body = (Get-Content $src -Raw) -replace '(?m)^(\s*max_steps:\s*)\S+', "`${1}$steps"
    $head = "# $tag / $p - generated by train-loop.ps1 on $(Get-Date -Format 'yyyy-MM-dd HH:mm') from TRAIN_${p}_${ConfigTag}.yaml.`n" +
            "# Continues $from (this profile's best-graded run so far); max_steps resized to fit the loop deadline.`n"
    Set-Content -Path $dst -Value ($head + $body) -Encoding utf8 -NoNewline
}

# ---- round 1: adopt a run that is already training -------------------------------
$needRebuild = $false
if ($AdoptTag) {
    Log "adopting running round $AdoptTag"
    $round = [ordered]@{ tag = $AdoptTag; profiles = $Profiles; steps = 10000000; grades = [ordered]@{}; kept = [ordered]@{} }
    $state.rounds += $round; Save-State
    $done = Wait-Round $AdoptTag $Profiles
    Log "round $AdoptTag finished ($($done -join ',') deployed)"
    $needRebuild = Grade-And-Keep $AdoptTag $Profiles $round
}

# ---- further rounds, sized to the deadline ---------------------------------------
$n = $FirstRound
while ($true) {
    $urgent = @($Profiles | Where-Object { -not $state.best[$_].pass } | Sort-Object { $state.best[$_].margin })
    $names = if ($urgent.Count -gt 0) { $urgent } else { @($Profiles | Sort-Object { $state.best[$_].margin }) }
    $secsLeft = ($deadline - (Get-Date)).TotalSeconds - ($GradeHours + $FinalHours) * 3600 - 600
    $mSteps = [Math]::Min($MaxRoundMSteps, [Math]::Floor($secsLeft / (SecondsPerMillion $names.Count)))
    if ($mSteps -lt $MinRoundMSteps) { Log "no time for another round (would be $mSteps M steps)"; break }

    $tag = "p$n"
    $steps = [long]($mSteps * 1e6)
    $map = @()
    foreach ($p in $names) {
        New-RoundConfig $p $tag $steps $state.best[$p].run
        $map += "$p=$($state.best[$p].run)"
    }
    Log ("round ${tag}: {0} ({1}), {2}M steps each, continuing {3}" -f ($names -join ','),
         $(if ($urgent.Count) { "failing the bar, worst first" } else { "all pass: refining" }), $mSteps, ($map -join ', '))
    $round = [ordered]@{ tag = $tag; profiles = $names; steps = $steps; grades = [ordered]@{}; kept = [ordered]@{} }
    $state.rounds += $round; Save-State

    & "$root\scripts\train-parallel.ps1" -Tag $tag -Profiles $names -InitFromRuns ($map -join ';') -AllowStale | Out-Null
    Start-Sleep -Seconds 30
    $done = Wait-Round $tag $names
    Log "round $tag finished ($($done -join ',') deployed)"
    $needRebuild = Grade-And-Keep $tag $names $round
    $n++
}

# ---- final: 1000-episode grade of the production weights -------------------------
Build-Player
$runs = @{}; foreach ($p in $Profiles) { $runs[$p] = $state.best[$p].run }
Log "final grade, $FinalEpisodes episodes: $(($runs.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', ')"
$final = Grade-Runs $runs $FinalEpisodes
$state.final = [ordered]@{}
foreach ($p in $Profiles) {
    $g = $final[$p]; $state.final[$p] = $g
    if ($g) { Log ("  FINAL {0}: {1:P1} wins, {2:P1} stale, {3:P1} losses -> {4}" -f $p, $g.win, $g.stale, $g.loss, $(if ($g.pass) { "PASS" } else { "fail" })) }
}
$state.finished = (Get-Date).ToString("o"); Save-State

$py = Join-Path $root ".venv\Scripts\python.exe"
& $py (Join-Path $root "scripts\make-loop-report.py") --state $statePath --out (Join-Path $dir "training-report.html")
Log "report: $(Join-Path $dir 'training-report.html')"
Log "loop done"
