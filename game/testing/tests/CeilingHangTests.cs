using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // A ceiling fixture must HANG. Shipped 2026-09-13 with all three pendants inverted -- plate on the slab, bulb
    // buried in it (strawberry: "they deploy upside down, point up into the ceiling instead of hanging down") --
    // because the defs carried no MeshEuler and the comment justifying that had the sign backwards. The meshes are
    // authored from z=0 at the plate into NEGATIVE z, and in this frame negative z is UP: StandBasis is a +90 about
    // X, which sends mesh -Z to world +Y.
    //
    // ⚠ WHY NO EXISTING CHECK CAUGHT IT, which is the more useful half. The pendants were verified through
    // `tools/shot.py lamp` (--lamptest), and that harness builds the MeshInstance at RotationDegrees(pitch,0,0) with
    // pitch defaulting to 0 -- raw authored coordinates, no StandBasis and no MeshBasis. It is structurally incapable
    // of showing an error in the composition, so four rounds of renders agreed the models hung correctly and the game
    // disagreed. Same blindness as the `prop` scene drawing raw mesh coords. This test exists because the render
    // instrument cannot see the transform under test: it composes the REAL one instead.
    //
    // Placed through Barricade.PlaceOnSurface, not Deployable.Spawn -- Spawn seats everything the FLOOR way, so a
    // test calling it would exercise a path a ceiling barricade never takes. That distinction is not academic: the
    // re-seat PlaceOnSurface does after Spawn is what left the omni 0.09 m inside the slab (LampLight.Reanchor).
    public class CeilingHangs : GameTest
    {
        public override string Name => "deploy.ceiling_hangs";

        // The fixture's whole visible extent in WORLD space. Not `mi.Mesh.GetAabb()` on its own: LampLight carves the
        // emitter onto a CHILD MeshInstance and leaves the housing behind (LampLight.cs, `_fixture.Mesh = body`), so
        // the parent mesh alone stops at the socket and reports the bulb-less 0.316 m where the model is 0.398 m.
        // Measuring the housing and calling it the fixture is the same class of error as measuring the .obj and
        // calling it the placed object.
        static Aabb VisualBounds(Node3D root)
        {
            Aabb acc = default; bool any = false;
            void Walk(Node n)
            {
                if (n is MeshInstance3D m && m.Mesh != null)
                {
                    Aabb w = m.GlobalTransform * m.Mesh.GetAabb();
                    acc = any ? acc.Merge(w) : w; any = true;
                }
                foreach (var c in n.GetChildren()) Walk(c);
            }
            Walk(root);
            return acc;
        }

        public override IEnumerable<Step> Run()
        {
            const float CeilY = 3f;
            var placed = new List<(DeployableDef def, Deployable d)>();
            float x = 0f;
            foreach (var def in DeployableDef.All)
            {
                if (def.Mount != BarricadeMount.Ceiling) continue;
                placed.Add((def, Barricade.PlaceOnSurface(World, def, new Vector3(x, CeilY, 0f), Vector3.Down, 0f)));
                x += 3f;
            }

            // Not "> 0": the loop IS the coverage, so a def silently dropping out of All would leave a green suite
            // testing less than it did. 3 is the count at the time of writing -- raise it when a fourth lands.
            T.Check($"found the ceiling-mount defs to test (got {placed.Count}, want >= 3)", placed.Count >= 3);
            yield return Ticks(2);

            foreach (var (def, d) in placed)
            {
                var mi = d.DebugMesh;
                if (mi == null || mi.Mesh == null) { T.Check($"{def.Name}: has a body mesh", false); continue; }

                Aabb w = VisualBounds(mi);
                float oy = d.GlobalPosition.Y;   // the mount origin: hit point + normal * standoff

                // Both halves stated, because together they pin the fixture to [origin - span, origin] and the
                // inverted build lands on exactly [origin, origin + span] -- each check fails on its own.
                T.Check($"{def.Name}: plate sits AT the mount plane (top is {w.End.Y - oy:0.000} m off the origin, want ~0)",
                        Mathf.Abs(w.End.Y - oy) <= 0.01f);
                T.Check($"{def.Name}: body hangs its full {w.Size.Y:0.000} m BELOW it (bottom is {w.Position.Y - oy:0.000} m off the origin)",
                        Mathf.Abs((w.Position.Y - oy) + w.Size.Y) <= 0.01f);

                // ...and the plate is against the SLAB, not merely against its own origin. The two checks above are
                // both relative to the body, so a standoff that floats the whole fixture passes them untouched --
                // which it did: Offset's clamped 0.05 hung every canopy 5 cm clear of the ceiling.
                T.Check($"{def.Name}: plate is flush with the ceiling ({CeilY - w.End.Y:0.000} m clear of it)",
                        CeilY - w.End.Y >= 0f && CeilY - w.End.Y <= 0.01f);

                // The bulb is at the BOTTOM of a pendant. Independent of the two above -- they place the bounding
                // box, this places the part inside it -- and it is the half a player actually looks at. It also
                // still fails if a future model is authored with the socket and bulb swapped, which a bounds check
                // on its own would happily accept.
                MeshInstance3D em = null;
                foreach (var c in mi.GetChildren()) if (c is MeshInstance3D e && e.Name == "Emissive") { em = e; break; }
                if (em == null || em.Mesh == null) { T.Check($"{def.Name}: has a split-out emissive bulb", false); continue; }
                Aabb ew = em.GlobalTransform * em.Mesh.GetAabb();
                float frac = w.Size.Y > 0.001f ? (w.End.Y - ew.GetCenter().Y) / w.Size.Y : 0f;   // 0 = at the plate, 1 = at the very bottom
                T.Check($"{def.Name}: the bulb is in the lower half of the fixture ({frac:0.00} of the way down)", frac > 0.5f);

                // And the light goes with it. Kind.CeilingBulb anchors the omni on that emissive sub-mesh, so this
                // is true by construction once the mesh is right AND the lamp has been re-anchored after the mount
                // re-seat. It was 0.09 m ABOVE the plate before Reanchor existed, which no bounds check would see.
                LampLight lamp = null;
                foreach (var c in d.GetChildren()) if (c is LampLight l) { lamp = l; break; }
                if (lamp == null) { T.Check($"{def.Name}: has a LampLight", false); continue; }
                T.Check($"{def.Name}: the omni sits below the plate ({lamp.DebugLightWorld.Y - oy:0.000} m off the origin)",
                        lamp.DebugLightWorld.Y < oy - 0.05f);
            }
        }
    }
}
