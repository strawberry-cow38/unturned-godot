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
        public float Flow;             // source = supplied now, consumer = received, passthrough = exported
        public bool Flowing;           // consumer: getting at least its intake?
        public float Load;             // source only: total flow drawn down its chain
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

            foreach (var n in tree.GetNodesInGroup("fluid_devices"))
                if (n is FluidContainer c && GodotObject.IsInstanceValid(c) && c.Tank != null)
                {
                    containers.Add(c);
                    // a source supplies only while it has fluid; a valve/broken container blocks (F5+).
                    bool supplying = c.Role == FluidRole.Source && c.Tank.Amount > 0.001f;
                    var dev = new FluidDevice { Supplying = supplying, Blocked = c.Blocked };
                    foreach (var p in c.Ports)
                    {
                        float rate = p.Rate;
                        // clamp a source's supply by what's left (so a near-empty source supplies less; empties cleanly)
                        if (c.Role == FluidRole.Source && p.Kind == FluidPortKind.Source)
                            rate = Mathf.Min(rate, c.Tank.Amount * inv);
                        // a storage can't take more than fits (near-full draws less; full -> 0 -> stops flowing)
                        else if (c.Role == FluidRole.Storage && p.Kind == FluidPortKind.Consumer)
                            rate = Mathf.Min(rate, c.Tank.Space * inv);
                        portMap[p] = dev.AddPort(p.Kind, rate);
                    }
                    devices.Add(dev);
                }

            // GRAVITY GATE (strawberry 2026-07-22, all fluids): a hose conducts passively only DOWNHILL — the consumer
            // end must sit below the source end. Level or uphill = the hose stays connected but carries 0 flow until an
            // electric pump on the line gives it head lift (F5). We gate here (not in the type-agnostic solver) since
            // elevation is a world concept; a non-conducting hose is simply left out of the solver graph -> both its
            // ports read Flow=0, which the HUD surfaces as "needs a pump".
            const float HeadEps = 0.05f;   // ignore sub-5cm height noise (same-level tanks don't dribble)
            var hoses = new System.Collections.Generic.List<FluidHose>();
            foreach (var n in tree.GetNodesInGroup("hoses"))
                if (n is Hose h && h.Source != null && h.Consumer != null
                    && portMap.TryGetValue(h.Source, out var s) && portMap.TryGetValue(h.Consumer, out var cons))
                {
                    float srcY = h.Source.Owner != null ? h.Source.Owner.GlobalPosition.Y : 0f;
                    float consY = h.Consumer.Owner != null ? h.Consumer.Owner.GlobalPosition.Y : 0f;
                    bool downhill = consY < srcY - HeadEps;
                    bool pumped = false;   // F5: an electric pump on the line overrides gravity (head lift)
                    if (downhill || pumped) hoses.Add(new FluidHose(s, cons));
                }

            FluidSolver.Solve(devices, hoses);

            foreach (var kv in portMap) { kv.Key.Flow = kv.Value.Flow; kv.Key.Flowing = kv.Value.Flowing; kv.Key.Load = kv.Value.Load; }

            // move the actual fluid this tick. Single-chain conservation: a source drains by the total load it feeds
            // (already capped by its amount above), each storage fills by what it received, consumers delete their draw.
            foreach (var c in containers)
                foreach (var p in c.Ports)
                {
                    if (c.Role == FluidRole.Source && p.Kind == FluidPortKind.Source && p.Load > 0f)
                        c.Tank.Drain(p.Load * dt);
                    else if (c.Role == FluidRole.Storage && p.Kind == FluidPortKind.Consumer && p.Flowing)
                        c.Tank.Fill(p.Flow * dt);
                    // FluidRole.Consumer: the fluid is deleted (nothing accumulates) — transformer output is F5.
                    c.LastFlow = p.Flow;
                }
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
