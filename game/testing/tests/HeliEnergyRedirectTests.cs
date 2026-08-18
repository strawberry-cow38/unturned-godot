using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // DOES DESCENT RATE AFFECT FORWARD SPEED AT ALL? (VoX: "when I level out it doesnt translate my falling speed
    // into forward speed enough.")
    //
    // The answer in the shipped model is NO, and not "not enough" -- exactly zero. The horizontal equation of
    // motion contains no term that depends on vertical velocity: lift reads attitude and flat speed, parasite drag
    // and the backstop read the flat vector only, and the heave damper is the one vel.Y-dependent force but points
    // along Vector3.Down, whose horizontal component is zero at every attitude.
    //
    // So this file is built around a CONTROL rather than a threshold: fly the identical aircraft at the identical
    // attitude with wildly different sink rates and compare. Same-answer is the bug; different-answer is the fix.
    // That makes the check two-sided by construction -- it cannot pass by the feature doing nothing, and it cannot
    // pass by the feature doing something arbitrary.
    public sealed class HeliEnergyRedirectTests : GameTest
    {
        public override string Name => "vehicle.heli_energy_redirect";
        public override double TimeoutSimSeconds => 200;

        const float DiveDeg = 45f;      // nose-down: tilts b.Y forward, so a descent should push forward
        const int RunTicks = 50;        // 1.0 s at 50 Hz -- short, so the two sink rates have not converged

        // Returns flat speed after holding a fixed nose-down attitude for RunTicks, starting from rest horizontally
        // at the given sink rate. Everything except vy0 is identical between calls.
        IEnumerable<Step> FlyFrom(float vy0, System.Action<float> report)
        {
            var v = Vehicle.BuildByName("huey");
            World.AddChild(v);
            v.GlobalPosition = new Vector3(0f, 3000f, 0f);
            v.EngineOn = true; v.DebugInstantStart = true; v.SpawnRotorRunning();
            v.DebugNoTurbulence = true;
            v.GlobalTransform = new Transform3D(new Basis(Vector3.Right, Mathf.DegToRad(-DiveDeg)), v.GlobalPosition);
            v.AngularVelocity = Vector3.Zero;
            v.LinearVelocity = new Vector3(0f, vy0, 0f);
            yield return Ticks(1);
            for (int i = 0; i < RunTicks; i++) { v.DriveHeli(0f, 0f, 0f, 0f, 0.02); yield return Ticks(1); }
            report(new Vector2(v.LinearVelocity.X, v.LinearVelocity.Z).Length());
            v.QueueFree();
            yield return Ticks(2);
        }

        public override IEnumerable<Step> Run()
        {
            float slowOff = 0f, fastOff = 0f, slowOn = 0f, fastOn = 0f;
            try
            {
                // ---- CONTROL: shipped behaviour. A 35 m/s difference in sink must produce NO difference forward.
                Vehicle.HeaveRedirect = 0f;
                foreach (var st in FlyFrom(-5f,  x => slowOff = x)) yield return st;
                foreach (var st in FlyFrom(-40f, x => fastOff = x)) yield return st;

                T.Check($"shipped: sink rate has ZERO effect on forward speed (-5 m/s -> {slowOff:0.####}, -40 m/s -> {fastOff:0.####} flat) -- this is the defect, asserted so it cannot be fixed by accident",
                    Mathf.Abs(slowOff - fastOff) < 0.02f);

                // ---- THE FIX: the same two flights with the redirect on must now diverge, in the right direction.
                Vehicle.HeaveRedirect = 1f;
                foreach (var st in FlyFrom(-5f,  x => slowOn = x)) yield return st;
                foreach (var st in FlyFrom(-40f, x => fastOn = x)) yield return st;

                GD.Print($"[REDIRECT] off: slow={slowOff:0.00} fast={fastOff:0.00} (delta {fastOff - slowOff:0.0000})  " +
                         $"on: slow={slowOn:0.00} fast={fastOn:0.00} (delta {fastOn - slowOn:0.00})");

                T.Check($"with redirect on, falling faster MAKES you faster ({slowOn:0.0} m/s from a 5 m/s sink vs {fastOn:0.0} from 40)",
                    fastOn > slowOn * 1.5f && fastOn - slowOn > 3f);
                // The slow-sink case must stay close to shipped: this is a sink-driven term, not a free speed boost.
                T.Check($"...and it is the SINK doing it, not a blanket thrust bonus (gentle-descent speed {slowOn:0.00} vs shipped {slowOff:0.00})",
                    Mathf.Abs(slowOn - slowOff) < Mathf.Abs(fastOn - fastOff) * 0.5f);
            }
            finally { Vehicle.HeaveRedirect = 0f; }   // global static; restore on every exit path
        }
    }
}
