using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // THE FOLIAGE BRUSH.
    //
    // Retail's PAINT mode raycasts DOWN from brushPos + (x, radius, y) over 2*radius and seats each instance on
    // the hit point's NORMAL. Both halves matter and neither is visible in a count: a brush that places the
    // right NUMBER of instances at the wrong HEIGHT buries them in the terrain or floats them above it, and a
    // brush that ignores the normal stands every blade vertical in a hillside.
    //
    // So this asserts placement GEOMETRY, not just that something happened -- and it paints onto a tilted floor
    // specifically, because a flat one cannot tell "seated on the surface normal" from "always upright".
    public sealed class FoliagePaintBrushTests : GameTest
    {
        public override string Name => "foliage.paint_brush";

        static StaticBody3D Floor(Node parent, float tiltDeg)
        {
            var body = new StaticBody3D { CollisionLayer = 1u << 0 };   // TerrainLayer, what the brush casts against
            var cs = new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(400, 2, 400) } };
            body.AddChild(cs);
            parent.AddChild(body);
            body.GlobalPosition = new Vector3(0, -1, 0);
            body.Rotation = new Vector3(0, 0, Mathf.DegToRad(tiltDeg));
            return body;
        }

        public override IEnumerable<Step> Run()
        {
            var field = new FoliageField();
            World.AddChild(field);
            field.LoadGrass();
            yield return Step.Ticks(2);

            string type = null;
            foreach (var t in field.AuthoringTypes) { type = t; break; }
            T.Check($"a foliage type is available to paint ({type})", type != null);
            if (type == null) yield break;

            // 20 degrees: enough that a surface-seated instance is unmistakably not vertical.
            const float TiltDeg = 20f;
            var slab = Floor(World, TiltDeg);
            var brush = new EditorFoliage(null, null, field);
            World.AddChild(brush);
            yield return Step.Ticks(4);   // let the static body register with the physics space

            int before = field.InstanceCount(type), manualBefore = field.ManualCount(type);
            var centre = new Vector3(0, 0, 0);
            brush.RadiusVal = 10f;
            brush.FalloffVal = 0f;        // no edge thinning, so "placed < requested" can only mean a real miss
            int placed = brush.PaintAt(centre, 40);

            T.Check($"the brush placed instances ({placed} of 40 samples)", placed > 0);
            T.Check($"the field grew by exactly what was placed ({field.InstanceCount(type) - before})",
                    field.InstanceCount(type) - before == placed);
            T.Check($"every one is flagged MANUAL, so a bake cannot clear them ({field.ManualCount(type) - manualBefore})",
                    field.ManualCount(type) - manualBefore == placed);

            // GEOMETRY. Ground truth comes from the SLAB ITSELF and from a fresh raycast -- not from me
            // re-deriving the tilted box's surface by hand. The first version of this test did the trig
            // inline, got the slope's sign backwards, dropped the offset, and rotated its expected up-vector
            // about Vector3.Forward (0,0,-1) while the body rotates about +Z. All three checks failed and every
            // one of them was the TEST being wrong, not the brush -- which is exactly the way to talk yourself
            // into "fixing" working code.
            var slabUp = slab.GlobalTransform.Basis.Y.Normalized();
            var space = World.GetWorld3D().DirectSpaceState;
            var added = new List<Transform3D>();
            foreach (var t in field.DebugInstancesForTest(type)) added.Add(t);
            int inRadius = 0, onSurface = 0, seated = 0;
            foreach (var xf in added)
            {
                if (xf.Origin.DistanceTo(centre) > 12f) continue;   // only the ones near our stroke
                inRadius++;
                var probe = new Vector3(xf.Origin.X, 60f, xf.Origin.Z);
                var q = new PhysicsRayQueryParameters3D { From = probe, To = probe + Vector3.Down * 200f, CollisionMask = 1u << 0 };
                var hit = space.IntersectRay(q);
                if (hit.Count > 0 && Mathf.Abs(xf.Origin.Y - ((Vector3)hit["position"]).Y) < 0.05f) onSurface++;
                if (xf.Basis.Y.Normalized().Dot(slabUp) > 0.99f) seated++;
            }
            T.Check($"instances landed inside the brush ({inRadius})", inRadius >= placed);
            T.Check($"...ON the surface, not floating or buried ({onSurface}/{inRadius})",
                    inRadius > 0 && onSurface == inRadius);
            // The teeth on the normal: on a 20-degree slope an upright instance scores cos(20)=0.94 against
            // world-up but only ~0.94 against the SLOPE normal if it were vertical -- so this compares against
            // the slope normal, which an always-upright implementation fails.
            T.Check($"...and seated on the SLOPE normal, not stood upright ({seated}/{inRadius})",
                    inRadius > 0 && seated == inRadius);

            // The population filter, through the brush's own erase path.
            int bakedSweep = brush.EraseAt(centre, manual: false, baked: true);
            T.Check($"a baked-only erase leaves hand-painted foliage alone ({bakedSweep})", bakedSweep == 0);
            // Count what is genuinely INSIDE the erase sphere first. On a slope an instance at the brush's XZ
            // edge sits high enough that its 3D distance exceeds the radius, so "erase removes everything
            // painted" is false by geometry, not by a bug -- the first version of this asserted it anyway.
            int inSphere = 0;
            foreach (var xf in field.DebugInstancesForTest(type))
                if (xf.Origin.DistanceTo(centre) <= brush.RadiusVal) inSphere++;
            int manualSweep = brush.EraseAt(centre, manual: true, baked: false);
            T.Check($"a manual erase removes exactly what lies inside the sphere ({manualSweep} of {inSphere})",
                    manualSweep == inSphere && inSphere > 0);

            brush.QueueFree(); field.QueueFree();
        }
    }
}
