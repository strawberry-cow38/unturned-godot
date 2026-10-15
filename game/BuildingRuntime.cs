using System.Collections.Generic;
using Godot;
using UnturnedSim;

namespace UnturnedGodot
{
    /// <summary>⭐ A GENERATED BUILDING, BUILT -- no editor stage, no bake (strawberry 2026-10-15: "now get onto having the
    /// buildings generate"; tinyclaw handed the extraction over).
    ///
    /// The node-building half of EditorBuildings.Generate, lifted out so the stage and the town share ONE path: each planned
    /// surface a WallSurface (its openings, doors and windows, built by WallSurface itself), each stair flight its treads, its
    /// invisible ramp and the hole it arrives through in the floor above, the corner solve that closes the notch where two
    /// walls meet, and the furniture. EditorBuildings' SpawnWall, SolveCorners and FitStairsToStructure are now thin editor
    /// wrappers -- the pick map, the undo, the stage -- round the statics here.
    ///
    /// ⚠ BUILT AT THE ORIGIN, THEN MOVED. The surfaces' own maths (SurfaceAabb, UVToWorld) reads GlobalTransform, which a node
    /// has only inside the tree -- so the building's root goes into the tree at its parent's origin, everything is built in the
    /// lattice's own frame there (as the stage builds it at its origin), and only then is the root put where its plot says.</summary>
    public static class BuildingRuntime
    {
        /// <summary>A surface, as EditorBuildings.SpawnWall has always made one, minus the editor's bookkeeping.
        /// NO SNAPPING HERE: a length rounded on the way in made loading lossy (see SpawnWall).</summary>
        public static WallSurface NewWall(Node3D parent, List<WallSurface> into, Vector3 origin, float yawDeg, float length, float thickness,
                                          int material, IReadOnlyList<WallOpening> openings, float height = WallOpenings.DoorHeight,
                                          float pitchDeg = 0f, SurfaceKind kind = SurfaceKind.Wall, float gableRise = 0f,
                                          int texel = -1, float insetL0 = 0f, float insetL1 = 0f,
                                          float insetR0 = 0f, float insetR1 = 0f,
                                          int materialBack = -1, int texelBack = -1)
        {
            var w = new WallSurface
            {
                Length = Mathf.Max(0.01f, length), Height = height, Thickness = thickness, MaterialId = material, Kind = kind,
                GableRise = gableRise, Texel = texel,
                InsetL0 = insetL0, InsetL1 = insetL1, InsetR0 = insetR0, InsetR1 = insetR1,
                MaterialIdBack = materialBack, TexelBack = texelBack,
                Position = origin, RotationDegrees = new Vector3(pitchDeg, yawDeg, 0f),
            };
            parent.AddChild(w);
            if (openings != null) { w.Openings.AddRange(openings); w.Rebuild(); }
            into?.Add(w);
            return w;
        }

        /// <summary>World-space bounds of a surface from its OWN dimensions (local box X 0..Length, Y 0..Height, Z +/-Thickness/2,
        /// Rebuild's frame), not its mesh's: the mesh would miss the openings partition and go stale mid-drag.</summary>
        public static Aabb SurfaceAabb(WallSurface w)
        {
            float t = w.Thickness * 0.5f;
            var xf = w.GlobalTransform;
            var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < 8; i++)
            {
                var c = xf * new Vector3((i & 1) == 0 ? 0f : w.Length,
                                         (i & 2) == 0 ? 0f : w.Height,
                                         (i & 4) == 0 ? -t : t);
                lo = new Vector3(Mathf.Min(lo.X, c.X), Mathf.Min(lo.Y, c.Y), Mathf.Min(lo.Z, c.Z));
                hi = new Vector3(Mathf.Max(hi.X, c.X), Mathf.Max(hi.Y, c.Y), Mathf.Max(hi.Z, c.Z));
            }
            return new Aabb(lo, hi - lo);
        }

        /// <summary>Close the notch at every corner and tee: each wall's end run on to where it meets another, its far face at a
        /// corner, its centreline at a tee (EditorBuildings.SolveCorners' rule, unchanged -- see the reasoning there). Returns what
        /// moved, as (wall, old length, old position), so the editor can undo it.</summary>
        public static List<(WallSurface W, float Len, Vector3 Pos)> SolveCorners(IEnumerable<WallSurface> all)
        {
            const float MaxGrow = 1.6f;        // a wall thickness and a bit; past that it is not a junction
            var undo = new List<(WallSurface, float, Vector3)>();
            var walls = new List<WallSurface>();
            foreach (var w in all)
                if (GodotObject.IsInstanceValid(w) && (w.Kind == SurfaceKind.Wall || w.Kind == SurfaceKind.Foundation)) walls.Add(w);
            var growStart = new float[walls.Count];
            var growEnd = new float[walls.Count];
            for (int i = 0; i < walls.Count; i++)
            {
                var a = walls[i];
                var a0 = a.UVToWorld(0f, 0f);
                var ad = new Vector3(a.UVToWorld(a.Length, 0f).X - a0.X, 0f, a.UVToWorld(a.Length, 0f).Z - a0.Z);
                if (ad.LengthSquared() < 1e-6f) continue;
                ad = ad.Normalized();
                for (int j = 0; j < walls.Count; j++)
                {
                    if (i == j) continue;
                    var b = walls[j];
                    // parallel walls are a seam, not a corner -- extending them just overlaps two walls
                    float dy = Mathf.Abs(Mathf.Wrap(a.RotationDegrees.Y - b.RotationDegrees.Y, -90f, 90f));
                    if (dy < 20f) continue;
                    // and they must be the same storey, or a foundation solves against the wall above it
                    if (Mathf.Abs(a.Position.Y - b.Position.Y) > 0.6f) continue;
                    if (!CrossOnPlan(a0, ad, b.UVToWorld(0f, 0f), b.UVToWorld(b.Length, 0f), out float t, out float s2)) continue;
                    if (s2 < -0.02f || s2 > 1.02f) continue;          // the crossing is off the end of b
                    // at a CORNER run on to b's far face (the notch is the outer quarter square); at a TEE stop on its centreline
                    bool atEndOfB = s2 < 0.08f || s2 > 0.92f;
                    float target = t + (atEndOfB ? b.Thickness * 0.5f : 0f);
                    // target is ABSOLUTE, so an end already past it grows by nothing: idempotent
                    if (target < 0f && target >= -MaxGrow) growStart[i] = Mathf.Max(growStart[i], -target);
                    else if (target > a.Length && target <= a.Length + MaxGrow) growEnd[i] = Mathf.Max(growEnd[i], target - a.Length);
                }
            }
            for (int i = 0; i < walls.Count; i++)
            {
                if (growStart[i] <= 1e-4f && growEnd[i] <= 1e-4f) continue;
                var w = walls[i];
                undo.Add((w, w.Length, w.Position));
                var dir = (w.UVToWorld(1f, 0f) - w.UVToWorld(0f, 0f)).Normalized();
                w.Position -= dir * growStart[i];
                w.Length += growStart[i] + growEnd[i];
                w.Rebuild();
            }
            return undo;
        }

        /// <summary>Where the ray (from, dir) crosses the segment b0..b1, in PLAN: `t` along the ray, `s` 0..1 along the segment.</summary>
        public static bool CrossOnPlan(Vector3 from, Vector3 dir, Vector3 b0, Vector3 b1, out float t, out float s)
        {
            t = s = 0f;
            var p = new Vector2(from.X, from.Z);
            var d = new Vector2(dir.X, dir.Z);
            var q = new Vector2(b0.X, b0.Z);
            var e = new Vector2(b1.X, b1.Z) - q;
            float den = d.X * e.Y - d.Y * e.X;
            if (Mathf.Abs(den) < 1e-6f) return false;          // parallel in plan
            var r = q - p;
            t = (r.X * e.Y - r.Y * e.X) / den;
            s = (r.X * d.Y - r.Y * d.X) / den;
            return true;
        }

        /// <summary>A flight's invisible ramp, lying on the tread NOSES from its foot at `origin` to the top tread's walking
        /// surface one rise up and one run back (EditorBuildings.FitStairsToStructure's, unchanged).</summary>
        public static StaticBody3D StairRamp(Node3D parent, Vector3 origin, float yawDeg, float run, float width, float rise)
        {
            float yaw = Mathf.DegToRad(yawDeg);
            var back = new Vector3(-Mathf.Sin(yaw), 0f, -Mathf.Cos(yaw));
            var right = new Vector3(Mathf.Cos(yaw), 0f, -Mathf.Sin(yaw));
            var foot = origin + back * (run * 0.5f) + right * (width * 0.5f)
                     + new Vector3(0f, rise * 0.5f + WallOpenings.StairTreadThickness * 0.5f, 0f);
            float slope = Mathf.Sqrt(run * run + rise * rise);
            var ramp = new StaticBody3D { Name = "StairRamp" };
            ramp.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(width, WallOpenings.StairTreadThickness, slope) } });
            parent.AddChild(ramp);
            ramp.GlobalPosition = foot;
            ramp.GlobalRotation = new Vector3(Mathf.Atan2(rise, run), yaw, 0f);   // pitched along the flight
            return ramp;
        }

        /// <summary>Cut the hole a flight arrives through in the floor slab it reaches (its plan footprint grown by
        /// StairHoleMargin), with `punch` -- the editor's PunchHole, or Punch here. Returns the slabs cut.</summary>
        public static int CutStairHole(IEnumerable<WallSurface> all, Vector3 origin, float yawDeg, float run, float width, float rise,
                                       System.Func<WallSurface, float, float, float, float, bool> punch)
        {
            float yaw = Mathf.DegToRad(yawDeg);
            var back = new Vector3(-Mathf.Sin(yaw), 0f, -Mathf.Cos(yaw));
            var right = new Vector3(Mathf.Cos(yaw), 0f, -Mathf.Sin(yaw));
            const float Margin = WallOpenings.StairHoleMargin;
            var a = origin;
            var b = origin + back * run + right * width;
            float minX = Mathf.Min(a.X, b.X) - Margin, maxX = Mathf.Max(a.X, b.X) + Margin;
            float minZ = Mathf.Min(a.Z, b.Z) - Margin, maxZ = Mathf.Max(a.Z, b.Z) + Margin;
            float topY = origin.Y + rise;
            int cut = 0;
            foreach (var w in new List<WallSurface>(all))
            {
                if (!GodotObject.IsInstanceValid(w) || w.Kind != SurfaceKind.Floor) continue;
                var box = SurfaceAabb(w);
                // only the slab this flight arrives at: its top within half a storey of the landing
                if (Mathf.Abs(box.End.Y - topY) > rise * 0.5f) continue;
                if (box.End.X < minX || box.Position.X > maxX || box.End.Z < minZ || box.Position.Z > maxZ) continue;
                // the four corners in the slab's own UV space (a floor is a wall lying down), bounded
                float u0 = float.MaxValue, v0 = float.MaxValue, u1 = float.MinValue, v1 = float.MinValue;
                bool ok = true;
                foreach (var c in new[] { new Vector3(minX, box.End.Y, minZ), new Vector3(maxX, box.End.Y, minZ),
                                          new Vector3(minX, box.End.Y, maxZ), new Vector3(maxX, box.End.Y, maxZ) })
                {
                    if (!w.WorldToUV(c, out float u, out float v)) { ok = false; break; }
                    u0 = Mathf.Min(u0, u); u1 = Mathf.Max(u1, u);
                    v0 = Mathf.Min(v0, v); v1 = Mathf.Max(v1, v);
                }
                if (ok && punch(w, u0, v0, u1, v1)) cut++;
            }
            return cut;
        }

        /// <summary>A rectangular hole in a surface (EditorBuildings.PunchHole's rule; a hole over the whole slab removes it).</summary>
        public static bool Punch(WallSurface w, float u0, float v0, float u1, float v1)
        {
            if (w == null || !GodotObject.IsInstanceValid(w)) return false;
            if (u0 > u1) (u0, u1) = (u1, u0);
            if (v0 > v1) (v0, v1) = (v1, v0);
            u0 = Mathf.Max(0f, u0); u1 = Mathf.Min(w.Length, u1);
            v0 = Mathf.Max(0f, v0); v1 = Mathf.Min(w.Height, v1);
            if (u1 - u0 < WallOpenings.MinOpening || v1 - v0 < WallOpenings.MinOpening) return false;
            if (u1 - u0 >= w.Length - WallOpenings.Eps && v1 - v0 >= w.Height - WallOpenings.Eps) { w.QueueFree(); return true; }
            w.Openings.Add(new WallOpening(u0, v0, u1 - u0, v1 - v0));
            w.Rebuild();
            return true;
        }

        /// <summary>The wall material a palette names (WallMaterials), or `fallback`.</summary>
        public static int MaterialFor(string palette, int fallback = 0)
        {
            for (int i = 0; i < WallMaterials.Count; i++) if (WallMaterials.At(i).Name == palette) return i;
            return fallback;
        }

        /// <summary>What the last Build made.</summary>
        public static int LastWalls, LastProps;

        /// <summary>⭐ A generated building, built under `parent` and stood in `frame` -- the lattice's (0, 0) corner at floor level,
        /// +X and +Z the plan's. Walls and their openings, stair flights with their ramps and floor holes, the corner solve, and
        /// the furniture as meshes (a kit doodad with its stock children, a curtain or rug from its inputs). Returns the root.
        /// ⚠ The furniture is DRAWN, not yet live: no collider, and the switches, outlets and ceiling lights are their meshes
        /// without the stage's wiring (that is the editor's PowerNet plumbing, next).</summary>
        public static Node3D Build(BuildingGenResult res, Transform3D frame, Node3D parent, EditorObjects objs, string name = "Building")
        {
            if (res == null || parent == null) return null;
            var root = new Node3D { Name = name };
            parent.AddChild(root);                      // at the parent's origin: built there, moved last (see the class summary)
            int mat = MaterialFor(res.Palette);
            int roofTexel = WallMaterials.Count > 0 ? WallMaterials.At(Mathf.PosMod(mat, WallMaterials.Count)).RoofTexel : -1;
            var walls = new List<WallSurface>();
            foreach (var pl in res.Walls)
                NewWall(root, walls, new Vector3(pl.X, pl.Y, pl.Z), pl.Yaw, pl.Length, pl.Thickness, mat, pl.Openings,
                        pl.Height, pl.Pitch, pl.Kind, pl.GableRise, pl.Kind == SurfaceKind.Roof ? roofTexel : pl.Texel,
                        pl.InsetL0, pl.InsetL1, pl.InsetR0, pl.InsetR1, pl.TexelBack >= 0 ? mat : -1, pl.TexelBack);
            foreach (var s in res.Stairs)
            {
                var o = new Vector3(s.X, s.Y, s.Z);
                float rise = s.Rise > 0f ? s.Rise : EditorBuildings.StoreyHeight;
                int steps = WallOpenings.StairSteps(rise);
                int treads = s.Concrete && BuildingGen.FloorTexels.TryGetValue(res.Palette ?? "", out var ft) ? ft.Concrete : -1;
                foreach (var p in EditorBuildings.StairTreadOrigins(o, s.Yaw, s.Run, s.Width, rise))
                    NewWall(root, walls, p, s.Yaw, s.Width, WallOpenings.StairTreadThickness, mat, null, s.Run / steps, -90f, SurfaceKind.Stairs, 0f, treads);
                StairRamp(root, o, s.Yaw, s.Run, s.Width, rise);
                // a switchback's half flights: the generator cut their floor's hole itself (it covers the landing too)
                if (rise >= EditorBuildings.StoreyHeight - 0.01f) CutStairHole(walls, o, s.Yaw, s.Run, s.Width, rise, Punch);
            }
            SolveCorners(walls);
            int props = 0;
            foreach (var m in res.Props)
            {
                if (m.Gen != null)
                {
                    var built = EditorBuildings.RuntimeDecorNode(m);
                    if (built == null) continue;
                    root.AddChild(built);
                    built.Transform = EditorBuildings.RuntimeDecorTransform(m);
                    props++;
                    continue;
                }
                var mesh = objs?.PreviewMesh(m.Name);
                if (mesh == null)
                {
                    if (DoodadKitView.Has(m.Name))
                    {
                        var holder = new Node3D { Name = "Furniture_" + m.Name, Transform = new Transform3D(EditorObjects.Upright(m.Yaw), new Vector3(m.X, m.Y, m.Z)) };
                        root.AddChild(holder);
                        DoodadKitView.Attach(holder, m.Name);
                        props++;
                    }
                    continue;
                }
                var mi = new MeshInstance3D { Name = "Furniture_" + m.Name, Mesh = mesh, MaterialOverride = objs.PreviewMaterial(m.Name),
                                              Transform = new Transform3D(EditorObjects.Upright(m.Yaw), new Vector3(m.X, m.Y, m.Z)) };
                root.AddChild(mi);
                DoodadKitView.Attach(mi, m.Name);
                props++;
            }
            root.Transform = frame;
            LastWalls = walls.Count; LastProps = props;
            return root;
        }
    }
}
