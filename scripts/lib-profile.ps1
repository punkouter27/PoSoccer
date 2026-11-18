# ---- The roster (2026-11-08) --------------------------------------------------
# One table for every script: profile name -> asset folder under Assets/Agents.
# STANDARD is the reference brain (benched from play, still trained and graded);
# the other 16 are the face characters (docs/players.md). Scripts take any name
# here; -Profile parameters use ValidatePattern and call Assert-Profile, because a
# ValidateSet would have to repeat this list in five places.
$PoSoccerProfiles = [ordered]@{
    STANDARD = "Standard_v01"; MATT = "Matt_v01"; KIM = "Kim_v01"; NICK = "Nick_v01"
    ALAN = "Alan_v01"; BAILY = "Baily_v01"; BRENDAN = "Brendan_v01"; DECOY = "Decoy_v01"
    DUPEE = "Dupee_v01"; JH = "Jh_v01"; JOSH = "Josh_v01"; MANZI = "Manzi_v01"
    MAYA = "Maya_v01"; PORTO = "Porto_v01"; SOKOL = "Sokol_v01"; TONG = "Tong_v01"
    VELEZ = "Velez_v01"
}

function Assert-Profile([string]$Name) {
    if (-not $PoSoccerProfiles.Contains($Name)) {
        throw "Unknown profile '$Name'. Known: $($PoSoccerProfiles.Keys -join ', ')"
    }
}

function Get-ProfileFolder([string]$Name) {
    Assert-Profile $Name
    return $PoSoccerProfiles[$Name]
}

# Base port for a profile's trainer. The original four keep the ports their logs and
# docs name; the rest get a 20-port block each from 5100, so any three can run side
# by side with -NumEnvs up to 19 without colliding.
function Get-ProfilePort([string]$Name) {
    $legacy = @{ STANDARD = 5005; MATT = 5015; KIM = 5025; NICK = 5045 }
    if ($legacy.ContainsKey($Name)) { return $legacy[$Name] }
    Assert-Profile $Name
    $index = [array]::IndexOf(@($PoSoccerProfiles.Keys), $Name)
    return 5100 + 20 * $index
}

# Shared helper: stamp a scalar field onto a Reward_Settings .asset (YAML text).
#
# Unity serializes ScriptableObjects as flat YAML, and a field that is absent from
# the file simply keeps its C# initializer value. So writing a field means either
# replacing its existing line or inserting a new one - both at the 2-space
# indentation the MonoBehaviour block uses.
#
# Only scalar fields are supported (int / float / string). Values are written raw,
# so anything needing YAML quoting must arrive already quoted.

function Set-ProfileField {
    param(
        [Parameter(Mandatory = $true)][string]$AssetPath,
        [Parameter(Mandatory = $true)][string]$Field,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value
    )

    if (-not (Test-Path $AssetPath)) {
        Write-Warning "Set-ProfileField: no asset at $AssetPath"
        return
    }

    $lines = [System.Collections.Generic.List[string]](Get-Content $AssetPath)
    $pattern = "^  $([regex]::Escape($Field)):"
    $index = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match $pattern) { $index = $i; break }
    }

    if ($index -ge 0) {
        $lines[$index] = "  ${Field}: $Value"
    }
    else {
        # Anchor on playerName - it is the first personality field and always
        # present, so the insert lands inside the MonoBehaviour body.
        $anchor = -1
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^  playerName:') { $anchor = $i; break }
        }
        if ($anchor -lt 0) {
            Write-Warning "Set-ProfileField: $AssetPath has no playerName anchor; skipped $Field"
            return
        }
        $lines.Insert($anchor + 1, "  ${Field}: $Value")
    }

    # LF, no BOM, trailing newline: the bytes Unity writes. Set-Content on Windows wrote
    # CRLF, so every stamp turned the whole asset into a line-ending diff until Unity
    # re-serialized it.
    [System.IO.File]::WriteAllText((Resolve-Path $AssetPath).Path,(($lines -join "`n") + "`n"),
                                   (New-Object System.Text.UTF8Encoding($false)))
}

# ---- Checkpoint provenance (2026-11-07) --------------------------------------
# AGENTS.md: "Tag each checkpoint with its interface and physics versions." The
# training scripts write results\<runId>.provenance.json when a run starts (a FILE,
# not a folder: tensorboard.ps1 prunes result folders that hold no events) and copy
# it into results\<runId>\ when the trainer exits. update-model.ps1 stamps it onto
# the profile; Agent_EditMode_CheckpointTags compares the stamp with the runtime.

# First 12 hex of the SHA-256 of a text file with line endings normalised to LF, so a
# CRLF checkout and an LF checkout of the same config hash the same.
function Get-ConfigHash {
    param([Parameter(Mandatory = $true)][string]$Path)
    $text = (Get-Content $Path -Raw) -replace "`r`n", "`n"
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash($bytes) } finally { $sha.Dispose() }
    return (($hash | ForEach-Object { $_.ToString("x2") }) -join "").Substring(0, 12)
}

# Reads a `public const string NAME = "value";` from Agent_Realism.cs. Read from the
# SOURCE, like ActionGain in update-model.ps1: a copy in a script is how a stamp drifts.
function Get-RealismConstant {
    param([Parameter(Mandatory = $true)][string]$Root, [Parameter(Mandatory = $true)][string]$Name)
    $source = Join-Path $Root "Assets\Scripts\Agents\Agent_Realism.cs"
    $m = Select-String -Path $source -Pattern ("public\s+const\s+string\s+" + [regex]::Escape($Name) + "\s*=\s*""([^""]+)""") |
         Select-Object -First 1
    if (-not $m) { throw "Agent_Realism.$Name not found in $source" }
    return $m.Matches[0].Groups[1].Value
}

# Same formulas as Agent_Realism.InterfaceVersionFor / PhysicsVersionFor.
function Get-InterfaceVersion {
    param([string]$Root, [double]$ReactionMs = 0)
    $base = Get-RealismConstant $Root "INTERFACE_VERSION"
    $ms = [int][Math]::Round([Math]::Min([Math]::Max($ReactionMs, 0), 400), [MidpointRounding]::ToEven)
    if ($ms -le 0) { return $base }
    return "$base+lat$ms"
}
function Get-PhysicsVersion {
    param([string]$Root, [string]$StaminaModel = "shipped")
    $base = Get-RealismConstant $Root "PHYSICS_VERSION"
    if ($StaminaModel -eq "human") { return "$base+stamina-human" }
    return $base
}

# Writes results\<runId>.provenance.json and returns its path.
function Write-RunProvenance {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RunId,
        [Parameter(Mandatory = $true)][string]$ConfigPath,
        [string]$Profile = "",
        [string]$InitFrom = "",
        [string]$StaminaModel = "shipped",
        [double]$ReactionMs = 0,
        [hashtable]$Extra = @{}
    )
    $commit = (git -C $Root rev-parse --short HEAD 2>$null)
    $dirty = [bool](git -C $Root status --porcelain 2>$null)
    $record = [ordered]@{
        runId            = $RunId
        profile          = $Profile
        config           = $ConfigPath.Replace($Root + "\", "")
        configHash       = Get-ConfigHash $ConfigPath
        interfaceVersion = Get-InterfaceVersion $Root $ReactionMs
        physicsVersion   = Get-PhysicsVersion $Root $StaminaModel
        staminaModel     = $StaminaModel
        reactionMs       = $ReactionMs
        initFrom         = $InitFrom
        gitCommit        = "$commit"
        gitDirty         = $dirty
        startedUtc       = (Get-Date).ToUniversalTime().ToString("o")
    }
    foreach ($key in $Extra.Keys) { $record[$key] = $Extra[$key] }
    $dir = Join-Path $Root "results"
    New-Item -ItemType Directory -Force $dir | Out-Null
    $path = Join-Path $dir "$RunId.provenance.json"
    $record | ConvertTo-Json | Set-Content -Path $path -Encoding UTF8
    return $path
}

# After the trainer exits: put the provenance next to the checkpoints it describes.
function Copy-RunProvenance {
    param([Parameter(Mandatory = $true)][string]$Root, [Parameter(Mandatory = $true)][string]$RunId)
    $src = Join-Path $Root "results\$RunId.provenance.json"
    $runDir = Join-Path $Root "results\$RunId"
    if ((Test-Path $src) -and (Test-Path $runDir)) {
        Copy-Item $src (Join-Path $runDir "provenance.json") -Force
    }
}

# Rough wall-clock estimate for a run, printed at startup (AGENTS.md: "Estimate training
# time at startup"). Measured rates (docs/training-history.md): ~540 s per million steps
# for one run at -NumEnvs 4 (p37bot), ~1,000 s per million each when four run in
# parallel (p31). Linear in between; an estimate, not a promise.
function Get-TrainingEstimate {
    param([Parameter(Mandatory = $true)][string]$ConfigPath, [int]$Concurrent = 1)
    $maxSteps = 0
    foreach ($line in (Get-Content $ConfigPath)) {
        if ($line -match '^\s*max_steps:\s*([0-9.eE+]+)') { $maxSteps = [Math]::Max($maxSteps, [double]$Matches[1]) }
    }
    if ($maxSteps -le 0) { return $null }
    $perMillion = 540 + (1000 - 540) * ([Math]::Min([Math]::Max($Concurrent, 1), 4) - 1) / 3.0
    $seconds = $maxSteps / 1e6 * $perMillion
    return [pscustomobject]@{
        Steps     = [long]$maxSteps
        Seconds   = [long]$seconds
        Text      = ("{0:N0} steps at ~{1:N0} s per million = ~{2:N1} h" -f $maxSteps, $perMillion, ($seconds / 3600))
        FinishUtc = (Get-Date).ToUniversalTime().AddSeconds($seconds)
    }
}
