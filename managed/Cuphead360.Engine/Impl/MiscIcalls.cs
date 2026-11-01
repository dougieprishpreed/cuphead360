using System;
using UnityEngine;

namespace Cuphead360.Engine
{
    public static partial class Icalls
    {
        // ------------------------------------------------------------ Random
        // Unity's generator is xorshift128 seeded through a Mersenne-style
        // initialiser. The float mapping and Range(float) formula still need the
        // V1 oracle (PORT_REVIEW A9); integer Range uses modulo as Unity does.
        static uint rx, ry, rz, rw;
        static int seed;

        public static void SeedRandom(int s)
        {
            seed = s;
            rx = (uint)s;
            ry = rx * 1812433253u + 1u;
            rz = ry * 1812433253u + 1u;
            rw = rz * 1812433253u + 1u;
        }

        static uint NextRandom()
        {
            uint t = rx ^ (rx << 11);
            rx = ry; ry = rz; rz = rw;
            rw = rw ^ (rw >> 19) ^ t ^ (t >> 8);
            return rw;
        }

        static float RandomValue() => (NextRandom() & 0x7FFFFFu) * (1f / 8388607f);

        public static int UnityEngine_Random__get_seed() => seed;
        public static void UnityEngine_Random__set_seed(int value) => SeedRandom(value);
        public static void UnityEngine_Random__InitState(int seed) => SeedRandom(seed);
        public static float UnityEngine_Random__get_value() => RandomValue();
        public static float UnityEngine_Random__Range(float min, float max) => min + (max - min) * RandomValue();
        public static int UnityEngine_Random__RandomRangeInt(int min, int max)
        {
            if (min < max) return min + (int)(NextRandom() % (uint)(max - min));
            if (min > max) return min - (int)(NextRandom() % (uint)(min - max));
            return min;
        }
        public static void UnityEngine_Random__INTERNAL_get_state(out UnityEngine.Random.State value)
        {
            value = default;
            value.s0 = (int)rx; value.s1 = (int)ry; value.s2 = (int)rz; value.s3 = (int)rw;
        }
        public static void UnityEngine_Random__INTERNAL_set_state(ref UnityEngine.Random.State value)
        {
            rx = (uint)value.s0; ry = (uint)value.s1; rz = (uint)value.s2; rw = (uint)value.s3;
        }
        public static void UnityEngine_Random__GetRandomUnitCircle(out Vector2 output)
        {
            float a = RandomValue() * Mathf.PI * 2f, r = Mathf.Sqrt(RandomValue());
            output = new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
        }
        public static void UnityEngine_Random__INTERNAL_get_insideUnitSphere(out Vector3 value)
        {
            Vector3 v;
            do { v = new Vector3(RandomValue() * 2 - 1, RandomValue() * 2 - 1, RandomValue() * 2 - 1); } while (v.sqrMagnitude > 1f);
            value = v;
        }
        public static void UnityEngine_Random__INTERNAL_get_onUnitSphere(out Vector3 value)
        {
            UnityEngine_Random__INTERNAL_get_insideUnitSphere(out var v);
            value = v.sqrMagnitude > 1e-12f ? v.normalized : Vector3.up;
        }
        public static void UnityEngine_Random__INTERNAL_get_rotation(out Quaternion value)
            => value = MathX.Normalize(new Quaternion(RandomValue() * 2 - 1, RandomValue() * 2 - 1, RandomValue() * 2 - 1, RandomValue() * 2 - 1));
        public static void UnityEngine_Random__INTERNAL_get_rotationUniform(out Quaternion value) => UnityEngine_Random__INTERNAL_get_rotation(out value);

        // ------------------------------------------------------------ Font
        public static void UnityEngine_Font__Internal_CreateFont(Font _font, string name)
        {
            if (Objects.Of(_font) == null) Objects.Register(_font, new NAsset(), name ?? "");
        }
        public static void UnityEngine_Font__Internal_CreateDynamicFont(Font _font, string[] _names, int size)
            => UnityEngine_Font__Internal_CreateFont(_font, _names != null && _names.Length > 0 ? _names[0] : "");
        public static string[] UnityEngine_Font__GetOSInstalledFontNames() => new string[0];

        // ------------------------------------------------------------ legacy Input
        // The console has no keyboard, mouse or touch. Pads reach the game
        // through Rewired, not UnityEngine.Input.
        public static bool UnityEngine_Input__GetKeyInt(int key) => false;
        public static bool UnityEngine_Input__GetKeyString(string name) => false;
        public static bool UnityEngine_Input__GetKeyUpInt(int key) => false;
        public static bool UnityEngine_Input__GetKeyUpString(string name) => false;
        public static bool UnityEngine_Input__GetKeyDownInt(int key) => false;
        public static bool UnityEngine_Input__GetKeyDownString(string name) => false;
        public static bool UnityEngine_Input__GetButton(string buttonName) => false;
        public static bool UnityEngine_Input__GetButtonDown(string buttonName) => false;
        public static bool UnityEngine_Input__GetButtonUp(string buttonName) => false;
        public static float UnityEngine_Input__GetAxis(string axisName) => 0f;
        public static float UnityEngine_Input__GetAxisRaw(string axisName) => 0f;
        public static bool UnityEngine_Input__get_mousePresent() => false;
        public static void UnityEngine_Input__INTERNAL_get_mousePosition(out Vector3 value) => value = Vector3.zero;
        public static string[] UnityEngine_Input__GetJoystickNames() => new string[0];
        public static bool UnityEngine_Input__get_touchSupported() => false;
        public static int UnityEngine_Input__get_touchCount() => 0;
        public static bool UnityEngine_Input__get_anyKey() => false;
        public static bool UnityEngine_Input__get_anyKeyDown() => false;
    }
}
