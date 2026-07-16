using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // L1 power-net tests -- the wire-power facts that used to be the [POWERTEST]/[MANAGETEST]/[FIRETEST] prints across
    // four separate UG_WIRE* engine boots, now four assertions sharing ONE boot. All Recompute-driven (no ramp), so
    // deterministic without waiting on the lamp warmup envelope.
    static class PowerRig
    {
        public struct Built { public Deployable Gen, Spot; public Wire Wire; public ConnectionPort GenOut, ConsA, PassA; }
        public static Built Build(Node3D world)
        {
            var gen = Deployable.Spawn(world, DeployableDef.Generator, new Vector3(-2f, 0f, 0f), 0f);
            var spot = Deployable.Spawn(world, DeployableDef.Spotlight, new Vector3(2f, 0f, 0f), 0f);
            var outp = gen.Ports.Find(p => p.Kind == DeployableDef.PortKind.Output);
            var cons = spot.Ports.Find(p => p.Kind == DeployableDef.PortKind.Consumer);
            var pass = spot.Ports.Find(p => p.Kind == DeployableDef.PortKind.Passthrough);
            var w = new Wire(); world.AddChild(w);
            w.Source = outp; w.Consumer = cons; w.AddToGroup("wires");
            w.SetPoints(new List<Vector3> { outp.GlobalPosition, cons.GlobalPosition }, true);
            return new Built { Gen = gen, Spot = spot, Wire = w, GenOut = outp, ConsA = cons, PassA = pass };
        }
        public static bool Approx(float a, float b) => Mathf.Abs(a - b) < 0.5f;
    }

    public class PowerGenPowersSpotlight : GameTest
    {
        public override string Name => "power.gen_powers_spotlight";
        public override IEnumerable<Step> Run()
        {
            var r = PowerRig.Build(World);
            yield return Ticks(1);                                   // let the nodes finish entering the tree
            r.Gen.TogglePower(); PowerNet.Recompute(Tree);
            T.Check("generator running", r.Gen.IsPowered);
            T.Check($"output produces 4000w (got {r.GenOut.Live:0})", PowerRig.Approx(r.GenOut.Live, 4000f));
            T.Check("consumer powered", r.ConsA.Powered);
            T.Check($"consumer receives 4000w (got {r.ConsA.Live:0})", PowerRig.Approx(r.ConsA.Live, 4000f));
            T.Check($"passthrough re-exports 3750w (got {r.PassA.Live:0})", PowerRig.Approx(r.PassA.Live, 3750f));
            T.Check($"generator draw = 250w load (got {r.GenOut.Draw:0})", PowerRig.Approx(r.GenOut.Draw, 250f));
        }
    }

    public class PowerWireClearUnpowers : GameTest
    {
        public override string Name => "power.wire_clear_unpowers";
        public override IEnumerable<Step> Run()
        {
            var r = PowerRig.Build(World);
            yield return Ticks(1);
            r.Gen.TogglePower(); PowerNet.Recompute(Tree);
            T.Check("powered before clear", r.ConsA.Powered);
            r.Wire.RemoveFromGroup("wires"); PowerNet.Recompute(Tree);   // clearing a wire drops it from the group
            T.Check("consumer unpowered after wire cleared", !r.ConsA.Powered);
        }
    }

    public class PowerFireStopsConduction : GameTest
    {
        public override string Name => "power.fire_stops_conduction";
        public override IEnumerable<Step> Run()
        {
            var r = PowerRig.Build(World);
            yield return Ticks(1);
            r.Gen.TogglePower(); PowerNet.Recompute(Tree);
            T.Check("powered before fire", r.ConsA.Powered);
            r.Gen.DebugStage("fire"); PowerNet.Recompute(Tree);         // a burning generator stops producing
            T.Check($"on-fire generator output 0w (got {r.GenOut.Live:0})", PowerRig.Approx(r.GenOut.Live, 0f));
            T.Check("consumer unpowered when source on fire", !r.ConsA.Powered);
        }
    }
}
