using MOBA.Logic.Sim;
using UnityEngine;

namespace MOBA.View
{
    /// <summary>
    /// Bottom-right minimap. World [-50,50]² mapped to a 200x200 IMGUI rect.
    /// Renders entity dots and (optionally) the local team's vision mask as fog.
    /// </summary>
    public sealed class Minimap : MonoBehaviour
    {
        public MonoBehaviour SourceBehaviour;
        public byte LocalSlot;
        public int Size = 200;
        public int Margin = 12;
        public bool ShowFog = true;

        IWorldSource _src;
        Texture2D _bg, _blue, _red, _yellow, _gray, _self, _fog;

        const float WorldHalf = 50f;

        void Awake()
        {
            _src = SourceBehaviour as IWorldSource ?? FindObjectOfType<NetClientBehaviour>();
            if (_src == null) _src = FindObjectOfType<ReplayPlayer>();
            if (SourceBehaviour is NetClientBehaviour ncb) LocalSlot = ncb.PlayerSlot;
            _bg     = Solid(new Color(0, 0, 0, 0.6f));
            _blue   = Solid(new Color(0.3f, 0.6f, 1f, 1f));
            _red    = Solid(new Color(1f, 0.3f, 0.3f, 1f));
            _yellow = Solid(new Color(1f, 0.9f, 0.2f, 1f));
            _gray   = Solid(new Color(0.5f, 0.5f, 0.5f, 1f));
            _self   = Solid(new Color(1f, 1f, 1f, 1f));
            _fog    = Solid(new Color(0, 0, 0, 0.55f));
        }

        static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c); t.Apply();
            return t;
        }

        Vector2 W2M(Box2DSharp.Common.FVector2 p, Rect map)
        {
            float nx = (((float)p.X) + WorldHalf) / (WorldHalf * 2f);
            float ny = (((float)p.Y) + WorldHalf) / (WorldHalf * 2f);
            return new Vector2(map.x + nx * map.width, map.y + (1 - ny) * map.height);
        }

        void OnGUI()
        {
            var w = _src?.World;
            if (w == null) return;
            int slot = Mathf.Clamp(LocalSlot, 0, DeterministicWorld.PlayerCount - 1);
            var localTeam = slot < 5 ? Team.Blue : Team.Red;

            var map = new Rect(Screen.width - Size - Margin, Screen.height - Size - Margin, Size, Size);
            GUI.DrawTexture(map, _bg);

            // Fog: draw cells NOT visible to local team. Coarse 8x downscale (200/8 = 25 cells per axis -> 625 quads).
            if (ShowFog)
            {
                var grid = localTeam == Team.Blue ? w.VisionBlue : w.VisionRed;
                const int Step = 8;
                float cellPx = (float)map.width / (Vision.GridSize / (float)Step);
                for (int gy = 0; gy < Vision.GridSize; gy += Step)
                {
                    for (int gx = 0; gx < Vision.GridSize; gx += Step)
                    {
                        if (grid.Get(gx, gy)) continue;
                        // Vision world coords: cell center = (-50 + (gx+0.5)*0.5, -50 + (gy+0.5)*0.5)
                        float wx = -WorldHalf + (gx + Step * 0.5f) * 0.5f;
                        float wy = -WorldHalf + (gy + Step * 0.5f) * 0.5f;
                        float nx = (wx + WorldHalf) / (WorldHalf * 2f);
                        float ny = (wy + WorldHalf) / (WorldHalf * 2f);
                        var px = map.x + nx * map.width - cellPx * 0.5f;
                        var py = map.y + (1 - ny) * map.height - cellPx * 0.5f;
                        GUI.DrawTexture(new Rect(px, py, cellPx + 1, cellPx + 1), _fog);
                    }
                }
            }

            void Dot(Box2DSharp.Common.FVector2 pos, Texture2D col, float size = 4f)
            {
                var p = W2M(pos, map);
                GUI.DrawTexture(new Rect(p.x - size * 0.5f, p.y - size * 0.5f, size, size), col);
            }

            for (int i = 0; i < w.Towers.Length; i++) { ref var t = ref w.Towers[i]; if (!t.Alive) continue;
                Dot(t.Pos, t.Team == Team.Blue ? _blue : _red, 6f); }
            for (int i = 0; i < w.Crystals.Length; i++) { ref var c = ref w.Crystals[i]; if (!c.Alive) continue;
                Dot(c.Pos, c.Team == Team.Blue ? _blue : _red, 9f); }
            for (int i = 0; i < w.Minions.Length; i++) { ref var m = ref w.Minions[i]; if (!m.Alive) continue;
                Dot(m.Pos, m.Team == Team.Blue ? _blue : _red, 2.5f); }
            for (int i = 0; i < w.Heroes.Length; i++) { ref var h = ref w.Heroes[i]; if (!h.Alive) continue;
                bool me = i == slot;
                Dot(h.Pos, me ? _self : ((i < 5 ? Team.Blue : Team.Red) == Team.Blue ? _blue : _red), me ? 7f : 5f); }
        }
    }
}
