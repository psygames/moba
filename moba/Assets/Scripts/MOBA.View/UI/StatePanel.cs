using MOBA.Logic.Sim;
using UnityEngine;

namespace MOBA.View
{
    /// <summary>
    /// Centered banner that appears when the local hero is dead (showing respawn timer)
    /// or when the network is disconnected (showing a Reconnect button).
    /// </summary>
    public sealed class StatePanel : MonoBehaviour
    {
        public NetClientBehaviour Net;

        void Awake()
        {
            if (Net == null) Net = FindObjectOfType<NetClientBehaviour>();
        }

        void OnGUI()
        {
            if (Net == null) return;
            var w = Net.World;

            // Disconnected state
            if (Net.Net == null || (!Net.Net.Connected && !Net.Net.RoomStarted))
            {
                Banner("Disconnected", subtitle: $"server {Net.Host}:{Net.Port}", showReconnect: true);
                return;
            }
            if (!Net.Net.RoomStarted)
            {
                Banner("Waiting for room…", subtitle: $"slot={Net.PlayerSlot}", showReconnect: false);
                return;
            }
            if (w == null) return;

            int slot = Mathf.Clamp(Net.PlayerSlot, 0, DeterministicWorld.PlayerCount - 1);
            ref var h = ref w.Heroes[slot];
            if (!h.Alive)
            {
                int frames = (int)(h.RespawnFrame > w.Frame ? h.RespawnFrame - w.Frame : 0);
                float secs = frames / (float)DeterministicWorld.TicksPerSecond;
                Banner("You died", subtitle: $"respawn in {secs:F1}s", showReconnect: false, color: new Color(1, 0.4f, 0.4f));
            }

            // Note: GameOver settlement is handled by SettlementPanel (S2C_GameOver authoritative).
        }

        void Banner(string title, string subtitle, bool showReconnect, Color? color = null)
        {
            int w = 360, h = 110;
            int x = (Screen.width - w) / 2;
            int y = Screen.height / 2 - 80;

            GUI.color = new Color(0, 0, 0, 0.6f);
            GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture);
            GUI.color = Color.white;

            var tStyle = new GUIStyle(GUI.skin.label) { fontSize = 30, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            tStyle.normal.textColor = color ?? Color.white;
            GUI.Label(new Rect(x, y + 8, w, 40), title, tStyle);

            var sStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleCenter };
            sStyle.normal.textColor = new Color(1, 1, 1, 0.85f);
            GUI.Label(new Rect(x, y + 50, w, 22), subtitle, sStyle);

            if (showReconnect)
            {
                if (GUI.Button(new Rect(x + (w - 140) / 2, y + h - 36, 140, 28), "Reconnect"))
                {
                    Net.Disconnect();
                    Net.Connect();
                }
            }
        }
    }
}
