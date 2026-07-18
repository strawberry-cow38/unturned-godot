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
    // BoneAttachment3D must actually bind to the Skull bone, and the quad must sit at the head-front
    // (~(0,1.75,-0.25) in character space), not at the shoulder/arm. IsPuppet = the MP path; the rig
    // build is shared with SP zombies, so this guards both.
    public class ZombieFaceOnSkull : GameTest
    {
        public override string Name => "zombie.face_on_skull";
        public override IEnumerable<Step> Run()
        {
            var z = new ZombieController { IsPuppet = true };
            World.AddChild(z);
            z.GlobalPosition = Vector3.Zero;
            yield return Ticks(3);   // let the rig enter the tree + the skeleton pose/attachment update run

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
            Vector3 local = z.ToLocal(face.GlobalPosition);
            T.Check($"face quad sits at the head-front, not the arm (local {local})",
                Mathf.Abs(local.X) < 0.15f && local.Y > 1.5f && local.Y < 2.0f && local.Z < -0.1f);
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
