using MOBA.Logic.Sim;
using MOBA.Net;
using UnityEngine;

namespace MOBA.View
{
    /// <summary>
    /// Bootstrap for spectator-only mode. Drop this on a single empty-scene GameObject,
    /// fill <see cref="ServerHost"/>, <see cref="ServerPort"/> and <see cref="RoomId"/> in
    /// the Inspector, then press Play.  No player slot is required.
    ///
    /// Flow:
    ///  1. Connect to server (no <c>C2S_JoinRoom</c>).
    ///  2. Send <c>C2S_SpectateRoom</c> once <see cref="NetClient.Connected"/> flips true.
    ///  3. Receive <c>S2C_SpectateAck</c> → seed is stored; server may push a snapshot.
    ///  4. Build <see cref="DeterministicWorld"/> from snapshot (or fresh if spectator joined
    ///     before the match started) and tick it with every incoming <c>S2C_FrameBatch</c>.
    ///  5. Implement <see cref="IWorldSource"/> so <see cref="WorldViewRoot"/>,
    ///     <see cref="HudOverlay"/>, <see cref="Minimap"/> etc. work without modification.
    /// </summary>
    public sealed class SpectatorApp : MonoBehaviour, IWorldSource
    {
        [Header("Server")]
        public string ServerHost = "127.0.0.1";
        public ushort ServerPort = 7777;
        public uint   RoomId     = 1;

        [Header("Camera")]
        public byte WatchSlot = 0;

        // ── state ────────────────────────────────────────────────────────────────
        private NetClient          _net;
        private DeterministicWorld _world;
        private bool               _worldReady;
        private bool               _specReqSent;

        // ── IWorldSource ─────────────────────────────────────────────────────────
        public DeterministicWorld World => _world;

        /// <summary>Expose the underlying NetClient so <see cref="SpectatorHud"/> can read state.</summary>
        public NetClient Net => _net;

        // ── MonoBehaviour ────────────────────────────────────────────────────────

        void Awake()
        {
            // PlayerSlot defaults to byte.MaxValue → OnConnectedInternal will NOT send JoinRoom.
            _net = new NetClient();
        }

        void Start()
        {
            _net.Connect(ServerHost, ServerPort);

            // ── world view ───────────────────────────────────────────────────────
            var viewGo = new GameObject("SpectatorWorldView");
            var view   = viewGo.AddComponent<WorldViewRoot>();
            view.SourceBehaviour = this;

            // ── camera rig ───────────────────────────────────────────────────────
            var cam = Camera.main;
            if (cam != null)
            {
                var rig = cam.gameObject.AddComponent<CameraRig>();
                rig.SourceBehaviour = this;
                rig.LocalSlot       = WatchSlot;
            }

            // ── UI overlays ──────────────────────────────────────────────────────
            var uiGo = new GameObject("SpectatorUI");
            uiGo.transform.SetParent(transform);

            var hud = uiGo.AddComponent<HudOverlay>();
            hud.SourceBehaviour = this;
            hud.LocalSlot       = WatchSlot;

            uiGo.AddComponent<HealthBarOverlay>();

            var mm = uiGo.AddComponent<Minimap>();

            var spHud = uiGo.AddComponent<SpectatorHud>();
            spHud.App = this;
        }

        void Update()
        {
            _net.Tick();

            // Send SpectateRoom once after KCP handshake.
            if (_net.Connected && !_specReqSent)
            {
                _net.SendSpectateRoom(RoomId);
                _specReqSent = true;
            }

            // Build world from first snapshot (server pushed it after accepting spectator).
            if (!_worldReady && _net.IsSpectating && _net.PendingSnapshot.HasValue)
            {
                var (snapFrame, snapBytes) = _net.PendingSnapshot.Value;
                _world = new DeterministicWorld(_net.Seed) { EnableGameplay = true };
                _world.ReadSnapshot(snapBytes, frame: snapFrame);
                _worldReady = true;
                _net.PendingSnapshot = null;
            }

            // If spectator joined before match started, no snapshot is pushed — build world fresh.
            if (!_worldReady && _net.IsSpectating && !_net.PendingSnapshot.HasValue
                && _net.RxFrames.Count > 0)
            {
                _world = new DeterministicWorld(_net.Seed) { EnableGameplay = true };
                _worldReady = true;
            }

            // Tick world with received broadcast frames in order.
            if (_worldReady && _world != null)
            {
                while (_net.RxFrames.TryDequeue(out var pair))
                {
                    if (pair.frame == _world.Frame)
                        _world.Tick(pair.inputs);
                    // Out-of-order frames are silently dropped (reliable channel; shouldn't happen).
                }
            }
        }

        void OnDestroy() => _net?.Disconnect();
    }
}
