using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // strawberry 2026-08-19: "make the ship's hitbox actually match the model completely. 1:1".
    //
    // "1:1" is a claim about a DISTANCE, so this measures one instead of asserting a vibe. A grid of rays is
    // dropped straight down over the whole ship; at each one the first COLLIDER hit is compared against the
    // height of the MESH's own surface at that exact (x,z), computed here by intersecting the same vertical ray
    // with ship_body.txt's triangles. The error between those two is the fidelity, in metres.
    //
    // The single number that makes this test worth having is that it runs TWICE -- once with the old single
    // BoxShape3D (Vehicle.ForceBoxHull) and once with the convex decomposition -- because "mean error 0.31 m"
    // on its own is unreadable. Against the box's own number it is an argument. This is also the only structure
    // in which the pass carries information: a decomposition that silently failed to build would score the same
    // as the box, and the comparison would say so.
    public sealed class ShipHullFidelity : GameTest
    {
        public override string Name => "vehicle.ship_hull_1to1";
        public override double TimeoutSimSeconds => 120;

        // Vertical ray vs the mesh: highest surface at (x,z), or null if the ship isn't over that spot at all.
        static float? MeshTopAt(Vector3[] tris, float x, float z)
        {
            float best = float.NegativeInfinity;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                Vector3 a = tris[i], b = tris[i + 1], c = tris[i + 2];
                // barycentric in the XZ plane
                float d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
                if (Mathf.Abs(d) < 1e-9f) continue;                       // triangle is edge-on from above
                float u = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / d;
                float v = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / d;
                float w = 1f - u - v;
                if (u < 0f || v < 0f || w < 0f) continue;
                float y = u * a.Y + v * b.Y + w * c.Y;
                if (y > best) best = y;
            }
            return float.IsNegativeInfinity(best) ? null : best;
        }

        struct Score { public float mean, max; public int sampled, missing, phantom; public Vector3 worstAt; public float worstMesh, worstCollider; }

        /// <summary>Is this point inside the mesh? Ray parity: fire along +X and count triangle crossings, odd
        /// = inside. Needed because the ray-grid score above is a HEIGHTMAP comparison -- it only ever looks at
        /// the topmost surface, so it is structurally blind to vertical faces, undersides, and any void beneath
        /// an overhang. strawberry, on a collider that scored 0.23 m mean error: "ur 1:1 model of the ship was
        /// not 1:1 at all." He was right and the number was answering a different question.</summary>
        static bool InsideMesh(Vector3[] tris, Vector3 p)
        {
            int crossings = 0;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                Vector3 a = tris[i], b = tris[i + 1], c = tris[i + 2];
                // Does the +X ray from p pass through this triangle? Solve in the YZ plane, then check x.
                float d = (b.Z - c.Z) * (a.Y - c.Y) + (c.Y - b.Y) * (a.Z - c.Z);
                if (Mathf.Abs(d) < 1e-9f) continue;
                float u = ((b.Z - c.Z) * (p.Y - c.Y) + (c.Y - b.Y) * (p.Z - c.Z)) / d;
                float v = ((c.Z - a.Z) * (p.Y - c.Y) + (a.Y - c.Y) * (p.Z - c.Z)) / d;
                float w = 1f - u - v;
                if (u < 0f || v < 0f || w < 0f) continue;
                if (u * a.X + v * b.X + w * c.X > p.X) crossings++;
            }
            return (crossings & 1) == 1;
        }

        struct Volume { public int sampled, falseSolid, falseAir, agree; }

        // WHERE the invisible walls are, not just how many. A count says the decomposition is worse than the box
        // on this axis (287 -> 633); it cannot say which piece to fix.
        readonly System.Collections.Generic.SortedDictionary<int, int> _falseSolidByY = new();
        void FalseSolidAt(Vector3 local) =>
            _falseSolidByY[Mathf.RoundToInt(local.Y)] = _falseSolidByY.TryGetValue(Mathf.RoundToInt(local.Y), out var n) ? n + 1 : 1;

        /// <summary>Volumetric fidelity: for a grid of points through the whole hull, does being inside the
        /// COLLIDER agree with being inside the MODEL? This is the question "1:1" actually asks, and the one
        /// the ray grid cannot answer.</summary>
        Volume MeasureVolume(Vehicle ship, Vector3[] tris)
        {
            var space = ship.GetWorld3D().DirectSpaceState;
            // bit0 TOO: the deckhouse rides on a StaticBody3D child (Spec.HullTrimesh), and masking only the
            // vehicle layer would report it missing -- measuring my own wiring instead of the collider.
            var q = new PhysicsPointQueryParameters3D { CollisionMask = (1u << 0) | (1u << 5), CollideWithBodies = true };
            var v = new Volume();
            var origin = ship.GlobalPosition;
            for (float x = -12f; x <= 12f; x += 1.5f)
                for (float y = 0.5f; y <= 22f; y += 1.0f)
                    for (float z = -33f; z <= 33f; z += 2.0f)
                    {
                        var local = new Vector3(x, y, z);
                        bool inModel = InsideMesh(tris, local);
                        q.Position = origin + local;
                        bool inCollider = space.IntersectPoint(q, 1).Count > 0;
                        v.sampled++;
                        if (inModel == inCollider) v.agree++;
                        else if (inCollider) { v.falseSolid++; FalseSolidAt(local); }   // invisible wall
                        else v.falseAir++;                                              // walk through it
                    }
            return v;
        }

        Score Measure(Vehicle ship, Vector3[] tris)
        {
            var space = ship.GetWorld3D().DirectSpaceState;
            var q = new PhysicsRayQueryParameters3D { CollisionMask = (1u << 0) | (1u << 5), CollideWithBodies = true };
            float sum = 0f, worst = 0f; int n = 0, missing = 0, phantom = 0;
            Vector3 worstAt = Vector3.Zero; float worstMesh = 0f, worstCol = 0f;
            var origin = ship.GlobalPosition;
            for (float x = -11.5f; x <= 11.5f; x += 1.0f)
                for (float z = -33f; z <= 33f; z += 1.5f)
                {
                    var meshTop = MeshTopAt(tris, x, z);
                    q.From = origin + new Vector3(x, 40f, z);
                    q.To = origin + new Vector3(x, -2f, z);
                    var hit = space.IntersectRay(q);
                    bool hasCollider = hit.Count > 0;
                    if (meshTop == null) { if (hasCollider) phantom++; continue; }   // collider where the model has nothing
                    if (!hasCollider) { missing++; continue; }                       // model where the collider has nothing
                    float colY = hit["position"].AsVector3().Y - origin.Y;
                    float err = Mathf.Abs(colY - meshTop.Value);
                    sum += err;
                    if (err > worst) { worst = err; worstAt = new Vector3(x, 0f, z); worstMesh = meshTop.Value; worstCol = colY; }
                    n++;
                }
            return new Score { mean = n > 0 ? sum / n : 999f, max = worst, sampled = n, missing = missing, phantom = phantom,
                               worstAt = worstAt, worstMesh = worstMesh, worstCollider = worstCol };
        }

        Vehicle Spawn(bool forceBox, float atX)
        {
            Vehicle.ForceBoxHull = forceBox;
            var v = Vehicle.BuildByName("ship");
            World.AddChild(v);
            v.GlobalPosition = new Vector3(atX, 0f, 0f);
            v.Freeze = true;            // this is a geometry measurement, not a physics one -- hold it still
            Vehicle.ForceBoxHull = false;
            return v;
        }

        public override IEnumerable<Step> Run()
        {
            // No water: a floating hull drifts mid-measurement and every ray lands somewhere slightly different.
            bool hadWater = Terrain.HasWater;
            Terrain.HasWater = false;
            try
            {
                var tris = ContentProvider.ParseObj("res://content/ship_body.txt").GetFaces();
                T.Check($"the ship mesh loaded to measure against ({tris.Length / 3} triangles)", tris.Length >= 3);
                if (tris.Length < 3) yield break;

                var box = Spawn(true, -400f);      // CONTROL: the collider as it shipped
                var hull = Spawn(false, 400f);     // TREATMENT: the convex decomposition
                yield return Ticks(4);

                int boxShapes = 0, hullShapes = 0;
                foreach (var c in box.GetChildren()) if (c is CollisionShape3D) boxShapes++;
                foreach (var c in hull.GetChildren()) if (c is CollisionShape3D) hullShapes++;
                T.Check($"the decomposition actually built its shapes ({hullShapes} against the box hull's {boxShapes})",
                        hullShapes >= 8 && boxShapes == 1);

                var sb = Measure(box, tris);
                var sh = Measure(hull, tris);
                GD.Print($"[HULL] BOX      mean err {sb.mean:0.00} m  max {sb.max:0.00} m  over {sb.sampled} samples; " +
                         $"{sb.missing} spots with model but NO collider, {sb.phantom} with collider but no model");
                GD.Print($"[HULL] DECOMPOSED mean err {sh.mean:0.00} m  max {sh.max:0.00} m  over {sh.sampled} samples; " +
                         $"{sh.missing} spots with model but NO collider, {sh.phantom} with collider but no model");

                GD.Print($"[HULL] worst spot, decomposed: x={sh.worstAt.X:0.0} z={sh.worstAt.Z:0.0} -> model y={sh.worstMesh:0.00}, collider y={sh.worstCollider:0.00}");
                T.Check($"the box hull really was a poor fit -- else 'the new one is better' proves nothing (mean {sb.mean:0.00} m off the model)",
                        sb.mean > 0.5f);
                T.Check($"the decomposition tracks the model far more closely (mean {sh.mean:0.00} m vs the box's {sb.mean:0.00} m)",
                        sh.mean < sb.mean * 0.5f);
                T.Check($"...and no longer leaves whole regions of the model uncollidable ({sh.missing} bare spots vs the box's {sb.missing})",
                        sh.missing <= sb.missing && sh.missing < 20);

                // SPOT CHECKS, because a mean can hide a specific thing being wrong. Each is a place the old box
                // was measurably absent.
                var space = hull.GetWorld3D().DirectSpaceState;
                var q = new PhysicsRayQueryParameters3D { CollisionMask = (1u << 0) | (1u << 5), CollideWithBodies = true };
                float Probe(Vector3 at)
                {
                    q.From = hull.GlobalPosition + at + new Vector3(0f, 30f, 0f);
                    q.To = hull.GlobalPosition + at + new Vector3(0f, -2f, 0f);
                    var h = space.IntersectRay(q);
                    return h.Count > 0 ? h["position"].AsVector3().Y - hull.GlobalPosition.Y : float.NaN;
                }
                float deck = Probe(new Vector3(0f, 0f, -10f));
                float bridge = Probe(new Vector3(0f, 0f, 18f));
                float beam = Probe(new Vector3(11.2f, 0f, 0f));
                GD.Print($"[HULL] deck surface y={deck:0.00} (model 11.00)  bridge roof y={bridge:0.00} (model 22.00)  outer beam x=11.2 y={beam:0.00}");

                T.Check($"you stand on the DECK, not a metre above it on the rail cap (collider y={deck:0.00}, model deck y=11.00)",
                        Mathf.Abs(deck - 11f) < 0.35f);
                T.Check($"the superstructure is SOLID -- the old box stopped at y=11 and the bridge was walk-through scenery (roof y={bridge:0.00}, model 22.00)",
                        Mathf.Abs(bridge - 22f) < 0.6f);
                T.Check($"the hull reaches its real beam: x=11.2 is inside the ship, and the old x+-10 box had nothing there (y={beam:0.00})",
                        !float.IsNaN(beam));

                // THE BULWARK, checked from the side: a horizontal ray just above deck height, coming inboard from
                // outside, must stop at the rail. Without it a parked vehicle simply rolls over the side.
                q.From = hull.GlobalPosition + new Vector3(20f, 11.5f, 0f);
                q.To = hull.GlobalPosition + new Vector3(0f, 11.5f, 0f);
                var rail = space.IntersectRay(q);
                float railX = rail.Count > 0 ? rail["position"].AsVector3().X - hull.GlobalPosition.X : float.NaN;
                GD.Print($"[HULL] bulwark: inbound ray at deck+0.5 stopped at x={railX:0.00} (rail outer face 12.00)");
                T.Check($"there IS a bulwark around the deck at rail height (stopped at x={railX:0.00}, expected ~12)",
                        !float.IsNaN(railX) && railX > 11f);

                // ---- IS THE DECKHOUSE ACTUALLY SOLID? Asked with a SIDEWAYS ray, because everything else here
                // would pass with it missing entirely: the volume test cannot see a trimesh (a surface has no
                // interior, so point-inside reads its whole inside as air -- identical to absent), and the roof
                // probe hits an explicit box slab, not the mesh. Removing the walls would "fix" the invisible-wall
                // count by deleting the walls, which is the one outcome that must not be able to pass.
                q.From = hull.GlobalPosition + new Vector3(0f, 17f, 40f);    // outside the stern, level with the deckhouse
                q.To = hull.GlobalPosition + new Vector3(0f, 17f, 15f);      // ...aimed into the middle of it
                var wall = space.IntersectRay(q);
                float wallZ = wall.Count > 0 ? wall["position"].AsVector3().Z - hull.GlobalPosition.Z : float.NaN;
                GD.Print($"[HULL] deckhouse wall: inbound ray at y=17 stopped at z={wallZ:0.00} (model's aft face is 25.75)");
                // ...AND FROM THE INSIDE, which is the direction that catches a one-sided trimesh. The check above
                // rays inward, meets the walls' FRONT faces and passes whether or not BackfaceCollision is set --
                // so it went on passing while the deckhouse stayed walk-through for anyone actually on the deck
                // ("ship superstructure still has no collision", master 2026-09-05, twice, against a green test).
                // A ray proves the collider is QUERYABLE. Only the far side proves it is SOLID.
                q.From = hull.GlobalPosition + new Vector3(0f, 17f, 18f);   // inside the deckhouse
                q.To   = hull.GlobalPosition + new Vector3(0f, 17f, 40f);   // ...heading out through its aft wall
                var outb = space.IntersectRay(q);
                float outZ = outb.Count > 0 ? outb["position"].AsVector3().Z - hull.GlobalPosition.Z : float.NaN;
                GD.Print($"[HULL] deckhouse wall: OUTBOUND ray at y=17 stopped at z={outZ:0.00}");
                T.Check($"...and SOLID from the inside, so the walls are two-sided (stopped at z={outZ:0.00})",
                        !float.IsNaN(outZ) && outZ < 30f);

                T.Check($"the deckhouse is SOLID to something coming at it from outside (stopped at z={wallZ:0.00}, model aft face 25.75)",
                        !float.IsNaN(wallZ) && wallZ > 24f && wallZ < 27f);

                // ---- THE MEASUREMENT THE RAY GRID CANNOT MAKE. Everything above compares heights; this asks
                // whether the collider occupies the same SPACE as the model. Run on both hulls, because the
                // box's number is the only thing that makes the decomposition's number readable.
                var vb = MeasureVolume(box, tris);
                _falseSolidByY.Clear();
                var vh = MeasureVolume(hull, tris);
                GD.Print($"[HULL] VOLUME box:        {100f * vb.agree / vb.sampled:0.0}% agree over {vb.sampled} points " +
                         $"({vb.falseSolid} solid-where-model-is-air, {vb.falseAir} air-where-model-is-solid)");
                GD.Print($"[HULL] VOLUME decomposed: {100f * vh.agree / vh.sampled:0.0}% agree over {vh.sampled} points " +
                         $"({vh.falseSolid} solid-where-model-is-air, {vh.falseAir} air-where-model-is-solid)");
                var byY = new System.Text.StringBuilder();
                foreach (var kv in _falseSolidByY) byY.Append($"y{kv.Key}:{kv.Value} ");
                GD.Print($"[HULL] invisible wall by height (decomposed run only): {byY}");
                T.Check($"the decomposition occupies the model's actual SPACE, not just its silhouette from above " +
                        $"({100f * vh.agree / vh.sampled:0.0}% of sampled points agree, against the box's {100f * vb.agree / vb.sampled:0.0}%)",
                        vh.agree > vb.agree);
            }
            finally { Terrain.HasWater = hadWater; Vehicle.ForceBoxHull = false; }
        }
    }
}
