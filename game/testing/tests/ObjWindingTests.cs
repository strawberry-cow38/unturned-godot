using Godot;
using System.Collections.Generic;
using System.Globalization;

namespace UnturnedGodot.Testing
{
    // THE CROSS-FAMILY TEST I OWED MASTER, for the bug I shipped to them twice in one day.
    //
    // ContentProvider.ParseObj serves .txt meshes authored under TWO OPPOSITE winding conventions, and nothing
    // in a file says which one it is:
    //   - retail vehicle bodies: ~0% of triangles wound opposite their authored normal
    //   - ripped items (content/items, 3686 files): ~98%
    //   - generated fluid art (content/fluid): 100%
    // Godot treats clockwise-from-the-camera as the FRONT face, so a triangle is front-facing when its
    // right-hand-rule normal OPPOSES the authored one. The loader therefore has to decide per triangle.
    //
    // I got there in three tries and shipped two of them:
    //   952f5884 reversed EVERY triangle. Fixed the vehicles, would have turned every fluid device inside out
    //            -- tinyclaw caught it before it reached master.
    //   c5aa8ec8 went per-triangle but aimed at "winding AGREES with the normal". Fixed the vehicles again and
    //            broke every dropped item. Master: "nope they are dark."
    //   3ce1c7d5 the target is OPPOSES.
    //
    // ⭐ WHY NOTHING CAUGHT IT: I verified with a JEEP RENDER, and a jeep looks identical under all three rules,
    // because on a family that is 0% opposed "reverse it" and "make it agree" are the SAME operation. The rules
    // only disagree ACROSS families. A single-family check cannot test a rule defined by how families differ --
    // so this test reads all three and fails unless it has at least two conventions in front of it.
    //
    // 952f5884 fails this on items+fluid. c5aa8ec8 fails it on all three. Both were checked against the
    // assertion below before it was written, which is the only reason to trust it.
    public class ObjWindingCrossFamily : GameTest
    {
        public override string Name => "obj.winding_cross_family";

        // A file, parsed the plain way: positions, normals, and the per-corner indices. Deliberately NOT
        // ContentProvider's parser -- measuring the input with the code under test would measure nothing.
        static bool ReadObj(string path, out List<Vector3> v, out List<Vector3> n, out List<int> fv, out List<int> fn)
        {
            v = new List<Vector3>(); n = new List<Vector3>(); fv = new List<int>(); fn = new List<int>();
            if (!System.IO.File.Exists(path)) return false;
            var ci = CultureInfo.InvariantCulture;
            foreach (var raw in System.IO.File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length < 2 || line[0] == '#') continue;
                var t = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (t[0] == "v" && t.Length >= 4)
                    v.Add(new Vector3(float.Parse(t[1], ci), float.Parse(t[2], ci), float.Parse(t[3], ci)));
                else if (t[0] == "vn" && t.Length >= 4)
                    n.Add(new Vector3(float.Parse(t[1], ci), float.Parse(t[2], ci), float.Parse(t[3], ci)));
                else if (t[0] == "f")
                    // The loader takes the FIRST THREE corners of a face and no more (ContentProvider:245,
                    // `for i = 1; i <= 3`). Mirrored exactly rather than improved on: a test that triangulates
                    // quads the loader ignores would measure triangles the loader never emits.
                    for (int k = 1; k <= 3 && k < t.Length; k++)
                    {
                        var p = t[k].Split('/');
                        fv.Add(int.Parse(p[0], ci) - 1);
                        fn.Add(p.Length > 2 && p[2].Length > 0 ? int.Parse(p[2], ci) - 1 : -1);
                    }
            }
            return fv.Count >= 3;
        }

        /// <summary>What fraction of this file's triangles are wound OPPOSITE the normal the file gives them.
        /// -1 when the file carries no usable normals at all (nothing to measure, and nothing the loader can
        /// decide from either -- those keep file order by design).</summary>
        static float OpposedFractionOfFile(string path)
        {
            if (!ReadObj(path, out var v, out var n, out var fv, out var fn)) return -1f;
            int opposed = 0, measured = 0;
            for (int i = 0; i + 2 < fv.Count; i += 3)
            {
                if (fn[i] < 0 || fn[i] >= n.Count) continue;
                Vector3 a = v[fv[i]], b = v[fv[i + 1]], c = v[fv[i + 2]];
                Vector3 geo = (b - a).Cross(c - a);
                if (geo.LengthSquared() <= 1e-20f) continue;
                Vector3 authored = n[fn[i]];
                for (int k = 1; k < 3; k++) if (fn[i + k] >= 0 && fn[i + k] < n.Count) authored += n[fn[i + k]];
                float d = geo.Dot(authored);
                if (d == 0f) continue;   // see PerpendicularNormals below
                measured++;
                if (d < 0f) opposed++;
            }
            return measured == 0 ? -1f : (float)opposed / measured;
        }

        // ⚠ PERPENDICULAR AUTHORED NORMALS, and why they are skipped rather than counted as failures. Item 10
        // has 4 triangles out of 127 whose summed authored normal is exactly perpendicular to the triangle's own
        // plane -- dot is 0.0, not nearly 0. Such a normal says nothing about which side is outward, so the
        // loader leaves those in file order (TriangleNeedsReverse returns false) and there is no right answer to
        // assert. Counting them made this test read 96.85% on CORRECT code, which is worse than no test: a
        // failing check on a fixed bug sends the next person to re-break the thing that was just fixed.

        /// <summary>...and the same measurement taken on what ParseObj actually EMITTED. This is the side that
        /// has to come out the same for every family, whatever the file said.</summary>
        static float OpposedFractionOfMesh(ArrayMesh m)
        {
            if (m == null || m.GetSurfaceCount() == 0) return -1f;
            var arr = m.SurfaceGetArrays(0);
            var verts = arr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var norms = arr[(int)Mesh.ArrayType.Normal].AsVector3Array();
            if (verts.Length < 3 || norms.Length != verts.Length) return -1f;
            var idxVar = arr[(int)Mesh.ArrayType.Index];
            int[] idx = idxVar.VariantType == Variant.Type.Nil ? null : idxVar.AsInt32Array();
            int tris = (idx?.Length ?? verts.Length) / 3;
            int opposed = 0, measured = 0;
            for (int i = 0; i < tris; i++)
            {
                int i0 = idx != null ? idx[i * 3] : i * 3, i1 = idx != null ? idx[i * 3 + 1] : i * 3 + 1, i2 = idx != null ? idx[i * 3 + 2] : i * 3 + 2;
                Vector3 geo = (verts[i1] - verts[i0]).Cross(verts[i2] - verts[i0]);
                if (geo.LengthSquared() <= 1e-20f) continue;
                Vector3 authored = norms[i0] + norms[i1] + norms[i2];
                if (authored.LengthSquared() <= 1e-12f) continue;
                float d = geo.Dot(authored);
                if (d == 0f) continue;   // perpendicular authored normal -- unanswerable, see above
                measured++;
                if (d < 0f) opposed++;
            }
            return measured == 0 ? -1f : (float)opposed / measured;
        }

        static string Res(string rel) => ProjectSettings.GlobalizePath("res://content/" + rel);

        // One representative per family. Chosen by DIRECTORY rather than by name where a directory exists, so a
        // re-rip that renames files does not quietly drop a family out of the test.
        static string FirstIn(string dir)
        {
            string d = ProjectSettings.GlobalizePath("res://content/" + dir);
            if (!System.IO.Directory.Exists(d)) return null;
            var files = System.IO.Directory.GetFiles(d, "*.txt");
            System.Array.Sort(files);
            foreach (var f in files) if (new System.IO.FileInfo(f).Length > 4096) return f;   // skip stubs: a 12-triangle file measures nothing
            return files.Length > 0 ? files[0] : null;
        }

        public override IEnumerable<Step> Run()
        {
            var families = new List<(string name, string path)>();
            foreach (var veh in new[] { "ambulance_body.txt", "jeep_body.txt", "offroader_body.txt" })
                if (System.IO.File.Exists(Res(veh))) { families.Add(("retail vehicle", Res(veh))); break; }
            var item = FirstIn("items"); if (item != null) families.Add(("ripped item", item));
            var fluid = FirstIn("fluid"); if (fluid != null) families.Add(("generated fluid art", fluid));

            var conventions = new List<float>();
            foreach (var (name, path) in families)
            {
                float inFrac = OpposedFractionOfFile(path);
                T.Check($"{name}: {System.IO.Path.GetFileName(path)} has measurable normals (in={inFrac:P0})", inFrac >= 0f);
                if (inFrac >= 0f) conventions.Add(inFrac);
            }

            // ⭐ THE CONTROL, and the reason this test exists at all. Two families whose files agree about winding
            // cannot distinguish "reverse it" from "make it agree" -- that is precisely the blind spot that let me
            // ship the rule backwards after checking it against a jeep. If the content on this machine no longer
            // spans conventions, this test has stopped being able to fail for the right reason, and it says so
            // instead of passing quietly.
            bool spans = false;
            for (int i = 0; i < conventions.Count && !spans; i++)
                for (int j = i + 1; j < conventions.Count && !spans; j++)
                    if (Mathf.Abs(conventions[i] - conventions[j]) > 0.5f) spans = true;
            T.Check($"the sample spans OPPOSITE conventions ({string.Join(", ", conventions.ConvertAll(c => c.ToString("P0")))}) "
                    + "-- without this the assertion below is vacuous", spans);

            // ...and the assertion itself: whatever the file's convention, the loader emits triangles whose
            // winding OPPOSES the authored normal, because Godot treats clockwise-from-camera as the front face.
            foreach (var (name, path) in families)
            {
                var mesh = ContentProvider.ParseObj(path);
                yield return Ticks(1);
                float outFrac = OpposedFractionOfMesh(mesh);
                // ALL of them, not most: every triangle whose normal can answer the question must come out
                // opposed. Measured against all three rules before this line was written --
                //   OPPOSES (shipping): 100 / 100 / 100
                //   AGREES  (c5aa8ec8, broke dropped items): 0 / 0 / 0
                //   REVERSE EVERYTHING (952f5884): 100 / 0 / 0  <- only the non-vehicle families catch it,
                //                                                  which is this test's entire reason to exist
                T.Check($"{name}: ParseObj emitted {outFrac:P1} opposed -- must be ALL of them "
                        + $"(file was {OpposedFractionOfFile(path):P0})", outFrac >= 0.999f);
            }
            yield break;
        }
    }
}
