using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Testing
{
    // The radiation readouts (grain + geiger) were mounted on the singleplayer build path and nowhere else,
    // so a joined player got neither -- no grain, no clicking, standing in a deadzone. The DATA half already
    // worked on a client: SpawnInteractables builds the DeadzoneField in Client mode too and the field ticks
    // every player in PlayerRegistry, so the shell was accumulating exposure that nothing drew.
    //
    // ⚠ WHY THE EXISTING DEADZONE TESTS COULD NOT SEE THIS. They assert through the STATIC
    // DeadzoneOverlay.ExposureFor(player), which is null-safe and instance-free by design -- so it returns
    // the right number whether the overlay is mounted, unmounted, or was never written. A test that never
    // touches the instance cannot notice that no instance exists, and five green deadzone tests did not.
    //
    // The fix mounts the readouts on the client BEFORE a player exists and binds the shell when it lands, so
    // what actually needs pinning is that contract: null Player is harmless and reads all-clear, and a late
    // bind starts driving it. That is the part that would silently render a permanent all-clear if wrong.
    public class DeadzoneClientMountBindsLate : GameTest
    {
        public override string Name => "deadzone.client_mount_binds_late";

        public override IEnumerable<Step> Run()
        {
            // MOUNTED WITH NO PLAYER -- exactly how a joined client mounts these, because its shell has not
            // arrived over the wire yet.
            var dzo = new DeadzoneOverlay { Player = null };
            World.AddChild(dzo);
            var geiger = new GeigerCounter { Player = null };
            World.AddChild(geiger);
            yield return Ticks(2);

            // TestHost steps _PhysicsProcess and never drives _Process, so the overlay's frame work has to be
            // pumped by hand here or DebugVisible simply never changes and the test asserts a stale default.
            dzo._Process(1.0 / 60.0);
            T.Check($"an unbound overlay reads all-clear ({dzo.DebugExposure:0.###})", dzo.DebugExposure == 0f);
            T.Check("an unbound overlay draws nothing", !dzo.DebugVisible);
            T.Check($"an unbound geiger is silent ({geiger.DebugRate:0.###})", geiger.DebugRate == 0f);

            // The shell lands. ClientWorldSession binds it at its one `Shell = shell` site; this is that bind.
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(2);
            player.Radiation = DeadzoneOverlay.FullExposureDose;   // a full dose, so the ramp is unambiguous
            dzo.Player = player;
            geiger.Player = player;

            dzo._Process(1.0 / 60.0);
            T.Check($"a late bind starts driving the grain ({dzo.DebugExposure:0.###})", dzo.DebugExposure > 0f);
            T.Check("...and the rect actually shows", dzo.DebugVisible);
            T.Check($"...and the geiger starts clicking ({geiger.DebugRate:0.###})", geiger.DebugRate > 0f);

            // CONTROL on the other side of the bind: clearing the dose must take it back down, so the checks
            // above are reading the PLAYER rather than latching true the moment anything is bound.
            player.Radiation = 0f;
            dzo._Process(1.0 / 60.0);
            T.Check($"a clean bound player reads all-clear again ({dzo.DebugExposure:0.###})", dzo.DebugExposure == 0f);
            T.Check("...and the rect hides again", !dzo.DebugVisible);
        }
    }
}
