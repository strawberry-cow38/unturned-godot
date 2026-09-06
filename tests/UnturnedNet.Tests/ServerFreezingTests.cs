using System.Collections.Generic;
using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    /// <summary>The freezer sweep (strawberry 2026-09-06). FreezingTests owns the rules; this owns the part that
    /// decides WHICH grid an item is in and therefore what happens to it.</summary>
    [TestFixture]
    public class ServerFreezingTests
    {
        [SetUp]
        public void SetUp()
        {
            TransactionalFixtures.RegisterAssets();
            Assets.add(new ItemAsset { id = 461, itemName = "Steak", size_x = 1, size_y = 1, type = EItemType.FOOD, useFood = 40 });
        }

        static (ServerFreezing fz, InventoryReplication inv, InventoryReplication.CrateEntry crate) Fridge()
        {
            var inv = new InventoryReplication();
            var crate = inv.ServerRegisterCrate(new NetId(11), 5, 3, new Vector3(0f, 0f, 0f));
            inv.ServerAddFreezer(11, 4, 2);
            return (new ServerFreezing(inv), inv, crate);
        }

        [Test]
        public void the_freezer_freezes_and_the_fridge_body_does_not()
        {
            // The single most important property: which COMPARTMENT an item sits in decides its fate. If both
            // grids froze, "a second container above the fridge container" would be decoration.
            var (fz, _, crate) = Fridge();
            var cold = new Item(461); crate.Freezer.tryAddItem(cold);
            var warm = new Item(461); crate.Storage.tryAddItem(warm);

            for (int i = 0; i < 20; i++) fz.Step(0.5f);

            Assert.That(cold.frozen, Is.GreaterThan(0), "the freezer compartment must actually freeze");
            Assert.That(warm.frozen, Is.Zero, "the fridge body keeps food cold, it does not freeze it");
        }

        [Test]
        public void a_steak_taken_out_of_the_freezer_thaws()
        {
            // "frozen % drops when not in a freezer container" -- tested by MOVING the item, because that is how
            // a player does it, and because the sweep has no per-item memory to get this wrong with.
            var (fz, _, crate) = Fridge();
            var steak = new Item(461);
            crate.Freezer.tryAddItem(steak);
            for (int i = 0; i < 200; i++) fz.Step(0.5f);
            Assert.That(steak.frozen, Is.EqualTo(Freezing.Max), "should be solid after standing in a freezer");

            // Move it to the fridge body and keep stepping.
            crate.Freezer.removeItem(0);
            crate.Storage.tryAddItem(steak);
            for (int i = 0; i < 10; i++) fz.Step(0.5f);
            Assert.That(steak.frozen, Is.LessThan(Freezing.Max), "out of the freezer it starts thawing");
        }

        [Test]
        public void an_unpowered_freezer_thaws_instead_of_freezing()
        {
            // A freezer that keeps working with the power cut would make the fridge's own power requirement
            // pointless, and would be a strictly better container than a powered one is expensive.
            var (fz, _, crate) = Fridge();
            fz.HasPower = _ => false;
            var steak = new Item(461) { frozen = 80 };
            crate.Freezer.tryAddItem(steak);
            for (int i = 0; i < 10; i++) fz.Step(0.5f);
            Assert.That(steak.frozen, Is.LessThan(80), "no power, no cold");
        }

        [Test]
        public void what_a_player_is_carrying_thaws_but_their_open_freezer_view_is_not_swept_twice()
        {
            // The subtle one. A crate the player has OPEN is aliased into their FREEZER page by CopyPage -- the
            // SAME Item objects, not clones. So if the owner sweep also walked the external pages, every item in
            // an open freezer would be stepped twice per tick: once as the crate's, once as the player's, and a
            // freezer would run at half speed exactly while somebody was watching it.
            var (fz, inv, crate) = Fridge();
            inv.ServerAdd(1, 0L);
            var carried = new Item(461) { frozen = 50 };
            Assert.That(inv.TryGet(1, out var owner), Is.True);
            owner.Inventory.tryAddItem(carried);

            var stored = new Item(461);
            crate.Freezer.tryAddItem(stored);
            Assert.That(inv.ServerOpenStorage(1, 11, new Vector3(0f, 0f, 0f), 0L), Is.True, "open the fridge");

            byte before = stored.frozen;
            fz.Step(1f);
            byte gained = (byte)(stored.frozen - before);

            Assert.That(carried.frozen, Is.LessThan(50), "a steak in your bag thaws");

            // Close it and take the same step with nobody looking: the rate must be identical.
            Assert.That(inv.ServerCloseStorage(1, 1L), Is.True);
            byte before2 = stored.frozen;
            fz.Step(1f);
            Assert.That((byte)(stored.frozen - before2), Is.EqualTo(gained),
                        "a freezer must freeze at the same rate whether or not its door is open");
        }

        [Test]
        public void it_thaws_at_the_real_tick_rate_and_not_only_at_a_convenient_one()
        {
            // THE BUG THIS EXISTS FOR, and it had already shipped in spirit. `frozen` is a whole-number percent
            // and the sweep runs at 2 Hz, so one thaw step is 0.8 %/s * 0.5 s = 0.4 % -- which round-to-nearest
            // threw away every single time. Food froze (2 %/s clears the half-unit) and then NEVER thawed, at
            // any dt the game actually uses. The earlier tests missed it only because they stepped 0.5 s
            // repeatedly and I read the result as "the rule is wrong" rather than "the step is too small".
            //
            // So this pins the REAL dt: ContainerNetSync steps freezing at DivisorTicks/50 = 0.5 s.
            const float RealDt = 25f / 50f;
            var (fz, _, crate) = Fridge();
            var steak = new Item(461) { frozen = 100 };
            crate.Storage.tryAddItem(steak);   // fridge body: thaws

            for (int i = 0; i < 4; i++) fz.Step(RealDt);
            Assert.That(steak.frozen, Is.LessThan(100),
                        "two seconds at the game's own tick rate has to move the number at all");

            // ...and it keeps moving all the way down rather than stalling at some rounding fixpoint.
            for (int i = 0; i < 600; i++) fz.Step(RealDt);
            Assert.That(steak.frozen, Is.Zero, "it must reach fully thawed, not stall part-way");
        }

        [Test]
        public void the_thaw_rate_is_the_stated_one_not_the_rounded_one()
        {
            // Carrying the remainder is what makes the rate exact; flooring without it would run ~20 % slow and
            // nothing would notice. 100 % at 0.8 %/s is 125 s of thawing.
            const float RealDt = 25f / 50f;
            var (fz, _, crate) = Fridge();
            var steak = new Item(461) { frozen = 100 };
            crate.Storage.tryAddItem(steak);

            int steps = 0;
            while (steak.frozen > 0 && steps < 2000) { fz.Step(RealDt); steps++; }
            float seconds = steps * RealDt;
            Assert.That(seconds, Is.EqualTo(100f / Freezing.ThawPerSecond).Within(2f),
                        $"a full thaw should take ~{100f / Freezing.ThawPerSecond:0} s, took {seconds:0}");
        }

        [Test]
        public void an_ice_box_freezes_its_whole_body_not_a_compartment()
        {
            // master 2026-09-06: "turn the ice box into a smart container that acts as a freezer". A fridge is a
            // chilled body with a freezer compartment above it; an ice merchandiser has no warm half at all, so
            // the distinction under test is that its MAIN grid freezes rather than thaws.
            var inv = new InventoryReplication();
            var crate = inv.ServerRegisterCrate(new NetId(21), 6, 4, new Vector3(0f, 0f, 0f));
            Assert.That(inv.ServerMakeFreezerBody(21), Is.True);
            var fz = new ServerFreezing(inv);

            var steak = new Item(461);
            crate.Storage.tryAddItem(steak);
            for (int i = 0; i < 40; i++) fz.Step(0.5f);
            Assert.That(steak.frozen, Is.GreaterThan(0), "the body of an ice box is the freezer");

            // A PLAIN crate is the control -- same sweep, same item, and it must NOT freeze. Without this the
            // check above passes if everything in the world freezes.
            var plain = inv.ServerRegisterCrate(new NetId(22), 6, 4, new Vector3(5f, 0f, 0f));
            var other = new Item(461);
            plain.Storage.tryAddItem(other);
            for (int i = 0; i < 40; i++) fz.Step(0.5f);
            Assert.That(other.frozen, Is.Zero, "an ordinary crate is not cold");
        }

        [Test]
        public void an_unpowered_ice_box_thaws_what_is_in_it()
        {
            // Cutting the grid to a stocked freezer has to be a real loss, not a cosmetic one -- the body falls
            // back to thawing on the same gate a compartment uses.
            var inv = new InventoryReplication();
            var crate = inv.ServerRegisterCrate(new NetId(23), 6, 4, new Vector3(0f, 0f, 0f));
            inv.ServerMakeFreezerBody(23);
            var fz = new ServerFreezing(inv) { HasPower = _ => false };

            var steak = new Item(461) { frozen = 90 };
            crate.Storage.tryAddItem(steak);
            for (int i = 0; i < 20; i++) fz.Step(0.5f);
            Assert.That(steak.frozen, Is.LessThan(90), "no power, no cold -- even in a box that is nothing but a freezer");
        }

        [Test]
        public void nothing_that_is_not_food_ever_gets_a_frozen_value()
        {
            var (fz, _, crate) = Fridge();
            var gun = new Item(4);   // fixture asset, not FOOD
            crate.Freezer.tryAddItem(gun);
            for (int i = 0; i < 50; i++) fz.Step(1f);
            Assert.That(gun.frozen, Is.Zero, "a frozen rifle is not a mechanic");
        }
    }
}
