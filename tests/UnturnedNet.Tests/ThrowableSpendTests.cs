using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // A THROW TAKES THE THING OUT OF YOUR BAG.
    //
    // ⚠ IT DID NOT, AND THE REASON IS WORTH KEEPING. PlayerController.ReleaseThrow routed the deletion the way a
    // finished consumable does -- NetConsume -> ServerTransactions.OnConsume -- and that handler opens with
    // `if (asset == null || !asset.IsConsumable) { Diag.ConsumesRejected++; return; }`. A grenade is not a
    // consumable, so the server refused every one of them, silently. The THROW itself was accepted by a
    // completely different handler (ServerCombat.OnGrenade), so the projectile flew, the blast landed, and the
    // item stayed in the bag. Two handlers, one of which worked, and nothing that made them agree.
    //
    // Singleplayer runs through the loopback, so this was never an MP-only bug: every grenade, smoke and flare
    // in the game was infinite (strawberry 2026-09-13: "smoke grenades, flares not being consumed after thrown").
    //
    // The spend now happens INSIDE OnGrenade -- the handler that mints the projectile is the one that pays for
    // it, so the two can no longer disagree about whether a throw happened.
    [TestFixture]
    public class ThrowableSpendTests
    {
        const ushort FragId = 254, FlareId = 255, SmokeId = 261;

        [SetUp]
        public void RegisterAssets()
        {
            TransactionalFixtures.RegisterAssets();   // clears the process-wide catalog; the throwables go in after
            Assets.add(new ItemAsset { id = FragId,  itemName = "Fragmentation Grenade", size_x = 1, size_y = 1 });
            Assets.add(new ItemAsset { id = FlareId, itemName = "Blue Flare",            size_x = 1, size_y = 1 });
            Assets.add(new ItemAsset { id = SmokeId, itemName = "Black Smoke",           size_x = 1, size_y = 1 });
        }

        // The frag AND the two the report actually named. The old routing failed identically for all three, so a
        // test that only covered the grenade would read "fixed" while the reported items stayed infinite.
        [TestCase(FragId,  TestName = "throwing a FRAG spends it")]
        [TestCase(SmokeId, TestName = "throwing a SMOKE spends it")]
        [TestCase(FlareId, TestName = "throwing a FLARE spends it")]
        public void Throwing_Spends_The_Item(ushort itemId)
        {
            var h = new TransactionalHarness(9300 + itemId).Connected("thrower");
            h.PumpCombatState = true;   // combat commands only cross the wire folded into a PlayerStateCommand
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(itemId));

            Assert.That(h.Server.Inventories.TryGet(a.PlayerId, out var inv), Is.True);
            Assume.That(inv.Inventory.getItemCount(itemId), Is.EqualTo(1), "fixture: the thrower carries exactly one");
            Assert.That(h.Server.Players.TryGetByOwner(a.PlayerId, out var pe), Is.True);

            long accepted = h.Server.Combat.Diag.GrenadesAccepted;
            a.SendGrenade(pe.Pos + new Vector3(0f, 1f, 0f), Vector3.zero, itemId);
            h.Step(10);

            Assert.That(h.Server.Combat.Diag.GrenadesAccepted, Is.EqualTo(accepted + 1),
                        "the throw itself was accepted -- otherwise the count below would prove nothing");
            Assert.That(inv.Inventory.getItemCount(itemId), Is.EqualTo(0),
                        $"the thrown item ({itemId}) must leave the bag; it used to fly AND stay");
        }

        // Throw the second of two and you still own one. Counts the difference rather than emptiness, so a
        // "clear every throwable on throw" implementation cannot pass.
        [Test]
        public void Throwing_Spends_Exactly_One()
        {
            var h = new TransactionalHarness(9350).Connected("thrower");
            h.PumpCombatState = true;
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(FragId));
            h.Grant(a.PlayerId, new Item(FragId));
            h.Grant(a.PlayerId, new Item(SmokeId));

            Assert.That(h.Server.Inventories.TryGet(a.PlayerId, out var inv), Is.True);
            Assume.That(inv.Inventory.getItemCount(FragId), Is.EqualTo(2));
            Assert.That(h.Server.Players.TryGetByOwner(a.PlayerId, out var pe), Is.True);

            a.SendGrenade(pe.Pos + new Vector3(0f, 1f, 0f), Vector3.zero, FragId);
            h.Step(10);

            Assert.That(inv.Inventory.getItemCount(FragId), Is.EqualTo(1), "one thrown, one left");
            Assert.That(inv.Inventory.getItemCount(SmokeId), Is.EqualTo(1), "and the smoke beside it is untouched");
        }

        // THE CONTROL for the whole mechanism: it is the ACCEPTED throw that spends, so a REFUSED one must not.
        // Without this, "spend on every OnGrenade call" would pass everything above while quietly charging a
        // player for throws the server threw away.
        [Test]
        public void A_Refused_Throw_Costs_Nothing()
        {
            var h = new TransactionalHarness(9399).Connected("thrower");
            h.PumpCombatState = true;
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(FragId));
            Assert.That(h.Server.Inventories.TryGet(a.PlayerId, out var inv), Is.True);
            Assert.That(h.Server.Players.TryGetByOwner(a.PlayerId, out var pe), Is.True);

            long rejected = h.Server.Combat.Diag.GrenadesRejected;
            // 500 m from the avatar, far outside MaxAimOriginOffset (3 m): refused before it reaches the spend.
            a.SendGrenade(pe.Pos + new Vector3(500f, 0f, 0f), Vector3.zero, FragId);
            h.Step(10);

            Assert.That(h.Server.Combat.Diag.GrenadesRejected, Is.EqualTo(rejected + 1), "the throw was refused");
            Assert.That(inv.Inventory.getItemCount(FragId), Is.EqualTo(1), "...and a refused throw does not spend");
        }

        // An UNKNOWN item id is refused by the throwable-table lookup, and that refusal must also come before the
        // spend -- naming a bandage must not delete the bandage.
        [Test]
        public void An_Unknown_Throwable_Id_Spends_Nothing()
        {
            var h = new TransactionalHarness(9398).Connected("thrower");
            h.PumpCombatState = true;
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.ScrapId));
            Assert.That(h.Server.Inventories.TryGet(a.PlayerId, out var inv), Is.True);
            Assert.That(h.Server.Players.TryGetByOwner(a.PlayerId, out var pe), Is.True);

            long rejected = h.Server.Combat.Diag.GrenadesRejected;
            a.SendGrenade(pe.Pos + new Vector3(0f, 1f, 0f), Vector3.zero, TransactionalFixtures.ScrapId);
            h.Step(10);

            Assert.That(h.Server.Combat.Diag.GrenadesRejected, Is.EqualTo(rejected + 1), "scrap is not a throwable");
            Assert.That(inv.Inventory.getItemCount(TransactionalFixtures.ScrapId), Is.EqualTo(1),
                        "and the item it named is still there");
        }

        // The bag the spend reads is the THROWER'S. Two players, one throws, and the other's stock is untouched
        // -- the failure this rejects is a spend keyed off anything but `sender`.
        [Test]
        public void Only_The_Throwers_Bag_Pays()
        {
            var h = new TransactionalHarness(9397).Connected("thrower", "bystander");
            h.PumpCombatState = true;
            var a = h.Clients[0];
            var b = h.Clients[1];
            h.Grant(a.PlayerId, new Item(FragId));
            h.Grant(b.PlayerId, new Item(FragId));
            Assert.That(h.Server.Inventories.TryGet(a.PlayerId, out var ainv), Is.True);
            Assert.That(h.Server.Inventories.TryGet(b.PlayerId, out var binv), Is.True);
            Assert.That(h.Server.Players.TryGetByOwner(a.PlayerId, out var ape), Is.True);

            a.SendGrenade(ape.Pos + new Vector3(0f, 1f, 0f), Vector3.zero, FragId);
            h.Step(10);

            Assert.That(ainv.Inventory.getItemCount(FragId), Is.EqualTo(0), "the thrower paid");
            Assert.That(binv.Inventory.getItemCount(FragId), Is.EqualTo(1), "the bystander did not");
        }
    }
}
