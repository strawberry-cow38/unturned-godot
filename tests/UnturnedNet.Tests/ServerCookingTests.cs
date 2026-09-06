using System.Collections.Generic;
using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // The SERVER half of cooking (strawberry 2026-09-05). Cooking's own rules are asserted engine-free in
    // UnturnedSim.Tests.CookingTests; what is left here is the behaviour that only exists once a cooker sits
    // on a real crate: the fuel, the detonation, and the refusals.
    [TestFixture]
    public class ServerCookingTests
    {
        [SetUp]
        public void SetUp()
        {
            TransactionalFixtures.RegisterAssets();
            // The shared fixture registers a handful of assets and Canned Beans (13) is one of them, which is
            // why the oven cases work off it. Bread and Charcoal are not, and an unregistered id fails twice
            // over: tryAddItem cannot size it into the grid, and the cook loop skips a jar whose asset is null.
            // Registering them here rather than widening the shared fixture -- these two exist for cooking.
            Assets.add(new ItemAsset { id = 460, itemName = "Bread", size_x = 1, size_y = 1, type = EItemType.FOOD, useFood = 30 });
            Assets.add(new ItemAsset { id = Cooking.CharcoalId, itemName = "Charcoal", size_x = 1, size_y = 1, type = EItemType.SUPPLY });
            Assets.add(new ItemAsset { id = 37, itemName = "Birch Log", size_x = 1, size_y = 2, type = EItemType.SUPPLY });
        }

        static (ServerCooking cook, InventoryReplication inv, InventoryReplication.CrateEntry crate) Rig(ECookerKind kind)
        {
            var inv = new InventoryReplication();
            var crate = inv.ServerRegisterCrate(new NetId(7), 5, 4, new Vector3(1f, 2f, 3f));
            var cook = new ServerCooking(inv, () => 0L);
            cook.Register(7, kind);
            return (cook, inv, crate);
        }

        // ---- THE FUEL BAR (v29) -------------------------------------------------------------------------
        // strawberry 2026-09-06: "as each fuel item burns, show a progress bar before its consumed". The bar's
        // value is server-derived and reaches the client as its own event, so it is only as good as these.

        static List<(uint id, bool on, byte fuel)> Recorded(ServerCooking cook)
        {
            var log = new List<(uint, bool, byte)>();
            cook.StateChanged = (id, on, fuel) => log.Add((id, on, fuel));
            return log;
        }

        [Test]
        public void the_fuel_bar_starts_full_and_empties_as_it_burns()
        {
            var (cook, _, crate) = Rig(ECookerKind.Campfire);
            crate.Storage.tryAddItem(new Item(37, 1));   // one Birch Log
            cook.SetOn(7, true);

            cook.Step(0.1f);   // lights it
            Assert.That(cook.TryGet(7, out var c), Is.True);
            Assert.That(c.FuelTotal, Is.GreaterThan(0f), "a lit log has a burn time to be a fraction OF");
            Assert.That(c.FuelFrac, Is.GreaterThan(240), $"just lit should read nearly full, got {c.FuelFrac}");

            cook.Step(c.FuelTotal * 0.5f);
            Assert.That(c.FuelFrac, Is.InRange(100, 160), $"half burnt should read about half, got {c.FuelFrac}");

            // Burn it well past the end. The bar must read EMPTY, not wrap or go negative -- Fuel goes negative
            // by design (Step subtracts before it checks), so the fraction is where that has to be caught.
            cook.Step(c.FuelTotal * 5f);
            Assert.That(c.FuelFrac, Is.Zero, "nothing left to burn is an empty bar");
        }

        [Test]
        public void a_campfire_that_burns_out_tells_the_opener_it_went_off()
        {
            // THE PATH THIS TEST EXISTS FOR. Running out of fuel sets On=false and takes an early `continue`,
            // so a state push written at the bottom of the cooking loop would never fire for it -- the bar would
            // sit at zero under a button still saying ON until the player closed and reopened the fire.
            var (cook, _, crate) = Rig(ECookerKind.Campfire);
            crate.Storage.tryAddItem(new Item(37, 1));
            cook.SetOn(7, true);
            cook.Step(0.1f);

            var log = Recorded(cook);
            cook.TryGet(7, out var c);
            cook.Step(c.FuelTotal + 1f);   // burn the last of it, with nothing left to light
            // Fuel is spent DOWN in this step and only tested at the top of the NEXT one, so the fire goes out
            // one tick after it runs dry rather than within the tick that empties it. At 50 Hz that is 20 ms and
            // nobody can see it; the test says so explicitly instead of quietly stepping twice.
            cook.Step(0.02f);

            Assert.That(c.On, Is.False, "an unfuelled fire is out");
            Assert.That(log, Is.Not.Empty, "the opener has to hear about it");
            Assert.That(log[log.Count - 1].on, Is.False, "...and the last thing it hears is that it went out");
            Assert.That(log[log.Count - 1].fuel, Is.Zero);
        }

        [Test]
        public void a_still_appliance_says_nothing_at_all()
        {
            // The rate limit, stated as the property that matters: an oven nobody is touching must not emit a
            // message per tick. Without this the "only on change" logic can rot into "every step" and the only
            // symptom is bandwidth, which no other test in this file can see.
            var (cook, _, crate) = Rig(ECookerKind.Oven);
            crate.Storage.tryAddItem(new Item(13));
            cook.SetOn(7, true);
            var log = Recorded(cook);
            for (int i = 0; i < 200; i++) cook.Step(0.02f);
            Assert.That(log, Is.Empty, $"a mains oven has no fuel bar to move, so it had nothing to say ({log.Count} sent)");

            // ...and a burning one speaks, but nowhere near once per tick.
            var (fire, _, fcrate) = Rig(ECookerKind.Campfire);
            fcrate.Storage.tryAddItem(new Item(37, 1));
            fire.SetOn(7, true);
            fire.Step(0.1f);
            var flog = Recorded(fire);
            for (int i = 0; i < 200; i++) fire.Step(0.02f);
            Assert.That(flog.Count, Is.LessThan(200), $"one message per tick is not a rate limit ({flog.Count})");
        }

        [Test]
        public void lighting_the_next_log_refills_the_bar()
        {
            // Two logs: the bar must go back UP when the second catches, not stay flat at zero. This is the
            // "before its consumed" part of the request -- each item gets its own countdown.
            var (cook, _, crate) = Rig(ECookerKind.Campfire);
            crate.Storage.tryAddItem(new Item(37, 2));
            cook.SetOn(7, true);
            cook.Step(0.1f);
            cook.TryGet(7, out var c);
            float total = c.FuelTotal;

            cook.Step(total * 0.95f);
            byte low = c.FuelFrac;
            cook.Step(total * 0.1f);   // spends the rest of the first log (see the tick note above)
            cook.Step(0.02f);          // ...and THIS is the tick that lights the second
            Assert.That(c.On, Is.True, "there was another log, so the fire keeps going");
            Assert.That(c.FuelFrac, Is.GreaterThan(low), $"the bar refills for the next piece ({low} -> {c.FuelFrac})");
        }

        [Test]
        public void a_stack_of_logs_burns_one_at_a_time_and_shrinks_by_one_each_time()
        {
            // strawberry 2026-09-06: "make the fuel system work with this. the fuel burning bar counting for one
            // item, when its up, remove it from the stack." Wood stacks now (ItemCatalog.WireStackableWood), so
            // this pins the behaviour end to end rather than trusting that TryLightNext's `amount--` still means
            // what it meant when nothing could stack.
            var (cook, _, crate) = Rig(ECookerKind.Campfire);
            var logs = new Item(37, 3);   // one jar, three logs in it
            crate.Storage.tryAddItem(logs);
            cook.SetOn(7, true);

            cook.Step(0.1f);
            Assert.That(cook.TryGet(7, out var c), Is.True);
            float total = c.FuelTotal;
            Assert.That(logs.amount, Is.EqualTo(2), "lighting one takes exactly one off the stack, not the jar");
            Assert.That(c.FuelFrac, Is.GreaterThan(240), "and the bar is counting down that ONE log");

            // Burn it out: the next log lights, the stack drops again, the bar refills.
            cook.Step(total); cook.Step(0.02f);
            Assert.That(logs.amount, Is.EqualTo(1), "the second log came off the stack");
            Assert.That(c.On, Is.True);
            Assert.That(c.FuelFrac, Is.GreaterThan(240), "the bar restarts for the new log");

            // The LAST one leaves the jar empty, which has to remove it rather than leave an amount-0 ghost.
            cook.Step(total); cook.Step(0.02f);
            Assert.That(crate.Storage.getItemCount(), Is.Zero, "the emptied stack leaves the grid");
            Assert.That(c.On, Is.True, "still burning the third log");

            cook.Step(total); cook.Step(0.02f);
            Assert.That(c.On, Is.False, "nothing left to light -> the fire goes out");
        }

        [Test]
        public void a_stack_burns_for_its_whole_count_not_a_single_items_worth()
        {
            // The failure this guards is the plausible one: treat the JAR as the unit and a stack of three logs
            // burns for one log's time, so stacking would quietly cost you two thirds of your firewood.
            var (one, _, c1) = Rig(ECookerKind.Campfire);
            c1.Storage.tryAddItem(new Item(37, 1));
            one.SetOn(7, true);
            int singleSteps = 0;
            while (one.TryGet(7, out var a) && a.On && singleSteps < 4000) { one.Step(0.5f); singleSteps++; }

            var (three, _, c3) = Rig(ECookerKind.Campfire);
            c3.Storage.tryAddItem(new Item(37, 3));
            three.SetOn(7, true);
            int stackSteps = 0;
            while (three.TryGet(7, out var b) && b.On && stackSteps < 12000) { three.Step(0.5f); stackSteps++; }

            Assert.That(stackSteps, Is.EqualTo(singleSteps * 3).Within(4),
                        $"three logs should last three times one ({stackSteps} vs {singleSteps})");
        }

        [Test]
        public void an_oven_that_is_off_cooks_nothing()
        {
            var (cook, _, crate) = Rig(ECookerKind.Oven);
            var beans = new Item(13);
            crate.Storage.tryAddItem(beans);
            cook.Step(10f);
            Assert.That(beans.cooked, Is.Zero, "the switch is the whole feature -- an off oven is furniture");
        }

        [Test]
        public void an_oven_that_is_on_cooks_and_then_burns()
        {
            var (cook, _, crate) = Rig(ECookerKind.Oven);
            var beans = new Item(13);
            crate.Storage.tryAddItem(beans);
            cook.SetOn(7, true);

            cook.Step(30f);
            Assert.That(Cooking.IsCooked(beans.cooked), Is.True, $"~31 s in an oven should be done, got {beans.cooked}");
            Assert.That(beans.cookStyle, Is.EqualTo((byte)ECookStyle.Plain), "an oven leaves no label");

            cook.Step(30f);
            Assert.That(Cooking.IsBurnt(beans.cooked), Is.True, "left in, it goes past 100 and burns");
        }

        [Test]
        public void a_toaster_refuses_everything_that_is_not_bread()
        {
            var (cook, _, crate) = Rig(ECookerKind.Toaster);
            var bread = new Item(460);
            var beans = new Item(13);
            crate.Storage.tryAddItem(bread);
            crate.Storage.tryAddItem(beans);
            cook.SetOn(7, true);
            cook.Step(20f);
            Assert.That(bread.cooked, Is.GreaterThan((byte)0), "bread toasts");
            Assert.That(beans.cooked, Is.Zero, "a tin of beans in a toaster does nothing at all");
        }

        [Test]
        public void metal_in_the_microwave_detonates_it_and_switches_it_off()
        {
            var (cook, _, crate) = Rig(ECookerKind.Microwave);
            crate.Storage.tryAddItem(new Item(13));   // Canned Beans -- a tin
            cook.SetOn(7, true);

            var blasts = new List<(Vector3 pos, float r, float dmg)>();
            cook.Detonate = (p, r, d) => blasts.Add((p, r, d));

            var blew = cook.Step(1f);
            Assert.That(blew, Does.Contain(7u), "the microwave reports itself as having gone off");
            Assert.That(blasts, Has.Count.EqualTo(1), "and it actually explodes");
            Assert.That(blasts[0].pos, Is.EqualTo(new Vector3(1f, 2f, 3f)), "at the appliance, not the origin");
            Assert.That(cook.TryGet(7, out var c) && !c.On, Is.True, "a detonated microwave is off afterwards");
        }

        [Test]
        public void the_same_tin_in_an_oven_just_cooks()
        {
            var (cook, _, crate) = Rig(ECookerKind.Oven);
            var beans = new Item(13);
            crate.Storage.tryAddItem(beans);
            cook.SetOn(7, true);
            bool blew = false;
            cook.Detonate = (p, r, d) => blew = true;
            cook.Step(5f);
            Assert.That(blew, Is.False, "only a MICROWAVE objects to metal");
            Assert.That(beans.cooked, Is.GreaterThan((byte)0));
        }

        [Test]
        public void a_barbecue_burns_charcoal_and_switches_itself_off_without_any()
        {
            var (cook, _, crate) = Rig(ECookerKind.Barbecue);
            var steak = new Item(13);
            crate.Storage.tryAddItem(steak);
            cook.SetOn(7, true);

            cook.Step(1f);
            Assert.That(steak.cooked, Is.Zero, "no charcoal -> nothing cooked");
            Assert.That(cook.TryGet(7, out var c1) && !c1.On, Is.True,
                        "and it turns ITSELF off rather than sitting on pretending to be lit");

            crate.Storage.tryAddItem(new Item(Cooking.CharcoalId));
            cook.SetOn(7, true);
            cook.Step(30f);
            Assert.That(Cooking.IsCooked(steak.cooked), Is.True, "with fuel it matches an oven");
            Assert.That(steak.cookStyle, Is.EqualTo((byte)ECookStyle.CharcoalGrilled), "and stamps its own label");
        }

        [Test]
        public void a_campfire_burns_logs_and_refuses_charcoal()
        {
            var (cook, _, crate) = Rig(ECookerKind.Campfire);
            var beans = new Item(13);
            crate.Storage.tryAddItem(beans);
            crate.Storage.tryAddItem(new Item(Cooking.CharcoalId));   // the WRONG fuel for this appliance
            cook.SetOn(7, true);

            cook.Step(1f);
            Assert.That(beans.cooked, Is.Zero, "charcoal is not wood -- the fire never lights");
            Assert.That(cook.TryGet(7, out var c1) && !c1.On, Is.True, "and it switches itself off");

            crate.Storage.tryAddItem(new Item(37));   // Birch Log
            cook.SetOn(7, true);
            cook.Step(50f);
            Assert.That(Cooking.IsCooked(beans.cooked), Is.True, $"a log lights it, got {beans.cooked}");
            Assert.That(beans.cookStyle, Is.EqualTo((byte)ECookStyle.Plain), "no special word for a campfire");
        }

        [Test]
        public void a_forged_toggle_for_a_crate_that_is_not_a_cooker_does_nothing()
        {
            var (cook, _, _) = Rig(ECookerKind.Oven);
            Assert.That(cook.SetOn(999, true), Is.False, "an unregistered NetId is refused...");
            Assert.That(cook.Count, Is.EqualTo(1), "...and does not conjure an appliance out of it");
        }
    }
}
