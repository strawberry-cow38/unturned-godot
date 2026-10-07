using System;
using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>Counters for the item movers -- what a test asserts on, and what a "my mover does nothing" report
    /// gets answered from.</summary>
    public sealed class ServerItemMoversDiagnostics
    {
        public long UnitsMoved;      // item UNITS that crossed a pipe (a stack of 14 moved whole counts 14)
        public long Transfers;       // individual pick-and-place operations
        public long Stalls;          // a powered mover with budget that found nowhere to put the item it picked
        public long UnpoweredSteps;  // mover-steps skipped for lack of power
        public long PowerSolves;     // full power solves the movers asked for (only when the power inputs changed)
        public long RouteVisits;     // junction/adapter evaluations by the routing walks -- the walk's real cost
        public long Rebuilds;        // times the network counters moved and the mover caches were rebuilt
        public long MissedChanges;   // ⚠ the backstop found the network changed with NO counter bump -- a forgotten ServerTouch
        public long MissedWakes;     // ⚠ a mover the backstop woke (nothing it watched had changed) and then moved items -- a missed Items bump
    }

    /// <summary>
    /// THE MOVER (strawberry 2026-10-06: "moves items from whatevers connected on the input side to whatevers
    /// connected on the output side when powered 32 stack quantity per second (so 32 stacks of 1, or a single
    /// stack of 32 item, or a mix of 14 in a single stack and 18 stacks of 1)").
    ///
    /// "only a mover can move". Adapters, splitters and combiners are passive: they are ROUTING, consulted by the
    /// mover when it looks upstream for a source and downstream for a destination. Nothing happens anywhere in a
    /// pipe network until a powered mover has budget.
    ///
    /// SERVER ONLY, and there is no singleplayer copy of this: singleplayer IS the in-process server
    /// (MpLoopback), so this is the singleplayer path. The host steps it from a DelegateSimStep next to the
    /// container publish, before replication.
    ///
    /// ⚠ THE UNIT IS AN ITEM UNIT, NOT A STACK. Her own example settles it: "32 stacks of 1, or a single stack
    /// of 32" are the same second's work. A stack of N costs N; a non-stackable item (amount means something
    /// else on it -- a magazine's rounds) costs 1 and is never split.
    /// </summary>
    public sealed class ServerItemMovers
    {
        /// <summary>How far an adapter's position may be from the ORIGIN of the container it binds. Containers are
        /// not points: the adapter sits on a FACE, and the server only knows the container's origin (its base
        /// centre). Measured off the registered container meshes, not guessed: the biggest is Shelf_1, the 5 m
        /// store gondola -- 2.61 m from its origin to its farthest side corner and 2.42 m tall, so a face point
        /// can be sqrt(2.61^2 + 2.42^2) = 3.56 m away before the adapter's own 0.12 m standoff. 4 m covers it;
        /// a forged target further than that is refused.</summary>
        public const float AdapterReach = 4f;

        /// <summary>Guard against a pipe loop the per-path visited set somehow misses, and against a pathological
        /// chain. Sixteen junctions between a mover and a container is far beyond anything built on purpose.</summary>
        public const int MaxRouteDepth = 16;

        /// <summary>DIRTY FLAGS (strawberry 2026-10-06: "could we dirty flag the industrial stuff so we only recalc
        /// if and where something changes in the network? trying hard not to break it").
        ///
        /// Nothing here is recomputed on a timer any more. Three counters say when something changed:
        ///   DeployableReplication.GraphVersion  -- devices placed/removed, wires, config, on/off, fire, running dry
        ///   ItemPipeGraph.Version               -- pipes connected or cut
        ///   InventoryReplication.CrateSetVersion -- containers registered or dropped (adapters re-bind to them)
        /// and when their sum moves, the mover list is rebuilt, power is re-solved if its inputs moved, and every
        /// sleeping mover looks again. A mover that finds nothing to do sleeps on the Items.Version of exactly the
        /// containers its last walk looked at, and wakes the step one of them changes -- WHERE it changed.
        ///
        /// THE BACKSTOP is how "not breaking it" is kept honest rather than hoped for. Once per BackstopSteps the
        /// whole state the counters stand for is fingerprinted from scratch, and a sleeping mover takes one look
        /// regardless. Either catching something the counters missed is COUNTED (Diag.MissedChanges,
        /// Diag.MissedWakes) and then handled -- so a forgotten bump costs at most a second of latency, never a
        /// frozen mover, and the tests that assert both counters are zero go red on it.
        ///
        /// (What it replaced, measured 2026-10-06: a power solve every 5 steps -- quadratic, ~15 ms at 100 movers
        /// among 1,000 deployables -- and a scan of every deployable every step to find the movers.)</summary>
        public const int BackstopSteps = 50;

        // Budgets are kept in MILLIONTHS of a unit, as integers. A float accumulator of 0.64 x 50 lands on
        // 31.999999 or 32.000001 depending on the order of the adds, and "32 in one second" would then be 31 on
        // some runs -- a rate that is right on average and wrong on the second strawberry asked about.
        const long Unit = 1_000_000;

        readonly DeployableReplication _deployables;
        readonly InventoryReplication _inventories;
        readonly Dictionary<uint, long> _budget = new Dictionary<uint, long>();
        readonly Dictionary<uint, int> _cursor = new Dictionary<uint, int>();     // round-robin: next ordinal to try (splitter outs / combiner ins)
        readonly Dictionary<uint, int[]> _swrr = new Dictionary<uint, int[]>();   // weighted splitter: smooth-WRR current weights per output

        // ONE routing walk's answers, per device. A splitter feeding a combiner is a diamond: three pipes leave and
        // rejoin, so everything behind the combiner is reachable by three PATHS -- and k diamonds in series by 3^k.
        // The walk was per path: measured 2026-10-06 at 2.2 ms for ONE mover behind seven diamonds, every transfer.
        // Within one walk a device's answer does not depend on how it was reached (same item, same source, nothing
        // mutates until Commit), so it is computed once and reused -- the walk becomes linear in DEVICES.
        // EXCEPT an answer cut short by a loop (a device already on the current path) or by MaxRouteDepth: that one
        // IS path-dependent, so it is never stored. Cleared before every walk.
        readonly Dictionary<uint, Memo> _srcMemo = new Dictionary<uint, Memo>();
        readonly Dictionary<uint, Memo> _dstMemo = new Dictionary<uint, Memo>();
        bool _cut;   // the walk in progress hit a loop or the depth cap somewhere below the current device
        readonly HashSet<uint> _visited = new HashSet<uint>();
        readonly List<Choice> _srcChoices = new List<Choice>(), _dstChoices = new List<Choice>();

        struct Memo { public InventoryReplication.CrateEntry Crate; public Choice[] Choices; }

        long _stamp = long.MinValue;   // NetStamp() the caches were built at
        ulong _fingerprint;            // NetworkInputs() at that moment -- what the backstop compares against
        ulong _poweredFor;             // PowerInputs() at the last solve
        bool _everSolved;
        int _sinceBackstop;
        readonly List<(DeployableReplication.DeployableEntity e, DeployableNetDef def)> _movers =
            new List<(DeployableReplication.DeployableEntity, DeployableNetDef)>();

        /// <summary>A mover that found nothing to do, and the containers whose contents decided that. It sleeps until
        /// one of their Items.Version moves, the network changes (which clears every Watch), or Backstop runs out.
        /// Idle and stalled are a pipe network's USUAL states: re-walking a route every step to re-learn "still
        /// full" was most of a big stalled base's cost.</summary>
        sealed class Watch
        {
            public Items[] Pages; public int[] Versions; public int Backstop;
            public bool Unchanged()
            {
                for (int i = 0; i < Pages.Length; i++) if (Pages[i].Version != Versions[i]) return false;
                return true;
            }
        }
        readonly Dictionary<uint, Watch> _sleep = new Dictionary<uint, Watch>();
        readonly List<Items> _watching = new List<Items>();   // the containers the walk in progress looked at

        /// <summary>Is this mover asleep (nothing to do, waiting on a change)? For tests and the debug overlay.</summary>
        public bool IsAsleep(uint moverId) => _sleep.ContainsKey(moverId);

        public ServerItemMoversDiagnostics Diag { get; } = new ServerItemMoversDiagnostics();

        /// <summary>THE FILTER SEAM (strawberry: "filters later, filter by any item in the game"). Null accepts
        /// everything, which is today. Asked twice per transfer: once with the MOVER (may it pick this item up?)
        /// and once with the destination ADAPTER (may this container take it?) -- the two places a per-item
        /// allow-list would naturally hang. A false at the mover stalls it, exactly like a full destination,
        /// because "don't hunt other slots" applies to filters too.</summary>
        public Func<DeployableReplication.DeployableEntity, Item, bool> Filter;

        public ServerItemMovers(DeployableReplication deployables, InventoryReplication inventories)
        {
            _deployables = deployables;
            _inventories = inventories;
        }

        // ---------------------------------------------------------------- adapter binding

        /// <summary>The crate an adapter at <paramref name="at"/> binds. A NAMED crate (the one the client's ghost
        /// snapped to) wins if it is real and within AdapterReach; a named crate that fails is a refusal, not a
        /// cue to guess -- the client said which box, and binding a different one is the wrong answer quietly.
        /// Unnamed (0: the console, a reload): the NEAREST crate within reach, ties to the lower id so two runs
        /// agree. 0 = nothing in reach, and the place is refused (strawberry: "has to snap or it wont place").</summary>
        public static uint FindCrateFor(InventoryReplication inv, Vector3 at, uint named = 0)
        {
            if (inv == null) return 0;
            if (named != 0)
                return inv.TryGetCrate(named, out var c) && !c.OnVehicle && (c.Pos - at).magnitude <= AdapterReach ? named : 0;
            uint best = 0; float bestD = float.MaxValue;
            foreach (var c in inv.Crates)
            {
                if (c.OnVehicle) continue;   // v58: a car's trunk or cabin drives away -- never something a pipe binds
                float d = (c.Pos - at).magnitude;
                if (d > AdapterReach) continue;
                if (d < bestD || (d == bestD && c.NetIdValue < best)) { best = c.NetIdValue; bestD = d; }
            }
            return best;
        }

        /// <summary>The crate an adapter feeds or drains, re-binding by nearest if its crate has gone (a fridge
        /// picked up, a reload whose saved position no longer matches anything).</summary>
        public InventoryReplication.CrateEntry CrateFor(DeployableReplication.DeployableEntity adapter)
        {
            if (adapter == null) return null;
            if (adapter.ItemCrateId != 0 && _inventories.TryGetCrate(adapter.ItemCrateId, out var c)) return c;
            adapter.ItemCrateId = FindCrateFor(_inventories, adapter.Pos);
            return adapter.ItemCrateId != 0 && _inventories.TryGetCrate(adapter.ItemCrateId, out c) ? c : null;
        }

        // ---------------------------------------------------------------- the step

        /// <summary>One sim step for every mover. <paramref name="dt"/> is the fixed tick (0.02 s); the budget
        /// math runs in integer microseconds so the rate is exact rather than right on average.</summary>
        public void Step(float dt)
        {
            long dtMicro = (long)Math.Round(dt * 1_000_000.0);
            if (dtMicro <= 0) return;
            long stamp = NetStamp();
            bool rebuild = stamp != _stamp;
            if (!rebuild && ++_sinceBackstop >= BackstopSteps)
            {
                _sinceBackstop = 0;
                if (NetworkInputs() != _fingerprint) { Diag.MissedChanges++; rebuild = true; }
            }
            if (rebuild) Rebuild(stamp);
            foreach (var (e, def) in _movers) StepMover(e, def, dtMicro);
        }

        long NetStamp() => _deployables.GraphVersion + _deployables.Pipes.Version + _inventories.CrateSetVersion;   // all only ever grow

        void Rebuild(long stamp)
        {
            _stamp = stamp;
            _fingerprint = NetworkInputs();
            _sinceBackstop = 0;
            Diag.Rebuilds++;
            _movers.Clear();
            foreach (var e in _deployables.All)
                if (_deployables.Schema.TryGet(e.DefId, out var def) && def.ItemDevice == ItemDeviceKind.Mover)
                    _movers.Add((e, def));
            // every verdict a sleeping mover holds was about the network as it WAS
            _sleep.Clear();
            // A picked-up mover leaves its budget behind otherwise; harmless, but a registry that only grows
            // is how the next "is this NetId a mover" question gets answered yes for a dead one.
            if (_budget.Count > _movers.Count)
            {
                var live = new HashSet<uint>(); foreach (var (e, _) in _movers) live.Add(e.NetIdValue);
                foreach (uint id in new List<uint>(_budget.Keys)) if (!live.Contains(id)) _budget.Remove(id);
            }
            if (_movers.Count == 0) { _budget.Clear(); return; }
            // a pipe or a config change moves the stamp too; only re-solve when what the solver READS moved
            ulong power = PowerInputs();
            if (!_everSolved || power != _poweredFor) { _deployables.Solve(); _poweredFor = power; _everSolved = true; Diag.PowerSolves++; }
        }

        /// <summary>The backstop's view: everything the three counters claim to cover, recomputed from scratch -- the
        /// power inputs, every pipe's ends, every configurable device's config, every adapter's binding, and which
        /// containers exist. If this moves while the counters did not, a bump was forgotten somewhere.</summary>
        ulong NetworkInputs()
        {
            ulong h = PowerInputs();
            foreach (var p in _deployables.Pipes.All)
                h += Mix(Mix(((ulong)p.SrcId << 8) | p.SrcPort) ^ (((ulong)p.DstId << 8) | p.DstPort) ^ 0x2545F4914F6CDD1DUL);
            foreach (var e in _deployables.All)
            {
                if (e.ItemCrateId != 0) h += Mix(((ulong)e.NetIdValue << 32) ^ e.ItemCrateId ^ 0x9E3779B97F4A7C15UL);
                var c = e.ItemConfig;
                if (c != null)
                    h += Mix(((ulong)e.NetIdValue << 32) ^ ((ulong)(byte)c.Mode << 24) ^ ((ulong)c.Rate << 16)
                             ^ Mix(((ulong)c.Weights[0] << 16) | ((ulong)c.Weights[1] << 8) | c.Weights[2]));
            }
            foreach (var c in _inventories.Crates) h += Mix(c.NetIdValue ^ 0xD6E8FEB86659FD93UL);
            return h;
        }

        /// <summary>A fingerprint of everything DeployableReplication.Solve reads: the devices, their def (ports),
        /// Producing (on, not burning, fuelled) and OnFire, and every wire's two ends. A SUM of per-element hashes,
        /// so the dictionaries' enumeration order cannot fake a change -- and anything Solve reads that is missing
        /// here is a power change the mover would not see, so add it HERE when Solve grows an input.</summary>
        ulong PowerInputs()
        {
            ulong h = 0;
            int n = 0, w = 0;
            foreach (var e in _deployables.All)
            {
                bool producing = _deployables.Schema.TryGet(e.DefId, out var def) && e.Producing(def);
                h += Mix(((ulong)e.NetIdValue << 32) | ((ulong)e.DefId << 8) | (producing ? 1u : 0u) | (e.OnFire ? 2u : 0u));
                n++;
            }
            foreach (var x in _deployables.AllWires)
            {
                h += Mix(Mix(((ulong)x.SrcId << 8) | x.SrcPort) ^ (((ulong)x.DstId << 8) | x.DstPort) ^ 0x5bd1e995UL);
                w++;
            }
            return h ^ Mix(((ulong)n << 32) | (uint)w);
        }

        static ulong Mix(ulong z)   // splitmix64's finaliser
        {
            z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9UL;
            z = (z ^ (z >> 27)) * 0x94d049bb133111ebUL;
            return z ^ (z >> 31);
        }

        /// <summary>Is this mover's power INPUT fed? Its first Consumer port after the last solve -- the same
        /// question OnExtractFuel asks of a gas pump. The Passthrough beside it re-exports what is left, so a
        /// row of movers daisy-chains off one generator like a row of lamps.</summary>
        public static bool IsPowered(DeployableReplication.DeployableEntity e, DeployableNetDef def)
        {
            for (int i = 0; i < def.Ports.Length && i < e.Solved.Length; i++)
                if (def.Ports[i].Kind == (byte)PowerPortKind.Consumer) return e.Solved[i].Powered;
            return false;
        }

        void StepMover(DeployableReplication.DeployableEntity e, DeployableNetDef def, long dtMicro)
        {
            uint id = e.NetIdValue;
            if (e.OnFire || !IsPowered(e, def))
            {
                // NOT banked while dark. A mover that sat unpowered for a minute does not get to dump a minute of
                // items the instant the generator starts -- it starts from nothing, like a motor spinning up.
                _budget.Remove(id);
                Diag.UnpoweredSteps++;
                return;
            }
            int rate = Mathf.Clamp(e.ItemConfig?.Rate ?? ItemDeviceConfig.DefaultRate, ItemDeviceConfig.MinRate, ItemDeviceConfig.MaxRate);
            // Capped at one second's worth: a mover stalled behind a full chest must not bank an hour of throughput
            // and empty the source in a single tick the moment someone frees a slot.
            long b = Math.Min(rate * Unit, (_budget.TryGetValue(id, out long had) ? had : 0) + rate * dtMicro);
            bool backstopWake = false;
            if (_sleep.TryGetValue(id, out var z))
            {
                // asleep: the budget fills, the walk waits for a container it looked at to change
                if (z.Unchanged() && --z.Backstop > 0) { _budget[id] = b; return; }
                backstopWake = z.Unchanged();   // nothing it watched moved -- only the backstop's timer woke it
                _sleep.Remove(id);
            }
            byte inPort = PortAt(def, ItemPortDir.In, 0), outPort = PortAt(def, ItemPortDir.Out, 0);
            bool idle = false;
            for (int guard = 0; b >= Unit && guard < ItemDeviceConfig.MaxRate * 2; guard++)
            {
                _watching.Clear();   // the containers THIS attempt looks at are the ones that decide it
                var srcChoices = _srcChoices; srcChoices.Clear();
                _srcMemo.Clear(); _cut = false; _visited.Clear(); _visited.Add(id);
                var src = SourceThrough(id, inPort, 0, _visited, srcChoices);
                if (src == null) { idle = true; break; }                  // nothing upstream, or everything upstream is empty
                // "should suck from the last full slot in the source container" -- and ONLY that slot. If it will
                // not go anywhere the mover stalls; it does not go hunting for a different item that would.
                var jar = ItemTransfer.LastOccupied(src.Storage);
                if (jar?.item == null) { idle = true; break; }
                if (Filter != null && !Filter(e, jar.item)) { Diag.Stalls++; idle = true; break; }
                var dstChoices = _dstChoices; dstChoices.Clear();
                _dstMemo.Clear(); _cut = false; _visited.Clear(); _visited.Add(id);
                var dst = DestThrough(id, outPort, jar.item, src, 0, _visited, dstChoices);
                // "mover wont try move anything if destination full, unless it can merge stacks" -- DestAt only
                // returns a crate that can take at least one unit of THIS item, by merge or by free footprint.
                if (dst == null) { Diag.Stalls++; idle = true; break; }
                int want = (int)Math.Min(ItemTransfer.UnitsOf(jar.item), b / Unit);
                int n = Math.Min(want, ItemTransfer.Acceptable(dst.Storage, jar.item));
                if (n <= 0) { Diag.Stalls++; idle = true; break; }
                int moved = ItemTransfer.Transfer(src.Storage, jar, dst.Storage, n);
                if (moved <= 0) { Diag.Stalls++; idle = true; break; }
                // (no Touch needed: ItemTransfer only changes pages through Items' own operations, each of which bumps
                // Items.Version -- a_mover_draining_a_container_wakes_the_mover_filling_it holds that true)
                if (backstopWake) { Diag.MissedWakes++; backstopWake = false; }
                b -= moved * Unit;
                Diag.UnitsMoved += moved; Diag.Transfers++;
                Commit(srcChoices); Commit(dstChoices);
                // Anyone with either container open is looking at a COPY in their own page; repaint it from the
                // grid we just changed, or their next drag is validated against items that are not there.
                _inventories.ServerRepaintCrateViewers(src.NetIdValue);
                _inventories.ServerRepaintCrateViewers(dst.NetIdValue);
            }
            _budget[id] = b;
            if (idle)
            {
                // the backstop look is STAGGERED by id (50-99 steps): a base whose movers all stalled on the same step
                // would otherwise have every one of them re-walk on the same later step -- measured as a ~1 ms spike
                // once a second at 100 movers, where spread out it is noise
                var w = new Watch { Pages = _watching.ToArray(), Versions = new int[_watching.Count], Backstop = BackstopSteps + (int)(id % BackstopSteps) };
                for (int i = 0; i < w.Pages.Length; i++) w.Versions[i] = w.Pages[i].Version;
                _sleep[id] = w;
            }
        }

        // ---------------------------------------------------------------- routing

        /// <summary>A decision taken at a junction, applied only once a transfer actually happens -- a stalled
        /// attempt must not advance a round-robin, or a full output would make its neighbours skip turns.</summary>
        struct Choice { public uint Device; public int Ordinal; public int CandMask; }

        static int CountPorts(DeployableNetDef def, ItemPortDir dir)
        {
            int n = 0;
            foreach (byte p in def.ItemPorts) if (p == (byte)dir) n++;
            return n;
        }

        /// <summary>The item-port INDEX of the ordinal-th port facing <paramref name="dir"/> (255 = none).</summary>
        public static byte PortAt(DeployableNetDef def, ItemPortDir dir, int ordinal)
        {
            for (int i = 0, k = 0; i < def.ItemPorts.Length; i++)
                if (def.ItemPorts[i] == (byte)dir && k++ == ordinal) return (byte)i;
            return byte.MaxValue;
        }

        bool Resolve(uint netId, out DeployableReplication.DeployableEntity e, out DeployableNetDef def)
        {
            def = null;
            return _deployables.TryGet(netId, out e) && !e.OnFire && _deployables.Schema.TryGet(e.DefId, out def);
        }

        // ---- upstream: where does the item come FROM ----

        InventoryReplication.CrateEntry SourceThrough(uint consumerId, byte inPort, int depth, HashSet<uint> visited, List<Choice> choices)
        {
            if (inPort == byte.MaxValue || !_deployables.Pipes.TryGetOnPort(consumerId, inPort, out var pipe)) return null;
            if (pipe.DstId != consumerId || pipe.DstPort != inPort) return null;   // the port's pipe runs the other way
            return SourceAt(pipe.SrcId, pipe.SrcPort, depth + 1, visited, choices);
        }

        InventoryReplication.CrateEntry SourceAt(uint devId, byte outPort, int depth, HashSet<uint> visited, List<Choice> choices)
            => Memoised(_srcMemo, devId, null, null, depth, visited, choices);

        /// <summary>Evaluate this device once per routing walk (downstream when <paramref name="memo"/> is the
        /// destination memo, else upstream): a stored answer is replayed (its junction choices too), a loop or the
        /// depth cap answers null and marks the walk CUT, and an answer that a cut fed into is returned but not
        /// stored. No delegate: a closure per device per walk was most of a stalled mover's garbage.</summary>
        InventoryReplication.CrateEntry Memoised(Dictionary<uint, Memo> memo, uint devId, Item item, InventoryReplication.CrateEntry src,
                                                 int depth, HashSet<uint> visited, List<Choice> choices)
        {
            if (memo.TryGetValue(devId, out var m)) { choices.AddRange(m.Choices); return m.Crate; }
            if (depth > MaxRouteDepth || !visited.Add(devId)) { _cut = true; return null; }
            bool outer = _cut;
            _cut = false;
            int from = choices.Count;
            try
            {
                Diag.RouteVisits++;
                var crate = memo == _dstMemo ? DestAtOnce(devId, item, src, depth, visited, choices)
                                             : SourceAtOnce(devId, depth, visited, choices);
                if (!_cut)
                {
                    int k = choices.Count - from;
                    memo[devId] = new Memo { Crate = crate, Choices = k == 0 ? Array.Empty<Choice>() : choices.GetRange(from, k).ToArray() };
                }
                return crate;
            }
            finally { visited.Remove(devId); _cut |= outer; }
        }

        InventoryReplication.CrateEntry SourceAtOnce(uint devId, int depth, HashSet<uint> visited, List<Choice> choices)
        {
            if (!Resolve(devId, out var e, out var def)) return null;
            switch (def.ItemDevice)
            {
                case ItemDeviceKind.Adapter:
                {
                    var crate = CrateFor(e);
                    if (crate?.Storage != null) _watching.Add(crate.Storage);
                    return crate != null && crate.Storage != null && crate.Storage.getItemCount() > 0 ? crate : null;
                }
                case ItemDeviceKind.Combiner:
                {
                    // ROUND-ROBIN over the CONNECTED inputs, skipping empty ones (strawberry: "completely ignore
                    // disconnected i/o"). An unpiped input is not a turn that yields nothing -- it is not there.
                    int n = CountPorts(def, ItemPortDir.In);
                    if (n == 0) return null;
                    int start = (_cursor.TryGetValue(devId, out int cur) ? cur : 0) % n;
                    for (int k = 0; k < n; k++)
                    {
                        int ord = (start + k) % n;
                        var sub = new List<Choice>();
                        var c = SourceThrough(devId, PortAt(def, ItemPortDir.In, ord), depth, visited, sub);
                        if (c == null) continue;
                        choices.Add(new Choice { Device = devId, Ordinal = ord });
                        choices.AddRange(sub);
                        return c;
                    }
                    return null;
                }
                case ItemDeviceKind.Splitter:
                    // a splitter UPSTREAM of a mover is a pass-through: pull through it from whatever feeds it
                    return SourceThrough(devId, PortAt(def, ItemPortDir.In, 0), depth, visited, choices);
                default:
                    return null;   // another mover is not a source: "only a mover can move", and it moves its own way
            }
        }

        // ---- downstream: where does the item go TO ----

        InventoryReplication.CrateEntry DestThrough(uint producerId, byte outPort, Item item, InventoryReplication.CrateEntry src,
                                                    int depth, HashSet<uint> visited, List<Choice> choices)
        {
            if (outPort == byte.MaxValue || !_deployables.Pipes.TryGetOnPort(producerId, outPort, out var pipe)) return null;
            if (pipe.SrcId != producerId || pipe.SrcPort != outPort) return null;
            return DestAt(pipe.DstId, item, src, depth + 1, visited, choices);
        }

        InventoryReplication.CrateEntry DestAt(uint devId, Item item, InventoryReplication.CrateEntry src,
                                               int depth, HashSet<uint> visited, List<Choice> choices)
            => Memoised(_dstMemo, devId, item, src, depth, visited, choices);

        InventoryReplication.CrateEntry DestAtOnce(uint devId, Item item, InventoryReplication.CrateEntry src,
                                                   int depth, HashSet<uint> visited, List<Choice> choices)
        {
            if (!Resolve(devId, out var e, out var def)) return null;
            switch (def.ItemDevice)
            {
                case ItemDeviceKind.Adapter:
                {
                    var crate = CrateFor(e);
                    // the source container is never a destination: moving a chest's last item back into the
                    // same chest is a loop that spends budget to change nothing
                    if (crate == null || crate == src || crate.Storage == null) return null;
                    _watching.Add(crate.Storage);
                    if (Filter != null && !Filter(e, item)) return null;
                    return ItemTransfer.Acceptable(crate.Storage, item) > 0 ? crate : null;
                }
                case ItemDeviceKind.Combiner:
                    // a combiner DOWNSTREAM of a mover is a pass-through to its single output
                    return DestThrough(devId, PortAt(def, ItemPortDir.Out, 0), item, src, depth, visited, choices);
                case ItemDeviceKind.Splitter:
                    return SplitterPick(devId, e, def, item, src, depth, visited, choices);
                default:
                    return null;   // a mover is not a destination
            }
        }

        /// <summary>Pick an output by the splitter's mode, among CONNECTED outputs whose branch can accept the item
        /// ("completely ignore disconnected i/o" -- and a branch that cannot take it is skipped rather than
        /// stalling the whole splitter). Every branch is probed so the mode chooses among real candidates.</summary>
        InventoryReplication.CrateEntry SplitterPick(uint devId, DeployableReplication.DeployableEntity e, DeployableNetDef def, Item item,
                                                     InventoryReplication.CrateEntry src, int depth, HashSet<uint> visited, List<Choice> choices)
        {
            int n = CountPorts(def, ItemPortDir.Out);
            if (n == 0) return null;
            var results = new InventoryReplication.CrateEntry[n];
            var subs = new List<Choice>[n];
            int mask = 0;
            for (int ord = 0; ord < n; ord++)
            {
                byte port = PortAt(def, ItemPortDir.Out, ord);
                if (!_deployables.Pipes.IsPortPiped(devId, port)) continue;   // disconnected: not a candidate at all
                subs[ord] = new List<Choice>();
                results[ord] = DestThrough(devId, port, item, src, depth, visited, subs[ord]);
                if (results[ord] != null) mask |= 1 << ord;
            }
            if (mask == 0) return null;
            var cfg = e.ItemConfig ?? new ItemDeviceConfig();
            int pick = -1;
            switch (cfg.Mode)
            {
                case SplitterMode.Overflow:
                    // first connected output (1 -> 2 -> 3) that can take it; the next only gets the overflow
                    for (int ord = 0; ord < n && pick < 0; ord++) if ((mask & (1 << ord)) != 0) pick = ord;
                    break;
                case SplitterMode.Weighted:
                {
                    // SMOOTH weighted round-robin (the nginx scheme): each candidate's current += weight, the largest
                    // wins and pays back the total. 3:1 comes out A A B A A A B A ... -- interleaved, rather than
                    // three in a row and then one, which is what a counter-based split would do to a chest.
                    var cur = Currents(devId);
                    int best = int.MinValue, candMask = 0;
                    for (int ord = 0; ord < n && ord < ItemDeviceConfig.Ways; ord++)
                    {
                        if ((mask & (1 << ord)) == 0 || cfg.Weights[ord] == 0) continue;   // weight 0 = this output is off
                        candMask |= 1 << ord;
                        int score = cur[ord] + cfg.Weights[ord];
                        if (score > best) { best = score; pick = ord; }
                    }
                    if (pick < 0) return null;
                    choices.Add(new Choice { Device = devId, Ordinal = pick, CandMask = candMask });
                    choices.AddRange(subs[pick]);
                    return results[pick];
                }
                default:   // RoundRobin: rotate through the connected outputs, one transfer each
                {
                    int start = (_cursor.TryGetValue(devId, out int c) ? c : 0) % n;
                    for (int k = 0; k < n && pick < 0; k++) { int ord = (start + k) % n; if ((mask & (1 << ord)) != 0) pick = ord; }
                    break;
                }
            }
            if (pick < 0) return null;
            choices.Add(new Choice { Device = devId, Ordinal = pick });
            choices.AddRange(subs[pick]);
            return results[pick];
        }

        int[] Currents(uint devId)
        {
            if (!_swrr.TryGetValue(devId, out var cur)) _swrr[devId] = cur = new int[ItemDeviceConfig.Ways];
            return cur;
        }

        void Commit(List<Choice> choices)
        {
            foreach (var c in choices)
            {
                if (!_deployables.TryGet(c.Device, out var e) || !_deployables.Schema.TryGet(e.DefId, out var def)) continue;
                if (def.ItemDevice == ItemDeviceKind.Combiner)
                    _cursor[c.Device] = (c.Ordinal + 1) % Math.Max(1, CountPorts(def, ItemPortDir.In));
                else if (def.ItemDevice == ItemDeviceKind.Splitter)
                {
                    var mode = e.ItemConfig?.Mode ?? SplitterMode.RoundRobin;
                    if (mode == SplitterMode.RoundRobin)
                        _cursor[c.Device] = (c.Ordinal + 1) % Math.Max(1, CountPorts(def, ItemPortDir.Out));
                    else if (mode == SplitterMode.Weighted && e.ItemConfig != null)
                    {
                        var cur = Currents(c.Device);
                        var w = e.ItemConfig.Weights;
                        int total = 0;
                        for (int ord = 0; ord < ItemDeviceConfig.Ways; ord++)
                            if ((c.CandMask & (1 << ord)) != 0) { cur[ord] += w[ord]; total += w[ord]; }
                        cur[c.Ordinal] -= total;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The stack arithmetic the mover runs on, engine-free and public so it is tested on its own.
    ///
    /// Deliberately NOT Items.tryAddItem, though it does the same merge-then-place. tryAddItem collapses money
    /// into the $1 carrier and, when the overflow will not fit, returns true having kept "what fitted" -- the
    /// rest of the dollars simply stop existing. Right for a pickup that cannot do better; wrong for a machine
    /// that is supposed to conserve what it carries. Here every unit that does not land goes back where it
    /// came from, and the only conversion is none.
    /// </summary>
    public static class ItemTransfer
    {
        /// <summary>A stack's ceiling -- AddCore's rule exactly, so the mover and a pickup agree on what full is.</summary>
        public static int StackCap(ushort id)
        {
            int cap = Math.Max(1, Assets.find(id)?.stackSize ?? 1);
            if (Items.StackingEnabled) cap = Math.Max(cap, byte.MaxValue);
            return cap;
        }

        /// <summary>Does `amount` mean "how many of these"? A magazine's amount is its ROUNDS -- splitting one
        /// would breed magazines (PlayerInventory.TryDrag carries the same guard for the same reason).</summary>
        public static bool IsStackable(Item it)
        {
            if (it == null) return false;
            var a = Assets.find(it.id);
            if (a != null && a.IsMagazine) return false;
            return StackCap(it.id) > 1;
        }

        /// <summary>What moving this item whole costs, in strawberry's "stack quantity".</summary>
        public static int UnitsOf(Item it) => it == null ? 0 : IsStackable(it) ? Math.Max(1, (int)it.amount) : 1;

        /// <summary>The occupied slot that comes LAST reading the grid the way the player does: bottom row first,
        /// then rightmost. By position, not by list order -- the list is insertion order, so after a drag the
        /// item a player put in the top-left corner would otherwise be "last".</summary>
        public static ItemJar LastOccupied(Items page)
        {
            if (page == null) return null;
            ItemJar best = null;
            for (byte i = 0; i < page.getItemCount(); i++)
            {
                var j = page.getItem(i);
                if (j?.item == null) continue;
                if (best == null || j.y > best.y || (j.y == best.y && j.x > best.x)) best = j;
            }
            return best;
        }

        /// <summary>How many units of <paramref name="it"/> this page could take right now: room in partial
        /// stacks of the same id, plus a whole stack if a free footprint exists. 0 = full ("mover wont try move
        /// anything if destination full, unless it can merge stacks").</summary>
        public static int Acceptable(Items dst, Item it)
        {
            if (dst == null || it == null) return 0;
            bool stack = IsStackable(it);
            int cap = StackCap(it.id), merge = 0;
            if (stack)
                for (byte i = 0; i < dst.getItemCount(); i++)
                {
                    var j = dst.getItem(i);
                    if (j?.item != null && j.item.id == it.id && j.item.amount < cap) merge += cap - j.item.amount;
                }
            var probe = new ItemJar(it);
            bool space = dst.getItemCount() < 200 && dst.width > 0 && dst.height > 0
                         && dst.tryFindSpace(probe.size_x, probe.size_y, out _, out _, out _);
            if (!stack) return space ? 1 : 0;
            return merge + (space ? cap : 0);
        }

        /// <summary>Merge into partial stacks first, then one new jar. Returns the units that did NOT land, which
        /// the caller puts back -- never discarded.</summary>
        public static int PlaceInto(Items dst, Item it)
        {
            bool stack = IsStackable(it);
            bool merged = false;
            if (stack)
            {
                int cap = StackCap(it.id);
                for (byte i = 0; i < dst.getItemCount() && it.amount > 0; i++)
                {
                    var j = dst.getItem(i);
                    if (j?.item == null || j.item.id != it.id || j.item.amount >= cap) continue;
                    int add = Math.Min(cap - j.item.amount, it.amount);
                    j.item.amount = (ushort)(j.item.amount + add);
                    it.amount = (ushort)(it.amount - add);
                    merged = true;
                }
                if (it.amount == 0) { dst.raiseStateUpdated(); return 0; }   // a bare amount write raises nothing on its own
            }
            var probe = new ItemJar(it);
            if (dst.getItemCount() < 200 && dst.tryFindSpace(probe.size_x, probe.size_y, out byte x, out byte y, out byte rot))
            {
                dst.addItem(x, y, rot, it);
                return 0;
            }
            if (merged) dst.raiseStateUpdated();
            return stack ? it.amount : 1;
        }

        /// <summary>Move <paramref name="n"/> units of the stack in <paramref name="jar"/> from src to dst. A whole
        /// jar moves as the SAME Item (quality, gun state and all ride along); part of a stack is split off with
        /// takeFrom. Whatever does not land is put back. Returns the units actually moved.</summary>
        public static int Transfer(Items src, ItemJar jar, Items dst, int n)
        {
            if (src == null || dst == null || jar?.item == null || n <= 0) return 0;
            byte idx = byte.MaxValue;
            for (byte i = 0; i < src.getItemCount(); i++) if (ReferenceEquals(src.getItem(i), jar)) { idx = i; break; }
            if (idx == byte.MaxValue) return 0;
            bool stack = IsStackable(jar.item);
            if (!stack || n >= jar.item.amount)
            {
                var item = jar.item;
                int before = UnitsOf(item);
                byte x = jar.x, y = jar.y, rot = jar.rot;
                src.removeItem(idx);
                int left = PlaceInto(dst, item);
                if (left > 0)
                {
                    // Back into the cells it just left -- they are free, because nothing ran in between.
                    if (stack) item.amount = (ushort)left;
                    src.addItem(x, y, rot, item);
                }
                return before - left;
            }
            var part = src.takeFrom(idx, n);   // reduces the source stack and hands back a clone of n
            if (part == null) return 0;
            int rest = PlaceInto(dst, part);
            if (rest > 0) { jar.item.amount = (ushort)(jar.item.amount + rest); src.raiseStateUpdated(); }
            return n - rest;
        }
    }
}
