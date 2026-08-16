using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // DETAIL PARTS ON THE HELICOPTER FLEET (meshes by cow tools 0f8719c1, wired 2026-08-16).
    //
    // This suite exists because of how the wiring failed the first time and how nearly it got shipped. The
    // Parts array compiled, the build was clean, the render showed seats through the canopy -- and the Hind's
    // chin turret was not on the aircraft at all. Nothing in that chain was capable of saying so: a missing
    // part looks exactly like a part you cannot see from the camera angle you happened to render, and both
    // look exactly like a correctly-wired aircraft.
    //
    // So the checks below never look at pixels. They ask the built vehicle which part nodes it has and where
    // their geometry actually sits, which is the claim -- "the turret is ON the Hind, at the nose" -- stated
    // in the units the claim is about. Cheap enough (no simulation, no flight) to run on every sweep.
    public sealed class HeliPartsTests : GameTest
    {
        public override string Name => "vehicle.heli_parts";
        // Five 3 s settle drops plus the build pass; the default watchdog cuts in at 15 s.
        public override double TimeoutSimSeconds => 90;

        // Every airframe carries seats/steer/taillights; the Hind adds a turret, and the Hind and Orca each
        // carry four landing WHEELS. Counted explicitly rather than derived from the spec, so a spec that
        // quietly loses its Parts array fails here instead of agreeing with itself.
        //
        // The wheels are listed because they were MISSING and nothing caught it: the parts extractor refuses to
        // descend into the "Wheels" node (rotors are handled separately, wheels were handled nowhere), so both
        // aircraft shipped resting on their bellies with no wheels at all. strawberry spotted it by looking at
        // them. Every automated check here passed the whole time, because none of them knew a wheel was owed.
        static readonly (string heli, string[] parts)[] Fleet =
        {
            ("huey",        new[] { "huey_seats", "huey_steer", "huey_taillights" }),
            ("hind",        new[] { "hind_seats", "hind_steer", "hind_taillights", "hind_wheels" }),
            ("orca",        new[] { "orca_seats", "orca_steer", "orca_taillights", "orca_wheels" }),
            ("skycrane",    new[] { "skycrane_seats", "skycrane_steer", "skycrane_taillights" }),
            ("hummingbird", new[] { "hummingbird_seats", "hummingbird_steer", "hummingbird_taillights" }),
        };

        static MeshInstance3D FindPart(Node root, string name)
        {
            foreach (var c in root.GetChildren())
            {
                if (c is MeshInstance3D mi && mi.Name == name) return mi;
                if (c is Node3D n) { var hit = FindPart(n, name); if (hit != null) return hit; }
            }
            return null;
        }

        public override IEnumerable<Step> Run()
        {
            foreach (var (heli, parts) in Fleet)
            {
                var v = Vehicle.BuildByName(heli);
                World.AddChild(v);
                v.GlobalPosition = new Vector3(0f, 200f, 0f);   // out of the way; nothing here needs the ground
                yield return Ticks(1);

                foreach (var part in parts)
                {
                    var mi = FindPart(v, part);
                    T.Check($"{heli}: {part} is on the aircraft", mi != null);
                    if (mi == null) continue;

                    // A part whose mesh failed to parse still gives you a node, and an empty node renders as
                    // nothing at all -- indistinguishable, from outside, from the part being absent.
                    bool hasGeo = mi.Mesh != null && mi.Mesh.GetSurfaceCount() > 0 && mi.Mesh.GetFaces().Length > 0;
                    T.Check($"{heli}: {part} carries real geometry", hasGeo);
                    if (!hasGeo) continue;

                    // Verts are baked root-relative, so the mesh AABB IS the part's position on the airframe.
                    // The envelope is deliberately loose: it is not a fidelity check, it is a check that the
                    // extractor's transform was applied at all. The failure it exists for is a part landing at
                    // the world origin or a hundred metres out, which is what an unapplied (or double-applied)
                    // parent transform looks like, and which no amount of staring at a render of the FUSELAGE
                    // would ever reveal.
                    var box = mi.Mesh.GetAabb();
                    var c = box.GetCenter();
                    bool sane = Mathf.Abs(c.X) < 6f && c.Y > -2f && c.Y < 7f && Mathf.Abs(c.Z) < 12f;
                    T.Check($"{heli}: {part} sits on the airframe, not off in space " +
                            $"(centre {c.X:0.##},{c.Y:0.##},{c.Z:0.##} size {box.Size.X:0.##}x{box.Size.Y:0.##}x{box.Size.Z:0.##})", sane);
                }

                // The Hind's turret is the one part with a claim beyond "it exists": it is a CHIN turret, so it
                // belongs at the nose and below the cabin floor. Asserted against the seats rather than against
                // a hardcoded number, because the thing that makes it a chin turret is where it sits RELATIVE
                // to the crew -- forward of them and lower. A turret pasted at the tail passes every check
                // above and fails this one.
                if (heli == "hind")
                {
                    // The turret is no longer a Part: it is an articulated yaw/pitch pair built from
                    // Spec.Turrets, because a single merged mesh cannot traverse. The claim is unchanged --
                    // chin turret, forward of the crew and below them -- so it is still asserted, just against
                    // the mount that can actually aim.
                    var turret = FindPart(v, "hind_turret_pitch");
                    var seats = FindPart(v, "hind_seats");
                    T.Check($"hind: the turret is built as an articulated mount ({v.TurretCountBuilt})",
                        v.TurretCountBuilt == 1);
                    T.Check("hind: ...operated from the nose gunner's seat, not the pilot's",
                        v.Turrets.Length == 1 && v.Turrets[0].Seat == 1);
                    if (turret?.Mesh != null && seats?.Mesh != null)
                    {
                        // The pitch mesh is baked at ITS OWN pivot, so its raw AABB is relative to the mount,
                        // not the hull -- adding the mount position back is what makes this comparable to the
                        // seats. Comparing the two frames directly would be the same units answering a
                        // different question.
                        var tb = turret.Mesh.GetAabb().GetCenter() + v.Turrets[0].Pivot;
                        var sb = seats.Mesh.GetAabb().GetCenter();
                        // -Z is forward (the tail rotor hub sits at +Z on every spec in the fleet).
                        T.Check($"hind: the turret is FORWARD of the crew (turret z {tb.Z:0.##} vs seats {sb.Z:0.##})",
                            tb.Z < sb.Z);
                        T.Check($"hind: ...and hangs BELOW them, as a chin turret does (turret y {tb.Y:0.##} vs seats {sb.Y:0.##})",
                            tb.Y < sb.Y);
                    }
                }

                v.QueueFree();
                yield return Ticks(1);
            }

            // ---- AND THEY MUST NOT REST BURIED IN THE GROUND.
            //
            // This is the check the missing turret actually turned out to be. The turret was on the aircraft
            // the whole time, correctly placed; the Hind's collision box floor sat 0.58 m ABOVE its own belly,
            // so a parked Hind sinks until half its underside is inside the terrain, and a chin turret whose
            // highest point is 0.06 m up vanishes completely. Four of the five had no landing-gear collision
            // at all -- only the Huey, which has explicit skid boxes.
            //
            // Measured as the lowest airframe VERTEX against the ground, not as the body's origin height,
            // because origin height is a number every one of these passes: the aircraft was always sitting
            // where physics put it. The question is where its GEOMETRY ended up.
            Rigs.Ground(World);
            yield return Ticks(2);
            float x = -300f;
            foreach (var (heli, _) in Fleet)
            {
                var v = Vehicle.BuildByName(heli);
                World.AddChild(v);
                v.GlobalPosition = new Vector3(x, 3f, 0f);
                x += 40f;
                for (int i = 0; i < 180; i++) yield return Ticks(1);   // drop and settle

                float lowest = 9e9f;
                foreach (var n in v.GetChildren())
                    if (n is MeshInstance3D mi && mi.Mesh != null && mi.Mesh.GetSurfaceCount() > 0)
                        lowest = Mathf.Min(lowest, v.GlobalPosition.Y + mi.Mesh.GetAabb().Position.Y);

                // 0.25 m of tolerance: skids compress into the ground slightly and the terrain is not glass.
                // Anything past that is the airframe wearing the map.
                T.Check($"{heli}: parked, its airframe sits ON the ground rather than in it " +
                        $"(lowest geometry {lowest:0.##} m, ground 0)", lowest > -0.25f);
                v.QueueFree();
                yield return Ticks(1);
            }
        }
    }
}
