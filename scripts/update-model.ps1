# Model pipeline (UNITY_RULES): overwrite the tracked .onnx IN PLACE so the .meta
# GUID reference on the agent prefab / Reward_ profile never changes; Unity
# hot-reloads the weights. One GUID-stable slot per personality.
#
#   .\scripts\update-model.ps1 -RunId soccer_p2_00                  # -> STANDARD
#   .\scripts\update-model.ps1 -RunId matt_p1_00 -Profile MATT      # -> MATT
param(
    [Parameter(Mandatory = $true)][string]$RunId,
    [ValidatePattern('^[A-Z]+$')]
    [string]$Profile = "STANDARD",
    [string]$Behavior
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot\lib-profile.ps1"

# Agent asset folders follow <AgentName>_v<NN> (UNITY_RULES 1).
$folder = Get-ProfileFolder $Profile

# The trainer exports under the behavior name, which defaults to the profile name.
if (-not $Behavior) { $Behavior = $Profile }

$source = Join-Path $root "results\$RunId\$Behavior.onnx"
# Legacy runs (pre-rename) exported under the old behavior name.
if (-not (Test-Path $source)) {
    $legacy = Join-Path $root "results\$RunId\SoccerAgent.onnx"
    if (Test-Path $legacy) { $source = $legacy }
}

if (-not (Test-Path $source)) {
    Write-Warning "No exported model at $source - nothing to assign."
    exit 0
}

$targetDir = Join-Path $root "Assets\Agents\$folder"
if (-not (Test-Path $targetDir)) {
    throw "Agent folder missing: Assets/Agents/$folder"
}
$target = Join-Path $targetDir "$Profile.onnx"

$isNewSlot = -not (Test-Path $target)
Copy-Item $source $target -Force

if ($isNewSlot) {
    # First brain for this character: point Reward_<NAME>.brainModel at the new file,
    # headless (2026-11-08; this used to be a hand step in the Inspector). Every later
    # run overwrites the file in place, so the GUID never changes again.
    Write-Host "NEW slot created: Assets/Agents/$folder/$Profile.onnx - assigning it to Reward_$Profile.brainModel"
    & "$PSScriptRoot\unity-exec.ps1" -Method PoSoccer.EditorTools.Editor_CreateCharacters.AssignBrains
    if ($LASTEXITCODE -ne 0) {
        Write-Warning ("Could not assign the brain headless (is the editor open?). Run: " +
            "scripts\unity-exec.ps1 -Method PoSoccer.EditorTools.Editor_CreateCharacters.AssignBrains")
    }
} else {
    Write-Host "OK: $RunId -> Assets/Agents/$folder/$Profile.onnx (GUID preserved)"
}

# Surface profiles that are still running as heuristic bots.
$profileAsset = Join-Path $targetDir "Reward_$Profile.asset"
if ((Test-Path $profileAsset) -and -not (Select-String -Path $profileAsset -Pattern 'brainModel:' -Quiet)) {
    Write-Warning "Reward_$Profile.asset has no brainModel yet - $Profile still plays as a heuristic bot."
}

# Stamp training provenance onto the profile so the menu can show what is behind
# each brain. Steps come from the checkpoint filename (<BEHAVIOR>-<steps>.onnx);
# the run root export has no step in its name, so fall back to the highest
# numbered checkpoint in the run.
$steps = 0
$checkpointDir = Join-Path $root "results\$RunId\$Behavior"
if (Test-Path $checkpointDir) {
    $steps = Get-ChildItem $checkpointDir -Filter "$Behavior-*.onnx" |
        ForEach-Object { [int]($_.BaseName -replace '^.*-', '') } |
        Sort-Object -Descending | Select-Object -First 1
}
if (-not $steps) { $steps = 0 }

# Deploying new weights invalidates any previously measured win rate; evaluate.ps1
# writes a fresh one. -1 renders as "unrated" in the menu.
Set-ProfileField $profileAsset 'trainingSteps' "$steps"
Set-ProfileField $profileAsset 'trainingRunId' $RunId
Set-ProfileField $profileAsset 'trainedOn' (Get-Date -Format 'yyyy-MM-dd')
Set-ProfileField $profileAsset 'evalWinRate' '-1'
Set-ProfileField $profileAsset 'evalEpisodes' '0'

# ActionGain provenance. Read from the SOURCE rather than hardcoded here, because a
# constant duplicated into a deploy script is exactly how the value silently diverges
# from the runtime it is supposed to describe.
#
# This exists because on 2026-09-07 the gain went 1.6 -> 1.0 (p22) while MATT, NICK and
# KIM stayed on p21 checkpoints trained at 1.6. Tensor shapes are unchanged, so those
# .onnx load without a warning and then under-drive. Agent_EditMode_ActionGain compares
# what this stamps against Agent_Soccer.ActionGain and fails on a mismatch.
$agentSource = Join-Path $root "Assets\Scripts\Agents\Agent_Soccer.cs"
$gain = $null
if (Test-Path $agentSource) {
    $m = Select-String -Path $agentSource -Pattern 'public\s+const\s+float\s+ActionGain\s*=\s*([0-9.]+)f' |
         Select-Object -First 1
    if ($m) { $gain = $m.Matches[0].Groups[1].Value }
}
if ($gain) {
    Set-ProfileField $profileAsset 'trainedActionGain' $gain
} else {
    Write-Warning "Could not read Agent_Soccer.ActionGain from source - trainedActionGain NOT stamped. Agent_EditMode_ActionGain will flag this profile as unknown provenance."
}

Write-Host "  provenance: $steps steps from $RunId, ActionGain $gain (eval rating cleared)"

# Checkpoint tags (2026-11-07): interface + physics version and config hash, from the
# provenance.json the training script wrote when the run started. The realism options
# the run trained under are stamped too, because the profile is what makes the runtime
# apply them (Agent_Realism): a latency-trained brain deployed without its latency
# reads a different world. No provenance = a run launched by an older script; the tags
# are cleared rather than guessed, and Agent_EditMode_CheckpointTags reports them.
$provenance = Join-Path $root "results\$RunId\provenance.json"
if (-not (Test-Path $provenance)) { $provenance = Join-Path $root "results\$RunId.provenance.json" }
if (Test-Path $provenance) {
    $prov = Get-Content $provenance -Raw | ConvertFrom-Json
    Set-ProfileField $profileAsset 'trainedInterfaceVersion' $prov.interfaceVersion
    Set-ProfileField $profileAsset 'trainedPhysicsVersion' $prov.physicsVersion
    Set-ProfileField $profileAsset 'trainingConfigHash' $prov.configHash
    $staminaValue = if ($prov.staminaModel -eq "human") { "1" } else { "0" }
    Set-ProfileField $profileAsset 'staminaModel' $staminaValue
    Set-ProfileField $profileAsset 'reactionLatencyMs' ("{0}" -f [double]$prov.reactionMs)
    Write-Host "  tags: $($prov.interfaceVersion) / $($prov.physicsVersion) / config $($prov.configHash)"
} else {
    Set-ProfileField $profileAsset 'trainedInterfaceVersion' ''
    Set-ProfileField $profileAsset 'trainedPhysicsVersion' ''
    Set-ProfileField $profileAsset 'trainingConfigHash' ''
    Write-Warning "No provenance.json for $RunId - checkpoint tags cleared (unknown provenance)."
}

# Re-link a profile whose brainModel was CLEARED (not missing) - the state an
# observation-contract change leaves it in (2026-09-28: 178 -> 188 inputs cleared
# STANDARD and MATT). The .onnx is overwritten in place, so its GUID is the one the
# profile pointed at before, but a cleared reference stays cleared: without this
# the new brain is deployed and silently never played. 5022602860645237092 is the
# ONNX importer's main-object fileID - every brainModel reference in this project
# carries it. A brand-new slot has no .meta until Unity imports it, and gets the
# one-time drag instructions printed above instead.
$metaPath = "$target.meta"
if ((Test-Path $profileAsset) -and (Test-Path $metaPath) -and
    (Select-String -Path $profileAsset -Pattern '^  brainModel: \{fileID: 0\}' -Quiet)) {
    $guidLine = Select-String -Path $metaPath -Pattern '^guid: ([0-9a-f]{32})' | Select-Object -First 1
    if ($guidLine) {
        $guid = $guidLine.Matches[0].Groups[1].Value
        Set-ProfileField $profileAsset 'brainModel' "{fileID: 5022602860645237092, guid: $guid, type: 3}"
        Write-Host "  re-linked: Reward_$Profile.asset brainModel -> $Profile.onnx (was cleared)"
    }
}
