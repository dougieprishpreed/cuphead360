using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Cuphead360.Engine
{
    /// <summary>One frame of device state, as the host or an input script provides it.</summary>
    public sealed class InputFrame
    {
        public readonly Pad[] Pads = { new Pad(), new Pad(), new Pad(), new Pad() };
        public readonly HashSet<KeyCode> Keys = new HashSet<KeyCode>();
        public Vector2 Mouse;

        public sealed class Pad
        {
            public bool Connected;
            public PadButton Buttons;
            public float LX, LY, RX, RY, LT, RT;   // sticks -1..1 (up = +1), triggers 0..1
            public void CopyFrom(Pad p) { Connected = p.Connected; Buttons = p.Buttons; LX = p.LX; LY = p.LY; RX = p.RX; RY = p.RY; LT = p.LT; RT = p.RT; }
        }

        public void CopyFrom(InputFrame f)
        {
            for (int i = 0; i < 4; i++) Pads[i].CopyFrom(f.Pads[i]);
            Keys.Clear(); Keys.UnionWith(f.Keys);
            Mouse = f.Mouse;
        }
    }

    /// <summary>XInput button bits (same values as XINPUT_GAMEPAD_*).</summary>
    [Flags]
    public enum PadButton
    {
        None = 0, DPadUp = 0x1, DPadDown = 0x2, DPadLeft = 0x4, DPadRight = 0x8, Start = 0x10, Back = 0x20,
        LeftThumb = 0x40, RightThumb = 0x80, LeftShoulder = 0x100, RightShoulder = 0x200, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000,
    }

    /// <summary>
    /// UnityEngine.Input over the port's devices. Rewired runs on its Unity
    /// input source here (its Raw Input source needs a Win32 message window
    /// the port doesn't have, so it falls back as it does in a sandboxed
    /// player): joysticks appear as "Controller (XBOX 360 For Windows)" with
    /// Unity's Windows layout, so Rewired applies its own Xbox 360 map. Axes
    /// and buttons go through the game's InputManager asset.
    /// </summary>
    public static class Inputs
    {
        public static Func<int, InputFrame> Source;
        static readonly InputFrame cur = new InputFrame(), prev = new InputFrame();
        public static InputFrame Current => cur;

        public const string PadName = "Controller (XBOX 360 For Windows)";

        sealed class Axis
        {
            public string Name;
            public int Type, AxisIndex, JoyNum;
            public string Pos, Neg, AltPos, AltNeg;
            public float Gravity, Dead, Sensitivity;
            public bool Snap, Invert;
            public float Smoothed;   // GetAxis state for key/button axes
        }

        static readonly Dictionary<string, List<Axis>> axes = new Dictionary<string, List<Axis>>(StringComparer.Ordinal);

        public static void LoadAxes(object inputManager)
        {
            axes.Clear();
            foreach (var a in Tree.List(inputManager, "m_Axes"))
            {
                var ax = new Axis
                {
                    Name = Tree.Str(a, "m_Name"), Type = (int)Tree.Int(a, "type"), AxisIndex = (int)Tree.Int(a, "axis"), JoyNum = (int)Tree.Int(a, "joyNum"),
                    Pos = Tree.Str(a, "positiveButton"), Neg = Tree.Str(a, "negativeButton"), AltPos = Tree.Str(a, "altPositiveButton"), AltNeg = Tree.Str(a, "altNegativeButton"),
                    Gravity = Tree.Float(a, "gravity"), Dead = Tree.Float(a, "dead"), Sensitivity = Tree.Float(a, "sensitivity", 1f),
                    Snap = Tree.Bool(a, "snap"), Invert = Tree.Bool(a, "invert"),
                };
                if (!axes.TryGetValue(ax.Name, out var l)) axes[ax.Name] = l = new List<Axis>();
                l.Add(ax);
            }
        }

        /// <summary>Start of a frame: the previous state is kept for Down/Up queries.</summary>
        public static void Update()
        {
            prev.CopyFrom(cur);
            var f = Source?.Invoke(Clock.FrameCount);
            if (f != null) cur.CopyFrom(f);
            // Key/button axes move toward their target at sensitivity, back at gravity.
            float dt = Clock.UnscaledDeltaTime;
            foreach (var l in axes.Values)
                foreach (var a in l)
                {
                    if (a.Type != 0) continue;
                    float target = KeyAxisRaw(a);
                    if (a.Snap && target != 0 && Math.Sign(target) != Math.Sign(a.Smoothed)) a.Smoothed = 0;
                    float rate = target == 0 ? a.Gravity : a.Sensitivity;
                    a.Smoothed = Mathf.MoveTowards(a.Smoothed, target, rate * dt);
                }
        }

        // Unity's Windows layout of an Xbox 360 pad (legacy input, XInput driver).
        static float PadAxis(InputFrame.Pad p, int axis)
        {
            if (!p.Connected) return 0f;
            switch (axis)
            {
                case 0: return p.LX;
                case 1: return -p.LY;                 // Unity's joystick Y is down-positive
                case 2: return p.LT - p.RT;           // shared trigger axis
                case 3: return p.RX;
                case 4: return -p.RY;
                case 5: return ((p.Buttons & PadButton.DPadRight) != 0 ? 1 : 0) - ((p.Buttons & PadButton.DPadLeft) != 0 ? 1 : 0);
                case 6: return ((p.Buttons & PadButton.DPadUp) != 0 ? 1 : 0) - ((p.Buttons & PadButton.DPadDown) != 0 ? 1 : 0);
                case 8: return p.LT;
                case 9: return p.RT;
                default: return 0f;
            }
        }

        static readonly PadButton[] buttonOrder =
        {
            PadButton.A, PadButton.B, PadButton.X, PadButton.Y, PadButton.LeftShoulder, PadButton.RightShoulder,
            PadButton.Back, PadButton.Start, PadButton.LeftThumb, PadButton.RightThumb,
        };

        static bool PadButtonDown(InputFrame f, int pad, int button)
        {
            if (button < 0 || button >= buttonOrder.Length) return false;
            if (pad < 0)
            {
                for (int i = 0; i < 4; i++) if (PadButtonDown(f, i, button)) return true;
                return false;
            }
            var p = f.Pads[pad];
            return p.Connected && (p.Buttons & buttonOrder[button]) != 0;
        }

        static bool KeyDown(InputFrame f, int code)
        {
            if (code >= (int)KeyCode.JoystickButton0 && code <= (int)KeyCode.Joystick8Button19)
            {
                int rel = code - (int)KeyCode.JoystickButton0;
                int joy = rel / 20, button = rel % 20;   // joy 0 = any joystick
                return PadButtonDown(f, joy == 0 ? -1 : joy - 1, button);
            }
            return f.Keys.Contains((KeyCode)code);
        }

        public static bool Key(int code) => KeyDown(cur, code);
        public static bool KeyPressed(int code) => KeyDown(cur, code) && !KeyDown(prev, code);
        public static bool KeyReleased(int code) => !KeyDown(cur, code) && KeyDown(prev, code);

        // ---------------------------------------------------------------- key names

        static readonly Dictionary<string, KeyCode> names = BuildNames();

        static Dictionary<string, KeyCode> BuildNames()
        {
            var d = new Dictionary<string, KeyCode>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyCode k in Enum.GetValues(typeof(KeyCode))) d[k.ToString()] = k;
            void N(string n, KeyCode k) => d[n] = k;
            for (int i = 0; i <= 9; i++) { N(i.ToString(CultureInfo.InvariantCulture), KeyCode.Alpha0 + i); N("[" + i + "]", KeyCode.Keypad0 + i); }
            N("left ctrl", KeyCode.LeftControl); N("right ctrl", KeyCode.RightControl); N("left alt", KeyCode.LeftAlt); N("right alt", KeyCode.RightAlt);
            N("left shift", KeyCode.LeftShift); N("right shift", KeyCode.RightShift); N("left cmd", KeyCode.LeftCommand); N("right cmd", KeyCode.RightCommand);
            N("up", KeyCode.UpArrow); N("down", KeyCode.DownArrow); N("left", KeyCode.LeftArrow); N("right", KeyCode.RightArrow);
            N("enter", KeyCode.KeypadEnter); N("page up", KeyCode.PageUp); N("page down", KeyCode.PageDown); N("caps lock", KeyCode.CapsLock);
            N("scroll lock", KeyCode.ScrollLock); N("numlock", KeyCode.Numlock); N("left windows", KeyCode.LeftWindows); N("right windows", KeyCode.RightWindows);
            N("[+]", KeyCode.KeypadPlus); N("[-]", KeyCode.KeypadMinus); N("[*]", KeyCode.KeypadMultiply); N("[/]", KeyCode.KeypadDivide); N("[.]", KeyCode.KeypadPeriod);
            N("-", KeyCode.Minus); N("=", KeyCode.Equals); N("[", KeyCode.LeftBracket); N("]", KeyCode.RightBracket); N(";", KeyCode.Semicolon);
            N("'", KeyCode.Quote); N(",", KeyCode.Comma); N(".", KeyCode.Period); N("/", KeyCode.Slash); N("\\", KeyCode.Backslash); N("`", KeyCode.BackQuote);
            return d;
        }

        /// <summary>A Unity key name ("left ctrl", "joystick 2 button 3", "mouse 0") as a KeyCode value, or -1.</summary>
        public static int Code(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            name = name.Trim();
            if (name.StartsWith("joystick", StringComparison.OrdinalIgnoreCase))
            {
                var parts = name.Split(' ');
                // "joystick button N" | "joystick J button N"
                if (parts.Length == 3 && int.TryParse(parts[2], out int b)) return (int)KeyCode.JoystickButton0 + b;
                if (parts.Length == 4 && int.TryParse(parts[1], out int j) && int.TryParse(parts[3], out int b2)) return (int)KeyCode.JoystickButton0 + j * 20 + b2;
                return -1;
            }
            if (name.StartsWith("mouse ", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.Substring(6), out int m)) return (int)KeyCode.Mouse0 + m;
            return names.TryGetValue(name, out var k) ? (int)k : -1;
        }

        // ---------------------------------------------------------------- axes

        static float KeyAxisRaw(Axis a)
        {
            float v = 0;
            if (Key(Code(a.Pos)) || Key(Code(a.AltPos))) v += 1;
            if (Key(Code(a.Neg)) || Key(Code(a.AltNeg))) v -= 1;
            return a.Invert ? -v : v;
        }

        static float JoyAxis(Axis a)
        {
            float v = 0;
            if (a.JoyNum == 0)
            {
                for (int i = 0; i < 4; i++) { float x = PadAxis(cur.Pads[i], a.AxisIndex); if (Math.Abs(x) > Math.Abs(v)) v = x; }
            }
            else if (a.JoyNum <= 4) v = PadAxis(cur.Pads[a.JoyNum - 1], a.AxisIndex);
            if (Math.Abs(v) < a.Dead) v = 0;
            else if (a.Dead > 0) v = Math.Sign(v) * (Math.Abs(v) - a.Dead) / (1 - a.Dead);
            return a.Invert ? -v : v;
        }

        public static float AxisRaw(string name, bool smoothed)
        {
            if (name == null || !axes.TryGetValue(name, out var l))
                throw new ArgumentException("Input Axis " + name + " is not setup.\n To change the input settings use: Edit -> Project Settings -> Input");
            float best = 0;
            foreach (var a in l)
            {
                float v = a.Type == 0 ? (smoothed ? a.Smoothed : KeyAxisRaw(a)) : a.Type == 2 ? JoyAxis(a) * (smoothed ? a.Sensitivity : 1f) : 0f;
                if (!smoothed && a.Type == 0 && v != 0) v = Math.Sign(v);
                if (Math.Abs(v) > Math.Abs(best)) best = v;
            }
            return best;
        }

        public static bool Button(string name, int edge)
        {
            if (name == null || !axes.TryGetValue(name, out var l))
                throw new ArgumentException("Input Button " + name + " is not setup.\n To change the input settings use: Edit -> Project Settings -> Input");
            foreach (var a in l)
            {
                if (a.Type != 0) continue;
                foreach (var n in new[] { a.Pos, a.AltPos })
                {
                    int c = Code(n);
                    if (c < 0) continue;
                    if (edge == 0 ? Key(c) : edge > 0 ? KeyPressed(c) : KeyReleased(c)) return true;
                }
            }
            return false;
        }

        public static string[] JoystickNames()
        {
            int last = -1;
            for (int i = 0; i < 4; i++) if (cur.Pads[i].Connected) last = i;
            var r = new string[last + 1];
            for (int i = 0; i <= last; i++) r[i] = cur.Pads[i].Connected ? PadName : "";
            return r;
        }

        public static bool AnyKey(InputFrame f)
        {
            if (f.Keys.Count > 0) return true;
            foreach (var p in f.Pads) if (p.Connected && p.Buttons != 0) return true;
            return false;
        }

        public static bool AnyKeyPressed()
        {
            foreach (var k in cur.Keys) if (!prev.Keys.Contains(k)) return true;
            for (int i = 0; i < 4; i++) if (cur.Pads[i].Connected && (cur.Pads[i].Buttons & ~prev.Pads[i].Buttons) != 0) return true;
            return false;
        }
    }

    /// <summary>
    /// A text input script for repeatable runs:
    ///   # comment
    ///   connect pad0                 at frame 0
    ///   120 pad0 A 6                 press A at frame 120 for 6 frames
    ///   300 pad0 LX -1 60            hold the left stick left for 60 frames
    ///   400 key Space 4              hold a key (Unity KeyCode name)
    ///   +30 pad0 Start 4             relative: 30 frames after the previous line
    /// Buttons: A B X Y LB RB Back Start LS RS Up Down Left Right; axes LX LY RX RY LT RT.
    /// </summary>
    public sealed class InputScript
    {
        readonly List<(int start, int frames, int pad, PadButton button, string axis, float value, KeyCode key)> events =
            new List<(int, int, int, PadButton, string, float, KeyCode)>();
        readonly bool[] connected = new bool[4];
        readonly InputFrame frame = new InputFrame();
        public int LastFrame;

        public static InputScript Load(string path)
        {
            var s = new InputScript();
            int at = 0, ln = 0;
            foreach (var raw in File.ReadAllLines(path))
            {
                ln++;
                var line = raw.Split('#')[0].Trim();
                if (line.Length == 0) continue;
                var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                try
                {
                    if (p[0] == "connect") { s.connected[int.Parse(p[1].Substring(3))] = true; continue; }
                    at = p[0][0] == '+' ? at + int.Parse(p[0].Substring(1)) : int.Parse(p[0]);
                    if (p[1] == "key")
                    {
                        var k = (KeyCode)Enum.Parse(typeof(KeyCode), p[2], true);
                        s.events.Add((at, p.Length > 3 ? int.Parse(p[3]) : 1, -1, 0, null, 0, k));
                    }
                    else
                    {
                        int pad = int.Parse(p[1].Substring(3));
                        s.connected[pad] = true;
                        string c = p[2];
                        var btn = c switch
                        {
                            "A" => PadButton.A, "B" => PadButton.B, "X" => PadButton.X, "Y" => PadButton.Y, "LB" => PadButton.LeftShoulder,
                            "RB" => PadButton.RightShoulder, "Back" => PadButton.Back, "Start" => PadButton.Start, "LS" => PadButton.LeftThumb,
                            "RS" => PadButton.RightThumb, "Up" => PadButton.DPadUp, "Down" => PadButton.DPadDown, "Left" => PadButton.DPadLeft,
                            "Right" => PadButton.DPadRight, _ => PadButton.None,
                        };
                        if (btn != PadButton.None) s.events.Add((at, p.Length > 3 ? int.Parse(p[3]) : 1, pad, btn, null, 0, 0));
                        else s.events.Add((at, p.Length > 4 ? int.Parse(p[4]) : 1, pad, 0, c, float.Parse(p[3], CultureInfo.InvariantCulture), 0));
                    }
                    s.LastFrame = Math.Max(s.LastFrame, at);
                }
                catch (Exception e) { throw new FormatException(path + ":" + ln + ": " + raw + " (" + e.Message + ")"); }
            }
            return s;
        }

        public InputFrame At(int f)
        {
            frame.Keys.Clear();
            for (int i = 0; i < 4; i++)
            {
                var p = frame.Pads[i];
                p.Connected = connected[i]; p.Buttons = 0; p.LX = p.LY = p.RX = p.RY = p.LT = p.RT = 0;
            }
            foreach (var e in events)
            {
                if (f < e.start || f >= e.start + e.frames) continue;
                if (e.pad < 0) { frame.Keys.Add(e.key); continue; }
                var p = frame.Pads[e.pad];
                if (e.axis == null) p.Buttons |= e.button;
                else switch (e.axis)
                    {
                        case "LX": p.LX = e.value; break; case "LY": p.LY = e.value; break;
                        case "RX": p.RX = e.value; break; case "RY": p.RY = e.value; break;
                        case "LT": p.LT = e.value; break; case "RT": p.RT = e.value; break;
                    }
            }
            return frame;
        }
    }

    public static partial class Icalls
    {
        public static bool UnityEngine_Input__GetKeyInt(int key) => Inputs.Key(key);
        public static bool UnityEngine_Input__GetKeyString(string name) => Inputs.Key(Inputs.Code(name));
        public static bool UnityEngine_Input__GetKeyUpInt(int key) => Inputs.KeyReleased(key);
        public static bool UnityEngine_Input__GetKeyUpString(string name) => Inputs.KeyReleased(Inputs.Code(name));
        public static bool UnityEngine_Input__GetKeyDownInt(int key) => Inputs.KeyPressed(key);
        public static bool UnityEngine_Input__GetKeyDownString(string name) => Inputs.KeyPressed(Inputs.Code(name));
        public static bool UnityEngine_Input__GetButton(string buttonName) => Inputs.Button(buttonName, 0);
        public static bool UnityEngine_Input__GetButtonDown(string buttonName) => Inputs.Button(buttonName, 1);
        public static bool UnityEngine_Input__GetButtonUp(string buttonName) => Inputs.Button(buttonName, -1);
        public static float UnityEngine_Input__GetAxis(string axisName) => Inputs.AxisRaw(axisName, true);
        public static float UnityEngine_Input__GetAxisRaw(string axisName) => Inputs.AxisRaw(axisName, false);
        public static string[] UnityEngine_Input__GetJoystickNames() => Inputs.JoystickNames();
        public static bool UnityEngine_Input__get_anyKey() => Inputs.AnyKey(Inputs.Current);
        public static bool UnityEngine_Input__get_anyKeyDown() => Inputs.AnyKeyPressed();
        public static bool UnityEngine_Input__get_mousePresent() => true;
        public static void UnityEngine_Input__INTERNAL_get_mousePosition(out Vector3 value) => value = new Vector3(Inputs.Current.Mouse.x, Inputs.Current.Mouse.y, 0);
        public static bool UnityEngine_Input__GetMouseButton(int button) => Inputs.Key((int)KeyCode.Mouse0 + button);
        public static bool UnityEngine_Input__GetMouseButtonDown(int button) => Inputs.KeyPressed((int)KeyCode.Mouse0 + button);
        public static bool UnityEngine_Input__GetMouseButtonUp(int button) => Inputs.KeyReleased((int)KeyCode.Mouse0 + button);
        public static void UnityEngine_Input__INTERNAL_get_mouseScrollDelta(out Vector2 value) => value = Vector2.zero;
        public static void UnityEngine_Input__ResetInputAxes() { }
        public static string UnityEngine_Input__get_inputString() => "";
        public static bool UnityEngine_Input__get_touchSupported() => false;
        public static int UnityEngine_Input__get_touchCount() => 0;
        public static bool UnityEngine_Input__get_multiTouchEnabled() => false;
        public static bool UnityEngine_Input__get_imeIsSelected() => false;
        public static string UnityEngine_Input__get_compositionString() => "";
    }
}
