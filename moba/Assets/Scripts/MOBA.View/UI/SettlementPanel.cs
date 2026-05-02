using MOBA.Logic.Sim;
using UnityEngine;

namespace MOBA.View
{
    /// <summary>
    /// C6 — Full-screen settlement overlay shown when the server sends S2C_GameOver
    /// (<see cref="MOBA.Net.NetClient.MatchEnded"/> becomes true).
    /// Displays VICTORY / DEFEAT, match duration, and a per-hero K/D/Lvl/Gold stats table.
    /// Attach on the same UI GameObject as the other overlays.
    /// </summary>
    public sealed class SettlementPanel : MonoBehaviour
    {
        public NetClientBehaviour Net;

        [Tooltip("Local player slot (0–9). Used to decide VICTORY vs DEFEAT colouring.")]
        public byte LocalSlot;

        void Awake()
        {
            if (Net == null) Net = FindObjectOfType<NetClientBehaviour>();
        }

        void OnGUI()
        {
            if (Net?.Net == null || !Net.Net.MatchEnded) return;
            var w = Net.World;
            if (w == null) return;
            DrawSettlement(w);
        }

        void DrawSettlement(DeterministicWorld w)
        {
            const int panelW = 680;
            const int panelH = 408;
            int x = (Screen.width  - panelW) / 2;
            int y = (Screen.height - panelH) / 2;

            // ── background ──────────────────────────────────────────────────────────
            GUI.color = new Color(0f, 0f, 0f, 0.88f);
            GUI.DrawTexture(new Rect(x, y, panelW, panelH), Texture2D.whiteTexture);
            GUI.color = Color.white;

            // ── title (VICTORY / DEFEAT) ─────────────────────────────────────────
            bool localBlue = LocalSlot < 5;
            bool win       = (w.Winner == Team.Blue) == localBlue;
            string title   = win ? "VICTORY" : "DEFEAT";
            Color titleCol = win ? new Color(0.3f, 1f, 0.5f) : new Color(1f, 0.35f, 0.35f);

            var titleStyle = new GUIStyle(GUI.skin.label)
                { fontSize = 42, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            titleStyle.normal.textColor = titleCol;
            GUI.Label(new Rect(x, y + 6, panelW, 54), title, titleStyle);

            // ── subtitle: winner team + duration ────────────────────────────────
            uint durSec = Net.Net.MatchDurationSec;
            string durationStr = $"{durSec / 60:D2}:{durSec % 60:D2}";
            var subStyle = new GUIStyle(GUI.skin.label)
                { fontSize = 15, alignment = TextAnchor.MiddleCenter };
            subStyle.normal.textColor = new Color(1f, 1f, 1f, 0.75f);
            GUI.Label(new Rect(x, y + 58, panelW, 22),
                      $"Winner: {w.Winner}    Duration: {durationStr}", subStyle);

            // ── divider ──────────────────────────────────────────────────────────
            GUI.color = new Color(1f, 1f, 1f, 0.18f);
            GUI.DrawTexture(new Rect(x + 8, y + 85, panelW - 16, 1), Texture2D.whiteTexture);
            GUI.color = Color.white;

            // ── stats table header ───────────────────────────────────────────────
            int ty = y + 90;
            var hStyle = new GUIStyle(GUI.skin.label)
                { fontSize = 12, fontStyle = FontStyle.Bold };
            hStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);

            GUI.Label(new Rect(x + 10,  ty, 30,  18), "#",     hStyle);
            GUI.Label(new Rect(x + 44,  ty, 50,  18), "Team",  hStyle);
            GUI.Label(new Rect(x + 100, ty, 70,  18), "Hero",  hStyle);
            GUI.Label(new Rect(x + 178, ty, 30,  18), "Lvl",   hStyle);
            GUI.Label(new Rect(x + 218, ty, 50,  18), "K / D", hStyle);
            GUI.Label(new Rect(x + 280, ty, 60,  18), "Gold",  hStyle);
            GUI.Label(new Rect(x + 350, ty, 80,  18), "HP",    hStyle);
            GUI.Label(new Rect(x + 440, ty, 200, 18), "Items", hStyle);

            // ── row divider ───────────────────────────────────────────────────────
            GUI.color = new Color(1f, 1f, 1f, 0.12f);
            GUI.DrawTexture(new Rect(x + 8, ty + 20, panelW - 16, 1), Texture2D.whiteTexture);
            GUI.color = Color.white;

            // ── per-hero rows ─────────────────────────────────────────────────────
            static string HeroName(byte id) => id switch { 0 => "Warrior", 1 => "Mage", 2 => "Marksman", _ => $"H{id}" };
            static string ItemName(byte id) => id == 0 ? "" : $"#{id}";

            for (int i = 0; i < DeterministicWorld.PlayerCount; i++)
            {
                ref var h    = ref w.Heroes[i];
                int     ry   = ty + 24 + i * 26;
                bool    blue = i < 5;
                Color   rc;
                if (i == LocalSlot)               rc = new Color(1f,  1f,  0.45f);   // self = yellow
                else if (blue)                    rc = new Color(0.5f, 0.75f, 1f);   // blue
                else                              rc = new Color(1f,  0.55f, 0.55f); // red

                var rs = new GUIStyle(GUI.skin.label) { fontSize = 12 };
                rs.normal.textColor = rc;

                int hpPct = h.MaxHp > 0 ? (int)((float)h.Hp / (float)h.MaxHp * 100) : 0;
                string items = string.Join(" ", new[]
                {
                    ItemName(h.Inv0), ItemName(h.Inv1), ItemName(h.Inv2),
                    ItemName(h.Inv3), ItemName(h.Inv4), ItemName(h.Inv5)
                }).Trim();

                GUI.Label(new Rect(x + 10,  ry, 30,  22), i.ToString(),              rs);
                GUI.Label(new Rect(x + 44,  ry, 50,  22), blue ? "Blue" : "Red",     rs);
                GUI.Label(new Rect(x + 100, ry, 70,  22), HeroName(h.HeroDefId),     rs);
                GUI.Label(new Rect(x + 178, ry, 30,  22), h.Level.ToString(),        rs);
                GUI.Label(new Rect(x + 218, ry, 50,  22), $"{h.Kills}/{h.Deaths}",   rs);
                GUI.Label(new Rect(x + 280, ry, 60,  22), h.Gold.ToString(),         rs);
                GUI.Label(new Rect(x + 350, ry, 80,  22), $"{hpPct}%",               rs);
                GUI.Label(new Rect(x + 440, ry, 200, 22), items,                     rs);
            }

            // ── bottom divider ────────────────────────────────────────────────────
            GUI.color = new Color(1f, 1f, 1f, 0.12f);
            GUI.DrawTexture(new Rect(x + 8, y + panelH - 46, panelW - 16, 1), Texture2D.whiteTexture);
            GUI.color = Color.white;

            // ── disconnect button ─────────────────────────────────────────────────
            if (GUI.Button(new Rect(x + panelW - 118, y + panelH - 38, 110, 28), "Disconnect"))
                Net.Disconnect();
        }
    }
}
