using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>A car nobody is driving is still an OBJECT. strawberry: "no physics unless driving SUCKS".
    ///
    /// A settled car used to become FreezeMode.Static -- not a heavy body at rest, a piece of the terrain.
    /// You could not push it, ram it or roll it downhill, and the only way back out was code calling Wake().
    /// Nothing in the suite noticed, because every other vehicle test drives the car it spawns.
    ///
    /// The measurement here is deliberately a COLLISION and not an impulse. ApplyCentralImpulse activates a
    /// sleeping body unconditionally in every engine, so poking the parked car would pass whether it were
    /// asleep, awake or welded to the ground -- the classic check whose PASS looks like its FAILURE. A ram
    /// by a second vehicle is the one stimulus that tells "asleep but dynamic" apart from "static".</summary>
    public class ParkedPhysicsProbe : GameTest
    {
        public override string Name => "vehicle.parked_physics";
        public override double TimeoutSimSeconds => 160;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            string car = System.Environment.GetEnvironmentVariable("UG_CAR") ?? "jeep";

            // Spawned exactly as the world spawns one: parked, braked, engine off, no driver. No Wake(), no
            // EngineOn -- touching either would be measuring a car the probe had already un-parked.
            var parked = Vehicle.BuildByName(car);
            World.AddChild(parked);
            parked.GlobalPosition = new Vector3(0f, 1.5f, 0f);
            yield return Ticks(500);   // outlast the 2.5 s spawn grace AND let the settle low-pass converge

            Vector3 rest = parked.GlobalPosition;
            float restSpeed = parked.LinearVelocity.Length();
            GD.Print($"[park] {car}: settled at {rest} |v|={restSpeed:0.000} m/s  (Freeze={parked.Freeze} Sleeping={parked.Sleeping})");
            T.Check($"the parked car settles instead of jittering forever (|v| {restSpeed:0.000} m/s)", restSpeed < 0.5f);

            // AND STAYS PUT. Sleep only replaces the static freeze if it holds as still -- a car that creeps,
            // sags or buzzes on its springs while nobody is near it is worse than the wall it replaced, and on
            // a map that parks ~89 of them it is also a solid frame of physics nobody asked for. Measured as
            // drift rather than as `Sleeping == true`, which would just be reading my own change back.
            for (int i = 0; i < 250; i++) yield return Ticks(1);
            float drift = parked.GlobalPosition.DistanceTo(rest);
            GD.Print($"[park] {car}: drifted {drift:0.0000} m over 5 s untouched");
            T.Check($"an untouched parked car does not creep or jitter ({drift:0.0000} m in 5 s)", drift < 0.05f);

            // ---- THE RAM. A second car, driven flat out into the parked one. Front is -Z, so the rammer
            // starts up-Z of it and drives forward down the line.
            var rammer = Vehicle.BuildByName(car);
            World.AddChild(rammer);
            rammer.GlobalPosition = rest + new Vector3(0f, 0f, 30f);
            rammer.EngineOn = true; rammer.Wake(); rammer.Brake = 0f;
            yield return Ticks(200);   // its own spawn grace

            float impactSpeed = 0f;
            for (int i = 0; i < 700; i++)
            {
                rammer.Drive(1f, 0f, false);
                yield return Ticks(1);
                float gap = rammer.GlobalPosition.Z - parked.GlobalPosition.Z;
                impactSpeed = Mathf.Max(impactSpeed, Mathf.Abs(rammer.LinearVelocity.Dot(-rammer.GlobalTransform.Basis.Z)));
                if (gap < 6f) break;   // contact-ish: the hulls are ~4.5 m long
            }
            float peakV = 0f;
            for (int i = 0; i < 250; i++)
            {
                rammer.Drive(1f, 0f, false); yield return Ticks(1);   // keep shoving through the hit
                peakV = Mathf.Max(peakV, parked.LinearVelocity.Length());
            }

            float shoved = parked.GlobalPosition.DistanceTo(rest);
            GD.Print($"[park] {car}: RAMMED at {impactSpeed:0.0} m/s -> the parked car moved {shoved:0.00} m, peak |v| {peakV:0.00} m/s");
            T.Check($"a parked car can be shoved by another vehicle ({shoved:0.00} m)", shoved > 0.25f);
            T.Check($"the shove actually MOVES it rather than nudging the mesh (peak |v| {peakV:0.00} m/s)", peakV > 0.5f);

            // ---- AND IT COMES BACK TO REST. The wake path is only half the fix: a car woken by every touch
            // that can never get back to sleep buzzes on its springs forever, which is the failure the static
            // freeze was papering over in the first place. Measured as a SECOND drift window, not as a
            // time-to-settle: the first version of this check timed how long the car took to drop below
            // 0.3 m/s after the shove loop and read 0.0 s, because the shove loop had already ended and the car
            // was already stopped. It asserted that a stationary car was stationary and would have passed on
            // any code at all, including the static freeze it exists to rule out.
            for (int i = 0; i < 400; i++) { rammer.Drive(-1f, 0f, false); yield return Ticks(1); }   // rammer backs off so it isn't leaning on it, and the car is given time to re-settle
            Vector3 rest2 = parked.GlobalPosition;
            for (int i = 0; i < 250; i++) { rammer.Drive(-1f, 0f, false); yield return Ticks(1); }
            float drift2 = parked.GlobalPosition.DistanceTo(rest2);
            GD.Print($"[park] {car}: re-settled -- drifted {drift2:0.0000} m over the 5 s after the ram (Sleeping={parked.Sleeping})");
            T.Check($"a rammed car comes back to a proper rest ({drift2:0.0000} m in 5 s)", drift2 < 0.05f);

            // ---- AND IT GETS BACK TO SLEEP. Both cars are now let go entirely -- no Drive() at all -- because
            // a body in contact with an ACTIVE one is held awake by the engine, and the rammer is driven every
            // tick right up to here. Sleeping is checked directly rather than inferred, because the whole claim
            // that sleep can replace the static freeze is a claim about COST: a car that settles to a standstill
            // but never sleeps looks identical from the outside and runs the full wheel sim forever, and ~89 of
            // them are parked on PEI. The transition count is the other half -- a sleep/wake oscillation drifts
            // exactly as little as a proper sleep does, so drift alone cannot tell them apart.
            int flips = 0; bool prevSleep = parked.Sleeping;
            for (int i = 0; i < 500; i++)
            {
                yield return Ticks(1);
                if (parked.Sleeping != prevSleep) { flips++; prevSleep = parked.Sleeping; }
            }
            float endGap = rammer.GlobalPosition.DistanceTo(parked.GlobalPosition);
            GD.Print($"[park] {car}: left alone 10 s -> Sleeping={parked.Sleeping}, {flips} sleep/wake flips, rammer {endGap:0.0} m away");
            T.Check($"a rammed car gets back to sleep and stays there ({flips} flips)", parked.Sleeping && flips <= 2);
        }
    }
}
