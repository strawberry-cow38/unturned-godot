using System.Collections.Generic;
using NUnit.Framework;
using SDG.NetPak;
using SDG.Unturned;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedNet.Tests;

[TestFixture]
public sealed class ViewedInventoryTests
{
    static NetPakReader Reader(byte[] bytes) { var r = new NetPakReader(); r.SetBufferSegment(bytes, bytes.Length); return r; }
    static byte[] Pack(ViewedMagazineTransferCommand c) => NetMessagePak.Pack(85, c.Write)[1..];
    static ViewedMagazineTransferCommand Request() => new() { RequestId = 1,
        Magazine = new(123, PlayerInventory.STORAGE, 0, 0, 6, 7), Round = new(0, 2, 1, 0, 5004, 8) };

    [Test]
    public void ScopedWireIsAtomicAndRejectsAreaAmbiguousOwnershipAndEveryTruncation()
    {
        foreach (bool unloading in new[] { false, true })
        {
            var c = Request(); c.Unloading = unloading;
            if (unloading) { c.Round = default; c.UnloadRoundId = 5004; }
            var bytes = Pack(c);
            Assert.That(ViewedMagazineTransferCommand.TryRead(Reader(bytes), out var got), Is.True);
            Assert.That(got, Is.EqualTo(c));
            for (int n = 0; n < bytes.Length; n++) {
                Assert.That(ViewedMagazineTransferCommand.TryRead(Reader(bytes[..n]), out got), Is.False);
                Assert.That(got, Is.EqualTo(default(ViewedMagazineTransferCommand)));
            }
        }
        var malformed = new List<ViewedMagazineTransferCommand>(); var bad = Request();
        bad.RequestId = 0; malformed.Add(bad);
        bad = Request(); bad.Magazine = bad.Magazine with { Token = 0 }; malformed.Add(bad);
        bad = Request(); bad.Magazine = bad.Magazine with { CrateId = 0 }; malformed.Add(bad);
        bad = Request(); bad.Magazine = bad.Magazine with { Page = PlayerInventory.AREA }; malformed.Add(bad);
        bad = Request(); bad.Magazine = bad.Magazine with { Page = 2 }; malformed.Add(bad);
        bad = Request(); bad.Magazine = bad.Magazine with { CrateId = 0, Page = 2 }; malformed.Add(bad); // owned-only stays81
        bad = Request(); bad.Round = bad.Round with { CrateId = 123 }; malformed.Add(bad);
        foreach (var c in malformed) Assert.That(ViewedMagazineTransferCommand.TryRead(Reader(Pack(c)), out _), Is.False);
        byte[] Empty(ScopedInventoryAddress a) => NetMessagePak.Pack(84, new EmptyViewedFuelCommand { Address = a }.Write)[1..];
        var external = Request().Magazine;
        Assert.That(EmptyViewedFuelCommand.TryRead(Reader(Empty(external)), out _), Is.True);
        var empty = Empty(external);
        for (int n = 0; n < empty.Length; n++) Assert.That(EmptyViewedFuelCommand.TryRead(Reader(empty[..n]), out _), Is.False);
        Assert.That(EmptyViewedFuelCommand.TryRead(Reader(Empty(external with { CrateId = 0, Page = 2 })), out _), Is.False);
        Assert.That(EmptyViewedFuelCommand.TryRead(Reader(Empty(external with { Page = 255 })), out _), Is.False);
    }

    [Test]
    public void ResolverRequiresLiveBackingViewerIdentityReachAndCanonicalRoot()
    {
        TransactionalFixtures.RegisterAssets();
        var inv = new InventoryReplication(); inv.ServerAdd(1, 0);
        var crate = inv.ServerRegisterCrate(new NetId(123), 6, 6, Vector3.zero);
        var item = new Item(TransactionalFixtures.StanagId, 3); crate.Storage.addItem(0, 0, 0, item);
        var tokens = new Dictionary<Item, ulong> { [item] = 5 }; inv.ItemIdentityProvider = i => tokens.GetValueOrDefault(i, 99UL);
        var a = new ScopedInventoryAddress(123, PlayerInventory.STORAGE, 0, 0, item.id, 5);
        Assert.That(inv.ServerResolveScoped(1, a, Vector3.zero), Is.Null, "not open");
        Assert.That(inv.ServerOpenStorage(1, 123, Vector3.zero, 0), Is.True);
        Assert.That(inv.ServerResolveScoped(1, a, Vector3.zero), Is.SameAs(item));
        Assert.That(inv.ServerResolveScoped(1, a, new Vector3(5, 0, 0)), Is.Null);
        Assert.That(inv.ServerResolveScoped(1, a with { Token = 99 }, Vector3.zero), Is.Null);
        Assert.That(inv.ServerResolveScoped(1, a with { X = 1 }, Vector3.zero), Is.Null);
        Assert.That(inv.ServerResolveScoped(1, a with { Page = PlayerInventory.FREEZER }, Vector3.zero), Is.Null);
        Assert.That(inv.ServerResolveScoped(1, a, null), Is.Null);
        crate.Viewers.Remove(1); Assert.That(inv.ServerResolveScoped(1, a, Vector3.zero), Is.Null); crate.Viewers.Add(1);
        bool allowed = false; crate.Access = (_, _) => allowed;
        Assert.That(inv.ServerResolveScoped(1, a, Vector3.zero), Is.Null, "vehicle-specific live access overrides fixed reach");
        allowed = true; Assert.That(inv.ServerResolveScoped(1, a, new Vector3(20, 0, 0)), Is.SameAs(item));
        inv.TryGet(1, out var entry);
        var fake = new Item(item.id, 3); tokens[fake] = 5;
        entry.Inventory.items[PlayerInventory.STORAGE].getItem(0).item = fake;
        Assert.That(inv.ServerResolveScoped(1, a, Vector3.zero), Is.Null, "matching token cannot replace live backing reference");
        inv.ServerRepaintCrateViewers(123);
        inv.ServerCloseStorage(1, 1); Assert.That(inv.ServerResolveScoped(1, a, Vector3.zero), Is.Null);
    }
    static byte[] Snapshot(InventoryReplication source) => NetMessagePak.Pack(0,
        w => source.WriteFull(w, new(1, 1, Vector3.zero)))[1..];

    [Test]
    public void ViewedIdentityTailIsAtomicPrivateAndExcludesArea()
    {
        TransactionalFixtures.RegisterAssets();
        var source = new InventoryReplication(); var owner = source.ServerAdd(1, 0); source.ServerAdd(2, 0);
        var tokens = new Dictionary<Item, ulong>();
        foreach (byte page in new byte[] { 2, 7, 9, 21 })
        {
            owner.Inventory.items[page].resize(6, 6);
            var item = new Item(TransactionalFixtures.BeansId);
            owner.Inventory.items[page].addItem(0, 0, 0, item); tokens[item] = (ulong)page + 10;
        }
        owner.Inventory.items[PlayerInventory.AREA].resize(6, 6);
        owner.Inventory.items[PlayerInventory.AREA].addItem(0, 0, 0, new Item(TransactionalFixtures.BeansId));
        source.ItemIdentityProvider = item => tokens[item]; // AREA lookup would throw
        var replica = new InventoryReplication(); replica.ReadSnapshot(Reader(Snapshot(source)), true);
        Assert.That(replica.TryGet(2, out _), Is.False);
        foreach (byte page in new byte[] { 2, 7, 9, 21 })
        {
            Assert.That(replica.TryGetItemToken(1, page, 0, 0, out var token), Is.True, $"page {page}");
            Assert.That(token, Is.EqualTo((ulong)page + 10));
        }
        Assert.That(replica.TryGetItemToken(1, PlayerInventory.AREA, 0, 0, out _), Is.False);
        replica.TryGet(1, out var accepted); var before = accepted.Inventory;
        var next = Snapshot(source);
        for (int missing = 1; missing <= 32; missing++)
        {
            replica.ReadSnapshot(Reader(next[..^missing]), true);
            Assert.That(accepted.Inventory, Is.SameAs(before), $"truncated container tail {missing}");
            Assert.That(accepted.ItemTokens.Count, Is.EqualTo(4));
        }
        source.ItemIdentityProvider = _ => 5;
        replica.ReadSnapshot(Reader(Snapshot(source)), true);
        Assert.That(accepted.Inventory, Is.SameAs(before), "duplicate across owned/viewed tails is atomic");
        source.ItemIdentityProvider = null; replica.ReadSnapshot(Reader(Snapshot(source)), true);
        Assert.That(accepted.ItemTokens, Is.Empty);
    }

    [Test]
    public void FinalCompartmentSupportsByteMaximumJarCountAndRejectsDuplicateRoots()
    {
        TransactionalFixtures.RegisterAssets();
        var source = new InventoryReplication(); var owner = source.ServerAdd(1, 0);
        var page = owner.Inventory.items[21]; page.resize(255, 1);
        var tokens = new Dictionary<Item, ulong>();
        for (int x = 0; x < 255; x++)
        {
            var item = new Item(TransactionalFixtures.BeansId);
            page.addItem((byte)x, 0, 0, item); tokens[item] = (ulong)x + 1;
        }
        source.ItemIdentityProvider = item => tokens[item];
        var replica = new InventoryReplication(); replica.ReadSnapshot(Reader(Snapshot(source)), true);
        replica.TryGet(1, out var accepted);
        Assert.That(accepted.Inventory.items[21].getItemCount(), Is.EqualTo(255));
        Assert.That(accepted.ItemTokens.Count, Is.EqualTo(255));
        Assert.That(replica.TryGetItemToken(1, 21, 254, 0, out var token), Is.True); Assert.That(token, Is.EqualTo(255));
        var before = accepted.Inventory; page.getItem(254).x = 0;
        replica.ReadSnapshot(Reader(Snapshot(source)), true);
        Assert.That(accepted.Inventory, Is.SameAs(before), "malformed duplicate root cannot replace grid or tail");
    }

}
