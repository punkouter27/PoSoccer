"""Training runs grid: results/runs.html, one row per real training run under results/, newest first.

    python benchmark/runs.py                                   rebuild results/runs.html from the run folders
    python benchmark/runs.py --record <run-id> --ended "<how>" [--launcher <script>]
                                                               note this machine and how the run ended, then rebuild

The grid is rebuilt from what mlagents-learn leaves in each run folder (run_logs/timers.json: start, end, command
line; run_logs/training_status.json: checkpoint steps and times; configuration.yaml), so a run started by hand, or one
whose launcher was killed, still gets a row. This repo's vendored mlagents writes those three files only when the
trainer shuts down, so a killed run has none of them; its row then falls back to results/<run>.provenance.json (or
<run>/provenance.json, written by scripts/lib-profile.ps1 at launch: start, config, envs, init-from, commit), the
<brain>/<brain>-<steps>.pt checkpoint files (steps, and their times for the speed) and results/train_<run>.log
(train-parallel.ps1's worker log, "Step: N. Time Elapsed: T s"). What the folder cannot tell (which machine, how the
run ended) comes from results/<run>/run_meta.json, which --record writes; the scripts/train-*.ps1 launchers call it
when mlagents-learn returns, crash or not. Without it those cells read "-".

Speed is the run's own: trainer steps per second of the busiest brain, measured between the first and last checkpoint
of this session, so a resumed run is not credited with the steps of its earlier sessions. Benchmark is the newest
benchmark/history.json result on the same machine from before the run started (ms per env step), for comparison.
Stdlib only; core.py next to this file supplies the machine description.
"""

from __future__ import annotations

import argparse
import datetime
import html
import json
import os
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
RESULTS = ROOT / "results"
OUT = RESULTS / "runs.html"
BENCH = HERE / "history.json"
META = "run_meta.json"
TITLE = "PoSoccer"
SKIP = {"_benchmark", "_archive", "eval"}  # the Benchmark's throwaway run dir, archived runs, evaluate.ps1 grades


# ---------------------------------------------------------------- collect


def _json(path: Path) -> dict:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def _arg(cmd: str, name: str) -> str | None:
    m = re.search(rf"--{name}[= ](\S+)", cmd)
    return m.group(1) if m else None


def _speed(status: dict, start: float, end: float) -> tuple[str | None, int | None, float | None]:
    """(brain, steps reached, steps/s this session) for the brain with the most steps."""
    best = (None, None, None)
    for brain, s in status.items():
        if not isinstance(s, dict) or "checkpoints" not in s:
            continue
        cps = [c for c in s["checkpoints"] if c.get("steps") is not None]
        if s.get("final_checkpoint"):
            cps.append(s["final_checkpoint"])
        if not cps:
            continue
        reached = max(c["steps"] for c in cps)
        session = sorted(
            (c for c in cps if start - 1 <= (c.get("creation_time") or 0) <= end + 60), key=lambda c: c["creation_time"]
        )
        rate = None
        if len(session) >= 2 and session[-1]["creation_time"] > session[0]["creation_time"]:
            rate = (session[-1]["steps"] - session[0]["steps"]) / (
                session[-1]["creation_time"] - session[0]["creation_time"]
            )
        if best[1] is None or reached > best[1]:
            best = (brain, reached, rate)
    return best


def _bench_for(host: str | None, before: float) -> dict | None:
    runs = [r for r in _json(BENCH).get("runs", []) if r.get("ok") and r.get("result")]
    if host:
        runs = [r for r in runs if r.get("machine", {}).get("hostname") == host]
    if not runs:
        return None
    stamp = lambda r: datetime.datetime.fromisoformat(r["when"]).timestamp()  # noqa: E731
    earlier = [r for r in runs if stamp(r) <= before]
    return (earlier or runs)[-1]


def _when(stamp: str | None) -> float | None:
    """ISO time from PowerShell ("...25.1280561Z": 7 fraction digits, Z) or Python, as a timestamp."""
    if not stamp:
        return None
    s = re.sub(r"(\.\d{6})\d+", r"\1", stamp.strip()).replace("Z", "+00:00")
    try:
        return datetime.datetime.fromisoformat(s).timestamp()
    except ValueError:
        return None


def _checkpoint_status(d: Path) -> dict:
    """training_status.json's shape, rebuilt from <brain>/<brain>-<steps>.pt files and their write times."""
    status: dict = {}
    for f in d.glob("*/*-*.pt"):
        m = re.fullmatch(rf"{re.escape(f.parent.name)}-(\d+)\.pt", f.name)
        if m:
            cps = status.setdefault(f.parent.name, {"checkpoints": []})["checkpoints"]
            cps.append({"steps": int(m.group(1)), "creation_time": f.stat().st_mtime})
    return status


def _log_speed(log: Path) -> tuple[int | None, float | None]:
    """(steps reached, steps/s) from mlagents' "Step: N. Time Elapsed: T s" summary lines in a worker log."""
    if not log.exists():
        return None, None
    pts = [
        (int(s), float(t))
        for s, t in re.findall(
            r"Step: (\d+)\. Time Elapsed: ([\d.]+) s", log.read_text(encoding="utf-8", errors="replace")
        )
    ]
    if not pts:
        return None, None
    (s0, t0), (s1, t1) = pts[0], pts[-1]
    return s1, ((s1 - s0) / (t1 - t0) if t1 > t0 else None)


def _init_run(path: str) -> str:
    """The run an init_path points into: the folder after results/ (the launchers pass an absolute results dir)."""
    parts = re.split(r"[\\/]+", path.strip("'\""))
    low = [p.lower() for p in parts]
    if "results" in low and low.index("results") + 1 < len(parts):
        return parts[low.index("results") + 1]
    return parts[1] if len(parts) > 1 else path


def _newest_mtime(d: Path) -> float:
    """The last file the trainer touched (tfevents, checkpoints, player logs); run_meta.json is ours, not its."""
    files = [f for f in d.rglob("*") if f.is_file() and f.name != META]
    return max((f.stat().st_mtime for f in files), default=d.stat().st_mtime)


def collect() -> list[dict]:
    rows = []
    if not RESULTS.is_dir():
        return rows
    for d in RESULTS.iterdir():
        timers = d / "run_logs" / "timers.json"
        prov = _json(d / "provenance.json") or _json(RESULTS / f"{d.name}.provenance.json")
        if d.name in SKIP or not d.is_dir() or not (timers.exists() or prov or (d / "run_logs").is_dir()):
            continue
        t = _json(timers)
        md = t.get("metadata", {})
        meta = _json(d / META)
        cmd = md.get("command_line_arguments", "")
        start = float(
            md.get("start_time_seconds")
            or _when(prov.get("startedUtc"))
            or (
                timers.stat().st_mtime
                if timers.exists()
                else ((d / "run_logs") if (d / "run_logs").is_dir() else d).stat().st_ctime
            )
        )
        if md.get("end_time_seconds"):
            end = float(md["end_time_seconds"])
        else:  # killed before mlagents wrote timers.json: the last file it touched (tfevents, checkpoints)
            end = timers.stat().st_mtime if timers.exists() else _newest_mtime(d)
        status = _json(d / "run_logs" / "training_status.json") or _checkpoint_status(d)
        brain, steps, rate = _speed(status, start, end)
        log_steps, log_rate = _log_speed(RESULTS / f"train_{d.name}.log")
        steps = max((s for s in (steps, log_steps) if s is not None), default=None)  # log runs past the last checkpoint
        rate = rate if rate is not None else log_rate
        if rate is None and steps and cmd and "--resume" not in cmd and end > start:
            rate = steps / (end - start)
        config_text = (
            (d / "configuration.yaml").read_text(encoding="utf-8", errors="replace")
            if (d / "configuration.yaml").exists()
            else ""
        )
        warm = sorted({_init_run(p) for p in re.findall(r"init_path:\s*(\S+)", config_text) if p not in ("null", "~")})
        init_from = _arg(cmd, "initialize-from") or prov.get("initFrom") or None
        cfg = next((a for a in cmd.split()[1:] if a.endswith((".yaml", ".yml"))), None) or prov.get("config")
        if cmd:
            envs = _arg(cmd, "num-envs") or "1 (Editor)"
        else:  # provenance records the -NumEnvs parameter even for an in-editor run; best available
            envs = prov.get("numEnvs") or "-"
        bench = _bench_for(meta.get("host"), start)
        commit = meta.get("commit")  # HEAD when the run ended; the launch commit in provenance wins when there is one
        if prov.get("gitCommit"):
            commit = prov["gitCommit"] + (" (dirty)" if prov.get("gitDirty") else "")
        rows.append(
            dict(
                run=d.name,
                start=start,
                wall=max(0.0, end - start),
                ended=meta.get("ended") or ("still running or killed" if not md.get("end_time_seconds") else "exited"),
                resumed="--resume" in cmd,
                brain=brain,
                steps=steps,
                rate=rate,
                bench=bench,
                config=Path(cfg.replace("\\", "/")).name if cfg else None,
                envs=envs,
                warm=sorted({init_from, *warm} - {None}) if init_from else warm,
                commit=commit,
                host=meta.get("host"),
                gpu=meta.get("gpu"),
                device=meta.get("device"),
                launcher=meta.get("launcher"),
            )
        )
    rows.sort(key=lambda r: r["start"], reverse=True)
    return rows


# ---------------------------------------------------------------- render


def _dur(s: float) -> str:
    h, m = divmod(int(s) // 60, 60)
    return f"{h} h {m:02d} m" if h else f"{m} m"


def _num(n: float | None, digits: int = 0) -> str:
    return "-" if n is None else f"{n:,.{digits}f}"


def render(rows: list[dict]) -> str:
    e = html.escape
    body = []
    for r in rows:
        b = r["bench"]
        if b:
            notes = [f"{b['result']['env_steps_per_s']} env steps/s", e(b["when"][:10])]
            if datetime.datetime.fromisoformat(b["when"]).timestamp() > r["start"]:
                notes.append("taken after the run")
            if not r["host"]:
                notes.append("machine unknown")
            bench = f"{b['result']['step_time_ms']} ms<small>{' · '.join(notes)}</small>"
        else:
            bench = "-<small>no benchmark on this machine</small>"
        ended = e(r["ended"])
        cls = "bad" if re.search(r"crash|died|killed|HALT|error|exception", r["ended"], re.I) else ""
        body.append(
            "<tr>"
            f"<td><b>{e(r['run'])}</b>{'<small>resumed</small>' if r['resumed'] else ''}</td>"
            f"<td>{datetime.datetime.fromtimestamp(r['start']).strftime('%Y-%m-%d %H:%M')}</td>"
            f'<td class="n">{_dur(r["wall"])}</td>'
            f'<td class="{cls}">{ended}</td>'
            f'<td class="n">{_num(r["steps"])}<small>{e(r["brain"] or "")}</small></td>'
            f'<td class="n"><b>{_num(r["rate"])}</b><small>trainer steps/s</small></td>'
            f'<td class="n">{bench}</td>'
            f"<td>{e(r['config'] or '-')}</td>"
            f'<td class="n">{e(str(r["envs"]))}</td>'
            f"<td>{e(', '.join(r['warm']) or 'cold')}</td>"
            f'<td class="mono">{e(r["commit"] or "-")}</td>'
            f"<td>{e(r['host'] or '-')}<small>{e(r['gpu'] or '')}{' · ' + e(r['device']) if r['device'] else ''}"
            f"</small></td>"
            "</tr>"
        )
    latest = rows[0] if rows else None
    prev = next((r for r in rows[1:] if r["rate"]), None) if rows else None
    delta = ""
    if latest and latest["rate"] and prev:
        pct = 100 * (latest["rate"] / prev["rate"] - 1)
        delta = f'<span class="{"up" if pct >= 0 else "down"}">{pct:+.0f}% vs {e(prev["run"])}</span>'
    summary = (
        f'<div class="sum"><div><span>Latest run</span><b>{e(latest["run"])}</b></div>'
        f"<div><span>Speed</span><b>{_num(latest['rate'])} steps/s</b>{delta}</div>"
        f"<div><span>Wall-clock</span><b>{_dur(latest['wall'])}</b></div>"
        f"<div><span>Runs listed</span><b>{len(rows)}</b></div></div>"
        if latest
        else "<p>No training runs under results/ yet.</p>"
    )
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M")
    return f"""<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>{e(TITLE)} training runs</title>
<style>
:root{{--bg:#f5f7f6;--card:#fff;--ink:#18211f;--muted:#5d6b67;--line:#d8e0dd;--accent:#0f6b5c;--bad:#b42318;--up:#1f7a43}}
@media (prefers-color-scheme:dark){{:root{{--bg:#0f1514;--card:#161e1c;--ink:#e2eae7;--muted:#91a19c;--line:#27332f;
--accent:#5cc9b0;--bad:#ff8a7a;--up:#5fcf8a;color-scheme:dark}}}}
body{{margin:0;background:var(--bg);color:var(--ink);font:14px/1.5 "Segoe UI",system-ui,sans-serif;padding:24px 16px}}
h1{{font-size:20px;margin:0 0 4px}} p{{margin:0;color:var(--muted)}} .wrap{{max-width:1500px;margin:auto;display:grid;gap:16px}}
.sum{{display:flex;flex-wrap:wrap;gap:12px 40px;background:var(--card);border:1px solid var(--line);border-radius:8px;padding:14px 18px}}
.sum span{{display:block;color:var(--muted);font-size:12px}} .sum b{{font-size:20px}} .sum .up,.sum .down{{display:block;font-size:12px}}
.up{{color:var(--up)}} .down{{color:var(--bad)}}
.t{{overflow-x:auto;background:var(--card);border:1px solid var(--line);border-radius:8px}}
table{{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums}}
th,td{{padding:8px 12px;border-bottom:1px solid var(--line);text-align:left;vertical-align:top;white-space:nowrap}}
th{{font-size:11px;text-transform:uppercase;letter-spacing:.06em;color:var(--muted);position:sticky;top:0;background:var(--card)}}
tr:last-child td{{border-bottom:0}} td.n{{text-align:right}} td small{{display:block;color:var(--muted);font-size:11px}}
td.bad{{color:var(--bad)}} .mono{{font-family:Consolas,monospace}}
</style></head><body><div class="wrap">
<div><h1>{e(TITLE)} training runs</h1><p>Every real training run under results/, newest first. Rebuilt {now} by
benchmark/runs.py. Speed = trainer steps/s of the busiest brain this session; Benchmark = the newest benchmark.ps1
result on the same machine before the run (ms per env step).</p></div>
{summary}
<div class="t"><table><thead><tr><th>Run</th><th>Started</th><th>Wall-clock</th><th>Ended</th><th>Steps</th>
<th>Speed</th><th>Benchmark</th><th>Config</th><th>Envs</th><th>Warm start</th><th>Commit</th><th>Machine</th></tr></thead>
<tbody>{"".join(body)}</tbody></table></div></div></body></html>
"""


def build() -> Path:
    RESULTS.mkdir(exist_ok=True)
    tmp = OUT.with_suffix(".tmp")
    tmp.write_text(render(collect()), encoding="utf-8")
    os.replace(tmp, OUT)
    return OUT


def record(run_id: str, ended: str, launcher: str | None) -> None:
    """Write results/<run>/run_meta.json: this machine, the commit and how the run ended."""
    sys.path.insert(0, str(HERE))
    import core

    m = core.machine()
    d = RESULTS / run_id
    d.mkdir(parents=True, exist_ok=True)
    meta = dict(
        host=m["hostname"],
        gpu=m["gpu"],
        cpu=m["cpu"],
        device="CPU" if os.environ.get("CUDA_VISIBLE_DEVICES") == "-1" else None,
        commit=core.git_commit(),
        ended=ended,
        launcher=launcher,
        recorded=datetime.datetime.now().astimezone().isoformat(timespec="seconds"),
    )
    (d / META).write_text(json.dumps(meta, indent=1), encoding="utf-8")


def main() -> None:
    p = argparse.ArgumentParser(description="Rebuild results/runs.html (the training runs grid).")
    p.add_argument("--record", metavar="RUN_ID", help="note this machine and how the run ended first")
    p.add_argument("--ended", default="exited", help="how the run ended, in words")
    p.add_argument("--launcher", help="the script that ran it")
    a = p.parse_args()
    if a.record:
        record(a.record, a.ended, a.launcher)
    print(f"Training runs grid: {build()}")


if __name__ == "__main__":
    main()
