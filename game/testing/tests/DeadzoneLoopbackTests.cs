using Godot;
using System.Collections.Generic;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    /// <summary>Radiation on the path the GAME ACTUALLY BOOTS.
    ///
    /// ⚠ THIS IS THE TEST THAT WAS MISSING, and its absence let a dead feature ship. Every other deadzone
    /// test drives either a bare DeadzoneField (no loopback -> the shell owns its own vitals) or a
    /// DedicatedServer (which has always seeded its volumes through InteractableNetSync). Neither is what
    /// `--peidrive` and the menu's "Drive PEI" do: they attach an MpLoopback with ConsumeDeployables ON,
    /// which makes the local player's fine vitals SERVER-owned and adopted every tick.
    ///
    /// In that configuration the shell stops applying its own dose -- and the loopback's server was never
    /// handed the volumes, so nothing applied it either. Radiation was inert in the only mode a player sees,
    /// while eleven green deadzone checks and a green net round-trip said the feature worked. The bug is
    /// invisible to every test that does not boot THIS combination, which is why this one exists.</summary>
    public class DeadzoneDosesThroughTheLoopback : GameTest
    {
        public override string Name => "deadzone.loopback_dose";
        public override double TimeoutSimSeconds => 40;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            var driver = new SimDriver();
            World.AddChild(driver);
            yield return Ticks(2);

            var field = new DeadzoneField();
            World.AddChild(field);
            field.AddVolume(new Vector3(0f, 0f, 0f), new Vector3(30f, 20f, 30f));

            // ConsumeDeployables: TRUE, deliberately. That is the game path's default, and it is the whole
            // point -- with it off the shell keeps its local vitals and this test cannot fail.
            // ⚠ Deadzones is NOT passed here, on purpose. The original bug was that Main's call site never
            // assigned it -- so a test that hands it over itself supplies the exact input that was missing and
            // passes against the broken code (verified: it did). Letting the loopback FIND the field is the
            // thing that actually has to work.
            var loop = new MpLoopback { Player = player, Driver = driver, ConsumeDeployables = true };
            World.AddChild(loop);
            yield return Until(() => loop.Client.State == NetSessionState.Connected
                                     && loop.Server.Vitals.TryGet(loop.Client.PlayerId, out _), 20);
            T.Check("the loopback session connected", loop.Client.State == NetSessionState.Connected);

            T.Check($"the built volume reached the listen-server ({loop.Server.Deadzones.VolumeCount})",
                    loop.Server.Deadzones.VolumeCount == 1);

            // The shell must genuinely be in the adopted state, or the rest proves nothing: if the local sim
            // were still running its own vitals, a dose would appear whether or not the server did anything.
            yield return Until(() => player.NetFineVitalsAdopted, 15);
            T.Check("the shell's fine vitals really are server-adopted", player.NetFineVitalsAdopted);

            loop.Server.Vitals.TryGet(loop.Client.PlayerId, out var sv);
            T.Check($"the server's player starts clean (dose {sv.Sim.Radiation:0.###})", sv.Sim.Radiation <= 0f);

            // Standing still in contaminated ground. The server polls at 0.25 s and owes a grace period first.
            yield return Until(() => sv.Sim.Radiation > 0f, 12);
            T.Check($"the listen-server doses the player ({sv.Sim.Radiation:0.###})", sv.Sim.Radiation > 0f);

            // ...and it reaches the SHELL, which is what the geiger and the grain read. A server that doses a
            // copy nobody adopts is the same silent failure one layer along.
            yield return Until(() => player.Radiation > 0f, 12);
            T.Check($"...and the dose is adopted into the shell ({player.Radiation:0.###})", player.Radiation > 0f);
            T.Check($"the overlay ramp is live off it ({DeadzoneOverlay.ExposureFor(player):0.###})",
                    DeadzoneOverlay.ExposureFor(player) > 0f);
        }
    }
}
