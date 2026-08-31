// Editor/Brand/MSBrandTokens.cs
// Vendored copy of the Game District Builder Notes design tokens — cream/navy/gold.
// Deliberately duplicated from CodeShield (no shared assembly) so MemoryShield
// stays standalone; a later merge is a folder move plus deleting this file.

using System.IO;
using UnityEditor;
using UnityEngine;

namespace GameDistrict.MemoryShield.Brand
{
    public static class MSBrandTokens
    {
        // ── Colors — straight from the Builder Notes design system ──────────
        public static readonly Color Cream    = new Color32(238, 237, 230, 255); // #EEEDE6 page bg
        public static readonly Color Navy     = new Color32(14,  26,  51,  255); // #0E1A33 primary text
        public static readonly Color Gold     = new Color32(244, 196, 48,  255); // #F4C430 accent
        public static readonly Color WarmGray = new Color32(107, 107, 102, 255); // #6B6B66 muted text
        public static readonly Color Taupe    = new Color32(211, 209, 199, 255); // #D3D1C7 hairlines
        public static readonly Color Ink      = new Color32(61,  61,  58,  255); // #3D3D3A body
        public static readonly Color Sky      = new Color32(133, 183, 235, 255); // #85B7EB ambient info
        public static readonly Color Overdue  = new Color32(192, 57,  43,  255); // #C0392B failing
        public static readonly Color Shipped  = new Color32(111, 167, 111, 255); // #6FA76F passing
        public static readonly Color Amber    = new Color32(200, 140, 20,  255); // medium severity

        public static readonly Color GoldTint    = new Color(244f/255f, 196f/255f, 48f/255f,  0.10f);
        public static readonly Color OverdueTint = new Color(192f/255f, 57f/255f,  43f/255f,  0.10f);
        public static readonly Color ShippedTint = new Color(111f/255f, 167f/255f, 111f/255f, 0.14f);
        public static readonly Color SkyTint     = new Color(133f/255f, 183f/255f, 235f/255f, 0.14f);

        // ── Layout ───────────────────────────────────────────────────────────
        public const float GoldBarWidth = 6f;

        private const string PackageRoot = "Packages/com.gamedistrict.memoryshield/Editor/Brand/";

        // ── Fonts — lazy-loaded from the bundled TTFs ───────────────────────
        private static Font _fraunces, _frauncesItalic, _inter;
        private static bool _fontsAttempted;

        public static Font Fraunces       { get { EnsureFonts(); return _fraunces; } }
        public static Font FrauncesItalic { get { EnsureFonts(); return _frauncesItalic; } }
        public static Font Inter          { get { EnsureFonts(); return _inter; } }

        private static void EnsureFonts()
        {
            if (_fontsAttempted) return;
            _fontsAttempted = true;
            _fraunces       = LoadFont("Fraunces");
            _frauncesItalic = LoadFont("Fraunces-Italic");
            _inter          = LoadFont("Inter");
            // Silent fallback — if fonts are missing the UI degrades to system font.
        }

        private static Font LoadFont(string nameWithoutExt)
        {
            string[] paths =
            {
                PackageRoot + "Fonts/" + nameWithoutExt + ".ttf",
                "Assets/Editor/Brand/Fonts/" + nameWithoutExt + ".ttf",
            };
            foreach (var p in paths)
            {
                if (!File.Exists(p)) continue;
                var f = AssetDatabase.LoadAssetAtPath<Font>(p);
                if (f != null) return f;
            }
            return null;
        }

        // ── Logo ─────────────────────────────────────────────────────────────
        private static Texture2D _logo;
        private static bool _logoAttempted;

        public static Texture2D GDLogo
        {
            get
            {
                if (_logoAttempted) return _logo;
                _logoAttempted = true;
                string[] paths =
                {
                    PackageRoot + "gd-logo.png",
                    "Assets/Editor/Brand/gd-logo.png",
                };
                foreach (var p in paths)
                {
                    if (!File.Exists(p)) continue;
                    var t = AssetDatabase.LoadAssetAtPath<Texture2D>(p);
                    if (t != null) { _logo = t; break; }
                }
                return _logo;
            }
        }
    }
}
