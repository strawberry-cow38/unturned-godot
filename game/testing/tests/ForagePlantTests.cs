using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>Berry bushes and mushrooms, client side (master 2026-09-10: "wire up harvestable berry bushes
    /// and mushrooms. check how the real game does it. make sure the harvest path goes through the server").
    ///
    /// The server half is covered at L0 in ForageTests. What is left here is the half that decides whether the
    /// server is ever reached, and the half that can quietly stop being server-owned: RequestForage must SEND
    /// and do nothing else. A local grant would look right on the picker's screen and be reverted by the next
    /// authoritative echo -- the shape that produced the shelf-take and magazine bugs, both of which passed
    /// every client-side assertion while the authoritative object never changed.
    ///
    /// The reward table is asserted against the retail .dats rather than against itself: those ids are read
    /// out of Bundles/Trees/&lt;Name&gt;/&lt;Name&gt;.dat and are not ours to choose.</summary>
    public sealed class ForagePlantTests : GameTest
    {
        public override string Name => "forage.plants";
        public override double TimeoutSimSeconds => 20;

        public override IEnumerable<Step> Run()
        {
            yield return Ticks(1);

            // ---- THE RETAIL TABLE. Reward_ID off each resource's own .dat.
            // ⚠ These are the items the reward SPAWN TABLE resolves to, not the Reward_ID in the resource
            // .dat. The first cut of this used the raw Reward_ID and handed out cosmetics -- 963 is a
            // BattlEye Halo in the item space, 901 a Cowboy Top -- because retail runs it through
            // SpawnTableTool.ResolveLegacyId(id, EAssetType.ITEM). Each of the ten tables holds exactly one
            // entry at weight 100, so the resolution is deterministic and checkable.
            var want = new (string Name, ushort Reward)[]
            {
                ("Bush_Amber", 270), ("Bush_Indigo", 271), ("Bush_Jade", 272), ("Bush_Mauve", 115),
                ("Bush_Russet", 273), ("Bush_Teal", 274), ("Bush_Vermillion", 275), ("Bush_Hanu", 571),
                ("Mushroom_Brown_0", 1932), ("Mushroom_Red_0", 1934),
            };
            foreach (var (n, r) in want)
                T.Check($"{n} gives retail item {r} (got {ResourceField.ForageReward(n)})", ResourceField.ForageReward(n) == r);
            T.Check("Yukon's snowy variants share their green twin's reward",
                    ResourceField.ForageReward("Bush_Amber_Snow") == 270 && ResourceField.ForageReward("Bush_Hanu_Snow") == 571);

            // ---- AND NOT A COSMETIC. The Reward_ID values themselves are real items in the ITEM id space --
            // a BattlEye Halo, a Cowboy Top, a Militia Jacket -- so handing one out looks like a working
            // feature right up until you open the bag. Each is rejected by name.
            foreach (var (n, bad) in new (string, ushort)[]
                     { ("Bush_Amber", 963), ("Bush_Indigo", 964), ("Bush_Jade", 965), ("Bush_Mauve", 966),
                       ("Bush_Russet", 967), ("Bush_Teal", 968), ("Bush_Vermillion", 969), ("Bush_Hanu", 903),
                       ("Mushroom_Brown_0", 901), ("Mushroom_Red_0", 902) })
                T.Check($"{n} does not hand out the raw Reward_ID ({bad}, a cosmetic)", ResourceField.ForageReward(n) != bad);

            // ---- WHAT IS NOT FORAGEABLE. Bush_0/Bush_1 carry no Forage key, no Reward_ID and no Explosion in
            // retail: they are scenery. Trees and ore nodes have their own harvest paths and must not answer
            // this one -- a tree that reported forageable would be pickable for a berry by pressing F at it.
            foreach (var n in new[] { "Bush_0", "Bush_1", "Birch_0", "Maple_1", "Pine_0", "Metal_0", "Clay_2", "Cane_00", "Snow_Pile_00", "" })
                T.Check($"{(n.Length > 0 ? n : "(empty)")} is not forageable", !ResourceField.IsForageable(n));
            T.Check("a null name does not throw", !ResourceField.IsForageable(null));

            // ---- A PLANT IS NOT A WALL. Its body sits on its own look-ray bit, never the world layer: a bush
            // you cannot walk through would change how the whole map moves, and one that stops bullets would
            // turn undergrowth into cover.
            var plant = new ForagePlant { Field = null, Index = 12, ResourceName = "Bush_Amber", Reward = 270,
                                          WorldPos = Vector3.Zero, CollisionLayer = ForagePlant.HitLayer };
            World.AddChild(plant);
            yield return Ticks(1);
            T.Check("the forage body is on its own layer", plant.CollisionLayer == ForagePlant.HitLayer);
            T.Check("...which is not the world layer", (plant.CollisionLayer & 1u) == 0);
            T.Check("...nor the dropped-item layer the look SPHERE assists on",
                    (plant.CollisionLayer & WorldItem.ItemHitLayer) == 0);

            // ---- REQUESTING IT SENDS, AND DOES NOTHING ELSE.
            var p = new PlayerController { CaptureMouse = false, Inventory = new SDG.Unturned.PlayerInventory() };
            World.AddChild(p);
            yield return Ticks(1);

            T.Check("with no server attached, F on a bush is refused rather than silently eaten",
                    !p.RequestForage(plant));

            int sent = -1, calls = 0;
            p.NetForageResource = i => { sent = i; calls++; };
            int bagBefore = p.Inventory.getItemCount(270);
            T.Check("with a server attached, the request is made", p.RequestForage(plant));
            T.Check($"...carrying the plant's INDEX (sent {sent}, expected 12)", sent == 12);
            T.Check("...exactly once", calls == 1);
            // THE POINT OF THE WHOLE TEST: the client asked and got nothing. Everything below is what a local
            // grant would have changed, and each would be reverted by the next authoritative echo.
            T.Check("the client granted itself NO item", p.Inventory.getItemCount(270) == bagBefore);
            T.Check("...and did not hide the plant either -- the harvested event does that",
                    plant.Alive);

            T.Check("a null plant is refused", !p.RequestForage(null));

            // ---- THE SOUND IT MAKES. Bushes and mushrooms share retail effect 43's Foliage rustle. The clip
            // that shipped under that name was the WRONG FILE (175584 bytes against the effect's real 120416,
            // and that prefab has exactly one AudioSource, so it was not a second source) -- which nothing
            // caught while nothing played it. Length-checked because that is what actually distinguishes them.
            var rustle = GameAudio.ResourceBreak("Bush_Amber");
            double rl = rustle != null ? rustle.GetLength() : -1;
            T.Check($"a bush rustles ({rl:0.00}s)", rustle != null && rl > 0.1);
            T.Check($"...with effect 43's clip, not the mislabelled 0.91s one", rl < 0.8);
            T.Check("mushrooms share it", ReferenceEquals(GameAudio.ResourceBreak("Mushroom_Red_0"), rustle));
            T.Check("...and it is NOT the tree's timber crash", !ReferenceEquals(GameAudio.ResourceBreak("Birch_0"), rustle));

            plant.QueueFree();
            p.QueueFree();
        }
    }
}
