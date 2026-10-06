using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Testing
{
    /// <summary>The water mirror's buffer must carry the WINDOW'S ASPECT, and the reflection layer must not be empty.
    ///
    /// Both of these shipped broken on 2026-10-06 and both were invisible to every other check:
    ///
    /// 1. ⚠⚠ ASPECT. `WaterReflection` copied the main camera's Fov/Near/Far onto the mirror camera, which looks
    ///    like copying the projection and is not: the ASPECT comes from the VIEWPORT. The SubViewport was a fixed
    ///    1024x1024 square against a wide window, so the horizontal FOV disagreed and the shader -- which samples
    ///    that buffer straight by SCREEN_UV -- drew the reflection at the wrong X. Master: "they dont follow their
    ///    actual world positions, and move with the camera".
    ///    ⭐ THE ERROR IS ZERO AT THE SCREEN CENTRE and grows toward the edges, which is why a centred still render
    ///    passed it. A picture cannot be the test for this; the ratio can.
    ///
    /// 2. ⚠⚠ AN EMPTY LAYER. The mirror camera's cull mask is one visual layer, and a call-site census found NOTHING
    ///    was ever put on it -- the pass rendered an empty buffer for six weeks while looking like a tuning problem.
    ///    A count is asserted here so "the reflection is just weak" can never again mean "it is not happening".
    ///
    /// Neither fails a build, and neither changes any number another test reads.</summary>
    public class WaterReflectionAspect : GameTest
    {
        public override string Name => "water.reflection_aspect";

        public override IEnumerable<Step> Run()
        {
            var water = new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(80f, 80f) },
                MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/water.gdshader") },
                Layers = WaterReflection.WaterLayer,
            };
            World.AddChild(water);

            // A camera for the mirror to follow -- the pass reads GetViewport().GetCamera3D() every frame.
            var cam = new Camera3D { Fov = 61f, Current = true, Position = new Vector3(0f, 4f, 30f) };
            World.AddChild(cam);

            int before = WaterReflection.MarkedCount;
            var tree = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(2f, 9f, 2f) }, Position = new Vector3(6f, 4.5f, 0f) };
            World.AddChild(tree);
            WaterReflection.MarkReflective(tree);
            T.Check("MarkReflective puts the instance on the reflection layer",
                    (tree.Layers & WaterReflection.ReflLayer) != 0);
            T.Check("MarkReflective is additive -- it must not wipe the layers the instance already had",
                    (tree.Layers & 1u) != 0);
            T.Check("MarkReflective counts what it marked (the census that proves the layer is populated)",
                    WaterReflection.MarkedCount == before + 1);

            var refl = WaterReflection.Attach(World, (ShaderMaterial)water.MaterialOverride, 0f);
            T.Check("Attach builds the mirror by default -- it must not need UG_REFLECT=1", refl != null);
            if (refl == null) yield break;

            yield return Ticks(4);   // let _Process run: it follows the camera and sizes the buffer

            var vp = refl.GetChildCount() > 0 ? refl.GetChild(0) as SubViewport : null;
            T.Check("the mirror has a SubViewport", vp != null);
            if (vp == null) yield break;

            // ⭐ THE ASSERTION THAT REJECTS THE BUG. Not "is the size sane" -- a 1024x1024 square is perfectly sane
            // and is the bug. The only thing that matters is that the buffer's ratio MATCHES THE WINDOW'S, because
            // that is the assumption SCREEN_UV sampling is built on.
            var win = refl.GetViewport().GetVisibleRect().Size;
            float wantAspect = win.X / Mathf.Max(1f, win.Y);
            float gotAspect = vp.Size.X / Mathf.Max(1f, (float)vp.Size.Y);
            GD.Print($"[water-refl-test] window {win.X:0}x{win.Y:0} (aspect {wantAspect:0.000}) -> mirror {vp.Size.X}x{vp.Size.Y} (aspect {gotAspect:0.000})");
            T.Check($"the mirror buffer matches the window's aspect (want {wantAspect:0.000}, got {gotAspect:0.000}) "
                    + "-- a square buffer against a wide window is what slid the reflection sideways",
                    Mathf.Abs(gotAspect - wantAspect) < 0.02f);

            // A CONTROL THAT MUST FAIL: force the square back and prove the assertion above would catch it. Without
            // this, a test that reads the aspect off whatever the code produced would pass for any implementation.
            vp.Size = new Vector2I(1024, 1024);
            float squareAspect = 1f;
            T.Check("control: a 1024-square buffer is REJECTED by that same check (if this fails the check is vacuous)",
                    Mathf.Abs(squareAspect - wantAspect) >= 0.02f || Mathf.Abs(win.X - win.Y) < 1f);

            refl.QueueFree(); water.QueueFree(); tree.QueueFree(); cam.QueueFree();
            yield return Ticks(1);
        }
    }
}
