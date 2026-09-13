using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // THE CCTV IS TWO PIECES (strawberry 2026-09-13: "split the CCTV camera prop into two pieces. the camera
    // body and the arm").
    //
    // ⚠ THE TWO RULES THAT WOULD NORMALLY WORK BOTH FAIL HERE, which is why this is asserted rather than
    // eyeballed. Camera_0's texture is a 2x1 palette, so the lamp trick -- split on the texel -- looks right
    // until you measure it: the dark texel is TWO faces, and it is the LENS. And the rip is 19 disconnected
    // quads welded to nothing, so there is no "arm object" to pick out either. The seam is a thin band in X and
    // nothing else finds it.
    public sealed class CameraSplitTests : GameTest
    {
        public override string Name => "props.camera_split";

        static int Tris(Mesh m)
        {
            if (m == null || m.GetSurfaceCount() < 1) return 0;
            var a = m.SurfaceGetArrays(0);
            return a[(int)Mesh.ArrayType.Vertex].VariantType == Variant.Type.Nil
                ? 0 : ((Vector3[])a[(int)Mesh.ArrayType.Vertex]).Length / 3;
        }
        static (Vector3 min, Vector3 max) Bounds(Mesh m)
        {
            var v = (Vector3[])m.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex];
            Vector3 lo = v[0], hi = v[0];
            foreach (var p in v) { lo = lo.Min(p); hi = hi.Max(p); }
            return (lo, hi);
        }

        public override IEnumerable<Step> Run()
        {
            var src = ObjMesh.Load("res://content/objects/Camera_0.obj");
            T.Check("Camera_0 loads", src != null && Tris(src) > 0);
            if (src == null) yield break;
            int total = Tris(src);

            var (body, arm) = ObjMesh.SplitCameraArm(src);
            T.Check($"the split produces BOTH pieces (body={Tris(body)} arm={Tris(arm ?? null)} of {total})",
                    body != null && arm != null && Tris(body) > 0 && Tris(arm) > 0);
            if (body == null || arm == null) yield break;

            // ⭐ NOTHING IS LOST. A split that drops faces renders as a camera with a hole in it, and a split
            // that duplicates them wastes draw. Either is invisible next to "two meshes exist".
            T.Check($"every triangle lands in exactly one half ({Tris(body)} + {Tris(arm)} = {total})",
                    Tris(body) + Tris(arm) == total);

            // The ARM is the minority and it is a THIN BAR -- that is what makes it the arm rather than a slice
            // of housing. Measured off the real mesh: 0.082 wide against the housing's 0.582.
            var (alo, ahi) = Bounds(arm);
            var (blo, bhi) = Bounds(body);
            T.Check($"the arm is the smaller half ({Tris(arm)} vs {Tris(body)})", Tris(arm) < Tris(body));
            T.Check($"...and is a thin bar in X ({ahi.X - alo.X:0.000} m wide)", ahi.X - alo.X < 0.15f);
            T.Check($"...inside the measured band ({alo.X:0.000}..{ahi.X:0.000})",
                    alo.X >= ObjMesh.CameraArmMinX - 0.001f && ahi.X <= ObjMesh.CameraArmMaxX + 0.001f);
            T.Check($"the body is the wide half ({bhi.X - blo.X:0.000} m)", bhi.X - blo.X > 0.4f);

            // ⭐ THE HALF THAT SEES IS THE HALF THAT MOVES. The lens must stay on the BODY -- a split that put it
            // on the arm would pass every size check above and leave the camera unable to look anywhere.
            // The lens is the dark palette texel, which sits at u > 0.5 after the loader's V-flip.
            var bu = (Vector2[])body.SurfaceGetArrays(0)[(int)Mesh.ArrayType.TexUV];
            var au = (Vector2[])arm.SurfaceGetArrays(0)[(int)Mesh.ArrayType.TexUV];
            int bodyDark = 0, armDark = 0;
            foreach (var u in bu) if (u.X > 0.5f) bodyDark++;
            foreach (var u in au) if (u.X > 0.5f) armDark++;
            T.Check($"the LENS stays with the body ({bodyDark} dark verts on the body, {armDark} on the arm)",
                    bodyDark > 0 && armDark == 0);
            yield break;
        }
    }
}
