using System;
using UnityEngine;

namespace Cuphead360.Engine
{
    public static partial class Icalls
    {
        // TextAsset contents are m_Script: raw bytes (converter v2 writes them
        // as bytes; older data has a string with the same UTF-8 bytes). Unity
        // decodes `text` as UTF-8 and keeps a BOM character if present.
        static byte[] TextAssetBytes(TextAsset self)
        {
            var v = Tree.Get(AssetSource(self)?.Tree, "m_Script");
            return v switch
            {
                byte[] b => b,
                string s => System.Text.Encoding.UTF8.GetBytes(s),
                _ => null,
            };
        }

        public static string UnityEngine_TextAsset__get_text(TextAsset self)
        {
            var b = TextAssetBytes(self);
            return b == null ? null : new System.Text.UTF8Encoding(false).GetString(b);
        }

        public static byte[] UnityEngine_TextAsset__get_bytes(TextAsset self) => TextAssetBytes(self) ?? new byte[0];

        // new TextAsset() from script: a run-time asset with no contents.
        public static void UnityEngine_TextAsset__Internal_CreateTextAsset(TextAsset @mono)
        {
            Objects.Register(@mono, new NAsset(), "", persistent: false);
        }
    }
}
