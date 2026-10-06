using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Testing
{
    /// <summary>The power-line wire rig: where the anchors land, what a span refuses, and that spans survive the
    /// pole list being rebuilt underneath them.
    ///
    /// ⭐ THE ANCHOR CHECK IS THE POINT OF THIS FILE. The four connection points are constants measured off the
    /// mesh, and the one thing that would make them silently useless is a sign error -- Z negated, and all four
    /// wire points sit 7.3 m UNDERGROUND while the build stays green and the pole still renders perfectly. I
    /// nearly shipped exactly that, because the remembered rule (map placements negate Z) is true of the ABANDONED
    /// loader convention. So the assertion is on the WORLD HEIGHT of the anchors under the real placement basis,
    /// which is the thing that has to be true and cannot be satisfied by a wrong sign.
    ///
    /// ⚠ The editor tool itself is click-driven and cannot be headless-tested; what it does to the FIELD can be,
    /// so everything the tool calls is asserted here instead.</summary>
    public class PowerLineWires : GameTest
    {
        public override string Name => "world.powerline_wires";

        /// <summary>PEI's own placement basis for this prop: ex=270 stands it up, 180-ey is the yaw.</summary>
        static Transform3D PoleAt(Vector3 pos, float ey)
        {
            var rot = new Basis(new Vector3(0, 1, 0), Mathf.DegToRad(180f - ey))
                    * new Basis(new Vector3(1, 0, 0), Mathf.DegToRad(270f))
                    * new Basis(new Vector3(0, 0, 1), 0f);
            return new Transform3D(rot, pos);
        }

        public override IEnumerable<Step> Run()
        {
            var field = new PowerLineField();
            World.AddChild(field);
            yield return Ticks(1);

            // ---- ANCHORS: four of them, up in the air, spread across the crossarms -------------------------
            int p0 = field.AddPole(PoleAt(Vector3.Zero, 332f));
            int p1 = field.AddPole(PoleAt(new Vector3(34f, 0f, 0f), 304f));   // a DIFFERENT yaw on purpose
            var a = new Vector3[4];
            field.AnchorsWorld(p0, a);

            T.Check("four anchors", PowerLineField.AnchorsLocal.Length == 4);
            // The two crossarms must be far enough apart to read as two runs rather than one thick line (master:
            // "the lower wire set should connect a lil lower"). 0.39 m was too close; the lower pair now hangs off
            // the measured BOTTOM of its insulator.
            float upperZ = PowerLineField.AnchorsLocal[0].Z, lowerZ = PowerLineField.AnchorsLocal[3].Z;
            T.Check($"the lower run hangs clear of the upper one ({upperZ - lowerZ:0.000} m of separation)",
                    upperZ - lowerZ > 0.6f);
            float lowest = Mathf.Inf, highest = -Mathf.Inf;
            foreach (var w in a) { lowest = Mathf.Min(lowest, w.Y); highest = Mathf.Max(highest, w.Y); }
            GD.Print($"[powerline-test] anchor heights {lowest:0.00}..{highest:0.00} m above the pole's base");
            // ⭐ THE SIGN CHECK. The pole is 8 m and the pads sit at 6.9 and 7.3, so every anchor must be well up
            // in the air. A Z-negated constant puts them at -6.9 and -7.3 and this fails by 14 metres.
            T.Check($"every anchor is up the pole, not under it ({lowest:0.00}..{highest:0.00} m)",
                    lowest > 6.0f && highest < 8.0f);
            // The two crossarms are at different heights, which is what stops the wires from being coplanar.
            T.Check($"the two crossarms sit at different heights ({highest - lowest:0.000} m apart)",
                    highest - lowest > 0.2f);
            // And they are spread sideways, not stacked on the pole's axis.
            float widest = 0f;
            for (int i = 0; i < 4; i++)
                for (int j = i + 1; j < 4; j++)
                    widest = Mathf.Max(widest, new Vector2(a[i].X - a[j].X, a[i].Z - a[j].Z).Length());
            T.Check($"the anchors are spread across the crossarms ({widest:0.00} m apart at the widest)", widest > 2.5f);

            // ---- SPANS: what gets refused ---------------------------------------------------------------
            T.Check("a pole cannot be wired to itself", !field.Connect(p0, p0, out _));
            T.Check("a real span connects", field.Connect(p0, p1, out _));
            T.Check("the same pair cannot be wired twice", !field.Connect(p0, p1, out _));
            T.Check("...nor in the other order -- a span is unordered", !field.Connect(p1, p0, out _));
            int far = field.AddPole(PoleAt(new Vector3(4000f, 0f, 0f), 0f));
            T.Check("a span longer than the limit is refused", !field.Connect(p0, far, out string whyFar));
            T.Check($"...and says why (\"{whyFar}\")", whyFar != null && whyFar.Contains("far"));
            T.Check("one span so far", field.SpanCount == 1);

            // ---- THE MESH actually gets built ------------------------------------------------------------
            field.Rebuild();
            yield return Ticks(1);
            var wires = field.GetNodeOrNull<MeshInstance3D>("Wires");
            T.Check("the wire mesh exists", wires != null && wires.Mesh != null);
            if (wires?.Mesh != null)
            {
                var aabb = wires.Mesh.GetAabb();
                // Four wires between two poles 34 m apart: the mesh must span that gap and hang BELOW the anchors.
                T.Check($"the wires span the gap ({aabb.Size.X:0.0} m wide)", aabb.Size.X > 30f);
                T.Check($"...and sag below the anchors ({aabb.Position.Y:0.00} m at the lowest)",
                        aabb.Position.Y < lowest - 0.05f);
            }

            // ---- ⚠⚠ WINDING: the tube must face OUTWARD ---------------------------------------------------
            //
            // This shipped inside out. Godot treats CLOCKWISE as front-facing, the ring traced counter-clockwise
            // seen from outside, so under `cull_back` every wire was invisible from outside and solid from within.
            // The source file carried a comment WARNING about exactly this failure mode, which is why the check
            // here is arithmetic and not prose: GenerateNormals derives normals from the winding, so a normal that
            // points back toward the wire's own axis is the bug, stated in a form that cannot be "nearly right".
            if (wires?.Mesh != null)
            {
                var arr = wires.Mesh.SurfaceGetArrays(0);
                var verts = (Vector3[])arr[(int)Mesh.ArrayType.Vertex];
                var norms = (Vector3[])arr[(int)Mesh.ArrayType.Normal];
                var aw = new Vector3[4]; var bw = new Vector3[4];
                field.AnchorsWorld(p0, aw); field.AnchorsWorld(p1, bw);
                int outward = 0, inward = 0;
                for (int i = 0; i < verts.Length; i += 7)   // a sample, not all of them -- this is a shape check
                {
                    // Nearest point on the straight run between the two poles' first anchors is a good enough
                    // axis: a wire's sag never moves it far enough sideways to flip the sign of this test.
                    Vector3 ax = bw[0] - aw[0];
                    float t = Mathf.Clamp((verts[i] - aw[0]).Dot(ax) / Mathf.Max(1e-4f, ax.LengthSquared()), 0f, 1f);
                    Vector3 onAxis = aw[0] + ax * t;
                    Vector3 outDir = verts[i] - onAxis;
                    outDir.Y = 0f;   // ignore the sag's vertical offset; the tube's radius is what matters
                    if (outDir.LengthSquared() < 1e-6f) continue;
                    if (norms[i].Dot(outDir.Normalized()) > 0f) outward++; else inward++;
                }
                GD.Print($"[powerline-test] winding sample: {outward} outward, {inward} inward");
                T.Check($"the tube faces OUTWARD ({outward} out vs {inward} in) -- inside-out is invisible under cull_back",
                        outward > inward * 3);
            }

            // ---- SAG: the midpoint hangs, the ends do not ------------------------------------------------
            var sa = new Vector3(0f, 10f, 0f); var sb = new Vector3(40f, 10f, 0f);
            T.Check("a span starts at its first anchor", PowerLineField.SpanPoint(sa, sb, 0f).IsEqualApprox(sa));
            T.Check("...and ends at its second", PowerLineField.SpanPoint(sa, sb, 1f).IsEqualApprox(sb));
            float mid = PowerLineField.SpanPoint(sa, sb, 0.5f).Y;
            float expect = 10f - 40f * PowerLineField.SagFraction;
            T.Check($"...and hangs lowest in the middle ({mid:0.00} m, expected {expect:0.00})",
                    Mathf.Abs(mid - expect) < 0.01f);
            // A CONTROL that must fail if the sag is ever zeroed: the middle has to be strictly below the ends.
            T.Check("control: the midpoint is strictly below a flat line", mid < 10f - 0.1f);

            // ---- REFRESH: spans survive the pole list being rebuilt --------------------------------------
            //
            // This is the editor's actual lifecycle -- the tool rebuilds the pole set every time it opens, from the
            // map's poles plus whatever was placed since. Re-matching by POSITION is what keeps a line you strung
            // earlier attached to the same poles; by index it would silently re-point.
            var rebuilt = new List<Transform3D>
            {
                PoleAt(new Vector3(-80f, 0f, 0f), 0f),              // a NEW pole, inserted BEFORE the originals,
                PoleAt(Vector3.Zero, 332f),                          // which shifts every old index by one
                PoleAt(new Vector3(34f, 0f, 0f), 304f),
            };
            int n = field.RefreshPoles(rebuilt, out int dropped);
            T.Check($"the pole set was replaced ({n} poles)", n == 3);
            T.Check($"the span survived an index shift ({field.SpanCount} span, {dropped} dropped)",
                    field.SpanCount == 1 && dropped == 0);

            // ...and a span whose pole is GONE is dropped and counted, not left dangling.
            field.RefreshPoles(new List<Transform3D> { PoleAt(Vector3.Zero, 332f) }, out int dropped2);
            T.Check($"a span whose far pole was deleted is dropped and counted ({dropped2})",
                    field.SpanCount == 0 && dropped2 == 1);

            field.QueueFree();
            yield return Ticks(1);
        }
    }
}
