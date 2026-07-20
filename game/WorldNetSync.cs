using Godot;
using System.Collections.Generic;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // ---------------------------------------------------------------------------------------------------
    // MP Phase 8 world-state syncs (MP_PLAN §3.7): the game-side bridges between the world's nodes and the
    // engine-free WorldClock/Crop/Resource replication systems, following the ZombieNetSync /
    // WorldItemNetSync pattern -- polled at a low cadence on the server's SimRoot, registered before the
    // replication send. None of this exists on the SP direct path.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Registers every farms.tsv crop def into a CropSchema -- the same def table on server and
    /// every client (content-hash-matched), so only the seed id crosses the wire (§3.7).</summary>
    public static class CropNetSchema
    {
        public static void RegisterAll(CropSchema schema)
        {
            if (FarmRegistry.Count == 0) FarmRegistry.Load();
            if (CropRegistry.Count == 0) CropRegistry.Load();
            foreach (var def in FarmRegistry.All)
                schema.Register(new CropNetDef { SeedId = def.Id, GrowthSeconds = def.Growth, YieldItemId = def.Grow });
        }
    }

    /// <summary>
    /// Day-night from the server tick (§3.7/§2.5): publishes the world's DayNightCycle into
    /// WorldClockReplication so every client can derive time-of-day from the snapshot tick. Two modes:
    ///  - driveFromTick (dedicated): the SERVER'S OWN clock becomes tick-derived (Time = base + tick x 0.02
    ///    / dayLength) -- exact, zero drift, one publish at boot.
    ///  - frame-driven (SP-loopback): the local DayNightCycle keeps SP's exact frame clock (behavior
    ///    neutral); this sync republishes the base only when the tick-derivation drifts past epsilon.
    /// </summary>
    public sealed class WorldClockNetSync
    {
        public const int DivisorTicks = 100;         // 2 s drift check -- a clock, not gameplay
        const float DriftEpsilon = 0.001f;           // ~0.3 s of a 300 s day

        readonly NetWorldServer _server;
        readonly DayNightCycle _dnc;
        readonly bool _driveFromTick;

        public WorldClockNetSync(NetWorldServer server, DayNightCycle dnc, bool driveFromTick)
        {
            _server = server;
            _dnc = dnc;
            _driveFromTick = driveFromTick;
            if (_dnc == null) return;
            if (_driveFromTick) _dnc.ExternalTime = true;
            Publish();
        }

        public void Tick()
        {
            if (_dnc == null || !GodotObject.IsInstanceValid(_dnc)) return;
            long tick = _server.Session.CurrentTick;
            if (_driveFromTick)
            {
                _dnc.Time = _server.Clock.TimeOfDayAt(tick);   // the authoritative clock IS the tick
                return;
            }
            if (tick % DivisorTicks != 0) return;
            float derived = _server.Clock.TimeOfDayAt(tick);
            float drift = Mathf.Abs(Mathf.PosMod(derived - _dnc.Time + 0.5f, 1f) - 0.5f);   // wrapped distance
            if (!_server.Clock.HasClock || drift > DriftEpsilon) Publish();
        }

        /// <summary>Re-anchor the replicated clock on the world's current time (boot, admin set-time,
        /// accumulated frame-vs-tick drift). ServerConfigure no-ops when the quantized base is unchanged.</summary>
        public void Publish()
        {
            long tick = _server.Session.CurrentTick;
            float dayLen = Mathf.Max(1f, _dnc.DayLength);
            float baseTime = Mathf.PosMod(_dnc.Time - (float)(tick * SimClock.FixedDelta / dayLen), 1f);
            _server.Clock.ServerConfigure(baseTime, dayLen, tick);
        }
    }

    /// <summary>
    /// Crops (§3.7), server side: bridges the world's CropNode NODES (the listen-server's local player
    /// plants via the direct SP path -- console `plant`, and harvests with F) into CropReplication
    /// entities, and materializes entities that arrived by COMMAND (a remote player's Plant) as real nodes
    /// in the listen-server's world. Removals reconcile both directions, exactly like WorldItemNetSync.
    /// On a dedicated server there is no CropManager: crops live purely in the engine-free replication
    /// (growth is tick math), and node materialization is skipped.
    /// </summary>
    public sealed class CropNetSync
    {
        public const int DivisorTicks = 25;   // 2 Hz -- growth cadence, not gameplay

        readonly NetWorldServer _server;
        readonly Node _host;
        readonly Dictionary<ulong, uint> _netIdByInstance = new();
        readonly Dictionary<uint, (CropNode Node, ulong Iid)> _nodes = new();

        public int TrackedCount => _nodes.Count;

        public CropNetSync(NetWorldServer server, Node host)
        {
            _server = server;
            _host = host;
            CropNetSchema.RegisterAll(server.Crops.Schema);
        }

        public void Tick()
        {
            long tick = _server.Session.CurrentTick;
            if (tick % DivisorTicks != 0) return;
            var tree = _host.GetTree();
            if (tree == null) return;

            // nodes -> entities: publish locally-planted crops; the node's clock (UG_FARMSPEED-scaled
            // CropManager time) is the world truth on a listen server, so force-mature the entity when the
            // node matures ahead of the tick derivation
            foreach (var n in tree.GetNodesInGroup("crop"))
            {
                if (n is not CropNode c || !GodotObject.IsInstanceValid(c) || c.Crop == null) continue;
                ulong iid = c.GetInstanceId();
                bool grown = c.Crop.IsFullyGrown(CropManager.Now);
                if (!_netIdByInstance.TryGetValue(iid, out uint netId))
                {
                    var gp = c.GlobalPosition;
                    var e = _server.Transactions.PlantCrop(c.Crop.Def.Id,
                        new UnityEngine.Vector3(gp.X, gp.Y, gp.Z), grown);
                    if (e == null) continue;   // seed not in farms.tsv (fallback def) -- stays SP-local
                    netId = e.NetIdValue;
                    _netIdByInstance[iid] = netId;
                    _nodes[netId] = (c, iid);
                    c.NetId = netId;   // B3: stamp the server crop id onto the host's own CropManager node so the
                                       // F-interact scan (RequestHarvestNearestCrop, NetId!=0) can route its harvest
                                       // over the wire; idempotent + harmless when no harvest seam is set (SP/dedicated)
                }
                else if (grown && _server.Crops.TryGet(netId, out var ent) && !_server.Crops.IsGrown(ent, tick))
                {
                    _server.Crops.ServerForceGrown(netId, tick);
                }
            }

            // entities -> nodes: a remote player's Plant command materializes in this world (listen server)
            if (CropManager.Active)
            {
                List<CropReplication.CropEntity> missing = null;
                foreach (var e in _server.Crops.All)
                    if (!_nodes.ContainsKey(e.NetIdValue)) (missing ??= new()).Add(e);
                if (missing != null)
                    foreach (var e in missing)
                    {
                        if (!CropRegistry.TryBySeed(e.SeedId, out string name)) continue;
                        var node = CropManager.Plant(name, new Vector3(e.Pos.x, e.Pos.y, e.Pos.z),
                                                     grown: _server.Crops.IsGrown(e, tick));
                        if (node == null) continue;
                        ulong iid = node.GetInstanceId();
                        _netIdByInstance[iid] = e.NetIdValue;
                        _nodes[e.NetIdValue] = (node, iid);
                        node.NetId = e.NetIdValue;   // B3: stamp the server crop id onto the freshly-materialized
                                                     // node (remote-planted crop) so the harvest scan finds it too
                    }
            }

            // reconcile removals both directions
            List<uint> forget = null;
            foreach (var kv in _nodes)
            {
                uint netId = kv.Key;
                var node = kv.Value.Node;
                bool nodeAlive = GodotObject.IsInstanceValid(node) && !node.IsQueuedForDeletion();
                bool entityAlive = _server.Crops.TryGet(netId, out _);
                if (!nodeAlive)
                {
                    // the local player harvested the node (direct SP path) -> retire the entity
                    _server.Transactions.RemoveCrop(netId);
                    (forget ??= new List<uint>()).Add(netId);
                }
                else if (!entityAlive)
                {
                    // a remote Harvest command consumed the entity -> the plant leaves this world too
                    node.QueueFree();
                    (forget ??= new List<uint>()).Add(netId);
                }
            }
            if (forget != null)
                foreach (uint id in forget)
                {
                    _netIdByInstance.Remove(_nodes[id].Iid);
                    _nodes.Remove(id);
                }
        }
    }

    /// <summary>
    /// Resources/trees (§3.7), server side: sizes the alive-bitmap to the world's deterministic resource
    /// index space at boot (the join-time WriteFull carries it whole), and mirrors authoritative bit flips
    /// back onto the ResourceField (visual + trunk collider). SetAlive is the single authoritative entry
    /// point for a future chop/mine mechanic -- no SP mechanic fells resources yet, so in live worlds this
    /// is join-consistency machinery.
    /// </summary>
    public sealed class ResourceNetSync
    {
        readonly NetWorldServer _server;
        readonly ResourceField _field;
        long _appliedVersion;

        public ResourceNetSync(NetWorldServer server, ResourceField field)
        {
            _server = server;
            _field = field;
            if (field != null)
            {
                _server.Resources.ServerInit(field.InstanceCount, server.Session.CurrentTick);
                _appliedVersion = _server.Resources.Version;
            }
        }

        /// <summary>Fell (false) / respawn (true) one resource: authoritative flip + event + world mirror.</summary>
        public bool SetAlive(int index, bool alive)
        {
            if (!_server.Transactions.SetResourceAlive(index, alive)) return false;
            _field?.SetAlive(index, alive);
            return true;
        }

        public void Tick()
        {
            // keep the field mirrored even when bits flip behind our back (console/test hooks)
            if (_field == null || _server.Resources.Version == _appliedVersion) return;
            _appliedVersion = _server.Resources.Version;
            int n = Mathf.Min(_server.Resources.Count, _field.InstanceCount);
            for (int i = 0; i < n; i++)
                if (_field.IsAlive(i) != _server.Resources.IsAlive(i))
                    _field.SetAlive(i, _server.Resources.IsAlive(i));
        }
    }

    /// <summary>
    /// Destructible props (rubble), server side: seeds the DestructibleReplication bitmap + the
    /// ServerDestructibles health/respawn authority from the world's deterministic index space at boot
    /// (each built slot's Rubble_Health + Rubble_Reset), ticks the respawn clock, and mirrors authoritative
    /// alive-bit flips back onto the DestructibleField (mesh hide + collider off). Combat routes an object
    /// hit into DestructibleHost.DamageObject (wired in NetWorldServer's ctor), which flips the bit here.
    /// </summary>
    public sealed class DestructibleNetSync
    {
        readonly NetWorldServer _server;
        readonly DestructibleField _field;
        long _appliedVersion;

        public DestructibleNetSync(NetWorldServer server, DestructibleField field)
        {
            _server = server;
            _field = field;
            if (field != null)
            {
                _server.DestructibleHost.ServerInit(field.InstanceCount, server.Session.CurrentTick);
                for (int i = 0; i < field.InstanceCount; i++)
                    if (field.MaxHealth(i) > 0f)
                        _server.DestructibleHost.SetMeta(i, field.MaxHealth(i), field.ResetTicks(i));
                _appliedVersion = _server.Destructibles.Version;
            }
        }

        public void Tick()
        {
            _server.DestructibleHost.Tick(_server.Session.CurrentTick);   // respawn any prop whose Rubble_Reset elapsed
            if (_field == null || _server.Destructibles.Version == _appliedVersion) return;
            _appliedVersion = _server.Destructibles.Version;
            int n = Mathf.Min(_server.Destructibles.Count, _field.InstanceCount);
            for (int i = 0; i < n; i++)
                if (_field.IsAlive(i) != _server.Destructibles.IsAlive(i))
                    _field.SetAlive(i, _server.Destructibles.IsAlive(i));
        }
    }
}
