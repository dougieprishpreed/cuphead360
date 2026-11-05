using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Cuphead360.Engine
{
    /// <summary>
    /// What the frame draws that the port can't draw right yet, logged once
    /// each ("GAP ..."), for the forced sweep (tools/sweep_levels.py --forced):
    /// a custom shader drawn as a plain sprite, a texture without a converted
    /// file for the console, a render texture sampled before anything drew it,
    /// a blit the effects don't know (Effects logs those). With Every set, the
    /// player loop builds the render list every that many frames to look.
    /// </summary>
    public static class Gaps
    {
        public static int Every;
        /// <summary>The console's converted textures (work/GameData/ctex): a texture whose file is missing there is reported.</summary>
        public static string ConvertedDir;

        static readonly HashSet<string> seen = new HashSet<string>();
        static readonly Dictionary<Shader, bool> plainChecked = new Dictionary<Shader, bool>(ReferenceEqualityComparer.Instance as IEqualityComparer<Shader>);
        static readonly HashSet<Texture> texChecked = new HashSet<Texture>(ReferenceEqualityComparer.Instance as IEqualityComparer<Texture>);
        static readonly HashSet<RenderTexture> drawn = new HashSet<RenderTexture>(ReferenceEqualityComparer.Instance as IEqualityComparer<RenderTexture>);

        // Shaders a plain sprite draws as they are: Unity's sprite and UI defaults, unlit textures, and
        // the Chalice recolour (it changes nothing unless the unlockable Chalice filter is on).
        static readonly HashSet<string> plainOk = new HashSet<string>
        {
            "Sprites/Default", "UI/Default", "UI/Default Font", "Unlit/Transparent", "Unlit/Texture", "Unlit/Transparent Cutout",
            "Cuphead/Sprites/ChaliceRecolor", "Cuphead/SpriteBlendDynamic/ScreenIgnoreColor", "GUI/Text Shader", "Sprites/Mask",
        };

        static void Report(string what, string where)
        {
            if (!seen.Add(what)) return;
            Log.Line($"GAP {what} (e.g. {where}, scene {Scenes.Active?.Name}, frame {Clock.FrameCount})");
        }

        public static void Scan(List<RenderList.View> views)
        {
            drawn.Clear();
            foreach (var v in views)
            {
                foreach (var it in v.Items)
                {
                    int mode = Effects.ModeOf(it);
                    var sh = MaterialState.Of(it.Material)?.Shader;
                    if (mode == Effects.Sprite && sh != null && it.Kind != "mask" && it.Effect < 0 && !plainChecked.ContainsKey(sh))
                    {
                        string name = Objects.Of(sh)?.Name ?? "";
                        bool ok = plainOk.Contains(name) || name.StartsWith("TextMeshPro") || name.StartsWith("TMPro/");
                        plainChecked[sh] = ok;
                        if (!ok) Report("shader " + name + " drawn as a plain sprite", it.Name);
                    }
                    if (it.Texture is RenderTexture rt)
                    {
                        if (!drawn.Contains(rt)) Report("render texture " + Objects.Of(rt)?.Name + " sampled before anything drew it this frame", it.Name);
                    }
                    else if (it.Texture != null && ConvertedDir != null && texChecked.Add(it.Texture)) CheckConverted(it.Texture, it.Name);
                    if (it.Tex2 != null && !(it.Tex2 is RenderTexture) && ConvertedDir != null && texChecked.Add(it.Tex2)) CheckConverted(it.Tex2, it.Name);
                }
                if (v.Target != null) drawn.Add(v.Target);
            }
        }

        static void CheckConverted(Texture t, string where)
        {
            if (RuntimePixels.Get(t) != null) return;   // made at run time: handed to the host as pixels
            var src = (Objects.Of(t) as NAsset)?.Source;
            if (src == null) return;
            string k = src.File.Path.Replace('/', '_');
            if (k.EndsWith(".chob")) k = k.Substring(0, k.Length - 5);
            k += "_" + IdText.Of(src.PathId);
            if (!File.Exists(Path.Combine(ConvertedDir, k + ".ctx"))) Report("texture " + k + " not converted for the console", where);
        }
    }
}
