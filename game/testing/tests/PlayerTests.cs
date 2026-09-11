using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Port of the --pronetest PronetestDriver: force each stance via ScriptedStance and read the stealth detection
    // radius zombies sense the player by (source PlayerStance DETECT_STAND/CROUCH/PRONE/SPRINT constants).
    public class PlayerStanceStealthRadius : GameTest
    {
        public override string Name => "player.stance_stealth_radius";
        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            var stances = new[] { SDG.Unturned.EPlayerStance.STAND, SDG.Unturned.EPlayerStance.CROUCH, SDG.Unturned.EPlayerStance.PRONE, SDG.Unturned.EPlayerStance.SPRINT };
            var expect = new float[] { 12f, 6f, 3f, 20f };
            yield return Ticks(3);   // land on the plane before reading stances
            for (int i = 0; i < stances.Length; i++)
            {
                p.ScriptedStance = stances[i];
                yield return Ticks(4);   // let the movement sim apply the stance (the old driver waited 3 + read next tick)
                float r = p.GetStealthDetectionRadius();
                T.Check($"{stances[i]} radius {r:0.#} (expect {expect[i]:0.#})", Mathf.Abs(r - expect[i]) < 0.01f);
            }
        }
    }

    // Port of the --falldemo FallTestDriver: drop the player from 40 m onto the ground plane; PlayerLife.onLanded
    // fires on the landing frame (impact speed well over the 22 m/s threshold) and cuts health.
    public class PlayerFallDamage : GameTest
    {
        public override string Name => "player.fall_damage";
        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0f, 40f, 0f));
            float start = p.Health;
            yield return Until(() => p.Health < start, maxSimSeconds: 8);
            T.Check($"landing cut health ({start:0} -> {p.Health:0})", p.Health < start);
        }
    }

    // Port of the --brokentest BrokenTestDriver: a 40 m fall breaks legs -> a forced SPRINT is demoted to STAND
    // (radius 12, not the SPRINT 20) -> a Medkit (Bones_Modifier Heal) mends -> sprint works again (radius 20).
    public class PlayerBrokenLegsMend : GameTest
    {
        public override string Name => "player.broken_legs_mend";
        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0f, 40f, 0f));   // _Ready registers the catalog, so Assets.find(15) resolves
            yield return Until(() => p.Broken, maxSimSeconds: 8);
            T.Check($"hard fall broke legs (health={p.Health:0})", p.Broken);

            p.ScriptedStance = SDG.Unturned.EPlayerStance.SPRINT;
            yield return Ticks(4);
            float r = p.GetStealthDetectionRadius();
            T.Check($"broken legs block sprint (radius {r:0} expect 12)", Mathf.Abs(r - 12f) < 0.01f);

            p.ScriptedStance = null;
            p.Consume(SDG.Unturned.Assets.find(15));   // Medkit: Bones_Modifier Heal
            yield return Ticks(2);
            T.Check("Medkit mended legs", !p.Broken);

            p.ScriptedStance = SDG.Unturned.EPlayerStance.SPRINT;
            yield return Ticks(4);
            r = p.GetStealthDetectionRadius();
            T.Check($"sprint restored after heal (radius {r:0} expect 20)", Mathf.Abs(r - 20f) < 0.01f);
        }
    }
    // strawberry 2026-09-10: "make sure that we can use rags, dressing, bandages, medkits, suturekits to fix
    // bleeding" / "make sure a splint fixes a broken leg".
    //
    // They all already worked -- the flags come from content/consumable_stats.tsv columns 7 and 8
    // (Bleeding_Modifier / Bones_Modifier Heal) and both consume paths clear them, PlayerController.Consume
    // locally and ServerTransactions -> ServerRaise over the wire. What was missing is anything PINNING the
    // data: a row edited or a column shifted in that tsv silently removes a cure, and nothing would fail.
    // That matters more now bleeding actually costs health than it did while it was a HUD icon.
    public class MedicalCuresArePinned : GameTest
    {
        public override string Name => "player.medical_cures_pinned";
        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0f, 1f, 0f));   // _Ready registers the catalog
            yield return Ticks(2);

            // (ushort id, name) -> must stop bleeding. Named individually rather than "every MEDICAL item",
            // because the point is that THESE specific ones work; a loop over the category would keep passing
            // if the Rag lost its flag and something else kept one.
            foreach (var (id, name) in new (ushort, string)[]
                     { (393, "Rag"), (95, "Bandage"), (394, "Dressing"), (403, "Suturekit"), (15, "Medkit") })
            {
                var a = SDG.Unturned.Assets.find(id);
                T.Check($"{name} ({id}) exists", a != null);
                T.Check($"{name} stops bleeding", a != null && a.useStopsBleeding);
            }

            foreach (var (id, name) in new (ushort, string)[] { (96, "Splint"), (15, "Medkit"), (388, "Morphine") })
            {
                var a = SDG.Unturned.Assets.find(id);
                T.Check($"{name} ({id}) exists", a != null);
                T.Check($"{name} mends broken legs", a != null && a.useHealBroken);
            }

            // CONTROL: a plain food item must do neither, or the two checks above pass on an asset table that
            // has simply set every flag.
            var beans = SDG.Unturned.Assets.find(19);
            T.Check("a food item is not a dressing", beans == null || (!beans.useStopsBleeding && !beans.useHealBroken));

            // ...and the cure actually clears the flag, not just carries it.
            p.Bleeding = true;
            p.Consume(SDG.Unturned.Assets.find(393));   // Rag
            yield return Ticks(2);
            T.Check("a Rag stopped the bleeding", !p.Bleeding);
        }
    }

}
