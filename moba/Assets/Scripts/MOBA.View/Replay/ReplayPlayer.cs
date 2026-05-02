using System.IO;
using MOBA.Logic.Replay;
using MOBA.Logic.Sim;
using MOBA.Shared.Protocol;
using UnityEngine;

namespace MOBA.View
{
    /// <summary>
    /// C5 — Plays a .mreplay file locally without connecting to a server.
    /// Drives a <see cref="DeterministicWorld"/> at <see cref="DeterministicWorld.TicksPerSecond"/> Hz
    /// and exposes it just like <see cref="NetClientBehaviour"/>, so <see cref="WorldViewRoot"/>
    /// renders it with no extra wiring.
    /// </summary>
    public sealed class ReplayPlayer : MonoBehaviour, IWorldSource
    {
        [Tooltip("Absolute path or path under StreamingAssets / persistentDataPath.")]
        public string ReplayPath = "demo.mreplay";

        [Range(0.25f, 8f)] public float Speed = 1f;
        public bool AutoStart = true;
        public bool VerifyHashEachFrame = false;

        public DeterministicWorld World { get; private set; }
        public uint DurationFrames { get; private set; }
        public uint Frame => World?.Frame ?? 0;

        readonly InputFrame[] _tick = new InputFrame[DeterministicWorld.PlayerCount];
        ReplayReader _reader;
        float _accum;
        bool _running;

        void Start() { if (AutoStart) Open(ReplayPath); }

        public bool Open(string path)
        {
            string p = ResolvePath(path);
            if (!File.Exists(p)) { Debug.LogError($"[Replay] not found: {p}"); return false; }
            var bytes = File.ReadAllBytes(p);
            _reader = new ReplayReader();
            _reader.Open(bytes);
            DurationFrames = _reader.DurationFrames;

            World = new DeterministicWorld(_reader.Seed) { EnableGameplay = true };
            if (_reader.SnapshotLength > 0)
                World.ReadSnapshot(_reader.SnapshotSpan, frame: 0);
            _accum = 0f;
            _running = true;
            Debug.Log($"[Replay] opened seed=0x{_reader.Seed:X16} frames={DurationFrames}");
            return true;
        }

        public bool IsPlaying => _running && World != null && Frame < DurationFrames;

        public void Pause() => _running = false;
        public void Resume() => _running = true;

        void Update()
        {
            if (!_running || World == null) return;
            float dt = Time.unscaledDeltaTime * Speed;
            _accum += dt;
            float step = 1f / DeterministicWorld.TicksPerSecond;
            while (_accum >= step && World.Frame < DurationFrames)
            {
                _accum -= step;
                _reader.GetTick(World.Frame, _tick);
                World.Tick(_tick);
                if (VerifyHashEachFrame && (World.Frame % 15) == 0)
                    Debug.Log($"[Replay] f={World.Frame} hash=0x{World.Hash():X16}");
            }
            if (World.Frame >= DurationFrames) _running = false;
        }

        static string ResolvePath(string p)
        {
            if (Path.IsPathRooted(p)) return p;
            string sa = Path.Combine(Application.streamingAssetsPath, p);
            if (File.Exists(sa)) return sa;
            return Path.Combine(Application.persistentDataPath, p);
        }
    }
}
