using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SDG.Unturned
{
    /// <summary>
    /// THE INFINITE WORLD, ENGINE-FREE HALF (strawberry 2026-10-09: "throw together a prototype of very very large /
    /// infinite procgen maps, completely with the chunk streaming etc it needs").
    ///
    /// The world is addressed as an INTEGER region + a FLOAT offset inside it, never as one big float. A float is
    /// precise to ~30 µm across a 256 m region, so the offset never degrades, and the region index is what grows.
    /// Absolute coordinates only exist as DOUBLES, here, at the moment a height is computed -- the engine never sees
    /// one, because the game half keeps the camera near (0,0,0) with a floating origin (see game/InfiniteWorld.cs).
    ///
    /// Everything is a pure function of (seed, absolute x, absolute z), so:
    ///   - any region generates in any order, on any thread, and comes out the same;
    ///   - a server running this file builds the same colliders a client draws (the MP half of streaming);
    ///   - two regions agree on their shared edge EXACTLY, because they evaluate the same function at the same
    ///     double coordinates -- there is no "stitching" step to get wrong.
    ///
    /// ⚠ PRECISION FAR OUT (cow tools 2026-10-09, from the water saga: a sin()-based hash gridded thousands of units
    /// out and looked perfect at every test position near the origin). The noise here is integer-lattice gradient
    /// noise: the lattice cell is computed in DOUBLE, floored to a LONG, hashed with 64-bit integer mixing, and only
    /// the in-cell fraction (0..1) ever becomes a float. No sin, no float hashing, no library calls -- so the noise
    /// at x = 10^7 is the same quality as at x = 0, and bit-identical on any machine. InfiniteTerrainTests generates
    /// at both and compares their statistics, so this is measured rather than asserted.
    /// </summary>
    public readonly struct RegionCoord : IEquatable<RegionCoord>
    {
        public readonly int X, Z;
        public RegionCoord(int x, int z) { X = x; Z = z; }

        /// <summary>The region containing an absolute position. Floor, not truncate: -1 m is in region -1.</summary>
        public static RegionCoord Containing(double x, double z) =>
            new RegionCoord((int)Math.Floor(x / InfiniteTerrain.RegionSize), (int)Math.Floor(z / InfiniteTerrain.RegionSize));

        /// <summary>Absolute position of this region's MIN corner.</summary>
        public double MinX => X * (double)InfiniteTerrain.RegionSize;
        public double MinZ => Z * (double)InfiniteTerrain.RegionSize;

        /// <summary>Ring distance (Chebyshev) -- the streamer's LOD bands are square rings of regions.</summary>
        public int RingTo(RegionCoord o) => Math.Max(Math.Abs(X - o.X), Math.Abs(Z - o.Z));

        public bool Equals(RegionCoord o) => X == o.X && Z == o.Z;
        public override bool Equals(object obj) => obj is RegionCoord o && Equals(o);
        public override int GetHashCode() => unchecked(X * 73856093 ^ Z * 19349663);
        public override string ToString() => $"({X},{Z})";
    }

    /// <summary>One tunnel mouth's GROUND (InfiniteTerrain.MouthGround): the LOD0 cells its holes take out, drawn again on
    /// the terrain's own vertices and split as two surfaces either side of the facade -- the approach's cut in front of
    /// it, the hill behind -- which the headwall joins along the Face profile. World coordinates.</summary>
    public sealed class MouthData
    {
        public double[] X, Z; public float[] Y, NX, NY, NZ, U, V;   // the surface's vertices; U, V into Splat
        public int[] Tris;                                         // three a triangle, any winding
        public int SplatW, SplatH; public byte[] Splat;            // vertex (I0 + i, J0 + j)'s layer, texel (i, j)
        /// <summary>The facade line across the mouth, left to right (across: + to the route's left at the near end, the
        /// same side at the far end -- the headwall's sense): the ground in front of it and behind it there. Linear
        /// between points; where the two differ the headwall must stand.</summary>
        public float[] FaceU, FaceFront, FaceBehind;
        // what it was made from, for the tests: the vertex window, its cells drawn here, and per vertex the ground the
        // terrain draws, the holes, and the two sides' heights
        public long I0, J0; public int W, H; public bool[] Cells, Hole; public float[] Ground, Front, Behind;
    }

    /// <summary>Where a streamed region's data comes from. The streamer (game/RegionStreamer.cs) asks only this, so the
    /// same streaming, LOD, colliders and floating origin serve a GENERATED world (InfiniteTerrain) and a HAND-MADE
    /// one too big to load whole -- a source that reads each region from disk, written by an editor in the same
    /// region shape. Both calls must be thread-safe: Generate runs on the streamer's workers.</summary>
    public interface IRegionSource
    {
        /// <summary>One region at one LOD (0 = 4 m grid). Pure: the same arguments return the same data.</summary>
        RegionData Generate(RegionCoord c, int lod);
        /// <summary>Ground height at an absolute position -- the spawn search and the never-under-the-ground guard.</summary>
        float HeightAt(double x, double z);
        /// <summary>What someone standing here stands on: the ground, or a tunnel's floor where the ground is overhead.</summary>
        float WalkableHeightAt(double x, double z) => HeightAt(x, z);
    }

    /// <summary>A tree the generator decided on. Position is LOCAL to the region's min corner.</summary>
    public struct TreeSpawn
    {
        public float X, Y, Z;     // local metres; Y is absolute world height (the ground at that point)
        public float Yaw;         // degrees
        public float Scale;
        public byte Kind;         // InfiniteTerrain.TreeKind
    }

    /// <summary>A short straight piece of road centreline (absolute metres) with its profile height and its distance
    /// along the road from the town it starts at -- so a road texture's UV runs on unbroken across region borders.</summary>
    public struct RoadPiece
    {
        public byte Kind;         // RoadKind: which surface (and material) this piece is
        public double X0, Z0, X1, Z1;
        public float H0, H1;
        public float S0, S1;
        /// <summary>The road's direction at each end -- averaged across a bend, so two pieces meeting there build
        /// the SAME edge and the surface closes. Each piece's own direction left a wedge-shaped gap on the outside
        /// of every bend, and the gravel showed through it as lines across the lanes.</summary>
        public float T0X, T0Z, T1X, T1Z;
        /// <summary>A highway piece on a RAISED stretch (InfiniteRoads.Stretch): where a spline bridge will go.</summary>
        public bool Raised;
        /// <summary>This piece is its line's first / last: the slab gets a ramp there, as RoadField's end caps do. NOT
        /// set where a ribbon is cut for a bridge deck -- the deck carries on from that edge.</summary>
        public bool OpenStart, OpenEnd;
        /// <summary>Under a tunnel or in its forecourt: the slab is laid at its own height, never lifted onto this LOD's
        /// ground mesh (the hill above it, or a cell across the facade that interpolates the hill).</summary>
        public bool InTunnel;
        /// <summary>A highway piece on a deep CUT stretch: a tunnel candidate.</summary>
        public bool Cut;
        /// <summary>Extra half-width at each end, metres: a highway carriageway widens to its bridge deck's roadway as
        /// it comes into a bridge's mouth (InfiniteRoads.MouthWiden over MouthTaper), with a plain-asphalt shoulder.</summary>
        public float W0, W1;
        /// <summary>A piece of an on/off ramp (InfiniteRoads.Line.Ramp): a small road's slab in road_6, two lanes with a
        /// dashed white divider (strawberry 2026-10-10: "use the road 1 (white dotted) for em", "its a 2 lane white
        /// dotted one").</summary>
        public bool Ramp;
    }

    /// <summary>One piece of a bridge (InfiniteRoads.BridgesIn), absolute metres. Kind 0 = a deck unit centred
    /// here, 1 = a pier pair rooted here and stretched by K along its own long axis, 2 = an end cap. Dir is the run
    /// direction INCLUDING the grade (a cap points it out of the bridge); the game turns it into the prop's basis with
    /// the bridge tool's own convention, so the two placements cannot disagree.</summary>
    public struct BridgePiece
    {
        public byte Kind;
        public double X, Y, Z;
        public float DX, DY, DZ;
        public float K;
        /// <summary>A deck unit's back and front ends as distance along its carriageway -- the SAME measure the
        /// ribbon's RoadPiece.S uses, so the roadway drawn over the deck carries the approach's dashes straight on
        /// instead of restarting them at the bridge.</summary>
        public float S0, S1;
        /// <summary>The road class the deck carries (RoadKind; Highway = 0). A main's overpass deck is widened across by
        /// InfiniteRoads.DeckScale and wears the main's own surface.</summary>
        public byte Road;
    }

    /// <summary>A power-line pole beside a road (absolute metres), the road's direction there, and the next pole along
    /// the same line -- which may stand in the next region, so a region can string the span that leaves it.</summary>
    public struct PolePlacement
    {
        public double X, Z; public float H, DirX, DirZ;
        public bool HasNext;
        public double NX, NZ; public float NH, NDirX, NDirZ;
    }

    /// <summary>Ground cover the generator scattered over a LOD0 region. Position LOCAL to the region's min corner.</summary>
    public struct FoliageSpawn
    {
        public float X, Y, Z, Yaw, Scale;
        public byte Kind;         // InfiniteTerrain.FoliageKind
    }

    /// <summary>One generated region at one level of detail. Arrays are plain C# so this is built on a worker
    /// thread and handed to the engine whole.</summary>
    public sealed class RegionData
    {
        public RegionCoord Coord;
        public int Lod;           // 0 = full 4 m grid; each step halves the resolution
        public int Cells;         // quads per side = 64 >> Lod
        public float Spacing;     // metres between samples = RegionSize / Cells
        /// <summary>(Cells+1)^2 heights, ROW-MAJOR IN Z: h[j * (Cells+1) + i] is at local (i*Spacing, j*Spacing).
        /// The same order a Godot HeightMapShape3D wants, so the collider is a straight copy.</summary>
        public float[] Heights;
        /// <summary>Per-vertex unit normals, same order, 3 floats each -- from the HEIGHT FUNCTION, not from this
        /// mesh, so neighbouring regions (even at different LODs) shade identically across their shared edge.</summary>
        public float[] Normals;
        /// <summary>Per-vertex clearance to the nearest asphalt of any road (capped; negative on it), same order -- so
        /// ground cover keeps off the roads by interpolation instead of a road query per blade of grass.</summary>
        public float[] RoadClear;
        /// <summary>Per-vertex dominant splat layer (Terrain's 8: Dirt Wheat Grass Gravel Road Sand Snow Stone).</summary>
        public byte[] Layers;
        public List<TreeSpawn> Trees;
        /// <summary>The road surface through this region, cut into pieces no longer than its grid spacing.</summary>
        public List<RoadPiece> Roads;
        /// <summary>Power-line poles standing in this region (LOD0/1 only).</summary>
        public List<PolePlacement> Poles;
        /// <summary>High-voltage pylons standing in this region, with the spans each owns (all LODs).</summary>
        public List<PylonPlacement> Pylons;
        public List<BridgePiece> Bridges;   // highway bridge pieces rooted in this region (all LODs)
        /// <summary>Railway track rooted in this region: New_Rail_Units and closing sleepers (all LODs).</summary>
        public List<RailPiece> Rails;
        /// <summary>Level crossings (a main over a railway) centred in this region.</summary>
        public List<LevelCrossing> LevelCrossings;
        /// <summary>The crossbucks at level crossings, by where they stand (all LODs, culled with the track).</summary>
        public List<CrossingSign> CrossingSigns;
        /// <summary>Highway tunnels whose middle lies in this region (all LODs; the whole tunnel, which may reach a
        /// neighbour -- like a bridge, it is one structure).</summary>
        public List<InfiniteRoads.TunnelSpan> Tunnels;
        /// <summary>Parallel to Tunnels: each one's two mouths' ground [end] (MouthGround) -- the cells its holes take out,
        /// drawn again either side of the facade.</summary>
        public List<MouthData[]> TunnelMouths;
        /// <summary>LOD0 only, same order as Heights: vertices that are HOLES -- just inside a tunnel portal, across the
        /// bore. The collider gets NaN there and the mesh drops every cell touching one. Null where there are none.</summary>
        public bool[] Holes;
        /// <summary>Any LOD, same order: vertices over a tunnel's run, across its shell. The mesh hangs no skirt from
        /// these -- a region-edge skirt is the hill's height dropped 6-8 m, straight into the bore. Null where none.</summary>
        public bool[] OverTunnel;
        /// <summary>Grass, flowers, pebbles, bushes -- LOD0 only (it is only ever drawn within ~160-300 m).</summary>
        public List<FoliageSpawn> Foliage;
        public float MinHeight, MaxHeight;
        public double GenMs;      // wall time spent generating (the streamer reports it)
    }

    public sealed class InfiniteTerrain : IRegionSource
    {
        public const float RegionSize = 256f;
        public const int FullCells = 64;              // LOD0: 4 m, the same spacing as retail PEI's heightmap
        public const float SeaLevel = 25.6f;          // Terrain.SeaLevelY's default, so swimming/buoyancy just work
        /// <summary>Kept under the wire's Y range (9 int bits = +-256 m) so a future MP build can carry any height.</summary>
        public const float MaxHeight = 240f;

        public enum Layer : byte { Dirt = 0, Wheat = 1, Grass = 2, Gravel = 3, Road = 4, Sand = 5, Snow = 6, Stone = 7 }
        public enum TreeKind : byte { Pine = 0, Birch = 1, Maple = 2 }
        public enum FoliageKind : byte { Grass = 0, Flower0 = 1, Flower1 = 2, Flower2 = 3, Flower3 = 4, Pebble = 5, PebbleSand = 6, Bush0 = 7, Bush1 = 8 }

        public readonly int Seed;
        readonly ulong _s;

        public InfiniteTerrain(int seed)
        {
            Seed = seed;
            _s = Mix((ulong)(uint)seed * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL);
            Roads = new InfiniteRoads(this, _s);
            Pylons = new InfinitePylons(this, _s);
        }

        // ---------------------------------------------------------------------------------------------------
        // Noise. Integer lattice + double cell lookup; see the class note on precision far from the origin.

        internal static ulong Mix(ulong h)
        {
            unchecked
            {
                h ^= h >> 31; h *= 0xBF58476D1CE4E5B9UL;
                h ^= h >> 27; h *= 0x94D049BB133111EBUL;
                h ^= h >> 31;
                return h;
            }
        }

        internal static uint Hash(long x, long z, ulong s)
        {
            unchecked { return (uint)(Mix((ulong)x * 0x9E3779B97F4A7C15UL ^ Mix((ulong)z + s)) >> 32); }
        }

        // 16 unit gradients as LITERALS (not cos/sin of an angle: library trig is not bit-identical across CPUs)
        static readonly float[] GX = { 1f, 0.92387953f, 0.70710678f, 0.38268343f, 0f, -0.38268343f, -0.70710678f, -0.92387953f,
                                       -1f, -0.92387953f, -0.70710678f, -0.38268343f, 0f, 0.38268343f, 0.70710678f, 0.92387953f };
        static readonly float[] GZ = { 0f, 0.38268343f, 0.70710678f, 0.92387953f, 1f, 0.92387953f, 0.70710678f, 0.38268343f,
                                       0f, -0.38268343f, -0.70710678f, -0.92387953f, -1f, -0.92387953f, -0.70710678f, -0.38268343f };

        static float Corner(long ix, long iz, ulong s, float dx, float dz)
        {
            uint h = Hash(ix, iz, s) & 15u;
            return GX[h] * dx + GZ[h] * dz;
        }

        static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

        /// <summary>2D gradient noise in about [-1, 1]. x/z are in LATTICE units (already divided by the wavelength).</summary>
        internal static float Gradient(double x, double z, ulong s)
        {
            double fx = Math.Floor(x), fz = Math.Floor(z);
            long ix = (long)fx, iz = (long)fz;
            float tx = (float)(x - fx), tz = (float)(z - fz);   // the ONLY double->float step: a value in [0,1)
            float a = Corner(ix, iz, s, tx, tz), b = Corner(ix + 1, iz, s, tx - 1f, tz);
            float c = Corner(ix, iz + 1, s, tx, tz - 1f), d = Corner(ix + 1, iz + 1, s, tx - 1f, tz - 1f);
            float u = Fade(tx), v = Fade(tz);
            float ab = a + (b - a) * u, cd = c + (d - c) * u;
            return (ab + (cd - ab) * v) * 1.41421356f;
        }

        /// <summary>Fractal sum, normalised by the amplitude actually summed (see ProcIsland.Fbm: otherwise the
        /// octave count becomes a contrast knob). Each octave is scaled x2 and rotated by a fixed 36.87° (the 3-4-5
        /// triangle) so the lattices of successive octaves do not line up into an axis grain.</summary>
        float Fbm(double x, double z, double wavelength, int octaves, ulong salt)
        {
            double px = x / wavelength, pz = z / wavelength;
            float sum = 0f, amp = 1f, norm = 0f;
            ulong s = _s ^ salt;
            for (int i = 0; i < octaves; i++)
            {
                sum += amp * Gradient(px, pz, s);
                norm += amp;
                amp *= 0.5f;
                double rx = px * 1.6 - pz * 1.2, rz = px * 1.2 + pz * 1.6;   // x2 and rotate 36.87°
                px = rx + 17.13; pz = rz - 9.71;
                s = Mix(s + 0x9E3779B97F4A7C15UL);
            }
            return sum / norm;
        }

        /// <summary>Ridged multifractal in [0, 1]: sharp crests where the noise crosses zero, each octave weighted by
        /// the one before so the detail gathers ON the ridges rather than spread evenly over the slopes.</summary>
        float Ridged(double x, double z, double wavelength, int octaves, ulong salt)
        {
            double px = x / wavelength, pz = z / wavelength;
            float sum = 0f, amp = 1f, norm = 0f, weight = 1f;
            ulong s = _s ^ salt;
            for (int i = 0; i < octaves; i++)
            {
                float r = 1f - Math.Abs(Gradient(px, pz, s));
                r *= r;
                r *= weight;
                weight = Math.Clamp(r * 1.6f, 0f, 1f);
                sum += r * amp;
                norm += amp;
                amp *= 0.5f;
                double rx = px * 1.6 - pz * 1.2, rz = px * 1.2 + pz * 1.6;
                px = rx + 5.31; pz = rz + 11.77;
                s = Mix(s + 0xD1B54A32D192ED03UL);
            }
            return sum / norm;
        }

        static float Smoothstep(float a, float b, float x)
        {
            float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        // ---------------------------------------------------------------------------------------------------
        // The landscape. All constants are WORLD METRES, so a coastline is argued about in metres.

        const ulong SaltWarpX = 0x1111, SaltWarpZ = 0x2222, SaltContinent = 0x3333, SaltMountainMask = 0x4444,
                    SaltRidge = 0x5555, SaltHills = 0x6666, SaltDetail = 0x7777, SaltTemp = 0x8888,
                    SaltMoist = 0x9999, SaltForest = 0xAAAA, SaltPatch = 0xBBBB, SaltTree = 0xCCCC, SaltVerge = 0xBEE5,
                    SaltRoad = 0xDDDD, SaltFoliage = 0xEEEE, SaltMeadow = 0xF0F0;

        /// <summary>Ground height (world Y, metres) at an absolute position, roads cut in.</summary>
        public float HeightAt(double x, double z) => Sample(x, z, out _);

        /// <summary>Ground height AND what the roads say about the point. A road is part of the ground, not laid on it:
        /// inside its paved half-width the height IS the road's smoothed profile (less the bed), and across its shoulder
        /// it blends back to the land -- so the collider, the mesh and the splat agree on where the road is without
        /// anything downstream knowing roads exist.</summary>
        public float Sample(double x, double z, out RoadHit hit) => SampleWith(Roads.LinesIn(x - 1, z - 1, x + 1, z + 1), x, z, out hit);

        /// <summary>Sample against a working set of road lines (a region's, gathered once).</summary>
        public float SampleWith(List<InfiniteRoads.Line> lines, double x, double z, out RoadHit hit,
                                InfiniteRoads.TunnelSpan mouthOf = null, InfiniteRoads.MouthSide mouth = InfiniteRoads.MouthSide.None)
        {
            float raw = RawHeight(x, z);
            hit = InfiniteRoads.Influence(lines, x, z, mouthOf, mouth);
            // the bed sits Bed under the paved surface: a ribbon laid at the profile then always covers the ground's own
            // triangles (the grid's vertices poked through as lines across the lanes when the bed WAS the surface)
            float g = hit.Any ? raw + (hit.Height - InfiniteRoads.Bed - raw) * hit.Weight : raw;
            return hit.Tunnel ? InfiniteRoads.TunnelGround(hit, g) : g;
        }

        /// <summary>What someone standing here stands on: the ground -- or, inside a tunnel's bore, its floor (the hill
        /// is overhead). The fall-through guard and anything else asking "where is the floor" use this, not HeightAt.</summary>
        public float WalkableHeightAt(double x, double z)
        {
            float g = Sample(x, z, out var hit);
            return hit.Tunnel && Math.Abs(hit.TunnelLat) <= InfiniteRoads.BoreReachOf(hit.TunnelKind)
                ? hit.TunnelRoad - InfiniteRoads.TunnelBedBelow(hit.TunnelKind) : g;
        }

        /// <summary>The land before any road touches it.</summary>
        /// <summary>The ground BEFORE any road carves it: what a bridge pier stands on, and what a raised stretch is
        /// measured against.</summary>
        public float NaturalHeight(double x, double z) => RawHeight(x, z);

        internal float RawHeight(double x, double z)
        {
            // domain warp: drag the coordinates by a slow field so coasts grow bays and ridges bend
            double wx = x + 220.0 * Fbm(x, z, 1100.0, 3, SaltWarpX);
            double wz = z + 220.0 * Fbm(x, z, 1100.0, 3, SaltWarpZ);

            // continents: ~8 km features decide ocean vs land. Biased upward so most of the world is land you can walk.
            float c = Fbm(wx, wz, 11000.0, 5, SaltContinent) + 0.10f;
            float land = Smoothstep(-0.10f, 0.06f, c);          // 0 = open sea, 1 = inland
            float inland = Smoothstep(0.04f, 0.50f, c);         // how far from the coast, for the big relief
            float seabed = SeaLevel - 5f - 34f * Smoothstep(-0.08f, -0.45f, c);

            // ON LAND NOTHING DIPS BELOW THE SEA: the hills are ONE-SIDED (0..1, not -1..1). Signed hills put the
            // ground under the water all along every coast, and the first map came back speckled with a thousand
            // pixel-sized ponds a few metres inland. Water is the continent field's decision and nobody else's.
            float hills01 = 0.5f + 0.5f * Fbm(wx, wz, 520.0, 5, SaltHills);
            // mountains: ridged noise, gated by a slow mask so ranges come in belts rather than everywhere
            float mmask = Smoothstep(-0.05f, 0.35f, Fbm(wx, wz, 4200.0, 3, SaltMountainMask)) * land;
            float ridge = Ridged(wx, wz, 1700.0, 5, SaltRidge);
            float mountains = ridge * ridge * 230f * mmask;
            float landH = SeaLevel + 2.5f + 24f * inland + hills01 * (5f + 26f * inland) * (1f - 0.5f * mmask) + mountains;

            float detail = Fbm(x, z, 48.0, 3, SaltDetail) * 1.4f * (0.25f + 0.75f * inland);
            float h = seabed + (landH - seabed) * land + detail;
            // soft ceiling: compress above 190 m instead of clipping, so the tallest peaks stay peaks, not plateaus
            if (h > 190f) h = 190f + (MaxHeight - 190f) * (1f - MathF.Exp(-(h - 190f) / (MaxHeight - 190f)));
            return h;
        }

        /// <summary>The MOUNTAIN part of the land's height at a point: the ridged belts RawHeight adds, without the hills
        /// under them. Rolling hills stay within ~30 m of their base; this is what stands above that.</summary>
        public float MountainHeight(double x, double z)
        {
            double wx = x + 220.0 * Fbm(x, z, 1100.0, 3, SaltWarpX);
            double wz = z + 220.0 * Fbm(x, z, 1100.0, 3, SaltWarpZ);
            float land = Smoothstep(-0.10f, 0.06f, Fbm(wx, wz, 11000.0, 5, SaltContinent) + 0.10f);
            float mmask = Smoothstep(-0.05f, 0.35f, Fbm(wx, wz, 4200.0, 3, SaltMountainMask)) * land;
            if (mmask <= 0f) return 0f;
            float ridge = Ridged(wx, wz, 1700.0, 5, SaltRidge);
            return ridge * ridge * 230f * mmask;
        }

        /// <summary>The finest octave band on its own, exposed so a test can compare the noise's statistics at the
        /// origin against 10^7 m out -- the far-origin failure shows up there first, as grain or as lost variance.</summary>
        public float DetailNoise(double x, double z) => Fbm(x, z, 48.0, 3, SaltDetail);

        /// <summary>Temperature 0 (cold) .. 1 (warm) and moisture 0..1 -- slow fields that pick the biome.</summary>
        public void Climate(double x, double z, float height, out float temp, out float moist)
        {
            temp = Math.Clamp(0.5f + 0.75f * Fbm(x, z, 9000.0, 3, SaltTemp) - Math.Max(0f, height - 60f) / 260f, 0f, 1f);
            moist = Math.Clamp(0.5f + 0.75f * Fbm(x, z, 5200.0, 3, SaltMoist), 0f, 1f);
        }

        /// <summary>The splat layer for a point, from its height, slope (rise over run), climate -- and roads first.</summary>
        /// <summary>How far past its asphalt a road's bed shows (it was 1.5 m for every class: one LOD0 vertex, at most).
        /// A rail's is NEGATIVE: its formation is wide (InfiniteRoads.RailFormationHalf, for the ground mesh's sake) and
        /// only the ballast and a 1.5 m cess beside it are worn to gravel.</summary>
        public static float VergeWidth(RoadKind k) => k switch { RoadKind.Highway => 6f, RoadKind.Main => 5f, RoadKind.Small => 4f,
            RoadKind.Rail => InfiniteRoads.RailHalfWidth + 1.5f - InfiniteRoads.RailFormationHalf, _ => 3f };
        /// <summary>The dirt worn round a power line's footing: a roadside pole's, and a pylon's -- its 7.9 m lattice base
        /// (InfinitePylons.FootHalf, half-diagonal 5.6 m) and a couple of metres of trampled ground round it.</summary>
        public const float PoleDirt = 2.5f;
        public static float PylonDirt => InfinitePylons.FootHalf * 1.42f + 2f;

        public Layer LayerAt(double x, double z, float h, float slope, float roadClear = float.MaxValue, RoadKind roadKind = RoadKind.Main)
        {
            // under and beside a road: its bed. NOT Layer.Road -- that is the parking-lot / car-park paving (strawberry
            // 2026-10-09); the carriageway itself is the ribbon drawn on top. A trail's bed is dirt, a paved road's gravel.
            // The VERGE that wears it (strawberry 2026-10-10: "increase the dirt painted footprint of all roads") is
            // VergeWidth past the asphalt, its edge broken up by a metre or so of noise so it is not a ruled line.
            if (roadClear < VergeWidth(roadKind) + 1.2f * Fbm(x, z, 11.0, 1, SaltVerge)) return roadKind == RoadKind.Trail ? Layer.Dirt : Layer.Gravel;
            Climate(x, z, h, out float temp, out float moist);
            if (h < SeaLevel + 1.6f) return slope > 0.6f ? Layer.Gravel : Layer.Sand;   // beaches and the seabed
            if (slope > 0.85f) return Layer.Stone;                                          // cliffs
            float snowLine = 150f + 70f * temp;
            if (h > snowLine) return slope > 0.6f ? Layer.Stone : Layer.Snow;
            if (slope > 0.5f) return Layer.Gravel;
            float patch = Fbm(x, z, 70.0, 2, SaltPatch);
            if (moist < 0.32f && temp > 0.45f) return patch > 0.25f ? Layer.Dirt : Layer.Wheat;   // dry grassland
            if (patch > 0.55f) return Layer.Dirt;
            return Layer.Grass;
        }

        // ---------------------------------------------------------------------------------------------------
        // Regions.

        public static int CellsFor(int lod) => FullCells >> lod;

        /// <summary>Generate one region. Pure: same (seed, coord, lod) -> the same arrays, every time, on any thread.</summary>
        public RegionData Generate(RegionCoord rc, int lod)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int n = CellsFor(lod), v = n + 1, b = v + 2;      // b = with a one-sample border for the normals
            float sp = RegionSize / n;
            double ox = rc.MinX, oz = rc.MinZ;
            // the region's road working set, gathered ONCE: every sample below tests these few lines, not the grid
            var lines = Roads.LinesIn(ox - sp - 1, oz - sp - 1, ox + RegionSize + sp + 1, oz + RegionSize + sp + 1);
            var hb = new float[b * b];
            var rh = new RoadHit[b * b];
            for (int j = 0; j < b; j++)
                for (int i = 0; i < b; i++)
                    hb[j * b + i] = SampleWith(lines, ox + (i - 1) * (double)sp, oz + (j - 1) * (double)sp, out rh[j * b + i]);

            var d = new RegionData { Coord = rc, Lod = lod, Cells = n, Spacing = sp,
                                     Heights = new float[v * v], Normals = new float[v * v * 3], Layers = new byte[v * v], RoadClear = new float[v * v],
                                     MinHeight = float.MaxValue, MaxHeight = float.MinValue };
            for (int j = 0; j < v; j++)
                for (int i = 0; i < v; i++)
                {
                    int k = j * v + i, kb = (j + 1) * b + (i + 1);
                    float h = hb[kb];
                    d.Heights[k] = h;
                    d.RoadClear[k] = Math.Min(rh[kb].Clear, 64f);
                    if (lod == 0 && rh[kb].Hole) { (d.Holes ??= new bool[v * v])[k] = true; d.RoadClear[k] = -1f; }   // nothing grows in a hole
                    if (rh[kb].Tunnel && Math.Abs(rh[kb].TunnelLat) <= InfiniteRoads.ShellReachOf(rh[kb].TunnelKind) + 2f)
                        (d.OverTunnel ??= new bool[v * v])[k] = true;
                    if (h < d.MinHeight) d.MinHeight = h;
                    if (h > d.MaxHeight) d.MaxHeight = h;
                    float dx = (hb[kb + 1] - hb[kb - 1]) / (2f * sp), dz = (hb[kb + b] - hb[kb - b]) / (2f * sp);
                    float inv = 1f / MathF.Sqrt(dx * dx + 1f + dz * dz);
                    d.Normals[k * 3] = -dx * inv; d.Normals[k * 3 + 1] = inv; d.Normals[k * 3 + 2] = -dz * inv;
                    d.Layers[k] = (byte)LayerAt(ox + i * (double)sp, oz + j * (double)sp, h, MathF.Sqrt(dx * dx + dz * dz), rh[kb].Clear, rh[kb].Kind);
                }
            // POWER LINES stand on dirt (strawberry 2026-10-10: "add it to the power lines, both big and small ones"): the
            // poles and pylons in and just round this region (so a footing on a region's edge paints both sides of it)
            double pm = PylonDirt + sp;
            var polesWide = lod <= 1 ? Roads.PolesIn(lines, ox - PoleDirt - sp, oz - PoleDirt - sp, ox + RegionSize + PoleDirt + sp, oz + RegionSize + PoleDirt + sp) : null;
            var pylonsWide = Pylons.PylonsIn(ox - pm, oz - pm, ox + RegionSize + pm, oz + RegionSize + pm);
            void Footing(double fx, double fz, float r)
            {
                int i0 = Math.Max(0, (int)Math.Floor((fx - r - ox) / sp)), i1 = Math.Min(n, (int)Math.Ceiling((fx + r - ox) / sp));
                int j0 = Math.Max(0, (int)Math.Floor((fz - r - oz) / sp)), j1 = Math.Min(n, (int)Math.Ceiling((fz + r - oz) / sp));
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        double ex = ox + i * (double)sp - fx, ez = oz + j * (double)sp - fz;
                        int k = j * v + i;
                        // the paved roads' verge layer: it reads as worn dirt, and no grass or bush grows on it
                        if (ex * ex + ez * ez <= r * r && d.Heights[k] >= SeaLevel + 1.6f) d.Layers[k] = (byte)Layer.Gravel;
                    }
            }
            if (polesWide != null) foreach (var p in polesWide) Footing(p.X, p.Z, PoleDirt);
            foreach (var p in pylonsWide) Footing(p.X, p.Z, PylonDirt);
            d.Trees = PlaceTrees(rc, lines);   // every LOD: the far rings draw them as billboards (RegionStreamer impostors)
            d.Foliage = lod == 0 ? PlaceFoliage(d) : null;
            d.Roads = Roads.PiecesIn(lines, ox, oz, ox + RegionSize, oz + RegionSize, Math.Max(4f, sp));
            d.Poles = polesWide?.FindAll(p => p.X >= ox && p.X < ox + RegionSize && p.Z >= oz && p.Z < oz + RegionSize);
            d.Bridges = Roads.BridgesIn(lines, ox, oz, ox + RegionSize, oz + RegionSize);
            d.Pylons = pylonsWide.FindAll(p => p.X >= ox && p.X < ox + RegionSize && p.Z >= oz && p.Z < oz + RegionSize);   // every LOD: 50 m landmarks
            d.Tunnels = Roads.TunnelsIn(lines, ox, oz, ox + RegionSize, oz + RegionSize);
            if (d.Tunnels.Count > 0)
            {
                d.TunnelMouths = new List<MouthData[]>(d.Tunnels.Count);
                foreach (var t in d.Tunnels) d.TunnelMouths.Add(MouthGround(t));
            }
            d.Rails = Roads.RailsIn(lines, ox, oz, ox + RegionSize, oz + RegionSize);
            d.LevelCrossings = Roads.LevelCrossingsIn(lines, ox, oz, ox + RegionSize, oz + RegionSize);
            d.CrossingSigns = Roads.CrossingSignsIn(lines, ox, oz, ox + RegionSize, oz + RegionSize);
            d.GenMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return d;
        }

        public const float TreeCell = 9f;   // one candidate per 9 m cell, jittered: ~800 per region before density

        /// <summary>Trees for a region, independent of LOD (so a region does not re-forest when its LOD changes).
        /// One jittered candidate per cell, kept with a probability from a forest-density field; never on beaches,
        /// cliffs, snow or under water.</summary>
        public List<TreeSpawn> PlaceTrees(RegionCoord rc) =>
            PlaceTrees(rc, Roads.LinesIn(rc.MinX - 4, rc.MinZ - 4, rc.MinX + RegionSize + 4, rc.MinZ + RegionSize + 4));

        List<TreeSpawn> PlaceTrees(RegionCoord rc, List<InfiniteRoads.Line> lines)
        {
            var list = new List<TreeSpawn>();
            int cells = (int)(RegionSize / TreeCell);
            double ox = rc.MinX, oz = rc.MinZ;
            // the high-voltage lines' corridors are kept clear: wires 9 m up at mid-span would run through a canopy
            var pylonLines = Pylons.LinesIn(ox, oz, ox + RegionSize, oz + RegionSize, InfinitePylons.TreeKeep + 10);
            ulong s = _s ^ SaltTree;
            for (int cj = 0; cj < cells; cj++)
                for (int ci = 0; ci < cells; ci++)
                {
                    long gx = (long)rc.X * cells + ci, gz = (long)rc.Z * cells + cj;   // global cell id: stable across regions
                    uint h1 = Hash(gx, gz, s), h2 = Hash(gx, gz, s + 1), h3 = Hash(gx, gz, s + 2);
                    float lx = (ci + (h1 & 0xFFFF) / 65536f) * TreeCell, lz = (cj + (h1 >> 16) / 65536f) * TreeCell;
                    double ax = ox + lx, az = oz + lz;
                    float forest = Smoothstep(-0.05f, 0.45f, Fbm(ax, az, 1300.0, 3, SaltForest));
                    if ((h2 & 0xFFFF) / 65536f >= forest * 0.55f) continue;
                    float y = SampleWith(lines, ax, az, out var treeRoad);
                    if (y < SeaLevel + 2.5f) continue;
                    if (treeRoad.Clear < 5f) continue;   // keep every road and its verge clear
                    if (pylonLines.Count > 0 && InfinitePylons.CorridorDistance(pylonLines, ax, az) < InfinitePylons.TreeKeep) continue;
                    if (treeRoad.Tunnel && treeRoad.TunnelIn < InfiniteRoads.TunnelHoleIn + 8f
                        && Math.Abs(treeRoad.TunnelLat) < InfiniteRoads.ShellReachOf(treeRoad.TunnelKind)) continue;   // nor over a portal's mouth
                    float sx = SampleWith(lines, ax + 2.0, az, out _) - SampleWith(lines, ax - 2.0, az, out _);
                    float sz = SampleWith(lines, ax, az + 2.0, out _) - SampleWith(lines, ax, az - 2.0, out _);
                    float slope = MathF.Sqrt(sx * sx + sz * sz) / 4f;
                    if (slope > 0.55f) continue;
                    Climate(ax, az, y, out float temp, out float moist);
                    if (y > 140f + 70f * temp) continue;   // below the snow line only
                    TreeKind kind = temp < 0.42f || y > 95f ? TreeKind.Pine : ((h3 & 1) == 0 ? TreeKind.Birch : TreeKind.Maple);
                    list.Add(new TreeSpawn { X = lx, Y = y, Z = lz, Yaw = (h3 >> 8 & 0xFFFF) / 65536f * 360f,
                                             Scale = 0.8f + (h3 >> 24) / 255f * 0.45f, Kind = (byte)kind });
                }
            return list;
        }

        // ---------------------------------------------------------------------------------------------------
        // Roads live in InfiniteRoads (the four classes). These are the generator's views onto them.

        /// <summary>The road network (highways, main roads, small roads, trails).</summary>
        public readonly InfiniteRoads Roads;
        /// <summary>The high-voltage lines: their own network of lattice pylons, clear of the roads.</summary>
        public readonly InfinitePylons Pylons;

        /// <summary>Height of a region's OWN mesh at a local point -- the two triangles each quad is drawn as. Anything
        /// laid on the ground (a road surface, a blade of grass) must sit on this, not on the smooth function, or it
        /// floats over the dips between vertices and sinks under the bumps.</summary>
        public static float MeshHeightAt(RegionData d, float lx, float lz) => MeshHeight(d, lx, lz);

        /// <summary>Clearance to the nearest asphalt of any road (negative on it). Public for the spawn search and tests.</summary>
        public float RoadClearance(double x, double z) => Roads.Influence(x, z).Clear;

        /// <summary>A tunnel's two MOUTHS' ground [end]. Every LOD0 cell touching a hole is dropped from the terrain (a
        /// heightfield cannot overhang the mouth), and something has to stand in for it: a concrete collar at the hill's
        /// cap stood 6 m out of a lower hill ("the tunnel is still humping?"), and one laid on the ground covered only a
        /// rectangle of the cells -- on any diagonal heading the corners it missed, and in front of the facade beside the
        /// bores nothing but a flat apron at the road's bed, a pit with the cut slope's edge hanging open over it
        /// (strawberry: "theres a bunch of holes/gaps in the terrain around the tunnels").
        ///
        /// So the cells are drawn again, exactly: the terrain's own vertices, its own split, cut by the facade line.
        /// In front of it a vertex stands at the approach's CUT run on (MouthSide.Cut), behind it at the HILL run out
        /// (MouthSide.Hill) -- except where the cell's edge is shared with a drawn cell on that side, where it is the
        /// terrain's own height, so the edge meets its neighbour exactly. Along the facade the two sides' heights differ
        /// only where a hole vertex weighs in, and the headwall closes that step (FaceU / FaceFront / FaceBehind).</summary>
        public MouthData[] MouthGround(InfiniteRoads.TunnelSpan t) => new[] { MouthOf(t, 0), MouthOf(t, 1) };

        MouthData MouthOf(InfiniteRoads.TunnelSpan t, int end)
        {
            const double sp = FullSpacing;
            int m = t.X.Length, ia = end == 0 ? 0 : m - 1, ib = end == 0 ? 1 : m - 2;
            double px = t.X[ia], pz = t.Z[ia], fx = t.X[ib] - px, fz = t.Z[ib] - pz, fl = Math.Max(1e-9, Math.Sqrt(fx * fx + fz * fz));
            fx /= fl; fz /= fl;
            double sx = -fz, sz = fx;
            if (end == 1) { sx = -sx; sz = -sz; }
            double In(double x, double z) => (x - px) * fx + (z - pz) * fz;           // metres in from the facade
            double Across(double x, double z) => (x - px) * sx + (z - pz) * sz;
            // the window: every vertex this mouth's holes can be (TunnelHoleIn in, TunnelHoleBeside past the bores), the
            // cells round them (a cell diagonal on any heading), and a vertex more for the normals
            double diag = sp * Math.Sqrt(2.0), holeLat = InfiniteRoads.BoreReachOf(t.Kind) + InfiniteRoads.TunnelHoleBeside;
            double aLo = -diag - 1, aHi = InfiniteRoads.TunnelHoleIn + diag + 1, lHi = holeLat + diag + 1;
            double minX = double.MaxValue, maxX = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
            foreach (var (a, l) in new[] { (aLo, -lHi), (aLo, lHi), (aHi, -lHi), (aHi, lHi) })
            {
                double x = px + fx * a + sx * l, z = pz + fz * a + sz * l;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
            }
            long I0 = (long)Math.Floor(minX / sp) - 1, J0 = (long)Math.Floor(minZ / sp) - 1;
            int W = (int)((long)Math.Ceiling(maxX / sp) + 1 - I0 + 1), H = (int)((long)Math.Ceiling(maxZ / sp) + 1 - J0 + 1);
            var lines = Roads.LinesIn(minX - 2 * sp, minZ - 2 * sp, maxX + 2 * sp, maxZ + 2 * sp);
            int nv = W * H, cw = W - 1, ch = H - 1;
            double VX(int k) => (I0 + k % W) * sp;
            double VZ(int k) => (J0 + k / W) * sp;
            var g = new float[nv]; var hits = new RoadHit[nv]; var hole = new bool[nv]; var aIn = new double[nv];
            for (int k = 0; k < nv; k++)
            {
                double x = VX(k), z = VZ(k);
                g[k] = SampleWith(lines, x, z, out hits[k]);
                aIn[k] = In(x, z);
                var h = hits[k];
                // this mouth's own holes; another tunnel's are its own mouth's to draw
                hole[k] = h.Hole && h.TunnelKind == t.Kind && Math.Abs(h.TunnelIn - aIn[k]) < 1.5
                          && Math.Abs(Math.Abs(h.TunnelLat) - Math.Abs(Across(x, z))) < 1.5;
            }
            // the cells the terrain drops for them: every one with a hole corner (RegionStreamer.Build)
            var cells = new bool[cw * ch];
            var used = new bool[nv];
            for (int cj = 0; cj < ch; cj++)
                for (int ci = 0; ci < cw; ci++)
                {
                    int a = cj * W + ci, b = a + 1, c = a + W, e = c + 1;
                    if (!(hole[a] || hole[b] || hole[c] || hole[e])) continue;
                    cells[cj * cw + ci] = true;
                    used[a] = used[b] = used[c] = used[e] = true;
                }
            bool Cell(int ci, int cj) => ci >= 0 && cj >= 0 && ci < cw && cj < ch && cells[cj * cw + ci];
            // EDGES SHARED WITH A DRAWN CELL keep the terrain's heights on whichever side of the facade they reach, so the
            // two meshes meet exactly along them
            var pinF = new bool[nv]; var pinB = new bool[nv];
            void Edge(int k0, int k1)
            {
                if (Math.Min(aIn[k0], aIn[k1]) <= 0) pinF[k0] = pinF[k1] = true;
                if (Math.Max(aIn[k0], aIn[k1]) >= 0) pinB[k0] = pinB[k1] = true;
            }
            for (int cj = 0; cj < ch; cj++)
                for (int ci = 0; ci < cw; ci++)
                {
                    if (!cells[cj * cw + ci]) continue;
                    int a = cj * W + ci, b = a + 1, c = a + W, e = c + 1;
                    if (!Cell(ci, cj - 1)) Edge(a, b);
                    if (!Cell(ci, cj + 1)) Edge(c, e);
                    if (!Cell(ci - 1, cj)) Edge(a, c);
                    if (!Cell(ci + 1, cj)) Edge(b, e);
                }
            var F = (float[])g.Clone(); var B = (float[])g.Clone();
            for (int k = 0; k < nv; k++)
            {
                if (!used[k]) continue;
                if (!pinF[k]) F[k] = SampleWith(lines, VX(k), VZ(k), out _, t, InfiniteRoads.MouthSide.Cut);
                if (!pinB[k]) B[k] = SampleWith(lines, VX(k), VZ(k), out _, t, InfiniteRoads.MouthSide.Hill);
            }
            // normals as Generate makes them (central differences): the terrain's own where a side is pinned, so the
            // lighting runs on across the seam; off that side's own heights elsewhere
            (float, float, float) Nrm(float[] f, int k)
            {
                int i = k % W, j = k / W;
                if (i == 0 || j == 0 || i == W - 1 || j == H - 1) return (0f, 1f, 0f);
                float dx = (f[k + 1] - f[k - 1]) / (2f * (float)sp), dz = (f[k + W] - f[k - W]) / (2f * (float)sp);
                float inv = 1f / MathF.Sqrt(dx * dx + 1f + dz * dz);
                return (-dx * inv, inv, -dz * inv);
            }
            var splat = new byte[nv];
            for (int k = 0; k < nv; k++)
            {
                if (!used[k]) continue;
                var (gx, gy, gz) = Nrm(g, k);
                float slope = MathF.Sqrt(gx * gx + gz * gz) / gy;
                splat[k] = (byte)LayerAt(VX(k), VZ(k), g[k], slope, hits[k].Clear, hits[k].Kind);
            }

            var X = new List<double>(); var Z = new List<double>(); var Y = new List<float>();
            var NX = new List<float>(); var NY = new List<float>(); var NZ = new List<float>();
            var U = new List<float>(); var V = new List<float>(); var T = new List<int>();
            var face = new List<(double u, float f, float b)>();
            // one triangle of the terrain's split, cut by the facade line: in front the cut, behind the hill
            void Emit(int k0, int k1, int k2, bool drawn)
            {
                int[] ks = { k0, k1, k2 };
                for (int side = 0; side < 2; side++)
                {
                    // side 0 keeps aIn <= 0 (front), side 1 aIn >= 0 (behind)
                    float[] hs = drawn ? g : side == 0 ? F : B;
                    bool[] pin = side == 0 ? pinF : pinB;
                    var poly = new List<(double x, double z, float y, float nx, float ny, float nz)>(4);
                    (double, double, float, float, float, float) Vtx(int k)
                    {
                        var (nx, ny, nz) = Nrm(drawn || pin[k] ? g : hs, k);
                        return (VX(k), VZ(k), hs[k], nx, ny, nz);
                    }
                    for (int q = 0; q < 3; q++)
                    {
                        int ka = ks[q], kb = ks[(q + 1) % 3];
                        double da = side == 0 ? -aIn[ka] : aIn[ka], db = side == 0 ? -aIn[kb] : aIn[kb];
                        if (da >= 0) poly.Add(Vtx(ka));
                        if (da >= 0 != db >= 0 && Math.Abs(da - db) > 1e-12)
                        {
                            double s = da / (da - db);
                            var A = Vtx(ka); var Bv = Vtx(kb);
                            float nx = (float)(A.Item4 + (Bv.Item4 - A.Item4) * s), ny = (float)(A.Item5 + (Bv.Item5 - A.Item5) * s), nz = (float)(A.Item6 + (Bv.Item6 - A.Item6) * s);
                            float nl = MathF.Max(1e-6f, MathF.Sqrt(nx * nx + ny * ny + nz * nz));
                            double cx = A.Item1 + (Bv.Item1 - A.Item1) * s, cz = A.Item2 + (Bv.Item2 - A.Item2) * s;
                            poly.Add((cx, cz, (float)(A.Item3 + (Bv.Item3 - A.Item3) * s), nx / nl, ny / nl, nz / nl));
                            if (side == 0)
                            {
                                float[] fh = drawn ? g : F, bh = drawn ? g : B;
                                face.Add((Across(cx, cz), (float)(fh[ka] + (fh[kb] - fh[ka]) * s), (float)(bh[ka] + (bh[kb] - bh[ka]) * s)));
                            }
                        }
                    }
                    if (drawn || poly.Count < 3) continue;   // a drawn cell is only walked for the facade's profile
                    int first = X.Count;
                    foreach (var p in poly)
                    {
                        X.Add(p.x); Z.Add(p.z); Y.Add(p.y); NX.Add(p.nx); NY.Add(p.ny); NZ.Add(p.nz);
                        U.Add((float)((p.x / sp - I0 + 0.5) / W)); V.Add((float)((p.z / sp - J0 + 0.5) / H));
                    }
                    for (int q = 1; q + 1 < poly.Count; q++) { T.Add(first); T.Add(first + q); T.Add(first + q + 1); }
                }
            }
            for (int cj = 0; cj < ch; cj++)
                for (int ci = 0; ci < cw; ci++)
                {
                    int a = cj * W + ci, b = a + 1, c = a + W, e = c + 1;
                    bool drawn = !cells[cj * cw + ci];
                    // the drawn cells are walked too, for the facade's profile where it runs across them between the mouth's
                    if (drawn && (Math.Min(Math.Min(aIn[a], aIn[b]), Math.Min(aIn[c], aIn[e])) > 0 || Math.Max(Math.Max(aIn[a], aIn[b]), Math.Max(aIn[c], aIn[e])) < 0)) continue;
                    Emit(a, b, c, drawn);   // the terrain's split: fx + fz <= 1 is (a, b, c), else (b, e, c)
                    Emit(b, e, c, drawn);
                }
            // the facade's profile: only as wide as the mouth's own crossings of it (a drawn cell's crossing between two of
            // them keeps it unbroken; past the outermost, the ground is whole and there is nothing to close)
            double uLo = double.MaxValue, uHi = double.MinValue;
            for (int cj = 0; cj < ch; cj++)
                for (int ci = 0; ci < cw; ci++)
                {
                    if (!cells[cj * cw + ci]) continue;
                    int a = cj * W + ci;
                    foreach (int k in new[] { a, a + 1, a + W, a + W + 1 })
                        foreach (int k2 in new[] { a, a + 1, a + W, a + W + 1 })
                        {
                            if (aIn[k] > 0 || aIn[k2] < 0 || aIn[k2] - aIn[k] < 1e-12) continue;
                            double s = -aIn[k] / (aIn[k2] - aIn[k]);
                            double u = Across(VX(k) + (VX(k2) - VX(k)) * s, VZ(k) + (VZ(k2) - VZ(k)) * s);
                            uLo = Math.Min(uLo, u); uHi = Math.Max(uHi, u);
                        }
                }
            face.Sort((p, q) => p.u.CompareTo(q.u));
            var fu = new List<float>(); var ff = new List<float>(); var fb = new List<float>();
            foreach (var p in face)
            {
                if (p.u < uLo - 1e-6 || p.u > uHi + 1e-6) continue;
                if (fu.Count > 0 && p.u - fu[fu.Count - 1] < 1e-4) { ff[ff.Count - 1] = Math.Min(ff[ff.Count - 1], p.f); fb[fb.Count - 1] = Math.Max(fb[fb.Count - 1], p.b); continue; }
                fu.Add((float)p.u); ff.Add(p.f); fb.Add(p.b);
            }
            return new MouthData
            {
                X = X.ToArray(), Z = Z.ToArray(), Y = Y.ToArray(), NX = NX.ToArray(), NY = NY.ToArray(), NZ = NZ.ToArray(),
                U = U.ToArray(), V = V.ToArray(), Tris = T.ToArray(), SplatW = W, SplatH = H, Splat = splat,
                FaceU = fu.ToArray(), FaceFront = ff.ToArray(), FaceBehind = fb.ToArray(),
                I0 = I0, J0 = J0, W = W, H = H, Cells = cells, Hole = hole, Ground = g, Front = F, Behind = B,
            };
        }
        const float FullSpacing = RegionSize / FullCells;

        // ---------------------------------------------------------------------------------------------------
        // Ground cover, scattered over a LOD0 region's own arrays (no extra noise calls for the grass: 25k blades a
        // region would cost more than the terrain did). Heights interpolate the SAME two triangles the mesh draws.

        public const float GrassCell = 1.6f, FlowerCell = 6f, PebbleCell = 6f, BushCell = 7f;

        static float MeshHeight(RegionData d, float lx, float lz)
        {
            int v = d.Cells + 1;
            float gx = lx / d.Spacing, gz = lz / d.Spacing;
            int i = Math.Clamp((int)gx, 0, d.Cells - 1), j = Math.Clamp((int)gz, 0, d.Cells - 1);
            float fx = gx - i, fz = gz - j;
            float ha = d.Heights[j * v + i], hb = d.Heights[j * v + i + 1], hc = d.Heights[(j + 1) * v + i], he = d.Heights[(j + 1) * v + i + 1];
            return fx + fz <= 1f ? ha + (hb - ha) * fx + (hc - ha) * fz       // triangle (a, b, c)
                                 : he + (hc - he) * (1f - fx) + (hb - he) * (1f - fz);   // triangle (b, e, c)
        }

        static float RoadClearNear(RegionData d, float lx, float lz)
        {
            int v = d.Cells + 1;
            float gx = lx / d.Spacing, gz = lz / d.Spacing;
            int i = Math.Clamp((int)gx, 0, d.Cells - 1), j = Math.Clamp((int)gz, 0, d.Cells - 1);
            float fx = gx - i, fz = gz - j;
            float a = d.RoadClear[j * v + i], b = d.RoadClear[j * v + i + 1], c = d.RoadClear[(j + 1) * v + i], e = d.RoadClear[(j + 1) * v + i + 1];
            return (a + (b - a) * fx) * (1f - fz) + (c + (e - c) * fx) * fz;
        }

        static Layer LayerNear(RegionData d, float lx, float lz)
        {
            int v = d.Cells + 1;
            int i = Math.Clamp((int)MathF.Round(lx / d.Spacing), 0, d.Cells), j = Math.Clamp((int)MathF.Round(lz / d.Spacing), 0, d.Cells);
            return (Layer)d.Layers[j * v + i];
        }

        public List<FoliageSpawn> PlaceFoliage(RegionData d)
        {
            var list = new List<FoliageSpawn>(24000);
            var rc = d.Coord;
            ulong s = _s ^ SaltFoliage;
            void Scatter(float cell, ulong salt, Func<float, float, uint, Layer, float, byte> pick)
            {
                int cells = (int)(RegionSize / cell);
                for (int cj = 0; cj < cells; cj++)
                    for (int ci = 0; ci < cells; ci++)
                    {
                        long gx = (long)rc.X * cells + ci, gz = (long)rc.Z * cells + cj;
                        uint h1 = Hash(gx, gz, s ^ salt), h2 = Hash(gx, gz, s ^ salt ^ 0x55);
                        float lx = (ci + (h1 & 0xFFFF) / 65536f) * cell, lz = (cj + (h1 >> 16) / 65536f) * cell;
                        float y = MeshHeight(d, lx, lz);
                        if (y < SeaLevel + 0.4f) continue;
                        if (RoadClearNear(d, lx, lz) < 1.2f) continue;   // nothing through the asphalt or its edge
                        byte k = pick(lx, lz, h2, LayerNear(d, lx, lz), y);
                        if (k == 255) continue;
                        list.Add(new FoliageSpawn { X = lx, Y = y, Z = lz, Yaw = (h2 >> 16) / 65536f * 360f,
                                                    Scale = 0.8f + ((h2 >> 8) & 0xFF) / 255f * 0.5f, Kind = k });
                    }
            }
            // grass: thick on Grass, thin on Wheat (dry) and Dirt
            Scatter(GrassCell, 1, (x, z, h, l, y) =>
            {
                float p = l == Layer.Grass ? 0.92f : l == Layer.Wheat ? 0.35f : l == Layer.Dirt ? 0.08f : 0f;
                return (h & 0xFFFF) / 65536f < p ? (byte)FoliageKind.Grass : (byte)255;
            });
            // flowers: in meadows, a slow noise field, so they come in drifts instead of a sprinkle
            Scatter(FlowerCell, 2, (x, z, h, l, y) =>
            {
                if (l != Layer.Grass) return 255;
                float meadow = Smoothstep(0.1f, 0.5f, Fbm(d.Coord.MinX + x, d.Coord.MinZ + z, 260.0, 2, SaltMeadow));
                return (h & 0xFFFF) / 65536f < meadow * 0.55f ? (byte)((int)FoliageKind.Flower0 + (int)(h % 4)) : (byte)255;
            });
            // pebbles: on bare ground and sand
            Scatter(PebbleCell, 3, (x, z, h, l, y) =>
            {
                float r = (h & 0xFFFF) / 65536f;
                if (l == Layer.Sand) return r < 0.3f ? (byte)FoliageKind.PebbleSand : (byte)255;
                if (l == Layer.Dirt || l == Layer.Gravel || l == Layer.Stone) return r < 0.35f ? (byte)FoliageKind.Pebble : (byte)255;
                return 255;
            });
            // bushes: thickest at forest edges, never on the road (its layer is Road/Gravel, so it never gets here)
            Scatter(BushCell, 4, (x, z, h, l, y) =>
            {
                if (l != Layer.Grass && l != Layer.Dirt) return 255;
                float f = Fbm(d.Coord.MinX + x, d.Coord.MinZ + z, 1300.0, 3, SaltForest);
                float edge = 1f - Math.Abs(f - 0.15f) / 0.35f;   // peaks where the forest field crosses into woodland
                return (h & 0xFFFF) / 65536f < 0.03f + 0.22f * Math.Max(0f, edge) ? (byte)((int)FoliageKind.Bush0 + (int)(h % 2)) : (byte)255;
            });
            return list;
        }

        /// <summary>A dry, walkable, grassy spot near an absolute position -- a spiral search out from it, preferring
        /// the side of a road (a world you spawn into next to a road is one you can see the roads of).</summary>
        public bool FindSpawn(double x, double z, out double sx, out double sz, out float sy, int maxRings = 400)
        {
            for (int pass = 0; pass < 2; pass++)
            for (int r = 0; r <= (pass == 0 ? 60 : maxRings); r++)
                for (int k = -r; k <= r; k++)
                    foreach (var (dx, dz) in new[] { (k, -r), (k, r), (-r, k), (r, k) })
                    {
                        if (r > 0 && Math.Abs(dx) != r && Math.Abs(dz) != r) continue;
                        double px = x + dx * 24.0, pz = z + dz * 24.0;
                        float h = Sample(px, pz, out var road);
                        if (h < SeaLevel + 4f || h > 120f) continue;
                        if (pass == 0 && (road.Clear < 7f || road.Clear > 30f)) continue;   // past the poles, in sight of a road
                        float gx = HeightAt(px + 3, pz) - HeightAt(px - 3, pz), gz = HeightAt(px, pz + 3) - HeightAt(px, pz - 3);
                        if (MathF.Sqrt(gx * gx + gz * gz) / 6f > 0.15f) continue;
                        sx = px; sz = pz; sy = h;
                        return true;
                    }
            sx = x; sz = z; sy = HeightAt(x, z);
            return false;
        }
    }
}
