using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Verifies the F6 skeletons-cut instrument (RiggedCharacter.SetAnimFrozen). It must ACTUALLY halt the
    // AnimationMixer's per-tick bone posing -- otherwise master's F6 read measures nothing and the engine-side
    // "bodies vs skeletons vs nav" cut is garbage-in (the exact trap that burned us today: never trust an
    // instrument you haven't seen work). z.rig only times the no-op Tick(); the real posing is engine-side (the
    // mixer runs in the PHYSICS callback via UsePhysicsAnimRate), so we prove the freeze by watching the SKELETON'S
    // POSE stop changing, not by any script timer. Also proves it resumes -- a temporarily-frozen (culled) zombie
    // that never un-froze would stay a statue the moment you walked up to it.
    public class ZombieAnimFreezeHaltsPosing : GameTest
    {
        public override string Name => "zombie.anim_freeze";
        public override IEnumerable<Step> Run()
        {
            RiggedCharacter.SetAnimFrozen(false);   // known state (a prior test could have left the global on)

            var z = new ZombieController();
            World.AddChild(z);                      // _Ready builds the rig
            z.GlobalPosition = Vector3.Zero;
            yield return Ticks(10);                  // rig builds + the mixer starts advancing

            var rig = FindDown<RiggedCharacter>(z);
            T.Check("zombie built a rig + skeleton",
                rig != null && rig.Skeleton != null && rig.Skeleton.GetBoneCount() > 0);
            if (rig == null || rig.Skeleton == null || rig.Skeleton.GetBoneCount() == 0) yield break;

            rig.PlayLoop("Move_0");                  // definite, continuous bone motion to freeze against
            yield return Ticks(2);

            // mutation guard: the mixer is genuinely posing bones each tick BEFORE we freeze (else the test is vacuous)
            float a = PoseSum(rig.Skeleton);
            yield return Ticks(4);
            float b = PoseSum(rig.Skeleton);
            T.Check($"mixer IS posing before freeze (Sigma pose {a:0.###} -> {b:0.###})", Mathf.Abs(a - b) > 1e-4f);

            // THE INSTRUMENT: SetAnimFrozen(true) sets AnimationMixer.Active=false -> posing must stop dead
            RiggedCharacter.SetAnimFrozen(true);
            yield return Ticks(1);                    // let the freeze take
            float c = PoseSum(rig.Skeleton);
            yield return Ticks(6);
            float d = PoseSum(rig.Skeleton);
            T.Check($"FROZEN halts posing (Sigma pose {c:0.####} held over 6 ticks, drift {Mathf.Abs(c - d):0.000000})",
                Mathf.Abs(c - d) < 1e-5f);

            // and it resumes, or a culled zombie you walk toward stays a frozen statue
            RiggedCharacter.SetAnimFrozen(false);
            yield return Ticks(1);
            float e = PoseSum(rig.Skeleton);
            yield return Ticks(4);
            float f = PoseSum(rig.Skeleton);
            T.Check($"UNFROZEN resumes posing (Sigma pose {e:0.###} -> {f:0.###})", Mathf.Abs(e - f) > 1e-4f);

            RiggedCharacter.SetAnimFrozen(false);   // leave clean for the next test
        }

        // Sum of every bone's local pose (position magnitude + |quat components|). Any bone the mixer moves changes
        // this; a halted mixer leaves it constant. Robust to which clip/bone is animating.
        static float PoseSum(Skeleton3D s)
        {
            float sum = 0f;
            int n = s.GetBoneCount();
            for (int i = 0; i < n; i++)
            {
                Vector3 p = s.GetBonePosePosition(i);
                Quaternion q = s.GetBonePoseRotation(i);
                sum += p.Length() + Mathf.Abs(q.X) + Mathf.Abs(q.Y) + Mathf.Abs(q.Z) + Mathf.Abs(q.W);
            }
            return sum;
        }

        static TN FindDown<TN>(Node n) where TN : Node
        {
            if (n is TN hit) return hit;
            foreach (var c in n.GetChildren())
                if (FindDown<TN>(c) is TN found) return found;
            return null;
        }
    }
}
