using Godot;
using System.Collections.Generic;
using SDG.Unturned;   // PlayerInventory

namespace UnturnedGodot.Testing
{
    // THE HANDHELD FLASHLIGHT (strawberry: "wire up the flashlight (handheld), with whichever the source light
    // toggle key is").
    //
    // The torch is a MELEE item in retail, not a gun attachment — flashlight.dat is `Type Melee / Useable Melee`
    // with a bare `Light` key, and UseableMelee owns the implementation. So the things that can quietly go wrong
    // are not "does a light exist" but:
    //
    //   1. the FULL-vs-HALF cone angle. The .dat carries Unity's full 90-degree cone; Godot's SpotAngle is the
    //      half-angle. Skip the halving and you get a 180-degree floodlight that still lights the room, still
    //      looks like "the flashlight works", and is twice the beam it should be. A pixel test would pass.
    //   2. the light outliving the item. Eight separate code paths drop the held melee; source kills the light on
    //      unequip. A torch that stays lit after you swap to a rifle is a bug you only notice in the dark.
    //   3. the defaults drifting. flashlight.dat declares NO SpotLight_* keys, so every value comes from the
    //      source defaults — which means a typo in one of them is invisible except as "the torch looks wrong".
    public sealed class HeldFlashlightTests : GameTest
    {
        public override string Name => "melee.held_flashlight";

        static string Dat(string n) => ProjectSettings.GlobalizePath($"res://content/{n}.dat");

        public override IEnumerable<Step> Run()
        {
            // ---- 1. the Light flag is read, and is a PRESENCE test -------------------------------------------
            T.Check("flashlight.dat exists", System.IO.File.Exists(Dat("flashlight")));
            var torch = MeleeDef.FromDatText("flashlight", System.IO.File.ReadAllText(Dat("flashlight")));
            T.Check("the flashlight parses as a light", torch.Light);

            // The contrast case matters: `Light` is a bare valueless key, so a parser that treated it as a bool
            // would read every melee as a light (or none of them).
            var axe = MeleeDef.FromDatText("axe_camp", System.IO.File.ReadAllText(Dat("axe_camp")));
            T.Check("an ordinary axe is NOT a light", !axe.Light);
            T.Check("fists are not a light", !MeleeDef.Fists.Light);

            // ---- 2. source-exact defaults (flashlight.dat declares none of these) -----------------------------
            T.Check($"range 64 ({torch.SpotRange})", Mathf.IsEqualApprox(torch.SpotRange, 64f));
            T.Check($"full cone 90 ({torch.SpotAngleFull})", Mathf.IsEqualApprox(torch.SpotAngleFull, 90f));
            T.Check($"intensity 1.3 ({torch.SpotIntensity})", Mathf.IsEqualApprox(torch.SpotIntensity, 1.3f));
            T.Check($"warm filament tint 245/223/147 ({torch.SpotColor})",
                    Mathf.IsEqualApprox(torch.SpotColor.R, 245f / 255f)
                 && Mathf.IsEqualApprox(torch.SpotColor.G, 223f / 255f)
                 && Mathf.IsEqualApprox(torch.SpotColor.B, 147f / 255f));
            T.Check("spot enabled by default", torch.SpotEnabled);

            // ---- 3. the live toggle, through the real player -------------------------------------------------
            var p = new PlayerController { CaptureMouse = false, Inventory = new PlayerInventory() };
            World.AddChild(p);
            yield return Ticks(2);

            T.Check("bare-handed, nothing claims the light key", !p.HoldingLight);
            p.ToggleHeldLight();
            T.Check("...and toggling with no torch does nothing", !p.HeldLightOn);

            p.EquipHeldMelee("flashlight");
            T.Check("holding the torch claims the light key", p.HoldingLight);
            // Source refuses the toggle while the equip is still playing (player.equipment.isBusy). WAIT for that
            // rather than guessing ticks -- the first run of this test failed on exactly that guard with a fixed
            // 4-tick yield, which is the guard working correctly and the test being wrong.
            T.Check("the torch is not usable mid-equip", !p.HeldItemReady || p.HeldLightOn == false);
            yield return Until(() => p.HeldItemReady, 5);
            T.Check("...and becomes ready once the equip finishes", p.HeldItemReady);
            T.Check("it starts OFF", !p.HeldLightOn);

            p.ToggleHeldLight();
            yield return Ticks(2);
            T.Check("B turns it on", p.HeldLightOn);

            var spot = FindSpot(p);
            T.Check("a real SpotLight3D exists and is visible", spot != null && spot.Visible);
            if (spot != null)
            {
                // THE UNIT BUG. 90 degrees full cone -> 45 half-angle. If someone passes the full angle straight
                // through, this reads 90 and the torch is twice as wide as the source's.
                T.Check($"cone is the HALF-angle, 45 not 90 ({spot.SpotAngle})",
                        Mathf.IsEqualApprox(spot.SpotAngle, 45f));
                T.Check($"range carries the dat value ({spot.SpotRange})", Mathf.IsEqualApprox(spot.SpotRange, 64f));
                T.Check($"energy carries the intensity ({spot.LightEnergy})", Mathf.IsEqualApprox(spot.LightEnergy, 1.3f));
            }

            p.ToggleHeldLight();
            yield return Ticks(2);
            T.Check("B turns it off again", !p.HeldLightOn && (spot == null || !spot.Visible));

            // ---- 4. it does not outlive the item -------------------------------------------------------------
            p.ToggleHeldLight();
            yield return Ticks(2);
            T.Check("lit again before the swap", p.HeldLightOn);
            p.EquipUnarmed();
            yield return Ticks(3);   // the per-frame derived guard has to catch this, not the equip path
            T.Check("swapping off the torch kills the light", !p.HeldLightOn);
            T.Check("...and the spot is actually hidden, not just flagged off",
                    spot == null || !spot.Visible);

            p.QueueFree();
        }

        static SpotLight3D FindSpot(Node n)
        {
            foreach (var c in n.GetChildren())
            {
                if (c is SpotLight3D s) return s;
                if (c is Node sub) { var r = FindSpot(sub); if (r != null) return r; }
            }
            return null;
        }
    }
}
