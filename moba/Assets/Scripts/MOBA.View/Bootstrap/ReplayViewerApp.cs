using UnityEngine;

namespace MOBA.View
{
    /// <summary>
    /// C9 — Standalone bootstrap for replay-only playback.
    /// Drop this on a single empty-scene GameObject, set <see cref="ReplayPath"/> in the
    /// Inspector (or pass an absolute path), and press Play.  No server connection is needed.
    ///
    /// Creates: <see cref="ReplayPlayer"/>, <see cref="WorldViewRoot"/>, overlay HUDs
    /// (<see cref="HudOverlay"/>, <see cref="HealthBarOverlay"/>, <see cref="Minimap"/>)
    /// and a <see cref="ReplayHud"/> transport bar.
    /// </summary>
    public sealed class ReplayViewerApp : MonoBehaviour
    {
        [Header("Replay File")]
        [Tooltip("Absolute path, or relative to StreamingAssets / persistentDataPath.")]
        public string ReplayPath = "demo.mreplay";

        [Header("Playback")]
        [Range(0.25f, 8f)] public float InitialSpeed = 1f;
        public bool VerifyHash = false;

        [Header("Camera")]
        public byte LocalSlot = 0;

        ReplayPlayer _player;

        void Awake()
        {
            EnsureCamera();
            EnsureGround();

            // ── replay player ────────────────────────────────────────────────────
            _player = gameObject.AddComponent<ReplayPlayer>();
            _player.ReplayPath            = ReplayPath;
            _player.Speed                 = InitialSpeed;
            _player.VerifyHashEachFrame   = VerifyHash;
            _player.AutoStart             = false; // opened manually in Start()

            // ── world view ───────────────────────────────────────────────────────
            var viewGo = new GameObject("WorldView");
            var view   = viewGo.AddComponent<WorldViewRoot>();
            view.SourceBehaviour = _player;

            // ── camera rig ───────────────────────────────────────────────────────
            var cam = Camera.main;
            if (cam != null)
            {
                var rig = cam.gameObject.AddComponent<CameraRig>();
                rig.SourceBehaviour = _player;
                rig.LocalSlot       = LocalSlot;
            }

            // ── UI overlays ──────────────────────────────────────────────────────
            var uiGo = new GameObject("ReplayUI");
            var hud  = uiGo.AddComponent<HudOverlay>();
            hud.SourceBehaviour = _player;
            hud.LocalSlot       = LocalSlot;

            var hp = uiGo.AddComponent<HealthBarOverlay>();
            hp.SourceBehaviour = _player;
            hp.LocalSlot       = LocalSlot;

            var mm = uiGo.AddComponent<Minimap>();
            mm.SourceBehaviour = _player;
            mm.LocalSlot       = LocalSlot;

            var rh    = uiGo.AddComponent<ReplayHud>();
            rh.Player = _player;
        }

        void Start()
        {
            // Open is deferred to Start so that all Awake() wiring is complete first.
            _player.Open(ReplayPath);
        }

        // ─────────────────────────────────────────────────────────────────────────
        void EnsureCamera()
        {
            if (Camera.main != null) return;
            var go  = new GameObject("Main Camera");
            go.tag  = "MainCamera";
            var cam = go.AddComponent<Camera>();
            cam.transform.position  = new Vector3(0f, 80f, -10f);
            cam.transform.rotation  = Quaternion.Euler(75f, 0f, 0f);
            cam.orthographic        = true;
            cam.orthographicSize    = 22f;
            cam.clearFlags          = CameraClearFlags.SolidColor;
            cam.backgroundColor     = new Color(0.05f, 0.07f, 0.10f);
        }

        void EnsureGround()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = Vector3.one * 10f;
            var col = ground.GetComponent<Collider>();
            if (col) Destroy(col);
        }
    }
}
