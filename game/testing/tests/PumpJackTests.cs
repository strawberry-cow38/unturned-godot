using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>The Pump Jack (master 2026-09-10: "wire the pumpjack deployable. takes power input to work,
    /// has a fluid output for crude oil when powered").
    ///
    /// It is the port's first fluid source that MAKES its fluid instead of holding some, so the assertions
    /// are about the two halves of "when powered" -- and the second half is the one worth having.
    ///
    /// A pump jack that merely stopped PRODUCING without power would look correct in every screenshot and be
    /// wrong in the solver: a source still advertising supply keeps every pump downstream awake waiting for
    /// fluid that never comes. That is the exact reason FluidContainer has SupplyEnabled rather than leaving
    /// each device to refuse quietly, so the test checks the solver-visible flag and not just the tank.</summary>
    public sealed class PumpJackTests : GameTest
    {
        public override string Name => "fluid.pumpjack";
        public override double TimeoutSimSeconds => 20;

        public override IEnumerable<Step> Run()
        {
            yield return Ticks(1);

            // ---- WHAT IT IS. An OIL source that starts EMPTY -- unlike every other Source in the table,
            // which spawns full, because a derrick has pumped nothing yet.
            var pj = PumpJack.Make();
            World.AddChild(pj);
            yield return Ticks(2);

            T.Check("it is a fluid SOURCE", pj.Role == FluidRole.Source);
            T.Check("...of oil", pj.Tank != null && pj.Tank.Type == FluidType.Oil);
            T.Check("...whose wellhead starts EMPTY", pj.Tank != null && pj.Tank.IsEmpty);
            T.Check("it draws power as a consumer", pj.PowerPorts.Count == 1 && !pj.PowerProducing);
            T.Check("...on the deployables group PowerNet reads", pj.IsInGroup("deployables"));

            // ---- UNPOWERED: PRODUCES NOTHING...
            pj.DebugForcePower = false;
            for (int i = 0; i < 20; i++) pj.OnPostTick(0.1f);   // two seconds of ticks
            T.Check("unpowered, it lifts nothing", pj.Tank.IsEmpty);

            // ---- ...AND IS DEAD IN THE SOLVER, which is the half a "stopped producing" version would miss.
            T.Check("unpowered, it does not advertise supply either", !pj.SupplyEnabled);
            T.Check("...and says why", pj.StatusLine().text == "No power");

            // ---- POWERED: IT PUMPS, at its own rate rather than the hose rate.
            pj.DebugForcePower = true;
            T.Check("powered, it supplies", pj.SupplyEnabled);
            pj.OnPostTick(1.0f);
            float afterOne = pj.Tank.Amount;
            T.Check($"one second lifts PumpRate ({afterOne:0.0} of {PumpJack.PumpRateMlPerSec:0.0} mL)",
                    Mathf.Abs(afterOne - PumpJack.PumpRateMlPerSec) < 0.01f);
            T.Check("...which is slower than the hose can carry, so the well is the bottleneck",
                    PumpJack.PumpRateMlPerSec < pj.FlowRate);
            T.Check("and it reports pumping", pj.StatusLine().text == "Pumping crude");

            // ---- IT DOES NOT OVERFILL. A tank that runs past its capacity is a well that prints oil.
            pj.OnPostTick(100000f);   // absurd dt: one tick that would lift far more than the wellhead holds
            T.Check($"the wellhead caps at capacity ({pj.Tank.Amount:0} / {pj.Tank.Capacity:0} mL)",
                    pj.Tank.Amount <= pj.Tank.Capacity + 0.001f);
            T.Check("...and says it is full", pj.StatusLine().text == "Wellhead full");

            pj.QueueFree();
            yield return Ticks(1);

            // ---- PLACING THE ITEM ACTUALLY BUILDS ONE. The dispatch is a switch EXPRESSION, whose arms match
            // in order, so the guarded `Source when FluidPumpsCrude` arm has to come BEFORE the plain Source
            // arm or it is subsumed and every pump jack silently places as an ordinary full oil tank -- which
            // is exactly what the first cut of this did.
            var def = DeployableDef.PumpJack;
            T.Check("the def is a fluid source", def.Fluid == FluidRole.Source);
            T.Check("...flagged as the crude pump", def.FluidPumpsCrude);
            T.Check("...carrying the retail Pump Jack id", def.Id == 1219);

            var placed = FluidDeploy.SpawnFor(def, World, Vector3.Zero, 0f);
            yield return Ticks(2);
            T.Check($"placing it builds a PumpJack, not a plain source ({placed?.GetType().Name})",
                    placed is PumpJack);
            if (placed is PumpJack pp)
                T.Check("...and that one starts empty too", pp.Tank != null && pp.Tank.IsEmpty);
            placed?.QueueFree();
        }
    }
}
