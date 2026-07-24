using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // L1 for traps: TrapRule is covered engine-free (L0 TrapRuleTests, 23 cases + a mutation battery), so these
    // guard the parts only the engine can answer -- that a placed trap actually BUILDS a trigger volume, that a
    // body moving into it fires the rule, and that the barricade takes its wear-and-tear.
    //
    // src: InteractableTrap / InteractableTrapTrigger (U3-SDK).

    // Ticks needed to clear the src 0.25 s Trap_Setup_Delay in SIM time, whatever the host's physics rate is.
    static class TrapTiming
    {
        public static int ArmTicks => Mathf.CeilToInt(0.25f * (float)Engine.PhysicsTicksPerSecond) + 4;
    }

    // A zombie walking onto barbed wire takes the .dat Zombie_Damage and chews the trap for WearNormal.
    public class TrapZombieTriggers : GameTest
    {
        public override string Name => "trap.zombie_triggers";
        public override IEnumerable<Step> Run()
        {
            var trap = Deployable.Spawn(World, DeployableDef.Barbedwire, Vector3.Zero, 0f);
            yield return Ticks(1);
            T.Check("barbed wire built a trap trigger", trap.FindChild("*", true, false) != null && FindTrap(trap) != null);
            T.Check($"trap starts at .dat health 70 (got {trap.Health})", Mathf.Abs(trap.Health - 70f) < 0.01f);

            var z = new ZombieController();
            World.AddChild(z);
            z.GlobalPosition = new Vector3(0f, 0f, 6f);   // well clear of the pad
            yield return Ticks(2);

            float zHealthBefore = ZombieHealth(z);
            float trapHealthBefore = trap.Health;

            yield return Ticks(TrapTiming.ArmTicks);   // let the 0.25 s setup delay elapse in SIM time

            z.GlobalPosition = Vector3.Zero;             // step onto the wire
            yield return Ticks(4);                        // Area3D body_entered lands on the next physics frame

            T.Check($"zombie took the wire's 80 damage (health {zHealthBefore} -> {ZombieHealth(z)})",
                    ZombieHealth(z) < zHealthBefore - 1f);
            T.Check($"trap took wear-and-tear 5 (health {trapHealthBefore} -> {trap.Health})",
                    Mathf.Abs(trapHealthBefore - trap.Health - TrapRule.WearNormal) < 0.01f);
        }

        static Trap FindTrap(Node n)
        {
            foreach (var c in n.GetChildren()) if (c is Trap t) return t;
            return null;
        }
        static float ZombieHealth(ZombieController z) => z.Health;
    }

    // The setup delay is real in-engine: a body already standing where the trap is planted must not be bitten
    // in the same instant the barricade appears (src: you can drop a mine at your feet and step off).
    public class TrapSetupDelayInEngine : GameTest
    {
        public override string Name => "trap.setup_delay";
        public override IEnumerable<Step> Run()
        {
            var z = new ZombieController();
            World.AddChild(z);
            z.GlobalPosition = Vector3.Zero;
            yield return Ticks(2);
            float before = z.Health;

            var trap = Deployable.Spawn(World, DeployableDef.Barbedwire, Vector3.Zero, 0f);   // planted ON the zombie
            yield return Ticks(3);                        // inside the 0.25 s setup window

            // teeth: this is a "nothing happened" assertion, so it has to prove the trap was actually ARMED and
            // watching -- otherwise it would pass just as happily against a trap that never built a trigger.
            bool armed = false;
            foreach (var c in trap.GetChildren()) if (c is Trap) armed = true;
            T.Check("the trap really did build a trigger (so the no-damage below means something)", armed);

            T.Check($"zombie unhurt inside the setup window (health {before} -> {z.Health})",
                    Mathf.Abs(z.Health - before) < 0.01f);
            T.Check("trap undamaged too (the contact never latched)", Mathf.Abs(trap.Health - 70f) < 0.01f);
        }
    }

    // An explosive trap detonating blows itself up as well as the victim (src damages the barricade FIRST so the
    // trap dies even at a zero barricade-armor multiplier -- issue #5188). Landmine health is 1, wear is 5.
    public class TrapExplosiveSelfDestructs : GameTest
    {
        public override string Name => "trap.explosive_self_destructs";
        public override IEnumerable<Step> Run()
        {
            var mine = Deployable.Spawn(World, DeployableDef.Landmine, Vector3.Zero, 0f);
            yield return Ticks(1);
            T.Check("landmine carries the .dat explosive block (Range2 8, player 91)",
                    DeployableDef.Landmine.TrapExplosive
                    && Mathf.Abs(DeployableDef.Landmine.TrapRange2 - 8f) < 0.01f
                    && Mathf.Abs(DeployableDef.Landmine.TrapPlayerDamage - 91f) < 0.01f);
            T.Check("landmine is a launcher by the src default (91 * 0.1 = 9.1 > 0.01)",
                    Mathf.Abs(DeployableDef.Landmine.TrapLaunchSpeed - 9.1f) < 0.001f);

            var z = new ZombieController();
            World.AddChild(z);
            z.GlobalPosition = new Vector3(0f, 0f, 5f);
            yield return Ticks(TrapTiming.ArmTicks);                 // arm it (past the 0.25 s setup delay)

            float zBefore = z.Health;
            z.GlobalPosition = Vector3.Zero;
            yield return Ticks(4);

            T.Check($"the blast hurt the zombie (health {zBefore} -> {z.Health})", z.Health < zBefore - 1f);
            // health 1 - wear 5 -> the mine is destroyed/burning, never a survivable barricade
            T.Check("the mine consumed itself (0 HP -> on fire / gone)",
                    !GodotObject.IsInstanceValid(mine) || mine.Health <= 0f || mine.OnFire);
        }
    }
}
