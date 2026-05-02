using MOBA.Logic.Sim;
using UnityEngine;

namespace MOBA.View
{
    /// <summary>
    /// C1 — Logs world hash every N frames and reports it to the server via
    /// <c>SendHashReport</c> for desync detection.
    /// </summary>
    [RequireComponent(typeof(NetClientBehaviour))]
    public sealed class DeterminismProbe : MonoBehaviour
    {
        [Tooltip("Hash + report cadence in logic frames (15 = once per second).")]
        public int IntervalFrames = 15;

        [Tooltip("Also Debug.Log the hash to the Unity console.")]
        public bool LogToConsole = true;

        NetClientBehaviour _net;

        void Awake()
        {
            _net = GetComponent<NetClientBehaviour>();
            _net.OnFrameTicked += HandleFrame;
        }

        void OnDestroy()
        {
            if (_net != null) _net.OnFrameTicked -= HandleFrame;
        }

        void HandleFrame(uint frame)
        {
            if (IntervalFrames <= 0 || (frame % IntervalFrames) != 0) return;
            var w = _net.World;
            if (w == null) return;
            ulong h = w.Hash();
            _net.Net?.SendHashReport(frame, h);
            if (LogToConsole) Debug.Log($"[Probe] f={frame} hash=0x{h:X16}");
        }
    }
}
