using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // WHICH WAY DO THE STICKS GO? A one-off probe that applies each axis alone and reports what the airframe does,
    // so a controller can be written from MEASUREMENT instead of from a sign derived off the torque expression.
    // I derived pitch correctly from DriveHeli and then guessed yaw and roll for NpcHeli; both were inverted, which
    // put the aircraft in the ground on the first run. Cheaper to ask the sim.
    public sealed class HeliAxisProbe : GameTest
    {
        public override string Name => "vehicle.heli_axis_probe";
        public override double TimeoutSimSeconds => 60;

        static Vehicle Fly(Node w, Vector3 at)
        {
            var v = Vehicle.BuildByName("huey");
            w.AddChild(v);
            v.GlobalPosition = at;
            v.EngineOn = true; v.DebugInstantStart = true; v.DebugNoTurbulence = true;
            return v;
        }

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var yv = Fly(World, new Vector3(0f, 300f, 0f));
            var rv = Fly(World, new Vector3(400f, 300f, 0f));
            var pv = Fly(World, new Vector3(800f, 300f, 0f));
            for (int i = 0; i < 260; i++)
            {
                yv.DriveHeli(0.6f, 0f, 0f, 0f, 0.02); rv.DriveHeli(0.6f, 0f, 0f, 0f, 0.02); pv.DriveHeli(0.6f, 0f, 0f, 0f, 0.02);
                yield return Ticks(1);
            }
            for (int i = 0; i < 60; i++)
            {
                yv.DriveHeli(0.6f, +1f, 0f, 0f, 0.02);   // yaw +1
                rv.DriveHeli(0.6f, 0f, 0f, +1f, 0.02);   // roll +1
                pv.DriveHeli(0.6f, 0f, +1f, 0f, 0.02);   // pitch +1
                yield return Ticks(1);
            }
            Vector3 fwd = -yv.GlobalTransform.Basis.Z;
            float headNow = Mathf.Atan2(-fwd.X, -fwd.Z);
            float bank = rv.GlobalTransform.Basis.X.Y;
            float noseUp = pv.GlobalTransform.Basis.Y.Dot(Vector3.Up) < 0.999f ? -(-pv.GlobalTransform.Basis.Z).Y : 0f;

            // These are the CONTROL CONVENTIONS every autopilot in the codebase is written against, asserted as
            // signs so a flip in DriveHeli cannot silently invert an AI's steering. Vacuous "true" checks would
            // have documented the numbers without guarding them.
            T.Check($"YAW +1 yaws one way consistently: AngularVelocity.Y {yv.AngularVelocity.Y:0.000} (negative), heading {headNow:0.000} rad",
                yv.AngularVelocity.Y < -0.1f && headNow < -0.05f);
            T.Check($"ROLL +1 puts the RIGHT wing down: Basis.X.Y {bank:0.000} (negative)", bank < -0.1f);
            T.Check($"PITCH +1 raises the NOSE: forward.Y {(-pv.GlobalTransform.Basis.Z).Y:0.000} (positive)",
                (-pv.GlobalTransform.Basis.Z).Y > 0.1f);
        }
    }
}
