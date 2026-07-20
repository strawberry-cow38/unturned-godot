using Godot;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // SP-as-loopback-listen-server, behind --mploopback (MP_PLAN §4 Phase 4: "SP-loopback lands BEHIND A
    // FLAG: SP still defaults to the direct path until parity is proven"). The single-player world gains
    // an in-process NetWorldServer + NetWorldClient over MemTransport; the local PlayerController keeps
    // playing exactly as in direct SP -- its node IS the listen-server's authoritative avatar
    // (PlayerReplication.ServerDrive writes the shell's sim-core + real-collision result into the
    // replication entity, per §2.1 "prediction becomes a pass-through"), while the full wire path runs
    // underneath: MoveInput commands -> server -> snapshots -> replica + lastProcessedInputSeq acks.
    // Remote players joining this session (listen-server proper) render through RemotePlayers puppets.
    // Steps ride the world's SimRoot in §2.5 order, replication LAST.
    public partial class MpLoopback : Node
    {
        public PlayerController Player;   // the SP shell (WorldBuildResult.Player)
        public SimDriver Driver;          // the world's sim spine
        public DayNightCycle DayNight;    // Phase 8 (§3.7): the world clock this session publishes
        public ResourceField Resources;   // Phase 8 (§3.7): the resource alive-bitmap index space

        // SP/MP-unify P1 (pattern-setter, --spconsume): when set, the LOCAL player stops OWNING deployables
        // via the direct SP path and instead CONSUMES them as server replicas -- exactly how the MP client
        // does it (ClientWorldSession). Opt-in and behavior-neutral when false: SP/loopback keep the direct
        // path byte-for-byte. This is the first subsystem to prove the "consume a replica, don't own a node"
        // seam inside the loopback; later phases copy this shape for the other subsystems.
        //
        // P1b (SAME --spconsume flag): P1 surfaced that a wire placement SPENDS an item server-side before
        // broadcasting, but the loopback local player's SERVER inventory was never stocked/owner-replicated,
        // so a real placement would be server-REJECTED. P1b closes that: the server owns a stocked inventory
        // for the local player (seeded from the SP demo kit on join) and the local grid/consume/craft seams
        // route over the wire + adopt the owner block -- the MP client's exact end-to-end inventory authority.
        //
        // P2 (SAME --spconsume flag): the local player CONSUMES world-item (dropped/loot) replicas + picks
        // them up over the wire, mirroring the MP client (ClientWorldSession). Builds on P1b: a wire pickup
        // adds the item to the now-authoritative SERVER inventory and the owner echo re-adopts it locally.
        // The one flag drives ALL of these consume subsystems (deployables + inventory + world-items); the
        // name is kept for continuity. Opt-in and behavior-neutral when false: SP/loopback keep the direct path.
        public bool ConsumeDeployables;   // "--spconsume": the local player consumes the replica subsystems instead of owning direct nodes
        public System.Collections.Generic.List<FixtureRecord> Fixtures;   // A3: world power fixtures (Circuit_0 grid sources) recorded by WorldBuilder -- ServerPlaced under consume, direct-Attached otherwise
        bool _localInventoryAdopted;   // P1b: latches the one-time initial owner-grid pull (ClientWorldSession.SpawnShell:456-457)

        public MemNetwork Net { get; private set; }
        public NetWorldServer Server { get; private set; }
        public NetWorldClient Client { get; private set; }
        public RemotePlayers Remotes { get; private set; }
        public DeployableReplicaView Deploys { get; private set; }   // P1 --spconsume: server deployable/wire entities -> local nodes (null unless ConsumeDeployables)
        public GasStationServer GasStation { get; private set; }     // A2 --spconsume: authoritative per-station fuel tanks (the ExtractFuel choke drains them)
        public WorldItemReplicaView Items { get; private set; }      // P2 --spconsume: server world-item (drop/loot) entities -> local puppets (null unless ConsumeDeployables)
        public ZombieNetSync ZombieSync { get; private set; }
        public WorldItemNetSync WorldItemSync { get; private set; }
        public VehicleNetSync VehicleSync { get; private set; }
        public WorldClockNetSync ClockSync { get; private set; }
        public CropNetSync CropSync { get; private set; }
        public ResourceNetSync ResourceSync { get; private set; }

        public override void _Ready()
        {
            Net = new MemNetwork(seed: 1);   // in-process wire; seed irrelevant without fault injection
            Server = new NetWorldServer(new MemServerTransport(Net), contentHash: NetContent.Hash);
            Client = new NetWorldClient(new MemClientTransport(Net), "local", contentHash: NetContent.Hash);
            Client.Connect();
            Server.Combat.WorldRay = GodotWorldRay;   // Phase 5: remote joiners' server bullets stop at real world geometry
            // Phase 6 def tables (see DedicatedServer): remote joiners' place/wire/craft commands validate
            // against these; the LOCAL player keeps the direct SP paths (the listen-server IS the authority).
            DeployableNetSchema.RegisterAll(Server.Deployables.Schema);
            DeployableNetSchema.RegisterAll(Client.Deployables.Schema);
            Server.Transactions.Blueprints = BlueprintRegistry.All;

            Remotes = new RemotePlayers { Client = Client };
            AddChild(Remotes);

            // SP/MP-unify P1 (--spconsume): route the LOCAL player's deployable/power actions through the
            // loopback server and consume the results as replicas, instead of the direct SP path. The schema
            // is already registered on both ends above (@44-45) and Blueprints are set (@46), so the server
            // validates + spends + broadcasts and the client mirrors the entity graph -- no new plumbing.
            if (ConsumeDeployables)
            {
                // (a) the SAME diff-materializer the MP client uses (ClientWorldSession:111-112): it walks
                //     Client.Deployables.All/.AllWires into real Deployable.Spawn nodes + Wires, stamps NetId,
                //     and lets the local PowerNet run on top -- lamps light from replicated INPUTS, as in SP.
                Deploys = new DeployableReplicaView { Client = Client };
                AddChild(Deploys);
                // A3 (SP/MP-unify): server-place the recorded grid-power fixtures into the loopback server's
                // deployable graph (mains default OFF), so they ride SystemDeployables and the Deploys view
                // materializes them as GridPowerSource nodes -- the SOLE local source of these nodes under
                // consume (WorldBuilder no longer inline-Attaches them; no double-spawn). And route the F1/
                // toggleGlobalPower mains toggle over the wire: DevConsole.RemoteClient makes the console send
                // the toggle as a ConsoleCommand (server flips every GridSource's ToggledOn + broadcasts),
                // never a local process-global PowerNet.GlobalPower flip.
                DevConsole.RemoteClient = Client;
                // A2 (SP/MP-unify): the authoritative fuel-station tanks. Built from the server-placed gas-pump
                // fixtures below (each registered pump seeds its replicated 100% full percent); the ExtractFuel
                // choke drains through it. Set on the loopback server's transactions so a local RMB-extract (routed
                // over the wire via NetExtractFuel) mutates the shared tank server-side.
                GasStation = new GasStationServer();
                if (Fixtures != null)
                    foreach (var f in Fixtures)
                    {
                        var fe = Server.Deployables.ServerPlace(Server.Ids.Mint(), f.DefId, 0,
                            new UnityEngine.Vector3(f.Pos.X, f.Pos.Y, f.Pos.Z), f.YawDegrees, Server.Session.CurrentTick);
                        if (fe != null && DeployableDef.ById(f.DefId)?.Fixture == FixtureKind.GasPump)
                            GasStation.RegisterPump(fe, f.StationId, Server.Deployables, Server.Session.CurrentTick);   // A2: map pump->station + seed the replicated full percent
                    }
                Server.Transactions.FuelStations = GasStation;   // A2: the extract choke reads the absolute tanks through this seam
                // (b) set the local player's deployable seams to route over the wire (verbatim from
                //     ClientWorldSession.SpawnShell:446-452). Each seam is null in default SP/loopback, so
                //     the direct mutation below it stays byte-identical; SETTING it makes PlayerController's
                //     "if (NetX != null) request-over-wire; else direct" take the wire branch.
                //     INVARIANT (no double-materialization): with these seams set, PlayerController's direct
                //     Deployable.Spawn else-branch (PlayerController.cs:1177) NEVER fires -- the
                //     DeployableReplicaView is the SOLE spawner of local deployable nodes. That is the whole
                //     point of the pattern: one owner of the node graph, and it's the replica view.
                Player.NetPlaceDeployable = (defId, pos, yaw) => Client.SendPlaceDeployable(defId, ToU(pos), yaw);
                Player.NetSalvageDeployable = netId => Client.SendSalvageDeployable(netId);
                Player.NetPickupDeployable = netId => Client.SendPickupDeployable(netId);   // B2: hold-F returns the live deployable to the bag over the wire
                Player.NetExtractFuel = pumpId => Client.SendExtractFuel(pumpId);   // A2: RMB a replica pump -> server drains the shared station tank into the held can
                Player.NetConnectWire = (srcId, srcPort, dstId, dstPort) => Client.SendConnectWire(srcId, srcPort, dstId, dstPort);
                Player.NetRemoveWire = wireId => Client.SendRemoveWire(wireId);
                Player.NetToggleDeployable = (netId, on) => Client.SendToggleDeployable(netId, on);
                Player.NetOpenStorage = netId => Client.SendOpenStorage(netId);
                Player.NetCloseStorage = () => Client.SendCloseStorage();
                // B7 (SP/MP-unify): route the local player's skill-upgrade through the loopback server -- the
                //     server's PlayerSkills.TryUpgrade is the cost/cap validator; the owner skills echo re-levels
                //     the shell via AdoptReplicatedSkills in TickLocal. Verbatim from ClientWorldSession.SpawnShell:468.
                //     Null in default SP/loopback, so SkillsUI's local TryUpgrade stays the SP path byte-identical;
                //     SETTING it makes RequestUpgradeSkill take the wire branch (PlayerController.cs:1909) instead.
                Player.NetUpgradeSkill = (spec, index) => Client.SendUpgradeSkill(spec, index);

                // (c) P1b -- server-authoritative inventory for the LOCAL player. The placement seam above
                //     (P1) routes over the wire, where OnPlaceDeployable SPENDS the deployable item before
                //     broadcasting; its validator requires getItemCount(defId) > 0 on the SERVER grid. So the
                //     local player's server inventory must be STOCKED and OWNER-REPLICATED, exactly as the MP
                //     client's is. Without this a real placement is server-rejected -- the gap P1 surfaced.
                //
                //     SEED: grant the SP demo kit into the SERVER grid when the local peer joins -- verbatim
                //     from DedicatedServer:67-71. Fires AFTER core's Inventories.ServerAdd (subscribed first,
                //     in the NetWorldServer ctor) and BEFORE the join snapshot composes in TickReplication, so
                //     the kit rides the join snapshot. PopulateDemoKit is the exact loadout PopulateDemoInventory
                //     grants the SP shell, so server and local shell start from the identical bag.
                Server.Session.PeerConnected += peer =>
                {
                    if (Server.Inventories.TryGet(peer.PlayerId, out var inv))
                        PlayerController.PopulateDemoKit(inv.Inventory);
                };
                // SEAMS (verbatim from ClientWorldSession.SpawnShell:441-445): the local player's grid/consume/
                //     craft actions route as INTENT over the wire. Each is null in default SP/loopback, so the
                //     direct mutation stays byte-identical; SETTING it makes PlayerController take the wire
                //     branch (RequestMoveItem/RequestEquipItem/RequestDropItem @1744-1763, TickConsume @1038)
                //     and SKIP its local mutation -- the server owns every spend, the owner echo re-adopts.
                //     INVARIANT (no double-mutation): the SP direct removeItemAmount/TryDrag paths are
                //     superseded while these seams are non-null (mirrors the P1 deployable invariant above).
                Player.NetMoveItem = (p0, x0, y0, p1, x1, y1, rot1) => Client.SendMoveItem(p0, x0, y0, p1, x1, y1, rot1);
                Player.NetEquipItem = (page, x, y, slot) => Client.SendEquipItem(page, x, y, slot);
                Player.NetDropItem = (page, x, y) => Client.SendDropItem(page, x, y);
                Player.NetConsume = (page, x, y) => Client.SendConsume(page, x, y);
                Player.NetCraft = index => Client.SendCraft(index);
                // ADOPT (mirror ClientWorldSession:190-194): every owner-block echo re-adopts the SERVER grid
                //     into the local Player's EXISTING Inventory instance (copy-in-place -- the InventoryUI /
                //     hotbar / reload-mag hunt all hold that reference; the UI's signature poll repaints). The
                //     local bag now mirrors server truth instead of the boot-time SP demo fiction. The initial
                //     pull (ClientWorldSession.SpawnShell:456-457) is done in TickLocal once Connected.
                Client.Inventories.ReplicaUpdated += owner =>
                {
                    if (owner != Client.PlayerId || Player == null || !IsInstanceValid(Player)) return;
                    if (Client.Inventories.TryGet(owner, out var inv)) Player.AdoptReplicatedInventory(inv.Inventory);
                };
                // P3a (SP/MP-unify): the loopback already runs PvP ON (only the dedicated server ever set it
                // false), so a remote joiner shooting the local player already produced server-side damage --
                // this is the first place server-authoritative HP + death bites. Render the server's death fact
                // on the local shell and revive off the respawn fact. The listen-server node IS the authority
                // here (TickLocal ServerDrives its position), so unlike the MP shell the respawn REPOSITIONS the
                // node itself (to its local Spawn) -- there is no ServerPlayerAuthority recov stream for it. The
                // per-tick HP adoption itself rides TickLocal (mirrors the owner-inventory adoption there).
                Client.PlayerDied += e => { if (e.Victim == Client.PlayerId && Player != null && IsInstanceValid(Player)) Player.NetDie(); };
                Client.PlayerRespawned += e => { if (e.PlayerId == Client.PlayerId && Player != null && IsInstanceValid(Player)) Player.NetRespawn(reposition: true); };
                // P3b (SP/MP-unify): the loopback host shell is the listen-server's OWN authority (not a follower
                // body, not a client-auth claim stream), so its LOCAL environmental damage (zombie melee/acid,
                // blast, fall, OOB) has nowhere to go under P3a's NetVitalsAdopted no-op. Route it to the server
                // sink -- the shell's TakeDamage forwards here instead of moving local HP. SINGLE source: the
                // server does NOT independently derive fall/OOB for the loopback owner (it has no
                // ServerPlayerAuthority claim stream -- MpLoopback ServerDrives its position directly), so no
                // double count. ExpectServerVitals latches the spawn-window guard so no local death fires before
                // the first AdoptReplicatedVitals. Client.PlayerId is read at hit time (connected by then).
                Player.ExpectServerVitals();
                Player.NetDamageSink = amount => Server.Combat.DamagePlayerExternal(Client.PlayerId, amount);

                // (d) P2 -- world-item (dropped/loot) consume, over the wire. Same shape as the deployable
                //     view in (a): the SAME diff-materializer the MP client uses (ClientWorldSession:144-145)
                //     walks Client.WorldItems.All into focusable item puppets (WorldItem.BuildItemPuppet),
                //     stamps NetId, and F-on-a-focused-puppet rides the NetPickupItem seam. The player's own
                //     DROPS already route over the wire (P1b NetDropItem @118): SendDropItem -> the server's
                //     OnDropItem SpawnWorldItem -- a server world-item ENTITY with NO local SP node -- which
                //     THIS view then materializes. So a drop leaves the bag, appears in the world as a puppet,
                //     and is pickup-able again, entirely over the wire (the whole point of the phase).
                // P2b (SAME --spconsume flag): flip WorldItem.SuppressLocalVisual so the host's OWN direct world-item
                // nodes (LootField loot, salvage scrap) stop rendering + focusing -- else passive loot shows TWICE
                // (its real SP node AND this view's puppet). Set here: AttachMpLoopback runs right after the world
                // build and BEFORE LootField's first _Process, so every loot node latches it at _Ready. Cleared in
                // _ExitTree (process-global flag). See the KNOWN-NOW-CLOSED note below + UnifyTests.passive_loot_single.
                WorldItem.SuppressLocalVisual = true;
                Items = new WorldItemReplicaView { Client = Client };
                AddChild(Items);
                // set the pickup seam to route over the wire (verbatim from ClientWorldSession.SpawnShell:436).
                // Null in default SP/loopback, so the direct SP path stays byte-identical; SETTING it makes F
                // on a focused item PUPPET send PickupItemCommand (PlayerController.RequestPickupFocusedPuppet
                // @1712 -> NetPickupItem @1721) instead of nothing. The server validates reach+facing, adds the
                // item to the P1b-authoritative SERVER grid, and broadcasts WorldItemRemoved; the owner echo
                // re-adopts the add locally and the diff-driven view retires the puppet.
                Player.NetPickupItem = netId => Client.SendPickupItem(netId);
                // B3 (SP/MP-unify): route the local player's crop HARVEST over the wire. The harvest command
                //     (CommandHarvestCrop 25) + OnHarvestCrop are already live under v10 -- OnHarvestCrop is the
                //     authoritative sink (removes the crop, spawns the REPLICATED yield world-item, rolls
                //     AGRICULTURE + awards XP server-side). Null in default SP/loopback, so the direct
                //     CropManager.Harvest stays byte-identical; SETTING it makes F-interact route
                //     RequestHarvestNearestCrop -> SendHarvestCrop (the direct CropManager.Harvest else-branch is
                //     superseded, PlayerController.cs:2543) so the yield is the server's visible+focusable world
                //     item (through the WorldItemReplicaView @178 above) and XP is server-adopted, instead of a
                //     hidden SuppressLocalVisual SP drop + locally-awarded-then-overwritten XP. The loopback's
                //     CropNetSync stamps NetId onto the host's real CropManager nodes so the scan finds them.
                Player.NetHarvestCrop = netId => Client.SendHarvestCrop(netId);
                // INVARIANT (no double, player-driven path): with NetDropItem + NetPickupItem set and this view
                // present, the local player's DROP and PICKUP paths are superseded by the wire -- a drop spawns
                // NO local SP WorldItem node (RequestDropItem short-circuits InventoryUI's WorldItem.Spawn), and
                // a pickup targets the PUPPET (_focusPuppet -> RequestPickupFocusedPuppet), never the SP-node
                // TryPickup (_focusItem). The view is the SOLE materializer of these entities' local visuals.
                // INVARIANT (no double, PASSIVE path -- P2b, now closed): passive LOOT is the mirror case. LootField
                // still streams real SP WorldItem nodes locally (WorldBuilder Loot phase) and WorldItemNetSync
                // (@199-200, unconditional) mints entities from them, which THIS view also materializes -- so a
                // node-backed loot/salvage item WOULD show TWICE (its real SP node AND the puppet). The
                // WorldItem.SuppressLocalVisual flag set above closes it WITHOUT touching LootField/salvage or the
                // sync: the host's own world-item nodes hide + drop off the look-hit layer, so the view's puppet is
                // the SOLE visible + focusable copy on the host, while the node stays a live physics body in the
                // "worlditems" group -- the sync keeps settling + publishing it so remote joiners still see it. This
                // is fix (b) (bounded visual/interaction suppression) vs (a) (make LootField/salvage entity-primary);
                // (a) was rejected as it would rewrite MP-shared node-streaming + lose the physics-settle transform
                // the server has no world-item body to reproduce. Proven by UnifyTests.unify.passive_loot_single.
                GD.Print("[MPLOOPBACK] --spconsume: local player CONSUMES deployables + world-items as replicas + SERVER-AUTHORITATIVE inventory (direct player drop/pickup/deploy paths disabled)");
            }
            else
            {
                // A3: a NON-consuming loopback keeps the DIRECT SP path -- Attach the recorded grid-power
                // fixtures as local nodes (NetId 0; F1 flips PowerNet.GlobalPower locally), byte-identical to
                // old SP. Parented to the world root (this loopback's parent), where the Circuit_0 mesh lives.
                WorldBuilder.SpawnFixturesDirect(GetParent() ?? this, Fixtures);
            }

            Driver.Sim.Add(new DelegateSimStep((t, dt) => TickLocal((float)dt), "mp.loopback.local"));
            // Phase 7 / B8 (SP/MP-unify): construct the vehicle sync here so its local-occupancy reconcile can
            // run as a PRE-SIM step. The host's direct SP enter/exit only becomes occupancy TRUTH (the entity's
            // DriverPlayerId, which remote Enter commands validate against) via that reconcile; running it BEFORE
            // net.server.sim guarantees the host's CURRENT Driving state is stamped before the sim dispatches+
            // validates a remote EnterVehicle that same tick. Pre-B8 the reconcile lived inside Tick() (below,
            // AFTER net.server.sim), so a same-tick remote Enter validated against a stale DriverPlayerId==0 and
            // double-seated the host (the 1-tick race). The publish/drive/hold half stays POST-sim (net.vehicles.sync).
            VehicleSync = new VehicleNetSync(Server, this) { LocalPlayer = Player, LocalPlayerId = () => Client.PlayerId };
            VehicleSync.RegisterCommands();   // B11: a joined client's tow tie/untie intents apply on the listen-server host's real nodes
            Driver.Sim.Add(new DelegateSimStep((t, dt) => VehicleSync.ReconcileLocalOccupancy(), "net.vehicles.occupancy"));
            Driver.Sim.Add(new DelegateSimStep((t, dt) => Server.TickSimulation(), "net.server.sim"));
            // Phase 5: the world's real zombie brains publish into ZombieReplication at 12.5 Hz (§3.5) --
            // every loopback session soaks the zombie wire; the local view renders the brains directly
            // (no ZombiePuppets here -- puppets are for worlds that don't own the brains).
            ZombieSync = new ZombieNetSync(Server, this);
            Driver.Sim.Add(new DelegateSimStep((t, dt) => ZombieSync.Tick(), "net.zombies.publish"));
            // Phase 6: the loopback world's dropped/loot items publish as entities too (§3.3) -- every SP
            // session soaks the world-item wire the same way it soaks the zombie wire.
            WorldItemSync = new WorldItemNetSync(Server, this);
            Driver.Sim.Add(new DelegateSimStep((t, dt) => WorldItemSync.Tick(), "net.worlditems.publish"));
            // Phase 7: the loopback world's vehicles publish as entities too (§3.6) -- every SP-loopback
            // session soaks the vehicle wire. The LOCAL player keeps the direct SP drive path (the node IS the
            // authority); this half publishes/drives/holds POST-sim. (VehicleSync is constructed above, and its
            // occupancy reconcile is registered PRE-sim as net.vehicles.occupancy -- B8.)
            Driver.Sim.Add(new DelegateSimStep((t, dt) => VehicleSync.Tick(), "net.vehicles.sync"));
            // Phase 8 world state (§3.7): the loopback world's clock/crops/resources publish too -- every
            // SP-loopback session soaks the world-state wire. The local DayNightCycle keeps SP's exact
            // frame clock (driveFromTick=false -- behavior-neutral); the sync only re-anchors on drift.
            ClockSync = new WorldClockNetSync(Server, DayNight, driveFromTick: false);
            Driver.Sim.Add(new DelegateSimStep((t, dt) => ClockSync.Tick(), "net.worldclock.sync"));
            CropSync = new CropNetSync(Server, this);
            CropNetSchema.RegisterAll(Client.Crops.Schema);   // the local replica derives growth stages too
            Driver.Sim.Add(new DelegateSimStep((t, dt) => CropSync.Tick(), "net.crops.sync"));
            ResourceSync = new ResourceNetSync(Server, Resources);
            Driver.Sim.Add(new DelegateSimStep((t, dt) => ResourceSync.Tick(), "net.resources.sync"));
            Driver.Sim.Add(new DelegateSimStep((t, dt) => Server.TickReplication(), "net.server.replicate"));   // LAST (§2.5)
            GD.Print($"[MPLOOPBACK] listen-server up over MemTransport (content {NetContent.Hash:X16})");
        }

        static UnityEngine.Vector3 ToU(Vector3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);   // Godot -> Unity vector for the Send* signatures (mirrors ClientWorldSession:76)

        bool GodotWorldRay(UnityEngine.Vector3 from, UnityEngine.Vector3 to, out UnityEngine.Vector3 point)
        {
            point = default;
            var world = GetViewport()?.World3D;
            if (world == null) return false;
            var q = PhysicsRayQueryParameters3D.Create(new Vector3(from.x, from.y, from.z), new Vector3(to.x, to.y, to.z), (1u << 0) | (1u << 6));
            var hit = world.DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return false;
            var p = (Vector3)hit["position"];
            point = new UnityEngine.Vector3(p.X, p.Y, p.Z);
            return true;
        }

        void TickLocal(float dt)
        {
            Net.Tick();
            Client.Tick();
            if (Client.State != NetSessionState.Connected || Player == null || !IsInstanceValid(Player)) return;

            // P1b: the one-time initial owner-grid pull (ClientWorldSession.SpawnShell:456-457). The
            // ReplicaUpdated subscription re-adopts every echo AFTER this; this catches the join snapshot's
            // owner block if it landed before Connected latched Client.PlayerId. Idempotent + guarded, so it
            // runs at most once (adopting an identical demo bag over itself would be a harmless no-op anyway).
            if (ConsumeDeployables && !_localInventoryAdopted
                && Client.Inventories.TryGet(Client.PlayerId, out var invEntry))
            {
                Player.AdoptReplicatedInventory(invEntry.Inventory);
                _localInventoryAdopted = true;
            }

            // B7 (SP/MP-unify): server-authoritative skills for the local player -- mirror the replicated owner
            // skills block into the shell each tick (the owner-inventory adoption analogue; SkillsReplication has
            // no per-echo event). Gated on --spconsume, so default SP/loopback keeps its LOCAL skills byte-identical.
            // Verbatim from ClientWorldSession:260. The server owns the XP + level; RequestUpgradeSkill routes the
            // spend over the wire (the NetUpgradeSkill seam above) and this adoption re-levels the shell.
            if (ConsumeDeployables && Client.Skills.TryGet(Client.PlayerId, out var sk))
                Player.AdoptReplicatedSkills(sk.Skills);

            // P3a (SP/MP-unify): server-authoritative HP for the local player -- mirror the replicated
            // CombatEntity coarse health (0..100) into the shell each tick (the owner-inventory adoption
            // analogue; PlayerCombatReplication has no per-echo event). Gated on --spconsume, so default
            // SP/loopback keeps its LOCAL vitals byte-identical.
            if (ConsumeDeployables && Client.CombatState.TryGet(Client.PlayerId, out var vit))
                Player.AdoptReplicatedVitals(vit.Health);

            // B5 (SP/MP-unify): the loopback host's fine vitals are server-owned too. Mirror PlayerController's
            // SurvivalDrain (F1 `survival on|off`, static) into the server authority, then adopt the owner
            // SystemVitals(13) block into the shell each tick (after HP adoption). Gated on --spconsume, so
            // default SP/loopback keeps its LOCAL vitals byte-identical.
            if (ConsumeDeployables)
            {
                Server.Vitals.SurvivalDrain = PlayerController.SurvivalDrain;
                if (Client.Vitals.TryGet(Client.PlayerId, out var fv))
                    Player.AdoptReplicatedFineVitals(fv.Sim.Food, fv.Sim.Water, fv.Sim.Stamina, fv.Sim.Infection);
            }

            // 1) the shell's captured input goes over the wire as this tick's MoveInput (held-keys model). B5:
            //    PACK the shell's stance (was buttons=0) so the server derives `sprinting` from the adopted
            //    stance for the stamina drain -- stamina server-owned while the sprint decision stays client-auth.
            float yaw = Player.RotationDegrees.Y;
            ushort seq = Client.SendMoveInput(Player.LastMoveInput.x, Player.LastMoveInput.y, yaw,
                                              MoveInput.PackStance(Player.Stance));

            // 2) the local node IS the authority (listen-server): write its sim-core + real-collision
            //    result into the replication entity; ServerStep skips externally-driven entities
            var pos = Player.GlobalPosition;
            Server.Players.ServerDrive(Client.PlayerId,
                new UnityEngine.Vector3(pos.X, pos.Y, pos.Z), yaw, seq, Server.Session.CurrentTick);

            // 3) prediction bookkeeping (pass-through in loopback, §2.1): record the shell's position under
            //    the sent seq; NetWorldClient.Tick reconciles it against the snapshot's
            //    lastProcessedInputSeq, closing the predict->ack loop end to end. The residual is
            //    quantization-sized and is NOT applied back to the node (the node IS the authority here) --
            //    remote-client shells consume corrections for real (ClientPrediction).
            if (seq != 0)
                Client.Prediction.Reconciler.Record(seq, new UnityEngine.Vector3(pos.X, pos.Y, pos.Z));
        }

        public override void _ExitTree()
        {
            if (ConsumeDeployables) WorldItem.SuppressLocalVisual = false;   // P2b: clear the process-global suppress flag so a later non-consume world isn't affected
            if (DevConsole.RemoteClient == Client) DevConsole.RemoteClient = null;   // A3: release the static console route (mirrors ClientWorldSession) so a later world isn't left pointing at a dead client
            Client?.Disconnect();
            Server?.TearDown();
        }
    }
}
