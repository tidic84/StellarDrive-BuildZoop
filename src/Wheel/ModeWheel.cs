using HarmonyLib;
using SDModKit.Game;
using SDModKit.Input;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BuildZoop.Wheel
{
    /// <summary>
    /// Mode selection wheel: hold the mode key, move the mouse towards a mode, release. A short tap still goes to
    /// the next mode. The camera does not turn while the wheel is open.
    /// </summary>
    public static class ModeWheel
    {
        private const float HoldDelay = 0.18f;
        private const float OpenTime = 0.22f;
        private const float CloseTime = 0.2f;
        private const float PointerReach = 120f;
        private const float DeadZone = 22f;

        private static readonly Color Panel = new Color(0.07f, 0.11f, 0.18f, 0.82f);
        private static readonly Color Bubble = new Color(0.93f, 0.95f, 0.98f, 1f);
        private static readonly Color Accent = new Color(0.24f, 0.55f, 0.95f, 1f);
        private static readonly Color IconDark = new Color(0.12f, 0.17f, 0.26f, 1f);

        private static float _pressedAt = -1f;
        private static bool _open;
        private static float _openedAt;
        private static float _closingAt = -1f;
        private static int _chosen = -1;
        private static Vector2 _pointer;
        private static int _hover = -1;
        private static readonly float[] HoverAmount = new float[16];
        private static GUIStyle _title;
#if DEV
        internal static bool DevHeld;
        internal static Vector2? DevPointer;
        private static bool _devWasHeld;
#endif
        private static GUIStyle _hint;

        /// <summary>True while the wheel is up (also while it animates closed).</summary>
        public static bool Visible => _open || _closingAt >= 0f;

        public static bool IsOpen => _open;

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            var look = AccessTools.Method(AccessTools.TypeByName("Players.LocalClient.Controls.PlayerLookControls"), "OnLook");
            if (look != null)
                harmony.Patch(look, new HarmonyMethod(typeof(ModeWheel), nameof(BeforeLook)));
        }

        public static bool BeforeLook()
        {
            return !_open;
        }

        /// <summary>Runs each frame while a build tool or the recycler is out. True when the mode changed.</summary>
        public static bool Update()
        {
            Keybind key = Settings.ModeKey;
            if (key == null || LocalPlayer.GameMenuOpen)
            {
                Close(-1);
                return false;
            }
            float now = Time.unscaledTime;
            bool held = key.Held, pressed = key.Pressed, released = key.Released;
#if DEV
            pressed |= DevHeld && !_devWasHeld;
            released |= !DevHeld && _devWasHeld;
            held |= DevHeld;
            _devWasHeld = DevHeld;
#endif
            if (pressed)
                _pressedAt = now;

            if (!_open && _pressedAt >= 0f && held && now - _pressedAt >= HoldDelay)
            {
                _open = true;
                _openedAt = now;
                _closingAt = -1f;
                _pointer = Vector2.zero;
                _hover = -1;
                for (int i = 0; i < HoverAmount.Length; i++)
                    HoverAmount[i] = 0f;
            }

            if (_open)
            {
                var mouse = Mouse.current;
#if DEV
                if (DevPointer.HasValue)
                {
                    _pointer = DevPointer.Value;
                    mouse = null;
                }
#endif
                if (mouse != null)
                {
                    _pointer += mouse.delta.ReadValue();
                    if (_pointer.magnitude > PointerReach)
                        _pointer = _pointer.normalized * PointerReach;
                    if (mouse.rightButton.wasPressedThisFrame)
                    {
                        // Right click: close without changing anything.
                        _pressedAt = -1f;
                        Close(-1);
                        return false;
                    }
                }
                _hover = _pointer.magnitude < DeadZone ? -1 : Sector(_pointer);
                if (!held)
                {
                    _pressedAt = -1f;
                    int pick = _hover;
                    Close(pick);
                    if (pick >= 0 && Settings.ModeOrder[pick] != Settings.CurrentMode)
                    {
                        Settings.CurrentMode = Settings.ModeOrder[pick];
                        return true;
                    }
                }
                return false;
            }

            if (released && _pressedAt >= 0f)
            {
                // A tap: next mode, as before the wheel.
                _pressedAt = -1f;
                Settings.NextMode();
                return true;
            }
            return false;
        }

        public static void Close(int chosen)
        {
            if (!_open)
                return;
            _open = false;
            _chosen = chosen;
            _closingAt = Time.unscaledTime;
        }

        /// <summary>Item index under a direction; item 0 at the top, then clockwise (screen y grows downwards in GUI).</summary>
        private static int Sector(Vector2 dir)
        {
            int n = Settings.ModeOrder.Length;
            // Mouse delta: +y is up. Angle from the top, clockwise.
            float angle = Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg;
            if (angle < 0f) angle += 360f;
            return Mathf.RoundToInt(angle / (360f / n)) % n;
        }

        // ---------------------------------------------------------------- drawing

        public static void OnGUI()
        {
            if (!Visible || Event.current.type != EventType.Repaint)
                return;
            float now = Time.unscaledTime;
            float open = _open ? Mathf.Clamp01((now - _openedAt) / OpenTime) : 1f;
            float close = _closingAt >= 0f ? Mathf.Clamp01((now - _closingAt) / CloseTime) : 0f;
            if (close >= 1f)
            {
                _closingAt = -1f;
                return;
            }

            float scale = Screen.height / 1080f;
            Vector2 center = new Vector2(Screen.width / 2f, Screen.height / 2f);
            int n = Settings.ModeOrder.Length;
            float dt = Time.unscaledDeltaTime;

            float appear = EaseOutBack(open) * (1f - EaseIn(close) * (_chosen >= 0 ? 0.15f : 1f));
            float alpha = Mathf.Clamp01(open * 1.6f) * (1f - EaseIn(close));
            float radius = 170f * scale * appear;
            Color saved = GUI.color;

            // Backdrop: soft shadow, then the panel ring.
            GUI.color = new Color(0f, 0f, 0f, 0.35f * alpha);
            DrawCentered(ModeIcons.Glow, center, 560f * scale * appear);
            GUI.color = new Color(Panel.r, Panel.g, Panel.b, Panel.a * alpha);
            DrawCentered(ModeIcons.Disc, center, 470f * scale * appear);
            GUI.color = new Color(1f, 1f, 1f, 0.08f * alpha);
            DrawCentered(ModeIcons.Ring, center, 470f * scale * appear);
            DrawCentered(ModeIcons.Ring, center, 210f * scale * appear);

            // Direction of the pointer: a glow on the inner ring.
            if (_open && _pointer.magnitude >= DeadZone)
            {
                Vector2 dir = new Vector2(_pointer.x, -_pointer.y).normalized;
                GUI.color = new Color(Accent.r, Accent.g, Accent.b, 0.55f * alpha);
                DrawCentered(ModeIcons.Glow, center + dir * 105f * scale * appear, 90f * scale);
            }

            for (int i = 0; i < n; i++)
            {
                ZoopMode mode = Settings.ModeOrder[i];
                bool hovered = _open ? i == _hover : i == _chosen;
                HoverAmount[i] = Mathf.MoveTowards(HoverAmount[i], hovered ? 1f : 0f, dt * 9f);
                float h = EaseOut(HoverAmount[i]);

                // Items fly out one after the other when the wheel opens.
                float stagger = Mathf.Clamp01(open * 1.5f - i * 0.06f);
                float angle = (i / (float)n) * Mathf.PI * 2f;
                Vector2 dir = new Vector2(Mathf.Sin(angle), -Mathf.Cos(angle));
                Vector2 pos = center + dir * radius * EaseOutBack(stagger) * (1f + 0.06f * h);

                float itemAlpha = alpha * stagger;
                bool picked = _closingAt >= 0f && i == _chosen;
                if (_closingAt >= 0f && _chosen >= 0 && !picked)
                    itemAlpha *= 1f - EaseOut(close) * 1.5f;
                float size = (78f + 22f * h + (picked ? 40f * EaseOut(close) : 0f)) * scale * EaseOutBack(stagger);

                if (h > 0f)
                {
                    GUI.color = new Color(Accent.r, Accent.g, Accent.b, 0.6f * h * itemAlpha);
                    DrawCentered(ModeIcons.Glow, pos, size * 1.9f);
                }
                Color bubble = Color.Lerp(Bubble, Accent, h);
                GUI.color = new Color(bubble.r, bubble.g, bubble.b, itemAlpha);
                DrawCentered(ModeIcons.Disc, pos, size);
                if (mode == Settings.CurrentMode)
                {
                    // The mode in use keeps a ring.
                    GUI.color = new Color(Accent.r, Accent.g, Accent.b, itemAlpha * (1f - h));
                    DrawCentered(ModeIcons.Ring, pos, size * 1.16f);
                }
                Color icon = Color.Lerp(IconDark, Color.white, h);
                GUI.color = new Color(icon.r, icon.g, icon.b, itemAlpha);
                DrawCentered(ModeIcons.Icon(mode), pos, size * 0.62f);
            }

            // Centre: the mode under the pointer, or the one in use.
            int shown = _open ? (_hover >= 0 ? _hover : System.Array.IndexOf(Settings.ModeOrder, Settings.CurrentMode)) : _chosen;
            if (shown >= 0)
            {
                EnsureStyles(scale);
                ZoopMode mode = Settings.ModeOrder[shown];
                GUI.color = new Color(1f, 1f, 1f, alpha);
                var titleRect = new Rect(center.x - 150f * scale, center.y - 30f * scale, 300f * scale, 34f * scale);
                GUI.Label(titleRect, Settings.ModeName(mode), _title);
                GUI.color = new Color(0.78f, 0.84f, 0.92f, alpha);
                var hintRect = new Rect(center.x - 95f * scale, center.y + 6f * scale, 190f * scale, 48f * scale);
                GUI.Label(hintRect, Settings.ModeHint(mode), _hint);
            }
            GUI.color = saved;
        }

        private static void EnsureStyles(float scale)
        {
            int titleSize = Mathf.Max(12, Mathf.RoundToInt(22f * scale));
            if (_title == null || _title.fontSize != titleSize)
            {
                _title = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = titleSize, wordWrap = false };
                _title.normal.textColor = Color.white;
                _hint = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, fontSize = Mathf.Max(10, Mathf.RoundToInt(14f * scale)), wordWrap = true };
                _hint.normal.textColor = Color.white;
            }
        }

        private static void DrawCentered(Texture2D tex, Vector2 center, float size)
        {
            if (size <= 0.5f)
                return;
            GUI.DrawTexture(new Rect(center.x - size / 2f, center.y - size / 2f, size, size), tex, ScaleMode.StretchToFill, true);
        }

        private static float EaseOut(float t) => 1f - (1f - t) * (1f - t) * (1f - t);

        private static float EaseIn(float t) => t * t;

        private static float EaseOutBack(float t)
        {
            const float c1 = 1.70158f, c3 = c1 + 1f;
            return 1f + c3 * Mathf.Pow(t - 1f, 3f) + c1 * Mathf.Pow(t - 1f, 2f);
        }
    }
}
