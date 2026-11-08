# Run one Unity test suite headless and print a summary.
#   .\scripts\unity-test.ps1 -Platform EditMode
#   .\scripts\unity-test.ps1 -Platform PlayMode [-Filter Agent_PlayMode_SmokeRun]
# Exit 0 = all passed, 1 = failures or ZERO tests ran, 2 = setup problem (no results file).
#
# The editor must be closed. A run that reports success with total 0 is a no-op, not a
# pass (CLAUDE.md "Tests"), so total 0 fails here.
param(
    [ValidateSet("EditMode", "PlayMode")]
    [string]$Platform = "EditMode",
    [string]$Filter = "",
    [string]$UnityExe = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot\lib-unity.ps1"

$UnityExe = Resolve-UnityExe -Root $root -UnityExe $UnityExe
Assert-ProjectClosed -Root $root

$assembly = "PoSoccer.$($Platform)Tests"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$resultsXml = Join-Path $root "Logs\tests-$Platform-$stamp.xml"
$log = Join-Path $root "Logs\tests-$Platform-$stamp.log"

# No -quit: -runTests exits by itself once the run finishes. No -nographics for PlayMode:
# several PlayMode tests read UI Toolkit layout, which needs a graphics device.
$unityArgs = @("-batchmode", "-projectPath", "`"$root`"", "-runTests",
               "-testPlatform", $Platform, "-assemblyNames", $assembly,
               "-testResults", "`"$resultsXml`"", "-logFile", "`"$log`"")
if ($Platform -eq "EditMode") { $unityArgs += "-nographics" }
if ($Filter) { $unityArgs += @("-testFilter", $Filter) }

Write-Host "Running $assembly headless (log: $log)"
$exit = Invoke-UnityBatch -UnityExe $UnityExe -Arguments $unityArgs

if (-not (Test-Path $resultsXml)) {
    Write-Host "No results file - compile error or the project failed to open. See $log" -ForegroundColor Red
    Select-String -Path $log -Pattern "error CS\d+" -ErrorAction SilentlyContinue | Select-Object -First 10 | ForEach-Object { $_.Line }
    exit 2
}

[xml]$xml = Get-Content $resultsXml
$run = $xml.'test-run'
$total = [int]$run.total; $passed = [int]$run.passed; $failed = [int]$run.failed; $skipped = [int]$run.skipped
Write-Host ("{0}: total {1}, passed {2}, failed {3}, skipped {4}, {5:N0} s (Unity exit {6})" -f
            $Platform, $total, $passed, $failed, $skipped, [double]$run.duration, $exit)

if ($failed -gt 0) {
    $xml.SelectNodes("//test-case[@result='Failed']") | ForEach-Object {
        Write-Host "  FAIL $($_.fullname)" -ForegroundColor Red
        $msg = $_.failure.message.'#cdata-section'
        if ($msg) { Write-Host ("       " + ($msg -split "`n")[0]) }
    }
    exit 1
}
if ($total -eq 0) {
    Write-Host "Zero tests ran - that is a no-op, not a pass. Check -Filter and $log" -ForegroundColor Red
    exit 1
}
exit 0
