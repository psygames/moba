using MOBA.Logic.Sim;
using UnityEngine;

namespace MOBA.View
{
    /// <summary>
    /// Spectator-mode HUD overlay (OnGUI, no prefab required).
    /// Shows a centred badge with connection state, current frame, and match-end notice.
    /// Attach automatically by <see cref="SpectatorApp"/>.
    /// </summary>
    public sealed class SpectatorHud : MonoBehaviour
    {
        /// <summary>Set by the owning <see cref="SpectatorApp"/> after creation.</summary>
        public SpectatorApp App;

        private GUIStyle _badge;
        private GUIStyle _matchEnd;

        private void EnsureStyles()
        {
            if (_badge != null) return;

            _badge = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 15,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
            _badge.normal.textColor      = new Color(0.1f, 0.85f, 1f);
            _badge.normal.background     = MakeTex(1, 1, new Color(0, 0, 0, 0.55f));

            _matchEnd = new GUIStyle(_badge)
            {
                fontSize = 22,
            };
            _matchEnd.normal.textColor = new Color(1f, 0.9f, 0.2f);
        }

        private static Texture2D MakeTex(int w, int h, Color c)
        {
            var t = new Texture2D(w, h);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        void OnGUI()
        {
            if (App == null) return;
            EnsureStyles();

            var net   = App.Net;
            var world = App.World;

            // ── match-ended banner (full-width, centred vertically) ─────────────
            if (net.MatchEnded)
            {
                string winner = (Team)net.MatchWinner == Team.Blue ? "BLUE" : "RED";
                GUI.Label(
                    new Rect(Screen.width * 0.5f - 240, Screen.height * 0.5f - 30, 480, 60),
                    $"SPECTATING — {winner} WINS  |  Frame {world?.Frame ?? 0}",
                    _matchEnd);
                return;
            }

            // ── normal spectator badge (top-centre) ─────────────────────────────
            string label;
            if (!net.Connected)
                label = "◉ SPECTATOR — connecting…";
            else if (!net.IsSpectating)
                label = "◉ SPECTATOR — awaiting room…";
            else
                label = $"◉ SPECTATING  |  Frame {world?.Frame ?? 0}";

            float w = 380, h = 28;
            GUI.Label(new Rect((Screen.width - w) * 0.5f, 8, w, h), label, _badge);
        }
    }
}
