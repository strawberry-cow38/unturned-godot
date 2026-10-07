using System.Collections.Generic;
using NUnit.Framework;
using SDG.NetPak;
using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    /// <summary>Blueprint knowledge (v55, strawberry 2026-10-04): "certain recipes arent known by default. unlocked by
    /// either directly learning or via skill unlocks ... learned blueprints should be tracked per player", "also
    /// persisting through saving etc".
    ///
    /// Four recipes, one per kind of knowledge: an OPEN one everybody knows, one a schematic teaches, one a skill level
    /// unlocks, and one only the console grants. Each check is aimed at the cheap way to fake it -- a craft refused by
    /// the CLIENT only, a schematic that teaches but is never spent (or is spent twice), knowledge that leaks to the
    /// player next to you, a save that writes the set and a load that ignores it.</summary>
    [TestFixture]
    public class BlueprintKnowledgeTests
    {
        const ushort SchematicId = 8101;

        static BlueprintDef PlankRecipe(int outAmount, params string[] unlocks)
        {
            var bp = new BlueprintDef { Operation = "Craft", OwnerItemId = TransactionalFixtures.PlankId.ToString() };
            bp.Inputs.Add(new BlueprintDef.Ingredient { Guid = "fixture-log", Amount = 1, Consume = true });
            bp.Outputs.Add(new BlueprintDef.Ingredient { Guid = "fixture-plank", Amount = outAmount });
            foreach (var u in unlocks) bp.Unlocks.Add(u);
            return bp;
        }

        // index:          0 open            1 taught by the schematic         2 crafting 2 unlocks it          3 console only
        static List<BlueprintDef> Catalog() => new List<BlueprintDef>
        {
            PlankRecipe(1), PlankRecipe(3, $"item:{SchematicId}"), PlankRecipe(5, "skill:crafting:2"), PlankRecipe(7, "learn"),
        };

        List<BlueprintDef> _cat;

        TransactionalHarness Rig(int seed, params string[] names)
        {
            TransactionalFixtures.RegisterAssets();
            Assets.add(new ItemAsset { id = SchematicId, itemName = "Plank Schematic", size_x = 1, size_y = 1, type = EItemType.SUPPLY });
            var h = new TransactionalHarness(seed);
            _cat = Catalog();
            h.Server.Transactions.Blueprints = _cat;
            return h.Connected(names);
        }

        static int Count(TransactionalHarness h, NetWorldClient c, ushort id)
            => h.Server.Inventories.TryGet(c.PlayerId, out var e) ? e.Inventory.getItemCount(id) : -1;

        static (byte page, byte x, byte y) Find(TransactionalHarness h, NetWorldClient c, ushort id)
        {
            h.Server.Inventories.TryGet(c.PlayerId, out var e);
            for (byte p = 0; p < PlayerInventory.OWNPAGES; p++)
            {
                var pg = e.Inventory.items[p];
                for (byte i = 0; i < pg.getItemCount(); i++)
                    if (pg.getItem(i).item.id == id) return (p, pg.getItem(i).x, pg.getItem(i).y);
            }
            Assert.Fail($"item {id} is not in the bag");
            return default;
        }

        [Test]
        public void the_tsv_carries_unlocks_and_an_explicit_key_and_an_old_row_is_unchanged()
        {
            var line = "901\tCraft\t\t\t0\tfixture-log:1:1\tfixture-plank:2\t\t4\tskill:crafting:2|item:8101\tbirch_plank";
            var bp = BlueprintDef.FromTsv(line);
            Assert.That(bp.Locked, Is.True);
            Assert.That(bp.Key, Is.EqualTo("birch_plank"), "the explicit key wins");
            Assert.That(new List<ushort>(bp.TaughtByItems()), Is.EqualTo(new List<ushort> { 8101 }));
            Assert.That(new List<(string, int)>(bp.SkillUnlocks()), Is.EqualTo(new List<(string, int)> { ("crafting", 2) }));
            Assert.That(BlueprintDef.FromTsv(bp.ToTsv()).ToTsv(), Is.EqualTo(bp.ToTsv()), "round-trips");

            // A retail row (8 columns) stays 8 columns and stays OPEN -- every recipe that existed before this column
            // did must mean exactly what it meant.
            var old = "901\tCraft\t\t\t0\tfixture-log:1:1\tfixture-plank:2\t";
            var ob = BlueprintDef.FromTsv(old);
            Assert.That(ob.Locked, Is.False, "no unlock column = known by everyone");
            Assert.That(ob.ToTsv().Split('\t').Length, Is.EqualTo(8), "and it writes back as the 8 columns it came in as");
        }

        [Test]
        public void a_derived_key_follows_the_recipe_not_its_row()
        {
            // The whole point of the key: knowledge saved against it must still name the same recipe after someone
            // inserts a row above it. So it is a function of the CONTENT -- equal for equal recipes wherever they sit,
            // different the moment an ingredient or an amount differs.
            var a = PlankRecipe(3); var b = PlankRecipe(3); var c = PlankRecipe(4);
            Assert.That(a.Key, Is.EqualTo(b.Key), "same recipe, same key, independent of position");
            Assert.That(a.Key, Is.Not.EqualTo(c.Key), "a different output amount is a different recipe");
            Assert.That(BlueprintDef.IsValidKey(a.Key), Is.True, $"and it is a key the wire accepts ({a.Key})");
        }

        [Test]
        public void an_unknown_locked_recipe_is_refused_by_the_SERVER_and_costs_nothing()
        {
            var h = Rig(7301, "a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.LogId));
            h.Step(10);

            a.SendCraft(1);   // locked, not known
            h.Step(30);
            Assert.That(h.Server.Transactions.Diag.CraftsUnknownBlueprint, Is.EqualTo(1), "refused as an unknown blueprint");
            Assert.That(Count(h, a, TransactionalFixtures.LogId), Is.EqualTo(1), "the log was not taken");
            Assert.That(Count(h, a, TransactionalFixtures.PlankId), Is.Zero, "and nothing was made");

            a.SendCraft(0);   // the open recipe, same ingredients -- proves the refusal above was the lock, not the bag
            Assert.That(h.StepUntil(() => Count(h, a, TransactionalFixtures.PlankId) == 1), Is.True, "the open recipe crafts");
        }

        [Test]
        public void a_schematic_teaches_once_and_is_spent_once()
        {
            var h = Rig(7302, "a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(SchematicId));
            h.Grant(a.PlayerId, new Item(SchematicId));
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.LogId));
            h.Step(10);
            string key = _cat[1].Key;

            var (p, x, y) = Find(h, a, SchematicId);
            a.SendConsume(p, x, y);
            Assert.That(h.StepUntil(() => a.KnownBlueprints != null && a.KnownBlueprints.Contains(key)), Is.True,
                        "the owner was TOLD it learned the recipe");
            Assert.That(h.Server.BlueprintKnowledge.Knows(a.PlayerId, _cat[1]), Is.True, "the server knows it too");
            Assert.That(Count(h, a, SchematicId), Is.EqualTo(1), "ONE schematic was spent");
            Assert.That(h.Server.BlueprintKnowledge.Knows(a.PlayerId, _cat[2]), Is.False, "it taught only what names it");

            (p, x, y) = Find(h, a, SchematicId);
            a.SendConsume(p, x, y);   // nothing new to learn
            h.Step(30);
            Assert.That(Count(h, a, SchematicId), Is.EqualTo(1), "reading it again teaches nothing and spends nothing");

            a.SendCraft(1);
            Assert.That(h.StepUntil(() => Count(h, a, TransactionalFixtures.PlankId) == 3), Is.True, "and now it crafts");
        }

        [Test]
        public void reaching_the_skill_teaches_the_skill_recipe()
        {
            var h = Rig(7303, "a");
            var a = h.Clients[0];
            string key = _cat[2].Key;

            a.SendConsole("skill crafting 1");
            h.Step(30);
            Assert.That(h.Server.BlueprintKnowledge.Knows(a.PlayerId, _cat[2]), Is.False, "level 1 is short of the unlock");

            a.SendConsole("skill crafting 2");
            Assert.That(h.StepUntil(() => a.KnownBlueprints != null && a.KnownBlueprints.Contains(key)), Is.True,
                        "level 2 teaches it, and the owner hears about it");
            Assert.That(h.Server.BlueprintKnowledge.Knows(a.PlayerId, _cat[1]), Is.False, "the schematic recipe is not a skill unlock");
        }

        [Test]
        public void knowledge_is_per_player_and_only_the_owner_hears_it()
        {
            var h = Rig(7304, "a", "b");
            var a = h.Clients[0]; var b = h.Clients[1];
            Assert.That(h.StepUntil(() => a.KnownBlueprints != null && b.KnownBlueprints != null), Is.True,
                        "every joiner is told its set, even when it is EMPTY -- 'none' and 'not told yet' must differ");
            Assert.That(a.KnownBlueprints.Count + b.KnownBlueprints.Count, Is.Zero);

            int bEvents = 0;
            b.KnownBlueprintsChanged += _ => bEvents++;
            a.SendConsole($"learn {_cat[3].Key}");
            Assert.That(h.StepUntil(() => a.KnownBlueprints.Contains(_cat[3].Key)), Is.True, "the console grants it");
            h.Step(20);
            Assert.That(bEvents, Is.Zero, "the other player was not sent a's knowledge");
            Assert.That(h.Server.BlueprintKnowledge.Knows(b.PlayerId, _cat[3]), Is.False, "and does not have it");

            h.Grant(b.PlayerId, new Item(TransactionalFixtures.LogId));
            h.Step(5);
            b.SendCraft(3);
            h.Step(30);
            Assert.That(Count(h, b, TransactionalFixtures.PlankId), Is.Zero, "b still cannot craft what only a learned");
        }

        [Test]
        public void knowledge_survives_a_save_and_a_rejoin()
        {
            var h = Rig(7305, "keeper");
            var a = h.Clients[0];
            a.SendConsole("learnall");
            Assert.That(h.StepUntil(() => a.KnownBlueprints != null && a.KnownBlueprints.Count == 3), Is.True, "learned the three locked ones");

            var json = WorldSave.Capture(h.Server, "", 0, 0.5f, 1200f).ToJson();
            Assert.That(WorldSave.TryParse(json, "", out var save, out var err), Is.True, err);

            // A FRESH server reading that file, and the same player walking back in.
            var h2 = new TransactionalHarness(7306);
            h2.Server.Transactions.Blueprints = _cat;
            h2.Server.PendingSave = save;
            h2.Connected("keeper");
            var back = h2.Clients[0];
            Assert.That(h2.StepUntil(() => back.KnownBlueprints != null && back.KnownBlueprints.Count == 3), Is.True,
                        "every learned blueprint came back from the save, and the client was told");
            foreach (var bp in _cat)
                Assert.That(h2.Server.BlueprintKnowledge.Knows(back.PlayerId, bp), Is.True, $"knows {bp.Key}");

            // ...and a save from BEFORE this feature (no KnownBlueprints property at all) still loads, knowing nothing.
            var legacy = json.Replace("\"KnownBlueprints\"", "\"KnownBlueprints_gone\"");
            Assert.That(WorldSave.TryParse(legacy, "", out var old, out err), Is.True, err);
            Assert.That(old.Players[0].KnownBlueprints, Is.Empty, "an old save restores as knowing no locked recipes");
        }

        [Test]
        public void the_event_round_trips_and_a_hostile_key_fails_closed()
        {
            var evt = new KnownBlueprintsEvent { Keys = new[] { "bp-901-0a1b2c3d", "birch_plank" } };
            var pak = NetMessagePak.Pack(ReplicationIds.EventKnownBlueprints, evt.Write);
            var r = new SDG.NetPak.NetPakReader(); r.SetBufferSegment(pak, pak.Length); r.ReadUInt8(out _);
            Assert.That(KnownBlueprintsEvent.TryRead(r, out var got), Is.True);
            Assert.That(got.Keys, Is.EqualTo(evt.Keys));

            // A key is text that becomes a set entry and a log line: anything outside the charset is refused whole.
            var bad = NetMessagePak.Pack(ReplicationIds.EventKnownBlueprints, w => { w.WriteUInt16(1); w.WriteString("[img]x[/img]"); });
            var br = new SDG.NetPak.NetPakReader(); br.SetBufferSegment(bad, bad.Length); br.ReadUInt8(out _);
            Assert.That(KnownBlueprintsEvent.TryRead(br, out _), Is.False, "a key with markup in it is refused");

            // The count is bounded BEFORE the allocation.
            var huge = NetMessagePak.Pack(ReplicationIds.EventKnownBlueprints, w => w.WriteUInt16(60000));
            var hr = new SDG.NetPak.NetPakReader(); hr.SetBufferSegment(huge, huge.Length); hr.ReadUInt8(out _);
            Assert.That(KnownBlueprintsEvent.TryRead(hr, out _), Is.False, "an absurd count is refused, not allocated");
        }
    }
}
