# Tournament mode and Elo

**2026-11-22, tournament standard** (shared across the Po games): bracket only, no FFA (a
two-sided game has no free-for-all); seeds pair 1v16, 8v9, 4v13, 5v12, 2v15, 7v10, 3v14, 6v11 top
to bottom (the 8-slot layout is 1v8, 4v5, 2v7, 3v6; the 4-slot one 1v4, 2v3); every slot with no
player reads **BYE** on the bracket and on the menu's 4x4 grid; a draw sends the higher seed
through; Elo starts at 1000 (was 1500), K = 32. The seed table was reordered to this layout (it
used 1v16, 8v9, 5v12, 4v13, 6v11, 3v14, 7v10, 2v15 before: the same pairs, other order); a cup saved
before then keeps its slots.

**2026-11-09:** the menu has two cups. Pick **1v1 CUP** or **4v4 CUP** in the mode bar, then
**BRACKET**. The 1v1 cup is the 16-slot bracket described below; untrained characters are BYEs.
The **4v4 cup** drafts the trained characters into up to four teams of four with a snake
draft by Elo (`Agent_Tournament.DraftTeams`; players who don't make a full team sit out),
names each team after its first pick, and plays semi-finals and a final in a 4-slot bracket
saved to `persistentDataPath/tournament-4v4.json`. It needs 8 trained characters. Team
matches don't change Elo, which stays a 1v1 rating.

The 1v1 cup opens a 16-slot knockout bracket with five columns:
round of 16, quarter-finals, semi-finals, final and champion.

- **Entrants.** The bracket is filled with every character that has a usable brain.
  Today that is STANDARD, MATT, KIM and NICK. Empty slots are **BYE**. Entrants are
  seeded by Elo into the standard 16-seed layout, so BYEs take the bottom seeds (the top
  seeds get the byes) and the strongest players meet last. A match against a BYE is
  decided without being played; BYE vs BYE sends a BYE on.
- **PLAY NEXT** plays the next match: a 1 v 1, first to the HUD's `matchGoals`, with
  the player introduction card at kickoff.
- **Unattended by default (2026-11-09).** Starting a cup (opening a new bracket, NEW, or
  PLAY NEXT) switches **AUTO** on, so the cup plays itself to a champion with no taps: the
  full-time screen stays up for 5 s, the game returns to the bracket, and the next match
  starts 3 s later. Tap **AUTO** to pause; PLAY NEXT resumes. AUTO switches itself off
  when there is a champion, and the champion's full-time screen stays up.
- **NEW** reseeds a fresh bracket by current Elo. **ELO** shows the rating table.
- **Draws.** A draw (only possible on the time limit) counts as a draw for Elo and
  sends the higher seed (lower seed number) through (`Agent_Bracket.RecordDraw`). Until
  2026-11-22 the pairing was replayed.

**Elo** (`Agent_Elo`): every character starts at 1000 (1500 before 2026-11-22; ratings
already saved in `elo.json` keep their values), K = 32, and expected score
is the standard logistic curve. Every finished 1 v 1 between two named characters is
recorded, whether it's a tournament match or an exhibition. Ratings live in
`persistentDataPath/elo.json` and the bracket in `persistentDataPath/tournament.json`.
Under batchmode, which covers the headless test runner, both stay in memory and
are never written.

| Piece | Where |
|---|---|
| Bracket rules (pure, EditMode-tested) | `Agent_Bracket` |
| Tournament state across scene loads | `Agent_Tournament` |
| Bracket screen | `Agent_BracketView` (raised by `Agent_MainMenu`) |
| Result, Elo and auto-return at full time | `Agent_TournamentMatch` (match scene, code-installed) |
| Kickoff versus card | `Agent_MatchIntro` |
| Tests | `Agent_EditMode_Tournament`, `Agent_PlayMode_TournamentFlow` |
