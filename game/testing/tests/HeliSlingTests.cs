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
            float drop = slung.SlingDeployed ? slung.ToGlobal(slung.DebugSlingAnchorLocal).Y - slung.Sling.GlobalPosition.Y : 0f;
            T.Check($"the cable pays out to near its full length ({drop:0.00} m of a {slung.DebugSlingLen:0.0} m cable)",
                drop > slung.DebugSlingLen * 0.7f);

            // ---- 3. NOT DANGLING ON THE GROUND. "Dangles below the heli when in flight" is the spec, and a
            // magnet left out while parked would drag through the terrain under a landed aircraft.
            var parked = Spawn(World, new Vector3(600f, 0.2f, 0f), sling: true);
            for (int i = 0; i < 200; i++) { parked.DriveHeli(0f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            T.Check($"a landed sky-crane has reeled its magnet in (grounded at {parked.GlobalPosition.Y:0.00} m)",
                !parked.SlingDeployed);
        }
    }
}
