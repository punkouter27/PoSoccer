"""HTML report for a train-roster.ps1 session: progress, grades and training curves per character.

    .venv\\Scripts\\python.exe scripts\\make-roster-report.py --state docs\\<date>\\train-roster.json \\
        --out docs\\<date>\\roster-report.html [--status docs\\<date>\\session-status.json] [--hours 8]

Reads the queue state (queue, done, grades), the optional hourly snapshots written by
watch-session.ps1, and every run's TensorBoard event files (results/soccer_<tag>_<name>/<NAME>).
Charts are inline SVG, drawn by the helpers in make-loop-report.py. Safe to run while
training: it only reads.
"""
import argparse
import html
import importlib.util
import os
from datetime import datetime

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
_spec = importlib.util.spec_from_file_location("loopreport", os.path.join(ROOT, "scripts", "make-loop-report.py"))
lr = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(lr)

TAGS = [("p50", "from scratch", 30_000_000), ("p51bot", "vs full-strength bot", 10_000_000)]
TOTAL = sum(t[2] for t in TAGS)
PALETTE = ["#2f6fdb", "#d9822b", "#8e44ad", "#1f8a4c", "#c0392b", "#16a2b8", "#b8860b",
           "#e84393", "#5d6d7e", "#6c5ce7", "#00a86b", "#a0522d", "#2d3436"]

PLAIN = {
    "Environment/Cumulative Reward": "Up is better. It measures how much the agent earns per training match. "
                                     "Useful to see learning happen; the graded matches decide what ships.",
    "PoSoccer/stalemate": "Down is better. Share of matches that time out with no goal; the bar allows 10%.",
    "Environment/Episode Length": "Usually down is better: matches end sooner because someone scores.",
    "Losses/Value Loss": "The agent's error predicting its own future score. Spikes when the opponent gets "
                         "harder, then settles.",
    "Losses/Policy Loss": "Size of each learning step. Small and steady is healthy.",
    "Policy/Entropy": "How much the agent still experiments. Falls as it commits to what works.",
    "Environment/Lesson Number/bot_strength": "How strong the practice opponent is. The curriculum raises it as "
                                              "the agent improves; grading uses full strength.",
}


def steps_done(name):
    done = 0
    for tag, _, budget in TAGS:
        run = f"soccer_{tag}_{name.lower()}"
        if os.path.isdir(lr.run_dir(run)):
            done += min(budget, lr.last_step(run, name))
    return done


def progress_bars(names, state):
    W, rowh, L, R = 760, 28, 110, 120
    H = rowh * len(names) + 34
    s = [f"<svg viewBox='0 0 {W} {H}' role='img' aria-label='Training progress per character'>"]
    split = L + TAGS[0][2] / TOTAL * (W - L - R)
    for i, name in enumerate(names):
        y = 6 + i * rowh
        done = steps_done(name)
        frac = done / TOTAL
        graded = name in (state.get("grades") or {})
        s.append(f"<text class='lab' x='{L - 10}' y='{y + 15}' text-anchor='end'>{name}</text>")
        s.append(f"<rect class='track' x='{L}' y='{y + 4}' width='{W - L - R}' height='{rowh - 12}' rx='4'/>")
        if frac > 0:
            s.append(f"<rect x='{L}' y='{y + 4}' width='{frac * (W - L - R):.1f}' height='{rowh - 12}' rx='4' "
                     f"fill='{color(name)}'><title>{done / 1e6:.1f}M of {TOTAL / 1e6:.0f}M steps</title></rect>")
        label = "graded" if graded else (f"{done / 1e6:.1f}M / {TOTAL / 1e6:.0f}M" if done else "waiting")
        s.append(f"<text class='tick' x='{W - R + 8}' y='{y + 15}'>{label}</text>")
    s.append(f"<line class='bar80' x1='{split:.1f}' x2='{split:.1f}' y1='0' y2='{H - 24}'/>")
    s.append(f"<text class='tick' x='{(L + split) / 2:.1f}' y='{H - 8}' text-anchor='middle'>stage 1: learn from scratch (30M)</text>")
    s.append(f"<text class='tick' x='{(split + W - R) / 2:.1f}' y='{H - 8}' text-anchor='middle'>stage 2: full bot (10M)</text>")
    s.append("</svg>")
    return "".join(s)


_colors = {}


def color(name):
    return _colors.get(name, "#888")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--state", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--status", default="")
    ap.add_argument("--hours", type=float, default=0)
    a = ap.parse_args()
    state = lr.load_json(a.state) or {}
    status = lr.load_json(a.status) if a.status else None
    names = state.get("queue", [])
    for i, n in enumerate(names):
        _colors[n] = PALETTE[i % len(PALETTE)]
    grades = state.get("grades") or {}

    started = datetime.fromisoformat(state["started"][:19])
    now = datetime.now()
    elapsed = (now - started).total_seconds() / 3600
    total_done = sum(steps_done(n) for n in names)
    rate = total_done / max(elapsed, 1e-6)
    remaining = (len(names) * TOTAL - total_done) / max(rate, 1) if rate > 0 else 0
    finished = state.get("finished")

    # ---- headline table
    rows = []
    for n in names:
        g = grades.get(n)
        d = steps_done(n)
        stage = ("graded" if g else "stage 2" if d > TAGS[0][2] else "stage 1" if d else "waiting")
        rows.append(f"<tr><td><span class='dot' style='background:{color(n)}'></span>{n}</td><td>{stage}</td>"
                    f"<td>{d / 1e6:.1f}M</td><td>{lr.pct(g['win']) if g else '—'}</td>"
                    f"<td>{lr.pct(g['stale']) if g else '—'}</td>"
                    f"<td class='{('ok' if g['pass'] else 'bad') if g else 'muted'}'>"
                    f"{('PASS' if g['pass'] else 'below bar') if g else 'not yet'}</td></tr>")

    graded_rows = [(n, {"win": g["win"], "stale": g["stale"], "pass": g["pass"]}, "") for n, g in grades.items()]
    grade_html = lr.grade_bars(graded_rows) if graded_rows else \
        "<p class='muted'>No character has finished both stages yet. Grades appear here as each batch of three ends.</p>"

    # ---- curves: one line per character, stage 2 continues where stage 1 ended
    charts = []
    active = [n for n in names if steps_done(n) > 0]
    for tag, title, _ in lr.METRICS:
        series, bound = [], None
        for n in active:
            off = 0
            for rtag, _, budget in TAGS:
                run = f"soccer_{rtag}_{n.lower()}"
                pts = lr.scalars(run, n, tag)
                if pts:
                    shifted = [(s - pts[0][0] + off if off else s, v) for s, v in lr.ema(pts)]
                    series.append((f"{n} {rtag}", color(n), shifted, False))
                if off == 0 and pts:
                    off = lr.last_step(run, n)
                    bound = off
        if not series:
            continue
        fmt = "{:.0%}" if tag == "PoSoccer/stalemate" else ("{:.0f}" if "Length" in tag else "{:.2f}")
        bounds = [(bound, "stage 2 starts")] if bound and any("p51bot" in s[0] for s in series) else []
        charts.append(f"<section class='card'><h3>{title}</h3><p class='muted'>{PLAIN.get(tag, '')}</p>"
                      f"{lr.line_chart(series, bounds, title, fmt)}</section>")

    hourly = ""
    if status:
        hrs = []
        for s in status:
            t = datetime.fromisoformat(s["time"][:19])
            hrs.append(f"<tr><td>{t:%a %H:%M}</td><td>{s['totalSteps'] / 1e6:.1f}M</td>"
                       f"<td>{html.escape(s.get('stage', ''))}</td></tr>")
        hourly = ("<h2>Hourly log</h2><div class='tablewrap'><table><tr><th>Time</th><th>Steps (all)</th>"
                  f"<th>Queue</th></tr>{''.join(hrs)}</table></div>")

    legend = " ".join(f"<span class='key'><span class='dot' style='background:{color(n)}'></span>{n}</span>"
                      for n in active)
    window = f" This report covers the first {a.hours:g} hours." if a.hours else ""
    eta = "finished" if finished else f"about {remaining:.0f} h of training left at the current pace"

    css = open(os.path.join(ROOT, "scripts", "make-loop-report.py"), encoding="utf-8").read()
    css = css[css.index("<style>") + 7:css.index("</style>")].replace("{{", "{").replace("}}", "}")
    css += " svg .track { fill:var(--grid); } .big { font-size:34px; font-weight:700; margin:0; }" \
           " .stats { display:grid; grid-template-columns:repeat(auto-fit,minmax(150px,1fr)); gap:12px; }"

    page = f"""<!doctype html><html lang='en'><head><meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>PoSoccer Roster Training</title><style>{css}</style></head><body><main>
<h1>Roster training report</h1>
<p class='muted'>Started {started:%a %Y-%m-%d %H:%M}, updated {now:%a %H:%M}.{window} The 13 new characters each learn
from scratch (stage 1) and then against the full-strength bot (stage 2), three at a time, before being graded.</p>

<div class='stats'>
<div class='card'><p class='muted'>Hours running</p><p class='big'>{elapsed:.1f}</p></div>
<div class='card'><p class='muted'>Steps trained</p><p class='big'>{total_done / 1e6:.0f}M</p></div>
<div class='card'><p class='muted'>Graded</p><p class='big'>{len(grades)} / {len(names)}</p></div>
<div class='card'><p class='muted'>Passing the bar</p><p class='big'>{sum(1 for g in grades.values() if g['pass'])}</p></div>
</div>
<p class='muted'>Pace: {rate / 1e6:.1f}M steps an hour across all trainers; {eta}.</p>

<h2>Progress</h2>
<div class='card'>{progress_bars(names, state)}</div>

<h2>Grades</h2>
<p>A character passes when it beats the bot in <b>at least 80%</b> of 350 separate graded matches, with <b>at most
10%</b> stalemates. Green = wins, yellow = stalemates, red = losses.</p>
<div class='card'>{grade_html}</div>
<div class='tablewrap'><table><tr><th>Character</th><th>Stage</th><th>Steps</th><th>Wins</th><th>Stalemates</th><th>Bar</th></tr>
{''.join(rows)}</table></div>

<h2>Learning curves</h2>
<p>{legend}</p>
<p class='muted'>Smoothed. Each line is one character; the dashed vertical line marks where stage 2 begins.</p>
{''.join(charts) or "<p class='muted'>No curves recorded yet.</p>"}
{hourly}

<h2>Where things are</h2>
<ul>
<li>Production weights: <code>Assets/Agents/&lt;Name&gt;_v01/&lt;NAME&gt;.onnx</code>, overwritten in place after each stage.</li>
<li>Checkpoints (one per million steps): <code>results/soccer_&lt;stage&gt;_&lt;name&gt;/&lt;NAME&gt;/</code>.</li>
<li>Live curves: TensorBoard at <code>http://localhost:6006</code>. Queue log: <code>docs/{started:%Y%m%d}/train-roster.log</code>.</li>
<li>Training reward can rise while the graded result falls; only grades decide what ships.</li>
</ul>
</main></body></html>"""
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    with open(a.out, "w", encoding="utf-8") as f:
        f.write(page)
    print("wrote", a.out)


if __name__ == "__main__":
    main()
