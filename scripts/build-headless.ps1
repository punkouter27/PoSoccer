# Produce the headless training build used by --env (no editor scripts needed:
# Unity's CLI -buildWindows64Player builds the scenes in Build Settings).
param(
    [string]$UnityExe = "",
    [string]$Output = "Builds\PoSoccer\PoSoccer.exe"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot\lib-unity.ps1"

$UnityExe = Resolve-UnityExe -Root $root -UnityExe $UnityExe
Assert-ProjectClosed -Root $root

$log = Join-Path $root "Logs\headless-build.log"
$outPath = Join-Path $root $Output
$dataDir = Join-Path (Split-Path $outPath -Parent) ((Split-Path $outPath -LeafBase) + "_Data")

# Baseline BOTH artifacts before the build. The freshness check below used to
# baseline only the .exe and then test the _Data/*.assets against that same
# timestamp - but Unity routinely rewrites the data files while leaving the .exe
# untouched, so once the data was newer than the .exe (which is the normal
# steady state) `$dataFresh` was permanently true and EVERY build reported
# success, including builds that never ran at all. That false green is how the
# 2026-08-05 player survived every phase-10 run: three training runs and three
# evals silently used a stale binary. Each artifact is now compared against its
# own prior timestamp.
function Get-NewestDataStamp($dir) {
    if (-not (Test-Path $dir)) { return [datetime]::MinValue }
    $newest = Get-ChildItem $dir -Filter "*.assets" -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($newest) { return $newest.LastWriteTimeUtc }
    return [datetime]::MinValue
}
$beforeExe  = if (Test-Path $outPath) { (Get-Item $outPath).LastWriteTimeUtc } else { [datetime]::MinValue }
$beforeData = Get-NewestDataStamp $dataDir

# Start-Process -Wait rather than the call operator: `&` has been observed
# returning before Unity's child process finished, so $LASTEXITCODE reported a
# failure on a build whose log ended with "Exiting batchmode successfully now!".
$exitCode = Invoke-UnityBatch -UnityExe $UnityExe -Root $root -Arguments @(
    "-batchmode", "-quit", "-projectPath", "`"$root`"",
    "-buildWindows64Player", "`"$outPath`"",
    "-logFile", "`"$log`""
)

# Trust the artifact over the exit code: a platform switch (e.g. after an
# Android build) makes Unity reimport everything and can muddy the return value.
# Each artifact is compared against its OWN pre-build timestamp (see above).
$afterExe  = if (Test-Path $outPath) { (Get-Item $outPath).LastWriteTimeUtc } else { [datetime]::MinValue }
$afterData = Get-NewestDataStamp $dataDir

if (($afterExe -le $beforeExe) -and ($afterData -le $beforeData)) {
    throw ("Build produced no new artifact (exit $exitCode) - see $log. " +
           "exe $beforeExe -> $afterExe, data $beforeData -> $afterData")
}
if ($exitCode -ne 0) {
    Write-Warning "Unity returned $exitCode but the player artifact is fresh; continuing."
}
Write-Host "OK: headless training build at $Output (data assets $afterData UTC)"
