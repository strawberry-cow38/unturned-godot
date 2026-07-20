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

    // Battery (master): the IN terminal charges the stored energy, the OUT terminal discharges it. Wire a generator ->
    // battery IN to charge it, then run a spotlight off the battery OUT with the generator off -> it discharges.
    public class PowerBatteryChargeDischarge : GameTest
    {
        public override string Name => "power.battery_charge_discharge";
        public override IEnumerable<Step> Run()
        {
            var gen = Deployable.Spawn(World, DeployableDef.Generator, new Vector3(-3f, 0f, 0f), 0f);
            var bat = Deployable.Spawn(World, DeployableDef.Battery, new Vector3(0f, 0f, 0f), 0f);
            var spot = Deployable.Spawn(World, DeployableDef.Spotlight, new Vector3(3f, 0f, 0f), 0f);
            yield return Ticks(1);
            var genOut = gen.Ports.Find(p => p.Kind == DeployableDef.PortKind.Output);
            var batIn = bat.Ports.Find(p => p.Kind == DeployableDef.PortKind.Consumer);
            var batOut = bat.Ports.Find(p => p.Kind == DeployableDef.PortKind.Output);
            var spotIn = spot.Ports.Find(p => p.Kind == DeployableDef.PortKind.Consumer);
            T.Check("battery has an IN (consumer) + an OUT (output) terminal", batIn != null && batOut != null);
            T.Check("fresh battery is empty -> not producing", !bat.IsPowered && bat.Energy <= 0f);

            PowerRig.Connect(World, genOut, batIn);            // gen OUT -> battery IN
            gen.TogglePower(); PowerNet.Recompute(Tree);
            T.Check("battery IN is fed by the generator", batIn.Powered);
            float e0 = bat.Energy;
            yield return Ticks(60);                            // ~1s charging
            PowerNet.Recompute(Tree);
            T.Check($"battery charged up (energy {bat.Energy:0} > {e0:0})", bat.Energy > e0 + 1f);
            T.Check("a charged battery produces on its OUT", bat.IsPowered);

            PowerRig.Connect(World, batOut, spotIn);           // battery OUT -> spotlight
            gen.NetSetPowered(false); PowerNet.Recompute(Tree);   // generator OFF (force, bypassing the ramp-settle toggle gate) -> the battery is the only source
            yield return Ticks(1); PowerNet.Recompute(Tree);
            T.Check("spotlight runs off the battery with the gen off", spotIn.Powered);
            float e1 = bat.Energy;
            yield return Ticks(60);                            // ~1s discharging
            T.Check($"battery discharges powering the load (energy {bat.Energy:0} < {e1:0})", bat.Energy < e1 - 1f);
        }
    }

    // master's "wire multiple batteries together = a bigger battery": daisy-chain A.OUT -> B.IN so the upstream battery
    // refills the downstream one while it powers a load. Proves the POOLING -- the load's energy is drawn from A's reserve
    // (A drains, B stays topped by A), so A+B back the load together = a bigger effective battery.
    public class PowerBatteryDaisyChain : GameTest
    {
        public override string Name => "power.battery_daisy_chain";
        public override IEnumerable<Step> Run()
        {
            var batA = Deployable.Spawn(World, DeployableDef.Battery, new Vector3(-3f, 0f, 0f), 0f);
            var batB = Deployable.Spawn(World, DeployableDef.Battery, new Vector3(0f, 0f, 0f), 0f);
            var spot = Deployable.Spawn(World, DeployableDef.Spotlight, new Vector3(3f, 0f, 0f), 0f);
            yield return Ticks(1);
            batA.Energy = DeployableDef.Battery.EnergyMax;      // both batteries start full
            batB.Energy = DeployableDef.Battery.EnergyMax;
            var aOut = batA.Ports.Find(p => p.Kind == DeployableDef.PortKind.Output);
            var bIn = batB.Ports.Find(p => p.Kind == DeployableDef.PortKind.Consumer);
            var bOut = batB.Ports.Find(p => p.Kind == DeployableDef.PortKind.Output);
            var spotIn = spot.Ports.Find(p => p.Kind == DeployableDef.PortKind.Consumer);

            PowerRig.Connect(World, aOut, bIn);                 // battery A OUT -> battery B IN  (A refills B)
            PowerRig.Connect(World, bOut, spotIn);              // battery B OUT -> the load
            PowerNet.Recompute(Tree);
            yield return Ticks(1); PowerNet.Recompute(Tree);
            T.Check("load runs off the daisy-chained batteries", spotIn.Powered);

            float a0 = batA.Energy, b0 = batB.Energy;
            yield return Ticks(60);                             // ~1s: B powers the load, A keeps B topped up
            PowerNet.Recompute(Tree);
            T.Check("load still powered after a second", spotIn.Powered);
            T.Check($"upstream battery A drains feeding B (energy {batA.Energy:0} < {a0:0})", batA.Energy < a0 - 1f);
            T.Check($"downstream battery B stays topped by A (energy {batB.Energy:0} >= 90% of {b0:0})", batB.Energy >= b0 * 0.9f);
        }
    }

    // master's Power Switch: a toggle-gated relay -- power passes to its OUT only when ON (PowerConducting gates the passthrough).
    public class PowerSwitchGates : GameTest
    {
        public override string Name => "power.switch_gates";
        public override IEnumerable<Step> Run()
        {
            var gen = Deployable.Spawn(World, DeployableDef.Generator, new Vector3(-3f, 0f, 0f), 0f);
            var sw = Deployable.Spawn(World, DeployableDef.Switch, new Vector3(0f, 0f, 0f), 0f);
            var spot = Deployable.Spawn(World, DeployableDef.Spotlight, new Vector3(3f, 0f, 0f), 0f);
            yield return Ticks(1);
            var genOut = gen.Ports.Find(p => p.Kind == DeployableDef.PortKind.Output);
            var swIn = sw.Ports.Find(p => p.Kind == DeployableDef.PortKind.Consumer);
            var swOut = sw.Ports.Find(p => p.Kind == DeployableDef.PortKind.Passthrough);
            var spotIn = spot.Ports.Find(p => p.Kind == DeployableDef.PortKind.Consumer);
            T.Check("switch has an IN (consumer) + an OUT (passthrough)", swIn != null && swOut != null);

            PowerRig.Connect(World, genOut, swIn);       // gen -> switch IN
            PowerRig.Connect(World, swOut, spotIn);      // switch OUT -> spotlight
            gen.TogglePower(); PowerNet.Recompute(Tree);
            T.Check("switch ON (default) -> spotlight powered through it", spotIn.Powered);

            sw.TogglePower(); PowerNet.Recompute(Tree);  // switch OFF
            T.Check("switch OFF -> spotlight loses power (passthrough dead)", !spotIn.Powered);

            sw.TogglePower(); PowerNet.Recompute(Tree);  // switch back ON
            T.Check("switch back ON -> spotlight powered again (state toggles cleanly)", spotIn.Powered);

            // remote trigger: feed the TurnOff side >=1w -> the switch flips OFF on its own (triggers draw 0w)
            var swOff = sw.Ports.Find(p => p.Role == DeployableDef.SwitchRole.TurnOff);
            var swOn = sw.Ports.Find(p => p.Role == DeployableDef.SwitchRole.TurnOn);
            T.Check("switch has TurnOn + TurnOff trigger ports", swOn != null && swOff != null);
            var gen2 = Deployable.Spawn(World, DeployableDef.Generator, new Vector3(-3f, 0f, 3f), 0f);
            gen2.TogglePower();
            PowerRig.Connect(World, gen2.Ports.Find(p => p.Kind == DeployableDef.PortKind.Output), swOff);
            PowerNet.Recompute(Tree);
            yield return Ticks(3);   // _Process reads the trigger's Live and flips the switch
            PowerNet.Recompute(Tree);
            T.Check("TurnOff trigger fed >=1w -> switch flips OFF -> spotlight dead", !spotIn.Powered);
        }
    }

    // Generator remote on/off (master): a >=1w sense wire on the TurnOn/TurnOff trigger (0w draw) commands the engine
    // on/off -> its startup/cooldown ramp. The switch trigger mechanism applied to a generator's _powered toggle.
    public class PowerGeneratorTriggers : GameTest
    {
        public override string Name => "power.generator_triggers";
        public override IEnumerable<Step> Run()
        {
            PowerNet.ResetForTests();
            var gen = Deployable.Spawn(World, DeployableDef.Generator, new Vector3(0f, 0f, 0f), 0f);   // fuelled, default OFF
            var src = Deployable.Spawn(World, DeployableDef.Generator, new Vector3(-3f, 0f, 0f), 0f);   // the sense source
            src.TogglePower();                                                                          // running -> feeds >=1w
            var srcOut = src.Ports.Find(p => p.Kind == DeployableDef.PortKind.Output);
            var tOn = gen.Ports.Find(p => p.Role == DeployableDef.SwitchRole.TurnOn);
            var tOff = gen.Ports.Find(p => p.Role == DeployableDef.SwitchRole.TurnOff);
            yield return Ticks(1);
            T.Check("generator has TurnOn + TurnOff trigger ports", tOn != null && tOff != null);
            T.Check("triggers draw 0w", tOn.Watts == 0f && tOff.Watts == 0f);
            T.Check("generator starts OFF", !gen.PoweredTarget && !gen.IsPowered);

            var wOn = PowerRig.Connect(World, srcOut, tOn);        // feed the TurnOn trigger >=1w
            PowerNet.Recompute(Tree);
            yield return Ticks(3);                                 // _Process reads the trigger's Live + spins the engine up
            PowerNet.Recompute(Tree);
            T.Check("TurnOn trigger fed >=1w -> generator commanded ON", gen.PoweredTarget);
            T.Check("commanded-on generator produces power", gen.IsPowered);

            wOn.QueueFree();                                       // drop the TurnOn sense (doesn't stop it) then feed TurnOff
            yield return Ticks(1);
            var wOff = PowerRig.Connect(World, srcOut, tOff);
            PowerNet.Recompute(Tree);
            yield return Ticks(3);
            PowerNet.Recompute(Tree);
            T.Check("TurnOff trigger fed >=1w -> generator commanded OFF", !gen.PoweredTarget);
            T.Check("commanded-off generator stops producing", !gen.IsPowered);
        }
    }

    // Wind turbine (master): output ramps with a drifting WindField sample x a height-above-sea multiplier; blades spin
    // ~ the wind. TestWind forces a fixed wind so the noise doesn't flake the checks.
    public class PowerWindTurbine : GameTest
    {
        public override string Name => "power.wind_turbine";
        public override IEnumerable<Step> Run()
        {
            PowerNet.ResetForTests();
            var sea = Deployable.Spawn(World, DeployableDef.WindTurbine, new Vector3(0f, 25.6f, 0f), 0f);   // at PEI sea level (world-Y 25.6)
            var high = Deployable.Spawn(World, DeployableDef.WindTurbine, new Vector3(5f, 65.6f, 0f), 0f);  // +40 m above sea -> ~2x height mult
            var spot = Deployable.Spawn(World, DeployableDef.Spotlight, new Vector3(0f, 0f, 3f), 0f);
            var spotIn = spot.Ports.Find(p => p.Kind == DeployableDef.PortKind.Consumer);
            PowerRig.Connect(World, sea.Ports.Find(p => p.Kind == DeployableDef.PortKind.Output), spotIn);

            WindField.TestWind = 0f;
            yield return Ticks(2); PowerNet.Recompute(Tree);
            T.Check("dead calm -> turbine not producing -> spotlight dead", !sea.IsPowered && !spotIn.Powered);

            WindField.TestWind = 0.6f;
            yield return Ticks(2); PowerNet.Recompute(Tree);
            T.Check("wind 0.6 at sea level -> ~0.6 factor, producing", sea.IsPowered && PowerRig.Approx(sea.WindFactorForTest, 0.6f));
            T.Check("windy turbine powers a wired spotlight", spotIn.Powered);
            T.Check("same wind at +40 m -> ~2x factor (higher = better, master)", PowerRig.Approx(high.WindFactorForTest, 1.2f));

            WindField.TestWind = null;   // restore live wind for other tests
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

    // Ramp AUTO-RESOLVE (master 2026-07-20: "turn genny off, pump STILL reads powered"). The rest of this suite runs
    // under InstantRampForTests + manual Recompute, so it never exercises the gradual spin-up/cooldown against the
    // auto-ticking PowerManager -- which is exactly where the bug lived. The power net only recomputes on MarkDirty,
    // and a generator toggle marks dirty ONCE (at the toggle instant, before _powerLevel has moved). With the real
    // 1.3s/1.1s ramp, that lone solve runs while the gen is still at full _powerLevel on a toggle-OFF, so a wired
    // consumer froze POWERED through the whole cooldown. The fix re-solves each frame the output ramps (Deployable
    // _Process), mirroring the battery/wind re-solve. This drives the REAL ramp + the lazily-spawned PowerManager with
    // NO manual Recompute -- the game's own path. Identical for a replica gen under --spconsume (same _Process ramp),
    // which is the case master hit. Toggles InstantRampForTests off for this one test and restores it after.
    public class PowerGenRampReSolves : GameTest
    {
        public override string Name => "power.gen_ramp_resolves";
        public override double TimeoutSimSeconds => 30;
        public override IEnumerable<Step> Run()
        {
            Deployable.InstantRampForTests = false;   // exercise the REAL ramp (the suite default is instant)
            var gen = Deployable.Spawn(World, DeployableDef.Generator, new Vector3(-3f, 0f, 0f), 0f);   // lazily spawns the PowerManager that auto-ticks the net
            var pump = GasPump.Attach(World, new Vector3(3f, 0f, 0f), Basis.Identity, GasPump.PortLocal);
            var genOut = gen.Ports.Find(p => p.Kind == DeployableDef.PortKind.Output);
            yield return Ticks(1);
            PowerRig.Connect(World, genOut, pump.PowerPorts[0]);   // gen OUT -> pump IN
            PowerNet.MarkDirty();

            // toggle ON with the real ramp: the pump must auto-power as the gen spins past the producing threshold,
            // with NO manual Recompute -- only the auto-ticking PowerManager + the per-frame ramp re-solve drive it.
            gen.TogglePower();
            yield return Until(() => pump.IsPowered, 10);
            T.Check("real ramp ON: pump auto-powers as the gen spins up (PowerManager + ramp re-solve, no manual solve)", pump.IsPowered);

            // let the warmup fully settle so the gen can be toggled again (CanTogglePower gates on PowerSettled)
            yield return Until(() => gen.CanTogglePower, 10);
            T.Check("gen settled after warmup (toggle buffer released)", gen.CanTogglePower);

            // THE BUG + fix: toggle OFF. Pre-fix the lone toggle-instant solve left the pump powered through the whole
            // 1.1s cooldown (gen still at full _powerLevel when it ran) -> master's "gen off, STILL reads powered".
            // The ramp re-solve un-powers it as _powerLevel winds past the threshold.
            gen.TogglePower();
            yield return Until(() => !pump.IsPowered, 10);
            T.Check("real ramp OFF: pump auto-UNPOWERS as the gen winds down (master's 'gen off, still powered' fix)", !pump.IsPowered);

            Deployable.InstantRampForTests = true;   // restore the suite default for the remaining tests
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

    // #3 (master 2026-07-20 "no visual node on breaker box"): the breaker's output port must STAND UP with the box
    // under CONSUME. GridPowerSource.Materialize was missing the flat->upright rotation that GasPump.Materialize +
    // SpawnEditorGridPower both apply, so the port cube stayed in the box's as-loaded FLAT frame while the world-drawn
    // mesh stood up -> the node floated off the box face. Assert the materialized port sits at box mid-HEIGHT (PortLocal.Z
    // = 0.933, the box's height axis, rotates to world-Y after the stand-up), NOT the un-stood-up depth (~0.18).
    public class PowerGridMaterializePortStandsUp : GameTest
    {
        public override string Name => "power.grid_materialize_port_standsup";
        public override IEnumerable<Step> Run()
        {
            var grid = GridPowerSource.Materialize(World, Vector3.Zero, 0f, GridPowerSource.DefaultWatts, netId: 4242);
            yield return Ticks(1);
            var port = grid.PowerPorts.Count > 0 ? grid.PowerPorts[0] : null;
            T.Check("materialized breaker has its wire-able output port", port != null && port.Kind == DeployableDef.PortKind.Output);
            // stood up: the box's height axis (PortLocal.Z=0.933) rotates to world-Y -> the port sits ~0.93 m up ON the box
            // face. Pre-fix (plain yaw, no stand-up) that height axis stayed in world-Z and world-Y was the ~0.18 depth ->
            // the cube floated off the box ("no visual node"). world-Y is the teeth: it flips 0.18 -> 0.93 with the fix.
            T.Check($"output port stands up ON the box (world-Y {port?.GlobalPosition.Y:0.00} ~ 0.93 mid-height, not ~0.18 flat depth)",
                    port != null && Mathf.Abs(port.GlobalPosition.Y - GridPowerSource.PortLocal.Z) < 0.25f);
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
