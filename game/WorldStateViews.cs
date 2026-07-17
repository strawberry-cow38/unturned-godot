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
}
