# Train several personality profiles AT THE SAME TIME, one mlagents-learn per profile.
#
#   .\scripts\tensorboard.ps1
#   .\scripts\train-parallel.ps1 -Tag p31                       # all four, config TRAIN_<NAME>_p31.yaml
#   .\scripts\train-parallel.ps1 -Tag p31 -Profiles MATT,KIM
#   .\scripts\train-parallel.ps1 -Tag p38 -Profiles MATT,KIM,NICK     # from scratch, config TRAIN_<NAME>_p38.yaml
#   .\scripts\train-parallel.ps1 -Tag p39bot -Profiles MATT,KIM,NICK -InitFromTag p38   # continue each from its own p38
#
# FROM SCRATCH IS THE DEFAULT (2026-11-07). AGENTS.md: every character trains its own
# brain from scratch; no character reuses another's. -InitFromTag only continues a
# profile from ITS OWN earlier run (soccer_<tag>_<name>), never from another profile.
#
# Before any worker starts: preflight.ps1 (stale build, scene order, toolchain parity,
# LFS stubs, orphaned trainers), TensorBoard (started if :6006 is not answering, per the
# project rule), and a wall-clock estimate. Each worker writes its provenance.json
# (interface/physics versions, config hash) and copies it next to the checkpoints when
# the trainer exits; update-model.ps1 stamps it onto the profile.
#
# WHY THIS EXISTS BESIDE train-all.ps1. train-all runs the profiles one after another,
# and one run uses about 2 of this machine's 12 logical cores (the trainer is a single
# thread; see CLAUDE.md), so four sequential runs leave the machine mostly idle for
# roughly seven hours. train-phase1.ps1 cannot simply be started four times either: its
# `finally` block calls cleanup-training.ps1, which kills EVERY trainer and env player on
# the machine, so the first run to finish would take the other three down with it.
#
# Each profile gets its own worker shell, its own port range and its own log in
# results\train_<runId>.log. The worker deploys its model with update-model.ps1 when its
# trainer exits; nothing here kills another run. Run cleanup-training.ps1 yourself
# afterwards if a worker died and left env players behind.
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [ValidatePattern('^[A-Z]+$')]
    [string[]]$Profiles = @("STANDARD", "MATT", "KIM", "NICK"),
    [string]$ConfigTag = "",
    [string]$EnvPath = "Builds\PoSoccer\PoSoccer.exe",
    [int]$NumEnvs = 4,
    [string]$Squad = "",
    # Continue each profile from its own soccer_<InitFromTag>_<name> run. Empty = from scratch.
    [string]$InitFromTag = "",
    # Per-profile override of -InitFromTag: "MATT=soccer_p38_matt;NICK=soccer_p39bot_nick".
    # Used by train-loop.ps1, which continues each profile from ITS OWN best-graded run.
    [string]$InitFromRuns = "",
    # Opt-in realism options (Agent_Realism), applied to every profile in this launch.
    [ValidateSet("shipped", "human")]
    [string]$StaminaModel = "shipped",
    [ValidateRange(0, 400)]
    [double]$ReactionMs = 0,
    [switch]$AllowStale,
    [switch]$Force,
    # Internal: set when this script re-invokes itself as one profile's worker.
    [string]$WorkerProfile = "",
    [string]$WorkerInitFrom = "",
    [int]$WorkerPort = 0
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot\lib-profile.ps1"
if (-not $ConfigTag) { $ConfigTag = $Tag }

# Resolve which run each profile continues from ("" = from scratch). Only ever the
# profile's own lineage (soccer_*_<name>), never another profile's brain (AGENTS.md).
function Resolve-InitFrom([string]$name) {
    foreach ($pair in ($InitFromRuns -split ';')) {
        $kv = $pair -split '=', 2
        if ($kv.Count -eq 2 -and $kv[0].Trim() -eq $name) { return $kv[1].Trim() }
    }
    if ($InitFromTag) { return "soccer_${InitFromTag}_$($name.ToLower())" }
    return ""
}

function Resolve-ProfileConfig([string]$name) {
    $path = Join-Path $root "config\TRAIN_${name}_${ConfigTag}.yaml"
    if (-not (Test-Path $path)) { $path = Join-Path $root "config\archive\TRAIN_${name}_${ConfigTag}.yaml" }
    return $path
}

if ($WorkerProfile) {
    # ---- one profile's worker -------------------------------------------------
    $name = $WorkerProfile
    $runId = "soccer_${Tag}_$($name.ToLower())"
    $venv = if (Test-Path "$root\.venv2\Scripts\Activate.ps1") { "$root\.venv2" } else { "$root\.venv" }
    & "$venv\Scripts\Activate.ps1"

    # Same environment train-phase1.ps1 and train-all.ps1 set: face the scripted bot,
    # and swap both agents onto this profile's reward table and behaviour name.
    $env:POSOCCER_OPPONENT = "bot"
    $env:POSOCCER_SQUAD = $Squad
    $env:POSOCCER_PROFILE = $name
    $env:POSOCCER_STAMINA_MODEL = $StaminaModel
    $env:POSOCCER_REACTION_MS = "$ReactionMs"

    $config = Resolve-ProfileConfig $name
    $mlArgs = @($config,
                "--run-id=$runId", "--base-port=$WorkerPort", "--results-dir=$root\results",
                "--env=$root\$EnvPath", "--no-graphics", "--num-envs=$NumEnvs")
    $initFrom = $WorkerInitFrom
    if ($initFrom) { $mlArgs += "--initialize-from=$initFrom" }
    if ($Force) { $mlArgs += "--force" }

    $prov = Write-RunProvenance -Root $root -RunId $runId -ConfigPath $config -Profile $name `
        -InitFrom $initFrom -StaminaModel $StaminaModel -ReactionMs $ReactionMs `
        -Extra @{ squad = $Squad; opponent = "bot"; numEnvs = $NumEnvs; parallelTag = $Tag }
    Write-Host "[$name] provenance $prov"

    mlagents-learn @mlArgs
    $code = $LASTEXITCODE
    Write-Host "[$name] mlagents-learn exited with code $code"
    Copy-RunProvenance -Root $root -RunId $runId
    if ($code -eq 0) {
        & "$root\scripts\update-model.ps1" -RunId $runId -Profile $name
    }
    exit $code
}

# ---- launcher ---------------------------------------------------------------
$preflightArgs = @{ EnvPath = $EnvPath }
if ($AllowStale) { $preflightArgs.AllowStale = $true }
& "$PSScriptRoot\preflight.ps1" @preflightArgs
if ($LASTEXITCODE -ne 0) { throw "Preflight failed - see above. Nothing was started." }

foreach ($name in $Profiles) {
    $config = Resolve-ProfileConfig $name
    if (-not (Test-Path $config)) { throw "Config not found: $config" }
    # The behavior name in the config is what the trainer listens for; the player names
    # its brain after the profile (POSOCCER_PROFILE). A mismatch trains nothing.
    if (-not (Select-String -Path $config -Pattern "^\s{2}${name}:\s*$" -Quiet)) {
        throw "Config $config has no 'behaviors: ${name}:' block - the $name worker would train nothing."
    }
    $init = Resolve-InitFrom $name
    if ($init) {
        if (-not $init.EndsWith("_$($name.ToLower())")) {
            throw "$name cannot continue from $init - a profile only continues its own lineage (AGENTS.md)."
        }
        $source = Join-Path $root "results\$init"
        if (-not (Test-Path $source)) {
            throw "Init source $source does not exist (archived runs in results\_archive are not searched)."
        }
    }
}
New-Item -ItemType Directory -Force (Join-Path $root "results") | Out-Null

# TensorBoard with every run (project rule). Start it if nothing answers on :6006.
$tbUp = $true
try { $null = Invoke-WebRequest -Uri "http://localhost:6006/" -UseBasicParsing -TimeoutSec 2 } catch { $tbUp = $false }
if (-not $tbUp) {
    Write-Host "TensorBoard is not running - starting it (scripts\tensorboard.ps1)."
    & "$PSScriptRoot\tensorboard.ps1"
}

# Wall-clock estimate (AGENTS.md). Profiles run concurrently, so the slowest sets it.
$longest = $null
foreach ($name in $Profiles) {
    $estimate = Get-TrainingEstimate -ConfigPath (Resolve-ProfileConfig $name) -Concurrent $Profiles.Count
    if ($estimate -and (-not $longest -or $estimate.Seconds -gt $longest.Seconds)) { $longest = $estimate }
}
if ($longest) {
    Write-Host ("Estimate ({0} profiles in parallel): {1}; done around {2:yyyy-MM-dd HH:mm} local." -f `
        $Profiles.Count, $longest.Text, $longest.FinishUtc.ToLocalTime())
}

# Port ranges are 10 apart and skip 5037, which adb listens on (see train-all.ps1).
$ports = @{}
foreach ($name in $Profiles) { $ports[$name] = Get-ProfilePort $name }
if ($NumEnvs -gt 8) { throw "-NumEnvs above 8 would overlap the per-profile port ranges." }

$self = $MyInvocation.MyCommand.Path
$workers = @()
foreach ($name in $Profiles) {
    $runId = "soccer_${Tag}_$($name.ToLower())"
    $log = Join-Path $root "results\train_$runId.log"
    $workerArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $self,
                    '-Tag', $Tag, '-ConfigTag', $ConfigTag, '-EnvPath', $EnvPath,
                    '-NumEnvs', "$NumEnvs", '-WorkerProfile', $name, '-WorkerPort', "$($ports[$name])")
    if ($Squad) { $workerArgs += @('-Squad', $Squad) }
    $init = Resolve-InitFrom $name
    if ($init) { $workerArgs += @('-WorkerInitFrom', $init) }
    $workerArgs += @('-StaminaModel', $StaminaModel, '-ReactionMs', "$ReactionMs")
    if ($Force) { $workerArgs += '-Force' }
    $p = Start-Process pwsh -ArgumentList $workerArgs -WorkingDirectory $root -WindowStyle Hidden -PassThru `
                       -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    Write-Host "$name -> run $runId, base port $($ports[$name]), worker PID $($p.Id), log $log"
    $workers += $p
}
Write-Host "PIDS: $(($workers | ForEach-Object { $_.Id }) -join ',')"
