using System.Collections.Generic;

namespace SDG.Unturned
{
    // The wire-power propagation algorithm, extracted engine-free from the game's PowerNet (proposal phase 3):
    // a producing generator OUTPUT pushes its watts down a wire to a CONSUMER; the consumer is powered if it
    // receives at least its usage, and its PASSTHROUGH re-exports the leftover (received - usage) down the next
    // wire. Iterated so chains (genny -> spot -> spot -> ...) settle in one Solve. A SPLITTER is just a device
    // whose input is a 0-watt consumer (a relay -- takes nothing for itself) with SEVERAL passthroughs: each
    // re-exports the full input (leftover = input - 0), so one wire fans out to 2/3/4 wires without dividing the
    // wattage (each downstream device draws what it needs). The game's PowerNet.Recompute is a thin adapter: it
    // walks the "deployables"/"wires" groups into these plain records, calls Solve, and writes Live/Powered/Draw
    // back to the ConnectionPort nodes.
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
                    PowerPort cons = null;
                    foreach (var p in d.Ports)
                        if (p.Kind == PowerPortKind.Consumer) { cons = p; break; }
                    if (cons != null)
                    {
                        // a normal consumer (Watts>0) needs at least its usage; a 0-watt RELAY (a splitter's input)
                        // just needs any live input, since it takes nothing for itself.
                        bool hasInput = cons.Watts > 0f ? cons.Live >= cons.Watts : cons.Live > 0f;
                        cons.Powered = !d.OnFire && hasInput;   // a burning consumer stops conducting
                        float exported = cons.Powered ? cons.Live - cons.Watts : 0f;   // leftover (spotlight) or the FULL input (splitter, Watts=0)
                        foreach (var p in d.Ports)   // re-export to EVERY passthrough -> a splitter fans one input out to N outputs, each carrying the full power
                            if (p.Kind == PowerPortKind.Passthrough) p.Live = exported;
                    }
                }
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
            var stack = new Stack<PowerPort>();
            stack.Push(output);
            while (stack.Count > 0)
            {
                var src = stack.Pop();
                if (!seen.Add(src)) continue;
                PowerPort consumer = null;
                foreach (var w in wires)   // the one wire fed by this source port (1 wire/port)
                    if (w.Source == src && w.Consumer != null) { consumer = w.Consumer; break; }
                if (consumer == null) continue;
                if (consumer.Powered) draw += consumer.Watts;   // a splitter's own input is 0w -> adds nothing itself
                if (consumer.Owner != null)
                    foreach (var pp in consumer.Owner.Ports)   // fork into ALL the owner's passthroughs (a splitter re-exports several)
                        if (pp.Kind == PowerPortKind.Passthrough) stack.Push(pp);
            }
            return draw;
        }
    }
}
