using System;
using System.Collections.Generic;
using kcp2k;
using MOBA.Logic.Sim;
using MOBA.Net;
using MOBA.Shared.Protocol;
using UnityEngine;

namespace MOBA.View
{
    /// <summary>
    /// Wraps <see cref="MOBA.Net.NetClient"/> for Unity. Pumps KCP every Update at ~1 kHz
    /// using a sub-frame loop, exposes connect/disconnect, and creates the
    /// <see cref="DeterministicWorld"/> when the room starts.
    /// </summary>
    public sealed class NetClientBehaviour : MonoBehaviour, IWorldSource
    {
        [Header("Server")]
        public string Host = "127.0.0.1";
        public ushort Port = 7777;

        [Header("Player")]
        [Range(0, 9)] public byte PlayerSlot = 0;

        [Header("Tick")]
        [Tooltip("KCP pumps per Unity Update.")]
        public int PumpsPerUpdate = 16;

        public NetClient Net { get; private set; }
        public DeterministicWorld World { get; private set; }
        public bool ConnectAutomatically = true;

        public event Action<DeterministicWorld> OnRoomStarted;
        public event Action<uint> OnFrameTicked; // post-tick

        // Local per-tick input override (set by InputController). Reset to Empty after consumption.
        InputFrame _pendingLocalInput = InputFrame.Empty;
        bool _hasLocalInput;

        // Last sent input frame number (server expects monotonic).
        uint _nextSendFrame = 0;

        void Awake()
        {
            // kcp2k logs go through UnityEngine.Debug
            Log.Info = msg => Debug.Log("[KCP] " + msg);
            Log.Warning = msg => Debug.LogWarning("[KCP] " + msg);
            Log.Error = msg => Debug.LogError("[KCP] " + msg);
        }

        void Start()
        {
            if (ConnectAutomatically) Connect();
        }

        public void Connect()
        {
            if (Net != null) return;
            Net = new NetClient { PlayerSlot = PlayerSlot };
            Net.Connect(Host, Port);
            Debug.Log($"[NetClientBehaviour] connecting slot={PlayerSlot} -> {Host}:{Port}");
        }

        public void Disconnect()
        {
            Net?.Disconnect();
            Net = null;
            World = null;
        }

        void OnDestroy() => Disconnect();

        void Update()
        {
            if (Net == null) return;
            for (int i = 0; i < PumpsPerUpdate; i++) Net.Tick();

            // Lazy-create world after RoomStart
            if (World == null && Net.RoomStarted && Net.Seed != 0UL)
            {
                World = new DeterministicWorld(Net.Seed) { EnableGameplay = true };
                Debug.Log($"[NetClientBehaviour] room started seed=0x{Net.Seed:X16}");
                OnRoomStarted?.Invoke(World);
            }

            DrainAndAdvance();
        }

        public void SubmitInput(in InputFrame f)
        {
            _pendingLocalInput = f;
            _hasLocalInput = true;
        }

        void DrainAndAdvance()
        {
            if (World == null) return;

            // Send our input for the upcoming target frame, ahead of receiving the batch.
            // Server collects then broadcasts. We keep a small lead.
            uint targetFrame = Net.NextExpectedFrame;
            while (_nextSendFrame < targetFrame + 4) // 4-frame lead
            {
                var f = _hasLocalInput ? _pendingLocalInput : InputFrame.Empty;
                Net.SendInput(_nextSendFrame, in f);
                _nextSendFrame++;
            }
            _hasLocalInput = false;

            // Apply any pending snapshot first (after disconnect/resync).
            if (Net.PendingSnapshot.HasValue)
            {
                var (frame, bytes) = Net.PendingSnapshot.Value;
                World.ReadSnapshot(bytes, frame);
                Net.PendingSnapshot = null;
                Debug.Log($"[NetClientBehaviour] snapshot applied @ frame={frame}");
            }

            // Drain frames from server. Each entry = (frame, 10 inputs).
            // Server broadcasts at frame index = world.Frame (i.e. the frame about to be ticked).
            while (Net.RxFrames.Count > 0)
            {
                var (frame, inputs) = Net.RxFrames.Peek();
                if (frame < World.Frame)
                {
                    Net.RxFrames.Dequeue(); // stale
                    continue;
                }
                if (frame > World.Frame) break; // gap, wait

                Net.RxFrames.Dequeue();
                World.Tick(inputs);
                OnFrameTicked?.Invoke(World.Frame);
                Net.NextExpectedFrame = World.Frame;
            }
        }

        /// <summary>Send a buy-item request (op 0x40). Delegates to the shared NetClient helper.</summary>
        public void SendBuyItem(ushort itemId)
        {
            if (Net == null || !Net.Connected) return;
            Net.SendBuyItem(itemId);
        }
    }
}
