using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>ROOF MAP for the rain (strawberry 2026-09-04: "when going into a building, it turns off rain altogether. it
    /// shouldnt. each building's roof should kill rain that reaches it"). A 1 m grid of the TOPMOST solid height at every
    /// cell -- roofs, canopies, the ground itself -- fed to the shaders as a float texture over the 64 m square around the
    /// camera. The rain streak shader hides any drop BELOW that surface at its XZ (it has passed through a roof); the
    /// wet-surface / terrain shaders keep roofed ground dry. The rain never switches off: it just cannot get under things.
    ///
    /// STATIC + CACHED (strawberry: "dont rebuild it every frame. buildings and props dont move. only rebuild when a prop
    /// gets broken and then only rebuild that area"): every cell is ray-cast ONCE, into a world-wide tile cache that lives
    /// for the whole session, and is only re-cast when something that changes the tops says so -- a prop breaking or
    /// resetting (DestructibleField.SetAlive), a structure placed or removed (StructureManager) -- and then only the cells
    /// under that thing (Invalidate). Walking just slides the 64 m window over the cache; the cells it has not seen yet
    /// are cast at up to RaysPerFrame a frame (they read as "no roof" until then, i.e. rain, never dry). Vehicles are
    /// NOT part of the map: they move, and a cached car roof would leave a dry patch behind it.
    ///
    /// CPU rays rather than a depth camera on purpose: a SubViewport's colour output passes through the world
    /// environment's tonemap and an sRGB 8-bit buffer, which mangles an encoded height; a ray grid is exact and
    /// orientation-free.
    ///
    /// Globals (RainSystem3D.EnsureGlobals): `rain_roof` (Rf texture, world Y of the topmost hit, -1e6 = nothing) and
    /// `rain_roof_rect` (centre X, centre Z, half-size, unused; z = 0 -> no map).</summary>
    public sealed partial class RainRoofMap : Node3D
    {
        public const float Half = 32f;         // half-size of the square the shaders see (m)
        public const int Res = 128;            // cells per side of that window -> 0.5 m cells (strawberry 2026-09-05 "up the rain occlusion mask resolution a bit"). 4x the cells, so the first window costs ~16k casts instead of ~4k at RaysPerFrame each -- a couple of seconds of fill on entering a fresh area, then the static cache serves it for the session.
        public const float Cell = 2f * Half / Res;
        public const float Above = 120f;       // rays start this far above the followed camera (above anything PEI builds)
        public const float RayLen = 400f;
        public const int RaysPerFrame = 128;   // cast budget while filling cells the cache has not seen
        public const int ScanPerFrame = 1024;  // window cells LOOKED AT per frame (a cached cell costs a dictionary read, nothing more)
        public const uint SolidMask = (1u << 0) | (1u << 5) | (1u << 6);   // world / props / small props -- what the look ray calls solid
        public const float NoHit = -1e6f;
        const int TileRes = 32;                // cache tile = 32 x 32 cells
        const int InvalidateDelayTicks = 2;    // a placed body is in the physics space (and a freed one out of it) by then

        public Camera3D Follow;
        public static bool Enabled = true;
        public static RainRoofMap Current;

        // The cache is STATIC: the world's tops do not change when the map node is rebuilt (scene swap), only when
        // something breaks or gets built, and those call Invalidate wherever they are.
        static readonly Dictionary<long, float[]> _tiles = new();
        static readonly List<(Vector3 centre, float radius, int ticks)> _pending = new();
        public static int CastCount;           // rays cast so far (perf probe)

        Image _img; ImageTexture _tex; byte[] _bytes; float[] _win;
        readonly float[] _raw = new float[(Res + 2) * (Res + 2)]; readonly float[] _nine = new float[9];
        static float Median9(float[] a)
        {
            for (int i = 1; i < 9; i++) { float v = a[i]; int j = i - 1; while (j >= 0 && a[j] > v) { a[j + 1] = a[j]; j--; } a[j + 1] = v; }
            return a[4];
        }
        int _ox = int.MinValue, _oz = int.MinValue;   // window origin, in cells
        bool _dirty, _wired; int _scan;
        PhysicsRayQueryParameters3D _q;

        public override void _Ready()
        {
            Current = this;
            _img = Image.CreateEmpty(Res, Res, false, Image.Format.Rf);
            _img.Fill(new Color(NoHit, 0f, 0f, 1f));
            _tex = ImageTexture.CreateFromImage(_img);
            _win = new float[Res * Res];
            _bytes = new byte[Res * Res * 4];
            _q = new PhysicsRayQueryParameters3D { CollisionMask = SolidMask, HitBackFaces = true };
            GD.Print($"[rainroof] {Res}x{Res} window over a cached 1 m grid, {RaysPerFrame} rays/frame while filling, {_tiles.Count} tiles cached");
        }

        public override void _ExitTree() { if (Current == this) Current = null; }

        // ---- cache ----
        static long Key(int tx, int tz) => ((long)tx << 32) ^ (uint)tz;
        static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
        static float[] Tile(int tx, int tz, bool create)
        {
            if (_tiles.TryGetValue(Key(tx, tz), out var t)) return t;
            if (!create) return null;
            t = new float[TileRes * TileRes];
            for (int i = 0; i < t.Length; i++) t[i] = float.NaN;   // NaN = not cast yet
            _tiles[Key(tx, tz)] = t;
            return t;
        }
        static float Get(int cx, int cz)
        {
            int tx = FloorDiv(cx, TileRes), tz = FloorDiv(cz, TileRes);
            var t = Tile(tx, tz, false);
            return t == null ? float.NaN : t[(cz - tz * TileRes) * TileRes + (cx - tx * TileRes)];
        }
        static void Set(int cx, int cz, float v, bool create)
        {
            int tx = FloorDiv(cx, TileRes), tz = FloorDiv(cz, TileRes);
            var t = Tile(tx, tz, create);
            if (t != null) t[(cz - tz * TileRes) * TileRes + (cx - tx * TileRes)] = v;
        }

        /// <summary>Something under this footprint changed height (a prop broke, a wall went up): forget those cells so
        /// they get re-cast. Applied a couple of physics ticks later so the physics space reflects the change.</summary>
        public static void Invalidate(Vector3 centre, float radius)
        {
            lock (_pending) _pending.Add((centre, radius, InvalidateDelayTicks));
        }

        /// <summary>Drop the whole cache (a different map loaded).</summary>
        public static void ClearCache() { _tiles.Clear(); lock (_pending) _pending.Clear(); }

        void ApplyPending()
        {
            lock (_pending)
            {
                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    var (c, r, ticks) = _pending[i];
                    if (ticks > 0) { _pending[i] = (c, r, ticks - 1); continue; }
                    _pending.RemoveAt(i);
                    int x0 = Mathf.FloorToInt((c.X - r) / Cell), x1 = Mathf.FloorToInt((c.X + r) / Cell);
                    int z0 = Mathf.FloorToInt((c.Z - r) / Cell), z1 = Mathf.FloorToInt((c.Z + r) / Cell);
                    for (int cz = z0; cz <= z1; cz++)
                        for (int cx = x0; cx <= x1; cx++)
                        {
                            Set(cx, cz, float.NaN, false);   // only cells the cache holds; unknown ones are unknown already
                            if (cx >= _ox && cx < _ox + Res && cz >= _oz && cz < _oz + Res) _dirty = true;
                        }
                }
            }
        }

        // SMALL PROPS DO NOT OCCLUDE (strawberry: "very small props (up to like.. fence size) shouldnt cast rain occlusion"):
        // anything whose collision footprint is fence-height or lower, or post-thin, is cast through like a car. Judged
        // once per collider off its CollisionShape3D children and remembered by instance id.
        public const float SmallHeight = 1.7f, SmallArea = 1.5f;   // m, m^2 (X*Z footprint)
        static readonly Dictionary<ulong, bool> _small = new();
        static readonly Dictionary<ulong, bool> _tiny = new();

        /// <summary>Too small to be OVERHEAD COVER: judged on FOOTPRINT ONLY, never on thickness.
        ///
        /// This exists because sharing IsSmallProp with the shelter probe was wrong, and wrong in a way that read as
        /// correct: that rule is `thin in Y OR small in plan`, and a ROOF IS THIN IN Y. Cast downward onto the world
        /// it means "a fence or a sign, skip it"; cast UPWARD from a player it describes the exact thing being looked
        /// for. Reusing it made every roof invisible to the audio probe -- buildtool.shelter_probe_sees_past_walls
        /// failed all three checks, "the middle of the room is sheltered" included (nightly 2026-09-05).
        ///
        /// A predicate whose meaning depends on which way the ray was travelling cannot be shared between two casts
        /// that travel opposite ways, however much the two want one rule. What survives sharing is the FOOTPRINT
        /// half: a sign or lamp head is small in plan (&lt;= SmallArea), a roof is not, and neither statement cares
        /// about the direction of travel.</summary>
        public static bool IsTooSmallForCover(GodotObject o)
        {
            if (o is not CollisionObject3D body || body is Vehicle) return false;
            ulong id = body.GetInstanceId();
            if (_tiny.TryGetValue(id, out var known)) return known;
            var box = BodyAabb(body);
            bool tiny = box.HasValue && box.Value.Size.X * box.Value.Size.Z <= SmallArea;
            _tiny[id] = tiny;
            return tiny;
        }

        /// <summary>PUBLIC for the visual occlusion path. NOT for the shelter probe -- see IsTooSmallForCover.</summary>
        public static bool IsSmallProp(GodotObject o)
        {
            if (o is not CollisionObject3D body || body is Vehicle) return false;
            ulong id = body.GetInstanceId();
            if (_small.TryGetValue(id, out var known)) return known;
            bool small = false;
            Aabb acc = default; bool any = false;
            foreach (var ch in body.GetChildren())
            {
                if (ch is not CollisionShape3D cs || cs.Shape == null) continue;
                Aabb local;
                switch (cs.Shape)
                {
                    case HeightMapShape3D: any = false; goto done;   // the ground itself
                    case BoxShape3D b: local = new Aabb(-b.Size * 0.5f, b.Size); break;
                    case SphereShape3D sp: local = new Aabb(-Vector3.One * sp.Radius, Vector3.One * 2f * sp.Radius); break;
                    case CapsuleShape3D cp: local = new Aabb(new Vector3(-cp.Radius, -cp.Height * 0.5f, -cp.Radius), new Vector3(cp.Radius * 2f, cp.Height, cp.Radius * 2f)); break;
                    case CylinderShape3D cy: local = new Aabb(new Vector3(-cy.Radius, -cy.Height * 0.5f, -cy.Radius), new Vector3(cy.Radius * 2f, cy.Height, cy.Radius * 2f)); break;
                    case ConvexPolygonShape3D cv: local = PointsAabb(cv.Points); break;
                    case ConcavePolygonShape3D cc: local = PointsAabb(cc.Data); break;
                    default: any = false; goto done;
                }
                var world = cs.GlobalTransform * local;
                acc = any ? acc.Merge(world) : world; any = true;
            }
            if (any) { var sz = acc.Size; small = sz.Y <= SmallHeight || sz.X * sz.Z <= SmallArea; }
        done:
            _small[id] = small;
            return small;
        }
        /// <summary>The body's merged collision AABB in world space, or null when it has no measurable shapes
        /// (or is the terrain heightmap). The geometry half of both predicates above, so they cannot disagree
        /// about the SHAPE while disagreeing about the rule.</summary>
        static Aabb? BodyAabb(CollisionObject3D body)
        {
            Aabb acc = default; bool any = false;
            foreach (var ch in body.GetChildren())
            {
                if (ch is not CollisionShape3D cs || cs.Shape == null) continue;
                Aabb local;
                switch (cs.Shape)
                {
                    case HeightMapShape3D: return null;   // the ground itself
                    case BoxShape3D b: local = new Aabb(-b.Size * 0.5f, b.Size); break;
                    case SphereShape3D sp: local = new Aabb(-Vector3.One * sp.Radius, Vector3.One * 2f * sp.Radius); break;
                    case CapsuleShape3D cp: local = new Aabb(new Vector3(-cp.Radius, -cp.Height * 0.5f, -cp.Radius), new Vector3(cp.Radius * 2f, cp.Height, cp.Radius * 2f)); break;
                    case CylinderShape3D cy: local = new Aabb(new Vector3(-cy.Radius, -cy.Height * 0.5f, -cy.Radius), new Vector3(cy.Radius * 2f, cy.Height, cy.Radius * 2f)); break;
                    case ConvexPolygonShape3D cv: local = PointsAabb(cv.Points); break;
                    case ConcavePolygonShape3D cc: local = PointsAabb(cc.Data); break;
                    default: return null;
                }
                var world = cs.GlobalTransform * local;
                acc = any ? acc.Merge(world) : world; any = true;
            }
            return any ? acc : null;
        }

        static Aabb PointsAabb(Vector3[] pts)
        {
            if (pts == null || pts.Length == 0) return new Aabb(Vector3.Zero, Vector3.Zero);
            Vector3 lo = pts[0], hi = pts[0];
            foreach (var p in pts) { lo = lo.Min(p); hi = hi.Max(p); }
            return new Aabb(lo, hi - lo);
        }

        static bool IsVehicle(GodotObject o)
        {
            var n = o as Node;
            for (int i = 0; i < 5 && n != null; i++, n = n.GetParent()) if (n is Vehicle) return true;
            return false;
        }

        float Cast(PhysicsDirectSpaceState3D space, int cx, int cz, float fromY)
        {
            _q.From = new Vector3((cx + 0.5f) * Cell, fromY, (cz + 0.5f) * Cell);
            _q.To = _q.From + Vector3.Down * RayLen;
            _q.Exclude = null;
            var excl = new Godot.Collections.Array<Rid>();
            for (int tries = 0; tries < 8; tries++)   // cars and fence-size props are not roofs: re-cast past them
            {
                var hit = space.IntersectRay(_q);
                CastCount++;
                if (hit.Count == 0) return NoHit;
                var col = hit["collider"].AsGodotObject();
                if (!IsVehicle(col) && !IsSmallProp(col)) return ((Vector3)hit["position"]).Y;
                excl.Add((Rid)hit["rid"]);
                _q.Exclude = excl;
            }
            return NoHit;
        }

        public override void _PhysicsProcess(double delta)
        {
            if (!Enabled || Follow == null || !IsInstanceValid(Follow)) return;
            var space = GetWorld3D()?.DirectSpaceState;
            if (space == null) return;
            ApplyPending();

            var fp = Follow.GlobalPosition;
            int ox = Mathf.FloorToInt(fp.X / Cell) - Res / 2, oz = Mathf.FloorToInt(fp.Z / Cell) - Res / 2;
            if (ox != _ox || oz != _oz) { _ox = ox; _oz = oz; _dirty = true; }

            // Fill: walk the window looking for cells the cache has not cast; budgeted rays, budgeted looks.
            int rays = 0;
            for (int n = 0; n < ScanPerFrame && rays < RaysPerFrame; n++)
            {
                int idx = _scan; _scan = (_scan + 1) % (Res * Res);
                int cx = _ox + idx % Res, cz = _oz + idx / Res;
                if (!float.IsNaN(Get(cx, cz))) continue;
                Set(cx, cz, Cast(space, cx, cz, fp.Y + Above), true);
                rays++; _dirty = true;
            }

            if (_dirty)
            {
                _dirty = false;
                // SMOOTHING PASS (strawberry: "the rain mask should get a smoothing pass after its made, smoothing jagged
                // edges"): a 3x3 MEDIAN over the cached heights. It chamfers the stair-steps of a diagonal roof edge and
                // drops single-cell spikes, and because a median only ever picks a height that is actually there it never
                // invents a mid-air level for drops to die at (a blur would). The cache itself stays raw.
                const int R1 = Res + 2;
                for (int iz = 0; iz < R1; iz++)
                    for (int ix = 0; ix < R1; ix++)
                    {
                        float h = Get(_ox + ix - 1, _oz + iz - 1);
                        _raw[iz * R1 + ix] = float.IsNaN(h) ? NoHit : h;   // not cast yet = no roof (rain), never a phantom dry patch
                    }
                for (int iz = 0; iz < Res; iz++)
                    for (int ix = 0; ix < Res; ix++)
                    {
                        int k = 0;
                        for (int dz = 0; dz < 3; dz++) for (int dx = 0; dx < 3; dx++) _nine[k++] = _raw[(iz + dz) * R1 + ix + dx];
                        _win[iz * Res + ix] = Median9(_nine);
                    }
                System.Buffer.BlockCopy(_win, 0, _bytes, 0, _bytes.Length);
                _img.SetData(Res, Res, false, Image.Format.Rf, _bytes);
                _tex.Update(_img);
                if (!_wired) { _wired = true; RenderingServer.GlobalShaderParameterSet("rain_roof", _tex); }
                RenderingServer.GlobalShaderParameterSet("rain_roof_rect", new Vector4((_ox + Res * 0.5f) * Cell, (_oz + Res * 0.5f) * Cell, Half, 0f));
            }
        }

        /// <summary>The cached topmost height at a world XZ (float.MinValue when nothing is there or the cell was never cast).</summary>
        public float RoofYAt(Vector3 world)
        {
            float h = Get(Mathf.FloorToInt(world.X / Cell), Mathf.FloorToInt(world.Z / Cell));
            return float.IsNaN(h) || h <= NoHit * 0.5f ? float.MinValue : h;
        }

        /// <summary>SELF-CHECK (UG_ROOFCHECK=1): the cache against fresh rays at +-X / +-Z offsets -- a flipped axis shows here, not in play.</summary>
        public void DebugCheck(PhysicsDirectSpaceState3D space)
        {
            if (Follow == null || !_wired) return;
            var fp = Follow.GlobalPosition;
            foreach (var off in new[] { new Vector3(8f, 0f, 0f), new Vector3(-8f, 0f, 0f), new Vector3(0f, 0f, 8f), new Vector3(0f, 0f, -8f), Vector3.Zero })
            {
                var p = fp + off;
                float rayY = Cast(space, Mathf.FloorToInt(p.X / Cell), Mathf.FloorToInt(p.Z / Cell), fp.Y + Above);
                if (rayY <= NoHit * 0.5f) rayY = float.MinValue;
                float mapY = RoofYAt(p);
                GD.Print($"[rainroof] check off=({off.X:0},{off.Z:0}) ray {rayY:0.00} map {mapY:0.00} {(Mathf.Abs(rayY - mapY) < 0.01f ? "OK" : "MISMATCH")} (tiles {_tiles.Count}, casts {CastCount})");
            }
        }
    }
}
