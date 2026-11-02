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
        }

        public override void CopyFrom(NComponent src, Dictionary<NObject, NObject> map)
        {
            var s = (NAudioSource)src;
            Clip = s.Clip; Group = s.Group; Volume = s.Volume; Pitch = s.Pitch; Pan = s.Pan; SpatialBlend = s.SpatialBlend;
            Loop = s.Loop; Mute = s.Mute; PlayOnAwake = s.PlayOnAwake; IgnoreListenerPause = s.IgnoreListenerPause; IgnoreListenerVolume = s.IgnoreListenerVolume;
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

        public sealed class Voice
        {
            public NAudioSource Src;
            public Clip Clip;
            public double Frame;          // position in the clip's frames
            public float VolumeScale = 1f;
            public bool OneShot, Paused;
            public double Delay;          // seconds until it starts
            // Streaming decode state.
            public VorbisReader Reader;
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
            public bool Missing;
            public long LastUse;
        }

        static readonly List<Voice> voices = new List<Voice>();
        static readonly Dictionary<AudioClip, Clip> clips = new Dictionary<AudioClip, Clip>();
        static long cachedSamples, useCounter, mixedFrames;
        /// <summary>Decoded short clips kept, in floats (the console host lowers it).</summary>
        public static long CacheFloats = 128L * 1024 * 1024;
        static double frameCarry;
        static readonly HashSet<string> reported = new HashSet<string>();
        public static bool ListenerPause;
        public static float ListenerVolume = 1f;

        /// <summary>True when an output consumes samples (device or capture); otherwise only positions advance.</summary>
        public static bool Render => Output != null || capture != null;
        /// <summary>Called with each mixed block (interleaved stereo floats) when set (the live host's device queue).</summary>
        public static Action<float[], int> Output;
        static BinaryWriter capture;
        static long capturedFrames;

        public static double DspTime => mixedFrames / (double)Rate;

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
                ci.Stream = Tree.Int(t, "m_LoadType") == 2 || Tree.Float(t, "m_Length") > 12f;
                string k = src.File.Path.Replace('/', '_');
                if (k.EndsWith(".chob")) k = k.Substring(0, k.Length - 5);
                ci.Key = k + "_" + src.PathId;
            }
            clips[c] = ci;
            return ci;
        }

        static string PathOf(Clip c) => c.Key == null || AssetDb.Root == null ? null : Path.Combine(AssetDb.Root, "audio", c.Key + ".ogg");

        static VorbisReader Open(Clip c)
        {
            string fn = PathOf(c);
            if (fn == null || !File.Exists(fn))
            {
                c.Missing = true;
                if (reported.Add(c.Name ?? "?")) Log.Line($"AUDIO clip not converted: {c.Name} ({c.Key}); run tools/convert_audio.py");
                return null;
            }
            var r = new VorbisReader(fn);
            if (r.Channels != c.Channels || r.SampleRate != c.Frequency) { c.Channels = r.Channels; c.Frequency = r.SampleRate; }
            if (r.TotalSamples > 0) c.Frames = r.TotalSamples;
            return r;
        }

        static float[] Pcm(Clip c)
        {
            c.LastUse = ++useCounter;
            if (c.Pcm != null || c.Missing) return c.Pcm;
            using (var r = Open(c))
            {
                if (r == null) return null;
                var data = new float[Math.Max(0, c.Frames) * c.Channels];
                int got = 0, n;
                while (got < data.Length && (n = r.ReadSamples(data, got, Math.Min(16384, data.Length - got))) > 0) got += n;
                c.Pcm = data;
                cachedSamples += data.Length;
            }
            // Keep the decoded cache bounded (CacheFloats; about 512 MB of floats by default).
            if (cachedSamples > CacheFloats)
            {
                var all = new List<Clip>(clips.Values);
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
            bool render = Render;
            if (render)
            {
                if (mix.Length < frames * 2) mix = new float[frames * 2];
                Array.Clear(mix, 0, frames * 2);
            }
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
                double step = v.Clip.Frequency / (double)Rate * Math.Max(0f, s.Pitch) * gpitch;
                bool loop = !v.OneShot && s.Loop;
                bool ended = Advance(v, mix, start, frames, step, gain, s.Pan, loop, render);
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
            for (int i = start; i < frames; i++)
            {
                if (v.Frame >= total)
                {
                    if (!loop) return true;
                    v.Frame -= total;
                    if (v.Reader != null) { v.BufStart = (long)v.Frame; v.BufFrames = 0; v.Reader.SamplePosition = v.BufStart; }
                }
                long f0 = (long)v.Frame;
                float t = (float)(v.Frame - f0);
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
                v.Frame += step;
            }
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
            int got = room > 0 ? v.Reader.ReadSamples(v.Buf, (int)v.BufFrames * ch, room * ch) : 0;
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
        public static void UnityEngine_AudioSource__set_spatialBlend(AudioSource self, float value) { var s = AS(self); if (s != null) s.SpatialBlend = value; }
        public static bool UnityEngine_AudioSource__get_mute(AudioSource self) => AS(self)?.Mute ?? false;
        public static void UnityEngine_AudioSource__set_mute(AudioSource self, bool value) { var s = AS(self); if (s != null) s.Mute = value; }
    }
}
