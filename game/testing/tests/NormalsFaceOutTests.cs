using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>Lighting from the wrong side (strawberry 2026-10-04: "the waving flag models appear dark", "animals are
    /// appearing dark", "normals bug is applying to 3p model too"). Two faults with one root: Godot's own back-face
    /// normal flip.
    ///
    /// A material that draws back faces -- cull_disabled OR cull_front, built-in or custom shader -- gets
    /// DO_SIDE_CHECK, which negates the normal of every back-facing fragment before fragment() runs. So:
    ///  1. A shader that ALSO does `NORMAL = FRONT_FACING ? NORMAL : -NORMAL` flips it twice: every back face is lit
    ///     from the wrong side (the dark side of a flag, a guard rail seen from behind).
    ///  2. A mesh wound so its OUTER surface is the back face, drawn with cull_front to make it visible, has every
    ///     outward normal turned inward: the whole body is lit from inside (the rigs, all of them).
    ///
    /// Both are checked against the thing that decides them, not against a render: the built mesh's winding against
    /// its own normals, the material's cull mode, and the shader source. Godot's front face is CLOCKWISE seen from the
    /// camera, so a triangle (a,b,c) faces the way its normal points when cross(c-a, b-a) agrees with it.</summary>
    public sealed class NormalsFaceOutTests : GameTest
    {
        public override string Name => "render.normals_face_out";
        public override double TimeoutSimSeconds => 30;

        static string ReadSrc(string rel)
        {
            foreach (var c in new[] { "res://" + rel, "res://game/" + rel })
            {
                string p = ProjectSettings.GlobalizePath(c);
                if (System.IO.File.Exists(p)) return System.IO.File.ReadAllText(p);
            }
            return "";
        }

        /// <summary>Whether the shader's render_mode line (comments stripped) names `mode` -- not the whole source, whose
        /// comments are allowed to talk about the mode it no longer uses.</summary>
        static bool RenderModeHas(string code, string mode)
        {
            foreach (var line in code.Split('\n'))
            {
                string c = line.Split("//")[0].Trim();
                if (c.StartsWith("render_mode") && c.Contains(mode)) return true;
            }
            return false;
        }

        /// <summary>(triangles counted, share whose Godot front face is on the side the authored normal points to).</summary>
        static (int tris, float frontWithNormal) Winding(Mesh mesh)
        {
            var a = ((ArrayMesh)mesh).SurfaceGetArrays(0);
            var v = a[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var n = a[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var ix = a[(int)Mesh.ArrayType.Index].AsInt32Array();
            int agree = 0, counted = 0;
            for (int t = 0; t + 2 < ix.Length; t += 3)
            {
                int i0 = ix[t], i1 = ix[t + 1], i2 = ix[t + 2];
                var front = (v[i2] - v[i0]).Cross(v[i1] - v[i0]);
                var nrm = n[i0] + n[i1] + n[i2];
                float d = front.Dot(nrm);
                if (d == 0f) continue;
                counted++;
                if (d > 0f) agree++;
            }
            return (counted, counted == 0 ? 0f : (float)agree / counted);
        }

        public override IEnumerable<Step> Run()
        {
            yield return Ticks(1);

            // ---- 1. EVERY RIG IS BUILT OUTWARD-FACING AND CULLS BACK. Measured before the fix: 0% on all of them.
            var rigs = new List<(string label, string json, bool arms, string tex)>
            {
                ("human body (clothes shader)", "res://content/rig.json", false, null),
                ("1P arms (clothes shader)", "res://content/rig.json", true, null),
            };
            foreach (var k in AnimalCatalog.All)
                rigs.Add(($"{k.Rig} (atlas material)", $"res://content/{k.Rig}_rig.json", false, $"res://content/objects/{k.Tex}"));
            foreach (var (label, json, arms, tex) in rigs)
            {
                var rc = RiggedCharacter.Build(json, Colors.White, arms, tex, null);
                T.Check($"{label}: built", rc?.Body?.Mesh != null);
                if (rc?.Body?.Mesh == null) continue;
                World.AddChild(rc);
                var (tris, share) = Winding(rc.Body.Mesh);
                // The count is printed beside the verdict: an empty mesh passes any "all of them" test.
                T.Check($"{label}: {tris} tris, {share:P1} face the way their normal points (want >= 99%)", tris > 50 && share >= 0.99f);
                var mat = rc.Body.MaterialOverride;
                bool cullsFront = mat is StandardMaterial3D sm ? sm.CullMode == BaseMaterial3D.CullModeEnum.Front
                                : mat is ShaderMaterial sh && sh.Shader != null && RenderModeHas(sh.Shader.Code, "cull_front");
                T.Check($"{label}: material does not cull_front (it would switch the side check on and invert the normals)", mat != null && !cullsFront);
            }

            // ---- 2. NO SHADER FLIPS THE BACK-FACE NORMAL BY HAND. Godot already did it; a second flip undoes it.
            foreach (var sh in new[] { "flag", "wind_sway", "wet_surface", "rain_glass", "vehicle_paint" })
            {
                string src = ReadSrc($"content/{sh}.gdshader");
                T.Check($"{sh}.gdshader found", src.Length > 0);
                bool flips = false;
                foreach (var line in src.Split('\n'))
                {
                    string code = line.Split("//")[0];
                    if (code.Contains("FRONT_FACING") && code.Contains("NORMAL")) flips = true;
                }
                T.Check($"{sh}.gdshader does not re-flip NORMAL by FRONT_FACING", !flips);
            }

            // ---- 3. THE FLAG RIPPLE INTEGRATES ITS RATE. `TIME * (2 + 4*wind)` with `wind` re-set 20x a second
            // jumped the wave by TIME * 4 * dwind on every update -- the flag jitter.
            string flag = ReadSrc("content/flag.gdshader");
            bool scalesClock = false;
            foreach (var line in flag.Split('\n'))
            {
                string code = line.Split("//")[0];
                if (code.Contains("TIME")) scalesClock = true;
            }
            T.Check("flag.gdshader takes its phase from the integrated wind_vec.w", flag.Contains("wind_vec.w"));
            T.Check("flag.gdshader does not drive the wave off TIME", !scalesClock);
        }
    }
}
