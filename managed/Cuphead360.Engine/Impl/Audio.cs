using System;
using System.Collections.Generic;
using System.IO;
using NVorbis;
using UnityEngine;
using UnityEngine.Audio;

namespace Cuphead360.Engine
{
    /// <summary>An AudioSource: its serialized settings and the voices it plays.</summary>
    public sealed class NAudioSource : NComponent
    {
        public AudioClip Clip;
        public AudioMixerGroup Group;
        public float Volume = 1f, Pitch = 1f, Pan, SpatialBlend;
        public bool Loop, Mute, PlayOnAwake = true, IgnoreListenerPause, IgnoreListenerVolume;
        // 3D: AudioRolloffMode (0 logarithmic, 1 linear, 2 custom), distances in world units, spread in degrees.
        public int Rolloff;
        public float MinDistance = 1f, MaxDistance = 500f, Spread;
        /// <summary>The custom curves over distance / MaxDistance, as serialized (null: none; a single key is a constant).</summary>
        public List<Keyframe> RolloffCurve, BlendCurve, SpreadCurve;
        /// <summary>The voice Play() started (null when stopped); one-shots are separate voices.</summary>
        internal Audio.Voice Main;
        /// <summary>Playback position set while stopped (Unity starts the next Play there).</summary>
        internal double PendingFrame;

        public override void Load(ChobObject co, SceneBuild b)
        {
            var t = co.Tree;
            Clip = b.Resolve(Tree.Ptr(t, "m_audioClip")) as AudioClip;
            Group = b.Resolve(Tree.Ptr(t, "OutputAudioMixerGroup")) as AudioMixerGroup;
            PlayOnAwake = Tree.Bool(t, "m_PlayOnAwake");
            Volume = Tree.Float(t, "m_Volume", 1f);
            Pitch = Tree.Float(t, "m_Pitch", 1f);
            Loop = Tree.Bool(t, "Loop");
            Mute = Tree.Bool(t, "Mute");
            Pan = Tree.Float(t, "Pan2D");
            Rolloff = (int)Tree.Int(t, "rolloffMode");
            MinDistance = Tree.Float(t, "MinDistance", 1f);
            MaxDistance = Tree.Float(t, "MaxDistance", 500f);
            RolloffCurve = Keys(t, "rolloffCustomCurve");
            BlendCurve = Keys(t, "panLevelCustomCurve");
            SpreadCurve = Keys(t, "spreadCustomCurve");
            SpatialBlend = BlendCurve != null ? BlendCurve[0].value : 0f;
            Spread = SpreadCurve != null ? SpreadCurve[0].value * 360f : 0f;
        }

        static List<Keyframe> Keys(object t, string key)
        {
            var list = Tree.List(Tree.Get(t, key), "m_Curve");
            if (list.Count == 0) return null;
            var keys = new List<Keyframe>(list.Count);
            foreach (var k in list) keys.Add(new Keyframe(Tree.Float(k, "time"), Tree.Float(k, "value"), Tree.Float(k, "inSlope"), Tree.Float(k, "outSlope")));
            return keys;
        }

        public override void CopyFrom(NComponent src, Dictionary<NObject, NObject> map)
        {
            var s = (NAudioSource)src;
            Clip = s.Clip; Group = s.Group; Volume = s.Volume; Pitch = s.Pitch; Pan = s.Pan; SpatialBlend = s.SpatialBlend;
            Loop = s.Loop; Mute = s.Mute; PlayOnAwake = s.PlayOnAwake; IgnoreListenerPause = s.IgnoreListenerPause; IgnoreListenerVolume = s.IgnoreListenerVolume;
            Rolloff = s.Rolloff; MinDistance = s.MinDistance; MaxDistance = s.MaxDistance; Spread = s.Spread;
            RolloffCurve = s.RolloffCurve; BlendCurve = s.BlendCurve; SpreadCurve = s.SpreadCurve;
        }

        // Unity plays a playOnAwake source when it becomes active and stops every
        // sound of a source that is disabled or destroyed.
        public override void Activated(bool on)
        {
            if (on) { if (PlayOnAwake && Clip != null && Main == null) Audio.Play(this, 0); }
            else Audio.StopAll(this);
        }

        public override void Removed() => Audio.StopAll(this);
    }

    /// <summary>An AudioListener: where 3D sources are heard from (every one made is kept, the newest live one listens).</summary>
    public sealed class NAudioListener : NComponent
    {
        static readonly List<NAudioListener> made = new List<NAudioListener>();
        static readonly Predicate<NAudioListener> isDestroyed = l => l.Destroyed;
        public NAudioListener() { made.Add(this); }

        public static NAudioListener Active
        {
            get
            {
                // The search below drops destroyed listeners newer than the live one only: older ones (every
                // scene's camera) stayed, and held what they reached (3,900 destroyed records after six levels).
                // Up to 32 were still kept: the map after three levels held 1,899 destroyed objects through them
                // (the heap census); every destroyed one goes now.
                made.RemoveAll(isDestroyed);
                for (int i = made.Count - 1; i >= 0; i--)
                {
                    var l = made[i];
                    if (l.Destroyed) { made.RemoveAt(i); continue; }
                    if (l.Enabled && l.GameObject != null && l.GameObject.ActiveInHierarchy) return l;
                }
                return null;
            }
        }
    }

    /// <summary>
    /// The audio renderer: voices of AudioSources, mixed on the game thread
    /// each frame to 48 kHz stereo through the game's AudioMixer group
    /// volumes and pitches (snapshots, exposed parameters). Clips come from
    /// tools/convert_audio.py (Ogg Vorbis, decoded with NVorbis): short clips
    /// are decoded whole and cached, long or streaming clips are decoded as
    /// they play. Playback positions advance in unscaled time whether or not
    /// anything listens; samples are only rendered when an output is attached
    /// (the live host's device, or a WAV capture).
    /// </summary>
    public static class Audio
    {
        public const int Rate = 48000;

        /// <summary>A converted clip's decoder: NVorbis here; the console host's native one (ConsoleHost.HostClip).</summary>
        public interface IClipReader : IDisposable
        {
            int Channels { get; }
            int SampleRate { get; }
            long TotalSamples { get; }
            /// <summary>The next read's first frame.</summary>
            long SamplePosition { set; }
            /// <summary>Interleaved samples (count of them, not frames); the samples read, 0 at the end.</summary>
            int ReadSamples(float[] buffer, int offset, int count);
        }

        sealed class NVorbisClip : IClipReader
        {
            readonly VorbisReader r;
            public NVorbisClip(string path) { r = new VorbisReader(path); }
            public int Channels => r.Channels;
            public int SampleRate => r.SampleRate;
            public long TotalSamples => r.TotalSamples;
            public long SamplePosition { set => r.SamplePosition = value; }
            public int ReadSamples(float[] buffer, int offset, int count) => r.ReadSamples(buffer, offset, count);
            public void Dispose() => r.Dispose();
        }

        /// <summary>Opens a converted clip (.ogg) for decoding, or null if it isn't there; the console host swaps in its native decoder.</summary>
        public static Func<string, IClipReader> OpenReader = path => File.Exists(path) ? new NVorbisClip(path) : null;

        /// <summary>
        /// The console host's native mixer (ConsoleHost.HostMixer, console/ch360_ogg.c): mixing here, as
        /// translated wasm on a big-endian CPU, moved every sample through byte-swapped words and the
        /// stack (2-3 ms of each console frame). With one set, short clips' samples are decoded into the
        /// host's memory and each voice mixes there; voices, gains and positions stay here.
        /// </summary>
        public interface IHostMixer
        {
            /// <summary>An opened clip decoded whole into the host's memory: its id (frames its frames), or -1.</summary>
            int Decode(IClipReader r, out long frames);
            void Free(int id);
            /// <summary>The host's handle of a streamed clip's reader, or -1.</summary>
            int Handle(IClipReader r);
            /// <summary>
            /// A mix of frames begins (the host's mixer held until End); the frames the host carried the last
            /// mix's voices on since, while the game was late (the console's audio thread), already played.
            /// </summary>
            int Begin(int frames);
            /// <summary>
            /// One voice (key: the same every mix while it plays) into the block over [start, frames): kind 1 a
            /// decoded clip, 2 a streamed one, 0 silence (only the position moves). True when a clip that
            /// doesn't loop ended; stepped counts the samples since the start or, when it wrapped, since the wrap
            /// at (wrapF0, wrapT). A voice the host carried on is given as wrapped at where it got to.
            /// </summary>
            bool Voice(int key, int kind, int id, int start, int frames, bool loop, long total, int channels, long f0, float t, long istep, float fstep,
                       float gl, float gr, out int stepped, out bool wrapped, out long wrapF0, out float wrapT);
            /// <summary>The block to the output.</summary>
            void End(int frames);
        }

        public static IHostMixer HostMixer;
        // The host's decoded clips, freed when their clip goes (AssetUnload) or the cache is over budget.
        sealed class HostEntry { public WeakReference<Clip> Clip; public int Id; public long Floats; }
        static readonly List<HostEntry> hostPcm = new List<HostEntry>();

        public sealed class Voice
        {
            static int keys;
            /// <summary>The host mixer's name for it (IHostMixer.Voice).</summary>
            public readonly int Key = ++keys;
            public NAudioSource Src;
            public Clip Clip;
            public double Frame;          // position in the clip's frames
            public float VolumeScale = 1f;
            public bool OneShot, Paused;
            public double Delay;          // seconds until it starts
            // Streaming decode state.
            public IClipReader Reader;
            public float[] Buf;
            public long BufStart, BufFrames;
        }

        /// <summary>A clip's facts (from its serialized fields) and, once loaded, its samples.</summary>
        public sealed class Clip
        {
            public string Key, Name;
            public int Channels = 1, Frequency = 44100;
            public long Frames;
            public bool Stream;
            public float[] Pcm;           // interleaved, short clips
            public int HostPcm = -1;      // the host mixer's decoded copy
            public bool Missing;
            public long LastUse;
        }

        static readonly List<Voice> voices = new List<Voice>();
        // Weak: a clip nothing references any more goes with its samples (AssetUnload).
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AudioClip, Clip> clips = new System.Runtime.CompilerServices.ConditionalWeakTable<AudioClip, Clip>();
        static long cachedSamples, useCounter, mixedFrames;
        /// <summary>Clips longer than this (seconds) stream rather than decode whole.</summary>
        public static float StreamOver = 1.5f;
        /// <summary>Decoded short clips kept, in floats (the console host lowers it).</summary>
        public static long CacheFloats = 128L * 1024 * 1024;
        static double frameCarry;
        static readonly HashSet<string> reported = new HashSet<string>();
        public static bool ListenerPause;
        public static float ListenerVolume = 1f;

        /// <summary>True when an output consumes samples (device, capture or the host's mixer); otherwise only positions advance.</summary>
        public static bool Render => Output != null || capture != null || HostMixer != null;
        /// <summary>Called with each mixed block (interleaved stereo floats) when set (the live host's device queue).</summary>
        public static Action<float[], int> Output;
        static BinaryWriter capture;
        static long capturedFrames;

        public static double DspTime => mixedFrames / (double)Rate;
        /// <summary>Stopwatch ticks spent decoding clips (streams and whole short clips).</summary>
        public static long DecodeTicks;

        /// <summary>
        /// The host reads a short clip ahead (path, the load's number): its first play then finds it in
        /// memory, not on the drive in the frame (null: no such host).
        /// </summary>
        public static Action<string, int> Preload;
        // Clips whose facts were read since the last load's preload (their assets built).
        static readonly List<WeakReference<Clip>> fresh = new List<WeakReference<Clip>>();
        static int preloadBatch;

        /// <summary>
        /// A scene load's end (under the loading screen): its short clips to the host to read ahead. more: those of
        /// what was made since, in play (ConsoleHost, once a second), joining the last load's (whose read-ahead a new
        /// batch would drop): the player is made as a level starts, and its first hit read its sound from the drive
        /// in that frame (the thirty-sixth run: a hitch at every first hit), as its first dash and death did.
        /// </summary>
        /// <remarks>
        /// A load sends every other clip still here too, the latest played first (the host skips those it keeps):
        /// read ahead only as they were built, the player's sounds were pushed out of the host's cache by the next
        /// level's read-ahead and a restart's music, and their first plays there read them in the frame (the
        /// Frogs' first dash, the first hit after a restart).
        /// </remarks>
        public static void PreloadFresh(bool more = false)
        {
            if (Preload == null || (more && (preloadBatch == 0 || fresh.Count == 0))) return;
            if (!more) preloadBatch++;
            var sent = new HashSet<Clip>();
            foreach (var w in fresh)
                if (w.TryGetTarget(out var c) && sent.Add(c)) PreloadOne(c);
            fresh.Clear();
            if (more) return;
            known.RemoveAll(w => !w.TryGetTarget(out _));
            var rest = new List<Clip>();
            foreach (var w in known) if (w.TryGetTarget(out var c) && !sent.Contains(c)) rest.Add(c);
            rest.Sort((a, b) => b.LastUse.CompareTo(a.LastUse));
            foreach (var c in rest) PreloadOne(c);
        }

        // streamed ones too: one under the host's size for reading off the game's thread opened in the frame (the
        // title's 59 ms frame as the logo played); the host keeps those under 256 KB
        static void PreloadOne(Clip c)
        {
            if (!c.Missing && c.HostPcm < 0 && PathOf(c) is string p) Preload(p, preloadBatch);
        }

        // Every clip built (weak: one whose asset went goes with it): a load's read-ahead of those still here.
        static readonly List<WeakReference<Clip>> known = new List<WeakReference<Clip>>();

        public static Clip ClipOf(AudioClip c)
        {
            if (c == null) return null;
            if (clips.TryGetValue(c, out var ci)) return ci;
            ci = new Clip { Name = c.name };
            var src = (Objects.Of(c) as NAsset)?.Source;
            if (src != null)
            {
                var t = src.Tree;
                ci.Channels = Math.Max(1, (int)Tree.Int(t, "m_Channels", 1));
                ci.Frequency = Math.Max(1, (int)Tree.Int(t, "m_Frequency", 44100));
                ci.Frames = (long)Math.Round(Tree.Float(t, "m_Length") * ci.Frequency);
                // Longer clips stream (decoded as they play): decoding one whole when it first
                // sounds took 0.4-0.6 s of a console frame (the world map's 3D loops as the player
                // neared them, the first EX shot's sound).
                ci.Stream = Tree.Int(t, "m_LoadType") == 2 || Tree.Float(t, "m_Length") > StreamOver;
                string k = src.File.Path.Replace('/', '_');
                if (k.EndsWith(".chob")) k = k.Substring(0, k.Length - 5);
                ci.Key = k + "_" + IdText.Of(src.PathId);
            }
            clips.AddOrUpdate(c, ci);
            if (Preload != null)
            {
                var w = new WeakReference<Clip>(ci);
                if (fresh.Count < 4096) fresh.Add(w);
                known.Add(w);
            }
            return ci;
        }

        static string PathOf(Clip c) => c.Key == null || AssetDb.Root == null ? null : Path.Combine(AssetDb.Root, "audio", c.Key + ".ogg");

        static IClipReader Open(Clip c)
        {
            string fn = PathOf(c);
            // One lookup (each costs a scan of the folder on the console's FAT32 drive).
            var r = fn != null ? OpenReader(fn) : null;
            if (r == null)
            {
                c.Missing = true;
                if (reported.Add(c.Name ?? "?")) Log.Line($"AUDIO clip not converted or unreadable: {c.Name} ({c.Key}); run tools/convert_audio.py");
                return null;
            }
            if (r.Channels != c.Channels || r.SampleRate != c.Frequency) { c.Channels = r.Channels; c.Frequency = r.SampleRate; }
            if (r.TotalSamples > 0) c.Frames = r.TotalSamples;
            return r;
        }

        static float[] Pcm(Clip c)
        {
            c.LastUse = ++useCounter;
            if (c.Pcm != null || c.Missing) return c.Pcm;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            using (var r = Open(c))
            {
                if (r == null) return null;
                var data = new float[Math.Max(0, c.Frames) * c.Channels];
                int got = 0, n;
                while (got < data.Length && (n = r.ReadSamples(data, got, Math.Min(16384, data.Length - got))) > 0) got += n;
                c.Pcm = data;
                cachedSamples += data.Length;
            }
            DecodeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            // Keep the decoded cache bounded (CacheFloats; about 512 MB of floats by default).
            if (cachedSamples > CacheFloats)
            {
                // Counted again from the clips still here (unloaded clips took their samples with them).
                var all = new List<Clip>();
                cachedSamples = 0;
                foreach (var kv in clips) { all.Add(kv.Value); if (kv.Value.Pcm != null) cachedSamples += kv.Value.Pcm.Length; }
                all.Sort((a, b) => a.LastUse.CompareTo(b.LastUse));
                foreach (var o in all)
                {
                    if (cachedSamples <= CacheFloats * 3 / 4) break;
                    if (o.Pcm == null || o == c || voices.Exists(v => v.Clip == o)) continue;
                    cachedSamples -= o.Pcm.Length; o.Pcm = null;
                }
            }
            return c.Pcm;
        }

        /// <summary>A short clip decoded whole into the host's memory (HostMixer): its id, or -1.</summary>
        static int HostPcmOf(Clip c)
        {
            c.LastUse = ++useCounter;
            if (c.HostPcm >= 0 || c.Missing) return c.HostPcm;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            using (var r = Open(c))
            {
                if (r == null) return -1;
                int id = HostMixer.Decode(r, out long frames);
                if (id < 0)
                {
                    // Out of host memory or ids: the other decoded clips go and it tries once more.
                    TrimHost(c, 0);
                    id = HostMixer.Decode(r, out frames);
                }
                if (id < 0)
                {
                    c.Missing = true;
                    Log.Line($"AUDIO clip {c.Name}: no memory to decode it");
                    return -1;
                }
                c.HostPcm = id;
                long floats = frames * c.Channels;
                cachedSamples += floats;
                hostPcm.Add(new HostEntry { Clip = new WeakReference<Clip>(c), Id = id, Floats = floats });
            }
            DecodeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            if (cachedSamples > CacheFloats) TrimHost(c, CacheFloats * 3 / 4);
            return c.HostPcm;
        }

        /// <summary>The host's copies of clips that are gone, then the least recently used until the cache is down to keepFloats.</summary>
        static void TrimHost(Clip keep, long keepFloats)
        {
            ReleaseUnloaded();
            if (cachedSamples <= keepFloats) return;
            var live = new List<(Clip c, HostEntry e)>();
            foreach (var e in hostPcm) if (e.Clip.TryGetTarget(out var c)) live.Add((c, e));
            live.Sort((a, b) => a.c.LastUse.CompareTo(b.c.LastUse));
            foreach (var (o, e) in live)
            {
                if (cachedSamples <= keepFloats) break;
                if (o == keep || voices.Exists(v => v.Clip == o)) continue;
                HostMixer.Free(e.Id);
                o.HostPcm = -1;
                cachedSamples -= e.Floats;
                hostPcm.Remove(e);
            }
        }

        /// <summary>Frees the host's copies of clips the engine let go (after an AssetUnload pass).</summary>
        public static void ReleaseUnloaded()
        {
            if (HostMixer == null) return;
            for (int i = hostPcm.Count - 1; i >= 0; i--)
                if (!hostPcm[i].Clip.TryGetTarget(out _))
                {
                    HostMixer.Free(hostPcm[i].Id);
                    cachedSamples -= hostPcm[i].Floats;
                    hostPcm.RemoveAt(i);
                }
        }

        // ------------------------------------------------------------ playback

        public static void Play(NAudioSource s, double delaySeconds)
        {
            if (s == null) return;
            if (s.Main != null) voices.Remove(s.Main);
            s.Main = null;
            var c = ClipOf(s.Clip);
            if (c == null) return;
            var v = new Voice { Src = s, Clip = c, Frame = s.PendingFrame, Delay = delaySeconds };
            s.PendingFrame = 0;
            s.Main = v;
            voices.Add(v);
        }

        public static void PlayOneShot(NAudioSource s, AudioClip clip, float volumeScale)
        {
            var c = ClipOf(clip);
            if (s == null || c == null) return;
            voices.Add(new Voice { Src = s, Clip = c, OneShot = true, VolumeScale = volumeScale });
        }

        public static void Stop(NAudioSource s)
        {
            if (s?.Main == null) return;
            Close(s.Main);
            voices.Remove(s.Main);
            s.Main = null;
        }

        public static void StopAll(NAudioSource s)
        {
            if (s == null) return;
            for (int i = voices.Count - 1; i >= 0; i--)
                if (voices[i].Src == s) { Close(voices[i]); voices.RemoveAt(i); }
            s.Main = null;
        }

        public static void Pause(NAudioSource s, bool paused)
        {
            foreach (var v in voices) if (v.Src == s) v.Paused = paused;
        }

        public static bool IsPlaying(NAudioSource s)
        {
            foreach (var v in voices) if (v.Src == s && !v.Paused) return true;
            return false;
        }

        public static double Time(NAudioSource s) => s?.Main != null ? s.Main.Frame : s?.PendingFrame ?? 0;

        public static void Seek(NAudioSource s, double frame)
        {
            if (s == null) return;
            if (s.Main == null) { s.PendingFrame = Math.Max(0, frame); return; }
            s.Main.Frame = Math.Max(0, frame);
            if (s.Main.Reader != null) { s.Main.BufFrames = 0; s.Main.BufStart = (long)s.Main.Frame; s.Main.Reader.SamplePosition = s.Main.BufStart; }
        }

        static void Close(Voice v) { v.Reader?.Dispose(); v.Reader = null; }

        // ------------------------------------------------------------ mixer groups

        sealed class GroupInfo { public MixerState Mixer; public int[] Volumes, Pitches; public bool Muted; }
        static readonly Dictionary<AudioMixerGroup, GroupInfo> groups = new Dictionary<AudioMixerGroup, GroupInfo>();

        static GroupInfo Info(AudioMixerGroup g)
        {
            if (g == null) return null;
            if (groups.TryGetValue(g, out var gi)) return gi;
            gi = new GroupInfo { Volumes = new int[0], Pitches = new int[0] };
            groups[g] = gi;
            var src = (Objects.Of(g) as NAsset)?.Source;
            var mixer = src == null ? null : AssetDb.Resolve(src.File, Tree.Ptr(src.Tree, "m_AudioMixer"), null) as AudioMixer;
            var msrc = mixer == null ? null : (Objects.Of(mixer) as NAsset)?.Source;
            var mc = msrc == null ? null : Tree.Get(msrc.Tree, "m_MixerConstant");
            if (mc == null) return gi;
            var id = Guid4.Of(Tree.Get(src.Tree, "m_GroupID"));
            var guids = Tree.List(mc, "groupGUIDs");
            var list = Tree.List(mc, "groups");
            int idx = -1;
            for (int i = 0; i < guids.Count; i++) if (Guid4.Of(guids[i]).Equals(id)) { idx = i; break; }
            var vols = new List<int>(); var pitches = new List<int>();
            for (int i = idx, guard = 0; i >= 0 && i < list.Count && guard < 64; guard++)
            {
                vols.Add((int)Tree.Int(list[i], "volumeIndex")); pitches.Add((int)Tree.Int(list[i], "pitchIndex"));
                if (Tree.Bool(list[i], "mute")) gi.Muted = true;
                i = (int)Tree.Int(list[i], "parentConstantIndex");
            }
            gi.Mixer = Icalls.Mixer(mixer); gi.Volumes = vols.ToArray(); gi.Pitches = pitches.ToArray();
            return gi;
        }

        static void GroupGain(AudioMixerGroup g, out float gain, out float pitch)
        {
            gain = 1f; pitch = 1f;
            var gi = Info(g);
            if (gi?.Mixer == null) return;
            if (gi.Muted) { gain = 0f; return; }
            float db = 0f;
            foreach (int i in gi.Volumes) db += gi.Mixer.Value(i);
            gain = db <= -80f ? 0f : (float)Math.Pow(10.0, db / 20.0);
            foreach (int i in gi.Pitches) { float p = gi.Mixer.Value(i); if (p > 0f) pitch *= p; }
        }

        // ------------------------------------------------------------ mixing

        static float[] mix = new float[0];

        /// <summary>Advance every voice by one frame of unscaled time and render it if an output listens.</summary>
        public static void Step(float unscaledDt)
        {
            frameCarry += Math.Max(0f, unscaledDt) * Rate;
            int frames = (int)frameCarry;
            frameCarry -= frames;
            if (frames <= 0) return;
            if (HostMixer != null) { StepHost(frames); return; }
            bool render = Render;
            if (render)
            {
                if (mix.Length < frames * 2) mix = new float[frames * 2];
                Array.Clear(mix, 0, frames * 2);
            }
            var listener = NAudioListener.Active;
            Vector3 hearAt = default, right = Vector3.right;
            if (listener != null) { var lt = listener.GameObject.Transform; hearAt = lt.Position; right = lt.Rotation * Vector3.right; }
            for (int vi = voices.Count - 1; vi >= 0; vi--)
            {
                var v = voices[vi];
                var s = v.Src;
                if (s == null || s.Destroyed) { Close(v); voices.RemoveAt(vi); continue; }
                if (v.Paused || (ListenerPause && !s.IgnoreListenerPause)) continue;
                int start = 0;
                if (v.Delay > 0)
                {
                    double d = v.Delay * Rate;
                    if (d >= frames) { v.Delay -= frames / (double)Rate; continue; }
                    start = (int)d; v.Delay = 0;
                }
                GroupGain(s.Group, out float gain, out float gpitch);
                gain *= s.Mute ? 0f : s.Volume * v.VolumeScale * (s.IgnoreListenerVolume ? 1f : ListenerVolume);
                float pan = s.Pan;
                if (listener != null && s.GameObject != null && (s.SpatialBlend > 0f || s.BlendCurve?.Count > 1)) Spatial(s, hearAt, right, ref gain, ref pan);
                double step = v.Clip.Frequency / (double)Rate * Math.Max(0f, s.Pitch) * gpitch;
                bool loop = !v.OneShot && s.Loop;
                bool ended = Advance(v, mix, start, frames, step, gain, pan, loop, render);
                if (ended)
                {
                    Close(v);
                    voices.RemoveAt(vi);
                    if (s.Main == v) s.Main = null;
                }
            }
            mixedFrames += frames;
            if (!render) return;
            Output?.Invoke(mix, frames);
            if (capture != null)
            {
                for (int i = 0; i < frames * 2; i++) capture.Write((short)Math.Round(Math.Clamp(mix[i], -1f, 1f) * 32767f));
                capturedFrames += frames;
            }
        }

        // A voice's mix in the host (StepHost's plan).
        sealed class Plan
        {
            public Voice V;
            public int Kind, Id, Start, Channels;
            public bool Loop, Ended;
            public long Total, F0, IStep;
            public float T, FStep, Gl, Gr;
            public double Step;
        }

        static readonly List<Plan> plans = new List<Plan>();

        /// <summary>
        /// Step with the host's mixer, in two passes: each voice's clip made ready and its mix worked out (a
        /// clip's first play reads it from the drive or decodes its first frames), then the mix, which holds
        /// the host's mixer from Begin to End: the console's audio thread carries the last mix's voices on
        /// while the game is late (ch360_ogg.c), and waits for nothing slow. The frames it carried on count
        /// as played (the clock and the voices' positions).
        /// </summary>
        static void StepHost(int frames)
        {
            var listener = NAudioListener.Active;
            Vector3 hearAt = default, right = Vector3.right;
            if (listener != null) { var lt = listener.GameObject.Transform; hearAt = lt.Position; right = lt.Rotation * Vector3.right; }
            int planned = 0;
            for (int vi = voices.Count - 1; vi >= 0; vi--)
            {
                var v = voices[vi];
                var s = v.Src;
                if (s == null || s.Destroyed) { Close(v); voices.RemoveAt(vi); continue; }
                if (v.Paused || (ListenerPause && !s.IgnoreListenerPause)) continue;
                int start = 0;
                if (v.Delay > 0)
                {
                    double d = v.Delay * Rate;
                    if (d >= frames) { v.Delay -= frames / (double)Rate; continue; }
                    start = (int)d; v.Delay = 0;
                }
                GroupGain(s.Group, out float gain, out float gpitch);
                gain *= s.Mute ? 0f : s.Volume * v.VolumeScale * (s.IgnoreListenerVolume ? 1f : ListenerVolume);
                float pan = s.Pan;
                if (listener != null && s.GameObject != null && (s.SpatialBlend > 0f || s.BlendCurve?.Count > 1)) Spatial(s, hearAt, right, ref gain, ref pan);
                var c = v.Clip;
                double step = c.Frequency / (double)Rate * Math.Max(0f, s.Pitch) * gpitch;
                bool loop = !v.OneShot && s.Loop;
                bool ended = c.Frames <= 0;
                if (!ended && gain <= 0f)
                {
                    // silent: only its position moves (a streaming voice's buffer repositions itself when it next sounds)
                    v.Frame += step * (frames - start);
                    if (v.Frame >= c.Frames) { if (!loop) ended = true; else v.Frame %= c.Frames; }
                    if (!ended) continue;
                }
                int kind = 0, id = -1;
                if (!ended && !c.Stream)
                {
                    id = HostPcmOf(c);
                    if (id >= 0) kind = 1;
                }
                else if (!ended)
                {
                    if (v.Reader == null && !c.Missing)
                    {
                        v.Reader = Open(c);
                        if (v.Reader != null) v.Reader.SamplePosition = (long)v.Frame;
                    }
                    if (v.Reader != null && (id = HostMixer.Handle(v.Reader)) >= 0) kind = 2;
                }
                // decoding can correct the clip's frame count
                if (ended || c.Frames <= 0)
                {
                    Close(v);
                    voices.RemoveAt(vi);
                    if (s.Main == v) s.Main = null;
                    continue;
                }
                if (planned == plans.Count) plans.Add(new Plan());
                var p = plans[planned++];
                long f0 = (long)v.Frame, istep = (long)step;
                p.V = v; p.Kind = kind; p.Id = id; p.Start = start; p.Channels = c.Channels; p.Loop = loop; p.Ended = false;
                p.Total = c.Frames; p.F0 = f0; p.T = (float)(v.Frame - f0); p.IStep = istep; p.FStep = (float)(step - istep); p.Step = step;
                p.Gl = gain * (pan > 0f ? 1f - pan : 1f); p.Gr = gain * (pan < 0f ? 1f + pan : 1f);
            }
            int carried = HostMixer.Begin(frames);
            try
            {
                for (int i = 0; i < planned; i++)
                {
                    var p = plans[i];
                    var v = p.V;
                    double basePos = v.Frame;
                    p.Ended = HostMixer.Voice(v.Key, p.Kind, p.Id, p.Start, frames, p.Loop, p.Total, p.Channels, p.F0, p.T, p.IStep, p.FStep, p.Gl, p.Gr,
                                              out int stepped, out bool wrapped, out long wrapF0, out float wrapT);
                    if (wrapped) basePos = wrapF0 + (double)wrapT;
                    v.Frame = basePos + p.Step * stepped;
                }
            }
            finally
            {
                HostMixer.End(frames);
            }
            for (int i = 0; i < planned; i++)
            {
                var p = plans[i];
                if (p.Ended)
                {
                    Close(p.V);
                    voices.Remove(p.V);
                    if (p.V.Src != null && p.V.Src.Main == p.V) p.V.Src.Main = null;
                }
                p.V = null;
            }
            mixedFrames += frames + Math.Max(0, carried);
        }

        /// <summary>
        /// A 3D source as Unity hears it from the listener: the rolloff
        /// (logarithmic min/d up to MaxDistance, linear from MinDistance to
        /// MaxDistance, or the custom curve over distance / MaxDistance) and a
        /// pan from the listener's right, both weighted by the spatial blend;
        /// the spread narrows the pan (a stereo approximation of Unity's 3D
        /// panner; no doppler).
        /// </summary>
        static void Spatial(NAudioSource s, Vector3 hearAt, Vector3 right, ref float gain, ref float pan)
        {
            var rel = s.GameObject.Transform.Position - hearAt;
            float d = rel.magnitude, max = Math.Max(s.MaxDistance, 1e-4f), x = d / max;
            float blend = s.BlendCurve != null && s.BlendCurve.Count > 1 ? Mathf.Clamp01(Eval(s.BlendCurve, x)) : Mathf.Clamp01(s.SpatialBlend);
            if (blend <= 0f) return;
            float min = Math.Max(s.MinDistance, 1e-4f), att;
            if (s.Rolloff == 1) att = d <= min ? 1f : d >= max ? 0f : (max - d) / Math.Max(max - min, 1e-4f);
            else if (s.Rolloff == 2) att = s.RolloffCurve != null ? Mathf.Clamp01(Eval(s.RolloffCurve, x)) : 1f;
            else att = d <= min ? 1f : min / Math.Min(d, max);
            float spread = s.SpreadCurve != null && s.SpreadCurve.Count > 1 ? Eval(s.SpreadCurve, x) * 360f : s.Spread;
            float side = d > 1e-5f ? Mathf.Clamp(Vector3.Dot(rel, right) / d, -1f, 1f) : 0f;
            float pan3 = side * (1f - Mathf.Clamp(spread, 0f, 360f) / 360f);
            gain *= 1f + (att - 1f) * blend;
            pan += (pan3 - pan) * blend;
            if (trace3d && Clock.FrameCount % 120 == 0)
                Log.Line($"TRACE audio3d frame {Clock.FrameCount} {s.GameObject.Name}: listener {hearAt}, distance {d:0.00} of {max:0.0}, attenuation {att:0.000}, blend {blend:0.00}, pan {pan:0.00}");
        }

        static readonly bool trace3d = Environment.GetEnvironmentVariable("CH360_TRACE_AUDIO3D") == "1";

        static float Eval(List<Keyframe> keys, float x) => Icalls.EvaluateCurve(keys, WrapMode.ClampForever, WrapMode.ClampForever, x);

        /// <summary>Mix one voice into the block; returns true when a non-looping voice has finished.</summary>
        static bool Advance(Voice v, float[] dst, int start, int frames, double step, float gain, float pan, bool loop, bool render)
        {
            var c = v.Clip;
            long total = c.Frames;
            if (total <= 0) return true;
            if (!render || gain <= 0f)
            {
                v.Frame += step * (frames - start);
                if (v.Frame >= total) { if (!loop) return true; v.Frame %= total; }
                return false;   // a streaming voice's buffer repositions itself when it next renders
            }
            int ch = c.Channels;
            float gl = gain * (pan > 0f ? 1f - pan : 1f), gr = gain * (pan < 0f ? 1f + pan : 1f);
            float[] pcm = c.Stream ? null : Pcm(c);
            if (c.Stream && v.Reader == null && !c.Missing) { v.Reader = Open(c); if (v.Reader != null) { ch = c.Channels; v.Buf = new float[8192 * ch]; v.BufStart = (long)v.Frame; v.BufFrames = 0; v.Reader.SamplePosition = v.BufStart; } }
            // Decoding can correct the clip's frame count and channels.
            ch = c.Channels;
            total = pcm != null ? Math.Min(c.Frames, pcm.Length / ch) : c.Frames;
            if (total <= 0) return true;
            // The position is a whole frame and a float fraction inside the loop
            // (a double-to-integer conversion per sample stalls the console's
            // CPU twice: it goes through memory); the exact double is carried
            // from block to block as before.
            double basePos = v.Frame;
            int stepped = 0;
            long f0 = (long)basePos, istep = (long)step;
            float t = (float)(basePos - f0), fstep = (float)(step - istep);
            for (int i = start; i < frames; i++)
            {
                if (f0 >= total)
                {
                    if (!loop) { v.Frame = basePos + step * stepped; return true; }
                    f0 -= total;
                    basePos = f0 + (double)t;
                    stepped = 0;
                    if (v.Reader != null) { v.BufStart = f0; v.BufFrames = 0; v.Reader.SamplePosition = v.BufStart; }
                }
                float l, r;
                if (pcm != null)
                {
                    long f1 = f0 + 1 < total ? f0 + 1 : (loop ? 0 : f0);
                    if (ch == 1) { float a = pcm[f0], b = pcm[f1]; l = r = a + (b - a) * t; }
                    else { long i0 = f0 * ch, i1 = f1 * ch; l = pcm[i0] + (pcm[i1] - pcm[i0]) * t; r = pcm[i0 + 1] + (pcm[i1 + 1] - pcm[i0 + 1]) * t; }
                }
                else if (v.Reader != null && Fill(v, f0))
                {
                    long b0 = (f0 - v.BufStart) * ch;
                    long b1 = Math.Min(f0 + 1 - v.BufStart, v.BufFrames - 1) * ch;
                    if (ch == 1) { float a = v.Buf[b0], b = v.Buf[b1]; l = r = a + (b - a) * t; }
                    else { l = v.Buf[b0] + (v.Buf[b1] - v.Buf[b0]) * t; r = v.Buf[b0 + 1] + (v.Buf[b1 + 1] - v.Buf[b0 + 1]) * t; }
                }
                else l = r = 0f;
                dst[i * 2] += l * gl;
                dst[i * 2 + 1] += r * gr;
                f0 += istep;
                t += fstep;
                if (t >= 1f) { t -= 1f; f0++; }
                stepped++;
            }
            v.Frame = basePos + step * stepped;
            return false;
        }

        /// <summary>Make a streaming voice's buffer hold frame <paramref name="f0"/> and, when the clip has it, the next one.</summary>
        static bool Fill(Voice v, long f0)
        {
            int ch = v.Clip.Channels;
            long end = v.BufStart + v.BufFrames;
            if (f0 >= v.BufStart && f0 + 1 < end) return true;
            if (f0 < v.BufStart || f0 > end) { v.Reader.SamplePosition = f0; v.BufStart = f0; v.BufFrames = 0; }
            long drop = f0 - v.BufStart;   // frames already played
            if (drop > 0) { Array.Copy(v.Buf, drop * ch, v.Buf, 0, (v.BufFrames - drop) * ch); v.BufStart = f0; v.BufFrames -= drop; }
            int room = v.Buf.Length / ch - (int)v.BufFrames;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int got = room > 0 ? v.Reader.ReadSamples(v.Buf, (int)v.BufFrames * ch, room * ch) : 0;
            DecodeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            v.BufFrames += got / ch;
            return f0 < v.BufStart + v.BufFrames;
        }

        // ------------------------------------------------------------ capture

        public static void StartCapture(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            capture = new BinaryWriter(File.Create(path));
            capture.Write(new byte[44]);
            capturedFrames = 0;
        }

        public static void EndCapture()
        {
            if (capture == null) return;
            long bytes = capturedFrames * 4;
            capture.Seek(0, SeekOrigin.Begin);
            capture.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); capture.Write((int)(36 + bytes));
            capture.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); capture.Write(16); capture.Write((short)1); capture.Write((short)2);
            capture.Write(Rate); capture.Write(Rate * 4); capture.Write((short)4); capture.Write((short)16);
            capture.Write(System.Text.Encoding.ASCII.GetBytes("data")); capture.Write((int)bytes);
            capture.Dispose(); capture = null;
        }

        public static int VoiceCount => voices.Count;
    }

    public static partial class Icalls
    {
        static NAudioSource AS(AudioSource s) => Objects.Of(s) as NAudioSource;

        public static float UnityEngine_AudioClip__get_length(AudioClip self) { var c = Audio.ClipOf(self); return c == null ? 0f : c.Frames / (float)c.Frequency; }
        public static int UnityEngine_AudioClip__get_samples(AudioClip self) => (int)(Audio.ClipOf(self)?.Frames ?? 0);
        public static int UnityEngine_AudioClip__get_channels(AudioClip self) => Audio.ClipOf(self)?.Channels ?? 0;
        public static int UnityEngine_AudioClip__get_frequency(AudioClip self) => Audio.ClipOf(self)?.Frequency ?? 0;
        public static bool UnityEngine_AudioClip__get_isReadyToPlay(AudioClip self) => true;
        public static AudioClipLoadType UnityEngine_AudioClip__get_loadType(AudioClip self) => (AudioClipLoadType)Tree.Int((Objects.Of(self) as NAsset)?.Source?.Tree, "m_LoadType");
        public static bool UnityEngine_AudioClip__get_preloadAudioData(AudioClip self) => Tree.Bool((Objects.Of(self) as NAsset)?.Source?.Tree, "m_PreloadAudioData");
        public static bool UnityEngine_AudioClip__get_ambisonic(AudioClip self) => false;
        public static AudioDataLoadState UnityEngine_AudioClip__get_loadState(AudioClip self) => AudioDataLoadState.Loaded;
        public static bool UnityEngine_AudioClip__get_loadInBackground(AudioClip self) => Tree.Bool((Objects.Of(self) as NAsset)?.Source?.Tree, "m_LoadInBackground");
        public static bool UnityEngine_AudioClip__LoadAudioData(AudioClip self) => true;
        public static bool UnityEngine_AudioClip__UnloadAudioData(AudioClip self) => true;

        public static float UnityEngine_AudioListener__get_volume() => Audio.ListenerVolume;
        public static void UnityEngine_AudioListener__set_volume(float value) => Audio.ListenerVolume = value;
        public static bool UnityEngine_AudioListener__get_pause() => Audio.ListenerPause;
        public static void UnityEngine_AudioListener__set_pause(bool value) => Audio.ListenerPause = value;
        public static double UnityEngine_AudioSettings__get_dspTime() => Audio.DspTime;
        public static int UnityEngine_AudioSettings__get_outputSampleRate() => Audio.Rate;
        public static void UnityEngine_AudioSettings__set_outputSampleRate(int value) { }

        public static float UnityEngine_AudioSource__get_volume(AudioSource self) => AS(self)?.Volume ?? 1f;
        public static void UnityEngine_AudioSource__set_volume(AudioSource self, float value) { var s = AS(self); if (s != null) s.Volume = Mathf.Clamp01(value); }
        public static float UnityEngine_AudioSource__get_pitch(AudioSource self) => AS(self)?.Pitch ?? 1f;
        public static void UnityEngine_AudioSource__set_pitch(AudioSource self, float value) { var s = AS(self); if (s != null) s.Pitch = Mathf.Clamp(value, -3f, 3f); }
        public static float UnityEngine_AudioSource__get_time(AudioSource self) { var s = AS(self); var c = Audio.ClipOf(s?.Clip); return c == null ? 0f : (float)(Audio.Time(s) / c.Frequency); }
        public static void UnityEngine_AudioSource__set_time(AudioSource self, float value) { var s = AS(self); var c = Audio.ClipOf(s?.Clip); if (c != null) Audio.Seek(s, value * c.Frequency); }
        public static int UnityEngine_AudioSource__get_timeSamples(AudioSource self) => (int)Audio.Time(AS(self));
        public static void UnityEngine_AudioSource__set_timeSamples(AudioSource self, int value) => Audio.Seek(AS(self), value);
        public static AudioClip UnityEngine_AudioSource__get_clip(AudioSource self) => AS(self)?.Clip;
        public static void UnityEngine_AudioSource__set_clip(AudioSource self, AudioClip value) { var s = AS(self); if (s != null && s.Clip != value) { Audio.Stop(s); s.Clip = value; s.PendingFrame = 0; } }
        public static AudioMixerGroup UnityEngine_AudioSource__get_outputAudioMixerGroup(AudioSource self) => AS(self)?.Group;
        public static void UnityEngine_AudioSource__set_outputAudioMixerGroup(AudioSource self, AudioMixerGroup value) { var s = AS(self); if (s != null) s.Group = value; }
        // Play(ulong delay): the delay counts samples at 44.1 kHz (Unity's legacy overload).
        public static void UnityEngine_AudioSource__Play(AudioSource self, ulong delay) => Audio.Play(AS(self), delay / 44100.0);
        public static void UnityEngine_AudioSource__PlayDelayed(AudioSource self, float delay) => Audio.Play(AS(self), Math.Max(0f, delay));
        public static void UnityEngine_AudioSource__Stop(AudioSource self) => Audio.Stop(AS(self));
        public static void UnityEngine_AudioSource__INTERNAL_CALL_Pause(AudioSource self) => Audio.Pause(AS(self), true);
        public static void UnityEngine_AudioSource__INTERNAL_CALL_UnPause(AudioSource self) => Audio.Pause(AS(self), false);
        public static bool UnityEngine_AudioSource__get_isPlaying(AudioSource self) => Audio.IsPlaying(AS(self));
        public static void UnityEngine_AudioSource__PlayOneShotHelper(AudioSource self, AudioClip clip, float volumeScale) => Audio.PlayOneShot(AS(self), clip, volumeScale);
        public static bool UnityEngine_AudioSource__get_loop(AudioSource self) => AS(self)?.Loop ?? false;
        public static void UnityEngine_AudioSource__set_loop(AudioSource self, bool value) { var s = AS(self); if (s != null) s.Loop = value; }
        public static bool UnityEngine_AudioSource__get_ignoreListenerVolume(AudioSource self) => AS(self)?.IgnoreListenerVolume ?? false;
        public static void UnityEngine_AudioSource__set_ignoreListenerVolume(AudioSource self, bool value) { var s = AS(self); if (s != null) s.IgnoreListenerVolume = value; }
        public static bool UnityEngine_AudioSource__get_playOnAwake(AudioSource self) => AS(self)?.PlayOnAwake ?? false;
        public static void UnityEngine_AudioSource__set_playOnAwake(AudioSource self, bool value) { var s = AS(self); if (s != null) s.PlayOnAwake = value; }
        public static bool UnityEngine_AudioSource__get_ignoreListenerPause(AudioSource self) => AS(self)?.IgnoreListenerPause ?? false;
        public static void UnityEngine_AudioSource__set_ignoreListenerPause(AudioSource self, bool value) { var s = AS(self); if (s != null) s.IgnoreListenerPause = value; }
        public static float UnityEngine_AudioSource__get_panStereo(AudioSource self) => AS(self)?.Pan ?? 0f;
        public static void UnityEngine_AudioSource__set_panStereo(AudioSource self, float value) { var s = AS(self); if (s != null) s.Pan = Mathf.Clamp(value, -1f, 1f); }
        public static float UnityEngine_AudioSource__get_spatialBlend(AudioSource self) => AS(self)?.SpatialBlend ?? 0f;
        public static void UnityEngine_AudioSource__set_spatialBlend(AudioSource self, float value) { var s = AS(self); if (s != null) { s.SpatialBlend = Mathf.Clamp01(value); s.BlendCurve = null; } }
        public static float UnityEngine_AudioSource__get_minDistance(AudioSource self) => AS(self)?.MinDistance ?? 1f;
        public static void UnityEngine_AudioSource__set_minDistance(AudioSource self, float value) { var s = AS(self); if (s != null) s.MinDistance = Math.Max(0f, value); }
        public static float UnityEngine_AudioSource__get_maxDistance(AudioSource self) => AS(self)?.MaxDistance ?? 500f;
        public static void UnityEngine_AudioSource__set_maxDistance(AudioSource self, float value) { var s = AS(self); if (s != null) s.MaxDistance = Math.Max(0f, value); }
        public static AudioRolloffMode UnityEngine_AudioSource__get_rolloffMode(AudioSource self) => (AudioRolloffMode)(AS(self)?.Rolloff ?? 0);
        public static void UnityEngine_AudioSource__set_rolloffMode(AudioSource self, AudioRolloffMode value) { var s = AS(self); if (s != null) s.Rolloff = (int)value; }
        public static float UnityEngine_AudioSource__get_spread(AudioSource self) => AS(self)?.Spread ?? 0f;
        public static void UnityEngine_AudioSource__set_spread(AudioSource self, float value) { var s = AS(self); if (s != null) { s.Spread = Mathf.Clamp(value, 0f, 360f); s.SpreadCurve = null; } }
        public static bool UnityEngine_AudioSource__get_mute(AudioSource self) => AS(self)?.Mute ?? false;
        public static void UnityEngine_AudioSource__set_mute(AudioSource self, bool value) { var s = AS(self); if (s != null) s.Mute = value; }
    }
}
