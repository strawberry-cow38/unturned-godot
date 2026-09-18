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
            // DETECT_SCALE 0.4 (strawberry 2026-09-17 "reduce em by a lot") and prone's own near-silent crawl
            // value ("prone should be almost zero"). Retail's 12/6/3/20 is still in StealthDetection; these are
            // what the game ships. Absolutes on purpose -- see the note in CombatMathTests on why the arithmetic
            // must not be written back into the assertion.
            var expect = new float[] { 4.8f, 2.4f, 0.25f, 8f };
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

    // ⭐ SUSPENDED IS NOT FALLING (strawberry 2026-09-15: "i wedged myself between two crates (not stuck) i was
    // suspended for a bit, but then when i landed i took fall damage and legs broke").
    //
    // Gravity accumulates into the SIM's velocity every airborne tick. Wedged between two crates the capsule is
    // not on a floor, so that number runs away toward terminal (-100 m/s) while the body is held by the geometry
    // and actually moves nowhere -- and the entire fictional speed is cashed in the moment you touch down.
    //
    // The control is the two tests above: a real 40 m drop must STILL hurt. A fix that simply stopped booking
    // fall damage would pass this test and fail those, which is why it is worth having all three.
    public class PlayerWedgedTakesNoFallDamage : GameTest
    {
        public override string Name => "player.wedged_no_fall_damage";
        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);

            // Two slabs with a gap narrower than the capsule, well above the ground: the player lands on their
            // inner faces and is held there, off the floor, exactly as the crates did it.
            foreach (float x in new[] { -0.45f, 0.45f })
            {
                var wall = new StaticBody3D { CollisionLayer = 1u << 0 };
                wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.6f, 6f, 4f) } });
                World.AddChild(wall);
                wall.GlobalPosition = new Vector3(x, 4f, 0f);
            }

            var p = Rigs.Player(World, new Vector3(0f, 6.5f, 0f));
            float start = p.Health;
            yield return Ticks(4);

            // Hang there long enough that the ACCUMULATOR would have reached terminal velocity -- 100 m/s at
            // 29.43 m/s^2 is ~3.4 s, so 5 s leaves no doubt it is pinned at the worst possible value.
            //
            // ⚠ Ticks, NOT `Until(() => false, 5)`. That reads like "wait 5 seconds" and is actually a harness
            // FAILURE: Until aborts the test when it times out, so every check below it was skipped and the test
            // reported red while the fix underneath was fine. 250 = 5 s x the 50 Hz in project.godot -- derived,
            // because a literal tick count is the thing that goes stale if the rate ever moves.
            yield return Ticks(5 * 50);

            T.Check($"suspended between two slabs, still at full health ({p.Health:0}/{start:0})", p.Health >= start);
            T.Check("...and legs are not broken", !p.Broken);
            T.Check($"...while never having touched the ground (y={p.GlobalPosition.Y:0.0} above 1)",
                    p.GlobalPosition.Y > 1f);
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
            // ⚠ Asserted as a RELATIONSHIP, not the literal it used to carry. What this test is about is that
            // asking to SPRINT on broken legs gets you the STANDING radius instead -- which is a fact about the
            // stance falling back, not about any particular number. It was pinned to a bare 12 and so broke on a
            // retune that changed nothing it cares about; written this way it survives the next one and still
            // fails for the reason it exists (a sprint that was NOT blocked).
            float stand = SDG.Unturned.StealthDetection.Radius(SDG.Unturned.EPlayerStance.STAND, moving: false);
            float sprint = SDG.Unturned.StealthDetection.Radius(SDG.Unturned.EPlayerStance.SPRINT, moving: false);
            T.Check($"broken legs block sprint (radius {r:0.##} == standing {stand:0.##}, not sprinting {sprint:0.##})",
                    Mathf.Abs(r - stand) < 0.01f && Mathf.Abs(r - sprint) > 0.01f);

            p.ScriptedStance = null;
            p.Consume(SDG.Unturned.Assets.find(15));   // Medkit: Bones_Modifier Heal
            yield return Ticks(2);
            T.Check("Medkit mended legs", !p.Broken);

            p.ScriptedStance = SDG.Unturned.EPlayerStance.SPRINT;
            yield return Ticks(4);
            r = p.GetStealthDetectionRadius();
            // The mirror of the check above, and a relationship for the same reason: healing restores SPRINT, so
            // the radius becomes the sprinting one and is no longer the standing one. Both halves matter -- equal
            // to sprint alone would pass if every stance collapsed to one number.
            T.Check($"sprint restored after heal (radius {r:0.##} == sprinting {sprint:0.##}, not standing {stand:0.##})",
                    Mathf.Abs(r - sprint) < 0.01f && Mathf.Abs(r - stand) > 0.01f);
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
