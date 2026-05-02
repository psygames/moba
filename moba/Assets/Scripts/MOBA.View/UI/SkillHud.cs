using MOBA.Logic.Sim;
using UnityEngine;

namespace MOBA.View
{
    /// <summary>
    /// IMGUI bottom-center skill bar: Q/W/E/R + basic attack + buy.
    /// Shows remaining cooldown seconds and mana cost; greyed if not affordable.
    /// </summary>
    public sealed class SkillHud : MonoBehaviour
    {
        public MonoBehaviour SourceBehaviour;
        public byte LocalSlot;
        IWorldSource _src;

        Texture2D _bg, _ready, _cd;
        GUIStyle _key, _cdLbl;

        void Awake()
        {
            _src = SourceBehaviour as IWorldSource ?? FindObjectOfType<NetClientBehaviour>();
            if (_src == null) _src = FindObjectOfType<ReplayPlayer>();
            if (SourceBehaviour is NetClientBehaviour ncb) LocalSlot = ncb.PlayerSlot;
            _bg    = Solid(new Color(0, 0, 0, 0.7f));
            _ready = Solid(new Color(0.2f, 0.7f, 0.3f, 0.85f));
            _cd    = Solid(new Color(0.4f, 0.4f, 0.4f, 0.85f));
        }

        static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c); t.Apply();
            return t;
        }

        void OnGUI()
        {
            var w = _src?.World;
            if (w == null) return;
            int slot = Mathf.Clamp(LocalSlot, 0, DeterministicWorld.PlayerCount - 1);
            ref var h = ref w.Heroes[slot];

            _key   ??= new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperLeft, normal = { textColor = Color.white } };
            _cdLbl ??= new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };

            const int slotSize = 56;
            const int gap = 6;
            string[] labels = { "Q", "W", "E", "R" };
            uint[] cdEnds = { h.SkillCd0, h.SkillCd1, h.SkillCd2, h.SkillCd3 };

            int n = labels.Length;
            int totalW = n * slotSize + (n - 1) * gap;
            int x0 = (Screen.width - totalW) / 2;
            int y0 = Screen.height - slotSize - 18;

            for (int i = 0; i < n; i++)
            {
                int x = x0 + i * (slotSize + gap);
                var rect = new Rect(x, y0, slotSize, slotSize);
                bool ready = cdEnds[i] <= w.Frame;
                GUI.DrawTexture(rect, _bg);
                GUI.DrawTexture(new Rect(rect.x + 2, rect.y + 2, rect.width - 4, rect.height - 4), ready ? _ready : _cd);
                GUI.Label(new Rect(rect.x + 4, rect.y + 2, 20, 18), labels[i], _key);

                if (!ready)
                {
                    float secs = (cdEnds[i] - w.Frame) / (float)DeterministicWorld.TicksPerSecond;
                    GUI.Label(rect, secs.ToString("F1"), _cdLbl);
                }

                // Display skill mana cost from BuiltinContent
                int defIdx = BuiltinContent.HeroSkills[h.HeroDefId, i];
                if (defIdx >= 0 && defIdx < SkillEngine.DefCount)
                {
                    int mp = (int)(float)SkillEngine.Defs[defIdx].ManaCost;
                    var manaStyle = new GUIStyle(_key) { fontSize = 10, normal = { textColor = new Color(0.6f, 0.85f, 1f) } };
                    GUI.Label(new Rect(rect.x + rect.width - 22, rect.y + rect.height - 14, 20, 14), mp.ToString(), manaStyle);
                }
            }

            // Recall hint
            var hint = new GUIStyle(_key) { fontSize = 11, normal = { textColor = new Color(1, 1, 1, 0.6f) } };
            GUI.Label(new Rect(x0, y0 + slotSize + 2, 400, 16), "A=auto B=recall  buy keys 1-9", hint);
        }
    }
}
