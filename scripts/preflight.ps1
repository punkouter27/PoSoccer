# Pre-flight check before any training run (2026-11-07). Every check here is a failure
# this project has actually shipped at least once (see CLAUDE.md "Landmines"):
#
#   1. Stale player build    - training and eval run what is baked into the build.
#   2. Scene order           - SCN_Training must stay index 0 in Build Settings.
#   3. Toolchain parity      - .venv mlagents == the pin, C# package == the pin, Python <= max.
#   4. Git LFS pointer stubs - a clone without `git lfs pull` fields bots silently.
#   5. Orphaned trainers     - a leftover mlagents-learn or env player holds the ports.
#
#   .\scripts\preflight.ps1 -EnvPath Builds\PoSoccer\PoSoccer.exe
#   .\scripts\preflight.ps1 -InEditor               # no player build involved
#
# Exit 0 = all clear, 1 = at least one blocking failure. Warnings never fail.
# Called by train-phase1.ps1 and train-parallel.ps1 before they start a trainer.
param(
    [string]$EnvPath = "Builds\PoSoccer\PoSoccer.exe",
    # Training in the editor (train-phase1 without -EnvPath): skip the build checks.
    [switch]$InEditor,
    # Grade or train a build older than Assets anyway. Deliberately awkward to reach.
    [switch]$AllowStale,
    # The launcher of a parallel run checks once, before its own workers exist.
    [switch]$AllowRunningTrainers
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$failures = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]

function Pass([string]$msg) { Write-Host "  ok    $msg" -ForegroundColor Green }

Write-Host "Preflight ($root)"

# ---- 1. Player build freshness ------------------------------------------------
if (-not $InEditor) {
    $exe = Join-Path $root $EnvPath
    $dataDir = Join-Path (Split-Path $exe -Parent) ((Split-Path $exe -LeafBase) + "_Data")
    if (-not (Test-Path $exe)) {
        $failures.Add("No player at $EnvPath. Build it (MCP manage_build, or scripts\build-headless.ps1 with the editor closed).")
    }
    elseif (-not (Test-Path (Join-Path $dataDir "Managed"))) {
        $failures.Add("$dataDir\Managed is missing: $EnvPath is not a complete player build.")
    }
    else {
        # Judge the build by its data files, never the .exe mtime (CLAUDE.md "Builds").
        $built = Get-ChildItem $dataDir -Filter "*.assets" -ErrorAction SilentlyContinue |
                 Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if (-not $built) {
            $failures.Add("No $dataDir\*.assets: the build is incomplete.")
        }
        else {
            # Editor scripts and tests never reach a player, so they cannot make it stale.
            $inputs = Get-ChildItem (Join-Path $root "Assets") -Recurse -File -Include *.cs, *.unity, *.asset, *.onnx, *.prefab, *.physicsMaterial2D -ErrorAction SilentlyContinue |
                      Where-Object { $_.FullName -notmatch '\\Assets\\(Editor|Tests)\\' }
            $newer = $inputs | Where-Object { $_.LastWriteTimeUtc -gt $built.LastWriteTimeUtc } |
                     Sort-Object LastWriteTimeUtc -Descending
            if ($newer) {
                $first = ($newer | Select-Object -First 3 | ForEach-Object { $_.FullName.Replace($root + "\", "") }) -join ", "
                $msg = "STALE BUILD: $($newer.Count) file(s) under Assets are newer than the player data " +
                       "($($built.LastWriteTimeUtc.ToString('yyyy-MM-dd HH:mm')) UTC), e.g. $first. The run would " +
                       "train or grade old code and old weights. Rebuild, or pass -AllowStale."
                if ($AllowStale) { $warnings.Add($msg) } else { $failures.Add($msg) }
            }
            else {
                Pass "player data is newer than every Assets input ($($built.LastWriteTimeUtc.ToString('yyyy-MM-dd HH:mm')) UTC)"
            }
        }
    }

    # ---- 2. Scene order ---------------------------------------------------------
    $buildSettings = Join-Path $root "ProjectSettings\EditorBuildSettings.asset"
    $firstScene = Select-String -Path $buildSettings -Pattern '^\s+path:\s*(\S+\.unity)' |
                  Select-Object -First 1
    if (-not $firstScene) {
        $failures.Add("ProjectSettings\EditorBuildSettings.asset lists no scenes.")
    }
    elseif ($firstScene.Matches[0].Groups[1].Value -ne "Assets/Scenes/SCN_Training.unity") {
        $failures.Add("Build Settings index 0 is $($firstScene.Matches[0].Groups[1].Value), not " +
                      "Assets/Scenes/SCN_Training.unity. A build with explicit scenes rewrote the list; re-assert it and rebuild.")
    }
    else {
        Pass "SCN_Training is Build Settings index 0"
    }
}

# ---- 3. Toolchain parity ------------------------------------------------------
$reqFile = Join-Path $root "requirements-training.txt"
function Get-Anchor([string]$key) {
    $line = Select-String -Path $reqFile -Pattern "^#\s*$key=(.+)$" | Select-Object -First 1
    if ($line) { return $line.Matches[0].Groups[1].Value.Trim() }
    return $null
}
$expectedPy = Get-Anchor "ML_AGENTS_PY_VERSION"
$expectedCs = Get-Anchor "CSHARP_PACKAGE_VERSION"
$pythonMax  = Get-Anchor "PYTHON_MAX"

$csVersion = (Get-Content (Join-Path $root "Packages\com.unity.ml-agents\package.json") -Raw | ConvertFrom-Json).version
if ($csVersion -ne $expectedCs) {
    $failures.Add("PARITY: embedded com.unity.ml-agents is $csVersion, requirements-training.txt pins $expectedCs.")
} else {
    Pass "C# package com.unity.ml-agents $csVersion matches the pin"
}

$venv = if (Test-Path "$root\.venv2\Scripts\python.exe") { "$root\.venv2" } else { "$root\.venv" }
$py = Join-Path $venv "Scripts\python.exe"
if (-not (Test-Path $py)) {
    $failures.Add("No Python at $py. Run: pwsh -ExecutionPolicy Bypass -File scripts\setup-training-env.ps1")
}
else {
    $probe = & $py -c "import sys, importlib.metadata as m; print('%d.%d.%d' % sys.version_info[:3]); print(m.version('mlagents')); print(m.version('mlagents_envs'))" 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $probe -or $probe.Count -lt 3) {
        $failures.Add("$py cannot import mlagents. Re-run scripts\setup-training-env.ps1 -Force.")
    }
    else {
        $pyVer = $probe[0].Trim(); $mlVer = $probe[1].Trim(); $envVer = $probe[2].Trim()
        if ($mlVer -ne $expectedPy -or $envVer -ne $expectedPy) {
            $failures.Add("PARITY: .venv has mlagents $mlVer / mlagents_envs $envVer, the pin is $expectedPy.")
        } else {
            Pass "mlagents $mlVer in $(Split-Path $venv -Leaf) matches the pin"
        }
        if ($pythonMax -and ([version]$pyVer -gt [version]$pythonMax)) {
            $failures.Add("Python $pyVer in $(Split-Path $venv -Leaf) is above PYTHON_MAX $pythonMax.")
        } elseif (-not $pyVer.StartsWith("3.10.")) {
            $failures.Add("Python $pyVer in $(Split-Path $venv -Leaf): the project supports 3.10.x only.")
        } else {
            Pass "Python $pyVer"
        }
    }
}

# ---- 4. Git LFS pointer stubs -------------------------------------------------
# Same signature Agent_EditMode_BinaryAssets checks: a pointer file is ~130 bytes and
# starts with the LFS spec line.
$lfsTargets = Get-ChildItem (Join-Path $root "Assets") -Recurse -File -Include *.onnx, *.png, *.wav, *.ttf -ErrorAction SilentlyContinue |
              Where-Object { $_.Length -lt 1024 }
$stubs = @()
foreach ($file in $lfsTargets) {
    $head = Get-Content $file.FullName -TotalCount 1 -ErrorAction SilentlyContinue
    if ($head -and $head.StartsWith("version https://git-lfs")) { $stubs += $file.FullName.Replace($root + "\", "") }
}
if ($stubs.Count -gt 0) {
    $failures.Add("$($stubs.Count) Git LFS pointer stub(s), e.g. $($stubs[0]). Run: git lfs install --local; git lfs pull")
} else {
    Pass "no Git LFS pointer stubs among $(@($lfsTargets).Count) small binary files"
}

# ---- 5. Orphaned trainers and env players -------------------------------------
$trainers = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
              Where-Object { $_.CommandLine -match "mlagents-learn|mlagents\.trainers" })
$players = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
             Where-Object { $_.Name -eq "PoSoccer.exe" -and $_.CommandLine -match "-batchmode|--mlagents" })
if ($trainers.Count -gt 0 -or $players.Count -gt 0) {
    $msg = "$($trainers.Count) trainer(s) and $($players.Count) headless env player(s) are already running " +
           "(PIDs $((@($trainers) + @($players) | ForEach-Object { $_.ProcessId }) -join ', ')). They hold ports and CPU. " +
           "If nothing should be running: .\scripts\cleanup-training.ps1"
    if ($AllowRunningTrainers) { $warnings.Add($msg) } else { $failures.Add($msg) }
} else {
    Pass "no trainers or headless env players running"
}

# ---- verdict --------------------------------------------------------------------
foreach ($w in $warnings) { Write-Warning $w }
if ($failures.Count -gt 0) {
    foreach ($f in $failures) { Write-Host "  FAIL  $f" -ForegroundColor Red }
    Write-Host "Preflight FAILED ($($failures.Count) blocking)." -ForegroundColor Red
    exit 1
}
Write-Host "Preflight passed." -ForegroundColor Green
exit 0
