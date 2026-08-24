using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // VoX, 2026-08-24 (first real push-to-report): "when I am sprinting ... and then I jump, I jump super
    // fast compared to when I jump normally."
    //
    // MEASURE IT BEFORE NAMING A CAUSE. The obvious suspect is the jump impulse, and it is innocent --
    // PlayerMovementSim sets Velocity.y = JUMP flat, with no stance term anywhere near it. So this probe
    // reports the two things that actually differ between the runs (horizontal distance and peak height)
    // rather than asserting a theory.
    public sealed class JumpProbe : GameTest
    {
        public override string Name => "player.jump_sprint_probe";
        public override double TimeoutSimSeconds => 60;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var p = new PlayerController();
            World.AddChild(p);
            p.GlobalPosition = new Vector3(0f, 1.2f, 0f);
            yield return Ticks(30);

            foreach (var (label, sprint) in new[] { ("walk ", false), ("sprint", true) })
            {
                p.GlobalPosition = new Vector3(0f, 1.2f, 0f);
                p.Velocity = Vector3.Zero;
                p.Stamina = 1f;
                yield return Ticks(25);

                // run up first: sprint stance only engages while actually moving
                p.ScriptedStance = sprint ? EPlayerStance.SPRINT : EPlayerStance.STAND;
                p.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
                yield return Ticks(50);

                float y0 = p.GlobalPosition.Y;
                var start = p.GlobalPosition;
                float vhAtTakeoff = new Vector2(p.Velocity.X, p.Velocity.Z).Length();
                p.ScriptedJump = true;
                yield return Ticks(2);
                p.ScriptedJump = false;

                float peak = y0, air = 0f;
                for (int i = 0; i < 200; i++)
                {
                    yield return Ticks(1);
                    peak = Mathf.Max(peak, p.GlobalPosition.Y);
                    if (p.IsOnFloor() && i > 4) break;
                    air += 0.02f;   // one 50 Hz tick
                }
                float dist = new Vector2(p.GlobalPosition.X - start.X, p.GlobalPosition.Z - start.Z).Length();
                GD.Print($"[jump] {label}: takeoff {vhAtTakeoff:0.00} m/s | height {peak - y0:0.000} m | " +
                         $"distance {dist:0.00} m | airtime {air:0.00} s | stamina after {p.Stamina:0.00}");
                p.ScriptedStance = null; p.ScriptedInput = null;
                yield return Ticks(20);
            }

            // AIR INERTIA. Retail accelerates the airborne horizontal velocity toward the desired one
            // (accel = desiredWalkVelocity * 8 * multiplier, then clamp); ours assigns it outright every
            // tick. If that is the difference, a mid-air reversal should be INSTANT here and gradual there.
            p.GlobalPosition = new Vector3(0f, 1.2f, 0f);
            p.Velocity = Vector3.Zero;
            p.Stamina = 1f;
            p.ScriptedStance = EPlayerStance.SPRINT;
            p.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
            yield return Ticks(50);
            p.ScriptedJump = true;
            yield return Ticks(2);
            p.ScriptedJump = false;
            yield return Ticks(3);
            float vBefore = new Vector2(p.Velocity.X, p.Velocity.Z).Length();
            float fwdBefore = p.Velocity.Z;
            p.ScriptedInput = new UnityEngine.Vector2(0f, -1f);   // full reverse, mid-air
            yield return Ticks(1);
            float fwdAfter = p.Velocity.Z;
            GD.Print($"[jump] mid-air reversal in ONE tick: forward {fwdBefore:0.00} -> {fwdAfter:0.00} m/s (speed {vBefore:0.00})");
            p.ScriptedStance = null; p.ScriptedInput = null;

            // TRAJECTORY SHAPE, not just its endpoints. VoX's follow-up: "when sprinting I seemed to
            // INSTANTLY go to the jump height". My first measurement compared peak height and airtime and
            // found them identical -- which it would, because a snap to the right height and a smooth arc
            // to the right height END in the same place. Sample the rise instead.
            foreach (var (label, sprint) in new[] { ("walk ", false), ("sprint", true) })
            {
                p.GlobalPosition = new Vector3(0f, 1.2f, 0f);
                p.Velocity = Vector3.Zero;
                p.Stamina = 1f;
                p.ScriptedStance = sprint ? EPlayerStance.SPRINT : EPlayerStance.STAND;
                p.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
                yield return Ticks(50);
                float y0 = p.GlobalPosition.Y;
                p.ScriptedJump = true;
                yield return Ticks(1);
                p.ScriptedJump = false;

                var rise = new List<float>();
                for (int i = 0; i < 12; i++) { rise.Add(p.GlobalPosition.Y - y0); yield return Ticks(1); }
                GD.Print($"[jump] {label} rise per tick: {string.Join(" ", rise.ConvertAll(v => v.ToString("0.000")))}");
                p.ScriptedStance = null; p.ScriptedInput = null;
                yield return Ticks(25);
            }

            T.Check("probe ran", true);
        }
    }
}
