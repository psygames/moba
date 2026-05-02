using System.Collections.Generic;
using MOBA.Logic.Sim;
using UnityEngine;

namespace MOBA.View
{
    /// <summary>
    /// Compares HP across logic frames for heroes/minions/towers/crystals and
    /// spawns floating "-N" labels above the unit. Uses OnGUI; no prefabs.
    /// </summary>
    public sealed class DamageFloater : MonoBehaviour
    {
        public MonoBehaviour SourceBehaviour;
        public float Lifetime = 1.0f;
        public float Rise = 1.5f;

        IWorldSource _src;
        NetClientBehaviour _net;
        Camera _cam;

        readonly float[] _heroPrev   = new float[10];
        readonly float[] _minionPrev = new float[256];
        readonly float[] _towerPrev  = new float[12];
        readonly float[] _crystalPrev= new float[2];
        bool _seeded;

        struct Float { public Vector3 World; public float Born; public float Amount; public Color Color; }
        readonly List<Float> _floats = new();

        GUIStyle _style;

        void Awake()
        {
            _src = SourceBehaviour as IWorldSource ?? FindObjectOfType<NetClientBehaviour>();
            if (_src == null) _src = FindObjectOfType<ReplayPlayer>();
            _net = SourceBehaviour as NetClientBehaviour;
            if (_net != null) _net.OnFrameTicked += HandleFrame;
            _cam = Camera.main;
        }

        void OnDestroy()
        {
            if (_net != null) _net.OnFrameTicked -= HandleFrame;
        }

        void HandleFrame(uint _)
        {
            var w = _src?.World;
            if (w == null) return;

            void Sample<TArr>() { } // placeholder for symmetry

            if (!_seeded)
            {
                Seed(w); _seeded = true; return;
            }

            for (int i = 0; i < w.Heroes.Length; i++)   { ref var h = ref w.Heroes[i];   if (!h.Alive) { _heroPrev[i] = 0; continue; } var hp=(float)h.Hp; if (hp+1 < _heroPrev[i]) Push(h.Pos, _heroPrev[i]-hp, true); _heroPrev[i] = hp; }
            for (int i = 0; i < w.Minions.Length; i++)  { ref var m = ref w.Minions[i];  if (!m.Alive) { _minionPrev[i] = 0; continue; } var hp=(float)m.Hp; if (hp+1 < _minionPrev[i]) Push(m.Pos, _minionPrev[i]-hp, false); _minionPrev[i] = hp; }
            for (int i = 0; i < w.Towers.Length; i++)   { ref var t = ref w.Towers[i];   if (!t.Alive) { _towerPrev[i] = 0; continue; } var hp=(float)t.Hp; if (hp+1 < _towerPrev[i]) Push(t.Pos, _towerPrev[i]-hp, false); _towerPrev[i] = hp; }
            for (int i = 0; i < w.Crystals.Length; i++) { ref var c = ref w.Crystals[i]; if (!c.Alive) { _crystalPrev[i] = 0; continue; } var hp=(float)c.Hp; if (hp+1 < _crystalPrev[i]) Push(c.Pos, _crystalPrev[i]-hp, true); _crystalPrev[i] = hp; }
        }

        void Seed(DeterministicWorld w)
        {
            for (int i = 0; i < w.Heroes.Length; i++)   _heroPrev[i]   = w.Heroes[i].Alive   ? (float)w.Heroes[i].Hp   : 0f;
            for (int i = 0; i < w.Minions.Length; i++)  _minionPrev[i] = w.Minions[i].Alive  ? (float)w.Minions[i].Hp  : 0f;
            for (int i = 0; i < w.Towers.Length; i++)   _towerPrev[i]  = w.Towers[i].Alive   ? (float)w.Towers[i].Hp   : 0f;
            for (int i = 0; i < w.Crystals.Length; i++) _crystalPrev[i]= w.Crystals[i].Alive ? (float)w.Crystals[i].Hp : 0f;
        }

        void Push(Box2DSharp.Common.FVector2 pos, float amount, bool big)
        {
            _floats.Add(new Float
            {
                World = new Vector3((float)pos.X, big ? 1.6f : 1.0f, (float)pos.Y),
                Born = Time.unscaledTime,
                Amount = amount,
                Color = big ? new Color(1f, 0.85f, 0.2f) : new Color(1f, 0.4f, 0.4f)
            });
        }

        void OnGUI()
        {
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;
            _style ??= new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };

            float now = Time.unscaledTime;
            for (int i = _floats.Count - 1; i >= 0; i--)
            {
                var f = _floats[i];
                float age = now - f.Born;
                if (age >= Lifetime) { _floats.RemoveAt(i); continue; }
                float t = age / Lifetime;
                var world = f.World + Vector3.up * (Rise * t);
                var sp = _cam.WorldToScreenPoint(world);
                if (sp.z <= 0) continue;
                var col = f.Color; col.a = 1f - t;
                _style.normal.textColor = col;
                int n = Mathf.RoundToInt(f.Amount);
                GUI.Label(new Rect(sp.x - 30, Screen.height - sp.y - 14, 60, 20), $"-{n}", _style);
            }
        }
    }
}
