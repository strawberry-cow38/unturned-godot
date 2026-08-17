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

        // Every airframe carries seats/steer; the Hind adds a turret, and the Hind and Orca each
        // The `_taillights` mesh is deliberately NOT in this list any more: on a helicopter it is a
        // NAVIGATION light, and BuildNavLights turns that single lens into a red/green PAIR named
        // NavLightPort / NavLightStarboard. The nav-light section below checks it properly -- by which
        // SIDE each lens lands on, which is the thing that was actually wrong.
        // carry four landing WHEELS. Counted explicitly rather than derived from the spec, so a spec that
        // quietly loses its Parts array fails here instead of agreeing with itself.
        //
        // The wheels are listed because they were MISSING and nothing caught it: the parts extractor refuses to
        // descend into the "Wheels" node (rotors are handled separately, wheels were handled nowhere), so both
        // aircraft shipped resting on their bellies with no wheels at all. strawberry spotted it by looking at
        // them. Every automated check here passed the whole time, because none of them knew a wheel was owed.
        static readonly (string heli, string[] parts)[] Fleet =
        {
            ("huey",        new[] { "huey_seats", "huey_steer" }),
            ("hind",        new[] { "hind_seats", "hind_steer", "hind_wheels" }),
            ("orca",        new[] { "orca_seats", "orca_steer", "orca_wheels" }),
            ("skycrane",    new[] { "skycrane_seats", "skycrane_steer" }),
            ("hummingbird", new[] { "hummingbird_seats", "hummingbird_steer" }),
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

            // ---- THE HIND'S TURRET ACTUALLY TRAVERSES.
            //
            // Asserted on where the MUZZLE ends up in the world, not on the node's rotation numbers. A rotation
            // of 30 degrees is true of a mount that swings the wrong way, swings about the wrong axis, or swings
            // geometry that is not attached to it -- all three of which read as "the turret aimed".
            var hind2 = Vehicle.BuildByName("hind");
            World.AddChild(hind2);
            hind2.GlobalPosition = new Vector3(0f, 300f, 0f);
            yield return Ticks(2);
            var centre = hind2.TurretMuzzle(1);
            T.Check("hind: the turret reports a muzzle", centre.HasValue);
            if (centre.HasValue)
            {
                T.Check("aiming a seat with no turret is refused", !hind2.AimTurret(0, 30f, 0f));
                hind2.AimTurret(1, 60f, 0f);
                var left = hind2.TurretMuzzle(1).Value;
                hind2.AimTurret(1, -60f, 0f);
                var right = hind2.TurretMuzzle(1).Value;
                T.Check($"...and yawing it moves the muzzle sideways ({left.DistanceTo(right):0.##} m apart)",
                    left.DistanceTo(right) > 1f);
                T.Check($"...to OPPOSITE sides of centre (x {left.X:0.##} vs {right.X:0.##})",
                    Mathf.Sign(left.X - centre.Value.X) != Mathf.Sign(right.X - centre.Value.X));

                hind2.AimTurret(1, 0f, -45f);
                float down = hind2.TurretMuzzle(1).Value.Y;
                hind2.AimTurret(1, 0f, 15f);
                float up = hind2.TurretMuzzle(1).Value.Y;
                T.Check($"...and pitching it raises and lowers the muzzle (down {down:0.##} vs up {up:0.##})", up > down);

                // The traverse limits are the point of a CHIN turret: it cannot shoot through its own airframe.
                hind2.AimTurret(1, 900f, 900f);
                var pinned = hind2.TurretMuzzle(1).Value;
                hind2.AimTurret(1, 120f, 15f);
                var atLimit = hind2.TurretMuzzle(1).Value;
                T.Check($"...and a wild input clamps to the limit rather than spinning through the hull " +
                        $"({pinned.DistanceTo(atLimit):0.###} m from the clamped pose)",
                    pinned.DistanceTo(atLimit) < 0.01f);
            }
            // ---- AND IT FIRES ITS OWN GUN, ALONG THE BARREL.
            //
            // The mount carries its own belt (retail's model: a gun item bolted to a seat), so it must not eat
            // the gunner's rifle rounds -- and the shot has to leave along the BARREL rather than the look ray.
            // Those two coincide whenever the turret is pointed where you are looking, which is most of the
            // time, so the check deliberately aims the gun somewhere the camera is not.
            hind2.AimTurret(1, 0f, 0f);
            int belt0 = hind2.TurretAmmo(1);
            T.Check($"hind: the turret starts with its own belt ({belt0} rounds)", belt0 > 0);
            T.Check("...and a seat with no turret cannot fire one",
                !hind2.TryTurretFire(0, out _, out _, out _));

            bool fired = hind2.TryTurretFire(1, out var o1, out var d1, out var gun1);
            T.Check($"...the gunner's seat can ({gun1})", fired && gun1 != null);
            T.Check($"...it costs a round ({belt0} -> {hind2.TurretAmmo(1)})", hind2.TurretAmmo(1) == belt0 - 1);
            // Cycling: a second shot in the same instant is refused, or a held trigger empties 200 rounds in a
            // frame and the belt means nothing.
            T.Check("...and it will not fire again the same instant", !hind2.TryTurretFire(1, out _, out _, out _));

            // The shot must follow the gun. Yaw the mount hard over and the direction has to swing with it.
            yield return Ticks(20);
            hind2.AimTurret(1, 90f, 0f);
            yield return Ticks(1);
            hind2.TryTurretFire(1, out var o2, out var d2, out _);
            T.Check($"...and the shot leaves along the BARREL, not the look ray " +
                    $"(dir {d1.Normalized().Dot(d2.Normalized()):0.##} after a 90 deg traverse)",
                d1.Normalized().Dot(d2.Normalized()) < 0.5f);
            T.Check($"...from a muzzle that moved with it ({o1.DistanceTo(o2):0.##} m)", o1.DistanceTo(o2) > 0.5f);

            hind2.QueueFree();
            yield return Ticks(1);

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

            // ---- NAVIGATION LIGHTS: red to PORT, green to STARBOARD, and both steady.
            //
            // Asserted by SIDE, not by name, because the name is the thing that was wrong. Every airframe ships
            // exactly ONE lens mesh and all five were painted flat red -- but the orca's lens is on STARBOARD
            // (mesh X +0.27..+1.24), so that aircraft was flying a red light on its right-hand side and had no
            // green at all. A check that trusted the node names would have agreed with the bug. This one reads
            // each lens's actual geometry and demands the colour match the side it is on.
            foreach (var heli in new[] { "huey", "hind", "orca", "skycrane", "hummingbird" })
            {
                var v = Vehicle.BuildByName(heli);
                World.AddChild(v);
                v.GlobalPosition = new Vector3(0f, 3f, 0f);
                yield return Ticks(1);

                var port = v.FindChild("NavLightPort", false, false) as MeshInstance3D;
                var star = v.FindChild("NavLightStarboard", false, false) as MeshInstance3D;
                T.Check($"{heli}: carries BOTH navigation lights, not just the one lens the mesh ships",
                    port != null && star != null);
                if (port != null && star != null)
                {
                    // Where the lens actually renders: the mirrored copy carries a -1 X scale, so the centre of
                    // its transformed AABB is the only honest answer to "which side is this on".
                    float px = (port.Transform * port.Mesh.GetAabb()).GetCenter().X;
                    float sx = (star.Transform * star.Mesh.GetAabb()).GetCenter().X;
                    T.Check($"{heli}: the RED light is on the port side ({px:+0.00;-0.00} m, left is negative)", px < 0f);
                    T.Check($"{heli}: the GREEN light is on the starboard side ({sx:+0.00;-0.00} m)", sx > 0f);
                    var pm = port.MaterialOverride as StandardMaterial3D;
                    var sm = star.MaterialOverride as StandardMaterial3D;
                    // EMISSIVE is the actual request -- "work as lights" -- and a lens merely PAINTED red looks
                    // identical in a lit render, so this asks the material rather than the pixels.
                    T.Check($"{heli}: both lenses actually emit rather than being painted the colour of light",
                        pm != null && sm != null && pm.EmissionEnabled && sm.EmissionEnabled);
                    T.Check($"{heli}: ...and they emit the RIGHT colours (port {pm.Emission.R:0.0}/{pm.Emission.G:0.0}, starboard {sm.Emission.R:0.0}/{sm.Emission.G:0.0})",
                        pm.Emission.R > 0.5f && pm.Emission.G < 0.3f && sm.Emission.G > 0.5f && sm.Emission.R < 0.3f);
                }
                v.QueueFree();
                yield return Ticks(1);
            }

            // ---- THE BEACON IS THE ONLY THING THAT BLINKS, and it blinks off the ROTOR.
            //
            // Two states are needed before "it flashes" means anything: a machine with a live rotor has to be
            // seen both LIT and DARK, and a machine with a stopped rotor has to stay dark throughout. Checking
            // only that the node exists would pass on a beacon welded permanently on, which is exactly the
            // failure mode of a light with no timer.
            var beac = Vehicle.BuildByName("huey");
            World.AddChild(beac);
            beac.GlobalPosition = new Vector3(0f, 60f, 0f);
            beac.EngineOn = true;
            var lamp = beac.FindChild("BeaconBelly", false, false) as MeshInstance3D;
            T.Check("the huey has a belly anti-collision beacon", lamp != null);
            for (int i = 0; i < 260; i++) { beac.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            bool sawLit = false, sawDark = false;
            for (int i = 0; i < 120 && !(sawLit && sawDark); i++)
            {
                beac.DriveHeli(1f, 0f, 0f, 0f, 0.02);
                yield return Ticks(1);
                float e = ((StandardMaterial3D)lamp.MaterialOverride).EmissionEnergyMultiplier;
                if (e > 1f) sawLit = true; else sawDark = true;
            }
            T.Check("...and it FLASHES: seen both lit and dark inside one 2.4 s window with the rotor turning",
                sawLit && sawDark);
            // The rotor is the gate, not the ignition switch -- and a beacon that ignores its gate is a beacon
            // that never turns off, which no other check here would notice.
            var idleHeli = Vehicle.BuildByName("huey");
            World.AddChild(idleHeli);
            idleHeli.GlobalPosition = new Vector3(60f, 3f, 0f);
            var idleLamp = idleHeli.FindChild("BeaconBelly", false, false) as MeshInstance3D;
            bool everLit = false;
            for (int i = 0; i < 120; i++)
            {
                yield return Ticks(1);
                if (((StandardMaterial3D)idleLamp.MaterialOverride).EmissionEnergyMultiplier > 1f) everLit = true;
            }
            T.Check("...and stays DARK on a machine whose rotor never spun", !everLit);
            beac.QueueFree(); idleHeli.QueueFree();
            yield return Ticks(1);
        }
    }
}
