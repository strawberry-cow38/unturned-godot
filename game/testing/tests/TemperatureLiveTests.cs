using System.Collections.Generic;
using Godot;
using SDG.Unturned;
using SDG.NetTransport.Mem;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // TEMPERATURE, WITH A SERVER RUNNING (strawberry 2026-10-07: "confirm temperature and the temperature bar is actually
    // working. add a thermometer item that adds the temperature in °c/f on the temp bar").
    //
    // It was not working, in any game that has a server -- which is singleplayer too. The body-temperature step sat after
    // the early return that hands the vitals to the server, so the body never moved off its 19 C spawn value and the bar
    // sat dead centre; and the server stepped everyone's vitals as a permanent Comfortable, so cold and heat cost nothing.
    // console.temperature never saw it: it drives a bare player with no server, the one path where both still worked.
    //
    // So this goes through the singleplayer loopback, and a second test through a JOINED client, whose band has to cross
    // the wire.
    public sealed class TemperatureLiveTests : GameTest
    {
        public override string Name => "temperature.live_under_server";
        public override double TimeoutSimSeconds => 60;

        Step Wait(System.Func<bool> c, double seconds) { int n = 0, max = (int)(seconds * 50); return Until(() => c() || ++n >= max, seconds + 1); }

        public override IEnumerable<Step> Run()
        {
            int start0 = WorldTemperature.StartDayOfYear;
            var units0 = Units.System;
            try { foreach (var s in Body()) yield return s; }
            finally { WorldTemperature.StartDayOfYear = start0; Units.System = units0; }
        }

        IEnumerable<Step> Body()
        {
            ItemCatalog.RegisterAll();
            WorldTemperature.StartDayOfYear = WorldTemperature.ColdestDayOfYear;   // mid-January: cold outside
            Units.System = MeasurementSystem.Both;
            Rigs.Ground(World);
            var driver = new SimDriver(); World.AddChild(driver);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(2);
            var loop = new MpLoopback { Player = player, Driver = driver, ConsumeDeployables = true };
            World.AddChild(loop);
            yield return Wait(() => loop.Client.State == NetSessionState.Connected && player.NetFineVitalsAdopted, 15);
            ushort pid = loop.Client.PlayerId;
            // ⚠ THE PRECONDITION. If the vitals are not server-owned this test is the offline path again and proves nothing.
            T.Check("the server owns this player's vitals (the path that was broken)", player.NetFineVitalsAdopted);
            if (!player.NetFineVitalsAdopted) yield break;

            // ---- 1. THE BODY MOVES toward a cold ambient, on its own
            yield return Ticks(20);
            float ambient = player.DebugTemperatureProbe.ambientC;
            float body0 = player.Temperature.BodyC;
            T.Check($"January is cold here (ambient {ambient:0.0} C)", ambient < PlayerTemperatureSim.ComfortLowC);
            yield return Ticks(250);   // 5 s; the body's time constant is 45 s
            float body1 = player.Temperature.BodyC;
            T.Check($"the body cools toward it ({body0:0.00} -> {body1:0.00} C in 5 s) -- it used to sit at 19 forever", body1 < body0 - 1f);

            // ---- 2. THE BAR DRAWS IT
            var hud = new HUD { Player = player };
            World.AddChild(hud);
            yield return Ticks(3);
            player.TemperatureHoldC = -20f;   // freezing, held so the rest is not a two-minute wait
            yield return Ticks(3);
            for (int i = 0; i < 40; i++) hud.HubTick(0.05);   // the tween eases in; drive it to rest
            T.Check($"at -20 C the bar is full on the cold side ({hud.DebugTemperatureShown:0.00})", hud.DebugTemperatureShown < -0.99f);

            // ---- 3. THE SERVER FEELS IT: freezing costs health on the authority now. Control first: comfortable costs nothing.
            loop.Server.CombatState.TryGet(pid, out var ce);
            player.TemperatureHoldC = 20f;
            yield return Ticks(10);
            float hpA = ce.HealthExact;
            yield return Ticks(150);
            float hpB = ce.HealthExact;
            T.Check($"CONTROL: comfortable, the server takes nothing ({hpA:0.00} -> {hpB:0.00})", hpB >= hpA - 0.01f);
            player.TemperatureHoldC = -20f;
            yield return Ticks(10);
            T.Check($"the server is told the band ({loop.Server.Vitals.TemperatureBandOf?.Invoke(pid)})",
                loop.Server.Vitals.TemperatureBandOf?.Invoke(pid) == PlayerTemperatureSim.Band.Freezing);
            float hpC = ce.HealthExact;
            yield return Ticks(150);   // 3 s at ExposureHealthPerSecond
            float hpD = ce.HealthExact;
            T.Check($"freezing, the SERVER takes health ({hpC:0.00} -> {hpD:0.00}, want ~{3f * PlayerVitalsSim.ExposureHealthPerSecond:0.0} lost)",
                hpD < hpC - 1.5f);
            player.TemperatureHoldC = 20f;

            // ---- 4. THE THERMOMETER: nothing on the bar without one, the number with one
            player.TemperatureHoldC = -20f;
            yield return Ticks(3);
            hud.HubTick(0.05);
            T.Check($"no thermometer, no number ({hud.DebugTemperatureText ?? "none"})", hud.DebugTemperatureText == null && !player.HasThermometer);
            var asset = Assets.find(PlayerController.ThermometerId);
            T.Check($"the Thermometer is an item ({asset?.itemName}, {asset?.size_x}x{asset?.size_y})", asset != null && asset.itemName == "Thermometer");
            // ...with its OWN model and icon, not the fallback: without a manifest row a dropped item is a 0.24 m cube
            var mesh = WorldItem.MeshForStack(PlayerController.ThermometerId, 1);
            var msz = mesh?.GetAabb().Size ?? Vector3.Zero;
            T.Check($"its model loads -- a thin 13.5 cm tube, not the placeholder cube ({msz})", mesh != null && msz.Z > 0.13f && msz.Z < 0.14f && msz.X < 0.02f);
            var icon = InventoryUI.IconFor(PlayerController.ThermometerId);
            T.Check($"...and its icon ({icon?.GetWidth()}x{icon?.GetHeight()})", icon != null && icon.GetWidth() == 256);
            loop.Server.Inventories.TryGet(pid, out var sinv);
            sinv.Inventory.items[2].clear();
            sinv.Inventory.items[2].addItem(0, 0, 0, new Item(PlayerController.ThermometerId));
            sinv.Inventory.items[2].raiseStateUpdated();
            yield return Wait(() => player.HasThermometer, 3);
            hud.HubTick(0.05);
            T.Check($"carrying one, the bar reads \"{hud.DebugTemperatureText}\"", hud.DebugTemperatureText == "-20 °C / -4 °F");
            Units.System = MeasurementSystem.Imperial;
            hud.HubTick(0.05);
            T.Check($"...in the player's units when they chose one ({hud.DebugTemperatureText})", hud.DebugTemperatureText == "-4 °F");
            Units.System = MeasurementSystem.Both;
            sinv.Inventory.items[2].clear();
            sinv.Inventory.items[2].raiseStateUpdated();
            yield return Wait(() => !player.HasThermometer, 3);
            hud.HubTick(0.05);
            T.Check($"dropped, the number goes ({hud.DebugTemperatureText ?? "none"})", hud.DebugTemperatureText == null);
            player.TemperatureHoldC = null;
        }
    }

    // The JOINED client: its band rides PlayerStateCommand.TempBand, so this is the wire half of the same fix.
    public sealed class TemperatureJoinedClientTests : GameTest
    {
        public override string Name => "temperature.joined_client_band";
        public override double TimeoutSimSeconds => 60;

        Step Wait(System.Func<bool> c, double seconds) { int n = 0, max = (int)(seconds * 50); return Until(() => c() || ++n >= max, seconds + 1); }

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt", syncLoad: true, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready", world.Ready);
            ItemCatalog.RegisterAll();
            var net = new MemNetwork(20261008);
            world.Sim.Sim.Add(new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump"));
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "cold" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);
            yield return Wait(() => sess.Shell != null && sess.Shell.NetFineVitalsAdopted, 8);
            T.Check("joined, vitals server-owned", sess.Shell != null && sess.Shell.NetFineVitalsAdopted);
            if (sess.Shell == null) yield break;
            ushort pid = sess.Client.PlayerId;
            var band = ded.Server.Vitals.TemperatureBandOf;

            sess.Shell.TemperatureHoldC = 20f;
            yield return Wait(() => band(pid) == PlayerTemperatureSim.Band.Comfortable, 3);
            T.Check($"CONTROL: comfortable on the client reads comfortable on the server ({band(pid)})", band(pid) == PlayerTemperatureSim.Band.Comfortable);
            sess.Shell.TemperatureHoldC = -20f;
            yield return Wait(() => band(pid) == PlayerTemperatureSim.Band.Freezing, 3);
            T.Check($"freezing on the client reaches the server over the wire ({band(pid)})", band(pid) == PlayerTemperatureSim.Band.Freezing);
            sess.Shell.TemperatureHoldC = 35f;
            yield return Wait(() => band(pid) == PlayerTemperatureSim.Band.Hot, 3);
            T.Check($"...and so does hot ({band(pid)})", band(pid) == PlayerTemperatureSim.Band.Hot);
            sess.Shell.TemperatureHoldC = null;
        }
    }
}
