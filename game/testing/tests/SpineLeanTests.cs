using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // The 3P spine lean (RiggedCharacter.LeanModifier). Retail leans the torso in HumanAnimator.cs:45
    // `spine.Rotate(0, _pitch * 0.5f, _lean * LEAN)` with LEAN = 20; we had no 3P lean at all, so a leaning player's
    // model stood bolt upright and every model-sourced visual (muzzle flash, tracer anchor) came out of an upright
    // torso while the camera and the bullet origin were off to the side.
    //
    // This test exists because the FIRST TWO attempts at that feature both shipped looking done and did nothing:
    //   1. the modifier was created inside EnableGunLayer, so an UNARMED rig never got one;
    //   2. the property was `set { if (_leanMod != null) ... }`, which silently DROPPED every assignment that landed
    //      before the modifier existed.
    // Both failed silently -- no exception, no log, a render pixel-identical to no lean. So the assertion here is
    // deliberately made on the OUTCOME (where the head physically ends up) rather than on the rotation we fed in:
    // re-deriving the delta quaternion would agree with the bug in exactly the way the bug needs to survive.
    //
    // The measurement is a DIFFERENTIAL between two rigs playing the same clip, advanced by the same ticks. The clip
    // moves the spine on its own, so a single rig sampled before/after would mix animation into the reading; two rigs
    // in lockstep cancel it and leave only the lean.
    public class SpineLeanTiltsTheTorso : GameTest
    {
        public override string Name => "rig.spine_lean";

        const float LeanDeg = 20f;      // retail HumanAnimator.LEAN

        public override IEnumerable<Step> Run()
        {
            RiggedCharacter.SetAnimFrozen(false);   // a prior test could have left the global freeze on

            // UNARMED rigs, which is the case both earlier attempts got wrong. No gun layer is ever enabled here.
            var flat = new ZombieController();
            var lean = new ZombieController();
            World.AddChild(flat);
            World.AddChild(lean);
            flat.GlobalPosition = new Vector3(-4f, 0f, 0f);
            lean.GlobalPosition = new Vector3(4f, 0f, 0f);
            yield return Ticks(10);                  // _Ready builds each rig

            var rf = FindDown<RiggedCharacter>(flat);
            var rl = FindDown<RiggedCharacter>(lean);
            bool built = rf?.Skeleton != null && rl?.Skeleton != null
                         && rf.Skeleton.FindBone("Spine") >= 0 && rf.Skeleton.FindBone("Skull") >= 0;
            T.Check("both rigs built with a Spine + Skull bone", built);
            if (!built) yield break;

            // Wiring guard, ahead of any pose reading: the modifier must EXIST on this rig. Both earlier failures were
            // a missing modifier reading as a valid unleaned pose, so "0 deg of swing" has to be able to tell
            // "nothing is attached" apart from "attached and not moving the bone".
            T.Check("rig has a LeanModifier attached (unarmed, no gun layer)",
                rl.Skeleton.FindChild("LeanModifier", false, false) != null);

            rf.PlayLoop("Idle_0");
            rl.PlayLoop("Idle_0");
            yield return Ticks(4);

            // Control: same clip, same ticks, NO lean on either -> the two rigs must read as the same pose. If this
            // drifts, the differential below is measuring clip desync and any number it prints is noise.
            float sync = TiltBetween(rf, rl);
            T.Check($"control: two unleaned rigs agree ({sync:0.###} deg apart)", sync < 0.5f);

            // Both spines start upright: the Spine->Skull axis is within a couple of degrees of world up. This is the
            // mutation guard -- it proves the instrument reads "upright" as upright, so a later 20 deg is a real tilt
            // and not the reading being broken in both samples.
            float upright = AngleFromUp(rf);
            T.Check($"unleaned spine is upright ({upright:0.###} deg off world up)", upright < 2f);

            rl.LeanDeg = LeanDeg;
            yield return Ticks(4);                   // _ProcessModification runs after the mixer, so give it frames

            // THE ASSERTION: the leaning rig's head has physically swung ~20 deg away from the unleaned one's.
            float tilt = TiltBetween(rf, rl);
            T.Check($"lean {LeanDeg} deg tilts the torso ({tilt:0.##} deg of head swing)",
                Mathf.Abs(tilt - LeanDeg) < 3f);

            // ...and it swings SIDEWAYS (toward the character's own right/left), not forward. A roll about the wrong
            // axis still moves the head by 20 deg and would pass the check above -- this is what separates a lean
            // from a bow. Measured in the rig's local frame, where +X is right and -Z is forward.
            Vector3 dl = SkullDirLocal(rl);
            T.Check($"the swing is lateral, not a forward bow (|x| {Mathf.Abs(dl.X):0.###} vs |z| {Mathf.Abs(dl.Z):0.###})",
                Mathf.Abs(dl.X) > 4f * Mathf.Abs(dl.Z));

            // Sign: + must lean the same way PlayerController's +_leanAngle peeks, or the model leans out from behind
            // the wrong side of the wall from every other player's view.
            T.Check($"+lean goes to the character's LEFT (x {dl.X:0.###})", dl.X < 0f);

            // And it releases. A lean that sticks would leave a permanently hunched model after one peek.
            rl.LeanDeg = 0f;
            yield return Ticks(4);
            float released = TiltBetween(rf, rl);
            T.Check($"lean 0 returns the torso upright ({released:0.###} deg residual)", released < 0.5f);
        }

        // Direction from the Spine bone to the Skull bone in skeleton space -- which way the torso points out of the
        // hips, derived from where the bones ENDED UP rather than from the rotation we asked for.
        //
        // It has to come from RiggedCharacter.LeanSkullDir (sampled inside the modification pass) and NOT from
        // Skeleton3D.GetBoneGlobalPose out here. Godot restores the stored bone pose when the pass ends, so the
        // skeleton an outside caller reads is the UNMODIFIED one: the obvious version of this helper read a
        // perfectly upright spine while the render showed a 20 deg lean, and had I trusted it over the picture I
        // would have "fixed" a feature that was already working.
        static Vector3 SkullDirLocal(RiggedCharacter r) => r.LeanSkullDir;

        static float TiltBetween(RiggedCharacter a, RiggedCharacter b) =>
            Mathf.RadToDeg(SkullDirLocal(a).AngleTo(SkullDirLocal(b)));

        static float AngleFromUp(RiggedCharacter r) =>
            Mathf.RadToDeg(SkullDirLocal(r).AngleTo(Vector3.Up));

        static TN FindDown<TN>(Node n) where TN : Node
        {
            if (n is TN hit) return hit;
            foreach (var c in n.GetChildren())
                if (FindDown<TN>(c) is TN found) return found;
            return null;
        }
    }
}
