using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SDG.NetPak;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    [TestFixture]
    public sealed class EquipmentWireTests
    {
        [SetUp] public void Setup() => TransactionalFixtures.RegisterAssets();
        static byte[] Pack(Action<NetPakWriter> write) => NetMessagePak.Pack(0, write)[1..];
        static NetPakReader Reader(byte[] bytes) { var reader = new NetPakReader(); reader.SetBufferSegment(bytes, bytes.Length); return reader; }
        static byte[] Inventory(InventoryReplication source, ushort owner) => Pack(w => source.WriteFull(w, new(1, owner, Vector3.zero)));

        [Test]
        public void TokenTailAndInventoryCommitAtomicallyAndDoNotExposeAnotherOwner()
        {
            var source = new InventoryReplication(); var a = source.ServerAdd(1, 0); var b = source.ServerAdd(2, 0);
            var first = new Item(TransactionalFixtures.BeansId); var other = new Item(TransactionalFixtures.ScrapId);
            a.Inventory.items[2].addItem(0, 0, 0, first); b.Inventory.items[2].addItem(0, 0, 0, other);
            source.ItemIdentityProvider = item => ReferenceEquals(item, first) ? 17UL : 29UL;
            var replica = new InventoryReplication(); replica.ReadSnapshot(Reader(Inventory(source, 1)), true);
            Assert.That(replica.TryGetItemToken(1, 2, 0, 0, out var token), Is.True); Assert.That(token, Is.EqualTo(17));
            Assert.That(replica.TryGet(2, out _), Is.False);
            replica.TryGet(1, out var accepted); var acceptedInventory = accepted.Inventory;
            a.Inventory.items[2].removeItem(0); a.Inventory.items[2].addItem(1, 0, 0, other);
            byte[] next = Inventory(source, 1);
            replica.ReadSnapshot(Reader(next[..^4]), true);
            Assert.That(accepted.Inventory, Is.SameAs(acceptedInventory), "An incomplete token must not install the newer grid");
            Assert.That(replica.TryGetItemToken(1, 2, 0, 0, out token), Is.True); Assert.That(token, Is.EqualTo(17));
            replica.ReadSnapshot(Reader(next), true);
            Assert.That(replica.TryGetItemToken(1, 2, 0, 0, out _), Is.False);
            Assert.That(replica.TryGetItemToken(1, 2, 1, 0, out token), Is.True); Assert.That(token, Is.EqualTo(29));
            source.ItemIdentityProvider = null; replica.ReadSnapshot(Reader(Inventory(source, 1)), true);
            Assert.That(accepted.ItemTokens, Is.Empty, "No-service snapshots cannot retain tokens from a previous provider");
        }

        [Test]
        public void DuplicateTokenTailIsRejectedWithoutReplacingAcceptedInventory()
        {
            var source = new InventoryReplication(); var a = source.ServerAdd(1, 0);
            a.Inventory.items[2].addItem(0, 0, 0, new Item(TransactionalFixtures.BeansId));
            var replica = new InventoryReplication(); replica.ReadSnapshot(Reader(Inventory(source, 1)), true);
            replica.TryGet(1, out var accepted); var before = accepted.Inventory;
            a.Inventory.items[2].addItem(1, 0, 0, new Item(TransactionalFixtures.BeansId));
            source.ItemIdentityProvider = _ => 5;
            replica.ReadSnapshot(Reader(Inventory(source, 1)), true);
            Assert.That(accepted.Inventory, Is.SameAs(before)); Assert.That(accepted.ItemTokens, Is.Empty);
            // A malformed frame with two jars claiming the same cell must not
            // throw or partially install the accompanying identity dictionary.
            a.Inventory.items[2].getItem(1).x = 0;
            ulong identity = 0; source.ItemIdentityProvider = _ => ++identity;
            Assert.DoesNotThrow(() => replica.ReadSnapshot(Reader(Inventory(source, 1)), true));
            Assert.That(accepted.Inventory, Is.SameAs(before));
        }

        [Test]
        public void EquipmentStateAndOptionEventsAreOwnerOnlyThroughTheActualStack()
        {
            var h = new TransactionalHarness(7620).Connected("first", "second"); var a = h.Clients[0]; var b = h.Clients[1];
            var first = new OwnerEquipmentState { Token = 80, Held = new(0, 0, 0, 4), SelectionVersion = 3, Ammo = 17, Capacity = 30, Firemode = 1 };
            var second = first with { Token = 90, Ammo = 7 };
            h.Server.Equipment.ServerSet(a.PlayerId, first, h.Server.Session.CurrentTick);
            h.Server.Equipment.ServerSet(b.PlayerId, second, h.Server.Session.CurrentTick);
            h.Step(12);
            Assert.That(a.Equipment.TryGet(a.PlayerId, out var own), Is.True); Assert.That(own, Is.EqualTo(first));
            Assert.That(a.Equipment.TryGet(b.PlayerId, out _), Is.False); Assert.That(b.Equipment.Count, Is.EqualTo(1));
            var repliesA = new List<AttachmentOptionsEvent>(); var repliesB = new List<AttachmentOptionsEvent>();
            a.AttachmentOptionsReceived += repliesA.Add; b.AttachmentOptionsReceived += repliesB.Add;
            ushort requestOwner = 0;
            h.Server.AttachmentOptionsFor = (owner, serial) => { requestOwner = owner; return new() { RequestId = serial, Supported = true, GunName = "Source gun" }; };
            a.SendAttachmentOptions(19); h.Step(10);
            Assert.That(requestOwner, Is.EqualTo(a.PlayerId)); Assert.That(repliesA.Single().RequestId, Is.EqualTo(19)); Assert.That(repliesB, Is.Empty);
            ushort selectionOwner = 0;
            h.Server.SelectHeldRequested = (owner, cmd) => selectionOwner = owner;
            a.SendSelectHeld(0, 0, 0, 4, 80); h.Step(10);
            Assert.That(selectionOwner, Is.EqualTo(a.PlayerId), "Sender comes from connection, never payload");
        }

        [Test]
        public void MalformedWeaponTagsCountsAndTokensNeverReachTheService()
        {
            var command = new WeaponIntentCommand { GunToken = 4, SelectionVersion = 2, Incoming = new(2, 0, 0, 7), IncomingToken = 8 };
            byte[] valid = Pack(command.Write);
            Assert.That(WeaponIntentCommand.TryRead(Reader(valid), out _), Is.True);
            for (int field = 0; field < 2; field++) { var bad = (byte[])valid.Clone(); bad[field] = 255; Assert.That(WeaponIntentCommand.TryRead(Reader(bad), out _), Is.False); }
            var unknownMode = (byte[])valid.Clone(); unknownMode[^1] = 4;
            Assert.That(WeaponIntentCommand.TryRead(Reader(unknownMode), out _), Is.False);
            Assert.That(WeaponIntentCommand.TryRead(Reader(valid[..^2]), out _), Is.False);
            command.IncomingToken = 0;
            Assert.That(WeaponIntentCommand.TryRead(Reader(Pack(command.Write)), out _), Is.False, "An install needs an actual instance token");
            var reply = new AttachmentOptionsEvent { Supported = true, Slots = new[] {
                new EquipmentSlotOptions { Slot = EquipmentSlot.Barrel, Color = new[] {1f,1f,1f,1f}, Choices = new[] { new EquipmentChoice(new(2,0,0,7),8,1,100) } }
            } };
            Assert.That(AttachmentOptionsEvent.TryRead(Reader(Pack(reply.Write)), out var read), Is.True);
            Assert.That(read.Slots[0].Choices[0].Token, Is.EqualTo(8));
            reply.Slots = new[] { reply.Slots[0], reply.Slots[0] };
            Assert.That(AttachmentOptionsEvent.TryRead(Reader(Pack(reply.Write)), out _), Is.False, "Duplicate slot tags are not a menu");
            reply.Slots = new[] { new EquipmentSlotOptions { Color = new[] {float.NaN,0,0,1f} } };
            Assert.That(AttachmentOptionsEvent.TryRead(Reader(Pack(reply.Write)), out _), Is.False);
        }

        [Test]
        public void EquipmentStateAndFactsRejectTruncationWithoutPartialAdoption()
        {
            var source = new OwnerEquipmentReplication();
            var before = new OwnerEquipmentState { Token = 13, Held = new(0, 0, 0, 4), SelectionVersion = 2, Ammo = 7, Firemode = 1 };
            source.ServerSet(1, before, 1);
            var replica = new OwnerEquipmentReplication();
            replica.ReadSnapshot(Reader(Pack(w => source.WriteFull(w, new(1, 1, Vector3.zero)))), true);
            source.ServerSet(1, before with { Ammo = 1, SelectionVersion = 3 }, 2);
            var next = Pack(w => source.WriteFull(w, new(2, 1, Vector3.zero)));
            replica.ReadSnapshot(Reader(next[..^3]), false);
            Assert.That(replica.TryGet(1, out var kept), Is.True); Assert.That(kept, Is.EqualTo(before));
            var fact = new EquipmentFactEvent { Kind = EquipmentFactKind.BarrelBroken, ItemId = 4, BarrelId = 149, Tick = 42, Reason = "The bottle silencer burst" };
            var bytes = Pack(fact.Write);
            Assert.That(EquipmentFactEvent.TryRead(Reader(bytes), out var accepted), Is.True);
            Assert.That(accepted.BarrelId, Is.EqualTo(149)); Assert.That(accepted.Tick, Is.EqualTo(42));
            Assert.That(EquipmentFactEvent.TryRead(Reader(bytes[..^1]), out _), Is.False);
        }

        [Test]
        public void Version61CannotJoinTheChangedEquipmentFraming()
        {
            Assert.That(NetProtocol.Version, Is.EqualTo(62));
            var h = new NetSimHarness(seed: 7621); var client = h.AddClient("old equipment", version: 61); client.Connect();
            Assert.That(h.StepUntil(() => client.State == NetSessionState.Disconnected, 100), Is.True);
            Assert.That(client.RejectReason, Is.EqualTo(NetRejectReason.VersionMismatch));
            Assert.That(client.RejectServerVersion, Is.EqualTo(62)); Assert.That(h.Server.Peers, Is.Empty);
        }
    }
}
