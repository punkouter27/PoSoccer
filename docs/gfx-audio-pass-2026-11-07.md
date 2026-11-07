# GFX / audio pass: 2026-11-07 (options 21-30)

Written without a running editor: no Unity licence was active on the machine, so
nothing below has been compiled, run or listened to yet. Read the "unverified" column
before trusting any of it. The 2026-09-06 record (`gfx-audio-pass-2026-09-06.md`)
still governs: no full-screen passes on the 2D renderer, every UV term through
`PoSoccerLocalUV`, and continuous cost is shed before momentary meaning.

| # | Item | Where | Unverified |
|---|---|---|---|
| 21 | Floodlight cast shadows, desktop Ultra only | `Agent_Stadium.SetCastShadows` | `Light2D.shadowsEnabled` / `shadowIntensity` / `ShadowCaster2D.selfShadows` compiled in July 2026 (commit 93983eb); not since the URP update |
| 22 | Turf micro-detail: blade grain + mow-direction sheen | `PoSoccerFX.hlsl`, `_TurfDetail` / `_TurfScale`, `Agent_Surfaces.TurfDetailEnabled` | shader compile; the look |
| 23 | Ball spin made visible (swirl arcs) | `Agent_BallSpin` (new, per-pitch) | needs an installer line |
| 24 | Divots on planted cuts and brakes; Ultra clods and double spray | `Agent_ParticleFX` | the 0.5 g threshold against real play |
| 25 | Ultra tier above tier 0, graded on a 30 fps p95 floor | `Agent_Quality.UltraActive` | the floor on the dev PC |
| 26 | Penetration-depth PlayMode test | `Agent_PlayMode_Penetration` | the 30 mm tolerance is a first bar |
| 27 | Modal impact synthesis | `Agent_ImpactSynth` (new, built by `Agent_Audio`) | the sound, and the audio-thread callback |
| 28 | Skid voice and impact scuffs keyed on lateral slip | `Agent_Audio.UpdateSkid`, `Agent_ImpactFX.EmitScuff` | levels |
| 29 | Stadium reverb zone with a distance-scaled send | `Agent_Audio.BuildStadiumSpace`, `ReverbSend` | `AudioReverbZone` int/float field types |
| 30 | Crowd gasps on missed chances, scaled by xG | `Agent_Crowd.ReactToChances`, `Agent_Audio.Gasp` | the levels |

## Two findings

**The skid voice and the impact scuffs never fired.** Both were gated on
`Agent_Soccer.TractionSaturation` above 0.85 and 0.9. The CLAUDE.md landmine
already records that this ratio cannot pass 0.59 on the shipped bodies. Both now key
on `LateralSlipSpeed`, the signal `Agent_Skids` and the turf spray already used. So
what is heard, what is drawn and what is sprayed are now the same event.
`Agent_Wear` keys on saturation above 0.35, which can be reached, and is unchanged.

**The ball already curves.** `Agent_EnvController.magnusScale` (0.0005) applies a
Magnus-lite force, so option 23's warning that "adding Magnus is a physics change"
was moot: it is in the physics every brain trained under. Option 23 only makes the
spin that causes the curve visible. It never writes to the body.

## Ultra, and why it is a flag

`Agent_Quality.Tier` keeps its 0..3 meaning, because the telemetry overlay indexes
a table with it. Ultra is a separate `UltraActive` flag that is only true at tier 0,
and only on a desktop that is not WebGL and has at least 2 GB of VRAM. While Ultra
is on, the normal ladder pauses and the only bar is a 30 fps p95 floor. Three bad
windows shed Ultra, before the wear goes. Recovery re-arms it after the usual
twelve good windows at tier 0. Two shed events in one scene latch it off, so the
controller does not oscillate between 25 and 60 fps.

## Impact synthesis, in one paragraph

There are four resonant modes per material, plus a contact noise burst. The
materials are leather (ball on boot), boards, post, body (player on player) and net
(struck on a goal, since the net has no collider). The impulse sets the level and
also the contact duration. A harder hit means a shorter pulse, and a shorter pulse
excites the high modes more, so a hard shot is brighter, not only louder. Pan and
distance (level plus air absorption) are applied per voice. The sample banks remain
the fallback (`Agent_Audio._synthesiseImpacts`). The main thread hands strikes to
the audio thread through a single-producer, single-consumer ring, and the callback
allocates nothing.
