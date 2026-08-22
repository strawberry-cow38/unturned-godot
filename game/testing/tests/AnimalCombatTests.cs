using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Animals used to be bare Node3Ds: nothing to collide with, nothing a bullet ray could hit, no health. This pins
    // the fix (strawberry 2026-08-22 "fix animals, they have no collision, cant take damage"): a hittable capsule on
    // the same enemy layer ZombieController uses (so the gun ray masks it AND the player body stops on it), health
    // that drains, and a corpse that drops off that layer on death. Facing (the third bug) is verified by --animaltest.
    public sealed class AnimalCombatTests : GameTest
    {
        public override string Name => "animal.combat";
        public override double TimeoutSimSeconds => 30;

        static AnimalAgent Spawn(Node w, float hp)
        {
            var a = new AnimalAgent { Foot = 0.7f, Home = Vector3.Zero, Seed = 12345u, Health = hp };   // Terr/Rig null: this exercises the body + damage logic, not the visual
            w.AddChild(a);
            a.GlobalPosition = new Vector3(0f, 0.7f, 0f);
            a.Begin();
            return a;
        }

        public override IEnumerable<Step> Run()
        {
            // 1. COLLISION: a real capsule on the enemy bit (1<<1) -- the layer the gun ray masks and the player body
            //    collides with. A bare Node3D had neither, which is exactly "no collision, cant take damage".
            var a = Spawn(World, 100f);
            T.Check("animal sits on the enemy collision layer (1<<1), not 0", a.CollisionLayer == (1u << 1));
            bool hasCapsule = false;
            foreach (var c in a.GetChildren()) if (c is CollisionShape3D cs && cs.Shape is CapsuleShape3D) hasCapsule = true;
            T.Check("animal has a capsule CollisionShape3D so bullets/players actually hit it", hasCapsule);
            T.Check("animal joined the 'animals' group (melee + blast sweep + net publish)", a.IsInGroup("animals"));

            // 2. TAKES DAMAGE: a non-fatal hit drains health, stays alive + hittable.
            a.DamageHit(30f, a.GlobalPosition, Vector3.Forward);
            T.Check($"a 30-dmg hit drops health 100 -> ~70 (got {a.Health:0})", Mathf.Abs(a.Health - 70f) < 0.5f);
            T.Check("...it survives the graze", !a.Dead);
            T.Check("...and stays on the enemy layer while alive", a.CollisionLayer == (1u << 1));

            // 3. DIES: a lethal hit flags Dead, drops the capsule to layer 0 (a corpse: rounds pass to the ragdoll
            //    bones instead of the hull capsule), and leaves the live group so nothing sweeps it again.
            a.DamageHit(999f, a.GlobalPosition, Vector3.Forward);
            T.Check("a lethal hit kills it", a.Dead);
            T.Check("...corpse capsule drops to layer 0", a.CollisionLayer == 0);
            T.Check("...and it leaves the 'animals' group", !a.IsInGroup("animals"));
            T.Check("...a hit on the corpse is a no-op (health does not go further negative)", NoOpOnCorpse(a));
            a.QueueFree();
            yield return Ticks(1);

            // 4. GROUNDING: a live agent walks and re-grounds every frame off the collision raycast (GroundY) --
            //    verify that path runs each _Process tick without dying or erroring (Terr-less here -> falls back).
            var b = Spawn(World, 100f);
            b.DamageHit(1f, new Vector3(0f, 0f, 6f), Vector3.Back);   // graze from +Z -> it bolts toward -Z, so it's walking + re-grounding
            yield return Ticks(6);
            T.Check("a walking animal runs the ground raycast each frame without dying/erroring", !b.Dead && IsInstanceValid(b));
            b.QueueFree();
        }

        static bool NoOpOnCorpse(AnimalAgent a)
        {
            float before = a.Health;
            a.DamageHit(50f, a.GlobalPosition, Vector3.Forward);
            return Mathf.Abs(a.Health - before) < 0.001f;   // DamageHit early-returns once Dead
        }
    }
}
