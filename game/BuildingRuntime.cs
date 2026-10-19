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
                                          int materialBack = -1, int texelBack = -1, float stepRise = 0f, int rails = 0)
        {
            var w = new WallSurface
            {
                Length = Mathf.Max(0.01f, length), Height = height, Thickness = thickness, MaterialId = material, Kind = kind,
                GableRise = gableRise, Texel = texel,
                InsetL0 = insetL0, InsetL1 = insetL1, InsetR0 = insetR0, InsetR1 = insetR1,
                MaterialIdBack = materialBack, TexelBack = texelBack, StepRise = stepRise, Rails = rails,
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

        /// <summary>How a flight is painted (strawberry 2026-10-19: "painting the tread the floor color"): the tops of its treads
        /// in the palette's floor colour -- concrete for a cast flight -- and the rest of each step (WallSurface.StepRise) in the
        /// wall colour, or concrete. Texel -1 is "the palette's default", as everywhere.</summary>
        public static (int Top, int Body) StairTexels(int material, bool concrete)
        {
            string pal = material >= 0 && material < WallMaterials.Count ? WallMaterials.At(material).Name : null;
            if (concrete && pal != null && BuildingGen.FloorTexels.TryGetValue(pal, out var ft)) return (ft.Concrete, ft.Concrete);
            return (BuildingGen.FloorTexelFor(pal, RoomKind.Hall), -1);
        }

        /// <summary>How a planned surface of a generated building is painted: its own texels, a roof the palette's roof texel,
        /// and a stair LANDING as a tread is (StairTexels) -- floor to walk on, its top the floor colour and its underside the
        /// wall colour.</summary>
        public static (int Texel, int MatBack, int TexelBack) PlanPaint(WallPlan pl, int mat, int roofTexel)
        {
            if (pl.Kind == SurfaceKind.Stairs && pl.StepRise <= 0f) return (StairTexels(mat, false).Top, mat, -1);
            return (pl.Kind == SurfaceKind.Roof ? roofTexel : pl.Texel, pl.TexelBack >= 0 ? mat : -1, pl.TexelBack);
        }

        /// <summary>The wall material a palette names (WallMaterials), or `fallback`.</summary>
        public static int MaterialFor(string palette, int fallback = 0)
        {
            for (int i = 0; i < WallMaterials.Count; i++) if (WallMaterials.At(i).Name == palette) return i;
            return fallback;
        }

        /// <summary>⭐ A PLANNED SURFACE FITTED TO THE PARTY LINES (strawberry 2026-10-15: "the walls should butt up against eachother
        /// with no overlap. shared walls shouldnt exist, just where 2 buildings have walls that meet"). Each building keeps its
        /// own walls; where another building stands against a side of its plan, nothing of it may pass that side's line:
        ///   - an upright wall standing ON the line, running along it, goes in by half its thickness, so its OUTER face is on
        ///     the line (two neighbours' walls then touch there, face to face);
        ///   - everything else is CLIPPED to the line, along its run and -- for a floor, a flat roof, a porch's slab -- across it
        ///     too, its openings moved with its start.
        /// ⚠ MOVING THE PARTY WALL ALONE WAS NOT ENOUGH (master: "its STILL doing the wall overlap"): the generator plans every
        /// outside wall pre-extended to the plan's outer CORNERS -- a front wall of a 15 m plan runs x -0.35..15.35 (its 0.7 m
        /// thickness, half each end), and so do the parapets above it, the foundations under it, the floors and the roofs
        /// (measured: 106 surfaces past a party line in the test town). Each one still reached 0.35 m into the neighbour.
        /// A pitched roof plane is left as planned.</summary>
        static void PartyFit(WallPlan pl, float wx, float dz, Sides party, out Vector3 origin, out float length, out float height,
                             out List<WallOpening> openings, ref int clipped)
        {
            origin = new Vector3(pl.X, pl.Y, pl.Z); length = pl.Length; height = pl.Height;
            openings = pl.Openings != null ? new List<WallOpening>(pl.Openings) : null;
            if (party == Sides.None) return;
            bool upright = Mathf.Abs(pl.Pitch) < 1f, flat = Mathf.Abs(Mathf.Abs(pl.Pitch) - 90f) < 1f;
            if (!upright && !flat) return;
            var basis = Basis.FromEuler(new Vector3(Mathf.DegToRad(pl.Pitch), Mathf.DegToRad(pl.Yaw), 0f));
            var u = basis * Vector3.Right;          // its run
            var v = basis * Vector3.Up;             // its height (across, for a slab)
            float xmin = party.HasFlag(Sides.West) ? 0f : float.NegativeInfinity, xmax = party.HasFlag(Sides.East) ? wx : float.PositiveInfinity;
            float zmin = party.HasFlag(Sides.South) ? 0f : float.NegativeInfinity, zmax = party.HasFlag(Sides.North) ? dz : float.PositiveInfinity;
            float h = pl.Thickness * 0.5f;
            bool moved = false;
            if (upright && (pl.Kind == SurfaceKind.Wall || pl.Kind == SurfaceKind.Foundation))
            {
                if (Mathf.Abs(u.X) < 0.1f)
                {
                    if (party.HasFlag(Sides.West) && Mathf.Abs(origin.X) < 0.05f) { origin.X += h; moved = true; }
                    else if (party.HasFlag(Sides.East) && Mathf.Abs(origin.X - wx) < 0.05f) { origin.X -= h; moved = true; }
                }
                else if (Mathf.Abs(u.Z) < 0.1f)
                {
                    if (party.HasFlag(Sides.South) && Mathf.Abs(origin.Z) < 0.05f) { origin.Z += h; moved = true; }
                    else if (party.HasFlag(Sides.North) && Mathf.Abs(origin.Z - dz) < 0.05f) { origin.Z -= h; moved = true; }
                }
            }
            bool cut = ClipAxis(ref origin, ref length, u, xmin, xmax, zmin, zmax, openings, true);
            if (flat) cut |= ClipAxis(ref origin, ref height, v, xmin, xmax, zmin, zmax, openings, false);
            if (moved || cut) clipped++;
        }

        /// <summary>Clip a surface's extent along one of its own axes (`dir`, which the lattice keeps on +-X or +-Z) to the lines
        /// [xmin, xmax] / [zmin, zmax]: its start moved forward where it began past a line (and its openings with it), its length
        /// cut where it ran past one. Returns whether anything changed.</summary>
        static bool ClipAxis(ref Vector3 origin, ref float len, Vector3 dir, float xmin, float xmax, float zmin, float zmax,
                             List<WallOpening> ops, bool alongU)
        {
            bool alongX = Mathf.Abs(dir.X) > 0.9f;
            if (!alongX && Mathf.Abs(dir.Z) <= 0.9f) return false;      // vertical (an upright wall's height): nothing to clip
            float lo = alongX ? xmin : zmin, hi = alongX ? xmax : zmax;
            if (float.IsNegativeInfinity(lo) && float.IsPositiveInfinity(hi)) return false;
            float sgn = alongX ? Mathf.Sign(dir.X) : Mathf.Sign(dir.Z);
            float start = alongX ? origin.X : origin.Z, end = start + sgn * len;
            float nStart = sgn > 0f ? Mathf.Max(start, lo) : Mathf.Min(start, hi);
            float nEnd = sgn > 0f ? Mathf.Min(end, hi) : Mathf.Max(end, lo);
            float shift = (nStart - start) * sgn, nLen = Mathf.Max(0f, (nEnd - nStart) * sgn);
            if (shift <= 1e-4f && nLen >= len - 1e-4f) return false;
            if (alongX) origin.X = nStart; else origin.Z = nStart;
            len = nLen;
            if (ops != null && shift > 1e-4f)
                for (int i = ops.Count - 1; i >= 0; i--)
                {
                    var o = ops[i];
                    if (alongU) o.U -= shift; else o.V -= shift;
                    float at = alongU ? o.U : o.V, size = alongU ? o.Width : o.Height;
                    if (at + size <= 0f || at >= len) { ops.RemoveAt(i); continue; }
                    ops[i] = o;
                }
            else if (ops != null)
                for (int i = ops.Count - 1; i >= 0; i--)
                {
                    var o = ops[i];
                    if ((alongU ? o.U : o.V) >= len) ops.RemoveAt(i);
                }
            return true;
        }

        /// <summary>A piece of furniture whose Y is under this stands in the basement (the ground floor's is at 0 and up; the
        /// basement's, a storey down, hang no higher than under the ground floor's slab).</summary>
        public const float BasementY = -0.05f;

        /// <summary>Is a planned surface the basement's INSIDES -- gated with its furniture (BuildingCull) -- rather than its shell?
        /// Inside: the partitions (a basement's thin walls), the floor covers laid over a room's slab, and the footings (every
        /// part of them under the ground). The shell stays drawn whatever the camera does: the outside walls, whose top shows
        /// over the ground all round the building; the floor slab, which is what a look in past the threshold meets instead of
        /// the dug-out hole; and the hatch's well, open to the sky.</summary>
        static bool InBasement(BuildingGenResult res, WallPlan pl)
        {
            if (pl.Y > -0.5f) return false;                                   // the ground floor's slab (Y -0.25) and up
            bool upright = Mathf.Abs(pl.Pitch) < 1f;
            var basis = Basis.FromEuler(new Vector3(Mathf.DegToRad(pl.Pitch), Mathf.DegToRad(pl.Yaw), 0f));
            var mid = new Vector3(pl.X, pl.Y, pl.Z) + basis * new Vector3(pl.Length * 0.5f, pl.Height * 0.5f, 0f);
            if (InWell(res, mid.X, mid.Z)) return false;
            if (upright)
            {
                if (pl.Y + pl.Height + pl.GableRise <= BuildingGen.Grade + 0.01f) return true;      // wholly in the ground: footings
                return pl.Kind == SurfaceKind.Wall && pl.Thickness < (BuildingGen.Ext + BuildingGen.Inner) * 0.5f;   // a partition
            }
            return pl.Kind == SurfaceKind.Floor && pl.Thickness < 0.1f;       // a room's floor cover, not the slab
        }

        /// <summary>Is a plan point in the hatch's well (its retaining walls' outer faces, and a little)?</summary>
        static bool InWell(BuildingGenResult res, float x, float z)
        {
            var (x0, z0, x1, z1) = res.HatchWell;
            return x1 > x0 && x >= x0 - 0.5f && x <= x1 + 0.5f && z >= z0 - 0.5f && z <= z1 + 0.5f;
        }

        /// <summary>Surfaces the last Build fitted to a party line.</summary>
        public static int LastClipped;

        /// <summary>What the last Build made.</summary>
        public static int LastWalls, LastProps;
        /// <summary>...and of that furniture, what the last Build's Furnish made live.</summary>
        public static Furnished LastFurnished;

        /// <summary>Where a generated TOWN's lamps fade out and stop being drawn, metres from the camera (LampLight.FadeBegin; the
        /// fade then runs LampFadeLength). A town hangs a light in nearly every room -- seed 7 measured 1,141 (915 ceiling
        /// lights, the rest floor and desk lamps) across 115 buildings, 283 of them within 100 m of its densest spot -- and Forward+
        /// sends every light in the view frustum to the cluster builder, walls or no walls, whose budget is 512
        /// (rendering/limits/cluster_builder/max_clustered_elements): a view over the town would overrun it and the renderer would
        /// drop lights arbitrarily. With the fade no camera has more than 111 lamps within 40 m. A room's lamp reaches 8 m (LampLight.Range); past 40 m the light it throws on a wall
        /// through a window is not something anyone sees, while the fixture's own glow (a material, not a light) still is.
        /// The building STAGE passes 0: one building, a dozen lamps, and its renders look at it from further than this.</summary>
        public const float LampFadeBegin = 30f, LampFadeLength = 10f;

        /// <summary>What Furnish made live, in the order the building's props came.</summary>
        public sealed class Furnished
        {
            /// <summary>Every piece's node, in prop order but for any that built nothing -- a switch, a curtain, a rug included.</summary>
            public readonly List<Node3D> Nodes = new();
            public readonly List<RoomLightSwitch> Switches = new();
            /// <summary>The ceiling lights in prop order -- RoomElectrical.Lights' ids, a slot kept (null) for one that built nothing.</summary>
            public readonly List<LampLight> CeilingLights = new();
            public readonly List<GridPowerSource> Outlets = new();
            /// <summary>Every light the furniture hangs: the ceiling lights and any floor or desk lamp.</summary>
            public readonly List<LampLight> Lamps = new();
            /// <summary>Pieces built with a collider, and the containers among them (SmartProps' StoreShelf: F opens it).</summary>
            public int Colliders, Containers;
            /// <summary>Where the time went, in stopwatch ticks: building the pieces (BuildProp), putting them in the tree (their
            /// devices' _Ready -- a container rolls its loot and hangs its leaves there), the switches, the outlets, the rugs and
            /// curtains.</summary>
            public long BuildTicks, ReadyTicks, SwitchTicks, OutletTicks, DecorTicks;
            public void Add(Furnished o)
            {
                if (o == null) return;
                Nodes.AddRange(o.Nodes); Switches.AddRange(o.Switches); CeilingLights.AddRange(o.CeilingLights);
                Outlets.AddRange(o.Outlets); Lamps.AddRange(o.Lamps); Colliders += o.Colliders; Containers += o.Containers;
                BuildTicks += o.BuildTicks; ReadyTicks += o.ReadyTicks; SwitchTicks += o.SwitchTicks; OutletTicks += o.OutletTicks; DecorTicks += o.DecorTicks;
            }
            static double Ms(long t) => t * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            public string Timings => $"pieces {Ms(BuildTicks):0} ms, into the tree {Ms(ReadyTicks):0} ms, switches {Ms(SwitchTicks):0} ms, "
                                   + $"outlets {Ms(OutletTicks):0} ms, rugs and curtains {Ms(DecorTicks):0} ms";
        }

        /// <summary>⭐ A BUILDING'S FURNITURE, MADE LIVE -- the one path for the editor's building stage AND a generated town
        /// (strawberry 2026-10-15: "work on placing props/smart storage containers in buildings via the actual editor prop
        /// placement path, instead of the weird new system they have. they dont have collision rn").
        ///
        /// Every piece with art is EditorObjects.BuildProp's -- the node the editor's own placer builds, minus its pick bit and
        /// its bookkeeping -- so it has the placed prop's trimesh collider on the world layer, its surface, and its SmartProps:
        /// a wardrobe or a fridge is a container F opens (StoreShelf, found by the collider's ancestry), its leaves swing, a
        /// television toggles, a lamp is a LampLight. On top of that, the building's own electrical, as the stage has wired it
        /// since round 12: each room's light switch (RoomLightSwitch, astraclaw's base and rocker) bound through the room's
        /// record (RoomElectrical) to the ceiling lights it names, and each outlet a mains-fed GridPowerSource.
        ///
        /// A runtime piece (PropDesignation.Gen) is built from its inputs as before (EditorBuildings.RuntimeDecorNode). A rug
        /// gets the collider the placer would give a mesh -- you walk on it -- and a curtain does not: it hangs in a window, and
        /// a cloth you would walk or climb through is the one thing the world loader leaves without one (IsWalkThrough).
        ///
        /// `frame` maps the props' coordinates into `parent`'s: identity for a town building (built in its lattice's frame, then
        /// moved -- see Build), the stage's for the stage. Lights hung here fade out past `lampFade` metres (0: never).</summary>
        /// <param name="below">Where a piece below the ground floor goes (its Y under BasementY), but for a lamp -- the basement's
        /// gated node (BuildingCull); null puts everything under `parent`.</param>
        public static Furnished Furnish(Node3D parent, Transform3D frame, IEnumerable<PropDesignation> props, IEnumerable<RoomElectrical> rooms,
                                        EditorObjects objs, float lampFade, Node3D below = null)
        {
            var f = new Furnished();
            if (parent == null || props == null) return f;
            var top = parent;
            // the switches and outlets by their place among the electrical props (ElectricalFixture.Id), the ceiling lights by
            // theirs among the ceiling lights (RoomElectrical.Lights) -- a slot kept for one that built nothing, so the ids hold
            var electrical = new List<Node3D>();
            foreach (var m in props)
            {
                // (this piece's parent: the basement's is gated -- all but its lamps, whose light is not the basement's alone: with no
                // shadow a lamp lights through the slab, and hiding them changed the floor above, seen from the street through a
                // window. A lamp still goes out past LampFadeBegin + LampFadeLength like every lamp in town.)
                parent = below != null && m.Y < BasementY && LampLight.KindFor(m.Name) == LampLight.Kind.Generic ? below : top;
                bool ceiling = LampLight.KindFor(m.Name) == LampLight.Kind.CeilingShade, outlet = m.Name == "Power_Outlet_0";
                var at = frame * new Transform3D(EditorObjects.Upright(m.Yaw), new Vector3(m.X, m.Y, m.Z));
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (m.Name == "Light_Switch_0")
                {
                    var sw = BuildSwitch(parent, at, objs);
                    PropCull.Apply(sw, PropCull.Layer("SMALL"));
                    electrical.Add(sw);
                    if (sw != null) { f.Switches.Add(sw); f.Nodes.Add(sw); f.Colliders++; }
                    f.SwitchTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    continue;
                }
                if (m.Gen != null)
                {
                    var built = EditorBuildings.RuntimeDecorNode(m);
                    if (built == null) continue;
                    built.Transform = frame * EditorBuildings.RuntimeDecorTransform(m);
                    PropCull.Apply(built, PropCull.Layer("MEDIUM"));   // a rug or a curtain: furniture-sized, as retail's furniture is
                    if (m.Gen.Kind == RuntimeDecorKind.Rug && RugCollider(built) != null) f.Colliders++;
                    parent.AddChild(built);
                    f.Nodes.Add(built);
                    f.DecorTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    continue;
                }
                Node3D node = null; StaticBody3D body = null; SmartProps.Attached smart = null;
                if (objs != null) node = objs.BuildProp(m.Name, at.Origin, at.Basis, null, 0u, out body, out smart);
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                f.BuildTicks += t1 - t0;
                if (node == null)
                {
                    if (ceiling) f.CeilingLights.Add(null);
                    if (outlet) electrical.Add(null);
                    continue;
                }
                node.Name = "Furniture_" + m.Name;
                if (smart?.Lamp != null) { smart.Lamp.FadeBegin = lampFade; smart.Lamp.FadeLength = LampFadeLength; f.Lamps.Add(smart.Lamp); }
                parent.AddChild(node);   // BEFORE an outlet's source: GridPowerSource.Attach looks for the tree's PowerManager
                long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                f.ReadyTicks += t2 - t1;
                f.Nodes.Add(node);
                if (body != null) f.Colliders++;
                if (smart?.Container != null) f.Containers++;
                if (ceiling) f.CeilingLights.Add(smart?.Lamp);
                else if (outlet)
                {
                    var src = BuildOutlet(parent, at, node);
                    PropCull.Apply(src, PropCull.Layer("SMALL"));
                    body?.SetMeta("gridpower", src);   // the plate's own collider focuses the outlet too, not only its look box
                    electrical.Add(src);
                    if (src != null) f.Outlets.Add(src);
                    f.OutletTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t2;
                }
            }
            // each room's switch to its record, and to the ceiling lights the record names: the record holds the state
            if (rooms != null)
                foreach (var e in rooms)
                {
                    if (e == null) continue;
                    var sws = new List<(RoomLightSwitch Sw, int Index)>();
                    for (int i = 0; i < e.Switches.Count; i++)
                    {
                        var fx = e.Switches[i];
                        if (fx.Id >= 0 && fx.Id < electrical.Count && electrical[fx.Id] is RoomLightSwitch sw) sws.Add((sw, i));
                    }
                    var lamps = new List<LampLight>();
                    foreach (int k in e.Lights) if (k >= 0 && k < f.CeilingLights.Count && f.CeilingLights[k] != null) lamps.Add(f.CeilingLights[k]);
                    var all = new List<RoomLightSwitch>(); foreach (var q in sws) all.Add(q.Sw);
                    foreach (var (sw, i) in sws) sw.Bind(e, i, lamps, all);
                }
            return f;
        }

        /// <summary>A room's light switch: astraclaw's base and rocker (RoomLightSwitch), placed as the prop is. The two meshes are
        /// parsed once -- a town has a switch at nearly every room's door.</summary>
        static RoomLightSwitch BuildSwitch(Node3D parent, Transform3D at, EditorObjects objs)
        {
            _switchBase ??= ContentProvider.ParseObj("res://content/objects/Light_Switch_0_Base.obj");
            _switchRocker ??= ContentProvider.ParseObj("res://content/objects/Light_Switch_0_Rocker.obj");
            if (_switchBase == null || _switchRocker == null) return null;
            var sw = RoomLightSwitch.Make(_switchBase, _switchRocker, objs?.PreviewMaterial("Light_Switch_0"));
            sw.Name = "Furniture_Light_Switch_0";
            sw.Transform = at;
            parent.AddChild(sw);
            return sw;
        }
        static Mesh _switchBase, _switchRocker;

        /// <summary>An outlet: a power I/O source of BuildingGen.OutletWatts fed by the mains (GridPowerSource, the breaker box's own
        /// node at an outlet's rating: wires take power from it while the grid is live, and the solver caps what they draw at its
        /// watts), a port before each receptacle sharing that one rating, and a small look-at body so the look-ray focuses it as it
        /// does the breaker box. Beside the plate's own node (`plate`, whose mesh it outlines), not under it.</summary>
        static GridPowerSource BuildOutlet(Node3D parent, Transform3D at, Node3D plate)
        {
            Mesh mesh = null;
            foreach (var c in plate.GetChildren()) if (c is MeshInstance3D mi) { mesh = mi.Mesh; break; }
            var src = GridPowerSource.Attach(parent, at.Origin, at.Basis, OutletPort, BuildingGen.OutletWatts, "Outlet", mesh);
            // and one before the lower receptacle (strawberry: "do both outlet holes have a wire io connection?"), on the same
            // 1500 W: one budget for the fixture's two ports (GridPowerSource.PowerOutputBudget)
            src.AddOutputPort(OutletPortLower, "Outlet");
            src.Transform = at;
            var body = new StaticBody3D { Name = "OutletLook", CollisionLayer = 1u << 6, CollisionMask = 0, Position = new Vector3(0f, 0.04f, 0f) };
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.34f, 0.08f, 0.56f) } });
            body.SetMeta("gridpower", src);
            src.AddChild(body);
            return src;
        }
        /// <summary>An outlet's power ports, in its native frame: before each receptacle (the handoff's socket points,
        /// (0, 0.054, +-0.13)), clear of the plate.</summary>
        static readonly Vector3 OutletPort = new Vector3(0f, 0.11f, 0.13f), OutletPortLower = new Vector3(0f, 0.11f, -0.13f);

        /// <summary>A rug's collider: its own mesh's trimesh (one per mesh -- the factory caches the mesh, so rugs of one size and
        /// pattern share it), on the small-prop layer the placer puts a mesh this size on, sounding like cloth (PropSurfaces).</summary>
        static StaticBody3D RugCollider(MeshInstance3D rug)
        {
            if (rug?.Mesh == null) return null;
            if (!_rugShapes.TryGetValue(rug.Mesh, out var shp)) _rugShapes[rug.Mesh] = shp = rug.Mesh.CreateTrimeshShape();
            if (shp == null) return null;
            var body = new StaticBody3D { CollisionLayer = 1u << 6, CollisionMask = 0 };
            body.SetMeta(PlayerController.SurfMeta, (int)PropSurfaces.SurfForMaterial("Cloth"));
            body.AddChild(new CollisionShape3D { Shape = shp });
            rug.AddChild(body);
            return body;
        }
        static readonly Dictionary<Mesh, Shape3D> _rugShapes = new();

        /// <summary>⭐ A generated building, built under `parent` and stood in `frame` -- the lattice's (0, 0) corner at floor level,
        /// +X and +Z the plan's. Walls and their openings, stair flights with their ramps and floor holes, the corner solve, and
        /// the furniture made live by Furnish -- the editor placer's own props, colliders, containers and lights, the rooms'
        /// switches wired to their ceiling lights and the outlets on the mains -- exactly as the building stage makes it.
        /// Returns the root.</summary>
        /// <param name="party">Sides of the plan another building stands against: its walls there are pulled in half their thickness,
        /// so the two buildings' outer faces meet on the line instead of the two walls being drawn one inside the other.</param>
        public static Node3D Build(BuildingGenResult res, Transform3D frame, Node3D parent, EditorObjects objs, string name = "Building",
                                   Sides party = Sides.None)
        {
            if (res == null || parent == null) return null;
            var root = new Node3D { Name = name };
            parent.AddChild(root);                      // at the parent's origin: built there, moved last (see the class summary)
            // the basement's insides, under one node the camera gates (BuildingCull): drawn only when it could be seen
            Node3D below = null;
            if (res.HasBasement && !BuildingCull.Disabled) { below = new Node3D { Name = "Basement" }; root.AddChild(below); }
            int mat = MaterialFor(res.Palette);
            int roofTexel = WallMaterials.Count > 0 ? WallMaterials.At(Mathf.PosMod(mat, WallMaterials.Count)).RoofTexel : -1;
            var walls = new List<WallSurface>();
            float wx = res.CellsX * BuildingGen.Cell, dz = res.CellsZ * BuildingGen.Cell;
            if (System.Environment.GetEnvironmentVariable("UG_NOPARTYINSET") == "1") party = Sides.None;   // (the control: walls stacked)
            int clipped = 0;
            foreach (var pl in res.Walls)
            {
                PartyFit(pl, wx, dz, party, out var o, out float len, out float hgt, out var ops, ref clipped);
                if (len < 0.02f || hgt < 0.02f) continue;      // clipped away entirely (a porch post standing past the line)
                var paint = PlanPaint(pl, mat, roofTexel);
                NewWall(below != null && InBasement(res, pl) ? below : root, walls, o, pl.Yaw, len, pl.Thickness, mat, ops,
                        hgt, pl.Pitch, pl.Kind, pl.GableRise, paint.Texel,
                        pl.InsetL0, pl.InsetL1, pl.InsetR0, pl.InsetR1, paint.MatBack, paint.TexelBack, pl.StepRise, pl.Rails);
            }
            LastClipped = clipped;
            foreach (var s in res.Stairs)
            {
                var o = new Vector3(s.X, s.Y, s.Z);
                float rise = s.Rise > 0f ? s.Rise : EditorBuildings.StoreyHeight;
                int steps = WallOpenings.StairSteps(rise);
                var (top, body) = StairTexels(mat, s.Concrete);
                // the stair room's flight down is the basement's; the hatch's, in a well open to the sky, is the outside's
                var treadParent = below != null && s.Floor == -1 && !InWell(res, s.X, s.Z) ? below : root;
                foreach (var p in EditorBuildings.StairTreadOrigins(o, s.Yaw, s.Run, s.Width, rise))
                    NewWall(treadParent, walls, p, s.Yaw, s.Width, WallOpenings.StairTreadThickness, mat, null, s.Run / steps, -90f, SurfaceKind.Stairs, 0f,
                            top, materialBack: mat, texelBack: body, stepRise: WallOpenings.StairStepRise(rise), rails: s.Rails);
                StairRamp(root, o, s.Yaw, s.Run, s.Width, rise);
                // a switchback's half flights: the generator cut their floor's hole itself (it covers the landing too)
                if (rise >= EditorBuildings.StoreyHeight - 0.01f) CutStairHole(walls, o, s.Yaw, s.Run, s.Width, rise, Punch);
            }
            SolveCorners(walls);
            // the furniture, live: the editor placer's own nodes (Furnish), in the lattice's frame like everything above
            var furnished = Furnish(root, Transform3D.Identity, res.Props, System.Linq.Enumerable.Select(res.Rooms, r => r.Electrical), objs, LampFadeBegin, below);
            root.Transform = frame;
            BuildingCull.AddBasement(root, below, res);
            BuildingCull.AddFurnitureShadows(root, res);
            // ⚠ A water source rides a TopLevel node in a yaw-only frame (SmartProps: a sink's FluidContainer), seated at the
            // position the prop was BUILT at -- here the lattice's, not the world's -- and a TopLevel node does not follow the root
            // being moved. Re-seat them now the building stands where its plot says, or every kitchen sink's water port in town
            // sits in a heap at the lattice origin's spot in the world.
            foreach (var n in furnished.Nodes) SmartProps.Resync(n);
            LastWalls = walls.Count; LastProps = furnished.Nodes.Count; LastFurnished = furnished;
            return root;
        }
    }
}
