using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Port of the --meleedemo MeleeTestDriver: a NORMAL zombie (100 HP) held 1.2 m dead ahead; the player swings the
    // equipped melee every tick (the per-weapon cooldown gates the cadence) until the kill registers.
    public class CombatMeleeKill : GameTest
    {
        public override string Name => "combat.melee_kill";
        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var p = new PlayerController { CaptureMouse = false };
            p.EquipHeldMelee("knife_military");   // real .dat range/damage
            World.AddChild(p);
            p.GlobalPosition = new Vector3(0f, 1f, 0f);

            var z = new ZombieController { Target = p, Speciality = ZombieController.ESpeciality.NORMAL };
            World.AddChild(z);
            z.GlobalPosition = p.GlobalPosition + new Vector3(0f, 0.2f, -1.4f);

            for (int i = 0; i < 250 && p.Kills == 0; i++)
            {
                if (!z.Dead) z.GlobalPosition = p.GlobalPosition + new Vector3(0f, 0f, -1.2f);   // keep it in reach of the (facing -Z) player
                if (i > 5) p.MeleeAttack();
                yield return Ticks(1);
            }
            T.Check($"zombie killed by melee (Kills={p.Kills})", p.Kills > 0 && z.Dead);
        }
    }

    // Port of the --grenadetest GrenadeTestDriver: zombies at increasing ranges from a blast point; detonate
    // radius-8 / 175-damage and confirm each zombie's health matches the source linear falloff 175*(1 - range/8).
    // Then a real FUSED grenade (fly+fuse+detonate chain) kills a point-blank zombie.
    public class CombatGrenadeFalloff : GameTest
    {
        public override string Name => "combat.grenade_falloff";
        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(100f, 1f, 0f));   // parked far away so the zombies stay idle

            var ranges = new float[] { 4f, 6f, 7.5f, 9f };
            var zs = new ZombieController[ranges.Length];
            for (int i = 0; i < ranges.Length; i++)
            {
                zs[i] = new ZombieController { Target = p, Speciality = ZombieController.ESpeciality.NORMAL };
                World.AddChild(zs[i]);
                zs[i].GlobalPosition = new Vector3(ranges[i], 0f, 0f);
            }
            var zThrow = new ZombieController { Target = p, Speciality = ZombieController.ESpeciality.NORMAL };
            World.AddChild(zThrow);
            zThrow.GlobalPosition = new Vector3(100f, 0f, -2f);

            yield return Ticks(4);   // settle; capture each zombie's ACTUAL range at detonation (idle AI may drift them)
            var atBlast = new float[zs.Length];
            for (int i = 0; i < zs.Length; i++) atBlast[i] = zs[i].GlobalPosition.DistanceTo(Vector3.Zero);
            p.Explode(Vector3.Zero, 8f, 175f, 175f, 100f);
            yield return Ticks(2);

            for (int i = 0; i < zs.Length; i++)
            {
                float r = atBlast[i];
                float expDmg = r > 8f ? 0f : 175f * (1f - r / 8f);
                float expHp = 100f - expDmg;
                float hp = zs[i].Dead ? 0f : zs[i].Health;
                T.Check($"falloff at r={r:0.00}: health {hp:0.0} (expect ~{expHp:0.0})", Mathf.Abs(hp - expHp) < 0.6f);
            }

            var g = new Grenade { Thrower = p, Fuse = 0.2f, Vel = Vector3.Zero };   // point-blank, short fuse
            World.AddChild(g);
            g.GlobalPosition = zThrow.GlobalPosition + Vector3.Up * 0.2f;
            yield return Until(() => zThrow.Dead, maxSimSeconds: 2);
            T.Check("fused throw killed the point-blank zombie", zThrow.Dead);
        }
    }
}
