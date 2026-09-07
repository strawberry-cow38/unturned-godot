using NUnit.Framework;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    /// <summary>Dragging goggles onto the glasses slot has to come back through the echo.
    ///
    /// master 2026-09-07: "night vision isnt working, neither is headlamp". Both read the SAME
    /// Inventory.wornGlasses and all three items are GLASSES-slot, so one empty slot kills both -- it presents
    /// as two bugs and is at most one.
    ///
    /// The sim half is already proven green in-engine (player.vision_slot: wear, predicate, toggle, swap,
    /// unwear, 19 checks) and the goggle shader renders standalone with no player at all. What neither covers
    /// is the path a PLAYER actually uses: the inventory UI does not set the slot locally when the inventory
    /// is server-owned -- it sends CommandWearClothing and waits for the owner echo to bring the slot back.
    /// Singleplayer runs through the loopback, so that is the live path in normal play, and it was the one
    /// stretch with no test on it at all.
    ///
    /// This drives exactly that: the item sits in the grid, the client asks, the SERVER's slot has to fill and
    /// the OWNER REPLICA has to receive it. Asserting only the server would pass while the client -- the side
    /// that actually decides whether your goggles switch on -- still saw nothing.</summary>
    [TestFixture]
    public class WearGlassesEchoTests
    {
        const ushort MilitaryNvgId = 334;

        [SetUp]
        public void SetUp()
        {
            TransactionalFixtures.RegisterAssets();
            Assets.add(new ItemAsset { id = MilitaryNvgId, itemName = "Military Nightvision",
                                       size_x = 2, size_y = 1, type = EItemType.GLASSES });
        }

        static (byte page, byte x, byte y)? Find(NetWorldServer server, ushort pid, ushort id)
        {
            if (!server.Inventories.TryGet(pid, out var e)) return null;
            for (byte p = 0; p < e.Inventory.items.Length; p++)
            {
                var pg = e.Inventory.items[p];
                for (byte i = 0; i < pg.getItemCount(); i++)
                {
                    var j = pg.getItem(i);
                    if (j?.item != null && j.item.id == id) return (p, j.x, j.y);
                }
            }
            return null;
        }

        [Test]
        public void dragging_nightvision_onto_the_slot_comes_back_through_the_echo()
        {
            var h = new TransactionalHarness(4477).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(MilitaryNvgId));
            h.Step(10);

            var cell = Find(h.Server, a.PlayerId, MilitaryNvgId);
            Assert.That(cell, Is.Not.Null, "the goggles are in the server grid to be worn from");

            Assert.That(h.Server.Inventories.TryGet(a.PlayerId, out var se), Is.True);
            Assert.That(se.Inventory.wornGlasses, Is.Null, "nothing worn to start");

            a.SendWearClothing(cell.Value.page, cell.Value.x, cell.Value.y, (byte)EItemType.GLASSES);
            Assert.That(h.StepUntil(() => se.Inventory.wornGlasses != null), Is.True,
                        $"the SERVER put them on (seed={h.Net.Seed})");
            Assert.That(se.Inventory.wornGlasses.id, Is.EqualTo(MilitaryNvgId));

            // THE HALF THAT MATTERS. WearingNightvision reads the CLIENT's inventory, so a server-only pass
            // would be a green test over a player whose goggles still do nothing.
            Assert.That(h.StepUntil(() => a.Inventories.TryGet(a.PlayerId, out var ce)
                                          && ce.Inventory.wornGlasses?.id == MilitaryNvgId), Is.True,
                        $"...and the owner replica received the worn slot (seed={h.Net.Seed})");

            // and it leaves the grid, or you are wearing a copy of an item you still have
            Assert.That(Find(h.Server, a.PlayerId, MilitaryNvgId), Is.Null,
                        "the goggles left the grid when they went on your face");
        }
    }
}
