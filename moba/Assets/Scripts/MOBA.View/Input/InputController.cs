using MOBA.Shared.Protocol;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace MOBA.View
{
    /// <summary>
    /// Minimal screen-space virtual joystick. Drag the handle inside the bg circle;
    /// <see cref="Value"/> is the normalised offset in [-1,1]^2.
    /// Attach to a UI GameObject under a Canvas with EventSystem.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public sealed class VirtualJoystick : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        public RectTransform Background;
        public RectTransform Handle;
        public float Radius = 80f;
        public Vector2 Value { get; private set; }
        public bool Active { get; private set; }

        public void OnPointerDown(PointerEventData ev) { Active = true; OnDrag(ev); }
        public void OnPointerUp(PointerEventData ev)   { Active = false; Value = Vector2.zero; if (Handle) Handle.anchoredPosition = Vector2.zero; }
        public void OnDrag(PointerEventData ev)
        {
            if (Background == null) return;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(Background, ev.position, ev.pressEventCamera, out var local);
            var clamped = Vector2.ClampMagnitude(local, Radius);
            if (Handle) Handle.anchoredPosition = clamped;
            Value = clamped / Radius;
        }
    }

    /// <summary>
    /// C4 — Builds <see cref="InputFrame"/> from joystick + 4 skill buttons + KB fallback,
    /// and submits to <see cref="NetClientBehaviour"/> each Update.
    /// </summary>
    public sealed class InputController : MonoBehaviour
    {
        public NetClientBehaviour Net;
        public VirtualJoystick Joystick;

        [Header("Skill buttons (UI)")]
        public Button Skill1; public Button Skill2; public Button Skill3; public Button Skill4;
        public Button BasicAttack;
        public Button Recall;

        [Header("Keyboard fallback")]
        public KeyCode Q = KeyCode.Q, W = KeyCode.W, E = KeyCode.E, R = KeyCode.R;
        public KeyCode A = KeyCode.A; // basic attack
        public KeyCode B = KeyCode.B; // recall

        byte _skillBits;       // accumulated until next SubmitInput
        byte _flags;
        ushort _aimAngleDeg;
        byte _targetSlot;

        void Reset() { Net = FindObjectOfType<NetClientBehaviour>(); }

        void Awake()
        {
            if (Net == null) Net = FindObjectOfType<NetClientBehaviour>();
            Bind(Skill1, () => SetSkill(0));
            Bind(Skill2, () => SetSkill(1));
            Bind(Skill3, () => SetSkill(2));
            Bind(Skill4, () => SetSkill(3));
            Bind(BasicAttack, () => SetSkill(4));
            Bind(Recall, () => _flags = (byte)(_flags | 1));
        }

        void SetSkill(int bit) => _skillBits = (byte)(_skillBits | (1 << bit));

        static void Bind(Button b, System.Action a)
        {
            if (b != null) b.onClick.AddListener(() => a());
        }

        void Update()
        {
            if (Net == null) return;

            if (Input.GetKeyDown(Q)) SetSkill(0);
            if (Input.GetKeyDown(W)) SetSkill(1);
            if (Input.GetKeyDown(E)) SetSkill(2);
            if (Input.GetKeyDown(R)) SetSkill(3);
            if (Input.GetKey(A))     SetSkill(4);
            if (Input.GetKeyDown(B)) _flags = (byte)(_flags | 1);

            // Buy hotkeys: digit 1..9 = ItemDefId 1..9 (server expects (defIdx+1) on the wire,
            // but the BuyItem op carries raw itemId — server resolves index internally).
            for (int k = 0; k < 9; k++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1 + k))
                {
                    BuyItem((ushort)(k + 1));
                    break;
                }
            }

            Vector2 stick = Joystick != null && Joystick.Active ? Joystick.Value : ReadKBStick();

            if (stick.sqrMagnitude > 0.0001f)
            {
                float deg = Mathf.Atan2(stick.y, stick.x) * Mathf.Rad2Deg;
                if (deg < 0) deg += 360f;
                _aimAngleDeg = (ushort)(((int)deg) % 360);
            }

            var f = new InputFrame
            {
                JoyX = (sbyte)Mathf.Clamp(Mathf.RoundToInt(stick.x * 100f), -100, 100),
                JoyY = (sbyte)Mathf.Clamp(Mathf.RoundToInt(stick.y * 100f), -100, 100),
                SkillBits = _skillBits,
                TargetSlot = _targetSlot,
                AimAngleDeg = _aimAngleDeg,
                Flags = _flags,
                BuyItemId = 0, // server fills this for buy events
            };
            Net.SubmitInput(in f);

            // One-shot bits cleared after submit; held bits (basic attack while key held) re-arm next Update.
            _skillBits = 0;
            _flags = 0;
        }

        static Vector2 ReadKBStick()
        {
            float x = Input.GetAxisRaw("Horizontal");
            float y = Input.GetAxisRaw("Vertical");
            return new Vector2(x, y);
        }

        /// <summary>Forward to the underlying NetClient (op 0x40, 4 bytes).</summary>
        public void BuyItem(ushort itemId) => Net?.SendBuyItem(itemId);
    }
}
