using MOBA.Logic.Sim;
using UnityEngine;

namespace MOBA.View
{
    /// <summary>
    /// Top-down orthographic camera that follows the local hero. Mouse wheel zooms.
    /// Edge-pan with WASD-Shift is intentionally omitted (Shift+WASD reserved for input).
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class CameraRig : MonoBehaviour
    {
        public MonoBehaviour SourceBehaviour;
        public byte LocalSlot;

        [Header("Framing")]
        public float Height = 80f;
        public float Pitch = 75f;
        public float OrthoSize = 22f;
        public float MinOrtho = 10f;
        public float MaxOrtho = 60f;
        public float ZoomSpeed = 6f;
        [Range(0.05f, 1f)] public float Smoothing = 0.18f;

        IWorldSource _src;
        Camera _cam;
        Vector3 _target;

        void Awake()
        {
            _cam = GetComponent<Camera>();
            _cam.orthographic = true;
            _cam.orthographicSize = OrthoSize;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            if (_cam.backgroundColor == Color.black) _cam.backgroundColor = new Color(0.05f, 0.07f, 0.10f);
            _src = SourceBehaviour as IWorldSource ?? FindObjectOfType<NetClientBehaviour>();
            if (_src == null) _src = FindObjectOfType<ReplayPlayer>();
            if (SourceBehaviour is NetClientBehaviour ncb) LocalSlot = ncb.PlayerSlot;
            transform.rotation = Quaternion.Euler(Pitch, 0, 0);
        }

        void Update()
        {
            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.001f)
                OrthoSize = Mathf.Clamp(OrthoSize - wheel * ZoomSpeed, MinOrtho, MaxOrtho);
            _cam.orthographicSize = Mathf.Lerp(_cam.orthographicSize, OrthoSize, 0.2f);

            var w = _src?.World;
            if (w == null) return;
            int slot = Mathf.Clamp(LocalSlot, 0, DeterministicWorld.PlayerCount - 1);
            ref var h = ref w.Heroes[slot];
            if (!h.Alive) return;

            _target = new Vector3((float)h.Pos.X, 0, (float)h.Pos.Y);
            // Position camera above and slightly behind the target along its pitch.
            float backDist = Height / Mathf.Tan(Pitch * Mathf.Deg2Rad);
            var desired = new Vector3(_target.x, Height, _target.z - backDist);
            transform.position = Vector3.Lerp(transform.position, desired, Smoothing);
        }
    }
}
