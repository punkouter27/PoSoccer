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
- **Demo Elo:** every completed AI exhibition or demo (attract) match, 1v1 or 4v4, updates the same
  `elo.json` and shows each player's rating and change on the full-time card. 4v4 is pairwise (each
  winner vs each loser, K divided by the opposing team size); a demo draw is 0.5 each; a demo left
  early records nothing; a character never plays itself. The 4v4 cup itself still leaves Elo alone.
- **Voices:** 5 emotions (angry, happy, hurt, insult, celebrate) x 5 levels for every character.
  `Assets/_Project/Data/Resources/Voices/voices.json` lists every character's files and whether
  each exists. To add a WAV, drop it in that folder; the editor rebuilds the manifest on import
  (or run **PoSoccer > Voices > Rebuild voices.json**). A missing file plays an advanced
  procedural imitation of that character's voice ([docs/voices.md](docs/voices.md)).
