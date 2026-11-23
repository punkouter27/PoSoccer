# Opens the training Benchmark page (benchmark/page.html) for this repo: one button runs ~10 s of real mlagents-learn
# training (the p50 roster recipe, Training/TRAIN_ALAN_p50.yaml, against the bot, 4 headless env players) and records
# step time, machine and machine load in benchmark/history.json (commit it to compare laptops). The first run on a
# machine builds Builds/PoSoccer with scripts/build-headless.ps1, so the Unity Editor must be closed.
# -Run: no page; runs one Benchmark in this console, records it in benchmark/history.json and exits 0 (recorded) or 1.
$ErrorActionPreference = 'Stop'
$py = Join-Path $PSScriptRoot '.venv/Scripts/python.exe'
if (-not (Test-Path $py)) {
    Write-Host 'Creating .venv (scripts/setup-training-env.ps1: mlagents pinned to the C# package commit)...'
    & (Join-Path $PSScriptRoot 'scripts/setup-training-env.ps1')
}
& $py (Join-Path $PSScriptRoot 'benchmark/serve.py') @args
exit $LASTEXITCODE
