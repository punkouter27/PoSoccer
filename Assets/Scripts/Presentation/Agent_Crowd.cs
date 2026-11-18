using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Lightweight stadium perimeter dressing built at runtime: static SpriteRenderer
    /// boards and stands rings (zero Tilemap chunk rebuilds, zero idle Light2D passes)
    /// plus audio reactions to xG chances.
    ///
    /// Presentation only. Self-disables in training and evaluation.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_Crowd : MonoBehaviour
    {
        [Tooltip("Depth of the crowd ring outside the boards, in world units.")]
        [SerializeField] private float _standsDepth = 1.5f;
        [Tooltip("Gap between the touchline and the advertising boards.")]
        [SerializeField] private float _boardGap = 0.35f;
        [Tooltip("Thickness of the advertising board ring.")]
        [SerializeField] private float _boardDepth = 0.5f;
        [SerializeField] private bool _enableCrowd = true;

        [Header("Chances (xG)")]
        [Range(0f, 1f)] [SerializeField] private float _gaspMinXg = 0.12f;
        [Range(0f, 1f)] [SerializeField] private float _gaspFullXg = 0.5f;

        Agent_EnvController _env;
        Agent_ExpectedGoals _xg;
        Agent_Audio _audio;
        bool _xgLookedUp;
        readonly int[] _seenShots = new int[2];
        readonly float[] _seenTotal = new float[2];
        int _seenResolved;
        bool _shotOpen, _shotScored;
        Agent_Soccer.Team _shotTeam;
        float _shotXg;
        SpriteRenderer _blueStands, _redStands;
        Color _blueStandsBase, _redStandsBase;
        float _standPulse;
        Mesh _bowlMesh;
        Material _bowlMaterial;

        void Start()
        {
            var ctx = GetComponent<Agent_MatchContext>();
            _env = ctx != null && ctx.Env != null ? ctx.Env : GetComponent<Agent_EnvController>();
            var hud = ctx != null && ctx.Hud != null ? ctx.Hud : FindAnyObjectByType<Agent_HUD>();

            if (!_enableCrowd || !Agent_Presentation.IsMatchScene(hud))
            {
                enabled = false;
                return;
            }

            if (ctx != null)
            {
                _audio = ctx.Audio;
                _xg = ctx.ExpectedGoals;
            }

            Build();
            _env.EpisodeEnded += OnEpisodeEnded;
        }

        void OnDestroy()
        {
            if (_env != null) _env.EpisodeEnded -= OnEpisodeEnded;
            if (_bowlMesh != null) Destroy(_bowlMesh);
            if (_bowlMaterial != null) Destroy(_bowlMaterial);
        }

        void Build()
        {
            Vector2 pitch = _env.PitchHalfExtents;
            Vector2 boardInner = pitch + Vector2.one * _boardGap;
            Vector2 boardOuter = boardInner + Vector2.one * _boardDepth;
            Vector2 standsInner = boardOuter + Vector2.one * 0.3f;
            Vector2 standsOuter = standsInner + Vector2.one * _standsDepth;

            var root = new GameObject("Stadium_Crowd").transform;
            root.SetParent(transform, false);

            Material lit = Agent_Stadium.Instance != null ? Agent_Stadium.Instance.litMaterial : null;
            var surfaces = GetComponent<Agent_Surfaces>();
            Material boardMaterial = surfaces != null && surfaces.BoardMaterial != null ? surfaces.BoardMaterial : lit;
            Sprite quad = Agent_Art.Square(1f);

            _blueStandsBase = Color.Lerp(new Color(0.11f, 0.14f, 0.17f), Agent_Palette.Blue, 0.24f);
            _redStandsBase = Color.Lerp(new Color(0.11f, 0.14f, 0.17f), Agent_Palette.Red, 0.24f);

            var stands = BuildRing(root, "Stands", standsInner, standsOuter, -18, new Color(0.11f, 0.14f, 0.17f), lit, quad);
            _redStands = stands.Find("N")?.GetComponent<SpriteRenderer>();
            _blueStands = stands.Find("S")?.GetComponent<SpriteRenderer>();
            if (_redStands != null) _redStands.color = _redStandsBase;
            if (_blueStands != null) _blueStands.color = _blueStandsBase;

            BuildRing(root, "Boards", boardInner, boardOuter, -16, new Color(0.10f, 0.12f, 0.15f), boardMaterial, quad);

            // Single-mesh outer concrete upper tier (keeps SpriteRenderer count on Stadium_Crowd at 8).
            Vector2 tier2Inner = standsOuter + Vector2.one * 0.15f;
            Vector2 tier2Outer = tier2Inner + Vector2.one * (_standsDepth * 0.85f);
            BuildOuterBowlMesh(root, tier2Inner, tier2Outer, lit);
        }

        void BuildOuterBowlMesh(Transform parent, Vector2 inner, Vector2 outer, Material material)
        {
            var go = new GameObject("Stadium_Bowl");
            go.transform.SetParent(parent, false);
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sortingOrder = -19;
            if (material != null) _bowlMaterial = new Material(material) { name = "PoSoccer_BowlTier" };
            else _bowlMaterial = new Material(Shader.Find("Sprites/Default"));
            mr.sharedMaterial = _bowlMaterial;

            _bowlMesh = new Mesh { name = "PoSoccer_BowlMesh" };
            float sideW = outer.x - inner.x;
            float endH = outer.y - inner.y;
            float totalW = outer.x * 2f;
            float innerH = inner.y * 2f;

            var verts = new Vector3[16];
            var uvs = new Vector2[16];
            var colors = new Color[16];
            var tris = new int[24];
            Color c = new(0.07f, 0.09f, 0.12f, 1f);

            AddQuad(0, new Vector2(0f, (inner.y + outer.y) * 0.5f), new Vector2(totalW, endH), c, verts, uvs, colors, tris);
            AddQuad(1, new Vector2(0f, -(inner.y + outer.y) * 0.5f), new Vector2(totalW, endH), c, verts, uvs, colors, tris);
            AddQuad(2, new Vector2((inner.x + outer.x) * 0.5f, 0f), new Vector2(sideW, innerH), c, verts, uvs, colors, tris);
            AddQuad(3, new Vector2(-(inner.x + outer.x) * 0.5f, 0f), new Vector2(sideW, innerH), c, verts, uvs, colors, tris);

            _bowlMesh.vertices = verts;
            _bowlMesh.uv = uvs;
            _bowlMesh.colors = colors;
            _bowlMesh.triangles = tris;
            _bowlMesh.RecalculateBounds();
            mf.sharedMesh = _bowlMesh;
        }

        static void AddQuad(int idx, Vector2 center, Vector2 size, Color c,
            Vector3[] verts, Vector2[] uvs, Color[] colors, int[] tris)
        {
            float hx = size.x * 0.5f, hy = size.y * 0.5f;
            int v = idx * 4;
            verts[v + 0] = new Vector3(center.x - hx, center.y - hy, 0f);
            verts[v + 1] = new Vector3(center.x + hx, center.y - hy, 0f);
            verts[v + 2] = new Vector3(center.x + hx, center.y + hy, 0f);
            verts[v + 3] = new Vector3(center.x - hx, center.y + hy, 0f);
            uvs[v + 0] = new Vector2(0f, 0f); uvs[v + 1] = new Vector2(1f, 0f);
            uvs[v + 2] = new Vector2(1f, 1f); uvs[v + 3] = new Vector2(0f, 1f);
            colors[v + 0] = c; colors[v + 1] = c; colors[v + 2] = c; colors[v + 3] = c;
            int t = idx * 6;
            tris[t + 0] = v + 0; tris[t + 1] = v + 2; tris[t + 2] = v + 1;
            tris[t + 3] = v + 0; tris[t + 4] = v + 3; tris[t + 5] = v + 2;
        }

        static Transform BuildRing(Transform parent, string label, Vector2 inner, Vector2 outer,
            int sortingOrder, Color color, Material material, Sprite sprite)
        {
            var group = new GameObject(label).transform;
            group.SetParent(parent, false);

            float sideW = outer.x - inner.x;
            float endH = outer.y - inner.y;
            float totalW = outer.x * 2f;
            float innerH = inner.y * 2f;

            AddStrip(group, "N", new Vector2(0f, (inner.y + outer.y) * 0.5f), new Vector2(totalW, endH), sortingOrder, color, material, sprite);
            AddStrip(group, "S", new Vector2(0f, -(inner.y + outer.y) * 0.5f), new Vector2(totalW, endH), sortingOrder, color, material, sprite);
            AddStrip(group, "E", new Vector2((inner.x + outer.x) * 0.5f, 0f), new Vector2(sideW, innerH), sortingOrder, color, material, sprite);
            AddStrip(group, "W", new Vector2(-(inner.x + outer.x) * 0.5f, 0f), new Vector2(sideW, innerH), sortingOrder, color, material, sprite);
            return group;
        }

        static void AddStrip(Transform parent, string name, Vector2 localPos, Vector2 scale,
            int sortingOrder, Color color, Material material, Sprite sprite)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(scale.x, scale.y, 1f);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = color;
            sr.sortingOrder = sortingOrder;
            if (material != null) sr.sharedMaterial = material;
        }

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            if (winner == null) return;
            if (_shotOpen && winner.Value == _shotTeam) _shotScored = true;
            _standPulse = 1f;
        }

        void Update()
        {
            ReactToChances();
            UpdateStandPulse();
        }

        void UpdateStandPulse()
        {
            if (_standPulse <= 0f) return;
            _standPulse = Mathf.Max(0f, _standPulse - Time.unscaledDeltaTime * 1.1f);
            float lift = Mathf.Sin(_standPulse * Mathf.PI * 3f) * 0.5f + 0.5f;
            float mix = _standPulse * lift * 0.55f;
            if (_blueStands != null) _blueStands.color = Color.Lerp(_blueStandsBase, Agent_Palette.Blue, mix);
            if (_redStands != null) _redStands.color = Color.Lerp(_redStandsBase, Agent_Palette.Red, mix);
        }

        void ReactToChances()
        {
            if (_xg == null)
            {
                if (_xgLookedUp) return;
                _xgLookedUp = true;
                _xg = GetComponent<Agent_ExpectedGoals>();
                if (_audio == null) _audio = GetComponent<Agent_Audio>() ?? FindAnyObjectByType<Agent_Audio>();
                if (_xg == null || !_xg.isActiveAndEnabled) { _xg = null; return; }
                SnapshotChances();
                return;
            }

            int resolved = _xg.ResolvedShots;
            if (resolved != _seenResolved)
            {
                _seenResolved = resolved;
                if (_shotOpen && !_shotScored && _shotXg >= _gaspMinXg)
                {
                    float amount = Mathf.InverseLerp(_gaspMinXg, _gaspFullXg, _shotXg);
                    if (_audio != null) _audio.Gasp(Mathf.Lerp(0.35f, 1f, amount));
                }
                _shotOpen = false;
                _shotScored = false;
            }

            for (int side = 0; side < 2; side++)
            {
                var team = (Agent_Soccer.Team)side;
                int shots = _xg.ShotsFor(team);
                if (shots == _seenShots[side]) continue;

                float total = _xg.TotalFor(team);
                float value = Mathf.Max(0f, total - _seenTotal[side]);
                _seenShots[side] = shots;
                _seenTotal[side] = total;

                _shotOpen = true;
                _shotScored = false;
                _shotTeam = team;
                _shotXg = value;

                if (_audio != null) _audio.Cheer(Mathf.Clamp01(value * 1.2f));
                if (Agent_Stadium.Instance != null) Agent_Stadium.Instance.PulseChance(value);
            }
        }

        void SnapshotChances()
        {
            _seenResolved = _xg.ResolvedShots;
            for (int side = 0; side < 2; side++)
            {
                _seenShots[side] = _xg.ShotsFor((Agent_Soccer.Team)side);
                _seenTotal[side] = _xg.TotalFor((Agent_Soccer.Team)side);
            }
        }
    }
}
