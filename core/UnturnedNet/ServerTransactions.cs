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
        public long PickupsDenied;          // legal pickup, full grid -> ItemPickupDenied went back
        public long ConsoleApplied;
        public long ConsoleRejected;        // unknown verb / cheats disabled / bad args
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

        readonly PlayerReplication _players;
        readonly PlayerCombatReplication _combat;
        readonly SkillsReplication _skills;
        readonly InventoryReplication _inventories;
        readonly WorldItemReplication _worldItems;
        readonly DeployableReplication _deployables;
        readonly CropReplication _crops;
        readonly ResourceReplication _resources;
        readonly NetIdMinter _ids;
        readonly Func<long> _tick;
        readonly Action<byte[]> _broadcast;
        readonly Action<ushort, byte[]> _sendTo;

        public ServerTransactions(PlayerReplication players, PlayerCombatReplication combat,
                                  SkillsReplication skills, InventoryReplication inventories,
                                  WorldItemReplication worldItems, DeployableReplication deployables,
                                  NetIdMinter ids, Func<long> tick,
                                  Action<byte[]> broadcast, Action<ushort, byte[]> sendTo,
                                  CropReplication crops = null, ResourceReplication resources = null)
        {
            _players = players; _combat = combat;
            _skills = skills; _inventories = inventories; _worldItems = worldItems; _deployables = deployables;
            _crops = crops; _resources = resources;
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

            // TODO(mp-security): salvage/wire/toggle are reach-gated only -- no e.OwnerPlayerId == sender
            // (or group) check (review M2). DELIBERATELY deferred while this is a friendly co-op test
            // server (editing each other's bases is convenient); add the ownership gate to these three
            // validators before any public/untrusted hosting. See MP_PLAN "Security posture".
            commands.Register<SalvageDeployableCommand>(ReplicationIds.CommandSalvageDeployable, SalvageDeployableCommand.TryRead,
                OnSalvageDeployable,
                validate: (sender, cmd) => TryGetSenderPos(sender, out var pos)
                                        && _deployables.TryGet(cmd.NetId, out var e)
                                        && e.OnFire   // only a dead/burning wreck tears down (SP: blowtorch a cooled wreck)
                                        && (e.Pos - pos).magnitude <= DeployableReplication.WireReach);

            // TODO(mp-security): no ownership check, reach-gated only (review M2 deferral -- see above)
            commands.Register<ConnectWireCommand>(ReplicationIds.CommandConnectWire, ConnectWireCommand.TryRead,
                OnConnectWire,
                validate: (sender, cmd) => TryGetSenderPos(sender, out var pos)
                                        && _deployables.CanConnectWire(cmd.SrcId, cmd.SrcPort, cmd.DstId, cmd.DstPort, pos));

            // TODO(mp-security): no ownership check, reach-gated only (review M2 deferral -- see above)
            commands.Register<RemoveWireCommand>(ReplicationIds.CommandRemoveWire, RemoveWireCommand.TryRead,
                OnRemoveWire,
                validate: (sender, cmd) => TryGetSenderPos(sender, out var pos)
                                        && _deployables.TryGetWire(cmd.WireId, out var w)
                                        && _deployables.TryGet(w.SrcId, out var src)
                                        && (src.Pos - pos).magnitude <= DeployableReplication.WireReach);

            // TODO(mp-security): no ownership check, reach-gated only (review M2 deferral -- see above)
            commands.Register<ToggleDeployableCommand>(ReplicationIds.CommandToggleDeployable, ToggleDeployableCommand.TryRead,
                OnToggleDeployable,
                validate: (sender, cmd) => TryGetSenderPos(sender, out var pos)
                                        && _deployables.CanToggle(cmd.NetId, out var e)
                                        && (e.Pos - pos).magnitude <= DeployableReplication.WireReach);

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
            var evt = new DeployablePlacedEvent { NetId = e.NetIdValue, DefId = e.DefId, OwnerPlayerId = sender, Pos = e.Pos, YawDegrees = e.YawDegrees };
            _broadcast(NetMessagePak.Pack(ReplicationIds.EventDeployablePlaced, evt.Write));
        }

        void OnSalvageDeployable(ushort sender, SalvageDeployableCommand cmd)
        {
            _deployables.TryGet(cmd.NetId, out var e);
            _deployables.Schema.TryGet(e.DefId, out var def);
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

        void OnPickupItem(ushort sender, PickupItemCommand cmd)
        {
            _worldItems.TryGet(cmd.NetId, out var e);
            var inv = SenderInventory(sender);
            if (inv.tryAddItem(e.ServerItem))
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

        void OnConsume(ushort sender, ConsumeCommand cmd)
        {
            var inv = SenderInventory(sender);
            var page = inv.items[cmd.Page];
            byte index = page.getIndex(cmd.X, cmd.Y);
            var jar = index == byte.MaxValue ? null : page.getItem(index);
            var asset = jar?.item != null ? Assets.find(jar.item.id) : null;
            if (asset == null || !asset.IsConsumable) { Diag.ConsumesRejected++; return; }
            inv.removeItemAmount(asset.id, 1);   // the SP consume path removes by id (PlayerController.TickConsume)
            Diag.ConsumesApplied++;

            // vitals: the server-side combat state carries health; food/water/stamina/infection have no
            // server model yet (Phase 5 replicated coarse health only) -- deferred with the vitals split.
            if (asset.useHealth > 0 && _combat.TryGet(sender, out var ce) && ce.Alive)
            {
                ce.HealthExact = Mathf.Min(100f, ce.HealthExact + asset.useHealth);
                ce.Health = (byte)Mathf.RoundToInt(ce.HealthExact);
                _combat.MarkDirty(ce, _tick());
            }
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
            if (!AllowCheats) { Diag.ConsoleRejected++; return "console commands are disabled on this server"; }

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

        bool TryGetSenderPos(ushort sender, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (!_players.TryGetByOwner(sender, out var p)) return false;
            pos = p.Pos;
            return true;
        }
    }
}
