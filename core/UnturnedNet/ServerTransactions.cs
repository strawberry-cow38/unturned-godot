using System;
using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot;   // Crafting + BlueprintDef (engine-free, core/UnturnedSim)

namespace UnturnedGodot.Net
{
    /// <summary>Counters for the Phase 6 grid/craft paths whose feasibility check IS the mutation (TryDrag,
    /// DoCraft): they can't be split into the registry's validate-then-apply, so rejections are counted
    /// here instead of CommandRegistryDiagnostics.ValidationRejected. Tests assert on these.</summary>
    public sealed class ServerTransactionsDiagnostics
    {
        public long GridMovesApplied;
        public long GridMovesRejected;      // the server grid said no (illegal cell/overlap/out-of-bounds)
        public long CraftsApplied;
        public long CraftsRejected;         // missing supplies / skill gate / station gate / non-Craft op
        public long CraftCancelsApplied;
        public long CraftCancelsRejected;   // no queue wired, or the slot finished before the packet landed
        public long ConsumesApplied;
        public long ConsumesRejected;
        public long AttachFitsApplied;
        public long AttachFitsRejected;     // empty cell / wrong item at that address (a stale client grid)
        public long MagLoadsApplied;        // one round moved into or out of a magazine
        public long MagLoadsRejected;       // stale slot, wrong item id, rule refused, or a full bag on unload
        public long PickupsDenied;          // legal pickup, full grid -> ItemPickupDenied went back
        public long ShelfTakesApplied;      // F straight onto an item on a shelf -> it moved into the taker's bag
        public long ShelfTakesRejected;     // ...out of reach, someone else has the container open, cell empty, or bag full
        public long ReloadsApplied;
        public long ReloadsRejected;        // no magazine at that address / not a magazine
        public long ClothingApplied;
        public long ClothingRejected;       // empty cell / wrong item type for that slot / no room for the displaced garment
        public long AutoDrinkApplied;
        public long AutoDrinkRejected;      // empty cell / a different item at that address
        public long GunStatesApplied;       // the client's gun state landed on the server's copy of that item
        public long GunStatesRejected;      // empty cell / a different item at that address (a stale client grid)
        public long ConsoleApplied;
        public long ConsoleRejected;        // unknown verb / cheats disabled / bad args
        public long ChatSent;               // v49: chat lines broadcast (player + server)
        public long ChatRejected;           // empty after sanitising, or rate limited
        public long DeathDrops;             // deaths that emptied a bag onto the ground (0 items carried still counts)
        public long DeathDropItems;         // world items those deaths created (grid items + worn clothing)
    }

    /// <summary>
    /// The Phase 6 transactional slice, server side (MP_PLAN §4 Phase 6): registers every §3.1/§3.2/§3.3
    /// command on the ONE validation choke point (§2.3 -- sender identity always from the connection) and
    /// coordinates the cross-system effects: placement consumes the deployable item, salvage drops scrap
    /// world items, pickup/drop move items between a grid and the world, consume heals the combat state,
    /// and the DevConsole's cheats run HERE, against authoritative state, or not at all.
    /// </summary>
    public sealed class ServerTransactions
    {
        /// <summary>Server-side pickup reach: SP picks up by eye-ray focus at arm's length; the server
        /// bounds it generously (grid-quantized feet positions, no eye trace).</summary>
        public const float PickupReach = 6f;

        /// <summary>Server-side pickup facing cone (strawberry's look-at requirement, honest v1): the
        /// engine-free core has yaw but no pitch and no world raycast, so the provable bound is a
        /// horizontal cone -- dot(facing, toItem) >= this. 0.25 ~= a 75-degree half-angle: generous for
        /// quantized wire yaw + look-down pickups, tight enough that a modified client can no longer
        /// hoover the full 6 m sphere behind its back. Through-wall pickup inside reach+cone remains
        /// until the game-side LOS seam (MP_PLAN §7 pre-public hardening).</summary>
        public const float PickupFacingMinDot = 0.25f;

        /// <summary>Inside this horizontal range the cone is SKIPPED: an item at your feet has an
        /// unstable bearing (and SP allows feet pickups via the eye ray anyway).</summary>
        public const float PickupFacingSkipRange = 1.5f;

        /// <summary>Server-side plant/harvest reach (SP harvests at 3 m by eye focus; same generous
        /// feet-position bound as PickupReach).</summary>
        public const float CropReach = 6f;

        /// <summary>Source ItemFarmAsset default (verified in CropManager: no Seed_* .dat overrides
        /// Harvest_Reward_Experience) -- awarded per harvest, same as the SP path.</summary>
        public const uint HarvestRewardExperience = 1;

        /// <summary>Dev/cheat console verbs (give/xp/skill/teleport) apply only while true -- a public dedicated
        /// server would flip this off (admin gating is deferred policy, the choke point is the mechanism).</summary>
        public bool AllowCheats = true;

        /// <summary>
        /// Gate base edits on who placed the thing: salvage, pickup, wire connect/remove and toggle.
        ///
        /// DEFAULT OFF, which is exactly today's behaviour -- this server is a friendly co-op box where
        /// editing each other's bases is the point, and flipping that by surprise would be a worse bug than
        /// the hole it closes. What changes is that the mechanism now EXISTS and is tested, so opening the
        /// server to strangers is one flag rather than a security project. The server browser landing today
        /// is what makes that difference matter.
        ///
        /// Ownership means OwnerPlayerId, which is already stamped at placement, already on the wire and
        /// already in the state hash -- nothing new is replicated for this.
        /// </summary>
        public bool EnforceOwnership;

        /// <summary>May `sender` modify this deployable? Owner-placed things answer to their owner.
        ///
        /// Owner 0 is the WORLD -- map fixtures placed by the level build, not by a player: street lamps,
        /// gas pumps, grid sources. Those must stay usable by everyone or enforcing ownership would silently
        /// make every municipal light and pump un-toggleable, which is the kind of "fix" that reads as a
        /// regression. A player's own base is the only thing this protects.</summary>
        public bool MayModify(ushort sender, DeployableReplication.DeployableEntity e) =>
            !EnforceOwnership || e == null || e.OwnerPlayerId == 0 || e.OwnerPlayerId == sender;

        /// <summary>A2 (SP/MP-unify): the authoritative gas-station tanks the ExtractFuel choke drains, behind
        /// the IFuelStation seam. The HOST supplies it (game: GasStationServer built from the placed gas-pump
        /// fixtures; tests: a fake). Null on a world with no gas pumps -> ExtractFuel is a no-op. Set after
        /// construction (like AllowCheats/IsSeated), since GasStationServer is built from the server-placed
        /// fixtures which mint their NetIds off this same server.</summary>
        public IFuelStation FuelStations;

        /// <summary>Seat query for the console teleport (#27): while seated the seat teleport owns the
        /// entity (ServerVehicles.Step re-asserts it every tick), so a ServerTeleport would silently lose
        /// the fight -- reject instead. NetWorldServer wires this to VehicleHost.IsDriver (it's built
        /// after this object); null (bare L0 harnesses without vehicles) = never seated.</summary>
        public Func<ushort, bool> IsSeated;

        /// <summary>The server-side craft queue. Null on a host that has not wired one, in which case OnCraft
        /// falls back to crafting instantly -- named with a trailing underscore only because `Crafting` is the
        /// static rules class this file already leans on, and shadowing it would be worse.</summary>
        public ServerCrafting Crafting_;

        /// <summary>The blueprint catalog the Craft command indexes into. The HOST supplies it (game:
        /// BlueprintRegistry.All; tests: fixtures); both sides must load the same list -- guaranteed by the
        /// same content-hash handshake that guarantees item defs match.</summary>
        public IReadOnlyList<BlueprintDef> Blueprints = Array.Empty<BlueprintDef>();

        public ServerTransactionsDiagnostics Diag { get; } = new ServerTransactionsDiagnostics();

        /// <summary>The server's yield-roll RNG seam (Phase 8, §3.7: the AGRICULTURE second-yield roll moves
        /// server-side -- SP keeps GD.Randf on the direct path). Injectable so L0 tests are deterministic.</summary>
        public Func<float> Rand;
        /// <summary>Installed by the game side (which owns the file path): deletes the save, clears the host's
        /// PendingSave, and returns the line to show the admin. Null on a server with no persistence, and the
        /// `wipe` verb says so rather than reporting a success it did not have.</summary>
        public Func<string> WipeSaveHandler;
        /// <summary>Installed alongside WipeSaveHandler: write the world out NOW. A normal admin action before a
        /// restart -- the autosave is on a timer, so without this the only way to be sure a save is current is to
        /// wait for it. It is also what makes the save path testable in a real world at all.</summary>
        public Func<string> SaveNowHandler;

        // ---- v49 chat + moderation --------------------------------------------------------------------
        /// <summary>The ban list. Engine-free rules (expiry, matching, duration parsing) live in
        /// ServerModeration; persisting it and refusing a banned peer at the handshake are the game side's.</summary>
        public readonly ServerModeration Moderation = new ServerModeration();

        /// <summary>Per-player chat rate limiting. Never consulted for server lines.</summary>
        public readonly ChatRateLimiter ChatLimiter = new ChatRateLimiter();

        /// <summary>Display name for a player id. INJECTED rather than read from ServerProfiles, so this
        /// class does not grow a dependency on the profile system to print a name.</summary>
        public Func<ushort, string> NameOf;

        /// <summary>Who is connected right now. Injected for the same reason NameOf is -- the peer list
        /// lives on NetWorldHost's session, which this class deliberately does not reach into.</summary>
        public Func<System.Collections.Generic.IEnumerable<ushort>> ConnectedPlayers;

        /// <summary>Disconnect a live peer, with a reason they see. The core cannot do this itself -- it
        /// owns no sockets -- so kick/ban delegate here the way save/wipe delegate to their handlers.
        /// Returns true if a peer was actually removed.</summary>
        public Func<ushort, string, bool> KickHandler;

        /// <summary>Address + name for a player id, for a ban that has to outlive the connection.
        /// Returns false if the id is not connected.</summary>
        public Func<ushort, (uint ipv4, string name)?> IdentityOf;

        /// <summary>Wall-clock seconds, injected for the same reason _tick is: the rules are testable
        /// without a clock and the server supplies the real one.</summary>
        public Func<double> NowSeconds;

        /// <summary>Called when the ban list changes, so the game side can persist it.</summary>
        public Action BansChanged;

        readonly PlayerReplication _players;
        readonly PlayerCombatReplication _combat;
        readonly SkillsReplication _skills;
        readonly InventoryReplication _inventories;
        readonly WorldItemReplication _worldItems;
        readonly DeployableReplication _deployables;
        readonly CropReplication _crops;
        readonly ResourceReplication _resources;
        readonly PlayerVitalsReplication _vitals;   // B5: OnConsume raises server food/water/stamina/infection here
        readonly ServerInteractables _interactables; // SP/MP unify: authoritative door + bed state
        readonly NetIdMinter _ids;
        // v43: WHO IS CUFFED. An instance, not a static -- MpLoopback stands hosts up and tears them down per
        // test, and arrest state surviving between them would be a bug nobody would look for.
        readonly SDG.Unturned.ArrestSim _arrest = new SDG.Unturned.ArrestSim();
        readonly Dictionary<ushort, (byte side, long tick)> _lastStruggle = new();   // the two bounds on wriggling: alternate sides, one per tick
        readonly Func<long> _tick;
        readonly Action<byte[]> _broadcast;
        readonly Action<ushort, byte[]> _sendTo;

        /// <summary>The cooking appliances. Settable rather than a constructor argument, the same shape
        /// ServerCombat.ZombieHost uses: an optional collaborator that a bare transactional harness does not
        /// need, on a constructor that already takes twelve things.</summary>
        public ServerCooking Cooking;

        /// <summary>Forageable resources (berry bushes, mushrooms). Settable for the same reason Cooking is.</summary>
        public ServerForage Forage;

        /// <summary>A spraypaint item id -> its colour as 0xRRGGBB, or null if that id is not a spraypaint.
        /// Set by the game layer (VehiclePaints), because the table is content and core cannot read content.
        /// Null here means no spraypaint resolves, which is the right failure: a server that does not know
        /// what a can is must not paint anything.</summary>
        public System.Func<ushort, uint?> PaintColorFor;

        /// <summary>Spend one spraypaint out of the sender's bag and report the colour it was. Null if the id
        /// is not a spraypaint or the sender does not actually have one -- the ownership check is the whole
        /// point, since the client names the can and a client that could name one it does not own could
        /// repaint the map for free.</summary>
        public uint? SpendPaintCan(ushort sender, ushort itemId)
        {
            if (PaintColorFor == null) return null;
            uint? rgb = PaintColorFor(itemId);
            if (rgb == null) return null;
            var inv = SenderInventory(sender);
            if (inv == null || inv.getItemCount(itemId) <= 0) return null;
            SpendAnyOf(inv, itemId, sender);
            return rgb;
        }

        /// <summary>v45: take one spare tire out of the sender's bag. Which item that IS comes from the game
        /// layer via TireItemId rather than a constant here -- core has no item catalogue and should not learn
        /// one for a single id. False when they have none, which is what makes the handler's ordering matter:
        /// everything else is checked first, and this is the step that cannot be undone.</summary>
        public ushort TireItemId;   // set by the host from the game layer's PlayerController.TireItemId
        public bool SpendTire(ushort sender)
        {
            if (TireItemId == 0) return false;
            var inv = SenderInventory(sender);
            if (inv == null || inv.getItemCount(TireItemId) <= 0) return false;
            SpendAnyOf(inv, TireItemId, sender);
            return true;
        }

        public ServerTransactions(PlayerReplication players, PlayerCombatReplication combat,
                                  SkillsReplication skills, InventoryReplication inventories,
                                  WorldItemReplication worldItems, DeployableReplication deployables,
                                  NetIdMinter ids, Func<long> tick,
                                  Action<byte[]> broadcast, Action<ushort, byte[]> sendTo,
                                  CropReplication crops = null, ResourceReplication resources = null,
                                  PlayerVitalsReplication vitals = null,
                                  ServerInteractables interactables = null)
        {
            _players = players; _combat = combat;
            _skills = skills; _inventories = inventories; _worldItems = worldItems; _deployables = deployables;
            _crops = crops; _resources = resources; _vitals = vitals;
            _interactables = interactables;
            _ids = ids; _tick = tick; _broadcast = broadcast; _sendTo = sendTo;
            var rng = new Random();   // server-side only (§2.5: only the server rolls); tests inject a stub
            Rand = () => (float)rng.NextDouble();
        }

        public void Register(CommandRegistry commands)
        {
            commands.Register<UpgradeSkillCommand>(ReplicationIds.CommandUpgradeSkill, UpgradeSkillCommand.TryRead,
                (sender, cmd) => _skills.ServerTryUpgrade(sender, cmd.Speciality, cmd.Index, _tick()),
                validate: (sender, cmd) => _skills.TryGet(sender, out _) && cmd.Speciality < PlayerSkills.SPECIALITIES);

            commands.Register<PlaceDeployableCommand>(ReplicationIds.CommandPlaceDeployable, PlaceDeployableCommand.TryRead,
                OnPlaceDeployable,
                validate: (sender, cmd) => TryGetSenderPos(sender, out var pos)
                                        && _deployables.CanPlace(cmd.DefId, cmd.Pos, pos)
                                        && SenderInventory(sender)?.getItemCount(cmd.DefId) > 0);   // placing spends the held item

            // Ownership (review M2, previously a TODO here): salvage/pickup/wire/toggle now run through
            // MayModify, which is a no-op until EnforceOwnership is set -- see that flag for why it defaults
            // off. The gate lives in the validators rather than the handlers so a rejected attempt is counted
            // as a validation rejection and never reaches authoritative state at all.
            commands.Register<SalvageDeployableCommand>(ReplicationIds.CommandSalvageDeployable, SalvageDeployableCommand.TryRead,
                OnSalvageDeployable,
                validate: (sender, cmd) => TryGetSenderPos(sender, out var pos)
                                        && _deployables.TryGet(cmd.NetId, out var e)
                                        && MayModify(sender, e)
                                        && e.OnFire   // only a dead/burning wreck tears down (SP: blowtorch a cooled wreck)
                                        && (e.Pos - pos).magnitude <= DeployableReplication.WireReach);

            // B2: hold-F pickup returns the LIVE deployable to the bag (distinct intent from Salvage's scrap --
            // the client gates hold-F on !IsWreck/!OnFire, so this never collides with a wreck salvage).
            // This is the one that mattered most: salvage at least requires the target to be ON FIRE first,
            // but pickup takes a healthy deployable straight into your bag, so an ungated pickup let anyone
            // walk a base away piece by piece.
            commands.Register<PickupDeployableCommand>(ReplicationIds.CommandPickupDeployable, PickupDeployableCommand.TryRead,
                OnPickupDeployable,
                validate: (sender, cmd) => _inventories.TryGet(sender, out _)
                                        && TryGetSenderPos(sender, out var pos)
                                        && _deployables.TryGet(cmd.NetId, out var e)
                                        && MayModify(sender, e)
                                        && (e.Pos - pos).magnitude <= DeployableReplication.WireReach   // review H2: reach-gate like salvage
                                        && _deployables.Schema.TryGet(e.DefId, out var def)
                                        && def.FixtureKind == FixtureKind.None);   // review M4: world fixtures (gas pump / grid source) are NOT pickup-able -- they'd be unreplaceable

            // A2: pull fuel from a gas-station pump into a held gas can. The validate is the cheap deref guard
            // (sender exists + the target is a registered gas-pump FIXTURE within reach + a station tank owns
            // it); OnExtractFuel does the REAL gating -- a fresh deterministic Solve() (the pump's Consumer
            // port must be Powered), a held can with free space, and the min(canSpace, remaining) drain. Extract
            // is the SOLE mutation on the shared tank, so it can't be double-spent (§ determinism 1/2/5).
            commands.Register<ExtractFuelCommand>(ReplicationIds.CommandExtractFuel, ExtractFuelCommand.TryRead,
                OnExtractFuel,
                validate: (sender, cmd) => FuelStations != null
                                        && FuelStations.TryGetStation(cmd.PumpNetId, out _)
                                        && TryGetSenderPos(sender, out var pos)
                                        && _deployables.TryGet(cmd.PumpNetId, out var e)
                                        && _deployables.Schema.TryGet(e.DefId, out var def)
                                        && def.FixtureKind == FixtureKind.GasPump
                                        && (e.Pos - pos).magnitude <= DeployableReplication.WireReach
                                        && SenderInventory(sender) != null);

            // BOTH ends are checked, not just the one you are standing at -- wiring your own generator into
            // someone else's grid is the same trespass as wiring theirs into yours.
            commands.Register<ConnectWireCommand>(ReplicationIds.CommandConnectWire, ConnectWireCommand.TryRead,
                OnConnectWire,
                validate: (sender, cmd) => TryGetSenderPos(sender, out var pos)
                                        && (!EnforceOwnership
                                            || (_deployables.TryGet(cmd.SrcId, out var s) && MayModify(sender, s)
                                             && _deployables.TryGet(cmd.DstId, out var d) && MayModify(sender, d)))
                                        && _deployables.CanConnectWire(cmd.SrcId, cmd.SrcPort, cmd.DstId, cmd.DstPort, pos));

            // Cutting a wire is gated on the SOURCE, which is the end the reach check already uses.
            commands.Register<RemoveWireCommand>(ReplicationIds.CommandRemoveWire, RemoveWireCommand.TryRead,
                OnRemoveWire,
                validate: (sender, cmd) => TryGetSenderPos(sender, out var pos)
                                        && _deployables.TryGetWire(cmd.WireId, out var w)
                                        && _deployables.TryGet(w.SrcId, out var src)
                                        && MayModify(sender, src)
                                        && (src.Pos - pos).magnitude <= DeployableReplication.WireReach);

            // Toggle is the one that stays open on world fixtures: OwnerPlayerId 0 means the level placed it,
            // so the street lamps and the grid mains keep answering to everybody (see MayModify).
            commands.Register<ToggleDeployableCommand>(ReplicationIds.CommandToggleDeployable, ToggleDeployableCommand.TryRead,
                OnToggleDeployable,
                validate: (sender, cmd) => TryGetSenderPos(sender, out var pos)
                                        && _deployables.CanToggle(cmd.NetId, out var e)
                                        && MayModify(sender, e)
                                        && (e.Pos - pos).magnitude <= DeployableReplication.WireReach);

            // SP/MP unify: doors + beds. Validation is reach (the server's business) plus the SAME
            // DoorLogic/BedClaims rules singleplayer runs -- one rule set, not a client copy and a server
            // copy that drift. A null _interactables leaves these unregistered, so a host without the
            // system behaves exactly as before.
            if (_interactables != null)
            {
                commands.Register<ToggleDoorCommand>(ReplicationIds.CommandToggleDoor, ToggleDoorCommand.TryRead,
                    OnToggleDoor,
                    validate: (sender, cmd) => TryGetSenderPos(sender, out var pos)
                                            && _interactables.CanToggleDoor(cmd.NetId, pos, sender, 0UL));

                commands.Register<SetDoorLockedCommand>(ReplicationIds.CommandSetDoorLocked, SetDoorLockedCommand.TryRead,
                    OnSetDoorLocked,
                    validate: (sender, cmd) => TryGetSenderPos(sender, out var pos)
                                            && _interactables.TryGetDoor(cmd.NetId, out var d)
                                            && (d.Pos - pos).magnitude <= ServerInteractables.InteractReach);

                commands.Register<ClaimBedCommand>(ReplicationIds.CommandClaimBed, ClaimBedCommand.TryRead,
                    OnClaimBed,
                    validate: (sender, cmd) => TryGetSenderPos(sender, out var pos)
                                            && _interactables.CanClaimBed(cmd.NetId, pos, sender));

                // v35: sitting on furniture. NetId 0 is STAND and needs no reach check -- you can always get
                // out of a chair, and requiring reach to leave one would strand a player whose seat was
                // removed under them. Anything else is a real seat and takes the same reach + occupancy test
                // an enter-vehicle does, because two clients each deciding they took the same chair is the
                // failure CommandEnterVehicle's occupancy check exists to stop.
                // v37: prop doors. Reach and nothing else -- a shipping container has no owner and no lock,
                // and the re-toggle cooldown is ObjectDoor's own, client-side, where it belongs (a second one
                // enforced here would fight it at a different rate and eat legitimate presses).
                commands.Register<ToggleObjectDoorCommand>(ReplicationIds.CommandToggleObjectDoor, ToggleObjectDoorCommand.TryRead,
                    OnToggleObjectDoor,
                    validate: (sender, cmd) => TryGetSenderPos(sender, out var pos)
                                            && _interactables.CanToggleObjectDoor(cmd.NetId, pos));

                commands.Register<SitSeatCommand>(ReplicationIds.CommandSitSeat, SitSeatCommand.TryRead,
                    OnSitSeat,
                    validate: (sender, cmd) => cmd.NetId == 0
                                            || (TryGetSenderPos(sender, out var pos)
                                                && _interactables.CanSit(cmd.NetId, pos, sender)));
            }

            commands.Register<MoveItemCommand>(ReplicationIds.CommandMoveItem, MoveItemCommand.TryRead,
                (sender, cmd) =>
                {
                    // TryDrag both validates (checkSpaceDrag/checkSpaceSwap -- the ported cell math) and
                    // applies; a false mutates nothing (§3.3 "the grid logic IS the validator").
                    bool ok = SenderInventory(sender)?.TryDrag(cmd.Page0, cmd.X0, cmd.Y0, cmd.Page1, cmd.X1, cmd.Y1, cmd.Rot1) == true;
                    if (ok) Diag.GridMovesApplied++; else Diag.GridMovesRejected++;
                },
                validate: (sender, cmd) => _inventories.TryGet(sender, out _));

            commands.Register<DropItemCommand>(ReplicationIds.CommandDropItem, DropItemCommand.TryRead,
                OnDropItem,
                validate: (sender, cmd) => _inventories.TryGet(sender, out _) && cmd.Page < PlayerInventory.PAGES);

            commands.Register<PickupItemCommand>(ReplicationIds.CommandPickupItem, PickupItemCommand.TryRead,
                OnPickupItem,
                validate: (sender, cmd) => TryGetSenderPos(sender, out var pos)
                                        && _inventories.TryGet(sender, out _)
                                        // ALIVE, like every other acting command. A corpse is still a peer with an
                                        // inventory for the whole respawn delay, and its own death drops land within
                                        // the radius where the facing check is skipped -- so without this a dead
                                        // player picks his kit back up off his own body before respawning, which
                                        // both voids the death penalty and takes the loot away from whoever earned
                                        // it. OnFire has checked cs.Alive since it was written; this never did.
                                        && _combat.TryGet(sender, out var ce) && ce.Alive
                                        && _worldItems.TryGet(cmd.NetId, out var e)
                                        && (e.Pos - pos).magnitude <= PickupReach
                                        && SenderFacingItem(sender, e.Pos));

            commands.Register<EquipItemCommand>(ReplicationIds.CommandEquipItem, EquipItemCommand.TryRead,
                (sender, cmd) =>
                {
                    bool ok = SenderInventory(sender)?.TryDrag(cmd.FromPage, cmd.X, cmd.Y, cmd.Slot, 0, 0, 0) == true;
                    if (ok) Diag.GridMovesApplied++; else Diag.GridMovesRejected++;
                },
                validate: (sender, cmd) => _inventories.TryGet(sender, out _) && cmd.Slot < PlayerInventory.SLOTS);

            commands.Register<CraftCommand>(ReplicationIds.CommandCraft, CraftCommand.TryRead,
                OnCraft,
                validate: (sender, cmd) => _inventories.TryGet(sender, out _) && cmd.BlueprintIndex < Blueprints.Count);

            commands.Register<CraftCancelCommand>(ReplicationIds.CommandCraftCancel, CraftCancelCommand.TryRead,
                OnCraftCancel,
                validate: (sender, cmd) => _inventories.TryGet(sender, out _));

            commands.Register<ConsumeCommand>(ReplicationIds.CommandConsume, ConsumeCommand.TryRead,
                OnConsume,
                validate: (sender, cmd) => _inventories.TryGet(sender, out _) && cmd.Page < PlayerInventory.PAGES);

            commands.Register<MagLoadCommand>(ReplicationIds.CommandMagLoad, MagLoadCommand.TryRead,
                OnMagLoad,
                validate: (sender, cmd) => _inventories.TryGet(sender, out _)
                                           && cmd.MagPage < PlayerInventory.PAGES
                                           && cmd.RoundPage < PlayerInventory.PAGES);

            // THE ON/OFF BUTTON. Reach-checked exactly like opening the thing: the client can already only
            // see a cooker it is standing at, and this makes the server agree rather than take its word.
            // SetOn returns false for a NetId that is not a registered cooker, so a forged id for some other
            // crate -- or for nothing at all -- is a no-op instead of creating an appliance.
            commands.Register<SetCookerOnCommand>(ReplicationIds.CommandSetCookerOn, SetCookerOnCommand.TryRead,
                (sender, cmd) =>
                {
                    if (Cooking == null) return;
                    if (!TryGetSenderPos(sender, out var pos)) return;
                    if (!_inventories.TryGetCrate(cmd.NetId, out var crate)) return;
                    if ((crate.Pos - pos).magnitude > InventoryReplication.StorageReach) return;
                    Cooking.SetOn(cmd.NetId, cmd.On);
                });

            // v43: CUFF a surrendering player. Everything that matters is checked here and nothing is taken
            // from the client but the target: which restraint is read off the captor's replicated hand, whether
            // the target is surrendering is read off the target's replicated gesture, and the reach is measured
            // between the two server-side positions. Retail's own server check is sqrMagnitude > 49 -- a 7 m
            // slack around a 3 m client ray, generous on purpose because the server is guarding against a forged
            // TARGET, not re-deciding what the client may aim at.
            commands.Register<ArrestTargetCommand>(ReplicationIds.CommandArrestPlayer, ArrestTargetCommand.TryRead,
                (sender, cmd) =>
                {
                    if (!_combat.TryGet(sender, out var captor) || !_combat.TryGet(cmd.TargetPlayerId, out var victim)) return;
                    if (!SDG.Unturned.ArrestDef.IsRestraint(captor.HeldId)) return;   // not holding cuffs
                    if (!InArrestReach(sender, cmd.TargetPlayerId)) return;
                    bool surrendering = victim.Gesture == (byte)SDG.Unturned.EPlayerGesture.SURRENDER_START;
                    if (!_arrest.TryArrest(sender, cmd.TargetPlayerId, captor.HeldId, surrendering)) return;
                    victim.Gesture = ServerGestures.Force(victim.Gesture, SDG.Unturned.EPlayerGesture.ARREST_START);
                    _combat.MarkDirty(victim, _tick());
                    SpendAnyOf(SenderInventory(sender), captor.HeldId, sender);   // the cuffs leave the captor's bag (retail equipment.use())
                });

            // v43: UNLOCK. Deliberately does NOT require the freer to be the captor -- retail lets anyone with
            // the key undo the cuffs, which is what makes a key worth carrying and worth taking off a body.
            commands.Register<ArrestTargetCommand>(ReplicationIds.CommandUnlockArrest, ArrestTargetCommand.TryRead,
                (sender, cmd) =>
                {
                    if (!_combat.TryGet(sender, out var freer) || !_combat.TryGet(cmd.TargetPlayerId, out var victim)) return;
                    if (!SDG.Unturned.ArrestDef.IsKey(freer.HeldId)) return;
                    if (!InArrestReach(sender, cmd.TargetPlayerId)) return;
                    ushort recovered = _arrest.TryUnlock(cmd.TargetPlayerId, freer.HeldId);
                    if (!_arrest.IsArrested(cmd.TargetPlayerId))
                    {
                        victim.Gesture = (byte)SDG.Unturned.EPlayerGesture.NONE;
                        _combat.MarkDirty(victim, _tick());
                        // The .dat's Recover: the cuffs come back to whoever turned the key. GiveOrDrop, not
                        // tryAddItem, so a full bag puts them on the floor instead of deleting them.
                        if (recovered != 0) GiveOrDrop(SenderInventory(sender), recovered,
                                                      _players.TryGetByOwner(sender, out var fp) ? fp.Pos : Vector3.zero);
                    }
                });

            // v43: STRUGGLE. Two bounds, and they are the whole security of the thing: the side must ALTERNATE
            // (retail's `lastLean != lean`) and at most one lands per server tick. A cuffed client that spams
            // this gets exactly what a player mashing Q and E gets.
            commands.Register<StruggleCommand>(ReplicationIds.CommandStruggle, StruggleCommand.TryRead,
                (sender, cmd) =>
                {
                    if (!_arrest.IsArrested(sender)) return;
                    long now = _tick();
                    if (_lastStruggle.TryGetValue(sender, out var last) && (last.side == cmd.Side || last.tick == now)) return;
                    _lastStruggle[sender] = (cmd.Side, now);
                    if (!_arrest.Struggle(sender)) return;
                    if (!_combat.TryGet(sender, out var me)) return;
                    me.Gesture = (byte)SDG.Unturned.EPlayerGesture.NONE;   // ARREST_STOP: they are out
                    _combat.MarkDirty(me, now);
                    ArrestBroke?.Invoke(sender);   // the metal clatter (retail's Metal_1 effect) -- game-layer, null on a bare host
                });

            // v42: a gesture REQUEST. Parked rather than decided -- the admission test needs the asker's stance
            // and whether their hands are full, and those arrive on the input packet the appearance publisher
            // holds. An out-of-range byte is dropped by the cast landing on a table row that is not
            // PlayerRequestable, so a forged value is refused by the same rule a legitimate one is.
            commands.Register<RequestGestureCommand>(ReplicationIds.CommandRequestGesture, RequestGestureCommand.TryRead,
                (sender, cmd) => ServerGestures.Request(sender, (SDG.Unturned.EPlayerGesture)cmd.Gesture));

            commands.Register<SetAutoDrinkCommand>(ReplicationIds.CommandSetAutoDrink, SetAutoDrinkCommand.TryRead,
                (sender, cmd) =>
                {
                    var inv = SenderInventory(sender);
                    var pg = inv?.items[cmd.Page];
                    byte ix = pg?.getIndex(cmd.X, cmd.Y) ?? byte.MaxValue;
                    var jr = ix == byte.MaxValue ? null : pg.getItem(ix);
                    if (jr?.item == null || jr.item.id != cmd.Id) { Diag.AutoDrinkRejected++; return; }
                    jr.item.autoDrink = cmd.AutoDrink;
                    Diag.AutoDrinkApplied++;
                    pg.raiseStateUpdated();
                },
                validate: (sender, cmd) => _inventories.TryGet(sender, out _) && cmd.Page < PlayerInventory.PAGES);

            commands.Register<GunStateCommand>(ReplicationIds.CommandGunState, GunStateCommand.TryRead,
                OnGunState,
                validate: (sender, cmd) => _inventories.TryGet(sender, out _) && cmd.Page < PlayerInventory.PAGES);

            commands.Register<FitAttachmentCommand>(ReplicationIds.CommandFitAttachment, FitAttachmentCommand.TryRead,
                OnFitAttachment,
                validate: (sender, cmd) => _inventories.TryGet(sender, out _) && cmd.Page < PlayerInventory.PAGES);

            commands.Register<ReloadSwapCommand>(ReplicationIds.CommandReloadSwap, ReloadSwapCommand.TryRead,
                OnReloadSwap,
                validate: (sender, cmd) => _inventories.TryGet(sender, out _) && cmd.Page < PlayerInventory.PAGES);

            commands.Register<GunUnloadCommand>(ReplicationIds.CommandGunUnload, GunUnloadCommand.TryRead,
                OnGunUnload,
                validate: (sender, cmd) => _inventories.TryGet(sender, out _) && cmd.Page < PlayerInventory.PAGES);

            commands.Register<WearClothingCommand>(ReplicationIds.CommandWearClothing, WearClothingCommand.TryRead,
                OnWearClothing,
                validate: (sender, cmd) => _inventories.TryGet(sender, out _) && cmd.Page < PlayerInventory.PAGES);

            commands.Register<UnwearClothingCommand>(ReplicationIds.CommandUnwearClothing, UnwearClothingCommand.TryRead,
                OnUnwearClothing,
                validate: (sender, cmd) => _inventories.TryGet(sender, out _));

            commands.Register<OpenStorageCommand>(ReplicationIds.CommandOpenStorage, OpenStorageCommand.TryRead,
                (sender, cmd) =>
                {
                    if (!TryGetSenderPos(sender, out var pos)) return;
                    if (_inventories.ServerOpenStorage(sender, cmd.NetId, pos, _tick())
                        && _inventories.TryGetCrate(cmd.NetId, out var crate))
                    {
                        ServerCooking.Cooker ck = null;
                        bool isCooker = Cooking != null && Cooking.TryGet(cmd.NetId, out ck);
                        var evt = new StorageOpenedEvent
                        {
                            NetId = cmd.NetId, Width = crate.Width, Height = crate.Height,
                            IsCooker = isCooker,
                            CookerKind = isCooker ? (byte)ck.Kind : (byte)0,
                            CookerOn = isCooker && ck.On,
                            CookerFuel = isCooker ? ck.FuelFrac : (byte)0,   // v29: the bar opens at the right height
                            FreezerWidth = crate.FreezerWidth, FreezerHeight = crate.FreezerHeight,   // v30: 0x0 = no freezer
                        };
                        _sendTo(sender, NetMessagePak.Pack(ReplicationIds.EventStorageOpened, evt.Write));
                        // ...and from here on this player is the one who hears the fuel burn down.
                        if (isCooker) Cooking.ForceStateSync(cmd.NetId);
                    }
                });

            // F on an item sitting on a shelf: take THAT one, without opening the container (v34). The grab is
            // an intent like every other grid mutation -- the client's shelf grid and its bag are both display
            // mirrors, so the local version of this was undone twice over.
            commands.Register<TakeFromStorageCommand>(ReplicationIds.CommandTakeFromStorage, TakeFromStorageCommand.TryRead,
                (sender, cmd) =>
                {
                    if (!TryGetSenderPos(sender, out var pos)) return;
                    if (_inventories.ServerTakeFromStorage(sender, cmd.NetId, cmd.X, cmd.Y, pos, _tick())) Diag.ShelfTakesApplied++;
                    else Diag.ShelfTakesRejected++;
                },
                validate: (sender, cmd) => _inventories.TryGet(sender, out _));

            commands.Register<CloseStorageCommand>(ReplicationIds.CommandCloseStorage, CloseStorageCommand.TryRead,
                (sender, cmd) =>
                {
                    uint crateId = _inventories.TryGet(sender, out var e) ? e.OpenCrateId : 0;
                    if (_inventories.ServerCloseStorage(sender, _tick()))
                    {
                        var evt = new StorageClosedEvent { NetId = crateId };
                        _sendTo(sender, NetMessagePak.Pack(ReplicationIds.EventStorageClosed, evt.Write));
                    }
                });

            commands.Register<ConsoleCommand>(ReplicationIds.CommandConsole, ConsoleCommand.TryRead, OnConsole,
                validate: (sender, cmd) => cmd.Text != null && cmd.Text.Length <= 128);

            // v49 global chat. The validate gate is a cheap RAW length bound only -- it stops a peer
            // spending memory before anything looks at the text. The real limit is applied to the
            // SANITISED string in OnChatSend, because 400 zero-width characters are not a long message.
            commands.Register<ChatSendCommand>(ReplicationIds.CommandChatSend, ChatSendCommand.TryRead, OnChatSend,
                validate: (sender, cmd) => cmd.Text != null && cmd.Text.Length <= ChatRules.MaxMessageChars * 8);

            // Phase 8 crops (§3.7): the server owns the growth clock and the yield roll. Planting spends
            // the seed item (server grid = the validator, like deployable placement); harvesting requires
            // tick-derived maturity. Both are reach-gated on the sender's authoritative position.
            if (_crops != null)
            {
                commands.Register<PlantCropCommand>(ReplicationIds.CommandPlantCrop, PlantCropCommand.TryRead,
                    OnPlantCrop,
                    validate: (sender, cmd) => TryGetSenderPos(sender, out var pos)
                                            && (cmd.Pos - pos).magnitude <= CropReach
                                            && _crops.Schema.TryGet(cmd.SeedId, out _)
                                            && SenderInventory(sender)?.getItemCount(cmd.SeedId) > 0);

                // FORAGE (retail ResourceManager.ReceiveForageRequest). Validated exactly as that does, in the
                // same order and against the same 400 sq.m: a real index, one registered forageable, still
                // standing, and within reach of the asker. All four live in ServerForage.CanForage so the
                // gate a test can call and the gate the wire runs are the same code.
                commands.Register<ForageResourceCommand>(ReplicationIds.CommandForageResource, ForageResourceCommand.TryRead,
                    OnForageResource,
                    validate: (sender, cmd) => Forage != null
                                            && TryGetSenderPos(sender, out var pos)
                                            && Forage.CanForage(cmd.Index, pos));
                commands.Register<HarvestCropCommand>(ReplicationIds.CommandHarvestCrop, HarvestCropCommand.TryRead,
                    OnHarvestCrop,
                    validate: (sender, cmd) => TryGetSenderPos(sender, out var pos)
                                            && _crops.TryGet(cmd.NetId, out var e)
                                            && (e.Pos - pos).magnitude <= CropReach
                                            && _crops.IsGrown(e, _tick()));
            }
        }

        // ---- cross-system handlers ----

        /// <summary>Spend ONE of `id` at the address the client named, and DIRTY the entry. Mirrors OnConsume,
        /// which had both halves of this right already.
        ///
        /// ⚠ THE DIRTY CALL IS NOT OPTIONAL. removeItemAmount's decrement is a bare `jar.item.amount -= take`,
        /// which raises no grid event -- only the branch where it hits zero calls removeItem, which does. So
        /// placing off a STACK mutated the server and never echoed: the client kept drawing a count the server no
        /// longer had, every later move addressed a jar the server disagreed about, and the bag read as full of
        /// fake items (strawberry 2026-09-10: "any item i moved in my bag went fake ... i couldnt remove items
        /// from my 1/2 slots"). Placing the LAST one was always fine, which is why it survived two repros.
        ///
        /// Returns false when the address does not hold that id -- a stale or hostile address spends nothing
        /// rather than something else.</summary>
        bool SpendAt(PlayerInventory inv, byte page, byte x, byte y, ushort id, ushort sender)
        {
            if (inv == null || page >= PlayerInventory.PAGES) return false;
            var pg = inv.items[page];
            byte idx = pg?.getIndex(x, y) ?? byte.MaxValue;
            var jar = idx == byte.MaxValue ? null : pg.getItem(idx);
            if (jar?.item == null || jar.item.id != id) return false;
            if (jar.item.amount > 1) { jar.item.amount--; _inventories.ServerMarkDirty(sender); }
            else pg.removeItem(idx);   // removeItem raises onStateUpdated, which dirties the entry itself
            return true;
        }

        /// <summary>The pre-address fallback: spend the first jar anywhere in pages 0..OWNPAGES with this id.
        /// Kept for commands that genuinely have no jar to name (the console's plant), and for an address that
        /// has gone stale. Dirties afterwards, which the bare decrement inside removeItemAmount never did.</summary>
        void SpendAnyOf(PlayerInventory inv, ushort id, ushort sender)
        {
            if (inv == null) return;
            inv.removeItemAmount(id, 1);
            _inventories.ServerMarkDirty(sender);
        }

        void OnPlaceDeployable(ushort sender, PlaceDeployableCommand cmd)
        {
            var inv = SenderInventory(sender);
            // READ THE CONDITION BEFORE SPENDING IT. The jar is about to be decremented or removed, and its
            // quality/fuelLevel are what a picked-up device carried here -- so they have to come off it first.
            // This is only possible because the command now names the jar: with an id search there was no
            // "the one you are holding" to read.
            float? placeHealth = null, placeFuel = null;
            if (cmd.Page < PlayerInventory.PAGES && inv != null)
            {
                var srcPage = inv.items[cmd.Page];
                byte si = srcPage?.getIndex(cmd.X, cmd.Y) ?? byte.MaxValue;
                var srcJar = si == byte.MaxValue ? null : srcPage.getItem(si);
                if (srcJar?.item != null && srcJar.item.id == cmd.DefId && _deployables.Schema.TryGet(cmd.DefId, out var sdef))
                {
                    if (sdef.Health > 0f) placeHealth = sdef.Health * srcJar.item.quality / 100f;
                    if (srcJar.item.fuelLevel >= 0f) placeFuel = srcJar.item.fuelLevel;
                }
            }
            // Spend the jar the client named. It carries the address as of v-this-commit; page 255 (or an address
            // that no longer holds the id) falls back to the old id search.
            if (!SpendAt(inv, cmd.Page, cmd.X, cmd.Y, cmd.DefId, sender)) SpendAnyOf(inv, cmd.DefId, sender);
            var e = _deployables.ServerPlace(_ids.Mint(), cmd.DefId, sender, cmd.Pos, cmd.YawDegrees, _tick(), placeHealth, placeFuel);
            if (e == null) return;
            // A STORAGE DEVICE BRINGS ITS OWN GRID, registered under the deployable's OWN NetId -- which is
            // what the client stamps onto the materialized crate and what its F-open addresses. So the whole
            // open/move/close path a map container already uses works on a placed fridge with no new command
            // and no new event: ServerOpenStorage is keyed by crate id and does not care whether that id came
            // from a world fixture or a deployable. Empty, unlike a map crate -- nobody loots a fridge you
            // have just put down.
            if (_deployables.Schema.TryGet(cmd.DefId, out var pdef) && pdef.StorageWidth > 0 && pdef.StorageHeight > 0)
            {
                _inventories.ServerRegisterCrate(new NetId(e.NetIdValue), pdef.StorageWidth, pdef.StorageHeight, e.Pos);
                // ...and if it cooks, it registers under the SAME NetId the crate uses, so the on/off command,
                // the open event's cooker fields and the cook step all address it exactly as they address an
                // oven. A placed campfire is a cooker that happens to have been carried there.
                if (pdef.CookerKind != 255 && Cooking != null)
                    Cooking.Register(e.NetIdValue, (ECookerKind)pdef.CookerKind);
            }
            var evt = new DeployablePlacedEvent { NetId = e.NetIdValue, DefId = e.DefId, OwnerPlayerId = sender, Pos = e.Pos, YawDegrees = e.YawDegrees };
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventDeployablePlaced, evt.Write));
        }

        void OnSalvageDeployable(ushort sender, SalvageDeployableCommand cmd)
        {
            _deployables.TryGet(cmd.NetId, out var e);
            _deployables.Schema.TryGet(e.DefId, out var def);
            SpillStorage(cmd.NetId, def, e.Pos);   // a container's contents are never silently deleted
            var cascaded = _deployables.ServerRemove(cmd.NetId, _tick());
            var evt = new DeployableRemovedEvent { NetId = cmd.NetId };
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventDeployableRemoved, evt.Write));
            foreach (uint wid in cascaded)
            {
                var wevt = new WireRemovedEvent { WireId = wid };
                _broadcast(NetMessagePak.Pack(ReplicationIds.EventWireRemoved, wevt.Write));
            }
            // the wreck breaks into scrap on the ground (SP Deployable.Salvage: 2x Metal Scrap)
            if (def != null && def.SalvageItemId != 0)
                for (int i = 0; i < def.SalvageCount; i++)
                    SpawnWorldItem(new Item(def.SalvageItemId), e.Pos + new Vector3((i - 0.5f) * 0.6f, 0.5f, 0f), Vector3.zero);
        }

        /// <summary>Tear down a placed container's grid, dropping whatever was in it on the floor. Salvaging
        /// or pocketing a full fridge must not swallow its contents: the grid is addressed by the deployable's
        /// NetId, so once the deployable is gone there is nothing left to stand next to and the items would be
        /// unreachable forever rather than merely lost.</summary>
        void SpillStorage(uint netId, DeployableNetDef def, Vector3 at)
        {
            if (def == null || def.StorageWidth == 0) return;
            // CLOSE FIRST. A player with this container open holds the live grid in his own STORAGE page;
            // crate.Storage is only brought up to date when he is closed out of it. Reading before closing
            // spills a stale snapshot and drops whatever he had just moved in.
            _inventories.ServerCloseCrateViewers(netId, _tick());
            if (_inventories.TryGetCrate(netId, out var crate))
            {
                int n = 0;
                for (byte i = 0; i < crate.Storage.getItemCount(); i++)
                {
                    var j = crate.Storage.getItem(i);
                    if (j?.item == null) continue;
                    SpawnWorldItem(j.item, at + new Vector3(((n % 4) - 1.5f) * 0.4f, 0.5f, ((n / 4) - 0.5f) * 0.4f), Vector3.zero);
                    n++;
                }
            }
            _inventories.ServerRemoveCrate(netId, _tick());
            // ...and it stops being a cooker. Without this a salvaged campfire leaves a live entry that steps
            // every tick against a crate that no longer exists -- harmless today because Step skips a missing
            // crate, but it is a registry that only ever grows, and the next thing keyed on "is this NetId a
            // cooker" would answer yes for a fire somebody dismantled an hour ago.
            Cooking?.Forget(netId);
        }

        void OnPickupDeployable(ushort sender, PickupDeployableCommand cmd)
        {
            // authority: read the LIVE entity's state before we tear it down, then reuse the salvage teardown
            // (ServerRemove + broadcast the removed/wire-removed facts). The DeployableReplicaView retires the
            // client node off EventDeployableRemoved; the returned item lands via the owner-inventory echo.
            _deployables.TryGet(cmd.NetId, out var e);
            _deployables.Schema.TryGet(e.DefId, out var def);
            SpillStorage(cmd.NetId, def, e.Pos);   // a container's contents are never silently deleted
            var cascaded = _deployables.ServerRemove(cmd.NetId, _tick());
            var evt = new DeployableRemovedEvent { NetId = cmd.NetId };
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventDeployableRemoved, evt.Write));
            foreach (uint wid in cascaded)
            {
                var wevt = new WireRemovedEvent { WireId = wid };
                _broadcast(NetMessagePak.Pack(ReplicationIds.EventWireRemoved, wevt.Write));
            }
            // hand back the ACTUAL deployable item with its HP (quality %) + fuel stamped on, so re-placing
            // restores them -- mirrors SP PlayerController.PickupDeployable @682-685.
            var item = Assets.makeLoot(e.DefId);
            if (def != null)
            {
                if (def.Health > 0f) item.quality = (byte)Mathf.Clamp(Mathf.RoundToInt(e.Health / def.Health * 100f), 1, 100);
                if (def.FuelCapacity > 0f) item.fuelLevel = e.Fuel;
            }
            // to the bag if it fits, else drop where it stood (SP "drop where it stood" @691)
            var inv = SenderInventory(sender);
            if (inv == null || !inv.tryAddItem(item))
                SpawnWorldItem(item, e.Pos + Vector3.up, Vector3.zero);
        }

        // A2 (SP/MP-unify): the ONE server-authoritative fuel-extract. The pump is a FixtureKind.GasPump
        // deployable; the shared 8000 L station tank lives ONLY here (FuelStations), never on the wire. Gate:
        // a FRESH deterministic Solve() (the pump's Consumer port must be Powered -- same solver both sides),
        // a held gas can with free space, and pulled = min(canSpace, stationRemaining) so the tank can't be
        // double-spent. Drain the absolute tank, fill the can (the owner-inventory echo re-adopts the fuller
        // can locally -- the client NEVER adds fuel itself), then write the recomputed 0..100 percent onto
        // EVERY same-station pump's Fuel scalar in ONE tick (atomic fan-out; a divergent per-pump fill desyncs).
        void OnExtractFuel(ushort sender, ExtractFuelCommand cmd)
        {
            if (FuelStations == null || !FuelStations.TryGetStation(cmd.PumpNetId, out int stationId)) return;
            if (!_deployables.TryGet(cmd.PumpNetId, out var pump)) return;

            // (1) powered gate: a fresh Solve() (pure/deterministic), reject unless the pump's Consumer port is live.
            _deployables.Solve();
            if (!IsPumpPowered(pump)) return;

            // (2) held gas can with free space (the SP _heldFuelItem, server-side = the sender's fullest-fillable can)
            var inv = SenderInventory(sender);
            if (inv == null) return;
            var can = FindFillableFuelCan(inv, out float canSpace);
            if (can == null || canSpace <= 0.01f) return;

            // (3) pulled = min(can free space, station remaining) -- validated server-side, so no double-spend
            float remaining = FuelStations.Remaining(stationId);
            float pulled = Mathf.Min(canSpace, remaining);
            if (pulled <= 0.01f) return;

            // (4) drain the ABSOLUTE tank + fill the can (owner echo carries the fuller can back to the client)
            FuelStations.Drain(stationId, pulled);
            can.fuelLevel = Mathf.Max(0f, can.fuelLevel) + pulled;
            _inventories.ServerMarkDirty(sender);   // a bare field write raises no grid event -- without this the fill never echoes (see ServerMarkDirty)

            // (5) recompute the 0..100 percent + fan it out onto EVERY same-station pump in ONE tick (same
            // LastChangedTick) so no two pumps ever replicate divergent fill. entity.Fuel IS the percent (the
            // absolute litres never leave the server); the pump has no HP/fire, so Health/OnFire pass through.
            float cap = FuelStations.Capacity(stationId);
            float percent = cap > 0f ? Mathf.Clamp(FuelStations.Remaining(stationId) / cap * 100f, 0f, 100f) : 0f;
            long tick = _tick();
            foreach (uint pid in FuelStations.Pumps(stationId))
                if (_deployables.TryGet(pid, out var pe))
                    _deployables.ServerSetScalars(pid, pe.Health, percent, pe.OnFire, tick);
        }

        // The pump's Consumer port is Powered after the fresh Solve() (a pure consumer needs a live wired
        // source; ToggledOn is irrelevant for a consumer). Scans the def's ports for the Consumer index.
        bool IsPumpPowered(DeployableReplication.DeployableEntity pump)
        {
            if (!_deployables.Schema.TryGet(pump.DefId, out var def)) return false;
            for (int i = 0; i < def.Ports.Length && i < pump.Solved.Length; i++)
                if (def.Ports[i].Kind == (byte)PowerPortKind.Consumer && pump.Solved[i].Powered) return true;
            return false;
        }

        // Server-side stand-in for the SP _heldFuelItem: the first fuel-container item in the sender's own
        // pages that still has free space (deterministic page/index scan). out its free space (capacity - fuel).
        static Item FindFillableFuelCan(PlayerInventory inv, out float space)
        {
            space = 0f;
            for (byte b = 0; b < PlayerInventory.OWNPAGES; b++)
            {
                var page = inv.items[b];
                for (byte i = 0; i < page.getItemCount(); i++)
                {
                    var it = page.getItem(i)?.item;
                    var a = it?.GetAsset();
                    if (a == null || !a.IsFuelContainer) continue;
                    float free = a.fuelCapacity - Mathf.Max(0f, it.fuelLevel);   // -1 (fresh) reads as empty, same as SP TryExtractFuel
                    if (free > 0.01f) { space = free; return it; }
                }
            }
            return null;
        }

        void OnConnectWire(ushort sender, ConnectWireCommand cmd)
        {
            var w = _deployables.ServerConnectWire(_ids.Mint(), cmd.SrcId, cmd.SrcPort, cmd.DstId, cmd.DstPort, _tick());
            var evt = new WireConnectedEvent { WireId = w.NetIdValue, SrcId = w.SrcId, SrcPort = w.SrcPort, DstId = w.DstId, DstPort = w.DstPort };
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventWireConnected, evt.Write));
        }

        void OnRemoveWire(ushort sender, RemoveWireCommand cmd)
        {
            if (!_deployables.ServerRemoveWire(cmd.WireId, _tick())) return;
            var evt = new WireRemovedEvent { WireId = cmd.WireId };
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventWireRemoved, evt.Write));
        }

        void OnToggleDeployable(ushort sender, ToggleDeployableCommand cmd)
        {
            if (!_deployables.ServerToggle(cmd.NetId, cmd.On, _tick())) return;
            var evt = new DeployableToggledEvent { NetId = cmd.NetId, On = cmd.On };
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventDeployableToggled, evt.Write));
        }

        // SP/MP unify: the door's authoritative flip, broadcast as a fact (the DeployableToggled shape).
        void OnToggleDoor(ushort sender, ToggleDoorCommand cmd)
        {
            if (!_interactables.ToggleDoor(cmd.NetId, out bool open)) return;
            BroadcastDoorState(cmd.NetId, open);
        }

        void OnSetDoorLocked(ushort sender, SetDoorLockedCommand cmd)
        {
            // Ownership is DoorLogic's rule: only the owner may lock. A refusal is silent -- the client
            // simply never sees the state change, which is the same answer it would get from a wall.
            if (!_interactables.SetDoorLocked(cmd.NetId, sender, cmd.Locked)) return;
            BroadcastDoorState(cmd.NetId, _interactables.IsDoorOpen(cmd.NetId));
        }

        void BroadcastDoorState(uint netId, bool open)
        {
            var evt = new DoorStateEvent { NetId = netId, Open = open, Locked = _interactables.IsDoorLocked(netId) };
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventDoorState, evt.Write));
        }

        void OnClaimBed(ushort sender, ClaimBedCommand cmd)
        {
            if (!_interactables.ClaimBed(cmd.NetId, sender, out uint released)) return;
            // The bed they LEFT is now free, and everyone needs to know -- otherwise a client keeps
            // rendering a claimed bed nobody owns.
            if (released != 0)
                _broadcast(NetMessagePak.Pack(ReplicationIds.EventBedClaimed,
                    new BedClaimedEvent { NetId = released, Owner = 0 }.Write));
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventBedClaimed,
                new BedClaimedEvent { NetId = cmd.NetId, Owner = sender }.Write));
        }

        void OnToggleObjectDoor(ushort sender, ToggleObjectDoorCommand cmd)
        {
            if (!_interactables.ToggleObjectDoor(cmd.NetId, out bool open)) return;
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventObjectDoorState,
                new ObjectDoorStateEvent { NetId = cmd.NetId, Open = open }.Write));
        }

        void OnSitSeat(ushort sender, SitSeatCommand cmd)
        {
            if (cmd.NetId == 0)
            {
                // Standing up. Silent when they were not sitting: an event saying a seat nobody was in came
                // free would make every client repaint a chair for nothing, and would let a client spam it.
                if (_interactables.Stand(sender, out uint freed) && freed != 0)
                {
                    _players.ServerRefreshStance(sender, _tick?.Invoke() ?? 0L);   // the pose belongs to whoever changed the seat, not to the next drive tick
                    _broadcast(NetMessagePak.Pack(ReplicationIds.EventSeatOccupied,
                        new SeatOccupiedEvent { NetId = freed, Occupant = 0 }.Write));
                }
                return;
            }
            if (!_interactables.Sit(cmd.NetId, sender, out uint released)) return;
            _players.ServerRefreshStance(sender, _tick?.Invoke() ?? 0L);
            // Moving straight from one chair to another: say the old one came free FIRST, so no client ever
            // sees this player in two seats at once. Same ordering as the bed re-claim above, and for the
            // same reason.
            if (released != 0)
                _broadcast(NetMessagePak.Pack(ReplicationIds.EventSeatOccupied,
                    new SeatOccupiedEvent { NetId = released, Occupant = 0 }.Write));
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventSeatOccupied,
                new SeatOccupiedEvent { NetId = cmd.NetId, Occupant = sender }.Write));
        }

        void OnDropItem(ushort sender, DropItemCommand cmd)
        {
            var inv = SenderInventory(sender);
            var page = inv.items[cmd.Page];
            byte index = page.getIndex(cmd.X, cmd.Y);
            if (index == byte.MaxValue) return;
            var jar = page.getItem(index);
            if (jar?.item == null) return;
            page.removeItem(index);

            // drop it just ahead of the avatar with a small toss -- clients run the cosmetic tumble (§3.3).
            // Godot convention (-sin,0,-cos): p.YawDegrees is the shell's RotationDegrees.Y, body faces -Z at yaw 0 --
            // the SAME frame SenderFacingItem uses. (Still latent -- no client sends DropItem yet -- but aligned so a
            // toss lands in FRONT of the player, not behind, when the seam wires up.)
            _players.TryGetByOwner(sender, out var p);
            float yawRad = (p?.YawDegrees ?? 0f) * (Mathf.PI / 180f);
            var fwd = new Vector3(-Mathf.Sin(yawRad), 0f, -Mathf.Cos(yawRad));
            var origin = (p?.Pos ?? Vector3.zero) + fwd * 1.2f + new Vector3(0f, 1.0f, 0f);
            SpawnWorldItem(jar.item, origin, fwd * 2.5f + new Vector3(0f, 2f, 0f));
        }

        /// <summary>DEATH DROP (strawberry 2026-09-02: "your items are kept after death instead of dropping on
        /// the ground"). Everything the player CARRIED becomes a real world item at the death spot: both hand
        /// slots, the pockets, every clothing page, then the worn clothing itself -- retail's
        /// Lose_Items/Lose_Clothes default. Each lands as a WorldItemReplication entity + a WorldItemSpawned
        /// broadcast, so every client (the victim included) materializes it through its WorldItemReplicaView and
        /// anyone can pick it up through the ordinary PickupItem command. Nothing is deleted: the Item objects
        /// move from the grid onto the ground with their state (ammo, attachments, fuel, fluid, quality) intact.
        ///
        /// ORDER MATTERS: pages before clothes. Taking a bag off resizes its page to 0x0 and DISCARDS whatever
        /// was in it (PlayerInventory.Resize) -- unwearing first would silently destroy the backpack's contents.
        /// STORAGE (7) and AREA (8) are external containers, not the player: an open crate's page is saved back
        /// into the crate and CLOSED (releasing the one-opener lock a corpse would otherwise hold forever).
        ///
        /// Placement: a flat ring at the feet, no toss. Command-spawned entities have no server-side physics node
        /// to settle them (WorldItemNetSync only tracks nodes), so a lofted item would hover where it spawned.
        /// Returns the number of world items created. Called from ServerCombat.PlayerDied (wired in
        /// NetWorldServer) -- the single death path, so bullets, fall, zombies, starvation and OOB all drop.</summary>
        public int DropInventoryOnDeath(ushort playerId)
        {
            var inv = SenderInventory(playerId);
            if (inv == null) return 0;

            // an open crate is not yours to drop: save its page back and release the lock (the client's
            // StorageClosed lands so its STORAGE tab shuts, exactly as a CloseStorage command would)
            uint openCrate = _inventories.TryGet(playerId, out var entry) ? entry.OpenCrateId : 0;
            if (openCrate != 0 && _inventories.ServerCloseStorage(playerId, _tick()))
                _sendTo(playerId, NetMessagePak.Pack(ReplicationIds.EventStorageClosed, new StorageClosedEvent { NetId = openCrate }.Write));

            var feet = _players.TryGetByOwner(playerId, out var p) ? p.Pos : Vector3.zero;
            int n = 0;
            for (byte page = 0; page < PlayerInventory.STORAGE; page++)
            {
                var pg = inv.items[page];
                while (pg.getItemCount() > 0)
                {
                    byte last = (byte)(pg.getItemCount() - 1);
                    var jar = pg.getItem(last);
                    pg.removeItem(last);
                    if (jar?.item == null) continue;
                    SpawnWorldItem(jar.item, DeathDropSpot(feet, n), Vector3.zero);
                    n++;
                }
            }
            foreach (var slot in DeathDropClothingOrder)
            {
                var worn = WornIn(inv, slot);
                if (worn == null) continue;
                Wear(inv, slot, null);
                SpawnWorldItem(worn, DeathDropSpot(feet, n), Vector3.zero);
                n++;
            }
            _inventories.ServerMarkDirty(playerId);   // removeItem dirtied the pages; the bare worn-slot writes did not
            Diag.DeathDrops++;
            Diag.DeathDropItems += n;
            return n;
        }

        static readonly EItemType[] DeathDropClothingOrder =
            { EItemType.HAT, EItemType.GLASSES, EItemType.MASK, EItemType.VEST, EItemType.BACKPACK, EItemType.SHIRT, EItemType.PANTS };

        /// <summary>The i-th drop's spot: a golden-angle spiral of 0.45-0.95 m around the feet, 5 cm up so an
        /// item sits on flat ground rather than in it. Deterministic (no RNG) so the L0 parity checks hold.</summary>
        static Vector3 DeathDropSpot(Vector3 feet, int i)
        {
            float ang = i * 2.399963f;                       // golden angle in radians -- no two of the first dozens overlap
            float r = 0.45f + 0.5f * (i % 4) / 3f;
            return feet + new Vector3(Mathf.Cos(ang) * r, 0.05f, Mathf.Sin(ang) * r);
        }

        void OnPickupItem(ushort sender, PickupItemCommand cmd)
        {
            _worldItems.TryGet(cmd.NetId, out var e);
            var inv = SenderInventory(sender);
            // retail tryAddItemAuto (strawberry 2026-09-04): an empty clothing slot wears the pickup, an empty hand slot
            // holsters it -- the owner echo carries worn + slots, and the client forces a slotted weapon into the hands
            if (inv.tryAddItemAuto(e.ServerItem, out _) != PlayerInventory.AutoPlace.None)
            {
                RemoveWorldItem(cmd.NetId);
            }
            else
            {
                // legal but no room. tryAddItem may have partially merged a stack (SP TryPickup behaves the
                // same) -- publish the reduced amount so replicas agree with the server's remainder.
                if (e.ServerItem != null && e.Amount != e.ServerItem.amount)
                {
                    e.Amount = e.ServerItem.amount;
                    e.LastChangedTick = _tick();
                }
                Diag.PickupsDenied++;
                var evt = new ItemPickupDeniedEvent { NetId = cmd.NetId };
                _sendTo(sender, NetMessagePak.Pack(ReplicationIds.EventItemPickupDenied, evt.Write));
            }
        }

        void OnCraft(ushort sender, CraftCommand cmd)
        {
            var bp = Blueprints[cmd.BlueprintIndex];
            // station proximity and target-item operations (RepairTargetItem/Ammo/Salvage) are deferred --
            // reject rather than mis-apply (the SP crafting UI drives those flows locally).
            if (bp.RequiresStation || bp.Operation != "Craft") { Diag.CraftsRejected++; return; }
            _skills.TryGet(sender, out var skillsEntry);
            if (!Crafting.MeetsSkill(bp, skillsEntry?.Skills)) { Diag.CraftsRejected++; return; }
            // TIMED NOW (master 2026-09-06: "add crafting timed jobs to the server"). This used to be a
            // straight DoCraft in the same tick, which meant the per-recipe times were enforced by the SP
            // client and ignored by the authoritative side. ServerCrafting takes the ingredients up front and
            // pays out when the clock runs down; the instant path stays only as the fallback for a host that
            // never wired a queue, so a bare test harness still crafts.
            if (Crafting_ != null)
            {
                if (Crafting_.Enqueue(sender, cmd.BlueprintIndex)) Diag.CraftsApplied++; else Diag.CraftsRejected++;
                return;
            }
            var adapter = new Crafting.PlayerInvAdapter(SenderInventory(sender));
            if (Crafting.DoCraft(bp, adapter)) Diag.CraftsApplied++; else Diag.CraftsRejected++;
        }

        /// <summary>Spend the item that was just fitted onto a gun.
        ///
        /// Deliberately NOT routed through OnConsume: that rejects anything whose asset is not IsConsumable -- a
        /// magazine or a scope is not edible -- and it applies useHealth/useFood effects, so fitting a sight
        /// would have healed the player. This only removes the item.
        ///
        /// The ID is checked against the cell before removing anything. The client's grid can shift between the
        /// click and the packet arriving, and deleting whatever now occupies that address would turn a stale
        /// click into "the server ate my medkit".</summary>
        /// <summary>MAGAZINE LOAD/UNLOAD, server side. THE HALF THAT WAS MISSING.
        ///
        /// The client had this working entirely locally: it moved rounds between the loose stack and the
        /// magazine in its OWN inventory and never told anyone. In the unified SP/MP path every inventory
        /// move round-trips through the authoritative server, so the next time the player nudged anything
        /// the owner-inventory echo arrived carrying the server's untouched magazine and put the rounds
        /// straight back. "Unload a mag, move anything, the rounds go back in."
        ///
        /// The gate is SDG.Unturned.MagRules.CheckLoad -- the same function the client draws its drag-over
        /// hint from, not a re-implementation of it. A second copy would let the two sides disagree about
        /// the RULE rather than the state, which is the same bug wearing a better disguise.</summary>
        void OnMagLoad(ushort sender, MagLoadCommand cmd)
        {
            var inv = SenderInventory(sender);
            var magPage = inv.items[cmd.MagPage];
            byte magIndex = magPage.getIndex(cmd.MagX, cmd.MagY);
            var magJar = magIndex == byte.MaxValue ? null : magPage.getItem(magIndex);
            // Identity, not just position: a slot the client believed held this magazine may hold something
            // else by the time the command lands, and loading rounds into whatever moved there is worse
            // than refusing.
            if (magJar?.item == null || magJar.item.id != cmd.MagId) { Diag.MagLoadsRejected++; return; }
            var magAsset = Assets.find(magJar.item.id);
            if (magAsset == null || !magAsset.IsMagazine) { Diag.MagLoadsRejected++; return; }

            if (cmd.Unloading)
            {
                var roundAsset = Assets.find(cmd.RoundId);
                string held = MagRules.EffectiveRound(magJar.item, magAsset);
                // The client names the cartridge it expects out; if the server's magazine holds a different
                // one the two have diverged and guessing would hand the player the wrong ammunition.
                // The output must be LOOSE AMMUNITION. Matching magRound alone is not enough: a magazine
                // declares the cartridge it ACCEPTS in the same field, so a client naming a second magazine
                // of the same calibre passed this check and the server built it one round at a time --
                // spending one round per magazine body created. That is an item printer, not an unload.
                if (magJar.item.amount <= 0 || roundAsset == null || roundAsset.IsMagazine
                    || held == null || held != roundAsset.magRound)
                { Diag.MagLoadsRejected++; return; }

                // ADD FIRST, DECREMENT ONLY IF IT LANDED. A full bag must abort before the magazine loses a
                // round, or unloading into a full inventory destroys ammunition -- silently, since nothing
                // reports a failed tryAddItem.
                if (!inv.tryAddItem(new Item(cmd.RoundId, 1))) { Diag.MagLoadsRejected++; return; }
                MagRules.ApplyUnload(magJar.item, magAsset);
                Diag.MagLoadsApplied++;
                return;
            }

            var roundPage = inv.items[cmd.RoundPage];
            byte roundIndex = roundPage.getIndex(cmd.RoundX, cmd.RoundY);
            var roundJar = roundIndex == byte.MaxValue ? null : roundPage.getItem(roundIndex);
            if (roundJar?.item == null || roundJar.item.id != cmd.RoundId || roundJar.item.amount <= 0)
            { Diag.MagLoadsRejected++; return; }

            var bullet = Assets.find(roundJar.item.id);
            if (!MagRules.ApplyLoad(magJar.item, magAsset, bullet)) { Diag.MagLoadsRejected++; return; }

            // Spend the round only after the load is committed, and free the slot at zero so an emptied
            // stack does not linger as a ghost the client cannot pick up.
            roundJar.item.amount--;
            if (roundJar.item.amount <= 0) roundPage.removeItem(roundIndex);
            Diag.MagLoadsApplied++;
        }

        /// <summary>Adopt the client's gun state onto the server's copy of that item, so the owner echo carries
        /// it back through a move instead of overwriting it with a default the server never populated. See
        /// ReplicationIds.CommandGunState for the failure this exists to stop.
        ///
        /// The one clamp that matters is the ammo count, and it is the same clamp OnReload already applies to
        /// ReloadSwapCommand.SpentAmount: the server owns no gun simulation, so it cannot verify the number,
        /// only cap it at what a legitimate reload of the loaded magazine could have produced. Everything else
        /// here is cosmetic or is itself validated elsewhere (fitting an attachment still has to spend the item
        /// through CommandFitAttachment -- this only records which slot the client says is filled).</summary>
        void OnGunState(ushort sender, GunStateCommand cmd)
        {
            var inv = SenderInventory(sender);
            var page = inv?.items[cmd.Page];
            byte index = page?.getIndex(cmd.X, cmd.Y) ?? byte.MaxValue;
            var jar = index == byte.MaxValue ? null : page.getItem(index);
            if (jar?.item == null || jar.item.id != cmd.Id) { Diag.GunStatesRejected++; return; }

            var gun = Assets.find(cmd.Id);
            if (gun?.gunName == null) { Diag.GunStatesRejected++; return; }   // only a gun has gun state

            // Capacity of the magazine the client says is loaded, +1 for a chambered round. An unknown or
            // unloaded mag falls back to the gun's own Ammo_Max, which is what CapForMag does client-side.
            var mag = cmd.MagId > 0 && cmd.MagId <= ushort.MaxValue ? Assets.find((ushort)cmd.MagId) : null;
            int cap = (mag != null && mag.magOverridesCapacity && mag.magCapacity > 0)
                ? mag.magCapacity                                  // a reservoir mag (the 100-round drum) overrides the gun
                : gun.gunAmmoMax;
            // gunAmmoMax is 0 when the gun's .dat did not parse, and clamping to 0 would confiscate the player's
            // magazine over a content problem. Fall back to the loaded mag's own capacity, then to leaving the
            // count alone -- a missing catalogue entry is a reason to stop clamping, not a reason to punish.
            if (cap <= 0) cap = mag != null && mag.magCapacity > 0 ? mag.magCapacity : int.MaxValue - 1;
            if (cmd.Chambered) cap += 1;

            var item = jar.item;
            item.gunAmmo = (int)Mathf.Clamp(cmd.Ammo, -1, cap);   // -1 stays meaningful: "this gun has never been held"
            item.gunChambered = cmd.Chambered;
            item.gunFiremode = cmd.Firemode;
            item.gunMagId = cmd.MagId;
            item.gunAttach = cmd.Attach;
            item.gunSightId = cmd.Sight;
            item.gunBarrelId = cmd.Barrel;
            item.gunGripId = cmd.Grip;
            item.gunTacticalId = cmd.Tactical;
            item.gunAttachSeeded = cmd.AttachSeeded;
            // The chambered round's type is re-derived from the loaded mag on the client's side of ReadJar; keep
            // the server's copy consistent with what it will send, rather than leaving a stale string behind.
            item.gunChamberedType = cmd.Chambered && cmd.MagId > 0 && cmd.MagId <= ushort.MaxValue
                ? Assets.find((ushort)cmd.MagId)?.ammoType : null;
            Diag.GunStatesApplied++;
            page.raiseStateUpdated();   // the echo only re-sends a page it knows changed
        }

        void OnFitAttachment(ushort sender, FitAttachmentCommand cmd)
        {
            var inv = SenderInventory(sender);
            var page = inv.items[cmd.Page];
            byte index = page.getIndex(cmd.X, cmd.Y);
            var jar = index == byte.MaxValue ? null : page.getItem(index);
            if (jar?.item == null || jar.item.id != cmd.Id) { Diag.AttachFitsRejected++; return; }
            page.removeItem(index);
            Diag.AttachFitsApplied++;
        }

        /// <summary>RELOAD, server side: spend the chosen magazine and hand back the spent one.
        ///
        /// This existed only on the client before, as a bare removeItem + tryAddItem inside DoMagSwap -- so the
        /// server's grid never changed and the next owner echo put the spare magazine BACK, at full, while the
        /// partially-spent one it had returned vanished. One spare magazine reloaded forever. ConsumeShells and
        /// the shotgun unload had the same shape. Review 2026-08-16.</summary>
        void OnReloadSwap(ushort sender, ReloadSwapCommand cmd)
        {
            var inv = SenderInventory(sender);
            var page = inv.items[cmd.Page];
            byte index = page.getIndex(cmd.X, cmd.Y);
            var jar = index == byte.MaxValue ? null : page.getItem(index);
            var asset = jar?.item != null ? Assets.find(jar.item.id) : null;
            // Must actually be a magazine OR A LOOSE AMMO STACK sitting where the client says it is. A stale
            // address is the ordinary case (the bag moved under the reload), not an attack, so it is a quiet
            // reject.
            //
            // AMMO ADDED 2026-09-09. A shell-fed gun (the ACE, the mosin -- anything whose Magazine item is
            // isAmmo) spends ROUNDS out of a stack, and this same intent already expresses that exactly: remove
            // the stack, hand back what is left. Refusing anything without magCapacity meant the shell path had
            // no wire at all, so it edited the bag locally and the next owner echo undid it -- the round came
            // back while the gun kept the +1, which is an infinite-ammo dupe rather than a cosmetic desync
            // (strawberry: "the bullet isnt consumed and i gain +1 in my mag").
            if (asset == null || (asset.magCapacity <= 0 && !asset.isAmmo)) { Diag.ReloadsRejected++; return; }
            page.removeItem(index);
            // Give the spent magazine back, CLAMPED to what that magazine can physically hold. SpentAmount is the
            // one number only the client knows (no gun state is replicated), so this is the cheat surface: the
            // clamp bounds it at "a full magazine back" rather than an arbitrary stack.
            if (cmd.SpentId != 0)
            {
                var spent = Assets.find(cmd.SpentId);
                // Clamped by what that item can physically hold: a magazine by its capacity, a loose round by its
                // stack size. Ammo returns through this path too now, and clamping ammo against magCapacity would
                // give a hard 0 -- the give-back would silently vanish while the spend still happened.
                int cap = spent == null ? 0
                        : spent.magCapacity > 0 ? spent.magCapacity
                        : spent.isAmmo ? System.Math.Max(1, spent.stackSize)
                        : 0;
                if (cap > 0)
                {
                    byte amt = (byte)System.Math.Min(cmd.SpentAmount, cap);
                    if (amt > 0) inv.tryAddItem(new Item(cmd.SpentId, amt, 100));
                }
            }
            Diag.ReloadsApplied++;
        }

        /// <summary>UNLOAD, server side: loose rounds out of a gun and into the bag.
        ///
        /// The one handler here that does NOT have to take the client's word for the count. The server has held
        /// Item.gunAmmo since v16, so it checks the gun is really carrying what is claimed, takes it off the gun
        /// FIRST, and only then pays out -- an over-claim is refused outright rather than clamped, because
        /// clamping a number the server can actually verify is just a slower way of trusting it.
        ///
        /// The round must also match the gun's caliber, or "unload" becomes "convert my ammo into anything".</summary>
        void OnGunUnload(ushort sender, GunUnloadCommand cmd)
        {
            var inv = SenderInventory(sender);
            var page = inv?.items[cmd.Page];
            byte index = page?.getIndex(cmd.X, cmd.Y) ?? byte.MaxValue;
            var jar = index == byte.MaxValue ? null : page.getItem(index);
            var gun = jar?.item != null ? Assets.find(jar.item.id) : null;
            if (gun?.gunName == null || cmd.Count == 0) { Diag.ReloadsRejected++; return; }

            var round = Assets.find(cmd.RoundId);
            // Loose ammo of THIS gun's caliber, and nothing else.
            // gunCaliber, not magCaliber: magCaliber is a MAGAZINE's field and reads 0 on a gun, so comparing the
            // two would have refused every honest unload while looking like a real check.
            if (round == null || !round.isAmmo || gun.gunCaliber <= 0 || round.magCaliber != gun.gunCaliber) { Diag.ReloadsRejected++; return; }
            if (jar.item.gunAmmo < cmd.Count) { Diag.ReloadsRejected++; return; }   // it cannot give up what it is not holding

            jar.item.gunAmmo -= cmd.Count;
            // tryAddItem raises the page's own state-updated event, which is what drives the owner echo -- the
            // gunAmmo write above is a field poke that raises nothing on its own, so it rides that.
            inv.tryAddItem(new Item(cmd.RoundId, cmd.Count, 100));
            Diag.ReloadsApplied++;
        }

        /// <summary>WEAR, server side: grid -> worn slot, with the displaced garment going back to the grid.
        ///
        /// Doing this locally only (InventoryUI.WearFromGrid) meant the server never learned the player was
        /// wearing anything: the echo put the backpack back in the bag and re-sized its page to 0x0, so a dragged
        /// on backpack un-equipped itself a moment later. Review 2026-08-16.</summary>
        void OnWearClothing(ushort sender, WearClothingCommand cmd)
        {
            var inv = SenderInventory(sender);
            if (cmd.Page >= inv.items.Length) { Diag.ClothingRejected++; return; }
            var page = inv.items[cmd.Page];
            byte index = page.getIndex(cmd.X, cmd.Y);
            var jar = index == byte.MaxValue ? null : page.getItem(index);
            var asset = jar?.item != null ? Assets.find(jar.item.id) : null;
            var want = (EItemType)cmd.Slot;
            if (asset == null || asset.type != want || !IsClothingType(want)) { Diag.ClothingRejected++; return; }
            var item = jar.item;
            var old = WornIn(inv, want);
            if (ReferenceEquals(old, item)) { Diag.ClothingRejected++; return; }
            page.removeItem(index);
            Wear(inv, want, item);
            // The displaced garment goes back into the grid. Deliberately AFTER the wear, because wearing a bag
            // resizes its page and the old one may only fit in the new geometry.
            if (old != null && !inv.tryAddItem(old))
            {
                // Nowhere to put it: undo rather than delete a garment. Same rule as MoveTo's restore.
                Wear(inv, want, old);
                page.tryAddItem(item);
                Diag.ClothingRejected++;
                return;
            }
            Diag.ClothingApplied++;
        }

        /// <summary>UNWEAR, server side: worn slot -> grid.</summary>
        /// <summary>Put `it` exactly where the client asked, if that cell is free and it fits. False for an
        /// unaddressed request (page 255) or a spot that is taken -- the caller then falls back to its usual
        /// search, so a stale address costs the player nothing worse than the old behaviour.</summary>
        static bool TryPlaceAt(PlayerInventory inv, byte page, byte x, byte y, Item it)
        {
            if (inv == null || it == null || page >= PlayerInventory.PAGES) return false;
            var pg = inv.items[page];
            if (pg == null || pg.width == 0 || pg.height == 0) return false;
            var a = it.GetAsset();
            byte sx = a?.size_x ?? 1, sy = a?.size_y ?? 1;
            if (!pg.checkSpaceEmpty(x, y, sx, sy, 0)) return false;
            pg.addItem(x, y, 0, it);
            return true;
        }

        void OnUnwearClothing(ushort sender, UnwearClothingCommand cmd)
        {
            var inv = SenderInventory(sender);
            var want = (EItemType)cmd.Slot;
            if (!IsClothingType(want)) { Diag.ClothingRejected++; return; }
            var old = WornIn(inv, want);
            if (old == null) { Diag.ClothingRejected++; return; }

            // EMPTY THE GARMENT BEFORE TAKING IT OFF. Wear(.., null) resizes the storage page to 0x0 and
            // Items.loadSize drops every jar that no longer fits, so unwearing a full backpack DESTROYED its
            // contents on this path -- including when the removal was then REJECTED for lack of room, which
            // re-wore an emptied bag and left the player looking like nothing had happened.
            //
            // The singleplayer path (InventoryUI.UnwearTo) has done this correctly since 2026-09-03: pull the
            // jars out first, unwear, then find each one a home and drop what does not fit. This is the same
            // sequence server-side. One more instance of the SP/MP seam -- the local path was fixed and the
            // authoritative one kept the old behaviour, so the bug only showed in multiplayer.
            byte spillPage = want switch
            {
                EItemType.BACKPACK => PlayerInventory.BACKPACK, EItemType.VEST => PlayerInventory.VEST,
                EItemType.SHIRT => PlayerInventory.SHIRT, EItemType.PANTS => PlayerInventory.PANTS,
                _ => byte.MaxValue,
            };
            var spill = new List<Item>();
            if (spillPage != byte.MaxValue && spillPage < inv.items.Length)
            {
                var pg = inv.items[spillPage];
                for (int i = pg.getItemCount() - 1; i >= 0; i--)
                {
                    var j = pg.getItem((byte)i);
                    if (j?.item != null) spill.Add(j.item);
                    pg.removeItem((byte)i);
                }
            }

            Wear(inv, want, null);
            // The cell the player dropped it on, if they named one and it is free; else the old find-anywhere.
            // Checked AFTER the wear is cleared, because the garment's own page has just gone 0x0 and a shirt
            // dropped onto its own vacated space is a legal target.
            if (!TryPlaceAt(inv, cmd.Page, cmd.X, cmd.Y, old) && !inv.tryAddItem(old))
            {
                // No room for the garment: put it back ON, and put its contents back IN. Restoring the garment
                // alone is what turned a rejected request into permanent item loss.
                Wear(inv, want, old);
                if (spillPage != byte.MaxValue && spillPage < inv.items.Length)
                {
                    var pg = inv.items[spillPage];
                    foreach (var it in spill) if (!pg.tryAddItem(it)) DropAtSender(sender, it);
                }
                else foreach (var it in spill) DropAtSender(sender, it);
                Diag.ClothingRejected++;
                return;
            }

            // The garment is off and in the grid. Everything that was inside it now looks for a page of its
            // own, and anything that does not fit lands at the player's feet rather than ceasing to exist --
            // the same fate ReturnToGrid gives it in singleplayer.
            foreach (var it in spill) if (!inv.tryAddItem(it)) DropAtSender(sender, it);
            Diag.ClothingApplied++;
        }

        /// <summary>Put an item on the ground at the sender, for the overflow that has nowhere left to go.
        /// Deleting it instead is silent and permanent, which is the failure this exists to avoid.</summary>
        void DropAtSender(ushort sender, Item it)
        {
            Vector3 at = TryGetSenderPos(sender, out var pos) ? pos + Vector3.up * 0.5f : Vector3.zero;
            SpawnWorldItem(it, at, Vector3.zero);
        }

        static bool IsClothingType(EItemType t)
            => t is EItemType.HAT or EItemType.GLASSES or EItemType.MASK or EItemType.SHIRT
                 or EItemType.VEST or EItemType.BACKPACK or EItemType.PANTS;

        static Item WornIn(PlayerInventory inv, EItemType t) => t switch
        {
            EItemType.HAT => inv.wornHat, EItemType.GLASSES => inv.wornGlasses, EItemType.MASK => inv.wornMask,
            EItemType.SHIRT => inv.wornShirt, EItemType.VEST => inv.wornVest,
            EItemType.BACKPACK => inv.wornBackpack, EItemType.PANTS => inv.wornPants, _ => null,
        };

        static void Wear(PlayerInventory inv, EItemType t, Item item)
        {
            switch (t)
            {
                case EItemType.HAT: inv.wearHat(item); break;
                case EItemType.GLASSES: inv.wearGlasses(item); break;
                case EItemType.MASK: inv.wearMask(item); break;
                case EItemType.SHIRT: inv.wearShirt(item); break;
                case EItemType.VEST: inv.wearVest(item); break;
                case EItemType.BACKPACK: inv.wearBackpack(item); break;
                case EItemType.PANTS: inv.wearPants(item); break;
            }
        }

        // CANCEL a queued craft. There was no way to do this on the wire at all until now, which mattered more
        // than it sounds: the client's queue tiles are clickable in MP too, and that click ran the SINGLE-PLAYER
        // cancel path against a job whose PerUnit list is null (AdoptServerQueue cannot know what the server
        // spent), so it threw inside the gui handler and the tile just sat there. The server kept crafting.
        void OnCraftCancel(ushort sender, CraftCancelCommand cmd)
        {
            if (Crafting_ == null) { Diag.CraftCancelsRejected++; return; }
            if (Crafting_.Cancel(sender, cmd.Slot)) Diag.CraftCancelsApplied++; else Diag.CraftCancelsRejected++;
        }

        void OnConsume(ushort sender, ConsumeCommand cmd)
        {
            var inv = SenderInventory(sender);
            var page = inv.items[cmd.Page];
            byte index = page.getIndex(cmd.X, cmd.Y);
            var jar = index == byte.MaxValue ? null : page.getItem(index);
            var asset = jar?.item != null ? Assets.find(jar.item.id) : null;
            if (asset == null || !asset.IsConsumable) { Diag.ConsumesRejected++; return; }
            // "frozen food cannot be eaten until thawed" (strawberry 2026-09-06). Rejected HERE, before anything
            // is spent or applied, because this is the side that owns the outcome -- the client's own gate below
            // is only there to stop the eat animation from playing on something that will bounce.
            if (Freezing.IsFrozen(jar.item)) { Diag.ConsumesRejected++; return; }
            // SPEND THE JAR WE JUST VALIDATED, not "some item with this id somewhere in the bag". This used to be
            // `inv.removeItemAmount(asset.id, 1)`, which scans pages 0..OWNPAGES only -- so eating out of an OPEN
            // CRATE (page 7, which ServerOpenStorage really does populate with the crate grid) validated fine,
            // applied every health/food/water/energy effect, and removed NOTHING. Carry no beans of your own and
            // a crate full of them feeds you forever. Removing at the validated address also fixes the quieter
            // half: even in the bag it was deleting an arbitrary same-id instance rather than the clicked one,
            // which is wrong once instances carry state (quality, fluid contents). Review 2026-08-16.
            if (jar.item.amount > 1) { jar.item.amount--; _inventories.ServerMarkDirty(sender); }
            else page.removeItem(index);   // removeItem raises onStateUpdated, which dirties the entry itself
            Diag.ConsumesApplied++;

            // HP stays the coarse-combat authority; the useHealth bump raises it directly (as before).
            if (asset.useHealth > 0 && _combat.TryGet(sender, out var ce) && ce.Alive)
            {
                ce.HealthExact = Mathf.Min(100f, ce.HealthExact + asset.useHealth);
                ce.Health = (byte)System.Math.Clamp((int)System.Math.Ceiling(ce.HealthExact), 0, 100);   // review L11: Ceiling, matching ApplyPlayerDamage/RegenSink (NetWorldHost:144, ServerCombat:537) -- was RoundToInt (a 2nd quantization of the same field)
                _combat.MarkDirty(ce, _tick());
            }
            // B5 (SP/MP-unify): the previously-stubbed fine-vitals effects now land on the server sim -- the
            // .dat 0-100 values map to the port's 0..1 vitals (÷100). Virus RAISES infection, Disinfectant
            // LOWERS it; Energy restores stamina. The owner-block echo re-adopts them onto the shell.
            _vitals?.ServerRaise(sender,
                asset.useFood / 100f, asset.useWater / 100f, asset.useEnergy / 100f,
                (asset.useVirus - asset.useDisinfectant) / 100f,
                asset.useStopsBleeding, asset.useHealBroken, _tick());
        }

        void OnPlantCrop(ushort sender, PlantCropCommand cmd)
        {
            var inv = SenderInventory(sender);
            if (!SpendAt(inv, cmd.Page, cmd.X, cmd.Y, cmd.SeedId, sender)) SpendAnyOf(inv, cmd.SeedId, sender);   // the seed is spent
            PlantCrop(cmd.SeedId, cmd.Pos, grown: false);
        }

        /// <summary>Server-side crop plant + its broadcast fact (remote Plant commands and the loopback
        /// world's locally-planted crops both funnel here). Null if the seed isn't in the schema.</summary>
        public CropReplication.CropEntity PlantCrop(ushort seedId, Vector3 pos, bool grown)
        {
            var e = _crops.ServerPlant(_ids.Mint(), seedId, pos, _tick(), grown);
            if (e == null) return null;
            var evt = new CropPlantedEvent { NetId = e.NetIdValue, SeedId = e.SeedId, Pos = e.Pos,
                                             PlantedAtTick = (uint)e.PlantedAtTick, Grown = e.Grown };
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventCropPlanted, evt.Write));
            return e;
        }

        void OnHarvestCrop(ushort sender, HarvestCropCommand cmd)
        {
            _crops.TryGet(cmd.NetId, out var e);
            _crops.Schema.TryGet(e.SeedId, out var def);
            if (!RemoveCrop(cmd.NetId)) return;

            // yield drops at the crop like SP (CropManager.Harvest), spawned as replicated world items
            var at = e.Pos + new Vector3(0f, 0.3f, 0f);
            if (def.YieldItemId != 0)
            {
                SpawnWorldItem(new Item(def.YieldItemId), at, Vector3.zero);
                // AGRICULTURE second-yield roll (source InteractableFarm): chance = mastery, rolled HERE --
                // the server owns the roll (§3.7); SP's GD.Randf stays on the direct path only.
                float mastery = _skills.TryGet(sender, out var se)
                    ? se.Skills.GetSkill((int)EPlayerSpeciality.SUPPORT, (int)EPlayerSupport.AGRICULTURE).Mastery : 0f;
                if (mastery > 0f && Rand() < mastery)
                    SpawnWorldItem(new Item(def.YieldItemId), at + new Vector3(0.25f, 0f, 0f), Vector3.zero);
            }
            AwardXp(sender, HarvestRewardExperience);   // source: harvest awards Harvest_Reward_Experience
        }

        /// <summary>Pick a berry bush or a mushroom. The whole transaction is server-side: the client named
        /// an index and nothing else, and every other fact -- what it gives, whether it is even forageable,
        /// whether it is still standing, whether the asker is near it -- is read here.
        ///
        /// Retail (ReceiveForageRequest) does `askDamage(1)` on a Health-1 resource, which is its way of
        /// saying "one interaction takes it"; the port has no health pool for resources, so taking it IS the
        /// damage. The reward goes straight into the BAG (retail forceAddItem(auto:true)) rather than onto
        /// the ground like a crop's yield -- you are picking a berry, not felling something that scatters --
        /// and only overflows to the ground when there is no room for it.
        ///
        /// The AGRICULTURE second-yield roll is the same one OnHarvestCrop makes, from the same skill, rolled
        /// on the server for the same reason: a client that rolls its own mastery always wins it.</summary>
        void OnForageResource(ushort sender, ForageResourceCommand cmd)
        {
            if (Forage == null) return;
            if (!TryGetSenderPos(sender, out var pos)) return;
            ushort reward = Forage.Take(cmd.Index, pos, _tick());
            if (reward == 0) return;                       // refused: not forageable, already picked, or out of reach
            if (!SetResourceAlive(cmd.Index, false)) return;   // one writer owns the alive bit + its broadcast

            // A FULL BAG STILL TAKES THE PLANT, and what will not fit is left lying at the plant. Retail's
            // forceAddItem drops the overflow the same way, and the alternative is worse: refusing after the
            // alive bit has flipped needs that flip undone, and refusing before it hands a player with one
            // free slot a way to probe the reach check for free.
            var inv = SenderInventory(sender);
            var at = Forage.Position(cmd.Index) + new Vector3(0f, 0.3f, 0f);
            GiveOrDrop(inv, reward, at);

            // The AGRICULTURE second-yield roll, same skill and same server-side roll as OnHarvestCrop.
            float mastery = _skills.TryGet(sender, out var se)
                ? se.Skills.GetSkill((int)EPlayerSpeciality.SUPPORT, (int)EPlayerSupport.AGRICULTURE).Mastery : 0f;
            if (mastery > 0f && Rand() < mastery) GiveOrDrop(inv, reward, at + new Vector3(0.25f, 0f, 0f));

            AwardXp(sender, ServerForage.RewardExperience);   // source: Forage_Reward_Experience, default 1
        }

        /// <summary>One picked item into the bag, or onto the ground at `at` when there is no room for it.</summary>
        void GiveOrDrop(PlayerInventory inv, ushort itemId, Vector3 at)
        {
            if (inv != null && inv.tryAddItem(new Item(itemId))) return;
            SpawnWorldItem(new Item(itemId), at, Vector3.zero);
        }

        /// <summary>Server-side crop removal + its broadcast fact. Idempotent -- false if already gone.</summary>
        public bool RemoveCrop(uint netId)
        {
            if (!_crops.ServerRemove(netId, _tick())) return false;
            var evt = new CropHarvestedEvent { NetId = netId };
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventCropHarvested, evt.Write));
            return true;
        }

        /// <summary>Resource (tree) alive-bit flip + its broadcast fact (§3.7). No game mechanic fells
        /// trees yet (SP has none either) -- this is the authoritative entry point for when one lands.</summary>
        public bool SetResourceAlive(int index, bool alive)
        {
            if (_resources == null || !_resources.ServerSetAlive(index, alive, _tick())) return false;
            if (alive)
            {
                var evt = new ResourceRespawnedEvent { Index = (ushort)index };
                _broadcast(NetMessagePak.Pack(ReplicationIds.EventResourceRespawned, evt.Write));
            }
            else
            {
                var evt = new ResourceHarvestedEvent { Index = (ushort)index };
                _broadcast(NetMessagePak.Pack(ReplicationIds.EventResourceHarvested, evt.Write));
            }
            return true;
        }

        /// <summary>A player said something in global chat.
        ///
        /// The SENDER is the peer this arrived on. The command carries no speaker field on purpose: an id
        /// on the wire is a licence to speak as anyone, including as the server.</summary>
        void OnChatSend(ushort sender, ChatSendCommand cmd)
        {
            // Sanitise FIRST, then judge the result. Rejecting on the raw text lets invisible padding
            // count against a message that renders as nothing.
            string text = ChatRules.Sanitize(cmd.Text);
            if (text.Length == 0) { Diag.ChatRejected++; return; }   // nothing to say; silently dropped

            double now = NowSeconds != null ? NowSeconds() : 0.0;
            var verdict = ChatLimiter.Check(sender, now);
            if (verdict != ChatVerdict.Ok)
            {
                Diag.ChatRejected++;
                // Tell only the sender, and as a SERVER line so it cannot be mistaken for someone talking.
                SendServerLineTo(sender, ChatRateLimiter.Explain(verdict));
                return;
            }
            ChatLimiter.Record(sender, now);
            Diag.ChatSent++;

            string name = NameOf?.Invoke(sender);
            if (string.IsNullOrEmpty(name)) name = ProfileRules.FallbackName;
            var evt = new ChatMessageEvent
            {
                Channel = (byte)ChatChannel.Global,
                SpeakerId = sender,
                Name = ChatRules.Sanitize(name),
                Text = text,
            };
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventChatMessage, evt.Write));
        }

        /// <summary>Post a line as the SERVER, to everyone. Never rate limited -- a kick notice has to go
        /// out even when the console is busy.</summary>
        public void SayAsServer(string text)
        {
            string clean = ChatRules.Sanitize(text);
            if (clean.Length == 0) return;
            var evt = new ChatMessageEvent { Channel = (byte)ChatChannel.Server, SpeakerId = 0, Name = "", Text = clean };
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventChatMessage, evt.Write));
            Diag.ChatSent++;
        }

        /// <summary>A server line to ONE peer -- rate-limit notices, command feedback.</summary>
        public void SendServerLineTo(ushort playerId, string text)
        {
            string clean = ChatRules.Sanitize(text);
            if (clean.Length == 0) return;
            var evt = new ChatMessageEvent { Channel = (byte)ChatChannel.Server, SpeakerId = 0, Name = "", Text = clean };
            _sendTo(playerId, NetMessagePak.Pack(ReplicationIds.EventChatMessage, evt.Write));
        }

        /// <summary>Resolve a console argument to a connected player: an exact player id, or a name matched
        /// case-insensitively. Refuses an AMBIGUOUS name rather than picking one -- kicking the wrong person
        /// because two names share a prefix is worse than making the admin retype it.</summary>
        bool TryFindPlayer(string who, out ushort id, out string name)
        {
            id = 0; name = "";
            if (string.IsNullOrWhiteSpace(who) || NameOf == null) return false;

            if (ushort.TryParse(who, out ushort direct))
            {
                string n = NameOf(direct);
                if (!string.IsNullOrEmpty(n)) { id = direct; name = n; return true; }
            }

            int hits = 0;
            var roster = ConnectedPlayers?.Invoke();
            if (roster == null) return false;
            foreach (var pid in roster)
            {
                string n = NameOf(pid);
                if (string.IsNullOrEmpty(n)) continue;
                if (!ServerModeration.NameMatches(n, who)) continue;
                hits++; id = pid; name = n;
            }
            if (hits == 1) return true;
            id = 0; name = "";
            return false;   // 0 = nobody, 2+ = ambiguous; both are "no match" to the caller
        }

        void OnConsole(ushort sender, ConsoleCommand cmd)
        {
            string reply = RunConsole(sender, cmd.Text ?? "");
            var evt = new ConsoleResultEvent { Text = reply };
            _sendTo(sender, NetMessagePak.Pack(ReplicationIds.EventConsoleResult, evt.Write));
        }

        /// <summary>The server-gated DevConsole verbs (§2.3: "one server-side validation choke point. No
        /// client ever writes authoritative state directly" -- including cheats). Returns the result line.</summary>
        public string RunConsole(ushort sender, string text)
        {
            var parts = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) { Diag.ConsoleRejected++; return "usage: give <item> | xp <n> | skill <name> [level] | teleport <x> <y> <z>"; }
            string verb = parts[0].ToLowerInvariant();
            string arg = parts.Length > 1 ? parts[1].Trim() : "";

            // A3 (SP/MP-unify): the grid mains toggle (F1 `toggleGlobalPower`) flips every GridSource fixture's
            // replicated ToggledOn bit (the mains-on state, produce-while-on) and broadcasts the toggled fact on
            // the EXISTING deployable-toggled wire; the client's DeployableReplicaView derives the source node's
            // producing from ToggledOn (never the local PowerNet.GlobalPower), and both sides' Solve() then
            // energize the wired consumers. Bare form flips the current mains state.
            // review M7: this is a SERVER-WIDE state mutation (it flips power for everyone), so it must respect the
            // AllowCheats gate exactly like give/xp/skill -- otherwise any client could grief a cheats-locked
            // server's grid. SP + friendly co-op run cheats-ON (Main.cs:2717 defaults --dedicated cheats-on too),
            // so strawberry's F1 mechanic still works everywhere it does today; only a UG_DEDICATED_NOCHEATS lockdown
            // now (correctly) blocks it.
            if (verb == "toggleglobalpower" || verb == "globalpower" || verb == "grid")
            {
                if (!AllowCheats) { Diag.ConsoleRejected++; return "console commands are disabled on this server"; }
                string g = arg.ToLowerInvariant();
                bool? want = g == "on" || g == "1" || g == "true" ? true
                           : g == "off" || g == "0" || g == "false" ? false
                           : (bool?)null;
                bool anyOn = false;
                foreach (var e in _deployables.All)
                    if (_deployables.Schema.TryGet(e.DefId, out var d) && d.FixtureKind == FixtureKind.GridSource && e.ToggledOn) { anyOn = true; break; }
                bool on = want ?? !anyOn;
                int n = 0;
                foreach (var e in _deployables.All)
                {
                    if (!_deployables.Schema.TryGet(e.DefId, out var d) || d.FixtureKind != FixtureKind.GridSource) continue;
                    n++;
                    if (_deployables.ServerToggle(e.NetIdValue, on, _tick()))
                    {
                        var evt = new DeployableToggledEvent { NetId = e.NetIdValue, On = on };
                        _broadcast(NetMessagePak.Pack(ReplicationIds.EventDeployableToggled, evt.Write));
                    }
                }
                Diag.ConsoleApplied++;
                return $"grid power {(on ? "ON" : "OFF")} ({n} source{(n == 1 ? "" : "s")})";
            }

            if (!AllowCheats) { Diag.ConsoleRejected++; return "console commands are disabled on this server"; }

            // WIPE (master 2026-09-03: "can be reset with a wipe command through the server control"). Deletes
            // the save and forgets it, so the world comes back fresh on the next restart. It deliberately does
            // NOT tear down the LIVE world out from under connected players: removing every deployable, wire,
            // dropped item and crop at runtime means a removal event per object to every peer, and a half-sent
            // teardown is a desync rather than a wipe. So the verb says exactly what it did and what is still
            // standing -- a wipe that silently left the current world in place would be the worse failure.
            // Sits BELOW the AllowCheats gate on purpose: it is the most destructive verb here.
            if (verb == "save")
            {
                if (SaveNowHandler == null) { Diag.ConsoleRejected++; return "no save is configured on this server"; }
                string r = SaveNowHandler();
                Diag.ConsoleApplied++;
                return r;
            }

            if (verb == "wipe")
            {
                if (WipeSaveHandler == null) { Diag.ConsoleRejected++; return "no save is configured on this server"; }
                string result = WipeSaveHandler();
                Diag.ConsoleApplied++;
                return result;
            }

            // ---- v49 moderation ---------------------------------------------------------------------
            // ⚠ GATED ON AllowCheats, AND THAT IS THE WRONG GATE LONG TERM. There is no admin concept in
            // this port -- no roles, no owner id, nothing that distinguishes one connected peer from
            // another. So "who may kick" has no correct answer yet, and the only safe available one is the
            // gate every other server-wide mutation already uses. The consequence is real and should not be
            // discovered later: a cheats-locked server cannot kick anybody. When an admin tier exists,
            // these three move behind it and come OUT from behind AllowCheats.
            if (verb == "say")
            {
                if (arg.Length == 0) { Diag.ConsoleRejected++; return "usage: say <message>"; }
                SayAsServer(arg);
                Diag.ConsoleApplied++;
                return "said";
            }

            if (verb == "kick" || verb == "ban")
            {
                if (KickHandler == null) { Diag.ConsoleRejected++; return "no transport is configured on this server"; }
                var a = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (a.Length == 0)
                    return verb == "kick" ? "usage: kick <player> [duration] [reason]   (10m, 2h, 3d; bare number = minutes)"
                                          : "usage: ban <player> [duration|perm] [reason]   (default permanent)";

                if (!TryFindPlayer(a[0], out ushort target, out string targetName))
                { Diag.ConsoleRejected++; return $"no connected player matches '{a[0]}'"; }

                // Duration is OPTIONAL and the defaults differ on purpose: a kick with no duration is a
                // boot with no ban, a ban with no duration is permanent. Parsing decides which -- an
                // unparseable second word is treated as the start of the REASON rather than rejected,
                // because "kick griefer stop building there" should not be a usage error.
                int i = 1;
                long seconds = 0; bool permanent = verb == "ban"; bool timed = false;
                if (a.Length > 1 && ServerModeration.TryParseDuration(a[1], out long secs, out bool perm))
                { seconds = secs; permanent = perm; timed = !perm; i = 2; }
                string reason = string.Join(' ', a, i, a.Length - i).Trim();

                long nowUnix = NowSeconds != null ? (long)NowSeconds() : 0L;
                string window = ServerModeration.DescribeDuration(seconds, permanent);

                // Record BEFORE disconnecting. The other order loses the identity: once the peer is gone
                // IdentityOf cannot answer, and the ban would be stored with no address to match on.
                if (verb == "ban" || timed)
                {
                    var id = IdentityOf?.Invoke(target);
                    uint ip = id?.ipv4 ?? 0u;
                    string nm = id?.name ?? targetName;
                    long expires = permanent ? 0L : nowUnix + seconds;
                    Moderation.Add(ip, nm, expires, reason, verb == "ban" ? ModerationKind.Ban : ModerationKind.Kick);
                    BansChanged?.Invoke();
                }

                string notice = reason.Length > 0
                    ? $"{targetName} was {(verb == "ban" ? "banned" : "kicked")} {window}: {reason}"
                    : $"{targetName} was {(verb == "ban" ? "banned" : "kicked")} {window}";
                KickHandler(target, notice);
                SayAsServer(notice);   // everyone sees it, so a disappearance is never a mystery
                Diag.ConsoleApplied++;
                return notice;
            }

            if (verb == "unban")
            {
                if (arg.Length == 0) { Diag.ConsoleRejected++; return "usage: unban <name>"; }
                int n = Moderation.Remove(0u, arg);
                if (n > 0) BansChanged?.Invoke();
                Diag.ConsoleApplied++;
                return n > 0 ? $"lifted {n} ban entr{(n == 1 ? "y" : "ies")} matching '{arg}'"
                             : $"no ban matches '{arg}'";
            }

            if (verb == "bans")
            {
                long nowUnix = NowSeconds != null ? (long)NowSeconds() : 0L;
                Moderation.Prune(nowUnix);
                if (Moderation.Count == 0) return "no active bans";
                var sb = new System.Text.StringBuilder();
                foreach (var e in Moderation.Entries)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    string left = e.IsPermanent ? "permanent" : ServerModeration.DescribeDuration(e.ExpiresUnix - nowUnix, false).Replace("for ", "") + " left";
                    sb.Append(string.IsNullOrEmpty(e.Name) ? "(unnamed)" : e.Name).Append(" -- ").Append(left);
                    if (!string.IsNullOrEmpty(e.Reason)) sb.Append(" (").Append(e.Reason).Append(')');
                }
                return sb.ToString();
            }

            if (verb == "give" && arg.Length > 0)
            {
                var asset = ResolveItem(arg);
                if (asset == null) { Diag.ConsoleRejected++; return $"no item matching '{arg}'"; }
                var item = Assets.makeLoot(asset.id);
                var inv = SenderInventory(sender);
                if (inv == null) { Diag.ConsoleRejected++; return "no inventory"; }
                Diag.ConsoleApplied++;
                if (inv.tryAddItem(item)) return $"gave {asset.itemName} (#{asset.id}) -> bag";
                _players.TryGetByOwner(sender, out var p);
                SpawnWorldItem(item, (p?.Pos ?? Vector3.zero) + new Vector3(0f, 2f, 0f), Vector3.zero);
                return $"gave {asset.itemName} (#{asset.id}) -> dropped at your feet";
            }
            if (verb == "xp" && uint.TryParse(arg.Split(' ')[0], out uint amount))
            {
                if (!_skills.TryGet(sender, out _)) { Diag.ConsoleRejected++; return "no skills"; }
                Diag.ConsoleApplied++;
                uint total = AwardXp(sender, amount);
                return $"+{amount} XP (now {total})";
            }
            if (verb == "skill" && arg.Length > 0)
            {
                var pp = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int target = pp.Length > 1 && int.TryParse(pp[1], out int lv) ? lv : int.MaxValue;   // no level = max is SP's +1; server default = explicit
                if (target == int.MaxValue && _skills.TryGet(sender, out var se) && se.Skills.TryFind(pp[0], out var sk, out _))
                    target = sk.level + 1;   // bare `skill <name>` bumps one level, like the SP console
                if (!_skills.ServerSetSkillLevel(sender, pp[0], target, _tick(), out string label, out byte applied))
                { Diag.ConsoleRejected++; return $"no skill '{pp[0]}'"; }
                Diag.ConsoleApplied++;
                return $"{label} skill -> level {applied}";
            }
            if (verb == "teleport" || verb == "tp")
            {
                // #27 (mp-teleport): the wire form is NUMERIC -- this engine-free core has no map/location
                // table, so the CLIENT resolves the name (DevConsole/MapNodes) and sends coordinates.
                // ServerTeleport moves the authoritative entity; PlayerNetSync adopts it (body snaps to
                // entity) and the owner's reconciler snaps the shell onto the replicated spot -- the
                // client-local TeleportTo path is what snapped back (the entity never moved).
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                var tt = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tt.Length != 3
                    || !float.TryParse(tt[0], System.Globalization.NumberStyles.Float, ci, out float x)
                    || !float.TryParse(tt[1], System.Globalization.NumberStyles.Float, ci, out float y)
                    || !float.TryParse(tt[2], System.Globalization.NumberStyles.Float, ci, out float z)
                    || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))   // NaN/Infinity would poison the replicated pos; range is Quantize-clamped
                { Diag.ConsoleRejected++; return "usage: teleport <x> <y> <z>"; }
                if (!_players.TryGetByOwner(sender, out _)) { Diag.ConsoleRejected++; return "no player"; }
                if (IsSeated?.Invoke(sender) == true) { Diag.ConsoleRejected++; return "exit the vehicle first"; }
                _players.ServerTeleport(sender, new Vector3(x, y, z), _tick());
                Diag.ConsoleApplied++;
                return FormattableString.Invariant($"teleported to ({x:0.#}, {y:0.#}, {z:0.#})");
            }
            Diag.ConsoleRejected++;
            return $"unknown command '{verb}' -- give / xp / skill / teleport";
        }

        /// <summary>Server-computed XP award (the §3.2 hook: kills/harvests/crafts/console feed this).
        /// Fires the owner's XpAwarded HUD event and returns the new total.</summary>
        public uint AwardXp(ushort playerId, uint amount)
        {
            uint total = _skills.ServerAward(playerId, amount, _tick());
            var evt = new XpAwardedEvent { Amount = amount, TotalExperience = total };
            _sendTo(playerId, NetMessagePak.Pack(ReplicationIds.EventXpAwarded, evt.Write));
            return total;
        }

        /// <summary>Server-spawned world item + its broadcast fact (drop/salvage/loot all funnel here).</summary>
        public WorldItemReplication.WorldItemEntity SpawnWorldItem(Item item, Vector3 pos, Vector3 vel)
        {
            var e = _worldItems.ServerSpawn(_ids.Mint(), item, pos, _tick());
            e.ServerVel = vel;   // kept, not just broadcast: the game side needs it to give this entity a falling node
            e.ServerDropped = true;   // ...and THIS says it needs one at all. Only a real drop comes through here.
            var evt = new WorldItemSpawnedEvent { NetId = e.NetIdValue, ItemId = e.ItemId, Amount = e.Amount, Quality = e.Quality, Pos = e.Pos, Vel = vel };
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventWorldItemSpawned, evt.Write));
            return e;
        }

        /// <summary>Server-side world-item removal + its broadcast fact (pickup, despawn, node teardown).
        /// Idempotent -- false if the entity was already gone.</summary>
        public bool RemoveWorldItem(uint netId)
        {
            if (!_worldItems.ServerRemove(netId, _tick())) return false;
            var evt = new WorldItemRemovedEvent { NetId = netId };
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventWorldItemRemoved, evt.Write));
            return true;
        }

        /// <summary>The settled-transform fact (§3.3): the server's physics froze the item here.</summary>
        public void SettleWorldItem(uint netId, Vector3 pos)
        {
            if (!_worldItems.TryGet(netId, out var e) || e.Settled) return;
            _worldItems.ServerSettle(netId, pos, _tick());
            var evt = new WorldItemSettledEvent { NetId = netId, Pos = e.Pos };
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventWorldItemSettled, evt.Write));
        }

        // mirror of the SP DevConsole item resolution: numeric id, exact name, then shortest prefix
        static ItemAsset ResolveItem(string arg)
        {
            if (ushort.TryParse(arg, out ushort id)) return Assets.find(id);
            string squashed = arg.Replace(" ", "");
            ItemAsset best = null;
            foreach (var a in Assets.all())
            {
                if (string.IsNullOrEmpty(a.itemName)) continue;
                if (string.Equals(a.itemName, arg, StringComparison.OrdinalIgnoreCase)) return a;
                if (a.itemName.Replace(" ", "").StartsWith(squashed, StringComparison.OrdinalIgnoreCase)
                    && (best == null || a.itemName.Length < best.itemName.Length))
                    best = a;
            }
            return best;
        }

        /// <summary>The pickup facing-cone check (reach says the item is CLOSE; this says the player is
        /// LOOKING that way). Forward is derived from the wire yaw in the GODOT convention --
        /// (-sin yaw, 0, -cos yaw) -- because that is what PlayerEntity.YawDegrees actually holds: the
        /// shell sends RotationDegrees.Y verbatim and the production server's avatars ServerDrive it back
        /// unchanged (a Godot body at yaw 0 faces -Z; PlayerController maps sim-forward to local -Z).
        /// This same (-sin,0,-cos) frame is now used by ServerCombat.StepMelee and OnDropItem -- they
        /// carried an inverted (+sin,+cos) that hit/tossed BEHIND the attacker (StepMelee was LIVE; the
        /// review caught it), fixed alongside this.</summary>
        bool SenderFacingItem(ushort sender, Vector3 itemPos)
        {
            if (!_players.TryGetByOwner(sender, out var p)) return false;
            var flat = itemPos - p.Pos;
            flat.y = 0f;
            float dist = flat.magnitude;
            if (dist < PickupFacingSkipRange) return true;   // at-feet: bearing unstable, cone skipped
            float yawRad = p.YawDegrees * (Mathf.PI / 180f);
            var fwd = new Vector3(-Mathf.Sin(yawRad), 0f, -Mathf.Cos(yawRad));
            return Vector3.Dot(fwd, flat / dist) >= PickupFacingMinDot;
        }

        PlayerInventory SenderInventory(ushort sender) => _inventories.TryGet(sender, out var e) ? e.Inventory : null;

        /// <summary>v43 arrest hooks, so the bag and the effects stay the game layer's business and this file
        /// keeps owning only who is cuffed. Null in a bare host (the wire tests), which is why every call site
        /// is null-conditional rather than assuming a listener.</summary>
        /// <summary>The one arrest hook that genuinely needs the game layer: the metal clatter when the cuffs
        /// break (retail triggers a Metal_1 effect). Spending the restraint and handing it back are done HERE
        /// against the server's own inventory -- they are bag writes, and the bag is this file's business.</summary>
        public System.Action<ushort> ArrestBroke;                    // (escapee) -> the clatter; null on a bare host

        /// <summary>Retail UseableArrestStart's server check is `sqrMagnitude > 49` -- 7 m, around a 3 m client
        /// ray. Kept generous for the same reason the forage reach is: the server is guarding against a forged
        /// target id, not re-deciding what the client was allowed to aim at.</summary>
        const float ArrestReachSq = 49f;
        bool InArrestReach(ushort a, ushort b)
        {
            if (!_players.TryGetByOwner(a, out var pa) || !_players.TryGetByOwner(b, out var pb)) return false;
            return (pa.Pos - pb.Pos).sqrMagnitude <= ArrestReachSq;
        }

        /// <summary>Who is cuffed, for the game layer (a cuffed player cannot equip) and for tests.</summary>
        public bool IsArrested(ushort playerId) => _arrest.IsArrested(playerId);

        /// <summary>The AUTHORITATIVE inventory, for tests. Exposed deliberately: the magazine bug was that
        /// every client-side assertion passed while this object never changed, so a test that cannot read
        /// it cannot tell a working command from a no-op.</summary>
        public PlayerInventory InventoryForTest(ushort playerId) => SenderInventory(playerId);

        bool TryGetSenderPos(ushort sender, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (!_players.TryGetByOwner(sender, out var p)) return false;
            pos = p.Pos;
            return true;
        }
    }
}
