"""The WebGL publish config: scripts/webgl.json and the Static Web App routes the page relies on."""

import json
import re


def load(root, rel):
    return json.loads((root / rel).read_text(encoding="utf-8"))


def test_webgl_json_has_build_method_and_app(root):
    cfg = load(root, "scripts/webgl.json")
    assert cfg["buildMethod"].endswith(".Build")
    assert cfg["staticWebApp"] and cfg["resourceGroup"]


def test_diag_checks_cover_every_external_call(root):
    """Each external URL the game code calls has a live /diag probe."""
    cfg = load(root, "scripts/webgl.json")
    probes = " ".join(c["url"] for c in cfg["diagChecks"])
    sources = "\n".join(p.read_text(encoding="utf-8-sig") for p in (root / "Assets/Scripts").rglob("*.cs"))
    hosts = set(re.findall(r'"https://([a-z0-9.-]+)/', sources))
    assert hosts, "expected at least the Open-Meteo call"
    for host in hosts:
        assert host in probes, f"{host} is called by the game but /diag does not probe it"
    for check in cfg["diagChecks"]:
        assert check["section"] in {"API", "Data", "Services"}
        assert check["url"].startswith("https://")


def test_static_web_app_serves_diag_and_unityweb(root):
    swa = load(root, "scripts/webgl/staticwebapp.config.json")
    assert ".unityweb" in swa["mimeTypes"]
    assert any(r["route"] == "/diag" and r["rewrite"] == "/diag.html" for r in swa["routes"])


def test_one_workflow_without_sync_wording(root):
    flows = list((root / ".github/workflows").glob("*.y*ml"))
    assert [f.name for f in flows] == ["webgl.yml"]
    text = flows[0].read_text(encoding="utf-8")
    assert "SAME FILE" not in text and "sync-ci" not in text
    assert "scripts/publish_webgl.ps1" in text
