# PoSoccer

Top-down 2D physics soccer in Unity 6 whose players are brains trained with Unity ML-Agents, and a
training benchmark (beat the rule-based bot in 80% of eval episodes).

- Play: https://ambitious-plant-09c70a10f.4.azurestaticapps.net/ (status: `/diag`)
- Start here: [AGENTS.md](AGENTS.md) (the Po Game Standard and repo notes), then
  [docs/project-notes.md](docs/project-notes.md).
- Publish the WebGL build: `pwsh -File scripts/publish_webgl.ps1`.
- Python checks: `uvx ruff check .`, `uvx ruff format --check .`, `uvx --with pytest pytest`.

## Tournament standard

Shared across the Po games (details in [docs/tournament.md](docs/tournament.md) and
[docs/voices.md](docs/voices.md)):

- **Bracket only, no FFA.** PoSoccer is a two-sided game, so it has no free-for-all mode; every
  cup is a 16-slot single-elimination bracket seeded by rating (1v16, 8v9, 4v13, 5v12, 2v15, 7v10,
  3v14, 6v11). BYEs take the bottom seeds, a BYE's opponent advances unfought, BYE vs BYE sends a
  BYE on, a draw goes to the higher seed, and the cup plays itself to a champion.
- **BYE** labels every slot with no player: the 4x4 main-menu grid and the bracket. A real character
  without a trained brain stays on the grid as **IN TRAINING** (and is a BYE in the cup).
- **Elo** starts at 1000 with K = 32, saved to `persistentDataPath/elo.json`. The start was 1500
  until 2026-11-22: ratings already saved keep their values; only characters with no saved entry
  start at 1000. Delete `elo.json` for a fresh league on the new scale.
- **Voices:** 5 emotions (angry, happy, hurt, insult, celebrate) x 5 levels for every character,
  with a procedural fallback until WAVs exist.
