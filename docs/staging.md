# Training staging manifest

What to train next, how to launch it, and how it is graded. The run-by-run record, with
every grade, is `docs/training-history.md`; read it before changing a lever here. Which
brain is deployed is in each `Reward_*.asset` (`trainingRunId`, `evalWinRate`).

All of this is the 173-input contract (105 ray + 68 vector, 2026-10-10). No brain trained
before that date loads against it.

## Status

STANDARD passed the benchmark on 2026-10-14 (`soccer_p37bot_standard`: 91.0% wins, 6.0%
stalemates over 2,000 episodes against bot v1).

**Staged 2026-11-07: MATT, KIM and NICK from scratch, in parallel** (not started; the
editor had no licence on this machine, so there is no player build yet).

- Why from scratch: the p31 checkpoints are not on this machine (there is no `results/`),
  and AGENTS.md now requires every character to train its own brain from scratch.
- Recipe: `config/TRAIN_<NAME>_p38.yaml` - p31's PPO block, 30M steps, and a
  `bot_strength` ladder that advances on training PROGRESS (0.0 drill -> 1.0 by the
  halfway mark) instead of on reward, which stalled in p31 and never promoted at all in
  p17/p18/p20. Then `config/TRAIN_<NAME>_p39bot.yaml`: 10M bot-only at full strength,
  continuing each profile's OWN p38, which is STANDARD's p34control..p37bot recipe.
- Launch (after a fresh build of SCN_Training):

```powershell
.\scripts\train-parallel.ps1 -Tag p38 -Profiles MATT,KIM,NICK          # ~7 h for the three
.\scripts\train-parallel.ps1 -Tag p39bot -Profiles MATT,KIM,NICK -InitFromTag p38
```

  Each launch runs `scripts/preflight.ps1` first, starts TensorBoard if it is not up,
  prints a wall-clock estimate, and writes `results/<run>.provenance.json` (interface and
  physics versions, config hash, git commit). `update-model.ps1` stamps those tags onto
  the profile when the run ends.

**Regrade the old MATT/KIM/NICK numbers before comparing against them.** Until
2026-11-07 `evaluate.ps1 -Profile <NAME>` did not set `POSOCCER_PROFILE` and nothing
swapped the eval model, so as the code stood it graded the brain serialized in
SCN_Training (STANDARD.onnx). How the p31 per-profile grades in
`docs/training-history.md` were produced is not recorded. `evaluate.ps1` now sets
`POSOCCER_PROFILE` and `Agent_EnvController.ApplyProfileOverride` puts the profile's
`brainModel` on the blue seat in eval mode; the eval JSON records `profile`.

Other candidates, none started: a 2v2 grade of p37bot (`evaluate.ps1 -Squad 2`; there is
no 2v2 bar, compare with the bot-vs-bot 2v2 baseline, never with the 1v1 figure).

## Training levers added 2026-11-07 (all off unless named)

| Lever | Where | Default | Notes |
|---|---|---|---|
| `action_rate` env parameter | trainer config `environment_parameters` | unset = profile's `actionJitterScale` (0.0004) | The AGENTS.md action-rate penalty. It already existed as the `jitter` term; a config can now set it like `turn_churn`. |
| `-StaminaModel human` | `train-phase1.ps1`, `train-parallel.ps1` (`POSOCCER_STAMINA_MODEL`) | `shipped` | ~6 s sprint, slow recovery, 20% repeated-sprint decrement. Physics tag gets `+stamina-human`. |
| `-ReactionMs <ms>` | same (`POSOCCER_REACTION_MS`) | 0 | Delays the learner's actions by whole physics ticks. Never applied to the bot. Interface tag gets `+lat<ms>`. |
| `-Profile <NAME>` | `train-phase1.ps1` | not set | Sets `POSOCCER_PROFILE` and deploys to that profile. |

A brain trained with a realism option must be played with it: `update-model.ps1` stamps
`staminaModel` and `reactionLatencyMs` onto the profile from the run's provenance, and
`Agent_EditMode_CheckpointTags` fails if a deployed brain's tags disagree with what the
runtime would apply. Movement probe now prints a `[SMOOTH]` line (action rate per
decision, RMS jerk) for the smoothness side of that question.

## The current recipe

`config/TRAIN_STANDARD_p37bot.yaml`: PPO, 1v1, bot v1 at full strength on every pitch, no
team terms, no self-play, 10M steps per run, warm-started from the previous run. It runs
at about 540 s per million steps at `-NumEnvs 4`.

```powershell
.\scripts\tensorboard.ps1
.\scripts\train-phase1.ps1 -RunId <run> -Config <config> -InitFrom <previous run> `
    -EnvPath Builds\PoSoccer\PoSoccer.exe -NumEnvs 4
# the script deploys over the profile's .onnx at the end; rebuild the player, then:
.\scripts\evaluate.ps1 -RunId <run> -Episodes 1000
```

## Checks during a run

- `PoSoccer/blue_squad` reads 1.0 on a 1v1 run and about 1.5 for `-Squad "1,2"`. On a
  squad run, 1.0 means the setting never reached the player: stop the run.
- League training (`-League`) was removed on 2026-11-07; p34control showed it added
  nothing. The code is in git history before that date.
- `Reward/turn_churn` is exactly 0 unless the config names the term.
- Mean reward is not a decision variable on this task. Judge on the eval and the probe.

## Gate for each brain

1. `Agent_PlayMode_MovementProbe`, blue trained side: it reaches the ball.
2. 1v1 eval against bot v1. The bar is at least 80% wins and at most 10% stalemates.
   Use 1,000 episodes once a brain is within about 5 points of either bar; at 350 the
   standard deviation is about 2.7 points.
3. Optional: bot v2 (`-BotVersion 2`, no bar) and 2v2 (`-Squad 2`, no bar).

## Configs

- `config/` holds the recipes behind deployed brains (`TRAIN_STANDARD_p37bot.yaml`,
  `TRAIN_<MATT|KIM|NICK>_p31.yaml`) and the staged ones (`TRAIN_<MATT|KIM|NICK>_p38.yaml`,
  `_p39bot.yaml`).
- `config/archive/` holds everything superseded, including the league, control and team
  recipes. The training scripts look in `config/` first and then in `config/archive/`, so
  an archived recipe still runs by name.
- Decision period 8 (12.5 Hz) and fixed timestep 0.01 s stay as they are. `threaded:
  false`; the `threaded: true` A/B (`archive/TRAIN_STANDARD_p28.yaml`) targets the old
  contract and needs porting before it can run.
