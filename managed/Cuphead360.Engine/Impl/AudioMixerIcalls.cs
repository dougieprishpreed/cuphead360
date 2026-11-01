using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Audio;

namespace Cuphead360.Engine
{
    /// <summary>
    /// AudioMixer parameter state, from the player-side mixer constant the
    /// converter keeps (m_MixerConstant): every snapshot is an array of
    /// parameter values (group volume/pitch, effect parameters), exposed
    /// parameters are CRC32 name hashes pointing into that array. The mixer
    /// starts at m_StartSnapshot; TransitionToSnapshots blends linearly to the
    /// weighted target over timeToReach (scaled time, or unscaled time in
    /// UnscaledTime update mode); SetFloat overrides an exposed parameter until
    /// ClearFloat. This is the state the audio renderer will read.
    /// </summary>
    public sealed class MixerState
    {
        public float[][] Snapshots;
        public Guid4[] SnapshotIds;
        public Dictionary<uint, int> Exposed = new Dictionary<uint, int>();
        public float[] From, To;
        public float Start, Length;
        public bool Unscaled;
        public Dictionary<int, float> Overrides = new Dictionary<int, float>();

        float Now => Unscaled ? Clock.UnscaledTime : Clock.Time;

        public float Value(int i) => Overrides.TryGetValue(i, out var o) ? o : Blended(i);

        float Blended(int i)
        {
            if (To == null || i >= To.Length) return 0f;
            float k = Length <= 0f ? 1f : Mathf.Clamp01((Now - Start) / Length);
            return From[i] + (To[i] - From[i]) * k;
        }

        public void Transition(float[] target, float time)
        {
            var cur = new float[target.Length];
            for (int i = 0; i < cur.Length; i++) cur[i] = Blended(i);
            From = cur; To = target; Start = Now; Length = time;
        }
    }

    public struct Guid4 : IEquatable<Guid4>
    {
        public long A, B, C, D;
        public static Guid4 Of(object t) => new Guid4
        {
            A = Tree.Int(t, "data[0]"), B = Tree.Int(t, "data[1]"), C = Tree.Int(t, "data[2]"), D = Tree.Int(t, "data[3]"),
        };
        public bool Equals(Guid4 o) => A == o.A && B == o.B && C == o.C && D == o.D;
    }

    public static partial class Icalls
    {
        static readonly ConditionalWeakTable<AudioMixer, MixerState> mixers = new ConditionalWeakTable<AudioMixer, MixerState>();

        static ChobObject AssetSource(UnityEngine.Object o) => (Objects.Of(o) as NAsset)?.Source;

        static float F(object v) => v switch { float f => f, double d => (float)d, long l => l, _ => 0f };

        public static uint Crc32(string s)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte b in System.Text.Encoding.UTF8.GetBytes(s))
            {
                c ^= b;
                for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320u & (uint)-(int)(c & 1));
            }
            return ~c;
        }

        public static MixerState Mixer(AudioMixer m)
        {
            if (m == null) return null;
            return mixers.GetValue(m, mm =>
            {
                var st = new MixerState();
                var src = AssetSource(mm);
                var mc = src == null ? null : Tree.Get(src.Tree, "m_MixerConstant");
                if (mc == null)
                {
                    if (src != null) AssetDb.Problem("AudioMixer without m_MixerConstant: " + mm.name);
                    st.Snapshots = new float[0][]; st.SnapshotIds = new Guid4[0];
                    return st;
                }
                var snaps = Tree.List(mc, "snapshots");
                st.Snapshots = new float[snaps.Count][];
                for (int i = 0; i < snaps.Count; i++)
                {
                    var vals = Tree.List(snaps[i], "values");
                    st.Snapshots[i] = new float[vals.Count];
                    for (int j = 0; j < vals.Count; j++) st.Snapshots[i][j] = F(vals[j]);
                }
                var ids = Tree.List(mc, "snapshotGUIDs");
                st.SnapshotIds = new Guid4[ids.Count];
                for (int i = 0; i < ids.Count; i++) st.SnapshotIds[i] = Guid4.Of(ids[i]);
                var names = Tree.List(mc, "exposedParameterNames");
                var idx = Tree.List(mc, "exposedParameterIndices");
                for (int i = 0; i < names.Count && i < idx.Count; i++) st.Exposed[(uint)(long)names[i]] = (int)(long)idx[i];
                st.Unscaled = Tree.Int(src.Tree, "m_UpdateMode") == (long)AudioMixerUpdateMode.UnscaledTime;
                int start = SnapshotIndex(st, Resolve(src, Tree.Ptr(src.Tree, "m_StartSnapshot")));
                var first = start >= 0 ? st.Snapshots[start] : (st.Snapshots.Length > 0 ? st.Snapshots[0] : new float[0]);
                st.From = (float[])first.Clone(); st.To = (float[])first.Clone();
                return st;
            });
        }

        static UnityEngine.Object Resolve(ChobObject from, PPtr p) => p.IsNull ? null : AssetDb.Resolve(from.File, p, null);

        static int SnapshotIndex(MixerState st, UnityEngine.Object snapshot)
        {
            var s = snapshot == null ? null : AssetSource(snapshot);
            if (s == null) return -1;
            var id = Guid4.Of(Tree.Get(s.Tree, "m_SnapshotID"));
            return Array.IndexOf(st.SnapshotIds, id);
        }

        public static bool UnityEngine_Audio_AudioMixer__GetFloat(AudioMixer self, string @name, out float @value)
        {
            var st = Mixer(self);
            if (st != null && @name != null && st.Exposed.TryGetValue(Crc32(@name), out int i)) { @value = st.Value(i); return true; }
            @value = 0f;
            return false;
        }

        public static bool UnityEngine_Audio_AudioMixer__SetFloat(AudioMixer self, string @name, float @value)
        {
            var st = Mixer(self);
            if (st == null || @name == null || !st.Exposed.TryGetValue(Crc32(@name), out int i)) return false;
            st.Overrides[i] = @value;
            return true;
        }

        public static bool UnityEngine_Audio_AudioMixer__ClearFloat(AudioMixer self, string @name)
        {
            var st = Mixer(self);
            if (st == null || @name == null || !st.Exposed.TryGetValue(Crc32(@name), out int i)) return false;
            st.Overrides.Remove(i);
            return true;
        }

        public static AudioMixerSnapshot UnityEngine_Audio_AudioMixer__FindSnapshot(AudioMixer self, string @name)
        {
            var src = AssetSource(self);
            if (src == null || @name == null) return null;
            foreach (var p in Tree.List(src.Tree, "m_Snapshots"))
                if (p is PPtr pp && Resolve(src, pp) is AudioMixerSnapshot s && s.name == @name) return s;
            return null;
        }

        public static AudioMixerGroup[] UnityEngine_Audio_AudioMixer__FindMatchingGroups(AudioMixer self, string @subPath)
        {
            // Unity matches group paths ("Master/SFX/...") by substring.
            var r = new List<AudioMixerGroup>();
            var src = AssetSource(self);
            if (src == null) return r.ToArray();
            void Walk(UnityEngine.Object g, string path)
            {
                if (!(g is AudioMixerGroup grp)) return;
                string p = path.Length == 0 ? grp.name : path + "/" + grp.name;
                if (string.IsNullOrEmpty(@subPath) || p.IndexOf(@subPath, StringComparison.OrdinalIgnoreCase) >= 0) r.Add(grp);
                var gs = AssetSource(grp);
                if (gs != null) foreach (var c in Tree.List(gs.Tree, "m_Children")) if (c is PPtr cp) Walk(Resolve(gs, cp), p);
            }
            Walk(Resolve(src, Tree.Ptr(src.Tree, "m_MasterGroup")), "");
            return r.ToArray();
        }

        public static void UnityEngine_Audio_AudioMixer__TransitionToSnapshots(AudioMixer self, AudioMixerSnapshot[] @snapshots, float[] @weights, float @timeToReach)
        {
            var st = Mixer(self);
            if (st == null || @snapshots == null || @weights == null || st.To == null) return;
            var target = new float[st.To.Length];
            float total = 0f;
            for (int k = 0; k < @snapshots.Length && k < @weights.Length; k++)
            {
                int i = SnapshotIndex(st, @snapshots[k]);
                if (i < 0) continue;
                float w = Mathf.Max(0f, @weights[k]);
                total += w;
                for (int j = 0; j < target.Length && j < st.Snapshots[i].Length; j++) target[j] += st.Snapshots[i][j] * w;
            }
            if (total <= 0f) return;
            for (int j = 0; j < target.Length; j++) target[j] /= total;
            st.Transition(target, @timeToReach);
        }

        public static void UnityEngine_Audio_AudioMixerSnapshot__TransitionTo(AudioMixerSnapshot self, float @timeToReach)
        {
            var mixer = self?.audioMixer;
            if (mixer != null) UnityEngine_Audio_AudioMixer__TransitionToSnapshots(mixer, new[] { self }, new[] { 1f }, @timeToReach);
        }
    }
}
