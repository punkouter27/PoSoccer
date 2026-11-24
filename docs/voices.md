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

## The manifest: `voices.json`

`Assets/_Project/Data/Resources/Voices/voices.json` (schema `po-voice-manifest/1`, shared by
the Po games) is the one place that says which file every character uses. It lists every face
character (the 16 of the menu grid; STANDARD and BYE slots are not listed) x 5 emotions x 5
levels. Each cell names the standard file `<name>/<emotion>-<level>.wav` and, under `takes`,
the five per-line files `<NAME>_<MOOD>_<line>_<level>.wav`, each with an `exists` flag, plus the
character's `voice` profile (pitchHz, formantScale, breath, rasp, vibratoHz, vibratoDepth,
tempo, timbre).

- **Adding a WAV:** drop it into the folder. The editor rebuilds `voices.json` on every WAV
  import, move or delete (`Editor_VoicePack`), or run **PoSoccer > Voices > Rebuild
  voices.json**. The log says "N of M clips recorded; missing: ...". Never edit `exists` by
  hand; you may tune a `voice` profile by hand, and a rebuild keeps it.
- **What plays** (`Agent_Voices.ClipFor`): the per-line take if it exists, else the standard
  file if it exists, else the advanced procedural imitation driven by the character's `voice`
  profile. A file the manifest calls missing is never loaded. With no valid manifest, every
  voice is procedural (one warning). STANDARD (not listed) gets a profile derived from its
  `voicePitch`.
- `tests/test_voice_manifest.py` (pytest, CI) and `Agent_EditMode_VoiceManifest` check that
  the file covers every face character x 5 x 5 and that the `exists` flags match the disk.

## The procedural imitation (`Agent_VoiceSynth`)

Rewritten 2026-11-22 from a buzz-through-formants placeholder into a short human
vocalisation by that character:

- **Source:** Rosenberg glottal pulses at the profile's `pitchHz`, with jitter, shimmer,
  breath noise and a subharmonic plus growl for `rasp`.
- **Filter:** four formant resonators that glide between vowels (a, e, i, o, u, schwa),
  scaled by `formantScale`.
- **Syllables** with consonant onsets: h/s/sh/f noise, p/t/k bursts, an n murmur and w/y glides.
- **Emotions:** ANGRY is tense, falling and raspy ("hah!", "kah-rrah"). HAPPY is a bright
  "heh" that becomes laughter from level 3. HURT is a gasp then an "oof"/"ugh" with a pitch
  break, and a long cry at level 5. INSULT is a sneering sing-song "nyah-nyah", with "pfft"
  from level 4. CELEBRATE is a "yeah!" or "woo-hoo!" with a held vibrato vowel, and a laugh
  from level 4.
- **Levels:** each level adds syllables, length, pitch range, rasp, breath, vibrato and loudness.
- **Takes and characters:** each per-line take varies slightly, and every character sounds
  different (profiles differ even at equal pitch).
- **Deterministic and cached:** the seed is (character, emotion, level, take). Each match
  warms its players' clips one per frame; output is peak-normalised so procedural and recorded
  clips sit at similar loudness.

Keep recordings short (under a second), mono, and normalised. The level is meant to be
audible as loudness as well as delivery.
