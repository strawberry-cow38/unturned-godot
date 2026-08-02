using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // The lamp's glow used to be a stand-in disc floating under the fixture, while the bulb the artist actually
    // modelled -- a warm-tan box inside the head -- was drawn with the same grey material as the pole. ObjMesh.SplitLens
    // carves those triangles onto their own surface so the REAL lens is what lights up.
    //
    // Two things here are easy to get quietly wrong and invisible in a screenshot:
    //
    //  1. The split has to be EXHAUSTIVE and DISJOINT. If a lens triangle is left behind in the body it renders grey
    //     underneath the emissive copy and z-fights; if a body triangle is dragged into the lens, a chunk of the pole
    //     glows. Asserting only "the lens has some triangles" would pass in both cases, so this asserts the partition:
    //     counts sum to the original, and NO body triangle samples the lens texel.
    //
    //  2. The extracted bulb is an OPEN box -- 5 quads, not 6. Its top face pointed up into the housing shell, so the
    //     artist deleted it as an interior face nobody could see. That is fine for a grey box and wrong for a lit one:
    //     the hole shows the unlit inside of the far wall. The cap is asserted by WATERTIGHTNESS (every edge shared by
    //     exactly two triangles) rather than by a triangle count, because a count passes just as well if the cap is
    //     stitched in the wrong place.
    public sealed class StreetLightLensSplitTests : GameTest
    {
        public override string Name => "props.streetlight_lens_split";

        // Mirrors ObjMesh's rule: after Load's V-flip the warm palette entry is the u>0.5,v>0.5 quadrant.
        static bool LensUv(Vector2 a, Vector2 b, Vector2 c)
            => a.X > 0.5f && a.Y > 0.5f && b.X > 0.5f && b.Y > 0.5f && c.X > 0.5f && c.Y > 0.5f;

        static int LensTris(ArrayMesh m)
        {
            if (m == null) return 0;
            var u = m.SurfaceGetArrays(0)[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            int n = 0;
            for (int i = 0; i + 2 < u.Length; i += 3) if (LensUv(u[i], u[i + 1], u[i + 2])) n++;
            return n;
        }

        static int TriCount(ArrayMesh m)
            => m == null ? 0 : m.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length / 3;

        public override IEnumerable<Step> Run()
        {
            string dir = ProjectSettings.GlobalizePath("res://content/objects/");
            var src = ObjMesh.Load(dir + "Street_Light_0.obj");
            T.Check("the streetlight prop mesh loads", src != null);
            if (src == null) yield break;

            int srcTris = TriCount(src), srcLens = LensTris(src);
            var (body, lens) = ObjMesh.SplitLens(src);
            T.Check($"the source mesh carries lens-texel geometry to split ({srcLens} tri)", srcLens > 0);
            T.Check("the split yields a body", body != null);
            T.Check("the split yields a lens", lens != null);
            if (body == null || lens == null) yield break;

            // (1) a PARTITION: nothing duplicated, nothing dropped -- modulo the cap triangles added to close the bulb.
            int bodyTris = TriCount(body), lensTris = TriCount(lens);
            T.Check($"no lens geometry is left behind in the body ({LensTris(body)} found)", LensTris(body) == 0);
            T.Check($"the body keeps every non-lens triangle ({bodyTris} = {srcTris} - {srcLens})", bodyTris == srcTris - srcLens);
            T.Check($"the lens is the split geometry plus its cap ({lensTris} >= {srcLens})", lensTris >= srcLens);

            // (2) the cap: the lens must be CLOSED. Every edge shared by exactly two triangles, none by one.
            var edges = new Dictionary<(int, int, int, int, int, int), int>();
            var lv = lens.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            (int, int, int) K(Vector3 p) => ((int)Mathf.Round(p.X * 10000f), (int)Mathf.Round(p.Y * 10000f), (int)Mathf.Round(p.Z * 10000f));
            for (int i = 0; i + 2 < lv.Length; i += 3)
                for (int k = 0; k < 3; k++)
                {
                    var (a, b) = (K(lv[i + k]), K(lv[i + (k + 1) % 3]));
                    var key = a.CompareTo(b) < 0 ? (a.Item1, a.Item2, a.Item3, b.Item1, b.Item2, b.Item3)
                                                 : (b.Item1, b.Item2, b.Item3, a.Item1, a.Item2, a.Item3);
                    edges[key] = edges.TryGetValue(key, out var n) ? n + 1 : 1;
                }
            int openEdges = 0;
            foreach (var kv in edges) if (kv.Value != 2) openEdges++;
            T.Check($"the capped lens is watertight -- no open edge ({openEdges} found, {edges.Count} edges)", openEdges == 0);
            T.Check($"capping added the missing face rather than nothing ({lensTris - srcLens} cap tri)", lensTris > srcLens);

            // (3) the split is CACHED: props are shared, so re-splitting per placement would rebuild a mesh per lamp.
            var again = ObjMesh.SplitLens(src);
            T.Check("splitting the same mesh twice reuses the result", ReferenceEquals(again.Lens, lens) && ReferenceEquals(again.Body, body));
        }
    }

    // The lamp ADOPTS a lens node it does not own: the node stays parented to the prop placement, because that is
    // where it gets the basis that puts it inside the fixture. Reparenting it under the StreetLight (which is
    // TopLevel, world-space, unrotated) would drop that basis and strand the lens beside the pole on any rotated
    // lamp -- which is most of them. So this asserts the lamp drives it WITHOUT stealing it, and that a lamp handed
    // no lens still falls back to its own disc (the --lighttest harness and the other L1 tests build bare lamps).
    public sealed class StreetLightAdoptsLensTests : GameTest
    {
        public override string Name => "props.streetlight_adopts_prop_lens";

        public override IEnumerable<Step> Run()
        {
            var host = new Node3D();
            World.AddChild(host);
            var lens = new MeshInstance3D { Mesh = new BoxMesh() };
            host.AddChild(lens);

            var lamp = StreetLight.Make(new Vector3(0f, 5f, 0f), 5f, lens);
            World.AddChild(lamp);
            yield return Ticks(2);

            T.Check("the lens stays parented to the prop, not the lamp", lens.GetParent() == host);
            T.Check("the lamp dressed it with an emissive material", lens.MaterialOverride is StandardMaterial3D m && m.EmissionEnabled);
            T.Check("...and stopped it casting shadows", lens.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off);

            lamp.SetNight(true); lamp.SetPowered(true);
            yield return Ticks(1);
            T.Check("a lit lamp shows the adopted lens", lens.Visible && lamp.LitPanelForTest);

            lamp.SetPowered(false);
            yield return Ticks(1);
            T.Check("cutting the grid hides it", !lens.Visible && !lamp.LitPanelForTest);

            lamp.SetPowered(true); yield return Ticks(1);
            lamp.SetBroken(true); yield return Ticks(1);
            T.Check("smashing the pole hides it too", !lens.Visible);

            // Fallback: no lens handed in -> the lamp builds its own disc and still has something to light.
            var bare = StreetLight.Make(new Vector3(20f, 5f, 0f), 5f);
            World.AddChild(bare);
            yield return Ticks(2);
            bare.SetNight(true); bare.SetPowered(true);
            yield return Ticks(1);
            T.Check("a lamp with no prop lens falls back to its own panel", bare.LitPanelForTest);
        }
    }
}
