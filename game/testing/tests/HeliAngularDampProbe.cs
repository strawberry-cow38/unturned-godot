using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // WHAT IS THE ANGULAR DAMPING, ACTUALLY? Vehicle.cs sets AngularDamp = 0.25 and leaves AngularDampMode at
    // Godot's default, which is COMBINE -- so the project's default_angular_damp is ADDED, not replaced. This file
    // already documents that exact trap for the LINEAR axis and resolved it by MEASURING (0.100 s^-1, "identical
    // across hind, orca and hummingbird"), then switched linear to Replace. Nobody ever measured the angular one.
    //
    // It matters because cmd is an angular ACCELERATION integrated by ApplyTorque, so total attitude change per
    // stick input is alpha/zeta -- the damping is a DIVISOR on how far the aircraft ends up rotating, not just on
    // how fast. 0.25 vs 0.35 is a 40% difference in every attitude excursion in the game.
    //
    // Method: spin it in clean air with no pilot input and no rotor, and fit the decay. Godot integrates linear
    // damping as v *= (1 - damp*dt) per step, so the decay is exponential and ln(w) is linear in t.
    public sealed class HeliAngularDampProbe : GameTest
    {
        public override string Name => "vehicle.heli_angular_damp";
        public override double TimeoutSimSeconds => 60;

        public override IEnumerable<Step> Run()
        {
            foreach (string name in new[] { "minicopter", "huey", "skycrane" })
            {
                var v = Vehicle.BuildByName(name);
                World.AddChild(v);
                v.GlobalPosition = new Vector3(0f, 3000f, 0f);
                v.EngineOn = false;                 // no rotor: no cmd torque, no turbulence (turb needs rpm > 0.4)
                v.DebugNoTurbulence = true;
                v.LinearVelocity = Vector3.Zero;
                yield return Ticks(5);
                v.AngularVelocity = new Vector3(0f, 2.0f, 0f);   // yaw, so gravity/attitude cannot feed back into it

                float w0 = 0f, w1 = 0f;
                const int T0 = 25, T1 = 125;        // 0.5 s and 2.5 s at 50 Hz -- skip the first tick's transient
                for (int i = 0; i <= T1; i++)
                {
                    yield return Ticks(1);
                    float w = Mathf.Abs(v.AngularVelocity.Y);
                    if (i == T0) w0 = w;
                    if (i == T1) w1 = w;
                }
                // w1 = w0 * exp(-zeta * dt)  ->  zeta = ln(w0/w1) / dt
                float dt = (T1 - T0) * 0.02f;
                float zeta = Mathf.Log(w0 / Mathf.Max(w1, 1e-6f)) / dt;
                GD.Print($"[ANGDAMP] {name,-12} w(0.5s)={w0:0.0000} w(2.5s)={w1:0.0000} -> zeta={zeta:0.000} s^-1  " +
                         $"(written 0.25; Combine+0.1 would give 0.35)");
                T.Check($"{name}: measured angular damping {zeta:0.000} s^-1 -- 0.25 written, 0.35 if Combine adds the project default",
                    zeta > 0.15f && zeta < 0.60f);
                v.QueueFree();
                yield return Ticks(3);
            }
        }
    }
}
