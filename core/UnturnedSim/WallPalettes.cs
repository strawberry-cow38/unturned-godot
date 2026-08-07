using System;
using System.Collections.Generic;
using System.Globalization;

namespace UnturnedSim
{
    /// <summary>One retail building palette. A "material" on these buildings is nothing but this: the models
    /// carry no textures, only a 4x2 image of eight flat colours that the mesh indexes per vertex, so choosing
    /// a material is choosing eight numbers.
    ///
    /// WallTexel and RevealTexel are MEASURED per model -- the texel covering the most vertical area, and the
    /// texel on the strips whose short side equals the wall thickness. They are stored rather than derived
    /// because the buildings disagree: texel 0 is the wall in 44 of 52 and texel 2 the reveal in 47, but the
    /// mall, one apartment, a police station and both ships break it.</summary>
    public struct WallPalette
    {
        public string Name;
        public int[] Rgb;             // eight 0xRRGGBB texels, image row-major
        public int WallTexel;
        public int RevealTexel;
        public float Thickness;       // that model's measured wall thickness

        public int Wall => Rgb[Math.Clamp(WallTexel, 0, 7)];
        public int Reveal => Rgb[Math.Clamp(RevealTexel, 0, 7)];
    }

    /// <summary>Parse of the palette table, kept engine-free so the data can be tested without a running
    /// engine -- which matters here because the numbers are measurements, and a measurement that silently
    /// shifts is the failure mode this table already had once.</summary>
    public static class WallPalettes
    {
        public static List<WallPalette> Parse(IEnumerable<string> lines)
        {
            var list = new List<WallPalette>();
            if (lines == null) return list;
            foreach (var raw in lines)
            {
                if (string.IsNullOrEmpty(raw) || raw[0] == '#' || raw.StartsWith("name\t")) continue;
                var p = raw.Split('\t');
                if (p.Length < 9) continue;
                var pal = new WallPalette { Name = p[0], Rgb = new int[8], RevealTexel = 2, Thickness = WallOpenings.DefaultThickness };
                bool ok = true;
                for (int i = 0; i < 8; i++)
                    if (!int.TryParse(p[i + 1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pal.Rgb[i])) { ok = false; break; }
                if (!ok) continue;
                if (p.Length >= 12)
                {
                    if (int.TryParse(p[9], out var wt)) pal.WallTexel = wt;
                    if (int.TryParse(p[10], out var rt)) pal.RevealTexel = rt;
                    if (float.TryParse(p[11], NumberStyles.Float, CultureInfo.InvariantCulture, out var th)) pal.Thickness = th;
                }
                list.Add(pal);
            }
            return list;
        }

        public static (int R, int G, int B) Split(int rgb) => ((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    }
}
