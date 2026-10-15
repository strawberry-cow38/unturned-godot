using System.Linq;
using NUnit.Framework;
using SDG.Unturned;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedNet.Tests
{
    [TestFixture]
    public class WorldRebaseTests
    {
        [SetUp] public void SetUp() => TransactionalFixtures.RegisterAssets();

        static void Drain(TransactionalHarness h)
        {
            foreach (var c in h.Clients) c.PollNetwork();
            for (int i = 0; i < 1024; i++)
            {
                h.Server.PollNetwork();
                foreach (var c in h.Clients) c.PollNetwork(false);
                if (h.Net.PendingDatagramCount == 0
                    && h.Clients.All(c => c.Session.Session.InFlightMessageCount == 0 && c.Session.Session.PendingSendCount == 0)
                    && h.Server.Session.Peers.All(p => p.Session.InFlightMessageCount == 0 && p.Session.PendingSendCount == 0)) return;
            }
            Assert.Fail("old-frame traffic did not drain");
        }

        [Test]
        public void PollBarrier_DeliversQueuedIntentAndFact_WithoutMovingAnyClock()
        {
            var h = new TransactionalHarness(8841).Connected("survivor"); h.Step(8);
            var c = h.Clients[0]; h.Grant(c.PlayerId, new Item(TransactionalFixtures.BeansId, 1, 100)); h.Step(5);
            h.Server.Inventories.TryGet(c.PlayerId, out var bag);
            var page = bag.Inventory.items.First(p => p.items.Any(j => j.item.id == TransactionalFixtures.BeansId));
            var jar = page.items.First(j => j.item.id == TransactionalFixtures.BeansId);
            long serverTick = h.Server.Session.CurrentTick, clientTick = c.Session.CurrentTick, netTick = h.Net.CurrentTick;
            c.SendDropItem(page.page, jar.x, jar.y);
            Drain(h);
            Assert.That(h.Server.WorldItems.Count, Is.EqualTo(1), "real reliable inventory intent executed");
            Assert.That(c.WorldItems.Count, Is.EqualTo(1), "its real reliable spawn fact arrived");
            Assert.That(bag.Inventory.getItemCount(TransactionalFixtures.BeansId), Is.Zero);
            Assert.That(h.Server.Session.CurrentTick, Is.EqualTo(serverTick));
            Assert.That(c.Session.CurrentTick, Is.EqualTo(clientTick));
            Assert.That(h.Net.CurrentTick, Is.EqualTo(netTick));
        }

        [Test]
        public void PollBarrier_AcceptedConsoleTeleportDeliversOwnerRecoveryBeforeRebase()
        {
            var h = new TransactionalHarness(8846); h.Server.SpawnProvider = _ => new Vector3(1025, 40, 12);
            h.Server.Transactions.RepositionOwner = h.Server.PlayerHost.RepositionOwner;
            h.Connected("walker"); h.Step(8); var c = h.Clients[0];
            c.SendPlayerState(new Vector3(1025, 40, 12), 0, 0, Vector3.zero, 0, true, 0); Drain(h);
            int recoveries = 0; Vector3 owner = Vector3.zero; byte ack = 0;
            c.PlayerRecov += e => { recoveries++; owner = e.Pos; ack = e.RecovCounter; };
            long tick = h.Server.Session.CurrentTick;
            c.SendConsole("teleport 1030 42 18"); Drain(h);
            Assert.That(recoveries, Is.EqualTo(1)); Assert.That(owner, Is.EqualTo(new Vector3(1030, 42, 18)));
            Assert.That(h.Server.Session.CurrentTick, Is.EqualTo(tick));
            var delta = new Vector3(-1024, 0, 0); WorldRebase.Translate(h.Server, c, delta); owner += delta;
            c.SendPlayerState(owner, 0, 0, Vector3.zero, 0, true, ack); Drain(h);
            Assert.That(h.Server.Players.TryGetByOwner(c.PlayerId, out var p), Is.True);
            Assert.That(p.Pos, Is.EqualTo(new Vector3(6, 42, 18)));
            Assert.That(recoveries, Is.EqualTo(1), "the accepted new-frame claim needs no second correction");
        }

        [Test]
        public void Rebase_PreservesAuthorityRecoveryWindow_AndUnsentCombatOrigin()
        {
            var h = new TransactionalHarness(8842); h.Server.SpawnProvider = _ => new Vector3(1025, 0, -1025);
            h.Connected("walker"); h.Step(8); var c = h.Clients[0];
            var oldPos = new Vector3(1025, 0, -1025); var d = new Vector3(-1024, 0, 1024);
            c.SendPlayerState(oldPos, 0, 0, Vector3.zero, 0, true, 0); Drain(h);
            h.Server.Players.TryGetByOwner(c.PlayerId, out var player); uint id = player.NetIdValue;
            int recoveries = 0; c.PlayerRecov += _ => recoveries++;
            c.SendGrenade(oldPos + Vector3.up, Vector3.zero, 254); // still only in client's redundancy ring
            Assert.That(c.PendingCombatCount, Is.EqualTo(1));
            long before = h.Server.Session.CurrentTick;
            WorldRebase.Translate(h.Server, c, d);
            Assert.That(h.Server.Session.CurrentTick, Is.EqualTo(before));
            Assert.That(player.NetIdValue, Is.EqualTo(id)); Assert.That(player.Pos, Is.EqualTo(oldPos + d));
            Assert.That(h.Server.PlayerHost.TryGetDrivenState(c.PlayerId, out var adopted), Is.True);
            Assert.That(adopted.Pos, Is.EqualTo(oldPos + d));
            c.SendPlayerState(oldPos + d, 0, 0, Vector3.zero, 0, true, 0); Drain(h);
            Assert.That(h.Server.Combat.LiveGrenades, Is.EqualTo(1), "pending throw moved with its frame and passed the real origin check");
            Assert.That(h.Server.Projectiles.All.Single().Pos.x, Is.EqualTo(1));
            Assert.That(h.Server.Projectiles.All.Single().Pos.z, Is.EqualTo(-1));
            h.Step(10); Assert.That(recoveries, Is.Zero, "coordinate shift is not an envelope violation");
        }

        [Test]
        public void Rebase_KeepsVehicleSeatAuthorityBedClaimAndCropDeadline()
        {
            var h = new TransactionalHarness(8845); h.Server.SpawnProvider = _ => new Vector3(1025, 0, 0);
            h.Connected("driver"); h.Step(8); var c = h.Clients[0];
            h.Server.Interactables.RegisterBed(1000, new Vector3(1026, 0, 0));
            c.SendClaimBed(1000); h.Step(5);
            var car = h.Server.Vehicles.ServerSpawn(h.Server.Ids.Mint(), 1, 0, new Vector3(1025, 0, 0), h.Server.Session.CurrentTick, 30);
            h.Server.Vehicles.ServerPublishVitals(new NetId(car.NetIdValue), 10, 300, 10, false, h.Server.Session.CurrentTick);
            c.SendEnterVehicle(car.NetIdValue, 0); h.Step(8);
            c.SendVehicleState(car.NetIdValue, car.Pos, Vector3.zero, Vector3.zero, Vector3.zero, 0, 0, 0, false, 0, 0); Drain(h);
            Assert.That(h.Server.VehicleHost.TryGetPredictedState(car.NetIdValue, out _), Is.True);
            h.Server.Crops.Schema.Register(new CropNetDef { SeedId = 333, GrowthSeconds = 120, YieldItemId = 13 });
            var crop = h.Server.Crops.ServerPlant(h.Server.Ids.Mint(), 333, new Vector3(1027, 0, 0), h.Server.Session.CurrentTick);
            long planted = crop.PlantedAtTick; double interactableTime = h.Server.Interactables.Now;
            int recoveries = 0; c.VehicleRecov += _ => recoveries++;
            WorldRebase.Translate(h.Server, c, new Vector3(-1024, 0, 0));
            Assert.That(car.DriverPlayerId, Is.EqualTo(c.PlayerId));
            Assert.That(h.Server.VehicleHost.TryGetPredictedState(car.NetIdValue, out var driving), Is.True);
            Assert.That(driving.Pos, Is.EqualTo(new Vector3(1, 0, 0)));
            Assert.That(h.Server.Interactables.TryGetSpawn(c.PlayerId, out var spawn, out _), Is.True);
            Assert.That(spawn, Is.EqualTo(new Vector3(2, 0, 0)));
            Assert.That(h.Server.Interactables.Now, Is.EqualTo(interactableTime));
            Assert.That(crop.PlantedAtTick, Is.EqualTo(planted)); Assert.That(crop.Pos, Is.EqualTo(new Vector3(3, 0, 0)));
            c.SendVehicleState(car.NetIdValue, new Vector3(1, 0, 0), Vector3.zero, Vector3.zero, Vector3.zero, 0, 0, 0, false, 0, 0); Drain(h);
            Assert.That(recoveries, Is.Zero, "same vehicle pose in its new frame remains inside the authority envelope");
        }

        [Test]
        public void UnboundedStorage_LeavesOriginalQuantizationUntouched_AndKeepsFarSavePositions()
        {
            var h = new TransactionalHarness(8843).Connected("owner"); h.Step(8);
            WorldRebase.EnableUnboundedStorage(h.Server);
            foreach (float n in new[] { -4096f, -1024.001f, -.000001f, 0f, 1.999999f, 4095.999f })
            {
                var p = new Vector3(n, -.000001f, n);
                var e = h.Server.WorldItems.ServerSpawn(h.Server.Ids.Mint(), new Item(67, 1, 100), p, 0);
                Assert.That(e.Pos, Is.EqualTo(PlayerReplication.Quantize(p)), "original in-range floor/near-zero behavior");
            }
            var distant = h.Server.WorldItems.ServerSpawn(h.Server.Ids.Mint(), new Item(67, 1, 100), new Vector3(-12000.125f, 2, 15000.25f), 0);
            uint id = distant.NetIdValue;
            WorldRebase.Translate(h.Server, h.Clients[0], new Vector3(-1024, 0, 1024));
            var saved = WorldSave.Capture(h.Server, "test", 0, 0, 1200);
            Assert.That(saved.WorldItems.Any(i => i.X == -13024.125f && i.Z == 16024.25f), Is.True);
            var restored = new TransactionalHarness(8844); WorldRebase.EnableUnboundedStorage(restored.Server);
            saved.ApplyWorld(restored.Server, 0);
            Assert.That(restored.Server.WorldItems.All.Any(i => i.Pos == new Vector3(-13024.125f, 2, 16024.25f)), Is.True);
            WorldRebase.Translate(h.Server, h.Clients[0], new Vector3(1024, 0, -1024));
            Assert.That(distant.NetIdValue, Is.EqualTo(id));
            Assert.That(distant.Pos, Is.EqualTo(new Vector3(-12000.125f, 2, 15000.25f)));
        }
    }
}
