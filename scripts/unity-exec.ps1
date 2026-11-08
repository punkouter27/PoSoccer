# Run a static editor method headless: the CLI replacement for the removed editor bridge.
# Scene, prefab and asset setup is written as an Editor_ method in Assets/Editor and run
# through this script.
#   .\scripts\unity-exec.ps1 -Method PoSoccer.EditorTools.Editor_BuildAndroidAAB.Build -BuildTarget Android
# The method should end with EditorApplication.Exit(code) when it has a result to report;
# otherwise -quit closes Unity after it returns. Exit code is Unity's.
param(
    [Parameter(Mandatory = $true)]
    [string]$Method,
    [string]$BuildTarget = "",
    [string[]]$Extra = @(),
    [string]$UnityExe = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot\lib-unity.ps1"

$UnityExe = Resolve-UnityExe -Root $root -UnityExe $UnityExe
Assert-ProjectClosed -Root $root

$leaf = ($Method -split '\.')[-2..-1] -join '.'
$log = Join-Path $root ("Logs\exec-{0}-{1}.log" -f $leaf, (Get-Date -Format "yyyyMMdd-HHmmss"))
$unityArgs = @("-batchmode", "-quit", "-projectPath", "`"$root`"", "-executeMethod", $Method, "-logFile", "`"$log`"")
if ($BuildTarget) { $unityArgs += @("-buildTarget", $BuildTarget) }
$unityArgs += $Extra

Write-Host "Running $Method headless (log: $log)"
$exit = Invoke-UnityBatch -UnityExe $UnityExe -Arguments $unityArgs
Select-String -Path $log -Pattern "error CS\d+|Exception" -ErrorAction SilentlyContinue |
    Select-Object -First 10 | ForEach-Object { Write-Host $_.Line }
Write-Host "Unity exit $exit"
exit $exit
