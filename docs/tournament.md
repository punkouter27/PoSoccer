# Tournament mode and Elo

**TOURNAMENT** on the main menu opens a 16-slot knockout bracket with five columns:
round of 16, quarter-finals, semi-finals, final and champion.

- **Entrants.** The bracket is filled with every character that has a usable brain.
  Today that is STANDARD, MATT, KIM and NICK. Empty slots are **BYE**. Entrants are
  seeded by Elo into the standard 16-seed layout, so byes go to the top seeds and the
  strongest players meet last. A match against a BYE is decided without being played.
- **PLAY NEXT** plays the next match: a 1 v 1, first to the HUD's `matchGoals`, with
  the player introduction card at kickoff.
- **AUTO** plays every remaining match unattended. At full time the result card counts
  down 6 s and returns to the bracket, and the bracket starts the next match after 3 s.
  This continues until there is a champion, then AUTO switches itself off.
- **NEW** reseeds a fresh bracket by current Elo. **ELO** shows the rating table.
- **Draws.** A draw (only possible on the time limit) counts for Elo but isn't a
  result. The same pairing is played again.

**Elo** (`Agent_Elo`): every character starts at 1500, K = 32, and expected score
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
