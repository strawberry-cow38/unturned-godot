using System.Collections.Generic;
using Godot;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // VENDING MACHINE PROMPTS AND OUTLINE (strawberry 2026-10-07: "add prompts and feedback (outline color) to vending
    // machines. ie showing insufficient funds, $1 soda/cola, dispensing, no power etc").
    //
    // The player path: walk up, LOOK at the machine, read what it says, press F. Through the singleplayer loopback, so
    // the wallet is the server's and a "Ready" machine is one the server will actually take a dollar for. Every state
    // is read off the billboard and the rim colour actually on screen, not off the machine's own answer.
    public sealed class VendingPromptTests : GameTest
    {
        public override string Name => "vending.prompts_and_outline";
        public override double TimeoutSimSeconds => 40;

        Step Wait(System.Func<bool> c, double seconds) { int n = 0, max = (int)(seconds * 50); return Until(() => c() || ++n >= max, seconds + 1); }

        static string Dir => ProjectSettings.GlobalizePath("res://content/objects/");

        public override IEnumerable<Step> Run()
        {
            bool power0 = PowerNet.GlobalPower;
            PlayerController.DebugForceLookScan = true;   // headless never captures the mouse, and the look scan needs it
            try { foreach (var s in Body()) yield return s; }
            finally { PlayerController.DebugForceLookScan = false; PowerNet.SetGlobalPower(power0); }
        }

        static int Dollars(PlayerInventory inv) => inv.getItemCount(Currency.StackId);

        // F, through the controller's real input handler -- the same dispatch a key press reaches.
        static void PressF(PlayerController p)
        {
            p._UnhandledInput(new InputEventKey { Pressed = true, PhysicalKeycode = Key.F, Keycode = Key.F });
            p._UnhandledInput(new InputEventKey { Pressed = false, PhysicalKeycode = Key.F, Keycode = Key.F });
        }

        IEnumerable<Step> Body()
        {
            ItemCatalog.RegisterAll();
            Rigs.Ground(World);

            // ---- the RED machine, placed the way WorldBuilder places it: the prop mesh under the world, upright, a body
            // on the small-prop layer carrying the device's hit meta, and the device made from that mesh.
            var mesh = ObjMesh.Load(Dir + "Vendor_0.obj");
            var xf = new Transform3D(new Basis(Vector3.Right, Mathf.DegToRad(270f)), Vector3.Zero);
            var mi = new MeshInstance3D { Mesh = mesh, Transform = xf };
            World.AddChild(mi);
            var vend = VendingMachine.Make(mi, "Vendor_0");
            World.AddChild(vend);
            var body = new StaticBody3D { Transform = xf, CollisionLayer = (1u << 6) | (1u << 8) };
            body.AddChild(new CollisionShape3D { Shape = ObjMesh.TrimeshShape(mesh) });
            body.SetMeta(VendingMachine.HitMeta, vend);
            World.AddChild(body);
            T.Check($"a red machine pours cola ({vend.MachineName})", vend.DrinkId == VendingMachine.ColaId && vend.MachineName == "Cola Machine");

            // its FRONT is mesh +Y, which the upright basis turns to world -Z
            var driver = new SimDriver(); World.AddChild(driver);
            var player = Rigs.Player(World, new Vector3(0f, 1f, -2.2f));
            yield return Ticks(2);
            var loop = new MpLoopback { Player = player, Driver = driver, ConsumeDeployables = true };
            World.AddChild(loop);
            yield return Wait(() => loop.Client.State == NetSessionState.Connected && loop.Server.Inventories.TryGet(loop.Client.PlayerId, out _), 15);
            ushort pid = loop.Client.PlayerId;
            loop.Server.Inventories.TryGet(pid, out var sinv);
            T.Check("the wallet is the server's", sinv != null && player.InventoryIsServerOwned);
            if (sinv == null) yield break;
            for (byte p = 0; p < PlayerInventory.OWNPAGES; p++) sinv.Inventory.items[p]?.clear();
            sinv.Inventory.items[2].raiseStateUpdated();

            var face = vend.GlobalTransform * new Vector3(0f, 0.6f, 1.25f);   // the front panel, chest height
            player.DebugLookAt(face);
            yield return Wait(() => player.DebugFocusVendor == vend, 2);
            T.Check("looking at it focuses it", player.DebugFocusVendor == vend);
            var bb = player.DebugVendInfo;

            // ---- 1. NO POWER
            PowerNet.SetGlobalPower(false);
            yield return Wait(() => bb?.DebugPromptText == "No power", 2);
            bb = player.DebugVendInfo;
            T.Check($"no power: the billboard says so ({bb?.DebugNameText} / {bb?.DebugPromptText})",
                bb != null && bb.DebugShown && bb.DebugNameText == "Cola Machine" && bb.DebugPromptText == "No power");
            T.Check($"...and the rim is GREY ({WorldItem.FocusColor})", WorldItem.FocusColor == VendingMachine.NoPowerColor && vend.DebugOutlineVisible);
            int d0 = Dollars(sinv.Inventory);
            PressF(player);
            yield return Ticks(3);
            T.Check($"F with no power is refused, and the rim FLASHES (denying {vend.Denying}, rim {WorldItem.FocusColor})",
                vend.Denying && (WorldItem.FocusColor == VendingMachine.NoFundsColor || WorldItem.FocusColor == Colors.White));
            yield return Wait(() => !vend.Denying, 2);
            yield return Ticks(3);
            T.Check($"...then settles back to grey ({WorldItem.FocusColor})", WorldItem.FocusColor == VendingMachine.NoPowerColor);

            // ---- 2. POWER, NO MONEY
            PowerNet.SetGlobalPower(true);
            yield return Wait(() => player.DebugVendInfo?.DebugPromptText?.StartsWith("Insufficient") == true, 2);
            bb = player.DebugVendInfo;
            T.Check($"powered but broke: \"{bb?.DebugPromptText}\" in red", bb?.DebugPromptText == "Insufficient funds -- $1 cola" && bb.DebugPromptColor == VendingMachine.NoFundsColor);
            T.Check($"...rim RED ({WorldItem.FocusColor})", WorldItem.FocusColor == VendingMachine.NoFundsColor);
            PressF(player);
            yield return Ticks(3);
            T.Check($"F while broke is refused -- flash, no vend ({vend.StateFor(player)})", vend.Denying && vend.StateFor(player) == VendingMachine.VendState.NoFunds);

            // ---- 3. ONE DOLLAR: ready
            yield return Wait(() => !vend.Denying, 2);
            sinv.Inventory.items[2].addItem(0, 0, 0, new Item(Currency.StackId, 1));
            sinv.Inventory.items[2].raiseStateUpdated();
            yield return Wait(() => Dollars(player.Inventory) == 1 && player.DebugVendInfo?.DebugPromptText?.Contains("$1 cola") == true
                                    && !player.DebugVendInfo.DebugPromptText.StartsWith("Insufficient"), 3);
            bb = player.DebugVendInfo;
            string want = $"[{Keybinds.Get(GameAction.Interact).Label}] $1 cola";
            T.Check($"with $1: \"{bb?.DebugPromptText}\" in green (want \"{want}\")", bb?.DebugPromptText == want && bb.DebugPromptColor == VendingMachine.ReadyColor);
            T.Check($"...rim GREEN ({WorldItem.FocusColor})", WorldItem.FocusColor == VendingMachine.ReadyColor);

            // ---- 4. BUY: dispensing, then the can, then broke again
            int items0 = loop.Server.WorldItems.Count;
            PressF(player);
            yield return Ticks(3);
            bb = player.DebugVendInfo;
            T.Check($"F buys: \"{bb?.DebugPromptText}\", rim AMBER ({WorldItem.FocusColor})",
                bb?.DebugPromptText == "Dispensing..." && WorldItem.FocusColor == VendingMachine.DispensingColor);
            yield return Wait(() => Dollars(sinv.Inventory) == 0, 2);
            T.Check($"the SERVER took the dollar ({Dollars(sinv.Inventory)} left)", Dollars(sinv.Inventory) == 0);
            yield return Wait(() => loop.Server.WorldItems.Count > items0, 3);
            bool cola = false;
            foreach (var e in loop.Server.WorldItems.All) if (e.ItemId == VendingMachine.ColaId) cola = true;
            T.Check($"a cola dropped out ({loop.Server.WorldItems.Count - items0} new item)", cola);
            yield return Wait(() => player.DebugVendInfo?.DebugPromptText?.StartsWith("Insufficient") == true, 3);
            T.Check($"once it has dropped, the machine says you are broke again ({player.DebugVendInfo?.DebugPromptText})",
                player.DebugVendInfo?.DebugPromptText == "Insufficient funds -- $1 cola" && WorldItem.FocusColor == VendingMachine.NoFundsColor);

            // ---- 5. NO WAY TO PAY (a joined multiplayer client has no vend command): it says so instead of promising
            sinv.Inventory.items[2].addItem(0, 0, 0, new Item(Currency.StackId, 1));
            sinv.Inventory.items[2].raiseStateUpdated();
            yield return Wait(() => Dollars(player.Inventory) == 1, 3);
            var pay = player.NetVendPay;
            player.NetVendPay = null;
            yield return Wait(() => player.DebugVendInfo?.DebugPromptText == "Out of order", 2);
            T.Check($"no vend route: \"{player.DebugVendInfo?.DebugPromptText}\", rim grey ({WorldItem.FocusColor})",
                player.DebugVendInfo?.DebugPromptText == "Out of order" && WorldItem.FocusColor == VendingMachine.NoPowerColor);
            player.NetVendPay = pay;
            yield return Wait(() => player.DebugVendInfo?.DebugPromptText?.EndsWith("] $1 cola") == true, 2);
            T.Check($"CONTROL: route back, it offers the can again ({player.DebugVendInfo?.DebugPromptText})", player.DebugVendInfo?.DebugPromptText?.EndsWith("] $1 cola") == true);

            // ---- 6. LOOK AWAY: the prompt goes, the rim goes
            player.DebugLookAt(player.GlobalPosition + new Vector3(0f, 1.5f, -10f));
            yield return Wait(() => player.DebugFocusVendor == null, 2);
            T.Check("looking away drops the prompt and the outline", player.DebugFocusVendor == null && !(player.DebugVendInfo?.DebugShown ?? false) && !vend.DebugOutlineVisible);
        }
    }
}
