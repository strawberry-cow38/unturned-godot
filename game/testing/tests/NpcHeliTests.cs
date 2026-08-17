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
            v.DebugNoTurbulence = true;
            v.LookAt(new Vector3(0f, NpcHeli.CanopyClearance, 0f), Vector3.Up);
            v.LinearVelocity = new Vector3(0f, 0f, 0f);

            var ai = new NpcHeli { Heli = v, Terr = null, Target = Vector3.Zero, TargetName = "TestMonument" };
            World.AddChild(ai);

            float startRange = new Vector2(v.GlobalPosition.X, v.GlobalPosition.Z).Length();
            float minY = 9999f, maxY = -9999f, closest = 9999f;
            for (int i = 0; i < 3500; i++)
            {
                yield return Ticks(1);
                Vector3 p = v.GlobalPosition;
                minY = Mathf.Min(minY, p.Y); maxY = Mathf.Max(maxY, p.Y);
                closest = Mathf.Min(closest, new Vector2(p.X, p.Z).Length());
                if (i % 40 == 0 || v.Exploded)
                    GD.Print($"[NPCHELI] t={i * 0.02f:0.0} y={p.Y:0.0} upY={v.GlobalTransform.Basis.Y.Y:0.00} spd={new Vector2(v.LinearVelocity.X, v.LinearVelocity.Z).Length():0.0} " +
                             $"coll={ai.LastColl:0.00} yaw={ai.LastYaw:0.00} pitch={ai.LastPitch:0.00} roll={ai.LastRoll:0.00} err={ai.LastErr:0.00} spool={v.RotorSpool:0.00}");
                if (v.Exploded) break;
            }
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

            // IT ORBITS rather than parking on top: it should still be out near the orbit radius at the end,
            // not sat at zero range. A controller that flew to the centre and hovered passes every check above.
            T.Check($"it is circling, not parked on the monument (final range {endRange:0} m, orbit radius {NpcHeli.OrbitRadius:0} m)",
                endRange > NpcHeli.OrbitRadius * 0.4f && endRange < NpcHeli.OrbitRadius * 3f);
        }
    }
}
