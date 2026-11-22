using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PoSoccer
{
    /// <summary>
    /// 1-Player human controller for a generic Blue box in 1v1 or 4v4 matches.
    ///
    /// STEERING (Screen-Relative):
    ///  - WASD / Arrow Keys / Gamepad Left Stick move directly Up/Down/Left/Right on
    ///    the pitch and automatically align the box's forward eye axis (+Y) toward
    ///    the desired movement direction.
    ///  - Left/Right Shift or K (or Gamepad East / Left Shoulder) activates Sprint/Boost.
    ///  - Space or J (or Gamepad South / Right Trigger) triggers an active Kick / Tackle
    ///    lunge on a short cooldown, propelling the box forward and striking the ball
    ///    when within <see cref="KICK_RADIUS"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Agent_HumanController : MonoBehaviour
    {
        public const string HUMAN_PLAYER_NAME = "YOU";
        public const float KICK_RADIUS = 1.45f;
        public const float KICK_COOLDOWN = 0.45f;
        public const float KICK_BALL_IMPULSE = 4.2f;
        public const float LUNGE_BODY_IMPULSE = 14f;
        public const float MAX_LUNGE_SPEED = 11.5f;

        static Reward_Settings s_humanProfile;

        Agent_Soccer _agent;
        float _nextKickTime;
        float _lastKickTime = -10f;
        bool _kickQueued;
        bool _touchKickPending;
        Transform _ringTransform;
        SpriteRenderer _ringRenderer;

        /// <summary>Virtual on-screen thumbstick movement vector (world XY).</summary>
        public Vector2 TouchMoveInput { get; set; }

        /// <summary>Virtual on-screen sprint/boost hold state.</summary>
        public bool TouchBoost { get; set; }

        /// <summary>Queues a kick/lunge from the on-screen KICK button.</summary>
        public void TriggerTouchKick() => _touchKickPending = true;

        /// <summary>Optional test override for screen-space movement input.</summary>
        public Vector2? InjectedMoveInput { get; set; }

        /// <summary>Optional test override for sprint/boost input.</summary>
        public bool? InjectedBoost { get; set; }

        /// <summary>Optional one-shot test trigger for the kick/lunge button.</summary>
        public bool InjectedKickPressed { get; set; }

        /// <summary>Timestamp (Time.time) when the next kick/lunge becomes available.</summary>
        public float NextKickTime => _nextKickTime;

        /// <summary>Timestamp (Time.time) when the most recent kick/lunge fired.</summary>
        public float LastKickTime => _lastKickTime;

        /// <summary>Current local X scale of the floor indicator ring (expands on kick lunge).</summary>
        public float CurrentRingScale => _ringTransform != null ? _ringTransform.localScale.x : 1.18f;

        /// <summary>
        /// Returns the shared runtime profile for the human-controlled generic Blue box:
        /// untextured (no face photo), standard 1.0x balanced physique, named "YOU".
        /// </summary>
        public static Reward_Settings GetOrCreateProfile()
        {
            if (s_humanProfile != null) return s_humanProfile;

            s_humanProfile = ScriptableObject.CreateInstance<Reward_Settings>();
            s_humanProfile.name = "Reward_HUMAN";
            s_humanProfile.playerName = HUMAN_PLAYER_NAME;
            s_humanProfile.playerColor = new Color(0.20f, 0.56f, 0.98f, 1f);
            s_humanProfile.faceTexture = null;
            s_humanProfile.bodyScale = 1f;
            s_humanProfile.bodyMass = 75f;
            s_humanProfile.brainModel = null;
            s_humanProfile.skills = "1P Human · WASD/Arrows move · Shift sprint · Space kick";
            s_humanProfile.personalityNotes = "Controlled live by the player.";
            s_humanProfile.hideFlags = HideFlags.DontSave;
            return s_humanProfile;
        }

        /// <summary>
        /// Configures <paramref name="agent"/> for live human control (or removes human
        /// control when <paramref name="human"/> is false).
        /// </summary>
        public static Agent_HumanController Configure(Agent_Soccer agent, bool human)
        {
            if (agent == null) return null;

            var existing = agent.GetComponent<Agent_HumanController>();
            if (!human)
            {
                if (existing != null) existing.enabled = false;
                return null;
            }

            if (existing == null) existing = agent.gameObject.AddComponent<Agent_HumanController>();
            existing.enabled = true;
            existing.Bind(agent);
            return existing;
        }

        void Awake()
        {
            if (_agent == null) _agent = GetComponent<Agent_Soccer>();
        }

        void OnEnable()
        {
            if (_agent == null) _agent = GetComponent<Agent_Soccer>();
            if (_agent != null) Bind(_agent);
        }

        void OnDisable()
        {
            if (_ringTransform != null) _ringTransform.gameObject.SetActive(false);
        }

        void Bind(Agent_Soccer agent)
        {
            _agent = agent;
            var behavior = agent.GetComponent<BehaviorParameters>();
            if (behavior != null)
            {
                behavior.Model = null;
                behavior.BehaviorType = BehaviorType.HeuristicOnly;
            }

            var bot = agent.GetComponent<Agent_HeuristicBot>();
            if (bot != null) bot.enabled = false;

            // Sample human input every physics tick so steering has zero decision-period lag.
            var requester = agent.GetComponent<DecisionRequester>();
            if (requester != null)
            {
                requester.DecisionPeriod = 1;
                requester.TakeActionsBetweenDecisions = true;
            }

            // Ensure no face sprite is left active from a previous character in this slot.
            var face = agent.transform.Find("Face");
            if (face != null) face.gameObject.SetActive(false);

            BuildHighlightRing();
        }

        void BuildHighlightRing()
        {
            if (Agent_Presentation.IsHeadless || _agent == null) return;

            var existing = _agent.transform.Find("HumanRing");
            if (existing != null)
            {
                _ringTransform = existing;
                _ringTransform.gameObject.SetActive(true);
                _ringRenderer = existing.GetComponent<SpriteRenderer>();
                return;
            }

            if (!_agent.TryGetComponent(out SpriteRenderer bodyRenderer)) return;

            var go = new GameObject("HumanRing");
            _ringTransform = go.transform;
            _ringTransform.SetParent(_agent.transform, false);
            _ringTransform.localPosition = new Vector3(0f, 0f, 0.005f);

            _ringRenderer = go.AddComponent<SpriteRenderer>();
            _ringRenderer.sprite = Agent_Art.Disc(1.1f, 0.80f);
            _ringRenderer.color = new Color(0.35f, 0.92f, 1f, 0.85f);
            _ringRenderer.sortingLayerID = bodyRenderer.sortingLayerID;
            _ringRenderer.sortingOrder = bodyRenderer.sortingOrder - 1;
            _ringTransform.localScale = new Vector3(1.18f, 1.18f, 1f);
        }

        void Update()
        {
            if (Agent_TimeFreeze.IsFrozen) return;

            if (ReadKickPressedThisFrame())
            {
                _kickQueued = true;
            }

            if (_ringRenderer != null)
            {
                UpdateRingVisual();
            }
        }

        void UpdateRingVisual()
        {
            float sinceKick = Time.time - _lastKickTime;
            float cooldown01 = Mathf.Clamp01((_nextKickTime - Time.time) / KICK_COOLDOWN);
            float burst01 = Mathf.Clamp01(sinceKick / 0.20f);
            float scale = 1.18f + 0.42f * Agent_Tween.Punch(burst01);
            if (_ringTransform != null)
            {
                _ringTransform.localScale = new Vector3(scale, scale, 1f);
            }

            if (sinceKick < 0.20f)
            {
                _ringRenderer.color = new Color(1f, 0.92f, 0.35f, 0.98f);
            }
            else if (cooldown01 > 0.01f)
            {
                _ringRenderer.color = new Color(0.55f, 0.72f, 0.85f, 0.45f);
            }
            else
            {
                float pulse = 0.78f + 0.14f * Mathf.Sin(Time.unscaledTime * 6f);
                _ringRenderer.color = new Color(0.35f, 0.92f, 1f, pulse);
            }
        }

        void FixedUpdate()
        {
            if (_agent == null || _agent.Body == null || Agent_TimeFreeze.IsFrozen) return;

            // Assist body rotation toward desired screen-space movement so high-speed cuts
            // align the forward eye axis quickly and unlock forward sprint authority.
            Vector2 moveInput = ReadWorldMoveInput();
            if (moveInput.sqrMagnitude > 0.01f)
            {
                float targetAngleDeg = Mathf.Atan2(-moveInput.x, moveInput.y) * Mathf.Rad2Deg;
                float currentAngleDeg = _agent.Body.rotation;
                float nextAngleDeg = Mathf.MoveTowardsAngle(
                    currentAngleDeg, targetAngleDeg, 540f * Time.fixedDeltaTime);
                _agent.Body.MoveRotation(nextAngleDeg);
            }

            if (_kickQueued)
            {
                _kickQueued = false;
                TryExecuteKickLunge(moveInput);
            }
        }

        /// <summary>
        /// Writes continuous actions [0..3] (forward, lateral, turn, boost) for
        /// <see cref="Agent_Soccer.Heuristic"/>.
        /// </summary>
        public void WriteHeuristicActions(ActionSegment<float> continuous)
        {
            if (_agent == null || _agent.Body == null) return;

            Vector2 worldMove = ReadWorldMoveInput();
            bool boost = ReadBoostHeld();
            Vector2 forward = _agent.transform.up;
            Vector2 right = _agent.transform.right;

            Vector4 actions = ComputeSteerActions(
                worldMove, forward, right, _agent.Body.angularVelocity, boost);
            continuous[0] = actions.x;
            continuous[1] = actions.y;
            continuous[2] = actions.z;
            continuous[3] = actions.w;
        }

        /// <summary>
        /// Executes the active Kick / Tackle lunge if the cooldown has elapsed.
        /// Returns true when the lunge fired.
        /// </summary>
        public bool TryExecuteKickLunge(Vector2 worldMoveInput)
        {
            if (_agent == null || _agent.Body == null) return false;
            if (Time.time < _nextKickTime) return false;

            _lastKickTime = Time.time;
            _nextKickTime = Time.time + KICK_COOLDOWN;
            if (_ringRenderer != null) UpdateRingVisual();

            Vector2 playerPos = _agent.Body.position;
            Vector2 forward = _agent.transform.up;
            Rigidbody2D ball = _agent.env != null ? _agent.env.Ball : null;

            // 1. Body lunge dash along movement input (or toward the ball / forward axis).
            Vector2 lungeDir = forward;
            if (worldMoveInput.sqrMagnitude > 0.01f)
            {
                lungeDir = worldMoveInput.normalized;
            }
            else if (ball != null && (ball.position - playerPos).sqrMagnitude <= 12f)
            {
                Vector2 toBall = ball.position - playerPos;
                if (toBall.sqrMagnitude > 0.001f) lungeDir = toBall.normalized;
            }

            _agent.Body.AddForce(lungeDir * LUNGE_BODY_IMPULSE, ForceMode2D.Impulse);
            if (_agent.Body.linearVelocity.sqrMagnitude > MAX_LUNGE_SPEED * MAX_LUNGE_SPEED)
            {
                _agent.Body.linearVelocity = _agent.Body.linearVelocity.normalized * MAX_LUNGE_SPEED;
            }

            // 2. Active ball strike if the ball is within kick reach.
            if (ball != null)
            {
                Vector2 kickImpulse = ComputeKickImpulse(
                    playerPos, forward, worldMoveInput, ball.position, KICK_RADIUS, KICK_BALL_IMPULSE);
                if (kickImpulse.sqrMagnitude > 0.0001f)
                {
                    ball.AddForce(kickImpulse, ForceMode2D.Impulse);
                    _agent.env.NotifyBallTouch(_agent);
                }
            }

            return true;
        }

        /// <summary>
        /// Pure screen-relative steering solver: maps a world-space movement vector
        /// (X = screen right, Y = screen up) into body-frame continuous actions
        /// (x = forward, y = lateral, z = turn, w = boost).
        /// </summary>
        public static Vector4 ComputeSteerActions(
            Vector2 worldMoveInput,
            Vector2 forwardAxis,
            Vector2 rightAxis,
            float angularVelocityDeg,
            bool boostWanted)
        {
            if (worldMoveInput.sqrMagnitude < 0.0025f)
            {
                return Vector4.zero;
            }

            Vector2 clamped = Vector2.ClampMagnitude(worldMoveInput, 1f);
            float mag = clamped.magnitude;
            Vector2 dir = clamped / mag;

            float forwardDot = Vector2.Dot(dir, forwardAxis);
            float rightDot = Vector2.Dot(dir, rightAxis);

            float move = Vector2.Dot(clamped, forwardAxis);
            float lateral = Vector2.Dot(clamped, rightAxis);

            // When nearly aligned with the target direction, bias drive onto the forward
            // channel so sprint boost (which requires forwardShare > 0.5) engages cleanly.
            if (forwardDot > 0.52f)
            {
                move = Mathf.Max(move, mag * 0.85f);
            }

            // Positive Z rotation in Unity 2D turns counter-clockwise (toward -rightAxis).
            float signedAngleDeg = Mathf.Atan2(-rightDot, forwardDot) * Mathf.Rad2Deg;
            float turn = Mathf.Clamp(signedAngleDeg / 28f - angularVelocityDeg * 0.0018f, -1f, 1f);

            float boost = boostWanted ? 1f : 0f;
            return new Vector4(
                Mathf.Clamp(move, -1f, 1f),
                Mathf.Clamp(lateral, -1f, 1f),
                turn,
                boost);
        }

        /// <summary>
        /// Pure kick impulse solver: returns the world-space impulse applied to the ball
        /// when the player triggers a kick at <paramref name="playerPos"/>, or
        /// <see cref="Vector2.zero"/> when the ball is outside <paramref name="kickRadius"/>.
        /// </summary>
        public static Vector2 ComputeKickImpulse(
            Vector2 playerPos,
            Vector2 playerForward,
            Vector2 worldMoveInput,
            Vector2 ballPos,
            float kickRadius = KICK_RADIUS,
            float maxBallImpulse = KICK_BALL_IMPULSE)
        {
            Vector2 toBall = ballPos - playerPos;
            float distSqr = toBall.sqrMagnitude;
            if (distSqr > kickRadius * kickRadius || distSqr < 0.00001f)
            {
                return Vector2.zero;
            }

            float dist = Mathf.Sqrt(distSqr);
            Vector2 toBallDir = toBall / dist;

            // Aim with movement input when held, blended with the player->ball line so a
            // kick never pulls the ball backward through the player's own body.
            Vector2 strikeDir = toBallDir;
            if (worldMoveInput.sqrMagnitude > 0.01f)
            {
                Vector2 aimDir = worldMoveInput.normalized;
                if (Vector2.Dot(aimDir, toBallDir) > -0.35f)
                {
                    Vector2 blended = aimDir * 0.65f + toBallDir * 0.35f;
                    if (blended.sqrMagnitude > 0.001f) strikeDir = blended.normalized;
                }
            }
            else if (playerForward.sqrMagnitude > 0.01f && Vector2.Dot(playerForward, toBallDir) > 0.2f)
            {
                Vector2 blended = playerForward.normalized * 0.4f + toBallDir * 0.6f;
                if (blended.sqrMagnitude > 0.001f) strikeDir = blended.normalized;
            }

            float falloff = Mathf.Lerp(1f, 0.6f, Mathf.Clamp01((dist - 0.55f) / Mathf.Max(0.01f, kickRadius - 0.55f)));
            return strikeDir * (maxBallImpulse * falloff);
        }

        Vector2 ReadWorldMoveInput()
        {
            if (InjectedMoveInput.HasValue)
            {
                return Vector2.ClampMagnitude(InjectedMoveInput.Value, 1f);
            }

            Vector2 v = Vector2.zero;
            Keyboard kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) v.x += 1f;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) v.x -= 1f;
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) v.y += 1f;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) v.y -= 1f;
            }

            Gamepad pad = Gamepad.current;
            if (pad != null)
            {
                Vector2 stick = pad.leftStick.ReadValue();
                if (stick.sqrMagnitude > v.sqrMagnitude) v = stick;
            }

            if (TouchMoveInput.sqrMagnitude > v.sqrMagnitude)
            {
                v = TouchMoveInput;
            }

            return Vector2.ClampMagnitude(v, 1f);
        }

        bool ReadBoostHeld()
        {
            if (InjectedBoost.HasValue) return InjectedBoost.Value;
            if (TouchBoost) return true;

            Keyboard kb = Keyboard.current;
            if (kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed || kb.kKey.isPressed))
            {
                return true;
            }

            Gamepad pad = Gamepad.current;
            return pad != null && (pad.buttonEast.isPressed || pad.leftShoulder.isPressed);
        }

        bool ReadKickPressedThisFrame()
        {
            if (InjectedKickPressed)
            {
                InjectedKickPressed = false;
                return true;
            }

            if (_touchKickPending)
            {
                _touchKickPending = false;
                return true;
            }

            Keyboard kb = Keyboard.current;
            if (kb != null && (kb.spaceKey.wasPressedThisFrame || kb.jKey.wasPressedThisFrame))
            {
                return true;
            }

            Gamepad pad = Gamepad.current;
            return pad != null && (pad.buttonSouth.wasPressedThisFrame || pad.rightTrigger.wasPressedThisFrame);
        }
    }
}
