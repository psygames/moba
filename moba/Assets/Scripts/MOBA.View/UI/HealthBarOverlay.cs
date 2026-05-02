using System.Collections.Generic;
using MOBA.Logic.Sim;
using UnityEngine;

namespace MOBA.View
{
    /// <summary>
    /// Renders world-space HP bars for every alive hero/minion/tower/crystal.
    /// Uses a single GUI.DrawTexture pass per entity in OnGUI for zero prefab work.
    /// Camera-projected; clipped to screen.
    /// </summary>
    public sealed class HealthBarOverlay : MonoBehaviour
    {
        public MonoBehaviour SourceBehaviour;
        public byte LocalSlot;
        IWorldSource _src;
        Camera _cam;

        Texture2D _bg, _blue, _red, _yellow, _gray;

        void Awake()
        {
            _src = SourceBehaviour as IWorldSource ?? FindObjectOfType<NetClientBehaviour>();
            if (_src == null) _src = FindObjectOfType<ReplayPlayer>();
            _cam = Camera.main;
            _bg     = Solid(new Color(0, 0, 0, 0.7f));
            _blue   = Solid(new Color(0.3f, 0.6f, 1f, 1f));
            _red    = Solid(new Color(1f, 0.3f, 0.3f, 1f));
            _yellow = Solid(new Color(1f, 0.9f, 0.2f, 1f));
            _gray   = Solid(new Color(0.6f, 0.6f, 0.6f, 1f));
        }

        static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c); t.Apply();
            return t;
        }

        void OnGUI()
        {
            var w = _src?.World;
            if (w == null) return;
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;

            for (int i = 0; i < w.Heroes.Length; i++) { ref var h = ref w.Heroes[i]; if (!h.Alive) continue;
                Draw(h.Pos, (float)h.Hp, (float)h.MaxHp, i < 5 ? Side.Blue : Side.Red, big: true, label: $"L{h.Level}"); }
            for (int i = 0; i < w.Minions.Length; i++) { ref var m = ref w.Minions[i]; if (!m.Alive) continue;
                Draw(m.Pos, (float)m.Hp, (float)m.MaxHp, m.Team == Team.Blue ? Side.Blue : Side.Red, big: false); }
            for (int i = 0; i < w.Towers.Length; i++) { ref var t = ref w.Towers[i]; if (!t.Alive) continue;
                Draw(t.Pos, (float)t.Hp, (float)t.MaxHp, t.Team == Team.Blue ? Side.Blue : Side.Red, big: true, yOff: 1.0f); }
            for (int i = 0; i < w.Crystals.Length; i++) { ref var c = ref w.Crystals[i]; if (!c.Alive) continue;
                Draw(c.Pos, (float)c.Hp, (float)c.MaxHp, c.Team == Team.Blue ? Side.Blue : Side.Red, big: true, yOff: 1.5f, label: "Nexus"); }
        }

        enum Side { Blue, Red }

        void Draw(Box2DSharp.Common.FVector2 pos, float hp, float maxHp, Side s, bool big, float yOff = 0.5f, string label = null)
        {
            if (maxHp <= 0) return;
            var world = new Vector3((float)pos.X, yOff, (float)pos.Y);
            var screen = _cam.WorldToScreenPoint(world);
            if (screen.z <= 0) return;
            float sx = screen.x;
            float sy = Screen.height - screen.y - (big ? 36f : 22f);

            float width = big ? 70f : 38f;
            float height = big ? 8f : 4f;
            var rect = new Rect(sx - width * 0.5f, sy, width, height);
            GUI.DrawTexture(rect, _bg);
            float ratio = Mathf.Clamp01(hp / maxHp);
            var fill = new Rect(rect.x + 1, rect.y + 1, (rect.width - 2) * ratio, rect.height - 2);
            GUI.DrawTexture(fill, s == Side.Blue ? _blue : _red);
            if (!string.IsNullOrEmpty(label))
            {
                var st = new GUIStyle(GUI.skin.label) { fontSize = 10, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
                GUI.Label(new Rect(sx - 30, sy - 14, 60, 14), label, st);
            }
        }
    }
}
