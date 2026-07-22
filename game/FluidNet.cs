using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // A fluid port on a container (mirror of ConnectionPort for the power net, minus the visual cube for now — the
    // interactive port cubes + hose tool come with placement in a later phase). A source PUSHES its supply rate; a
    // consumer/storage DRAWS its intake. FluidNet writes the solver results back here each tick (for bars/debug).
    public class FluidPortNode
    {
        public FluidPortKind Kind;     // Source / Consumer / Passthrough (SDG.Unturned solver enum)
        public float Rate;             // source: base supply rate; consumer/storage: intake rate (units/s)
        public FluidContainer Owner;
        // solver results:
        public float Flow;             // source = supplied now, consumer = received (available), passthrough = exported
        public bool Flowing;           // consumer: getting at least its intake?
        public float Load;             // source only: total flow drawn down its chain
        public float SolveRate;        // the clamped demand/supply the solver used this tick (a consumer ACCEPTS this, not Flow)
    }

    // Fluid propagation over the hose graph — the thin Godot adapter over the engine-free FluidSolver (mirror of
    // PowerNet). Unlike power (static until an event), fluid AMOUNTS change continuously, so this ticks every frame:
    // clamp each source's supply by its remaining amount (+ each storage's intake by its free space), Solve the flow
    // rates, then MOVE the actual fluid — source.Drain(load·dt), storage.Fill(received·dt), consumers delete.
    public static class FluidNet
    {
        public static void Tick(SceneTree tree, float dt)
        {
            if (dt <= 0f) return;
            float inv = 1f / dt;
            var devices = new System.Collections.Generic.List<FluidDevice>();
            var portMap = new System.Collections.Generic.Dictionary<FluidPortNode, FluidPort>();
            var containers = new System.Collections.Generic.List<FluidContainer>();
            var transformers = new System.Collections.Generic.List<FluidContainer>();
            var allC = new System.Collections.Generic.List<FluidContainer>();   // every device (incl. fittings) for pump-lift propagation

            foreach (var n in tree.GetNodesInGroup("fluid_devices"))
                if (n is FluidContainer c && GodotObject.IsInstanceValid(c))
                {
                    allC.Add(c);
                    bool hasTank = c.Tank != null;   // a fitting (splitter/combiner/pump/transformer) is tankless -> no clamp/move
                    // a source supplies only while it has fluid; a TRANSFORMER supplies its output only while its input flowed
                    // last tick (1-tick lag, TransformActive); a valve/broken container blocks (F5+).
                    bool supplying = c.Role == FluidRole.Transformer ? c.TransformActive
                                   : (hasTank && c.Role == FluidRole.Source && c.Tank.Amount > 0.001f);
                    var dev = new FluidDevice { Supplying = supplying, Blocked = c.Blocked };
                    foreach (var p in c.Ports)
                    {
                        float rate = p.Rate;
                        // clamp a source's supply by what's left (so a near-empty source supplies less; empties cleanly)
                        if (hasTank && c.Role == FluidRole.Source && p.Kind == FluidPortKind.Source)
                            rate = Mathf.Min(rate, c.Tank.Amount * inv);
                        // a storage can't take more than fits (near-full draws less; full -> 0 -> stops flowing)
                        else if (hasTank && c.Role == FluidRole.Storage && p.Kind == FluidPortKind.Consumer)
                            rate = Mathf.Min(rate, c.Tank.Space * inv);
                        portMap[p] = dev.AddPort(p.Kind, rate);
                    }
                    devices.Add(dev);
                    if (hasTank) containers.Add(c);   // only tanked containers move fluid (fittings just relay)
                    if (c.Role == FluidRole.Transformer) transformers.Add(c);
                }

            // GRAVITY GATE + PUMP LIFT (strawberry 2026-07-22): a hose conducts passively only DOWNHILL (consumer below
            // source). Uphill/level needs a powered PUMP's head lift. A pump's head sets a pressure CEILING = its world-Y +
            // HeadLift, and that ceiling carries THROUGH relay fittings (splitter/combiner/pump) to the whole connected
            // chain, but STOPS at a tank or a transformer ("up to a source/consumer, not through it"). So a hose conducts
            // uphill if its consumer end sits at/below the best ceiling reachable at either of its ends. Gated here (not in
            // the type-agnostic solver) since elevation is a world concept; a blocked hose is left out -> its ports read 0.
            const float HeadEps = 0.05f;   // ignore sub-5cm height noise (same-level tanks don't dribble)

            // relax the pump pressure ceiling across the hose graph, propagating only OUT OF relay-fitting nodes
            var ceiling = new System.Collections.Generic.Dictionary<FluidContainer, float>();
            foreach (var c in allC) ceiling[c] = (c is FluidPump p && p.IsPowered) ? c.GlobalPosition.Y + p.HeadLift : float.NegativeInfinity;
            var rawHoses = new System.Collections.Generic.List<(FluidContainer A, FluidContainer B)>();
            foreach (var n in tree.GetNodesInGroup("hoses"))
                if (n is Hose h && h.Source?.Owner is FluidContainer a && h.Consumer?.Owner is FluidContainer b)
                    rawHoses.Add((a, b));
            for (int pass = 0; pass <= rawHoses.Count; pass++)
            {
                bool changed = false;
                foreach (var (a, b) in rawHoses)
                {
                    if (a.IsFlowRelay && ceiling[a] > ceiling[b]) { ceiling[b] = ceiling[a]; changed = true; }   // ceiling passes THROUGH a
                    if (b.IsFlowRelay && ceiling[b] > ceiling[a]) { ceiling[a] = ceiling[b]; changed = true; }   // ...and through b
                }
                if (!changed) break;
            }

            var hoses = new System.Collections.Generic.List<FluidHose>();
            foreach (var n in tree.GetNodesInGroup("hoses"))
                if (n is Hose h && h.Source != null && h.Consumer != null
                    && portMap.TryGetValue(h.Source, out var s) && portMap.TryGetValue(h.Consumer, out var cons))
                {
                    var aO = h.Source.Owner; var bO = h.Consumer.Owner;
                    float srcY = aO != null ? aO.GlobalPosition.Y : 0f;
                    float consY = bO != null ? bO.GlobalPosition.Y : 0f;
                    float reach = Mathf.Max(aO != null && ceiling.TryGetValue(aO, out var ra) ? ra : float.NegativeInfinity,
                                            bO != null && ceiling.TryGetValue(bO, out var rb) ? rb : float.NegativeInfinity);
                    if (consY < srcY - HeadEps || consY < reach - HeadEps) hoses.Add(new FluidHose(s, cons));   // downhill, or within a powered pump's reach
                }

            FluidSolver.Solve(devices, hoses);

            foreach (var kv in portMap) { kv.Key.Flow = kv.Value.Flow; kv.Key.Flowing = kv.Value.Flowing; kv.Key.Load = kv.Value.Load; kv.Key.SolveRate = kv.Value.Rate; }

            // move the actual fluid this tick. Conservation: a source drains by the total LOAD it feeds (= sum of the
            // downstream demands it charges); each storage fills by what it ACCEPTS = its demand (SolveRate), NOT the
            // Flow it RECEIVES. Flow is the amount OFFERED down the hose — a splitter re-exports the full supply to every
            // branch, so a storage sees far more available than it draws; filling by Flow would fabricate fluid (the
            // source only drained by the demand). SolveRate is the clamped intake the solver charged the source for.
            foreach (var c in containers)
                foreach (var p in c.Ports)
                {
                    if (c.Role == FluidRole.Source && p.Kind == FluidPortKind.Source && p.Load > 0f)
                        c.Tank.Drain(p.Load * dt);
                    else if (c.Role == FluidRole.Storage && p.Kind == FluidPortKind.Consumer && p.Flowing)
                        c.Tank.Fill(p.SolveRate * dt);
                    // FluidRole.Consumer: the fluid is deleted (nothing accumulates).
                    c.LastFlow = p.Flowing ? p.SolveRate : 0f;
                }

            // TRANSFORMERS (refinery/sluice): tankless, so no amount is moved — the input fluid is DELETED and the output
            // fluid is CREATED (intentionally not conserved). Latch whether the input received flow THIS tick; that gates
            // whether the output SUPPLIES next tick (a 1-tick startup lag). Ports[0]=Consumer input, Ports[1]=Source output.
            foreach (var t in transformers)
                t.TransformActive = t.Ports.Count > 0 && t.Ports[0].Flowing;
        }
    }

    // Ticks the fluid net once a frame (mirror of PowerManager). One instance — joins group "fluid_managers" so
    // spawners can avoid creating a second (two managers = double-ticked flow).
    public partial class FluidManager : Node
    {
        public override void _Ready() => AddToGroup("fluid_managers");
        public override void _Process(double delta) => FluidNet.Tick(GetTree(), (float)delta);
    }
}
