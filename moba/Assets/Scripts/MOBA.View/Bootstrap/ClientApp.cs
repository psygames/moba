using MOBA.Logic.Sim;
using UnityEngine;

namespace MOBA.View
{
    /// <summary>
    /// One-stop launcher: spawns the camera, the NetClient, the view root and an input rig.
    /// Drop on a single GameObject in an empty bootstrap scene; play.
    /// </summary>
    public sealed class ClientApp : MonoBehaviour
    {
        public string Host = "127.0.0.1";
        public ushort Port = 7777;
        [Range(0, 9)] public byte PlayerSlot = 0;
        public bool EnableDeterminismProbe = true;
        public bool AutoConnect = true;

        NetClientBehaviour _net;
        WorldViewRoot _view;

        void Awake()
        {
            EnsureCamera();
            EnsureGround();

            _net = gameObject.AddComponent<NetClientBehaviour>();
            _net.Host = Host;
            _net.Port = Port;
            _net.PlayerSlot = PlayerSlot;
            _net.ConnectAutomatically = AutoConnect;

            if (EnableDeterminismProbe)
                gameObject.AddComponent<DeterminismProbe>();

            var viewGo = new GameObject("WorldView");
            _view = viewGo.AddComponent<WorldViewRoot>();
            _view.SourceBehaviour = _net;

            var inputGo = new GameObject("Input");
            var input = inputGo.AddComponent<InputController>();
            input.Net = _net;

            // Camera follow rig
            var cam = Camera.main;
            var rig = cam.gameObject.AddComponent<CameraRig>();
            rig.SourceBehaviour = _net;
            rig.LocalSlot = PlayerSlot;

            // Overlays — all OnGUI, zero prefab dependency.
            var uiGo = new GameObject("UI");
            var hud = uiGo.AddComponent<HudOverlay>();          hud.SourceBehaviour = _net; hud.LocalSlot = PlayerSlot;
            var hp  = uiGo.AddComponent<HealthBarOverlay>();    hp.SourceBehaviour = _net;  hp.LocalSlot = PlayerSlot;
            var sk  = uiGo.AddComponent<SkillHud>();            sk.SourceBehaviour = _net;  sk.LocalSlot = PlayerSlot;
            var mm  = uiGo.AddComponent<Minimap>();             mm.SourceBehaviour = _net;  mm.LocalSlot = PlayerSlot;
            var df  = uiGo.AddComponent<DamageFloater>();       df.SourceBehaviour = _net;
            var sp  = uiGo.AddComponent<StatePanel>();          sp.Net = _net;
            var spl = uiGo.AddComponent<SettlementPanel>();      spl.Net = _net; spl.LocalSlot = PlayerSlot;
            var cs  = uiGo.AddComponent<ConnectionStatusHud>(); cs.Net = _net;
        }

        void EnsureCamera()
        {
            if (Camera.main != null) return;
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.transform.position = new Vector3(0, 80, -10);
            cam.transform.rotation = Quaternion.Euler(75, 0, 0);
            cam.orthographic = true;
            cam.orthographicSize = 22;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.05f, 0.07f, 0.10f);
        }

        void EnsureGround()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = Vector3.one * 10f; // 100x100
            var col = ground.GetComponent<Collider>();
            if (col) Destroy(col);
        }
    }
}
