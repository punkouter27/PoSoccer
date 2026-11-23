# Character voices

Every character has five moods (the tournament standard's emotions: angry, happy, hurt,
insult, celebrate), five lines per mood and five levels of expressiveness per line:
5 x 5 x 5 = 125 sounds per character. The 17 characters (STANDARD plus the 16 with faces)
come to 2,125 sounds. CELEBRATE was added on 2026-11-22; the four older moods are unchanged.

| Mood | When it plays (`Agent_Voices`) |
|---|---|
| HAPPY | The scorer, right after a goal. Level 5 for the winning goal, 4 for an equaliser, 3 for taking the lead, 2 otherwise. |
| ANGRY | A player on the side that conceded, 0.7 s later. Level 5 if that goal lost the match. Also one player, level 1–2, on a stalemate. |
| HURT | A player hit hard in a collision. The level scales with the impulse. |
| INSULT | The scorer taunts the conceding player 1.7 s after the goal. Level 5 for the winning goal. |
| CELEBRATE | A teammate of the scorer (the scorer in a 1 v 1) 2.6 s after a goal: level 1 still behind, 2 level, 3 ahead, 4 if it won the match. At full time a match won on the clock gets one level-4 cheer; a cup win has the whole winning side cheer at level 5 (`Agent_Voices.CelebrateResult`, called by `Agent_TournamentMatch`). Distinct synth profile: long climbing "woo-hoo" syllables. |

Each player has a 1.2 s cooldown, so a scramble doesn't turn into a choir. Voices play
only in a match; they never play in training, evaluation or headless runs.

## Adding a recorded sound

Drop a WAV into `Assets/_Project/Data/Resources/Voices/` named

    <NAME>_<MOOD>_<line>_<level>.wav        e.g.  MATT_INSULT_3_5.wav

- `NAME` is the character's `playerName`, in upper case: STANDARD, MATT, KIM, NICK,
  ALAN, BAILY, BRENDAN, DECOY, DUPEE, JH, JOSH, MANZI, MAYA, PORTO, SOKOL, TONG or VELEZ.
- `MOOD` is HAPPY, ANGRY, HURT, INSULT or CELEBRATE.
- `line` is 1–5: five different remarks.
- `level` is 1–5, from 1 (the weakest, a mutter) to 5 (the strongest, a shout).

The cross-project standard name also works, one take for all five lines of an emotion
and level, in a folder per character (lower case):

    <name>/<emotion>-<level>.wav               e.g.  matt/celebrate-5.wav

Lookup order: the per-line `<NAME>_<MOOD>_<line>_<level>` take, then
`<name>/<emotion>-<level>`, then the synthesiser.

A recorded WAV always wins. Any sound that has no WAV is rendered at runtime by
`Agent_VoiceSynth`, a formant voice synthesiser, so the whole set exists from day one:

- **Each character** sounds different: its `voicePitch` on `Reward_Settings` moves both
  the fundamental and the formants.
- **Each line** differs in its vowels, syllable count and rhythm.
- **Each level** raises loudness, length, pitch range and roughness together.

Keep recordings short (under a second), mono, and normalised. The level is meant to be
audible as loudness as well as delivery.
