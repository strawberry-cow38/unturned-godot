using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Port of --heartest: a zombie reacts to the LOUDEST+CLOSEST sound it can hear (salience = loudness - dist),
    // ignoring sounds outside its HearingRange sphere or too quiet to carry that far; and while committed to a loud
    // sound it stays on task unless something LOUDER shows up (master's hearing rework).
    public class ZombieHearSalience : GameTest
    {
        public override string Name => "zombie.hear_salience";
        public override IEnumerable<Step> Run()
        {
            var z = new ZombieController();
            World.AddChild(z);   // _Ready: joins the "zombies" group, HearingRange 48
            z.GlobalPosition = Vector3.Zero;
            yield return Ticks(1);

            // all Hear calls + the readback happen inside ONE tick, like the old inline test
            z.Hear(new Vector3(10, 0, 0), 12f);   // dist 10 <= 12 loud  -> heard, salience 2
            z.Hear(new Vector3(5, 0, 0), 6f);     // dist 5  <= 6  loud  -> heard, salience 1
            z.Hear(new Vector3(40, 0, 0), 48f);   // dist 40 <= 48 loud  -> heard, salience 8 (LOUD gunshot beats near footsteps)
            z.Hear(new Vector3(3, 0, 0), 2f);     // dist 3  >  2  loud  -> IGNORED (too quiet to carry)
            z.Hear(new Vector3(60, 0, 0), 64f);   // dist 60 >  48 range -> IGNORED (outside the ears)
            var (pos, sal) = z.DebugHeard();
            T.Check($"winner is the loud gunshot at (40,0,0) sal 8 (got {pos} sal {sal:0.##})",
                pos.DistanceTo(new Vector3(40, 0, 0)) < 0.01f && Mathf.Abs(sal - 8f) < 0.01f);

            // stay-on-task gate: committed to salience 8, a quieter footstep must NOT override, a louder shot must
            T.Check("ignores a quieter footstep while on task", !z.DebugWouldOverride(8f, new Vector3(5, 0, 0), 6f));
            T.Check("switches to a louder gunshot", z.DebugWouldOverride(8f, new Vector3(10, 0, 0), 48f));
        }
    }

    // Regression for the MP-report "zombie face renders on the LEFT ARM" (#36): the face decal's
    // BoneAttachment3D must bind to the Skull bone and the quad must ride that bone, not an arm.
    // IsPuppet = the MP path; the rig build is shared with SP zombies, so this guards both.
    // Assertions are pose-invariant on purpose -- see the note by the distance checks.
    public class ZombieFaceOnSkull : GameTest
    {
        public override string Name => "zombie.face_on_skull";
        public override IEnumerable<Step> Run()
        {
            var z = new ZombieController { IsPuppet = true };
            World.AddChild(z);
            z.GlobalPosition = Vector3.Zero;
            yield return Ticks(1);   // rig enters the tree

            // Wait for the rig to stop moving before sampling. Deliberately a SETTLE condition ("position stopped
            // changing"), not "position reached the value we're about to assert" -- the latter would make the
            // assertion tautological.
            Vector3 prev = Vector3.Inf; int stable = 0;
            yield return Until(() =>
            {
                var fq = FindFaceQuad(z);
                if (fq == null) return false;
                Vector3 p = fq.GlobalPosition;
                stable = p.DistanceSquaredTo(prev) < 1e-10f ? stable + 1 : 0;
                prev = p;
                return stable >= 3;          // three consecutive identical samples = the pose has come to rest
            }, maxSimSeconds: 5);

            Skeleton3D skel = FindDown<Skeleton3D>(z);
            T.Check("puppet zombie has a skeleton", skel != null);
            if (skel == null) yield break;
            int skull = skel.FindBone("Skull");
            T.Check($"skeleton has a Skull bone (idx {skull})", skull >= 0);

            BoneAttachment3D att = null; MeshInstance3D face = null;
            foreach (var c in skel.GetChildren())
                if (c is BoneAttachment3D ba && ba.GetNodeOrNull<MeshInstance3D>("Face") is MeshInstance3D fq) { att = ba; face = fq; }
            T.Check("face decal quad exists under a BoneAttachment3D", att != null && face != null);
            if (att == null || face == null) yield break;

            T.Check($"face attachment bound to the Skull bone (BoneIdx {att.BoneIdx}, Skull {skull})", att.BoneIdx == skull);
            // Position is asserted RELATIVE TO THE SKULL BONE, not in character space.
            //
            // This was `local.Y > 1.5 && local.Y < 2.0` -- head height in character space -- and it failed on a
            // correctly-bound rig about a third of the time. Measured cause, not guessed: the rig comes to rest
            // in one of two exact reproducible poses (upright, skull at y=1.32; or hunched, skull at y=0.25) and
            // the upright one has mirrored variants (face x = +0.112 / -0.112). So the old check was asserting
            // WHICH ANIMATION PHASE the zombie happened to be sampled in -- nothing to do with bone binding.
            //
            // (An earlier theory that this was a partial/unfinished pose was wrong: waiting for the pose to
            // settle does not fix it, because the hunched pose is fully settled. dist(face -> Skull) measured
            // 0.50 in EVERY run, passing and failing alike -- the binding was never once broken.)
            //
            // That distance is the real invariant, so assert it. Bug #36 ("face renders on the LEFT ARM") would
            // put the quad on Left_Arm -- tiny distance to the arm, large one to the skull -- which both checks
            // below catch in any pose. Verified by re-binding the attachment to Left_Arm: FAIL, "skull 0.72 vs
            // L-arm 0.50".
            Vector3 faceInSkel = skel.ToLocal(face.GlobalPosition);
            float dSkull = faceInSkel.DistanceTo(skel.GetBoneGlobalPose(skull).Origin);
            int leftArm = skel.FindBone("Left_Arm"), rightArm = skel.FindBone("Right_Arm");
            float dLeftArm  = leftArm  >= 0 ? faceInSkel.DistanceTo(skel.GetBoneGlobalPose(leftArm).Origin)  : float.MaxValue;
            float dRightArm = rightArm >= 0 ? faceInSkel.DistanceTo(skel.GetBoneGlobalPose(rightArm).Origin) : float.MaxValue;

            T.Check($"face quad rides the Skull bone rigidly (dist {dSkull:0.00}, expected ~0.5 in every pose)",
                    dSkull < 0.75f);
            T.Check($"face quad is on the skull, NOT an arm (skull {dSkull:0.00} vs L-arm {dLeftArm:0.00}, R-arm {dRightArm:0.00})",
                    dSkull < dLeftArm && dSkull < dRightArm);
        }

        /// <summary>The face decal quad under whichever BoneAttachment3D carries it, or null if the rig
        /// hasn't built it yet. Used by the settle-wait above and mirrors the lookup the assertions do.</summary>
        static MeshInstance3D FindFaceQuad(Node root)
        {
            var sk = FindDown<Skeleton3D>(root);
            if (sk == null) return null;
            foreach (var c in sk.GetChildren())
                if (c is BoneAttachment3D ba && ba.GetNodeOrNull<MeshInstance3D>("Face") is MeshInstance3D fq)
                    return fq;
            return null;
        }

        static TN FindDown<TN>(Node n) where TN : Node
        {
            if (n is TN hit) return hit;
            foreach (var c in n.GetChildren())
                if (FindDown<TN>(c) is TN found) return found;
            return null;
        }
    }

    // The other half of #36 -- the ACTUAL root cause: the zombie atlases had the 16x16 face texture BAKED
    // into texels u[0.254-0.371] v[0.563-0.625] (x 32-46, y 72-79 of the 128 atlas), which the bake scripts
    // believed was the head-front quad. It isn't: the mesh triangles sampling that rect are skinned to
    // Left_Arm/Spine (the head-front UV is a skin-only sliver elsewhere), so every zombie wore the face as
    // a decal ON THE LEFT ARM. The pre-fix bake signature was ALL face-opaque texels byte-equal to the
    // NEAREST-resized face_19; this guards that no atlas carries that stamp again.
    public class ZombieAtlasNoArmFace : GameTest
    {
        public override string Name => "zombie.atlas_no_arm_face";
        public override IEnumerable<Step> Run()
        {
            yield return Ticks(1);   // pure content assert, but give the host its expected first step
            var face = Image.LoadFromFile(ProjectSettings.GlobalizePath("res://content/face_19.png"));
            T.Check("face_19.png loads", face != null);
            if (face == null) yield break;
            face.Resize(15, 8, Image.Interpolation.Nearest);   // the exact rect the old bake stamped: 15x8 at (32,72)

            for (int i = 0; i <= 5; i++)
            {
                var atlas = Image.LoadFromFile(ProjectSettings.GlobalizePath($"res://content/zombie_atlas_{i}.png"));
                T.Check($"zombie_atlas_{i}.png loads (128x128)", atlas != null && atlas.GetWidth() == 128 && atlas.GetHeight() == 128);
                if (atlas == null) continue;
                int opaque = 0, exact = 0;
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 15; x++)
                    {
                        Color fp = face.GetPixel(x, y);
                        if (fp.A8 <= 8) continue;   // only the face's opaque texels (eyes + mouth) betray the stamp
                        opaque++;
                        if ((atlas.GetPixel(32 + x, 72 + y).ToRgba32() >> 8) == (fp.ToRgba32() >> 8)) exact++;
                    }
                // pre-fix every atlas matched 14/14 exactly; a legit garment coinciding on half of them is implausible
                T.Check($"zombie_atlas_{i}: face NOT baked into the Left_Arm rect ({exact}/{opaque} texels match)", opaque > 0 && exact < opaque / 2);
            }
        }
    }
}
