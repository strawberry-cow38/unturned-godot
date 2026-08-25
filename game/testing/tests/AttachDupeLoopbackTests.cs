using Godot;
using System.Collections.Generic;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // THE ATTACHMENT/MAGAZINE DUPE, THROUGH THE LOOPBACK (strawberry 2026-08-16: "the attachment dupe is still
    // there", "inv dupe still happens with gun magazines").
    //
    // WHY THIS SUITE EXISTS AT ALL, which matters more than the bug: every other inventory test in this repo
    // drives a bare PlayerController with no server in the loop. This game runs SINGLEPLAYER THROUGH A LOOPBACK
    // SERVER (strawberry: "SP is MP"), and on that path the SERVER owns the bag -- the client's copy is adopted
    // from the server's grid on every owner update. So a local mutation that never reaches the server is
    // silently reverted by the next echo.
    //
    // That is the dupe. Fitting an attachment removes it from the CLIENT bag and puts it on the gun; the server
    // is never told, still believes the player owns it, and hands it straight back. The player keeps the item
    // AND the thing they fitted. NetShellMoveItem's own comment already names the hazard for drags -- "the SP
    // local drag would be silently reverted by the next echo anyway" -- but nothing covered attachments.
    //
    // Two "fixes" of mine passed their tests today and left this untouched, because those tests exercise a
    // configuration nobody plays. A suite that cannot see the bug is not evidence about the bug.
    public sealed class AttachDupeLoopbackTests : GameTest
    {
        public override string Name => "net.attach_no_dupe";
        public override double TimeoutSimSeconds => 40;

        const ushort MagId = 6;   // Military STANAG -- what PopulateDemoKit stocks and what the eaglefire takes

        static int CountOf(PlayerInventory inv, ushort id)
        {
            int n = 0;
            for (byte b = 0; b < (byte)(PlayerInventory.PAGES - 2); b++)
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
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "fitter" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned", sess.Shell != null);
            if (sess.Shell == null) yield break;
            T.Check("seeded the fixture kit into the server grid", Rigs.SeedServerKit(ded, sess));
            T.Check("server owns an inventory for the joiner", ded.Server.Inventories.TryGet(sess.Client.PlayerId, out _));
            if (!ded.Server.Inventories.TryGet(sess.Client.PlayerId, out var sInv)) yield break;

            // Wait for the shell to ADOPT the server grid, so both sides start agreeing. Without this the counts
            // below compare a bag that simply has not synced yet, and every number is meaningless.
            yield return Until(() => CountOf(sess.Shell.Inventory, MagId) == CountOf(sInv.Inventory, MagId)
                                  && CountOf(sInv.Inventory, MagId) > 0, 5);
            int serverBefore = CountOf(sInv.Inventory, MagId);
            int clientBefore = CountOf(sess.Shell.Inventory, MagId);
            T.Check($"client and server agree on the magazine count before ({clientBefore} vs {serverBefore})",
                serverBefore > 0 && clientBefore == serverBefore);
            if (serverBefore == 0) yield break;

            // FIT ONE, exactly as the ring's click does.
            int cal = Assets.find(MagId)?.magCaliber ?? 0;
            var spare = AttachmentFit.InBagInstances(sess.Shell.Inventory, "Magazine", cal);
            T.Check($"the shell can see a fittable magazine ({spare.Count})", spare.Count > 0);
            if (spare.Count == 0) yield break;
            // Driven through the MENU INSTANCE, not the static helper. The first cut of this called
            // FitAttachmentTo directly and kept failing after the fix was in -- because the wire routing lives
            // on the instance path the ring's click actually uses, and the static one is the bare local swap.
            // Testing the function instead of the path is the same mistake that let this bug ship twice.
            T.Check($"the shell's bag is server-owned (the wire seams are live: {sess.Shell.InventoryIsServerOwned})",
                sess.Shell.InventoryIsServerOwned);
            var menu = new AttachmentMenu { Player = sess.Shell };
            World.AddChild(menu);
            yield return Ticks(1);
            bool fitted = menu.FitAttachment("Magazine", MagId, spare[0].Item);
            T.Check("fitting the magazine succeeds", fitted);
            // NOT asserting an immediate local drop: on the wire path the server owns the removal and the echo
            // applies it, which is the entire point. What matters is where it ends up, checked below.

            // THE SPEND HAS TO REACH THE SERVER. This is the check that fails today: the attachment path makes
            // no wire call at all, so the server still holds the magazine.
            yield return Ticks(30);
            T.Check($"the SERVER grid also spent the magazine ({serverBefore} -> {CountOf(sInv.Inventory, MagId)})",
                CountOf(sInv.Inventory, MagId) == serverBefore - 1);

            // ...and therefore the echo must not put it back. This is the bug as the player experiences it:
            // "the mag reappears when I update the inventory".
            yield return Ticks(30);
            int clientAfter = CountOf(sess.Shell.Inventory, MagId);
            T.Check($"the owner echo does NOT hand the magazine back ({clientBefore} -> {clientAfter}, fitted one)",
                clientAfter == clientBefore - 1);

            world.Sim.Sim.Remove(pump);
        }
    }
}
