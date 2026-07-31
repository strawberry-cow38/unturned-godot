using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // The temperature icon is the visible half of the mechanic, and a HUD element that is CONSTRUCTED
    // but never displayed is indistinguishable from a working one by reading the code. This walks the
    // real path: real campfire, real player, real HUD, and asks the HUD what it is showing.
    public class TemperatureHudIcon : GameTest
    {
        public override string Name => "temperature.hud_icon";
        public override IEnumerable<Step> Run()
        {
            TemperatureField.Clear();
            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(4f, 1f, 0f));
            var hud = new HUD { Player = p };
            World.AddChild(hud);
            yield return Ticks(3);

            T.Check($"hidden while the state is NONE (temp={p.Temperature})", !hud.DebugTempIconVisible);

            // Waits POLL rather than counting ticks. The HUD refreshes in _Process (render frames) while
            // this harness advances _PhysicsProcess, so a fixed tick count can step straight past the
            // frame that would have updated it -- the first version of this test failed for exactly that
            // reason and the icon was fine. Polling still fails honestly if it never updates at all.
            var far = Deployable.Spawn(World, DeployableDef.Campfire, Vector3.Zero, 0f);
            yield return Until(() => hud.DebugTempIconVisible, 3);
            T.Check($"warm near a campfire shows the icon (temp={p.Temperature})",
                    p.Temperature == PlayerTemperature.Warm && hud.DebugTempIconVisible);
            // The NAME, not just "a box is showing": the wrong texture, or a texture that failed to load,
            // both leave a visible box.
            T.Check($"and it is the WARM icon (got '{hud.DebugTempIconName}')",
                    hud.DebugTempIconName == "hud_temp_warm.png");

            // Put a fire at the player's feet rather than teleporting the player into one -- the body is
            // a CharacterBody3D the movement sim owns, and assigning GlobalPosition under it does not
            // reliably stick. Same thing under test either way: the ONE box swaps its icon.
            var under = Deployable.Spawn(World, DeployableDef.Campfire, p.GlobalPosition with { Y = 0f }, 0f);
            yield return Until(() => hud.DebugTempIconName == "hud_temp_burning.png", 3);
            T.Check($"standing in one swaps to BURNING (temp={p.Temperature}, icon '{hud.DebugTempIconName}')",
                    p.Temperature == PlayerTemperature.Burning && hud.DebugTempIconName == "hud_temp_burning.png");

            // And with every fire gone it goes back to showing nothing at all.
            far.QueueFree(); under.QueueFree();
            yield return Until(() => !hud.DebugTempIconVisible, 3);
            T.Check($"with the fires gone it hides again (temp={p.Temperature})",
                    p.Temperature == PlayerTemperature.None && !hud.DebugTempIconVisible);
        }
    }

    // On a joined client the server owns the fine vitals, and UpdateVitals early-returns for that case.
    // Temperature was stepped BELOW that return, so the entire mechanic was invisible in multiplayer --
    // no state, no icon, nothing, while the server happily did nothing about it either. The field was
    // never the problem: replicated deployables materialise through Deployable.Spawn and register their
    // bubbles like any other, so a client's TemperatureField is correct. Only the tick was missing.
    public class TemperatureStepsWhenVitalsAreServerOwned : GameTest
    {
        public override string Name => "temperature.steps_under_server_vitals";
        public override IEnumerable<Step> Run()
        {
            TemperatureField.Clear();
            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(4f, 1f, 0f));
            var hud = new HUD { Player = p };
            World.AddChild(hud);
            // Both authorities handed to the server, exactly as ClientWorldSession does on a joined client.
            p.AdoptReplicatedFineVitals(1f, 1f, 1f, 0f);
            p.AdoptReplicatedVitals(100);
            yield return Ticks(3);

            Deployable.Spawn(World, DeployableDef.Campfire, Vector3.Zero, 0f);
            yield return Until(() => p.Temperature == PlayerTemperature.Warm, 3);
            T.Check($"a server-owned client still resolves its temperature (temp={p.Temperature})",
                    p.Temperature == PlayerTemperature.Warm);
            yield return Until(() => hud.DebugTempIconVisible, 3);
            T.Check($"and still gets the icon (icon '{hud.DebugTempIconName}')",
                    hud.DebugTempIconVisible && hud.DebugTempIconName == "hud_temp_warm.png");

            // ...but does NOT burn itself. Health is the server's to move; local damage here would fight
            // the replicated value every tick and read as flickering HP rather than as an error.
            Deployable.Spawn(World, DeployableDef.Campfire, p.GlobalPosition with { Y = 0f }, 0f);
            yield return Until(() => p.Temperature == PlayerTemperature.Burning, 3);
            float before = p.Health;
            yield return Ticks(180);
            T.Check($"burning, but health is left to the server ({before:0} -> {p.Health:0})",
                    p.Temperature == PlayerTemperature.Burning && Mathf.Abs(p.Health - before) < 0.01f);
        }
    }
}
