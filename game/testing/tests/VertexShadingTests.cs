using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // VERTEX LIGHTING A/B SWITCH (strawberry 2026-08-16: "Vertex. Lighting.").
    //
    // This suite deliberately does NOT test whether vertex lighting is faster. It cannot: this box renders on
    // lavapipe, a software rasteriser, and GPU lighting cost measured against a CPU rasteriser answers a
    // different question than the one being asked. That measurement has to happen on real hardware.
    //
    // What it tests is the only part that CAN be established here, and the part that would otherwise poison the
    // real measurement: that the switch actually reaches materials. A toggle that quietly matches nothing and a
    // toggle that works both print a cheerful confirmation, and the difference between them is an experiment
    // that says "vertex lighting made no difference" for the wrong reason.
    public sealed class VertexShadingTests : GameTest
    {
        public override string Name => "graphics.vertex_shading";

        public override IEnumerable<Step> Run()
        {
            bool restore = GraphicsOptions.VertexShading;

            var lit = new MeshInstance3D
            {
                Mesh = new BoxMesh(),
                MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel },
            };
            // An unshaded material stands in for the build ghosts, port arrows and wire overlays, which are
            // unshaded on purpose. Dragging those into a lit mode would be a visual change wearing a perf
            // experiment's clothes.
            var unlit = new MeshInstance3D
            {
                Mesh = new BoxMesh(),
                MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
            };
            World.AddChild(lit);
            World.AddChild(unlit);
            yield return Ticks(1);

            GraphicsOptions.VertexShading = true;
            int changed = GraphicsOptions.ApplyShading(World);
            T.Check($"the switch reaches real materials ({changed} changed)", changed > 0);
            T.Check("...and a lit material is now per-vertex",
                (lit.MaterialOverride as StandardMaterial3D)?.ShadingMode == BaseMaterial3D.ShadingModeEnum.PerVertex);
            T.Check("...while an UNSHADED material is left alone",
                (unlit.MaterialOverride as StandardMaterial3D)?.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded);

            // Idempotent: running it twice must report 0 the second time, because the count is what a human
            // reads to decide whether the experiment was real. A count that stayed high on a no-op pass would
            // make "it applied" unfalsifiable.
            int again = GraphicsOptions.ApplyShading(World);
            T.Check($"...and re-applying the same mode changes nothing ({again})", again == 0);

            GraphicsOptions.VertexShading = false;
            int back = GraphicsOptions.ApplyShading(World);
            T.Check($"flipping back restores per-pixel ({back} changed)", back > 0);
            T.Check("...and the lit material is per-pixel again",
                (lit.MaterialOverride as StandardMaterial3D)?.ShadingMode == BaseMaterial3D.ShadingModeEnum.PerPixel);

            // A real vehicle, not just a hand-built box: the materials that matter are the ones SolidMat and
            // PaintMat produce, and a switch that only moved my test cube would be useless.
            var v = Vehicle.BuildByName("sedan");
            World.AddChild(v);
            v.GlobalPosition = new Vector3(0f, 200f, 0f);
            yield return Ticks(1);
            GraphicsOptions.VertexShading = true;
            int onCar = GraphicsOptions.ApplyShading(v);
            T.Check($"it reaches a built vehicle's own materials ({onCar} changed on a sedan)", onCar > 0);

            GraphicsOptions.VertexShading = restore;
            GraphicsOptions.ApplyShading(World);
            v.QueueFree();
            yield break;
        }
    }
}
