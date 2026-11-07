# Training-artifact purge and disk report (UU_AGENT section 4 "Cleanup").
#
#   .\scripts\purge-artifacts.ps1            # dry run: lists what WOULD go, and the disk report
#   .\scripts\purge-artifacts.ps1 -Apply     # actually delete
#   .\scripts\purge-artifacts.ps1 -Report    # disk report only, nothing considered for deletion
#
# What counts as purgeable (all three AGENTS.md rules, nothing else):
#   - a FAILED run: a results/<run> folder with no checkpoint (.pt) and no exported .onnx;
#   - a SHORT test: a run whose newest checkpoint is below -MinSteps (default 5,000);
#   - an UNNEEDED checkpoint: inside a kept run, every <Behavior>-<step>.pt/.onnx except
#     the newest -KeepCheckpoints per behaviour (the run's final <Behavior>.onnx always stays).
#
# What is NEVER touched, whatever the rules above say:
#   - any run written to in the last 24 h (newest file mtime, not the folder's);
#   - any run a deployed brain names (`trainingRunId` in Assets/Agents/*/Reward_*.asset);
#   - any run named in a queue file (results/queue.txt, one run id per line) - a queue will resume it;
#   - anything tracked by git, and anything under Assets/ (the GUID-stable .onnx slots live there).
#
# Unity assets are only REPORTED, never deleted: a texture or clip whose GUID no scene,
# prefab, material, asset, stylesheet or template mentions, and whose file name no script
# mentions (Resources.Load and the code-built UI load by name). A low reference count does
# not prove an asset is dead - see CLAUDE.md "Method rules" - so a human decides.
param(
    [switch]$Apply,
    [switch]$Report,
    [int]$MinSteps = 5000,
    [int]$KeepCheckpoints = 2,
    [double]$RecentHours = 24
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$results = Join-Path $root "results"
$now = Get-Date

function Format-Size([long]$bytes) {
    if ($bytes -ge 1GB) { return "{0:N2} GB" -f ($bytes / 1GB) }
    if ($bytes -ge 1MB) { return "{0:N1} MB" -f ($bytes / 1MB) }
    return "{0:N0} KB" -f ($bytes / 1KB)
}

function Get-DirSize([string]$path) {
    if (-not (Test-Path $path)) { return 0L }
    $sum = (Get-ChildItem $path -Recurse -File -Force -ErrorAction SilentlyContinue |
            Measure-Object Length -Sum).Sum
    if ($sum) { return [long]$sum } else { return 0L }
}

# ---------------------------------------------------------------- disk report
Write-Host "== Disk report ($root) =="
$rows = foreach ($name in @("Library", "Builds", "Logs", "results", "summaries", ".venv", ".tooling", "Temp", "Assets")) {
    $p = Join-Path $root $name
    if (Test-Path $p) { [pscustomobject]@{ Folder = $name; Size = Get-DirSize $p } }
}
$rows | Sort-Object Size -Descending |
    ForEach-Object { Write-Host ("  {0,-10} {1,12}" -f $_.Folder, (Format-Size $_.Size)) }
$editorLog = Join-Path $env:LOCALAPPDATA "Unity\Editor\Editor.log"
if (Test-Path $editorLog) {
    Write-Host ("  Editor.log {0,12}  (a runaway log once grew ~200 MB/min - CLAUDE.md, manifest.json)" -f (Format-Size (Get-Item $editorLog).Length))
}
$drive = (Get-Item $root).PSDrive
Write-Host ("  Free on {0}: {1}" -f $drive.Name, (Format-Size ([long]$drive.Free)))

if ($Report) { return }

# ---------------------------------------------------------------- protected run ids
$protected = @{}
Get-ChildItem (Join-Path $root "Assets\Agents") -Recurse -Filter "Reward_*.asset" -ErrorAction SilentlyContinue |
    ForEach-Object {
        $m = Select-String -Path $_.FullName -Pattern '^\s*trainingRunId:\s*(\S+)' -ErrorAction SilentlyContinue
        if ($m) { $protected[$m.Matches[0].Groups[1].Value] = "deployed brain ($($_.Name))" }
    }
$queue = Join-Path $results "queue.txt"
if (Test-Path $queue) {
    Get-Content $queue | Where-Object { $_.Trim() -and -not $_.Trim().StartsWith("#") } |
        ForEach-Object { $protected[$_.Trim()] = "queued (results/queue.txt)" }
}

# Highest step number among <Behavior>-<step>.pt|.onnx checkpoints in a run.
function Get-RunSteps([System.IO.FileInfo[]]$files) {
    $max = -1L
    foreach ($f in $files) {
        if ($f.BaseName -match '-(\d+)$') {
            $s = [long]$Matches[1]
            if ($s -gt $max) { $max = $s }
        }
    }
    return $max
}

$candidates = New-Object System.Collections.Generic.List[object]

if (Test-Path $results) {
    foreach ($run in Get-ChildItem $results -Directory) {
        if ($run.Name -eq "_archive") { continue }
        $files = @(Get-ChildItem $run.FullName -Recurse -File -Force -ErrorAction SilentlyContinue)
        $newest = ($files | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
        $ageH = if ($newest) { ($now - $newest.LastWriteTime).TotalHours } else { [double]::MaxValue }

        if ($protected.ContainsKey($run.Name)) {
            Write-Host "  keep  $($run.Name): $($protected[$run.Name])"
            continue
        }
        if ($ageH -lt $RecentHours) {
            Write-Host ("  keep  {0}: written {1:N1} h ago (< {2} h)" -f $run.Name, $ageH, $RecentHours)
            continue
        }

        $ckpts = @($files | Where-Object { $_.Extension -in ".pt", ".onnx" })
        $steps = Get-RunSteps $ckpts
        $size = ($files | Measure-Object Length -Sum).Sum

        if ($ckpts.Count -eq 0) {
            $candidates.Add([pscustomobject]@{ Path = $run.FullName; Size = [long]$size; Why = "failed run (no checkpoint, no .onnx)" })
            continue
        }
        if ($steps -ge 0 -and $steps -lt $MinSteps) {
            $candidates.Add([pscustomobject]@{ Path = $run.FullName; Size = [long]$size; Why = "short test ($steps < $MinSteps steps)" })
            continue
        }

        # Kept run: thin intermediate checkpoints per behaviour folder.
        $byBehaviour = $ckpts | Where-Object { $_.BaseName -match '-(\d+)$' } | Group-Object DirectoryName
        foreach ($group in $byBehaviour) {
            $steps = $group.Group | ForEach-Object {
                [pscustomobject]@{ File = $_; Step = [long]($_.BaseName -replace '^.*-(\d+)$', '$1') }
            }
            $keepSteps = $steps | Select-Object -ExpandProperty Step -Unique |
                         Sort-Object -Descending | Select-Object -First $KeepCheckpoints
            foreach ($s in $steps) {
                if ($keepSteps -notcontains $s.Step) {
                    $candidates.Add([pscustomobject]@{ Path = $s.File.FullName; Size = [long]$s.File.Length; Why = "intermediate checkpoint ($($run.Name) step $($s.Step))" })
                }
            }
        }
    }
} else {
    Write-Host "  (no results/ folder - nothing to purge there)"
}

# Untracked .onnx / .pt outside results/ and outside Assets/ (stray exports).
$tracked = @{}
git -C $root ls-files -- "*.onnx" "*.pt" 2>$null | ForEach-Object { $tracked[(Join-Path $root $_)] = $true }
Get-ChildItem $root -Recurse -File -Include *.onnx, *.pt -ErrorAction SilentlyContinue |
    Where-Object {
        $_.FullName -notlike "$results*" -and
        $_.FullName -notlike (Join-Path $root "Assets") + "*" -and
        $_.FullName -notlike (Join-Path $root ".tooling") + "*" -and
        $_.FullName -notlike (Join-Path $root ".venv") + "*" -and
        $_.FullName -notlike (Join-Path $root "Library") + "*" -and
        -not $tracked.ContainsKey($_.FullName) -and
        ($now - $_.LastWriteTime).TotalHours -ge $RecentHours
    } |
    ForEach-Object { $candidates.Add([pscustomobject]@{ Path = $_.FullName; Size = [long]$_.Length; Why = "untracked stray checkpoint" }) }

Write-Host ""
Write-Host "== Purge candidates =="
if ($candidates.Count -eq 0) {
    Write-Host "  none"
} else {
    $candidates | ForEach-Object { Write-Host ("  {0,10}  {1}  [{2}]" -f (Format-Size $_.Size), $_.Path.Substring($root.Length + 1), $_.Why) }
}
$total = [long](($candidates | Measure-Object Size -Sum).Sum)

if ($Apply -and $candidates.Count -gt 0) {
    foreach ($c in $candidates) { Remove-Item -LiteralPath $c.Path -Recurse -Force }
    Write-Host ("Reclaimed {0}." -f (Format-Size $total))
} else {
    Write-Host ("Would reclaim {0}. Re-run with -Apply to delete." -f (Format-Size $total))
}

# ---------------------------------------------------------------- unreferenced Unity assets (report only)
Write-Host ""
Write-Host "== Possibly unreferenced textures / audio (report only, never deleted) =="
$assets = Join-Path $root "Assets"
$textExt = "*.unity", "*.prefab", "*.asset", "*.mat", "*.uss", "*.uxml", "*.spriteatlas", "*.spriteatlasv2", "*.controller", "*.anim", "*.physicsMaterial2D", "*.tss"
$corpus = New-Object System.Text.StringBuilder
Get-ChildItem $assets -Recurse -File -Include $textExt -ErrorAction SilentlyContinue |
    ForEach-Object { [void]$corpus.Append([System.IO.File]::ReadAllText($_.FullName)) }
$code = New-Object System.Text.StringBuilder
Get-ChildItem $assets -Recurse -File -Include *.cs -ErrorAction SilentlyContinue |
    ForEach-Object { [void]$code.Append([System.IO.File]::ReadAllText($_.FullName)) }
$corpusText = $corpus.ToString()
$codeText = $code.ToString()

$unref = 0
Get-ChildItem $assets -Recurse -File -Include *.png, *.jpg, *.wav, *.ogg, *.mp3, *.psd, *.tga, *.fbx, *.glb -ErrorAction SilentlyContinue |
    ForEach-Object {
        $meta = "$($_.FullName).meta"
        if (-not (Test-Path $meta)) { return }
        $guid = (Select-String -Path $meta -Pattern '^guid:\s*(\w+)').Matches[0].Groups[1].Value
        if ($corpusText.Contains($guid)) { return }
        if ($codeText.Contains($_.BaseName)) { return }
        $unref++
        Write-Host ("  {0,10}  {1}" -f (Format-Size $_.Length), $_.FullName.Substring($root.Length + 1))
    }
if ($unref -eq 0) { Write-Host "  none" }
