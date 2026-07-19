using System.Collections.Generic;

namespace SDG.Unturned
{
    // The wire-power propagation algorithm, extracted engine-free from the game's PowerNet (proposal phase 3):
    // a producing generator OUTPUT pushes its watts down a wire to a CONSUMER; the consumer is powered if it
    // receives at least its usage, and its PASSTHROUGH re-exports the leftover (received - usage) down the next
    // wire. Iterated so chains (genny -> spot -> spot -> ...) settle in one Solve. A SPLITTER is just a device
    // whose input is a 0-watt consumer (a relay -- takes nothing for itself) with SEVERAL passthroughs: each
    // re-exports the full input (leftover = input - 0), so one wire fans out to 2/3/4 wires without dividing the
    // wattage (each downstream device draws what it needs). A COMBINER is the mirror: TWO 0-watt consumer inputs and
    // one passthrough that re-exports their SUM (input1 + input2 added), and the downstream load is traced back to the
    // sources in PROPORTION to what each provides (so two gens split the load, capped at each one's output). The
    // game's PowerNet.Recompute is a thin adapter: it walks the "deployables"/"wires" groups into these plain records,
    // calls Solve, and writes Live/Powered/Draw back to the ConnectionPort nodes.
    public enum PowerPortKind { Output, Consumer, Passthrough }

    public sealed class PowerPort
    {
        public PowerPortKind Kind;
        public float Watts;        // output: produced; consumer: drawn; passthrough: unused (= input - usage)
        public PowerDevice Owner;  // the device this port sits on (set by PowerDevice.AddPort)
        // solver results:
        public float Live;         // output = produced now, consumer = received, passthrough = exported now
        public bool Powered;       // consumer: is it getting at least its usage?
        public float Draw;         // output only: total wattage drawn by the powered consumers down its chain
    }

    public sealed class PowerDevice
    {
        public bool Producing;     // a generator whose engine is effectively running (on + fuelled + not on fire)
        public bool OnFire;        // a burning/wrecked device stops conducting (its passthrough dies with it)
        public readonly List<PowerPort> Ports = new List<PowerPort>();
        public PowerPort AddPort(PowerPortKind kind, float watts)
        {
            var p = new PowerPort { Kind = kind, Watts = watts, Owner = this };
            Ports.Add(p);
            return p;
        }
    }

    public readonly struct PowerWire
    {
        public readonly PowerPort Source, Consumer;
        public PowerWire(PowerPort source, PowerPort consumer) { Source = source; Consumer = consumer; }
    }

    public static class PowerSolver
    {
        public static void Solve(IReadOnlyList<PowerDevice> devices, IReadOnlyList<PowerWire> wires)
        {
            // reset: outputs produce (if their device is running); consumer/passthrough start at 0
            foreach (var d in devices)
                foreach (var p in d.Ports)
                {
                    if (p.Kind == PowerPortKind.Output) p.Live = d.Producing ? p.Watts : 0f;
                    else { p.Live = 0f; p.Powered = false; }
                }

            // propagate one hop per pass; wires.Count+1 passes fully settles any chain
            int passes = wires.Count + 1;
            for (int k = 0; k < passes; k++)
            {
                foreach (var w in wires)
                    if (w.Source != null && w.Consumer != null)
                        w.Consumer.Live = w.Source.Live;   // the consumer receives whatever the source is exporting
                foreach (var d in devices)
                {
                    // score EVERY consumer on the device (a combiner has two inputs), then re-export the SUM of their
                    // leftovers to every passthrough -- spotlight: one 250w consumer -> its leftover; splitter: one 0w
                    // relay -> its full input fanned to N outputs; combiner: two 0w relays -> input1 + input2 ADDED.
                    float exported = 0f; bool anyConsumer = false;
                    foreach (var p in d.Ports)
                        if (p.Kind == PowerPortKind.Consumer)
                        {
                            anyConsumer = true;
                            // a normal consumer (Watts>0) needs at least its usage; a 0-watt RELAY (splitter/combiner
                            // input) just needs any live input, since it takes nothing for itself.
                            bool hasInput = p.Watts > 0f ? p.Live >= p.Watts : p.Live > 0f;
                            p.Powered = !d.OnFire && hasInput;   // a burning consumer stops conducting
                            if (p.Powered) exported += p.Live - p.Watts;
                        }
                    if (anyConsumer)
                        foreach (var p in d.Ports)
                            if (p.Kind == PowerPortKind.Passthrough) p.Live = exported;
                }
            }

            // OUTPUT CAP: a source can't power more than its rated Watts. Walk each producing output's tree in
            // wire order, greedily charging powered consumers against a shared budget; a consumer (and everything
            // downstream of it) that would push past the budget is switched OFF. This stops a SPLITTER -- which
            // re-exports the full input to every branch -- from letting you draw unlimited power past the generator's
            // rating (master: "the generator should stop powering new devices above the threshold"). Only bites on
            // OVERLOAD; an under-budget net is untouched. `drawn` is global so a combiner's two sources each spend
            // their own budget on the shared load without double-charging (and #2 picks up what #1 couldn't afford).
            // PASS 1 charges each producing output's tree against its budget, threading the combiner load-SHARE (same
            // ratio TraceLoad uses) so a consumer fed by two sources only costs each one its proportional slice; a
            // consumer a source can't cover its share of is STARVED. Deterministic (wire order) so it's the first
            // ~4000w of devices that stay lit -- master's "stop powering NEW devices above the threshold".
            var starved = new HashSet<PowerPort>();
            foreach (var d in devices)
                foreach (var o in d.Ports)
                {
                    if (o.Kind != PowerPortKind.Output || o.Live <= 0f) continue;
                    float remaining = o.Watts;
                    var seen = new HashSet<PowerPort>();
                    var stack = new Stack<(PowerPort port, float share)>();
                    stack.Push((o, 1f));
                    while (stack.Count > 0)
                    {
                        var (src, share) = stack.Pop();
                        if (!seen.Add(src)) continue;
                        PowerPort consumer = null;
                        foreach (var w in wires) if (w.Source == src && w.Consumer != null) { consumer = w.Consumer; break; }
                        if (consumer == null || !consumer.Powered) continue;
                        float cost = consumer.Watts * share;   // this source's proportional slice of the load (share==1 outside a combiner)
                        if (cost > 0f)
                        {
                            if (remaining >= cost) remaining -= cost;
                            else { starved.Add(consumer); continue; }   // over budget -> starve it + don't feed its subtree from this source
                        }
                        var owner = consumer.Owner;
                        if (owner == null) continue;
                        float totalIn = 0f;
                        foreach (var pp in owner.Ports) if (pp.Kind == PowerPortKind.Consumer && pp.Powered) totalIn += pp.Live;
                        float subShare = totalIn > 0f ? share * (consumer.Live / totalIn) : 0f;
                        if (subShare > 0f)
                            foreach (var pp in owner.Ports)
                                if (pp.Kind == PowerPortKind.Passthrough) stack.Push((pp, subShare));
                    }
                }
            // PASS 2 (only if something starved): re-walk the live sources, stopping at starved consumers, and switch
            // off any powered consumer no longer reachable (the starved loads + everything downstream of them).
            if (starved.Count > 0)
            {
                var reached = new HashSet<PowerPort>();
                foreach (var d in devices)
                    foreach (var o in d.Ports)
                    {
                        if (o.Kind != PowerPortKind.Output || o.Live <= 0f) continue;
                        var seen = new HashSet<PowerPort>();
                        var stack = new Stack<PowerPort>();
                        stack.Push(o);
                        while (stack.Count > 0)
                        {
                            var src = stack.Pop();
                            if (!seen.Add(src)) continue;
                            PowerPort consumer = null;
                            foreach (var w in wires) if (w.Source == src && w.Consumer != null) { consumer = w.Consumer; break; }
                            if (consumer == null || !consumer.Powered || starved.Contains(consumer)) continue;
                            reached.Add(consumer);
                            if (consumer.Owner != null)
                                foreach (var pp in consumer.Owner.Ports)
                                    if (pp.Kind == PowerPortKind.Passthrough) stack.Push(pp);
                        }
                    }
                foreach (var d in devices)
                    foreach (var p in d.Ports)
                        if (p.Kind == PowerPortKind.Consumer && p.Powered && !reached.Contains(p))
                            p.Powered = false;
            }

            // per-output LOAD: trace each output's chain/tree (output -> wire -> consumer -> that consumer's
            // passthrough(s) -> ...) and sum the usage of every powered consumer it feeds (the generator's usage
            // bar + vibration). A splitter forks the trace, so this is a tree walk, not a single chain.
            foreach (var d in devices)
                foreach (var p in d.Ports)
                    if (p.Kind == PowerPortKind.Output)
                        p.Draw = TraceLoad(p, wires);
        }

        static float TraceLoad(PowerPort output, IReadOnlyList<PowerWire> wires)
        {
            float draw = 0f;
            var seen = new HashSet<PowerPort>();   // guard against a wiring cycle
            var stack = new Stack<(PowerPort port, float share)>();
            stack.Push((output, 1f));   // share = the fraction of the downstream load THIS source carries (a combiner splits it across its sources)
            while (stack.Count > 0)
            {
                var (src, share) = stack.Pop();
                if (!seen.Add(src)) continue;
                PowerPort consumer = null;
                foreach (var w in wires)   // the one wire fed by this source port (1 wire/port)
                    if (w.Source == src && w.Consumer != null) { consumer = w.Consumer; break; }
                if (consumer == null) continue;
                if (consumer.Powered) draw += consumer.Watts * share;   // a splitter/combiner relay input is 0w -> adds nothing itself
                var owner = consumer.Owner;
                if (owner == null) continue;
                // continue into the device's passthrough(s). A COMBINER has two inputs feeding one output: this source
                // carries the downstream load only in PROPORTION to what it provides (its input / the device's total
                // input), so two gens SPLIT the load. Single-input devices (spotlight/splitter) -> ratio 1, unchanged.
                float totalIn = 0f;
                foreach (var pp in owner.Ports)
                    if (pp.Kind == PowerPortKind.Consumer && pp.Powered) totalIn += pp.Live;
                float subShare = totalIn > 0f ? share * (consumer.Live / totalIn) : 0f;
                if (subShare > 0f)
                    foreach (var pp in owner.Ports)   // fork into ALL the owner's passthroughs (a splitter re-exports several)
                        if (pp.Kind == PowerPortKind.Passthrough) stack.Push((pp, subShare));
            }
            return draw;
        }
    }
}
