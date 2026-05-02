using MOBA.Logic.Sim;
using UnityEngine;

namespace MOBA.View
{
    /// <summary>
    /// C8 — IMGUI transport bar for <see cref="ReplayPlayer"/>.
    /// Shows a progress bar with current/total time, play/pause toggle, and speed
    /// buttons (½× 1× 2× 4×). Appears at the bottom-centre of the screen.
    /// Attach to any active GameObject in the replay scene.
    /// </summary>
    public sealed class ReplayHud : MonoBehaviour
    {
        public ReplayPlayer Player;

        void Awake()
        {
            if (Player == null) Player = FindObjectOfType<ReplayPlayer>();
        }

        void OnGUI()
        {
            if (Player == null || Player.World == null) return;

            uint  cur      = Player.Frame;
            uint  tot      = Player.DurationFrames;
            float progress = tot > 0 ? (float)cur / tot : 0f;
            float fps      = DeterministicWorld.TicksPerSecond;

            const int panelW = 500;
            const int panelH = 58;
            int       px     = (Screen.width - panelW) / 2;
            int       py     = Screen.height - panelH - 14;

            // ── background ─────────────────────────────────────────────────────────
            GUI.color = new Color(0f, 0f, 0f, 0.72f);
            GUI.DrawTexture(new Rect(px, py, panelW, panelH), Texture2D.whiteTexture);
            GUI.color = Color.white;

            // ── progress bar track ─────────────────────────────────────────────────
            GUI.color = new Color(0.28f, 0.28f, 0.28f);
            GUI.DrawTexture(new Rect(px + 8, py + 9, panelW - 16, 10), Texture2D.whiteTexture);

            // ── progress bar fill ──────────────────────────────────────────────────
            float fillW = (panelW - 16) * progress;
            if (fillW > 0f)
            {
                GUI.color = new Color(0.25f, 0.65f, 1f);
                GUI.DrawTexture(new Rect(px + 8, py + 9, fillW, 10), Texture2D.whiteTexture);
            }
            GUI.color = Color.white;

            // ── time label ──────────────────────────────────────────────────────────
            string timeStr = $"{cur / fps:F0}s / {tot / fps:F0}s  (f{cur})";
            var    tStyle  = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleLeft };
            tStyle.normal.textColor = new Color(1f, 1f, 1f, 0.82f);
            GUI.Label(new Rect(px + 8, py + 24, 200, 22), timeStr, tStyle);

            // ── speed buttons: 4× 2× 1× ½× (right-to-left layout) ─────────────────
            float[] speeds  = { 0.5f, 1f, 2f, 4f };
            string[] labels = { "½×", "1×", "2×", "4×" };
            int      bx     = px + panelW - 6;
            const int btnW  = 34;
            const int btnH  = 22;

            for (int i = speeds.Length - 1; i >= 0; i--)
            {
                bx -= btnW + 4;
                bool active = Mathf.Approximately(Player.Speed, speeds[i]);
                GUI.color = active ? new Color(0.25f, 0.65f, 1f) : new Color(0.55f, 0.55f, 0.55f);
                if (GUI.Button(new Rect(bx, py + 26, btnW, btnH), labels[i]))
                    Player.Speed = speeds[i];
            }
            GUI.color = Color.white;

            // ── play / pause button ────────────────────────────────────────────────
            bx -= 44;
            bool  playing  = Player.IsPlaying;
            string ppLabel = playing ? "▐▐" : (cur >= tot ? "■" : "▶");
            GUI.color = playing ? new Color(0.25f, 0.65f, 1f) : new Color(0.7f, 0.7f, 0.7f);
            if (GUI.Button(new Rect(bx, py + 26, 40, btnH), ppLabel))
            {
                if (playing)  Player.Pause();
                else          Player.Resume();
            }
            GUI.color = Color.white;

            // ── "finished" overlay ─────────────────────────────────────────────────
            if (cur >= tot && tot > 0)
            {
                var eStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleCenter };
                eStyle.normal.textColor = new Color(1f, 1f, 1f, 0.6f);
                GUI.Label(new Rect(px, py + 24, panelW, 22), "— Replay finished —", eStyle);
            }
        }
    }
}
