using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    /// <summary>
    /// DURABILITY on the server (strawberry 2026-10-06). Every loss lands on the SERVER's copy and reaches the owner
    /// through the inventory echo -- the client reports, never writes. These drive the real paths: a client command
    /// over the wire, the server tick, the combat damage funnel, the craft queue.
    /// </summary>
    [TestFixture]
    public class DurabilityServerTests
    {
        const ushort Gun = 9801, Knife = 9802, Shirt = 9803, Vest = 9804, Pants = 9805, Mask = 9806, Hat = 9807;

        [SetUp]
        public void SetUp()
        {
            TransactionalFixtures.RegisterAssets();
            Assets.add(new ItemAsset { id = Gun, itemName = "Wear Rifle", type = EItemType.GUN, gunName = "wearrifle", size_x = 2, size_y = 1, durability = 0.15f, wear = 2 });
            Assets.add(new ItemAsset { id = Knife, itemName = "Wear Knife", type = EItemType.MELEE, meleeName = "wearknife", durability = 0.1f, wear = 1 });
            Assets.add(new ItemAsset { id = Shirt, itemName = "Wear Shirt", type = EItemType.SHIRT, armor = 0.9f });
            Assets.add(new ItemAsset { id = Vest, itemName = "Wear Vest", type = EItemType.VEST, armor = 0.6f });
            Assets.add(new ItemAsset { id = Pants, itemName = "Wear Pants", type = EItemType.PANTS, armor = 0.8f });
            Assets.add(new ItemAsset { id = Mask, itemName = "Wear Gasmask", type = EItemType.MASK, proofRadiation = true });
            Assets.add(new ItemAsset { id = Hat, itemName = "Wear Helmet", type = EItemType.HAT, armor = 0.7f });
        }

        static (TransactionalHarness h, NetWorldClient a) One(int seed)
        {
            var h = new TransactionalHarness(seed).Connected("a");
            return (h, h.Clients[0]);
        }

        static (byte page, byte x, byte y, Item item) Find(PlayerInventory inv, ushort id)
        {
            for (byte pg = 0; pg < PlayerInventory.PAGES; pg++)
            {
                var page = inv.items[pg];
                if (page == null) continue;
                for (byte i = 0; i < page.getItemCount(); i++)
                {
                    var j = page.getItem(i);
                    if (j?.item?.id == id) return (pg, j.x, j.y, j.item);
                }
            }
            return (255, 0, 0, null);
        }

        // ---------------------------------------------------------------- weapons, over the wire

        [Test]
        public void reported_uses_wear_the_servers_gun_and_the_owner_sees_it()
        {
            var (h, a) = One(9101);
            h.Grant(a.PlayerId, new Item(Gun) { quality = 100 });
            h.Server.Transactions.Rand = () => 0f;   // every roll lands: each use costs the gun's Wear (2)
            h.Step(5);
            h.Server.Inventories.TryGet(a.PlayerId, out var se);
            var (pg, x, y, item) = Find(se.Inventory, Gun);
            Assert.That(item, Is.Not.Null, "fixture: the rifle is in the bag");

            a.SendWeaponUse(pg, x, y, Gun, 5);
            h.Step(5);
            Assert.That(Find(se.Inventory, Gun).item.quality, Is.EqualTo(90), "5 uses x Wear 2 on the server's copy");
            Assert.That(h.Server.Transactions.Diag.WeaponWearPoints, Is.EqualTo(10));
            Assert.That(h.StepUntil(() => a.Inventories.TryGet(a.PlayerId, out var ce) && Find(ce.Inventory, Gun).item?.quality == 90), Is.True,
                        "...and the owner echo carries it to the client");

            h.Server.Transactions.Rand = () => 0.5f;   // over the 0.15 chance: nothing
            a.SendWeaponUse(pg, x, y, Gun, 20);
            h.Step(5);
            Assert.That(Find(se.Inventory, Gun).item.quality, Is.EqualTo(90), "a roll over Durability costs nothing");
        }

        [Test]
        public void a_report_naming_the_wrong_item_wears_nothing_and_a_huge_one_is_clamped()
        {
            var (h, a) = One(9102);
            h.Grant(a.PlayerId, new Item(Gun) { quality = 100 });
            h.Server.Transactions.Rand = () => 0f;
            h.Step(5);
            h.Server.Inventories.TryGet(a.PlayerId, out var se);
            var (pg, x, y, _) = Find(se.Inventory, Gun);
            a.SendWeaponUse(pg, x, y, Knife, 5);       // stale/forged identity
            h.Step(5);
            Assert.That(Find(se.Inventory, Gun).item.quality, Is.EqualTo(100));
            Assert.That(h.Server.Transactions.Diag.WeaponUsesRejected, Is.EqualTo(1));
            h.Grant(a.PlayerId, new Item(Knife) { quality = 100 });   // Wear 1, so the clamp is visible below 100
            h.Step(5);
            var k = Find(se.Inventory, Knife);
            a.SendWeaponUse(k.page, k.x, k.y, Knife, 255);   // a client claiming 255 swings in one report
            h.Step(5);
            Assert.That(Find(se.Inventory, Knife).item.quality, Is.EqualTo(100 - WeaponUseCommand.MaxUses), "clamped to MaxUses");
        }

        // ---------------------------------------------------------------- clothing over time

        [Test]
        public void a_worn_shirt_loses_a_tenth_each_in_game_day_and_a_corpse_and_a_filter_do_not()
        {
            var (h, a) = One(9103);
            h.Server.Clock.ServerConfigure(0.5f, 20f, h.Server.Session.CurrentTick);   // a 20-second day: 10 days = 200 s
            h.Server.Inventories.TryGet(a.PlayerId, out var se);
            var shirt = new Item(Shirt) { quality = 100 };
            var mask = new Item(Mask) { quality = 100 };
            se.Inventory.wearShirt(shirt);
            se.Inventory.wearMask(mask);
            h.Step(1000);   // 20 s = ONE in-game day
            Assert.That(shirt.quality, Is.InRange(89, 91), $"one day of wear ~ 10 points ({shirt.quality})");
            Assert.That(mask.quality, Is.EqualTo(100), "a respirator's quality is its FILTER; wearing it does not spend it");
            Assert.That(h.StepUntil(() => a.Inventories.TryGet(a.PlayerId, out var ce) && ce.Inventory.wornShirt?.quality < 100), Is.True,
                        "the owner sees the wear");

            h.Server.Combat.DamagePlayerExternal(a.PlayerId, 500f);   // dead
            h.Step(5);
            byte atDeath = shirt.quality;
            h.Step(1000);
            Assert.That(shirt.quality, Is.EqualTo(atDeath), "a corpse does not wear its clothes out");
        }

        // ---------------------------------------------------------------- clothing under fire

        [Test]
        public void a_hit_wears_what_covers_where_it_landed_and_that_clothing_stops_its_share()
        {
            var (h, a) = One(9104);
            h.Server.Inventories.TryGet(a.PlayerId, out var se);
            var shirt = new Item(Shirt) { quality = 100 }; var vest = new Item(Vest) { quality = 100 };
            var pants = new Item(Pants) { quality = 100 }; var hat = new Item(Hat) { quality = 100 };
            se.Inventory.wearShirt(shirt); se.Inventory.wearVest(vest); se.Inventory.wearPants(pants); se.Inventory.wearHat(hat);

            // a weapon hit to the torso: shirt AND vest lose 1, then pass 0.9 x 0.6 (at 99%: a hair more)
            float through = h.Server.Combat.ClothingHit(a.PlayerId, Durability.Zone.Torso, 50f, ServerCombat.ArmorKind.Weapon);
            Assert.That((shirt.quality, vest.quality, pants.quality, hat.quality), Is.EqualTo(((byte)99, (byte)99, (byte)100, (byte)100)));
            float expect = 50f * Durability.PassThrough(Assets.find(Shirt), 99) * Durability.PassThrough(Assets.find(Vest), 99);
            Assert.That(through, Is.EqualTo(expect).Within(1e-3f), "retail: wear first, then the armor at the NEW condition");
            Assert.That(through, Is.LessThan(28f), "~46% of a torso hit stopped by shirt + vest");

            // a fall through the REAL damage funnel: trousers only, and no armor (the client already applied its own)
            float hp0 = h.Server.CombatState.TryGet(a.PlayerId, out var cs) ? cs.HealthExact : 0f;
            h.Server.Combat.DamagePlayerExternal(a.PlayerId, 10f, 0, Durability.Zone.Legs);
            h.Step(2);
            Assert.That(pants.quality, Is.EqualTo(99), "the fall wore the trousers");
            Assert.That((shirt.quality, hat.quality), Is.EqualTo(((byte)99, (byte)100)), "...and nothing above them");
            Assert.That(hp0 - cs.HealthExact, Is.EqualTo(10f).Within(1e-3f), "a zone without an armor kind is not reduced again");

            // a broken piece stops nothing
            hat.quality = 0;
            float head = h.Server.Combat.ClothingHit(a.PlayerId, Durability.Zone.Head, 40f, ServerCombat.ArmorKind.Weapon);
            Assert.That(head, Is.EqualTo(40f).Within(1e-3f), "a broken helmet passes the whole hit");
            Assert.That(hat.quality, Is.EqualTo(0), "...and cannot go below 0");
        }

        // ---------------------------------------------------------------- a broken gun

        [Test]
        public void the_server_refuses_a_shot_from_a_broken_gun_and_softens_one_from_a_worn_gun()
        {
            var (h, a) = One(9105);
            h.Server.Players.TryGetByOwner(a.PlayerId, out var pe);
            var eye = pe.Pos + new Vector3(0f, 1.5f, 0f);
            byte held = 0;
            h.Server.Combat.HeldCondition = _ => held;
            int ammo0 = h.Server.Combat.AmmoOf(a.PlayerId);
            // straight into the command handler: combat commands only cross the wire folded into a player-state
            // packet, which this harness does not stream -- the handler is the thing under test
            h.Server.Combat.OnFire(a.PlayerId, new FireCommand { Seq = 1, Origin = eye, Dir = new Vector3(0f, 0f, 1f) }, h.Server.Session.CurrentTick);
            h.Step(10);
            var dg = h.Server.Combat.Diag;
            Assert.That(dg.ShotsRejectedBroken, Is.EqualTo(1),
                $"a broken gun does not fire (accepted {dg.ShotsAccepted} ammo {dg.ShotsRejectedAmmo} rate {dg.ShotsRejectedRate} range {dg.ShotsRejectedRange} dead {dg.ShotsRejectedDeadOrMissing} reload {dg.ShotsRejectedReloading})");
            Assert.That(h.Server.Combat.Diag.ShotsAccepted, Is.EqualTo(0));
            Assert.That(h.Server.Combat.AmmoOf(a.PlayerId), Is.EqualTo(ammo0), "...and costs no round");
            held = 30;
            h.Server.Combat.OnFire(a.PlayerId, new FireCommand { Seq = 2, Origin = eye, Dir = new Vector3(0f, 0f, 1f) }, h.Server.Session.CurrentTick);
            h.Step(10);
            Assert.That(h.Server.Combat.Diag.ShotsAccepted, Is.EqualTo(1), "a worn one still fires");
        }

        [Test]
        public void the_host_reads_the_held_guns_condition_off_the_servers_inventory()
        {
            var (h, a) = One(9106);
            h.Grant(a.PlayerId, new Item(Gun) { quality = 37 });
            h.Step(5, () => a.SendMoveInput(0f, 0f, 0f, 0, Gun));
            Assert.That(h.Server.Combat.HeldCondition(a.PlayerId), Is.EqualTo((byte?)37));
            h.Step(5, () => a.SendMoveInput(0f, 0f, 0f, 0, 0));
            Assert.That(h.Server.Combat.HeldCondition(a.PlayerId), Is.Null, "nothing held: no opinion, no refusal");
        }

        // ---------------------------------------------------------------- tools

        const string LogGuid = "eeee0000000000000000000000000001";
        const string PlankGuid = "eeee0000000000000000000000000002";
        const string SawGuid = "eeee0000000000000000000000000003";

        [Test]
        public void a_craft_wears_its_tool_and_a_broken_tool_cannot_be_crafted_with()
        {
            Assets.add(new ItemAsset { id = 9811, itemName = "Wear Log", type = EItemType.SUPPLY, guid = LogGuid });
            Assets.add(new ItemAsset { id = 9812, itemName = "Wear Plank", type = EItemType.SUPPLY, guid = PlankGuid });
            Assets.add(new ItemAsset { id = 9813, itemName = "Wear Saw", type = EItemType.SUPPLY, guid = SawGuid });
            var bp = new BlueprintDef { Operation = "Craft", OwnerItemId = "9812", Seconds = 0.1f };
            bp.Inputs.Add(new BlueprintDef.Ingredient { Guid = LogGuid, Amount = 1, Consume = true });
            bp.Inputs.Add(new BlueprintDef.Ingredient { Guid = SawGuid, Amount = 1, Consume = false });
            bp.Outputs.Add(new BlueprintDef.Ingredient { Guid = PlankGuid, Amount = 2, Consume = true });
            Durability.ToolIds.Clear();
            Durability.RegisterTools(new[] { bp });

            var inv = new InventoryReplication();
            inv.ServerAdd(1, 0L);
            inv.TryGet(1, out var e);
            for (int i = 0; i < 5; i++) e.Inventory.tryAddItem(new Item(9811));
            var saw = new Item(9813) { quality = 7 };
            e.Inventory.tryAddItem(saw);
            var list = new List<BlueprintDef> { bp };
            var c = new ServerCrafting(inv) { BlueprintsSource = () => list, ToolRoll = () => 0.0 };   // every craft wears

            Assert.That(c.Enqueue(1, 0), Is.True);
            Assert.That(saw.quality, Is.EqualTo(4), "a craft cost the saw 3");
            Assert.That(c.Enqueue(1, 0), Is.True);
            Assert.That(saw.quality, Is.EqualTo(1));
            Assert.That(c.Enqueue(1, 0), Is.True, "a saw at 1% still saws...");
            Assert.That(saw.quality, Is.EqualTo(0), "...and breaks doing it");
            Assert.That(c.Enqueue(1, 0), Is.False, "a broken saw cannot be crafted with");
            Assert.That(e.Inventory.getItemCount(9811), Is.EqualTo(2), "and that refusal took no log");
            Durability.ToolIds.Clear();
        }

        // ---------------------------------------------------------------- the wire

        [Test]
        public void a_weapon_use_command_round_trips()
        {
            var w = new SDG.NetPak.NetPakWriter { buffer = new byte[64] };
            new WeaponUseCommand { Page = 2, X = 3, Y = 4, Id = 9801, Uses = 17 }.Write(w);
            w.Flush();
            var r = new SDG.NetPak.NetPakReader(); r.SetBufferSegment(w.buffer, w.writeByteIndex);
            Assert.That(WeaponUseCommand.TryRead(r, out var back), Is.True);
            Assert.That((back.Page, back.X, back.Y, back.Id, back.Uses), Is.EqualTo(((byte)2, (byte)3, (byte)4, (ushort)9801, (byte)17)));
        }

        // ---------------------------------------------------------------- a JOINED client (client-auth, PlayerState)

        // v57. A joiner streams PlayerState, never MoveInput -- and v22 put the held id on MoveInput only, so for every
        // joiner the server's "what is in their hands" read nothing: no broken-gun refusal, no worn-gun damage, and
        // (outside durability) no gun on their puppet, no crouch, no gesture, no lamp. These drive the real claim path.
        [Test]
        public void a_joiners_player_state_tells_the_server_what_it_holds_and_how_it_stands()
        {
            var (h, a) = One(9821);
            h.Server.Inventories.TryGet(a.PlayerId, out var se);
            var gun = new Item(Gun) { quality = 0 };
            se.Inventory.items[0].addItem(0, 0, 0, gun);
            h.Server.Players.TryGetByOwner(a.PlayerId, out var pe);
            var pos = pe.Pos;
            void Claim(ushort held, EPlayerStance stance) =>
                a.SendPlayerState(pos, 90f, 0f, Vector3.zero, MoveInput.PackStance(stance), true, 0, held);

            // CONTROL: empty hands -- the server must not invent a held item (a lookup that returned the first gun in
            // the bag would pass the next assertion on its own)
            h.Step(4, () => Claim(0, EPlayerStance.STAND));
            Assert.That(h.Server.Players.TryGetHeldInput(a.PlayerId, out var empty) && empty.HeldItemId == 0, Is.True,
                "a joiner's claim is a held input now, holding nothing");
            Assert.That(h.Server.Combat.HeldCondition(a.PlayerId), Is.Null, "empty hands have no condition");

            h.Step(4, () => Claim(Gun, EPlayerStance.CROUCH));
            Assert.That(h.Server.Players.TryGetHeldInput(a.PlayerId, out var mi), Is.True);
            Assert.That(mi.HeldItemId, Is.EqualTo(Gun), "the held id rode the PlayerState");
            Assert.That(h.Server.Combat.HeldCondition(a.PlayerId), Is.EqualTo((byte?)0), "...so the server sees the gun is broken");
            h.Server.Players.TryGetByOwner(a.PlayerId, out pe);
            Assert.That(pe.Stance, Is.EqualTo(2), "...and the crouch reached the entity other players draw (2 = crouch)");

            // death drops it like the MoveInput it stands in for
            h.Server.Players.ServerClearInput(a.PlayerId);
            Assert.That(h.Server.Players.TryGetHeldInput(a.PlayerId, out _), Is.False, "ServerClearInput clears a joiner's held input too");
        }

        [Test]
        public void a_joiners_fall_wears_the_trousers()
        {
            var (h, a) = One(9822);
            h.Server.Inventories.TryGet(a.PlayerId, out var se);
            var pants = new Item(Pants) { quality = 100 }; var shirt = new Item(Shirt) { quality = 100 };
            se.Inventory.wearPants(pants); se.Inventory.wearShirt(shirt);
            h.Server.Players.TryGetByOwner(a.PlayerId, out var pe);
            var pos = pe.Pos;
            void Claim(bool grounded) => a.SendPlayerState(pos, 90f, 0f, new Vector3(0f, grounded ? 0f : -25f, 0f),
                MoveInput.PackStance(EPlayerStance.STAND), grounded, 0);

            h.Step(6, () => Claim(true));
            float hp0 = h.Server.CombatState.TryGet(a.PlayerId, out var cs) ? cs.HealthExact : 0f;
            for (int i = 0; i < 12; i++) { pos.y -= 0.5f; h.Step(() => Claim(false)); }
            h.Step(6, () => Claim(true));   // land: the server FallMaths the peak through PlayerHost.DamageOwner

            Assert.That(hp0 - cs.HealthExact, Is.GreaterThan(0f), "the fall hurt (the measurement happened)");
            Assert.That(pants.quality, Is.EqualTo(99), "a joiner's fall wears the trousers, like the SP fall");
            Assert.That(shirt.quality, Is.EqualTo(100), "...and nothing above them");
        }
    }
}
