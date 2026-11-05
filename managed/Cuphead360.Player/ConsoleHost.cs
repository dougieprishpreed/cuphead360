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
        // By reference: the default comparer runs UnityEngine.Object's Equals (CompareBaseObjects) per lookup.
        static readonly Dictionary<Texture, int> ids = new Dictionary<Texture, int>(ReferenceEqualityComparer.Instance as IEqualityComparer<Texture>);
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
        static readonly double[] phaseSum = new double[PlayerLoop.PhaseMs.Length + 2];
        static double frameSum;

        public static int Play(Func<int, bool> frame)
        {
            var scripted = Inputs.Source;   // --bot / --input still drive a console run when given
            if (scripted == null) Inputs.Source = f => input;
            // Scenes built a slice a frame, so the loading screen's hourglass keeps turning (bot
            // runs keep one-frame loads, frame for frame as before, unless CH360_LOAD_SLICE_MS).
            if (scripted == null && Cuphead360.Engine.SceneLoader.SliceMs == 0) Cuphead360.Engine.SceneLoader.SliceMs = 20;
            // The mix goes to the host as it is (floats; the host clamps where it hands them on):
            // converting each sample here went through memory twice on the console's CPU.
            Audio.Output = (mix, frames) => { fixed (float* p = mix) AudioOut(p, frames); };
            Console.WriteLine("CONSOLE host loop");
            int n = 0;
            long last = System.Diagnostics.Stopwatch.GetTimestamp(), hz = System.Diagnostics.Stopwatch.Frequency;
            PlayerLoop.Profile = true;
            while (FrameBegin() != 0)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                float real = (float)((now - last) / (double)hz);
                last = now;
                // A console frame slower than the vblank still moves the game at its real speed.
                Delta = scripted != null ? scriptedStep : Math.Clamp(real, 1f / 60f, 1f / 30f);
                PlayerLoop.AudioDelta = scripted != null ? -1 : Math.Clamp(real, 0f, 0.25f);
                PollPads();
                if (!frame(++n)) break;
                long built = System.Diagnostics.Stopwatch.GetTimestamp();
                var views = RenderList.Build();
                long listed = System.Diagnostics.Stopwatch.GetTimestamp();
                Draw(views);
                long drawn = System.Diagnostics.Stopwatch.GetTimestamp();
                var ph = PlayerLoop.PhaseMs;
                for (int i = 0; i < ph.Length; i++) phaseSum[i] += ph[i];
                phaseSum[ph.Length] += (listed - built) * 1000.0 / hz;
                phaseSum[ph.Length + 1] += (drawn - listed) * 1000.0 / hz;
                frameSum += (drawn - now) * 1000.0 / hz;
                // A frame over half a second says where its time went (the player loop's phases, the draw list).
                if ((drawn - now) * 2 > hz)
                    Console.WriteLine($"SLOW frame {n}: {(drawn - now) * 1000 / hz} ms; {PlayerLoop.ProfileReport(1)} list={(listed - built) * 1000 / hz} submit={(drawn - listed) * 1000 / hz}");
                else Array.Clear(PlayerLoop.PhaseMs);
                // Where an ordinary frame's time goes, averaged over 300 frames.
                if (n % 300 == 0)
                {
                    var sb = new StringBuilder($"FRAMES {n - 299}-{n}: {frameSum / 300:0.0} ms/frame;");
                    for (int i = 0; i < ph.Length; i++) sb.Append(' ').Append(PlayerLoop.PhaseNames[i]).Append('=').Append((phaseSum[i] / 300).ToString("0.0"));
                    sb.Append(" list=").Append((phaseSum[ph.Length] / 300).ToString("0.0")).Append(" submit=").Append((phaseSum[ph.Length + 1] / 300).ToString("0.0"));
                    sb.Append("; audio decode ").Append((Audio.DecodeTicks * 1000.0 / hz / 300).ToString("0.0")).Append(" ms/frame, ").Append(Audio.VoiceCount).Append(" voices");
                    Audio.DecodeTicks = 0;
                    Console.WriteLine(sb.ToString());
                    // The scripts behind update and fixed: the costliest types (messages and coroutines).
                    Console.WriteLine($"TOP {n - 299}-{n} ms/frame:{PlayerLoop.TopReport(300, 12)}");
                    Array.Clear(phaseSum);
                    frameSum = 0;
                }
                if (n % 600 == 0)
                    Console.WriteLine($"CONSOLE frame {n} scene={Cuphead360.Engine.Scenes.Active?.Name} heap {GC.GetTotalMemory(false) >> 20} MB exceptions {Exceptions.Count}; "
                        + $"GCs {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} (gen0/1/2), paused {(long)GC.GetTotalPauseDuration().TotalMilliseconds} ms in all; "
                        + $"converted files {ChobFile.LoadedFiles} ({ChobFile.LoadedBytes >> 20} MB, objects read {ChobFile.ObjectReads} ({ChobFile.ObjectBytes >> 20} MB) in {ChobFile.BlockReads} block reads)"
                        + $", trees dropped {ChobObject.TreesDropped}, parsed again {ChobObject.TreesReparsed}");
            }
            return 0;
        }

        /// <summary>
        /// Console set-up and a 64-bit integer check at start-up: the first two
        /// hardware runs formatted path ids with their low word's sign, which
        /// "I64CHECK" catches (values come from strings so nothing folds at
        /// compile time).
        /// </summary>
        public static void SelfCheck()
        {
            // The console reports slow loads and keeps a smaller decoded-audio cache (32 MB).
            Cuphead360.Engine.SceneLoader.Timing = true;
            Audio.OpenReader = HostClip.Open;
            ChobFile.Trace = Console.WriteLine;
            Audio.CacheFloats = 4L * 1024 * 1024;   // 16 MB
            if (ChobObject.TreeBudget == long.MaxValue) ChobObject.TreeBudget = 24L << 20;   // parsed trees kept (estimated bytes)
            ChobFile.BlockBudget = 8L << 20;   // large files' blocks kept
            RenderList.HostTransforms = !managedDraw;   // sprites' vertices go to the host untransformed
            var bad = new List<string>();
            foreach (var s in new[] { "5848540871337674781", "-2592357432173453771", "4294967296", "-4294967296", "2147483648", "-1" })
                if (long.Parse(s).ToString() != s) bad.Add("format " + s + " -> " + long.Parse(s));
            long two32 = long.Parse("4294967296"), one = long.Parse("1"), two31 = long.Parse("2147483648"), zero = long.Parse("0");
            foreach (var s in new[] { "5848540871337674781", "-2592357432173453771", "-9223372036854775808", "0" })
                if (IdText.Of(long.Parse(s)) != s) bad.Add("IdText " + s + " -> " + IdText.Of(long.Parse(s)));
            if (!(two32 > one)) bad.Add("2^32 > 1");
            if (!(two31 > zero)) bad.Add("2^31 > 0");
            if (two32 + 5 == long.Parse("5")) bad.Add("2^32+5 == 5");
            if (!((ulong)two32 > uint.MaxValue)) bad.Add("(ulong)2^32 > uint.MaxValue");
            if (!(-two32 < zero)) bad.Add("-2^32 < 0");
            // What 32-bit mode could still get wrong after record_forms.py: signs
            // and zero tests of 64-bit results, carries, shifts of negatives.
            long neg2_32 = -two32, mixed = long.Parse("-4294967295");   // 0xFFFFFFFF_00000000, 0xFFFFFFFF_00000001
            if (Math.Sign(two31) != 1 || Math.Sign(neg2_32) != -1 || Math.Sign(mixed) != -1) bad.Add("Math.Sign");
            if (two31.CompareTo(zero) != 1 || neg2_32.CompareTo(zero) != -1) bad.Add("CompareTo 0");
            if (Math.Abs(neg2_32) != two32 || Math.Abs(two31) != two31) bad.Add("Math.Abs");
            if (neg2_32 / 16 != long.Parse("-268435456") || mixed / 2 != long.Parse("-2147483647")) bad.Add("signed / 2^n");
            if (neg2_32 >> 4 != long.Parse("-268435456")) bad.Add("signed >> 4");
            if ((two32 & two32) == 0 || (neg2_32 & long.Parse("-4294967296")) == 0) bad.Add("high-word & != 0");
            ulong ua = ulong.Parse("4294967296"), ub = ulong.Parse("4294967295"), uc = ulong.Parse("18446744069414584320");
            if (!(ua > ub) || !(uc > ua) || Math.Max(ua, ub) != ua || Math.Min(uc, ua) != ua) bad.Add("unsigned compare");
            if (ub + 1 != ua || ua - 1 != ub || unchecked(uc + ua) != 0) bad.Add("carry");
            Console.WriteLine(bad.Count == 0 ? "I64CHECK ok" : "I64CHECK FAIL: " + string.Join("; ", bad));
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
        static void Draw(List<RenderList.View> views)
        {
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

        static int TextureId(Texture t)
        {
            if (t is RenderTexture rt) return RtBase | Effects.TargetId(rt);
            if (!ids.TryGetValue(t, out int id))
            {
                id = ids.Count;
                ids[t] = id;
                var src = (Objects.Of(t) as NAsset)?.Source;
                if (src != null && RuntimePixels.Get(t) == null)
                {
                    string k = src.File.Path.Replace('/', '_');
                    if (k.EndsWith(".chob")) k = k.Substring(0, k.Length - 5);
                    var key = Encoding.UTF8.GetBytes(k + "_" + IdText.Of(src.PathId));
                    fixed (byte* p = key) TextureFile(id, p, key.Length);
                }
                else if (RuntimePixels.Get(t) == null) { ids[t] = -1; return -1; }
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
