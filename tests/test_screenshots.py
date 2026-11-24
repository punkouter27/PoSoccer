"""scripts/screenshots.py: pairs 'before' PNGs with screens by name and width (no browser needed)."""

from pathlib import Path

import screenshots


def test_find_before_matches_screen_and_width(tmp_path: Path):
    for name in ("before-home-1366.png", "before-home-390.png", "before-diag-1366.png", "after-diag-390.png"):
        (tmp_path / name).write_bytes(b"")
    found = screenshots.find_before(tmp_path, {"home": "/", "diag": "/diag"})
    assert found[("home", 1366)].name == "before-home-1366.png"
    assert found[("home", 390)].name == "before-home-390.png"
    assert found[("diag", 1366)].name == "before-diag-1366.png"
    assert ("diag", 390) not in found, "an 'after' image is never used as 'before'"


def test_captures_phone_and_desktop():
    assert (1366, 768) in screenshots.SIZES
    assert (390, 844) in screenshots.SIZES
