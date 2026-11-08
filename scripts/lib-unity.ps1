# Shared helpers for the headless Unity CLI pipeline (unity-test.ps1, unity-exec.ps1,
# build-headless.ps1). Dot-source:  . "$PSScriptRoot\lib-unity.ps1"
#
# Since 2026-11-07 this project has no editor bridge (the CoplayDev, IvanMurzak and
# Unity-Skills packages were removed). Every editor operation - tests, builds, scene
# and asset setup - runs as `Unity.exe -batchmode` against a CLOSED project.

function Resolve-UnityExe {
    param([string]$Root, [string]$UnityExe = "")
    if ($UnityExe) { return $UnityExe }
    # Derived from ProjectVersion.txt, never pinned: a pinned path broke silently on the
    # 6000.5 -> 6000.6 move (see build-headless.ps1).
    $versionFile = Join-Path $Root "ProjectSettings\ProjectVersion.txt"
    $version = (Select-String -Path $versionFile -Pattern '^m_EditorVersion:\s*(\S+)').Matches[0].Groups[1].Value
    $exe = "C:\Program Files\Unity\Hub\Editor\$version\Editor\Unity.exe"
    if (-not (Test-Path $exe)) {
        throw "Unity $version (from ProjectVersion.txt) is not installed at $exe. Install it via Unity Hub, or pass -UnityExe."
    }
    return $exe
}

# Batchmode cannot open a project an editor already holds: Unity prints the reason to the
# console only, exits 1 and leaves nothing in -logFile. Catch it before launching.
function Assert-ProjectClosed {
    param([string]$Root)
    $holder = Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction SilentlyContinue |
              Where-Object { $_.CommandLine -match [regex]::Escape($Root) -or
                             $_.CommandLine -match [regex]::Escape($Root.Replace('\', '/')) }
    if ($holder) {
        throw ("The Unity editor has this project open (PID $(($holder.ProcessId) -join ', ')). " +
               "Batchmode cannot open the same project - close the editor and re-run.")
    }
}

# Start-Process -Wait, not `&`: the call operator has returned before Unity's child
# process finished, so the exit code described a build that was still running.
function Invoke-UnityBatch {
    param([string]$UnityExe, [string[]]$Arguments)
    $proc = Start-Process -FilePath $UnityExe -Wait -PassThru -NoNewWindow -ArgumentList $Arguments
    return $proc.ExitCode
}
