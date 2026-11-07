# Policy impact log, 2026-11-07 (items 41-50, realism and RL)

AGENTS.md section 3, "Before any change": list the policies each change touches, and
re-run every policy's regression scenarios afterwards. The four trained policies are
STANDARD, MATT, KIM, NICK (`Assets/Agents/*_v01/*.onnx`); the scripted bot (BOT,
`Agent_HeuristicBot` v1) is the benchmark opponent.

| # | Change | Policies touched on the DEFAULT path | Policies touched when switched on |
|---|---|---|---|
| 41 | `action_rate` env parameter overrides the existing `jitter` term's scale | none (unset = each profile's 0.0004, as before) | the policy being trained with it |
| 42 | `[SMOOTH]` line in `Agent_PlayMode_MovementProbe` (action rate per decision, RMS jerk) | none (measurement only) | - |
| 43 | Human repeated-sprint stamina (`Agent_Stamina.ApplyModel`, `POSOCCER_STAMINA_MODEL` / `Reward_Settings.staminaModel`) | none (Shipped returns without touching a field) | every policy trained or played under it; physics tag `+stamina-human` |
| 44 | `config/TRAIN_<MATT|KIM|NICK>_p38.yaml` and `_p39bot.yaml` | none until trained | MATT, KIM, NICK (each its own run, from scratch) |
| 45 | Checkpoint tags: `Agent_Realism` versions, `Reward_Settings.trainedInterfaceVersion / trainedPhysicsVersion / trainingConfigHash`, `provenance.json` per run, `Agent_EditMode_CheckpointTags` | none (metadata) | - |
| 46 | Already true: mass 75 x bodyScale^2, drive force scaled to mass. Pinned by a test; misleading `bodyMass` tooltip fixed | none | - |
| 47 | Reaction latency: learner actions delayed by whole physics ticks (`POSOCCER_REACTION_MS` / `Reward_Settings.reactionLatencyMs`) | none (no queue allocated at 0) | every learner policy trained or played under it; never the bot; interface tag `+lat<ms>` |
| 48 | Design only: `docs/plans/2026-11-07-articulated-legs-design.md` | none | all four policies and the bot (if ever built) |
| 49 | `train-parallel.ps1`: from scratch by default, `-InitFromTag` (own runs only), preflight, TensorBoard, estimate, provenance, realism switches | none | - |
| 50 | `scripts/preflight.ps1`, called by `train-phase1.ps1` and `train-parallel.ps1` | none | - |
| - | `evaluate.ps1 -Profile` now grades the profile's brain (`POSOCCER_PROFILE` + model swap on blue in eval mode) | grading of MATT, KIM, NICK (STANDARD unchanged: the scene already carries STANDARD.onnx) | - |

## Regression scenarios still owed

Nothing above has been compiled, run, built or trained: the Unity editor had no licence
on this machine on 2026-11-07. Before trusting any of it:

1. EditMode suite (`PoSoccer.EditModeTests`), checking `summary.total` went up by the 13
   new tests in `Agent_EditMode_Realism` and `Agent_EditMode_CheckpointTags`.
2. `Agent_PlayMode_MovementProbe` PROBE-D on STANDARD: the chase numbers must match the
   last recorded probe (the default path is meant to be bit-identical), plus the new
   `[SMOOTH]` line as the first smoothness baseline.
3. `evaluate.ps1 -RunId soccer_p37bot_standard -Profile STANDARD -Episodes 1000`: within
   noise of 91.0% / 6.0%. This is the check that the default path really is unchanged.
4. Regrade MATT, KIM, NICK with the fixed `evaluate.ps1` before p38 is compared to them.

## Version tags at this date

- Interface `iface-2026-10-10` (fingerprint `vec34x2|rays105|opp2|act4|gain1`).
- Physics `phys-2026-10-10` (SCN_Training drive, traction, turn, stamina fields, masses,
  materials, timestep 0.01 s, Agent_Contact defaults).
- All four deployed profiles stamped with both, and with the config hash of the recipe
  `docs/staging.md` names for them: STANDARD `79333c61e4e9` (p37bot), MATT `6dfb7210638c`,
  KIM `4e9cd9730b67`, NICK `25e6e074f203` (p31). Checked in git: no dynamics or interface
  change landed between the 173-input contract (2026-10-10) and their training dates.

## The other items in the same pass (1-3, 6, 11-40)

| Items | Change | Policies touched |
|---|---|---|
| 1, 2, 3, 6 | Passing lanes, momentum card, brain-cam saliency (own CPU worker, never drives the body), stamina rings | none: presentation only, gated off in training, eval and headless |
| 11-16, 20 | Purge script, `_Recovery` removed, naming test, dead methods removed (`Agent_Quality.StatusLine`, `Agent_BrainCam.ChurnOf`) | none |
| 17 | League training (`POSOCCER_OPPONENT=league`, `-League`) removed | none deployed (no deployed brain was trained under it); "league" now means self-play |
| 18-19, 31-39 | HUD declutter, settings tabs, overlay registry, build stamp, chrome corners | none: UI only |
| 21-30 | Turf detail, floodlight shadows, ball-spin swirl, divots, Ultra tier, impact synthesis, skid sound on `LateralSlipSpeed`, reverb, crowd xG | none: presentation only. The ball's Magnus curve already existed (`magnusScale`) and was not touched |
