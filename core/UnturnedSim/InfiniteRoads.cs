using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SDG.Unturned
{
    /// <summary>The four road classes (strawberry 2026-10-09): "several roads designated 'highways' (2x highway 2 type
    /// splines a fixed distance apart) that stay on a pretty set course, avoiding elevation shifts and obstacles while
    /// trying to stay somewhat straight, dont have power lines. then there are main roads, using the highway 1 road type,
    /// spread similarly to what we have now. then there are smaller roads. and then trails. both trails and small roads
    /// can branch off main roads".</summary>
    /// Rail (strawberry 2026-10-10: "add railways.") is not a road you drive, but it is a line the ground is carved to
    /// and every other class has to cross or keep clear of -- so it lives in the same network (InfiniteRails.cs).
    public enum RoadKind : byte { Highway = 0, Main = 1, Small = 2, Trail = 3, Rail = 4 }

    /// <summary>What the strongest road at a point says about it. Clear is the MINIMUM over every road of (distance to
    /// its centreline - its paved half-width): negative on asphalt, the room ground cover and trees go by.</summary>
    public struct RoadHit
    {
        public bool Any;          // some road's carve reaches this point
        public RoadKind Kind;     // ...the one whose carve is strongest
        public float Dist;        // distance to that road's centreline
        public float Height;      // that road's profile height at the closest point
        public float Weight;      // 1 on the road, falling to 0 across its shoulder
        public float Clear;       // min over ALL roads of (dist - paved half-width)
        /// <summary>Inside a highway tunnel's run (facade to facade, any distance across): the hill stands here, the
        /// carve does not apply, and TunnelGround shapes the ground round the bore.</summary>
        public bool Tunnel;
        public float TunnelIn;    // metres in from the nearer facade
        public float TunnelLat;   // signed metres from the route's centreline (+ = the +offset carriageway's side)
        public float TunnelRoad;  // the driven surface (TunnelSurfaceY) at the nearest point of the route
        public RoadKind TunnelKind;   // whose tunnel: a highway's twin bores, or a railway's one (TunnelShape)
        /// <summary>A ground vertex here is a HOLE: just inside a portal, across the bore -- the only place a heightfield
        /// would otherwise run straight across the tunnel mouth (it cannot overhang).</summary>
        public bool Hole;
    }

    /// <summary>
    /// The road network for the infinite world. Every road is a centreline polyline with a height profile, built the
    /// first time any region asks and cached; built identically whoever asks, so roads cross region borders like the
    /// ground does. Nothing here is global: each class is laid out on its own macro grid and every line is a function of
    /// (seed, grid cell) alone.
    ///
    ///   HIGHWAY  -- a long route per 12 km band, east-west and north-south, through anchors every 6 km. Between two
    ///               anchors a dynamic-programming search over a lateral corridor (+-900 m) picks the path that keeps the
    ///               grade low, stays off water and high ground, and turns as little as possible. Drawn as TWO Highway_1
    ///               carriageways either side of a median. No power lines.
    ///   MAIN     -- the original network: one node per 1536 m cell, links east and south, ~4 in 5 kept. Highway_0,
    ///               power lines down one side.
    ///   SMALL / TRAIL -- branches off a main road: a wandering polyline leaving it at an angle, ending where the land
    ///               says stop (water, a climb too steep, high ground) or at its length.
    /// </summary>
    public sealed partial class InfiniteRoads
    {
        // ---- specs. Widths are PEI's Roads.dat x RoadField.WidthScale 1.15 (Highway_1 6, Highway_0 8, White/Yellow 4, Trail 4).
        public const float HighwayLaneHalf = 6.9f;        // one Highway_1 carriageway
        public const float HighwayMedian = 4f;            // grass between the two carriageways
        public const float HighwayRibbonOffset = HighwayMedian * 0.5f + HighwayLaneHalf;   // each carriageway's centre from the route's
        public static float PavedHalf(RoadKind k) => k switch
        {
            RoadKind.Highway => HighwayMedian * 0.5f + 2f * HighwayLaneHalf,   // the whole corridor, median included
            RoadKind.Main => 9.2f,
            RoadKind.Rail => RailFormationHalf,
            _ => 4.6f,
        };
        /// <summary>The carve's blend from the road's level back to the land. A rail's is per point (Line.Shoulders: its
        /// cuttings and embankments are deeper than any road's), this being the least of it.</summary>
        public static float Shoulder(RoadKind k) => k switch { RoadKind.Highway => 14f, RoadKind.Main => 10f, RoadKind.Small => 6f, RoadKind.Rail => 10f, _ => 4f };
        public static float MaxGrade(RoadKind k) => k switch { RoadKind.Highway => 0.07f, RoadKind.Main => 0.16f, RoadKind.Small => 0.14f, RoadKind.Rail => RailGrade, _ => 0.22f };
        public const float Bed = 0.12f;   // the ground under a paved surface sits this far below it

        /// <summary>The road slab's thickness, RoadField's halfVerticalSize: PEI's own Roads.dat depth x DepthScale 1.1
        /// (strawberry 2026-10-09: "the proper thickness (vertical height)"). Highway_0/1 and Road_8 are depth 0.2 ->
        /// 0.22 m, Trail 0.3 -> 0.33 m. As in RoadField, each edge is a bevel running out AND down twice this from the
        /// top, so its foot is buried.</summary>
        public static float Thickness(RoadKind k) => k == RoadKind.Trail ? 0.33f : 0.22f;
        /// <summary>How far OUT a slab's edge bevel (and a line end's ramp) runs while it drops its 2 x Thickness: three
        /// times the drop, a 1:3 slope (strawberry 2026-10-10: "make the ramps (at the sides of each road spline) a lot
        /// more gentle", then of 1:5: "a bit less ramp at the edges"). RoadField's is 1:1, 45 degrees -- a kerb a car
        /// bumps off.</summary>
        public const float BevelSlope = 3f;
        public static float BevelRun(RoadKind k) => BevelSlope * 2f * Thickness(k);
        /// <summary>How much wider each side a highway carriageway's deck roadway is than its ribbon (8.0 vs 6.9 m), and
        /// how far before a deck end the ribbon starts widening to it.</summary>
        public const float MouthWiden = DeckRoadwayHalf - HighwayLaneHalf;
        public const float MouthTaper = 40f;
        /// <summary>How far every road's top stands over the ground it is carved into: the paved classes' Thickness.
        /// ⚠ ONE value for all classes, trails included, because a trail or small road STARTS on its main road at the
        /// main's profile height -- given its own 0.33 it would stand 9 cm proud of the main's surface over the overlap,
        /// a ridge across the asphalt. The order between classes is Lift's job alone.</summary>
        public const float Proud = 0.22f;
        /// <summary>Per-class lift so no two surfaces that overlap z-fight: highways over the mains they cross, mains
        /// over the small roads and trails that start under their edges.</summary>
        public static float Lift(RoadKind k) => k switch { RoadKind.Highway => 0.045f, RoadKind.Main => 0.03f, RoadKind.Small => 0.015f, RoadKind.Rail => 0f, _ => 0.01f };
        /// <summary>The DRIVEN surface of a road whose profile is at `profileH`: the ground under it (profile - Bed),
        /// plus the slab, plus the lift. Ribbons, their colliders AND bridge decks all stand on this one number, which
        /// is what makes a deck meet its approach flush. A rail's is the top of its rails (RailHeadY).</summary>
        public static float SurfaceY(RoadKind k, float profileH) => k == RoadKind.Rail ? RailHeadY(profileH) : profileH - Bed + Proud + Lift(k);

        public const double MainCell = 1536.0;
        public const double HighwayBand = 12000.0, HighwaySeg = 6000.0;
        const int MainCtrl = 24, Sub = 4;
        const int HwStations = 40, HwLanes = 41;   // 41 lanes at 60 m: the route may swing 1.2 km either way round an obstacle
        const double BridgeCost = 2500.0;          // per wet station: a long detour is cheaper, a short crossing is not
        const int MaxBridgeStations = 10;          // 1.5 km: past that it is a sea, and the highway ends at the shore
        public const float DeckAboveWater = 3f;
        const double HwStep = HighwaySeg / HwStations, HwLaneStep = 60.0;

        readonly InfiniteTerrain _t;
        readonly ulong _s;

        public sealed class Line
        {
            public RoadKind Kind;
            public double[] X, Z; public float[] H;
            public double MinX, MaxX, MinZ, MaxZ;
            // per-chunk boxes (8 segments each) so a distance query skips most of a long line cheaply
            public double[] CMinX, CMaxX, CMinZ, CMaxZ;
            public List<Line> Branches;   // main roads only
            /// <summary>Highways: where the road stands clear of the natural ground across its whole width -- the
            /// places a spline bridge will replace the embankment the carve builds today. Null on other classes.</summary>
            public List<Stretch> Raised;
            /// <summary>Highways: where the road runs deep below the natural ground across its whole width -- the
            /// carve has dug a trench through a hill, and a tunnel is the candidate.</summary>
            public List<Stretch> Cut;
            public bool[][] RaisedSeg, CutSeg;   // [carriageway: 0 = -offset side, 1 = +offset side][segment] (what PiecesIn copies onto pieces)
            public List<BridgePiece> BridgePieces;   // the bridges over this line's Raised stretches, walked once with the line
            /// <summary>Per carriageway [0 = -offset, 1 = +offset], the spans its bridge decks ACTUALLY cover, as
            /// fractional dense-point indices (k + t). The ribbon is cut and the carve suspended over exactly these --
            /// not over whole raised SEGMENTS, which left up to a deck's length at each bridge end with neither road nor
            /// deck (strawberry: "make sure they are aligned properly and theres no big gap").</summary>
            public List<DeckSpan>[] DeckCover;
            /// <summary>Highways: the tunnels bored along this line, facade to facade (null where none).</summary>
            public List<TunnelSpan> TunnelSpans;
            /// <summary>Highways: a stable name (axis, band, segment, shore-spur index) -- how a main road's underpass says
            /// which highway it passes under without holding the cached object, which a cache trim replaces.</summary>
            public long Id;
            /// <summary>Mains: where this main dips under a highway, and the highway decks it carries over itself.</summary>
            public List<Underpass> Underpasses;
            /// <summary>Mains: the on/off ramps between this main and the highway it crosses (BuildRamps).</summary>
            public List<Line> Ramps;
            public bool Ramp;   // this line IS a ramp: small-road surface, no power line
            public double[] HArc;   // horizontal arc length at each dense point (set where there are tunnels)
            /// <summary>Rails: the carve's shoulder at each dense point (it widens with the cutting's or embankment's
            /// depth); null on roads, which take Shoulder(Kind).</summary>
            public float[] Shoulders;
            /// <summary>Rails: where a main road may cross on the level (InfiniteRails.Sites).</summary>
            public List<RailSite> Sites;
            /// <summary>Rails: the track as laid -- New_Rail_Units and any terminal sleeper (built on first ask).</summary>
            public List<RailPiece> Track;
            /// <summary>Mains: where this main crosses a railway on the level.</summary>
            public List<LevelCrossing> LevelCrossings;
            /// <summary>Rails: the crossings stage 1 shaped (InfiniteRails.ShapeRail), for stage 2 to keep.</summary>
            internal List<RailCross> Plan;
            public bool Exists => X != null;
            public int Segments => X.Length - 1;
        }
        static readonly Line None = new Line();

        /// <summary>A stretch of ONE carriageway where the carve has moved a lot of ground (strawberry: "both lanes may
        /// candidate separately" -- on a side slope one carriageway can stand on fill while the other sits in a cut).
        /// RAISED (bridge candidate): the carriageway is at least RaiseFill above the natural terrain at its centre and
        /// both its edges, or any of them is over water, for at least RaiseMinLength. CUT (tunnel candidate): at least
        /// CutDepth BELOW it at all three, for at least CutMinLength. Indices are into the line's dense X/Z/H.</summary>
        public struct Stretch
        {
            public int Side;            // which carriageway: -1 = the -HighwayRibbonOffset one, +1 = the + one
            public int I0, I1;          // first and last dense point
            public float Length;        // metres of road
            public float Max;           // the tallest the embankment / deepest the cut gets (m, across the whole width)
            public bool OverWater;      // raised only: some of it crosses water, a bridge proper rather than a viaduct
        }
        // strawberry 2026-10-09: 4 m / 8 m across the whole width, then "make it more sensitive than it was before" --
        // so below both, per carriageway. UG_INF_RAISE / UG_INF_CUT override them, for tuning.
        public static readonly float RaiseFill = EnvF("UG_INF_RAISE", 3f);   // m of fill under the whole carriageway
        public const float RaiseMinLength = 30f;    // shorter than this is a bump, not a bridge
        public const float RaiseMergeGap = 40f;     // two stretches closer than this are one bridge
        public static readonly float CutDepth = EnvF("UG_INF_CUT", 6f);      // m of cut under the whole carriageway
        public const float CutMinLength = 40f;      // a tunnel shorter than this is a culvert
        public const float CutMergeGap = 40f;
        // ---- BRIDGES over the raised stretches (strawberry 2026-10-09: "implementing the bridges"). The kit is cow tools'
        // Bridge_Line_1 cut (EditorBridgeSpline): a 7.8349 m deck unit, a pier pair, an end cap. These numbers are
        // THAT tool's, copied because core cannot see the game; L1 world.inf_bridge_kit_matches asserts they agree.
        /// <summary>UG_INF_BRIDGES=0: no bridges -- raised stretches stay embankments under the road ribbon, as before.</summary>
        public static bool Bridges = Environment.GetEnvironmentVariable("UG_INF_BRIDGES") != "0";
        public const float BridgePitch = 7.8349f;        // one lane-dash period of the deck's own paint
        public const float BridgeHalfWidth = 8.5f;       // the deck's half-width: sets how much a joint closes on a bend
        /// <summary>Half the deck's ROADWAY, between the parapets' inner feet (the cut unit's local X +-8.0 at Z 0).</summary>
        public const float DeckRoadwayHalf = 8.0f;
        /// <summary>How much a deck is widened across for the road it carries: 1 for a highway carriageway (13.8 m on the
        /// 16 m roadway, a 1.1 m shoulder each side); a main's 18.4 m of asphalt needs the roadway at its full width.</summary>
        public static float DeckScale(RoadKind k) => k == RoadKind.Highway ? 1f : k == RoadKind.Rail ? RailDeckHalf / DeckRoadwayHalf : PavedHalf(k) / DeckRoadwayHalf;
        public const float DeckSoffit = -4.00f, DeckParapetTop = 1.25f;
        public const float PierTop = -2.98f, PierBottom = -52.98f;
        public const float PierSpan = PierTop - PierBottom;
        public const float MinPierDrop = DeckParapetTop - DeckSoffit;   // the deck's own thickness
        public const int PierEveryUnits = 6;             // retail's one pair per 48 m span

        // ---- TUNNELS (strawberry 2026-10-09: "next is wiring up tunnels to use the tool nyatools made"). cow tools'
        // TunnelMesh: Tunnel_Line_0's section SWEPT along the road. ONE TUBE PER CARRIAGEWAY (strawberry, on the first
        // version's single bore over both: "really stretched and goofy looking... do the separate carriageways as
        // separate tunnels"), each widened across by the tool's own rule (carriageway half + 1.5 m border over the
        // authored 8 m bore half = x1.05). The two bores stand 1 m apart -- but each SHELL is 12.6 m from its centre and
        // the carriageways only 17.8 m apart, so a shell would cut straight through the other tube's inner lane: the
        // tubes are swept from the BORE alone (the hill is their outside), which is also why the Tunnel_Line_Cap_0 prop
        // (bore + shell + facade) is not used; each mouth gets one twin-arch headwall instead. The section numbers are
        // the prop's own, copied because core cannot see the game; L1 asserts they match TunnelMesh.ProfileFrom.
        //   A tunnel is bored only where the hill ALREADY buries the whole widened shell (plus TunnelCover) for at least
        // two portals' length; strawberry agreed the shallow cuts stay open rather than squash a 17.4 m section into a
        // 6 m cut (and cow tools: berming one would be "not a berm, a new hill"). Measured over the test window: 38 such
        // runs, 48-332 m.
        /// <summary>UG_INF_TUNNELS=0: no tunnels -- deep cuts stay open trenches, as before.</summary>
        public static bool Tunnels = Environment.GetEnvironmentVariable("UG_INF_TUNNELS") != "0";
        public const float TunnelBoreHalf = 8f, TunnelShellHalf = 12f;   // Tunnel_Line_0, X at the bore and the shell
        public const float TunnelBoreTop = 12.4f;                        // the bore's crown over the road
        public const float TunnelFloorDrop = 1f;                         // the section's feet, under the road surface
        public const float TunnelSectionLength = 24f;                    // one portal's run
        public const float TunnelBorder = 1.5f;                          // EditorTunnelSpline.BoreBorder
        public const float TunnelStep = 2f;                              // EditorTunnelSpline.Step: ring spacing
        /// <summary>The section widened to carry one carriageway plus the border.</summary>
        public static float TunnelLateral => Math.Max(1f, (HighwayLaneHalf + TunnelBorder) / TunnelBoreHalf);
        /// <summary>From the route's centreline: the outer bore walls, and the twin shells' outer edges (the footprint
        /// the hill must bury).</summary>
        public static float TunnelBoreReach => HighwayRibbonOffset + TunnelBoreHalf * TunnelLateral;
        public static float TunnelShellReach => HighwayRibbonOffset + TunnelShellHalf * TunnelLateral;
        /// <summary>The higher of the two tubes' shell surfaces over the road at lateral offset `lat` from the route's
        /// centreline (negative infinity outside both) -- what the hill has to cover.</summary>
        public static float TwinShellTop(float lat)
        {
            float best = float.NegativeInfinity, L = TunnelLateral;
            for (int s = -1; s <= 1; s += 2)
            {
                // ⚠ a hair of slack: at exactly the reach, (reach - offset) / L comes out 12.00001, "outside both", and the
                // -Infinity that returned went into the ground as a HEIGHT -- which took out every collider it touched
                float x = (lat - s * HighwayRibbonOffset) / L;
                if (Math.Abs(x) <= TunnelShellHalf + 1e-3f) best = Math.Max(best, ShellTop(Math.Min(Math.Abs(x), TunnelShellHalf)));
            }
            return best;
        }
        /// <summary>The bore's inner surface over the road at lateral `lat`, inside whichever tube holds it; NaN in
        /// neither (between the tubes, or outside them).</summary>
        public static float TwinBoreTop(float lat)
        {
            float L = TunnelLateral;
            for (int s = -1; s <= 1; s += 2)
            {
                float x = (lat - s * HighwayRibbonOffset) / L;
                if (Math.Abs(x) <= TunnelBoreHalf) return BoreTop(x);
            }
            return float.NaN;
        }
        // the shell's outer arch over the road, from the prop: (X, height) along its upper chain
        static readonly float[] ShellX = { 0f, 6f, 10.45f, 12f }, ShellY = { 16.4f, 14.53f, 10.13f, 4.4f };
        /// <summary>Height of the shell's outer surface above the road at authored half-width x (0..12).</summary>
        public static float ShellTop(float x)
        {
            x = Math.Abs(x);
            for (int i = 0; i < 3; i++)
                if (x <= ShellX[i + 1]) return ShellY[i] + (ShellY[i + 1] - ShellY[i]) * (x - ShellX[i]) / (ShellX[i + 1] - ShellX[i]);
            return ShellY[3];
        }
        static readonly float[] BoreX = { 0f, 4f, 6.93f, 8f }, BoreY = { 12.4f, 11.33f, 8.4f, 4.4f };
        /// <summary>Height of the bore's inner surface (the ceiling and, below 4.4 m, the wall) above the road at authored
        /// half-width x (0..8) -- the space nothing else may enter.</summary>
        public static float BoreTop(float x)
        {
            x = Math.Abs(x);
            for (int i = 0; i < 3; i++)
                if (x <= BoreX[i + 1]) return BoreY[i] + (BoreY[i + 1] - BoreY[i]) * (x - BoreX[i]) / (BoreX[i + 1] - BoreX[i]);
            return BoreY[3];
        }
        // ---- per class. A RAILWAY's tunnel is ONE bore on the track's centreline, the same Tunnel_Line_0 section scaled
        // to it: RailTunnelLateral across (4.8 m to the walls; the train is 1.7 m to its side) and RailTunnelVertical up
        // (the crown 8.1 m over the track's root, where the highway's 12.4 m would be a cathedral over a 4 m train).
        public const float RailTunnelLateral = 0.6f, RailTunnelVertical = 0.65f;
        public static int TunnelTubes(RoadKind k) => k == RoadKind.Rail ? 1 : 2;
        /// <summary>Tube i's centreline offset from the route's (highway: the two carriageways; rail: the track).</summary>
        public static float TubeOffset(RoadKind k, int i) => k == RoadKind.Rail ? 0f : (i == 0 ? -1 : 1) * HighwayRibbonOffset;
        public static float TunnelLateralOf(RoadKind k) => k == RoadKind.Rail ? RailTunnelLateral : TunnelLateral;
        public static float TunnelVerticalOf(RoadKind k) => k == RoadKind.Rail ? RailTunnelVertical : 1f;
        public static float BoreReachOf(RoadKind k) => k == RoadKind.Rail ? TunnelBoreHalf * RailTunnelLateral : TunnelBoreReach;
        public static float ShellReachOf(RoadKind k) => k == RoadKind.Rail ? TunnelShellHalf * RailTunnelLateral : TunnelShellReach;
        public static float TunnelFloorDropOf(RoadKind k) => TunnelFloorDrop * TunnelVerticalOf(k);
        /// <summary>The tunnel's datum over its line's profile: the highway's driven surface, the track's root.</summary>
        public static float TunnelSurfaceY(RoadKind k, float profileH) => k == RoadKind.Rail ? RailOriginY(profileH) : SurfaceY(RoadKind.Highway, profileH);
        /// <summary>From that datum down to the bed (the floor): the slab and its lift, or the track's set.</summary>
        public static float TunnelBedBelow(RoadKind k) => k == RoadKind.Rail ? RailSet : Proud + Lift(RoadKind.Highway);
        /// <summary>The shells' top over the datum at lateral `lat` (negative infinity outside them).</summary>
        public static float ShellTopOf(RoadKind k, float lat)
        {
            if (k != RoadKind.Rail) return TwinShellTop(lat);
            float x = Math.Abs(lat) / RailTunnelLateral;
            return x <= TunnelShellHalf + 1e-3f ? ShellTop(Math.Min(x, TunnelShellHalf)) * RailTunnelVertical : float.NegativeInfinity;
        }
        /// <summary>The bore's inner surface over the datum at lateral `lat`; NaN outside every bore.</summary>
        public static float BoreTopOf(RoadKind k, float lat)
        {
            if (k != RoadKind.Rail) return TwinBoreTop(lat);
            float x = lat / RailTunnelLateral;
            return Math.Abs(x) <= TunnelBoreHalf ? BoreTop(x) * RailTunnelVertical : float.NaN;
        }
        // ---- THE COLLAR behind each portal (RegionStreamer.BuildTunnel): a lid over the ground the mouth's holes take out,
        // CollarDepth in from the facade and TunnelHoleExtentOf to either side, on a CollarStep grid. Its points here, so
        // the generator can find the ground under each (InfiniteTerrain.CollarGround) and the game lay the lid on it.
        public const float CollarDepth = TunnelHoleIn + 4.5f, CollarStep = 1f;
        /// <summary>How far from the route the ground can be missing at a mouth: the hole vertices' reach plus the LOD0
        /// cell every one of them takes with it, plus a margin.</summary>
        public static float TunnelHoleExtentOf(RoadKind k) => BoreReachOf(k) + TunnelHoleBeside + InfiniteTerrain.RegionSize / InfiniteTerrain.FullCells + 0.5f;
        public static int CollarAcross(RoadKind k) => (int)Math.Ceiling(2f * TunnelHoleExtentOf(k) / CollarStep);
        public static int CollarAlong => (int)Math.Ceiling(CollarDepth / CollarStep);
        /// <summary>Collar grid point (ia along, from the facade inward; iu across) of the tunnel's end 0 (near) or 1 (far):
        /// in from the facade along the route's first (last) step, across by its left-hand normal at the near end and its
        /// right-hand one at the far end, so +across is the same side of the route at both.</summary>
        public static void CollarPoint(TunnelSpan t, int end, int ia, int iu, out double x, out double z, out float aIn, out float across)
        {
            int m = t.X.Length, a = end == 0 ? 0 : m - 1, b = end == 0 ? 1 : m - 2;
            double fx = t.X[b] - t.X[a], fz = t.Z[b] - t.Z[a], fl = Math.Max(1e-9, Math.Sqrt(fx * fx + fz * fz));
            fx /= fl; fz /= fl;
            double sx = -fz, sz = fx;
            if (end == 1) { sx = -sx; sz = -sz; }
            float wing = TunnelHoleExtentOf(t.Kind);
            int nu = CollarAcross(t.Kind), na = CollarAlong;
            aIn = CollarDepth * ia / na; across = -wing + 2f * wing * iu / nu;
            x = t.X[a] + fx * aIn + sx * across; z = t.Z[a] + fz * aIn + sz * across;
        }
        public const float TunnelCover = 1f;          // natural ground over the shell for a run to be bored, not cut
        public const float TunnelProbeStep = 4f;      // along-route sampling when looking for runs to bore
        public static float TunnelMinLength => 2f * TunnelSectionLength;   // two portals; any bore is extra
        /// <summary>Ground vertices this far in behind a facade, across the bore, are holes. More than a LOD0 cell's
        /// DIAGONAL (4 m grid -> 5.66 m), so every cell the facade line crosses loses a vertex whatever the tunnel's heading.</summary>
        public const float TunnelHoleIn = 6f;
        /// <summary>How far out from a portal the approach cut widens to the bore's width.</summary>
        public const float TunnelForecourt = 20f;
        /// <summary>...and this far out beside the bore, for the same reason: a cell whose corners straddle the bore's
        /// edge on a diagonal heading still pulls the hill across the mouth's corner unless it loses one -- so the cell
        /// diagonal again, not a tuned number (3 m happens to pass the test window's probes; it is not a guarantee).</summary>
        public const float TunnelHoleBeside = 6f;
        /// <summary>The hill over a portal is cut back: no higher than the shell + TunnelCover at the facade, rising this
        /// many metres per metre behind the hole band -- so no ground hangs over the mouth or the gap behind it.</summary>
        public const float HeadwallSlope = 1.5f;
        public const float HeadwallCover = 0.35f;     // over the shell at the hole band's edge (TunnelGround keeps >= 0.25)

        static float EnvF(string name, float fallback) =>
            float.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) && v > 0f ? v : fallback;
        /// <summary>Why each highway segment that does not exist was dropped (diagnostics; a dropped segment is a dead end).</summary>
        public readonly ConcurrentDictionary<(int axis, long band, long k), string> HighwayDrops = new();
        Line Drop(int axis, long band, long k, string why) { HighwayDrops[(axis, band, k)] = why; return None; }
        /// <summary>Why a main link that the hash said should exist was not built (survey/debug aid, like HighwayDrops).</summary>
        public readonly ConcurrentDictionary<(long cx, long cz, int dir), string> MainDrops = new();
        const int Chunk = 8;

        readonly ConcurrentDictionary<(long, long, int), Line> _main = new();
        readonly ConcurrentDictionary<(int, long, long), Line> _hwy = new();
        readonly ConcurrentDictionary<(int, long, long), (double u, double w, float h, bool ok)> _anchor = new();

        internal InfiniteRoads(InfiniteTerrain t, ulong seed) { _t = t; _s = seed; }

        static float Smoothstep(float a, float b, float x) { float t = Math.Clamp((x - a) / (b - a), 0f, 1f); return t * t * (3f - 2f * t); }

        // Only ever called on a cache MISS: ConcurrentDictionary.Count takes every bucket lock, and checking it on each
        // lookup (60 per height sample) serialised all the worker threads behind it
        void Trim() { if (_main.Count > 20000) _main.Clear(); if (_hwy.Count > 4000) _hwy.Clear(); if (_anchor.Count > 8000) _anchor.Clear(); if (HighwayDrops.Count > 4000) HighwayDrops.Clear(); if (MainDrops.Count > 4000) MainDrops.Clear();
                      if (_rail.Count > 2000) _rail.Clear(); if (_railAnchor.Count > 4000) _railAnchor.Clear(); if (RailDrops.Count > 4000) RailDrops.Clear(); }

        // =============================================================================================================
        // Main roads

        void MainNode(long cx, long cz, out double x, out double z)
        {
            uint h = InfiniteTerrain.Hash(cx, cz, _s ^ 0xDDDD);
            x = (cx + 0.2 + 0.6 * ((h & 0xFFFF) / 65536.0)) * MainCell;
            z = (cz + 0.2 + 0.6 * ((h >> 16) / 65536.0)) * MainCell;
            // A TOWN IS NEVER ON A HIGHWAY: pushed straight out from any highway nearer than NodeClear, so every link has
            // room to leave it, turn, and meet a highway square-on (MainApproach) -- or to keep clear of it
            var hw = new List<Line>();
            TakeHighways(x - 2 * NodeClear, z - 2 * NodeClear, x + 2 * NodeClear, z + 2 * NodeClear, hw);
            TakeRails(x - 2 * NodeClear, z - 2 * NodeClear, x + 2 * NodeClear, z + 2 * NodeClear, hw);   // nor on a railway
            foreach (var e in hw)
            {
                var (d, px, pz, nx, nz) = NearestOn(e, x, z);
                if (d >= NodeClear) continue;
                x = px + nx * NodeClear; z = pz + nz * NodeClear;
            }
        }

        // ---- MAINS AND HIGHWAYS (strawberry 2026-10-10: "fixing up all of the road pathing ... routing roads around each
        // other/over/under each other with bridges"). A main used to cross a highway wherever its wiggle took it -- 106
        // crossings in the test window, 18 of them under 30 degrees, mains braiding back and forth across a highway, 22
        // running alongside one. Now a link that has to cross does it ONCE, square-on, and dips under: the highway carries
        // itself over the main on a short span of its own deck (the main owns those decks, so a highway never needs to
        // know about mains -- they are built from it, not the other way round). A link that need not cross keeps clear.
        public const double NodeClear = 160;       // a town's minimum distance from a highway's centreline
        public const double MainApproach = 120;    // the main runs straight and square for this far either side of a crossing
        public const double MainClear = 60;        // ...and otherwise never comes nearer a highway than this
        public const float UnderDepth = 9.5f;      // main profile under the highway's: deck 4 m + 5 m headroom + slabs
        public const float OverRise = 9.5f;        // ...or over it, the same clearance the other way up
        public const float UnderMargin = 4f;       // a deck reaches this far past the asphalt it spans on each side
        /// <summary>A main runs level this far either side of a level crossing: the rail's formation and a little.</summary>
        public const float LevelFlat = RailFormationHalf + 3f;
        /// <summary>How far along a railway a main will move its crossing to reach one of the rail's sites.</summary>
        public const double MainSiteReach = 400;
        /// <summary>Either side of a crossing the main runs LEVEL for this far along itself: the highway's whole corridor
        /// and the margin, so the clearance holds under (or over) every lane, not only at the highway's centreline.</summary>
        public const float CrossFlat = 15.8f + UnderMargin;   // PavedHalf(Highway) + UnderMargin

        /// <summary>One main road crossing one highway, grade-separated: under it (the highway's decks over the main) or,
        /// with Over, over it (the main's own decks over the highway).</summary>
        public sealed class Underpass
        {
            public bool Over;                                       // the main bridges the highway: Pieces are the MAIN's decks, Cover unused
            public long HwId;
            public double X, Z;                                     // where the centrelines cross
            public float HwSurface, MainSurface;                    // driven surfaces there
            public DeckSpan?[] Cover = new DeckSpan?[2];           // per highway carriageway [-offset, +offset], dense-index space of the highway
            public List<BridgePiece> Pieces = new();                // the highway's decks and caps over the main
            public bool Existing;                                   // the highway was already a bridge here: no decks of our own
            public bool OverTunnel;                                 // a rail over the hill a highway's tunnel bores: no decks at all
        }

        /// <summary>Nearest point on a line's centreline: distance, the point, and the unit normal from it toward (x, z).</summary>
        static (double d, double px, double pz, double nx, double nz) NearestOn(Line e, double x, double z)
        {
            double best = double.MaxValue, bx = 0, bz = 0, bnx = 0, bnz = 1;
            for (int k = 0; k < e.Segments; k++)
            {
                double sx = e.X[k + 1] - e.X[k], sz = e.Z[k + 1] - e.Z[k], ss = sx * sx + sz * sz;
                if (ss < 1e-12) continue;
                double t = Math.Clamp(((x - e.X[k]) * sx + (z - e.Z[k]) * sz) / ss, 0, 1);
                double px = e.X[k] + sx * t, pz = e.Z[k] + sz * t, d = Math.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz));
                if (d < best)
                {
                    best = d; bx = px; bz = pz;
                    double l = Math.Sqrt(ss), nx = -sz / l, nz = sx / l;
                    if ((x - px) * nx + (z - pz) * nz < 0) { nx = -nx; nz = -nz; }
                    bnx = nx; bnz = nz;
                }
            }
            return (best, bx, bz, bnx, bnz);
        }

        /// <summary>Which side of a line's centreline (x, z) lies: +1 left of its run, -1 right, 0 where the nearest point
        /// is one of its two ENDS (past an end, a side means nothing).</summary>
        static int SideOf(Line e, double x, double z)
        {
            double best = double.MaxValue; int side = 0;
            for (int k = 0; k < e.Segments; k++)
            {
                double sx = e.X[k + 1] - e.X[k], sz = e.Z[k + 1] - e.Z[k], ss = sx * sx + sz * sz;
                if (ss < 1e-12) continue;
                double tr = ((x - e.X[k]) * sx + (z - e.Z[k]) * sz) / ss, t = Math.Clamp(tr, 0, 1);
                double px = e.X[k] + sx * t, pz = e.Z[k] + sz * t, d = (x - px) * (x - px) + (z - pz) * (z - pz);
                if (d < best)
                {
                    best = d;
                    bool end = k == 0 && tr < 0 || k == e.Segments - 1 && tr > 1;
                    side = end ? 0 : sx * (z - e.Z[k]) - sz * (x - e.X[k]) >= 0 ? 1 : -1;
                }
            }
            return side;
        }

        /// <summary>A main's profile through a grade-separated crossing at arc aX: level at `target` for `flat` either
        /// side, then back toward the land at 0.92 of the grade limit -- the lower of the two under, the higher over.
        /// Both bounds are grade-limited, so the result is too. ⚠ The profile is LINEAR between its points, so a level
        /// stretch exists only between points that are all level: to hold CrossFlat, `flat` must reach the first point
        /// past it (CrossFlat + the point spacing). Shaped to CrossFlat exactly, a 15 m spacing left the main level only
        /// to 15 m, falling 8 cm by the highway's edge -- and a deck unit spanning that kink sat 61 mm off it.</summary>
        static void ShapeCrossing(float[] h, double[] arc, double aX, float target, bool over, double flat, float grade = -1f)
        {
            float g = grade > 0f ? grade : 0.92f * MaxGrade(RoadKind.Main);
            for (int i = 0; i < h.Length; i++)
            {
                float b = (float)Math.Max(0.0, Math.Abs(arc[i] - aX) - flat) * g;
                h[i] = over ? Math.Max(h[i], target - b) : Math.Min(h[i], target + b);
            }
        }

        /// <summary>The lowest and highest of a line's profile within `reach` metres along it (horizontally) of dense
        /// index f -- sampled every metre, so a window end between two points counts.</summary>
        static (float lo, float hi) ProfileRange(Line e, double f, double reach)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            int k0 = Math.Min((int)f, e.Segments - 1);
            double Len(int i) => Math.Sqrt((e.X[i + 1] - e.X[i]) * (e.X[i + 1] - e.X[i]) + (e.Z[i + 1] - e.Z[i]) * (e.Z[i + 1] - e.Z[i]));
            for (int sgn = -1; sgn <= 1; sgn += 2)
            {
                int k = k0; double t = f - k0;
                for (double walked = 0; ; walked += 1.0)
                {
                    float h = e.H[k] + (e.H[k + 1] - e.H[k]) * (float)t;
                    lo = Math.Min(lo, h); hi = Math.Max(hi, h);
                    if (walked + 1.0 > reach) break;
                    // one metre on, crossing into the next segment as needed
                    double left = 1.0;
                    while (left > 0)
                    {
                        double L = Math.Max(1e-9, Len(k)), room = sgn > 0 ? (1 - t) * L : t * L;
                        if (left <= room) { t += sgn * left / L; left = 0; }
                        else if (sgn > 0 ? k + 1 < e.Segments : k > 0) { left -= room; k += sgn; t = sgn > 0 ? 0 : 1; }
                        else { t = sgn > 0 ? 1 : 0; left = 0; }
                    }
                }
            }
            return (lo, hi);
        }

        static bool SegCross(double ax, double az, double bx, double bz, double cx, double cz, double dx, double dz, out double t, out double u)
        {
            double rx = bx - ax, rz = bz - az, sx = dx - cx, sz = dz - cz, den = rx * sz - rz * sx;
            t = u = 0;
            if (Math.Abs(den) < 1e-12) return false;
            t = ((cx - ax) * sz - (cz - az) * sx) / den; u = ((cx - ax) * rz - (cz - az) * rx) / den;
            return t >= 0 && t < 1 && u >= 0 && u < 1;
        }

        /// <summary>Every crossing of a polyline with a line's centreline: (fraction along the polyline in segment units,
        /// fraction along the line in dense-index units, point, crossing angle in degrees 0..90).</summary>
        static List<(double f, double g, double x, double z, double angle)> Crossings(double[] px, double[] pz, int count, Line e)
        {
            var r = new List<(double, double, double, double, double)>();
            for (int i = 0; i + 1 < count; i++)
            {
                double mnx = Math.Min(px[i], px[i + 1]), mxx = Math.Max(px[i], px[i + 1]), mnz = Math.Min(pz[i], pz[i + 1]), mxz = Math.Max(pz[i], pz[i + 1]);
                if (mxx < e.MinX || mnx > e.MaxX || mxz < e.MinZ || mnz > e.MaxZ) continue;
                for (int k = 0; k < e.Segments; k++)
                {
                    if (!SegCross(px[i], pz[i], px[i + 1], pz[i + 1], e.X[k], e.Z[k], e.X[k + 1], e.Z[k + 1], out double t, out double u)) continue;
                    double ax = px[i + 1] - px[i], az = pz[i + 1] - pz[i], bx = e.X[k + 1] - e.X[k], bz = e.Z[k + 1] - e.Z[k];
                    double ang = Math.Abs(Math.Atan2(ax * bz - az * bx, ax * bx + az * bz)) * 180 / Math.PI;
                    if (ang > 90) ang = 180 - ang;
                    r.Add((i + t, k + u, px[i] + ax * t, pz[i] + az * t, ang));
                }
            }
            return r;
        }

        internal Line Main(long cx, long cz, int dir)
        {
            return _main.TryGetValue((cx, cz, dir), out var hit) ? hit
                 : _main.GetOrAdd((cx, cz, dir), k => { Trim(); return BuildMain(k.Item1, k.Item2, k.Item3); });
        }

        Line BuildMain(long cx, long cz, int dir)
        {
            if (InfiniteTerrain.Hash(cx, cz, _s ^ 0xDDDD ^ (ulong)(dir + 7)) % 100 >= 78) return None;   // ~4 in 5 links exist
            Line Gone(string why) { MainDrops[(cx, cz, dir)] = why; return None; }
            MainNode(cx, cz, out double ax, out double az);
            MainNode(cx + (dir == 0 ? 1 : 0), cz + (dir == 1 ? 1 : 0), out double bx, out double bz);
            double dx = bx - ax, dz = bz - az, len = Math.Sqrt(dx * dx + dz * dz);
            ulong ws = InfiniteTerrain.Mix(_s ^ 0xDDDD ^ (ulong)cx * 31UL ^ (ulong)cz * 1031UL ^ (ulong)dir);
            var cxs = new double[MainCtrl + 1]; var czs = new double[MainCtrl + 1]; var ch = new float[MainCtrl + 1];

            // the highways this link could meet, and where the straight line between its towns crosses them. An ODD count
            // means the towns are on opposite sides and the link must cross -- once. An EVEN count (a highway bowing out
            // between two towns on the same side) means it need not: it keeps to the towns' side, round the bow.
            // RAILWAYS are in this list too: a main treats one exactly as it treats a highway -- once, square, or not at
            // all -- except that it crosses on the level, at one of the rail's own Sites (InfiniteRails)
            var hws = new List<Line>();
            TakeHighways(Math.Min(ax, bx) - 400, Math.Min(az, bz) - 400, Math.Max(ax, bx) + 400, Math.Max(az, bz) + 400, hws);
            TakeRails(Math.Min(ax, bx) - 400, Math.Min(az, bz) - 400, Math.Max(ax, bx) + 400, Math.Max(az, bz) + 400, hws);
            var straight = new[] { ax, bx }; var straightZ = new[] { az, bz };
            Line hwX = null; double hwF = 0, xX = 0, zX = 0;
            foreach (var e in hws)
            {
                var cs = Crossings(straight, straightZ, 2, e);
                if (cs.Count % 2 == 0) continue;
                if (cs.Count > 1) return Gone(e.Kind == RoadKind.Rail ? "weaves across a railway" : "weaves across a highway");
                if (hwX != null) return Gone(hwX.Kind == RoadKind.Rail || e.Kind == RoadKind.Rail ? "crosses a highway and a railway" : "crosses two highways");   // (there are four more links round each town)
                hwX = e; hwF = cs[0].g; xX = cs[0].x; zX = cs[0].z;
            }
            bool railX = false;   // a LEVEL crossing
            RailSite site = default;
            if (hwX != null && hwX.Kind == RoadKind.Rail)
            {
                // the rail is crossed where IT is level and at the land's height: the nearest of its sites, within reach.
                // With none, the main crosses it as it would a highway, grade-separated: over it on the main's own deck,
                // or under a viaduct the rail already stands on
                var arcR = Arc(hwX);
                int gk = Math.Min((int)hwF, hwX.Segments - 1);
                double aR = arcR[gk] + (arcR[gk + 1] - arcR[gk]) * (hwF - gk), bd = double.MaxValue;
                if (hwX.Sites != null) foreach (var st in hwX.Sites) if (Math.Abs(st.A - aR) < bd) { bd = Math.Abs(st.A - aR); site = st; }
                if (bd <= MainSiteReach) { railX = true; hwF = site.F; xX = site.X; zX = site.Z; }
            }

            // a wandering stretch from (x0,z0) to (x1,z1) into control points [k0, k0 + count]
            void Wander(int k0, int count, double x0, double z0, double x1, double z1, double amp, double phase)
            {
                double sx = x1 - x0, sz = z1 - z0, sl = Math.Max(1e-6, Math.Sqrt(sx * sx + sz * sz)), qx = -sz / sl, qz = sx / sl;
                for (int k = 0; k <= count; k++)
                {
                    double t = (double)k / count;
                    double w = amp * 4.0 * t * (1.0 - t) * InfiniteTerrain.Gradient(phase + t * 2.7 * sl / len, 0.5, ws);   // zero at both ends
                    cxs[k0 + k] = x0 + sx * t + qx * w; czs[k0 + k] = z0 + sz * t + qz * w;
                }
            }
            int keepA = -1, keepB = -1, kX = -1;   // the square crossing section, [keepA, keepB], centred on kX
            if (hwX == null) Wander(0, MainCtrl, ax, az, bx, bz, 160.0, 0.0);
            else
            {
                // SQUARE ON: from the crossing, MainApproach out along the highway's normal on each side, then on to the towns
                int jk = Math.Min((int)hwF, hwX.Segments - 1);
                double hx = hwX.X[jk + 1] - hwX.X[jk], hz = hwX.Z[jk + 1] - hwX.Z[jk], hl = Math.Sqrt(hx * hx + hz * hz);
                double nx = -hz / hl, nz = hx / hl;
                if (nx * dx + nz * dz < 0) { nx = -nx; nz = -nz; }
                double pax = xX - nx * MainApproach, paz = zX - nz * MainApproach, pbx = xX + nx * MainApproach, pbz = zX + nz * MainApproach;
                double l1 = Math.Sqrt((pax - ax) * (pax - ax) + (paz - az) * (paz - az)), l3 = Math.Sqrt((bx - pbx) * (bx - pbx) + (bz - pbz) * (bz - pbz));
                if (l1 < 40 || l3 < 40) return Gone("town too near the crossing");
                const int mid = 4;
                int n1 = Math.Clamp((int)Math.Round((MainCtrl - mid) * l1 / (l1 + l3)), 2, MainCtrl - mid - 2), n3 = MainCtrl - mid - n1;
                Wander(0, n1, ax, az, pax, paz, 100.0 * Math.Min(1.0, l1 / 600.0), 0.0);
                for (int q = 0; q <= mid; q++) { double t = (double)q / mid; cxs[n1 + q] = pax + (pbx - pax) * t; czs[n1 + q] = paz + (pbz - paz) * t; }
                Wander(n1 + mid, n3, pbx, pbz, bx, bz, 100.0 * Math.Min(1.0, l3 / 600.0), 1.3);
                keepA = n1; keepB = n1 + mid; kX = n1 + mid / 2;
            }
            // ON ITS TOWNS' SIDE of every highway: a control point the wander -- or the straight line, round a bow -- put
            // across one is mirrored back over it, at least MainClear off. The crossed highway's sides switch at the
            // square section: A's side before it, B's after.
            foreach (var e in hws)
            {
                int sA = SideOf(e, ax, az), sB = SideOf(e, bx, bz);
                for (int k = 1; k < MainCtrl; k++)
                {
                    if (k >= keepA && k <= keepB) continue;
                    int want = e == hwX && k > keepB ? sB : sA;
                    if (want == 0 || SideOf(e, cxs[k], czs[k]) != -want) continue;
                    var (d, px, pz, nx, nz) = NearestOn(e, cxs[k], czs[k]);
                    double r = Math.Max(d, MainClear);
                    cxs[k] = px - nx * r; czs[k] = pz - nz * r;
                }
            }
            // CLEAR OF EVERY HIGHWAY everywhere else: a point nearer than MainClear is pushed straight out to it
            for (int k = 0; k <= MainCtrl; k++)
            {
                if (k >= keepA && k <= keepB) continue;
                foreach (var e in hws)
                {
                    var (d, px, pz, nx, nz) = NearestOn(e, cxs[k], czs[k]);
                    if (d < MainClear) { cxs[k] = px + nx * MainClear; czs[k] = pz + nz * MainClear; }
                }
            }
            for (int k = 0; k <= MainCtrl; k++)
            {
                float h = _t.RawHeight(cxs[k], czs[k]);
                if (h < InfiniteTerrain.SeaLevel + 1.5f || h > 135f) return Gone(h > 135f ? "pass" : "sea");   // no bridges, no passes
                ch[k] = h;
            }
            Smooth(ch, 4);
            // GRADE-SEPARATED: through the crossing the main runs level for CrossFlat either side, UnderDepth below the
            // highway's profile (UNDER: the highway decks over it) or OverRise above it (OVER: the main decks over the
            // highway), and leaves that level at just under its grade limit -- min() (under) or max() (over) with the
            // land, so where the land already does it nothing changes. Over where the land at the crossing stands above
            // the highway (the highway is in a cut: the main stays up and spans it) or where going under would take the
            // main below the sea; under otherwise. Neither where the highway is in a tunnel (the main goes over the hill),
            // and under one of the highway's own bridges the dip clears that deck and lays none of its own.
            Underpass up = null;
            float target = 0f; bool over = false;
            // A LEVEL CROSSING: the main comes to the rails' top (RailProud under it) and runs level over the formation
            float level = site.H + (RailHeadY(0f) - SurfaceY(RoadKind.Main, 0f)) - RailProud;
            if (railX)
            {
                var arcC = new double[MainCtrl + 1];
                for (int k = 1; k <= MainCtrl; k++) arcC[k] = arcC[k - 1] + Math.Sqrt((cxs[k] - cxs[k - 1]) * (cxs[k] - cxs[k - 1]) + (czs[k] - czs[k - 1]) * (czs[k] - czs[k - 1]));
                ShapeCrossing(ch, arcC, arcC[kX], level, true, LevelFlat);
                ShapeCrossing(ch, arcC, arcC[kX], level, false, LevelFlat);
            }
            else if (hwX != null)
            {
                int jk = Math.Min((int)hwF, hwX.Segments - 1);
                float hwH = hwX.H[jk] + (hwX.H[jk + 1] - hwX.H[jk]) * (float)(hwF - jk);
                bool tunnel = false, bridged = false;
                if (Tunnels && hwX.TunnelSpans != null)
                {
                    double along = hwX.HArc[jk] + (hwX.HArc[jk + 1] - hwX.HArc[jk]) * (hwF - jk);
                    foreach (var tn in hwX.TunnelSpans) if (along >= tn.A0 - 10 && along <= tn.A1 + 10) tunnel = true;
                }
                // ON ONE OF THE HIGHWAY'S OWN BRIDGES only if BOTH carriageways are decked over the whole of the main's
                // asphalt and margin: a raised stretch is per carriageway, and a main dipped under a bridge END, or under
                // the carriageway still on its embankment, is buried in that embankment
                int partial = 0;
                if (Bridges && hwX.DeckCover != null)
                {
                    double reach = (PavedHalf(RoadKind.Main) + UnderMargin) / Math.Max(1e-6, Math.Sqrt((hwX.X[jk + 1] - hwX.X[jk]) * (hwX.X[jk + 1] - hwX.X[jk]) + (hwX.Z[jk + 1] - hwX.Z[jk]) * (hwX.Z[jk + 1] - hwX.Z[jk])));
                    for (int s = 0; s < 2; s++)
                        for (int q = -4; q <= 4; q++) if (Covered(hwX.DeckCover[s], hwF + reach * q / 4.0)) partial++;
                }
                if (partial == 18) bridged = true;
                else if (partial > 0) return Gone("crosses at a highway bridge's end");
                if (!tunnel)
                {
                    var arcC = new double[MainCtrl + 1];
                    for (int k = 1; k <= MainCtrl; k++) arcC[k] = arcC[k - 1] + Math.Sqrt((cxs[k] - cxs[k - 1]) * (cxs[k] - cxs[k - 1]) + (czs[k] - czs[k - 1]) * (czs[k] - czs[k - 1]));
                    var under = (float[])ch.Clone();
                    // the clearance holds over the highway's profile across the whole width of the main, not only at its
                    // centreline: a 7% highway rises 0.64 m over the main's half-width
                    var (hwLo, hwHi) = ProfileRange(hwX, hwF, (PavedHalf(RoadKind.Main) + UnderMargin) / Math.Sin(60 * Math.PI / 180));
                    ShapeCrossing(under, arcC, arcC[kX], hwLo - UnderDepth, false, CrossFlat);
                    bool floods = false;
                    foreach (var h in under) if (h < InfiniteTerrain.SeaLevel + 1.2f) floods = true;
                    over = !bridged && (hwX.Kind == RoadKind.Rail || floods || ch[kX] > hwH);   // a rail has no decks to spare
                    if (floods && !over) return Gone("underpass below sea");   // under a highway bridge, by the sea
                    if (over) { target = hwHi + OverRise; ShapeCrossing(ch, arcC, arcC[kX], target, true, CrossFlat); }
                    else { target = hwLo - UnderDepth; Array.Copy(under, ch, ch.Length); }
                    up = new Underpass { HwId = hwX.Id, X = xX, Z = zX, Over = over, Existing = bridged,
                                         HwSurface = SurfaceY(RoadKind.Highway, hwH), MainSurface = SurfaceY(RoadKind.Main, target) };
                }
            }
            for (int k = 0; k < MainCtrl; k++)
            {
                double sl = Math.Sqrt((cxs[k + 1] - cxs[k]) * (cxs[k + 1] - cxs[k]) + (czs[k + 1] - czs[k]) * (czs[k + 1] - czs[k]));
                if (Math.Abs(ch[k + 1] - ch[k]) / Math.Max(1e-6, sl) > MaxGrade(RoadKind.Main)) return Gone("grade");
            }
            var line = Finish(RoadKind.Main, cxs, czs, ch);
            if (line == null) return Gone("finish");
            // the drawn line must do what the controls promised: no highway crossed but the one, exactly once, square-on
            double fMain = -1, fHw = -1, sinX = 1;
            foreach (var e in hws)
            {
                var cs = Crossings(line.X, line.Z, line.X.Length, e);
                if (e == hwX ? cs.Count != 1 : cs.Count > 0) return Gone(e == hwX ? "drawn line recrosses" : "drawn line crosses another");
                foreach (var c in cs) { if (c.angle < 60) return Gone("drawn crossing skewed"); fMain = c.f; fHw = c.g; sinX = Math.Sin(c.angle * Math.PI / 180); }
            }
            if (railX)
            {
                // level in the DENSE profile too (see below), and remembered: poles stop short of it, the crossing gets
                // its signs
                var arcD = Arc(line);
                int fk = Math.Min((int)fMain, line.Segments - 1);
                double aX = arcD[fk] + (arcD[fk + 1] - arcD[fk]) * (fMain - fk), spacing = 0;
                for (int i = 0; i < line.Segments; i++)
                    if (Math.Abs(arcD[i] - aX) < LevelFlat + 60) spacing = Math.Max(spacing, arcD[i + 1] - arcD[i]);
                ShapeCrossing(line.H, arcD, aX, level, true, LevelFlat + spacing);
                ShapeCrossing(line.H, arcD, aX, level, false, LevelFlat + spacing);
                At(line, arcD, aX, out double lx, out double lz, out _, out double ltx, out double ltz);
                line.LevelCrossings = new List<LevelCrossing> { new LevelCrossing { X = lx, Z = lz, Y = RailHeadY(site.H), RX = (float)site.TX, RZ = (float)site.TZ, MX = (float)ltx, MZ = (float)ltz } };
            }
            if (up != null)
            {
                // the drawn profile is linear between control points ~60 m apart, so the level stretch exists only in the
                // DENSE profile: shape that too, or the main is level only at the highway's centreline and 2.6 m nearer
                // the deck at its edges
                var arcD = Arc(line);
                int fk = Math.Min((int)fMain, line.Segments - 1);
                double aX = arcD[fk] + (arcD[fk + 1] - arcD[fk]) * (fMain - fk), spacing = 0;
                for (int i = 0; i < line.Segments; i++)
                    if (Math.Abs(arcD[i] - aX) < CrossFlat + 60) spacing = Math.Max(spacing, arcD[i + 1] - arcD[i]);
                ShapeCrossing(line.H, arcD, aX, target, over, CrossFlat + spacing);
                if (!over) foreach (var h in line.H) if (h < InfiniteTerrain.SeaLevel + 1.2f) return Gone("underpass below sea");
                if (!up.Existing && !(over ? LayOverpass(line, fMain, sinX, up, hwX.Kind) : LayUnderpass(line, hwX, up))) return Gone(over ? "overpass decks" : "underpass decks");
                line.Underpasses = new List<Underpass> { up };
                if (hwX.Kind == RoadKind.Highway) line.Ramps = BuildRamps(line, fMain, hwX, fHw);
            }
            line.Branches = BuildBranches(line, cx, cz, dir);
            return line;
        }

        /// <summary>The highway's own decks over a main road: per carriageway, whole units centred where the main crosses
        /// it, reaching UnderMargin past the main's asphalt either side (longer for a skewed crossing), abutment caps, no
        /// piers -- a short span, and the ground under it is the main's cutting, not the land a pier is measured to.</summary>
        bool LayUnderpass(Line main, Line hw, Underpass up)
        {
            for (int s = 0; s < 2; s++)
            {
                int side = s == 0 ? -1 : 1;
                var cw = CarriagewayPts(hw, side, 0, hw.X.Length - 1);
                var cx = new double[cw.Length]; var cz = new double[cw.Length];
                for (int i = 0; i < cw.Length; i++) { cx[i] = cw[i].x; cz[i] = cw[i].z; }
                // where the main crosses THIS carriageway, in its dense-index space, and at what angle
                var cr = new List<(double, double, double, double, double)>();
                for (int i = 0; i + 1 < main.X.Length; i++)
                    for (int k = 0; k + 1 < cw.Length; k++)
                        if (SegCross(main.X[i], main.Z[i], main.X[i + 1], main.Z[i + 1], cx[k], cz[k], cx[k + 1], cz[k + 1], out double t, out double u))
                        {
                            double ax = main.X[i + 1] - main.X[i], az = main.Z[i + 1] - main.Z[i], bx = cx[k + 1] - cx[k], bz = cz[k + 1] - cz[k];
                            double ang = Math.Abs(Math.Atan2(ax * bz - az * bx, ax * bx + az * bz));
                            cr.Add((k + u, ang > Math.PI / 2 ? Math.PI - ang : ang, 0, 0, 0));
                        }
                if (cr.Count != 1) return false;
                double fc = cr[0].Item1, sin = Math.Max(0.5, Math.Sin(cr[0].Item2));
                double need = 2.0 * (PavedHalf(main.Kind) + UnderMargin) / sin;
                double half = Math.Ceiling(need / BridgePitch) * BridgePitch * 0.5;
                int i0 = Math.Max(0, (int)Math.Floor(fc) - 2), i1 = Math.Min(hw.X.Length - 1, (int)Math.Ceiling(fc) + 2);
                var span = WalkDecks(hw, side, i0, i1, fc, half, false, up.Pieces);
                if (span == null) return false;
                up.Cover[s] = span;
            }
            return true;
        }

        /// <summary>The main's own decks over a highway: whole units centred where it crosses, reaching UnderMargin past
        /// the highway's asphalt either side (longer for a skewed crossing), widened to the main (DeckScale), no piers --
        /// the gap is the highway's. The main's ribbon is cut and its embankment stopped under exactly that span.</summary>
        bool LayOverpass(Line main, double fX, double sin, Underpass up, RoadKind under = RoadKind.Highway)
        {
            double need = 2.0 * (PavedHalf(under) + UnderMargin) / Math.Max(0.5, sin);
            double half = Math.Ceiling(need / BridgePitch) * BridgePitch * 0.5;
            int reach = main.Kind == RoadKind.Rail ? (int)Math.Ceiling(half / 8.0) + 2 : 4;   // a rail's dense points are closer
            int i0 = Math.Max(0, (int)Math.Floor(fX) - reach), i1 = Math.Min(main.X.Length - 1, (int)Math.Ceiling(fX) + reach);
            if (WalkDecks(main, 0, i0, i1, fX, half, false, up.Pieces) is not DeckSpan d) return false;
            main.DeckCover ??= new[] { new List<DeckSpan>(), new List<DeckSpan>() };   // one roadway: both "sides" of the centreline
            main.DeckCover[0].Add(d); main.DeckCover[1].Add(d);
            return true;
        }

        // ---- RAMPS (strawberry 2026-10-10: "work on a simple on/offramp for these areas"). A diamond at each grade
        // separation: per highway carriageway, one ramp each side of the main, on the carriageway's OUTER side. A ramp
        // starts INSIDE its carriageway at the carriageway's profile, its outer edge on the carriageway's -- the way a
        // branch starts on its main -- RampReach along the highway from the crossing, leaves it along the highway,
        // swings out in a gentle S and meets the main square-on, RampMeet along the main from the crossing, at the
        // main's profile there. A small road's slab drawn in road_6, two lanes with a dashed WHITE divider (strawberry:
        // "use the road 1 (white dotted) for em", "its a 2 lane white dotted one") -- RoadPiece.Ramp; no power line.
        public const double RampReach = 260;   // along the highway, from the crossing to where a ramp leaves it
        public const double RampMeet = 90;     // along the main, from the crossing to where a ramp joins it
        const int RampCtrl = 12;

        List<Line> BuildRamps(Line main, double fMain, Line hw, double fHw)
        {
            var list = new List<Line>();
            var arcM = Arc(main); var arcH = Arc(hw);
            int mk = Math.Min((int)fMain, main.Segments - 1), hk = Math.Min((int)fHw, hw.Segments - 1);
            double aM = arcM[mk] + (arcM[mk + 1] - arcM[mk]) * (fMain - mk), aH = arcH[hk] + (arcH[hk + 1] - arcH[hk]) * (fHw - hk);
            At(hw, arcH, aH, out double xX, out double zX, out _, out _, out _);
            float rampHalf = PavedHalf(RoadKind.Small);
            for (int side = -1; side <= 1; side += 2)          // the carriageway, by which side of the route it runs
                for (int way = -1; way <= 1; way += 2)         // which side of the main, along the highway
                {
                    double sH = aH + way * RampReach;
                    if (sH < 0 || sH > arcH[arcH.Length - 1]) continue;   // the highway's next segment: not this one's
                    At(hw, arcH, sH, out double hx, out double hz, out float hh, out double htx, out double htz);
                    // not where the carriageway is on a bridge or in a tunnel here
                    double fS = FracAt(arcH, sH);
                    if (Bridges && hw.DeckCover != null && Covered(hw.DeckCover[side < 0 ? 0 : 1], fS, -20.0 / Math.Max(1e-6, arcH[Math.Min(arcH.Length - 1, (int)fS + 1)] - arcH[(int)fS]))) continue;
                    if (Tunnels && hw.TunnelSpans != null && hw.HArc != null)
                    {
                        bool bored = false;
                        foreach (var tn in hw.TunnelSpans) if (sH > tn.A0 - TunnelForecourt - 30 && sH < tn.A1 + TunnelForecourt + 30) bored = true;
                        if (bored) continue;
                    }
                    double nx = -htz * side, nz = htx * side;          // out from the route, on this carriageway's side
                    double off = HighwayRibbonOffset + HighwayLaneHalf - rampHalf;
                    double sx = hx + nx * off, sz = hz + nz * off;
                    // the main's point RampMeet out on this carriageway's side of the highway
                    double best = double.NaN;
                    foreach (double sm in new[] { aM - RampMeet, aM + RampMeet })
                    {
                        if (sm < 0 || sm > arcM[arcM.Length - 1]) continue;
                        At(main, arcM, sm, out double px, out double pz, out _, out _, out _);
                        if ((px - xX) * nx + (pz - zX) * nz > 0) best = sm;
                    }
                    if (double.IsNaN(best)) continue;
                    At(main, arcM, best, out double mx, out double mz, out _, out double mtx, out double mtz);
                    // ...joined from the ramp's side of the main, just inside its asphalt
                    double qx = -mtz, qz = mtx;
                    if ((sx - mx) * qx + (sz - mz) * qz < 0) { qx = -qx; qz = -qz; }
                    double ex = mx + qx * (PavedHalf(RoadKind.Main) - 1.0), ez = mz + qz * (PavedHalf(RoadKind.Main) - 1.0);
                    float hS = hh, hE = ProfileNear(main, ex, ez);
                    // the S: Hermite from the start (heading along the highway, toward the crossing) to the end (heading
                    // into the main, square to it)
                    double t0x = -way * htx, t0z = -way * htz, t1x = -qx, t1z = -qz;
                    double chord = Math.Sqrt((ex - sx) * (ex - sx) + (ez - sz) * (ez - sz));
                    var cxs = new double[RampCtrl + 1]; var czs = new double[RampCtrl + 1]; var chs = new float[RampCtrl + 1];
                    bool wet = false;
                    for (int k = 0; k <= RampCtrl; k++)
                    {
                        double t = (double)k / RampCtrl, t2 = t * t, t3 = t2 * t;
                        double h00 = 2 * t3 - 3 * t2 + 1, h10 = t3 - 2 * t2 + t, h01 = -2 * t3 + 3 * t2, h11 = t3 - t2;
                        cxs[k] = h00 * sx + h10 * chord * t0x + h01 * ex + h11 * chord * t1x;
                        czs[k] = h00 * sz + h10 * chord * t0z + h01 * ez + h11 * chord * t1z;
                        if (_t.RawHeight(cxs[k], czs[k]) < InfiniteTerrain.SeaLevel + 1.5f) wet = true;
                    }
                    if (wet) continue;
                    // the profile: from the carriageway's to the main's, eased so it leaves and arrives level with them
                    var arcR = new double[RampCtrl + 1];
                    for (int k = 1; k <= RampCtrl; k++) arcR[k] = arcR[k - 1] + Math.Sqrt((cxs[k] - cxs[k - 1]) * (cxs[k] - cxs[k - 1]) + (czs[k] - czs[k - 1]) * (czs[k] - czs[k - 1]));
                    for (int k = 0; k <= RampCtrl; k++) { float u = (float)(arcR[k] / arcR[RampCtrl]); chs[k] = hS + (hE - hS) * u * u * (3f - 2f * u); }
                    var ramp = Finish(RoadKind.Small, cxs, czs, chs, (t0x, t0z), (t1x, t1z));
                    if (ramp == null) continue;   // too steep
                    // it meets no road on the way but the two it joins: not the main short of its end, not the highway
                    if (Crossings(ramp.X, ramp.Z, ramp.X.Length, main).Count > 0 || Crossings(ramp.X, ramp.Z, ramp.X.Length, hw).Count > 0) continue;
                    var rails = new List<Line>();
                    TakeRails(ramp.MinX, ramp.MinZ, ramp.MaxX, ramp.MaxZ, rails);
                    bool railed = false;
                    foreach (var rl in rails) if (Crossings(ramp.X, ramp.Z, ramp.X.Length, rl).Count > 0) railed = true;
                    if (railed) continue;
                    ramp.Ramp = true;
                    list.Add(ramp);
                }
            return list.Count > 0 ? list : null;
        }

        static double FracAt(double[] arc, double s)
        {
            int k = 0;
            while (k < arc.Length - 2 && arc[k + 1] < s) k++;
            return k + Math.Clamp((s - arc[k]) / Math.Max(1e-9, arc[k + 1] - arc[k]), 0.0, 1.0);
        }

        // =============================================================================================================
        // Branches: small roads and trails leaving a main road

        /// <summary>A small road or trail ends this far (from the formation's edge) short of a railway.</summary>
        public const float BranchRailClear = 30f;

        List<Line> BuildBranches(Line main, long cx, long cz, int dir)
        {
            var list = new List<Line>();
            ulong bs = InfiniteTerrain.Mix(_s ^ 0xB4A1 ^ (ulong)cx * 7919UL ^ (ulong)cz * 104729UL ^ (ulong)dir);
            int count = (int)(InfiniteTerrain.Hash(cx, cz, bs) % 4);   // 0..3 per link
            var arc = Arc(main);
            // small roads and trails do not cross a highway at grade: they end at its shoulder
            List<Line> hw = null, rl = null;
            if (count > 0)
            {
                hw = new List<Line>(); TakeHighways(main.MinX - 1200, main.MinZ - 1200, main.MaxX + 1200, main.MaxZ + 1200, hw);
                rl = new List<Line>(); TakeRails(main.MinX - 1200, main.MinZ - 1200, main.MaxX + 1200, main.MaxZ + 1200, rl);
            }
            // ...nor a railway: they end clear of its cutting or embankment too
            bool NearHighway(double x, double z) => hw.Count > 0 && Influence(hw, x, z).Clear < Shoulder(RoadKind.Highway) + 6f
                                                 || rl.Count > 0 && Influence(rl, x, z).Clear < BranchRailClear;
            for (int b = 0; b < count; b++)
            {
                uint h = InfiniteTerrain.Hash(b, 17, bs);
                RoadKind kind = (h & 0xFF) < 140 ? RoadKind.Small : RoadKind.Trail;
                double s = arc[arc.Length - 1] * (0.15 + 0.7 * ((h >> 8 & 0xFFFF) / 65536.0));
                // not off the main where it crosses a highway: that stretch is the interchange's (its ramps join there)
                if (main.Underpasses != null)
                {
                    bool atCrossing = false;
                    foreach (var u in main.Underpasses)
                    {
                        At(main, arc, s, out double bx0, out double bz0, out _, out _, out _);
                        if (Math.Sqrt((bx0 - u.X) * (bx0 - u.X) + (bz0 - u.Z) * (bz0 - u.Z)) < RampMeet + 120) atCrossing = true;
                    }
                    if (atCrossing) continue;
                }
                if (main.LevelCrossings != null)
                {
                    At(main, arc, s, out double bx1, out double bz1, out _, out _, out _);
                    bool atLevel = false;
                    foreach (var c in main.LevelCrossings) if (Math.Sqrt((bx1 - c.X) * (bx1 - c.X) + (bz1 - c.Z) * (bz1 - c.Z)) < BranchRailClear + 60) atLevel = true;
                    if (atLevel) continue;
                }
                int side = (h >> 24 & 1) == 0 ? 1 : -1;
                double angle = ((h >> 25) / 127.0 - 0.5) * 1.2;   // +-0.6 rad off square
                At(main, arc, s, out double sx, out double sz, out _, out double tx, out double tz);
                double hx = -tz * side, hz = tx * side;   // perpendicular, this side
                double ca = Math.Cos(angle), sa = Math.Sin(angle);
                double headX = hx * ca - hz * sa, headZ = hx * sa + hz * ca;
                double length = kind == RoadKind.Small ? 400 + 600 * ((h >> 3 & 0xFF) / 255.0) : 250 + 550 * ((h >> 3 & 0xFF) / 255.0);
                const double step = 60.0;
                int steps = (int)(length / step);
                var xs = new List<double>(); var zs = new List<double>(); var hs = new List<float>();
                // start just inside the main road's edge, at the main road's surface THERE. Not `sh`: the start is
                // offset at an angle, so the main's nearest point (which is what the carve reads) sits along-track of s
                double start = PavedHalf(RoadKind.Main) - 1.0;
                double x = sx + headX * start, z = sz + headZ * start;
                if (NearHighway(x, z)) continue;   // the main meets a highway here
                xs.Add(x); zs.Add(z); hs.Add(ProfileNear(main, x, z));
                ulong turn = InfiniteTerrain.Mix(bs + (ulong)b * 0x9E37UL);
                float hiLimit = kind == RoadKind.Small ? 150f : 175f;
                for (int k = 1; k <= steps; k++)
                {
                    double bend = 0.32 * InfiniteTerrain.Gradient(k * 0.35, 0.5, turn);
                    double c2 = Math.Cos(bend), s2 = Math.Sin(bend);
                    double nx = headX * c2 - headZ * s2, nz = headX * s2 + headZ * c2;
                    headX = nx; headZ = nz;
                    // sample the step at thirds: a highway corridor is ~60 m across, the step is 60 m, so the ends alone
                    // could straddle it
                    if (NearHighway(x + headX * step / 3, z + headZ * step / 3) || NearHighway(x + headX * step * 2 / 3, z + headZ * step * 2 / 3)
                        || NearHighway(x + headX * step, z + headZ * step)) break;
                    x += headX * step; z += headZ * step;
                    float rh = _t.RawHeight(x, z);
                    if (rh < InfiniteTerrain.SeaLevel + 1.5f || rh > hiLimit) break;
                    xs.Add(x); zs.Add(z); hs.Add(rh);
                }
                if (xs.Count < 4) continue;   // under ~180 m: not worth a road
                var ch = hs.ToArray();
                float pin = ch[0];
                Smooth(ch, 2);
                ch[0] = pin;
                // truncate at the first stretch steeper than this class allows
                int keep = ch.Length;
                for (int k = 1; k < ch.Length; k++)
                    if (Math.Abs(ch[k] - ch[k - 1]) / step > MaxGrade(kind)) { keep = k; break; }
                if (keep < 4) continue;
                var line = Finish(kind, xs.GetRange(0, keep).ToArray(), zs.GetRange(0, keep).ToArray(), ch[..keep]);
                if (line != null) list.Add(line);
            }
            return list;
        }

        // =============================================================================================================
        // Highways. axis 0 runs east-west (along x, one per z band); axis 1 runs north-south (along z, one per x band).

        double BandCentre(int axis, long band)
        {
            uint h = InfiniteTerrain.Hash(band, axis, _s ^ 0x4777);
            return (band + 0.5 + 0.5 * ((h & 0xFFFF) / 65536.0 - 0.5)) * HighwayBand;   // +-1/4 band of jitter
        }

        void ToWorld(int axis, double u, double w, out double x, out double z) { if (axis == 0) { x = u; z = w; } else { x = w; z = u; } }

        (double u, double w, float h, bool ok) Anchor(int axis, long band, long k)
        {
            return _anchor.GetOrAdd((axis, band, k), key =>
            {
                double u = k * HighwaySeg;
                double meander = 1500.0 * InfiniteTerrain.Gradient(u / 9000.0, band * 3.1 + axis * 0.7, _s ^ 0x4778);
                double w0 = BandCentre(axis, band) + meander;
                double best = double.MaxValue, bw = w0; float bh = 0f;
                // +-1.2 km; the outer +-1.2..2.4 km only when every inner spot is wet (a lake on the band line), so a
                // lake shifts the anchor round it rather than ending the highway at its shore
                for (int c = -8; c <= 8; c++)
                {
                    double w = w0 + c * 300.0;
                    ToWorld(axis, u, w, out double x, out double z);
                    float h = _t.RawHeight(x, z);
                    double cost = (h < InfiniteTerrain.SeaLevel + 2f ? 1e6 : 0) + (Math.Abs(c) > 4 ? 5e5 : 0) + Math.Max(0f, h - 100f) * 20.0 + Math.Abs(c) * 15.0;
                    if (cost < best) { best = cost; bw = w; bh = h; }
                }
                return (u, bw, bh, best < 1e6);
            });
        }

        internal Line Highway(int axis, long band, long k)
        {
            return _hwy.TryGetValue((axis, band, k), out var hit) ? hit
                 : _hwy.GetOrAdd((axis, band, k), key =>
                 {
                     Trim();
                     var l = BuildHighway(key.Item1, key.Item2, key.Item3);
                     if (l.Exists)
                     {
                         long Id(int br) => ((long)(key.Item1 & 1) << 60) | ((key.Item2 & 0xFFFFF) << 40) | ((key.Item3 & 0xFFFFFFF) << 8) | (long)br;
                         l.Id = Id(0);
                         if (l.Branches != null) for (int i = 0; i < l.Branches.Count; i++) l.Branches[i].Id = Id(i + 1);
                     }
                     return l;
                 });
        }

        Line BuildHighway(int axis, long band, long k)
        {
            var a = Anchor(axis, band, k); var b = Anchor(axis, band, k + 1);
            if (!a.ok && !b.ok) return Drop(axis, band, k, "open sea");
            // The route's base line through the anchors is a Hermite cubic with Catmull-Rom tangents from the anchors either
            // side, so consecutive segments meet with the SAME heading. (A straight base per segment kinked the highway by
            // up to ~15 degrees at every anchor, every 6 km.)
            double wPrev = Anchor(axis, band, k - 1).w, wNext = Anchor(axis, band, k + 2).w;
            double m0 = 0.5 * (b.w - wPrev), m1 = 0.5 * (wNext - a.w);
            Func<double, double> baseW = t =>
            {
                double t2 = t * t, t3 = t2 * t;
                return (2 * t3 - 3 * t2 + 1) * a.w + (t3 - 2 * t2 + t) * m0 + (-2 * t3 + 3 * t2) * b.w + (t3 - t2) * m1;
            };
            // dw/dt at the two anchors is m0 / m1 -- and the NEIGHBOUR segment computes the identical value for the anchor
            // they share, so handing it to the spline as the end tangent makes the heading continuous across the join
            string why;
            if (a.ok && b.ok)
            {
                var whole = Route(a, b, axis, baseW, m0, m1, false, out why);
                if (whole != null) return whole;
                if (why != "bridge") return Drop(axis, band, k, why);
                // water too wide to bridge between two dry anchors (a bay, a big lake): run each side down to its own
                // shore, like a ferry crossing, rather than dropping 6 km of highway and dead-ending both neighbours
                var sa = Route(a, b, axis, baseW, m0, m1, true, out string wa);
                var sb = Route(b, a, axis, baseW, m0, m1, true, out string wb);
                if (sa == null && sb == null) return Drop(axis, band, k, $"water too wide to bridge, and no shore spur ({wa} / {wb})");
                var first = sa ?? sb;
                if (sa != null && sb != null) first.Branches = new List<Line> { sb };
                return first;
            }
            // a wet anchor is the COAST: from the dry end to the shore, so the highway runs down to the sea rather than
            // dead-ending at the last dry anchor, up to a whole segment (6 km) inland
            return Route(a.ok ? a : b, a.ok ? b : a, axis, baseW, m0, m1, true, out why) ?? Drop(axis, band, k, why);
        }

        /// <summary>One highway segment from anchor p toward anchor q. `toShore`: q is not reached -- the road goes as far as
        /// it can on dry land and ends there (water forbidden, end free). Otherwise both ends pinned, and water may be
        /// bridged (dear, and at most MaxBridgeStations); "bridge" in `why` means only a longer one would do.</summary>
        Line Route((double u, double w, float h, bool ok) p, (double u, double w, float h, bool ok) q, int axis, Func<double, double> baseW, double slope0, double slope1, bool toShore, out string why)
        {
            why = null;
            double du = Math.Sign(q.u - p.u) * HwStep;
            bool fwd = du > 0;
            double BaseAt(int i) => baseW(fwd ? (double)i / HwStations : 1.0 - (double)i / HwStations);
            int N = HwStations, L = HwLanes, mid = L / 2;
            float wetBelow = InfiniteTerrain.SeaLevel + 2f;
            var hgt = new float[N + 1, L];
            for (int i = 0; i <= N; i++)
            {
                double u = p.u + i * du, wBase = BaseAt(i);
                for (int j = 0; j < L; j++)
                {
                    if ((i == 0 || (i == N && !toShore)) && j != mid) { hgt[i, j] = float.NaN; continue; }   // pinned to the anchors
                    ToWorld(axis, u, wBase + (j - mid) * HwLaneStep, out double x, out double z);
                    hgt[i, j] = _t.RawHeight(x, z);
                }
            }
            // DP over stations x lateral lanes. Node cost: high ground expensive, water a BRIDGE (allowed when pinned, but
            // dearer than any sane detour). Edge cost: grade (squared, so gentle beats steep by a lot) and lateral change
            // (squared: straight beats wiggly).
            var cost = new double[N + 1, L];
            var from = new int[N + 1, L];
            for (int j = 0; j < L; j++) cost[0, j] = j == mid ? 0 : double.MaxValue;
            int end = 0;
            for (int i = 1; i <= N; i++)
            {
                bool reached = false;
                for (int j = 0; j < L; j++)
                {
                    cost[i, j] = double.MaxValue;
                    float h = hgt[i, j];
                    if (float.IsNaN(h)) continue;
                    bool wet = h < wetBelow;
                    if (wet && toShore) continue;
                    double node = (wet ? BridgeCost : 0) + Math.Max(0f, h - 100f) * 6.0 + Math.Abs(j - mid) * 0.15;
                    for (int jp = Math.Max(0, j - 2); jp <= Math.Min(L - 1, j + 2); jp++)
                    {
                        if (cost[i - 1, jp] == double.MaxValue) continue;
                        double run = Math.Sqrt(HwStep * HwStep + ((j - jp) * HwLaneStep) * ((j - jp) * HwLaneStep));
                        double grade = Math.Abs(h - hgt[i - 1, jp]) / run * 100.0;
                        double c = cost[i - 1, jp] + node + grade * grade * 0.35 + (j - jp) * (j - jp) * 3.0;
                        if (c < cost[i, j]) { cost[i, j] = c; from[i, j] = jp; reached = true; }
                    }
                }
                if (!reached) break;
                end = i;
            }
            int last = mid;
            if (toShore)
            {
                if (end < 6) { why = "shore within 900 m of the anchor"; return null; }
                for (int j = 0; j < L; j++) if (cost[end, j] < cost[end, last]) last = j;
            }
            else if (end < N || cost[N, mid] == double.MaxValue) { why = "no route"; return null; }
            var lane = new int[end + 1];
            lane[end] = last;
            for (int i = end; i > 0; i--) lane[i - 1] = from[i, lane[i]];
            int wetRun = 0, longestBridge = 0;
            for (int i = 0; i <= end; i++) { wetRun = hgt[i, lane[i]] < wetBelow ? wetRun + 1 : 0; longestBridge = Math.Max(longestBridge, wetRun); }
            if (longestBridge > MaxBridgeStations) { why = "bridge"; return null; }
            var xs = new double[end + 1]; var zs = new double[end + 1]; var hs = new float[end + 1];
            var lat = new double[end + 1];
            for (int i = 0; i <= end; i++) lat[i] = (lane[i] - mid) * HwLaneStep;
            // straighten the lane staircase (ends held), then place
            for (int pass = 0; pass < 6; pass++)
            {
                var tmp = (double[])lat.Clone();
                for (int i = 1; i < end; i++) tmp[i] = 0.25 * lat[i - 1] + 0.5 * lat[i] + 0.25 * lat[i + 1];
                lat = tmp;
            }
            // ...and fade the offset in over the first/last 600 m at a pinned anchor: lat is 0 there but its SLOPE is not,
            // and a slope is a heading change. Offset x a taper that is 0 at the end has zero slope at the end.
            for (int i = 0; i <= end; i++)
            {
                double fromEnd = toShore ? i : Math.Min(i, end - i);
                double f = Math.Clamp(fromEnd / 4.0, 0.0, 1.0);
                lat[i] *= f * f * (3 - 2 * f);
            }
            for (int i = 0; i <= end; i++)
            {
                double u = p.u + i * du, w = BaseAt(i) + lat[i];
                ToWorld(axis, u, w, out xs[i], out zs[i]);
                hs[i] = i == 0 ? p.h : i == N ? q.h : _t.RawHeight(xs[i], zs[i]);
                // over water (a bridged lake, or a station the straightening pulled off the shore) the deck stands clear
                // of it; the ground carve then raises a causeway underneath
                hs[i] = Math.Max(hs[i], InfiniteTerrain.SeaLevel + DeckAboveWater);
            }
            Smooth(hs, 8);   // averaging values that are all >= the deck height cannot take one below it
            hs[0] = Math.Max(p.h, InfiniteTerrain.SeaLevel + DeckAboveWater);
            if (!toShore) hs[end] = Math.Max(q.h, InfiniteTerrain.SeaLevel + DeckAboveWater);
            // cut and fill to the grade limit: forward then backward, each pass only ever moves a station TOWARD its
            // neighbour, so a stretch that is too steep is shared out across the stations either side of it
            float maxRise = MaxGrade(RoadKind.Highway) * (float)HwStep;
            for (int rep = 0; rep < 4; rep++)
            {
                for (int i = 1; i < end || (toShore && i == end); i++) hs[i] = Math.Clamp(hs[i], hs[i - 1] - maxRise, hs[i - 1] + maxRise);
                for (int i = end - 1; i > 0; i--) hs[i] = Math.Clamp(hs[i], hs[i + 1] - maxRise, hs[i + 1] + maxRise);
            }
            for (int i = 0; i < end; i++) if (Math.Abs(hs[i + 1] - hs[i]) > maxRise * 1.001f) { why = $"grade unfixable at station {i}"; return null; }
            // unit heading of the base curve at an anchor, in the direction of travel
            (double, double) Heading(double dwdt)
            {
                double tu = fwd ? HighwaySeg : -HighwaySeg, tw = fwd ? dwdt : -dwdt, l = Math.Sqrt(tu * tu + tw * tw);
                ToWorld(axis, tu / l, tw / l, out double tx, out double tz);
                return (tx, tz);
            }
            var startT = Heading(fwd ? slope0 : slope1);
            (double, double)? endT = toShore ? null : Heading(fwd ? slope1 : slope0);
            var line = Finish(RoadKind.Highway, xs, zs, hs, startT, endT);
            if (line == null) why = "drawn curve over grade";
            else MarkStretches(line);
            return line;
        }

        /// <summary>Find a highway's raised and cut Stretches, each CARRIAGEWAY on its own. Samples the natural ground
        /// at the carriageway's centre and both its edges at every dense point; the fill (or cut) that counts is the
        /// SMALLEST of the three, so a carriageway half on fill and half in a cut is a retaining wall, neither.</summary>
        void MarkStretches(Line e)
        {
            int n = e.X.Length;
            e.Raised = new List<Stretch>(); e.Cut = new List<Stretch>();
            e.RaisedSeg = new bool[2][]; e.CutSeg = new bool[2][];
            for (int side = -1; side <= 1; side += 2)
            {
                // lengths along THIS carriageway: the outside of a bend is longer than the route's centreline
                var cw = CarriagewayPts(e, side, 0, n - 1);
                var arc = new double[n];
                for (int i = 1; i < n; i++)
                    arc[i] = arc[i - 1] + Math.Sqrt((cw[i].x - cw[i - 1].x) * (cw[i].x - cw[i - 1].x) + (cw[i].z - cw[i - 1].z) * (cw[i].z - cw[i - 1].z));
                var on = new bool[n]; var fill = new float[n]; var wet = new bool[n];
                var deep = new bool[n]; var cut = new float[n];
                for (int i = 0; i < n; i++)
                {
                    CarriagewaySample(e, i, side, out float f, out float c, out bool w);
                    fill[i] = f; cut[i] = c; wet[i] = w;
                    on[i] = f >= RaiseFill || w;
                    deep[i] = c >= CutDepth;
                }
                int si = side < 0 ? 0 : 1;
                (var raised, e.RaisedSeg[si]) = Stretches(e, arc, on, fill, wet, RaiseMinLength, RaiseMergeGap, side);
                (var cuts, e.CutSeg[si]) = Stretches(e, arc, deep, cut, null, CutMinLength, CutMergeGap, side);
                e.Raised.AddRange(raised); e.Cut.AddRange(cuts);
            }
            e.BridgePieces = new List<BridgePiece>();
            foreach (var s in e.Raised) WalkBridge(e, s, e.BridgePieces);
            FindTunnels(e);
        }

        /// <summary>One tunnel: its facades at horizontal route arc A0..A1 (F0..F1 in dense-index space), and the
        /// centreline every TunnelStep between them at the driven surface -- what the game sweeps the section along.</summary>
        public sealed class TunnelSpan
        {
            public RoadKind Kind;                     // a highway's (two tubes) or a railway's (one)
            public double A0, A1, F0, F1;
            public double[] X, Z; public float[] Y;   // the route's centreline (the median between the tubes)
            public double[][] SX, SZ;                 // [tube][station]: each tube's centreline (TubeOffset: a highway's -offset, +offset carriageway; a rail's track)
        }

        /// <summary>Find the runs where the hill already buries the whole widened shell (nine samples across it, every
        /// TunnelProbeStep along) for at least two portals' length, and bore them. A run whose portal would hang past the
        /// segment's end is left to the open cut: the next segment's own run would put a second portal face to face.</summary>
        void FindTunnels(Line e, Func<double, bool> keepOpen = null)
        {
            e.HArc = Arc(e);
            double total = e.HArc[e.HArc.Length - 1];
            var kind = e.Kind;
            float reach = ShellReachOf(kind);
            var spans = new List<TunnelSpan>();
            var runs = new List<(double a0, double a1)>();
            double runStart = -1;
            // a rail's one shell is 14 m across where a highway's two are 41 m: seven samples across do what thirteen
            // do there, and every 8 m along (its runs are kilometres of track; this is its single dearest step)
            int across = kind == RoadKind.Rail ? 3 : 6;
            double step = kind == RoadKind.Rail ? 2 * TunnelProbeStep : TunnelProbeStep;
            for (double s = 0; s <= total; s += step)
            {
                At(e, e.HArc, s, out double px, out double pz, out float h, out double tx, out double tz);
                float road = TunnelSurfaceY(kind, h);
                bool covered = keepOpen == null || !keepOpen(s);
                for (int q = -across; q <= across && covered; q++)
                {
                    float o = reach * 0.98f * q / across;   // across the shells, the edges (and a highway's median) included
                    if (_t.RawHeight(px - tz * o, pz + tx * o) < road + ShellTopOf(kind, o) + TunnelCover) covered = false;
                }
                if (covered) { if (runStart < 0) runStart = s; }
                else { if (runStart >= 0) Keep(runStart, s - step); runStart = -1; }
            }
            if (runStart >= 0) Keep(runStart, total);
            // A BORE IS STRAIGHT (strawberry 2026-10-10: "the tunnel tries to follow the terrain when it shouldnt?"). The
            // profile was shaped to the smoothed land, so through a hill it humped up with the hill -- up to 2.6 m over a
            // straight portal-to-portal grade on a highway, 6 m on a rail. Inside each run (dense point to dense point,
            // so the line is exactly straight through both portals) it IS that grade -- no steeper than the grade it
            // already climbed between them. Mostly that lowers it (the hump; the hill only covers the shell by more);
            // where a crossing's approach dipped it near a portal it rises by up to ~0.4 m, inside the TunnelCover the
            // run was chosen with.
            foreach (var (a0, a1) in runs)
            {
                int i0 = Math.Max(0, (int)Math.Floor(Frac(a0))), i1 = Math.Min(e.Segments, (int)Math.Ceiling(Frac(a1)));
                double span = Math.Max(1e-6, e.HArc[i1] - e.HArc[i0]);
                for (int i = i0 + 1; i < i1; i++)
                    e.H[i] = e.H[i0] + (e.H[i1] - e.H[i0]) * (float)((e.HArc[i] - e.HArc[i0]) / span);
            }
            foreach (var (a0, a1) in runs) Close(a0, a1);
            e.TunnelSpans = spans.Count > 0 ? spans : null;

            void Keep(double a0, double a1)
            {
                if (a1 - a0 < TunnelMinLength) return;
                if (a0 < TunnelSectionLength || a1 > total - TunnelSectionLength) return;
                runs.Add((a0, a1));
            }
            void Close(double a0, double a1)
            {
                int m = (int)Math.Ceiling((a1 - a0) / TunnelStep), tubes = TunnelTubes(kind);
                var t = new TunnelSpan { Kind = kind, A0 = a0, A1 = a1, F0 = Frac(a0), F1 = Frac(a1), X = new double[m + 1], Z = new double[m + 1], Y = new float[m + 1],
                                         SX = new double[tubes][], SZ = new double[tubes][] };
                for (int s = 0; s < tubes; s++) { t.SX[s] = new double[m + 1]; t.SZ[s] = new double[m + 1]; }
                for (int i = 0; i <= m; i++)
                {
                    double a = Math.Min(a0 + i * TunnelStep, a1);
                    At(e, e.HArc, a, out t.X[i], out t.Z[i], out float hh, out _, out _);
                    t.Y[i] = TunnelSurfaceY(kind, hh);
                    // each carriageway's own centreline here, built exactly as its ribbon is (RibbonTangent offsets at the
                    // two dense points, joined straight), so the tube sits on the road it carries
                    double f = Frac(a); int k = Math.Min((int)f, e.Segments - 1); double ft = f - k;
                    var (t0x, t0z) = RibbonTangent(e, k); var (t1x, t1z) = RibbonTangent(e, k + 1);
                    for (int s = 0; s < tubes; s++)
                    {
                        double o = TubeOffset(kind, s);
                        double ax = e.X[k] - t0z * o, az = e.Z[k] + t0x * o, bx = e.X[k + 1] - t1z * o, bz = e.Z[k + 1] + t1x * o;
                        t.SX[s][i] = ax + (bx - ax) * ft; t.SZ[s][i] = az + (bz - az) * ft;
                    }
                }
                spans.Add(t);
            }
            double Frac(double a)
            {
                int k = 0; while (k < e.Segments - 1 && e.HArc[k + 1] < a) k++;
                return k + Math.Clamp((a - e.HArc[k]) / Math.Max(1e-9, e.HArc[k + 1] - e.HArc[k]), 0, 1);
            }
        }

        /// <summary>The ground round a tunnel, given the ground the rest of the world made there (`g`: natural, since a
        /// tunnel suspends its own road's carve). Over the shell it is never lower than the shell (a gully between the
        /// samples that chose the run would otherwise open a window into it), and over each portal the hill is cut back
        /// to the facade's height plus TunnelCover, rising HeadwallSlope per metre behind the hole band -- a heightfield
        /// cannot overhang the mouth. Blended out beside the shell.</summary>
        public static float TunnelGround(in RoadHit hit, float g)
        {
            var k = hit.TunnelKind;
            float lat = hit.TunnelLat, reach = ShellReachOf(k), alat = Math.Abs(lat);
            if (alat <= reach) g = Math.Max(g, hit.TunnelRoad + ShellTopOf(k, lat) + 0.25f);
            // the cap sits just over the shells where the hole band ends -- the terrain's edge there is seen at a grazing
            // angle past the headwall's top, and every metre between it and the shell is a slot of open sky
            float cap = hit.TunnelRoad + ShellTopOf(k, Math.Clamp(lat, -reach, reach)) + HeadwallCover + Math.Max(0f, hit.TunnelIn - TunnelHoleIn) * HeadwallSlope;
            // out to where the tunnel stops being reported (the line's carve reach), so there is no step at its edge
            if (g > cap) g += (cap - g) * Smoothstep(PavedHalf(k) + Shoulder(k), reach, alat);
            return g;
        }

        /// <summary>The tunnels whose middle station lies in the rectangle (each tunnel belongs to exactly one region).</summary>
        public List<TunnelSpan> TunnelsIn(List<Line> lines, double x0, double z0, double x1, double z1)
        {
            var list = new List<TunnelSpan>();
            if (!Tunnels) return list;
            foreach (var e in lines)
            {
                if (e.TunnelSpans == null || e.MaxX < x0 || e.MinX > x1 || e.MaxZ < z0 || e.MinZ > z1) continue;
                foreach (var t in e.TunnelSpans)
                {
                    int m = t.X.Length / 2;
                    if (t.X[m] >= x0 && t.X[m] < x1 && t.Z[m] >= z0 && t.Z[m] < z1) list.Add(t);
                }
            }
            return list;
        }

        /// <summary>Lay one carriageway's bridge over a raised stretch: deck units at BridgePitch along the carriageway,
        /// each joint closed on a bend by the tiling rule (SplineTiling.OverlapFor: back off halfWidth*turn, at most
        /// half a pitch -- free on a straight), a pier pair every PierEveryUnits where the drop clears the deck's own
        /// thickness, and a cap at each end. Pier: top at deckY + PierTop (1.02 m up inside the deck, retail's own
        /// joint), foot on the NATURAL ground -- under a bridge the carve leaves the ground alone.</summary>
        void WalkBridge(Line e, Stretch st, List<BridgePiece> outp, Func<double, double, bool> pierOk = null)
        {
            var span = WalkDecks(e, st.Side, st.I0, st.I1, null, 0, true, outp, pierOk);
            if (span is DeckSpan d)
            {
                e.DeckCover ??= new[] { new List<DeckSpan>(), new List<DeckSpan>() };
                if (st.Side == 0) { e.DeckCover[0].Add(d); e.DeckCover[1].Add(d); }   // a rail's one deck: both "sides" of the centreline
                else e.DeckCover[st.Side < 0 ? 0 : 1].Add(d);
            }
        }

        /// <summary>The deck walk itself: units over carriageway `side` between dense points i0..i1 -- the whole range
        /// (a raised stretch), or, given `centre` (a fractional dense index) and `half`, exactly that many metres of 3D arc
        /// either side of it (an underpass under a main road). Returns the span the decks cover, null if none fitted.</summary>
        DeckSpan? WalkDecks(Line e, int side, int i0, int i1, double? centre, double half, bool piers, List<BridgePiece> outp, Func<double, double, bool> pierOk = null)
        {
            var st = new Stretch { Side = side, I0 = i0, I1 = i1 };
            // the carriageway as a polyline, a little past each end so the deck reaches the stretch's ends
            var pts = CarriagewayPts(e, st.Side, Math.Max(0, st.I0 - 1), Math.Min(e.X.Length - 1, st.I1 + 1));
            int n = pts.Length;
            // ⚠ 3D ARC LENGTH, grade included. A deck unit is BridgePitch long along its own graded axis; spacing the
            // units by HORIZONTAL distance left every joint on a slope short by pitch*(1/cos(grade) - 1) -- 1.97 cm at
            // 7% -- and strawberry saw the seams: "looks like theres some slight gaps"
            var arc = new double[n];
            for (int i = 1; i < n; i++)
            {
                double dx = pts[i].x - pts[i - 1].x, dz = pts[i].z - pts[i - 1].z, dh = pts[i].h - pts[i - 1].h;
                arc[i] = arc[i - 1] + Math.Sqrt(dx * dx + dz * dz + dh * dh);
            }
            // only the stretch itself is bridged: start and end at its own first and last points -- or the given span
            double a0 = st.I0 > 0 ? arc[1] : 0, a1 = st.I1 < e.X.Length - 1 ? arc[n - 2] : arc[n - 1];
            if (centre is double fc)
            {
                double lf = Math.Clamp(fc - Math.Max(0, st.I0 - 1), 0, n - 1 - 1e-9);
                int ki = (int)lf; double ac = arc[ki] + (arc[Math.Min(n - 1, ki + 1)] - arc[ki]) * (lf - ki);
                a0 = ac - half; a1 = ac + half;
                if (a0 < 0 || a1 > arc[n - 1]) return null;   // the span runs off the polyline handed in
            }
            // the deck's roadway is its local Z=0, so it sits ON the driven surface -- the same SurfaceY the approach
            // slab's top is at, which is what makes the two meet flush (EditorBridgeSpline lifts by RoadSurfaceOffset
            // for the same reason)
            double surf = e.Kind == RoadKind.Rail ? RailOriginY(0f) : SurfaceY(e.Kind, 0f);   // a rail deck carries the track's root
            (double x, double y, double z) At(double s)
            {
                s = Math.Clamp(s, 0, arc[n - 1]);
                int k = 0; while (k < n - 2 && arc[k + 1] < s) k++;
                double t = (s - arc[k]) / Math.Max(1e-9, arc[k + 1] - arc[k]);
                return (pts[k].x + (pts[k + 1].x - pts[k].x) * t, pts[k].h + (pts[k + 1].h - pts[k].h) * t + surf, pts[k].z + (pts[k + 1].z - pts[k].z) * t);
            }
            (double x, double y, double z) Dir((double x, double y, double z) p, (double x, double y, double z) q)
            {
                double dx = q.x - p.x, dy = q.y - p.y, dz = q.z - p.z, l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                return l < 1e-9 ? (1, 0, 0) : (dx / l, dy / l, dz / l);
            }
            int units = 0, jBase = Math.Max(0, st.I0 - 1);
            double sLastEnd = a0;
            // the ribbon's texture distance (horizontal arc along this carriageway from the line's start) at each point
            var tex = new double[n];
            {
                var head = CarriagewayPts(e, st.Side, 0, jBase);
                for (int i = 1; i < head.Length; i++) tex[0] += Math.Sqrt((head[i].x - head[i - 1].x) * (head[i].x - head[i - 1].x) + (head[i].z - head[i - 1].z) * (head[i].z - head[i - 1].z));
                for (int i = 1; i < n; i++) tex[i] = tex[i - 1] + Math.Sqrt((pts[i].x - pts[i - 1].x) * (pts[i].x - pts[i - 1].x) + (pts[i].z - pts[i - 1].z) * (pts[i].z - pts[i - 1].z));
            }
            float TexAt(double s)
            {
                s = Math.Clamp(s, 0, arc[n - 1]);
                int k = 0; while (k < n - 2 && arc[k + 1] < s) k++;
                double t = (s - arc[k]) / Math.Max(1e-9, arc[k + 1] - arc[k]);
                return (float)(tex[k] + (tex[k + 1] - tex[k]) * t);
            }
            (double x, double y, double z) firstMid = default, lastMid = default, firstDir = default, lastDir = default;
            for (double s = a0; s + BridgePitch <= a1 + 1e-3;)
            {
                var p0 = At(s); var p1 = At(s + BridgePitch);
                sLastEnd = s + BridgePitch;
                var dir = Dir(p0, p1);
                var mid = ((p0.x + p1.x) * 0.5, (p0.y + p1.y) * 0.5, (p0.z + p1.z) * 0.5);
                outp.Add(new BridgePiece { Kind = 0, X = mid.Item1, Y = mid.Item2, Z = mid.Item3, DX = (float)dir.x, DY = (float)dir.y, DZ = (float)dir.z, K = 1f,
                                           S0 = TexAt(s), S1 = TexAt(s + BridgePitch), Road = (byte)e.Kind });
                if (units == 0) { firstMid = mid; firstDir = dir; }
                lastMid = mid; lastDir = dir;
                if (piers && units % PierEveryUnits == 0 && (pierOk == null || pierOk(mid.Item1, mid.Item3)))
                {
                    float ground = _t.RawHeight(mid.Item1, mid.Item3);
                    double drop = (mid.Item2 + DeckSoffit) - ground;
                    if (drop >= MinPierDrop)
                    {
                        double k = (mid.Item2 + PierTop - ground) / PierSpan;
                        outp.Add(new BridgePiece { Kind = 1, X = mid.Item1, Y = ground - PierBottom * k, Z = mid.Item3, DX = (float)dir.x, DY = (float)dir.y, DZ = (float)dir.z, K = (float)k, Road = (byte)e.Kind });
                    }
                }
                units++;
                // THE JOINT. Two rigid units meeting at a bend have square ends that splay about the roadway's centre line:
                // the corners on the OUTSIDE of the turn part by (their distance from it) x (the turn), the inside ones
                // bury the same. So the next unit is backed off along the road by that much. Across (a horizontal bend)
                // the outside corner is the deck's edge, BridgeHalfWidth out; up and down (a vertical bend) it is the
                // SOFFIT, 4 m under the roadway, at a SAG (grade increasing -- the hollow is the inside), and the
                // parapet's top, 1.25 m over it, at a CREST. Without the vertical term 63 sag joints in the test window
                // stood open at the soffit, worst 84 mm (strawberry: "the gaps with vertical bends on bridges").
                var nd = Dir(At(s + BridgePitch), At(s + 2 * BridgePitch));
                double hx = dir.x, hz = dir.z, hl = Math.Sqrt(hx * hx + hz * hz), nx = nd.x, nz = nd.z, nl = Math.Sqrt(nx * nx + nz * nz);
                double turn = hl > 1e-9 && nl > 1e-9 ? Math.Acos(Math.Clamp((hx * nx + hz * nz) / (hl * nl), -1.0, 1.0)) : 0;
                double rise = Math.Asin(Math.Clamp(nd.y, -1.0, 1.0)) - Math.Asin(Math.Clamp(dir.y, -1.0, 1.0));
                double lever = rise > 0 ? -DeckSoffit : DeckParapetTop;
                double overlap = Math.Min(BridgeHalfWidth * DeckScale(e.Kind) * turn + lever * Math.Abs(rise), BridgePitch * 0.5);
                s += BridgePitch - overlap;
            }
            if (units == 0) return null;
            // the span the decks cover, in the line's dense-index space, so ribbon and carve stop exactly where they do
            double Frac(double s)
            {
                s = Math.Clamp(s, 0, arc[n - 1]);
                int k = 0; while (k < n - 2 && arc[k + 1] < s) k++;
                return jBase + k + (s - arc[k]) / Math.Max(1e-9, arc[k + 1] - arc[k]);
            }
            var cover = new DeckSpan(Frac(a0), Frac(sLastEnd), Heading(firstDir), Heading(lastDir));
            static (float x, float z) Heading((double x, double y, double z) d)
            {
                double l = Math.Sqrt(d.x * d.x + d.z * d.z);
                return l < 1e-9 ? (1f, 0f) : ((float)(d.x / l), (float)(d.z / l));
            }
            // end caps, facing OUT: the far one along the run, the near one against it
            outp.Add(new BridgePiece { Kind = 2, X = lastMid.x + lastDir.x * BridgePitch * 0.5, Y = lastMid.y + lastDir.y * BridgePitch * 0.5, Z = lastMid.z + lastDir.z * BridgePitch * 0.5,
                                       DX = (float)lastDir.x, DY = (float)lastDir.y, DZ = (float)lastDir.z, K = 1f, Road = (byte)e.Kind });
            outp.Add(new BridgePiece { Kind = 2, X = firstMid.x - firstDir.x * BridgePitch * 0.5, Y = firstMid.y - firstDir.y * BridgePitch * 0.5, Z = firstMid.z - firstDir.z * BridgePitch * 0.5,
                                       DX = -(float)firstDir.x, DY = -(float)firstDir.y, DZ = -(float)firstDir.z, K = 1f, Road = (byte)e.Kind });
            return cover;
        }

        /// <summary>How far the embankment reaches in under a deck end -- more than one LOD0 ground cell, so the
        /// cell the deck ends in is carved on its approach side.</summary>
        public const float AbutmentTuck = 2f;

        /// <summary>One bridge's span on one carriageway: from F0 to F1 in the line's dense-index space (k + t), and the
        /// deck's horizontal heading at each end. The ribbon's cut edge is squared to THAT heading, not its own segment's:
        /// a deck unit is a straight chord, so where one spans a profile vertex it points up to half the vertex's bend
        /// off either segment -- 3.15 deg at worst, which at the deck's 8.5 m half-width is a 0.47 m wedge at its end.</summary>
        public readonly record struct DeckSpan(double F0, double F1, (float x, float z) StartHeading, (float x, float z) EndHeading);

        /// <summary>Is the route at dense index k + t inside one of the line's tunnels -- or in a portal's forecourt? There
        /// the ground mesh must never lift the slab: inside it is the hill, and a cell straddling a facade interpolates
        /// the hill too (the first render stood the approach's last pieces up as 6 m walls across the mouth).</summary>
        static bool InTunnel(Line e, int k, double t)
        {
            if (!Tunnels || e.TunnelSpans == null) return false;
            double a = e.HArc[k] + (e.HArc[k + 1] - e.HArc[k]) * t;
            foreach (var tn in e.TunnelSpans) if (a >= tn.A0 - TunnelForecourt && a <= tn.A1 + TunnelForecourt) return true;
            return false;
        }

        static bool Covered(List<DeckSpan> cover, double f, double inset = 0)
        {
            if (cover == null) return false;
            foreach (var c in cover) if (f >= c.F0 + inset && f <= c.F1 - inset) return true;
            return false;
        }

        /// <summary>The sub-intervals (in t, 0..1) of segment k that no deck covers, each with the heading its end must
        /// be squared to where that end meets a deck (null where it is an ordinary piece joint).</summary>
        static List<(double u0, double u1, (float x, float z)? t0, (float x, float z)? t1)> Uncovered(List<DeckSpan> cover, int k)
        {
            const double Eps = 1e-9;
            var free = new List<(double u0, double u1, (float x, float z)? t0, (float x, float z)? t1)> { (0.0, 1.0, null, null) };
            if (cover == null) return free;
            foreach (var c in cover)
            {
                double c0 = c.F0 - k, c1 = c.F1 - k;
                if (c1 < -Eps || c0 > 1 + Eps) continue;
                var next = new List<(double, double, (float, float)?, (float, float)?)>();
                foreach (var f in free)
                {
                    // the deck starts at or past this interval's end: untouched, but squared to the deck if they MEET
                    if (c0 >= f.u1 - Eps) { next.Add(c0 <= f.u1 + Eps ? (f.u0, f.u1, f.t0, c.StartHeading) : f); continue; }
                    if (c1 <= f.u0 + Eps) { next.Add(c1 >= f.u0 - Eps ? (f.u0, f.u1, c.EndHeading, f.t1) : f); continue; }
                    if (c0 > f.u0) next.Add((f.u0, c0, f.t0, c.StartHeading));
                    if (c1 < f.u1) next.Add((c1, f.u1, c.EndHeading, f.t1));
                }
                free = next;
            }
            free.RemoveAll(f => f.u1 - f.u0 < 1e-6);
            return free;
        }

        /// <summary>The bridge pieces whose root lies in the rectangle (each piece belongs to exactly one region).</summary>
        public List<BridgePiece> BridgesIn(List<Line> lines, double x0, double z0, double x1, double z1)
        {
            var list = new List<BridgePiece>();
            if (!Bridges) return list;
            foreach (var e in lines)
            {
                if (e.MaxX < x0 || e.MinX > x1 || e.MaxZ < z0 || e.MinZ > z1) continue;
                if (e.BridgePieces != null)
                    foreach (var p in e.BridgePieces)
                        if (p.X >= x0 && p.X < x1 && p.Z >= z0 && p.Z < z1) list.Add(p);
                if (e.Underpasses != null)   // a main's: the highway's decks over it, or its own over the highway
                    foreach (var u in e.Underpasses)
                        foreach (var p in u.Pieces)
                            if (p.X >= x0 && p.X < x1 && p.Z >= z0 && p.Z < z1) list.Add(p);
            }
            return list;
        }

        /// <summary>A highway carriageway's deck cover: its own bridges', plus the decks mains in the working set have laid
        /// over themselves on it.</summary>
        static List<DeckSpan> CoverWith(Line e, int side, List<Underpass> ups)
        {
            var own = e.DeckCover?[side];
            if (ups == null || e.Id == 0) return own;
            List<DeckSpan> all = null;
            foreach (var u in ups)
                if (u.HwId == e.Id && u.Cover[side] is DeckSpan d) (all ??= own != null ? new List<DeckSpan>(own) : new List<DeckSpan>()).Add(d);
            return all ?? own;
        }

        /// <summary>Fill and cut under one carriageway at dense point i: the smallest of the three samples (centre and
        /// both edges, just inside the asphalt), and whether any of them is over water. Public-facing twin of what the
        /// marking uses, so a test can measure the same carriageway without re-deriving its geometry.</summary>
        void CarriagewaySample(Line e, int i, int side, out float fill, out float cut, out bool wet)
        {
            int n = e.X.Length, a = Math.Max(0, i - 1), b = Math.Min(n - 1, i + 1);
            double tx = e.X[b] - e.X[a], tz = e.Z[b] - e.Z[a], tl = Math.Sqrt(tx * tx + tz * tz);
            double nx = -tz / tl, nz = tx / tl;
            float ground = e.H[i] - Bed;
            fill = float.MaxValue; cut = float.MaxValue; wet = false;
            for (int s = -1; s <= 1; s++)
            {
                double o = side * HighwayRibbonOffset + s * HighwayLaneHalf * 0.9;
                float r = _t.RawHeight(e.X[i] + nx * o, e.Z[i] + nz * o);
                fill = Math.Min(fill, ground - r);
                cut = Math.Min(cut, r - ground);
                if (r < InfiniteTerrain.SeaLevel) wet = true;
            }
        }

        /// <summary>Runs of marked points, merged across gaps under `mergeGap`, then the ones under `minLength` dropped.</summary>
        static (List<Stretch>, bool[]) Stretches(Line e, double[] arc, bool[] on, float[] measure, bool[] wet, float minLength, float mergeGap, int side)
        {
            int n = on.Length;
            var runs = new List<(int i0, int i1)>();
            for (int i = 0; i < n; i++)
            {
                if (!on[i]) continue;
                int j = i; while (j + 1 < n && on[j + 1]) j++;
                if (runs.Count > 0 && arc[i] - arc[runs[^1].i1] < mergeGap) runs[^1] = (runs[^1].i0, j);
                else runs.Add((i, j));
                i = j;
            }
            var list = new List<Stretch>();
            var seg = new bool[e.Segments];
            foreach (var (i0, i1) in runs)
            {
                float len = (float)(arc[i1] - arc[i0]);
                if (len < minLength) continue;
                float mx = 0f; bool w = false;
                for (int i = i0; i <= i1; i++) { mx = Math.Max(mx, measure[i]); if (wet != null) w |= wet[i]; }
                list.Add(new Stretch { Side = side, I0 = i0, I1 = i1, Length = len, Max = mx, OverWater = w });
                for (int k = i0; k < i1; k++) seg[k] = true;
            }
            return (list, seg);
        }

        // =============================================================================================================
        // Shared shaping

        static void Smooth(float[] h, int passes)
        {
            int n = h.Length - 1;
            var tmp = new float[h.Length];
            for (int p = 0; p < passes; p++)
            {
                tmp[0] = h[0]; tmp[n] = h[n];
                for (int k = 1; k < n; k++) tmp[k] = 0.25f * h[k - 1] + 0.5f * h[k] + 0.25f * h[k + 1];
                Array.Copy(tmp, h, h.Length);
            }
        }

        /// <summary>Catmull-Rom the control points x4 for the drawn/carved centreline; the profile LINEAR IN ARC LENGTH
        /// between controls (linear in the spline parameter steepens where Catmull-Rom bunches samples -- the drivability
        /// test caught that at 20.7%). Returns null if the drawn geometry breaks the class's grade limit.</summary>
        Line Finish(RoadKind kind, double[] cx, double[] cz, float[] ch, (double x, double z)? startTangent = null, (double x, double z)? endTangent = null, int subdiv = Sub)
        {
            int C = cx.Length - 1, P = C * subdiv;
            var e = new Line { Kind = kind, X = new double[P + 1], Z = new double[P + 1], H = new float[P + 1] };
            // Catmull-Rom's tangent at P0 is (P1 - P[-1]) / 2. With no neighbour, P[-1] = P0 (the curve leaves along its
            // first chord). Given a heading T, P[-1] = P1 - 2|P1-P0| T makes the tangent exactly T, so two lines that are
            // handed the same T at a shared point meet there without a kink. Same at the far end.
            double sx0 = cx[0], sz0 = cz[0], ex1 = cx[C], ez1 = cz[C];
            if (startTangent is (double tx, double tz))
            {
                double l = Math.Sqrt((cx[1] - cx[0]) * (cx[1] - cx[0]) + (cz[1] - cz[0]) * (cz[1] - cz[0]));
                sx0 = cx[1] - 2 * l * tx; sz0 = cz[1] - 2 * l * tz;
            }
            if (endTangent is (double ux, double uz))
            {
                double l = Math.Sqrt((cx[C] - cx[C - 1]) * (cx[C] - cx[C - 1]) + (cz[C] - cz[C - 1]) * (cz[C] - cz[C - 1]));
                ex1 = cx[C - 1] + 2 * l * ux; ez1 = cz[C - 1] + 2 * l * uz;
            }
            double PX(int i) => i < 0 ? sx0 : i > C ? ex1 : cx[i];
            double PZ(int i) => i < 0 ? sz0 : i > C ? ez1 : cz[i];
            for (int k = 0; k < C; k++)
            {
                for (int sub = 0; sub < subdiv; sub++)
                {
                    double u = (double)sub / subdiv, u2 = u * u, u3 = u2 * u;
                    double b0 = -0.5 * u3 + u2 - 0.5 * u, b1 = 1.5 * u3 - 2.5 * u2 + 1.0, b2 = -1.5 * u3 + 2.0 * u2 + 0.5 * u, b3 = 0.5 * u3 - 0.5 * u2;
                    int i = k * subdiv + sub;
                    e.X[i] = b0 * PX(k - 1) + b1 * cx[k] + b2 * cx[k + 1] + b3 * PX(k + 2);
                    e.Z[i] = b0 * PZ(k - 1) + b1 * cz[k] + b2 * cz[k + 1] + b3 * PZ(k + 2);
                }
            }
            e.X[P] = cx[C]; e.Z[P] = cz[C];
            var arc = Arc(e);
            for (int k = 0; k < C; k++)
            {
                double a0 = arc[k * subdiv], a1 = arc[(k + 1) * subdiv];
                for (int sub = 0; sub < subdiv; sub++)
                {
                    int i = k * subdiv + sub;
                    e.H[i] = ch[k] + (ch[k + 1] - ch[k]) * (float)((arc[i] - a0) / Math.Max(1e-9, a1 - a0));
                }
            }
            e.H[P] = ch[C];
            for (int i = 0; i < P; i++)
                if (Math.Abs(e.H[i + 1] - e.H[i]) / Math.Max(1e-9, arc[i + 1] - arc[i]) > MaxGrade(kind) * 1.0001f) return null;
            Rebox(e);
            return e;
        }

        /// <summary>The line's influence boxes, whole and per Chunk segments: its points padded by the carve's reach
        /// (paved half + shoulder -- per point where the line has its own Shoulders).</summary>
        static void Rebox(Line e)
        {
            int P = e.Segments;
            float Reach(int i) => PavedHalf(e.Kind) + (e.Shoulders != null ? e.Shoulders[i] : Shoulder(e.Kind));
            int chunks = (P + Chunk - 1) / Chunk;
            e.CMinX = new double[chunks]; e.CMaxX = new double[chunks]; e.CMinZ = new double[chunks]; e.CMaxZ = new double[chunks];
            e.MinX = e.MinZ = double.MaxValue; e.MaxX = e.MaxZ = double.MinValue;
            for (int c = 0; c < chunks; c++)
            {
                double mnx = double.MaxValue, mxx = double.MinValue, mnz = double.MaxValue, mxz = double.MinValue, m = 0;
                for (int i = c * Chunk; i <= Math.Min(P, (c + 1) * Chunk); i++)
                {
                    mnx = Math.Min(mnx, e.X[i]); mxx = Math.Max(mxx, e.X[i]); mnz = Math.Min(mnz, e.Z[i]); mxz = Math.Max(mxz, e.Z[i]);
                    m = Math.Max(m, Reach(i));
                }
                e.CMinX[c] = mnx - m; e.CMaxX[c] = mxx + m; e.CMinZ[c] = mnz - m; e.CMaxZ[c] = mxz + m;
                e.MinX = Math.Min(e.MinX, e.CMinX[c]); e.MaxX = Math.Max(e.MaxX, e.CMaxX[c]);
                e.MinZ = Math.Min(e.MinZ, e.CMinZ[c]); e.MaxZ = Math.Max(e.MaxZ, e.CMaxZ[c]);
            }
        }

        internal static double[] Arc(Line e)
        {
            var arc = new double[e.X.Length];
            for (int i = 0; i + 1 < e.X.Length; i++)
                arc[i + 1] = arc[i] + Math.Sqrt((e.X[i + 1] - e.X[i]) * (e.X[i + 1] - e.X[i]) + (e.Z[i + 1] - e.Z[i]) * (e.Z[i + 1] - e.Z[i]));
            return arc;
        }

        /// <summary>Point, profile height and unit tangent at arc length s along a line.</summary>
        internal static void At(Line e, double[] arc, double s, out double x, out double z, out float h, out double tx, out double tz)
        {
            int k = 0, n = e.Segments;
            while (k < n - 1 && arc[k + 1] < s) k++;
            double t = Math.Clamp((s - arc[k]) / Math.Max(1e-9, arc[k + 1] - arc[k]), 0.0, 1.0);
            double sx = e.X[k + 1] - e.X[k], sz = e.Z[k + 1] - e.Z[k], len = Math.Sqrt(sx * sx + sz * sz);
            x = e.X[k] + sx * t; z = e.Z[k] + sz * t; h = e.H[k] + (e.H[k + 1] - e.H[k]) * (float)t;
            tx = sx / len; tz = sz / len;
        }

        // =============================================================================================================
        // Queries

        /// <summary>Every line whose influence box touches the rectangle -- the per-region working set, so the 4,500
        /// height samples of a region each test a handful of lines instead of walking the grid.</summary>
        public List<Line> LinesIn(double x0, double z0, double x1, double z1)
        {
            var list = new List<Line>();
            void Take(Line e) { if (e.Exists && e.MaxX >= x0 && e.MinX <= x1 && e.MaxZ >= z0 && e.MinZ <= z1) list.Add(e); }
            // main links + their branches (branches reach ~1 km off their link: look two cells out)
            long ci0 = (long)Math.Floor(x0 / MainCell) - 2, ci1 = (long)Math.Floor(x1 / MainCell) + 2;
            long cj0 = (long)Math.Floor(z0 / MainCell) - 2, cj1 = (long)Math.Floor(z1 / MainCell) + 2;
            for (long i = ci0; i <= ci1; i++)
                for (long j = cj0; j <= cj1; j++)
                    for (int dir = 0; dir < 2; dir++)
                    {
                        var e = Main(i, j, dir);
                        if (!e.Exists) continue;
                        Take(e);
                        if (e.Branches != null) foreach (var br in e.Branches) Take(br);
                        if (e.Ramps != null) foreach (var rp in e.Ramps) Take(rp);
                    }
            TakeHighways(x0, z0, x1, z1, list);
            TakeRails(x0, z0, x1, z1, list);
            return list;
        }

        /// <summary>Highway lines (both sides of any water gap) whose bounds touch the rectangle.</summary>
        void TakeHighways(double x0, double z0, double x1, double z1, List<Line> list)
        {
            void Take(Line e) { if (e.Exists && e.MaxX >= x0 && e.MinX <= x1 && e.MaxZ >= z0 && e.MinZ <= z1) list.Add(e); }
            // the bands whose route could pass within reach (jitter + meander + anchor pick + DP lane) -- and then, from
            // the band's ACTUAL centre (cheap: a hash), only those whose route can (meander + anchor pick + DP lane).
            // The second test is what keeps a query from building highways two bands away: it cost a cold region 4 s.
            const double reach = 0.25 * HighwayBand + 1500 + 2400 + 1200 + 40, fromCentre = 1500 + 2400 + 1200 + 40;
            for (int axis = 0; axis < 2; axis++)
            {
                double w0 = axis == 0 ? z0 : x0, w1 = axis == 0 ? z1 : x1, u0 = axis == 0 ? x0 : z0, u1 = axis == 0 ? x1 : z1;
                long b0 = (long)Math.Floor((w0 - reach) / HighwayBand), b1 = (long)Math.Floor((w1 + reach) / HighwayBand);
                long k0 = (long)Math.Floor(u0 / HighwaySeg) - 1, k1 = (long)Math.Floor(u1 / HighwaySeg);
                for (long band = b0; band <= b1; band++)
                {
                    double c = BandCentre(axis, band);
                    if (c + fromCentre < w0 || c - fromCentre > w1) continue;
                    for (long k = k0; k <= k1; k++)
                    {
                        var e = Highway(axis, band, k);
                        Take(e);
                        if (e.Branches != null) foreach (var br in e.Branches) Take(br);   // the far side of a water gap
                    }
                }
            }
        }

        /// <summary>The line's profile height at the centreline point nearest (x, z).</summary>
        static float ProfileNear(Line e, double x, double z)
        {
            double best = double.MaxValue; float bh = e.H[0];
            for (int k = 0; k < e.Segments; k++)
            {
                double sx = e.X[k + 1] - e.X[k], sz = e.Z[k + 1] - e.Z[k];
                double qx = x - e.X[k], qz = z - e.Z[k];
                double t = Math.Clamp((qx * sx + qz * sz) / (sx * sx + sz * sz), 0.0, 1.0);
                double ex = qx - sx * t, ez = qz - sz * t, d = ex * ex + ez * ez;
                if (d < best) { best = d; bh = e.H[k] + (e.H[k + 1] - e.H[k]) * (float)t; }
            }
            return bh;
        }

        /// <summary>The strongest road at a point among `lines`, and the clearance to the nearest asphalt of any.</summary>
        public static RoadHit Influence(List<Line> lines, double x, double z)
        {
            var hit = new RoadHit { Clear = float.MaxValue };
            // the decks mains have laid over themselves on highways in this working set (their own decks, not a highway's)
            List<Underpass> ups = null;
            if (Bridges) foreach (var m in lines) if (m.Underpasses != null) foreach (var u in m.Underpasses) if (!u.Existing && !u.Over) (ups ??= new List<Underpass>()).Add(u);
            foreach (var e in lines)
            {
                if (x < e.MinX || x > e.MaxX || z < e.MinZ || z > e.MaxZ) continue;
                float best = float.MaxValue, bh = 0f; int bestK = -1; double bestT = 0; bool bestRight = false;
                for (int c = 0; c < e.CMinX.Length; c++)
                {
                    if (x < e.CMinX[c] || x > e.CMaxX[c] || z < e.CMinZ[c] || z > e.CMaxZ[c]) continue;
                    int end = Math.Min(e.Segments, (c + 1) * Chunk);
                    for (int k = c * Chunk; k < end; k++)
                    {
                        double sx = e.X[k + 1] - e.X[k], sz = e.Z[k + 1] - e.Z[k];
                        double qx = x - e.X[k], qz = z - e.Z[k];
                        double t = Math.Clamp((qx * sx + qz * sz) / (sx * sx + sz * sz), 0.0, 1.0);
                        double ex = qx - sx * t, ez = qz - sz * t;
                        float dist = (float)Math.Sqrt(ex * ex + ez * ez);
                        if (dist < best) { best = dist; bh = e.H[k] + (e.H[k + 1] - e.H[k]) * (float)t; bestK = k; bestT = t; bestRight = -ex * sz + ez * sx >= 0; }
                    }
                }
                if (best == float.MaxValue) continue;
                float half = PavedHalf(e.Kind), sh = e.Shoulders == null ? Shoulder(e.Kind) : e.Shoulders[bestK] + (e.Shoulders[bestK + 1] - e.Shoulders[bestK]) * (float)bestT;
                hit.Clear = Math.Min(hit.Clear, best - half);
                if (best >= half + sh) continue;
                // INSIDE A TUNNEL'S RUN the hill stands: no carve from this road, and the tunnel's own shaping instead
                if (Tunnels && e.TunnelSpans != null && bestK >= 0)
                {
                    double tsx = e.X[bestK + 1] - e.X[bestK], tsz = e.Z[bestK + 1] - e.Z[bestK];
                    double along = e.HArc[bestK] + bestT * Math.Sqrt(tsx * tsx + tsz * tsz);
                    bool bored = false;
                    foreach (var tn in e.TunnelSpans)
                    {
                        // THE FORECOURT: approaching a portal, the cut's flat widens from the road to the bore (plus a
                        // metre), so the bore's lower corners open onto level ground rather than the cut's side slope
                        double outside = along < tn.A0 ? tn.A0 - along : along - tn.A1;
                        if (outside > 0 && outside < TunnelForecourt)
                        {
                            float wide = BoreReachOf(e.Kind) + 1f;
                            if (wide > half) half += (wide - half) * Smoothstep(TunnelForecourt, 0f, (float)outside);
                        }
                        if (along < tn.A0 || along > tn.A1) continue;
                        bored = true;
                        if (!hit.Tunnel)
                        {
                            hit.Tunnel = true;
                            hit.TunnelIn = (float)Math.Min(along - tn.A0, tn.A1 - along);
                            hit.TunnelLat = bestRight ? best : -best;
                            hit.TunnelRoad = TunnelSurfaceY(e.Kind, bh);
                            hit.TunnelKind = e.Kind;
                            hit.Hole = hit.TunnelIn <= TunnelHoleIn && best <= BoreReachOf(e.Kind) + TunnelHoleBeside;
                        }
                        break;
                    }
                    if (bored) continue;
                }
                // UNDER A BRIDGE the carve leaves the ground alone: the deck spans it, piers stand on it. Per carriageway,
                // so a road along a side slope keeps its embankment on the side that is not bridged.
                // The embankment runs AbutmentTuck metres in under each deck end, so the ground grid's last carved vertex
                // is never short of the deck and the approach slab never overhangs a dip.
                if (Bridges && bestK >= 0 && (e.DeckCover != null || ups != null && e.Id != 0))
                {
                    double sx = e.X[bestK + 1] - e.X[bestK], sz = e.Z[bestK + 1] - e.Z[bestK], tuck = AbutmentTuck / Math.Max(1e-6, Math.Sqrt(sx * sx + sz * sz));
                    int si = bestRight ? 1 : 0;
                    if (e.DeckCover != null && Covered(e.DeckCover[si], bestK + bestT, tuck)) continue;
                    // ...and under a main's underpass decks the highway does not fill: the main's cutting runs through
                    bool under = false;
                    if (ups != null && e.Id != 0)
                        foreach (var u in ups)
                            if (u.HwId == e.Id && u.Cover[si] is DeckSpan d && bestK + bestT >= d.F0 + tuck && bestK + bestT <= d.F1 - tuck) { under = true; break; }
                    if (under) continue;
                }
                float w = Smoothstep(half + sh, half, best);
                // strongest carve wins; inside two corridors at once, the one you are deeper inside
                if (!hit.Any || w > hit.Weight + 1e-6f || (w >= hit.Weight - 1e-6f && best - half < hit.Dist - PavedHalf(hit.Kind)))
                {
                    hit.Any = true; hit.Kind = e.Kind; hit.Dist = best; hit.Height = bh; hit.Weight = w;
                }
            }
            return hit;
        }

        public RoadHit Influence(double x, double z) => Influence(LinesIn(x - 1, z - 1, x + 1, z + 1), x, z);

        // =============================================================================================================
        // What a region draws

        /// <summary>Road surface pieces whose midpoint lies in the rectangle, at most `maxPiece` long, with tangents
        /// averaged across bends (so neighbouring pieces close). A highway gives TWO ribbons, one per carriageway.</summary>
        public List<RoadPiece> PiecesIn(List<Line> lines, double x0, double z0, double x1, double z1, float maxPiece)
        {
            var list = new List<RoadPiece>();
            List<Underpass> ups = null;
            if (Bridges) foreach (var m in lines) if (m.Underpasses != null) foreach (var u in m.Underpasses) if (!u.Existing && !u.Over) (ups ??= new List<Underpass>()).Add(u);
            foreach (var e in lines)
            {
                if (e.Kind == RoadKind.Rail) continue;   // track, not a ribbon: RailsIn
                if (e.Kind == RoadKind.Highway) { Ribbon(e, +HighwayRibbonOffset); Ribbon(e, -HighwayRibbonOffset); }   // each carriageway carries its OWN marks
                else Ribbon(e, 0.0);
            }
            return list;

            void Ribbon(Line e, double offset)
            {
                int n = e.Segments;
                // the ribbon's own centreline: the road's, moved sideways along the averaged normal
                var rx = new double[n + 1]; var rz = new double[n + 1];
                var tgx = new float[n + 1]; var tgz = new float[n + 1];
                for (int i = 0; i <= n; i++)
                {
                    var (tx, tz) = RibbonTangent(e, i);
                    tgx[i] = (float)tx; tgz[i] = (float)tz;
                    rx[i] = e.X[i] - tz * offset; rz[i] = e.Z[i] + tx * offset;
                }
                // THE BRIDGE MOUTHS (strawberry 2026-10-10: "widen the width of the actual highway road splines at the mouths
                // of bridges to match up with the bridge's road width"): a carriageway's deck roadway is 1.1 m wider each
                // side than its ribbon, so coming into a deck the ribbon widens to it over MouthTaper -- its arc distance to
                // the nearest deck end along this ribbon, measured where each piece ends
                var mouthCover = !Bridges || offset == 0.0 ? null : CoverWith(e, offset > 0 ? 1 : 0, ups);
                List<double> mouths = null;
                if (mouthCover != null && mouthCover.Count > 0)
                {
                    var ar = new double[n + 1];
                    for (int i = 1; i <= n; i++) ar[i] = ar[i - 1] + Math.Sqrt((rx[i] - rx[i - 1]) * (rx[i] - rx[i - 1]) + (rz[i] - rz[i - 1]) * (rz[i] - rz[i - 1]));
                    double ArcAt(double f) { int fk = Math.Clamp((int)f, 0, n - 1); return ar[fk] + (ar[fk + 1] - ar[fk]) * Math.Clamp(f - fk, 0, 1); }
                    mouths = new List<double>();
                    foreach (var c in mouthCover) { mouths.Add(ArcAt(c.F0)); mouths.Add(ArcAt(c.F1)); }
                }
                float Widen(double at)
                {
                    if (mouths == null) return 0f;
                    double best = double.MaxValue;
                    foreach (var m in mouths) best = Math.Min(best, Math.Abs(at - m));
                    return MouthWiden * Smoothstep(MouthTaper, 0f, (float)best);
                }
                float s = 0f;
                for (int k = 0; k < n; k++)
                {
                    double sx = rx[k + 1] - rx[k], sz = rz[k + 1] - rz[k];
                    float segLen = (float)Math.Sqrt(sx * sx + sz * sz);
                    if (segLen < 1e-4f) continue;
                    float dX = (float)(sx / segLen), dZ = (float)(sz / segLen);
                    // the parts of this segment NOT under a bridge deck (all of it, almost always), each cut into pieces
                    var cover = !Bridges ? null : offset != 0.0 ? CoverWith(e, offset > 0 ? 1 : 0, ups) : e.DeckCover?[0];
                    foreach (var (u0, u1, sq0, sq1) in Uncovered(cover, k))
                    {
                    int pcs = Math.Max(1, (int)Math.Ceiling(segLen * (u1 - u0) / maxPiece));
                    for (int p = 0; p < pcs; p++)
                    {
                        double ta = u0 + (u1 - u0) * p / pcs, tb = u0 + (u1 - u0) * (p + 1) / pcs;
                        double mx = rx[k] + sx * (ta + tb) * 0.5, mz = rz[k] + sz * (ta + tb) * 0.5;
                        if (mx < x0 || mx >= x1 || mz < z0 || mz >= z1) continue;
                        list.Add(new RoadPiece
                        {
                            Kind = (byte)e.Kind,
                            X0 = rx[k] + sx * ta, Z0 = rz[k] + sz * ta, X1 = rx[k] + sx * tb, Z1 = rz[k] + sz * tb,
                            H0 = e.H[k] + (e.H[k + 1] - e.H[k]) * (float)ta, H1 = e.H[k] + (e.H[k + 1] - e.H[k]) * (float)tb,
                            S0 = s + segLen * (float)ta, S1 = s + segLen * (float)tb,
                            // a piece end that meets a deck is squared to the DECK's heading (see DeckSpan); ends at a
                            // profile point take the averaged tangent the neighbouring piece also takes; the rest, the
                            // segment's own
                            T0X = p == 0 && sq0.HasValue ? sq0.Value.x : ta == 0 ? tgx[k] : dX, T0Z = p == 0 && sq0.HasValue ? sq0.Value.z : ta == 0 ? tgz[k] : dZ,
                            T1X = p == pcs - 1 && sq1.HasValue ? sq1.Value.x : tb == 1 ? tgx[k + 1] : dX, T1Z = p == pcs - 1 && sq1.HasValue ? sq1.Value.z : tb == 1 ? tgz[k + 1] : dZ,
                            Raised = offset != 0.0 && e.RaisedSeg != null && e.RaisedSeg[offset > 0 ? 1 : 0][k],
                            InTunnel = InTunnel(e, k, (ta + tb) * 0.5),
                            Cut = offset != 0.0 && e.CutSeg != null && e.CutSeg[offset > 0 ? 1 : 0][k],
                            OpenStart = k == 0 && ta == 0, OpenEnd = k == n - 1 && tb == 1,
                            Ramp = e.Ramp,
                            W0 = Widen(s + segLen * ta), W1 = Widen(s + segLen * tb),
                        });
                    }
                    }
                    s += segLen;
                }
            }
        }

        public const float PoleSpacing = 40f, PoleSetback = 3f, PoleEndClear = 24f;
        /// <summary>How far short of a grade separation (measured along the main from where it crosses) a power line
        /// stops: past the longest deck either way (a skewed overpass, ~31 m) and the abutment's slope.</summary>
        public const double PoleCrossingGap = 45.0;

        /// <summary>Does this class carry power lines? Main and small roads do; highways and dirt trails do not
        /// (strawberry 2026-10-09: highways "dont have power lines", "dirt trails dont get power lines either").</summary>
        public static bool HasPowerLines(RoadKind k) => k == RoadKind.Main || k == RoadKind.Small;

        /// <summary>Power-line poles along main and small roads (see HasPowerLines), right-hand side, every
        /// PoleSpacing by arc length, clear of both towns; a pole that would stand on another road is skipped and the
        /// wire jumps to the next one. Each pole carries the next for the span that may leave the region.</summary>
        public List<PolePlacement> PolesIn(List<Line> lines, double x0, double z0, double x1, double z1)
        {
            var list = new List<PolePlacement>();
            foreach (var e in lines)
            {
                if (!HasPowerLines(e.Kind) || e.Ramp) continue;
                double off = PavedHalf(e.Kind) + PoleSetback;
                var arc = Arc(e);
                double total = arc[arc.Length - 1];
                int count = (int)Math.Floor((total - 2 * PoleEndClear) / PoleSpacing) + 1;
                if (count < 2) continue;
                void Spot(int q, out double px, out double pz, out float dx, out float dz)
                {
                    At(e, arc, PoleEndClear + q * PoleSpacing, out double cx, out double cz, out _, out double tx, out double tz);
                    dx = (float)tx; dz = (float)tz;
                    px = cx - tz * off; pz = cz + tx * off;
                }
                // NO POLE OR WIRE THROUGH A GRADE SEPARATION: a pole stands at ground level beside the road, so near one it
                // would be in the cutting under the highway's deck or down beside the main's own embankment, and its wire
                // would run through a deck. The line stops PoleCrossingGap short of each crossing and starts again after.
                List<double> gaps = null;
                if (e.Underpasses != null)
                    foreach (var u in e.Underpasses)
                    {
                        double bd = double.MaxValue, ba = 0;
                        for (int i = 0; i < e.X.Length; i++)
                        {
                            double dd = (e.X[i] - u.X) * (e.X[i] - u.X) + (e.Z[i] - u.Z) * (e.Z[i] - u.Z);
                            if (dd < bd) { bd = dd; ba = arc[i]; }
                        }
                        (gaps ??= new List<double>()).Add(ba);
                    }
                // ...nor across a railway: a level crossing is a gap like a grade separation
                if (e.LevelCrossings != null)
                    foreach (var c in e.LevelCrossings)
                    {
                        double bd = double.MaxValue, ba = 0;
                        for (int i = 0; i < e.X.Length; i++)
                        {
                            double dd = (e.X[i] - c.X) * (e.X[i] - c.X) + (e.Z[i] - c.Z) * (e.Z[i] - c.Z);
                            if (dd < bd) { bd = dd; ba = arc[i]; }
                        }
                        (gaps ??= new List<double>()).Add(ba);
                    }
                bool InGap(double a0, double a1)
                {
                    if (gaps != null) foreach (var g in gaps) if (a1 >= g - PoleCrossingGap && a0 <= g + PoleCrossingGap) return true;
                    return false;
                }
                bool Stands(int q)
                {
                    if (InGap(PoleEndClear + q * PoleSpacing, PoleEndClear + q * PoleSpacing)) return false;
                    Spot(q, out double sx, out double sz, out _, out _);
                    return Influence(sx, sz).Clear > 1.5f;
                }
                for (int q = 0; q < count; q++)
                {
                    Spot(q, out double px, out double pz, out float dx, out float dz);
                    if (px < x0 || px >= x1 || pz < z0 || pz >= z1) continue;
                    if (!Stands(q)) continue;
                    var pp = new PolePlacement { X = px, Z = pz, H = _t.HeightAt(px, pz), DirX = dx, DirZ = dz };
                    for (int nq = q + 1; nq < count && nq <= q + 2; nq++)
                    {
                        if (InGap(PoleEndClear + q * PoleSpacing, PoleEndClear + nq * PoleSpacing)) break;   // no wire across it
                        if (!Stands(nq)) continue;
                        Spot(nq, out double nx, out double nz, out float ndx, out float ndz);
                        pp.HasNext = true; pp.NX = nx; pp.NZ = nz; pp.NH = _t.HeightAt(nx, nz); pp.NDirX = ndx; pp.NDirZ = ndz;
                        break;
                    }
                    list.Add(pp);
                }
            }
            return list;
        }

        // ---- for tests and tools
        static (double, double, float)[] Pts(Line e)
        {
            if (!e.Exists) return null;
            var r = new (double, double, float)[e.X.Length];
            for (int i = 0; i < r.Length; i++) r[i] = (e.X[i], e.Z[i], e.H[i]);
            return r;
        }
        public (double x, double z, float h)[] MainCentreline(long cx, long cz, int dir) => Pts(Main(cx, cz, dir));
        public void MainNodeAt(long cx, long cz, out double x, out double z) => MainNode(cx, cz, out x, out z);
        public IReadOnlyList<Underpass> MainUnderpasses(long cx, long cz, int dir) => Main(cx, cz, dir).Underpasses ?? (IReadOnlyList<Underpass>)Array.Empty<Underpass>();
        public List<(double x, double z, float h)[]> MainRamps(long cx, long cz, int dir)
        {
            var r = new List<(double x, double z, float h)[]>();
            var m = Main(cx, cz, dir);
            if (m.Exists && m.Ramps != null) foreach (var rp in m.Ramps) r.Add(Pts(rp));
            return r;
        }
        public (double x, double z, float h)[] HighwayCentreline(int axis, long band, long k) => Pts(Highway(axis, band, k));
        /// <summary>Test/tool accessor: the raised stretches of a highway segment (and of the far side of a water gap).</summary>
        public List<(Stretch r, (double x, double z, float h)[] pts)> RaisedOf(int axis, long band, long k) => StretchesOf(axis, band, k, false);
        /// <summary>Test/tool accessor: the cut (tunnel-candidate) stretches of a highway segment.</summary>
        public List<(Stretch r, (double x, double z, float h)[] pts)> CutOf(int axis, long band, long k) => StretchesOf(axis, band, k, true);
        List<(Stretch r, (double x, double z, float h)[] pts)> StretchesOf(int axis, long band, long k, bool cut)
        {
            var e = Highway(axis, band, k);
            var r = new List<(Stretch, (double, double, float)[])>();
            void Add(Line l)
            {
                var list = cut ? l.Cut : l.Raised;
                if (!l.Exists || list == null) return;
                foreach (var s in list) r.Add((s, CarriagewayPts(l, s.Side, s.I0, s.I1)));
            }
            Add(e);
            if (e.Branches != null) foreach (var b in e.Branches) Add(b);
            return r;
        }
        /// <summary>The centreline of one carriageway (side -1/+1) over dense points i0..i1, at the road's height.</summary>
        static (double x, double z, float h)[] CarriagewayPts(Line e, int side, int i0, int i1)
        {
            var pts = new (double, double, float)[i1 - i0 + 1];
            for (int i = i0; i <= i1; i++)
            {
                var (tx, tz) = RibbonTangent(e, i);
                pts[i - i0] = (e.X[i] - tz * side * HighwayRibbonOffset, e.Z[i] + tx * side * HighwayRibbonOffset, e.H[i]);
            }
            return pts;
        }

        /// <summary>The heading at dense point i that a carriageway is offset along: the mean of the two adjoining
        /// segments' UNIT directions. ⚠ ONE definition for the ribbon AND the bridge walk. They used to differ (the
        /// walk took the central difference, which leans toward the longer neighbour), and a deck laid on one polyline
        /// meeting a ribbon built on the other is a lateral step at every bridge end.</summary>
        static (double tx, double tz) RibbonTangent(Line e, int i)
        {
            int n = e.Segments, a = Math.Max(0, i - 1), b = Math.Min(n - 1, i);
            double ax = e.X[a + 1] - e.X[a], az = e.Z[a + 1] - e.Z[a], al = Math.Sqrt(ax * ax + az * az);
            double bx = e.X[b + 1] - e.X[b], bz = e.Z[b + 1] - e.Z[b], bl = Math.Sqrt(bx * bx + bz * bz);
            double mx = ax / al + bx / bl, mz = az / al + bz / bl, ml = Math.Sqrt(mx * mx + mz * mz);
            return (mx / ml, mz / ml);
        }
        /// <summary>Test accessor: the bridge pieces of a highway segment (and of the far side of a water gap), in walk
        /// order -- each bridge's decks and piers, then its two caps.</summary>
        public List<BridgePiece> BridgesOf(int axis, long band, long k)
        {
            var e = Highway(axis, band, k);
            var r = new List<BridgePiece>();
            if (e.Exists && e.BridgePieces != null) r.AddRange(e.BridgePieces);
            if (e.Branches != null) foreach (var b in e.Branches) if (b.BridgePieces != null) r.AddRange(b.BridgePieces);
            return r;
        }
        /// <summary>Test accessor: the tunnels of a highway segment (and of its shore spurs).</summary>
        public List<TunnelSpan> TunnelsOf(int axis, long band, long k)
        {
            var e = Highway(axis, band, k);
            var r = new List<TunnelSpan>();
            if (e.Exists && e.TunnelSpans != null) r.AddRange(e.TunnelSpans);
            if (e.Branches != null) foreach (var b in e.Branches) if (b.TunnelSpans != null) r.AddRange(b.TunnelSpans);
            return r;
        }
        /// <summary>Test accessor: one carriageway of a highway segment, as a centreline.</summary>
        public (double x, double z, float h)[] HighwayCarriageway(int axis, long band, long k, int side)
        {
            var e = Highway(axis, band, k);
            return e.Exists ? CarriagewayPts(e, side, 0, e.X.Length - 1) : null;
        }

        public List<(RoadKind kind, (double x, double z, float h)[] pts)> BranchesOf(long cx, long cz, int dir)
        {
            var e = Main(cx, cz, dir);
            var r = new List<(RoadKind, (double, double, float)[])>();
            if (e.Exists && e.Branches != null) foreach (var b in e.Branches) r.Add((b.Kind, Pts(b)));
            return r;
        }
    }
}
