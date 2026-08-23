using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedNet.Tests
{
    // FIELDS KEEP GETTING ADDED TO Item AND NEVER JOINING THE WIRE, AND NOTHING NOTICES.
    //
    // It has happened at least three times. Per-slot attachments: "added to Item after the schema was
    // written and never joined it, so every owner echo rebuilt the jar WITHOUT them" -- fitting a scope
    // deleted it. Gas-can fuelLevel: the server filled the can and the echo dropped the level, so the can
    // showed empty ("can won't fill"). And magLoadedRound, added for the magazine load/unload, which this
    // test was written alongside: without it every echo rebuilds a part-loaded magazine with no cartridge
    // lock, so it forgets what it holds and accepts a mix on the next drag.
    //
    // Each was found by a player hitting it. Nothing could catch them, and the reason is exact: the byte
    // goldens pin the packet HEADER (PacketHeaderGoldenTests) and no test pins the JAR schema at all. I
    // changed the jar format while writing this and the entire suite stayed green.
    //
    // So this walks Item's fields by REFLECTION and requires each one to survive a real round trip through
    // the live replication path -- not a unit test of the writer, which would only prove the writer agrees
    // with itself. A field added tomorrow fails here until someone either wires it or writes down why not.
    [TestFixture]
    public class ItemWireCompletenessTests
    {
        [SetUp]
        public void SetUp() => TransactionalFixtures.RegisterAssets();

        // Fields that deliberately do NOT travel. Each needs a reason, because "it's in the list" is how a
        // genuine omission hides.
        static readonly Dictionary<string, string> NotReplicated = new()
        {
            // ReadJar re-derives this from the loaded magazine: it is a string, this stack has no string
            // primitive, and the mag id it comes from is already on the wire.
            ["gunChamberedType"] = "re-derived from the loaded magazine on the receiving side",
            // A loot-spawn flag consumed at spawn time; it never describes a live item in a grid.
            ["preserved"] = "spawn-time loot flag, not live item state",
        };

        static IEnumerable<FieldInfo> ItemFields() =>
            typeof(Item).GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => !f.IsInitOnly);

        /// <summary>A value distinguishable from both the default AND from zero, so "the field arrived" and
        /// "the field was default-constructed" cannot be confused.</summary>
        static object Distinctive(FieldInfo f) => f.FieldType switch
        {
            var t when t == typeof(bool) => true,
            var t when t == typeof(byte) => (byte)7,
            var t when t == typeof(ushort) => (ushort)TransactionalFixtures.StanagId,
            var t when t == typeof(int) => 23,
            var t when t == typeof(float) => 42.5f,
            var t when t == typeof(string) => "556",
            _ => null,
        };

        [Test]
        public void every_item_field_survives_a_real_round_trip()
        {
            var h = new TransactionalHarness(7701).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.StanagId) { amount = 0 });
            h.Step(10);

            var server = h.Server.Transactions.InventoryForTest(a.PlayerId);
            ItemJar serverJar = null;
            foreach (var page in server.items)
                for (byte i = 0; i < page.getItemCount(); i++)
                    if (page.getItem(i)?.item?.id == TransactionalFixtures.StanagId) serverJar = page.getItem(i);
            Assert.That(serverJar, Is.Not.Null);

            // Stamp every settable field with a distinctive value on the AUTHORITATIVE item.
            var stamped = new List<FieldInfo>();
            foreach (var f in ItemFields())
            {
                if (f.Name == "id") continue;                       // the id addresses the item; changing it changes what this is
                object v = Distinctive(f);
                if (v == null) continue;                            // a type this test cannot stamp -- see the coverage assert below
                f.SetValue(serverJar.item, v);
                stamped.Add(f);
            }
            h.Server.Inventories.ServerMarkDirty(a.PlayerId);
            Assert.That(h.StepUntil(() =>
            {
                if (!a.Inventories.TryGet(a.PlayerId, out var e)) return false;
                foreach (var pg in e.Inventory.items)
                    for (byte i = 0; i < pg.getItemCount(); i++)
                        if (pg.getItem(i)?.item?.id == TransactionalFixtures.StanagId
                            && pg.getItem(i).item.amount == 7) return true;
                return false;
            }), Is.True, $"the stamped magazine reached the client (seed={h.Net.Seed})");

            a.Inventories.TryGet(a.PlayerId, out var ent);
            Item replica = null;
            foreach (var pg in ent.Inventory.items)
                for (byte i = 0; i < pg.getItemCount(); i++)
                    if (pg.getItem(i)?.item?.id == TransactionalFixtures.StanagId) replica = pg.getItem(i).item;
            Assert.That(replica, Is.Not.Null);

            var lost = new List<string>();
            foreach (var f in stamped)
            {
                if (NotReplicated.ContainsKey(f.Name)) continue;
                object want = f.GetValue(serverJar.item), got = f.GetValue(replica);
                if (!Equals(want, got)) lost.Add($"{f.Name} (sent {want}, arrived {got})");
            }
            Assert.That(lost, Is.Empty,
                "these Item fields do not survive replication -- wire them in WriteJar/ReadJar, or add them "
                + "to NotReplicated WITH A REASON: " + string.Join("; ", lost));
        }

        [Test]
        public void the_exemption_list_names_only_fields_that_exist()
        {
            // An exemption for a deleted or renamed field is worse than none: it silently exempts nothing
            // while reading as though a decision was made.
            var names = ItemFields().Select(f => f.Name).ToHashSet();
            var stale = NotReplicated.Keys.Where(k => !names.Contains(k)).ToList();
            Assert.That(stale, Is.Empty, "exemptions for fields that no longer exist: " + string.Join(", ", stale));
        }
    }
}
