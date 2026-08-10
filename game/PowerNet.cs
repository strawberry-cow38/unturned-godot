using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // Power propagation over the wire graph. The ALGORITHM lives engine-free in core/UnturnedSim/PowerSolver.cs
    // (tested under tests/UnturnedSim.Tests); this is the thin adapter: walk the "deployables"/"wires" groups into
    // plain PowerDevice/PowerPort/PowerWire records, Solve, and write Live/Powered/Draw back to the port nodes.
    public static class PowerNet
    {
        // The graph only changes on discrete events (wire built/cleared, deployable placed/removed, generator toggled,
        // something catches fire) -- nothing per-frame -- so recompute is event-driven, not every frame. MarkDirty()
        // flags state changes; the wire/deployable COUNT is a backstop that catches any structural add/remove for free.
        static bool _dirty = true;
        static int _lastWires = -1, _lastDeployables = -1;
        public static void MarkDirty() => _dirty = true;

        // Global grid-power flag (SP): while ON, every GridPowerSource (a Circuit_0 breaker box) feeds its 10kW into
        // the wire graph; while OFF the mains are dead. Default ON (master 2026-07-20 -- grid power just works out of
        // the box); the F1 console `toggleGlobalPower` flips it. A flag flip is NOT a structural change (the wire/
        // deployable counts don't move), so the toggle path MUST MarkDirty() or RecomputeIfDirty's count-backstop
        // would skip the recompute. (Under --spconsume the mains ride the replicated ToggledOn, seeded ON in MpLoopback.)
        static bool _globalPower = true;
        public static bool GlobalPower => _globalPower;
        public static bool ToggleGlobalPower() { _globalPower = !_globalPower; MarkDirty(); RefreshMains(); return _globalPower; }
        public static void SetGlobalPower(bool on) { if (_globalPower != on) { _globalPower = on; MarkDirty(); RefreshMains(); } }

        // ---- MAINS AUTHORITY, which is NOT always the flag above -----------------------------------------------------
        //
        // On a joined client or a consuming loopback the mains are server-authoritative: `toggleGlobalPower` is
        // forwarded over the console plane (DevConsole returns before touching _globalPower), the server flips each
        // GridPowerSource fixture's replicated ToggledOn, and DeployableReplicaView pushes that into
        // NetProducingOverride. So on that path the process-global flag NEVER MOVES -- GridPowerSource already knows
        // this and reads `_netProducing ?? GlobalPower`, with a comment saying never to use the global directly.
        //
        // Every OTHER mains-fed fixture -- televisions, patient monitors, gas pumps -- read the raw flag, so grid
        // sources correctly stopped producing while the fixtures carried on regardless. Reported as "globalpower has
        // no effect on tvs", and it also let a set be switched ON with the grid dead, because the same flag gates
        // Toggle's Refresh. L1 could not see any of it: the harness is direct SP, where the flag path is the real one
        // and the replicated path never runs.
        //
        // Tracked as a SET of live replicated sources rather than one last-write bool, so several breaker boxes
        // toggled independently give the right answer instead of whichever reported last.
        static readonly System.Collections.Generic.HashSet<GodotObject> _netMainsOn = new();
        static bool _anyReplicatedGrid;

        /// <summary>A replicated grid source reporting its server-authoritative producing bit.</summary>
        public static void ReportNetMains(GodotObject src, bool on)
        {
            _anyReplicatedGrid = true;
            if (on) _netMainsOn.Add(src); else _netMainsOn.Remove(src);
            RefreshMains();
            MarkDirty();
        }

        /// <summary>Is the mains live? The replicated bit where there IS one, the local flag otherwise. This is what a
        /// mains-fed fixture must gate on -- never GlobalPower, for the reasons above.</summary>
        public static bool MainsLive => _mains;

        // CACHED, because this is read by every mains-fed consumer every frame and the replicated branch is not
        // free: it did a RemoveWhere validity sweep of the source set on EVERY read. With ~34 televisions plus
        // monitors, lamps and hydrants all polling HasFeed per frame, that was N set-scans a frame to answer a
        // question that changes a handful of times a session.
        //
        // Refreshed from the four places that can change the answer -- all of them in this file -- plus once per
        // frame from RecomputeIfDirty, which is what catches the case a setter cannot: a replicated source that
        // was freed without telling anyone. That per-frame sweep is why the validity check lived in the getter
        // in the first place; doing it once instead of once-per-reader is the whole change.
        static bool _mains = true;   // matches _globalPower's initial value

        internal static void RefreshMains()
        {
            if (!_anyReplicatedGrid) { _mains = _globalPower; return; }
            _netMainsOn.RemoveWhere(o => !GodotObject.IsInstanceValid(o));   // a despawned source is not a supply
            _mains = _netMainsOn.Count > 0;
        }

        public static void ResetForTests()
        {
            _dirty = true; _lastWires = -1; _lastDeployables = -1; _globalPower = false;
            _netMainsOn.Clear(); _anyReplicatedGrid = false;   // or one sandbox's replicas decide the next one's mains
            RefreshMains();
        }

        public static void RecomputeIfDirty(SceneTree tree)
        {
            RefreshMains();   // once a frame, not once per reader -- and it is what notices a freed replicated source
            int w = tree.GetNodeCountInGroup("wires"), d = tree.GetNodeCountInGroup("deployables");
            if (!_dirty && w == _lastWires && d == _lastDeployables) return;   // idle: nothing changed -> skip the whole O(W*(W+D)) pass
            _dirty = false; _lastWires = w; _lastDeployables = d;
            Recompute(tree);
        }

        public static void Recompute(SceneTree tree)
        {
            var devices = new System.Collections.Generic.List<PowerDevice>();
            var portMap = new System.Collections.Generic.Dictionary<ConnectionPort, PowerPort>();
            foreach (var n in tree.GetNodesInGroup("deployables"))
                if (n is IPowerDevice d)   // a Deployable OR a powered world fixture (gas pump)
                {
                    var dev = new PowerDevice { Producing = d.PowerProducing, OnFire = d.PowerOnFire, Conducting = d.PowerConducting };
                    foreach (var p in d.PowerPorts)
                    {
                        if (p == null || !GodotObject.IsInstanceValid(p)) continue;
                        p.Occupied = false;   // reset; the wire loop below re-marks the wired ports
                        float watts = p.Kind == DeployableDef.PortKind.Output ? p.Watts * d.PowerScale : p.Watts;   // a generator's output CAP ramps with its spin-up/cooldown (master); consumer/passthrough unscaled
                        portMap[p] = dev.AddPort(Kind(p.Kind), watts);
                    }
                    devices.Add(dev);
                }

            var wires = new System.Collections.Generic.List<PowerWire>();
            foreach (var n in tree.GetNodesInGroup("wires"))
                if (n is Wire w && GodotObject.IsInstanceValid(w.Source) && GodotObject.IsInstanceValid(w.Consumer))
                {
                    w.Source.Occupied = true; w.Consumer.Occupied = true;   // a wired port shades darker
                    if (portMap.TryGetValue(w.Source, out var src) && portMap.TryGetValue(w.Consumer, out var cons))
                        wires.Add(new PowerWire(src, cons));
                }

            PowerSolver.Solve(devices, wires);

            foreach (var kv in portMap)   // write the solved state back onto the port nodes
            {
                kv.Key.Live = kv.Value.Live;
                kv.Key.Powered = kv.Value.Powered;
                kv.Key.Draw = kv.Value.Draw;
                kv.Key.UpdateCubeColor();   // reflect the new occupancy shade
            }
        }

        // Pre-warm helper: flash every wire-tool port arrow visible (show) or hide, so their shared spatial-shader
        // compiles at load instead of all-at-once on the first wire-tool activation (the hitch, master). See PowerManager._prewarm.
        public static void PrewarmWireArrows(SceneTree tree, bool show)
        {
            foreach (var n in tree.GetNodesInGroup("ports"))
                if (n is ConnectionPort p && GodotObject.IsInstanceValid(p)) p.SetArrowState(show, false);
        }

        static PowerPortKind Kind(DeployableDef.PortKind k) => k switch
        {
            DeployableDef.PortKind.Output => PowerPortKind.Output,
            DeployableDef.PortKind.Consumer => PowerPortKind.Consumer,
            _ => PowerPortKind.Passthrough,
        };
    }

    // Ticks the power net once a frame. One instance is created lazily by the first placed deployable.
    public partial class PowerManager : Node
    {
        // Pre-warm the wire-tool arrow SHADER over the first few frames (master): the port arrows are created hidden at
        // spawn, so their shared spatial-shader wouldn't compile until the tool first comes out -> all of them compile
        // at once = a hitch on the first wire interaction. Flash them visible for a couple frames so it compiles up
        // front (any in-view arrow warms the shared shader). Runs during/right-after load, then never again.
        int _prewarm = 3;
        public override void _Process(double delta)
        {
            using var _prof = Prof.Scope("PowerNet");
            PowerNet.RecomputeIfDirty(GetTree());
            if (_prewarm > 0) { PowerNet.PrewarmWireArrows(GetTree(), _prewarm > 1); _prewarm--; }
        }
    }
}
