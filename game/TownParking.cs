using Godot;
using System;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // ==============================================================================================
    // PARKING LOTS (strawberry 2026-10-14: "add parking lots, large areas of the road surface with the "line parking"
    // decals (from vanilla) turned white, with block parking props as parking blocks. a building can specifically request
    // a parking lot of any size and the game should solve it. roads should branch into them with a tiny connecting road
    // (keeping the sidewalks), can have multiple entrances.")
    // ==============================================================================================
    public sealed partial class TownRoadGrid
    {
        /// <summary>UG_TOWNNOLOTS=1: no parking lots -- the control.</summary>
        public bool ParkingLots = System.Environment.GetEnvironmentVariable("UG_TOWNNOLOTS") != "1";

        /// <summary>⭐ RETAIL'S STALL, MEASURED OFF ITS PROPS. Line_Parking_0 is four 0.5 x 5 m stripes 4 m apart (three stalls
        /// between its outer ones), and PEI lays them end to end 16 m apart so the stripes keep one 4 m rhythm down a row;
        /// Block_Parking_0 is a 3 x 0.5 x 0.5 m block, one per stall, at its head. So a stall is 4 m wide and 5 m deep, the
        /// stripes 0.5 m. (The game's cars are 3 m across their tyres -- see BayDepth -- which is why it is a generous 4.)</summary>
        public const float StallWidth = 4f, StallDepth = 5f, StallStripe = 0.5f;
        /// <summary>⭐ EVERY ROAD IN A LOT IS TWO LANES (strawberry 2026-10-14: "make the internal 'road' of the parking lot two
        /// lanes"): an aisle between rows of stalls, and the cross road along the front that ties a lot's aisles together,
        /// both this wide -- a lane each way, as the way in is. (The aisles were tied together by a 4 m apron: one lane.)</summary>
        public float LotAisle = 7f;
        /// <summary>From a row's end stripe (its middle) to the kerb or the road the row ends at: the stripe and half a metre of
        /// asphalt, room for a car door and no more.</summary>
        public float RowEndGap = 0.75f;
        /// <summary>⭐ A LOT HAS ITS OWN KERB (strawberry 2026-10-14: "the parking lot needs a kerb/sidewalk"): a pavement this
        /// wide round the inside of its edge, open only where its ways in come through.</summary>
        public float LotKerbWidth = 1.5f;
        /// <summary>⚠ NO PARKING BLOCKS (strawberry 2026-10-14: "kill blocks"). In the US and Canada a wheel stop goes only where
        /// a car's nose would overhang something it must not -- a narrow sidewalk, a building, a property line -- and they
        /// are a trip hazard and the snowploughs' enemy everywhere else; a lot's kerb stops a tyre itself. So the stalls run
        /// right up to the kerb and the strip the blocks stood in is gone. UG_TOWNLOTBLOCKS=1 puts them back, with that strip
        /// -- retail's: the block 0.4 m past the end of its stripes, PEI's Line_Parking_0 / Block_Parking_0 measured -- at the
        /// heads that face the lot's kerb only, never where two rows meet nose to nose.</summary>
        public bool ParkingBlocks = System.Environment.GetEnvironmentVariable("UG_TOWNLOTBLOCKS") == "1";
        public float LotHeadStrip = 0.9f, BlockPast = 0.4f;
        /// <summary>The strip between a row's heads and the kerb they face: the blocks', when there are blocks.</summary>
        float HeadStrip => ParkingBlocks ? LotHeadStrip : 0f;
        /// <summary>⭐ THE LINES STAND OFF THE KERB AND THE AISLE (strawberry 2026-10-14: "have the parking lines be spaced slightly
        /// away from sidewalk and the internal road lanes"): each stall's stripes stop this far short of its head's kerb and
        /// of the road's edge at its open end.</summary>
        public float StripeGap = 0.35f;
        /// <summary>⭐ A FULL PAVEMENT ON THE SIDE THAT MEETS THE BUSINESS (strawberry 2026-10-14: "parking lots should get full
        /// sidewalk thickness on the side that connects to a business"): the lot's kerb along the building that asked for it
        /// is the streets' pavement wide (PavementWidth), not LotKerbWidth -- the way from the car to the door.</summary>
        float BizExtra => Mathf.Max(0f, PavementWidth - LotKerbWidth);
        /// <summary>Which side of a lot (-1 its left, +1 its right, along Along) its building stands on; 0 if it has none.</summary>
        static int BizSide(Plot p, Plot forPlot) => forPlot == null ? 0 : (forPlot.Front - p.Front).Dot(p.Along) < 0f ? -1 : 1;
        /// <summary>A lot's kerb width on its left (-1) or right (+1) side.</summary>
        float SideKerb(Plot p, Plot forPlot, int side) => BizSide(p, forPlot) == side ? LotKerbWidth + BizExtra : LotKerbWidth;
        /// <summary>A lot's inside, across: its left and right kerb lines (lot frame).</summary>
        (float xL, float xR) InnerX(Plot p, Plot forPlot) => (-p.Width * 0.5f + SideKerb(p, forPlot, -1), p.Width * 0.5f - SideKerb(p, forPlot, 1));
        /// <summary>⭐ A SIDEWALK BETWEEN BACK-TO-BACK ROWS (strawberry 2026-10-14: "add sidewalk dividers between back to back
        /// parking lines"): where two rows meet nose to nose, a raised island this wide runs between their heads, from the
        /// cross road to the back kerb -- the lot's own pavement, kerbed both sides, a rounded nose at the road.</summary>
        public float LotDividerWidth = 1.5f;
        /// <summary>⭐ A LOT FILLS THE ROOM IT HAS (strawberry 2026-10-14: "parking lots should try fill the space they reserved
        /// (within reason)"): once the town's plots have taken their yards, each lot stretches over what is left beside and
        /// behind it -- at most this far a side and this far back -- is laid again for the most stalls that holds, and keeps
        /// up to LotSliver of what it does not use (a strip too narrow for anything else) rather than leave it as grass.</summary>
        public float LotStretchSide = 8f, LotStretchBack = 12f, LotSliver = 4f;
        /// <summary>⭐ STREET LIGHTS IN A LOT (strawberry 2026-10-14: "add streetlight support for parking lots too"): the
        /// street's own lamp, along its dividers, else along the kerbs its rows face, about this far apart.</summary>
        public float LotLampSpacing = 18f;
        /// <summary>⭐ A TWO-LANE WAY IN (strawberry 2026-10-14: "2 lane road going in"): its carriageway this wide a side -- a
        /// lane in and a lane out, as wide as the aisle it leads to -- not a back lane's single track.</summary>
        public float ParkingEntranceHalf = 3.5f;
        /// <summary>⚠ HOW FAR CLEAR OF A JUNCTION'S CORNER A WAY IN OPENS (m, past where the street's pavement starts): the
        /// corner is the crossing's and the signal mast's, and a 7 m mouth opened 2.6 m from one took the mast's spot --
        /// PlaceLights pushed it clear of the way in's carriageway, which put it in the lot. A lot whose every way in is
        /// that close keeps the one nearest its middle anyway: a lot with no way in is worse.</summary>
        public float ParkingCornerClear = 5f;

        /// <summary>The ways in's material: the street's plain tarmac, ParkingEntranceHalf wide. -1: none.</summary>
        public int ParkingEntranceMaterial = -1;
        /// <summary>What the town's buildings ask for when nothing else says: an apartment block its residents' spaces, and
        /// a shop -- some shops -- its customers'.</summary>
        public int ApartmentParking = AskEnv(0, 14), ShopParking = AskEnv(1, 8);
        /// <summary>UG_TOWNLOTASK=<apartments>[,<shops>]: what they ask for instead -- to see the bigger plans laid.</summary>
        static int AskEnv(int i, int dflt)
        {
            var parts = (System.Environment.GetEnvironmentVariable("UG_TOWNLOTASK") ?? "").Split(',');
            return i < parts.Length && int.TryParse(parts[i], out int v) && v >= 0 ? v : dflt;
        }
        public float ShopParkingChance = 0.4f;
        /// <summary>⭐ A BUILDING'S OWN REQUEST: how many spaces the building on this plot wants (0: none). Whatever builds on
        /// the plots sets it; null asks the defaults above. Any size: the lot is solved to fit it.</summary>
        public Func<Plot, int> ParkingWanted;
        /// <summary>The spaces this plot's building asks for.</summary>
        public int ParkingFor(Plot pl)
        {
            if (pl == null || pl.Kind == PlotKind.Parking || pl.Outline != null) return 0;
            if (ParkingWanted != null) return Math.Max(0, ParkingWanted(pl));
            if (pl.Kind == PlotKind.Apartments) return ApartmentParking;
            if (pl.Kind == PlotKind.Shop)
            {
                ulong h = Mix(((ulong)(uint)Seed << 32) ^ Mix(((ulong)(uint)pl.Road << 20) ^ (ulong)(uint)Mathf.RoundToInt(pl.Arc * 10f) ^ 0x5A0BUL));
                return Unit(h, 8) < ShopParkingChance ? ShopParking : 0;
            }
            return 0;
        }

        /// <summary>A parking lot: the plot it is laid on (kind Parking), the plot whose building asked for it, the spaces asked
        /// and laid, its plan, and its layout -- each stall (its middle, the way across it, the way in from its aisle to its
        /// head), each stripe (its centre line), each of its two-lane roads (in the lot's own frame: x along the street from
        /// the front's middle, y back from the front -- x0, y0, x1, y1), each way in from the street.</summary>
        public sealed class ParkingLot
        {
            public Plot Lot, For; public int Wanted, Spaces; public LotPlan Plan;
            public readonly List<(float x0, float y0, float x1, float y1)> Roads = new();
            /// <summary>Its sidewalk dividers between back-to-back rows, in its own frame: x0..x1 across, y0 (the nose, at the
            /// cross road) to y1 (the back kerb).</summary>
            public readonly List<(float x0, float y0, float x1, float y1)> Dividers = new();
            /// <summary>Its ways out other than through the front: the edge, and where along it (lot frame) -- each one cuts the
            /// row it comes through (LayOutLot).</summary>
            public readonly List<(int edge, float at)> Exits = new();
            public readonly List<(Vector3 at, Vector3 across, Vector3 into)> Stalls = new();
            public readonly List<(Vector3 a, Vector3 b)> Stripes = new();
            /// <summary>Each block: its middle, and the way its 3 m length runs (across its stall).</summary>
            public readonly List<(Vector3 at, Vector3 along)> Blocks = new();
            public readonly List<ParkingEntrance> Entrances = new();
            /// <summary>Its street's asphalt height along its front, a metre apart from FrontU0 (m along the street from the
            /// front's middle) -- BuildParkingLots.</summary>
            public float[] FrontY; public float FrontU0;
            public float FrontAt(float u)
            {
                if (FrontY == null || FrontY.Length == 0) return float.NaN;
                if (FrontY.Length == 1) return FrontY[0];
                float f = Mathf.Clamp(u - FrontU0, 0f, FrontY.Length - 1);
                int i = Mathf.Min((int)f, FrontY.Length - 2);
                return Mathf.Lerp(FrontY[i], FrontY[i + 1], f - i);
            }
            /// <summary>Graded (TownParking.GradeLots): the ground's line along its back edge, BackA + BackB u -- its surface
            /// runs straight from its street's height at the front kerb to that.</summary>
            public bool Graded; public float BackA, BackB;
            public float BackAt(float u) => BackA + BackB * u;
        }
        /// <summary>A way into a parking lot from its street (Road, Side; From..To the stretch of the street's pavement it takes,
        /// arc metres), Mouth where it meets the lot's front edge, Into the way into the lot.</summary>
        public sealed class ParkingEntrance
        {
            public int Road, Side; public float From, To; public Vector3 Mouth, Into; public float Width, TopY; public ParkingLot Lot;
            /// <summary>Which of the lot's edges it comes through: 0 its front, 1 its right (+Along), 2 its back, 3 its left.</summary>
            public int Edge;
            /// <summary>⭐ THE WAY IN IS A ROAD (strawberry 2026-10-14: "a real road connection, not just a dipped kerb"): a short
            /// two-lane road from the street to the lot -- its mouth cut through the street's pavement with a kerb round each
            /// corner, the lot's kerb curving round to meet those -- built by BuildLanes like a back lane, its far end open
            /// onto the lot. TopY: its end's asphalt as built (the probe holds it to the lot's front).</summary>
            public LanePlan Lane;
        }
        /// <summary>What the last PlanPlots laid.</summary>
        public readonly List<ParkingLot> ParkingLotsLaid = new();
        public int ParkingAsked, ParkingSpacesAsked, ParkingSpacesLaid, ParkingRefused, ParkingCornersTaken;
        public int ParkingWaysIn { get { int n = 0; foreach (var l in ParkingLotsLaid) n += l.Entrances.Count; return n; } }

        /// <summary>⭐ A LOT'S PLAN (strawberry 2026-10-14: "optimize the layout for the most accessable spaces as it can" --
        /// "yes drive to"): every stall on a two-lane road, and as many as the lot holds. Stalls square to a two-way aisle,
        /// a row down each side of it, is the densest a lot is laid (90 degrees, double-loaded: about 26 m2 a stall here), so
        /// both arrangements are that: ACROSS -- aisles running back from the street side by side, their rows nose to nose
        /// between them, two or more tied together by a cross road along the front (and, if it pays, a row along the front
        /// kerb facing that road); ALONG -- one aisle along the street, a row behind it and, if it pays, one in front of it,
        /// cut where the way in comes through. PerRow: the stalls in each across row, or in the along lot's back row.</summary>
        public sealed class LotPlan
        {
            public bool Along, FrontRow; public int Aisles = 1, PerRow; public float Width, Depth; public int Stalls;
            public override string ToString() => Along
                ? $"one aisle along the street, {(FrontRow ? "a row each side" : "a row behind it")}"
                : $"{Aisles} aisle{(Aisles > 1 ? "s" : "")} across{(Aisles > 1 ? ", a cross road along the front" : "")}{(FrontRow ? " and a row facing it" : "")}";
        }

        /// <summary>How far along its street (m, each way) a building and its lot may slide to find room for both.</summary>
        public float ParkingSlide = 60f;
        /// <summary>The lot's front kerb left between its corner and an along lot's way in, where that comes in at an end.</summary>
        public float WayInEndStub = 1f;
        Dictionary<int, List<LotPlan>> _lotPlans;

        /// <summary>The width an across plan's aisles take: each a row, the aisle, a row; a divider between each two (their rows
        /// meet nose to nose there); a head strip at each end.</summary>
        float AcrossUsed(int k) => 2f * HeadStrip + k * (2f * StallDepth + LotAisle) + (k - 1) * LotDividerWidth;
        float AcrossPitch => 2f * StallDepth + LotAisle + LotDividerWidth;
        /// <summary>Where an across lot's first row's heads are (lot frame x): its aisles centred in whatever width it has.</summary>
        float AcrossX0(LotPlan plan, float xL, float xR) => xL + HeadStrip + Mathf.Max(0f, (xR - xL - AcrossUsed(plan.Aisles)) * 0.5f);

        /// <summary>⭐ EVERY PLAN THAT HOLDS `n` IN A LOT NO DEEPER THAN `maxDepth`, the smallest first (and of two the same size,
        /// the one holding more): across with 1 to 5 aisles, a row along the front or not, each the shallowest that holds
        /// n; along, a row in front or not, each the narrowest. Their stalls counted the way they will be laid, ways in
        /// where they cut the fewest (where they come in is only known once the lot is placed -- ParkingLotBeside).</summary>
        List<LotPlan> LotPlansFor(int n, float maxDepth)
        {
            _lotPlans ??= new Dictionary<int, List<LotPlan>>();
            if (_lotPlans.TryGetValue(n, out var cached)) return cached;
            var list = new List<LotPlan>();
            float Wk = LotKerbWidth, H = HeadStrip, Sd = StallDepth, Sw = StallWidth, A = LotAisle;
            for (int k = 1; k <= 5 && n >= 1; k++)
                foreach (bool front in k >= 2 ? new[] { false, true } : new[] { false })
                {
                    var plan = new LotPlan { Aisles = k, FrontRow = front, Width = 2f * Wk + BizExtra + AcrossUsed(k) };
                    float cross = k >= 2 ? (front ? H + Sd : 0f) + A : 0f;
                    // the rows hold the rest of what the front row does not (counted with no rows behind it)
                    plan.PerRow = 0; plan.Depth = 2f * Wk + cross + 2f * RowEndGap;
                    int frontStalls = front ? CountStalls(plan) : 0;
                    plan.PerRow = Mathf.Max(1, Mathf.CeilToInt((n - frontStalls) / (2f * k)));
                    plan.Depth = 2f * Wk + cross + 2f * RowEndGap + plan.PerRow * Sw;
                    if (plan.Depth > maxDepth + 1e-3f) continue;
                    plan.Stalls = CountStalls(plan);
                    if (plan.Stalls >= n) list.Add(plan);
                }
            foreach (bool front in new[] { true, false })
            {
                float depth = 2f * Wk + (front ? H + Sd : 0f) + A + Sd + H;
                if (depth > maxDepth + 1e-3f) continue;
                // (wide enough for its way in at an end: the stub, the kerb's curve, the way in, the curve, the stub)
                float minInner = 2f * (WayInEndStub + Mathf.Max(LaneKerbRadius, LaneWalkWidth)) + 2f * ParkingEntranceHalf;
                for (int per = 1; per <= 80; per++)
                {
                    var plan = new LotPlan { Along = true, FrontRow = front, PerRow = per, Depth = depth, Width = 2f * Wk + BizExtra + per * Sw + 2f * RowEndGap };
                    if (plan.Width - 2f * Wk - BizExtra < minInner) continue;
                    plan.Stalls = CountStalls(plan);
                    if (plan.Stalls >= n) { list.Add(plan); break; }
                }
            }
            list.Sort((x, y) =>
            {
                int c = (x.Width * x.Depth).CompareTo(y.Width * y.Depth);
                return c != 0 ? c : y.Stalls.CompareTo(x.Stalls);
            });
            _lotPlans[n] = list;
            return list;
        }

        /// <summary>The stalls a plan holds, laid on a plot of its size by itself, its ways in where it would prefer them.</summary>
        int CountStalls(LotPlan plan)
        {
            var p = new Plot { Kind = PlotKind.Parking, Width = plan.Width, Depth = plan.Depth, Along = Vector3.Right, Facing = Vector3.Back, Road = -1, Arc = -1f };
            var biz = new Plot { Front = p.Front - p.Along * p.Width };            // (its building beside it, as every lot has)
            var lot = new ParkingLot { Lot = p, Plan = plan, For = biz };
            LayOutLot(lot, plan, ChooseWaysIn(null, plan, p, biz));
            return lot.Spaces;
        }

        /// <summary>Where a way in may come into a lot (m along its front from the middle), best first: an across lot's at its
        /// aisles, the middle ones first; an along lot's at an end -- the end away from the building first, so it is driven
        /// past the stalls to them -- or else the middle.</summary>
        List<float> WayInSpots(LotPlan plan, Plot p, Plot forPlot)
        {
            var spots = new List<float>();
            var (xL, xR) = InnerX(p, forPlot);
            if (plan.Along)
            {
                float inset = WayInEndStub + Mathf.Max(LaneKerbRadius, LaneWalkWidth) + ParkingEntranceHalf + PavementGap;
                bool forOnLeft = forPlot != null && (forPlot.Front - p.Front).Dot(p.Along) < 0f;
                spots.Add(forOnLeft ? xR - inset : xL + inset);
                spots.Add(forOnLeft ? xL + inset : xR - inset);
                spots.Add(0f);
                return spots;
            }
            float x0 = AcrossX0(plan, xL, xR);
            for (int i = 0; i < plan.Aisles; i++) spots.Add(x0 + StallDepth + LotAisle * 0.5f + i * AcrossPitch);
            spots.Sort((u, v) => Mathf.Abs(u).CompareTo(Mathf.Abs(v)));
            return spots;
        }

        /// <summary>The ways in a lot placed here gets: as many as it wants (one; two for four aisles or more, the cross road
        /// tying the rest together) of its spots, best first, that can open (WayInOpens; with no street, every spot can).</summary>
        List<float> ChooseWaysIn(RoadField roads, LotPlan plan, Plot p, Plot forPlot)
        {
            int want = !plan.Along && plan.Aisles >= 4 ? 2 : 1;
            var ways = new List<float>();
            foreach (float xe in WayInSpots(plan, p, forPlot))
            {
                if (ways.Count >= want) break;
                if (roads == null || p.Road < 0 || WayInOpens(roads, p, xe)) ways.Add(xe);
            }
            return ways;
        }

        /// <summary>⚠ WHETHER A WAY IN AT `xe` (m along the lot's front from its middle) COULD OPEN -- OpenMouth's own test,
        /// ahead of time: its mouth on the street's pavement, clear of the junctions' corners by ParkingCornerClear. A two-lane
        /// way in opens 9 m of the street's pavement, and a lot beside a corner (its plot clear of the corner's pavement, its
        /// middle not) had none that could -- a lot with no way in. The solve moves on until one can.</summary>
        bool WayInOpens(RoadField roads, Plot lotPlot, float xe)
        {
            int r = lotPlot.Road;
            if (lotPlot.Arc < 0f || r < 0) return true;                // (nothing to measure along: OpenMouth decides)
            float L = roads.RoadLength(r);
            if (!roads.EvaluateAlong(r, Mathf.Clamp(lotPlot.Arc, 0f, L), out _, out var tan, snapTerrain: false)) return true;
            float sgn = new Vector3(tan.X, 0f, tan.Z).Dot(lotPlot.Along) >= 0f ? 1f : -1f;
            float lo = TrimFor(roads, r, false, CornerRadius) + 0.25f + ParkingCornerClear;
            float hi = L - TrimFor(roads, r, true, CornerRadius) - 0.25f - ParkingCornerClear;
            // (each corner's kerb leaves the street's the fillet's tangent length past the way in's kerb line -- or the
            // return's width, whichever is more: MouthCorner's sA, square on)
            float reach = ParkingEntranceHalf + PavementGap + Mathf.Max(LaneKerbRadius, LaneWalkWidth);
            float a = lotPlot.Arc + sgn * xe;
            return a - reach >= lo && a + reach <= hi;
        }

        /// <summary>⭐ THE LOT `n` SPACES WANT BESIDE `forPlot` on its frontage (`dir` -1 its left, +1 its right), as deep as the
        /// street's deepest plots at most: the smallest plan that fits there, has a way in that can open, and laid that way
        /// still holds n. Null if none does.</summary>
        ParkingLot ParkingLotBeside(RoadField roads, Plot forPlot, int n, int dir)
        {
            float deepest = 0f; foreach (var ps in PlotSizes) if (ps.Kind != PlotKind.Parking) deepest = Mathf.Max(deepest, ps.Depth);
            foreach (var plan in LotPlansFor(n, deepest))
            {
                var pl = new Plot
                {
                    Kind = PlotKind.Parking, Width = plan.Width, Depth = plan.Depth, Along = forPlot.Along, Facing = forPlot.Facing, Road = forPlot.Road,
                    Front = forPlot.Front + forPlot.Along * (dir * (forPlot.Width * 0.5f + plan.Width * 0.5f)),
                    Arc = forPlot.Arc >= 0f ? forPlot.Arc + dir * (forPlot.Width * 0.5f + plan.Width * 0.5f) : -1f,
                    RoadSide = forPlot.RoadSide, Straight = forPlot.Straight
                };
                if (!PlotFits(roads, pl)) continue;
                var ways = ChooseWaysIn(roads, plan, pl, forPlot);
                if (ways.Count == 0) continue;
                var lot = new ParkingLot { Lot = pl, For = forPlot, Wanted = n, Plan = plan };
                LayOutLot(lot, plan, ways);
                SetFrontEntrances(lot, ways);
                if (lot.Spaces >= n) return lot;
            }
            return null;
        }

        /// <summary>The plan holding the most stalls in a lot `width` x `depth` (of two holding as many, the smaller), at its own
        /// size: across with each number of aisles that fits the width, its rows as long as the depth allows, a row facing the
        /// cross road or not; along, a row in front or not, as long as the width allows. Null if none holds a stall.</summary>
        LotPlan BestPlanIn(float width, float depth)
        {
            LotPlan best = null;
            float Wk = LotKerbWidth, H = HeadStrip, Sd = StallDepth, Sw = StallWidth, A = LotAisle;
            void Consider(LotPlan plan)
            {
                if (plan.Width > width + 1e-3f || plan.Depth > depth + 1e-3f) return;
                plan.Stalls = CountStalls(plan);
                if (plan.Stalls < 1) return;
                if (best == null || plan.Stalls > best.Stalls || plan.Stalls == best.Stalls && plan.Width * plan.Depth < best.Width * best.Depth) best = plan;
            }
            for (int k = 1; k <= 6; k++)
            {
                float w = 2f * Wk + BizExtra + AcrossUsed(k);
                if (w > width + 1e-3f) break;
                foreach (bool front in k >= 2 ? new[] { false, true } : new[] { false })
                {
                    float cross = k >= 2 ? (front ? H + Sd : 0f) + A : 0f;
                    int per = Mathf.FloorToInt((depth - 2f * Wk - cross - 2f * RowEndGap) / Sw + 1e-4f);
                    if (per >= 1) Consider(new LotPlan { Aisles = k, FrontRow = front, PerRow = per, Width = w, Depth = 2f * Wk + cross + 2f * RowEndGap + per * Sw });
                }
            }
            float minInner = 2f * (WayInEndStub + Mathf.Max(LaneKerbRadius, LaneWalkWidth)) + 2f * ParkingEntranceHalf;
            foreach (bool front in new[] { true, false })
            {
                int per = Mathf.FloorToInt((width - 2f * Wk - BizExtra - 2f * RowEndGap) / Sw + 1e-4f);
                float w = 2f * Wk + BizExtra + per * Sw + 2f * RowEndGap;
                if (per >= 1 && w - 2f * Wk - BizExtra >= minInner)
                    Consider(new LotPlan { Along = true, FrontRow = front, PerRow = per, Width = w, Depth = 2f * Wk + (front ? H + Sd : 0f) + A + Sd + H });
            }
            return best;
        }

        /// <summary>Lots stretched over leftover room, and the stalls that gained them.</summary>
        public int LotsStretched, StallsGainedByStretch;
        /// <summary>⭐ EACH LOT OVER THE ROOM LEFT BESIDE AND BEHIND IT (strawberry 2026-10-14: "parking lots should try fill the
        /// space they reserved (within reason) like pic 4 could stretch further over that lil sliver of grass"). After the
        /// plots have grown their yards (GrowPlots), each lot grows the same way -- half a metre at a time, left, right and
        /// back, while its new edge is buildable and it overlaps nothing -- out to LotStretchSide / LotStretchBack; is planned
        /// again for the most stalls that room holds (BestPlanIn); then gives back whatever of the room that plan leaves
        /// over, unless it is no more than LotSliver -- a strip nothing else could use, which it keeps as margin rather than
        /// leave as grass. Never fewer stalls, never without a way in that can open: else it is put back as it was.</summary>
        void StretchLots(RoadField roads)
        {
            LotsStretched = StallsGainedByStretch = 0;
            if (!ParkingLots || roads == null || ParkingLotsLaid.Count == 0) return;
            float deepest = 0f; foreach (var ps in PlotSizes) if (ps.Kind != PlotKind.Parking) deepest = Mathf.Max(deepest, ps.Depth);
            const float step = 0.5f;
            foreach (var lot in ParkingLotsLaid)
            {
                var pl = lot.Lot;
                float w0 = pl.Width, d0 = pl.Depth, arc0 = pl.Arc; var f0 = pl.Front;
                float sgn = 1f;
                if (pl.Arc >= 0f && pl.Road >= 0 && roads.EvaluateAlong(pl.Road, Mathf.Clamp(pl.Arc, 0f, roads.RoadLength(pl.Road)), out _, out var tan, snapTerrain: false))
                    sgn = new Vector3(tan.X, 0f, tan.Z).Dot(pl.Along) >= 0f ? 1f : -1f;
                void Shift(float by) { pl.Front += pl.Along * by; if (pl.Arc >= 0f) pl.Arc += sgn * by; }
                bool Clear(Vector3 a, Vector3 b)
                {
                    // GrowPlots' test: the new edge on buildable ground, off every bay and lane, and the lot over nobody
                    int n = Mathf.Max(1, Mathf.CeilToInt(Flat(a, b) / 1.5f));
                    for (int k = 0; k <= n; k++) { var q = a.Lerp(b, (float)k / n); if (!Buildable(roads, q) || BayFrontAt(roads, q) || LaneAt(q)) return false; }
                    var c = pl.Corners();
                    foreach (var o in Plots) if (!ReferenceEquals(o, pl) && (o.Outline != null ? QuadsOverlap(c, o.Outline) : PlotsOverlap(pl, o))) return false;
                    foreach (var al in Alleys) if (QuadsOverlap(c, al.Corners())) return false;
                    return true;
                }
                float gl = 0f, gr = 0f, gb = 0f;
                for (bool any = true; any;)
                {
                    any = false;
                    for (int sd = 0; sd < 3; sd++)
                    {
                        if (sd == 0 && (gb + step > LotStretchBack + 1e-3f || pl.Depth + step > deepest + 1e-3f)) continue;
                        if (sd == 1 && gl + step > LotStretchSide + 1e-3f || sd == 2 && gr + step > LotStretchSide + 1e-3f) continue;
                        if (sd == 0) pl.Depth += step; else { pl.Width += step; Shift(sd == 1 ? -step * 0.5f : step * 0.5f); }
                        var c = pl.Corners();          // front-left, front-right, back-right, back-left
                        var (ea, eb) = sd == 0 ? (c[2], c[3]) : sd == 1 ? (c[3], c[0]) : (c[1], c[2]);
                        if (Clear(ea, eb)) { any = true; if (sd == 0) gb += step; else if (sd == 1) gl += step; else gr += step; continue; }
                        if (sd == 0) pl.Depth -= step; else { pl.Width -= step; Shift(sd == 1 ? step * 0.5f : -step * 0.5f); }
                    }
                }
                void PutBack() { pl.Width = w0; pl.Depth = d0; pl.Front = f0; pl.Arc = arc0; }
                if (gl + gr + gb < 1e-3f) continue;
                var plan = BestPlanIn(pl.Width, pl.Depth);
                if (plan == null || plan.Stalls < lot.Spaces) { PutBack(); continue; }
                // give back the room the plan does not use, unless it is only a sliver: the side that grew the most first
                float spareW = pl.Width - plan.Width, spareD = pl.Depth - plan.Depth;
                if (spareW > LotSliver + 1e-3f)
                {
                    float give = Mathf.Min(spareW, gl + gr);
                    float fromL = gl + gr > 1e-4f ? give * gl / (gl + gr) : 0f, fromR = give - fromL;
                    pl.Width -= fromL + fromR; Shift(fromL * 0.5f - fromR * 0.5f);
                    gl -= fromL; gr -= fromR;
                }
                if (spareD > LotSliver + 1e-3f) { float give = Mathf.Min(spareD, gb); pl.Depth -= give; gb -= give; }
                if (Mathf.Abs(pl.Width - w0) < 1e-3f && Mathf.Abs(pl.Depth - d0) < 1e-3f) { PutBack(); continue; }   // (all of it given back)
                var ways = ChooseWaysIn(roads, plan, pl, lot.For);
                if (ways.Count == 0) { PutBack(); continue; }
                int before = lot.Spaces;
                var oldPlan = lot.Plan;
                lot.Plan = plan;
                LayOutLot(lot, plan, ways);
                if (lot.Spaces < before)
                {
                    PutBack(); lot.Plan = oldPlan;
                    var oldWays = ChooseWaysIn(roads, oldPlan, pl, lot.For);
                    LayOutLot(lot, oldPlan, oldWays); SetFrontEntrances(lot, oldWays);
                    continue;
                }
                SetFrontEntrances(lot, ways);
                LotsStretched++; StallsGainedByStretch += lot.Spaces - before; ParkingSpacesLaid += lot.Spaces - before;
                if (ProbeEverything)
                    Log.Print($"[towngrid]   parking lot at ({f0.X:0},{f0.Z:0}) stretched {gl:0.0} m left, {gr:0.0} m right, {gb:0.0} m back to {pl.Width:0.0}x{pl.Depth:0.0} m: "
                            + $"{before} -> {lot.Spaces} stalls, {plan}; {StallsReachable(lot)} of {lot.Spaces} reachable");
            }
        }

        /// <summary>⭐ THE BUILDINGS THAT ASKED, AND A LOT BESIDE EACH. Laid on the second layout pass, after the corner lots and
        /// before the frontages fill: each building that asked on the first pass is put back where it stood, and its lot
        /// solved and laid beside it -- the side it fits on, the seed choosing when both do; and if neither side takes all it
        /// asked, the most it can. The frontages then fill round both.</summary>
        void PlaceRequestedParking(RoadField roads, List<(Plot pl, int n)> asks)
        {
            ParkingLotsLaid.Clear();
            ParkingAsked = ParkingSpacesAsked = ParkingSpacesLaid = ParkingRefused = ParkingCornersTaken = 0;
            _lotPlans = null;
            if (!ParkingLots || asks == null) return;
            foreach (var (was, n) in asks)
            {
                ParkingAsked++; ParkingSpacesAsked += n;
                ulong h = Mix(((ulong)(uint)Seed << 32) ^ Mix(((ulong)(uint)was.Road << 20) ^ (ulong)(uint)Mathf.RoundToInt(was.Arc * 10f) ^ 0x10717UL));
                int first = Unit(h, 0) < 0.5f ? -1 : 1;
                // ⭐ SOLVED, NOT JUST TRIED: the building and its lot as a PAIR, slid along its street from where it stood -- a metre at
                // a time each way, out to ParkingSlide -- until both fit; only if nowhere on that frontage takes all it asked
                // does it take less, the most that fits beside it where it stood. (The frontages are not filled yet: what is
                // in the way is a corner lot, a lane, a junction, another building that asked and its lot.)
                Plot pl = null; ParkingLot lot = null;
                Plot Shifted(float by) => new Plot
                {
                    Kind = was.Kind, Width = was.Width, Depth = was.Depth, Along = was.Along, Facing = was.Facing, Road = was.Road, Road2 = was.Road2,
                    Front = was.Front + was.Along * by, Arc = was.Arc >= 0f ? was.Arc + by : -1f, RoadSide = was.RoadSide, Straight = was.Straight,
                    YardFront = was.YardFront, YardBack = was.YardBack, YardLeft = was.YardLeft, YardRight = was.YardRight, Setback = was.Setback
                };
                float reach = was.Straight && was.Road2 < 0 ? ParkingSlide : 0f;
                void Slide()
                {
                    for (float k = 0f; k <= reach && lot == null; k += 1f)
                        foreach (float by in k == 0f ? new[] { 0f } : new[] { k, -k })
                        {
                            var cand = Shifted(by);
                            if (!PlotFits(roads, cand)) continue;
                            Plots.Add(cand);
                            foreach (int dir in new[] { first, -first })
                                if ((lot = ParkingLotBeside(roads, cand, n, dir)) != null) break;
                            if (lot != null) { pl = cand; break; }
                            Plots.RemoveAt(Plots.Count - 1);
                        }
                }
                Slide();
                // ...and if its frontage is too short between its two corner lots, those corner lots give way -- a car park
                // on a street corner, reached from both streets, is the commonest kind there is; the frontage fill puts
                // houses back in whatever the pair leaves
                if (lot == null && reach > 0f)
                {
                    var gave = new List<Plot>();
                    foreach (var cl in Plots)
                    {
                        if (cl.Kind != PlotKind.Corner) continue;
                        var rel = cl.Centre - was.Centre;
                        if (Mathf.Abs(rel.Dot(was.Facing)) < 20f && Mathf.Abs(rel.Dot(was.Along)) < reach + 40f) gave.Add(cl);
                    }
                    foreach (var cl in gave) Plots.Remove(cl);
                    Slide();
                    if (lot == null) Plots.AddRange(gave);
                    else
                    {
                        // (only the ones the pair actually needed: any it does not touch go back -- asked with the LOT in place too,
                        // or a corner the lot sits on came straight back over it)
                        foreach (var cl in gave) if (PlotFits(roads, cl) && !PlotsOverlap(cl, lot.Lot)) Plots.Add(cl); else ParkingCornersTaken++;
                    }
                }
                if (lot == null && PlotFits(roads, was))
                {
                    // the most that fits beside it where it stood
                    pl = was; Plots.Add(pl);
                    for (int want = n - 1; want >= 1 && lot == null; want = want > 8 ? want - 2 : want - 1)
                        foreach (int dir in new[] { first, -first })
                            if ((lot = ParkingLotBeside(roads, pl, want, dir)) != null) break;
                }
                if (lot == null)
                {
                    ParkingRefused++;
                    if (ProbeEverything) Log.Print($"[towngrid]   parking lot for the {was.Kind} at ({was.Front.X:0},{was.Front.Z:0}): asked {n} -- no room on its frontage");
                    continue;
                }
                lot.For = pl; lot.Wanted = n;
                Plots.Add(lot.Lot);
                ParkingLotsLaid.Add(lot);
                ParkingSpacesLaid += lot.Spaces;
                if (ProbeEverything)
                {
                    var lp = lot.Lot;
                    Log.Print($"[towngrid]   parking lot for the {pl.Kind} at ({pl.Front.X:0},{pl.Front.Z:0}): asked {n}, laid {lot.Spaces} -- {lot.Plan}, "
                            + $"{lp.Width:0.0}x{lp.Depth:0.0} m ({lp.Width * lp.Depth / Mathf.Max(1, lot.Spaces):0} m2 a stall) at ({lp.Front.X:0},{lp.Front.Z:0}); "
                            + $"{StallsReachable(lot)} of {lot.Spaces} stalls reachable on its {lot.Roads.Count} two-lane roads from {lot.Entrances.Count} way{(lot.Entrances.Count > 1 ? "s" : "")} in");
                }
            }
        }
        /// <summary>⭐ A LOT LAID OUT TO ITS PLAN, its ways in at `ways` (m along its front from the middle). In its own frame
        /// (x along the street from the front's middle, y back from the front edge), inside its kerb (xL..xR, yF..yB):
        /// ACROSS -- from the left kerb, module after module of [row | aisle | row], each 2 x 5 + 7 m, the right row of one
        /// nose to nose with the left row of the next; the rows running back from just behind the cross road (or the front
        /// kerb, for one aisle) to just short of the back kerb; the cross road along the front, behind the front row if
        /// there is one. ALONG -- the front row (if any) with its heads at the front kerb, cut where each way in comes
        /// through, the aisle behind it, and the back row with its heads at the back kerb.</summary>
        void LayOutLot(ParkingLot lot, LotPlan plan, List<float> ways, List<(int edge, float at)> exits = null)
        {
            var p = lot.Lot;
            Vector3 At(float x, float y) => p.Front + p.Along * x - p.Facing * y;      // x along the street, y back from it
            var (xL, xR) = InnerX(p, lot.For);
            float yF = LotKerbWidth, yB = p.Depth - LotKerbWidth;
            float H = HeadStrip, Sd = StallDepth, A = LotAisle, th = ParkingEntranceHalf, cut = th + StallStripe * 0.5f;
            lot.Stalls.Clear(); lot.Stripes.Clear(); lot.Blocks.Clear(); lot.Roads.Clear(); lot.Dividers.Clear();
            // the stretches of a row's run a way out through `edge` takes (its middles, cut half-width either side)
            List<(float lo, float hi)> Cuts(int edge)
            {
                var c = new List<(float, float)>();
                if (exits != null) foreach (var (e, at) in exits) if (e == edge) c.Add((at - cut, at + cut));
                return c;
            }
            // a row of stalls: along x (or y), as many as fit between stripe middles a..b, centred, less any a way out takes; its
            // heads on the line `head` across it and its open side -- the road's edge -- on `open`; a block at each head that
            // faces the lot's kerb
            void Row(bool alongX, float a, float b, float head, float open, bool kerbAhead, List<(float lo, float hi)> skip = null)
            {
                int n = Mathf.FloorToInt((b - a) / StallWidth + 1e-4f);
                if (n < 1) return;
                float s0 = a + (b - a - n * StallWidth) * 0.5f, into = Mathf.Sign(head - open), mid = (head + open) * 0.5f;
                Vector3 P(float run, float acr) => alongX ? At(run, acr) : At(acr, run);
                var runDir = alongX ? p.Along : -p.Facing;
                var intoDir = alongX ? -p.Facing * into : p.Along * into;
                var stripes = new SortedSet<int>();
                for (int k = 0; k < n; k++)
                {
                    float c0 = s0 + k * StallWidth, c1 = c0 + StallWidth;
                    bool taken = false;
                    if (skip != null) foreach (var (lo, hi) in skip) if (c0 < hi && lo < c1) { taken = true; break; }
                    if (taken) continue;
                    float c = (c0 + c1) * 0.5f;
                    lot.Stalls.Add((P(c, mid), runDir, intoDir));
                    if (kerbAhead && ParkingBlocks) lot.Blocks.Add((P(c, head + into * BlockPast), runDir));
                    stripes.Add(k); stripes.Add(k + 1);
                }
                // (each stripe stands StripeGap off the kerb at its head and off the road at its open end)
                float g = Mathf.Min(StripeGap, Mathf.Abs(head - open) * 0.25f);
                foreach (int k in stripes) { float sx = s0 + k * StallWidth; lot.Stripes.Add((P(sx, head - into * g), P(sx, open + into * g))); }
            }
            // a row along the front kerb, cut where each way in comes through it -- and the way in's road on through it
            void FrontRow(float roadFrom)
            {
                var cuts = new List<float>(ways); cuts.Sort();
                float a = xL + RowEndGap;
                foreach (float xe in cuts)
                {
                    Row(true, a, xe - cut, yF + H, yF + H + Sd, true);
                    a = xe + cut;
                    lot.Roads.Add((xe - th, yF, xe + th, roadFrom));
                }
                Row(true, a, xR - RowEndGap, yF + H, yF + H + Sd, true);
            }
            if (!plan.Along)
            {
                float y0 = yF + RowEndGap;
                if (plan.Aisles >= 2)
                {
                    float roadFrom = yF + (plan.FrontRow ? H + Sd : 0f);
                    if (plan.FrontRow) FrontRow(roadFrom);
                    lot.Roads.Add((xL, roadFrom, xR, roadFrom + A));          // the cross road
                    y0 = roadFrom + A + RowEndGap;
                }
                float y1 = y0 + plan.PerRow * StallWidth, x = AcrossX0(plan, xL, xR);
                for (int i = 0; i < plan.Aisles; i++)
                {
                    // (the outermost rows face the lot's sides: a way out through a side comes through its row there, onto the aisle)
                    Row(false, y0, y1, x, x + Sd, true, i == 0 ? Cuts(3) : null);
                    lot.Roads.Add((x + Sd, yF, x + Sd + A, yB));
                    Row(false, y0, y1, x + 2f * Sd + A, x + Sd + A, true, i == plan.Aisles - 1 ? Cuts(1) : null);
                    if (i == 0) foreach (var (lo, hi) in Cuts(3)) lot.Roads.Add((xL, lo + StallStripe * 0.5f, x + Sd, hi - StallStripe * 0.5f));
                    if (i == plan.Aisles - 1) foreach (var (lo, hi) in Cuts(1)) lot.Roads.Add((x + Sd + A, lo + StallStripe * 0.5f, xR, hi - StallStripe * 0.5f));
                    // ...and between this aisle's right row and the next one's left, nose to nose, the divider: from just
                    // short of the first stall (its nose at the cross road) to the back kerb
                    if (i + 1 < plan.Aisles)
                        lot.Dividers.Add((x + 2f * Sd + A, y0 - RowEndGap + 0.25f, x + 2f * Sd + A + LotDividerWidth, yB));
                    x += AcrossPitch;
                }
            }
            else
            {
                float aisle0 = yF + (plan.FrontRow ? H + Sd : 0f);
                if (plan.FrontRow) FrontRow(aisle0);
                lot.Roads.Add((xL, aisle0, xR, aisle0 + A));
                // (a way out through the back comes through the back row, onto the aisle)
                Row(true, xL + RowEndGap, xR - RowEndGap, aisle0 + A + Sd, aisle0 + A, true, Cuts(2));
                foreach (var (lo, hi) in Cuts(2)) lot.Roads.Add((lo + StallStripe * 0.5f, aisle0 + A, hi - StallStripe * 0.5f, yB));
            }
            lot.Spaces = lot.Stalls.Count;
        }

        /// <summary>A lot's ways in through its front, at `ways` (m along it from the middle).</summary>
        void SetFrontEntrances(ParkingLot lot, List<float> ways)
        {
            var p = lot.Lot;
            lot.Entrances.Clear();
            foreach (float xe in ways)
                lot.Entrances.Add(new ParkingEntrance { Road = p.Road, Side = p.RoadSide, Mouth = p.Front + p.Along * xe, Into = -p.Facing,
                                                        Width = 2f * ParkingEntranceHalf, Lot = lot });
        }

        /// <summary>⭐ MEASURED: how many of a lot's stalls can be driven to -- the asphalt in front of each (half a metre out of
        /// its open side) on one of its two-lane roads, and that road joined, road to road, to a way in at the front kerb. A
        /// stall whose open side faces no road, or a road nothing joins, is a space nobody can reach.</summary>
        int StallsReachable(ParkingLot lot)
        {
            var p = lot.Lot;
            (float x, float y) Local(Vector3 q) { var r = q - p.Front; return (r.Dot(p.Along), -r.Dot(p.Facing)); }
            var roads = lot.Roads;
            bool Touch((float x0, float y0, float x1, float y1) a, (float x0, float y0, float x1, float y1) b)
                => a.x0 <= b.x1 + 0.01f && b.x0 <= a.x1 + 0.01f && a.y0 <= b.y1 + 0.01f && b.y0 <= a.y1 + 0.01f;
            var reached = new bool[roads.Count];
            var queue = new Queue<int>();
            for (int i = 0; i < roads.Count; i++)
                foreach (var e in lot.Entrances)
                {
                    // (where the way comes through the lot's kerb line)
                    var (xe, ye) = Local(e.Mouth + e.Into * LotKerbWidth);
                    var r = roads[i];
                    if (!reached[i] && r.x0 - 0.01f <= xe && xe <= r.x1 + 0.01f && r.y0 - 0.01f <= ye && ye <= r.y1 + 0.01f) { reached[i] = true; queue.Enqueue(i); }
                }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                for (int j = 0; j < roads.Count; j++)
                    if (!reached[j] && Touch(roads[i], roads[j])) { reached[j] = true; queue.Enqueue(j); }
            }
            int n = 0;
            foreach (var (at, _, into) in lot.Stalls)
            {
                var (x, y) = Local(at - into * (StallDepth * 0.5f + 0.5f));
                for (int i = 0; i < roads.Count; i++)
                {
                    var r = roads[i];
                    if (reached[i] && r.x0 - 0.01f <= x && x <= r.x1 + 0.01f && r.y0 - 0.01f <= y && y <= r.y1 + 0.01f) { n++; break; }
                }
            }
            return n;
        }
        /// <summary>⭐ EACH WAY IN, A ROAD (strawberry 2026-10-14: "a real road connection, not just a dipped kerb"; "2 lane road
        /// going in"): opened off the street the way a back lane is (OpenMouth: the street's pavement cut, a kerb returning
        /// round each corner) but two lanes wide, and run in through the lot's own kerb to its asphalt, where it stops OPEN
        /// (LanePlan.OpenB) -- no kerb across it, and no pavements of its own: the lot's kerb curves round to meet the
        /// returns (BuildParkingLots). LEVEL, at the street's height where it meets it -- which is the lot's front's.
        /// Planned with the lanes, before the street's parking bays, which keep off every mouth; built with them (BuildLanes).</summary>
        void PlanParkingEntrances(RoadField roads)
        {
            if (!ParkingLots || roads == null || ParkingEntranceMaterial < 0) return;
            ParkingWaysNearCorner = ParkingExtraExits = 0;
            // each lot's surface first: the height every way in comes in at
            foreach (var lot in ParkingLotsLaid) DesignLotSurface(lot, roads);
            float laneSurf = roads.RoadSurfaceOffsetOfMaterial(ParkingEntranceMaterial);
            LaneMouth Try(ParkingEntrance e, float clear)
            {
                _tryWhy = "too far, or across a plot";
                var m = OpenMouth(roads, e.Mouth, -e.Into, ParkingEntranceHalf, clear);
                if (m == null) return null;
                if (e.Edge != 0)
                {
                    // ...a side or back way only to a street right there, and across nobody's plot to it
                    if (Flat(m.Edge, e.Mouth) > _walkWidth + PlotSetback + LotWayReach) { _tryWhy = $"too far ({Flat(m.Edge, e.Mouth):0.0} m to the street)"; return null; }
                    var lat = new Vector3(-e.Into.Z, 0f, e.Into.X) * ParkingEntranceHalf;
                    var quad = new[] { m.Edge - lat, m.Edge + lat, e.Mouth + lat, e.Mouth - lat };
                    foreach (var o in Plots)
                        if (!ReferenceEquals(o, e.Lot.Lot) && (o.Outline != null ? QuadsOverlap(quad, o.Outline) : QuadsOverlap(quad, o.Corners()))) { _tryWhy = $"across a {o.Kind} at ({o.Front.X:0},{o.Front.Z:0})"; return null; }
                    foreach (var al in Alleys) if (QuadsOverlap(quad, al.Corners())) { _tryWhy = "across an alley"; return null; }
                }
                return m;
            }
            bool Commit(ParkingEntrance e, LaneMouth m)
            {
                var end = e.Mouth + e.Into * LotKerbWidth;        // through the lot's kerb, onto its asphalt
                var line = RoundedPath(m.Edge, m.Edge, end, 0f, 2f);
                if (line == null || line.Count < 2) return false;
                // the street's height at its mouth, the lot's where it comes in: level through the front, where the lot's front
                // IS its street's height; graded like a lane's through a side or the back, which the lot meets wherever it is
                float ya = m.Edge.Y, yb = LotSurfaceY(e.Lot, end.X, end.Z, LotGround(end.X, end.Z)) - laneSurf;
                if (e.Edge == 0) yb = ya;
                // (graded to the lot's height by the plot's edge, and level from there over the lot's own ground -- graded up to
                // the kerb line, a way climbing to a lot stood under the lot's ground where it crossed the plot's edge)
                float run = Mathf.Max(0.1f, (e.Mouth - m.Edge).Dot(e.Into));
                for (int q = 0; q < line.Count; q++)
                {
                    float t = Mathf.Clamp((line[q] - m.Edge).Dot(e.Into) / run, 0f, 1f);
                    line[q] = new Vector3(line[q].X, Mathf.Lerp(ya, yb, t), line[q].Z);
                }
                LaneMouths.Add(m);
                e.Lane = new LanePlan { Pts = line, A = m, OpenB = true, Material = ParkingEntranceMaterial, Half = ParkingEntranceHalf };
                if (e.Edge != 0)
                {
                    // ...its end ring in the lot's own surface there: the lot slopes front to back, which is across a way through
                    // its side, and a level end met it 17 cm high one side and low the other (the side-way probe)
                    float S(Vector3 q) => LotSurfaceY(e.Lot, q.X, q.Z, LotGround(q.X, q.Z));
                    float sy = S(end);
                    var grad = new Vector3(S(end + Vector3.Right * 0.5f) - S(end - Vector3.Right * 0.5f), 0f,
                                           S(end + Vector3.Back * 0.5f) - S(end - Vector3.Back * 0.5f));
                    e.Lane.EndPlane = (new Vector3(end.X, sy, end.Z), grad);
                }
                LanePlans.Add(e.Lane);
                e.From = m.From; e.To = m.To; e.Road = m.Road; e.Side = m.Side;
                return true;
            }
            bool Open(ParkingEntrance e, float clear) { var m = Try(e, clear); return m != null && Commit(e, m); }
            foreach (var lot in ParkingLotsLaid)
            {
                int opened = 0;
                foreach (var e in lot.Entrances) if (Open(e, ParkingCornerClear)) opened++;
                if (opened > 0 || lot.Entrances.Count == 0) continue;
                // every way in too near a corner: the one nearest the lot's middle opens anyway
                var mid = lot.Lot.Front;
                var order = new List<ParkingEntrance>(lot.Entrances);
                order.Sort((x, y) => Flat(x.Mouth, mid).CompareTo(Flat(y.Mouth, mid)));
                foreach (var e in order) if (Open(e, 0f)) { ParkingWaysNearCorner++; break; }
            }
            // ⭐ AND OUT ANY OTHER SIDE A STREET IS ON (strawberry 2026-10-14: "gimme an example of one with exits on multiple
            // sides"): a lot beside a cross street, or one through to the street behind, opens onto that too -- where its
            // roads run out to that edge without crossing a stall: an across lot's aisles out its back, its cross road out
            // its sides; an along lot's aisle out its sides. One a side, the middle-most first, wherever OpenMouth finds a
            // street's straight pavement within reach of that edge and clear of its corners. (Its tries that find no street
            // are not dead ends: the lanes' counters are put back.)
            var dead = (DeadNoStreet, DeadGlancing, DeadNoCorner, DeadOffPavement, DeadOnMouth, MouthsNearCorner);
            float rEst = Mathf.Max(LaneKerbRadius, LaneWalkWidth);
            foreach (var lot in ParkingLotsLaid)
            {
                lot.Exits.Clear();
                if (lot.Entrances.Count == 0 || lot.Entrances.TrueForAll(e => e.Lane == null)) continue;
                var p = lot.Lot;
                foreach (var spots in ExitSpots(lot))
                    foreach (var (edge, at0) in spots)
                    {
                        float at = at0;
                        var e = ExitAt(lot, edge, at);
                        var why = (DeadNoStreet, DeadGlancing, DeadNoCorner, DeadOffPavement, DeadOnMouth, MouthsNearCorner);
                        var m = Try(e, ParkingCornerClear);
                        // a side way at the back end of its row: if the kerb's curve into it is wider than allowed for (more grass
                        // to cross to the street), it comes in that much further forward, so the curve still clears the corner
                        if (m != null && !lot.Plan.Along && (edge == 1 || edge == 3))
                        {
                            float r = CurveRadius(lot, e, m);
                            if (r > rEst + 0.05f)
                            {
                                at -= r - rEst;
                                e = ExitAt(lot, edge, at);
                                m = Try(e, ParkingCornerClear);
                            }
                        }
                        bool ok = m != null && Commit(e, m);
                        if (ProbeEverything)
                        {
                            string res = ok ? "OPENED" : DeadNoStreet > why.Item1 ? "no street within reach" : DeadGlancing > why.Item2 ? "glancing"
                                       : DeadNoCorner > why.Item3 ? "no corner" : DeadOffPavement > why.Item4 ? "into a junction's corner"
                                       : DeadOnMouth > why.Item5 ? "onto another mouth" : MouthsNearCorner > why.Item6 ? "too near a corner" : _tryWhy;
                            Log.Print($"[towngrid]   lot at ({p.Front.X:0},{p.Front.Z:0}): a way out its {(edge == 2 ? "back" : edge == 1 ? "right side" : "left side")} at ({e.Mouth.X:0.0},{e.Mouth.Z:0.0}) -- {res}");
                        }
                        if (!ok) continue;
                        lot.Entrances.Add(e); lot.Exits.Add((edge, at)); ParkingExtraExits++;
                        break;
                    }
                if (lot.Exits.Count > 0)
                {
                    // laid again round its ways out: the stalls they come through go
                    var front = new List<float>();
                    foreach (var e in lot.Entrances) if (e.Edge == 0) front.Add((e.Mouth - p.Front).Dot(p.Along));
                    int before = lot.Spaces;
                    LayOutLot(lot, lot.Plan, front, lot.Exits);
                    ParkingSpacesLaid += lot.Spaces - before;
                    if (ProbeEverything)
                        Log.Print($"[towngrid]   lot at ({p.Front.X:0},{p.Front.Z:0}) laid again round {lot.Exits.Count} way{(lot.Exits.Count > 1 ? "s" : "")} out: {before} -> {lot.Spaces} stalls, "
                                + $"{StallsReachable(lot)} of {lot.Spaces} reachable from its {lot.Entrances.Count} ways");
                }
            }
            (DeadNoStreet, DeadGlancing, DeadNoCorner, DeadOffPavement, DeadOnMouth, MouthsNearCorner) = dead;
        }
        string _tryWhy = "";
        /// <summary>How much grass a way out through a lot's side or back may cross to its street, past the street's pavement
        /// (m): its own little road, pavements down both sides.</summary>
        public float LotWayReach = 14f;
        /// <summary>Lots whose only way in had to open nearer a junction's corner than ParkingCornerClear; ways out other
        /// than the front ones.</summary>
        public int ParkingWaysNearCorner, ParkingExtraExits;

        /// <summary>Where else a lot's ways may run out through its edges, a list per edge, best first (edge 1 its right, 2 its
        /// back, 3 its left; `at` along that edge, lot frame): an across lot's aisles straight out its back, and out each side
        /// through its outer row at the back end -- as far from its front street's junction as it can be; an along lot's
        /// aisle out each side, and out its back through the back row.</summary>
        List<List<(int edge, float at)>> ExitSpots(ParkingLot lot)
        {
            var plan = lot.Plan; var p = lot.Lot;
            var all = new List<List<(int, float)>>();
            float yF = LotKerbWidth, yB = p.Depth - LotKerbWidth;
            float inset = WayInEndStub + Mathf.Max(LaneKerbRadius, LaneWalkWidth) + ParkingEntranceHalf + PavementGap;
            if (plan.Along)
            {
                float y = yF + (plan.FrontRow ? HeadStrip + StallDepth : 0f) + LotAisle * 0.5f;
                all.Add(new List<(int, float)> { (1, y) }); all.Add(new List<(int, float)> { (3, y) });
                all.Add(new List<(int, float)> { (2, 0f) });
                return all;
            }
            var back = new List<(int, float)>();
            var (exL, exR) = InnerX(p, lot.For);
            float x0 = AcrossX0(plan, exL, exR);
            for (int i = 0; i < plan.Aisles; i++) back.Add((2, x0 + StallDepth + LotAisle * 0.5f + i * AcrossPitch));
            back.Sort((u, v) => Mathf.Abs(u.Item2).CompareTo(Mathf.Abs(v.Item2)));
            all.Add(back);
            all.Add(new List<(int, float)> { (1, yB - inset) }); all.Add(new List<(int, float)> { (3, yB - inset) });
            return all;
        }

        /// <summary>The radius the lot's kerb turns into way `e` on, given its mouth: what is left of the way between the end of
        /// the mouth's kerb return and the lot's kerb line (BuildParkingLots' curve) -- the larger of its two sides.</summary>
        float CurveRadius(ParkingLot lot, ParkingEntrance e, LaneMouth m)
        {
            float r = 0f;
            for (int c = 0; c < 2; c++)
            {
                var kp = m.Kerb[c];
                if (kp == null || kp.Count == 0) continue;
                var end = (kp[0] - m.Edge).Dot(m.Into) > (kp[^1] - m.Edge).Dot(m.Into) ? kp[0] : kp[^1];
                float inn = (end - e.Mouth).Dot(e.Into);
                r = Mathf.Max(r, Mathf.Clamp(LotKerbWidth - inn, 0.3f, Mathf.Max(0.3f, LaneWalkWidth)));
            }
            return r;
        }

        /// <summary>A way through edge `edge` of a lot, `at` along it: its middle on the plot's edge, and the way in.</summary>
        ParkingEntrance ExitAt(ParkingLot lot, int edge, float at)
        {
            var p = lot.Lot;
            Vector3 At(float x, float y) => p.Front + p.Along * x - p.Facing * y;
            var (pt, inward) = edge switch
            {
                1 => (At(p.Width * 0.5f, at), -p.Along),
                2 => (At(at, p.Depth), p.Facing),
                3 => (At(-p.Width * 0.5f, at), p.Along),
                _ => (At(at, 0f), -p.Facing),
            };
            return new ParkingEntrance { Road = -1, Side = 0, Mouth = pt, Into = inward, Width = 2f * ParkingEntranceHalf, Lot = lot, Edge = edge };
        }

        /// <summary>The ground under a lot, before it is graded: the terrain's, or the town's own height function's.</summary>
        float LotGround(float x, float z) => Terrain.Active != null ? Terrain.Active.SampleHeight(x, z) : _heightAt != null ? _heightAt(x, z) : Centre.Y;

        /// <summary>⭐ EACH LOT'S SURFACE, DESIGNED BEFORE ANYTHING MEETS IT: its street's height along its front, and -- where the
        /// town grades its site (GradeSite), which GradeLots then cuts the ground to -- the ground's fitted line along its
        /// back, its surface running straight between the two. The ways in that come through its sides or back are graded
        /// to meet it there.</summary>
        void DesignLotSurface(ParkingLot lot, RoadField roads)
        {
            var p = lot.Lot;
            SampleLotFront(lot, roads);
            lot.Graded = false;
            if (!GradeSite) return;
            // the ground's line along the back edge: least squares over a sample a metre
            double su = 0, sy = 0, suu = 0, suy = 0; int ns = 0;
            for (float u = -p.Width * 0.5f; u <= p.Width * 0.5f + 1e-3f; u += 1f)
            {
                var q = p.Front + p.Along * u - p.Facing * p.Depth;
                float gy = LotGround(q.X, q.Z);
                su += u; sy += gy; suu += (double)u * u; suy += (double)u * gy; ns++;
            }
            if (ns < 2) return;
            double den = ns * suu - su * su;
            lot.BackB = den > 1e-6 ? (float)((ns * suy - su * sy) / den) : 0f;
            lot.BackA = (float)(sy / ns) - lot.BackB * (float)(su / ns);
            lot.Graded = true;
        }

        /// <summary>Each lot's street's height along its front, a metre apart and past each end as far as its grading reaches:
        /// its asphalt across from there, which is its pavement's top less a kerb (so the lot's front kerb stands exactly as
        /// high).</summary>
        void SampleLotFront(ParkingLot lot, RoadField roads)
        {
            var p = lot.Lot;
            float kerbH = KerbProud(roads), reach = Terrain.GridUnit + 2f;
            int n = Mathf.CeilToInt(p.Width + 2f * reach) + 1;
            lot.FrontU0 = -p.Width * 0.5f - reach;
            lot.FrontY = new float[n];
            for (int k = 0; k < n; k++)
                lot.FrontY[k] = RoadNear(roads, p.Road, p.Front + p.Along * (lot.FrontU0 + k), out _, out _) + PavementKerb - kerbH;
        }

        /// <summary>How far under a graded lot's asphalt the ground is cut, and how far round it the cut eases back out.</summary>
        public float LotGradeDrop = 0.12f, LotGradeFeather = 6f;
        /// <summary>⭐ EACH LOT GRADED, ONE SMOOTH SURFACE (UG_TOWNHILLS: the ground showed through the asphalt in patches). A lot
        /// laid on the ground took the ground's height at each metre, and the terrain between its 4 m samples is the mesh's
        /// own triangles -- a hump a few centimetres over the lot's surface, on any slope, and the grass came up through the
        /// stalls. A real lot is graded: its surface runs straight from its street's height at the front kerb to the ground's
        /// line along its back (fitted, so a bump there is not copied), and the ground under it is cut or filled to just below
        /// that (LotGradeDrop) -- and beside it only ever cut, never raised: a grid cell past its sides and back, so no mesh
        /// triangle reaches up through its kerb, easing back out over LotGradeFeather (raising that ground too lifted the
        /// neighbours' yards through their plot marks) -- never toward its street, which the street's grading has, nor under a street or a lane (graded
        /// already, to their own profiles, and not yet built for a road test to see).</summary>
        void GradeLots(Terrain terr, RoadField roads)
        {
            if (terr == null || roads == null) return;
            int graded = 0; float worstCut = 0f, worstFill = 0f;
            foreach (var lot in ParkingLotsLaid)
            {
                var p = lot.Lot;
                if (!lot.Graded || lot.FrontY == null) DesignLotSurface(lot, roads);
                if (!lot.Graded) continue;
                // (measured: how far the surface stands off the ground it replaces, at the lot's middle row of samples)
                for (float u = -p.Width * 0.5f; u <= p.Width * 0.5f; u += 2f)
                    for (float y = 0f; y <= p.Depth; y += 2f)
                    {
                        var q = p.Front + p.Along * u - p.Facing * y;
                        float off = LotSurfaceY(lot, q.X, q.Z, 0f) - LotGradeDrop - terr.SampleHeight(q.X, q.Z);
                        worstFill = Mathf.Max(worstFill, off); worstCut = Mathf.Min(worstCut, off);
                    }
                // (where a way comes through a side or the back, the ground over the plot's edge is under the way too: level across
                // there, it is lower than the lot's tilted surface on one side)
                var sideWays = new List<(Vector3 at, Vector3 into, float surf)>();
                foreach (var e in lot.Entrances)
                    if (e.Edge != 0 && e.Lane?.Pts != null && e.Lane.Pts.Count > 0)
                        sideWays.Add((e.Mouth, e.Into, e.Lane.Pts[^1].Y + roads.RoadSurfaceOffsetOfMaterial(ParkingEntranceMaterial) - 0.2f));
                float Target(float x, float z)
                {
                    float t = LotSurfaceY(lot, x, z, 0f);
                    foreach (var (at, into, surf) in sideWays)
                    {
                        var r = new Vector3(x - at.X, 0f, z - at.Z);
                        float inn = r.Dot(into), lat = Mathf.Abs(r.Dot(new Vector3(-into.Z, 0f, into.X)));
                        if (inn > -1f && inn < LotKerbWidth + 1f && lat < ParkingEntranceHalf + LaneWalkWidth + 1f) t = Mathf.Min(t, surf);
                    }
                    return t - LotGradeDrop;
                }
                terr.ConformToSurface(p.Front, p.Along, -p.Facing, -p.Width * 0.5f, p.Width * 0.5f, -(PlotSetback + p.Setback), p.Depth,
                                      Target, Terrain.GridUnit, LotGradeFeather, beforeV0: false,
                                      keepOut: (x, z) =>
                                      {
                                          // (the lot's own ground is its own, its ways' ends included; outside it the ground is only ever cut, which
                                          // harms nothing -- a lane or a pavement beside it is laid to whatever is there, skirt and all -- except under a
                                          // street's asphalt, which is laid already. Keeping the lanes' ground out too left a back lane's bank standing
                                          // over the lot's back corner, through its asphalt.)
                                          float dx = x - p.Front.X, dz = z - p.Front.Z;
                                          float u = dx * p.Along.X + dz * p.Along.Z, y = -(dx * p.Facing.X + dz * p.Facing.Z);
                                          if (Mathf.Abs(u) <= p.Width * 0.5f && y >= -(PlotSetback + p.Setback) && y <= p.Depth) return false;
                                          return roads.DeepestRoadOverlap(new Vector3(x, 0f, z), 0.5f, out _, out _);
                                      });
                graded++;
            }
            if (graded > 0)
                Log.Print($"[towngrid] parking lots graded: {graded}, each one smooth surface from its street's height to the ground's line at its back; "
                        + $"the ground cut up to {-worstCut:0.00} m and filled up to {worstFill:0.00} m to sit {LotGradeDrop:0.00} m under it");
        }

        /// <summary>How long a lot's asphalt takes to come from its street's height at its front kerb to the ground (m).</summary>
        public float LotRamp = 3.5f;
        /// <summary>⚠⚠ THE LOT'S FRONT IS AT ITS STREET'S HEIGHT, ALL ALONG IT. Its ways in come off the street level, a kerb's
        /// step over the ground behind the pavement, and its front kerb meets the street's pavement edge to edge
        /// (strawberry 2026-10-14: "kill that gap between the sidewalk and parking lot kerb") -- so to stand as high as that
        /// pavement, its foot has to be where the street's asphalt is. Out to its front kerb's line the asphalt is at the
        /// street's height across from it (FrontY: the street climbs, and so does the front), easing to the ground over
        /// LotRamp into the lot -- the apron, where no stall stands.</summary>
        float LotSurfaceY(ParkingLot lot, float x, float z, float ground)
        {
            float g = ground + LotLift;
            if (lot?.FrontY == null) return g;
            var p = lot.Lot;
            float dx = x - p.Front.X, dz = z - p.Front.Z;
            float u = dx * p.Along.X + dz * p.Along.Z, y = -(dx * p.Facing.X + dz * p.Facing.Z);
            // graded: one smooth surface, the street's height at the front kerb straight to the ground's line at the back
            if (lot.Graded) return Mathf.Lerp(lot.FrontAt(u), lot.BackAt(u), Mathf.Clamp((y - LotKerbWidth) / Mathf.Max(1f, p.Depth - LotKerbWidth), 0f, 1f));
            float t = Mathf.Clamp((y - LotKerbWidth) / Mathf.Max(0.1f, LotRamp), 0f, 1f);
            return Mathf.Lerp(g, lot.FrontAt(u), 1f - t * t * (3f - 2f * t));
        }

        /// <summary>The way into a parking lot that takes road `road`'s pavement on `side` anywhere in [a, b] (arc metres), if any.</summary>
        public ParkingEntrance EntranceAt(int road, int side, float a, float b)
        {
            foreach (var lot in ParkingLotsLaid)
                foreach (var e in lot.Entrances)
                    if (e.Road == road && e.Side == side && e.From < b && a < e.To) return e;
            return null;
        }

        /// <summary>The lots, built: the asphalt inside their kerb (the road's own, as the street's parking bays are), the kerb
        /// and its pavement round the inside of the edge -- open where each way in's lane comes through, its pavements
        /// meeting the lane's -- and the stripes: retail's Line_Parking_0, its 0.5 x 5 m stripes at its 4 m, painted WHITE
        /// ("turned white"), off the road texture's own white like every line in the town.</summary>
        int BuildParkingLots(Node3D parent, RoadField roads)
        {
            if (parent == null || roads == null || ParkingLotsLaid.Count == 0) return 0;
            // (the main road's material, as the street's parking bays use: uvWhite is a white spot in ITS atlas -- the street
            // material's has none there, and the stripes came out the colour of the asphalt)
            var mat = roads.MaterialOfMaterial(MainMaterial);
            const float U0 = 4f / 256f, U1 = 56f / 256f;
            float tile = (U1 - U0) * 2f * 2f * Mathf.Max(1f, roads.RoadHalfWidthOfMaterial(MainMaterial));
            Vector2 UVof(Vector3 q) => new Vector2(U0 + PingPong(q.X / tile) * (U1 - U0), q.Z / tile);
            var uvWhite = new Vector2(0.2363f, 0.0664f);
            float Ground(float x, float z) => Terrain.Active != null ? Terrain.Active.SampleHeight(x, z) : (_heightAt != null ? _heightAt(x, z) : Centre.Y);
            Func<float, float, float> groundFn = Terrain.Active != null && !EditorSidewalk.NoGround ? (x, z) => Terrain.Active.SampleHeight(x, z) : null;
            float kerbH = KerbProud(roads);
            _lotKerbH = kerbH;
            SliverPoints = 0; SliverWidest = 0f;
            foreach (var lot in ParkingLotsLaid) if (lot.FrontY == null) SampleLotFront(lot, roads);
            // each way in's height where it meets the lot: its lane's end, as built -- against the lot's front there
            foreach (var lot in ParkingLotsLaid)
                foreach (var e in lot.Entrances)
                {
                    e.TopY = float.NaN;
                    if (e.Lane == null || e.Lane.Road < 0) continue;
                    float L = roads.RoadLength(e.Lane.Road);
                    if (roads.EvaluateAlong(e.Lane.Road, L, out var q, out _, snapTerrain: false)) e.TopY = q.Y + roads.RoadSurfaceOffset(e.Lane.Road);
                    if (ProbeEverything)
                    {
                        float planned = 0f; for (int k = 1; k < e.Lane.Pts.Count; k++) planned += Flat(e.Lane.Pts[k - 1], e.Lane.Pts[k]);
                        roads.EvaluateAlong(e.Lane.Road, 0f, out var q0, out _, snapTerrain: false);
                        var at = e.Mouth + e.Into * LotKerbWidth;
                        float front = LotSurfaceY(lot, at.X, at.Z, Ground(at.X, at.Z));
                        if (e.Edge != 0 && Terrain.Active != null)
                        {
                            // MEASURED: the ground against the way's asphalt and the lot's surface, across its last metres
                            var lat = new Vector3(-e.Into.Z, 0f, e.Into.X);
                            float off = roads.RoadSurfaceOffset(e.Lane.Road);
                            var sb = new System.Text.StringBuilder();
                            foreach (float inn in new[] { -3f, -2f, -1f, 0f, 0.5f, 1f, 1.4f })
                            {
                                float worst = float.MinValue, lotAt = 0f, laneAt = 0f;
                                foreach (float la in new[] { -3.3f, -2f, 0f, 2f, 3.3f })
                                {
                                    var w = e.Mouth + e.Into * inn + lat * la;
                                    float gy = Terrain.Active.SampleHeight(w.X, w.Z);
                                    float lane = RoadNear(roads, e.Lane.Road, w, out _, out _) + off;
                                    if (gy - lane > worst) { worst = gy - lane; lotAt = LotSurfaceY(lot, w.X, w.Z, gy); laneAt = lane; }
                                }
                                sb.Append($" {inn:0.0}:{worst * 100f:+0;-0}cm(lane {laneAt:0.00} lot {lotAt:0.00})");
                            }
                            Log.Print($"[towngrid]   way through a side at ({e.Mouth.X:0.0},{e.Mouth.Z:0.0}): ground over its asphalt, by metres in from the plot's edge:{sb}; planned {e.Lane.Pts[0].Y:0.00} -> {e.Lane.Pts[^1].Y:0.00}");
                        }
                        Log.Print($"[towngrid]   way in at ({e.Mouth.X:0.0},{e.Mouth.Z:0.0}): {2f * roads.RoadHalfWidth(e.Lane.Road):0.0} m wide, planned {planned:0.00} m from ({e.Lane.Pts[0].X:0.0},{e.Lane.Pts[0].Z:0.0}) to ({e.Lane.Pts[^1].X:0.0},{e.Lane.Pts[^1].Z:0.0}); built {L:0.00} m from ({q0.X:0.0},{q0.Z:0.0}) to ({q.X:0.0},{q.Z:0.0}); "
                                + $"its end {e.TopY:0.000} against the lot there {front:0.000} -- a step of {(e.TopY - front) * 100f:0.0} cm{(e.Edge != 0 ? $" (through its {(e.Edge == 2 ? "back" : "side")})" : "")}");
                    }
                }
            var ground = new SurfaceTool(); ground.Begin(Mesh.PrimitiveType.Triangles);
            var paint = new SurfaceTool(); paint.Begin(Mesh.PrimitiveType.Triangles);
            void Tri(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector2? uv)
            {
                bool keep = (c - a).Cross(b - a).Y >= 0f;
                var i1 = keep ? b : c; var i2 = keep ? c : b;
                st.SetNormal(Vector3.Up); st.SetUV(uv ?? UVof(a)); st.AddVertex(a);
                st.SetNormal(Vector3.Up); st.SetUV(uv ?? UVof(i1)); st.AddVertex(i1);
                st.SetNormal(Vector3.Up); st.SetUV(uv ?? UVof(i2)); st.AddVertex(i2);
            }
            // a quad on the lot's surface, in pieces about `cell` across so it follows the ground and the ramps, `lift` over it
            ParkingLot cur = null;
            void Quad(SurfaceTool st, Vector3 o, Vector3 u, Vector3 v, float lu, float lv, float lift, Vector2? uv, float cell = 1f)
            {
                int nu = Mathf.Max(1, Mathf.CeilToInt(lu / cell)), nv = Mathf.Max(1, Mathf.CeilToInt(lv / cell));
                Vector3 P(int i, int k) { var q = o + u * (lu * i / nu) + v * (lv * k / nv); q.Y = LotSurfaceY(cur, q.X, q.Z, Ground(q.X, q.Z)) + lift; return q; }
                for (int i = 0; i < nu; i++)
                    for (int k = 0; k < nv; k++)
                    {
                        Tri(st, P(i, k), P(i + 1, k), P(i + 1, k + 1), uv);
                        Tri(st, P(i, k), P(i + 1, k + 1), P(i, k + 1), uv);
                    }
            }
            int made = 0, kerbs = 0;
            foreach (var lot in ParkingLotsLaid)
            {
                cur = lot;
                var p = lot.Lot;
                Vector3 At(float x, float y) => p.Front + p.Along * x - p.Facing * y;
                var (xL, xR) = InnerX(p, lot.For);
                float yF = LotKerbWidth, yB = p.Depth - LotKerbWidth;
                // the asphalt, inside the kerb (and a few centimetres under its foot)
                Quad(ground, At(xL - 0.05f, yF - 0.05f), p.Along, -p.Facing, xR - xL + 0.1f, yB - yF + 0.1f, 0f, null);
                foreach (var (a, b) in lot.Stripes)
                {
                    var d = (b - a); float len = new Vector2(d.X, d.Z).Length(); if (len < 0.01f) continue;
                    var dn = new Vector3(d.X, 0f, d.Z) / len; var nn = new Vector3(-dn.Z, 0f, dn.X);
                    Quad(paint, a - nn * (StallStripe * 0.5f), dn, nn, len, StallStripe, 0.012f, uvWhite, 1f);
                }
                // ⭐ ITS KERB, round the inside of its edge: a pavement LotKerbWidth wide, its kerb facing the asphalt -- and along
                // the front, on out to the back of the street's pavement (strawberry 2026-10-14: "kill that gap between the
                // sidewalk and parking lot kerb"): the plot's front stands PlotSetback behind it, and the 0.5 m of grass between
                // the two showed as a strip. Its foot is at the street's height there (LotSurfaceY), so its top is the
                // pavement's and the two meet edge to edge, level.
                float frontW = LotKerbWidth + PlotSetback + p.Setback;
                var near = SidewalksNear(roads, lot);
                // the kerb line as one loop: the front left to right, the right side front to back, the back right to left, the
                // left side back to front -- each leg's way out of the lot and its pavement's width; a point on it by its arc s
                var legA = new[] { new Vector2(xL, yF), new Vector2(xR, yF), new Vector2(xR, yB), new Vector2(xL, yB) };
                var legB = new[] { new Vector2(xR, yF), new Vector2(xR, yB), new Vector2(xL, yB), new Vector2(xL, yF) };
                var legOut = new[] { new Vector2(0f, -1f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(-1f, 0f) };
                var legW = new[] { frontW, SideKerb(p, lot.For, 1), LotKerbWidth, SideKerb(p, lot.For, -1) };
                var cum = new float[5];
                for (int k = 0; k < 4; k++) cum[k + 1] = cum[k] + (legB[k] - legA[k]).Length();
                float perim = cum[4];
                (int leg, Vector2 q) OnLoop(float sArc)
                {
                    sArc = Mathf.PosMod(sArc, perim);
                    for (int k = 0; k < 4; k++)
                        if (sArc <= cum[k + 1] + 1e-4f)
                            return (k, legA[k].Lerp(legB[k], Mathf.Clamp((sArc - cum[k]) / Mathf.Max(1e-4f, cum[k + 1] - cum[k]), 0f, 1f)));
                    return (3, legB[3]);
                }
                float ArcOf(int leg, Vector2 q) => cum[leg] + (q - legA[leg]).Dot((legB[leg] - legA[leg]).Normalized());
                Vector3 Out3(Vector2 o) => (p.Along * o.X - p.Facing * o.Y).Normalized();
                Vector2 Loc(Vector3 w) { var r = w - p.Front; return new Vector2(r.Dot(p.Along), -r.Dot(p.Facing)); }
                // ...open across each way in or out, from where the kerb curves round into it on one side to the other: each
                // mouth's return ends up the way's kerb line (its far end), and from there the kerb turns a quarter circle onto
                // the lot's kerb line, its radius what is left of the way past the return -- worked in the way's own frame (lat
                // across it, inn into the lot from the plot's edge): the return's end, the curve's centre, its radius, and which
                // way along the edge is away from the way
                var gaps = new List<(float a, float b)>();
                var curves = new List<(Vector2 c0, Vector2 nIn, Vector2 tLat, Vector2 end, Vector2 centre, float r, float away)>();
                foreach (var e in lot.Entrances)
                {
                    var m = e.Lane?.A;
                    if (m == null || e.Lane.Road < 0) continue;            // (no way built: the kerb stays shut)
                    var c0 = Loc(e.Mouth);
                    var nIn = new Vector2(e.Into.Dot(p.Along), -e.Into.Dot(p.Facing)).Normalized();
                    var tLat = new Vector2(-nIn.Y, nIn.X);
                    float sa = float.MaxValue, sb = float.MinValue;
                    for (int c = 0; c < 2; c++)
                    {
                        var kp = m.Kerb[c];
                        if (kp == null || kp.Count == 0) continue;
                        var end = (kp[0] - m.Edge).Dot(m.Into) > (kp[^1] - m.Edge).Dot(m.Into) ? kp[0] : kp[^1];
                        var rel = Loc(end) - c0;
                        float lat = rel.Dot(tLat), inn = rel.Dot(nIn), away = lat < 0f ? -1f : 1f;
                        // (no wider than the way's own pavement, which runs down to it where the way is longer than the returns)
                        float r = Mathf.Clamp(LotKerbWidth - inn, 0.3f, Mathf.Max(0.3f, LaneWalkWidth));
                        curves.Add((c0, nIn, tLat, new Vector2(lat, LotKerbWidth - r), new Vector2(lat + away * r, LotKerbWidth - r), r, away));
                        float sg = ArcOf(e.Edge, c0 + tLat * (lat + away * r) + nIn * LotKerbWidth);   // where the curve meets the kerb line
                        sa = Mathf.Min(sa, sg); sb = Mathf.Max(sb, sg);
                    }
                    if (sb > sa) gaps.Add((sa, sb));
                }
                gaps.Sort((u, v) => u.a.CompareTo(v.a));
                // the kerb as runs round the loop between the gaps (with none, one run all the way round, from the back's middle)
                var runs = new List<(float a, float b)>();
                if (gaps.Count == 0) { float s0 = (cum[2] + cum[3]) * 0.5f; runs.Add((s0, s0 + perim)); }
                else
                    for (int k = 0; k < gaps.Count; k++)
                    {
                        float a = gaps[k].b, b = k + 1 < gaps.Count ? gaps[k + 1].a : gaps[0].a + perim;
                        if (b - a > 0.05f) runs.Add((a, b));
                    }
                void Keep(MeshInstance3D mi)
                {
                    parent.AddChild(mi);
                    var body = new StaticBody3D { Name = mi.Name + "_body", CollisionLayer = 1u << 0 };
                    body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Concrete);
                    body.AddChild(new CollisionShape3D { Shape = mi.Mesh.CreateTrimeshShape() });
                    mi.AddChild(body);
                    kerbs++;
                }
                foreach (var (ra, rb) in runs)
                {
                    // every 2 m along it and on each corner it turns, so the kerb follows the ground; each point's way out of the
                    // lot -- at a corner, out to where the two legs' back edges cross (the front's wider than the sides)
                    var marks = new List<(float s, int corner)> { (ra, -1), (rb, -1) };
                    for (int j = 0; j < 8; j++)
                    {
                        float cb = cum[j % 4] + (j / 4) * perim;
                        if (cb > ra + 1e-3f && cb < rb - 1e-3f) marks.Add((cb, j % 4));
                    }
                    marks.Sort((u, v) => u.s.CompareTo(v.s));
                    var pts = new List<Vector3>(); var outs = new List<Vector3>(); var widths = new List<float>();
                    var kerbsAt = new List<float>(); var surf = new List<float>();
                    for (int k = 0; k + 1 < marks.Count; k++)
                    {
                        int n = Mathf.Max(1, Mathf.CeilToInt((marks[k + 1].s - marks[k].s) / 2f));
                        for (int q = (k == 0 ? 0 : 1); q <= n; q++)
                        {
                            float sq = Mathf.Lerp(marks[k].s, marks[k + 1].s, (float)q / n);
                            int corner = q == n ? marks[k + 1].corner : q == 0 ? marks[k].corner : -1;
                            var (leg, lq) = OnLoop(corner >= 0 ? cum[corner] : sq);
                            var w = At(lq.X, lq.Y);
                            w.Y = LotSurfaceY(lot, w.X, w.Z, Ground(w.X, w.Z));
                            // ⭐ ...and on across a sliver to another pavement just beyond (FillSlivers): a side or back leg's pavement
                            // runs over the bare ground between and meets it, its far edge at that pavement's height
                            (float gap, float top) Merge(int lg) => lg == 0 ? (0f, float.NaN)
                                : GapToSidewalk(roads, lot, w + Out3(legOut[lg]) * legW[lg], Out3(legOut[lg]), near);
                            float Top(float t) => float.IsNaN(t) ? kerbH : t - w.Y;
                            if (corner >= 0)
                            {
                                int prev = (corner + 3) % 4;          // (a rectangle's corners are square)
                                var (gp, tp) = Merge(prev); var (gc, tc) = Merge(corner);
                                var back = legOut[prev] * (legW[prev] + gp) + legOut[corner] * (legW[corner] + gc);
                                pts.Add(w); outs.Add(Out3(back)); widths.Add(back.Length());
                                kerbsAt.Add(kerbH); surf.Add(Top(gp >= gc ? tp : tc));
                                continue;
                            }
                            var (g, tg) = Merge(leg);
                            pts.Add(w); outs.Add(Out3(legOut[leg])); widths.Add(legW[leg] + g);
                            kerbsAt.Add(kerbH); surf.Add(Top(tg));
                        }
                    }
                    if (pts.Count < 2) continue;
                    var mi = EditorSidewalk.BuildRibbon(pts, outs, LotKerbWidth, kerbH, 0.6f, $"Parking_Kerb_{made}_{kerbs}", groundFn, kerbsAt, widths, surf);
                    if (mi != null) Keep(mi);
                }
                // ⭐ AND ROUND INTO EACH WAY, CURVED (strawberry 2026-10-14: "add a curve for the sidewalk going into the parking
                // lot"): a quarter disc of pavement fanned onto its centre, from the way's kerb round onto the lot's kerb line,
                // as wide as the way's own pavement. Through the front that is exactly what is left past the mouth's return
                // (the lot's kerb, less the return's metre past the back of the street's pavement), so the return, the curve and
                // the front kerb meet square and tile the corner; through a side, across grass, the way's own pavements run down
                // to it (BuildLanes). The asphalt it curves away from is filled under it.
                int steps = Mathf.Max(4, Mathf.CeilToInt(90f / ArcFacetDegrees));
                foreach (var (c0, nIn, tLat, end, centre, r, away) in curves)
                {
                    Vector3 FromWay(float la, float inn) { var q = c0 + tLat * la + nIn * inn; var w = At(q.X, q.Y); w.Y = LotSurfaceY(lot, w.X, w.Z, Ground(w.X, w.Z)); return w; }
                    var C = FromWay(centre.X, centre.Y);
                    float top = C.Y + kerbH;                               // the fan's one apex, one height
                    var pts = new List<Vector3>(); var outs = new List<Vector3>(); var widths = new List<float>();
                    var kerbsAt = new List<float>(); var surf = new List<float>();
                    for (int i = 0; i <= steps; i++)
                    {
                        float ang = Mathf.Pi * 0.5f * i / steps;
                        var w = FromWay(centre.X - away * r * Mathf.Cos(ang), centre.Y + r * Mathf.Sin(ang));
                        var toC = new Vector3(C.X - w.X, 0f, C.Z - w.Z); float l = toC.Length();
                        pts.Add(w); outs.Add(l > 1e-4f ? toC / l : -p.Facing); widths.Add(Mathf.Max(l, 0.01f));
                        kerbsAt.Add(kerbH); surf.Add(top - w.Y);
                    }
                    var mi = EditorSidewalk.BuildRibbon(pts, outs, r, kerbH, 0.6f, $"Parking_KerbCurve_{made}_{kerbs}", groundFn, kerbsAt, widths, surf);
                    if (mi != null) Keep(mi);
                    // the asphalt outside the curve, fanned from the corner it rounds off -- a hair under the way's own
                    var K = FromWay(end.X, LotKerbWidth); K.Y -= 0.005f;
                    for (int i = 0; i + 1 < pts.Count; i++) Tri(ground, K, pts[i] - Vector3.Up * 0.005f, pts[i + 1] - Vector3.Up * 0.005f, null);
                }
                // ⭐ THE DIVIDERS between back-to-back rows: an island of the lot's pavement, kerbed both sides, from its rounded
                // nose at the cross road to the back kerb, which it joins -- one ribbon up its left side, round the nose and down
                // its right, each point's surface reaching to the island's middle line (the nose fanned onto its centre)
                foreach (var (dx0, dy0, dx1, dy1) in lot.Dividers)
                {
                    float cx = (dx0 + dx1) * 0.5f, rr = (dx1 - dx0) * 0.5f, ny = dy0 + rr;
                    var pts = new List<Vector3>(); var outs = new List<Vector3>(); var widths = new List<float>();
                    var kerbsAt = new List<float>(); var surf = new List<float>();
                    void Add(float x, float y, float bx, float by)
                    {
                        var w = At(x, y); w.Y = LotSurfaceY(lot, w.X, w.Z, Ground(w.X, w.Z));
                        var b = At(bx, by); float bTop = LotSurfaceY(lot, b.X, b.Z, Ground(b.X, b.Z)) + kerbH;
                        var d = new Vector3(b.X - w.X, 0f, b.Z - w.Z); float l = d.Length();
                        pts.Add(w); outs.Add(l > 1e-4f ? d / l : -p.Facing); widths.Add(Mathf.Max(l, 0.01f)); kerbsAt.Add(kerbH); surf.Add(bTop - w.Y);
                    }
                    int ns = Mathf.Max(1, Mathf.CeilToInt((dy1 - ny) / 2f));
                    for (int k = 0; k <= ns; k++) { float y = Mathf.Lerp(dy1, ny, (float)k / ns); Add(dx0, y, cx, y); }
                    int na = Mathf.Max(6, Mathf.CeilToInt(180f / ArcFacetDegrees));
                    for (int k = 1; k < na; k++) { float ang = Mathf.Pi * k / na; Add(cx - rr * Mathf.Cos(ang), ny - rr * Mathf.Sin(ang), cx, ny); }
                    for (int k = 0; k <= ns; k++) { float y = Mathf.Lerp(ny, dy1, (float)k / ns); Add(dx1, y, cx, y); }
                    var mi = EditorSidewalk.BuildRibbon(pts, outs, rr, kerbH, 0.6f, $"Parking_Divider_{made}_{kerbs}", groundFn, kerbsAt, widths, surf);
                    if (mi != null) Keep(mi);
                }
            }
            var gm = ground.Commit();
            if (gm != null && gm.GetSurfaceCount() > 0)
            {
                var mi = new MeshInstance3D { Name = "Parking_Lots", Mesh = gm };
                if (mat != null) mi.MaterialOverride = mat;
                parent.AddChild(mi);
                var body = new StaticBody3D { Name = mi.Name + "_body", CollisionLayer = 1u << 0 };
                body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Concrete);
                body.AddChild(new CollisionShape3D { Shape = gm.CreateTrimeshShape() });
                mi.AddChild(body);
                made++;
            }
            var pm = paint.Commit();
            if (pm != null && pm.GetSurfaceCount() > 0)
            {
                var mi = new MeshInstance3D { Name = "Parking_Lots_Paint", Mesh = pm };
                if (mat != null) mi.MaterialOverride = mat;
                parent.AddChild(mi);
                made++;
            }
            int lanes = 0; foreach (var lot in ParkingLotsLaid) foreach (var e in lot.Entrances) if (e.Lane != null && e.Lane.Road >= 0) lanes++;
            if (FillSlivers) Log.Print($"[towngrid] slivers: lots' kerbs carried across to the pavement beyond at {SliverPoints} points, the widest gap {SliverWidest:0.00} m (at most {SliverMax:0.0})"
                                     + (SliverPoints > 0 ? $"; the first at ({SliverFirst.X:0.0},{SliverFirst.Z:0.0}), the widest at ({SliverWidestAt.X:0.0},{SliverWidestAt.Z:0.0})" : ""));
            Log.Print($"[towngrid] parking lots built: {ParkingLotsLaid.Count} lots, {kerbs} kerb runs round them, {lanes} ways in built as lanes; "
                    + $"{MouthsNearCorner} kept off a junction's corner ({ParkingWaysNearCorner} lots had to open their only one there)");
            return made + kerbs;
        }
        /// <summary>The streets and lanes whose pavements run near a lot (within SliverMax and a carriageway of its edge).</summary>
        (List<int> roads, List<LanePlan> lanes, List<(Vector3 node, List<Vector3> kerb)> corners) SidewalksNear(RoadField roads, ParkingLot lot)
        {
            var rs = new List<int>(); var ls = new List<LanePlan>(); var cs = new List<(Vector3, List<Vector3>)>();
            if (!FillSlivers) return (rs, ls, cs);
            var c = lot.Lot.Corners();
            var mid = (c[0] + c[1] + c[2] + c[3]) * 0.25f;
            float reach = Mathf.Sqrt(lot.Lot.Width * lot.Lot.Width + lot.Lot.Depth * lot.Lot.Depth) * 0.5f + SliverMax + _walkWidth + 12f;
            foreach (int r in Built) { RoadNear(roads, r, mid, out _, out float off); if (off <= reach) rs.Add(r); }
            foreach (var lp in LanePlans)
            {
                if (lp.Pts == null || lp.Pts.Count < 2 || lp.Road < 0) continue;
                if (lp.OpenB && lp.Pts.Count > 0 && lot.Entrances.Exists(e => e.Lane == lp)) continue;     // (its own ways)
                foreach (var q in lp.Pts) if (Flat(q, mid) <= reach) { ls.Add(lp); break; }
            }
            // ...and the junctions' corner pavements, kerb line and node
            foreach (var (node, kerb) in LastCorners)
                if (kerb != null && kerb.Count >= 2) foreach (var q in kerb) if (Flat(q, mid) <= reach) { cs.Add((node, kerb)); break; }
            return (rs, ls, cs);
        }

        /// <summary>⭐ HOW FAR OUT FROM A LOT'S EDGE ANOTHER PAVEMENT BEGINS (`p` on its plot's edge, `n` straight out), over nothing
        /// but bare ground, within SliverMax -- and that pavement's top there: a street's pavement, a lane's, another lot's kerb.
        /// (0, NaN) where anything else comes first -- a yard, a carriageway, a way's mouth -- or nothing does.</summary>
        (float gap, float top) GapToSidewalk(RoadField roads, ParkingLot lot, Vector3 p, Vector3 n, (List<int> roads, List<LanePlan> lanes, List<(Vector3 node, List<Vector3> kerb)> corners) near)
        {
            if (!FillSlivers) return (0f, float.NaN);
            for (float t = 0.05f; t <= SliverMax + 1e-3f; t += 0.1f)
            {
                var q = p + n * t;
                foreach (var o in Plots)
                {
                    if (ReferenceEquals(o, lot.Lot) || !InsidePoly(o.Boundary(), q)) continue;
                    if (o.Kind != PlotKind.Parking) return (0f, float.NaN);            // somebody's yard
                    var other = ParkingLotsLaid.Find(l => ReferenceEquals(l.Lot, o));
                    if (other == null) return (0f, float.NaN);
                    Note(t, q); return (t, LotSurfaceY(other, q.X, q.Z, LotGround(q.X, q.Z)) + _lotKerbH);
                }
                foreach (int r in near.roads)
                {
                    float sy = RoadNear(roads, r, q, out float along, out float off), half = roads.RoadHalfWidth(r);
                    if (off < half + PavementGap - 0.05f) return (0f, float.NaN);       // its carriageway
                    float back = half + PavementGap + _walkWidth + Mathf.Max(BayBack(r, 1, along), BayBack(r, -1, along));
                    if (off > back + 0.02f) continue;
                    if (MouthAt(r, 1, along - 0.5f, along + 0.5f) != null || MouthAt(r, -1, along - 0.5f, along + 0.5f) != null) return (0f, float.NaN);
                    Note(t, q); return (t, sy + PavementKerb);
                }
                foreach (var lp in near.lanes)
                {
                    float best = float.MaxValue, y = q.Y;
                    for (int k = 1; k < lp.Pts.Count; k++)
                    {
                        var a = lp.Pts[k - 1]; var b = lp.Pts[k];
                        var ab = new Vector2(b.X - a.X, b.Z - a.Z); float l2 = Mathf.Max(1e-6f, ab.LengthSquared());
                        float u = Mathf.Clamp(((q.X - a.X) * ab.X + (q.Z - a.Z) * ab.Y) / l2, 0f, 1f);
                        float d = new Vector2(a.X + ab.X * u - q.X, a.Z + ab.Y * u - q.Z).Length();
                        if (d < best) { best = d; y = Mathf.Lerp(a.Y, b.Y, u); }
                    }
                    float lh = lp.Half > 0f ? lp.Half : LaneHalf;
                    if (best < lh + PavementGap - 0.05f) return (0f, float.NaN);        // its carriageway
                    if (best > lh + PavementGap + LaneWalkWidth + 0.02f) continue;
                    if (lp.OpenB && lp.A != null && lp.EndPlane == null) return (0f, float.NaN);   // (a way in through a front: no pavements)
                    Note(t, q); return (t, y + PavementKerb);
                }
                foreach (var (node, kerb) in near.corners)
                {
                    // a corner's pavement: within its width of its kerb line, on the far side of it from the junction
                    float best = float.MaxValue; var np = kerb[0];
                    for (int k = 1; k < kerb.Count; k++)
                    {
                        var a = kerb[k - 1]; var b = kerb[k];
                        var ab = new Vector2(b.X - a.X, b.Z - a.Z); float l2 = Mathf.Max(1e-6f, ab.LengthSquared());
                        float u = Mathf.Clamp(((q.X - a.X) * ab.X + (q.Z - a.Z) * ab.Y) / l2, 0f, 1f);
                        var c = a.Lerp(b, u);
                        float d = new Vector2(c.X - q.X, c.Z - q.Z).Length();
                        if (d < best) { best = d; np = c; }
                    }
                    if (best > _walkWidth + 0.02f) continue;
                    if (Flat(q, node) < Flat(np, node) - 0.05f) return (0f, float.NaN);   // the junction's asphalt
                    Note(t, q); return (t, np.Y + PavementKerb);
                }
            }
            return (0f, float.NaN);
        }
        void Note(float t, Vector3 at) { if (SliverPoints++ == 0) SliverFirst = at; if (t > SliverWidest) { SliverWidest = t; SliverWidestAt = at; } }
        public Vector3 SliverFirst, SliverWidestAt;

        /// <summary>How far a lot's asphalt stands over the ground under it.</summary>
        public float LotLift = 0.04f;

        /// <summary>The lots' props: their blocks, if they have any -- retail's Block_Parking_0 at the head of a stall, a Z-up
        /// mesh stood up at 270 about X like every vanilla prop, its 3 m length (local X) across the stall -- and ⭐ THEIR
        /// STREET LIGHTS (strawberry 2026-10-14: "add streetlight support for parking lots too"): the streets' own lamp,
        /// down the middle of each divider, its arm over one aisle then the other, and along the kerbs its outer rows face --
        /// an across lot's sides, an along lot's back -- their arms over the stalls toward the aisle.
        /// About LotLampSpacing apart, on the kerb's pavement a metre out from its edge, never in a way in's opening.</summary>
        public int PlaceParkingProps(EditorObjects objs, List<Node3D> into = null)
        {
            if (objs == null) return 0;
            int blocks = 0, lamps = 0;
            var terr = Terrain.Active;
            float Ground(float x, float z) => terr != null ? terr.SampleHeight(x, z) : (_heightAt != null ? _heightAt(x, z) : Centre.Y);
            foreach (var lot in ParkingLotsLaid)
                foreach (var (at, along) in lot.Blocks)
                {
                    var q = at;
                    q.Y = LotSurfaceY(lot, q.X, q.Z, Ground(q.X, q.Z));
                    var node = objs.Place("Block_Parking_0", q, HedgeBasis(along, 1f, 1f, false));
                    if (node == null) continue;
                    into?.Add(node); blocks++;
                }
            if (StreetLights)
                foreach (var lot in ParkingLotsLaid)
                {
                    var p = lot.Lot;
                    Vector3 At(float x, float y) => p.Front + p.Along * x - p.Facing * y;
                    var (xL, xR) = InnerX(p, lot.For);
                    float yF = LotKerbWidth, yB = p.Depth - LotKerbWidth;
                    // each way's opening, by edge and middle, to keep the lamps out of
                    var ways = new List<(int edge, float at)>();
                    foreach (var e in lot.Entrances)
                    {
                        if (e.Lane == null || e.Lane.Road < 0) continue;
                        var r = e.Mouth - p.Front; float lx = r.Dot(p.Along), ly = -r.Dot(p.Facing);
                        ways.Add((e.Edge, e.Edge == 0 || e.Edge == 2 ? lx : ly));
                    }
                    bool InOpening(int edge, float at) { foreach (var w in ways) if (w.edge == edge && Mathf.Abs(w.at - at) < ParkingEntranceHalf + 4f) return true; return false; }
                    // spots along a line, evenly, about LotLampSpacing apart
                    IEnumerable<float> Spots(float a, float b)
                    {
                        float len = b - a; if (len < 1f) yield break;
                        int n = Mathf.Max(1, Mathf.RoundToInt(len / LotLampSpacing));
                        for (int k = 0; k < n; k++) yield return a + (k + 0.5f) * len / n;
                    }
                    var spots = new List<(float x, float y, Vector2 arm)>();
                    foreach (var (dx0, dy0, dx1, dy1) in lot.Dividers)
                    {
                        float cx = (dx0 + dx1) * 0.5f; int k = 0;
                        foreach (float y in Spots(dy0 + (dx1 - dx0) * 0.5f + 1f, dy1 - 1f)) spots.Add((cx, y, new Vector2((k++ % 2 == 0) ? -1f : 1f, 0f)));
                    }
                    if (!lot.Plan.Along)
                    {
                        // an across lot's outer rows face its sides
                        float inset = Mathf.Min(1f, LotKerbWidth * 0.66f), y0 = yF + (lot.Plan.Aisles >= 2 ? LotAisle : 0f);
                        foreach (float y in Spots(y0, yB)) { if (!InOpening(3, y)) spots.Add((xL - inset, y, new Vector2(1f, 0f))); if (!InOpening(1, y)) spots.Add((xR + inset, y, new Vector2(-1f, 0f))); }
                    }
                    else
                    {
                        // an along lot's back row faces its back
                        float inset = Mathf.Min(1f, LotKerbWidth * 0.66f);
                        foreach (float x in Spots(xL, xR)) if (!InOpening(2, x)) spots.Add((x, yB + inset, new Vector2(0f, -1f)));
                    }
                    foreach (var (x, y, arm) in spots)
                    {
                        var at = At(x, y);
                        at.Y = LotSurfaceY(lot, at.X, at.Z, Ground(at.X, at.Z)) + _lotKerbH;
                        var armW = (p.Along * arm.X - p.Facing * arm.Y).Normalized();
                        float yaw = ProcIsland.YawForDir(armW.X, -armW.Z);
                        var basis = SplineTiling.SeatedBasis(terr, at, yaw, 6f, out _);
                        var node = objs.Place(StreetLightProp, at, basis);
                        if (node == null) continue;
                        into?.Add(node); lamps++;
                    }
                }
            if (blocks + lamps > 0) Log.Print($"[towngrid] parking lot props: {lamps} street lights, {blocks} blocks in {ParkingLotsLaid.Count} lots");
            return blocks + lamps;
        }
        /// <summary>The lots' kerbs' height over their asphalt, as BuildParkingLots built them.</summary>
        float _lotKerbH = 0.18f;

        /// <summary>How close a lamp, hydrant, signal or sign may stand to a parking meter. ⚠ 2 m, not the 1.2 it was: a meter
        /// 1.33 m off a lamp's pole passed, and next to a 7 m pole that is ON it as far as anyone looking is concerned.</summary>
        public static float MeterClear = float.TryParse(System.Environment.GetEnvironmentVariable("UG_TOWNMETERCLEAR"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float mc) ? mc : 2f;
        /// <summary>What the last ClearMeterClashes found and took down.</summary>
        public int MetersCleared;

        /// <summary>⭐ NOTHING STANDS ON A METER (strawberry 2026-10-15: "prevent street lights or hydrants being placed on top of
        /// parking meters"). PlaceLights' meters already keep MeterClear off the street lamps and hydrants it puts up first --
        /// but not off anything placed AFTER them: the signal masts and stop signs at the junctions, and the parking lots'
        /// own lamps. So once the town is up, a meter with any of those within MeterClear of it gives way: the space keeps
        /// its paint and loses its meter, the rule the meters already followed. UG_TOWNMETERCLASH=1 only counts (the control).
        /// Returns the meters taken down.</summary>
        public int ClearMeterClashes(EditorObjects objs)
        {
            MetersCleared = 0;
            if (objs == null) return 0;
            var standing = new List<(Vector3 at, string what)>();
            foreach (var what in new[] { StreetLightProp, HydrantProp, TrafficLightProp, StopSignProp })
                foreach (var n in objs.PlacedOfNodes(what)) standing.Add((n.GlobalPosition, what));
            var drop = new List<Node3D>();
            var by = new Dictionary<string, int>();
            Vector3 firstAt = Vector3.Zero;
            var nearest = new List<string>();
            foreach (var m in objs.PlacedOfNodes(ParkingMeterProp))
            {
                var mp = m.GlobalPosition;
                float nd = float.MaxValue; string nw = "";
                foreach (var (q, what) in standing) { float dq = Flat(q, mp); if (dq < nd) { nd = dq; nw = what; } }
                if (nd < 4f) nearest.Add($"{nd:0.00} m {nw} at ({mp.X:0.0},{mp.Z:0.0})");
                foreach (var (q, what) in standing)
                {
                    if (Flat(q, mp) >= MeterClear) continue;
                    if (drop.Count == 0) firstAt = mp;
                    drop.Add(m);
                    by[what] = (by.TryGetValue(what, out int c) ? c : 0) + 1;
                    break;
                }
            }
            bool control = System.Environment.GetEnvironmentVariable("UG_TOWNMETERCLASH") == "1";
            if (!control && drop.Count > 0) objs.RemovePlaced(drop);
            MetersCleared = control ? 0 : drop.Count;
            Log.Print($"[towngrid] meters: {drop.Count} stood within {MeterClear:0.#} m of "
                    + (by.Count == 0 ? "nothing" : string.Join(", ", System.Linq.Enumerable.Select(by, kv => $"{kv.Value} x {kv.Key}")))
                    + (drop.Count > 0 ? $" (the first at {firstAt.X:0.0},{firstAt.Z:0.0}) -- {(control ? "LEFT (control)" : "taken down")}" : ""));
            if (nearest.Count > 0) Log.Print($"[towngrid] meters within 4 m of something standing: {string.Join("; ", nearest)}");
            return MetersCleared;
        }
    }
}
