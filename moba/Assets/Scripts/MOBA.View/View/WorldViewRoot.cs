using System.Collections.Generic;
using MOBA.Logic.Sim;
using UnityEngine;

namespace MOBA.View
{
    /// <summary>
    /// C3 — Spawns and updates simple primitive renderers for every alive entity in the
    /// <see cref="DeterministicWorld"/>. Position is sampled directly from the logic state
    /// each Update; no interpolation in v1 (15Hz logic + Lerp every frame keeps it smooth
    /// enough for a debug view).
    /// </summary>
    public sealed class WorldViewRoot : MonoBehaviour
    {
        [Tooltip("Either a NetClientBehaviour or a ReplayPlayer (must be IWorldSource).")]
        public MonoBehaviour SourceBehaviour;
        IWorldSource _source;

        [Header("Visuals")]
        public Material BlueMat;
        public Material RedMat;
        public Material NeutralMat;

        [Tooltip("Lerp factor per Update for visual smoothing. 0 = teleport, 1 = none.")]
        [Range(0.05f, 1f)] public float Smoothing = 0.4f;

        readonly Dictionary<long, EntityView> _views = new();

        DeterministicWorld _world;

        void Reset()
        {
            SourceBehaviour = (MonoBehaviour)(object)GetComponentInParent<NetClientBehaviour>();
        }

        void Awake()
        {
            EnsureMaterials();
            _source = SourceBehaviour as IWorldSource;
            if (_source == null)
            {
                _source = FindObjectOfType<NetClientBehaviour>();
                if (_source == null) _source = FindObjectOfType<ReplayPlayer>();
            }
        }

        void EnsureMaterials()
        {
            // Use Unlit/Color so we don't depend on URP shader graph at runtime.
            // Unity falls back to the built-in shader if URP is absent.
            var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            if (BlueMat == null) BlueMat = MakeMat(sh, new Color(0.25f, 0.55f, 1f));
            if (RedMat == null) RedMat = MakeMat(sh, new Color(1f, 0.3f, 0.3f));
            if (NeutralMat == null) NeutralMat = MakeMat(sh, new Color(0.8f, 0.8f, 0.2f));
        }

        static Material MakeMat(Shader sh, Color c)
        {
            var m = new Material(sh) { color = c };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            return m;
        }

        void Update()
        {
            _world = _source?.World;
            if (_world == null) return;

            // Mark all stale; flip alive ones below.
            foreach (var kv in _views) kv.Value.Touched = false;

            UpdateHeroes();
            UpdateMinions();
            UpdateTowers();
            UpdateCrystals();
            UpdateProjectiles();

            // Cull entities that disappeared (death / despawn).
            _killBuf.Clear();
            foreach (var kv in _views) if (!kv.Value.Touched) _killBuf.Add(kv.Key);
            foreach (var k in _killBuf)
            {
                if (_views.TryGetValue(k, out var v)) Destroy(v.gameObject);
                _views.Remove(k);
            }
        }

        readonly List<long> _killBuf = new();

        EntityView GetOrCreate(long key, PrimitiveType prim, Material mat, float scale, string label)
        {
            if (_views.TryGetValue(key, out var v))
            {
                v.Touched = true;
                return v;
            }
            var go = GameObject.CreatePrimitive(prim);
            go.name = label;
            go.transform.SetParent(transform, false);
            go.transform.localScale = Vector3.one * scale;
            // Drop the auto-collider on primitives — pure visual.
            var col = go.GetComponent<Collider>();
            if (col) Destroy(col);
            var rend = go.GetComponent<MeshRenderer>();
            if (rend) rend.sharedMaterial = mat;
            v = go.AddComponent<EntityView>();
            v.Touched = true;
            _views.Add(key, v);
            return v;
        }

        Material MatFor(Team t) => t == Team.Blue ? BlueMat : RedMat;

        const byte KindCrystal = 200;
        static long Key(UnitKind kind, int idx) => ((long)(byte)kind << 32) | (uint)idx;
        static long CrystalKey(int idx) => ((long)KindCrystal << 32) | (uint)idx;

        void UpdateHeroes()
        {
            for (int i = 0; i < _world.Heroes.Length; i++)
            {
                ref var h = ref _world.Heroes[i];
                if (!h.Alive) continue;
                var team = i < 5 ? Team.Blue : Team.Red;
                var v = GetOrCreate(Key(UnitKind.Hero, i), PrimitiveType.Capsule, MatFor(team), 1.2f, $"Hero[{i}]");
                v.SetTarget(FixConv.ToWorld(h.Pos), Smoothing);
            }
        }

        void UpdateMinions()
        {
            for (int i = 0; i < _world.Minions.Length; i++)
            {
                ref var m = ref _world.Minions[i];
                if (!m.Alive) continue;
                var v = GetOrCreate(Key(UnitKind.Minion, i), PrimitiveType.Cube, MatFor(m.Team), 0.6f, $"Minion[{i}]");
                v.SetTarget(FixConv.ToWorld(m.Pos), Smoothing);
            }
        }

        void UpdateTowers()
        {
            for (int i = 0; i < _world.Towers.Length; i++)
            {
                ref var t = ref _world.Towers[i];
                if (!t.Alive) continue;
                var v = GetOrCreate(Key(UnitKind.Tower, i), PrimitiveType.Cylinder, MatFor(t.Team), 1.5f, $"Tower[{i}]");
                var p = FixConv.ToWorld(t.Pos); p.y = 1f;
                v.SetTarget(p, 1f);
            }
        }

        void UpdateCrystals()
        {
            for (int i = 0; i < _world.Crystals.Length; i++)
            {
                ref var c = ref _world.Crystals[i];
                if (!c.Alive) continue;
                var v = GetOrCreate(CrystalKey(i), PrimitiveType.Sphere, MatFor(c.Team), 2.5f, $"Crystal[{i}]");
                var p = FixConv.ToWorld(c.Pos); p.y = 1.5f;
                v.SetTarget(p, 1f);
            }
        }

        void UpdateProjectiles()
        {
            for (int i = 0; i < _world.Projectiles.Length; i++)
            {
                ref var p = ref _world.Projectiles[i];
                if (!p.Alive) continue;
                var v = GetOrCreate(Key(UnitKind.Projectile, i), PrimitiveType.Sphere, NeutralMat, 0.3f, $"Proj[{i}]");
                v.SetTarget(FixConv.ToWorld(p.Pos), Smoothing);
            }
        }
    }

    /// <summary>Tiny per-entity component holding the smoothing target.</summary>
    public sealed class EntityView : MonoBehaviour
    {
        public Vector3 Target;
        float _smooth = 1f;
        public bool Touched;

        public void SetTarget(Vector3 worldPos, float smoothing)
        {
            Target = worldPos;
            _smooth = Mathf.Clamp01(smoothing);
            if (transform.position == Vector3.zero) transform.position = worldPos;
        }

        void Update()
        {
            transform.position = Vector3.Lerp(transform.position, Target, _smooth);
        }
    }
}
