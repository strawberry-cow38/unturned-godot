using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // The landmine also arms on a PLAYER (PvP + you can't just walk over your own field). This covers the DETECTION
    // path (TrapVictimNear via PlayerRegistry) without detonating -- there's no need to blast a bare test player
    // through its damage/UI path just to prove the trigger radius.
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

    // A Detonator fires the charge (Deployable.DetonateManual). That path detonates it -- the remote-trigger core.
    public class ChargeDetonatesOnCommand : GameTest
    {
        public override string Name => "trap.charge_detonate";
        public override IEnumerable<Step> Run()
        {
            var charge = Deployable.Spawn(World, DeployableDef.Charge, Vector3.Zero, 0f);
            yield return Ticks(2);
            T.Check("placed, not yet blown", charge != null && !charge.DebugExploded);
            if (charge == null) yield break;

            charge.DetonateManual();   // what a Detonator plunge calls
            T.Check("DetonateManual blows the charge", charge.DebugExploded);
        }
    }

    // Health 1 + Vulnerable: a shot/blast destroys a charge, which DETONATES it (explosive), same as the landmine.
    public class ChargeDetonatesWhenShot : GameTest
    {
        public override string Name => "trap.charge_shot";
        public override IEnumerable<Step> Run()
        {
            var charge = Deployable.Spawn(World, DeployableDef.Charge, Vector3.Zero, 0f);
            yield return Ticks(2);
            if (charge == null) yield break;
            T.Check("placed, not yet blown", !charge.DebugExploded);
            charge.TakeDamage(5f);   // exceeds its 1 HP -> detonate
            T.Check("a shot detonates the charge", charge.DebugExploded);
        }
    }

    // Charges are for RAIDING: the blast does huge Structure_Damage (1000) to nearby placed deployables -- far more than a
    // landmine (75). Detonate a charge next to a generator; it should be devastated (not merely dinged).
    public class ChargeDevastatesStructures : GameTest
    {
        public override string Name => "trap.charge_raid";
        public override IEnumerable<Step> Run()
        {
            var gen = Deployable.Spawn(World, DeployableDef.Generator, new Vector3(2f, 0f, 0f), 0f);   // within the 8 m blast
            var charge = Deployable.Spawn(World, DeployableDef.Charge, Vector3.Zero, 0f);
            yield return Ticks(3);
            T.Check("generator + charge placed", gen != null && charge != null);
            if (gen == null || charge == null) yield break;

            float genHp = gen.Health;
            charge.DetonateManual();
            yield return Ticks(1);
            T.Check($"the charge devastates the generator ({genHp:0} -> {gen.Health:0}, big Structure_Damage)", gen.Health <= genHp - 300f);
        }
    }

    // DetonateAllCharges (a Detonator plunge) fires EVERY placed charge at once. Place three; all blow. (Each blast skips
    // other traps, so they don't chain-detonate early -- src Proof_Explosion.)
    public class DetonatorFiresAllCharges : GameTest
    {
        public override string Name => "trap.charge_detonate_all";
        public override IEnumerable<Step> Run()
        {
            var a = Deployable.Spawn(World, DeployableDef.Charge, new Vector3(-4f, 0f, 0f), 0f);
            var b = Deployable.Spawn(World, DeployableDef.Charge, new Vector3(0f, 0f, 0f), 0f);
            var c = Deployable.Spawn(World, DeployableDef.Charge, new Vector3(4f, 0f, 0f), 0f);
            yield return Ticks(3);
            T.Check("three charges placed", a != null && b != null && c != null);
            if (a == null || b == null || c == null) yield break;

            int n = Deployable.DetonateAllCharges(World.GetTree());
            T.Check($"DetonateAllCharges fired every charge (n={n})", n >= 3);
            T.Check("all three charges blew", a.DebugExploded && b.DebugExploded && c.DebugExploded);
        }
    }

    // The Detonator ITEM (id 1240, charge increment B) is a held TOOL on the wire/rope/hose rail: equip it ->
    // HoldingDetonatorTool; LMB (TryDetonateCharges) plunges -> fires every placed remote Charge at once. This is the
    // in-hand trigger end of the remote-charge feature (the charge itself was cf1abbc1).
    public class DetonatorItemFiresCharges : GameTest
    {
        public override string Name => "trap.detonator";
        public override IEnumerable<Step> Run()
        {
            T.Check("the Detonator dispatches as a tool (id 1240)", ToolDef.ById(1240) == ToolDef.Detonator);
            var player = new PlayerController { CaptureMouse = false };
            World.AddChild(player);
            var a = Deployable.Spawn(World, DeployableDef.Charge, new Vector3(-3f, 0f, 0f), 0f);
            var b = Deployable.Spawn(World, DeployableDef.Charge, new Vector3(3f, 0f, 0f), 0f);
            yield return Ticks(3);
            T.Check("two charges placed + inert", a != null && b != null && !a.DebugExploded && !b.DebugExploded);
            if (a == null || b == null) yield break;

            player.EquipDetonator();   // tool rail -> viewmodel drives HoldingDetonatorTool
            yield return Ticks(1);
            T.Check("equipping the detonator puts it in hand (HoldingDetonatorTool)", player.HoldingDetonatorTool);

            player.TryDetonateCharges();   // the LMB plunge
            T.Check("the detonator plunge fires every placed charge", a.DebugExploded && b.DebugExploded);
        }
    }
}
