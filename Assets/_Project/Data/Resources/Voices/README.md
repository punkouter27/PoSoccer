Recorded character voices go here: <NAME>_<MOOD>_<line>_<level>.wav (e.g. MATT_INSULT_3_5.wav),
or the cross-project standard <name>/<emotion>-<level>.wav (e.g. matt/celebrate-5.wav).
Moods: HAPPY, ANGRY, HURT, INSULT, CELEBRATE; levels 1-5.
Any sound without a WAV is synthesised at runtime. See docs/voices.md.
voices.json lists every character x emotion x level, the file names and whether each exists;
the editor rebuilds it whenever a WAV here changes (or PoSoccer > Voices > Rebuild voices.json).
Missing files play the procedural imitation from the character's "voice" profile.
