using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Cuphead360.Engine
{
    // Native-backed value classes (AnimationCurve, Gradient, RectOffset): their
    // state lives here, keyed by the managed object.
    public static partial class Icalls
    {
        sealed class Curve
        {
            public List<Keyframe> Keys = new List<Keyframe>();
            public WrapMode Pre = WrapMode.ClampForever, Post = WrapMode.ClampForever;
        }
        static readonly ConditionalWeakTable<AnimationCurve, Curve> curves = new ConditionalWeakTable<AnimationCurve, Curve>();
        static Curve C(AnimationCurve a) => curves.GetValue(a, _ => new Curve());

        /// <summary>The serialized m_PreInfinity / m_PostInfinity codes (0 ping-pong, 1 loop, 2 clamp).</summary>
        public static WrapMode CurveWrapFromInternal(int code) => code == 0 ? WrapMode.PingPong : code == 1 ? WrapMode.Loop : WrapMode.ClampForever;

        public static void UnityEngine_AnimationCurve__Init(AnimationCurve self, Keyframe[] keys) { var c = C(self); c.Keys.Clear(); if (keys != null) c.Keys.AddRange(keys); Sort(c); }
        public static void UnityEngine_AnimationCurve__Cleanup(AnimationCurve self) { }
        public static void UnityEngine_AnimationCurve__SetKeys(AnimationCurve self, Keyframe[] keys) => UnityEngine_AnimationCurve__Init(self, keys);
        public static Keyframe[] UnityEngine_AnimationCurve__GetKeys(AnimationCurve self) => C(self).Keys.ToArray();
        public static int UnityEngine_AnimationCurve__get_length(AnimationCurve self) => C(self).Keys.Count;
        public static void UnityEngine_AnimationCurve__INTERNAL_CALL_GetKey_Internal(AnimationCurve self, int index, out Keyframe value) => value = C(self).Keys[index];
        public static int UnityEngine_AnimationCurve__AddKey(AnimationCurve self, float time, float value)
        {
            var k = new Keyframe(time, value);
            return UnityEngine_AnimationCurve__INTERNAL_CALL_AddKey_Internal(self, ref k);
        }
        public static int UnityEngine_AnimationCurve__INTERNAL_CALL_AddKey_Internal(AnimationCurve self, ref Keyframe key)
        {
            var c = C(self);
            foreach (var k in c.Keys) if (k.time == key.time) return -1;
            c.Keys.Add(key); Sort(c);
            return c.Keys.IndexOf(key);
        }
        public static int UnityEngine_AnimationCurve__INTERNAL_CALL_MoveKey(AnimationCurve self, int index, ref Keyframe key)
        {
            var c = C(self);
            c.Keys.RemoveAt(index); c.Keys.Add(key); Sort(c);
            return c.Keys.IndexOf(key);
        }
        public static void UnityEngine_AnimationCurve__RemoveKey(AnimationCurve self, int index) => C(self).Keys.RemoveAt(index);
        public static void UnityEngine_AnimationCurve__SmoothTangents(AnimationCurve self, int index, float weight) { }
        public static WrapMode UnityEngine_AnimationCurve__get_preWrapMode(AnimationCurve self) => C(self).Pre;
        public static void UnityEngine_AnimationCurve__set_preWrapMode(AnimationCurve self, WrapMode value) => C(self).Pre = value;
        public static WrapMode UnityEngine_AnimationCurve__get_postWrapMode(AnimationCurve self) => C(self).Post;
        public static void UnityEngine_AnimationCurve__set_postWrapMode(AnimationCurve self, WrapMode value) => C(self).Post = value;
        static void Sort(Curve c) => c.Keys.Sort((a, b) => a.time.CompareTo(b.time));

        public static float UnityEngine_AnimationCurve__Evaluate(AnimationCurve self, float time) => EvaluateCurve(C(self).Keys, C(self).Pre, C(self).Post, time);

        public static float EvaluateCurve(List<Keyframe> keys, WrapMode pre, WrapMode post, float t)
        {
            int n = keys.Count;
            if (n == 0) return 0f;
            if (n == 1) return keys[0].value;
            float t0 = keys[0].time, t1 = keys[n - 1].time, len = t1 - t0;
            if (t < t0 || t > t1)
            {
                var mode = t < t0 ? pre : post;
                if (len > 0f && (mode == WrapMode.Loop || mode == WrapMode.PingPong))
                {
                    float x = (t - t0) / len;
                    if (mode == WrapMode.Loop) x -= MathF.Floor(x);
                    else { x = x - 2f * MathF.Floor(x / 2f); if (x > 1f) x = 2f - x; }
                    t = t0 + x * len;
                }
                else return t < t0 ? keys[0].value : keys[n - 1].value;
            }
            int i = 0;
            while (i < n - 2 && t > keys[i + 1].time) i++;
            var a = keys[i]; var b = keys[i + 1];
            float dt = b.time - a.time;
            if (dt <= 0f) return b.value;
            if (float.IsInfinity(a.outTangent) || float.IsInfinity(b.inTangent)) return a.value;
            float s = (t - a.time) / dt, s2 = s * s, s3 = s2 * s;
            float m0 = a.outTangent * dt, m1 = b.inTangent * dt;
            return (2 * s3 - 3 * s2 + 1) * a.value + (s3 - 2 * s2 + s) * m0 + (s3 - s2) * m1 + (-2 * s3 + 3 * s2) * b.value;
        }

        sealed class Grad
        {
            public GradientColorKey[] Colors = { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) };
            public GradientAlphaKey[] Alphas = { new GradientAlphaKey(1, 0), new GradientAlphaKey(1, 1) };
            public GradientMode Mode;
        }
        static readonly ConditionalWeakTable<Gradient, Grad> gradients = new ConditionalWeakTable<Gradient, Grad>();
        static Grad G(Gradient g) => gradients.GetValue(g, _ => new Grad());

        public static void UnityEngine_Gradient__Init(Gradient self) => G(self);
        public static void UnityEngine_Gradient__Cleanup(Gradient self) { }
        public static GradientColorKey[] UnityEngine_Gradient__get_colorKeys(Gradient self) => (GradientColorKey[])G(self).Colors.Clone();
        public static void UnityEngine_Gradient__set_colorKeys(Gradient self, GradientColorKey[] value) => G(self).Colors = value ?? new GradientColorKey[0];
        public static GradientAlphaKey[] UnityEngine_Gradient__get_alphaKeys(Gradient self) => (GradientAlphaKey[])G(self).Alphas.Clone();
        public static void UnityEngine_Gradient__set_alphaKeys(Gradient self, GradientAlphaKey[] value) => G(self).Alphas = value ?? new GradientAlphaKey[0];
        public static GradientMode UnityEngine_Gradient__get_mode(Gradient self) => G(self).Mode;
        public static void UnityEngine_Gradient__set_mode(Gradient self, GradientMode value) => G(self).Mode = value;
        public static void UnityEngine_Gradient__SetKeys(Gradient self, GradientColorKey[] colorKeys, GradientAlphaKey[] alphaKeys)
        {
            var g = G(self); g.Colors = colorKeys ?? new GradientColorKey[0]; g.Alphas = alphaKeys ?? new GradientAlphaKey[0];
        }
        public static void UnityEngine_Gradient__INTERNAL_CALL_Evaluate(Gradient self, float time, out Color value)
        {
            var g = G(self);
            bool step = g.Mode == GradientMode.Fixed;
            Color c = Color.white;
            var ck = g.Colors;
            if (ck.Length > 0)
            {
                c = ck[ck.Length - 1].color;
                if (time <= ck[0].time) c = ck[0].color;
                else for (int i = 0; i + 1 < ck.Length; i++)
                        if (time <= ck[i + 1].time)
                        {
                            float u = (time - ck[i].time) / Math.Max(1e-6f, ck[i + 1].time - ck[i].time);
                            c = step ? ck[i + 1].color : Color.Lerp(ck[i].color, ck[i + 1].color, u);
                            break;
                        }
            }
            float a = 1f;
            var ak = g.Alphas;
            if (ak.Length > 0)
            {
                a = ak[ak.Length - 1].alpha;
                if (time <= ak[0].time) a = ak[0].alpha;
                else for (int i = 0; i + 1 < ak.Length; i++)
                        if (time <= ak[i + 1].time)
                        {
                            float u = (time - ak[i].time) / Math.Max(1e-6f, ak[i + 1].time - ak[i].time);
                            a = step ? ak[i + 1].alpha : Mathf.Lerp(ak[i].alpha, ak[i + 1].alpha, u);
                            break;
                        }
            }
            c.a = a;
            value = c;
        }

        sealed class Offset { public int L, R, T, B; }
        static readonly ConditionalWeakTable<RectOffset, Offset> offsets = new ConditionalWeakTable<RectOffset, Offset>();
        static Offset O(RectOffset r) => offsets.GetValue(r, _ => new Offset());
        public static void UnityEngine_RectOffset__Init(RectOffset self) => O(self);
        public static void UnityEngine_RectOffset__Cleanup(RectOffset self) { }
        public static int UnityEngine_RectOffset__get_left(RectOffset self) => O(self).L;
        public static void UnityEngine_RectOffset__set_left(RectOffset self, int value) => O(self).L = value;
        public static int UnityEngine_RectOffset__get_right(RectOffset self) => O(self).R;
        public static void UnityEngine_RectOffset__set_right(RectOffset self, int value) => O(self).R = value;
        public static int UnityEngine_RectOffset__get_top(RectOffset self) => O(self).T;
        public static void UnityEngine_RectOffset__set_top(RectOffset self, int value) => O(self).T = value;
        public static int UnityEngine_RectOffset__get_bottom(RectOffset self) => O(self).B;
        public static void UnityEngine_RectOffset__set_bottom(RectOffset self, int value) => O(self).B = value;
        public static int UnityEngine_RectOffset__get_horizontal(RectOffset self) => O(self).L + O(self).R;
        public static int UnityEngine_RectOffset__get_vertical(RectOffset self) => O(self).T + O(self).B;
        public static void UnityEngine_RectOffset__INTERNAL_CALL_Add(RectOffset self, ref Rect rect, out Rect value)
        { var o = O(self); value = new Rect(rect.x - o.L, rect.y - o.T, rect.width + o.L + o.R, rect.height + o.T + o.B); }
        public static void UnityEngine_RectOffset__INTERNAL_CALL_Remove(RectOffset self, ref Rect rect, out Rect value)
        { var o = O(self); value = new Rect(rect.x + o.L, rect.y + o.T, rect.width - o.L - o.R, rect.height - o.T - o.B); }
    }
}
