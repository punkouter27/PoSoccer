"""PoSoccer Workload: mlagents-learn on the p50 roster recipe (Training/TRAIN_ALAN_p50.yaml) against the scripted bot,
4 headless env players, as scripts/train-parallel.ps1 runs one profile's worker.

Run by serve.py with the training venv:  .venv/Scripts/python.exe benchmark/workload.py
Benchmark player: Builds/PoSoccer/PoSoccer.exe, built on first use by scripts/build-headless.ps1 (Unity Editor closed).

Every p50 config is the same recipe under a different behavior name, so one profile stands for all of them. The env
players get the worker's environment (POSOCCER_OPPONENT=bot, POSOCCER_PROFILE=ALAN, shipped stamina, no reaction
delay); they inherit it from this process.

Hooks: TrainerController.advance = one env-manager step (sim) followed by trainer.advance (learn: trajectory
processing and, once per full buffer, the PPO update). Warm-up ends after the first update; the window then ends
on a later update so it always holds whole collect-then-update cycles. A Step = every env player advancing once.

Deliberately NO TensorBoard: this is a ~10 s timing run that is never graded or deployed, and a TensorBoard process
would add load that skews the number. Its run dir results/_benchmark is throwaway (--force; results/ is gitignored).
"""

from __future__ import annotations

import datetime
import os
import subprocess
import sys
import time
from importlib import metadata
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import core  # noqa: E402

ROOT = Path(__file__).resolve().parents[1]
PROFILE = "ALAN"
CONFIG = ROOT / "Training" / f"TRAIN_{PROFILE}_p50.yaml"
PLAYER = ROOT / "Builds" / "PoSoccer" / "PoSoccer.exe"
NUM_ENVS = 4  # scripts/train-parallel.ps1 default
BASE_PORT = 5900  # clear of the per-profile trainer ports (5005-5045, 5100 + 20 per profile)
RUN_ID = "_benchmark"
WORKER_ENV = dict(
    POSOCCER_OPPONENT="bot",
    POSOCCER_SQUAD="",
    POSOCCER_PROFILE=PROFILE,
    POSOCCER_STAMINA_MODEL="shipped",
    POSOCCER_REACTION_MS="0",
)


def version(pkg: str) -> str | None:
    try:
        return metadata.version(pkg)
    except metadata.PackageNotFoundError:
        return None


def unity_version() -> str:
    text = (ROOT / "ProjectSettings" / "ProjectVersion.txt").read_text()
    return text.split("m_EditorVersion:")[1].split()[0]


def build_player() -> None:
    """Build the headless player with the repo's own script (first use only; needs the Unity Editor closed)."""
    core.say("building the headless player Builds/PoSoccer (first run on this machine, several minutes)")
    script = ROOT / "scripts" / "build-headless.ps1"
    proc = subprocess.run(
        ["pwsh", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script)],
        cwd=ROOT,
        capture_output=True,
        text=True,
        errors="replace",
    )
    if proc.returncode != 0 or not PLAYER.exists():
        tail = (proc.stderr or proc.stdout).strip().splitlines()[-1:] or ["see Logs/headless-build.log"]
        raise RuntimeError(f"headless player build failed: {tail[0]}")


def workload() -> dict:
    import yaml

    b = yaml.safe_load(CONFIG.read_text(encoding="utf-8"))["behaviors"][PROFILE]
    built = datetime.datetime.fromtimestamp(PLAYER.stat().st_mtime).isoformat(timespec="seconds")
    return dict(
        repo="PoSoccer",
        trainer=f"mlagents {version('mlagents')} (torch {version('torch')})",
        config=f"Training/{CONFIG.name}",
        config_hash=core.workload_id(b),
        num_envs=NUM_ENVS,
        env=" ".join(f"{k}={v}" for k, v in WORKER_ENV.items() if v),
        hyperparameters=b["hyperparameters"],
        net=[b["network_settings"]["hidden_units"]] * b["network_settings"]["num_layers"],
        time_horizon=b.get("time_horizon"),
        unity=unity_version(),
        player_built=built,
    )


def main() -> None:
    from mlagents.trainers import learn
    from mlagents.trainers.trainer.on_policy_trainer import OnPolicyTrainer
    from mlagents.trainers.trainer.rl_trainer import RLTrainer
    from mlagents.trainers.trainer_controller import TrainerController

    os.environ.update(WORKER_ENV)
    meter = None
    try:
        if not PLAYER.exists():
            build_player()
        core.T_PROCESS = time.time()  # Startup time excludes the one-time player build
        versions = dict(python=sys.version.split()[0], torch=version("torch"), mlagents=version("mlagents"))
        meter = core.Meter(workload(), versions, warmup_blocks=None)
    except Exception as e:  # noqa: BLE001 - every failure becomes a failed row
        meter = meter or core.Meter({"repo": "PoSoccer", "config": CONFIG.name}, {"python": sys.version.split()[0]})
        meter.finish(ok=False, error=core.fail_text(e))

    state = {"learn": 0.0, "updated": False}
    trainer_advance, update_policy, tc_advance = RLTrainer.advance, OnPolicyTrainer._update_policy, None

    def trainer_advance_(self):
        t = time.perf_counter()
        trainer_advance(self)
        state["learn"] += time.perf_counter() - t

    def update_policy_(self):
        done = update_policy(self)
        state["updated"] = True
        meter.end_warmup()  # no-op once measuring; the call holding this update is still warm-up (see below)
        return done

    tc_advance = TrainerController.advance

    def tc_advance_(self, env_manager):
        if not meter.on_exit:
            meter.on_exit.append(env_manager.close)  # shut the Unity players down before the process ends
        was_measuring = meter.measuring
        state["learn"], state["updated"] = 0.0, False
        t = time.perf_counter()
        n = tc_advance(self, env_manager)
        total = time.perf_counter() - t
        if was_measuring:
            meter.block(total - state["learn"], state["learn"], n / NUM_ENVS)
        return n

    meter.can_finish = lambda: state["updated"]
    RLTrainer.advance, OnPolicyTrainer._update_policy, TrainerController.advance = (
        trainer_advance_,
        update_policy_,
        tc_advance_,
    )
    sys.argv = [
        "mlagents-learn",
        str(CONFIG),
        f"--run-id={RUN_ID}",
        f"--results-dir={ROOT / 'results'}",
        f"--env={PLAYER}",
        f"--num-envs={NUM_ENVS}",
        "--no-graphics",
        f"--base-port={BASE_PORT}",
        "--force",
    ]
    os.chdir(ROOT)
    try:
        learn.main()
        meter.finish(ok=False, error="training ended before the measured window finished")
    except BaseException as e:  # noqa: BLE001
        meter.finish(ok=False, error=core.fail_text(e))


if __name__ == "__main__":
    main()
