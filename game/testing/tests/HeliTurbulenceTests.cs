using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // TURBULENCE SCALES WITH HEIGHT ABOVE GROUND, in severity AND in how often gusts arrive
    // (strawberry: "low to the ground should be relatively calm").
    //
    // Written as a control pair on one airframe, because absolutes prove nothing here: "it gets gusts at altitude"
    // passes on a build with no altitude term at all, and "it is calm down low" passes on a build where turbulence
    // is simply broken everywhere. Only the DIFFERENCE between two heights is the claim.
    //
    // Both subjects are held at their altitude rather than flown: a hands-off helicopter drifts, and a subject that
    // wandered from 200 m down to 40 m would quietly average two regimes together and blunt the very difference
    // being measured.
    public sealed class HeliTurbulenceTests : GameTest
    {
        public override string Name => "vehicle.heli_turbulence";
        public override double TimeoutSimSeconds => 180;

        static Vehicle Spawn(Node world, Vector3 at)
        {
            var v = Vehicle.BuildByName("huey");
            world.AddChild(v);
            v.GlobalPosition = at;
            v.DebugInstantStart = true;
            v.EngineOn = true;
            return v;
        }

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);

            var low = Spawn(World, new Vector3(0f, 6f, 0f));       // just off the deck
            var high = Spawn(World, new Vector3(400f, 210f, 0f));  // well above TurbFullAgl

            float lowSum = 0f, highSum = 0f, lowPeak = 0f, highPeak = 0f;
            int lowGusts = 0, highGusts = 0;
            float lowPrev = 0f, highPrev = 0f;
            for (int i = 0; i < 3000; i++)
            {
                // Hold each subject at its own altitude, and keep the rotor turning (turbulence is gated on it).
                low.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(0f, 6f, 0f));
                high.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(400f, 210f, 0f));
                low.LinearVelocity = Vector3.Zero; high.LinearVelocity = Vector3.Zero;
                low.AngularVelocity = Vector3.Zero; high.AngularVelocity = Vector3.Zero;
                low.DriveHeli(0.6f, 0f, 0f, 0f, 0.02);
                high.DriveHeli(0.6f, 0f, 0f, 0f, 0.02);
                yield return Ticks(1);

                float l = low.DebugTurbulence.Length(), h = high.DebugTurbulence.Length();
                lowSum += l; highSum += h;
                lowPeak = Mathf.Max(lowPeak, l); highPeak = Mathf.Max(highPeak, h);
                // A gust is a step UP in kick magnitude: between gusts it only ever decays.
                if (l > lowPrev + 0.02f) lowGusts++;
                if (h > highPrev + 0.02f) highGusts++;
                lowPrev = l; highPrev = h;
            }

            // The rig has to have produced turbulence at all, or every comparison below is between two zeroes.
            T.Check($"the high subject actually gets gusts, so there is something to compare ({highGusts} gusts, peak {highPeak:0.###} rad/s, AGL {high.DebugTurbAgl:0})",
                highGusts > 3 && highPeak > 0.05f);
            T.Check($"...and both subjects read the altitude they were placed at (low AGL {low.DebugTurbAgl:0.#}, high AGL {high.DebugTurbAgl:0})",
                low.DebugTurbAgl < 20f && high.DebugTurbAgl > 100f);

            T.Check($"SEVERITY: gusts down low are much weaker than at altitude (peak {lowPeak:0.###} vs {highPeak:0.###} rad/s)",
                lowPeak < highPeak * 0.5f);
            T.Check($"FREQUENCY: gusts arrive less often down low ({lowGusts} vs {highGusts} in the same window)",
                lowGusts < highGusts);
            // Calm, not dead: a low-level machine should still be moved a little, or "scales with height" has
            // silently become "off below 12 m".
            T.Check($"...but low level is CALM, not perfectly still (accumulated {lowSum:0.#}, peak {lowPeak:0.###} rad/s)",
                lowSum > 0.01f);
        }
    }
}
