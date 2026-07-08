using Godot;

namespace UnturnedGodot
{
    // Bridges the engine-agnostic SimRoot to Godot's frame loop. The sim runs on its OWN 50 Hz fixed
    // clock (SimClock, decoupled from Godot's physics tick) so simulation rate matches retail Unturned
    // regardless of the host's framerate -- the same reason the dedicated server ticks identically.
    // Add stepped systems (movement, AI, combat, replication) via Sim.Add(...).
    public partial class SimDriver : Node
    {
        public readonly SDG.Unturned.SimRoot Sim = new SDG.Unturned.SimRoot();

        public long Tick => Sim.Clock.Tick;
        public double SimTime => Sim.Clock.SimTime;

        public override void _PhysicsProcess(double delta)
        {
            Sim.Frame(delta);
        }
    }
}
