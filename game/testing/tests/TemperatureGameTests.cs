using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // L1 for temperature, and the reason it exists is reachability rather than arithmetic.
    //
    // TemperatureSim's own rules are covered engine-free in UnturnedSim.Tests, and every one of those
    // passes whether or not anything in the game ever registers a bubble. That is the failure this
    // suite is aimed at: a correct, well-tested field that no campfire is plugged into. These tests
    // place a REAL campfire through the real Deployable.Spawn path and read the REAL player's state.
    public class TemperatureCampfireWarms : GameTest
    {
        public override string Name => "temperature.campfire_warms";
        public override IEnumerable<Step> Run()
        {
            TemperatureField.Clear();
            Rigs.Ground(World);
            // Well inside the 10 m warm sphere, well outside the 0.75 m burning core.
            var p = Rigs.Player(World, new Vector3(4f, 1f, 0f));
            yield return Ticks(3);
            T.Check($"nothing placed yet: NONE (got {p.Temperature})", p.Temperature == PlayerTemperature.None);

            Deployable.Spawn(World, DeployableDef.Campfire, Vector3.Zero, 0f);
            yield return Ticks(4);
            T.Check($"4 m from a campfire is WARM (got {p.Temperature})", p.Temperature == PlayerTemperature.Warm);

            float before = p.Health;
            yield return Ticks(120);
            T.Check($"warm does not hurt (health {before:0} -> {p.Health:0})", p.Health >= before - 0.01f);
        }
    }

    public class TemperatureCampfireBurns : GameTest
    {
        public override string Name => "temperature.campfire_burns";
        public override IEnumerable<Step> Run()
        {
            TemperatureField.Clear();
            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0f, 1f, 0f));   // standing IN it
            Deployable.Spawn(World, DeployableDef.Campfire, Vector3.Zero, 0f);
            // Wait for the LANDING, not just a few ticks. Spawned at y=1 the player's feet are 1 m above
            // the bubble's centre -- outside a 0.75 m sphere -- so an early read says WARM and looks
            // like the burning core does not work. It is a 3D sphere anchored at the fire's base, and
            // the player's origin is their feet; both facts have to line up before this means anything.
            yield return Ticks(40);
            T.Check($"standing in the fire is BURNING (got {p.Temperature}, feet y={p.GlobalPosition.Y:0.00})",
                    p.Temperature == PlayerTemperature.Burning);

            float before = p.Health;
            yield return Ticks(180);   // ~3 s at 60 Hz
            // 3 s at 10 damage per 0.8 s is 3 ticks, so a range rather than a number -- where the step
            // boundary lands is not something this test should pin.
            float lost = before - p.Health;
            T.Check($"standing in a fire costs health (lost {lost:0.#})", lost >= 20f && lost <= 50f);
        }
    }

    public class TemperatureBubbleDiesWithTheFire : GameTest
    {
        public override string Name => "temperature.bubble_dies_with_the_fire";
        public override IEnumerable<Step> Run()
        {
            TemperatureField.Clear();
            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(3f, 1f, 0f));
            var fire = Deployable.Spawn(World, DeployableDef.Campfire, Vector3.Zero, 0f);
            yield return Ticks(4);
            T.Check("warm while it stands", p.Temperature == PlayerTemperature.Warm);

            fire.QueueFree();
            yield return Ticks(6);
            // The symptom of getting this wrong is invisible: a patch of ground that keeps burning
            // people with nothing standing on it.
            T.Check($"the heat goes with it (got {p.Temperature}, {TemperatureField.Sim.Count} bubbles left)",
                    p.Temperature == PlayerTemperature.None && TemperatureField.Sim.Count == 0);
        }
    }
}
