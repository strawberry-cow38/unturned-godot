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
            var w = Connect(world, outp, cons);
            return new Built { Gen = gen, Spot = spot, Wire = w, GenOut = outp, ConsA = cons, PassA = pass };
        }

        public static Wire Connect(Node3D world, ConnectionPort source, ConnectionPort consumer)
        {
            var w = new Wire(); world.AddChild(w);
            w.Source = source; w.Consumer = consumer; w.AddToGroup("wires");
            w.SetPoints(new List<Vector3> { source.GlobalPosition, consumer.GlobalPosition }, true);
            return w;
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

    // The proposal's flagship case: gen -> spotA -> (passthrough) -> spotB. The second consumer runs on spotA's
    // leftover re-export, and the generator's load traces BOTH consumers down the chain (usage bar + vibration).
    public class PowerChainPassthrough : GameTest
    {
        public override string Name => "power.chain_passthrough";
        public override IEnumerable<Step> Run()
        {
            var r = PowerRig.Build(World);
            var spotB = Deployable.Spawn(World, DeployableDef.Spotlight, new Vector3(6f, 0f, 0f), 0f);
            var consB = spotB.Ports.Find(p => p.Kind == DeployableDef.PortKind.Consumer);
            var passB = spotB.Ports.Find(p => p.Kind == DeployableDef.PortKind.Passthrough);
            PowerRig.Connect(World, r.PassA, consB);
            yield return Ticks(1);
            r.Gen.TogglePower(); PowerNet.Recompute(Tree);
            T.Check("first consumer powered", r.ConsA.Powered);
            T.Check($"second consumer receives the 3750w re-export (got {consB.Live:0})", PowerRig.Approx(consB.Live, 3750f));
            T.Check("second consumer powered through the chain", consB.Powered);
            T.Check($"second passthrough re-exports 3500w (got {passB.Live:0})", PowerRig.Approx(passB.Live, 3500f));
            T.Check($"generator draw traces both consumers = 500w (got {r.GenOut.Draw:0})", PowerRig.Approx(r.GenOut.Draw, 500f));
        }
    }

    // Splitter fan-out (master's own system): gen -> 2-way splitter -> two spotlights. The splitter's input is a
    // 0-watt relay; each of its two passthroughs re-exports the FULL input, so BOTH spotlights receive 4000w and
    // light -- the wattage is NOT divided. The generator's load traces both branches = 2 * 250 = 500w.
    public class PowerSplitterFansOut : GameTest
    {
        public override string Name => "power.splitter_fans_out";
        public override IEnumerable<Step> Run()
        {
            var gen = Deployable.Spawn(World, DeployableDef.Generator, new Vector3(-4f, 0f, 0f), 0f);
            var split = Deployable.Spawn(World, DeployableDef.Splitter2, new Vector3(0f, 0f, 0f), 0f);
            var spotA = Deployable.Spawn(World, DeployableDef.Spotlight, new Vector3(4f, 0f, -2f), 0f);
            var spotB = Deployable.Spawn(World, DeployableDef.Spotlight, new Vector3(4f, 0f, 2f), 0f);
            var genOut = gen.Ports.Find(p => p.Kind == DeployableDef.PortKind.Output);
            var sIn = split.Ports.Find(p => p.Kind == DeployableDef.PortKind.Consumer);
            var sOuts = split.Ports.FindAll(p => p.Kind == DeployableDef.PortKind.Passthrough);
            var consA = spotA.Ports.Find(p => p.Kind == DeployableDef.PortKind.Consumer);
            var consB = spotB.Ports.Find(p => p.Kind == DeployableDef.PortKind.Consumer);
            T.Check($"2-way splitter has 1 input + 2 outputs (got {sOuts.Count} outputs)", sIn != null && sOuts.Count == 2);
            PowerRig.Connect(World, genOut, sIn);
            PowerRig.Connect(World, sOuts[0], consA);
            PowerRig.Connect(World, sOuts[1], consB);
            yield return Ticks(1);
            gen.TogglePower(); PowerNet.Recompute(Tree);
            T.Check("splitter input relays power (0w consumer, powered)", sIn.Powered);
            T.Check($"output 0 carries the FULL 4000w (got {sOuts[0].Live:0})", PowerRig.Approx(sOuts[0].Live, 4000f));
            T.Check($"output 1 carries the FULL 4000w (got {sOuts[1].Live:0})", PowerRig.Approx(sOuts[1].Live, 4000f));
            T.Check("spotlight A powered through the splitter", consA.Powered);
            T.Check("spotlight B powered through the splitter", consB.Powered);
            T.Check($"generator load traces BOTH branches = 500w (got {genOut.Draw:0})", PowerRig.Approx(genOut.Draw, 500f));
        }
    }

    // Combiner (master's own system): two generators -> a 2-way combiner -> one spotlight. The combiner's output is
    // the two gens ADDED (8000w), the spotlight runs, and the 250w load splits evenly across the two equal sources.
    public class PowerCombinerSumsSources : GameTest
    {
        public override string Name => "power.combiner_sums_sources";
        public override IEnumerable<Step> Run()
        {
            var genA = Deployable.Spawn(World, DeployableDef.Generator, new Vector3(-4f, 0f, -2f), 0f);
            var genB = Deployable.Spawn(World, DeployableDef.Generator, new Vector3(-4f, 0f, 2f), 0f);
            var comb = Deployable.Spawn(World, DeployableDef.Combiner2, new Vector3(0f, 0f, 0f), 0f);
            var spot = Deployable.Spawn(World, DeployableDef.Spotlight, new Vector3(4f, 0f, 0f), 0f);
            var outA = genA.Ports.Find(p => p.Kind == DeployableDef.PortKind.Output);
            var outB = genB.Ports.Find(p => p.Kind == DeployableDef.PortKind.Output);
            var ins = comb.Ports.FindAll(p => p.Kind == DeployableDef.PortKind.Consumer);
            var cOut = comb.Ports.Find(p => p.Kind == DeployableDef.PortKind.Passthrough);
            var cons = spot.Ports.Find(p => p.Kind == DeployableDef.PortKind.Consumer);
            T.Check($"2-way combiner has 2 inputs + 1 output (got {ins.Count} in)", ins.Count == 2 && cOut != null);
            PowerRig.Connect(World, outA, ins[0]);
            PowerRig.Connect(World, outB, ins[1]);
            PowerRig.Connect(World, cOut, cons);
            yield return Ticks(1);
            genA.TogglePower(); genB.TogglePower(); PowerNet.Recompute(Tree);
            T.Check("both combiner inputs powered by their sources", ins[0].Powered && ins[1].Powered);
            T.Check($"output = both gens ADDED = 8000w (got {cOut.Live:0})", PowerRig.Approx(cOut.Live, 8000f));
            T.Check("spotlight powered off the combined output", cons.Powered);
            T.Check($"the 250w load splits evenly across the two sources = 125w each (got A {outA.Draw:0} / B {outB.Draw:0})", PowerRig.Approx(outA.Draw, 125f) && PowerRig.Approx(outB.Draw, 125f));
        }
    }

    // Hold-F pickup (master): picking a wired deployable back up frees its wires (Deployable.Pickup) + despawns it --
    // same wire teardown as a wreck, but it returns to the bag instead of shattering. The generator's load drops to 0.
    public class PowerPickupFreesWires : GameTest
    {
        public override string Name => "power.pickup_frees_wires";
        public override IEnumerable<Step> Run()
        {
            var r = PowerRig.Build(World);
            yield return Ticks(1);
            r.Gen.TogglePower(); PowerNet.Recompute(Tree);
            T.Check("powered before pickup", r.ConsA.Powered);
            r.Spot.Pickup();                 // hold-F pickup: frees the wire + despawns (no husk, unlike a wreck)
            yield return Ticks(2);           // let the QueueFrees flush out of the tree/groups
            T.Check("wire freed when the spotlight was picked up", !GodotObject.IsInstanceValid(r.Wire));
            T.Check("picked-up spotlight removed from the tree", !GodotObject.IsInstanceValid(r.Spot));
            PowerNet.Recompute(Tree);
            T.Check($"generator load back to 0w (got {r.GenOut.Draw:0})", PowerRig.Approx(r.GenOut.Draw, 0f));
        }
    }

    // Gas pump power input (master): a Gas_Pump_0 world FIXTURE (not a Deployable -- an IPowerDevice) joins the power
    // net with a 750w consumer port. Wire a running generator to it and its On/Off flag (IsPowered) flips true; the
    // generator's load reflects the 750w. Proves the power net keys on the interface, not the concrete Deployable.
    public class PowerGasPumpPowers : GameTest
    {
        public override string Name => "power.gas_pump_powers";
        public override IEnumerable<Step> Run()
        {
            var gen = Deployable.Spawn(World, DeployableDef.Generator, new Vector3(-3f, 0f, 0f), 0f);
            var pump = GasPump.Attach(World, new Vector3(3f, 0f, 0f), Basis.Identity, GasPump.PortLocal);
            var genOut = gen.Ports.Find(p => p.Kind == DeployableDef.PortKind.Output);
            var pumpIn = pump.PowerPorts[0];
            yield return Ticks(1);
            T.Check("gas pump has a 750w consumer input", pumpIn != null && pumpIn.Kind == DeployableDef.PortKind.Consumer && PowerRig.Approx(pumpIn.Watts, 750f));
            T.Check("gas pump OFF before wiring", !pump.IsPowered);
            PowerRig.Connect(World, genOut, pumpIn);
            gen.TogglePower(); PowerNet.Recompute(Tree);
            T.Check("gas pump ON once wired to a running generator", pump.IsPowered);
            T.Check($"generator load = the pump's 750w (got {genOut.Draw:0})", PowerRig.Approx(genOut.Draw, 750f));
        }
    }

    // Gas pump fuel extraction (master's fluids): you can only pull fuel from a POWERED pump. Unpowered -> nothing;
    // wire a running generator to it -> Extract(space) drains the shared station tank by that much (min of space/remaining).
    public class PowerGasPumpExtract : GameTest
    {
        public override string Name => "power.gas_pump_extract";
        public override IEnumerable<Step> Run()
        {
            StationFuel.Reset();   // fresh shared station tanks for isolation
            var gen = Deployable.Spawn(World, DeployableDef.Generator, new Vector3(-3f, 0f, 0f), 0f);
            var pump = GasPump.Attach(World, new Vector3(3f, 0f, 0f), Basis.Identity, GasPump.PortLocal);
            var genOut = gen.Ports.Find(p => p.Kind == DeployableDef.PortKind.Output);
            var pumpIn = pump.PowerPorts[0];
            yield return Ticks(1);
            float full = pump.Fluid.Amount;
            T.Check("unpowered pump gives no fuel", pump.Extract(8f) == 0f);
            PowerRig.Connect(World, genOut, pumpIn);
            gen.TogglePower(); PowerNet.Recompute(Tree);
            float pulled = pump.Extract(8f);   // fill an 8-unit can from the powered pump
            T.Check($"powered pump extracts 8 (got {pulled:0})", PowerRig.Approx(pulled, 8f));
            T.Check($"station tank drained by 8 (got {full - pump.Fluid.Amount:0})", PowerRig.Approx(full - pump.Fluid.Amount, 8f));
        }
    }

    // Grid power source (SP grid-power feature): a Circuit_0 breaker box is a GridPowerSource -- a 10kW OUTPUT that
    // produces while the global mains flag (PowerNet.GlobalPower) is ON. Wire it to a spotlight consumer and prove the
    // FLAG is the gate: OFF -> unpowered; toggleGlobalPower -> ON -> powered (consumer receives the 10kW export, draws
    // its 250w usage as the source's load); toggle OFF -> dark again. The SAME `cons.Powered` assert is checked BOTH
    // false and true in one run, gated only by the flag => teeth: it flips WITH the flag, not by luck. Also proves the
    // ownerless-usable path -- the grid source (NOT a Deployable) owns a Usable, wire-able output via IPowerDevice.
    public class PowerGridSourcePowers : GameTest
    {
        public override string Name => "power.grid_source_powers";
        public override IEnumerable<Step> Run()
        {
            PowerNet.SetGlobalPower(false);   // defensive: the grid starts OFF (default), independent of test order
            var grid = GridPowerSource.Attach(World, new Vector3(-3f, 0f, 0f), Basis.Identity, GridPowerSource.PortLocal);
            var spot = Deployable.Spawn(World, DeployableDef.Spotlight, new Vector3(3f, 0f, 0f), 0f);
            var gridOut = grid.PowerPorts[0];
            var cons = spot.Ports.Find(p => p.Kind == DeployableDef.PortKind.Consumer);
            var pass = spot.Ports.Find(p => p.Kind == DeployableDef.PortKind.Passthrough);
            PowerRig.Connect(World, gridOut, cons);
            yield return Ticks(1);

            T.Check("grid source owns a 10kW output port", gridOut != null && gridOut.Kind == DeployableDef.PortKind.Output && PowerRig.Approx(gridOut.Watts, 10000f));
            T.Check("grid output is wire-able (Usable) though the owner is NOT a Deployable", gridOut.Usable);

            // globalPower OFF (default) -> the mains are dead -> the consumer stays UNPOWERED
            PowerNet.Recompute(Tree);
            T.Check($"OFF: grid output produces 0w (got {gridOut.Live:0})", PowerRig.Approx(gridOut.Live, 0f));
            T.Check("OFF: consumer unpowered", !cons.Powered);
            T.Check($"OFF: consumer receives 0w (got {cons.Live:0})", PowerRig.Approx(cons.Live, 0f));

            // toggleGlobalPower -> ON -> the 10kW mains energize the wire -> the consumer runs off the budget
            bool nowOn = PowerNet.ToggleGlobalPower(); PowerNet.Recompute(Tree);
            T.Check("toggle flips the grid ON", nowOn && PowerNet.GlobalPower);
            T.Check($"ON: grid output produces its 10000w (got {gridOut.Live:0})", PowerRig.Approx(gridOut.Live, 10000f));
            T.Check("ON: consumer powered", cons.Powered);
            T.Check($"ON: consumer receives the 10kW export (got {cons.Live:0})", PowerRig.Approx(cons.Live, 10000f));
            T.Check($"ON: consumer's 250w usage is drawn from the budget -> grid load 250w (got {gridOut.Draw:0})", PowerRig.Approx(gridOut.Draw, 250f));
            T.Check($"ON: passthrough re-exports the 9750w leftover (got {pass.Live:0})", PowerRig.Approx(pass.Live, 9750f));

            // toggle back OFF -> dark again: the SAME consumer assert, now false, proving the flag is the gate
            bool nowOff = PowerNet.ToggleGlobalPower(); PowerNet.Recompute(Tree);
            T.Check("toggle flips the grid OFF", !nowOff && !PowerNet.GlobalPower);
            T.Check($"OFF again: grid output back to 0w (got {gridOut.Live:0})", PowerRig.Approx(gridOut.Live, 0f));
            T.Check("OFF again: consumer unpowered once the grid is toggled off", !cons.Powered);
        }
    }

    // The UG_WIREWRECK fact (strawberry): wrecking a wired spotlight must take its wire + port cubes with it --
    // the spotlight shatters (ShatterOnDeath -> no husk), the wire is freed, and the generator's load drops to 0.
    public class PowerWreckDropsWires : GameTest
    {
        public override string Name => "power.wreck_drops_wires";
        public override IEnumerable<Step> Run()
        {
            var r = PowerRig.Build(World);
            yield return Ticks(1);
            r.Gen.TogglePower(); PowerNet.Recompute(Tree);
            T.Check("powered before wreck", r.ConsA.Powered);

            r.Spot.DebugStage("wreck");   // Explode -> KillPowerHardware (wires snapped, ports retired) -> shatter
            yield return Ticks(2);        // let the QueueFrees flush out of the tree/groups
            T.Check("wire freed with the wrecked spotlight", !GodotObject.IsInstanceValid(r.Wire));
            T.Check("shattered spotlight removed from the tree (no husk)", !GodotObject.IsInstanceValid(r.Spot));
            PowerNet.Recompute(Tree);
            T.Check($"generator load back to 0w (got {r.GenOut.Draw:0})", PowerRig.Approx(r.GenOut.Draw, 0f));
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
