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
        }

        static (ServerCooking cook, InventoryReplication inv, InventoryReplication.CrateEntry crate) Rig(ECookerKind kind)
        {
            var inv = new InventoryReplication();
            var crate = inv.ServerRegisterCrate(new NetId(7), 5, 4, new Vector3(1f, 2f, 3f));
            var cook = new ServerCooking(inv, () => 0L);
            cook.Register(7, kind);
            return (cook, inv, crate);
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
        public void a_forged_toggle_for_a_crate_that_is_not_a_cooker_does_nothing()
        {
            var (cook, _, _) = Rig(ECookerKind.Oven);
            Assert.That(cook.SetOn(999, true), Is.False, "an unregistered NetId is refused...");
            Assert.That(cook.Count, Is.EqualTo(1), "...and does not conjure an appliance out of it");
        }
    }
}
