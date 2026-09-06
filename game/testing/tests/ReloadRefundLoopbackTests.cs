using Godot;
using System.Collections.Generic;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // RELOADING MUST NOT REFUND THE MAGAZINE (review 2026-08-16).
    //
    // DoMagSwap took the fresh magazine out of the bag and put the spent one back with a pair of LOCAL grid
    // calls. The server owns that grid -- singleplayer included -- and no reload command existed, so the next
    // owner echo overwrote both edits from server truth: the spare came back FULL and the partially-spent
    // magazine that had been returned was gone. One spare reloaded forever, and every returned magazine was
    // destroyed. The same shape as the attachment and magazine dupes, in the opposite direction.
    //
    // Counted, not eyeballed: every individual magazine in the bag is a legitimate object, and only the TOTAL
    // and the round counts are wrong.
    public sealed class ReloadRefundLoopbackTests : GameTest
    {
        public override string Name => "net.reload_no_refund";
        public override double TimeoutSimSeconds => 40;

        static int CountOf(PlayerInventory inv, ushort id)
        {
            int n = 0;
            for (byte b = 0; b < PlayerInventory.OWNPAGES; b++)
            {
                var pg = inv.items[b];
                if (pg == null) continue;
                for (byte i = 0; i < pg.getItemCount(); i++)
                    if (pg.getItem(i)?.item?.id == id) n++;
            }
            return n;
        }

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                syncLoad: true, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready", world.Ready);
            ItemCatalog.RegisterAll();

            var net = new MemNetwork(20260816);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "reloader" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned", sess.Shell != null);
            if (sess.Shell == null) yield break;
            if (!ded.Server.Inventories.TryGet(sess.Client.PlayerId, out var sInv)) { T.Check("server owns an inventory", false); yield break; }

            // A gun in the primary slot and EXACTLY ONE spare magazine, both seeded server-side.
            ItemAsset rifle = null;
            for (ushort id = 1; id < 2000 && rifle == null; id++)
            {
                var a = Assets.find(id);
                if (a?.gunName != null && a.slot == ESlotType.PRIMARY) rifle = a;
            }
            T.Check($"a PRIMARY gun exists ({rifle?.itemName})", rifle != null);
            if (rifle == null) yield break;
            sInv.Inventory.items[0].addItem(0, 0, 0, new Item(rifle.id));

            yield return Until(() => sess.Shell.Inventory.items[0].getItemCount() == 1, 5);
            sess.Shell.EquipHotbar(1);
            yield return Ticks(4);
            T.Check($"holding the rifle ({sess.Shell.HeldGunName ?? "none"})", sess.Shell.HasGunOut);
            if (!sess.Shell.HasGunOut) yield break;

            ushort magId = (ushort)(sess.Shell.Gun?.MagazineId ?? 0);
            T.Check($"the gun declares a magazine ({magId})", magId != 0);
            if (magId == 0) yield break;
            int cap = Assets.find(magId)?.magCapacity ?? 30;

            sInv.Inventory.items[2].clear();
            sInv.Inventory.items[2].addItem(0, 0, 0, new Item(magId, (byte)cap, 100));
            sInv.Inventory.items[2].raiseStateUpdated();   // clear() is silent by design -- announce the hand-seeded page

            yield return Until(() => CountOf(sess.Shell.Inventory, magId) == 1, 5);
            T.Check($"the client sees exactly one spare magazine ({CountOf(sess.Shell.Inventory, magId)})",
                CountOf(sess.Shell.Inventory, magId) == 1);

            // SPEND MOST OF THE LOADED MAGAZINE, then reload. One magazine goes in; the near-empty one comes out.
            sess.Shell.Ammo = 3;
            sess.Shell.DebugStartReload();
            yield return Until(() => !sess.Shell.DebugIsReloading, 8);
            yield return Ticks(30);   // let the command land and the owner block echo back

            // THE SERVER SPENT IT. This is the check that used to fail: no command existed, so the server grid
            // still held a full spare and the echo handed it straight back.
            int serverFull = 0, serverPartial = 0;
            for (byte b = 0; b < PlayerInventory.OWNPAGES; b++)
            {
                var pg = sInv.Inventory.items[b];
                for (byte i = 0; i < (pg?.getItemCount() ?? 0); i++)
                {
                    var it = pg.getItem(i)?.item;
                    if (it?.id != magId) continue;
                    if (it.amount >= cap) serverFull++; else serverPartial++;
                }
            }
            T.Check($"the SERVER no longer holds a full spare magazine ({serverFull})", serverFull == 0);
            T.Check($"...and it holds the partially-spent one that came out ({serverPartial})", serverPartial == 1);

            // ...so the echo cannot hand the spare back. Still exactly one magazine in the bag, and it is the
            // near-empty one, not a fresh full one.
            int clientCount = CountOf(sess.Shell.Inventory, magId);
            T.Check($"the player still owns exactly ONE magazine after reloading ({clientCount})", clientCount == 1);
            int amt = -1;
            for (byte b = 0; b < PlayerInventory.OWNPAGES && amt < 0; b++)
            {
                var pg = sess.Shell.Inventory.items[b];
                for (byte i = 0; i < (pg?.getItemCount() ?? 0); i++)
                    if (pg.getItem(i)?.item?.id == magId) { amt = pg.getItem(i).item.amount; break; }
            }
            T.Check($"...and it is the SPENT one, not a refilled spare ({amt} rounds, cap {cap})", amt >= 0 && amt < cap);

            world.Sim.Sim.Remove(pump);
        }
    }
}
