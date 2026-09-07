using Godot;
using System.Collections.Generic;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    /// <summary>strawberry 2026-09-07: "stuff isnt getting cooked. anywhere. ever."
    ///
    /// ON THE REAL SINGLEPLAYER PATH, and the first version of this was not, which is the whole reason the
    /// rig is spelled out so carefully here. That one stood up a bare DedicatedServer against
    /// `__no_such_map__`, so the world had no fixtures in it at all, so there was no breaker box, so the
    /// mains were down and the oven sat cold -- correct-by-design behaviour that I very nearly reported as the
    /// bug. A repro in the wrong world reproduces the world, not the bug.
    ///
    /// The game path is MpLoopback with ConsumeDeployables (P6a: true by default), and it SEEDS every map
    /// GridSource fixture ToggledOn -- "grid mains ON by DEFAULT", master 2026-07-20. So the fixture list is
    /// part of the fixture: without a grid source this test measures a world nobody plays in.
    ///
    /// The L0 ServerCookingTests cannot see any of this: their rig never assigns HasPower, and ServerCooking
    /// reads null as powered. The one input that can stop every appliance in the world is the one the engine-free
    /// rig substitutes away, which is why this has to exist at L1 at all.</summary>
    public class CookerPowerRepro : GameTest
    {
        public override string Name => "cook.repro_mains";
        public override double TimeoutSimSeconds => 90;

        /// <summary>The materialized StoreShelf for a replicated container, which is what a player actually
        /// opens -- OpenCrate takes the node, not the NetId.</summary>
        static StorageCrate FindShelf(MpLoopback loop, uint netId)
        {
            if (loop.Storage != null && loop.Storage.TryGetNode(netId, out var shelf)) return shelf;
            return null;
        }

        public override IEnumerable<Step> Run()
        {
            var driver = new SimDriver();
            World.AddChild(driver);
            var dayNight = new DayNightCycle { VisualsEnabled = false };
            World.AddChild(dayNight);
            var resources = new ResourceField { VisualInstances = false };
            World.AddChild(resources);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(2);
            ItemCatalog.RegisterAll();

            // An OVEN (mains) and a BARBECUE (fuelled), as world-build containers -- the same manifest shape
            // WorldBuilder hands the loopback, so these register through ContainerNetSync exactly as the map's
            // own appliances do, cooker kind and all.
            var manifest = new List<(string mesh, int table, bool display, string label, Vector3 pos, float yaw)>
            {
                ("Oven_0", 17, false, "Stove", new Vector3(2f, 0f, 0f), 0f),
                ("Barbecue_0", 6, false, "BBQ", new Vector3(4f, 0f, 0f), 0f),
            };
            // THE BREAKER BOX. PEI's world build records these from the map (WorldBuilder: Circuit_0 ->
            // FixtureRecord with DeployableDef.GridSource), and the loopback places them ServerPlaced and
            // switched ON. Included here for the same reason the manifest is: leaving it out is what made the
            // first attempt measure a blackout.
            var fixtures = new List<FixtureRecord>
            {
                new FixtureRecord { DefId = DeployableDef.GridSource.Id, Pos = new Vector3(6f, 0f, 0f), YawDegrees = 0f, Basis = Basis.Identity },
            };

            var loop = new MpLoopback { Player = player, Driver = driver, DayNight = dayNight, Resources = resources,
                                        Containers = manifest, Fixtures = fixtures, ConsumeDeployables = true };
            World.AddChild(loop);
            yield return Until(() => loop.Client.State == NetSessionState.Connected, 20);
            T.Check("the loopback session connected", loop.Client.State == NetSessionState.Connected);
            yield return Until(() => loop.ContainerSync != null && loop.Server.Cooking.Count >= 2, 20);
            T.Check($"both appliances registered as cookers ({loop.Server.Cooking.Count})", loop.Server.Cooking.Count >= 2);

            // THE MAINS, read off the server's own answer rather than assumed -- this is the number the first
            // attempt got wrong, so it is printed rather than inferred.
            bool mains = false; int gridSources = 0;
            foreach (var e in loop.Server.Deployables.All)
                if (loop.Server.Deployables.Schema.TryGet(e.DefId, out var d) && d.FixtureKind == FixtureKind.GridSource)
                { gridSources++; if (e.ToggledOn) mains = true; }
            GD.Print($"[cookrepro] server mains up? {mains}  (grid sources placed: {gridSources})");
            T.Check($"the map's breaker box has the mains up, as the game path seeds it ({mains})", mains);

            // Find the two cookers' crates and put food in them, server-side.
            uint ovenId = 0, bbqId = 0;
            foreach (var kv in loop.Server.Cooking.AllForTest())
            {
                if (kv.Value == ECookerKind.Oven) ovenId = kv.Key;
                else if (kv.Value == ECookerKind.Barbecue) bbqId = kv.Key;
            }
            T.Check($"located the oven ({ovenId}) and the bbq ({bbqId})", ovenId != 0 && bbqId != 0);
            if (ovenId == 0 || bbqId == 0) yield break;

            loop.Server.Inventories.TryGetCrate(ovenId, out var oven);
            loop.Server.Inventories.TryGetCrate(bbqId, out var bbq);
            oven.Storage.tryAddItem(new Item(13));                       // Canned Beans
            bbq.Storage.tryAddItem(new Item(13));
            bbq.Storage.tryAddItem(new Item(Cooking.CharcoalId));

            // SWITCHED ON THROUGH THE PLAYER'S BUTTON, not by calling the server -- and that distinction IS the
            // test. Reaching straight for ServerCooking.SetOn asserts that the server can cook, which it always
            // could; it cannot see a client that never asks. The reported bug lived entirely in the gap between
            // the two, so the only version of this that fails on the broken build is the one that presses the
            // button the player presses.
            byte ovenStart = oven.Storage.getItem(0).item.cooked, bbqStart = bbq.Storage.getItem(0).item.cooked;
            var ovenShelf = FindShelf(loop, ovenId);
            GD.Print($"[cookrepro] oven shelf node found: {ovenShelf != null}");
            if (ovenShelf != null) player.GlobalPosition = ovenShelf.GlobalPosition + new Vector3(0.8f, 1f, 0f);   // inside StorageReach; the server reach-checks the open
            yield return Ticks(2);
            bool opened = ovenShelf != null && player.OpenCrate(ovenShelf);
            GD.Print($"[cookrepro] OpenCrate returned {opened}");
            yield return Until(() => player.OpenCookerKind != null, 10);
            T.Check($"opening the oven offers its on/off button ({player.OpenCookerKind})", player.OpenCookerKind != null);
            if (player.OpenCookerKind == null) yield break;
            player.ToggleOpenCooker();
            yield return Ticks(20);
            loop.Server.Cooking.TryGet(ovenId, out var ovenAfterPress);
            T.Check($"pressing it reaches the SERVER, not just the local flag (server On = {ovenAfterPress?.On}, button = {player.OpenCookerOn})",
                    ovenAfterPress != null && ovenAfterPress.On);
            player.DebugCloseCrate();
            yield return Ticks(10);

            var bbqShelf = FindShelf(loop, bbqId);
            if (bbqShelf != null) player.GlobalPosition = bbqShelf.GlobalPosition + new Vector3(0.8f, 1f, 0f);
            yield return Ticks(2);
            if (bbqShelf != null) player.OpenCrate(bbqShelf);
            yield return Until(() => player.OpenCookerKind != null, 10);
            player.ToggleOpenCooker();
            yield return Ticks(20);
            player.DebugCloseCrate();
            yield return Ticks(10);

            // ...AND IT STAYS ON WITH THE PANEL SHUT. Her third symptom, and it is the same bug seen from the
            // other side: with the switch never reaching the server, the close cleared the local flag and the
            // reopen showed the server's real answer, which had always been OFF.
            loop.Server.Cooking.TryGet(ovenId, out var ovenClosed);
            T.Check($"the oven is still on after the panel is shut ({ovenClosed?.On})", ovenClosed != null && ovenClosed.On);

            for (int i = 0; i < 750; i++) yield return Ticks(1);   // 15 s of the real host stepping itself

            var ovenItem = oven.Storage.getItem(0).item;
            var bbqItem = bbq.Storage.getItem(0).item;
            loop.Server.Cooking.TryGet(ovenId, out var ovenC);
            loop.Server.Cooking.TryGet(bbqId, out var bbqC);
            GD.Print($"[cookrepro] oven: cooked {ovenStart}->{ovenItem.cooked}, still on = {ovenC?.On}");
            GD.Print($"[cookrepro] bbq : cooked {bbqStart}->{bbqItem.cooked}, still on = {bbqC?.On}, fuel {bbqC?.Fuel:0.0}");

            T.Check($"a MAINS appliance cooks its food ({ovenStart} -> {ovenItem.cooked})", ovenItem.cooked > ovenStart);
            T.Check($"...and stays switched on ({ovenC?.On})", ovenC != null && ovenC.On);
            T.Check($"a FUELLED appliance cooks its food ({bbqStart} -> {bbqItem.cooked})", bbqItem.cooked > bbqStart);
            T.Check($"...and stays switched on ({bbqC?.On})", bbqC != null && bbqC.On);
        }
    }
}
