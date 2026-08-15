using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // RECOIL MOVES THE CAMERA, NOT THE GUN (strawberry: "making recoil move the whole camera instead of just the
    // gun. same thing as the scope sway fix u just did, but for recoil impulse").
    //
    // What this has to catch is not "recoil exists" -- recoil existed before, on TWO paths at once, and that was
    // the bug. `_recoilPending` folded one kick into the aim while Kick() rotated the viewmodel by a SEPARATE
    // RandfRange roll, so on a single shot the gun climbed by one amount and the crosshair by a different one.
    // Both paths were live, both looked healthy, and no check on "did the aim move" would have told them apart.
    //
    // So the assertion is an EQUALITY, not an inequality: the aim's total climb must equal the sum of the rolled
    // impulses, shot for shot. That is what makes it single-source. A second path re-added anywhere -- on the
    // viewmodel, or a second roll into _recoilPending -- breaks the sum even though "the aim moved" still passes.
    public sealed class RecoilCameraTests : GameTest
    {
        public override string Name => "gun.recoil_camera";
        public override double TimeoutSimSeconds => 60;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var p = new PlayerController { CaptureMouse = false, Inventory = new SDG.Unturned.PlayerInventory() };
            World.AddChild(p);
            p.GlobalPosition = new Vector3(0f, 1f, 0f);
            yield return Ticks(40);
            p.EquipHeldGun("eaglefire");
            p.Ammo = 60;
            yield return Until(() => p.HeldItemReady, 6);
            p.DebugSetPitch(0f);
            yield return Ticks(10);

            // THE CONTROL, and it runs FIRST so it cannot be contaminated by the burst. If the aim drifts on its
            // own -- settle, sway, a stray input -- then "the aim climbed after firing" measures that drift and
            // not the recoil, and the equality below would be luck. Same tick count the burst gets.
            float idle0 = p.DebugPitch, idleYaw0 = p.GlobalRotation.Y;
            yield return Ticks(120);
            float idleDrift = Mathf.Abs(p.DebugPitch - idle0);
            float idleYawDrift = Mathf.Abs(Mathf.RadToDeg(p.GlobalRotation.Y - idleYaw0));
            T.Check($"the aim holds still when nothing is firing ({idleDrift:0.####} deg pitch, {idleYawDrift:0.####} yaw over 120 ticks)",
                idleDrift < 0.01f && idleYawDrift < 0.01f);

            p.DebugSetPitch(0f);
            yield return Ticks(5);
            float pitch0 = p.DebugPitch;
            float rolled = 0f, rolledYaw = 0f, rolledYawAbs = 0f;
            float gunRotPeak = 0f;
            int shots = 0;

            float yawDeltaAbs = 0f;
            for (int i = 0; i < 8; i++)
            {
                float yawBefore = p.GlobalRotation.Y;
                if (!p.Fire()) { yield return Ticks(6); continue; }
                shots++;
                // Read the roll IMMEDIATELY: the impulse is stamped inside Fire(), and a yield here would let the
                // drain start before the number is banked.
                rolled += p.DebugLastRecoilKick.X;
                rolledYaw += p.DebugLastRecoilKick.Y;
                rolledYawAbs += Mathf.Abs(p.DebugLastRecoilKick.Y);
                // Drain THIS shot before the next one: yaw is measured per-shot below, and a shared drain would
                // let two opposite kicks cancel inside one measurement -- the same cancellation that made the
                // first version of the yaw check vacuous.
                for (int k = 0; k < 24; k++)
                {
                    yield return Ticks(1);
                    var vr = p.DebugViewmodelRecoilRot;
                    gunRotPeak = Mathf.Max(gunRotPeak, Mathf.Max(Mathf.Abs(vr.X), Mathf.Max(Mathf.Abs(vr.Y), Mathf.Abs(vr.Z))));
                }
                yawDeltaAbs += Mathf.Abs(Mathf.RadToDeg(p.GlobalRotation.Y - yawBefore));
            }
            T.Check($"the burst actually fired ({shots} shots)", shots >= 6);
            T.Check($"...and each shot rolled a real impulse (sum {rolled:0.##} deg pitch)", rolled > 1f);

            // Drain. The kick lands on _recoilPending and bleeds into the aim at 18/s, so the aim lags the roll by
            // a few frames; 0.64^60 is nothing.
            yield return Ticks(60);
            float climb = p.DebugPitch - pitch0;

            // THE LOAD-BEARING CHECK. Not "> 0" -- equal to what was rolled. Under the old two-path code the gun
            // took an unscaled roll and the aim took a Recover-scaled DIFFERENT roll, so this sum could not match.
            T.Check($"the aim climbs by exactly the rolled impulse ({climb:0.###} deg vs {rolled:0.###} rolled)",
                Mathf.Abs(climb - rolled) < 0.05f);

            // And the gun does not take a second copy of it. The viewmodel still has a positional Shake_* channel
            // -- that is deliberate, a gun jolting in the hands -- but its ROTATION must never leave rest, because
            // rotating the model is exactly the "just the gun" behaviour being removed.
            T.Check($"the gun itself never rotates from recoil (peak {gunRotPeak:0.####} deg across the burst)",
                gunRotPeak < 0.01f);

            // YAW, and the first cut of this check was wrong in a way worth keeping written down. It asserted
            // Abs(SIGNED sum) > 0.05, having just noted in a comment that random signs let a burst cancel. The
            // eaglefire has Recoil_Min_X == Recoil_Max_X == 1.09, so every roll is EXACTLY +/-1.09 and an even
            // 4/4 split sums to EXACTLY ZERO -- it passed alone on a lucky split and failed the full sweep with
            // "rolled 0 deg". Magnitude and destination are two claims, so they get two checks:
            T.Check($"horizontal recoil rolls a real impulse ({rolledYawAbs:0.###} deg total, signs cancel to {rolledYaw:0.###})",
                rolledYawAbs > 0.05f);
            // PER-SHOT, summed as ABSOLUTES. The signed-sum version of this passed while the yaw drain was
            // deliberately scaled to 0.4x -- both sides were ~0 from the cancellation, so the equality held
            // vacuously and proved nothing. Per shot the roll is +/-1.09 and cannot cancel with itself.
            T.Check($"the body yaws by exactly the rolled impulse, shot for shot ({yawDeltaAbs:0.###} deg turned vs {rolledYawAbs:0.###} rolled)",
                Mathf.Abs(yawDeltaAbs - rolledYawAbs) < 0.05f);

            p.QueueFree();
            yield break;
        }
    }
}
