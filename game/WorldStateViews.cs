using Godot;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // PEI_CLIENT_PLAN §3 Phase C5, client side of the §3.7 world state: two tiny views a joined client
    // attaches beside its replica views. Both are READ-ONLY consumers of the Phase 8 replicas (Clock /
    // Resources) -- they render replica state into the local world and never mutate it, so the desync
    // detector stays quiet by construction. Neither exists on the SP direct path.

    /// <summary>
    /// Drives the client world's DayNightCycle from the replicated clock (§3.7): time-of-day derives from
    /// the last applied snapshot tick. The glide/re-anchor shape (§7 risk 8, mirroring WorldClockNetSync's
    /// driveFromTick:false mode): the cycle keeps SP's exact per-frame clock (smooth sky), and this view
    /// re-anchors Time only when the tick derivation drifts past epsilon -- a per-snapshot Time write
    /// would step the sky at the 25 Hz snapshot cadence instead.
    /// </summary>
    public partial class WorldClockView : Node
    {
        public NetWorldClient Client;
        public DayNightCycle DayNight;

        const float DriftEpsilon = 0.001f;   // ~0.3 s of a 300 s day -- the WorldClockNetSync threshold

        // Wind drifts off the SERVER's tick on a joined client, for the same reason the sky does: the noise
        // map is identical everywhere, so wind agrees exactly when the drift offset agrees. This overrides
        // SimDriver's default (the client's OWN sim tick, which is not the server's). Nothing goes on the
        // wire for it -- LastAppliedServerTick is already there.
        public override void _Ready() => WindField.UseTickClock(() => Client?.Applier?.LastAppliedServerTick ?? 0L);
        public override void _ExitTree() => WindField.UseLocalClock();

        // Re-anchor on the PHYSICS tick, not _Process: the derived time changes with the applied snapshot
        // tick (a physics-tick quantity), and adopting here runs in lock-step with replication + the L1
        // harness's Ticks() advance -- an idle-frame _Process is decoupled from physics in headless, so the
        // adoption raced the tick budget and net.client_world_views flaked (~50% even pre-rubberband). The
        // sky still glides smoothly: DayNightCycle advances its own Time per render frame; this only
        // re-anchors on drift, which per-tick vs per-frame does not change.
        public override void _PhysicsProcess(double delta)
        {
            if (Client == null || DayNight == null || !GodotObject.IsInstanceValid(DayNight)) return;
            if (!Client.Clock.HasClock || Client.Applier.LastAppliedServerTick <= 0) return;
            if (Client.Clock.DayLengthSeconds > 0f) DayNight.DayLength = Client.Clock.DayLengthSeconds;
            float derived = Client.Clock.TimeOfDayAt(Client.Applier.LastAppliedServerTick);
            float drift = Mathf.Abs(Mathf.PosMod(derived - DayNight.Time + 0.5f, 1f) - 0.5f);   // wrapped distance
            if (drift > DriftEpsilon) DayNight.Time = derived;
        }
    }

    /// <summary>
    /// Mirrors the replicated resource alive-bitmap (§3.7) onto the client world's ResourceField: a
    /// server-felled tree loses its visual AND its trunk collider locally (§7 risk 7 -- SetAlive toggles
    /// both). Version-polled like the server-side ResourceNetSync mirror: snapshots and the
    /// Harvested/Respawned events both bump Resources.Version before this runs, so one poll covers every
    /// path -- join bitmap, delta flips, and event immediacy alike.
    /// </summary>
    public partial class ResourceAliveView : Node
    {
        public NetWorldClient Client;
        public ResourceField Field;

        long _appliedVersion;

        /// <summary>P3: the client's ResourceField loads LATE (with the server's holiday, at Accept) --
        /// re-apply the whole bitmap even if its replica Version was already consumed against the empty
        /// field, or trees felled before the join would stay visually alive until the next fell event.</summary>
        public void Refresh() => _appliedVersion = -1;

        public override void _PhysicsProcess(double delta)
        {
            if (Client == null || Field == null || !GodotObject.IsInstanceValid(Field)) return;
            if (Client.Resources.Version == _appliedVersion) return;
            _appliedVersion = Client.Resources.Version;
            int n = Mathf.Min(Client.Resources.Count, Field.InstanceCount);
            for (int i = 0; i < n; i++)
                if (Field.IsAlive(i) != Client.Resources.IsAlive(i))
                    Field.SetAlive(i, Client.Resources.IsAlive(i));
        }
    }

    /// <summary>
    /// Mirrors the replicated destructible-props alive-bitmap (DestructibleReplication 16) onto the client
    /// world's DestructibleField: a server-broken prop loses its mesh(es) AND its collider locally; a
    /// respawn restores both. Same Version-poll pattern as ResourceAliveView -- snapshots and the
    /// Destroyed/Restored events both bump Destructibles.Version, so one poll covers join/delta/event alike.
    /// </summary>
    public partial class DestructibleAliveView : Node
    {
        public NetWorldClient Client;
        public DestructibleField Field;

        long _appliedVersion;

        /// <summary>The client's DestructibleField loads LATE for holiday props (with the server's holiday, at
        /// Accept) -- re-apply the whole bitmap so props broken before the join don't stay visually intact.</summary>
        public void Refresh() => _appliedVersion = -1;

        public override void _PhysicsProcess(double delta)
        {
            if (Client == null || Field == null) return;
            if (Client.Destructibles.Version == _appliedVersion) return;
            _appliedVersion = Client.Destructibles.Version;
            int n = Mathf.Min(Client.Destructibles.Count, Field.InstanceCount);
            for (int i = 0; i < n; i++)
                if (Field.IsAlive(i) != Client.Destructibles.IsAlive(i))
                    Field.SetAlive(i, Client.Destructibles.IsAlive(i));
        }
    }

    /// <summary>
    /// Mirrors the replicated door/bed table (InteractableStateReplication 17) onto the client's own Door
    /// and Bed nodes, and stamps each node with the NetId that makes it commandable.
    ///
    /// The ids come from world-build order rather than the wire, exactly as the resource and destructible
    /// bitmaps do: every peer runs the same WorldBuilder, so the nth door is the nth door everywhere. That
    /// is also why this view has to run before a player can touch anything -- an unstamped door has NetId 0,
    /// which routes down the SINGLEPLAYER path, and the client would swing a door the server never agreed
    /// to move. Stamping happens once, on the first block to arrive.
    ///
    /// Same Version-poll shape as the two views above, so join snapshots and later deltas need no separate
    /// handling. The DoorState/BedClaimed events are the low-latency path and paint the same nodes; this is
    /// what makes a client that joined late agree with one that was there all along.
    /// </summary>
    public partial class InteractableStateView : Node
    {
        public NetWorldClient Client;
        /// <summary>Where to scan for Door/Bed nodes. Defaults to this node's parent.</summary>
        public Node WorldRoot;

        long _appliedVersion = -1;
        bool _stamped;

        public override void _PhysicsProcess(double delta)
        {
            if (Client == null) return;
            if (Client.InteractableState.Version == _appliedVersion) return;
            _appliedVersion = Client.InteractableState.Version;
            if (!_stamped) { StampNetIds(); _stamped = true; }

            _seenDoors.Clear();
            foreach (var kv in Client.InteractableState.ReplicaDoors)
            {
                _seenDoors.Add(kv.Key);
                _knownDoors.Add(kv.Key);
                if (!Door.TryGetByNetId(kv.Key, out var d)) continue;
                d.ApplyReplicatedLocked(kv.Value.Locked);
                d.ApplyReplicatedToggle(kv.Value.Open);
            }
            _seenBeds.Clear();
            foreach (var kv in Client.InteractableState.ReplicaBeds)
            {
                _seenBeds.Add(kv.Key);
                _knownBeds.Add(kv.Key);
                if (Bed.TryGetByNetId(kv.Key, out var b)) b.ApplyReplicatedClaim(kv.Value);
            }
            RetireMissing();
        }

        // Every other replica view retires nodes that left the replica (DeployableReplicaView.RetireMissing,
        // and the crop / world-item / storage views all QueueFree the same way). This one did not, and the
        // consequence was specific: a raider breaks a door, the server drops it from the table, and every
        // OTHER client goes on rendering it -- solid, blocking bullets and bodies, at a doorway its owner
        // already lost. The table was right and the world was wrong, which is exactly what the L0 test that
        // asserted on the table could not see.
        void RetireMissing()
        {
            _doomed.Clear();
            foreach (var id in _knownDoors)
                if (!_seenDoors.Contains(id)) _doomed.Add(id);
            foreach (var id in _doomed)
            {
                _knownDoors.Remove(id);
                if (Door.TryGetByNetId(id, out var d) && GodotObject.IsInstanceValid(d)) d.QueueFree();
            }

            _doomed.Clear();
            foreach (var id in _knownBeds)
                if (!_seenBeds.Contains(id)) _doomed.Add(id);
            foreach (var id in _doomed)
            {
                _knownBeds.Remove(id);
                if (Bed.TryGetByNetId(id, out var b) && GodotObject.IsInstanceValid(b)) b.QueueFree();
            }
        }

        readonly System.Collections.Generic.HashSet<uint> _seenDoors = new System.Collections.Generic.HashSet<uint>();
        readonly System.Collections.Generic.HashSet<uint> _seenBeds = new System.Collections.Generic.HashSet<uint>();
        // Ids this client has seen the server publish AT LEAST ONCE. Retirement is driven off these rather
        // than off what was stamped, because a stamped-but-never-published id is one the server has not got
        // to yet -- destroying those would delete perfectly good doors over a registration race, which is a
        // far worse failure than the phantom door this sweep exists to prevent. Something has to have
        // existed in the replica before its absence can mean anything.
        readonly System.Collections.Generic.HashSet<uint> _knownDoors = new System.Collections.Generic.HashSet<uint>();
        readonly System.Collections.Generic.HashSet<uint> _knownBeds = new System.Collections.Generic.HashSet<uint>();
        readonly System.Collections.Generic.List<uint> _doomed = new System.Collections.Generic.List<uint>();

        void StampNetIds()
        {
            var root = WorldRoot ?? GetParent();
            if (root == null) return;
            uint doorId = InteractableNetSync.FirstId, bedId = InteractableNetSync.FirstId;
            foreach (var n in Walk(root))
            {
                if (n is Door d) d.NetId = doorId++;
                else if (n is Bed b) b.NetId = bedId++;
            }
        }

        static System.Collections.Generic.IEnumerable<Node> Walk(Node n)
        {
            yield return n;
            foreach (var child in n.GetChildren())
                foreach (var d in Walk(child))
                    yield return d;
        }
    }
}
