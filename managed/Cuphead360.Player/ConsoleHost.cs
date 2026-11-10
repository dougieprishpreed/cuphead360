using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Cuphead360.Engine;
using UnityEngine;

namespace Cuphead360.Player
{
    /// <summary>
    /// The console build's side of the host boundary (the XEX's console/main.cpp
    /// implements the other). The player runs as a WASI module translated to
    /// C; each frame it asks the host for the pads, runs the player loop, and
    /// hands over the frame's draw list, new textures and the mixed audio
    /// through the "ch360" imports. Wasm memory is little-endian; the host
    /// reads it with w2c2's load helpers.
    ///
    /// Draw list (little-endian):
    ///   u32 'CHF1', u32 views
    ///   view: f32 x y w h, u32 clear, f32 r g b a, u32 items
    ///   item: i32 texture (-1 none), u32 mode, f32 blend r g b a, u32 vertices
    ///         vertex: f32 x y u v, u8 r g b a      (a triangle list)
    /// Modes: 0 texel * colour, 1 screen, 2 multiply, 3 overlay (the blend
    /// colour is the renderer colour), 5 SDF text (texel alpha thresholded).
    /// </summary>
    internal static unsafe class ConsoleHost
    {
        [DllImport("ch360", EntryPoint = "frame_begin"), WasmImportLinkage]
        static extern int FrameBegin();
        [DllImport("ch360", EntryPoint = "pad"), WasmImportLinkage]
        static extern int Pad(int index, byte* state);
        [DllImport("ch360", EntryPoint = "submit"), WasmImportLinkage]
        static extern void Submit(byte* data, int length);
        [DllImport("ch360", EntryPoint = "draw_view"), WasmImportLinkage]
        static extern void DrawView(float* view);
        [DllImport("ch360", EntryPoint = "draw_item"), WasmImportLinkage]
        static extern void DrawItem(int* args);
        [DllImport("ch360", EntryPoint = "draw_end"), WasmImportLinkage]
        static extern void DrawEnd();
        [DllImport("ch360", EntryPoint = "texture_file"), WasmImportLinkage]
        static extern void TextureFile(int id, byte* key, int length);
        [DllImport("ch360", EntryPoint = "texture_pixels"), WasmImportLinkage]
        static extern void TexturePixels(int id, int width, int height, byte* bgra);
        [DllImport("ch360", EntryPoint = "texture_prefetch"), WasmImportLinkage]
        static extern void TexturePrefetch(int id);
        [DllImport("ch360", EntryPoint = "texture_ahead"), WasmImportLinkage]
        static extern void TextureAhead(int id, float u0, float v0, float u1, float v1);
        [DllImport("ch360", EntryPoint = "textures_pending"), WasmImportLinkage]
        static extern int TexturesPending();
        [DllImport("ch360", EntryPoint = "textures_scene"), WasmImportLinkage]
        static extern void TexturesScene(int begin);
        [DllImport("ch360", EntryPoint = "textures_waited"), WasmImportLinkage]
        static extern int TexturesWaited();
        [DllImport("ch360", EntryPoint = "loading_screen"), WasmImportLinkage]
        static extern void LoadingScreenFlag(uint state);
        [DllImport("ch360", EntryPoint = "audio_queued"), WasmImportLinkage]
        static extern int AudioQueued();
        [DllImport("ch360", EntryPoint = "ogg_preload"), WasmImportLinkage]
        static extern void OggPreload(byte* path, int length, int batch);
        [DllImport("ch360", EntryPoint = "audio"), WasmImportLinkage]
        static extern void AudioOut(float* samples, int frames);
        [DllImport("ch360", EntryPoint = "ogg_open"), WasmImportLinkage]
        static extern int OggOpen(byte* path, int length, int* info);
        [DllImport("ch360", EntryPoint = "ogg_read"), WasmImportLinkage]
        static extern int OggRead(int handle, float* samples, int frames);
        [DllImport("ch360", EntryPoint = "ogg_seek"), WasmImportLinkage]
        static extern int OggSeek(int handle, int frame);
        [DllImport("ch360", EntryPoint = "ogg_close"), WasmImportLinkage]
        static extern void OggClose(int handle);
        [DllImport("ch360", EntryPoint = "pcm_decode"), WasmImportLinkage]
        static extern int PcmDecode(int handle, int* frames);
        [DllImport("ch360", EntryPoint = "pcm_free"), WasmImportLinkage]
        static extern void PcmFree(int id);
        [DllImport("ch360", EntryPoint = "mix_begin"), WasmImportLinkage]
        static extern int MixBegin(int frames);
        [DllImport("ch360", EntryPoint = "mix_voice"), WasmImportLinkage]
        static extern int MixVoice(int key, int kind, int id, int start, int frames, int loop, int total, int channels, int f0, float t, int istep, float fstep,
                                   float gl, float gr, int* result);
        [DllImport("ch360", EntryPoint = "mix_end"), WasmImportLinkage]
        static extern void MixEnd(int frames);

        /// <summary>The host's native mixer (console/ch360_ogg.c; Audio.IHostMixer).</summary>
        sealed class HostMixer : Audio.IHostMixer
        {
            public int Decode(Audio.IClipReader r, out long frames)
            {
                int n = 0;
                int id = r is HostClip hc && hc.Handle >= 0 ? PcmDecode(hc.Handle, &n) : -1;
                frames = (uint)n;
                return id;
            }

            public void Free(int id) => PcmFree(id);
            public int Handle(Audio.IClipReader r) => r is HostClip hc ? hc.Handle : -1;
            public int Begin(int frames) => MixBegin(frames);

            public bool Voice(int key, int kind, int id, int start, int frames, bool loop, long total, int channels, long f0, float t, long istep, float fstep,
                              float gl, float gr, out int stepped, out bool wrapped, out long wrapF0, out float wrapT)
            {
                int* r = stackalloc int[6];
                bool ended = MixVoice(key, kind, id, start, frames, loop ? 1 : 0, (int)Math.Min(total, int.MaxValue), channels, (int)f0, t, (int)istep, fstep,
                                      gl, gr, r) != 0;
                stepped = r[2];
                wrapped = r[3] != 0;
                wrapF0 = (uint)r[4];
                wrapT = *(float*)&r[5];
                return ended;
            }

            public void End(int frames) => MixEnd(frames);
        }

        /// <summary>
        /// A clip decoded by the host (stb_vorbis in native code, console/ch360_ogg.c):
        /// NVorbis as translated wasm took most of a world map frame on the console.
        /// </summary>
        sealed class HostClip : Audio.IClipReader
        {
            int h;
            public int Channels { get; }
            public int SampleRate { get; }
            public long TotalSamples { get; }
            HostClip(int handle, int channels, int rate, long frames) { h = handle; Channels = channels; SampleRate = rate; TotalSamples = frames; }
            public int Handle => h;

            public static HostClip Open(string path)
            {
                var b = Encoding.UTF8.GetBytes(path);
                int* info = stackalloc int[3];
                int h;
                fixed (byte* p = b) h = OggOpen(p, b.Length, info);
                if (h >= 0 && info[0] <= 0) { OggClose(h); h = -1; }
                return h < 0 ? null : new HostClip(h, info[0], info[1], (uint)info[2]);
            }

            public long SamplePosition { set { if (h >= 0) OggSeek(h, (int)Math.Clamp(value, 0, int.MaxValue)); } }

            public int ReadSamples(float[] buffer, int offset, int count)
            {
                int frames = count / Channels;
                if (h < 0 || frames <= 0) return 0;
                fixed (float* p = &buffer[offset]) return OggRead(h, p, frames) * Channels;
            }

            public void Dispose() { if (h >= 0) OggClose(h); h = -1; }
        }

        static readonly InputFrame input = new InputFrame();
        static byte[] buf = new byte[1 << 20];
        static int len;
        // A texture's id is kept on its engine object (NObject.HostId); converted textures' ids by their key, so a
        // texture unloaded and loaded again keeps its id (and the host its image). A table of the textures
        // themselves held every texture ever drawn, and through them their converted files: never unloaded.
        static readonly Dictionary<ulong, int> idsByKey = new Dictionary<ulong, int>();
        static int nextId;
        static readonly Dictionary<int, int> versions = new Dictionary<int, int>();
        /// <summary>
        /// The last frame's real duration in seconds (Unity's deltaTime), 1/60 to
        /// 1/30: a slower frame moves the game less than real time rather than run
        /// more fixed steps and fall further behind; the mixer still gets the real
        /// time (PlayerLoop.AudioDelta). Scripted runs keep 1/60.
        /// </summary>
        public static float Delta = 1f / 60f;
        /// <summary>A scripted run's frame step: 1/60, or CH360_STEP (seconds; a PC check through CH360_GUEST_ENV).</summary>
        static readonly float scriptedStep = float.TryParse(Environment.GetEnvironmentVariable("CH360_STEP"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var st) && st > 0 ? st : 1f / 60f;
        // The eighth run's EX shots showed no pose on the console: the player's first three EX
        // animations (a Play of "Ex.<direction>_<Ground|Air>") open an animator trace of the player
        // and log the player's sprite as drawn, 30 frames each. Off since the tenth run showed the
        // EX right (its lines were written in the fights' frames): call it again to trace.
        static void TraceEx()
        {
            NAnimator.WindowName = "PlayerOne";
            NAnimator.WindowsLeft = 3;
            NAnimator.WindowOnPlay = new HashSet<int>();
            foreach (var d in new[] { "Forward", "Up", "Diagonal_Up", "Down", "Diagonal_Down" })
                foreach (var g in new[] { "Ground", "Air" })
                    NAnimator.WindowOnPlay.Add(Animator.StringToHash("Ex." + d + "_" + g));
        }
        [DllImport("ch360", EntryPoint = "host_stats"), WasmImportLinkage]
        static extern void HostStats(uint* values, int count);

        /// <summary>
        /// What the console run logs to find where frame time goes (the run after the forced sweep is for
        /// performance): every 300 frames the game's phases, the time between frames as percentiles and
        /// 60/30/20 fps shares, script time, GC and allocation (FRAMES, TOP); a frame over 50 ms with its
        /// breakdown (HITCH, ten a window); and at each scene change the scene's summary, with the host's
        /// side from host_stats: the GPU's time by kind of pass, fill, texture loads (SCENE SUMMARY, GPU, TOP).
        /// The host adds HOST, GPU and HOST SLOW lines of its own on the same frame numbers.
        /// </summary>
        sealed class PerfLog
        {
            const int Bins = 251;   // 1 ms bins of the time between frames, the last 250 ms and over
            readonly double hz;
            readonly int phases = PlayerLoop.PhaseMs.Length;
            sealed class Tally
            {
                public readonly int[] Hist = new int[Bins];
                public readonly double[] Phase = new double[PlayerLoop.PhaseMs.Length + 2];   // phases, list, submit
                public readonly Dictionary<Type, long> Scripts = new Dictionary<Type, long>(), Bytes = new Dictionary<Type, long>();
                public double Game, Period, Decode;
                public int Frames, Periods, Hitches, Unsaid;
                public readonly StringBuilder UnsaidAt = new StringBuilder();   // the window's hitches past ten: frame and ms
                public long Gc0, Gc1, Gc2, PauseMs, Alloc, DecodeAt;
                public void Start()
                {
                    Array.Clear(Hist); Array.Clear(Phase); Scripts.Clear(); Bytes.Clear();
                    Game = Period = Decode = 0; Frames = Periods = Hitches = Unsaid = 0; UnsaidAt.Clear();
                    Gc0 = GC.CollectionCount(0); Gc1 = GC.CollectionCount(1); Gc2 = GC.CollectionCount(2);
                    PauseMs = (long)GC.GetTotalPauseDuration().TotalMilliseconds; Alloc = Allocated(); DecodeAt = Audio.DecodeTicks;
                }
            }
            readonly Tally window = new Tally(), scene = new Tally();
            string sceneName, windowScenes = "";
            int windowStart = 1;
            // The last frame's breakdown, for a HITCH line when the time to the next frame says it was long.
            readonly double[] lastPhase = new double[PlayerLoop.PhaseMs.Length + 2];
            double lastGame;
            long lastGc0, lastGc1, lastGc2, lastPauseMs, lastAlloc, lastDecode, gcAt0, gcAt1, gcAt2, pauseAt, allocAt, decodeAt;
            int lastFrame, loadsAt;
            bool lastLoaded, activation;
            readonly uint[] host = new uint[22];
            readonly double[] hostSum = new double[22];
            static bool noAlloc;

            static long Allocated()
            {
                if (noAlloc) return 0;
                try { return GC.GetTotalAllocatedBytes(false); }
                catch (Exception) { noAlloc = true; return 0; }
            }

            public PerfLog(long hz)
            {
                this.hz = hz;
                window.Start(); scene.Start();
                gcAt0 = window.Gc0; gcAt1 = window.Gc1; gcAt2 = window.Gc2; pauseAt = window.PauseMs; allocAt = window.Alloc; decodeAt = Audio.DecodeTicks;
                loadsAt = Cuphead360.Engine.SceneLoader.Loads;
                fixed (uint* h = host) HostStats(h, host.Length);   // the host's tallies start here
            }

            /// <summary>The time from the last frame's start to this one's: its share of the histograms, and a HITCH line when over 50 ms.</summary>
            public void Between(double ms)
            {
                if (lastFrame == 0) return;
                int bin = ms >= Bins - 1 ? Bins - 1 : (int)ms;
                window.Hist[bin]++; window.Period += ms; window.Periods++;
                // The frame that activated a scene (the load's last step) stays out of the new scene's spread.
                bool act = activation;
                activation = false;
                if (!act) { scene.Hist[bin]++; scene.Period += ms; scene.Periods++; }
                if (ms <= 50) return;
                window.Hitches++;
                if (!act) scene.Hitches++;
                if (window.Hitches > 10)
                {
                    // where they were (the boot's window hid the logo's among the startup's: the twenty-sixth run)
                    if (window.Unsaid++ < 24) window.UnsaidAt.Append(window.Unsaid > 1 ? ", " : ": frames ").Append(lastFrame).Append(' ').Append(ms.ToString("0"));
                    return;
                }
                var sb = new StringBuilder();
                sb.Append("HITCH frame ").Append(lastFrame).Append(": ").Append(ms.ToString("0")).Append(" ms to the next frame; game ")
                  .Append(lastGame.ToString("0.0")).Append(" (");
                bool any = false;
                for (int i = 0; i < phases; i++)
                    if (lastPhase[i] >= 0.5) { sb.Append(any ? " " : "").Append(PlayerLoop.PhaseNames[i]).Append('=').Append(lastPhase[i].ToString("0.0")); any = true; }
                sb.Append(") list ").Append(lastPhase[phases].ToString("0.0")).Append(" submit ").Append(lastPhase[phases + 1].ToString("0.0"))
                  .Append(", host and vblank ").Append((ms - lastGame).ToString("0.0")).Append(" ms");
                if (lastGc0 + lastGc1 + lastGc2 > 0) sb.Append("; GC ").Append(lastGc0).Append('/').Append(lastGc1).Append('/').Append(lastGc2).Append(" paused ").Append(lastPauseMs).Append(" ms");
                sb.Append("; allocated ").Append(lastAlloc >> 10).Append(" KB, audio decode ").Append((lastDecode * 1000.0 / hz).ToString("0.0")).Append(" ms");
                sb.Append("; ").Append(sceneName ?? "?").Append(lastLoaded ? " (a scene activated)" : "");
                Console.WriteLine(sb.ToString());
            }

            /// <summary>A frame done: its game time (the player loop's phases, the draw list and its hand-over).</summary>
            // What each phase allocated over the window (bytes): the scripts' messages within theirs.
            readonly double[] windowBytes = new double[PlayerLoop.PhaseBytes.Length + 2];
            public void Allocs(long[] ph, long list, long submit)
            {
                for (int i = 0; i < ph.Length; i++) windowBytes[i] += ph[i];
                windowBytes[ph.Length] += list;
                windowBytes[ph.Length + 1] += submit;
            }

            public void Frame(int n, double game, double[] ph, double list, double submit)
            {
                long g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                long pause = (long)GC.GetTotalPauseDuration().TotalMilliseconds, alloc = Allocated(), decode = Audio.DecodeTicks;
                int loads = Cuphead360.Engine.SceneLoader.Loads;
                lastFrame = n; lastGame = game;
                for (int i = 0; i < phases; i++) lastPhase[i] = ph[i];
                lastPhase[phases] = list; lastPhase[phases + 1] = submit;
                lastGc0 = g0 - gcAt0; lastGc1 = g1 - gcAt1; lastGc2 = g2 - gcAt2; lastPauseMs = pause - pauseAt; lastAlloc = alloc - allocAt;
                lastDecode = decode - decodeAt; lastLoaded = loads != loadsAt;
                gcAt0 = g0; gcAt1 = g1; gcAt2 = g2; pauseAt = pause; allocAt = alloc; decodeAt = decode; loadsAt = loads;
                // A frame that changed the scene counts to the scene it ended (the loading screen's: the
                // activation, its GC); the new scene's tally starts after it.
                Add(window, game);
                Add(scene, game);
                var name = Cuphead360.Engine.Scenes.Active?.Name;
                if (name != sceneName)
                {
                    if (sceneName != null && scene.Frames > 1) SceneSummary();
                    sceneName = name;
                    scene.Start();
                    activation = true;
                    if (!windowScenes.Contains(name ?? "?")) windowScenes += (windowScenes.Length > 0 ? "," : "") + (name ?? "?");
                }
                if (windowScenes.Length == 0) windowScenes = name ?? "?";
                if (n % 300 == 0) Window(n);
            }

            void Add(Tally t, double game)
            {
                t.Frames++; t.Game += game;
                for (int i = 0; i < lastPhase.Length; i++) t.Phase[i] += lastPhase[i];
            }

            static string Share(int[] hist, int periods, double lo, double hi)
            {
                int c = 0;
                for (int i = 0; i < Bins; i++) if (i + 0.5 > lo && i + 0.5 <= hi) c += hist[i];
                return (100.0 * c / Math.Max(1, periods)).ToString("0");
            }

            static string Percentile(int[] hist, int periods, double p)
            {
                int want = (int)Math.Ceiling(periods * p), c = 0;
                for (int i = 0; i < Bins; i++) { c += hist[i]; if (c >= want && want > 0) return i == Bins - 1 ? ">250" : (i + 1).ToString(); }
                return "-";
            }

            static string Max(int[] hist)
            {
                for (int i = Bins - 1; i >= 0; i--) if (hist[i] > 0) return i == Bins - 1 ? ">250" : (i + 1).ToString();
                return "-";
            }

            // The time between frames: percentiles (1 ms bins, upper edges) and the shares at 60, 30, 20 fps and slower.
            static string Spread(Tally t) =>
                $"between frames p50 {Percentile(t.Hist, t.Periods, 0.5)} p95 {Percentile(t.Hist, t.Periods, 0.95)} p99 {Percentile(t.Hist, t.Periods, 0.99)} max {Max(t.Hist)} ms, "
                + $"60fps {Share(t.Hist, t.Periods, 0, 17.5)}% 30fps {Share(t.Hist, t.Periods, 17.5, 34.5)}% 20fps {Share(t.Hist, t.Periods, 34.5, 51)}% slower {Share(t.Hist, t.Periods, 51, 1e9)}%";

            string Phases(Tally t)
            {
                var sb = new StringBuilder();
                int f = Math.Max(1, t.Frames);
                for (int i = 0; i < phases; i++) sb.Append(i == 0 ? "" : " ").Append(PlayerLoop.PhaseNames[i]).Append('=').Append((t.Phase[i] / f).ToString("0.0"));
                sb.Append(" list=").Append((t.Phase[phases] / f).ToString("0.0")).Append(" submit=").Append((t.Phase[phases + 1] / f).ToString("0.0"));
                return sb.ToString();
            }

            string Gc(Tally t)
            {
                int f = Math.Max(1, t.Frames);
                return $"GC {GC.CollectionCount(0) - t.Gc0}/{GC.CollectionCount(1) - t.Gc1}/{GC.CollectionCount(2) - t.Gc2} paused {(long)GC.GetTotalPauseDuration().TotalMilliseconds - t.PauseMs} ms, "
                    + (noAlloc ? "allocation not counted" : $"allocated {(Allocated() - t.Alloc) / (1024.0 * 1024.0) / f:0.00} MB/frame");
            }

            void Window(int n)
            {
                PlayerLoop.DrainScriptTicks(window.Scripts, scene.Scripts, window.Bytes, scene.Bytes);
                string top = PlayerLoop.TopOf(window.Scripts, Math.Max(1, window.Frames), 12, out double scripts);
                int f = Math.Max(1, window.Frames);
                Console.WriteLine($"FRAMES {windowStart}-{n}: {window.Game / f:0.0} ms/frame; {Phases(window)}; audio decode {(Audio.DecodeTicks - window.DecodeAt) * 1000.0 / hz / f:0.0} ms/frame, "
                    + $"{Audio.VoiceCount} voices; {Spread(window)}; scripts {scripts:0.0} ms/frame; {Gc(window)}; hitches {window.Hitches}"
                    + (window.Unsaid > 0 ? $" ({window.Unsaid} not said{window.UnsaidAt} ms)" : "") + $"; {windowScenes}");
                Console.WriteLine($"TOP {windowStart}-{n} ms/frame:{top}");
                // What each phase allocated, then the scripts' top allocators (each message and coroutine step)
                if (!noAlloc && PlayerLoop.Profile)
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < windowBytes.Length; i++)
                        sb.Append(i == 0 ? "" : " ").Append(i < phases ? PlayerLoop.PhaseNames[i] : i == phases ? "list" : "submit").Append('=').Append((windowBytes[i] / 1024.0 / f).ToString("0.0"));
                    Array.Clear(windowBytes);
                    Console.WriteLine($"ALLOC {windowStart}-{n} KB/frame: {sb}; scripts{PlayerLoop.TopBytesOf(window.Bytes, f, 12)}");
                }
                windowStart = n + 1;
                windowScenes = sceneName ?? "";
                window.Start();
            }

            void SceneSummary()
            {
                PlayerLoop.DrainScriptTicks(window.Scripts, scene.Scripts, window.Bytes, scene.Bytes);
                string top = PlayerLoop.TopOf(scene.Scripts, Math.Max(1, scene.Frames), 16, out double scripts);
                int f = Math.Max(1, scene.Frames);
                double secs = scene.Period / 1000.0;
                Console.WriteLine($"SCENE SUMMARY {sceneName}: {scene.Frames} frames in {secs:0.0} s ({scene.Periods / Math.Max(0.001, secs):0.0} fps); {Spread(scene)}; "
                    + $"game {scene.Game / f:0.0} ms/frame ({Phases(scene)}), scripts {scripts:0.0}; {Gc(scene)}; hitches {scene.Hitches}; "
                    + $"heap {GC.GetTotalMemory(false) >> 20} MB, {PlayerLoop.TrackedCount} behaviours");
                fixed (uint* h = host) HostStats(h, host.Length);
                double hf = Math.Max(1, host[0]), gf = Math.Max(1, host[1]);
                Console.WriteLine($"SCENE GPU {sceneName}: {host[2] / 100.0 / gf:0.0} ms/frame (render textures {host[3] / 100.0 / gf:0.0}, effects {host[4] / 100.0 / gf:0.0}, "
                    + $"screen {host[5] / 100.0 / gf:0.0}; {host[1]} frames timed); {host[15] / hf:0.0} passes, {host[14] / hf:0} draws a frame; fill {host[12] / 100.0 / hf:0.00} screens "
                    + $"+ {host[13] / 100.0 / hf:0.00} in render textures; host part {host[8] / 100.0 / hf:0.0} ms/frame (texture loads {host[9] / 100.0 / hf:0.0}, uploads {host[10] / 100.0 / hf:0.0}; "
                    + $"{host[11]} textures loaded), fence {host[6] / 100.0 / hf:0.0}, present {host[7] / 100.0 / hf:0.0}; {host[19]} slow host parts; audio underruns {host[16]}; "
                    + $"textures {host[17]} MB, {host[18]} MB free; {host[20]} loaded on demand, {host[21]} read again");
                Console.WriteLine($"SCENE TOP {sceneName} ms/frame:{top}");
                if (!noAlloc) Console.WriteLine($"SCENE ALLOC {sceneName} KB/frame:{PlayerLoop.TopBytesOf(scene.Bytes, Math.Max(1, scene.Frames), 16)}");
            }
        }

        public static int Play(Func<int, bool> frame)
        {
            var scripted = Inputs.Source;   // --bot / --input still drive a console run when given
            if (scripted == null) Inputs.Source = f => input;
            // Scenes built a slice a frame, so the loading screen's hourglass keeps turning (bot
            // runs keep one-frame loads, frame for frame as before, unless CH360_LOAD_SLICE_MS).
            if (scripted == null && Cuphead360.Engine.SceneLoader.SliceMs == 0) Cuphead360.Engine.SceneLoader.SliceMs = 20;
            // The host mixes (HostMixer); CH360_MANAGED_MIX=1 mixes here as before (a PC check) and
            // hands the block over as it is (floats; the host clamps where it hands them on).
            if (Environment.GetEnvironmentVariable("CH360_MANAGED_MIX") == "1")
                Audio.Output = (mix, frames) => { fixed (float* p = mix) AudioOut(p, frames); };
            else
                Audio.HostMixer = new HostMixer();
            Console.WriteLine($"CONSOLE audio mixed by the {(Audio.HostMixer != null ? "host" : "engine")}");
            // A load's short clips read ahead by the host (its clip reader's thread, into its cache of clips).
            Audio.Preload = (path, batch) =>
            {
                var b = Encoding.UTF8.GetBytes(path);
                fixed (byte* p = b) OggPreload(p, b.Length, batch);
            };
            // A load's last step waits under the hourglass for the pages it read ahead (SceneLoad, RenderList.ReadAheadLoad).
            RenderList.TexturesPending = () => TexturesPending();
            RenderList.SceneReadAhead = begin => TexturesScene(begin ? 1 : 0);
            // An atlas the game loads: its pages read ahead (the boot's preload of the title's atlases readies the logo).
            Atlases.Prefetch = src => TexturePrefetch(FileTextureId(src));
            // Sprites near the view and the clips those on screen play: their pages read ahead (converted ones only).
            RenderList.ReadAhead = (t, box) =>
            {
                if (RuntimePixels.Get(t) != null) return;
                int id = TextureId(t);
                if (id >= 0) TextureAhead(id, box.x, box.y, box.z, box.w);
            };
            // Rewired's control mapper reads this field by name (ReflectionTools.GetPrivateField, widened by the
            // patcher to public members too: publicize made the field public).
            var groupsField = typeof(UnityEngine.UI.Selectable).GetField("m_GroupsAllowInteraction",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            Console.WriteLine($"CONSOLE reflection: Selectable.m_GroupsAllowInteraction {(groupsField != null ? "found" : "MISSING")}");
            Console.WriteLine("CONSOLE host loop");
            int n = 0;
            long last = System.Diagnostics.Stopwatch.GetTimestamp(), hz = System.Diagnostics.Stopwatch.Frequency;
            // The log's per-phase and per-script figures (CH360_PROFILE=0, through CH360_GUEST_ENV on PC: off, to measure their cost).
            PlayerLoop.Profile = Environment.GetEnvironmentVariable("CH360_PROFILE") != "0";
            // Per-script timing only in a profile run (profile.txt): with the host's texture lines it cost the
            // twenty-eighth run a third of its hitches against a quiet one.
            PlayerLoop.ProfileScripts = Environment.GetEnvironmentVariable("CH360_PROFILE") == "2";
            var perf = new PerfLog(hz);
            while (FrameBegin() != 0)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                float real = (float)((now - last) / (double)hz);
                last = now;
                perf.Between(real * 1000.0);
                // A console frame slower than the vblank still moves the game at its real speed.
                Delta = scripted != null ? scriptedStep : Math.Clamp(real, 1f / 60f, 1f / 30f);
                PlayerLoop.AudioDelta = scripted != null ? -1 : AudioLead(real);
                if ((scripted == null || holdScripted) && (BootHold(n) || LevelHold())) { Delta = 1e-4f; PlayerLoop.AudioDelta = 0f; }
                PollPads();
                if (!frame(++n)) break;
                if (n % 60 == 1) NoKeyboard();
                // Once a second in play: the scene's fonts kept read (RenderList.KeepFontsAhead), and the short clips
                // of what was made since the load read ahead (Audio.PreloadFresh: the player's, made as a level starts).
                if (n % 60 == 30 && !Cuphead360.Engine.SceneLoader.Loading) { RenderList.KeepFontsAhead(); Audio.PreloadFresh(more: true); }
                long built = System.Diagnostics.Stopwatch.GetTimestamp(), allocBuilt = PlayerLoop.Profile ? GC.GetAllocatedBytesForCurrentThread() : 0;
                var views = RenderList.Build();
                long listed = System.Diagnostics.Stopwatch.GetTimestamp(), allocListed = PlayerLoop.Profile ? GC.GetAllocatedBytesForCurrentThread() : 0;
                if (BootBlack()) DrawBlack();
                else Draw(views);
                long drawn = System.Diagnostics.Stopwatch.GetTimestamp(), allocDrawn = PlayerLoop.Profile ? GC.GetAllocatedBytesForCurrentThread() : 0;
                perf.Frame(n, (drawn - now) * 1000.0 / hz, PlayerLoop.PhaseMs, (listed - built) * 1000.0 / hz, (drawn - listed) * 1000.0 / hz);
                perf.Allocs(PlayerLoop.PhaseBytes, allocListed - allocBuilt, allocDrawn - allocListed);
                Array.Clear(PlayerLoop.PhaseMs);
                Array.Clear(PlayerLoop.PhaseBytes);
                if (n % 600 == 0)
                    Console.WriteLine($"CONSOLE frame {n} scene={Cuphead360.Engine.Scenes.Active?.Name} heap {GC.GetTotalMemory(false) >> 20} MB exceptions {Exceptions.Count}; "
                        + $"GCs {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} (gen0/1/2), paused {(long)GC.GetTotalPauseDuration().TotalMilliseconds} ms in all; "
                        + $"converted files {ChobFile.LoadedFiles} ({ChobFile.LoadedBytes >> 20} MB, objects read {ChobFile.ObjectReads} ({ChobFile.ObjectBytes >> 20} MB) in {ChobFile.BlockReads} block reads)"
                        + $", trees dropped {ChobObject.TreesDropped}, parsed again {ChobObject.TreesReparsed}");
            }
            return 0;
        }

        /// <summary>
        /// The mix's length this frame: the frame's time, corrected toward a little ahead of what the host's
        /// output still holds. Mixing the frame's time alone left the output nothing to spare (a frame a
        /// little longer than the one before ran it dry: 7,351 empty audio frames in the twenty-sixth run's
        /// first minute). It keeps AudioLead ahead (a frame's jitter); a surplus is played off at up to half
        /// the frame's time. A frame later than that no longer runs the output dry: the host's audio thread
        /// carries the last mix's voices on until the next (the twenty-ninth run crackled at every slow
        /// frame), so what the frame took is already played and the next mix covers one frame (at most a
        /// 30 fps one), not the whole hold-up: mixing that (up to 250 ms) stayed queued as that much delay.
        /// </summary>
        const float AudioLeadSeconds = 0.015f;

        static float AudioLead(float real)
        {
            int queued = AudioQueued();
            if (queued < 0) return Math.Clamp(real, 0f, 0.25f);   // no output playing in real time (the PC host)
            float mix = Math.Clamp(real, 0f, 1f / 30f);
            float gap = AudioLeadSeconds - queued / (float)Audio.Rate;
            return Math.Clamp(mix + Math.Clamp(gap, -0.5f * mix, 0.25f), 0f, 0.5f);
        }

        static readonly bool keepKeyboard = Environment.GetEnvironmentVariable("CH360_KEYBOARD") == "1";

        /// <summary>
        /// The console has no keyboard or mouse, so Rewired's are turned off (checked again now and then, in
        /// case its input manager starts over): polling the keyboard's 130-odd keys and evaluating each
        /// player's keyboard and mouse maps were a third to half of its update, which runs before every
        /// fixed step and every frame (1.6-2 ms of each console frame in the fifteenth run, about 40% of
        /// the scripts' time in fights). CH360_KEYBOARD=1 keeps them (key-scripted PC checks).
        /// </summary>
        static void NoKeyboard()
        {
            if (keepKeyboard || !Rewired.ReInput.isReady) return;
            var k = Rewired.ReInput.controllers.Keyboard;
            var m = Rewired.ReInput.controllers.Mouse;
            if (k != null && k.enabled) k.enabled = false;
            if (m != null && m.enabled) m.enabled = false;
        }

        /// <summary>
        /// Console set-up and a 64-bit integer check at start-up: the first two
        /// hardware runs formatted path ids with their low word's sign, which
        /// "I64CHECK" catches (values come from strings so nothing folds at
        /// compile time).
        /// </summary>
        public static void SelfCheck()
        {
            // A check that throws is named in the FAIL line with what it threw (the first -Os build
            // stopped here with a bare "Unhandled exception.").
            var bad = new List<string>();
            string step = "set-up";
            try { SelfCheck(bad, ref step); }
            catch (Exception e) { bad.Add(step + " threw " + Program.Describe(e)); }
            Console.WriteLine(bad.Count == 0 ? "I64CHECK ok" : "I64CHECK FAIL: " + string.Join("; ", bad));
        }

        static void SelfCheck(List<string> bad, ref string step)
        {
            // The console reports slow loads and keeps a smaller decoded-audio cache (32 MB).
            Cuphead360.Engine.SceneLoader.Timing = true;
            Audio.OpenReader = HostClip.Open;
            ChobFile.Trace = Console.WriteLine;
            Audio.CacheFloats = 2L * 1024 * 1024;   // 8 MB of the host mixer's decoded clips (textures need the room)
            if (ChobObject.TreeBudget == long.MaxValue) ChobObject.TreeBudget = 24L << 20;   // parsed trees kept (estimated bytes)
            ChobFile.BlockBudget = 8L << 20;   // large files' blocks kept
            Cuphead360.Engine.SceneLoader.CollectEveryBytes = 32L << 20;   // collections while scenes load (the memory's peak stays)
            RenderList.HostTransforms = !managedDraw;   // sprites' vertices go to the host untransformed
            step = "long.Parse and ToString";
            foreach (var s in new[] { "5848540871337674781", "-2592357432173453771", "4294967296", "-4294967296", "2147483648", "-1" })
                if (long.Parse(s).ToString() != s) bad.Add("format " + s + " -> " + long.Parse(s));
            long two32 = long.Parse("4294967296"), one = long.Parse("1"), two31 = long.Parse("2147483648"), zero = long.Parse("0");
            step = "IdText.Of";
            foreach (var s in new[] { "5848540871337674781", "-2592357432173453771", "-9223372036854775808", "0" })
                if (IdText.Of(long.Parse(s)) != s) bad.Add("IdText " + s + " -> " + IdText.Of(long.Parse(s)));
            step = "compares";
            if (!(two32 > one)) bad.Add("2^32 > 1");
            if (!(two31 > zero)) bad.Add("2^31 > 0");
            if (two32 + 5 == long.Parse("5")) bad.Add("2^32+5 == 5");
            if (!((ulong)two32 > uint.MaxValue)) bad.Add("(ulong)2^32 > uint.MaxValue");
            if (!(-two32 < zero)) bad.Add("-2^32 < 0");
            // What 32-bit mode could still get wrong after record_forms.py: signs
            // and zero tests of 64-bit results, carries, shifts of negatives.
            long neg2_32 = -two32, mixed = long.Parse("-4294967295");   // 0xFFFFFFFF_00000000, 0xFFFFFFFF_00000001
            step = "signs, division and shifts";
            if (Math.Sign(two31) != 1 || Math.Sign(neg2_32) != -1 || Math.Sign(mixed) != -1) bad.Add("Math.Sign");
            if (two31.CompareTo(zero) != 1 || neg2_32.CompareTo(zero) != -1) bad.Add("CompareTo 0");
            if (Math.Abs(neg2_32) != two32 || Math.Abs(two31) != two31) bad.Add("Math.Abs");
            if (neg2_32 / 16 != long.Parse("-268435456") || mixed / 2 != long.Parse("-2147483647")) bad.Add("signed / 2^n");
            if (neg2_32 >> 4 != long.Parse("-268435456")) bad.Add("signed >> 4");
            if ((two32 & two32) == 0 || (neg2_32 & long.Parse("-4294967296")) == 0) bad.Add("high-word & != 0");
            step = "ulong.Parse and compares";
            ulong ua = ulong.Parse("4294967296"), ub = ulong.Parse("4294967295"), uc = ulong.Parse("18446744069414584320");
            if (!(ua > ub) || !(uc > ua) || Math.Max(ua, ub) != ua || Math.Min(uc, ua) != ua) bad.Add("unsigned compare");
            if (ub + 1 != ua || ua - 1 != ub || unchecked(uc + ua) != 0) bad.Add("carry");
        }

        static void PollPads()
        {
            byte* s = stackalloc byte[16];
            for (int i = 0; i < 4; i++)
            {
                var p = input.Pads[i];
                if (Pad(i, s) == 0) { p.Connected = false; p.Buttons = 0; p.LX = p.LY = p.RX = p.RY = p.LT = p.RT = 0; continue; }
                var span = new ReadOnlySpan<byte>(s, 16);
                p.Connected = true;
                p.Buttons = (PadButton)BinaryPrimitives.ReadUInt16LittleEndian(span);
                p.LX = Stick(BinaryPrimitives.ReadInt16LittleEndian(span.Slice(2)), 7849);
                p.LY = Stick(BinaryPrimitives.ReadInt16LittleEndian(span.Slice(4)), 7849);
                p.RX = Stick(BinaryPrimitives.ReadInt16LittleEndian(span.Slice(6)), 8689);
                p.RY = Stick(BinaryPrimitives.ReadInt16LittleEndian(span.Slice(8)), 8689);
                p.LT = span[10] > 30 ? span[10] / 255f : 0;
                p.RT = span[11] > 30 ? span[11] / 255f : 0;
            }
        }

        static float Stick(short v, int dead) => Math.Abs((int)v) < dead ? 0 : Math.Max(-1f, v / 32767f);

        // ------------------------------------------------------------ draw list

        static void Need(int bytes)
        {
            if (len + bytes <= buf.Length) return;
            Array.Resize(ref buf, Math.Max(buf.Length * 2, len + bytes));
        }
        static void U32(uint v) { Need(4); BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(len), v); len += 4; }
        static void I32(int v) { Need(4); BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(len), v); len += 4; }
        static void F32(float v) { Need(4); BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(len), v); len += 4; }

        /// <summary>CH360_DRAW=managed (a PC check, through CH360_GUEST_ENV): the draw list written here, the old way, to compare with.</summary>
        static readonly bool managedDraw = Environment.GetEnvironmentVariable("CH360_DRAW") == "managed";

        /// <summary>
        /// The frame's draw list through the host: per view its rect and clear,
        /// per item its arrays where they lie in wasm memory (the host reads
        /// them during the call: vertices, uvs, colours, triangles) and, for
        /// sprites, the map from their own vertices to pixels. The host's
        /// native code transforms, clips and writes the vertices (as managed
        /// code here they were most of a world map frame on the console).
        /// </summary>
        // SceneLoader's state, read for the host: its loading flag, and its icon (the hourglass) and fader images.
        static System.Reflection.PropertyInfo loaderLoading;
        static System.Reflection.FieldInfo loaderInstance, loaderIcon, loaderFader;
        static bool loaderLooked;

        /// <summary>
        /// The loading screen with the hourglass faded in on a black screen (SceneLoader loading, its fader
        /// and icon opaque): the host keeps these frames and draws them again while the game is held up
        /// (an activation's Awakes and Starts, a full collection), so the hourglass keeps turning. 0 if not;
        /// else 0x80000000 | the hourglass animation's cycle in ms &lt;&lt; 16 (under 16384) | where it is in it, ms
        /// (the host draws the kept frame nearest where the animation would be; 0 for the cycle if it isn't
        /// known); 0x40000000 marks a frame recorded ahead, not shown (RecordHourglass).
        /// </summary>
        static uint LoadingScreen()
        {
            if (!loaderLooked)
            {
                loaderLooked = true;
                var t = Type.GetType("SceneLoader, Assembly-CSharp");
                const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
                loaderLoading = t?.GetProperty("CurrentlyLoading", any);
                loaderInstance = t?.GetField("_instance", any);
                loaderIcon = t?.GetField("icon", any);
                loaderFader = t?.GetField("fader", any);
            }
            if (loaderLoading == null || loaderInstance == null || loaderIcon == null || loaderFader == null) return 0;
            if (!(loaderLoading.GetValue(null) is bool loading) || !loading) return 0;
            var inst = loaderInstance.GetValue(null) as UnityEngine.Object;
            if (inst == null) return 0;
            var icon = loaderIcon.GetValue(inst) as UnityEngine.UI.Graphic;
            var fader = loaderFader.GetValue(inst) as UnityEngine.UI.Graphic;
            if (icon == null || fader == null || !icon.isActiveAndEnabled || icon.color.a < 0.99f || fader.color.a < 0.99f) return 0;
            uint state = 0x80000000u;
            var animator = icon.GetComponent<Animator>();
            if (animator != null && animator.isActiveAndEnabled)
            {
                var info = animator.GetCurrentAnimatorStateInfo(0);
                float speed = Math.Abs(info.speed * info.speedMultiplier);
                float cycle = speed > 0f ? info.length / speed * 1000f : 0f;
                if (cycle >= 1f && cycle < 16384f)
                {
                    float at = info.normalizedTime - (float)Math.Floor(info.normalizedTime);
                    state |= (uint)cycle << 16 | (uint)(at * cycle) & 0xFFFF;
                }
            }
            return state;
        }

        // The boot's hold (CH360_BOOT_HOLD=0: off): the title scene's first frames run with the game's clock all
        // but stopped (its logo waits a second on a black screen first) until the atlases it asked for have come
        // and the textures its logo, title and their clips show are read: in the twenty-fourth run they were read
        // as the logo and the title played, frames waiting on them (the user: both hitched).
        static int bootHold;   // 0 not yet, 1 holding, 2 done
        static long bootFrom, bootProgressAt;
        static int bootFrames, bootSprites, bootPending = int.MaxValue, bootUnbound = -1;
        static bool bootSettled;
        static readonly bool bootHoldOff = Environment.GetEnvironmentVariable("CH360_BOOT_HOLD") == "0";

        static bool BootHold(int frame)
        {
            if (bootHold == 2 || bootHoldOff) return false;
            long now = System.Diagnostics.Stopwatch.GetTimestamp(), hz = System.Diagnostics.Stopwatch.Frequency;
            if (bootHold == 0)
            {
                if (Cuphead360.Engine.Scenes.Active?.Name != "scene_title") return false;
                bootHold = 1;
                bootFrom = bootProgressAt = now;
                MakeSceneLoader();
            }
            bootFrames++;
            if (bootFrames % 10 == 1 && Atlases.Outstanding == 0)
            {
                // Sprites whose atlas isn't here ask for it; once a pass binds no more of them, nothing else will.
                int read = RenderList.ReadAheadScene(out int unbound);
                bootSprites = Math.Max(bootSprites, read);
                bootSettled = unbound == 0 || unbound == bootUnbound;
                bootUnbound = unbound;
            }
            int pending = TexturesPending(), atlases = Atlases.Outstanding;
            if (pending < bootPending) { bootPending = pending; bootProgressAt = now; }
            bool done = bootFrames > 12 && bootSettled && atlases == 0 && pending == 0;
            // Never longer than 12 s, nor 3 s without a texture read (the host's memory may hold the rest back).
            if (done || now - bootFrom > 12 * hz || now - bootProgressAt > 3 * hz)
            {
                bootHold = 2;
                Console.WriteLine($"BOOT hold: {bootFrames} frames, {(now - bootFrom) * 1000 / hz} ms; {bootSprites} sprites read ahead, {bootUnbound} with no atlas; "
                    + $"{pending} textures still coming, {atlases} atlases asked for{(done ? "" : " (gave up)")}");
                return false;
            }
            return true;
        }

        /// <summary>
        /// The boot's frames drawn black: scene_start's (its build held frames for up to 1.4 s) and the boot
        /// hold's (the game's clock all but stopped). They showed the film grain on black, which stopped
        /// whenever a frame was held: the user saw the screen before the MDHR logo hitch in every run. The
        /// grain is still listed (its frames are read ahead the first time, under the hold) and shows from
        /// the title's own black second on.
        /// </summary>
        static bool BootBlack() => !managedDraw && (bootHold == 1 || Cuphead360.Engine.Scenes.Active?.Name == "scene_start");

        static void DrawBlack()
        {
            LoadingScreenFlag(0);
            float* vh = stackalloc float[13];
            vh[0] = 0f; vh[1] = 0f; vh[2] = Icalls.ScreenWidth; vh[3] = Icalls.ScreenHeight;
            ((uint*)vh)[4] = 3u;   // the colour cleared, and depth and stencil
            vh[5] = vh[6] = vh[7] = 0f; vh[8] = 1f;
            ((uint*)vh)[9] = 0u;   // the screen
            ((uint*)vh)[10] = (uint)Icalls.ScreenWidth;
            ((uint*)vh)[11] = (uint)Icalls.ScreenHeight;
            vh[12] = 1f;
            DrawView(vh);
            DrawEnd();
        }

        /// <summary>
        /// The game makes its scene loader (the iris wipe and the hourglass: Resources "UI/Scene_Loader") the
        /// first time it loads a scene, the title's button press: reading its 155 objects (74 sprites, 16
        /// textures, 25 shaders) froze that frame for 230-245 ms in every run (the twenty-sixth to the
        /// twenty-ninth, update 189-202 ms). It is made here, under the boot's black, as that press makes it
        /// (SceneLoader.instance), and the boot hold reads its pages ahead with the other persistent objects.
        /// </summary>
        static void MakeSceneLoader()
        {
            try
            {
                var p = Type.GetType("SceneLoader, Assembly-CSharp")?.GetProperty("instance",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                Console.WriteLine($"BOOT scene loader {(p?.GetValue(null) != null ? "made" : "not found")}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"BOOT scene loader: {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>
        /// A level's start, as the boot's: the game held (its clock all but stopped, behind the load's black)
        /// until its frames draw without waiting for a page (three in a row), the clips its animators play now
        /// asked for ahead (RenderList.ReadAheadFirst). They came in its first frames, under the fade in: the
        /// twenty-sixth run's Frogs started with frames of 234, 308 and 196 ms. Not until every page asked for
        /// is in, as the boot's: the read-ahead's backlog (300-odd pages) ran it to its limit in the
        /// twenty-seventh run. At most 3 s; CH360_LEVEL_HOLD=0: none. Every scene that comes in from the
        /// loader's black, not only levels (SceneLoader.Prepared: the slot select's fonts and pages came as it
        /// faded in, a 250 ms frame in every run).
        /// </summary>
        static int levelLoads = -1, levelFrames, levelSprites, levelUnbound, levelClear, levelPending;
        static long levelFrom, levelProgressAt;
        static bool levelHolding, levelSettled;
        static readonly bool levelHoldOff = Environment.GetEnvironmentVariable("CH360_LEVEL_HOLD") == "0";
        // CH360_HOLD_SCRIPTED=1: the holds in scripted runs too (PC checks of them; scripted runs skip them otherwise)
        static readonly bool holdScripted = Environment.GetEnvironmentVariable("CH360_HOLD_SCRIPTED") == "1";

        static bool LevelHold()
        {
            if (levelHoldOff || bootHold != 2) return false;
            long now = System.Diagnostics.Stopwatch.GetTimestamp(), hz = System.Diagnostics.Stopwatch.Frequency;
            var name = Cuphead360.Engine.Scenes.Active?.Name;
            if (!levelHolding)
            {
                int loads = Cuphead360.Engine.SceneLoader.Loads;
                if (loads == levelLoads || name == null) return false;
                levelLoads = loads;
                if (!Cuphead360.Engine.SceneLoader.Prepared(name)) return false;
                levelHolding = true;
                levelFrom = levelProgressAt = now;
                levelPending = int.MaxValue;
                levelFrames = levelSprites = levelClear = 0;
                levelUnbound = -1;
                levelSettled = false;
            }
            levelFrames++;
            if (levelFrames % 5 == 1 && Atlases.Outstanding == 0)
            {
                // A menu (the slot select, the shop, the results) shows most of what it has only as it plays: its
                // whole scene, active or not, with its texts' fonts (the slot select's atlas came from the drive two
                // seconds in, a 234 ms frame as it faded in). A level, map or cutscene: what it shows first.
                bool menu = !name.StartsWith("scene_level_") && !name.StartsWith("scene_map_") && !name.StartsWith("scene_cutscene_");
                int read = menu ? RenderList.ReadAheadScene(out int unbound, languageFonts: true) : RenderList.ReadAheadFirst(out unbound);
                levelSprites = Math.Max(levelSprites, read);
                levelSettled = unbound == 0 || unbound == levelUnbound;
                levelUnbound = unbound;
            }
            int waited = TexturesWaited(), atlases = Atlases.Outstanding, pending = TexturesPending();
            levelClear = waited == 0 ? levelClear + 1 : 0;
            if (pending < levelPending) { levelPending = pending; levelProgressAt = now; }
            // and what it asked for (the player's first clips: the player is made after the load), until none
            // is coming or half a second passes without a page read (memory holds the rest back)
            bool done = levelFrames > 2 && levelSettled && atlases == 0 && levelClear >= 3 && (pending == 0 || (now - levelProgressAt) * 2 > hz);
            if (done || now - levelFrom > 3 * hz)
            {
                levelHolding = false;
                Console.WriteLine($"LEVEL hold {name}: {levelFrames} frames, {(now - levelFrom) * 1000 / hz} ms; {levelSprites} sprites read ahead, "
                    + $"{levelUnbound} with no atlas; the last frame waited for {waited} textures, {TexturesPending()} still coming, {atlases} atlases asked for{(done ? "" : " (gave up)")}");
                return false;
            }
            return true;
        }

        // The hourglass by the clock: the game's clock runs at most 1/30 s a frame, so a slow load turned it slowly,
        // and after a hold-up the host's frames (which go by the clock) gave way to the game's from further back
        // (the twenty-fourth run's skips). Each loading frame shows the kept frame for where the clock says the
        // hourglass is, and its animator is put there for the frames after (the fade out).
        static long hourglassFrom;
        static float hourglassAt;
        static uint hourglassCycle;

        static void Draw(List<RenderList.View> views)
        {
            uint state = LoadingScreen();
            uint cycle = (state >> 16) & 0x3FFF;
            if (state != 0 && cycle != 0 && !managedDraw)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (hourglassFrom == 0 || cycle != hourglassCycle)
                {
                    hourglassFrom = now;
                    hourglassAt = (state & 0xFFFF) / (float)cycle;
                    hourglassCycle = cycle;
                }
                float fraction = hourglassAt + (float)((now - hourglassFrom) * 1000.0 / System.Diagnostics.Stopwatch.Frequency / cycle);
                fraction -= MathF.Floor(fraction);
                var inst = loaderInstance.GetValue(null) as UnityEngine.Object;
                var icon = inst != null ? loaderIcon.GetValue(inst) as UnityEngine.UI.Image : null;
                var animator = icon != null ? icon.GetComponent<Animator>() : null;
                var sprite = animator != null ? NAnimator.SpriteAt(animator, fraction) : null;
                if (sprite != null)
                {
                    if (!ReferenceEquals(icon.sprite, sprite)) { icon.sprite = sprite; Cuphead360.Engine.UI.WillRender(); views = RenderList.Build(); }
                    animator.Play(0, 0, fraction);   // 0: the current state
                    state = 0x80000000u | cycle << 16 | (uint)(fraction * cycle) & 0xFFFF;
                }
            }
            else hourglassFrom = 0;
            DrawFrame(views, state);
            RecordHourglass(state, views);
        }

        // The hourglass's frames recorded ahead: one per slot of the host's 64 over its cycle, a few each frame
        // from the loading screen's first. The host kept only the frames the game drew, a part of the 2.2 s
        // cycle in a long load, and drew that part over and over (the twenty-first to twenty-third runs).
        // Not while a scene still draws behind the fader (the one before the load, the level being held): the
        // host keeps only the screen itself (the hourglass on black, under 24 items; main.cpp record_loading),
        // and eight builds of the map a frame were for nothing.
        const int HourglassSlots = 64, HourglassSlotsAFrame = 8, HourglassItems = 24;
        static int hourglassRecorded = -1;

        static void RecordHourglass(uint state, List<RenderList.View> drawn)
        {
            uint cycle = (state >> 16) & 0x3FFF;
            if (state == 0 || cycle == 0) { hourglassRecorded = -1; return; }
            if (hourglassRecorded >= HourglassSlots || managedDraw) return;
            int items = 0;
            foreach (var v in drawn) items += v.Items.Count;
            if (items > HourglassItems) return;
            if (hourglassRecorded < 0) hourglassRecorded = 0;
            var inst = loaderInstance.GetValue(null) as UnityEngine.Object;
            var icon = inst != null ? loaderIcon.GetValue(inst) as UnityEngine.UI.Image : null;
            var animator = icon != null ? icon.GetComponent<Animator>() : null;
            if (animator == null) { hourglassRecorded = HourglassSlots; return; }
            var shown = icon.sprite;
            for (int n = 0; n < HourglassSlotsAFrame && hourglassRecorded < HourglassSlots; n++, hourglassRecorded++)
            {
                float fraction = (hourglassRecorded + 0.5f) / HourglassSlots;
                var sprite = NAnimator.SpriteAt(animator, fraction);
                if (sprite == null) { hourglassRecorded = HourglassSlots; break; }
                icon.sprite = sprite;
                Cuphead360.Engine.UI.WillRender();
                DrawFrame(RenderList.Build(), 0xC0000000u | cycle << 16 | (uint)(fraction * cycle));
            }
            icon.sprite = shown;
        }

        static void DrawFrame(List<RenderList.View> views, uint loading)
        {
            LoadingScreenFlag(loading);
            if (managedDraw) { DrawManaged(views); return; }
            float* vh = stackalloc float[13];
            int* a = stackalloc int[ItemSlots];
            foreach (var v in views)
            {
                vh[0] = v.PixelRect.x; vh[1] = v.PixelRect.y; vh[2] = v.PixelRect.width; vh[3] = v.PixelRect.height;
                var cam = v.Camera;
                bool clear = cam != null && (cam.ClearFlags == CameraClearFlags.SolidColor || cam.ClearFlags == CameraClearFlags.Skybox);
                // 1 the colour; 2 depth and stencil (every clear flag but Nothing; the sprite masks' stencil)
                ((uint*)vh)[4] = (clear ? 1u : 0u) | (cam != null && cam.ClearFlags != CameraClearFlags.Nothing ? 2u : 0u);
                if (v.Resume) ((uint*)vh)[4] = 4u;   // 4: go on in the target with what it held (after a grab's copy)
                var bg = cam != null ? cam.Background : default;
                vh[5] = bg.r; vh[6] = bg.g; vh[7] = bg.b; vh[8] = 1f;
                // The target: 0 the screen, else a render texture (cleared with the camera's alpha).
                ((uint*)vh)[9] = (uint)Effects.TargetId(v.Target);
                ((uint*)vh)[10] = (uint)(v.Target != null ? Effects.Width(v.Target) : Icalls.ScreenWidth);
                ((uint*)vh)[11] = (uint)(v.Target != null ? Effects.Height(v.Target) : Icalls.ScreenHeight);
                vh[12] = v.Target != null ? bg.a : 1f;
                DrawView(vh);
                masks.Clear();
                lastMaterial = null;
                foreach (var it in v.Items) HostItem(it, a);
            }
            DrawEnd();
        }

        // The last item's material, its state and mode (items in a row mostly share one).
        static Material lastMaterial;
        static MaterialState lastState;
        static uint lastMode;
        static readonly float[] effectParams = new float[Effects.ParamCount];

        // draw_item's arguments, 32-bit slots (console/ch360_imports.c reads the same):
        //  0 texture  1 mode  2-5 blend colour (the tint)  6 flags (1 vertex colours as they are, 2 the affine map, 4 clip)
        //  7 alpha scale  8/9 triangles  10/11 positions  12/13 uvs  14/15 colours (address, count)
        //  16-21 A00 A01 B0 A10 A11 B1  22-25 clip x0 y0 x1 y1
        //  26/27 mode 6's shadow and sky glow textures  28-30 its t, textureWidth, textureSpeed
        //  31 sprite masks' stencil (RenderList.Item.SpriteStencil: 1 add, 2 remove for a mask, mode 7; 3 inside, 4 outside)
        //  32-43 an effect's values (modes 8-17, Effects.cs); 26 mode 17's overlay texture
        const int ItemSlots = 44;
        static readonly int shadowTex = ShaderProps.Id("_ShadowTex"), skyGlowTex = ShaderProps.Id("_SkyGlowTex");
        static readonly int rayT = ShaderProps.Id("t"), rayWidth = ShaderProps.Id("textureWidth"), raySpeed = ShaderProps.Id("textureSpeed");

        static void HostItem(RenderList.Item it, int* a)
        {
            if (it.Triangles.Length == 0) return;
            bool clipped = MaskClip(it, out var clip, out bool stencilOnly);
            if (stencilOnly) return;
            int tex = -1;
            if (it.Texture != null && (tex = TextureId(it.Texture)) < 0) return;   // not drawable
            if (Clock.FrameCount <= NAnimator.WindowUntil && it.Name.StartsWith("Cuphead ["))
                Console.WriteLine($"EXTRACE frame {Clock.FrameCount} draws {it.Name} texture {tex}, layer {it.Layer} order {it.Order}, {it.Triangles.Length} corners, tint a {it.Tint.a:0.##}");
            if (it.Material == null || !ReferenceEquals(it.Material, lastMaterial))
            {
                lastMaterial = it.Material;
                lastState = MaterialState.Of(it.Material);
                lastMode = (uint)Effects.ModeOf(lastState?.Shader);
            }
            var ms = lastState;
            // A sprite mask writes the stencil only: mode 7 (its sprite's alpha against the cutoff in the tint);
            // a blit has its own mode.
            uint mode = it.Effect >= 0 ? (uint)it.Effect : it.Kind == "mask" ? 7u : lastMode;
            var tint = it.Tint;
            bool blend = mode >= 1 && mode <= 3;
            var f = (float*)a;
            a[26] = a[27] = -1;
            f[28] = f[29] = f[30] = 0f;
            if (mode == 6)
            {
                a[26] = ms.Textures.TryGetValue(shadowTex, out var st) && st != null ? TextureId(st) : -1;
                a[27] = ms.Textures.TryGetValue(skyGlowTex, out var gt) && gt != null ? TextureId(gt) : -1;
                f[28] = ms.Floats.TryGetValue(rayT, out var rt) ? rt : 0f;
                f[29] = ms.Floats.TryGetValue(rayWidth, out var rw) ? rw : 1f;
                f[30] = ms.Floats.TryGetValue(raySpeed, out var rs) ? rs : 0f;
            }
            if (mode >= Effects.KnightGlow)
            {
                var p = it.Effect >= 0 ? it.P : effectParams;
                if (it.Effect < 0) Effects.Params(it, (int)mode, p);
                for (int k = 0; k < Effects.ParamCount; k++) f[32 + k] = p[k];
                if (it.Tex2 != null) a[26] = TextureId(it.Tex2);
            }
            else for (int k = 0; k < Effects.ParamCount; k++) f[32 + k] = 0f;
            var pos = it.Screen ?? it.Local;
            a[0] = tex; a[1] = (int)mode;
            f[2] = tint.r; f[3] = tint.g; f[4] = tint.b; f[5] = tint.a;
            a[6] = (blend ? 1 : 0) | (it.Screen == null ? 2 : 0) | (clipped ? 4 : 0);
            f[7] = blend && it.Kind == "ui" ? Mathf.Clamp01(tint.a) : 1f;
            a[9] = it.Triangles.Length; a[11] = pos.Length; a[13] = it.Uv?.Length ?? 0; a[15] = it.Colors?.Length ?? 0;
            f[16] = it.A00; f[17] = it.A01; f[18] = it.B0; f[19] = it.A10; f[20] = it.A11; f[21] = it.B1;
            f[22] = clip.x; f[23] = clip.y; f[24] = clip.z; f[25] = clip.w;
            a[31] = it.SpriteStencil;
            fixed (int* tri = it.Triangles)
            fixed (Vector2* p = pos)
            fixed (Vector2* uv = it.Uv)
            fixed (Color32* c = it.Colors)
            {
                a[8] = (int)tri; a[10] = (int)p; a[12] = (int)uv; a[14] = (int)c;
                DrawItem(a);
            }
        }

        // UGUI masks: a mask's graphic writes the stencil (its children test it;
        // a "pop" after them writes it back), and the console has no stencil
        // here, so the stencil values are kept as nested screen rectangles and
        // an item that tests one is clipped to it: exact for rectangular masks,
        // which is what UGUI's Mask on an Image mostly is (the results board's
        // banner wipe). The stack is per view, innermost last: (value, x0 y0 x1 y1).
        static readonly List<(int value, Vector4 rect)> masks = new List<(int, Vector4)>();

        static bool MaskClip(RenderList.Item it, out Vector4 clip, out bool stencilOnly)
        {
            clip = default; stencilOnly = false;
            var st = it.Stencil;   // ref, comp, op, readMask, writeMask, colorMask
            if (st == null) return false;
            int rf = st[0], comp = st[1], op = st[2];
            bool clipped = false;
            // CompareFunction 3 Equal: where the stencil is ref & readMask; 4 LessEqual: where it is ref or more.
            if (comp == 3 || comp == 4)
                for (int i = masks.Count - 1; i >= 0; i--)
                    if (comp == 3 ? masks[i].value == (rf & st[3]) : masks[i].value >= rf) { clip = masks[i].rect; clipped = true; break; }
            // StencilOp 2 Replace, 1 Zero, 3/6 increment, 4/7 decrement: the value the item writes.
            int top = masks.Count > 0 ? masks[masks.Count - 1].value : 0, value = -1;
            if (op == 2) value = rf & st[4];
            else if (op == 1) value = 0;
            else if (op == 3 || op == 6) value = top + 1;
            else if (op == 4 || op == 7) value = Math.Max(0, top - 1);
            if (value >= 0)
            {
                while (masks.Count > 0 && masks[masks.Count - 1].value > value) masks.RemoveAt(masks.Count - 1);
                if (value > 0 && (masks.Count == 0 || masks[masks.Count - 1].value != value) && it.Screen != null && it.Screen.Length > 0)
                {
                    var r = Bounds(it.Screen);
                    if (clipped) r = new Vector4(Math.Max(r.x, clip.x), Math.Max(r.y, clip.y), Math.Min(r.z, clip.z), Math.Min(r.w, clip.w));
                    masks.Add((value, r));
                }
            }
            stencilOnly = st[5] == 0;
            return clipped;
        }

        static Vector4 Bounds(Vector2[] s)
        {
            float x0 = s[0].x, y0 = s[0].y, x1 = x0, y1 = y0;
            for (int i = 1; i < s.Length; i++)
            {
                if (s[i].x < x0) x0 = s[i].x; else if (s[i].x > x1) x1 = s[i].x;
                if (s[i].y < y0) y0 = s[i].y; else if (s[i].y > y1) y1 = s[i].y;
            }
            return new Vector4(x0, y0, x1, y1);
        }

        static void DrawManaged(List<RenderList.View> views)
        {
            len = 0;
            U32(0x31464843);   // 'CHF1'
            uint screenViews = 0;
            foreach (var v in views) if (v.Target == null) screenViews++;
            U32(screenViews);
            foreach (var v in views)
            {
                if (v.Target != null) continue;
                F32(v.PixelRect.x); F32(v.PixelRect.y); F32(v.PixelRect.width); F32(v.PixelRect.height);
                var cam = v.Camera;
                bool clear = cam != null && (cam.ClearFlags == CameraClearFlags.SolidColor || cam.ClearFlags == CameraClearFlags.Skybox);
                U32((clear ? 1u : 0u) | (cam != null && cam.ClearFlags != CameraClearFlags.Nothing ? 2u : 0u));
                var bg = cam != null ? cam.Background : default;
                F32(bg.r); F32(bg.g); F32(bg.b); F32(1f);
                int countAt = len; U32(0);
                uint items = 0;
                foreach (var it in v.Items) if (Item(it)) items++;
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(countAt), items);
            }
            fixed (byte* p = buf) Submit(p, len);
        }

        static bool Item(RenderList.Item it)
        {
            int n = it.Triangles.Length;
            if (n == 0) return false;
            // A UGUI mask's own graphic only writes the stencil (no stencil here yet: its children draw unclipped).
            if (it.Stencil != null && it.Stencil[5] == 0) return false;
            if (it.Kind == "mask") return false;
            // Render textures and effects are the native path's (this one checks the old list).
            if (it.Texture is RenderTexture || Effects.ModeOf(it) >= Effects.KnightGlow) return false;
            int tex = -1;
            if (it.Texture != null && (tex = TextureId(it.Texture)) < 0) return false;   // not drawable
            uint mode = (uint)Effects.ModeOf(MaterialState.Of(it.Material)?.Shader);
            var tint = it.Tint;
            I32(tex); U32(mode);
            F32(tint.r); F32(tint.g); F32(tint.b); F32(tint.a);
            U32((uint)n);
            Need(n * 20);
            bool blend = mode >= 1 && mode <= 3;
            float alphaScale = blend && it.Kind == "ui" ? Mathf.Clamp01(tint.a) : 1f;
            var span = buf.AsSpan();
            for (int i = 0; i < n; i++)
            {
                int k = it.Triangles[i];
                if (k < 0 || k >= it.Screen.Length) k = 0;
                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(len), it.Screen[k].x);
                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(len + 4), it.Screen[k].y);
                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(len + 8), it.Uv[k].x);
                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(len + 12), it.Uv[k].y);
                var c = it.Colors != null && k < it.Colors.Length ? it.Colors[k] : new Color32(255, 255, 255, 255);
                if (blend) { buf[len + 16] = c.r; buf[len + 17] = c.g; buf[len + 18] = c.b; buf[len + 19] = (byte)(c.a * alphaScale); }
                else
                {
                    // the renderer colour folds into the vertex colour
                    buf[len + 16] = (byte)Mathf.Clamp(c.r * tint.r, 0, 255); buf[len + 17] = (byte)Mathf.Clamp(c.g * tint.g, 0, 255);
                    buf[len + 18] = (byte)Mathf.Clamp(c.b * tint.b, 0, 255); buf[len + 19] = (byte)Mathf.Clamp(c.a * tint.a, 0, 255);
                }
                len += 20;
            }
            return true;
        }

        /// <summary>Render textures' numbers start here (CH360_RT_BASE, console/ch360_host.h); the player's textures from 0.</summary>
        const int RtBase = 0x40000000;

        /// <summary>A converted texture's id by its key (the file's path and the object's path id), given to the host the first time.</summary>
        static int FileTextureId(ChobObject src)
        {
            string k = src.File.Path.Replace('/', '_');
            if (k.EndsWith(".chob")) k = k.Substring(0, k.Length - 5);
            var key = Encoding.UTF8.GetBytes(k + "_" + IdText.Of(src.PathId));
            ulong h = 0xcbf29ce484222325;   // FNV-1a 64 of the key: no key strings kept
            foreach (var b in key) h = (h ^ b) * 0x100000001b3;
            if (!idsByKey.TryGetValue(h, out int id))
            {
                id = nextId++;
                idsByKey[h] = id;
                fixed (byte* p = key) TextureFile(id, p, key.Length);
            }
            return id;
        }

        static int TextureId(Texture t)
        {
            if (t is RenderTexture rt) return RtBase | Effects.TargetId(rt);
            var no = Objects.Of(t);
            if (no == null) return -1;
            int id = no.HostId;
            if (id == -1)
            {
                var src = (no as NAsset)?.Source;
                if (src != null && RuntimePixels.Get(t) == null) id = FileTextureId(src);
                else id = RuntimePixels.Get(t) != null ? nextId++ : -2;
                no.HostId = id;
            }
            if (id < 0) return -1;
            var rp = RuntimePixels.Get(t);
            if (rp != null && (!versions.TryGetValue(id, out int ver) || ver != rp.Version))
            {
                versions[id] = rp.Version;
                // Runtime pixels are stored top row first; the console's first row is Unity's v = 0 (the bottom).
                var bgra = new byte[rp.W * rp.H * 4];
                for (int y = 0; y < rp.H; y++)
                {
                    int src = y * rp.W * rp.Channels, dst = (rp.H - 1 - y) * rp.W * 4;
                    for (int x = 0; x < rp.W; x++, src += rp.Channels, dst += 4)
                    {
                        if (rp.Channels == 1) { bgra[dst] = bgra[dst + 1] = bgra[dst + 2] = 255; bgra[dst + 3] = rp.Data[src]; }
                        else { bgra[dst] = rp.Data[src + 2]; bgra[dst + 1] = rp.Data[src + 1]; bgra[dst + 2] = rp.Data[src]; bgra[dst + 3] = rp.Data[src + 3]; }
                    }
                }
                fixed (byte* p = bgra) TexturePixels(id, rp.W, rp.H, p);
            }
            return id;
        }
    }
}
