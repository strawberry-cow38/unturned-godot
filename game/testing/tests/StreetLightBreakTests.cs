using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // A smashed Street_Light_0 has to stop lighting the street. It didn't: the lamp's SpotLight3D, emissive
    // lens and glow cone live on a SEPARATE world-space StreetLight node, while DestructibleField only toggles
    // Visible on the MeshInstance3Ds it was handed. Breaking the pole hid the pole and left a lit cone hanging
    // in the air over the rubble.
    //
    // The subtle half, and the reason SetBroken is STATE rather than a one-shot "switch it off": StreetLight
    // re-derives lit from night+power in Refresh(), which runs on every day/night tick and every grid toggle.
    // A lamp merely switched off at the moment of breaking would light itself again at the next dusk, with its
    // pole still in rubble. So the test breaks the prop and THEN runs a dusk cycle and a grid toggle over it --
    // asserting darkness only at the instant of breaking would pass against that.
    //
    // Rubble_Reset is 300 ticks for this prop, so it respawns; the restore direction is asserted too.
    public sealed class BrokenStreetLightGoesDarkTests : GameTest
    {
        public override string Name => "props.broken_streetlight_goes_dark";

        public override IEnumerable<Step> Run()
        {
            var lamp = StreetLight.Make(new Vector3(0f, 5f, 0f), 5f);
            World.AddChild(lamp);
            yield return Ticks(2);
            lamp.SetNight(true);
            lamp.SetPowered(true);
            yield return Ticks(1);

            T.Check("a powered lamp at night lights its spot", lamp.LitSpotForTest);
            T.Check("...its lens", lamp.LitPanelForTest);
            T.Check("...and its cone", lamp.LitConeForTest);

            // Wire it exactly the way WorldBuilder.PlaceObject does for a Street_Light_0.
            var field = new DestructibleField();
            var body = new StaticBody3D { CollisionLayer = 1u << 0 };
            World.AddChild(body);
            var mesh = new MeshInstance3D { Mesh = new BoxMesh() };
            World.AddChild(mesh);
            field.Register(0, body, new[] { mesh }, 275f, 300L, 0, alive => lamp.SetBroken(!alive));

            field.SetAlive(0, false);
            yield return Ticks(1);
            T.Check("breaking the pole kills the spot", !lamp.LitSpotForTest);
            T.Check("...the lens", !lamp.LitPanelForTest);
            T.Check("...and the cone", !lamp.LitConeForTest);
            T.Check("the pole mesh is hidden", !mesh.Visible);
            T.Check("and its collider is dropped", body.CollisionLayer == 0u);

            // THE REGRESSION: the world keeps running. Refresh() recomputes lit on each of these.
            lamp.SetNight(false); yield return Ticks(1);
            lamp.SetNight(true); yield return Ticks(1);
            T.Check("nightfall cannot relight a smashed lamp", !lamp.LitSpotForTest && !lamp.LitPanelForTest && !lamp.LitConeForTest);

            lamp.SetPowered(false); yield return Ticks(1);
            lamp.SetPowered(true); yield return Ticks(1);
            T.Check("a grid toggle cannot relight a smashed lamp", !lamp.LitSpotForTest && !lamp.LitPanelForTest && !lamp.LitConeForTest);

            // Rubble reset: the prop comes back, so the lamp has to come back with it.
            field.SetAlive(0, true);
            yield return Ticks(1);
            T.Check("a respawned pole lights again", lamp.LitSpotForTest && lamp.LitPanelForTest && lamp.LitConeForTest);
            T.Check("and its mesh is visible again", mesh.Visible);
        }
    }
}
