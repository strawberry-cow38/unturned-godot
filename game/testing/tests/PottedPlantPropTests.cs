using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>The four approved potted plants are PLACEABLE PROPS with BILINEAR foliage -- not art on disk,
    /// and not art on disk rendered with the wrong filter (strawberry 2026-10-12, to astraclaw: "bit lower
    /// poly pots, and use bilinear on the foliage textures"; "1, 2, 4, 5 approved").
    ///
    /// ⚠ TWO HALVES THAT BOTH FAIL SILENTLY, which is the whole reason this file exists.
    /// 1. REGISTRATION. astraclaw ships models and cannot push; the guid_mesh.txt row is a separate job and
    ///    when it is skipped NOTHING ERRORS -- every .obj sits happily in content/objects, every art check
    ///    passes, and the prop simply never appears in the palette.
    /// 2. THE FILTER. An OBJ and a PNG cannot encode a texture filter, and EditorObjects' default is
    ///    Nearest, which is right for the game's palette props and wrong for a leaf card. Drop the override
    ///    and the plants still load, still place, still look like plants -- with hard-edged leaves nobody
    ///    notices until master does. So it is asserted, not trusted.
    ///
    /// ⭐ FOUR PROPS, NOT FIVE. Lineup position 3 (the large dark-leaf floor plant) was NOT approved, and a
    /// check that it stayed out is the one that catches a handoff quietly re-adding it.</summary>
    public sealed class PottedPlantPropTests : GameTest
    {
        public override string Name => "props.potted_plants";
        public override double TimeoutSimSeconds => 30;

        static readonly string[] Approved =
            { "Potted_Plant_Table_0", "Potted_Plant_Upright_0", "Potted_Flowers_0", "Potted_Plant_Tall_0" };
        const string Rejected = "Potted_Plant_Floor_0";

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor();
            World.AddChild(ed);
            var objs = new EditorObjects(ed, World, null);
            World.AddChild(objs);
            yield return Ticks(2);

            var catalog = new HashSet<string>();
            foreach (var n in objs.Catalog) catalog.Add(n);

            foreach (var prop in Approved)
            {
                var mesh = ContentProvider.ParseObj($"res://content/objects/{prop}.obj");
                T.Check($"{prop}.obj loads", mesh != null);
                if (mesh != null)
                {
                    var a = mesh.GetAabb();
                    // ⚠ PROPS ARE Z-UP (retail convention), while the SAME author's resource handoffs are
                    // +Y-up. A prop on the wrong axis lies on its side and nothing complains -- and a potted
                    // plant lying down still reads as a potted plant in a thumbnail.
                    T.Check($"...{prop} stands on Z ({a.Size.X:0.00} x {a.Size.Y:0.00} x {a.Size.Z:0.00} m)",
                            a.Size.Z >= a.Size.X * 0.8f && a.Size.Z >= a.Size.Y * 0.8f);
                    T.Check($"...sitting on its base rather than sunk or floating ({a.Position.Z:0.000})",
                            Mathf.Abs(a.Position.Z) < 0.05f);
                    // ⭐ AND IT IS CROSSED QUADS, NOT MODELLED LEAVES -- what master actually asked for.
                    // 158 triangles for a whole prop only happens if the foliage is flat cards; modelled
                    // leaves on an 8-sided pot would be in the thousands.
                    int tris = mesh.GetFaces().Length / 3;
                    T.Check($"...and is still low-poly crossed quads ({tris} tris)", tris > 0 && tris <= 400);
                }

                // THE REGISTRATION -- the half that silently does not happen.
                T.Check($"{prop} is in the editor's prop catalog", catalog.Contains(prop));
            }

            T.Check($"the unapproved {Rejected} did NOT come along", !catalog.Contains(Rejected));

            // THE FILTER OVERRIDE. Place one and read the filter off the material that actually got built,
            // rather than off the json that asked for it -- the json is the request, the material is what
            // the player sees.
            var node = objs.Place(Approved[3], new Vector3(6f, 0f, 6f), Basis.Identity);
            yield return Ticks(2);
            T.Check("placing one returns a node", node != null);
            if (node != null)
            {
                int meshes = 0; StaticBody3D body = null;
                var filters = new List<BaseMaterial3D.TextureFilterEnum>();
                void Walk(Node n)
                {
                    if (n is MeshInstance3D mi && mi.Mesh != null)
                    {
                        meshes++;
                        for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                        {
                            var m = mi.GetActiveMaterial(s) as BaseMaterial3D;
                            if (m?.AlbedoTexture != null) filters.Add(m.TextureFilter);
                        }
                    }
                    if (n is StaticBody3D sb) body ??= sb;
                    foreach (var c in n.GetChildren()) Walk(c);
                }
                Walk(node);
                T.Check($"...with something to look at ({meshes} mesh instance(s))", meshes > 0);
                T.Check("...and something to walk into", body != null);

                bool anyNearest = false, anyLinear = false;
                foreach (var f in filters)
                {
                    if (f == BaseMaterial3D.TextureFilterEnum.Nearest
                     || f == BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps) anyNearest = true;
                    if (f == BaseMaterial3D.TextureFilterEnum.Linear
                     || f == BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps) anyLinear = true;
                }
                // ⚠ BOTH DIRECTIONS. "a linear one exists" would pass on a prop that also built a nearest
                // material for its foliage; what was asked for is that NONE of it is nearest.
                T.Check($"...and its foliage is filtered LINEAR, as the handoff asked ({filters.Count} textured surface(s))",
                        filters.Count > 0 && anyLinear);
                T.Check("...with no nearest-filtered surface left on it", filters.Count > 0 && !anyNearest);
            }

            // ⭐ THE CONTROL THE FILTER CHECK NEEDS: a stock prop that asked for nothing must still be
            // NEAREST. Without this, a build that switched every prop in the game to linear would pass
            // every check above -- the override would be untested and the rest of the game quietly resampled.
            var stock = objs.Place("Fire_Hydrant_0", new Vector3(12f, 0f, 12f), Basis.Identity);
            yield return Ticks(2);
            if (stock != null)
            {
                int stockNearest = 0, stockLinear = 0;
                var seen = new List<string>();
                void Walk2(Node n)
                {
                    if (n is MeshInstance3D mi && mi.Mesh != null)
                        for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                            if (mi.GetActiveMaterial(s) is BaseMaterial3D m && m.AlbedoTexture != null)
                            {
                                bool near = m.TextureFilter == BaseMaterial3D.TextureFilterEnum.Nearest
                                         || m.TextureFilter == BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps;
                                if (near) stockNearest++; else stockLinear++;
                                if (!near) seen.Add($"{mi.Name}#{s}:{m.TextureFilter}");
                            }
                    foreach (var c in n.GetChildren()) Walk2(c);
                }
                Walk2(stock);
                // ⚠ ASSERTED ON THE NEAREST COUNT, NOT ON THE ABSENCE OF LINEAR. Measured, the stock hydrant
                // already carries a non-nearest textured surface of its own -- so "no linear anywhere on a
                // stock prop" is not true today and a control that asserts it is asserting my assumption
                // rather than the behaviour. What this control has to prove is that the DEFAULT still runs:
                // a build that flipped every prop to linear drops this to zero and fails here.
                T.Check($"the control: a prop that asked for nothing still gets NEAREST "
                      + $"({stockNearest} nearest, {stockLinear} not{(seen.Count > 0 ? ": " + string.Join(", ", seen) : "")})",
                        stockNearest > 0);
            }
        }
    }
}
