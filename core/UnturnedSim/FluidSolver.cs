using System.Collections.Generic;

namespace SDG.Unturned
{
    // The hose-fluid propagation algorithm — a parallel of PowerSolver for the fluid IO system. A SOURCE port pushes its
    // flow RATE down a hose to a CONSUMER; the consumer flows if it receives at least its demand, and its PASSTHROUGH
    // re-exports the leftover (received - demand) down the next hose. Iterated so chains settle in one Solve. A SPLITTER
    // is a device whose input is a 0-rate consumer (relay — takes nothing) with SEVERAL passthroughs (each re-exports the
    // full input, so one hose fans out to N without dividing the rate). A COMBINER is the mirror: two 0-rate consumer
    // inputs + one passthrough re-exporting their SUM, with the downstream load traced back to the sources in PROPORTION.
    //
    // Type-lock (no mixing) is enforced when a HOSE is CREATED (see F2/F3), so every connected network is single-fluid by
    // construction and this solver stays pure rate-math — a clean mirror of the power net. Finite AMOUNTS (draining the
    // source, filling the storage) are applied per-tick by FluidNet, NOT here: here a source's Rate is its instantaneous
    // supply CAP (FluidNet clamps it by the source's remaining amount / a pump's head before calling Solve).
    public enum FluidPortKind { Source, Consumer, Passthrough }

    public sealed class FluidPort
    {
        public FluidPortKind Kind;
        public float Rate;         // source: supply cap; consumer: demand drawn; passthrough: unused (= input - demand)
        public FluidDevice Owner;  // the device this port sits on (set by FluidDevice.AddPort)
        // solver results:
        public float Flow;         // source = supplied now, consumer = received, passthrough = exported now
        public bool Flowing;       // consumer: is it getting at least its demand?
        public float Load;         // source only: total flow drawn by the flowing consumers down its chain
    }

    public sealed class FluidDevice
    {
        public bool Supplying;     // a source with fluid available (later: a running pump raises the rate/head)
        public bool Blocked;       // a broken/clogged device stops conducting (its passthrough dies with it)
        public bool Open = true;   // a closed VALVE stops conducting too (default true = open, a normal relay)
        public readonly List<FluidPort> Ports = new List<FluidPort>();
        public FluidPort AddPort(FluidPortKind kind, float rate)
        {
            var p = new FluidPort { Kind = kind, Rate = rate, Owner = this };
            Ports.Add(p);
            return p;
        }
    }

    public readonly struct FluidHose
    {
        public readonly FluidPort Source, Consumer;
        public FluidHose(FluidPort source, FluidPort consumer) { Source = source; Consumer = consumer; }
    }

    public static class FluidSolver
    {
        public static void Solve(IReadOnlyList<FluidDevice> devices, IReadOnlyList<FluidHose> hoses)
        {
            // reset: sources supply (if their device has fluid); consumer/passthrough start at 0
            foreach (var d in devices)
                foreach (var p in d.Ports)
                {
                    if (p.Kind == FluidPortKind.Source) p.Flow = d.Supplying ? p.Rate : 0f;
                    else { p.Flow = 0f; p.Flowing = false; }
                }

            // propagate one hop per pass; hoses.Count+1 passes fully settles any chain
            int passes = hoses.Count + 1;
            for (int k = 0; k < passes; k++)
            {
                foreach (var h in hoses)
                    if (h.Source != null && h.Consumer != null)
                        h.Consumer.Flow = h.Source.Flow;   // the consumer receives whatever the source is exporting
                foreach (var d in devices)
                {
                    // score EVERY consumer on the device (a combiner has two), then re-export the SUM of their leftovers
                    // to every passthrough — storage/consumer: one demand -> its leftover; splitter: one 0-rate relay ->
                    // its full input fanned to N; combiner: two 0-rate relays -> input1 + input2 ADDED.
                    float exported = 0f; bool anyConsumer = false;
                    foreach (var p in d.Ports)
                        if (p.Kind == FluidPortKind.Consumer)
                        {
                            anyConsumer = true;
                            // a real consumer (Rate>0) needs at least its demand; a 0-rate RELAY (splitter/combiner input)
                            // just needs any live input, since it takes nothing for itself.
                            bool hasInput = p.Rate > 0f ? p.Flow >= p.Rate : p.Flow > 0f;
                            p.Flowing = !d.Blocked && d.Open && hasInput;   // a clogged OR closed-valve consumer stops conducting -> nothing exported
                            if (p.Flowing) exported += p.Flow - p.Rate;
                        }
                    if (anyConsumer)
                        foreach (var p in d.Ports)
                            if (p.Kind == FluidPortKind.Passthrough) p.Flow = exported;
                }
            }

            // SOURCE CAP: a source can't supply more than its rated Rate. Walk each supplying source's tree in hose order,
            // greedily charging flowing consumers against a shared budget; a consumer (and everything downstream) that
            // would push past the budget is starved. Stops a SPLITTER — which re-exports the full input to every branch —
            // from letting you draw unlimited flow past the source's cap. Only bites on OVER-DRAW; an under-cap net is
            // untouched. Combiner load-share threaded (same ratio TraceLoad uses) so two sources split a shared consumer.
            var starved = new HashSet<FluidPort>();
            foreach (var d in devices)
                foreach (var o in d.Ports)
                {
                    if (o.Kind != FluidPortKind.Source || o.Flow <= 0f) continue;
                    float remaining = o.Rate;
                    var seen = new HashSet<FluidPort>();
                    var stack = new Stack<(FluidPort port, float share)>();
                    stack.Push((o, 1f));
                    while (stack.Count > 0)
                    {
                        var (src, share) = stack.Pop();
                        if (!seen.Add(src)) continue;
                        FluidPort consumer = null;
                        foreach (var h in hoses) if (h.Source == src && h.Consumer != null) { consumer = h.Consumer; break; }
                        if (consumer == null || !consumer.Flowing) continue;
                        float cost = consumer.Rate * share;
                        if (cost > 0f)
                        {
                            if (remaining >= cost) remaining -= cost;
                            else { starved.Add(consumer); continue; }   // over cap -> starve it + don't feed its subtree from this source
                        }
                        var owner = consumer.Owner;
                        if (owner == null) continue;
                        float totalIn = 0f;
                        foreach (var pp in owner.Ports) if (pp.Kind == FluidPortKind.Consumer && pp.Flowing) totalIn += pp.Flow;
                        float subShare = totalIn > 0f ? share * (consumer.Flow / totalIn) : 0f;
                        if (subShare > 0f)
                            foreach (var pp in owner.Ports)
                                if (pp.Kind == FluidPortKind.Passthrough) stack.Push((pp, subShare));
                    }
                }
            // PASS 2 (only if something starved): re-walk the live sources, stopping at starved consumers, and shut off
            // any flowing consumer no longer reachable (the starved loads + everything downstream of them).
            if (starved.Count > 0)
            {
                var reached = new HashSet<FluidPort>();
                foreach (var d in devices)
                    foreach (var o in d.Ports)
                    {
                        if (o.Kind != FluidPortKind.Source || o.Flow <= 0f) continue;
                        var seen = new HashSet<FluidPort>();
                        var stack = new Stack<FluidPort>();
                        stack.Push(o);
                        while (stack.Count > 0)
                        {
                            var src = stack.Pop();
                            if (!seen.Add(src)) continue;
                            FluidPort consumer = null;
                            foreach (var h in hoses) if (h.Source == src && h.Consumer != null) { consumer = h.Consumer; break; }
                            if (consumer == null || !consumer.Flowing || starved.Contains(consumer)) continue;
                            reached.Add(consumer);
                            if (consumer.Owner != null)
                                foreach (var pp in consumer.Owner.Ports)
                                    if (pp.Kind == FluidPortKind.Passthrough) stack.Push(pp);
                        }
                    }
                foreach (var d in devices)
                    foreach (var p in d.Ports)
                        if (p.Kind == FluidPortKind.Consumer && p.Flowing && !reached.Contains(p))
                            p.Flowing = false;
            }

            // per-source LOAD: trace each source's chain/tree and sum the demand of every flowing consumer it feeds (the
            // source's usage bar). A splitter forks the trace, so this is a tree walk, not a single chain.
            foreach (var d in devices)
                foreach (var p in d.Ports)
                    if (p.Kind == FluidPortKind.Source)
                        p.Load = TraceLoad(p, hoses);
        }

        static float TraceLoad(FluidPort source, IReadOnlyList<FluidHose> hoses)
        {
            float draw = 0f;
            var seen = new HashSet<FluidPort>();   // guard against a hosing cycle
            var stack = new Stack<(FluidPort port, float share)>();
            stack.Push((source, 1f));   // share = the fraction of the downstream load THIS source carries (a combiner splits it)
            while (stack.Count > 0)
            {
                var (src, share) = stack.Pop();
                if (!seen.Add(src)) continue;
                FluidPort consumer = null;
                foreach (var h in hoses)   // the one hose fed by this source port (1 hose/port)
                    if (h.Source == src && h.Consumer != null) { consumer = h.Consumer; break; }
                if (consumer == null) continue;
                if (consumer.Flowing) draw += consumer.Rate * share;   // a splitter/combiner relay input is 0-rate -> adds nothing itself
                var owner = consumer.Owner;
                if (owner == null) continue;
                // continue into the device's passthrough(s). A COMBINER has two inputs feeding one output: this source
                // carries the downstream load only in PROPORTION to what it provides (its input / the device's total in),
                // so two sources SPLIT the load. Single-input devices (storage/consumer/splitter) -> ratio 1, unchanged.
                float totalIn = 0f;
                foreach (var pp in owner.Ports)
                    if (pp.Kind == FluidPortKind.Consumer && pp.Flowing) totalIn += pp.Flow;
                float subShare = totalIn > 0f ? share * (consumer.Flow / totalIn) : 0f;
                if (subShare > 0f)
                    foreach (var pp in owner.Ports)   // fork into ALL the owner's passthroughs (a splitter re-exports several)
                        if (pp.Kind == FluidPortKind.Passthrough) stack.Push((pp, subShare));
            }
            return draw;
        }
    }
}
