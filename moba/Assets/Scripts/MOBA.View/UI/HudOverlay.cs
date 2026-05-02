using MOBA.Logic.Sim;
using UnityEngine;

namespace MOBA.View
{
    /// <summary>
    /// Tiny IMGUI HUD: shows frame, hash, my hero HP/MP/lvl/gold, and game-over banner.
    /// No prefabs needed; useful while iterating without UI Builder.
    /// </summary>
    public sealed class HudOverlay : MonoBehaviour
    {
        public MonoBehaviour SourceBehaviour;
        IWorldSource _source;
        public byte LocalSlot;

        void Awake()
        {
            _source = SourceBehaviour as IWorldSource ?? FindObjectOfType<NetClientBehaviour>();
            if (_source == null) _source = FindObjectOfType<ReplayPlayer>();
            if (SourceBehaviour is NetClientBehaviour ncb) LocalSlot = ncb.PlayerSlot;
        }

        void OnGUI()
        {
            var w = _source?.World;
            if (w == null) { GUI.Label(new Rect(10, 10, 320, 22), "Connecting…"); return; }

            int slot = Mathf.Clamp(LocalSlot, 0, DeterministicWorld.PlayerCount - 1);
            ref var h = ref w.Heroes[slot];

            int y = 8;
            void L(string s) { GUI.Label(new Rect(10, y, 600, 22), s); y += 18; }
            L($"frame={w.Frame}  hash=0x{w.Hash():X16}");
            L($"slot={slot} team={(slot < 5 ? "Blue" : "Red")} alive={h.Alive} lvl={h.Level} gold={h.Gold}");
            L($"HP {FixConv.I(h.Hp)}/{FixConv.I(h.MaxHp)}   MP {FixConv.I(h.Mp)}/{FixConv.I(h.MaxMp)}");
            L($"K/D {h.Kills}/{h.Deaths}   pos=({(float)h.Pos.X:F1},{(float)h.Pos.Y:F1})");
            L($"minions alive={w.AliveMinionCount}  waves={w.WavesSpawned}");

            if (w.GameOver)
            {
                var s = "GAME OVER — winner: " + w.Winner;
                var style = new GUIStyle(GUI.skin.label) { fontSize = 32, alignment = TextAnchor.MiddleCenter };
                GUI.Label(new Rect(0, Screen.height / 2 - 24, Screen.width, 60), s, style);
            }
        }
    }
}
