using System.Collections.Generic;
using NUnit.Framework;
using SDG.NetPak;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    /// <summary>
    /// v58 CAR STORAGE (strawberry 2026-10-07: "when opening the inventory in a car, add several storage 'containers'
    /// (like how we have both a fridge and freezer compartment on fridges) for each seat + glovebox"). Driven over the
    /// wire through the real command and the real owner echo: a car's grids are ordinary server containers, so what
    /// is being tested is that a CAR's rules -- who may open one, that it moves, that getting out shuts it -- sit on top
    /// of the container machinery without a second implementation of any of it.
    /// </summary>
    [TestFixture]
    public class VehicleStorageTests
    {
        [SetUp]
        public void SetUp() => TransactionalFixtures.RegisterAssets();

        static readonly string[] SeatNames = { "Driver's seat", "Front passenger", "Rear left", "Rear right" };

        /// <summary>A four-seat car with a trunk, at x=1 between the two harness players (x 0 and x 2).</summary>
        static uint SpawnCar(TransactionalHarness h, Vector3 at, bool trunk = true, bool cabin = true)
        {
            var id = h.Server.Ids.Mint();
            h.Server.Vehicles.ServerSpawn(id, 0, 0, at, h.Server.Session.CurrentTick);
            uint vid = id.Value;
            var prev = h.Server.VehicleStorage.ShapeOf;
            h.Server.VehicleStorage.ShapeOf = n =>
            {
                if (n != vid) return prev?.Invoke(n);
                var s = new VehicleStorageShape();
                if (trunk) { s.TrunkWidth = 6; s.TrunkHeight = 4; }
                if (cabin)
                {
                    s.GloveboxWidth = 3; s.GloveboxHeight = 2;
                    s.Seats = new (byte, byte, string)[] { (4, 2, SeatNames[0]), (4, 2, SeatNames[1]), (4, 2, SeatNames[2]), (4, 2, SeatNames[3]) };
                }
                return s;
            };
            return vid;
        }

        static PlayerInventory Mine(NetWorldClient c) => c.Inventories.TryGet(c.PlayerId, out var e) ? e.Inventory : null;

        [Test]
        public void a_seated_player_opens_the_glovebox_and_a_pocket_per_seat()
        {
            var h = new TransactionalHarness(9581).Connected("driver");
            var a = h.Clients[0];
            uint car = SpawnCar(h, new Vector3(1f, 0f, 0f));
            h.Server.Vehicles.ServerSetSeat(new NetId(car), 0, a.PlayerId, h.Server.Session.CurrentTick);
            h.Step(3);

            StorageOpenedEvent got = default; bool opened = false;
            a.StorageOpened += e => { got = e; opened = true; };
            a.SendOpenVehicleStorage(car, VehicleStorageKind.Cabin);
            Assert.That(h.StepUntil(() => opened && Mine(a)?.items[PlayerInventory.COMPARTMENT0 + 3].width == 4), Is.True,
                $"the cabin opened and its pages arrived (seed={h.Net.Seed})");

            Assert.That(got.StorageLabel, Is.EqualTo("Glovebox"), "Storage is the glovebox, by name");
            Assert.That((got.Width, got.Height), Is.EqualTo(((byte)3, (byte)2)));
            Assert.That(got.Compartments.Length, Is.EqualTo(4), "one compartment per seat");
            for (int i = 0; i < 4; i++)
                Assert.That(got.Compartments[i], Is.EqualTo(((byte)4, (byte)2, SeatNames[i])), $"seat {i} named and sized");

            var inv = Mine(a);
            Assert.That((inv.items[PlayerInventory.STORAGE].width, inv.items[PlayerInventory.STORAGE].height), Is.EqualTo(((byte)3, (byte)2)));
            for (int i = 0; i < 4; i++)
                Assert.That(inv.items[PlayerInventory.COMPARTMENT0 + i].width, Is.EqualTo(4), $"seat page {i} sized on the owner echo");
            // CONTROL: the page after the last seat stays shut -- a projection that sized every compartment page would
            // pass the four checks above on its own.
            Assert.That(inv.items[PlayerInventory.COMPARTMENT0 + 4].width, Is.EqualTo(0), "no fifth seat page");
        }

        [Test]
        public void an_item_put_on_a_seat_stays_in_the_car_and_getting_out_shuts_the_cabin()
        {
            var h = new TransactionalHarness(9582).Connected("driver", "passenger");
            var a = h.Clients[0]; var b = h.Clients[1];
            uint car = SpawnCar(h, new Vector3(1f, 0f, 0f));
            var vid = new NetId(car);
            h.Server.Vehicles.ServerSetSeat(vid, 0, a.PlayerId, h.Server.Session.CurrentTick);
            h.Server.Vehicles.ServerSetSeat(vid, 2, b.PlayerId, h.Server.Session.CurrentTick);
            h.Server.Inventories.TryGet(a.PlayerId, out var sa);
            sa.Inventory.items[2].clear();
            sa.Inventory.items[2].addItem(0, 0, 0, new Item(TransactionalFixtures.ScrapId));
            sa.Inventory.items[2].raiseStateUpdated();
            h.Step(3);

            bool aOpen = false, bOpen = false;
            a.StorageOpened += e => aOpen = true;
            b.StorageOpened += e => bOpen = true;
            a.SendOpenVehicleStorage(car, VehicleStorageKind.Cabin);
            b.SendOpenVehicleStorage(car, VehicleStorageKind.Cabin);
            Assert.That(h.StepUntil(() => aOpen && bOpen && Mine(a)?.items[PlayerInventory.COMPARTMENT0 + 2].width == 4), Is.True,
                $"driver and a rear passenger are both in the cabin (seed={h.Net.Seed})");

            // pockets -> the rear-left seat (compartment 2)
            byte rearLeft = (byte)(PlayerInventory.COMPARTMENT0 + 2);
            a.SendMoveItem(2, 0, 0, rearLeft, 1, 0, 0);
            uint crateId = h.Server.VehicleStorage.CrateFor(car, VehicleStorageKind.Cabin);
            h.Server.Inventories.TryGetCrate(crateId, out var crate);
            Assert.That(h.StepUntil(() => crate.Compartments[2].Grid.getItemCount() == 1), Is.True,
                $"the item is in the CAR's rear-left compartment, on the server (seed={h.Net.Seed})");
            Assert.That(crate.Storage.getItemCount() + crate.Compartments[0].Grid.getItemCount() + crate.Compartments[1].Grid.getItemCount()
                        + crate.Compartments[3].Grid.getItemCount(), Is.EqualTo(0), "...and nowhere else in the car");
            Assert.That(h.StepUntil(() => Mine(b)?.items[rearLeft].getItemCount() == 1), Is.True,
                "the other passenger watches it land, like a second person at a fridge");

            // the driver gets out: the server shuts the cabin for them and SAYS so
            bool aClosed = false;
            a.StorageClosed += e => aClosed = e.NetId == crateId;
            h.Server.Vehicles.ServerSetSeat(vid, 0, 0, h.Server.Session.CurrentTick);
            Assert.That(h.StepUntil(() => aClosed), Is.True, $"leaving the seat closed the cabin and told the client (seed={h.Net.Seed})");
            Assert.That(h.StepUntil(() => Mine(a)?.items[rearLeft].width == 0), Is.True, "the seat page left with it");
            Assert.That(crate.Viewers, Does.Not.Contain(a.PlayerId));
            Assert.That(crate.Viewers, Does.Contain(b.PlayerId), "the passenger who is still sitting there keeps it");

            // ...and cannot open it again from outside
            long refused = h.Server.Transactions.Diag.VehicleStorageRefused;
            a.SendOpenVehicleStorage(car, VehicleStorageKind.Cabin);
            Assert.That(h.StepUntil(() => h.Server.Transactions.Diag.VehicleStorageRefused == refused + 1), Is.True,
                "a player standing beside the car is not in it");

            // back in: the item is still on the seat
            h.Server.Vehicles.ServerSetSeat(vid, 0, a.PlayerId, h.Server.Session.CurrentTick);
            a.SendOpenVehicleStorage(car, VehicleStorageKind.Cabin);
            Assert.That(h.StepUntil(() => Mine(a)?.items[rearLeft].getItemCount() == 1), Is.True,
                $"what was left on the seat is there when you get back in (seed={h.Net.Seed})");
        }

        [Test]
        public void the_trunk_opens_within_reach_follows_the_car_and_shuts_when_it_drives_off()
        {
            var h = new TransactionalHarness(9583).Connected("loader");
            var a = h.Clients[0];
            uint car = SpawnCar(h, new Vector3(3f, 0f, 0f));
            h.Step(3);

            StorageOpenedEvent got = default; bool opened = false;
            a.StorageOpened += e => { got = e; opened = true; };
            a.SendOpenVehicleStorage(car, VehicleStorageKind.Trunk);
            Assert.That(h.StepUntil(() => opened && Mine(a)?.items[PlayerInventory.STORAGE].width == 6), Is.True,
                $"standing 3 m from the car, the trunk opens (seed={h.Net.Seed})");
            Assert.That(got.StorageLabel, Is.EqualTo("Trunk"));
            Assert.That(got.Compartments.Length, Is.EqualTo(0), "a trunk is one grid");

            // the car drives away with it open
            bool closed = false;
            a.StorageClosed += e => closed = true;
            h.Server.Vehicles.ServerPublish(new NetId(car), new Vector3(40f, 0f, 0f), Vector3.zero, Vector3.zero, Vector3.zero,
                0f, 1f, 1f, 1f, 0, h.Server.Session.CurrentTick);
            Assert.That(h.StepUntil(() => closed), Is.True, $"a trunk left open does not travel with the car (seed={h.Net.Seed})");
            uint crateId = h.Server.VehicleStorage.CrateFor(car, VehicleStorageKind.Trunk);
            h.Server.Inventories.TryGetCrate(crateId, out var crate);
            Assert.That((crate.Pos - new Vector3(40f, 0f, 0f)).magnitude, Is.LessThan(0.1f), "the container moved with the car");

            // and from 40 m it will not open
            long refused = h.Server.Transactions.Diag.VehicleStorageRefused;
            a.SendOpenVehicleStorage(car, VehicleStorageKind.Trunk);
            Assert.That(h.StepUntil(() => h.Server.Transactions.Diag.VehicleStorageRefused == refused + 1), Is.True, "out of reach");
        }

        [Test]
        public void a_car_with_no_cabin_or_no_trunk_refuses_and_a_gone_car_takes_its_containers()
        {
            var h = new TransactionalHarness(9584).Connected("a");
            var a = h.Clients[0];
            uint quad = SpawnCar(h, new Vector3(1f, 0f, 0f), trunk: false, cabin: false);
            h.Server.Vehicles.ServerSetSeat(new NetId(quad), 0, a.PlayerId, h.Server.Session.CurrentTick);
            h.Step(3);
            Assert.That(h.Server.VehicleStorage.CrateFor(quad, VehicleStorageKind.Cabin), Is.EqualTo(0u), "no cabin, no container");
            Assert.That(h.Server.VehicleStorage.CrateFor(quad, VehicleStorageKind.Trunk), Is.EqualTo(0u));
            Assert.That(h.Server.VehicleStorage.CrateFor(9999, VehicleStorageKind.Trunk), Is.EqualTo(0u), "no such vehicle");

            uint car = SpawnCar(h, new Vector3(1f, 0f, 0f));
            uint crateId = h.Server.VehicleStorage.CrateFor(car, VehicleStorageKind.Trunk);
            Assert.That(crateId, Is.Not.EqualTo(0u));
            Assert.That(h.Server.VehicleStorage.CrateFor(car, VehicleStorageKind.Trunk), Is.EqualTo(crateId), "made once, then reused");
            Assert.That(h.Server.Inventories.TryGetCrate(crateId, out _), Is.True);
            h.Server.Vehicles.ServerRemove(new NetId(car), h.Server.Session.CurrentTick);
            h.Step(2);
            Assert.That(h.Server.Inventories.TryGetCrate(crateId, out _), Is.False, "the containers went with the car");
            Assert.That(h.Server.VehicleStorage.CrateCount, Is.EqualTo(0));
        }

        [Test]
        public void a_pipe_adapter_never_binds_a_cars_container()
        {
            var h = new TransactionalHarness(9585).Connected("a");
            uint car = SpawnCar(h, new Vector3(1f, 0f, 0f));
            uint trunk = h.Server.VehicleStorage.CrateFor(car, VehicleStorageKind.Trunk);
            Assert.That(trunk, Is.Not.EqualTo(0u));
            Assert.That(ServerItemMovers.FindCrateFor(h.Server.Inventories, new Vector3(1f, 0f, 0f)), Is.EqualTo(0u),
                "nearest-crate binding skips it");
            Assert.That(ServerItemMovers.FindCrateFor(h.Server.Inventories, new Vector3(1f, 0f, 0f), trunk), Is.EqualTo(0u),
                "and so does naming it");
            // CONTROL: a fixed crate in the same spot IS bound, so the two zeros above are about the car, not the reach.
            var fixedCrate = h.Server.Inventories.ServerRegisterCrate(h.Server.Ids.Mint(), 2, 2, new Vector3(1f, 0f, 0f));
            Assert.That(ServerItemMovers.FindCrateFor(h.Server.Inventories, new Vector3(1f, 0f, 0f)), Is.EqualTo(fixedCrate.NetIdValue));
        }

        [Test]
        public void storage_opened_round_trips_labels_and_compartments()
        {
            var evt = new StorageOpenedEvent
            {
                NetId = 77, Width = 3, Height = 2, StorageLabel = "Glovebox",
                Compartments = new (byte, byte, string)[] { (4, 2, "Driver's seat"), (4, 2, "Rear left") },
            };
            var w = new NetPakWriter { buffer = new byte[256] };
            evt.Write(w); w.Flush();
            var r = new NetPakReader(); r.SetBufferSegment(w.buffer, w.writeByteIndex);
            Assert.That(StorageOpenedEvent.TryRead(r, out var back), Is.True);
            Assert.That(back.StorageLabel, Is.EqualTo("Glovebox"));
            Assert.That(back.Compartments, Is.EqualTo(evt.Compartments));

            // an ordinary container: no label, no compartments -- and it reads back as exactly that
            var plain = new StorageOpenedEvent { NetId = 5, Width = 5, Height = 4 };
            w = new NetPakWriter { buffer = new byte[256] };
            plain.Write(w); w.Flush();
            r = new NetPakReader(); r.SetBufferSegment(w.buffer, w.writeByteIndex);
            Assert.That(StorageOpenedEvent.TryRead(r, out var back2), Is.True);
            Assert.That(string.IsNullOrEmpty(back2.StorageLabel), Is.True);
            Assert.That(back2.Compartments.Length, Is.EqualTo(0));
        }

        [Test]
        public void a_save_keeps_the_players_own_pages_and_never_an_open_containers_view()
        {
            var h = new TransactionalHarness(9586).Connected("saver");
            var a = h.Clients[0];
            var crate = h.Server.Inventories.ServerRegisterCrate(h.Server.Ids.Mint(), 5, 4, new Vector3(1f, 0f, 0f));
            crate.Storage.tryAddItem(new Item(TransactionalFixtures.ScrapId));
            bool opened = false;
            a.StorageOpened += e => opened = true;
            a.SendOpenStorage(crate.NetIdValue);
            Assert.That(h.StepUntil(() => opened), Is.True);
            h.Server.Inventories.TryGet(a.PlayerId, out var sa);
            Assert.That(sa.Inventory.items[PlayerInventory.STORAGE].getItemCount(), Is.EqualTo(1), "the view holds the crate's item");

            var save = WorldSave.Capture(h.Server, "", 0, 0.5f, 1200f);
            var ps = save.Players.Find(p => p.Name == "saver");
            Assert.That(ps, Is.Not.Null);
            Assert.That(ps.Pages.Count, Is.EqualTo(PlayerInventory.OWNPAGES), "only the pages the player carries");
            int saved = 0;
            foreach (var pg in ps.Pages) saved += pg.Items.Count;
            Assert.That(saved, Is.EqualTo(0), "the crate's scrap is not in the PLAYER's save -- the crate keeps it");
        }

        [Test]
        public void food_left_on_a_seat_thaws_like_food_in_a_crate()
        {
            var h = new TransactionalHarness(9587).Connected("a");
            uint car = SpawnCar(h, new Vector3(1f, 0f, 0f));
            uint cabin = h.Server.VehicleStorage.CrateFor(car, VehicleStorageKind.Cabin);
            h.Server.Inventories.TryGetCrate(cabin, out var crate);
            var onSeat = new Item(TransactionalFixtures.BeansId) { frozen = 100 };
            var inGlovebox = new Item(TransactionalFixtures.BeansId) { frozen = 100 };
            crate.Compartments[2].Grid.tryAddItem(onSeat);
            crate.Storage.tryAddItem(inGlovebox);
            for (int i = 0; i < 10; i++) h.Server.Freezing.Step(1f);
            // CONTROL first: the glovebox is the container's ordinary grid and always thawed. If it did not, the seat
            // staying frozen below would say nothing about compartments.
            Assert.That(inGlovebox.frozen, Is.LessThan((byte)100), "the glovebox thaws (the control)");
            Assert.That(onSeat.frozen, Is.LessThan((byte)100), "...and so does a seat pocket");
            Assert.That(onSeat.frozen, Is.EqualTo(inGlovebox.frozen), "at the same rate");
        }
    }
}
