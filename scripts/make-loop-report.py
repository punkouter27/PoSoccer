"""HTML report for a train-loop.ps1 session: grades per round and training curves.

    .venv\\Scripts\\python.exe scripts\\make-loop-report.py --state docs\\<date>\\train-loop.json --out docs\\<date>\\training-report.html

Reads the loop state (rounds, grades, best runs, final grade), each run's provenance.json
(to chain a run onto the run it continued from) and the TensorBoard event files. The
page is self-contained: inline SVG charts, no network, light and dark themes.
"""
import argparse
import glob
import html
import json
import os
from datetime import datetime

from tensorboard.backend.event_processing.event_accumulator import EventAccumulator

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
COLORS = {"MATT": "#2f6fdb", "KIM": "#d9822b", "NICK": "#8e44ad", "STANDARD": "#1f8a4c"}

METRICS = [
    ("Environment/Cumulative Reward", "Reward per match",
     "The score the agent earns in a training match. It rises as the agent learns what the reward "
     "table pays for. It does <b>not</b> decide anything here (CLAUDE.md): the graded matches below do."),
    ("PoSoccer/stalemate", "Stalemate rate in training",
     "The share of training matches that ran out the clock with no goal. The benchmark allows at most "
     "10% in grading, and this is the criterion MATT and NICK failed after p38."),
    ("Environment/Episode Length", "Match length (decision steps)",
     "How long a match lasts. Shorter means goals come sooner, whichever side scores them."),
    ("Losses/Value Loss", "Value loss",
     "How wrong the agent's own prediction of its future reward was. It spikes when the opponent or "
     "the curriculum changes and settles as the agent re-learns to predict."),
    ("Losses/Policy Loss", "Policy loss",
     "The size of each update to the agent's decision rule. PPO keeps it small on purpose; a flat, "
     "small line means steady learning rather than lurches."),
    ("Policy/Entropy", "Exploration (entropy)",
     "How random the agent's actions still are. It falls as the agent commits to what works."),
    ("Environment/Lesson Number/bot_strength", "Opponent strength",
     "The curriculum: how strong the scripted bot was (1.0 = the full-strength bot used for grading)."),
]


def load_json(path):
    if not os.path.exists(path):
        return None
    with open(path, encoding="utf-8-sig") as f:
        return json.load(f)


def run_dir(run):
    return os.path.join(ROOT, "results", run)


def init_from(run):
    # Copied next to the checkpoints when the trainer exits; results/<run>.provenance.json while it runs.
    prov = (load_json(os.path.join(run_dir(run), "provenance.json"))
            or load_json(os.path.join(ROOT, "results", run + ".provenance.json")) or {})
    for key in ("initFrom", "init_from", "InitFrom"):
        if prov.get(key):
            return prov[key]
    return ""


_scalar_cache = {}


def scalars(run, profile, tag):
    key = (run, tag)
    if key in _scalar_cache:
        return _scalar_cache[key]
    d = os.path.join(run_dir(run), profile)
    pts = []
    if glob.glob(os.path.join(d, "events.out.tfevents*")):
        ea = EventAccumulator(d, size_guidance={"scalars": 0})
        ea.Reload()
        if tag in ea.Tags()["scalars"]:
            pts = [(e.step, e.value) for e in ea.Scalars(tag)]
    _scalar_cache[key] = pts
    return pts


def last_step(run, profile):
    pts = scalars(run, profile, "Policy/Entropy") or scalars(run, profile, "Environment/Cumulative Reward")
    return pts[-1][0] if pts else 0


def offset(run, profile, seen=None):
    """Steps already behind this run: the end of the run it continued from, recursively."""
    seen = seen or set()
    src = init_from(run)
    if not src or src in seen or not os.path.isdir(run_dir(src)):
        return 0
    seen.add(src)
    return offset(src, profile, seen) + last_step(src, profile)


def ema(points, alpha=0.9):
    out, acc = [], None
    for s, v in points:
        acc = v if acc is None else alpha * acc + (1 - alpha) * v
        out.append((s, acc))
    return out


def fmt_steps(v):
    return f"{v / 1e6:.0f}M" if v >= 1e6 else f"{v / 1e3:.0f}k"


def line_chart(series, boundaries, title, y_fmt="{:.2f}", y_floor=None):
    """series: list of (label, color, points, dashed). boundaries: list of (x, label)."""
    W, H, L, R, T, B = 760, 300, 60, 16, 16, 40
    xs = [p[0] for _, _, pts, _ in series for p in pts]
    ys = [p[1] for _, _, pts, _ in series for p in pts]
    if not xs:
        return "<p class='muted'>No data recorded for this metric.</p>"
    x0, x1 = 0, max(xs)
    y0, y1 = min(ys), max(ys)
    if y_floor is not None:
        y0 = min(y0, y_floor)
    if y1 - y0 < 1e-9:
        y1 = y0 + 1
    pad = (y1 - y0) * 0.06
    y0, y1 = y0 - pad, y1 + pad

    def X(v):
        return L + (v - x0) / (x1 - x0 or 1) * (W - L - R)

    def Y(v):
        return T + (1 - (v - y0) / (y1 - y0)) * (H - T - B)

    parts = [f"<svg viewBox='0 0 {W} {H}' role='img' aria-label='{html.escape(title)}'>"]
    for i in range(5):
        v = y0 + (y1 - y0) * i / 4
        parts.append(f"<line class='grid' x1='{L}' x2='{W - R}' y1='{Y(v):.1f}' y2='{Y(v):.1f}'/>")
        parts.append(f"<text class='tick' x='{L - 8}' y='{Y(v) + 4:.1f}' text-anchor='end'>{y_fmt.format(v)}</text>")
    for i in range(6):
        v = x0 + (x1 - x0) * i / 5
        parts.append(f"<text class='tick' x='{X(v):.1f}' y='{H - B + 18}' text-anchor='middle'>{fmt_steps(v)}</text>")
    parts.append(f"<text class='tick' x='{(L + W - R) / 2}' y='{H - 4}' text-anchor='middle'>training steps (each agent's own lineage)</text>")
    for bx, blabel in boundaries:
        if 0 < bx < x1:
            parts.append(f"<line class='bound' x1='{X(bx):.1f}' x2='{X(bx):.1f}' y1='{T}' y2='{H - B}'/>")
            parts.append(f"<text class='bound-l' x='{X(bx) + 4:.1f}' y='{T + 12}'>{html.escape(blabel)}</text>")
    for label, color, pts, dashed in series:
        if len(pts) < 2:
            continue
        step = max(1, len(pts) // 400)
        d = " ".join(f"{'M' if i == 0 else 'L'}{X(s):.1f},{Y(v):.1f}" for i, (s, v) in enumerate(pts[::step]))
        dash = " stroke-dasharray='5 4' opacity='0.55'" if dashed else ""
        parts.append(f"<path d='{d}' fill='none' stroke='{color}' stroke-width='2'{dash}><title>{html.escape(label)}</title></path>")
    parts.append("</svg>")
    return "".join(parts)


def grade_bars(rows):
    """rows: list of (label, grade dict or None, note). Stacked win/stale/loss bars with the 80% line."""
    W, rowh, L, R = 760, 30, 190, 70
    H = rowh * len(rows) + 30
    s = [f"<svg viewBox='0 0 {W} {H}' role='img' aria-label='Graded results'>"]

    def X(v):
        return L + v * (W - L - R)

    for i, (label, g, note) in enumerate(rows):
        y = 8 + i * rowh
        s.append(f"<text class='lab' x='{L - 10}' y='{y + 15}' text-anchor='end'>{html.escape(label)}</text>")
        if not g:
            s.append(f"<text class='tick' x='{L}' y='{y + 15}'>not graded</text>")
            continue
        win, stale = g["win"], g["stale"]
        loss = max(0.0, 1 - win - stale)
        x = X(0)
        for frac, cls, name in ((win, "win", "wins"), (stale, "stale", "stalemates"), (loss, "loss", "losses")):
            w = X(frac) - X(0)
            s.append(f"<rect class='{cls}' x='{x:.1f}' y='{y + 3}' width='{max(w, 0):.1f}' height='{rowh - 10}'>"
                     f"<title>{name}: {frac:.1%}</title></rect>")
            x += w
        verdict = "PASS" if g.get("pass") else "fail"
        s.append(f"<text class='val {'ok' if g.get('pass') else 'bad'}' x='{W - R + 6}' y='{y + 15}'>{win:.0%} {verdict}</text>")
        if note:
            s.append(f"<text class='note' x='{X(win) - 4:.1f}' y='{y + 15}' text-anchor='end'>{html.escape(note)}</text>")
    s.append(f"<line class='bar80' x1='{X(0.8):.1f}' x2='{X(0.8):.1f}' y1='0' y2='{H - 22}'/>")
    s.append(f"<text class='tick' x='{X(0.8):.1f}' y='{H - 8}' text-anchor='middle'>80% bar</text>")
    s.append("</svg>")
    return "".join(s)


def pct(v):
    return "—" if v is None else f"{v:.1%}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--state", required=True)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    state = load_json(a.state)
    profiles = state["profiles"]
    rounds = state["rounds"]

    # Grades before the session: the 2026-11-07 regrade of the brains deployed then.
    before = {}
    for p in profiles:
        j = load_json(os.path.join(ROOT, "results", "eval", f"regrade_{p}_20261107.json"))
        if j and j.get("episodes"):
            w, st = j["blueWins"] / j["episodes"], j["stalemates"] / j["episodes"]
            before[p] = {"win": w, "stale": st, "pass": w >= 0.8 and st <= 0.1}

    # ---- summary table
    rows_html = []
    for p in profiles:
        fin = (state.get("final") or {}).get(p) or state["best"][p]
        b = before.get(p)
        rows_html.append(
            f"<tr><td><span class='dot' style='background:{COLORS.get(p, '#888')}'></span>{p}</td>"
            f"<td>{pct(b['win']) if b else '—'}</td><td><b>{pct(fin['win'])}</b></td>"
            f"<td>{pct(fin['stale'])}</td><td>{fin['episodes']}</td>"
            f"<td class='{'ok' if fin['pass'] else 'bad'}'>{'PASS' if fin['pass'] else 'fail'}</td>"
            f"<td><code>{html.escape(state['best'][p]['run'])}</code></td></tr>")

    # ---- per-profile grade bars across rounds
    grade_sections = []
    for p in profiles:
        rows = []
        if p in before:
            rows.append(("before (p31 brain)", before[p], ""))
        for r in rounds:
            if p in r["profiles"]:
                g = (r.get("grades") or {}).get(p)
                kept = (r.get("kept") or {}).get(p)
                rows.append((f"{r['tag']}", g, "" if kept else "discarded"))
        fin = (state.get("final") or {}).get(p)
        if fin:
            rows.append((f"final ({fin['episodes']} matches)", fin, ""))
        grade_sections.append(f"<h3><span class='dot' style='background:{COLORS.get(p, '#888')}'></span>{p}</h3>"
                              + grade_bars(rows))

    # ---- curves: every run in the session, placed after the run it continued from
    runs_by_profile = {p: [] for p in profiles}
    for r in rounds:
        for p in r["profiles"]:
            run = f"soccer_{r['tag']}_{p.lower()}"
            if os.path.isdir(run_dir(run)):
                kept = (r.get("kept") or {}).get(p, True)
                runs_by_profile[p].append((r["tag"], run, kept))

    charts = []
    for tag, title, blurb in METRICS:
        series, bounds = [], {}
        for p in profiles:
            for rtag, run, kept in runs_by_profile[p]:
                off = offset(run, p)
                pts = scalars(run, p, tag)
                if not pts:
                    continue
                bounds[off] = rtag if off == 0 else f"{rtag} starts"
                series.append((f"{p} {rtag}", COLORS.get(p, "#888"),
                               [(s - pts[0][0] + off if off else s, v) for s, v in ema(pts)], not kept))
        if not series:
            continue
        fmt = "{:.0%}" if tag == "PoSoccer/stalemate" else ("{:.0f}" if "Length" in tag else "{:.2f}")
        chart = line_chart(series, sorted(bounds.items())[1:], title, fmt)
        charts.append(f"<section class='card'><h3>{title}</h3><p class='muted'>{blurb}</p>{chart}</section>")

    legend = " ".join(f"<span class='key'><span class='dot' style='background:{COLORS.get(p, '#888')}'></span>{p}</span>"
                      for p in profiles)

    round_rows = []
    for r in rounds:
        steps = f"{r['steps'] / 1e6:.0f}M" if r.get("steps") else "baseline"
        round_rows.append(f"<tr><td>{r['tag']}</td><td>{', '.join(r['profiles'])}</td><td>{steps}</td>"
                          + "".join(f"<td>{pct(((r.get('grades') or {}).get(p) or {}).get('win')) if p in r['profiles'] else ''}"
                                    f"{'' if p not in r['profiles'] or (r.get('kept') or {}).get(p, True) else ' ✗'}</td>"
                                    for p in profiles) + "</tr>")

    started = datetime.fromisoformat(state["started"][:19])
    finished = datetime.fromisoformat(state["finished"][:19]) if state.get("finished") else None
    span = f"{started:%Y-%m-%d %H:%M} to {finished:%H:%M}" if finished else f"started {started:%Y-%m-%d %H:%M} (in progress)"

    page = f"""<!doctype html><html lang='en'><head><meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>PoSoccer Training Report</title>
<style>
:root {{ --bg:#f7f7f5; --fg:#1b1d1c; --muted:#5d625f; --card:#fff; --line:#dcdedb; --grid:#e7e9e6;
  --win:#2e9e5b; --stale:#c9a227; --loss:#c0392b; --ok:#1f8a4c; --bad:#b54708; }}
@media (prefers-color-scheme: dark) {{ :root:not([data-theme='light']) {{ --bg:#111413; --fg:#e8eae8; --muted:#a3a8a5;
  --card:#1a1e1c; --line:#2c312e; --grid:#252a27; --ok:#3fbf73; --bad:#f0a35e; }} }}
:root[data-theme='dark'] {{ --bg:#111413; --fg:#e8eae8; --muted:#a3a8a5; --card:#1a1e1c; --line:#2c312e; --grid:#252a27; --ok:#3fbf73; --bad:#f0a35e; }}
* {{ box-sizing:border-box; }}
body {{ margin:0; background:var(--bg); color:var(--fg); font:16px/1.55 system-ui,-apple-system,'Segoe UI',sans-serif; }}
main {{ max-width:860px; margin:0 auto; padding:32px 16px 64px; }}
h1 {{ font-size:28px; margin:0 0 4px; }} h2 {{ font-size:21px; margin:40px 0 10px; }} h3 {{ font-size:17px; margin:22px 0 6px; }}
.muted {{ color:var(--muted); }} .ok {{ color:var(--ok); font-weight:600; }} .bad {{ color:var(--bad); font-weight:600; }}
.card {{ background:var(--card); border:1px solid var(--line); border-radius:10px; padding:14px 16px; margin:14px 0; }}
.card p {{ margin:2px 0 8px; font-size:14px; }}
table {{ width:100%; border-collapse:collapse; font-size:14px; }} th,td {{ text-align:left; padding:7px 6px; border-bottom:1px solid var(--line); }}
.tablewrap {{ overflow-x:auto; }}
.dot {{ display:inline-block; width:10px; height:10px; border-radius:50%; margin-right:6px; vertical-align:middle; }}
.key {{ margin-right:14px; font-size:14px; }}
svg {{ width:100%; height:auto; display:block; }}
svg .grid {{ stroke:var(--grid); }} svg .tick, svg .note {{ fill:var(--muted); font-size:11px; }}
svg .lab {{ fill:var(--fg); font-size:12px; }} svg .val {{ font-size:12px; }} svg .val.ok {{ fill:var(--ok); }} svg .val.bad {{ fill:var(--bad); }}
svg .bound {{ stroke:var(--muted); stroke-dasharray:3 4; }} svg .bound-l {{ fill:var(--muted); font-size:11px; }}
svg .win {{ fill:var(--win); }} svg .stale {{ fill:var(--stale); }} svg .loss {{ fill:var(--loss); }}
svg .bar80 {{ stroke:var(--fg); stroke-width:1.5; stroke-dasharray:4 3; }} svg .note {{ fill:#fff; font-weight:600; }}
code {{ font-size:13px; }}
</style></head><body><main>
<h1>Training report</h1>
<p class='muted'>Unattended keep-best loop, {span}. Agents: {', '.join(profiles)}. Opponent: the benchmark bot (version 1).</p>

<h2>Result</h2>
<p>An agent passes when it beats the bot in <b>at least 80%</b> of graded matches with <b>at most 10%</b> stalemates
(timeouts with no goal). Grades are separate matches played after training, not training scores.</p>
<div class='tablewrap'><table><tr><th>Agent</th><th>Before</th><th>Now: wins</th><th>Stalemates</th><th>Matches</th><th>Bar</th><th>Production weights</th></tr>
{''.join(rows_html)}</table></div>

<h2>How each round graded</h2>
<p class='muted'>Green = wins, yellow = stalemates, red = losses. Each round continues the agent from its best graded
brain so far. A round that grades worse is <b>discarded</b> and the previous best is put back, so the production
weights never get worse. "Better" means further from failing on whichever criterion is closer.</p>
<div class='card'>{''.join(grade_sections)}</div>

<h2>Rounds</h2>
<div class='tablewrap'><table><tr><th>Round</th><th>Trained</th><th>Steps each</th>{''.join(f'<th>{p}</th>' for p in profiles)}</tr>
{''.join(round_rows)}</table></div>
<p class='muted'>Win rate per round (350 graded matches; ✗ = discarded). Agents failing the bar train first; once all pass, all refine.</p>

<h2>Learning curves</h2>
<p>{legend}<span class='key muted'>dashed = discarded round</span></p>
<p class='muted'>Smoothed. Each agent's steps run along its own lineage: a round's curve starts where the run it continued from ended.</p>
{''.join(charts)}

<h2>Reading the numbers</h2>
<ul>
<li>350 graded matches pin a win rate to about ±2 points; the final grade uses 1000 matches (about ±1.3).</li>
<li>Training reward and grades can disagree. This project has seen a rising curve end in a worse grade three times, so
only the grades decide what ships.</li>
<li>Production weights live in <code>Assets/Agents/&lt;Name&gt;_v01/&lt;NAME&gt;.onnx</code>; every round's checkpoints stay
in <code>results/&lt;run&gt;/</code>.</li>
</ul>
</main></body></html>"""
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    with open(a.out, "w", encoding="utf-8") as f:
        f.write(page)
    print("wrote", a.out)


if __name__ == "__main__":
    main()
