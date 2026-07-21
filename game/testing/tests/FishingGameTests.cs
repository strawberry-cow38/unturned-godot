using System.Collections.Generic;
using Godot;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // In-engine fishing (increment 2): equipping the rod routes through EquipItemAsset's EItemType.FISHER branch and
    // arms the FishingSim; a full press -> charge -> release -> bobber-into-water -> bite -> press-in-window loop lands
    // a real fish into the player's inventory and pays fishing XP. Drives the REAL controller path (FisherPrimary/
    // Release/TickFishing) via test seams, since a headless run has no mouse. Terrain water is static -> restored at end.
    public class FishingCastBiteCatch : GameTest
    {
        public override string Name => "fishing.cast_bite_catch";
        public override IEnumerable<Step> Run()
        {
            bool hadWater = Terrain.HasWater;
            float oldSea = Terrain.WaterSurfaceY;

            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0f, 30f, 0f));   // up high; the ocean sits just below
            p.Inventory = new PlayerInventory();
            yield return Ticks(2);                                  // _Ready registers the item catalog

            var rod = Assets.find(503);
            T.Check($"rod 503 loaded as FISHER (type={rod?.type})", rod != null && rod.type == EItemType.FISHER);

            p.EquipItemAsset(rod, new Item(503));
            T.Check("equipping the rod -> HoldingFisher", p.HoldingFisher);

            // ocean surface just under the player so the cast bobber splashes down quickly
            Terrain.HasWater = true;
            Terrain.WaterSurfaceY = 28f;

            // charge the gauge, then fling (deterministic fast bite for the test)
            p.FisherPrimaryForTest();                              // Idle -> PreparingToCast
            var sim = p.FisherSimForTest;
            sim.MinBiteInterval = sim.MaxBiteInterval = 0.2f;
            for (int i = 0; i < 12; i++) p.TickFishingForTest(0.02f);   // build strength
            p.FisherReleaseForTest();                             // -> Casting (bobber spawns next tick)

            // fly the bobber down into the water -> LineDeployed
            bool deployed = false;
            for (int i = 0; i < 400 && !deployed; i++) { p.TickFishingForTest(0.02f); deployed = sim.State == EFishingState.LineDeployed; }
            T.Check($"bobber reached water -> line deployed (state={sim.State})", deployed);

            // wait out the bite timer
            bool bit = false;
            for (int i = 0; i < 400 && !bit; i++) { p.TickFishingForTest(0.02f); bit = sim.HasBite; }
            T.Check("a fish bit the line", bit);

            // let the catch window open (1.0s warning), then hook it
            bool windowOpen = false;
            for (int i = 0; i < 200 && !windowOpen; i++) { p.TickFishingForTest(0.02f); windowOpen = sim.IsBiteWindowOpen; }
            T.Check("the catch window opened", windowOpen);

            uint xpBefore = p.Skills.experience;
            int fishBefore = TotalFish(p.Inventory);
            p.FisherPrimaryForTest();                            // press in the window -> Rod_Fishing opens the catch challenge
            T.Check($"the bite opened the catch challenge (state={sim.State})", sim.State == EFishingState.CatchChallenge);

            // play the minigame: track the fish with the cursor (hold to rise, release to fall) until it's landed.
            // GrantFish fires from TickFishing's pending-catch drain on the winning tick.
            for (int i = 0; i < 4000 && sim.State == EFishingState.CatchChallenge; i++)
            {
                float cursorCenter = sim.ChallengeCursorPos + sim.ChallengeCursorSizeNorm / 2f;
                if (cursorCenter < sim.ChallengeFishPos) p.FisherPrimaryForTest(); else p.FisherReleaseForTest();
                p.TickFishingForTest(0.02f);
            }

            T.Check($"tracked + landed a fish into the bag (fish {fishBefore} -> {TotalFish(p.Inventory)})", TotalFish(p.Inventory) == fishBefore + 1);
            T.Check($"fishing paid XP ({xpBefore} -> {p.Skills.experience})", p.Skills.experience > xpBefore);
            T.Check($"line reeled back to idle (state={sim.State})", sim.State == EFishingState.Idle);

            // switching to another item tears the rod down (LMB must fire the gun, not fish)
            p.EquipUnarmed();
            T.Check("equipping away from the rod clears HoldingFisher", !p.HoldingFisher);

            Terrain.HasWater = hadWater;         // restore static water -> don't leak into later tests
            Terrain.WaterSurfaceY = oldSea;
        }

        static int TotalFish(PlayerInventory inv)
        {
            int n = 0;
            foreach (ushort id in new ushort[] { 1349, 504, 505, 1351, 1959 }) n += inv.getItemCount(id);
            return n;
        }
    }
}
