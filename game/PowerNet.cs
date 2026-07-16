using Godot;

namespace UnturnedGodot
{
    // Power propagation over the wire graph: a producing generator OUTPUT pushes its watts down a wire to a CONSUMER;
    // the consumer is powered if it receives at least its usage, and its PASSTHROUGH re-exports the leftover
    // (received - usage) down the next wire. Iterated so chains (genny -> spot -> spot -> ...) settle each tick.
    public static class PowerNet
    {
        // The graph only changes on discrete events (wire built/cleared, deployable placed/removed, generator toggled,
        // something catches fire) -- nothing per-frame -- so recompute is event-driven, not every frame. MarkDirty()
        // flags state changes; the wire/deployable COUNT is a backstop that catches any structural add/remove for free.
        static bool _dirty = true;
        static int _lastWires = -1, _lastDeployables = -1;
        public static void MarkDirty() => _dirty = true;

        public static void RecomputeIfDirty(SceneTree tree)
        {
            int w = tree.GetNodeCountInGroup("wires"), d = tree.GetNodeCountInGroup("deployables");
            if (!_dirty && w == _lastWires && d == _lastDeployables) return;   // idle: nothing changed -> skip the whole O(W*(W+D)) pass
            _dirty = false; _lastWires = w; _lastDeployables = d;
            Recompute(tree);
        }

        public static void Recompute(SceneTree tree)
        {
            var deployables = tree.GetNodesInGroup("deployables");
            var wires = tree.GetNodesInGroup("wires");

            // reset: outputs produce (if their source is on); consumer/passthrough start at 0
            foreach (var n in deployables)
                if (n is Deployable d)
                    foreach (var p in d.Ports)
                    {
                        if (p == null || !GodotObject.IsInstanceValid(p)) continue;
                        if (p.Kind == DeployableDef.PortKind.Output) p.Live = d.IsPowered ? p.Watts : 0f;
                        else { p.Live = 0f; p.Powered = false; }
                    }

            // propagate one hop per pass; wires.Count+1 passes fully settles any chain
            int passes = wires.Count + 1;
            for (int k = 0; k < passes; k++)
            {
                foreach (var n in wires)
                    if (n is Wire w && GodotObject.IsInstanceValid(w.Source) && GodotObject.IsInstanceValid(w.Consumer))
                        w.Consumer.Live = w.Source.Live;   // the consumer receives whatever the source is exporting
                foreach (var n in deployables)
                    if (n is Deployable d)
                    {
                        ConnectionPort cons = null, pass = null;
                        foreach (var p in d.Ports)
                        {
                            if (p == null || !GodotObject.IsInstanceValid(p)) continue;
                            if (p.Kind == DeployableDef.PortKind.Consumer) cons = p;
                            else if (p.Kind == DeployableDef.PortKind.Passthrough) pass = p;
                        }
                        if (cons != null)
                        {
                            cons.Powered = !d.OnFire && cons.Watts > 0f && cons.Live >= cons.Watts;   // a burning/wrecked consumer stops conducting (its passthrough dies with it)
                            if (pass != null) pass.Live = cons.Powered ? cons.Live - cons.Watts : 0f;   // re-export the leftover
                        }
                    }
            }
        }
    }

    // Ticks the power net once a frame. One instance is created lazily by the first placed deployable.
    public partial class PowerManager : Node
    {
        public override void _Process(double delta) => PowerNet.RecomputeIfDirty(GetTree());
    }
}
