using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Cuphead360.Engine
{
    /// <summary>
    /// Render textures drawn into, image effects and the game's effect shaders.
    ///
    /// A camera with a target texture draws into it (RenderList: those cameras
    /// first, as Unity renders them), and whatever samples that texture reads
    /// what it drew: the Knight's glow (Sprites/KnightGlow lights the knight's
    /// edges from its silhouette), the Bishop's darkness (Sprites/BishopDarkness
    /// darkens the room but for the glow masks drawn into it), Saltbaker's
    /// reflection, in-hands and blur, Howling Aces' sky, Glumstone's geese.
    ///
    /// A camera with OnRenderImage scripts draws into a temporary texture and
    /// each script then blits it on (Graphics.Blit, recorded here as a view of
    /// one quad over its target): BlurOptimized on Saltbaker's phase-four
    /// camera, and on the game's top camera ChromaticAberrationFilmGrain (the
    /// colour fringes, the film grain, dust and vignette) then BlurGamma (a
    /// slight blur and the brightness setting). A camera that doesn't clear
    /// starts from what the cameras before it drew (Unity copies the screen
    /// in), so the top camera's effects cover the whole picture.
    ///
    /// Modes are the hosts' shaders; values (P, 12 floats) per mode below.
    /// The effect shaders are translated from the game's own pixel programs.
    /// </summary>
    public static class Effects
    {
        // Sprites and meshes (premultiplied over).
        public const int Sprite = 0, ScreenBlend = 1, Multiply = 2, OverlayBlend = 3, Sdf = 5, LightRay = 6, Mask = 7;
        /// <summary>Sprites/KnightGlow: P0-3 _OutlineColor, P4 _DimFactor, P5 _OutlineIncreaseFactor, P6-7 _LightingVector, P8-9 texel size.</summary>
        public const int KnightGlow = 8;
        /// <summary>Sprites/BishopDarkness: black at the texel's red times P0 (_Level).</summary>
        public const int Darkness = 9;
        /// <summary>Cuphead/Sprites/FastBlurSprites (its five passes in one): P0 _BlurAmount, P1 _BlurLerp, P2-3 _DullBlacksCutoff/Factor, P8-9 texel size.</summary>
        public const int BlurSprite = 10;
        // Blits (one quad over the target, replacing it).
        /// <summary>Graphics.Blit without a material: the texels as they are.</summary>
        public const int Copy = 11;
        /// <summary>Hidden/FastBlur pass 0: four taps around the texel (P8-9 the source's texel size).</summary>
        public const int Down4 = 12;
        /// <summary>Seven taps of a Gaussian along P0-1 (uv), alpha from the middle one (FastBlur 1-4, BlurGamma 0).</summary>
        public const int Blur7 = 13;
        /// <summary>BlurGamma 1-3: the vertical seven taps, then rgb to the power P2 (the brightness setting); two-strip and black-and-white colour after.</summary>
        public const int Blur7Gamma = 14, Blur7TwoStrip = 15, Blur7Bw = 16;
        /// <summary>Hidden/Cuphead/ChromaticAberrationFilmGrain: red, green and blue from P0-1, P2-3, P4-5 (uv offsets), the overlay (Tex2) at uv (dot(uv, P6-7), dot(uv, P8-9)) multiplying and lighting.</summary>
        public const int FilmGrain = 17;
        /// <summary>Hidden/BlurWithAlpha passes 1-4: the seven taps along P0-1, alpha blurred too.</summary>
        public const int Blur7Alpha = 18;
        public const int FirstBlit = Copy;
        public static bool IsBlit(int mode) => mode >= Copy && mode <= Blur7Alpha;
        /// <summary>Sprites/Color/Overlay (the HUD's cards): the colour overlaid on the texel, both halves chosen by its red.</summary>
        public const int ColorOverlay = 19;
        // The game's blend and mask shaders, drawn in their passes with their blend and stencil states (the
        // hosts' pass tables): the alpha masks write the target's alpha, the masked sprites draw by it (where a
        // mask is, or with Reversed where it isn't; Destructive ones also wear the mask away, NonDestructive
        // ones leave it); the stencil masks write 5 (or 14, or 0) where their alpha is half or more, the masked
        // sprites draw where it is 5; Unity's Screen and Hard Light (from a grab of the screen there) as the
        // target times one colour plus another, two passes and no grab. P0 _Amount (the Screen variants).
        public const int AlphaMask = 20, AlphaMasked = 21, AlphaMaskedNonDestructive = 22, AlphaMaskedNonDestructiveReversed = 23,
            AlphaMaskedNonDestructiveScreen = 24, AlphaMaskedNonDestructiveReversedScreen = 25, AlphaMaskedNonDestructiveReversedGlyphs = 26,
            AlphaMaskClearer = 27, StencilMask = 28, StencilMasked = 29, StencilMaskMulti = 30, StencilMaskMultiClearer = 32,
            BlendModeScreen = 33, BlendModeHardLight = 34;
        /// <summary>Cuphead/Sprites/MonochromeNoBlack: P0-3 _MonochromeBase, P4 _Factor, P8-9 texel size (one pass in the pass tables).</summary>
        public const int MonochromeNoBlack = 35;
        /// <summary>The Rum Runners' banded stencil masks: StencilMaskMulti writes 9-14 by its alpha's band, the reversed masked sprite fades across them.</summary>
        public const int StencilMaskedMultiReversed = 31;
        /// <summary>
        /// Shaders that read what was drawn behind them (Unity's GrabPass): Honey, IceCubes, Unity's Overlay blend
        /// mode. RenderList splits the camera's view before such an item, copies the target so far into a
        /// temporary texture (Tex2) and goes on in the same target with what it held. P0 Honey _Amount; IceCubes
        /// P0-5 _Amount, _CutoffMax, _CutoffMin, _CutoffGreen, _CutoffBlue, _BlendValue; P10-11 one over the grab's size.
        /// </summary>
        public const int Honey = 36, IceCubes = 37, BlendModeOverlay = 38;
        public static bool NeedsGrab(int mode) => mode >= Honey && mode <= BlendModeOverlay;
        public const int ParamCount = 12;

        static readonly Dictionary<string, int> passModes = new Dictionary<string, int>
        {
            ["Cuphead/Sprites/AlphaMask"] = AlphaMask, ["Cuphead/Sprites/AlphaMasked"] = AlphaMasked,
            ["Cuphead/Sprites/AlphaMaskedNonDestructive"] = AlphaMaskedNonDestructive,
            ["Cuphead/Sprites/AlphaMaskedNonDestructiveReversed"] = AlphaMaskedNonDestructiveReversed,
            ["Cuphead/Sprites/AlphaMaskedNonDestructiveScreen"] = AlphaMaskedNonDestructiveScreen,
            ["Cuphead/Sprites/AlphaMaskedNonDestructiveReversedScreen"] = AlphaMaskedNonDestructiveReversedScreen,
            ["Cuphead/Sprites/AlphaMaskedNonDestructiveReversedGlyphs"] = AlphaMaskedNonDestructiveReversedGlyphs,
            ["Cuphead/Sprites/AlphaMaskClearer"] = AlphaMaskClearer, ["Cuphead/Sprites/StencilMask"] = StencilMask,
            ["Cuphead/Sprites/StencilMasked"] = StencilMasked, ["Cuphead/Sprites/StencilMaskMulti"] = StencilMaskMulti,
            ["Cuphead/Sprites/StencilMaskMultiClearer"] = StencilMaskMultiClearer,
            ["Sprites/Blend Modes/Screen"] = BlendModeScreen, ["Sprites/Blend Modes/Hard Light"] = BlendModeHardLight,
            ["Sprites/Color/Overlay"] = ColorOverlay, ["Cuphead/Font/Alpha Mask Font"] = AlphaMask,
            ["Cuphead/Sprites/MonochromeNoBlack"] = MonochromeNoBlack,
            ["Cuphead/Sprites/StencilMaskedMultiReversed"] = StencilMaskedMultiReversed,
            ["Cuphead/Sprites/Honey"] = Honey, ["Cuphead/Sprites/IceCubes"] = IceCubes, ["Sprites/Blend Modes/Overlay"] = BlendModeOverlay,
        };

        // ------------------------------------------------------------ shaders

        static readonly Dictionary<Shader, int> modes = new Dictionary<Shader, int>(ReferenceEqualityComparer.Instance as IEqualityComparer<Shader>);

        /// <summary>A shader's draw mode, worked out from its name once.</summary>
        public static int ModeOf(Shader sh)
        {
            if (sh == null) return Sprite;
            if (modes.TryGetValue(sh, out int mode)) return mode;
            string shader = Objects.Of(sh)?.Name ?? "";
            if (shader.Contains("SpriteBlendDynamic/Screen") || shader.EndsWith("/ScreenBlend")) mode = ScreenBlend;
            else if (shader.Contains("ScreenIgnoreColor")) mode = Sprite;
            else if (shader.Contains("SpriteBlendDynamic/Multiply") || shader.EndsWith("/MultiplyBlend")) mode = Multiply;
            else if (shader.Contains("SpriteBlendDynamic/Overlay") || shader.EndsWith("/OverlayBlend")) mode = OverlayBlend;
            else if (shader.Contains("Distance Field")) mode = Sdf;
            else if (shader.EndsWith("Sprites/LightRay")) mode = LightRay;
            else if (shader == "Sprites/KnightGlow") mode = KnightGlow;
            else if (shader == "Sprites/BishopDarkness") mode = Darkness;
            else if (shader == "Cuphead/Sprites/FastBlurSprites") mode = BlurSprite;
            else if (passModes.TryGetValue(shader, out int pm)) mode = pm;
            modes[sh] = mode;
            return mode;
        }

        /// <summary>An item's draw mode: its fixed effect (blits), a sprite mask, or its material's shader's.</summary>
        public static int ModeOf(RenderList.Item it)
        {
            if (it.Effect >= 0) return it.Effect;
            if (it.Kind == "mask") return Mask;
            return ModeOf(MaterialState.Of(it.Material)?.Shader);
        }

        static readonly int outlineColorId = ShaderProps.Id("_OutlineColor"), dimId = ShaderProps.Id("_DimFactor"),
            increaseId = ShaderProps.Id("_OutlineIncreaseFactor"), lightingId = ShaderProps.Id("_LightingVector"), levelId = ShaderProps.Id("_Level"),
            blurAmountId = ShaderProps.Id("_BlurAmount"), blurLerpId = ShaderProps.Id("_BlurLerp"), dullCutoffId = ShaderProps.Id("_DullBlacksCutoff"),
            dullFactorId = ShaderProps.Id("_DullBlacksFactor"), amountId = ShaderProps.Id("_Amount"), monoBaseId = ShaderProps.Id("_MonochromeBase"), factorId = ShaderProps.Id("_Factor"), cutoffMaxId = ShaderProps.Id("_CutoffMax"),
            cutoffMinId = ShaderProps.Id("_CutoffMin"), cutoffGreenId = ShaderProps.Id("_CutoffGreen"), cutoffBlueId = ShaderProps.Id("_CutoffBlue"),
            blendValueId = ShaderProps.Id("_BlendValue"), parameterId = ShaderProps.Id("_Parameter"), redId = ShaderProps.Id("_Red"),
            greenId = ShaderProps.Id("_Green"), blueId = ShaderProps.Id("_Blue"), screenId = ShaderProps.Id("_Screen"), uvTransformId = ShaderProps.Id("_UV_Transform"),
            overlayId = ShaderProps.Id("_Overlay");

        static float F(MaterialState ms, int id, float def) => ms != null && ms.Floats.TryGetValue(id, out float v) ? v : def;
        static Vector4 V(MaterialState ms, int id, Vector4 def) => ms != null && ms.Vectors.TryGetValue(id, out var v) ? v : def;

        /// <summary>A sprite effect's values (modes 8-10, 24-25) from the item's material, into p (ParamCount floats).</summary>
        public static void Params(RenderList.Item it, int mode, float[] p)
        {
            Array.Clear(p, 0, p.Length);
            var ms = MaterialState.Of(it.Material);
            int w = Width(it.Texture), h = Height(it.Texture);
            p[8] = w > 0 ? 1f / w : 0f; p[9] = h > 0 ? 1f / h : 0f;
            if (it.Tex2 is RenderTexture grab) { p[10] = 1f / Math.Max(1, Width(grab)); p[11] = 1f / Math.Max(1, Height(grab)); }
            if (mode == KnightGlow)
            {
                var oc = V(ms, outlineColorId, new Vector4(1, 1, 1, 0.5f));
                var lv = V(ms, lightingId, new Vector4(2, 2, 0, 0));
                p[0] = oc.x; p[1] = oc.y; p[2] = oc.z; p[3] = oc.w;
                p[4] = F(ms, dimId, 0f); p[5] = F(ms, increaseId, 0.3f); p[6] = lv.x; p[7] = lv.y;
            }
            else if (mode == Darkness) p[0] = F(ms, levelId, 0.3f);
            else if (mode == BlurSprite)
            {
                p[0] = F(ms, blurAmountId, 0f); p[1] = F(ms, blurLerpId, 1f);
                p[2] = F(ms, dullCutoffId, 0f); p[3] = F(ms, dullFactorId, 0f);
            }
            else if (mode == AlphaMaskedNonDestructiveScreen || mode == AlphaMaskedNonDestructiveReversedScreen) p[0] = F(ms, amountId, 1f);
            else if (mode == BlendModeHardLight) p[0] = 1f;   // the pass shaders' choice of Hard Light over Screen
            else if (mode == Honey) p[0] = F(ms, amountId, 0.01f);
            else if (mode == IceCubes)
            {
                p[0] = F(ms, amountId, 0.01f); p[1] = F(ms, cutoffMaxId, 1f); p[2] = F(ms, cutoffMinId, 0f);
                p[3] = F(ms, cutoffGreenId, 0f); p[4] = F(ms, cutoffBlueId, 0f); p[5] = F(ms, blendValueId, 0f);
            }
            else if (mode == MonochromeNoBlack)
            {
                var b = V(ms, monoBaseId, Vector4.one);
                p[0] = b.x; p[1] = b.y; p[2] = b.z; p[3] = b.w; p[4] = F(ms, factorId, 0f);
            }
        }

        static readonly HashSet<string> warnedBlits = new HashSet<string>();

        /// <summary>A blit's mode and values from its material as it is now (scripts change it between blits).</summary>
        static int BlitMode(Material mat, int pass, Texture src, float[] p, out Texture tex2)
        {
            Array.Clear(p, 0, p.Length);
            tex2 = null;
            if (mat == null) return Copy;
            var ms = MaterialState.Of(mat);
            string shader = Objects.Of(ms?.Shader)?.Name ?? "";
            int w = Width(src), h = Height(src);
            float tx = w > 0 ? 1f / w : 0f, ty = h > 0 ? 1f / h : 0f;
            var par = V(ms, parameterId, Vector4.zero);
            if (shader == "Hidden/FastBlur" || shader == "Hidden/BlurWithAlpha")
            {
                if (pass == 0) { p[8] = tx; p[9] = ty; return Down4; }
                // 1 and 3 vertical, 2 and 4 horizontal (3 and 4 its SGX variants: the same taps).
                if (pass == 1 || pass == 3) p[1] = ty * par.x; else p[0] = tx * par.x;
                return shader == "Hidden/BlurWithAlpha" ? Blur7Alpha : Blur7;
            }
            if (shader == "Hidden/BlurGamma")
            {
                if (pass == 0) { p[0] = tx * par.x; return Blur7; }
                p[1] = ty * par.x; p[2] = par.z;
                // At the default brightness the power is 1: the plain blur, without three log/exp a pixel.
                return pass == 2 ? Blur7TwoStrip : pass == 3 ? Blur7Bw : par.z == 1f ? Blur7 : Blur7Gamma;
            }
            if (shader == "Hidden/Cuphead/ChromaticAberrationFilmGrain")
            {
                var scr = V(ms, screenId, new Vector4(w, h, 0, 0));
                float sx = scr.x != 0 ? 1f / scr.x : 0f, sy = scr.y != 0 ? 1f / scr.y : 0f;
                var r = V(ms, redId, Vector4.zero); var g = V(ms, greenId, Vector4.zero); var b = V(ms, blueId, Vector4.zero);
                // Pass 1 (two-strip) takes green where it takes blue; pass 2 (black and white) has no fringes.
                if (pass == 1) g = b;
                if (pass != 2)
                {
                    p[0] = r.x * sx; p[1] = r.y * sy; p[2] = g.x * sx; p[3] = g.y * sy; p[4] = b.x * sx; p[5] = b.y * sy;
                }
                var uvt = V(ms, uvTransformId, new Vector4(1, 0, 0, 1));
                p[6] = uvt.x; p[7] = uvt.y; p[8] = uvt.z; p[9] = uvt.w;
                ms.Textures.TryGetValue(overlayId, out tex2);
                return FilmGrain;
            }
            if (warnedBlits.Add(shader + "/" + pass)) Log.Line($"EFFECT not drawn yet: {shader} pass {pass} (copied)");
            return Copy;
        }

        // ------------------------------------------------------------ render textures

        public static int Width(Texture t) => t is RenderTexture rt ? Icalls.UnityEngine_RenderTexture__Internal_GetWidth(rt) : Icalls.TextureWidth(t);
        public static int Height(Texture t) => t is RenderTexture rt ? Icalls.UnityEngine_RenderTexture__Internal_GetHeight(rt) : Icalls.TextureHeight(t);

        static readonly Dictionary<RenderTexture, int> targetIds = new Dictionary<RenderTexture, int>(ReferenceEqualityComparer.Instance as IEqualityComparer<RenderTexture>);

        /// <summary>A render texture's number for the hosts (1 up, kept for its life).</summary>
        public static int TargetId(RenderTexture rt)
        {
            if (rt == null) return 0;
            if (!targetIds.TryGetValue(rt, out int id)) targetIds[rt] = id = targetIds.Count + 1;
            return id;
        }

        sealed class Temp
        {
            public RenderTexture Rt;
            public int W, H, LastFrame;
            public bool InUse;
        }
        static readonly List<Temp> temps = new List<Temp>();

        /// <summary>RenderTexture.GetTemporary: a free one of that size, or a new one (kept for the frames after).</summary>
        public static RenderTexture GetTemporary(int w, int h)
        {
            w = Math.Max(1, w); h = Math.Max(1, h);
            foreach (var t in temps)
                if (!t.InUse && t.W == w && t.H == h) { t.InUse = true; t.LastFrame = Clock.FrameCount; return t.Rt; }
            var rt = Lifecycle.NewManaged<RenderTexture>();
            Objects.Register(rt, new NAsset(), "TempBuffer " + (temps.Count + 1) + " " + w + "x" + h, persistent: true);
            Icalls.UnityEngine_RenderTexture__Internal_SetWidth(rt, w);
            Icalls.UnityEngine_RenderTexture__Internal_SetHeight(rt, h);
            temps.Add(new Temp { Rt = rt, W = w, H = h, InUse = true, LastFrame = Clock.FrameCount });
            return rt;
        }

        public static bool IsTemporary(RenderTexture rt)
        {
            foreach (var t in temps) if (ReferenceEquals(t.Rt, rt)) return true;
            return false;
        }

        public static void ReleaseTemporary(RenderTexture rt)
        {
            foreach (var t in temps) if (ReferenceEquals(t.Rt, rt)) { t.InUse = false; return; }
        }

        /// <summary>After the frame's list: temporaries a script kept are free again (Unity frees them a few frames on).</summary>
        static void EndFrame()
        {
            foreach (var t in temps) t.InUse = false;
        }

        // ------------------------------------------------------------ image effects

        static readonly List<NBehaviour> fxScratch = new List<NBehaviour>();

        /// <summary>The camera's enabled scripts with OnRenderImage, in component order (Unity's chain).</summary>
        public static List<NBehaviour> ImageEffects(NCamera cam)
        {
            fxScratch.Clear();
            var comps = cam.GameObject.Components;
            for (int i = 0; i < comps.Count; i++)
                if (comps[i] is NBehaviour b && !b.Destroyed && b.Enabled && b.Messages?.OnRenderImage != null) fxScratch.Add(b);
            return fxScratch;
        }

        internal static bool Recording;
        static readonly object[] fxArgs = new object[2];
        static readonly HashSet<Type> loggedFx = new HashSet<Type>();

        /// <summary>One script's OnRenderImage(source, destination); its Graphics.Blit calls become blit views.</summary>
        public static void RunImageEffect(NBehaviour b, RenderTexture src, RenderTexture dst)
        {
            int first = RenderList.ViewCount;
            Recording = true;
            fxArgs[0] = src; fxArgs[1] = dst;
            try { b.Messages.OnRenderImage.Invoke(b.Managed, fxArgs); }
            catch (TargetInvocationException e) { Exceptions.Report(e.InnerException ?? e, b, "OnRenderImage"); }
            finally { Recording = false; fxArgs[0] = fxArgs[1] = null; }
            if (loggedFx.Add(b.Managed.GetType())) LogEffect(b);
            HalfRateSequence(b, first);
        }

        static void LogEffect(NBehaviour b)
        {
            var sb = new System.Text.StringBuilder("EFFECT " + b.Managed.GetType().Name + " on " + b.GameObject.Name + ":");
            foreach (var f in b.Managed.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var v = f.GetValue(b.Managed);
                if (v is float || v is int || v is bool || v is Vector2 || v is Vector4 || v is Enum) sb.Append(' ').Append(f.Name).Append('=').Append(v);
            }
            Log.Line(sb.ToString());
        }

        static readonly Dictionary<Type, FieldInfo> sequenceFields = new Dictionary<Type, FieldInfo>();

        /// <summary>
        /// An effect's overlay from a texture sequence (the film grain: 127 frames at 1024x512, a new one every
        /// 25 ms) shows its even frames only, each for two steps: on the console the 64 kept frames are 8 MB at
        /// half size, all 127 would be 16 MB of the 112 MB its textures may use. The grain still changes 20 times
        /// a second (film runs at 24).
        /// </summary>
        static void HalfRateSequence(NBehaviour b, int firstView)
        {
            var type = b.Managed.GetType();
            if (!sequenceFields.TryGetValue(type, out var field))
            {
                field = type.GetField("textures", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && field.FieldType != typeof(Texture2D[])) field = null;
                sequenceFields[type] = field;
            }
            if (field == null || !(field.GetValue(b.Managed) is Texture2D[] seq) || seq.Length < 2) return;
            for (int v = firstView; v < RenderList.ViewCount; v++)
                foreach (var it in RenderList.ViewAt(v).Items)
                {
                    if (it.Tex2 == null) continue;
                    int i = Array.IndexOf(seq, it.Tex2 as Texture2D);
                    if (i > 0 && (i & 1) == 1 && seq[i - 1] != null) it.Tex2 = seq[i - 1];
                }
        }

        /// <summary>Graphics.Blit while an image effect runs: a view of one quad over the target (null: the screen).</summary>
        internal static void Blit(Texture src, RenderTexture dst, Material mat, int pass)
        {
            if (!Recording)
            {
                if (warnedBlits.Add("outside")) Log.Line("EFFECT Graphics.Blit outside OnRenderImage (not drawn)");
                return;
            }
            int w = dst != null ? Width(dst) : Icalls.ScreenWidth, h = dst != null ? Height(dst) : Icalls.ScreenHeight;
            var it = RenderList.AddBlit(dst, w, h);
            it.Texture = src;
            it.Material = mat;
            it.P ??= new float[ParamCount];
            it.Effect = BlitMode(mat, pass, src, it.P, out it.Tex2);
        }

        internal static void BeginFrame() { }
        internal static void FinishFrame() => EndFrame();
    }

    public static partial class Icalls
    {
        public static RenderTexture UnityEngine_RenderTexture__INTERNAL_CALL_GetTemporary_Internal(ref RenderTextureDescriptor desc)
            => Effects.GetTemporary(desc.width, desc.height);
        public static void UnityEngine_RenderTexture__ReleaseTemporary(RenderTexture temp) => Effects.ReleaseTemporary(temp);
        public static void UnityEngine_RenderTexture__Internal_CreateRenderTexture(RenderTexture rt)
        {
            if (Objects.Of(rt) == null) Objects.Register(rt, new NAsset(), "RenderTexture");
        }
        public static void UnityEngine_Graphics__Blit(Texture source, RenderTexture dest) => Effects.Blit(source, dest, null, -1);
        public static void UnityEngine_Graphics__INTERNAL_CALL_Blit(Texture source, RenderTexture dest, ref Vector2 scale, ref Vector2 offset) => Effects.Blit(source, dest, null, -1);
        public static void UnityEngine_Graphics__INTERNAL_CALL_Internal_BlitMaterial(Texture source, RenderTexture dest, Material mat, int pass, bool setRT, ref Vector2 scale, ref Vector2 offset)
            => Effects.Blit(source, dest, mat, pass);
    }
}
