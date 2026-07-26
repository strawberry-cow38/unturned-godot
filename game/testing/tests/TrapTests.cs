using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Landmine trap: a placed Deployable (IsTrap) is inert for TrapArmDelay (placer grace), then arms and watches
    // TrapTrigger for a zombie; a victim in range detonates it (AoE via DamageTool.explode, covered by the grenade
    // tests) and shatters the mine. Proves the pieces I added: the arming grace, the trigger radius, and detonation.
    public class LandmineArmsAndDetonates : GameTest
    {
        public override string Name => "trap.landmine";
        public override IEnumerable<Step> Run()
        {
            var mine = Deployable.Spawn(World, DeployableDef.Landmine, Vector3.Zero, 0f);
            var z = new ZombieController();
            World.AddChild(z);
            yield return Ticks(3);   // mesh/collision build + the zombie joins the "zombies" group
            T.Check("landmine placed (not yet exploded)", mine != null && !mine.DebugExploded);
            if (mine == null) yield break;

            z.GlobalPosition = new Vector3(0.8f, 0f, 0f);   // INSIDE the 1.4 m trigger (no Ticks after -> it stays put)

            // GRACE: a freshly-planted mine is inert -- a zombie in range must NOT set it off (else you blast yourself)
            mine.DebugTrapCheck();
            T.Check("placer grace: a fresh mine ignores a zombie in range", !mine.DebugExploded);

            mine.DebugAdvanceArm(2f);   // past TrapArmDelay -> armed

            // armed, but a zombie OUT of range still doesn't trigger it
            z.GlobalPosition = new Vector3(5f, 0f, 0f);     // 5 m > 1.4 m
            mine.DebugTrapCheck();
            T.Check("armed but zombie out of range: still armed", !mine.DebugExploded);

            // armed + a zombie IN range detonates it
            z.GlobalPosition = new Vector3(0.8f, 0f, 0f);
            mine.DebugTrapCheck();
            T.Check("armed + zombie in range detonates the mine", mine.DebugExploded);
        }
    }

    // The landmine also arms on a PLAYER (PvP + you can't just walk over your own field). This covers the DETECTION
    // path (TrapVictimNear via PlayerRegistry) without detonating -- the detonation is the same code the zombie test
    // already exercises, so there's no need to blast a bare test player through its damage/UI path.
    public class LandmineDetectsPlayer : GameTest
    {
        public override string Name => "trap.landmine_player";
        public override IEnumerable<Step> Run()
        {
            var player = new PlayerController();
            World.AddChild(player);                              // registers in PlayerRegistry on _EnterTree
            player.GlobalPosition = new Vector3(30f, 0f, 0f);    // far away
            var mine = Deployable.Spawn(World, DeployableDef.Landmine, Vector3.Zero, 0f);
            yield return Ticks(3);
            T.Check("placed", mine != null);
            if (mine == null) yield break;

            T.Check("a player far away is NOT a victim", !mine.DebugVictimNear());

            player.GlobalPosition = new Vector3(0.8f, 0f, 0f);   // inside the 1.4 m trigger
            T.Check("a player in range IS a victim", mine.DebugVictimNear());
        }
    }

    // Source: Landmine.dat is Health 1 + Vulnerable Explosive -- a shot/blast destroys it, which DETONATES it (not a
    // silent crumble). TakeDamage on a trap routes to DetonateTrap.
    public class LandmineDetonatesWhenShot : GameTest
    {
        public override string Name => "trap.landmine_shot";
        public override IEnumerable<Step> Run()
        {
            var mine = Deployable.Spawn(World, DeployableDef.Landmine, Vector3.Zero, 0f);
            yield return Ticks(2);
            T.Check("placed, not yet exploded", mine != null && !mine.DebugExploded);
            if (mine == null) yield break;

            mine.TakeDamage(5f);   // any hit exceeds its 1 HP -> detonate
            T.Check("a shot detonates the mine (Vulnerable Explosive, Health 1)", mine.DebugExploded);
        }
    }

    // The blast also damages nearby placed DEPLOYABLES (src Structure_Damage) -- base-raiding. Detonate a mine next to
    // a generator; the generator loses health. (Other traps are skipped so mines don't chain-recurse.)
    public class LandmineDamagesNearbyDeployable : GameTest
    {
        public override string Name => "trap.landmine_structure";
        public override IEnumerable<Step> Run()
        {
            var gen = Deployable.Spawn(World, DeployableDef.Generator, new Vector3(2f, 0f, 0f), 0f);   // within the 8 m blast
            var mine = Deployable.Spawn(World, DeployableDef.Landmine, Vector3.Zero, 0f);
            yield return Ticks(3);
            T.Check("generator + mine placed", gen != null && mine != null);
            if (gen == null || mine == null) yield break;

            float genHp = gen.Health;
            mine.TakeDamage(5f);   // shoot the mine -> it detonates
            yield return Ticks(1);
            T.Check($"the mine's blast damaged the nearby generator ({genHp:0} -> {gen.Health:0})", gen.Health < genHp);
        }
    }

    // Wooden Spikes (src Spikes_Pine.dat, id 385): a CONTACT trap, not explosive. A zombie that steps onto it gets shredded
    // (Zombie_Damage 60) and the spike WEARS 5 HP (src BarricadeManager.damage 5f) rather than detonating. Proves the
    // contact path: per-victim direct damage + self-wear, no AoE, no self-consume.
    public class SpikeShredsAndWears : GameTest
    {
        public override string Name => "trap.spike_contact";
        public override IEnumerable<Step> Run()
        {
            var spike = Deployable.Spawn(World, DeployableDef.Spike, Vector3.Zero, 0f);
            var z = new ZombieController();
            World.AddChild(z);
            yield return Ticks(3);   // mesh build + the zombie joins the "zombies" group
            T.Check("spike placed (not broken)", spike != null && !spike.DebugExploded);
            if (spike == null) yield break;

            spike.DebugAdvanceArm(1f);   // past the 0.25 s setup delay
            z.GlobalPosition = new Vector3(0.5f, 0f, 0f);   // INSIDE the 1.1 m footprint
            float zhp = z.Health, shp = spike.Health;
            spike.DebugContactTick();

            T.Check($"a zombie on the spike is shredded ({zhp:0} -> {z.Health:0}, ~60)", z.Health <= zhp - 59f);
            T.Check($"the spike wears down per hit ({shp:0} -> {spike.Health:0}, ~5)", Mathf.IsEqualApprox(spike.Health, shp - 5f));
            T.Check("a contact spike does NOT detonate/consume itself", !spike.DebugExploded);
        }
    }

    // src OnTriggerEnter fires once per ENTRY, not every frame you stand on it. The _trapInside set models that: a victim
    // that stays gets hit ONCE; it must leave and re-enter to be hit again.
    public class SpikeHitsOnEntryNotWhileStanding : GameTest
    {
        public override string Name => "trap.spike_enter_once";
        public override IEnumerable<Step> Run()
        {
            var spike = Deployable.Spawn(World, DeployableDef.Spike, Vector3.Zero, 0f);
            var z = new ZombieController();
            World.AddChild(z);
            yield return Ticks(3);
            if (spike == null) yield break;
            spike.DebugAdvanceArm(1f);
            z.Health = 100000f;   // survive many hits so we isolate the ENTER semantics, not zombie death

            z.GlobalPosition = new Vector3(0.5f, 0f, 0f);   // ENTER
            spike.DebugContactTick();
            float afterEnter = z.Health, spikeAfterEnter = spike.Health;

            spike.DebugContactTick();   // STILL inside, no re-entry
            T.Check("standing on the spike does not re-hit the victim", Mathf.IsEqualApprox(z.Health, afterEnter));
            T.Check("standing on the spike does not wear it further", Mathf.IsEqualApprox(spike.Health, spikeAfterEnter));

            z.GlobalPosition = new Vector3(6f, 0f, 0f); spike.DebugContactTick();   // LEAVE (clears the inside-set)
            z.GlobalPosition = new Vector3(0.5f, 0f, 0f); spike.DebugContactTick();  // RE-ENTER
            T.Check("leaving and re-entering hits again", z.Health < afterEnter && spike.Health < spikeAfterEnter);
        }
    }

    // src: each shred wears the trap 5 HP and it's Unrepairable, so a spike is a war of attrition -- Health 40 -> ~8 hits,
    // then it BREAKS (clean, no blast). Prove it grinds down to a break.
    public class SpikeWearsOutAndBreaks : GameTest
    {
        public override string Name => "trap.spike_wears_out";
        public override IEnumerable<Step> Run()
        {
            var spike = Deployable.Spawn(World, DeployableDef.Spike, Vector3.Zero, 0f);
            var z = new ZombieController();
            World.AddChild(z);
            yield return Ticks(3);
            if (spike == null) yield break;
            spike.DebugAdvanceArm(1f);
            z.Health = 100000f;

            for (int i = 0; i < 8 && !spike.DebugExploded; i++)   // 8 entries x 5 HP wear = 40 HP = its whole health
            {
                z.GlobalPosition = new Vector3(6f, 0f, 0f); spike.DebugContactTick();   // out (reset inside-set)
                z.GlobalPosition = new Vector3(0.5f, 0f, 0f); spike.DebugContactTick();  // in -> one shred + 5 wear
            }
            T.Check("the spike wears out and breaks after ~8 hits", spike.DebugExploded);
        }
    }

    // The spike also shreds a PLAYER that steps on it (src Player_Damage 30). Covers the player victim branch.
    public class SpikeShredsPlayer : GameTest
    {
        public override string Name => "trap.spike_player";
        public override IEnumerable<Step> Run()
        {
            var player = new PlayerController();
            World.AddChild(player);
            player.GlobalPosition = new Vector3(30f, 0f, 0f);   // far away at first
            var spike = Deployable.Spawn(World, DeployableDef.Spike, Vector3.Zero, 0f);
            yield return Ticks(3);
            if (spike == null) yield break;
            spike.DebugAdvanceArm(1f);

            float php = player.Health;
            player.GlobalPosition = new Vector3(0.5f, 0f, 0f);   // step onto it
            spike.DebugContactTick();
            T.Check($"a player on the spike is shredded ({php:0} -> {player.Health:0}, ~30)", player.Health <= php - 29f);
        }
    }

    // A contact trap is NOT explosive: shooting it just BREAKS it -- unlike the landmine, it must NOT blast nearby
    // deployables. Contrast with LandmineDamagesNearbyDeployable.
    public class SpikeBreaksWithoutBlast : GameTest
    {
        public override string Name => "trap.spike_no_blast";
        public override IEnumerable<Step> Run()
        {
            var gen = Deployable.Spawn(World, DeployableDef.Generator, new Vector3(1.5f, 0f, 0f), 0f);
            var spike = Deployable.Spawn(World, DeployableDef.Spike, Vector3.Zero, 0f);
            yield return Ticks(3);
            T.Check("generator + spike placed", gen != null && spike != null);
            if (gen == null || spike == null) yield break;

            float genHp = gen.Health;
            spike.TakeDamage(100f);   // exceeds its 40 HP -> break
            yield return Ticks(1);
            T.Check("shooting the spike breaks it", spike.DebugExploded);
            T.Check($"breaking a spike does NOT blast the neighbour ({genHp:0} -> {gen.Health:0})", Mathf.IsEqualApprox(gen.Health, genHp));
        }
    }
}
