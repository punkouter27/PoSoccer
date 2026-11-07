# Articulated 2D legs: design (2026-11-07)

Status: **design only, not started.** This is item 48 of the 2026-11-07 options list and
the plan for open deviation #2 in `docs/rules-exemptions.md` ("No active-ragdoll
articulation"). Nothing in the runtime changes until this phase is scheduled. The
decision to do it is the owner's; this page is what it would take.

## What exists today

- Each player is one `Rigidbody2D` disc driven by one foot force and one torque
  (`Agent_Soccer.OnActionReceived`): traction circle `mu * m * g`, force slew, active
  braking, anisotropic drag, a speed-dependent turn cap, stamina.
- `Agent_Limbs` draws two leg quads whose stride phase is integrated from forward
  speed. The legs are presentation only: no collider, no mass, no joint.
- The brain contract is 34 vector observations x 2 stacked + 105 ray inputs = 173,
  and 4 continuous actions (forward, lateral, turn, boost), decision period 8.
- STANDARD passes the benchmark on this body (91.0% wins, 6.0% stalemates, n=2,000). MATT, KIM and NICK
  do not (21-27%, p31).

## The body

The pitch stays top-down and 2D (exemption #1 is permanent; the owner rejected a Z
axis on 2026-08-02). "Articulated" therefore means joints in the ground plane:

| Segment | Shape (inside the sprite) | Mass share | Joint to parent | Limits |
|---|---|---|---|---|
| Pelvis (root) | capsule, 0.36 x 0.22 m | 0.50 | - | - |
| Left thigh | capsule, 0.42 x 0.12 m | 0.10 | `HingeJoint2D` at hip | -35 to +60 deg about the facing axis |
| Right thigh | capsule | 0.10 | hip | mirrored |
| Left shin+foot | capsule, 0.45 x 0.10 m | 0.05 | `HingeJoint2D` at knee | 0 to +80 deg |
| Right shin+foot | capsule | 0.05 | knee | mirrored |
| Torso/arms (lumped into pelvis) | - | 0.20 | - | - |

Shares are from Winter's segment tables, collapsed to five bodies; total mass stays
the profile's `bodyMass` (75 x bodyScale^2). In 2D a hinge about the plane normal is
a leg swinging forward and back as seen from above, which is what a top-down camera
sees of a stride; knee flexion is foreshortening of the shin.

Collision (AGENTS.md section 2): primitive capsules only, sized inside the sprite.
All non-adjacent pairs collide (thigh-thigh, shin-shin, shin-opposite thigh); parent
and child do not (`HingeJoint2D.enableCollision = false`). Validation before any
training: zero overlap at rest, at both limits of every joint, and through a scripted
stride sweep, measured by a PlayMode test with `Physics2D.Distance` on every pair.

Motors: each hinge gets a motor whose `maxMotorTorque` is scaled to segment mass
(hip ~180 N m, knee ~120 N m at 75 kg, published isometric peaks scaled to a
top-down projection) and a joint speed cap of ~700 deg/s (hip) and ~1000 deg/s
(knee). Ground propulsion stays on the traction circle: a foot in its stance phase
(contact flag below) can push the pelvis with at most `mu * m_total * g`, shared
across both feet. That keeps everything the current body gets right (cut costs
speed, braking uses spare grip) and makes the feet the reason for it.

## Interface change

This breaks the frozen interface, so per AGENTS.md ("If the interface must break,
distil the old policy into the new layout") it gets a new version and a distillation
step rather than a silent edit.

- `Agent_Realism.INTERFACE_VERSION` -> `iface-<date>-legs`;
  `PHYSICS_VERSION` -> `phys-<date>-legs`. Both get new rows in
  `Agent_EditMode_CheckpointTags`.
- Observations, appended at the END of the vector block (append-only rule):
  per joint angle and angular velocity (4 joints x 2 = 8), per foot contact flag (2),
  pelvis angular velocity is already present. 34 -> 44 vector floats; with 2 stacks
  68 -> 88; model inputs 173 -> 193. Ray sensors unchanged.
- Actions: the four existing channels keep their meaning as COMMANDS (desired forward
  and lateral velocity, turn, boost). Four new channels, appended: hip and knee
  target offsets per leg, added to a scripted gait generator's targets. 4 -> 8
  continuous actions. A policy that outputs zero on the new channels walks with the
  scripted gait, which is what makes distillation possible.
- Zero-initialised weights on the appended inputs and outputs (AGENTS.md "Frozen
  interface"), so the distilled starting policy behaves exactly like the old one.

## Training plan

1. Body first, no learning: the scripted gait generator drives the hinges from the
   commanded velocity; the existing heuristic bot drives the commands. Gate: the
   movement probe's chase numbers within 10% of today's (9.54 m/s sprint, arrival
   time), zero self-overlap, zero NaN.
2. Distil each deployed brain into the new layout: copy the 173-input weights,
   zero-init the 20 appended inputs and the 4 appended outputs, then behaviour-clone
   on the new body against the old policy's actions for ~2M steps. Gate: the distilled
   brain's grade within the n=1000 noise band of its parent on the new body.
3. Fine-tune each character on its own (AGENTS.md: no character reuses another's
   brain): STANDARD first, bot-only at full strength, 10M steps a run, graded at
   n=1000 after each, until the bar holds. Then MATT, KIM, NICK the same way.
4. Re-baseline: bot-vs-bot on the new body, because the benchmark bot is a body too.
   The 80% bar is defined against bot v1 on the same body the learner has.

Cost estimate: one to two weeks of engineering for steps 1-2, then roughly 10-20 h of
training per character (540-1000 s per million steps at -NumEnvs 4, 40-60M steps).

## Policies touched (AGENTS.md "Before any change")

Body physics -> every policy acting on that body: all four trained brains and the
scripted bot's tuning. Nothing else.

## Risks

- The bot is tuned on the disc body; on legs it may get weaker or stronger, which
  moves the bar. Step 4 exists for this.
- Joint-space chatter: needs the action-rate penalty (`action_rate`, already a config
  lever) on the new channels from the first run.
- Throughput: five bodies per player instead of one. The trainer thread is the
  bottleneck today (docs/ml-audit-2026-09-13.md), so physics cost should not dominate,
  but measure before promising the estimate above.
