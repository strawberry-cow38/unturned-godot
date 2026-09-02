using Godot;
using System.Collections.Generic;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // The "phantom eaglefire" (strawberry 2026-09-02: "you spawn with a phantom eaglefire in your hands").
    //
    // A joiner on the dedicated server spawned LOOKING at a rifle that existed on no grid: the MP shell's
    // SpawnShell called EquipHotbar(1) for a primary slot that has been empty since the clothes-only spawn kit
    // (2026-08-16), and an empty-slot equip with nothing in hand is a silent return -- so the viewmodel that
    // PlayerController._Ready builds by default (`new Viewmodel { GunName = "eaglefire" }`, no Gun loaded, no
    // backing item) simply stayed on screen. Not the spawn kit: the server grid was empty the whole time.
    //
    // TEETH: the assertions are on the VIEWMODEL, not the inventory. `Gun == null` and an empty primary slot
    // were both already true with the bug, which is exactly why "phantom" was the right word for it. Reverting
    // EquipUnarmed -> EquipHotbar(1) leaves VM.IsGunViewmodel true and VM.Fists false, and this fails.
    public class NetShellSpawnsUnarmed : GameTest
    {
        public override string Name => "net.shell_spawns_unarmed";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                syncLoad: true, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (the ONE world path, flat fallback on CI)", world.Ready);

            var net = new MemNetwork(20260902);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "joiner" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned on the first authoritative own-entity sample", sess.Shell != null);
            if (sess.Shell == null) yield break;
            var shell = sess.Shell;

            // let the join snapshot's owner inventory block land and be adopted before judging the hands
            yield return Ticks(60);

            var serverInv = ded.Server.Transactions.InventoryForTest(sess.Client.PlayerId);
            T.Check("the server grid exists for the joiner", serverInv != null);
            if (serverInv == null) { world.Sim.Sim.Remove(pump); yield break; }
            T.Check("the server's spawn kit has NO gun: primary and secondary slots are empty (clothes only)",
                    serverInv.items[0].getItemCount() == 0 && serverInv.items[1].getItemCount() == 0);
            T.Check("the shell adopted that: its own primary slot is empty too", shell.Inventory.items[0].getItemCount() == 0);

            // the hands agree with the grid: nothing held, no GunDef loaded, and -- the actual bug -- the
            // viewmodel on screen is the FISTS one, not a gun viewmodel with no gun behind it
            T.Check("nothing is held (HasSomethingHeld false)", !shell.HasSomethingHeld);
            T.Check("no GunDef is loaded (Gun null)", shell.Gun == null);
            T.Check("a viewmodel exists (arms render)", shell.VM != null && GodotObject.IsInstanceValid(shell.VM));
            T.Check($"the viewmodel is the FISTS viewmodel (Fists={shell.VM?.Fists})", shell.VM != null && shell.VM.Fists);
            T.Check($"...and NOT a gun viewmodel with no gun behind it (IsGunViewmodel={shell.VM?.IsGunViewmodel})",
                    shell.VM != null && !shell.VM.IsGunViewmodel);
            // the bug-report line said "empty" while a rifle filled the screen -- fists are a melee, so unarmed reads as one
            T.Check($"the bug-report line names the fists ('{shell.EquippedNameForReport}', expected 'melee:fists')", shell.EquippedNameForReport == "melee:fists");

            world.Sim.Sim.Remove(pump);
        }
    }
}
