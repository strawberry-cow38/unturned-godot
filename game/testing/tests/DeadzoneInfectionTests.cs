using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Contaminated ground costs INFECTION, not health (strawberry 2026-09-11: "wire deadzones to deal
    // infection damage instead of hp"), and publishes the exposure the screen effect and the HUD icon read.
    //
    // The L0 suite owns the arithmetic. What can only be wrong HERE is the wiring: which player field moves,
    // which one must not, and whether leaving actually clears the state the overlay is ramping on.
    public sealed class DeadzoneInfection : GameTest
    {
        public override string Name => "deadzone.infection";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var player = Rigs.Player(World, new Vector3(0, 2, 0));
            var field = new DeadzoneField();
            World.AddChild(field);
            // A zone around the origin, so the rig player is standing in it.
            field.AddVolume(new Vector3(0, 2, 0), new Vector3(20, 20, 20));
            yield return Ticks(2);

            float hp0 = player.Health, inf0 = player.Infection;
            T.Check($"the player starts clean (hp {hp0:0.#}, infection {inf0:0.###})", inf0 <= 0.001f);

            // Past the entry grace, then a real dose. Driven through Apply directly -- the field's own poll is
            // rate-limited and this is about what a step DOES, not about when it runs.
            field.Apply(player, 1f);
            field.Apply(player, 4f);
            yield return Ticks(2);

            T.Check($"standing in it raises infection ({player.Infection:0.###})", player.Infection > inf0);

            // ⭐ THE CLAIM. Health must be untouched -- a deadzone that still nicked HP would look identical on
            // the infection meter and quietly keep the second damage path the change existed to remove.
            T.Check($"...and does NOT touch health directly ({player.Health:0.##} vs {hp0:0.##})",
                    Mathf.IsEqualApprox(player.Health, hp0));

            // The exposure clock the overlay and the HUD icon both read.
            T.Check($"exposure is published to the player ({player.DeadzoneSeconds:0.##} s)", player.DeadzoneSeconds > 0f);
            T.Check("...and InDeadzone reads true, which is what the HUD icon gates on", player.InDeadzone);

            float ramp = DeadzoneOverlay.ExposureFor(player);
            T.Check($"the overlay ramp is live but not yet full ({ramp:0.###})", ramp > 0f && ramp < 1f);

            // STARTS TAME, asserted rather than described: 5 s of a 40 s ramp must still be a hint, not a
            // whiteout. Without this the ramp could be a step function and every other check here would pass.
            T.Check($"...and is still gentle this early ({ramp:0.###} at {player.DeadzoneSeconds:0.#} s)", ramp < 0.25f);

            // LEAVING CLEARS IT. The overlay keeps no clock of its own precisely so that walking out ends the
            // effect; if this leaks, the grain stays on screen after the danger is gone.
            player.GlobalPosition = new Vector3(500f, 2f, 500f);
            field.Apply(player, 1f);
            yield return Ticks(2);
            T.Check($"walking out clears the exposure ({player.DeadzoneSeconds:0.##} s)", player.DeadzoneSeconds <= 0f);
            T.Check("...so the overlay ramps back to nothing", Mathf.IsZeroApprox(DeadzoneOverlay.ExposureFor(player)));
            T.Check("...and InDeadzone is false again, dropping the HUD icon", !player.InDeadzone);

            // NO MORE DOSE, asserted as "does not RISE" rather than "is unchanged" -- deliberately. Infection
            // below 0.5 drains on its own in PlayerVitalsSim, so the honest expectation outside a zone is that
            // it falls. The first version of this check demanded exact equality and failed at 0.124, catching
            // the self-clear doing precisely its job; equality here would have been a test asserting that a
            // feature I wrote earlier tonight is broken.
            float infAfterLeaving = player.Infection;
            field.Apply(player, 5f);
            yield return Ticks(2);
            T.Check($"...and standing outside accrues no more ({player.Infection:0.###} vs {infAfterLeaving:0.###})",
                    player.Infection <= infAfterLeaving + 1e-4f);
        }
    }
}
