using System;
using System.Collections.Generic;
using NUnit.Framework;
using SDG.NetPak;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests;

[TestFixture]
public sealed class CraftingContextTests
{
    const string Station = "7b82c125a5a54984b8bb26576b59e977";
    static NetPakReader Reader(byte[] bytes) { var reader = new NetPakReader(); reader.SetBufferSegment(bytes, bytes.Length); return reader; }
    static byte[] Bytes(Action<NetPakWriter> write) => NetMessagePak.Pack(82, write)[1..];

    [Test]
    public void ContextWireRejectsTruncationInvalidModesOriginsAndUnusableTagFactsAtomically()
    {
        var request = new CraftingContextCommand { RequestId = uint.MaxValue, Eye = new(1, 2, 3),
            Action = CraftingContextAction.Craft, BlueprintIndex = ushort.MaxValue };
        var data = Bytes(request.Write);
        Assert.That(CraftingContextCommand.TryRead(Reader(data), out var decoded), Is.True);
        Assert.That(decoded, Is.EqualTo(request));
        for (int n = 0; n < data.Length; n++) {
            Assert.That(CraftingContextCommand.TryRead(Reader(data[..n]), out decoded), Is.False);
            Assert.That(decoded, Is.EqualTo(default(CraftingContextCommand)));
        }
        request.Eye.x = float.NaN; Assert.That(request.IsValid, Is.False);
        request.Eye = Vector3.zero; request.Action = (CraftingContextAction)255; Assert.That(request.IsValid, Is.False);
        request.Action = CraftingContextAction.Probe; Assert.That(request.IsValid, Is.False, "probe cannot smuggle a craft index");
        request.BlueprintIndex = 0; request.RequestId = 0; Assert.That(request.IsValid, Is.False);

        var fact = new CraftingContextEvent { RequestId = 1, Tick = uint.MaxValue,
            Result = CraftingContextResult.Ready, Tags = new[] { Station, "source-tag" } };
        data = Bytes(fact.Write);
        Assert.That(CraftingContextEvent.TryRead(Reader(data), out var result), Is.True);
        Assert.That(result.Tags, Is.EqualTo(fact.Tags));
        for (int n = 0; n < data.Length; n++) {
            Assert.That(CraftingContextEvent.TryRead(Reader(data[..n]), out result), Is.False);
            Assert.That(result, Is.EqualTo(default(CraftingContextEvent)));
        }
        fact.Result = CraftingContextResult.InvalidEye; Assert.That(fact.IsValid, Is.False, "refusal grants no tags");
        fact.Result = CraftingContextResult.Ready; fact.Tags = new[] { Station, Station }; Assert.That(fact.IsValid, Is.False);
        fact.Tags = new string[65]; Assert.That(fact.IsValid, Is.False);
        fact.Tags = new[] { "not a source tag" }; Assert.That(fact.IsValid, Is.False);
    }

    [Test]
    public void OwnerProbeIsReadOnlyCraftRechecksLiveStationsAndReplayCannotSpendAgain()
    {
        TransactionalFixtures.RegisterAssets();
        var h = new TransactionalHarness(48201).Connected("owner", "other");
        var owner = h.Clients[0]; var other = h.Clients[1];
        var recipe = new BlueprintDef { Operation = "Craft", Seconds = 30 };
        recipe.StationTags.Add(Station);
        recipe.Inputs.Add(new BlueprintDef.Ingredient { Guid = "fixture-log", Amount = 1, Consume = true });
        recipe.Outputs.Add(new BlueprintDef.Ingredient { Guid = "fixture-plank", Amount = 2 });
        h.Server.Transactions.Blueprints = new[] { recipe };
        var bag = h.Server.Transactions.InventoryForTest(owner.PlayerId); bag.items[2].resize(8, 5);
        for (int n = 0; n < 3; n++) bag.tryAddItem(new Item(TransactionalFixtures.LogId));
        h.Server.Players.TryGetByOwner(owner.PlayerId, out var player);
        Vector3 eye = player.Pos + new Vector3(0, 1.75f, 0);
        bool available = true; int reads = 0;
        h.Server.CraftingStationsAt = (who, at) => { Assert.That(who, Is.EqualTo(owner.PlayerId));
            Assert.That(at, Is.EqualTo(eye)); reads++; return available ? new[] { Station } : Array.Empty<string>(); };
        var facts = new List<CraftingContextEvent>(); var foreign = new List<CraftingContextEvent>();
        owner.CraftingContext += facts.Add; other.CraftingContext += foreign.Add;
        CraftingContextEvent Await(int count) { Assert.That(h.StepUntil(() => facts.Count == count), Is.True); return facts[^1]; }

        owner.SendCraft(0); h.Step(12);
        Assert.That(h.Server.CraftQueue.JobCount(owner.PlayerId), Is.Zero, "legacy16 has no station access to assert");
        owner.SendCraftingContext(1, eye); Assert.That(Await(1).Tags, Is.EqualTo(new[] { Station }));
        Assert.That(bag.getItemCount(TransactionalFixtures.LogId), Is.EqualTo(3), "browsing does not escrow supplies");
        owner.SendCraftingContext(2, eye, craft: true); Assert.That(Await(2).Result, Is.EqualTo(CraftingContextResult.Crafted));
        Assert.That(bag.getItemCount(TransactionalFixtures.LogId), Is.EqualTo(2));
        Assert.That(h.Server.CraftQueue.JobCount(owner.PlayerId), Is.EqualTo(1));
        int afterCraft = reads; available = false;
        owner.SendCraftingContext(2, eye, craft: true); Assert.That(Await(3).Result, Is.EqualTo(CraftingContextResult.Crafted));
        Assert.That(reads, Is.EqualTo(afterCraft), "an exact replay only replays the accepted fact");
        Assert.That(bag.getItemCount(TransactionalFixtures.LogId), Is.EqualTo(2));
        owner.SendCraftingContext(3, eye, craft: true); Assert.That(Await(4).Result, Is.EqualTo(CraftingContextResult.CraftRejected));
        Assert.That(reads, Is.GreaterThan(afterCraft), "a new craft recomputes access even if a prior probe granted it");
        Assert.That(facts[^1].Tags, Is.Empty);
        Assert.That(bag.getItemCount(TransactionalFixtures.LogId), Is.EqualTo(2));
        Assert.That(h.Server.CraftQueue.JobCount(owner.PlayerId), Is.EqualTo(1));
        Assert.That(foreign, Is.Empty, "station access/results belong to the requester");
    }

    [Test]
    public void MissingHostAdapterForgedEyesAndDeadOwnersCannotGrantContext()
    {
        TransactionalFixtures.RegisterAssets();
        var h = new TransactionalHarness(48202).Connected("owner"); var owner = h.Clients[0];
        h.Server.Players.TryGetByOwner(owner.PlayerId, out var player);
        var eye = player.Pos + new Vector3(0, 1.75f, 0);
        var facts = new List<CraftingContextEvent>(); owner.CraftingContext += facts.Add;
        void Await(int count) => Assert.That(h.StepUntil(() => facts.Count == count), Is.True);
        owner.SendCraftingContext(uint.MaxValue, eye); Await(1);
        Assert.That(facts[^1].Result, Is.EqualTo(CraftingContextResult.Unavailable));
        int reads = 0; h.Server.CraftingStationsAt = (_, _) => { reads++; return new[] { Station }; };
        owner.SendCraftingContext(1, eye + new Vector3(100, 0, 0)); Await(2);
        Assert.That(facts[^1].Result, Is.EqualTo(CraftingContextResult.InvalidEye)); Assert.That(reads, Is.Zero);
        h.Server.CombatState.TryGet(owner.PlayerId, out var life); life.Alive = false;
        owner.SendCraftingContext(2, eye); Await(3);
        Assert.That(facts[^1].Result, Is.EqualTo(CraftingContextResult.Dead)); Assert.That(reads, Is.Zero);
        life.Alive = true;
        owner.SendCraftingContext(3, eye); Await(4); Assert.That(facts[^1].HasContext, Is.True);
        owner.SendCraftingContext(2, eye); Await(5); Assert.That(facts[^1].Result, Is.EqualTo(CraftingContextResult.StaleRequest));
        Assert.That(facts[^1].Tags, Is.Empty);
        owner.Disconnect(); h.Step(12);
        var entries = (System.Collections.IDictionary)typeof(NetWorldServer).GetField("_craftingContexts",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(h.Server)!;
        Assert.That(entries.Count, Is.Zero, "rejoining cannot inherit an old request counter");
    }
}
