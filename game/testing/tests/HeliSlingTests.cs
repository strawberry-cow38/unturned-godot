using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // THE WINCH COSTS THRUST, AND THRUST IS THE THING THE SKY-CRANE HAS LEAST OF.
    //
    // Lift force is HeliThrust * Mass (StepHeli), so the crane's entire spare capacity at full collective is
    // (12.2 - 9.8) * 900 = 2160 N -- about 220 kg. The magnet shipped at 260 kg, i.e. MORE than the whole
    // margin, so the aircraft could not lift its own equipment and simply sank. strawberry found that in
    // about a minute of flying ("THE MAGNET IS VERRRYYYY HEAVY").
    //
    // It survived my own testing because the rig that "verified" the magnet (--magnettest) pins the airframe
    // level and gravity-free on a gantry. That rig measures the cable and the weld, and it is structurally
    // incapable of noticing that the aircraft cannot carry the thing -- a PASS there looks identical whether
    // the crane could lift a car or could not lift itself. This suite is the missing instrument: it flies the
    // real flight model and asks whether the machine still goes up.
    //
    // Written as a CONTROL PAIR on one airframe, per the house style: the same sky-crane, same collective,
    // same altitude, differing only in whether the winch deployed. An absolute "it climbs" would pass on any
    // helicopter with spare power; the claim here is about the DIFFERENCE the magnet makes.
    public sealed class HeliSlingTests : GameTest
    {
        public override string Name => "vehicle.heli_sling";
        public override double TimeoutSimSeconds => 240;

        static Vehicle Spawn(Node world, Vector3 at, bool sling)
        {
            var v = Vehicle.BuildByName("skycrane");
            v.DebugNoSling = !sling;
            world.AddChild(v);
            v.GlobalPosition = at;
            v.DebugNoTurbulence = true;
            v.DebugInstantStart = true;   // this suite measures LIFT, not the start-up gate
            v.EngineOn = true;
            return v;
        }

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);

            // ---- 1. THE CRANE STILL CLIMBS CARRYING ITS OWN MAGNET.
            // Both at altitude so ground effect is out of it, both at full collective, run long enough to
            // settle to terminal climb (heave damping makes that an equilibrium, not a ramp).
            var bare = Spawn(World, new Vector3(0f, 500f, 0f), sling: false);
            var slung = Spawn(World, new Vector3(300f, 500f, 0f), sling: true);
            for (int i = 0; i < 420; i++)
            {
                bare.DriveHeli(1f, 0f, 0f, 0f, 0.02);
                slung.DriveHeli(1f, 0f, 0f, 0f, 0.02);
                yield return Ticks(1);
            }
            float bareVy = bare.LinearVelocity.Y, slungVy = slung.LinearVelocity.Y;

            // The subject's flight model must have actually run, or "it climbed" is a statement about nothing.
            T.Check($"the slung subject's rotor sim really ran (spool {slung.RotorSpool:0.###})", slung.RotorSpool > 0.95f);
            T.Check($"the winch actually deployed on the subject, so this IS the loaded case", slung.SlingDeployed);
            T.Check($"...and the control flew WITHOUT one, so the pair differs in the magnet alone", !bare.SlingDeployed);

            // THE REGRESSION ITSELF. At 260 kg this was NEGATIVE -- the crane sank at full collective.
            T.Check($"a sky-crane carrying its own magnet still CLIMBS at full collective ({slungVy:0.00} m/s)",
                slungVy > 1.5f);
            // ...and the magnet is a real but bounded tax. A check on the subject alone would pass on an
            // airframe with infinite power; the control is what makes this a measurement of the magnet.
            float cost = bareVy > 0.01f ? 1f - slungVy / bareVy : 1f;
            T.Check($"the magnet costs some climb, so it is genuinely being carried ({bareVy:0.00} -> {slungVy:0.00} m/s, {cost * 100f:0.#}%)",
                cost > 0.02f);
            T.Check($"...but not most of it ({cost * 100f:0.#}% of the empty climb rate)", cost < 0.45f);

            // ---- 2. THE CABLE PAYS OUT. A magnet that deploys but stays jammed under the hull would satisfy
            // every check above. This is the one that caught the ground-ray self-detection bug, where the
            // crane read its own winch as terrain and stow/deployed every tick, pinning the cable at ~1.2 m.
            float drop = slung.SlingDeployed ? slung.ToGlobal(slung.DebugSlingVisualAnchorLocal).Y - slung.Sling.GlobalPosition.Y : 0f;
            T.Check($"the cable pays out to near its full length ({drop:0.00} m of a {slung.DebugSlingLen:0.0} m cable)",
                drop > slung.DebugSlingLen * 0.7f);

            // ---- 3. THE LOAD MUST NOT HAUL THE AIRCRAFT BACKWARDS. strawberry, flying it: "its pulling the heli
            // backwards". A slung load DOES trail -- that is real -- but the steady trail angle here is set by the
            // magnet's own damping, not by anything aerodynamic: with linear damping the balance is
            // tan(theta) = damp * v / g, which is INDEPENDENT OF MASS. So making the magnet lighter cannot fix the
            // angle (it only scales the force m*g*tan(theta) down); the damping coefficient is the thing that sets it.
            //
            // Cruise is imposed kinematically here rather than flown, so the reading is the cable's steady trail at a
            // known speed and not a measurement of whatever airspeed the model happened to reach.
            var cruise = Spawn(World, new Vector3(1200f, 500f, 0f), sling: true);
            for (int i = 0; i < 260; i++) { cruise.DriveHeli(1f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            float v = cruise.SpeedMaxMps;
            // RAMP to cruise rather than snapping to it. An instantaneous velocity change kicks the pendulum, and
            // with zero drag that kick never decays -- so a snap would be measuring my own step input forever.
            for (int i = 0; i < 200; i++)
            {
                cruise.LinearVelocity = new Vector3(0f, 0f, -v * (i / 199f));
                cruise.DriveHeli(1f, 0f, 0f, 0f, 0.02);
                yield return Ticks(1);
            }
            // WITH NO DRAG THE SWING NEVER SETTLES, so a single end-of-window sample is a phase of an undamped
            // oscillation, not a steady state -- the first cut of this check did exactly that and read 89.9 deg.
            // The quantity that matters is what the airframe FEELS over time: average the rearward pull across a
            // long window (a net haul shows up here, an honest pendulum averages toward zero) and keep the peak
            // separately, because those two numbers answer different questions.
            float sumBack = 0f, peakBack = 0f, sumTrail = 0f, peakTrail = 0f; int n = 0;
            float peakEarly = 0f, peakLate = 0f;   // swing amplitude in the first vs last third -> does it DECAY?
            for (int i = 0; i < 900; i++)
            {
                cruise.LinearVelocity = new Vector3(0f, 0f, -v);
                cruise.DriveHeli(1f, 0f, 0f, 0f, 0.02);
                yield return Ticks(1);
                if (!cruise.SlingDeployed) continue;
                Vector3 r = cruise.Sling.GlobalPosition - cruise.ToGlobal(cruise.DebugSlingVisualAnchorLocal);
                float hz = new Vector2(r.X, r.Z).Length(), dn = Mathf.Max(0.01f, -r.Y);
                float deg = Mathf.RadToDeg(Mathf.Atan2(hz, dn));
                // Rearward component only: a load swinging FORWARD pushes the aircraft along and is not the complaint.
                float back = SlingMagnet.MagnetMass * 9.8f * Mathf.Tan(Mathf.DegToRad(Mathf.Min(deg, 80f))) * (r.Z > 0f ? 1f : -1f);
                sumBack += back; peakBack = Mathf.Max(peakBack, back);
                sumTrail += deg; peakTrail = Mathf.Max(peakTrail, deg); n++;
                if (i < 300) peakEarly = Mathf.Max(peakEarly, deg);
                else if (i >= 600) peakLate = Mathf.Max(peakLate, deg);
            }
            float meanBack = n > 0 ? sumBack / n : 0f, meanTrail = n > 0 ? sumTrail / n : 0f;
            T.Check($"the sim actually produced a hanging load to measure ({n} samples, mean trail {meanTrail:0.#} deg, peak {peakTrail:0.#})",
                n > 500 && peakTrail > 0.5f);
            // THE COMPLAINT ITSELF: a NET rearward haul. Weight-only means the load cannot steadily drag the
            // aircraft back -- it may swing, but it must not average into a tow.
            T.Check($"weight-only: no NET rearward haul at cruise (mean {meanBack:0} N against {(16.5f - 9.8f) * 900f:0} N of spare thrust)",
                Mathf.Abs(meanBack) < 60f);
            T.Check($"...and the worst instantaneous pull is still small ({peakBack:0} N)", peakBack < 400f);
            // ANTI-SWAY HAS TEETH ONLY IF THE SWING SHRINKS. Amplitude in the last third against the first third:
            // with weight-only and no cross-cable damper this ratio sat at ~1 (an undamped pendulum rings forever),
            // so this is the check that separates "we added a damper" from "we added a damper that does nothing".
            float decay = peakEarly > 0.01f ? peakLate / peakEarly : 1f;
            T.Check($"the swing DECAYS rather than ringing forever (peak {peakEarly:0.#} deg early -> {peakLate:0.#} deg late, {decay * 100f:0}%)",
                decay < 0.55f);

            // ---- 4. STABLE WITH SOMETHING ACTUALLY ON THE HOOK. Every check above flies an EMPTY magnet, so the
            // suspended mass is always 12 kg -- which is precisely why the suite stayed green while the render blew
            // up to NaN the moment a crate was picked up. The anti-sway and bridle were scaled by the SUSPENDED mass
            // but applied to the magnet body, so a welded 800 kg load over-drove a 12 kg body by ~68x and the solver
            // diverged. An all-green suite that never grabs anything cannot see a load-dependent instability.
            var hauler = Spawn(World, new Vector3(1800f, 12.0f, 0f), sling: true);
            var crate = new RigidBody3D { Name = "Crate", Mass = 800f, CollisionLayer = 1u << 6, CollisionMask = (1u << 0) | (1u << 5) };
            var cs = new Vector3(2.4f, 2.4f, 2.4f);
            crate.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = cs } });
            crate.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = cs } });
            World.AddChild(crate);
            crate.GlobalPosition = new Vector3(1800f, cs.Y * 0.5f, 0f);
            hauler.ToggleSlingMagnet();
            for (int i = 0; i < 500; i++) { hauler.DriveHeli(0.6f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            bool finite = hauler.GlobalPosition.IsFinite() && hauler.LinearVelocity.IsFinite()
                          && (!hauler.SlingDeployed || (hauler.Sling.GlobalPosition.IsFinite() && hauler.Sling.AngularVelocity.IsFinite()))
                          && crate.GlobalPosition.IsFinite();
            T.Check($"carrying a real 800 kg load stays numerically stable (heli {hauler.GlobalPosition.Y:0.0}, crate {crate.GlobalPosition.Y:0.0})",
                finite);
            T.Check($"...and the coil is not spun up by its own bridle ({(hauler.SlingDeployed ? hauler.Sling.AngularVelocity.Length() : 0f):0.0} rad/s)",
                !hauler.SlingDeployed || hauler.Sling.AngularVelocity.Length() < 25f);

            // ---- 5. THE MAGNETABLE CONTAINER'S FIXED ATTACH POINT. The whole point of a declared magnet point is
            // that the grab is not "wherever the coil brushed it": the load snaps to a known spot and hangs level and
            // centred. So the container is offset LATERALLY from the magnet before the grab -- the generic path only
            // ever moves a load in Y, so it would leave that offset in place and this check would catch it.
            var crane = Spawn(World, new Vector3(2400f, 12.0f, 0f), sling: true);
            var box = MagnetableContainer.Spawn(World, new Vector3(2400f + 0.9f, 0.2f, 0.8f));
            yield return Ticks(2);
            T.Check($"the container built its retail door leaves ({(box.DoorsOpen ? "open" : "shut")} at rest)", !box.DoorsOpen);
            box.SetDoorsOpen(true);
            for (int i = 0; i < 60; i++) yield return Ticks(1);
            T.Check("...and the doors open when told to", box.DoorsOpen);

            crane.ToggleSlingMagnet();
            for (int i = 0; i < 520; i++) { crane.DriveHeli(0.6f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            bool got = crane.SlingDeployed && crane.Sling.Held == box;
            T.Check($"the crane's magnet grabs the container (held={(crane.SlingDeployed ? crane.Sling.Held?.Name.ToString() ?? "nothing" : "no magnet")})", got);
            if (got)
            {
                // Snapped on ALL THREE axes, so the lateral offset it was spawned with is gone.
                float off = new Vector2(box.MagnetPointWorld.X - crane.Sling.FaceWorld.X,
                                        box.MagnetPointWorld.Z - crane.Sling.FaceWorld.Z).Length();
                T.Check($"...at its FIXED point, so it hangs centred not wherever it was touched ({off:0.00} m lateral offset, spawned 0.90 m off)",
                    off < 0.25f);
            }

            // ---- 6. NOT DANGLING ON THE GROUND. "Dangles below the heli when in flight" is the spec, and a
            // magnet left out while parked would drag through the terrain under a landed aircraft.
            var parked = Spawn(World, new Vector3(600f, 0.2f, 0f), sling: true);
            for (int i = 0; i < 200; i++) { parked.DriveHeli(0f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            T.Check($"a landed sky-crane has reeled its magnet in (grounded at {parked.GlobalPosition.Y:0.00} m)",
                !parked.SlingDeployed);
        }
    }
}
