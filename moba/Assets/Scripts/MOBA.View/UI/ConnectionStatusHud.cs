using UnityEngine;

namespace MOBA.View
{
    /// <summary>
    /// C7 — Persistent connection-state indicator (top-right corner).
    /// Also drives auto-reconnect: every <see cref="ReconnectIntervalSec"/> seconds while
    /// the KCP socket is not connected and the room has not ended, it calls
    /// <see cref="NetClientBehaviour.Disconnect"/> + <see cref="NetClientBehaviour.Connect"/>.
    /// </summary>
    public sealed class ConnectionStatusHud : MonoBehaviour
    {
        public NetClientBehaviour Net;

        [Tooltip("Seconds between automatic reconnect attempts after an unexpected disconnect.")]
        public float ReconnectIntervalSec = 5f;

        float _reconnectTimer;
        bool  _wasConnected;

        void Awake()
        {
            if (Net == null) Net = FindObjectOfType<NetClientBehaviour>();
        }

        void Update()
        {
            if (Net == null) return;

            bool connected = Net.Net != null && Net.Net.Connected;
            bool matchEnded = Net.Net != null && Net.Net.MatchEnded;

            // Detect transition connected → disconnected (but not if match is already over).
            if (_wasConnected && !connected && !matchEnded)
            {
                _reconnectTimer = ReconnectIntervalSec;
                Debug.Log("[ConnectionStatusHud] unexpected disconnect – scheduling reconnect");
            }
            _wasConnected = connected;

            // Auto-reconnect while in a runnable state.
            if (!connected && !matchEnded)
            {
                _reconnectTimer -= Time.deltaTime;
                if (_reconnectTimer <= 0f)
                {
                    _reconnectTimer = ReconnectIntervalSec;
                    Debug.Log("[ConnectionStatusHud] auto-reconnecting…");
                    Net.Disconnect();
                    Net.Connect();
                }
            }
        }

        void OnGUI()
        {
            if (Net == null) return;

            bool connected  = Net.Net != null && Net.Net.Connected;
            bool inRoom     = Net.Net != null && Net.Net.RoomStarted;
            bool matchEnded = Net.Net != null && Net.Net.MatchEnded;

            string label;
            Color  dotColor;

            if (matchEnded)
            {
                label    = "Match ended";
                dotColor = new Color(0.55f, 0.55f, 0.55f);
            }
            else if (inRoom)
            {
                // Show how many frames behind we are compared to what the server expects.
                int lag = Net.World != null
                    ? System.Math.Max(0, (int)Net.Net.NextExpectedFrame - (int)Net.World.Frame)
                    : 0;
                label    = lag == 0 ? "● In room" : $"● In room  lag={lag}f";
                dotColor = lag == 0 ? new Color(0.25f, 0.95f, 0.4f) : new Color(1f, 0.8f, 0.1f);
            }
            else if (connected)
            {
                label    = "● Connecting…";
                dotColor = new Color(1f, 0.85f, 0.2f);
            }
            else
            {
                label    = _reconnectTimer > 0f
                    ? $"✗ Disconnected  retry in {_reconnectTimer:F1}s"
                    : "✗ Disconnected";
                dotColor = new Color(1f, 0.3f, 0.3f);
            }

            const float w  = 220f;
            const float h  = 20f;
            float       px = Screen.width - w - 6f;
            float       py = 6f;

            GUI.color = new Color(0f, 0f, 0f, 0.50f);
            GUI.DrawTexture(new Rect(px - 4f, py, w + 8f, h), Texture2D.whiteTexture);
            GUI.color = Color.white;

            var style = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            style.normal.textColor = dotColor;
            GUI.Label(new Rect(px, py, w, h), label, style);
        }
    }
}
