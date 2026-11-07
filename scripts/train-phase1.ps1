# Phase 1 PPO bootstrap training vs the heuristic bot.
# In-editor:  .\scripts\train-phase1.ps1 -RunId soccer_p1_00      (press Play when prompted)
# Headless:   .\scripts\train-phase1.ps1 -RunId soccer_p1_00 -EnvPath Builds\PoSoccer\PoSoccer.exe -NumEnvs 4
# Team (2v2): .\scripts\train-phase1.ps1 -RunId soccer_p30team_standard -Config TRAIN_STANDARD_p30team.yaml -Squad "1,2" -EnvPath ...
# Profile:    .\scripts\train-phase1.ps1 -RunId soccer_p38_matt -Profile MATT -Config TRAIN_MATT_p38.yaml -EnvPath ...
# Realism:    add -StaminaModel human and/or -ReactionMs 200 (opt-in, see Agent_Realism; both
#             change what the brain learns against, and update-model.ps1 stamps them onto the profile).
param(
    [string]$RunId = "soccer_p1_00",
    [string]$EnvPath = "",
    [int]$NumEnvs = 4,
    [int]$BasePort = 5005,
    [string]$Config = "TRAIN_STANDARD_p37bot.yaml",
    [string]$InitFrom = "",
    [switch]$Resume,
    [switch]$Force,
    [Parameter(HelpMessage = "Leave both teams on the trainer (symmetric self-play) instead of facing the bot.")]
    [switch]$SelfPlay,
    [Parameter(HelpMessage = "Scripted-bot version the learner faces (1 = benchmark bot, 2 = lead pursuit and bank shots).")]
    [ValidateRange(1, 2)]
    [int]$BotVersion = 1,
    [Parameter(HelpMessage = "Players per side on each training pitch, cycled over the grid: '2' = all 2v2, '1,2' = alternate. Empty = the authored 1v1.")]
    [string]$Squad = "",
    [Parameter(HelpMessage = "Roster profile to train (POSOCCER_PROFILE). The config must name the same behavior.")]
    [ValidateSet("STANDARD", "MATT", "KIM", "NICK")]
    [string]$Profile = "STANDARD",
    [Parameter(HelpMessage = "Opt-in stamina model: shipped (default) or human (repeated-sprint).")]
    [ValidateSet("shipped", "human")]
    [string]$StaminaModel = "shipped",
    [Parameter(HelpMessage = "Opt-in reaction latency in ms applied to the learner's actions (0 = off).")]
    [ValidateRange(0, 400)]
    [double]$ReactionMs = 0,
    [Parameter(HelpMessage = "Skip the stale-build gate in preflight.ps1.")]
    [switch]$AllowStale
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot\lib-profile.ps1"

# Pre-flight: stale build, scene order, toolchain parity, LFS stubs, orphaned trainers.
$preflightArgs = @()
if ($EnvPath) { $preflightArgs += @("-EnvPath", $EnvPath) } else { $preflightArgs += "-InEditor" }
if ($AllowStale) { $preflightArgs += "-AllowStale" }
& "$PSScriptRoot\preflight.ps1" @preflightArgs
if ($LASTEXITCODE -ne 0) { throw "Preflight failed - see above. Nothing was started." }
# Prefer .venv2 if it exists (the canonical .venv can be locked by Unity at
# runtime; .venv2 is created out-of-band by uv with the pinned interpreter
# when .venv is unusable. Both are valid; the newer one wins).
$venv = if (Test-Path "$root\.venv2\Scripts\Activate.ps1") { "$root\.venv2" } else { "$root\.venv" }
& "$venv\Scripts\Activate.ps1"

# Optional observability nudge: warn if TensorBoard isn't responding.
# (UNITY_RULES 4: track active TensorBoard sessions.)
try {
    $null = Invoke-WebRequest -Uri "http://localhost:6006/" -UseBasicParsing -TimeoutSec 2
} catch {
    Write-Warning "TensorBoard is not reachable on :6006. Run .\scripts\tensorboard.ps1 in another terminal to watch reward convergence / policy entropy / value loss. Training will continue."
}

# Phase 1 means "learn against the scripted bot". The training scene leaves both
# teams on BehaviorType.Default, which routes everything to the trainer, so
# without this the run is symmetric self-play and Agent_HeuristicBot never
# executes - which is what silently happened to every run before 2026-08-04.
# Agent_Soccer.ApplyTrainingOpponent reads this and forces Red to HeuristicOnly.
# Env players are spawned as children of mlagents-learn, so they inherit it.
# (-League, mixing self-play and bot pitches, was retired 2026-11-07: p34control
# measured it adding nothing.)
$env:POSOCCER_OPPONENT = if ($SelfPlay) { "" } else { "bot" }
$env:POSOCCER_BOT_VERSION = "$BotVersion"
$env:POSOCCER_BOT_VERSION_BLUE = "$BotVersion"

# Squad size per pitch (Agent_EnvController.ApplyTrainingSquad). Validated here so a
# typo fails before a multi-hour run rather than as one LogError in a player log.
# Confirm it reached the player on TensorBoard: PoSoccer/blue_squad (1.5 for "1,2").
if ($Squad -and $Squad -notmatch '^\s*\d+\s*(,\s*\d+\s*)*$') {
    throw "-Squad '$Squad' must be a comma-separated list of squad sizes, e.g. '2' or '1,2'."
}
$env:POSOCCER_SQUAD = $Squad
# Profile and the opt-in realism options (Agent_Realism). "shipped" / "0" are the
# shipped dynamics; the env var overrides whatever the profile asset carries.
# Only when asked: the STANDARD line (p34control..p37bot) ran without the override, and
# with it the scene's red agent would take STANDARD's profile instead of its own.
$env:POSOCCER_PROFILE = if ($PSBoundParameters.ContainsKey("Profile")) { $Profile } else { "" }
$env:POSOCCER_STAMINA_MODEL = $StaminaModel
$env:POSOCCER_REACTION_MS = "$ReactionMs"

# The default used to be STANDARD_phase1_ppo.yaml, which lived in config\_archive
# and therefore never resolved - the documented default invocation of this script
# could not start a run. Resolve and verify before handing the path to the trainer,
# so a missing config fails here, naming the file, rather than inside mlagents-learn.
$configPath = Join-Path "$root\config" $Config
# Superseded recipes live in config\archive and stay runnable by name.
if (-not (Test-Path $configPath)) { $configPath = Join-Path "$root\config\archive" $Config }
if (-not (Test-Path $configPath)) {
    $available = (Get-ChildItem "$root\config\*.yaml" | ForEach-Object { $_.Name }) -join ", "
    throw "Config not found: $configPath`nAvailable in config\: $available"
}

$mlArgs = @($configPath,
          "--run-id=$RunId", "--base-port=$BasePort", "--results-dir=$root\results")
if ($EnvPath) { $mlArgs += @("--env=$root\$EnvPath", "--no-graphics", "--num-envs=$NumEnvs") }
if ($InitFrom) { $mlArgs += "--initialize-from=$InitFrom" }
if ($Resume)  { $mlArgs += "--resume" }
if ($Force)   { $mlArgs += "--force" }

# Checkpoint provenance (interface/physics versions, config hash) and a time estimate.
$provenancePath = Write-RunProvenance -Root $root -RunId $RunId -ConfigPath $configPath -Profile $Profile `
    -InitFrom $InitFrom -StaminaModel $StaminaModel -ReactionMs $ReactionMs `
    -Extra @{ squad = $Squad; botVersion = $BotVersion; opponent = $env:POSOCCER_OPPONENT; numEnvs = $NumEnvs }
Write-Host "Provenance: $provenancePath"
$estimate = Get-TrainingEstimate -ConfigPath $configPath -Concurrent 1
if ($estimate) {
    Write-Host ("Estimate: {0}; done around {1:yyyy-MM-dd HH:mm} local." -f $estimate.Text, $estimate.FinishUtc.ToLocalTime())
}

try {
    mlagents-learn @mlArgs
}
finally {
    $env:POSOCCER_OPPONENT = ""
    $env:POSOCCER_SQUAD = ""
    $env:POSOCCER_BOT_VERSION = ""
    $env:POSOCCER_BOT_VERSION_BLUE = ""
    $env:POSOCCER_PROFILE = ""
    $env:POSOCCER_STAMINA_MODEL = ""
    $env:POSOCCER_REACTION_MS = ""
    # Lifecycle guardrail: no orphaned trainer/env processes after a run.
    & "$root\scripts\cleanup-training.ps1"
    # Auto-assign the freshest checkpoint into the agent prefab slot (GUID preserved).
    Copy-RunProvenance -Root $root -RunId $RunId
    & "$root\scripts\update-model.ps1" -RunId $RunId -Profile $Profile
}
