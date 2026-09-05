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
        public long ConsumesApplied;
        public long ConsumesRejected;
        public long AttachFitsApplied;
        public long AttachFitsRejected;     // empty cell / wrong item at that address (a stale client grid)
        public long MagLoadsApplied;        // one round moved into or out of a magazine
        public long MagLoadsRejected;       // stale slot, wrong item id, rule refused, or a full bag on unload
        public long PickupsDenied;          // legal pickup, full grid -> ItemPickupDenied went back
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
        readonly Func<long> _tick;
        readonly Action<byte[]> _broadcast;
        readonly Action<ushort, byte[]> _sendTo;

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

            commands.Register<ConsumeCommand>(ReplicationIds.CommandConsume, ConsumeCommand.TryRead,
                OnConsume,
                validate: (sender, cmd) => _inventories.TryGet(sender, out _) && cmd.Page < PlayerInventory.PAGES);

            commands.Register<MagLoadCommand>(ReplicationIds.CommandMagLoad, MagLoadCommand.TryRead,
                OnMagLoad,
                validate: (sender, cmd) => _inventories.TryGet(sender, out _)
                                           && cmd.MagPage < PlayerInventory.PAGES
                                           && cmd.RoundPage < PlayerInventory.PAGES);

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
                        var evt = new StorageOpenedEvent { NetId = cmd.NetId, Width = crate.Width, Height = crate.Height };
                        _sendTo(sender, NetMessagePak.Pack(ReplicationIds.EventStorageOpened, evt.Write));
                    }
                });

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

                commands.Register<HarvestCropCommand>(ReplicationIds.CommandHarvestCrop, HarvestCropCommand.TryRead,
                    OnHarvestCrop,
                    validate: (sender, cmd) => TryGetSenderPos(sender, out var pos)
                                            && _crops.TryGet(cmd.NetId, out var e)
                                            && (e.Pos - pos).magnitude <= CropReach
                                            && _crops.IsGrown(e, _tick()));
            }
        }

        // ---- cross-system handlers ----

        void OnPlaceDeployable(ushort sender, PlaceDeployableCommand cmd)
        {
            var inv = SenderInventory(sender);
            inv.removeItemAmount(cmd.DefId, 1);   // the deployable item is spent (SP: planting consumes it)
            var e = _deployables.ServerPlace(_ids.Mint(), cmd.DefId, sender, cmd.Pos, cmd.YawDegrees, _tick());
            if (e == null) return;
            // A STORAGE DEVICE BRINGS ITS OWN GRID, registered under the deployable's OWN NetId -- which is
            // what the client stamps onto the materialized crate and what its F-open addresses. So the whole
            // open/move/close path a map container already uses works on a placed fridge with no new command
            // and no new event: ServerOpenStorage is keyed by crate id and does not care whether that id came
            // from a world fixture or a deployable. Empty, unlike a map crate -- nobody loots a fridge you
            // have just put down.
            if (_deployables.Schema.TryGet(cmd.DefId, out var pdef) && pdef.StorageWidth > 0 && pdef.StorageHeight > 0)
                _inventories.ServerRegisterCrate(new NetId(e.NetIdValue), pdef.StorageWidth, pdef.StorageHeight, e.Pos);
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
            for (byte b = 0; b < (byte)(PlayerInventory.PAGES - 2); b++)
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
                if (magJar.item.amount <= 0 || roundAsset == null || held == null || held != roundAsset.magRound)
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
            // Must actually be a magazine sitting where the client says it is. A stale address is the ordinary
            // case (the bag moved under the reload), not an attack, so it is a quiet reject.
            if (asset == null || asset.magCapacity <= 0) { Diag.ReloadsRejected++; return; }
            page.removeItem(index);
            // Give the spent magazine back, CLAMPED to what that magazine can physically hold. SpentAmount is the
            // one number only the client knows (no gun state is replicated), so this is the cheat surface: the
            // clamp bounds it at "a full magazine back" rather than an arbitrary stack.
            if (cmd.SpentId != 0)
            {
                var spent = Assets.find(cmd.SpentId);
                if (spent != null && spent.magCapacity > 0)
                {
                    byte amt = (byte)System.Math.Min(cmd.SpentAmount, (int)spent.magCapacity);
                    if (amt > 0) inv.tryAddItem(new Item(cmd.SpentId, amt, 100));
                }
            }
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
        void OnUnwearClothing(ushort sender, UnwearClothingCommand cmd)
        {
            var inv = SenderInventory(sender);
            var want = (EItemType)cmd.Slot;
            if (!IsClothingType(want)) { Diag.ClothingRejected++; return; }
            var old = WornIn(inv, want);
            if (old == null) { Diag.ClothingRejected++; return; }
            // Clear the slot FIRST: taking a bag off resizes its page to 0x0 and discards whatever was in it, so
            // asking for room before that would measure a grid that is about to shrink.
            Wear(inv, want, null);
            if (!inv.tryAddItem(old)) { Wear(inv, want, old); Diag.ClothingRejected++; return; }   // no room -> put it back on
            Diag.ClothingApplied++;
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

        void OnConsume(ushort sender, ConsumeCommand cmd)
        {
            var inv = SenderInventory(sender);
            var page = inv.items[cmd.Page];
            byte index = page.getIndex(cmd.X, cmd.Y);
            var jar = index == byte.MaxValue ? null : page.getItem(index);
            var asset = jar?.item != null ? Assets.find(jar.item.id) : null;
            if (asset == null || !asset.IsConsumable) { Diag.ConsumesRejected++; return; }
            // SPEND THE JAR WE JUST VALIDATED, not "some item with this id somewhere in the bag". This used to be
            // `inv.removeItemAmount(asset.id, 1)`, which scans pages 0..PAGES-2 only -- so eating out of an OPEN
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
            SenderInventory(sender).removeItemAmount(cmd.SeedId, 1);   // the seed is spent (SP: planting consumes it)
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
