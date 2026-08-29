using Godot;
using System.Collections.Generic;
using UnturnedSim;

namespace UnturnedGodot
{
    // "Material" on a retail building is just a choice of PALETTE. There are no textures on these models --
    // every building texture is a 4x2 image, eight flat colours, and each vertex indexes one texel. So the
    // editor's material id selects one of the 52 distinct palettes sampled from the retail buildings.
    //
    // Which texel is the wall and which is the reveal is measured per model, not assumed, because retail is
    // not consistent: texel 0 walls 44 of the 52 and texel 2 lines 47, but the mall, an apartment, a police
    // station and both ships use something else.
    public static class WallMaterials
    {
        public sealed class Mat
        {
            public string Name;
            public Color[] Texels = new Color[8];
            // Measured off each model, not assumed: texel 0 is the wall in 44 of the 52 buildings and texel 2
            // the reveal in 47, but Mall/Apartment_2/Police_1/Ship break both, so the roles are stored per
            // material rather than hardcoded.
            public int WallTexel, RevealTexel = 2;
            /// <summary>Texel the roof is painted in, -1 if this model has no sloped roof to measure one
            /// from. A roof is NOT the wall colour, and a drawn roof used to come out as one.</summary>
            public int RoofTexel = -1;
            public float Thickness = WallOpenings.DefaultThickness;   // that model's measured wall thickness
            public Color Wall => Texels[Mathf.Clamp(WallTexel, 0, 7)];
            public Color Reveal => Texels[Mathf.Clamp(RevealTexel, 0, 7)];
        }

        static List<Mat> _all;
        public static IReadOnlyList<Mat> All => _all ??= Load();

        /// <summary>The raw palette rows, kept alongside the resolved Mats so the combo browser can work off
        /// the same eight texels the renderer samples. Rebuilding them from Mat would mean converting Color
        /// back to 0xRRGGBB and rounding twice, which is how two views of "the same" palette drift apart.
        /// Populated by Load(); empty only when the table is missing entirely.</summary>
        static List<WallPalette> _palettes = new();
        public static IReadOnlyList<WallPalette> Palettes { get { _ = All; return _palettes; } }

        static List<WallCombos.Combo> _combos;
        /// <summary>Every distinct (palette, texel) swatch, for the editor's material browser.</summary>
        public static IReadOnlyList<WallCombos.Combo> Combos => _combos ??= WallCombos.All(Palettes);
        public static int Count => All.Count;
        public static Mat At(int id) => All.Count == 0 ? Fallback() : All[Mathf.PosMod(id, All.Count)];

        static Mat Fallback()
        {
            var m = new Mat { Name = "fallback" };
            for (int i = 0; i < 8; i++) m.Texels[i] = new Color(0.6f, 0.55f, 0.48f);
            return m;
        }

        // GlobalizePath + System.IO, matching how sights.tsv is read. A .tsv has no .import sidecar, so it is
        // not a resource -- ResourceLoader hands back null for these and the failure is silent.
        // GlobalizePath + System.IO, matching how sights.tsv is read. A .tsv has no .import sidecar, so it is
        // not a resource -- ResourceLoader hands back null for these and the failure is silent.
        static List<Mat> Load()
        {
            var list = new List<Mat>();
            string path = ProjectSettings.GlobalizePath("res://content/wall_palettes.tsv");
            if (!System.IO.File.Exists(path))
            {
                GD.PrintErr($"[walls] no wall_palettes.tsv at {path}; falling back to one flat colour");
                return list;
            }
            var parsed = WallPalettes.Parse(System.IO.File.ReadAllLines(path));
            _palettes = parsed;
            foreach (var pal in parsed)
            {
                var m = new Mat { Name = pal.Name, WallTexel = pal.WallTexel, RevealTexel = pal.RevealTexel,
                                  RoofTexel = pal.RoofTexel, Thickness = pal.Thickness };
                for (int i = 0; i < 8; i++)
                {
                    var (r, g, b) = WallPalettes.Split(pal.Rgb[i]);
                    m.Texels[i] = new Color(r / 255f, g / 255f, b / 255f);
                }
                list.Add(m);
            }
            return list;
        }
    }
}
