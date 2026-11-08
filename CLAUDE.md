# CLAUDE.md

@AGENTS.md

Guidance for Claude Code in this repository. Slimmed 2026-10-10: the previous 100 KB file,
with every dated correction chain and the full reasoning behind each landmine, is preserved
verbatim in `docs/claude-md-archive-2026-10-10.md`. Read the matching section there before
re-litigating anything below.

**This file describes rules and durable traps. It does not describe state.** Which brains
are deployed, what is on disk, test counts and package versions all change without touching
this file, and every time this file tried to record them it drifted. Check the source:

| Question | Source of truth |
|---|---|
| Editor version | `ProjectSettings/ProjectVersion.txt` |
| Packages | `Packages/manifest.json`, `Packages/packages-lock.json` |
| Deployed brains, run ids, grades | the `Reward_*.asset` files (`trainingRunId`, `evalWinRate`, ...) |
| Toolchain present? | `Test-Path .venv`, `.tooling\ml-agents`, `Builds\PoSoccer` |
| Test counts | `summary.total` from an actual run |
| What each training phase tried | `docs/training-history.md` |
| What to train next, and how it is graded | `docs/staging.md` |

## What this is

PoSoccer: a top-down 2D physics soccer game and an **ML-Agents training benchmark**. Unity 6
(2D URP, mobile portrait) plus the Python `mlagents` trainer.

- **Benchmark**: train a brain that beats the rule-based bot in at least 80% of eval
  episodes with at most 10% stalemates
  (`docs/plans/2026-07-30-posoccer-v1-training-benchmark-design.md`).
- **Game**: `SCN_Menu` -> pick lineups from the roster -> exhibition match with scoreboard,
  first-to-5, replays, stadium lighting and sound.

## Hard rules (UNITY_AGENT / UU_AGENT)

`UU_AGENT` lives in the root `AGENTS.md`; read it alongside this section. PoSoccer uses
`mlagents`, so its own override switches off every MuJoCo / Newton / Isaac Lab line there.
Deliberate departures are recorded in `docs/rules-exemptions.md`; an audit finding not on
that page is a real defect.

- **Read the root `docs/` folder first.** It outranks inference from the code.
- **Git branch: `master` only.** Do not create other branches unless explicitly asked.
- **Never push unless explicitly asked.** Committing locally is fine. The phrase
  **`git sync`** counts as the ask (commit what is pending, pull, push).
- **Unity is driven only through the headless CLI** (since 2026-11-07; the CoplayDev,
  IvanMurzak and Unity-Skills MCP packages were removed). Scene, prefab and asset setup is
  an `Editor_` method in `Assets/Editor` run with `scripts/unity-exec.ps1`; never a text
  edit of `.unity` / `.prefab` YAML. Building things at runtime in gameplay code is fine
  and used heavily (see the exemption for why the global presentation components must
  stay code-installed). Do not reinstall an editor-bridge package without being asked.
- Script prefixes `Agent_` / `Sensor_` / `Reward_` (`Editor_` inside `Assets/Editor`, exempt);
  scenes `SCN_`; folder depth at most 2 under `Assets/`; agent assets in `<Name>_v<NN>`.
- **UI Toolkit only**, code-built, shared `PanelSettings` at 1080x1920 match-width. Fixed
  timestep 0.01 s. Portrait locked.
- **Version parity**: embedded `Packages/com.unity.ml-agents` must match the Python
  `mlagents` pinned in `requirements-training.txt`. `scripts/setup-training-env.ps1` builds
  `.venv` from that commit and fails loudly on drift. Python 3.10.x, at most 3.10.12.
- Trained `.onnx` overwrite **in place** (GUID-stable slot per personality:
  `Assets/Agents/<Name>_v01/<NAME>.onnx`). Kill orphaned trainers after runs.
- **Always start TensorBoard with training** (`.\scripts\tensorboard.ps1`, :6006), and prune
  obsolete runs first. Verify the script really archived them.
- **Always start the game from `SCN_Menu`.** Only the menu sets the `Agent_MatchSetup`
  statics; loading `SCN_Exhibition` directly plays a lineup nobody chose. Headless training
  and eval boot `SCN_Training` by design.
- **Agent colours.** The heuristic bot is always RED. The reference RL brain is always GREEN
  and untextured. Custom brains get user-supplied textures; never auto-assign one.
- **Roster shape.** One heuristic bot, one reference RL brain, and 0..n custom brains. The
  bot is the benchmark opponent and is never removed. Since 2026-10-11 it is not SELECTABLE:
  the menu lists only RL-trained brains (exemption recorded); the bot remains the training
  and grading opponent.
- **Creatures move realistically**: Earth gravity, real joint ranges, mass scaled to size,
  drive force scaled to mass. The top-down plane is the one standing exemption; gravity
  enters through the `mu * m * g` traction budget.
- **Observability.** Unity ML-Agents runs stay headless; the substitutes are TensorBoard and
  `Agent_PlayMode_MovementProbe`, and both are mandatory.
- Android/MuJoCo builds use https://github.com/joanllobera/mujoco-bin/ (not applicable to
  this project's `mlagents` pipeline).
- **TLDR rule.** Any answer over about 100 words ends with a `TLDR:` line of about 20 words.

## Commands

```powershell
# One-time, or after any parity bump. PowerShell execution policy blocks the scripts:
# run them as  pwsh -ExecutionPolicy Bypass -File scripts\<name>.ps1
.\scripts\setup-training-env.ps1 [-Force]

.\scripts\tensorboard.ps1                      # :6006; keeps newest 3 runs
.\scripts\train-phase1.ps1 -RunId <run> -EnvPath Builds\PoSoccer\PoSoccer.exe -NumEnvs 4 `
    [-Config <yaml in config/>] [-Squad "1,2"] [-InitFrom <run>] [-Resume]
.\scripts\evaluate.ps1 -RunId <run> -Episodes 350 [-Squad 2]   # exit 0 pass / 1 fail / 2-3 setup
.\scripts\evaluate.ps1 -Baseline -Episodes 350                 # bot vs bot sanity
.\scripts\evaluate.ps1 -Baseline -BlueBotVersion 2 -Episodes 350   # bot v2 against the benchmark bot
.\scripts\train-parallel.ps1 ...               # MATT/KIM/NICK side by side; see docs/staging.md
.\scripts\preflight.ps1 -EnvPath Builds\PoSoccer\PoSoccer.exe   # stale build, scene order, parity, LFS, orphans
.\scripts\update-model.ps1 -RunId <run> [-Profile STANDARD|MATT|KIM|NICK]
.\scripts\cleanup-training.ps1                 # kill orphaned trainers and env players
.\scripts\purge-artifacts.ps1 [-Apply|-Report] # dry-run purge of failed/short runs + disk report
```

**Builds**: `.\scripts\build-headless.ps1` (Build Settings scenes, `SCN_Training` at index
0, output `Builds/PoSoccer/PoSoccer.exe`). Never judge a build by the `.exe` mtime; the
script compares `PoSoccer_Data/*.assets` against its own pre-build stamp. Rebuild after
every `update-model.ps1` or you grade stale weights.

**Tests**: `.\scripts\unity-test.ps1 -Platform EditMode` and `-Platform PlayMode`
(`-Filter <name>` to narrow). Exit 0 pass, 1 failures or **zero tests ran** (a `total 0`
run is a no-op, not a pass), 2 no results file (compile error; the script prints the
first `error CS` lines). Results XML and log land in `Logs/tests-*`.

## Headless Unity CLI

Three scripts share `scripts/lib-unity.ps1` (editor path from `ProjectVersion.txt`, lock
check, `Start-Process -Wait`): `unity-test.ps1`, `unity-exec.ps1 -Method <Ns.Class.Method>
[-BuildTarget X]`, `build-headless.ps1`. The Android and WebGL builds have their own
scripts (`build-android-aab.ps1`, `Tools/ci_webgl.ps1`).

- **The editor must be closed.** Batchmode cannot open a project an editor holds; Unity
  says so on the console only and exits 1 with nothing in `-logFile`. The scripts check
  first and name the PID.
- **An `-executeMethod` method reports through `EditorApplication.Exit(code)`.** A method
  that returns normally exits 0 under `-quit` whatever it logged.
- **Trust the artifact or results file, not Unity's exit code.** A platform switch
  reimports everything and muddies the return value.
- **Observe play-mode behaviour with PlayMode tests**, not by watching an editor. Every
  screen is UI Toolkit on an overlay: tests walk `UIDocument.rootVisualElement` and read
  `worldBound` / `resolvedStyle`. PlayMode runs without `-nographics` for that reason.
- **`Packages/manifest.json` must be strict JSON.** A trailing comma makes the resolver
  return 500, the Package Manager logs a `FormatException` every frame and
  `Logs/Editor.log` grows by about 200 MB a minute (2026-10-10). Validate with a strict
  parser (`py -3.10 -c "import json; json.load(open(...))"`); PowerShell's
  `ConvertFrom-Json` accepts trailing commas.
- **Batchmode is not "no screen".** The PlayMode runner is batchmode WITH graphics.
  `Agent_Presentation.IsHeadless` checks only for a Null graphics device (training and
  eval pass `-nographics`). Gating on `Application.isBatchMode` once failed 13
  presentation tests, all reading a HUD that was never built (2026-11-08).
- **`WaitForEndOfFrame` never resumes under the headless runner**: a test yielding on
  it hangs the whole run. Use `ScreenCapture.CaptureScreenshot(path)` and `yield return null`.
- A collider added to a sprite-less object auto-sizes to 0.0001; set `size` explicitly.
- Overwriting a PNG in place can corrupt cached sprite geometry; use a new file name.
- `Assets/Editor/Editor_PlayModeOptionsGuard.cs` resets "Enter Play Mode Options" after test
  runs. That setting must stay off: the ML-Agents Academy needs the domain reload.
- Installing a Unity module while an editor is open breaks compilation until a restart.

## Architecture

**Brain contract** (enforced in `Agent_Soccer.Awake`): behaviour name from `brainName`,
**34 vector observations x 2 stacked = 68**, **three ray sensors = 105** (ball 39,
opponents 39, walls 27), **173 model inputs**, **4 continuous actions** (forward, lateral,
turn, boost), decision period 8. `Agent_EditMode_ObsContract` pins the numbers and
cross-checks every assigned `.onnx` and every scene-serialized `m_Model`.

- 2026-10-10: the goal ray sensor was removed (goal position is a vector observation) and
  ray sensors cast per physics layer (`Agent_Layers`): ball -> `Ball`, walls -> `Pitch`,
  opponents -> the opposing team layer (`PlayerBlue` / `PlayerRed`). A mask that includes the
  caster's own layer reports a hit on the caster. `Agent_PlayMode_RayLayers` measures it.
- **Changing any of this obsoletes every trained `.onnx`.** A shape change is the safe kind:
  the runtime refuses the model and fields the bot. The dangerous kind keeps the tensor
  shape and changes the meaning (sensor arc or range, the 2026-08-28 body-frame change,
  `ActionGain`, layers and masks): the model loads without a warning and reads a different
  world. Treat those as full retrains and never compare across them.
- A ScriptableObject asset never receives a changed C# field initializer.

**Physics**: 75 kg reference agent, traction-limited locomotion on one friction circle
(`mu * m * g`, mu 1.2), force scaled to mass. FIFA ball (r 0.11 m, 0.43 kg). Ball and
players use Continuous collision detection. Zero in-plane gravity.

**Episode flow**: `Reward_GoalTrigger` -> `Agent_EnvController.OnGoalScored / OnStalemate /
OnOutOfBounds` -> terminal rewards -> `EpisodeEnded` fires **before** reset (HUD, FX, audio
and the match log hang off it) -> `ResetPitch`. Random spawns and the step cap apply only
under `IsBoundedEpisode` (training and eval).

**Team play**: `POSOCCER_SQUAD` (`-Squad`) makes training pitches N-a-side at runtime.
`Agent_TeamRoles` assigns goalie / defender / attacker; brains observe their role. Shaping
terms `team_roles`, `team_spacing` and `turn_churn` are off unless a config names them.

**Bot versions**: `Agent_HeuristicBot.version` 1 is the benchmark bot and the default
everywhere. Version 2 (lead pursuit, bank shots) is opt-in through `POSOCCER_BOT_VERSION`
(`evaluate.ps1 -BotVersion 2`, `train-phase1.ps1 -BotVersion 2`). The 80% bar is defined
against version 1 only; an eval JSON stamps `botVersion` and a v2 grade never stamps a
profile.

**League training** was removed on 2026-11-07 (p34control: it added nothing). It is in git
history before that date.

**Realism options (2026-11-07)** are opt-in and default to the shipped behaviour bit for
bit, so every deployed `.onnx` reads the same world: `POSOCCER_STAMINA_MODEL=human`
(`Reward_Settings.staminaModel`) and `POSOCCER_REACTION_MS` (`reactionLatencyMs`). Turning
either on is a meaning change: a full retrain, never compared across. Checkpoints carry
`trainedInterfaceVersion` / `trainedPhysicsVersion` / `trainingConfigHash`
(`Agent_Realism`, `Agent_EditMode_CheckpointTags`); bump the version constants with any
contract or dynamics change. Articulated legs are a design only
(`docs/plans/2026-11-07-articulated-legs-design.md`).

**Run configuration**: every `POSOCCER_*` variable is named and read in `Agent_RunConfig`
(live reads, no caching). Eval mode is env-var driven and aggregated by `Agent_EvalStats`.

**Personalities**: a player is a `Reward_Settings` asset (reward DNA, name, colour,
`brainModel`). STANDARD, MATT, KIM, NICK, plus BOT whose `brainModel` is permanently null.
A null or incompatible brain fields `Agent_HeuristicBot`. See `docs/players.md`.

**Presentation**: `Agent_Presentation` installs the spectator layer in code.

- Components that own global state (clock, camera, scoreboard, pitch geometry, input) gate
  on `IsMatchScene` and **must stay code-installed**: `Agent_Gallery` clones the pitch root,
  so a serialized one would be multiplied by six. Per-pitch drawing components gate on
  `IsVisualScene` and are serialized on the pitch root in `SCN_Exhibition`.
  `Agent_EditMode_SceneComposition` pins both lists and that `SCN_Training` carries neither.
- `Agent_Overtime` moves walls and widens goals. If it ever reached training or eval it would
  inflate the benchmark's headline number with nothing in an eval JSON able to show it.
- `Agent_TimeFreeze` is the owner of the match clock: frozen gives 0, otherwise slow motion
  times `SpectatorSpeed` (0.25 to 4). Any static registry keyed on an instance-owned token
  needs a release in `OnDestroy`.
- Camera: `Agent_CameraFollow` priority is replay override > focus lock > director shot >
  ball follow. Cinemachine is not installed. The wide shot frames the pitch into the clear
  band between the HUD bands (`Agent_HUD.TopBandFraction` / `BottomBandFraction`), not the
  viewport; both goal mouths were once hidden under the HUD.
- Overlays are one mesh each (`Agent_Lines`), never a renderer per mark.
- `Agent_Palette` owns team colour. The win-probability strip always says MODEL.
- `Agent_Quality` sheds continuous cost before momentary meaning: wear, then bloom and the
  follow spot, then the goal overlays last.
- Any UV-space shader term must go through `PoSoccerLocalUV` (sprites are atlased).
- **Full-screen passes do not execute on this 2D renderer.** Do not retry without reading
  `docs/gfx-audio-pass-2026-09-06.md`.
- Kits ship switched off on every profile.

**UI**: default HUD is scoreboard, clock, chips and controls. Ticker, win-probability and
the ball-control meter sit behind one STATS switch (off by default). TAB (or F3, or the
DEBUG button) opens the health panel in `Agent_Telemetry`, which also holds the overlay
switches and speed buttons. Settings live in one bottom sheet (`Agent_SettingsSheet`) with
MATCH / VIEW / OVERLAYS / AUDIO tabs, opened from the SETTINGS button in the chrome's top
right on every screen. FPS shows only while the health panel is open. The bottom-right
label carries the `YYMMDD` build stamp (`Editor_BuildStamp` writes it at build time).
Touch targets use `--touch-min` (124 declared, 120 asserted; do not lower the assertion).

Overlays register once in `Agent_Overlays` and appear in the health panel, the OVERLAYS
tab, the HUD's VIEW cycler and **O**. A new overlay is one `Agent_Overlays.Register` line;
a new setting is one `Agent_SettingsSheet.RegisterSwitch` line.

Keys in a match: **Tab** health panel, **O** cycle overlays, **I** intent, **V** vision,
**T** shot tracer, **B** brain cam, **J** brain-cam saliency, **P** passing lanes,
**H** stamina rings, **X** xG field, **G** skid marks, **[ ]** speed, tap a player to focus,
pinch or scroll to zoom.

**Match overlays (2026-10-11)**: `Agent_ExpectedGoals` (xG per shot, per-side totals, the
threat field on **X**) is a hand-set model and says MODEL wherever it shows; it scores
itself with a Brier figure and appends every shot to `xg_shots.csv` for a later fit.
`Agent_Skids` (**G**) draws streaks from `Agent_Soccer.LateralSlipSpeed` and a measured g
from `GroundForce`. Both are code-installed, match only.

**Characters, voices, tournament (2026-11-08)**: 16 characters with faces
(`docs/players.md`; `Agent_Faces` draws the face with the chin toward the front), each
with a voice set of HAPPY / ANGRY / HURT / INSULT x 5 lines x 5 levels (`Agent_Voices`).
WAVs live in `Resources/Voices`; any missing sound is synthesised by `Agent_VoiceSynth`
(`docs/voices.md`). The menu's TOURNAMENT is a 16-slot bracket of trained characters
plus BYEs, auto-playable to a champion, with Elo for every 1 v 1 (`docs/tournament.md`).
`Agent_MatchIntro` shows the versus card at kickoff. Elo and bracket files are never
written under batchmode.

**Community brains**: `Agent_BrainHub` loads `.sentis` files from
`persistentDataPath/brains` into the menu roster and can fetch them from a Hugging Face
repo. The network client is compiled out of Android builds because the privacy policy
promises no network access. See `docs/brain-hub.md`.

**Diagnostics**: `Agent_ErrorSink` records every error with scene and clock;
`Agent_MatchLog` writes one CSV row per decision per player to `persistentDataPath`;
`Agent_PlayMode_SmokeRun` plays every lineup with an error budget of zero. Do not add an
ignore list to it.

**Assemblies**: `PoSoccer.Runtime` (`Assets/`), `PoSoccer.Editor.Build` (`Assets/Editor`),
`PoSoccer.EditModeTests`, `PoSoccer.PlayModeTests`.

## Method rules

- **Mean reward is not a decision variable on this task.** Three times a mid-training curve
  pointed the opposite way from the graded outcome. Judge on `evaluate.ps1` and the probe.
- **A 100-episode eval cannot tell 16% from 24%.** Never rank on single small runs.
- **Run `Agent_PlayMode_MovementProbe` on every trained brain** before any win-rate theory;
  it separates "bad at soccer" from "cannot move". Use the blue-trained-side probe.
- A reward-table change makes mean reward incomparable across runs.
- **"This failure is pre-existing" needs a control run**, not a matching comment.
- A low cross-reference count does not mean a component is dead: the installer adds each
  with one `AddComponent` line.
- A plausible mechanism plus a real symptom is not a diagnosis. Measure.
- Read `docs/training-history.md` before proposing a lever. Longer training, gamma 0.999,
  group credit with a group of one, curiosity and imitation have each been run and measured.

## Landmines

- **Git LFS.** Every `*.onnx`, `*.png`, `*.wav`, `*.ttf` is LFS-tracked. A clone without
  `git lfs install --local; git lfs pull` leaves ~130-byte pointer stubs: the game renders
  wrong, plays silent and fields bots with zero console errors. Seen three times.
  `Agent_EditMode_BinaryAssets` detects it by pointer header.
- **Stale player build.** Training and eval run whatever is baked into the build. An old
  build silently ignores new env parameters and squads. `evaluate.ps1` hard-fails on a stale
  build; on TensorBoard check `PoSoccer/blue_squad` and `Reward/turn_churn`.
- **Phase-1 training faces the bot only because `POSOCCER_OPPONENT=bot` is set** by the
  scripts. Without it both sides go to the trainer.
- **The training bottleneck is the single trainer thread**, not Unity physics or the GPU.
  Raising `time_scale` or env count buys nothing; the observation payload is a throughput
  term (`docs/ml-audit-2026-09-13.md`).
- **`TractionSaturation` cannot pass 0.59 on the shipped bodies** (519 N boosted drive
  against an 883 N budget), so "at the limit of grip" never fires: the AT THE LIMIT stat
  should read 0 and the intent overlay's arc should never close. Derived from the
  serialized values on 2026-10-11 (236 N x 2.2 over 1.2 x 75 x 9.81), not yet measured in
  a match and not fixed.
  Use `LateralSlipSpeed` or `GroundForce` for anything that should react to a hard cut.
  The skid voice and the ImpactFX scuffs were gated on it (0.85 / 0.9) and so never
  played; both moved to `LateralSlipSpeed` on 2026-11-07.
- The scripted side skips its ray battery (`Agent_Soccer.IsSensorlessScriptedSide`); the
  three exclusions in that predicate are the whole contract.
- Headless players still play audio unless `Agent_Audio` bails out under batchmode.
- `Mean Group Reward: 0.000` in self-play is cancellation, not missing groups.
- **Protobuf**: never reintroduce `Google.Protobuf_Packed.dll`.
- **`com.unity.pipeline` breaks Roslyn project-wide.** Do not install it. Check the
  `compiler` field of `execute_code`: a silent downgrade to `codedom` is the tell.
- **Scene order in Build Settings**: `SCN_Training` stays index 0. A build with explicit
  `scenes` rewrites the list; re-assert it.
- **Android side-load vs Play config**: `androidUseCustomKeystore` and `buildAppBundle` are
  set for Play releases, so a generic Android build is unsigned-or-AAB. See the archive.
- `python` on PATH is the Microsoft Store stub. Use `py -3.10` or the full path.
- Do not raise `PYTHON_MAX`; pip enforces the upstream bound.
- `setup-training-env.ps1` can leave a hollow `.tooling/ml-agents` if the clone dies; inside
  it `git -C` resolves up to this repo and the clone looks real. Clone by hand first.
- The team colours are nearly isoluminant by design; the alternative palettes exist for
  that reason. Do not silently change the default.

## Coding rules

Style lives in `.claude/rules/` and loads automatically: `architecture.md` (aspirational;
the code is plain MonoBehaviour and ScriptableObject), `csharp-unity.md`, `performance.md`
(zero allocation in Update), `serialization.md` (`[FormerlySerializedAs]` on every rename),
`unity-specifics.md` (no `?.` on Unity objects, no coroutines).

## Android release

Full procedure, identity table, icons and the browser-only steps:
`docs/claude-md-archive-2026-10-10.md` ("Android release & Play internal testing"),
`docs/android-internal-testing.md`, `docs/play-console-submission.md`.

- Application id `com.punkoutersoftware.posoccer` is permanent. Bump `VERSION_CODE` in
  `Editor_ConfigureAndroidRelease` for every upload.
- Secrets live outside the repo in `C:/Users/punko/Downloads/PoSoccer-Release/`. Losing the
  upload key means losing the ability to update the app. Builds abort without a password
  (`POSOCCER_KEYSTORE_PASS` or the `.pass` file); nothing falls back to the debug keystore.
- The shipped scene list is `Editor_BuildAndroidAAB.SHIP_SCENES` (menu, exhibition), not
  Build Settings.
- Menu: *PoSoccer -> Build Android AAB (Play release)*. Headless:
  `-executeMethod PoSoccer.EditorTools.Editor_BuildAndroidAAB.Build`, then grep the log for
  `AAB BUILD RESULT:`.
