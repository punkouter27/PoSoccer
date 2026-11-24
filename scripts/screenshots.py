"""Before/after screenshot page for a UI change (AGENTS.md section 1: UI changes get before/after
screenshots and an HTML report in SCREENSHOTS/).

Captures the key screens of the LOCAL WebGL build (never the live site) at 1366x768 and 390x844,
pairs them with the "before" screens and writes, under SCREENSHOTS/<YYYYMMDD>_<feature>/:
  index.html          before | after per screen, with notes
  <feature>.png       one side-by-side annotated image of every pair
  <screen>-<w>-before.png / -after.png   the (downscaled) captures

Rebuilding an old commit's WebGL player takes a Unity build, so "before" comes either from a folder
of PNGs (--before-dir; a file whose name holds the screen name and the width, e.g. before-home-1366.png)
or from another local WebGL build folder (--before-build). Run (from the repo root):

  uv run --with playwright --with pillow python scripts/screenshots.py --feature v4-audit \
      --before-dir <folder of PNGs> [--build Builds/WebGL] [--screens home=/,diag=/diag] [--notes "..."]

Build the "after" player first: pwsh -File scripts/publish_webgl.ps1 -NoPublish.
"""

from __future__ import annotations

import argparse
import contextlib
import datetime as dt
import functools
import html
import http.server
import subprocess
import sys
import threading
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SIZES = [(1366, 768), (390, 844)]
THUMB_WIDTH = 640


class _Handler(http.server.SimpleHTTPRequestHandler):
    """Serves a WebGL build the way staticwebapp.config.json does (/diag rewrite, gzip .unityweb)."""

    def translate_path(self, path):
        clean = path.split("?", 1)[0]
        if clean.rstrip("/") == "/diag":
            path = "/diag.html"
        return super().translate_path(path)

    def end_headers(self):
        if self.path.split("?", 1)[0].endswith(".unityweb"):
            self.send_header("Content-Encoding", "gzip")
        self.send_header("X-Commit", "local")
        super().end_headers()

    def guess_type(self, path):
        p = str(path)
        if p.endswith(".unityweb"):
            return "application/octet-stream"
        if p.endswith(".wasm"):
            return "application/wasm"
        return super().guess_type(path)

    def log_message(self, *args):
        pass


@contextlib.contextmanager
def serve(build: Path):
    handler = functools.partial(_Handler, directory=str(build))
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield f"http://127.0.0.1:{server.server_address[1]}"
    finally:
        server.shutdown()


def capture(build: Path, screens: dict[str, str], out: Path, tag: str, wait_s: float) -> dict[tuple[str, int], Path]:
    from playwright.sync_api import sync_playwright

    shots: dict[tuple[str, int], Path] = {}
    with serve(build) as base, sync_playwright() as pw:
        browser = pw.chromium.launch(args=["--use-angle=d3d11", "--ignore-gpu-blocklist"])
        try:
            for name, route in screens.items():
                for w, h in SIZES:
                    page = browser.new_page(viewport={"width": w, "height": h})
                    page.goto(base + route, wait_until="load", timeout=120_000)
                    # The game page needs time to download, compile and reach its menu; /diag only probes.
                    page.wait_for_timeout(int((wait_s if route in ("/", "/index.html") else 4) * 1000))
                    path = out / f"{name}-{w}-{tag}.png"
                    page.screenshot(path=str(path))
                    page.close()
                    shots[(name, w)] = path
        finally:
            browser.close()
    return shots


def find_before(before_dir: Path, screens: dict[str, str]) -> dict[tuple[str, int], Path]:
    found: dict[tuple[str, int], Path] = {}
    pngs = sorted(before_dir.glob("*.png"))
    for name in screens:
        for w, _ in SIZES:
            match = [p for p in pngs if name in p.stem and str(w) in p.stem and "after" not in p.stem]
            if match:
                found[(name, w)] = match[0]
    return found


def shrink(src: Path, dst: Path) -> Path:
    from PIL import Image

    with Image.open(src) as im:
        im = im.convert("RGB")
        if im.width > THUMB_WIDTH:
            im = im.resize((THUMB_WIDTH, round(im.height * THUMB_WIDTH / im.width)), Image.LANCZOS)
        im.quantize(colors=256, method=Image.Quantize.MEDIANCUT).save(dst, optimize=True)
    return dst


def side_by_side(pairs: list[tuple[str, Path | None, Path]], dst: Path, title: str) -> None:
    from PIL import Image, ImageDraw

    label_h, pad = 28, 12
    rows = []
    for label, before, after in pairs:
        b = Image.open(before).convert("RGB") if before else None
        a = Image.open(after).convert("RGB")
        rows.append((label, b, a))
    col_w = max(a.width for _, _, a in rows)
    height = label_h + sum(label_h + max(a.height, b.height if b else 0) + pad for _, b, a in rows)
    sheet = Image.new("RGB", (col_w * 2 + pad * 3, height), "#0b0f17")
    draw = ImageDraw.Draw(sheet)
    draw.text((pad, 8), title, fill="#e6e9f2")
    y = label_h
    for label, b, a in rows:
        draw.text((pad, y + 8), f"BEFORE  {label}", fill="#9aa3b5")
        draw.text((col_w + pad * 2, y + 8), f"AFTER  {label}", fill="#e6e9f2")
        y += label_h
        if b:
            sheet.paste(b, (pad, y))
        else:
            draw.text((pad, y + 8), "(no before image)", fill="#f87171")
        sheet.paste(a, (col_w + pad * 2, y))
        y += max(a.height, b.height if b else 0) + pad
    sheet.quantize(colors=256, method=Image.Quantize.MEDIANCUT).save(dst, optimize=True)


def write_page(folder: Path, feature: str, pairs, notes: str, before_label: str, image: str) -> Path:
    cards = []
    for label, before, after in pairs:
        b = f'<img src="{html.escape(before.name)}" alt="before: {html.escape(label)}">' if before else "<p>(none)</p>"
        cards.append(
            f'<section><h2>{html.escape(label)}</h2><div class="pair">'
            f"<figure>{b}<figcaption>Before</figcaption></figure>"
            f'<figure><img src="{html.escape(after.name)}" alt="after: {html.escape(label)}">'
            f"<figcaption>After</figcaption></figure></div></section>"
        )
    page = f"""<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>PoSoccer: {html.escape(feature)}</title>
<style>body{{margin:0;background:#0b0f17;color:#e6e9f2;font:14px/1.5 Inter,system-ui,sans-serif}}
main{{max-width:1360px;margin:0 auto;padding:20px 16px}}.note{{color:#9aa3b5}}
.pair{{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:16px}}
figure{{margin:0;background:#121826;border:1px solid #232c3f;border-radius:10px;padding:8px}}
img{{max-width:100%;height:auto;display:block}}figcaption{{color:#9aa3b5;margin-top:6px}}</style></head>
<body><main><h1>PoSoccer: {html.escape(feature)}</h1>
<p class="note">Before: {html.escape(before_label)}. After: local WebGL build of the working tree, captured
{dt.datetime.now().strftime("%Y-%m-%d %H:%M")} at 1366x768 and 390x844 by scripts/screenshots.py.
All pairs in one image: <a href="{html.escape(image)}" style="color:#e6e9f2">{html.escape(image)}</a>.</p>
<p>{html.escape(notes)}</p>
{"".join(cards)}</main></body></html>"""
    path = folder / "index.html"
    path.write_text(page, encoding="utf-8")
    return path


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--feature", required=True, help="short name, used in the folder and image names")
    ap.add_argument("--before-dir", type=Path, help="folder of 'before' PNGs (name holds screen + width)")
    ap.add_argument("--before-build", type=Path, help="another local WebGL build folder to capture as 'before'")
    ap.add_argument("--build", type=Path, default=ROOT / "Builds" / "WebGL", help="the 'after' WebGL build")
    ap.add_argument("--screens", default="home=/,diag=/diag", help="name=route pairs, comma separated")
    ap.add_argument("--wait", type=float, default=60, help="seconds the game page gets to load")
    ap.add_argument("--notes", default="", help="what changed, shown on the page")
    args = ap.parse_args(argv)

    if not (args.build / "index.html").exists():
        sys.exit(f"No WebGL build at {args.build}. Run: pwsh -File scripts/publish_webgl.ps1 -NoPublish")
    if not args.before_dir and not args.before_build:
        sys.exit("Give --before-dir (PNGs) or --before-build (a WebGL build folder).")
    screens = dict(s.split("=", 1) for s in args.screens.split(","))
    feature = "".join(c if c.isalnum() or c in "-_" else "-" for c in args.feature)
    folder = ROOT / "SCREENSHOTS" / f"{dt.date.today():%Y%m%d}_{feature}"
    raw = folder / "_raw"
    raw.mkdir(parents=True, exist_ok=True)

    try:
        import playwright  # noqa: F401
    except ImportError:
        sys.exit("Run through: uv run --with playwright --with pillow python scripts/screenshots.py ...")
    subprocess.run([sys.executable, "-m", "playwright", "install", "chromium"], check=True)

    after_raw = capture(args.build, screens, raw, "after", args.wait)
    if args.before_build:
        before_raw = capture(args.before_build, screens, raw, "before", args.wait)
        before_label = f"local WebGL build {args.before_build}"
    else:
        before_raw = find_before(args.before_dir, screens)
        before_label = f"PNGs from {args.before_dir.name}/"

    pairs = []
    for name in screens:
        for w, h in SIZES:
            after = shrink(after_raw[(name, w)], folder / f"{name}-{w}-after.png")
            src = before_raw.get((name, w))
            before = shrink(src, folder / f"{name}-{w}-before.png") if src else None
            pairs.append((f"{name} {w}x{h}", before, after))
    for p in raw.iterdir():
        p.unlink()
    raw.rmdir()

    image = f"{feature}.png"
    side_by_side(pairs, folder / image, f"PoSoccer {feature}: before | after")
    page = write_page(folder, feature, pairs, args.notes, before_label, image)
    print(f"Wrote {page.relative_to(ROOT)} and {image}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
