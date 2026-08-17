using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // AN NPC HELICOPTER HAS TO ACTUALLY FLY THERE. The failure modes are all quiet ones: it can sink into the
    // ground, it can stall in place with the nose swinging, it can arrive and spiral onto the monument, or it can
    // tumble because nothing on this airframe self-levels. So the checks are about the TRAJECTORY over time, not
    // about the controller being wired up.
    //
    // There is no terrain in the test world (that needs a real Unturned install), so height-hold is measured against
    // flat ground at y=0 -- which is the honest limit of this rig and is stated in the check text.
    public sealed class NpcHeliTests : GameTest
    {
        public override string Name => "vehicle.npc_heli";
        public override double TimeoutSimSeconds => 200;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);

            var v = Vehicle.BuildByName("huey");
            World.AddChild(v);
            v.GlobalPosition = new Vector3(-600f, NpcHeli.CanopyClearance, 0f);
            v.EngineOn = true;
            v.DebugInstantStart = true;
            v.SpawnRotorRunning();   // spawned mid-air, same as the real NPC path
            // TURBULENCE STAYS ON, AND IT SPAWNS POINTED THE WRONG WAY. Both deliberate, and both were missing:
            // the first version disabled turbulence and did a LookAt straight down the track, so the aircraft flew
            // 600 m in a dead-straight line with err=0.00 and roll=0.00 the whole way. The roll loop was never
            // commanded and never disturbed, so the bank check below was measuring an axis that never moved -- it
            // reported "worst bank 0.0 deg" and passed while the shipped controller was rolling itself to 45 deg
            // each way in strawberry's game. Turbulence is the EXCITATION (an anti-damper needs a gust to amplify)
            // and the 90 deg offset forces a real banked turn, which is the manoeuvre being checked.
            v.LookAt(new Vector3(-600f, NpcHeli.CanopyClearance, 600f), Vector3.Up);
            v.LinearVelocity = new Vector3(0f, 0f, 1f) * (v.SpeedMaxMps * 0.5f);

            var ai = new NpcHeli { Heli = v, Terr = null, Target = Vector3.Zero, TargetName = "TestMonument" };
            World.AddChild(ai);

            float startRange = new Vector2(v.GlobalPosition.X, v.GlobalPosition.Z).Length();
            float minY = 9999f, maxY = -9999f, closest = 9999f, worstBankDeg = 0f;
            int bankSamples = 0, bankedSamples = 0;
            float topSpeed = 0f;
            for (int i = 0; i < 3500; i++)
            {
                yield return Ticks(1);
                Vector3 p = v.GlobalPosition;
                minY = Mathf.Min(minY, p.Y); maxY = Mathf.Max(maxY, p.Y);
                closest = Mathf.Min(closest, new Vector2(p.X, p.Z).Length());
                // Track the worst bank: the aircraft rolling itself back and forth is a real complaint, and no
                // check about reaching the target would ever notice it.
                worstBankDeg = Mathf.Max(worstBankDeg, Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(Mathf.Abs(v.GlobalTransform.Basis.X.Y), 0f, 1f))));
                bankSamples++; if (Mathf.Abs(v.GlobalTransform.Basis.X.Y) > 0.10f) bankedSamples++;   // did it bank AT ALL?
                topSpeed = Mathf.Max(topSpeed, new Vector2(v.LinearVelocity.X, v.LinearVelocity.Z).Length());
                if (i % 40 == 0 || v.Exploded)
                    GD.Print($"[NPCHELI] t={i * 0.02f:0.0} y={p.Y:0.0} upY={v.GlobalTransform.Basis.Y.Y:0.00} spd={new Vector2(v.LinearVelocity.X, v.LinearVelocity.Z).Length():0.0} " +
                             $"coll={ai.LastColl:0.00} yaw={ai.LastYaw:0.00} pitch={ai.LastPitch:0.00} roll={ai.LastRoll:0.00} err={ai.LastErr:0.00} " +
                             $"phase={ai.Phase} noseUp={ai.LastNoseUpDeg:0.0}/{ai.LastWantNoseUpDeg:0.0}deg");
                if (v.Exploded) break;
            }
            GD.Print($"[NPCHELI/SUMMARY] worstBank={worstBankDeg:0.0}deg bankedTicks={bankedSamples}/{bankSamples} " +
                     $"y={minY:0.0}..{maxY:0.0} closest={closest:0}m topSpeed={topSpeed:0.0}m/s cruiseTarget={v.SpeedMaxMps * 0.62f:0.0}m/s");
            Vector3 end = v.GlobalPosition;
            float endRange = new Vector2(end.X, end.Z).Length();

            // It has to survive the trip at all -- a crash would satisfy "it got closer" on the way down.
            T.Check($"the npc is still flying at the end (hp {v.Health:0}/{v.HealthMax:0}, exploded={v.Exploded})",
                !v.Exploded && v.Health > v.HealthMax * 0.5f);
            T.Check($"...and upright, not tumbling (up.Y {v.GlobalTransform.Basis.Y.Y:0.00})",
                v.GlobalTransform.Basis.Y.Y > 0.7f);

            // IT CLOSED THE DISTANCE. Started 600 m out; anything that merely drifts fails this.
            T.Check($"it flew to the monument ({startRange:0} m out -> closest approach {closest:0} m)",
                closest < startRange * 0.35f);

            // HEIGHT HELD. Flat ground here, so the band is around CanopyClearance itself.
            T.Check($"it held height rather than sinking or climbing away (y {minY:0.#}..{maxY:0.#} against a {NpcHeli.CanopyClearance:0} m target; FLAT ground in this rig, no terrain)",
                minY > NpcHeli.CanopyClearance * 0.45f && maxY < NpcHeli.CanopyClearance * 2.2f);

            // POSITIVE CONTROL FOR THE CHECK BELOW. "worst bank was small" is only meaningful if the aircraft
            // banked at all -- a controller that never touched the roll axis, or one that was never disturbed,
            // scores a perfect 0.0 deg. So require that it spent real time in a bank first, then bound the bank.
            T.Check($"it actually banked during the flight ({bankedSamples} of {bankSamples} ticks past 6 deg) -- without this the bank bound below is vacuous",
                bankedSamples > bankSamples / 100);
            T.Check($"it does not roll itself back and forth (worst bank {worstBankDeg:0.#} deg)", worstBankDeg < 42f);

            // IT ORBITS rather than parking on top: it should still be out near the orbit radius at the end,
            // not sat at zero range. A controller that flew to the centre and hovered passes every check above.
            T.Check($"it is circling, not parked on the monument (final range {endRange:0} m, orbit radius {NpcHeli.OrbitRadius:0} m)",
                endRange > NpcHeli.OrbitRadius * 0.4f && endRange < NpcHeli.OrbitRadius * 3f);
        }
    }
}
