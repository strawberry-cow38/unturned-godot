using Godot;
using System.Collections.Generic;
using System.Linq;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // INDUSTRIAL ITEM PIPES in the engine (v56, strawberry 2026-10-06). The L0 suite proves the rules on a bare
    // server; these prove the GAME reaches them -- the adapter ghost really snaps to a container node, the pipe
    // tool's real look-ray and click really send the connect, a real F tap really opens a panel whose widgets
    // really reach the server, and a whole chain moves items in the singleplayer loopback, which is the path the
    // game is played on.
    //
    // All of it runs on MpLoopback with ConsumeDeployables -- singleplayer IS that listen server, so these are the
    // singleplayer tests. Containers come from the same world-build manifest the map uses (Crate_0), so the node
    // the ghost snaps to is a real StoreShelf with the real trimesh collider, not a box made for the test.
    static class PipeL1
    {
        public const ushort Tool = 9215, Adapter = 9216, Splitter = 9217, Combiner = 9218, Mover = 9219, Generator = 458;

        public static MpLoopback Loopback(Node3D world, PlayerController player, SimDriver driver, params Vector3[] crates)
        {
            var manifest = new List<(string mesh, int table, bool display, string label, Vector3 pos, float yaw)>();
            foreach (var c in crates) manifest.Add(("Crate_0", 8, false, "Crate", c, 0f));
            var loop = new MpLoopback { Player = player, Driver = driver, Containers = manifest, ConsumeDeployables = true };
            world.AddChild(loop);
            return loop;
        }

        /// <summary>The server crate for the container registered at this position (map containers get NetIds per boot).</summary>
        public static InventoryReplication.CrateEntry CrateAt(MpLoopback loop, Vector3 pos)
        {
            foreach (var c in loop.Server.Inventories.Crates)
                if (new Vector3(c.Pos.x, c.Pos.y, c.Pos.z).DistanceTo(pos) < 0.01f) return c;
            return null;
        }

        /// <summary>Containers are stocked from a loot table at registration; the tests want them empty.</summary>
        public static void Empty(InventoryReplication.CrateEntry c)
        {
            while (c.Storage.getItemCount() > 0) c.Storage.removeItem(0);
        }

        public static uint FindEntity(MpLoopback loop, ushort defId, System.Func<DeployableReplication.DeployableEntity, bool> extra = null)
        {
            foreach (var e in loop.Server.Deployables.All) if (e.DefId == defId && (extra == null || extra(e))) return e.NetIdValue;
            return 0;
        }

        public static int Units(Items page)
        {
            int n = 0;
            for (byte i = 0; i < page.getItemCount(); i++) n += ItemTransfer.UnitsOf(page.getItem(i).item);
            return n;
        }

        public static UnityEngine.Vector3 U(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);
    }

    /// <summary>"the adapter should snap to the storage container. has to snap or it wont place -red."</summary>
    public class AdapterSnapsOrRefuses : GameTest
    {
        public override string Name => "pipes.adapter_snaps_or_refuses";
        public override double TimeoutSimSeconds => 40;

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            Rigs.Ground(World);
            var driver = new SimDriver();
            World.AddChild(driver);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(2);
            var cratePos = new Vector3(0f, 0f, -2.6f);
            var loop = PipeL1.Loopback(World, player, driver, cratePos);
            yield return Until(() => loop.Client.State == NetSessionState.Connected
                                     && loop.Server.Inventories.TryGet(loop.Client.PlayerId, out _), 15);
            var crate = PipeL1.CrateAt(loop, cratePos);
            T.Check("the map container registered server-side", crate != null);
            if (crate == null) yield break;
            StoreShelf shelf = null;
            yield return Until(() => loop.Storage.TryGetNode(crate.NetIdValue, out shelf), 10);
            T.Check("...and materialized as a real container node", shelf != null);
            if (shelf == null) yield break;

            loop.Server.Inventories.TryGet(loop.Client.PlayerId, out var sinv);
            T.Check("the adapter goes into the server bag", sinv.Inventory.tryAddItem(new Item(PipeL1.Adapter)));
            yield return Until(() => player.Inventory.getItemCount(PipeL1.Adapter) == 1, 5);
            T.Check("equipped from the bag", player.EquipItemAsset(Assets.find(PipeL1.Adapter), new Item(PipeL1.Adapter)));
            T.Check("held as the Storage Adapter", player.DebugHeldDeployable?.Id == PipeL1.Adapter);
            yield return Ticks(2);

            // (a) OPEN GROUND: red, with the reason said, and the click places nothing
            player.DebugLookAt(new Vector3(2.5f, 0f, 0.5f));
            yield return Ticks(1);
            bool groundOk = player.DebugPlacerAim();
            T.Check($"aimed at bare ground the ghost is RED (reason: '{player.DebugPlacer?.Reason}')", !groundOk);
            T.Check("...and says what it needs", player.DebugPlacer?.Reason == "Needs a storage container");
            player.DebugTryPlace();
            player.DebugDeployTick(5f);
            yield return Ticks(20);
            T.Check($"nothing was placed on the ground ({loop.Server.Deployables.Count} entities)", PipeL1.FindEntity(loop, PipeL1.Adapter) == 0);
            T.Check("and the adapter is still in the bag", sinv.Inventory.getItemCount(PipeL1.Adapter) == 1);

            // (b) THE CONTAINER: aim at its front face. The face plane is read off the container's OWN collision box
            // (the same bounds the snap uses), not typed in, so a mesh 4 mm off my measurement cannot pass or fail this.
            T.Check("the container node has collision bounds", BarricadePlacer.ContainerBounds(shelf, out var cbox));
            float faceZ = shelf.GlobalTransform.Origin.Z + cbox.End.Z;   // the crate is unrotated: local +Z is world +Z
            var faceAim = new Vector3(cratePos.X + 0.2f, 0.7f, faceZ);
            player.DebugLookAt(faceAim);
            yield return Ticks(1);
            bool crateOk = player.DebugPlacerAim();
            var placer = player.DebugPlacer;
            T.Check($"aimed at the container the ghost is VALID (reason '{placer?.Reason}')", crateOk);
            T.Check($"...snapped to THAT container (id {placer?.SnappedCrateId} vs {crate.NetIdValue})", placer?.SnappedCrateId == crate.NetIdValue);
            T.Check($"...ON its face (point z {placer?.Point.Z:0.0000} vs face {faceZ:0.0000})", placer != null && Mathf.Abs(placer.Point.Z - faceZ) < 0.005f);
            T.Check($"...facing out of it (normal {placer?.Normal})", placer != null && placer.Normal.Dot(Vector3.Back) > 0.99f);

            player.DebugTryPlace();
            player.DebugDeployTick(5f);
            uint adapter = 0;
            yield return Until(() => (adapter = PipeL1.FindEntity(loop, PipeL1.Adapter)) != 0, 5);
            T.Check("the server placed the adapter", adapter != 0);
            if (adapter == 0) yield break;
            loop.Server.Deployables.TryGet(adapter, out var ae);
            T.Check($"...bound to the container the ghost named ({ae.ItemCrateId} vs {crate.NetIdValue})", ae.ItemCrateId == crate.NetIdValue);
            T.Check("...and spent the item", sinv.Inventory.getItemCount(PipeL1.Adapter) == 0);
            Deployable node = null;
            yield return Until(() => loop.Deploys.TryGetNode(adapter, out node), 5);
            T.Check("the replica node materialized", node != null);
            if (node == null) yield break;
            // back face = origin - facing * half-depth; it should sit on the container face (the 1 cm hair aside)
            var facing = node.GlobalTransform.Basis * new Vector3(0f, 1f, 0f);   // flat +Y = the front after the stand-up
            float backZ = node.GlobalPosition.Z - facing.Normalized().Z * DeployableDef.StorageAdapter.Size.Y * 0.5f;
            // flush = the back face 1 cm proud of the container (the z-fight hair in BarricadePlacer.Standoff), +-5 mm
            T.Check($"the placed adapter's BACK sits flush on the face (back z {backZ:0.0000}, face {faceZ:0.0000}, gap {backZ - faceZ:0.0000})",
                    Mathf.Abs(backZ - faceZ - 0.01f) < 0.005f);
            T.Check($"...and its sockets face away from the box (front {facing.Normalized()})", facing.Normalized().Dot(Vector3.Back) > 0.99f);
        }
    }

    /// <summary>The whole chain in the singleplayer loopback: crate -> adapter -> pipe -> powered mover -> pipe ->
    /// splitter -> two adapters -> two crates. Items arrive at the configured rate and split per mode.</summary>
    public class PipeChainEndToEnd : GameTest
    {
        public override string Name => "pipes.end_to_end_loopback";
        public override double TimeoutSimSeconds => 80;

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            Rigs.Ground(World);
            var driver = new SimDriver();
            World.AddChild(driver);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(2);
            Vector3 sPos = new(-3f, 0f, -5f), aPos = new(2f, 0f, -5f), bPos = new(5f, 0f, -5f);
            var loop = PipeL1.Loopback(World, player, driver, sPos, aPos, bPos);
            yield return Until(() => loop.Client.State == NetSessionState.Connected
                                     && loop.Server.Inventories.TryGet(loop.Client.PlayerId, out _), 15);
            var c = loop.Client;
            var s = PipeL1.CrateAt(loop, sPos); var a = PipeL1.CrateAt(loop, aPos); var b = PipeL1.CrateAt(loop, bPos);
            T.Check("three containers", s != null && a != null && b != null);
            if (s == null || a == null || b == null) yield break;
            PipeL1.Empty(s); PipeL1.Empty(a); PipeL1.Empty(b);

            loop.Server.Inventories.TryGet(c.PlayerId, out var sinv);
            // ONE AT A TIME: the spawn kit's pockets do not hold six 2x2 devices at once, and a grant that did not fit
            // fails the place's "has the item" validation silently -- which reads as the place being broken.
            float face = 0.62f;
            var plan = new (ushort id, Vector3 at, uint target)[]
            {
                (PipeL1.Adapter, sPos + new Vector3(0f, 0.6f, face), s.NetIdValue),   // THROUGH THE COMMAND the ghost sends, container named
                (PipeL1.Adapter, aPos + new Vector3(0f, 0.6f, face), a.NetIdValue),
                (PipeL1.Adapter, bPos + new Vector3(0f, 0.6f, face), b.NetIdValue),
                (PipeL1.Mover, new Vector3(-1f, 0f, -3f), 0u),
                (PipeL1.Splitter, new Vector3(3.5f, 0f, -3f), 0u),
                (PipeL1.Generator, new Vector3(-1f, 0f, -1f), 0u),   // unaddressed = a full tank
            };
            for (int i = 0; i < plan.Length; i++)
            {
                bool granted = sinv.Inventory.tryAddItem(new Item(plan[i].id));
                c.SendPlaceDeployable(plan[i].id, PipeL1.U(plan[i].at), 0f, 255, 0, 0, plan[i].target);
                int want = i + 1;
                GD.Print($"[pipes-e2e] placing {plan[i].id} at {plan[i].at} (granted {granted}, bag holds {sinv.Inventory.getItemCount(plan[i].id)})");
                yield return Until(() => loop.Server.Deployables.Count >= want, 5);
                T.Check($"placed {plan[i].id} #{want} (granted {granted}, server has {loop.Server.Deployables.Count})", loop.Server.Deployables.Count == want);
            }
            uint aS = PipeL1.FindEntity(loop, PipeL1.Adapter, e => e.ItemCrateId == s.NetIdValue);
            uint aA = PipeL1.FindEntity(loop, PipeL1.Adapter, e => e.ItemCrateId == a.NetIdValue);
            uint aB = PipeL1.FindEntity(loop, PipeL1.Adapter, e => e.ItemCrateId == b.NetIdValue);
            uint mover = PipeL1.FindEntity(loop, PipeL1.Mover), split = PipeL1.FindEntity(loop, PipeL1.Splitter), gen = PipeL1.FindEntity(loop, PipeL1.Generator);
            T.Check("each adapter bound its own container", aS != 0 && aA != 0 && aB != 0);

            c.SendConnectPipe(aS, 1, mover, 0, new[] { PipeL1.U(new Vector3(-2f, 0.5f, -3.5f)) });   // with a bend
            c.SendConnectPipe(mover, 1, split, 0, null);
            c.SendConnectPipe(split, 1, aA, 0, null);
            c.SendConnectPipe(split, 2, aB, 0, null);   // output 3 is left unpiped on purpose: it must be ignored
            c.SendConnectWire(gen, 0, mover, 0);
            c.SendToggleDeployable(gen, true);
            c.SendConfigureItemDevice(mover, ItemDeviceConfig.From(0, 1, 1, 1, 8));
            GD.Print($"[pipes-e2e] ids aS={aS} aA={aA} aB={aB} mover={mover} split={split} gen={gen}");
            yield return Until(() => loop.Server.Deployables.Pipes.Count == 4 && loop.Server.Deployables.WireCount == 1, 10);
            T.Check($"four pipes + the power wire on the server ({loop.Server.Deployables.Pipes.Count}/{loop.Server.Deployables.WireCount})",
                    loop.Server.Deployables.Pipes.Count == 4 && loop.Server.Deployables.WireCount == 1);
            yield return Until(() => loop.Deploys.PipeCount == 4, 10);
            T.Check($"...and four pipe NODES on the client ({loop.Deploys.PipeCount})", loop.Deploys.PipeCount == 4);
            if (loop.Deploys.TryGetPipe(loop.Server.Deployables.Pipes.All.First(p => p.SrcId == aS).NetIdValue, out var bent))
                T.Check($"the bent pipe is drawn through its bend ({bent.Points.Count} points)", bent.Points.Count == 3);

            // the goods: 40 singles in the source. A 1x1 non-stacking item, so one unit = one jar.
            const ushort Scrap = 67;
            var scrap = Assets.find(Scrap);
            T.Check($"fixture: item {Scrap} is a 1x1 that does not stack", scrap != null && scrap.size_x == 1 && scrap.size_y == 1 && !ItemTransfer.IsStackable(new Item(Scrap)));
            for (int i = 0; i < 40; i++) s.Storage.tryAddItem(new Item(Scrap));
            T.Check("40 in the source", PipeL1.Units(s.Storage) == 40);

            // power settles, the mover starts; then measure TWO SECONDS of server ticks
            GD.Print($"[pipes-e2e] waiting for the first move (gen on: {(loop.Server.Deployables.TryGet(gen, out var g0) && g0.ToggledOn)}, fuel {g0?.Fuel})");
            yield return Until(() => loop.Server.ItemMovers.Diag.UnitsMoved > 0, 10);
            long t0 = loop.Server.Session.CurrentTick, m0 = loop.Server.ItemMovers.Diag.UnitsMoved;
            int a0 = PipeL1.Units(a.Storage), b0 = PipeL1.Units(b.Storage);
            yield return Until(() => loop.Server.Session.CurrentTick >= t0 + 100, 10);
            long dt = loop.Server.Session.CurrentTick - t0, moved = loop.Server.ItemMovers.Diag.UnitsMoved - m0;
            float expect = 8f * dt / 50f;
            GD.Print($"[pipes-e2e] 8/s: moved {moved} in {dt} ticks (expect {expect:0.0}); split A+{PipeL1.Units(a.Storage) - a0} B+{PipeL1.Units(b.Storage) - b0}");
            T.Check($"8/s: {moved} moved in {dt} ticks (expect {expect:0.0})", Mathf.Abs(moved - expect) <= 1.01f);
            int da = PipeL1.Units(a.Storage) - a0, db = PipeL1.Units(b.Storage) - b0;
            T.Check($"round-robin over the TWO connected outputs: {da} / {db}", Mathf.Abs(da - db) <= 1 && da + db == moved);
            T.Check("conservation: source + both destinations = 40", PipeL1.Units(s.Storage) + PipeL1.Units(a.Storage) + PipeL1.Units(b.Storage) == 40);

            // switch the splitter to WEIGHTED 3:1 through the same command the panel sends
            c.SendConfigureItemDevice(split, ItemDeviceConfig.From((byte)SplitterMode.Weighted, 3, 1, 0, 32));
            yield return Until(() => loop.Server.Deployables.TryGet(split, out var se) && se.ItemConfig.Mode == SplitterMode.Weighted, 5);
            int a1 = PipeL1.Units(a.Storage), b1 = PipeL1.Units(b.Storage);
            long t1 = loop.Server.Session.CurrentTick;
            yield return Until(() => PipeL1.Units(a.Storage) + PipeL1.Units(b.Storage) - a1 - b1 >= 16, 10);
            int wa = PipeL1.Units(a.Storage) - a1, wb = PipeL1.Units(b.Storage) - b1;
            GD.Print($"[pipes-e2e] weighted 3:1 -> A+{wa} B+{wb}");
            T.Check($"weighted 3:1 -> {wa} : {wb} over {wa + wb}", wa + wb >= 16 && Mathf.Abs(wa - 3 * wb) <= 3);

            // power OFF stops it
            c.SendToggleDeployable(gen, false);
            yield return Until(() => loop.Server.Deployables.TryGet(gen, out var ge) && !ge.ToggledOn, 5);
            yield return Ticks(10);   // past the solve refresh
            long m2 = loop.Server.ItemMovers.Diag.UnitsMoved;
            yield return Until(() => loop.Server.Session.CurrentTick >= t1 + 400, 15);
            T.Check($"generator off: nothing more moves ({loop.Server.ItemMovers.Diag.UnitsMoved - m2})", loop.Server.ItemMovers.Diag.UnitsMoved == m2);
        }
    }

    /// <summary>The pipe tool's REAL path: equip it, look at a socket through the real look-ray, click, lay a bend
    /// on the ground, click the far socket -- and the server has a pipe with that bend in it.</summary>
    public class PipeToolConnects : GameTest
    {
        public override string Name => "pipes.tool_connects";
        public override double TimeoutSimSeconds => 40;

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            Rigs.Ground(World);
            var driver = new SimDriver();
            World.AddChild(driver);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(2);
            var loop = PipeL1.Loopback(World, player, driver);
            yield return Until(() => loop.Client.State == NetSessionState.Connected
                                     && loop.Server.Inventories.TryGet(loop.Client.PlayerId, out _), 15);
            var c = loop.Client;
            loop.Server.Inventories.TryGet(c.PlayerId, out var sinv);
            sinv.Inventory.tryAddItem(new Item(PipeL1.Mover));
            sinv.Inventory.tryAddItem(new Item(PipeL1.Splitter));
            sinv.Inventory.tryAddItem(new Item(PipeL1.Tool));
            c.SendPlaceDeployable(PipeL1.Mover, PipeL1.U(new Vector3(-1.2f, 0f, -2.5f)), 0f);
            c.SendPlaceDeployable(PipeL1.Splitter, PipeL1.U(new Vector3(1.4f, 0f, -2.5f)), 0f);
            yield return Until(() => loop.Server.Deployables.Count == 2, 10);
            uint mover = PipeL1.FindEntity(loop, PipeL1.Mover), split = PipeL1.FindEntity(loop, PipeL1.Splitter);
            Deployable mNode = null, sNode = null;
            yield return Until(() => loop.Deploys.TryGetNode(mover, out mNode) && loop.Deploys.TryGetNode(split, out sNode), 10);
            T.Check("both devices materialized", mNode != null && sNode != null);
            if (mNode == null || sNode == null) yield break;
            yield return Until(() => player.Inventory.getItemCount(PipeL1.Tool) == 1, 5);
            T.Check("pipe tool equipped", player.EquipItemAsset(Assets.find(PipeL1.Tool), new Item(PipeL1.Tool)) && player.HoldingPipeTool);
            player.DriveFP = true;
            PlayerController.DebugForceLookScan = true;   // headless refuses to capture the mouse
            yield return Ticks(2);

            var outPort = mNode.ItemPorts[1];   // the mover's OUT socket
            var inPort = sNode.ItemPorts[0];    // the splitter's IN socket
            var powerPort = mNode.Ports[0];     // the mover's POWER input -- the pipe tool must not even see it

            player.DebugLookAt(powerPort.GlobalPosition);
            player.DebugPipeLook();
            T.Check("the pipe tool's ray does NOT see a power socket", player.PipeLookPort == null);
            // ...and the converse, at the layer the wire tool's ray uses: an item socket is invisible to it
            var space = player.GetWorld3D().DirectSpaceState;
            var eye = player.DebugEye.Origin;
            var wq = PhysicsRayQueryParameters3D.Create(eye, eye + (outPort.GlobalPosition - eye) * 1.2f, ConnectionPort.PortLayer);
            var whit = space.IntersectRay(wq);
            T.Check("the wire tool's layer does NOT see an item socket", whit.Count == 0 || whit["collider"].As<GodotObject>() is not ItemPortNode);

            // an IN socket is where a pipe ENDS: clicking one with no route does not start one
            player.DebugLookAt(inPort.GlobalPosition);
            player.DebugPipeLook();
            T.Check("looking at the splitter's IN socket finds it", player.PipeLookPort == inPort);
            player.DebugPipeClick();
            T.Check("LMB on an IN socket does NOT start a route (pipes run OUT -> IN)", !player.DebugPiping);

            player.DebugLookAt(outPort.GlobalPosition);
            player.DebugPipeLook();
            T.Check($"looking at the mover's OUT socket finds it ({player.PipeLookPort?.InfoLine(false) ?? "nothing"})", player.PipeLookPort == outPort);
            player.DebugPipeClick();
            T.Check("LMB on an OUT socket starts a route", player.DebugPiping);

            var bend = new Vector3(0.1f, 0f, -1.4f);   // a point on the ground between the two
            player.DebugLookAt(bend);
            player.DebugPipeLook();
            player.DebugPipeClick();
            T.Check($"LMB on the ground lays a bend ({player.DebugPipeNodeCount})", player.DebugPipeNodeCount == 1);

            player.DebugLookAt(inPort.GlobalPosition);
            player.DebugPipeLook();
            T.Check($"looking at the splitter's IN socket finds it ({player.PipeLookPort?.InfoLine(false) ?? "nothing"})", player.PipeLookPort == inPort);
            player.DebugPipeClick();
            T.Check("LMB on the IN socket finishes the route locally", !player.DebugPiping);
            yield return Until(() => loop.Server.Deployables.Pipes.Count == 1, 5);
            var pipe = loop.Server.Deployables.Pipes.All.FirstOrDefault();
            T.Check("the SERVER has the pipe", pipe != null);
            if (pipe == null) yield break;
            T.Check($"mover OUT -> splitter IN ({pipe.SrcId}:{pipe.SrcPort} -> {pipe.DstId}:{pipe.DstPort})",
                    pipe.SrcId == mover && pipe.SrcPort == 1 && pipe.DstId == split && pipe.DstPort == 0);
            T.Check($"with the bend in it ({pipe.Path.Length} node(s))", pipe.Path.Length == 1 && new Vector3(pipe.Path[0].x, pipe.Path[0].y, pipe.Path[0].z).DistanceTo(bend) < 0.25f);
            yield return Until(() => loop.Deploys.PipeCount == 1, 5);
            T.Check("and the client drew it", loop.Deploys.PipeCount == 1);

            // the same socket now refuses a second pipe, in the tool and on the server
            player.DebugLookAt(outPort.GlobalPosition);
            player.DebugPipeLook();
            player.DebugPipeClick();
            T.Check("a piped OUT socket does not start a second route", !player.DebugPiping);
            PlayerController.DebugForceLookScan = false;
        }
    }

    /// <summary>"F" on a splitter and a mover opens the panel, and the panel's widgets reach the server.</summary>
    public class ItemDeviceConfigReachesServer : GameTest
    {
        public override string Name => "pipes.f_config_reaches_server";
        public override double TimeoutSimSeconds => 40;

        static void TapF(PlayerController p)
        {
            // press and release in the SAME frame: UpdateDeployPickup cancels a hold whose key is not physically down,
            // and headless has no keyboard -- a tap is exactly press-then-release with no frame between
            p._UnhandledInput(new InputEventKey { PhysicalKeycode = Key.F, Keycode = Key.F, Pressed = true });
            p._UnhandledInput(new InputEventKey { PhysicalKeycode = Key.F, Keycode = Key.F, Pressed = false });
        }

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            Rigs.Ground(World);
            var driver = new SimDriver();
            World.AddChild(driver);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(2);
            var loop = PipeL1.Loopback(World, player, driver);
            yield return Until(() => loop.Client.State == NetSessionState.Connected
                                     && loop.Server.Inventories.TryGet(loop.Client.PlayerId, out _), 15);
            var c = loop.Client;
            loop.Server.Inventories.TryGet(c.PlayerId, out var sinv);
            sinv.Inventory.tryAddItem(new Item(PipeL1.Splitter));
            sinv.Inventory.tryAddItem(new Item(PipeL1.Mover));
            // both inside LookReach (2.6 m from the eye): the focus scan is what F acts on
            c.SendPlaceDeployable(PipeL1.Splitter, PipeL1.U(new Vector3(-0.5f, 0f, -1.4f)), 0f);
            c.SendPlaceDeployable(PipeL1.Mover, PipeL1.U(new Vector3(0.9f, 0f, -1.4f)), 0f);
            yield return Until(() => loop.Server.Deployables.Count == 2, 10);
            uint split = PipeL1.FindEntity(loop, PipeL1.Splitter), mover = PipeL1.FindEntity(loop, PipeL1.Mover);
            Deployable sNode = null, mNode = null;
            yield return Until(() => loop.Deploys.TryGetNode(split, out sNode) && loop.Deploys.TryGetNode(mover, out mNode), 10);
            T.Check("both materialized", sNode != null && mNode != null);
            if (sNode == null || mNode == null) yield break;
            player.DriveFP = true;   // first person: DebugLookAt aims the camera, and in 1P the scan traces from it
            PlayerController.DebugForceLookScan = true;

            // ---- the splitter: look, tap F, press WEIGHTED, set the steppers
            player.DebugLookAt(sNode.GlobalPosition);
            yield return Until(() => player.DebugFocusDeployable == sNode, 5);
            T.Check("the look scan focuses the splitter", player.DebugFocusDeployable == sNode);
            TapF(player);
            T.Check("a tap of F opened the panel", player.ItemPanelOpen && player.DebugItemPanel.NetId == split);
            var panel = player.DebugItemPanel;
            if (panel == null) { PlayerController.DebugForceLookScan = false; yield break; }
            T.Check("...as a SPLITTER panel", panel.Kind == ItemDeviceKind.Splitter && panel.DebugModeButton(2) != null);
            panel.DebugModeButton(2).EmitSignal(BaseButton.SignalName.Pressed);   // "Weighted"
            panel.DebugWeightBox(0).Value = 4;
            panel.DebugWeightBox(1).Value = 2;
            panel.DebugWeightBox(2).Value = 0;
            yield return Until(() => loop.Server.Deployables.TryGet(split, out var se) && se.ItemConfig.Mode == SplitterMode.Weighted
                                     && se.ItemConfig.Weights.SequenceEqual(new byte[] { 4, 2, 0 }), 5);
            loop.Server.Deployables.TryGet(split, out var sEnt);
            T.Check($"the SERVER has weighted 4/2/0 ({sEnt.ItemConfig.Mode} {sEnt.ItemConfig.Weights[0]}/{sEnt.ItemConfig.Weights[1]}/{sEnt.ItemConfig.Weights[2]})",
                    sEnt.ItemConfig.Mode == SplitterMode.Weighted && sEnt.ItemConfig.Weights.SequenceEqual(new byte[] { 4, 2, 0 }));
            yield return Until(() => c.Deployables.TryGet(split, out var ce) && ce.ItemConfig.Mode == SplitterMode.Weighted, 5);
            T.Check("...and it came back to the client replica", c.Deployables.TryGet(split, out var cEnt) && cEnt.ItemConfig.Weights[0] == 4);
            TapF(player);
            T.Check("F closes it", !player.ItemPanelOpen);

            // ---- the mover: look, tap F, set the rate box
            player.DebugLookAt(mNode.GlobalPosition);
            yield return Until(() => player.DebugFocusDeployable == mNode, 5);
            TapF(player);
            panel = player.DebugItemPanel;
            T.Check("F on the mover opens a MOVER panel", player.ItemPanelOpen && panel.Kind == ItemDeviceKind.Mover && panel.DebugRateBox != null);
            if (panel?.DebugRateBox == null) { PlayerController.DebugForceLookScan = false; yield break; }
            T.Check($"...opened on the server's current rate ({panel.DebugRateBox.Value})", (int)panel.DebugRateBox.Value == ItemDeviceConfig.DefaultRate);
            panel.DebugRateBox.Value = 5;
            yield return Until(() => loop.Server.Deployables.TryGet(mover, out var me) && me.ItemConfig.Rate == 5, 5);
            T.Check("the SERVER's mover runs at 5/s", loop.Server.Deployables.TryGet(mover, out var mEnt) && mEnt.ItemConfig.Rate == 5);
            panel.Close();
            PlayerController.DebugForceLookScan = false;
        }
    }
    /// <summary>Every socket's arrow points straight out of (OUT) or into (IN) the face the socket sits on. The
    /// splitter and combiner are wider than they are deep, and MakeArrow's raw-coordinate rule sent their outer
    /// sockets' arrows SIDEWAYS -- the first render of the chain showed the splitter's outputs pointing at each
    /// other. "The face it sits on" is judged here by CONTACT (the coordinate equals that half-extent), not by
    /// the ratio rule the fix uses, so the test is not the fix restated.</summary>
    public class PipeSocketArrows : GameTest
    {
        public override string Name => "pipes.socket_arrows_point_out_of_their_face";

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            yield return Ticks(1);
            var defs = new[] { DeployableDef.StorageAdapter, DeployableDef.ItemSplitter, DeployableDef.ItemCombiner, DeployableDef.ItemMover };
            int checkedPorts = 0;
            for (int k = 0; k < defs.Length; k++)
            {
                var def = defs[k];
                var d = Deployable.Spawn(World, def, new Vector3(k * 2f, 0f, 0f), 0f);
                Vector3 half = def.Size * 0.5f;
                foreach (var ip in d.ItemPorts)
                {
                    var pos = def.ItemPorts[ip.Index].Pos;
                    bool onX = Mathf.Abs(Mathf.Abs(pos.X) - half.X) < 1e-3f, onY = Mathf.Abs(Mathf.Abs(pos.Y) - half.Y) < 1e-3f;
                    if (onX == onY) { T.Fail($"{def.Name} socket {ip.Index} at {pos} is on exactly one side face"); continue; }
                    var face = onX ? new Vector3(Mathf.Sign(pos.X), 0f, 0f) : new Vector3(0f, Mathf.Sign(pos.Y), 0f);
                    var want = ip.Dir == ItemPortDir.Out ? face : -face;
                    var flow = ip.DebugArrowFlow;
                    T.Check($"{def.Name} {ip.Dir} socket {ip.Index} at {pos}: arrow {flow} along {want}", flow.Dot(want) > 0.99f);
                    checkedPorts++;
                }
            }
            T.Check($"every socket of the four devices was looked at ({checkedPorts}/12)", checkedPorts == 12);
        }
    }
}
