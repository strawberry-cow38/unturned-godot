using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // THE INVERSE OF ItemWireCompletenessTests, AND THE QUESTION THAT TEST CANNOT ANSWER.
    //
    // That one stamps a field on the SERVER's item and proves it reaches the client. Every gun field passed it
    // while the bug was live, and passed it honestly: the wire was never broken. gunAmmo is written by WriteJar,
    // read by ReadJar and round-trips perfectly. What was broken is that NOTHING EVER PUT A REAL VALUE IN THE
    // SERVER'S COPY -- gunAmmo, gunChambered, gunFiremode, gunMagId, gunAttach and the four per-slot attachment
    // ids were written only by the client (SaveGunState, AttachmentFit), so the server held the field
    // initialiser for the whole session and the owner echo delivered that default over the top of the player's
    // real magazine. Move a gun anywhere in the grid and it came back full (strawberry, 2026-08-26).
    //
    // A test that stamps the authority and reads the replica structurally cannot see this, because it supplies
    // the very value the real system fails to supply. Its green is not weak evidence, it is evidence about a
    // different question. So this asks the opposite one: for each field of Item, who can put a real value on
    // the AUTHORITATIVE side? Server-owned, carried up from the client by a named command, or derived on
    // arrival -- and a field that is none of those fails here.
    //
    // The three lists are not documentation. CarriedFromClient is checked by actually sending the command and
    // reading the server's item back, so an entry that names a command which does not carry that field fails.
    [TestFixture]
    public class ItemAuthoritySourceTests
    {
        [SetUp]
        public void SetUp() => TransactionalFixtures.RegisterAssets();

        /// <summary>Fields the SERVER writes itself, from its own simulation or from a validated command.</summary>
        static readonly Dictionary<string, string> ServerOwned = new()
        {
            ["id"] = "set at construction by whoever spawned the item",
            ["amount"] = "stack size -- server-side spends, crafts, mag loads and pickups all write it",
            ["quality"] = "durability -- server-side spawn quality and use effects write it",
            ["fuelLevel"] = "OnExtractFuel / gas-pump transactions write it server-side",
            ["fluidType"] = "server-owned fluid container contents",
            ["fluidAmount"] = "server-owned fluid container contents",
            ["fluidQuality"] = "server-owned fluid container contents",
            ["magLoadedRound"] = "MagRules writes it during the server-side load/unload",
            // Cooking. SERVER-owned for two specific reasons, not a general preference: an oven left on has to
            // keep cooking while no client is near it, and `cooked` multiplies what a meal is worth, so a
            // client allowed to assert it is a client allowed to print food.
            ["cooked"] = "ServerCooking.Step advances it on the crate's items each server tick",
            ["cookStyle"] = "ServerCooking.Step stamps it when an item reaches the cooked band",
        };

        /// <summary>Fields only the CLIENT can know, carried up by the named command. Verified below.</summary>
        static readonly Dictionary<string, string> CarriedFromClient = new()
        {
            ["gunAmmo"] = "CommandGunState",
            ["gunChambered"] = "CommandGunState",
            ["gunFiremode"] = "CommandGunState",
            ["gunMagId"] = "CommandGunState",
            ["gunAttach"] = "CommandGunState",
            ["gunSightId"] = "CommandGunState",
            ["gunBarrelId"] = "CommandGunState",
            ["gunGripId"] = "CommandGunState",
            ["gunTacticalId"] = "CommandGunState",
            ["gunAttachSeeded"] = "CommandGunState",
            ["autoDrink"] = "CommandSetAutoDrink",
        };

        /// <summary>Fields nobody needs to transmit because the receiving side recomputes them. A field belongs
        /// here only if something RE-DERIVES it after every echo -- "it gets set again eventually" is the
        /// argument that hid gunAmmo, so the reason has to name the thing that does the re-deriving.</summary>
        static readonly Dictionary<string, string> Derived = new()
        {
            ["gunChamberedType"] = "ReadJar re-derives it from gunMagId, which is on the wire",
            ["preserved"] = "StorageCrate.TrackPreserved and FoodSpoil's daily sweep both reconcile every FOOD "
                          + "item in a crate to that crate's CURRENT power state, so a cleared flag is restored "
                          + "before it can be read -- it is derived from the fridge, not stored on the item",
        };

        static IEnumerable<FieldInfo> ItemFields() =>
            typeof(Item).GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => !f.IsInitOnly);

        [Test]
        public void every_item_field_has_a_writer_on_the_authoritative_side()
        {
            var unclassified = ItemFields().Select(f => f.Name)
                .Where(n => !ServerOwned.ContainsKey(n) && !CarriedFromClient.ContainsKey(n) && !Derived.ContainsKey(n))
                .ToList();
            Assert.That(unclassified, Is.Empty,
                "these Item fields have no stated route onto the server's copy. If the server writes it, add it "
                + "to ServerOwned; if only the client can know it, give it a command and add it to "
                + "CarriedFromClient; if something re-derives it, say WHAT in Derived. A field in none of the "
                + "three is one the owner echo will overwrite with a default nobody chose: "
                + string.Join(", ", unclassified));
        }

        [Test]
        public void the_lists_name_only_fields_that_exist()
        {
            var names = ItemFields().Select(f => f.Name).ToHashSet();
            var stale = ServerOwned.Keys.Concat(CarriedFromClient.Keys).Concat(Derived.Keys)
                .Where(k => !names.Contains(k)).ToList();
            Assert.That(stale, Is.Empty,
                "entries for fields that no longer exist -- they exempt nothing while reading as a decision: "
                + string.Join(", ", stale));
        }

        [Test]
        public void a_field_is_claimed_by_exactly_one_list()
        {
            var dupes = ItemFields().Select(f => f.Name)
                .Where(n => (ServerOwned.ContainsKey(n) ? 1 : 0) + (CarriedFromClient.ContainsKey(n) ? 1 : 0)
                          + (Derived.ContainsKey(n) ? 1 : 0) > 1)
                .ToList();
            Assert.That(dupes, Is.Empty, "claimed by more than one route: " + string.Join(", ", dupes));
        }

        // ---- the lists earn their keep: send the real command, read the server's item back ----

        static ItemJar ServerJar(TransactionalHarness h, ushort playerId, ushort id, out byte page)
        {
            var inv = h.Server.Transactions.InventoryForTest(playerId);
            for (byte p = 0; p < PlayerInventory.PAGES; p++)
                for (byte i = 0; i < inv.items[p].getItemCount(); i++)
                    if (inv.items[p].getItem(i)?.item?.id == id) { page = p; return inv.items[p].getItem(i); }
            page = byte.MaxValue;
            return null;
        }

        [Test]
        public void the_gun_state_command_writes_every_field_it_claims()
        {
            var h = new TransactionalHarness(7702).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.RifleId));
            h.Step(10);

            var jar = ServerJar(h, a.PlayerId, TransactionalFixtures.RifleId, out byte page);
            Assert.That(jar, Is.Not.Null, "the rifle is on the server grid");

            // Every value distinct from every other AND from the -1/false defaults, so a field that silently
            // took its neighbour's value (a copy-paste in Write/TryRead) is a failure rather than a pass.
            const short ammo = 7;
            a.SendGunState(page, jar.x, jar.y, TransactionalFixtures.RifleId, ammo, chambered: true, firemode: 2,
                           magId: TransactionalFixtures.StanagId, attach: 17,
                           sight: 3, barrel: 4, grip: 5, tactical: 6, attachSeeded: true);

            Assert.That(h.StepUntil(() => h.Server.Transactions.Diag.GunStatesApplied == 1), Is.True,
                $"the server applied the gun state (seed={h.Net.Seed})");

            var it = jar.item;
            var got = new Dictionary<string, object>
            {
                ["gunAmmo"] = it.gunAmmo, ["gunChambered"] = it.gunChambered, ["gunFiremode"] = it.gunFiremode,
                ["gunMagId"] = it.gunMagId, ["gunAttach"] = it.gunAttach, ["gunSightId"] = it.gunSightId,
                ["gunBarrelId"] = it.gunBarrelId, ["gunGripId"] = it.gunGripId, ["gunTacticalId"] = it.gunTacticalId,
                ["gunAttachSeeded"] = it.gunAttachSeeded,
            };
            var want = new Dictionary<string, object>
            {
                ["gunAmmo"] = (int)ammo, ["gunChambered"] = true, ["gunFiremode"] = 2,
                ["gunMagId"] = (int)TransactionalFixtures.StanagId, ["gunAttach"] = 17, ["gunSightId"] = 3,
                ["gunBarrelId"] = 4, ["gunGripId"] = 5, ["gunTacticalId"] = 6, ["gunAttachSeeded"] = true,
            };

            // The claim under test is the LIST's, not this method's: every field that says CommandGunState
            // carries it has to be one this command actually moved. A new field added to the list without
            // being added to the wire fails here rather than in a player's magazine.
            var claimed = CarriedFromClient.Where(kv => kv.Value == "CommandGunState").Select(kv => kv.Key).ToList();
            var unchecked_ = claimed.Where(n => !want.ContainsKey(n)).ToList();
            Assert.That(unchecked_, Is.Empty,
                "these fields claim CommandGunState carries them but this test never checks them: "
                + string.Join(", ", unchecked_));

            var wrong = claimed.Where(n => !Equals(want[n], got[n]))
                               .Select(n => $"{n} (sent {want[n]}, server has {got[n]})").ToList();
            Assert.That(wrong, Is.Empty, "CommandGunState did not land these on the server: " + string.Join("; ", wrong));
        }

        [Test]
        public void the_auto_drink_command_writes_the_server_copy()
        {
            var h = new TransactionalHarness(7703).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.WaterBottleId));
            h.Step(10);

            var jar = ServerJar(h, a.PlayerId, TransactionalFixtures.WaterBottleId, out byte page);
            Assert.That(jar, Is.Not.Null);
            Assert.That(jar.item.autoDrink, Is.True, "(gate) autoDrink starts at its field initialiser -- the value the echo used to force back");

            a.SendSetAutoDrink(page, jar.x, jar.y, TransactionalFixtures.WaterBottleId, autoDrink: false);
            Assert.That(h.StepUntil(() => h.Server.Transactions.Diag.AutoDrinkApplied == 1), Is.True,
                $"the server applied the toggle (seed={h.Net.Seed})");
            Assert.That(jar.item.autoDrink, Is.False, "the server's copy is OFF, so the next echo cannot turn it back on");
        }

        [Test]
        public void a_client_cannot_claim_more_ammo_than_the_magazine_holds()
        {
            var h = new TransactionalHarness(7704).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.RifleId));
            h.Step(10);
            var jar = ServerJar(h, a.PlayerId, TransactionalFixtures.RifleId, out byte page);

            // The server owns no gun simulation, so it cannot verify an ammo count -- only cap it at what a
            // legitimate reload could have produced. Same contract as OnReload's clamp on SpentAmount.
            a.SendGunState(page, jar.x, jar.y, TransactionalFixtures.RifleId, 4000, chambered: false, firemode: 0,
                           magId: TransactionalFixtures.StanagId, attach: 0, sight: -1, barrel: -1, grip: -1,
                           tactical: -1, attachSeeded: false);
            Assert.That(h.StepUntil(() => h.Server.Transactions.Diag.GunStatesApplied == 1), Is.True);
            Assert.That(jar.item.gunAmmo, Is.EqualTo(30), "clamped to the eaglefire's Ammo_Max, not taken on trust");
        }

        [Test]
        public void a_command_naming_the_wrong_item_is_refused()
        {
            var h = new TransactionalHarness(7705).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.RifleId));
            h.Step(10);
            var jar = ServerJar(h, a.PlayerId, TransactionalFixtures.RifleId, out byte page);
            int before = jar.item.gunAmmo;

            // An address is not an identity: a command in flight while the player swaps that cell must land on
            // nothing rather than stamp a rifle's magazine onto whatever moved in.
            a.SendGunState(page, jar.x, jar.y, TransactionalFixtures.ScrapId, 12, chambered: false, firemode: 0,
                           magId: -1, attach: -1, sight: -1, barrel: -1, grip: -1, tactical: -1, attachSeeded: false);
            Assert.That(h.StepUntil(() => h.Server.Transactions.Diag.GunStatesRejected == 1), Is.True,
                $"the mismatched id was rejected (seed={h.Net.Seed})");
            Assert.That(jar.item.gunAmmo, Is.EqualTo(before), "and the real item was left alone");
        }
    }
}
