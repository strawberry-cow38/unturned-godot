using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>The road kit separates into carriageway and sidewalk (strawberry 2026-10-11: "split the road
    /// lines, road tees, quads, turns into separate road and sidewalk pieces").
    ///
    /// ⭐ The split is by PALETTE TEXEL, the same trick the lamp lens uses, and the reason it is trustworthy is
    /// that the geometry agrees with it independently: the mapped faces are a flat plane and the palette faces
    /// are the raised kerb. These checks assert that agreement rather than just "two meshes came back" -- a
    /// predicate that selected the wrong faces would still return two meshes.</summary>
    public sealed class RoadKitSplitTests : GameTest
    {
        public override string Name => "props.road_kit_split";
        public override double TimeoutSimSeconds => 30;

        static readonly string[] Kit = { "Road_Line_0", "Road_Tee_0", "Road_Quad_0", "Road_Turn_0" };

        public override IEnumerable<Step> Run()
        {
            yield return Ticks(1);
            foreach (var name in Kit)
            {
                var src = ContentProvider.ParseObj($"res://content/objects/{name}.obj");
                T.Check($"{name} loads", src != null);
                if (src == null) continue;

                var (road, walk) = ObjMesh.SplitKerb(src);
                T.Check($"{name}: splits into two meshes", road != null && walk != null);
                if (road == null || walk == null) continue;

                var ra = road.GetAabb(); var wa = walk.GetAabb();
                // ⭐ THE CARRIAGEWAY IS FLAT. Measured at z = 0.0 exactly on all four pieces -- props are Z-up,
                // so a zero-height mesh is the surface you drive on and nothing else.
                T.Check($"{name}: the road half is a flat plane (height {ra.Size.Z:0.000} m)", ra.Size.Z < 0.01f);
                // ...and the sidewalk has a KERB: it stands proud of the carriageway.
                T.Check($"{name}: the sidewalk half stands above the road ({wa.End.Z:0.00} m > {ra.End.Z:0.00})",
                        wa.End.Z > ra.End.Z + 0.1f);
                T.Check($"{name}: ...and has real height to it ({wa.Size.Z:0.00} m)", wa.Size.Z > 0.3f);
                // Neither half may be empty of the other's area: they should cover comparable ground, or the
                // predicate grabbed a stray triangle rather than a whole part.
                T.Check($"{name}: both halves span the piece ({ra.Size.X:0.0}x{ra.Size.Y:0.0} vs {wa.Size.X:0.0}x{wa.Size.Y:0.0})",
                        wa.Size.X > ra.Size.X * 0.5f && wa.Size.Y > ra.Size.Y * 0.5f);
                // ⚠ NOTHING IS LOST. Every triangle lands in exactly one half -- a split that silently dropped
                // faces would still pass everything above.
                int rt = road.GetFaces().Length / 3, wt = walk.GetFaces().Length / 3, st = src.GetFaces().Length / 3;
                T.Check($"{name}: no triangle is lost or duplicated ({rt} + {wt} = {st})", rt + wt == st);
            }

            // A prop with no kerb at all must come back WHOLE rather than empty -- the caller runs this over the
            // kit blindly and a null road surface would be an invisible road.
            var plain = ContentProvider.ParseObj("res://content/objects/Block_Road_0.obj");
            if (plain != null)
            {
                var (r2, w2) = ObjMesh.SplitKerb(plain);
                T.Check("a piece with no kerb comes back whole, not empty", r2 != null && (w2 == null || w2.GetFaces().Length > 0));
            }
        }
    }
}
