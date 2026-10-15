using System.Collections.Generic;
using NUnit.Framework;
using SDG.NetPak;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests;

[TestFixture]
public sealed class MagazineTransferTests
{
    static NetPakReader Reader(byte[] bytes) { var r = new NetPakReader(); r.SetBufferSegment(bytes, bytes.Length); return r; }
    static byte[] Pack(MagazineTransferCommand c) => NetMessagePak.Pack(81, c.Write)[1..];
    static MagazineTransferCommand Request(uint id = 1) => new() { RequestId = id,
        Magazine = new(2, 0, 0, TransactionalFixtures.StanagId), MagazineToken = 1,
        Round = new(2, 1, 0, TransactionalFixtures.Round556Id), RoundToken = 2 };

    [Test]
    public void ExactRequestAndOwnerResultRejectEveryTruncationAndInvalidTagWithoutPartialAdoption()
    {
        var c = Request(); var bytes = Pack(c);
        Assert.That(MagazineTransferCommand.TryRead(Reader(bytes), out var decoded), Is.True);
        Assert.That(decoded, Is.EqualTo(c));
        for (int n = 0; n < bytes.Length; n++) {
            Assert.That(MagazineTransferCommand.TryRead(Reader(bytes[..n]), out decoded), Is.False);
            Assert.That(decoded, Is.EqualTo(default(MagazineTransferCommand)));
        }
        var bad = new List<MagazineTransferCommand>();
        c = Request(); c.RequestId = 0; bad.Add(c);
        c = Request(); c.MagazineToken = 0; bad.Add(c);
        c = Request(); c.RoundToken = 0; bad.Add(c);
        c = Request(); c.Magazine = c.Magazine with { Page = 7 }; bad.Add(c);
        c = Request(); c.Round = c.Round with { Page = 8 }; bad.Add(c);
        c = Request(); c.Round = c.Round with { Id = 0 }; bad.Add(c);
        c = Request(); c.Unloading = true; bad.Add(c); // canonical unload has no source address/token
        foreach (var item in bad) Assert.That(MagazineTransferCommand.TryRead(Reader(Pack(item)), out _), Is.False);
        c = Request(); c.Unloading = true; c.Round = new(0, 0, 0, c.Round.Id); c.RoundToken = 0;
        Assert.That(MagazineTransferCommand.TryRead(Reader(Pack(c)), out decoded), Is.True);

        byte[] Fact(MagazineTransferEvent e) => NetMessagePak.Pack(60, e.Write)[1..];
        var accepted = new MagazineTransferEvent { RequestId = 7, MagazineToken = ulong.MaxValue, Tick = uint.MaxValue,
            Result = MagazineTransferResult.Applied, Amount = 29 };
        var fact = Fact(accepted);
        Assert.That(MagazineTransferEvent.TryRead(Reader(fact), out var result), Is.True);
        Assert.That(result, Is.EqualTo(accepted));
        for (int n = 0; n < fact.Length; n++) {
            Assert.That(MagazineTransferEvent.TryRead(Reader(fact[..n]), out result), Is.False);
            Assert.That(result, Is.EqualTo(default(MagazineTransferEvent)));
        }
        accepted.Result = (MagazineTransferResult)255;
        Assert.That(MagazineTransferEvent.TryRead(Reader(Fact(accepted)), out _), Is.False);
        accepted.Result = MagazineTransferResult.NoRoom;
        Assert.That(MagazineTransferEvent.TryRead(Reader(Fact(accepted)), out _), Is.False, "refusal never carries a fabricated amount");
    }

    [Test]
    public void OneOwnerFactPerRequestAndReplayIsIdempotentIncludingCounterWrapAndRejoin()
    {
        TransactionalFixtures.RegisterAssets();
        var h = new TransactionalHarness(48101).Connected("owner", "other"); var a = h.Clients[0]; var b = h.Clients[1];
        var bag = h.Server.Transactions.InventoryForTest(a.PlayerId); bag.items[2].resize(6, 4);
        var mag = new Item(TransactionalFixtures.StanagId, 0); var rounds = new Item(TransactionalFixtures.Round556Id, 5);
        bag.items[2].addItem(0, 0, 0, mag); bag.items[2].addItem(1, 0, 0, rounds);
        var identities = new Dictionary<Item, ulong> { [mag] = 1, [rounds] = 2 };
        h.Server.Inventories.ItemIdentityProvider = item => identities.TryGetValue(item, out var token) ? token : 99;
        var facts = new List<MagazineTransferEvent>(); var foreign = new List<MagazineTransferEvent>();
        a.MagazineTransferred += facts.Add; b.MagazineTransferred += foreign.Add;
        void Send(uint request) => a.SendMagazineTransfer(request, 2, 0, 0, mag.id, 1, 2, 1, 0, rounds.id, 2, false);
        void Await(int count) => Assert.That(h.StepUntil(() => facts.Count == count), Is.True);
        Send(uint.MaxValue); Await(1); Assert.That(facts[0].Applied, Is.True); Assert.That(mag.amount, Is.EqualTo(1));
        Send(uint.MaxValue); Await(2); Assert.That(facts[1], Is.EqualTo(facts[0])); Assert.That(mag.amount, Is.EqualTo(1));
        Send(1); Await(3); Assert.That(mag.amount, Is.EqualTo(2));
        Send(uint.MaxValue); Await(4); Assert.That(facts[^1].Result, Is.EqualTo(MagazineTransferResult.StaleRequest));
        Assert.That(mag.amount, Is.EqualTo(2)); Assert.That(rounds.amount, Is.EqualTo(3)); Assert.That(foreign, Is.Empty);
        a.Disconnect(); h.Step(20);
        var pending = (System.Collections.IDictionary)typeof(ServerTransactions).GetField("_magazineRequests",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(h.Server.Transactions)!;
        Assert.That(pending.Count, Is.Zero, "disconnect discards the prior owner request state");
        var next = h.AddClient("new"); Assert.That(h.StepUntil(() => next.State == NetSessionState.Connected), Is.True);
        bag = h.Server.Transactions.InventoryForTest(next.PlayerId); bag.items[2].resize(6, 4);
        bag.items[2].addItem(0, 0, 0, mag); bag.items[2].addItem(1, 0, 0, rounds);
        var joined = new List<MagazineTransferEvent>(); next.MagazineTransferred += joined.Add;
        next.SendMagazineTransfer(1, 2, 0, 0, mag.id, 1, 2, 1, 0, rounds.id, 2, false);
        Assert.That(h.StepUntil(() => joined.Count == 1), Is.True);
        Assert.That(joined[0].Applied, Is.True); Assert.That(mag.amount, Is.EqualTo(3));
    }
}
