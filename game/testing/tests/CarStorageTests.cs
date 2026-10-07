using System.Collections.Generic;
using Godot;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // v58 CAR STORAGE, in the engine (strawberry 2026-10-07: "when opening the inventory in a car, add several storage
    // 'containers' (like how we have both a fridge and freezer compartment on fridges) for each seat + glovebox").
    //
    // The rules are L0 (VehicleStorageTests). What only the game can show is the ROUTE from the key a player presses to
    // the grids they see: sit in a real sedan, open the inventory the way the key does, and find the glovebox and four
    // named seat pockets drawn in the dashboard -- off the server's containers, through the owner echo. Then the same
    // through a JOINED client, whose shell never drives a node and knows its car only by the seat the server latched.
    //
    // ⚠ The trunk half is a regression test for a bug that predates this: the trunk was a client-only grid, and under a
    // server-owned inventory (the singleplayer loopback, and every joiner) a drag into it was refused -- the server had
    // opened nothing. Measured before the fix: item still in the pocket, trunk empty.
    public sealed class CarStorageLoopbackTests : GameTest
    {
        public override string Name => "carstorage.loopback_cabin_and_trunk";
        public override double TimeoutSimSeconds => 60;

        Step Wait(System.Func<bool> c, double seconds, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        {
            int n = 0, max = (int)(seconds * 50);
            return Until(() => { if (c()) return true; if (++n < max) return false; Log.Print($"[carstorage] wait at line {line} ran out ({seconds}s)"); return true; }, seconds + 1);
        }

        static readonly string[] Seats = { "Driver's seat", "Front passenger", "Rear left", "Rear right" };

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            Rigs.Ground(World);
            var driver = new SimDriver(); World.AddChild(driver);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(2);
            var loop = new MpLoopback { Player = player, Driver = driver, ConsumeDeployables = true };
            World.AddChild(loop);
            yield return Wait(() => loop.Client.State == NetSessionState.Connected && loop.Server.Inventories.TryGet(loop.Client.PlayerId, out _), 15);
            ushort pid = loop.Client.PlayerId;
            loop.Server.Inventories.TryGet(pid, out var sinv);
            T.Check("the loopback owns the inventory", sinv != null && player.InventoryIsServerOwned);
            if (sinv == null) yield break;

            var car = Vehicle.BuildByName("sedan"); World.AddChild(car); car.GlobalPosition = new Vector3(3f, 1f, 0f);
            uint vid = 0;
            yield return Wait(() => loop.VehicleSync != null && loop.VehicleSync.TryGetNetId(car, out vid), 5);
            T.Check($"the sedan is a server vehicle ({vid})", vid != 0);

            // what the sedan says it has, off its own seats and hull
            var shape = car.StorageShape();
            T.Check($"a sedan has a glovebox and a trunk ({shape.GloveboxWidth}x{shape.GloveboxHeight}, trunk {shape.TrunkWidth}x{shape.TrunkHeight})",
                shape.HasCabin && shape.HasTrunk);
            var names = new List<string>(); foreach (var s in shape.Seats) names.Add(s.label);
            T.Check($"...and a pocket per seat, named by where the seat is ({string.Join(" / ", names)})",
                names.Count == 4 && names[0] == Seats[0] && names[1] == Seats[1] && names.Contains(Seats[2]) && names.Contains(Seats[3]));

            void Seed(byte page, ushort id)
            {
                sinv.Inventory.items[page].clear();
                sinv.Inventory.items[page].addItem(0, 0, 0, new Item(id));
                sinv.Inventory.items[page].raiseStateUpdated();
            }
            Seed(2, 15);   // a medkit in the pockets
            yield return Wait(() => player.Inventory.items[2].getItemCount() == 1, 5);

            // ---- 1. SIT DOWN, OPEN THE INVENTORY: the glovebox and the four seats, drawn.
            player.EnterVehicle(car, 0);
            yield return Wait(() => loop.Server.VehicleStorage.IsSeatedIn(pid, vid), 5);
            T.Check("seated, as far as the server is concerned", loop.Server.VehicleStorage.IsSeatedIn(pid, vid));
            player.OpenInventory();
            int openTicks = 0;
            byte rearLeft = 0;
            yield return Wait(() => { openTicks++; return player.OpenCompartmentLabels.Length == 4 && player.Inventory.items[PlayerInventory.COMPARTMENT0 + 3].width == 4; }, 5);
            Log.Print($"[carstorage] cabin labels + pages arrived after {openTicks} physics ticks");
            for (int i = 0; i < player.OpenCompartmentLabels.Length; i++) if (player.OpenCompartmentLabels[i] == "Rear left") rearLeft = (byte)(PlayerInventory.COMPARTMENT0 + i);
            T.Check($"the inventory opened onto the car: \"{player.OpenStorageLabel}\" + {player.OpenCompartmentLabels.Length} seats",
                player.OpenStorageLabel == "Glovebox" && player.OpenCompartmentLabels.Length == 4 && rearLeft != 0);
            // The dashboard repaints on a FRAME when it sees its pages change, not on a physics tick -- so wait for the
            // drawing itself (bounded), rather than a fixed tick count that passed or failed on frame timing.
            var ui = player.DebugInvUI;
            bool Drawn() { var t = ui?.DebugContainerTitles; if (t == null) return false; foreach (var s in t) if (s == "Rear left") return true; return false; }
            int drawTicks = 0;
            yield return Wait(() => { drawTicks++; return Drawn(); }, 3);
            Log.Print($"[carstorage] seats drawn after {drawTicks} physics ticks");
            T.Check($"the dashboard is open ({ui != null && ui.IsOpen})", ui != null && ui.IsOpen);
            var titles = ui?.DebugContainerTitles != null ? new List<string>(ui.DebugContainerTitles) : new List<string>();
            T.Check($"...and DREW them, glovebox first: [{string.Join(", ", titles)}]",
                titles.Count >= 5 && titles[0] == "Glovebox" && titles.Contains("Driver's seat") && titles.Contains("Rear left") && titles.Contains("Rear right"));
            T.Check("...as grids you can drop on (the rear-left pocket has a cell layout)", ui != null && ui.DebugCellPoint(rearLeft, 0, 0, out _));

            // ---- 2. PUT SOMETHING ON THE BACK SEAT: it is in the car, on the server.
            var jar = player.Inventory.items[2].getItem(0);
            player.RequestMoveItem(2, jar.x, jar.y, rearLeft, 0, 0, 0);
            uint cabin = loop.Server.VehicleStorage.CrateFor(vid, VehicleStorageKind.Cabin);
            loop.Server.Inventories.TryGetCrate(cabin, out var cabinCrate);
            int seatIdx = rearLeft - PlayerInventory.COMPARTMENT0;   // -10 when the cabin never opened: guarded, so the TRUNK half below still runs
            int OnSeat() => cabinCrate != null && seatIdx >= 0 && seatIdx < cabinCrate.Compartments.Length ? cabinCrate.Compartments[seatIdx].Grid.getItemCount() : -1;
            yield return Wait(() => OnSeat() == 1, 5);
            T.Check("the medkit is on the car's rear-left seat, server-side", OnSeat() == 1 && sinv.Inventory.items[2].getItemCount() == 0);

            // ---- 3. GET OUT: the car's grids go, the medkit stays in the car.
            T.Check("got out", player.TryExitVehicle());
            byte rl = rearLeft != 0 ? rearLeft : (byte)(PlayerInventory.COMPARTMENT0 + 2);
            yield return Wait(() => player.OpenCompartmentLabels.Length == 0 && player.Inventory.items[rl].width == 0, 5);
            T.Check($"leaving the car shut its grids ({player.OpenCompartmentLabels.Length} names, seat page {player.Inventory.items[rl].width} wide)",
                player.OpenCompartmentLabels.Length == 0 && player.Inventory.items[rl].width == 0);
            T.Check("...and left the medkit on the seat", OnSeat() == 1);
            if (ui != null && ui.IsOpen) { player.CloseCrate(); ui.Close(); }
            yield return Ticks(5);

            // ---- 4. THE TRUNK, from outside: it takes an item now, and keeps it.
            Seed(2, 15);
            yield return Wait(() => player.Inventory.items[2].getItemCount() == 1, 5);
            player.GlobalPosition = car.GlobalPosition + new Vector3(0f, 0f, 2.5f);
            yield return Ticks(5);
            player.OpenVehicleTrunkForTest(car);
            yield return Wait(() => player.OpenStorageLabel == "Trunk" && player.Inventory.items[PlayerInventory.STORAGE].width == 6, 5);
            T.Check($"F on the trunk opened the SERVER's trunk ({player.OpenStorageLabel}, {player.Inventory.items[PlayerInventory.STORAGE].width}x{player.Inventory.items[PlayerInventory.STORAGE].height})",
                player.OpenStorageLabel == "Trunk" && player.Inventory.items[PlayerInventory.STORAGE].width == 6);
            jar = player.Inventory.items[2].getItem(0);
            player.RequestMoveItem(2, jar.x, jar.y, PlayerInventory.STORAGE, 0, 0, 0);
            uint trunk = loop.Server.VehicleStorage.CrateFor(vid, VehicleStorageKind.Trunk);
            loop.Server.Inventories.TryGetCrate(trunk, out var trunkCrate);
            yield return Wait(() => trunkCrate != null && trunkCrate.Storage.getItemCount() == 1, 5);
            T.Check($"a drag into the trunk LANDS (trunk {trunkCrate?.Storage.getItemCount()}, pockets {sinv.Inventory.items[2].getItemCount()}) -- before v58 it was refused",
                trunkCrate != null && trunkCrate.Storage.getItemCount() == 1 && sinv.Inventory.items[2].getItemCount() == 0);
            player.CloseCrate();
            yield return Ticks(10);
            player.OpenVehicleTrunkForTest(car);
            yield return Wait(() => player.Inventory.items[PlayerInventory.STORAGE].getItemCount() == 1, 5);
            T.Check("...and it is still there when the trunk is opened again", player.Inventory.items[PlayerInventory.STORAGE].getItemCount() == 1);
            player.CloseCrate();
        }
    }

    public sealed class CarStorageJoinedClientTests : GameTest
    {
        public override string Name => "carstorage.joined_client_cabin";
        public override double TimeoutSimSeconds => 60;

        Step Wait(System.Func<bool> c, double seconds, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        {
            int n = 0, max = (int)(seconds * 50);
            return Until(() => { if (c()) return true; if (++n < max) return false; Log.Print($"[carstorage] wait at line {line} ran out ({seconds}s)"); return true; }, seconds + 1);
        }

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt", syncLoad: true, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready", world.Ready);
            ItemCatalog.RegisterAll();

            var net = new MemNetwork(20261007);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "passenger" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);

            var car = Vehicle.BuildByName("sedan"); World.AddChild(car); car.GlobalPosition = new Vector3(1f, 1.2f, 0f);

            yield return Wait(() => sess.Shell != null && sess.Client.Vehicles.Count == 1, 8);
            T.Check("joined, and the sedan replicated", sess.Shell != null && sess.Client.Vehicles.Count == 1);
            if (sess.Shell == null) yield break;
            ushort pid = sess.Client.PlayerId;
            uint vid = 0; foreach (var e in sess.Client.Vehicles.All) { vid = e.NetIdValue; break; }
            ded.Server.Inventories.TryGet(pid, out var sinv);
            sinv.Inventory.items[2].clear();
            sinv.Inventory.items[2].addItem(0, 0, 0, new Item(15));
            sinv.Inventory.items[2].raiseStateUpdated();
            var p = sess.Shell;
            yield return Wait(() => p.Inventory.items[2].getItemCount() == 1, 5);
            yield return Ticks(30);

            // in through the real enter command; the session latches the seat off the server's VehicleEntered
            bool entered = false;
            sess.Client.VehicleEntered += e => { if (e.PlayerId == pid) entered = true; };
            sess.Client.SendEnterVehicle(vid);
            yield return Wait(() => entered && ded.Server.VehicleStorage.IsSeatedIn(pid, vid), 5);
            T.Check("seated in the server's sedan", ded.Server.VehicleStorage.IsSeatedIn(pid, vid));
            yield return Ticks(5);
            // ⚠ the car the joined shell sits in is a local TWIN the server has no id for -- so the cabin open has to come
            // off the seat the session latched. A cabin keyed on the shell's own Driving node would pass the loopback test
            // and never open here. (Measured: IsDriving is TRUE here, on the twin -- an earlier version of this check
            // assumed the opposite and was wrong.)
            uint twinId = 0;
            T.Check($"the shell's car is a client twin the server cannot name (driving {p.IsDriving}, server id {(p.Driving != null && ded.VehicleSync.TryGetNetId(p.Driving, out twinId) ? twinId : 0)})",
                p.Driving != null && !ded.VehicleSync.TryGetNetId(p.Driving, out _));

            p.OpenInventory();
            yield return Wait(() => p.OpenCompartmentLabels.Length == 4 && p.Inventory.items[PlayerInventory.COMPARTMENT0].width == 4, 5);
            T.Check($"the inventory opened onto the car: \"{p.OpenStorageLabel}\" + {p.OpenCompartmentLabels.Length} seats",
                p.OpenStorageLabel == "Glovebox" && p.OpenCompartmentLabels.Length == 4);

            byte driverSeat = PlayerInventory.COMPARTMENT0;
            var jar = p.Inventory.items[2].getItem(0);
            p.RequestMoveItem(2, jar.x, jar.y, driverSeat, 0, 0, 0);
            uint cabin = ded.Server.VehicleStorage.CrateFor(vid, VehicleStorageKind.Cabin);
            ded.Server.Inventories.TryGetCrate(cabin, out var crate);
            yield return Wait(() => crate != null && crate.Compartments[0].Grid.getItemCount() == 1, 5);
            T.Check("a joiner's drag onto the driver's seat landed in the server's car",
                crate != null && crate.Compartments[0].Grid.getItemCount() == 1 && sinv.Inventory.items[2].getItemCount() == 0);
            world.Sim.Sim.Remove(pump);
        }
    }
}
