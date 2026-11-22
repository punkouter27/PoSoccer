# Builds the PoSoccer WebGL player on THIS machine and publishes it as a GitHub Release. Publishing
# the release triggers .github/workflows/webgl.yml, which deploys the release's webgl.zip to the
# game's Azure Static Web App on a GitHub-hosted runner. No Unity licence is needed in CI.
#
#   pwsh -File scripts\publish_webgl.ps1               # EditMode tests, build, zip, publish release
#   pwsh -File scripts\publish_webgl.ps1 -SkipBuild    # publish the existing Builds/WebGL (keeps its stamp)
#   pwsh -File scripts\publish_webgl.ps1 -NoPublish    # tests, build and zip only
#   pwsh -File scripts\publish_webgl.ps1 -SkipTests    # skip the EditMode gate (not for releases)
#
# Close the Unity editor on this project first (a batch-mode Unity cannot open a project that is
# already open). The release is tagged at HEAD, so HEAD must already be pushed to origin.
# Build method, Static Web App and the /diag checks come from scripts/webgl.json.
#
# ONE VERSION STAMP: UTC yyMMdd, computed here once. It reaches the player through the
# PO_BUILD_VERSION environment variable (Editor_BuildStamp sets PlayerSettings.bundleVersion from
# it, so Application.version matches), the page (the template prints v{PRODUCT_VERSION}) and
# diag.html (written below). The script checks index.html really carries it.

param(
    [switch] $SkipBuild,
    [switch] $SkipTests,
    [switch] $NoPublish,
    [int] $Keep = 0          # >0: prune to the newest $Keep webgl-* releases; 0 (default) deletes nothing
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$cfg = Get-Content (Join-Path $PSScriptRoot 'webgl.json') -Raw | ConvertFrom-Json
$buildDir = Join-Path $root 'Builds\WebGL'
$zip = Join-Path $root 'Builds\webgl.zip'
$indexHtml = Join-Path $buildDir 'index.html'

$sha = (git rev-parse HEAD).Trim()
if (-not $NoPublish) {
    git fetch --quiet origin
    if (-not (git branch -r --contains $sha)) {
        throw "HEAD $($sha.Substring(0,7)) is not on origin. Push it first: the release is tagged at HEAD."
    }
    if (git status --porcelain --untracked-files=no) {
        Write-Warning 'Uncommitted changes: the build will include them, but the release is tagged at HEAD.'
    }
}

function Get-UnityExe {
    $want = ((Get-Content 'ProjectSettings\ProjectVersion.txt' | Select-String 'm_EditorVersion:') -split ':\s*')[1].Trim()
    $exe = "C:\Program Files\Unity\Hub\Editor\$want\Editor\Unity.exe"
    if (-not (Test-Path $exe)) { throw "Unity $want is not installed at $exe. Install exactly that version (with WebGL) in Unity Hub." }
    if (-not (Test-Path "C:\Program Files\Unity\Hub\Editor\$want\Editor\Data\PlaybackEngines\WebGLSupport")) {
        throw "Unity $want has no WebGL support module. Add it in Unity Hub."
    }
    return $exe
}

function Invoke-Unity([string[]] $arguments) {
    $p = Start-Process -FilePath $script:unity -PassThru -Wait -NoNewWindow -ArgumentList $arguments
    return $p.ExitCode
}

if ($SkipBuild) {
    if (-not (Test-Path $indexHtml)) { throw "No WebGL build at $buildDir." }
    $m = Select-String -Path $indexHtml -Pattern 'v(\d{6})<' | Select-Object -First 1
    if (-not $m) { throw "Builds/WebGL/index.html carries no vYYMMDD stamp; rebuild without -SkipBuild." }
    $stamp = $m.Matches[0].Groups[1].Value
}
else {
    $stamp = (Get-Date).ToUniversalTime().ToString('yyMMdd')
    $script:unity = Get-UnityExe
    New-Item -ItemType Directory -Force (Join-Path $root 'Builds') | Out-Null

    if (-not $SkipTests) {
        $results = Join-Path $root 'Builds\editmode-results.xml'
        $tlog = Join-Path $root 'Builds\editmode-tests.log'
        if (Test-Path $results) { Remove-Item $results -Force }
        "EditMode tests first (log: $tlog)"
        # The local Unity MCP bridge otherwise reconnects mid-run and its auth error fails a random test.
        $env:CI = 'true'; $env:UNITY_MCP_KEEP_CONNECTED = 'false'
        $code = Invoke-Unity @('-batchmode', '-nographics', '-projectPath', "`"$root`"", '-runTests',
            '-testPlatform', 'EditMode', '-testResults', "`"$results`"", '-logFile', "`"$tlog`"")
        if (-not (Test-Path $results)) { throw "EditMode tests wrote no results (exit $code; compile error?). Log: $tlog" }
        [xml] $x = Get-Content $results
        $run = $x.'test-run'
        "EditMode: total $($run.total), passed $($run.passed), failed $($run.failed), skipped $($run.skipped)"
        if ([int]$run.failed -gt 0 -or [int]$run.total -eq 0) {
            $x.SelectNodes("//test-case[@result='Failed']") | ForEach-Object { "  FAIL $($_.fullname)" }
            throw 'EditMode tests failed; not building.'
        }
    }

    $log = Join-Path $root 'Builds\webgl-build.log'
    "Building WebGL v$stamp ($($cfg.buildMethod)); log: $log"
    $env:PO_BUILD_VERSION = $stamp
    try {
        $code = Invoke-Unity @('-batchmode', '-quit', '-projectPath', "`"$root`"", '-buildTarget', 'WebGL',
            '-executeMethod', $cfg.buildMethod, '-logFile', "`"$log`"")
    }
    finally { Remove-Item Env:PO_BUILD_VERSION -ErrorAction SilentlyContinue }
    if ($code -ne 0) {
        Get-Content $log -Tail 40
        throw "Unity build failed (exit $code). Full log: $log"
    }
    if (-not (Test-Path $indexHtml)) { throw "No WebGL build at $buildDir." }
    if (-not (Select-String -Path $indexHtml -Pattern "v$stamp<" -Quiet)) {
        throw "index.html does not carry v${stamp}: PlayerSettings.bundleVersion was not stamped."
    }
}

# Static Web Apps 404s .unityweb without this MIME config (and it maps /diag to diag.html).
Copy-Item (Join-Path $PSScriptRoot 'webgl\staticwebapp.config.json') $buildDir -Force

# /diag: every check is fetched live by the browser, nothing is written in as "Ready".
$bundle = Get-ChildItem (Join-Path $buildDir 'Build') -File | Where-Object { $_.Name -notlike '*.symbols.json*' } |
    ForEach-Object { [ordered]@{ section = 'Data'; name = "Build bundle: $($_.Name)"; url = "/Build/$($_.Name)"; expect = 'status' } }
$checks = @($cfg.diagChecks) + @($bundle) + @([ordered]@{ section = 'Services'; name = 'Game page (/index.html)'; url = '/index.html'; expect = 'status' })
$checksJson = ConvertTo-Json -InputObject $checks -Depth 5 -Compress
$title = $cfg.title
$diagHtml = @"
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>System status - $title</title>
<style>
:root{--bg:#0b0f17;--surface:#121826;--line:#232c3f;--text:#e6e9f2;--muted:#9aa3b5;--accent:#6366f1;--ok:#34d399;--warn:#fbbf24;--bad:#f87171}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 Inter,system-ui,sans-serif}
.bar{height:44px;display:flex;align-items:center;justify-content:space-between;padding:0 16px;background:var(--surface);border-bottom:1px solid var(--line)}
.bar a{color:var(--text);text-decoration:none;font-weight:600}.bar a.back{color:var(--muted);font-weight:500}
main{max-width:760px;margin:0 auto;padding:20px 16px 48px}h1{font-size:22px;margin:4px 0 16px}h2{font-size:13px;letter-spacing:.08em;text-transform:uppercase;color:var(--muted);margin:24px 0 8px}
#banner{padding:12px 16px;border-radius:10px;font-weight:600;background:var(--surface);border:1px solid var(--line)}
#banner.ok{border-color:var(--ok);color:var(--ok)}#banner.warn{border-color:var(--warn);color:var(--warn)}#banner.bad{border-color:var(--bad);color:var(--bad)}
.card{background:var(--surface);border:1px solid var(--line);border-radius:10px;padding:10px 14px;display:flex;gap:12px;justify-content:space-between;align-items:center;margin-bottom:8px}
.card .n{overflow-wrap:anywhere}.card .s{white-space:nowrap;font-weight:600}.s.ok{color:var(--ok)}.s.bad{color:var(--bad)}.s.wait{color:var(--muted)}
.note{color:var(--muted);font-size:13px}footer{position:fixed;left:0;right:0;bottom:0;height:20px;padding:0 10px;text-align:right;font-size:11px;line-height:20px;color:var(--muted);background:var(--bg)}
</style></head>
<body><div class="bar"><a href="/">$title</a><a class="back" href="/">Back to the game</a></div>
<main><h1>System status</h1><div id="banner" class="wait">Checking...</div><div id="sections"></div>
<p class="note">Every row is fetched live from this browser when the page opens (latency in ms). Build v$stamp.</p></main>
<footer>v$stamp</footer>
<script>
const CHECKS = $checksJson;
CHECKS.push({section:'Services',name:'3D renderer (WebGL2 in this browser)',webgl2:true});
CHECKS.push({section:'Services',name:'Deployed commit (X-Commit header)',xcommit:true});
const order=['API','Data','Services'];const root=document.getElementById('sections');const rows=[];
for(const sec of order){const items=CHECKS.filter(c=>c.section===sec);const h=document.createElement('h2');h.textContent=sec;root.appendChild(h);
 if(!items.length){const p=document.createElement('p');p.className='note';p.textContent=sec==='API'?'The game calls no API.':'None.';root.appendChild(p);}
 for(const c of items){const d=document.createElement('div');d.className='card';d.innerHTML='<span class="n"></span><span class="s wait">checking</span>';d.firstChild.textContent=c.name;root.appendChild(d);rows.push([c,d.lastChild]);}}
async function probe(c){if(c.webgl2){return {ok:!!document.createElement('canvas').getContext('webgl2'),text:null};}
 if(c.xcommit){const t0=performance.now();try{const r=await fetch('/',{cache:'no-store',method:'HEAD'});const x=r.headers.get('x-commit')||'';
 const ok=/^[0-9a-f]{7,40}$/.test(x);return {ok,text:(ok?x.slice(0,7):'Missing')+' · '+Math.round(performance.now()-t0)+' ms'};}
 catch(e){return {ok:false,text:'unreachable'};}}
 const t0=performance.now();try{const json=c.expect==='json';
 let r=await fetch(c.url,{cache:'no-store',method:json?'GET':'HEAD'});
 if(!json&&(r.status===405||r.status===501))r=await fetch(c.url,{cache:'no-store'});
 let ok=r.ok;
 if(ok&&json){const j=await r.json();ok=!c.jsonKey||j[c.jsonKey]!==undefined;}
 return {ok,text:(ok?'OK':'HTTP '+r.status)+' · '+Math.round(performance.now()-t0)+' ms'};}
 catch(e){return {ok:false,text:'unreachable · '+Math.round(performance.now()-t0)+' ms'};}}
Promise.all(rows.map(async([c,el])=>{const r=await probe(c);el.className='s '+(r.ok?'ok':'bad');el.textContent=r.text||(r.ok?'Available':'Unavailable');return [c,r.ok];})).then(res=>{
 const b=document.getElementById('banner');const failed=res.filter(x=>!x[1]);const core=failed.some(x=>x[0].section==='Data'||x[0].webgl2);
 b.className=failed.length?(core?'bad':'warn'):'ok';b.textContent=failed.length?(core?'Down: the game cannot load here':'Degraded: '+failed.map(x=>x[0].name).join(', ')):'All systems operational';});
</script></body></html>
"@
[IO.File]::WriteAllText((Join-Path $buildDir 'diag.html'), $diagHtml)
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $buildDir '*') -DestinationPath $zip -CompressionLevel Optimal
'{0}: {1:N1} MB (v{2})' -f $zip, ((Get-Item $zip).Length / 1MB), $stamp

if ($NoPublish) { 'Built and zipped; not published (-NoPublish).'; return }

$repo = (gh repo view --json nameWithOwner --jq .nameWithOwner).Trim()
$when = (Get-Date).ToUniversalTime().ToString('yyMMdd-HHmm')
$tag = "webgl-$when-$($sha.Substring(0,7))"
gh release create $tag $zip -R $repo --target $sha --title "WebGL v$stamp ($when)" --latest=false `
    --notes "Local WebGL build v$stamp of $($sha.Substring(0,7)). Publishing this release deploys it to Azure (.github/workflows/webgl.yml)."
if ($LASTEXITCODE -ne 0) { throw "gh release create failed ($LASTEXITCODE)." }
"PUBLISHED $tag  ->  deploy: https://github.com/$repo/actions/workflows/webgl.yml"

# Releases are kept unless -Keep N asks to prune to the newest N (and their tags).
$old = gh release list -R $repo --limit 100 --json tagName,createdAt |
    ConvertFrom-Json | Where-Object { $_.tagName -like 'webgl-*' } |
    Sort-Object createdAt -Descending | Select-Object -Skip $Keep
if ($Keep -gt 0) {
    foreach ($r in $old) { gh release delete $r.tagName -R $repo --yes --cleanup-tag; "pruned $($r.tagName)" }
}
