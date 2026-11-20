# PoSoccer

Top-down 2D physics soccer in Unity 6 whose players are brains trained with Unity ML-Agents, and a
training benchmark (beat the rule-based bot in 80% of eval episodes).

- Play: https://ambitious-plant-09c70a10f.4.azurestaticapps.net/ (status: `/diag`)
- Start here: [AGENTS.md](AGENTS.md) (the Po Game Standard and repo notes), then
  [docs/project-notes.md](docs/project-notes.md).
- Publish the WebGL build: `pwsh -File scripts/publish_webgl.ps1`.
- Python checks: `uvx ruff check .`, `uvx ruff format --check .`, `uvx --with pytest pytest`.
