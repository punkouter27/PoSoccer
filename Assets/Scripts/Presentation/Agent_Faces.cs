using System.Collections.Generic;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Draws each player's face photo (Reward_Settings.faceTexture) on top of its
    /// square body. The photo is stored upright; on the pitch it is turned 180
    /// degrees so the CHIN points along transform.up - the way the player faces -
    /// and the top of the head is the back of the square.
    ///
    /// The face covers FACE_FRACTION of the body, so a ring of the body (and the
    /// team rim the body material draws) stays visible: on a pitch of faces, team
    /// is still read from colour, never from whose face it is.
    ///
    /// One child SpriteRenderer per player with a face, one sprite per texture
    /// shared across players and rematches. Players without a face (STANDARD, the
    /// bot, any community brain) are left exactly as they were.
    ///
    /// Per-pitch visual: installed by Agent_Presentation.InstallVisuals and gated on
    /// IsVisualScene, so training and evaluation never build it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Agent_Faces : MonoBehaviour
    {
        /// <summary>Share of the body's side the face covers.</summary>
        const float FACE_FRACTION = 0.84f;
        const string CHILD_NAME = "Face";

        static readonly Dictionary<Texture2D, Sprite> Sprites = new();

        void Start()
        {
            var env = GetComponent<Agent_EnvController>();
            if (env == null) env = FindAnyObjectByType<Agent_EnvController>();
            var hud = FindAnyObjectByType<Agent_HUD>();
            if (env == null || !Agent_Presentation.IsVisualScene(hud))
            {
                enabled = false;
                return;
            }

            var agents = env.GetComponentsInChildren<Agent_Soccer>(true);
            for (int agentIndex = 0; agentIndex < agents.Length; agentIndex++)
            {
                Attach(agents[agentIndex]);
            }
            enabled = false;   // nothing to do per frame: the child follows the body
        }

        /// <summary>Adds (or refreshes) the face child on one player. Safe to call twice.</summary>
        public static void Attach(Agent_Soccer agent)
        {
            if (agent == null) return;
            var face = agent.rewards != null ? agent.rewards.faceTexture : null;
            var existing = agent.transform.Find(CHILD_NAME);

            if (face == null)
            {
                if (existing != null) existing.gameObject.SetActive(false);
                return;
            }
            if (!agent.TryGetComponent(out SpriteRenderer body) || body.sprite == null) return;

            SpriteRenderer renderer;
            if (existing != null)
            {
                existing.gameObject.SetActive(true);
                renderer = existing.GetComponent<SpriteRenderer>();
            }
            else
            {
                var child = new GameObject(CHILD_NAME);
                child.transform.SetParent(agent.transform, false);
                renderer = child.AddComponent<SpriteRenderer>();
            }

            // Upright photo -> chin toward local +Y (forward) is a half turn.
            renderer.transform.localPosition = new Vector3(0f, 0f, -0.001f);
            renderer.transform.localRotation = Quaternion.Euler(0f, 0f, 180f);

            renderer.sprite = SpriteFor(face);
            renderer.sortingLayerID = body.sortingLayerID;
            renderer.sortingOrder = body.sortingOrder + 1;

            // Fit the body's LOCAL square, so bodyScale (applied to the parent)
            // scales the face with the body.
            Vector2 bodySize = body.sprite.bounds.size;
            float side = Mathf.Min(bodySize.x, bodySize.y) * FACE_FRACTION;
            float spriteSide = Mathf.Max(0.0001f, renderer.sprite.bounds.size.x);
            float scale = side / spriteSide;
            renderer.transform.localScale = new Vector3(scale, scale, 1f);
        }

        /// <summary>One sprite per face texture, 1 world unit across before scaling.</summary>
        static Sprite SpriteFor(Texture2D texture)
        {
            if (Sprites.TryGetValue(texture, out var cached) && cached != null) return cached;
            var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height),
                new Vector2(0.5f, 0.5f), texture.width, 0, SpriteMeshType.FullRect);
            sprite.name = "face_" + texture.name;
            Sprites[texture] = sprite;
            return sprite;
        }
    }
}
