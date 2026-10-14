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
                    var plan = new LotPlan { Aisles = k, FrontRow = front, Width = 2f * Wk + 2f * H + k * (2f * Sd + A) };
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
                    var plan = new LotPlan { Along = true, FrontRow = front, PerRow = per, Depth = depth, Width = 2f * Wk + per * Sw + 2f * RowEndGap };
                    if (plan.Width - 2f * Wk < minInner) continue;
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
            var lot = new ParkingLot { Lot = p, Plan = plan };
            LayOutLot(lot, plan, ChooseWaysIn(null, plan, p, null));
            return lot.Spaces;
        }

        /// <summary>Where a way in may come into a lot (m along its front from the middle), best first: an across lot's at its
        /// aisles, the middle ones first; an along lot's at an end -- the end away from the building first, so it is driven
        /// past the stalls to them -- or else the middle.</summary>
        List<float> WayInSpots(LotPlan plan, Plot p, Plot forPlot)
        {
            var spots = new List<float>();
            float xL = -p.Width * 0.5f + LotKerbWidth, xR = p.Width * 0.5f - LotKerbWidth;
            if (plan.Along)
            {
                float inset = WayInEndStub + Mathf.Max(LaneKerbRadius, LaneWalkWidth) + ParkingEntranceHalf + PavementGap;
                bool forOnLeft = forPlot != null && (forPlot.Front - p.Front).Dot(p.Along) < 0f;
                spots.Add(forOnLeft ? xR - inset : xL + inset);
                spots.Add(forOnLeft ? xL + inset : xR - inset);
                spots.Add(0f);
                return spots;
            }
            for (int i = 0; i < plan.Aisles; i++) spots.Add(xL + HeadStrip + StallDepth + LotAisle * 0.5f + i * (2f * StallDepth + LotAisle));
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
                if (lot.Spaces >= n) return lot;
            }
            return null;
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
        void LayOutLot(ParkingLot lot, LotPlan plan, List<float> ways)
        {
            var p = lot.Lot;
            Vector3 At(float x, float y) => p.Front + p.Along * x - p.Facing * y;      // x along the street, y back from it
            float xL = -p.Width * 0.5f + LotKerbWidth, xR = p.Width * 0.5f - LotKerbWidth, yF = LotKerbWidth, yB = p.Depth - LotKerbWidth;
            float H = HeadStrip, Sd = StallDepth, A = LotAisle, th = ParkingEntranceHalf;
            lot.Stalls.Clear(); lot.Stripes.Clear(); lot.Blocks.Clear(); lot.Entrances.Clear(); lot.Roads.Clear();
            // a row of stalls: along x (or y), as many as fit between stripe middles a..b, centred; its heads on the line `head`
            // across it and its open side -- the road's edge -- on `open`; a block at each head that faces the lot's kerb
            void Row(bool alongX, float a, float b, float head, float open, bool kerbAhead)
            {
                int n = Mathf.FloorToInt((b - a) / StallWidth + 1e-4f);
                if (n < 1) return;
                float s0 = a + (b - a - n * StallWidth) * 0.5f, into = Mathf.Sign(head - open), mid = (head + open) * 0.5f;
                Vector3 P(float run, float acr) => alongX ? At(run, acr) : At(acr, run);
                var runDir = alongX ? p.Along : -p.Facing;
                var intoDir = alongX ? -p.Facing * into : p.Along * into;
                for (int k = 0; k < n; k++)
                {
                    float c = s0 + (k + 0.5f) * StallWidth;
                    lot.Stalls.Add((P(c, mid), runDir, intoDir));
                    if (kerbAhead && ParkingBlocks) lot.Blocks.Add((P(c, head + into * BlockPast), runDir));
                }
                for (int k = 0; k <= n; k++) { float sx = s0 + k * StallWidth; lot.Stripes.Add((P(sx, head), P(sx, open))); }
            }
            // a row along the front kerb, cut where each way in comes through it -- and the way in's road on through it
            void FrontRow(float roadFrom)
            {
                var cuts = new List<float>(ways); cuts.Sort();
                float a = xL + RowEndGap, cut = th + StallStripe * 0.5f;
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
                float y1 = y0 + plan.PerRow * StallWidth, x = xL + H;
                for (int i = 0; i < plan.Aisles; i++)
                {
                    Row(false, y0, y1, x, x + Sd, i == 0);
                    lot.Roads.Add((x + Sd, yF, x + Sd + A, yB));
                    Row(false, y0, y1, x + 2f * Sd + A, x + Sd + A, i == plan.Aisles - 1);
                    x += 2f * Sd + A;
                }
            }
            else
            {
                float aisle0 = yF + (plan.FrontRow ? H + Sd : 0f);
                if (plan.FrontRow) FrontRow(aisle0);
                lot.Roads.Add((xL, aisle0, xR, aisle0 + A));
                Row(true, xL + RowEndGap, xR - RowEndGap, aisle0 + A + Sd, aisle0 + A, true);
            }
            foreach (float xe in ways)
                lot.Entrances.Add(new ParkingEntrance { Road = p.Road, Side = p.RoadSide, Mouth = At(xe, 0f), Into = -p.Facing,
                                                        Width = 2f * ParkingEntranceHalf, Lot = lot });
            lot.Spaces = lot.Stalls.Count;
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
            float yF = LotKerbWidth;
            for (int i = 0; i < roads.Count; i++)
                foreach (var e in lot.Entrances)
                {
                    var (xe, _) = Local(e.Mouth);
                    var r = roads[i];
                    if (!reached[i] && r.y0 <= yF + 0.01f && r.x0 <= xe && xe <= r.x1) { reached[i] = true; queue.Enqueue(i); }
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
            ParkingWaysNearCorner = 0;
            bool Open(ParkingEntrance e, float clear)
            {
                var m = OpenMouth(roads, e.Mouth, -e.Into, ParkingEntranceHalf, clear);
                if (m == null) return false;
                var end = e.Mouth + e.Into * LotKerbWidth;        // through the lot's kerb, onto its asphalt
                var line = RoundedPath(m.Edge, m.Edge, end, 0f, 2f);
                if (line == null || line.Count < 2) return false;
                for (int q = 0; q < line.Count; q++) line[q] = new Vector3(line[q].X, m.Edge.Y, line[q].Z);
                LaneMouths.Add(m);
                e.Lane = new LanePlan { Pts = line, A = m, OpenB = true, Material = ParkingEntranceMaterial, Half = ParkingEntranceHalf };
                LanePlans.Add(e.Lane);
                e.From = m.From; e.To = m.To;
                return true;
            }
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
        }
        /// <summary>Lots whose only way in had to open nearer a junction's corner than ParkingCornerClear.</summary>
        public int ParkingWaysNearCorner;

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
                SampleLotFront(lot, roads);
                // the ground's line along the back edge: least squares over a sample a metre
                double su = 0, sy = 0, suu = 0, suy = 0; int ns = 0;
                for (float u = -p.Width * 0.5f; u <= p.Width * 0.5f + 1e-3f; u += 1f)
                {
                    var q = p.Front + p.Along * u - p.Facing * p.Depth;
                    float gy = terr.SampleHeight(q.X, q.Z);
                    su += u; sy += gy; suu += (double)u * u; suy += (double)u * gy; ns++;
                }
                if (ns < 2) continue;
                double den = ns * suu - su * su;
                lot.BackB = den > 1e-6 ? (float)((ns * suy - su * sy) / den) : 0f;
                lot.BackA = (float)(sy / ns) - lot.BackB * (float)(su / ns);
                lot.Graded = true;
                // (measured: how far the surface stands off the ground it replaces, at the lot's middle row of samples)
                for (float u = -p.Width * 0.5f; u <= p.Width * 0.5f; u += 2f)
                    for (float y = 0f; y <= p.Depth; y += 2f)
                    {
                        var q = p.Front + p.Along * u - p.Facing * y;
                        float off = LotSurfaceY(lot, q.X, q.Z, 0f) - LotGradeDrop - terr.SampleHeight(q.X, q.Z);
                        worstFill = Mathf.Max(worstFill, off); worstCut = Mathf.Min(worstCut, off);
                    }
                terr.ConformToSurface(p.Front, p.Along, -p.Facing, -p.Width * 0.5f, p.Width * 0.5f, -(PlotSetback + p.Setback), p.Depth,
                                      (x, z) => LotSurfaceY(lot, x, z, 0f) - LotGradeDrop, Terrain.GridUnit, LotGradeFeather, beforeV0: false,
                                      keepOut: (x, z) => roads.DeepestRoadOverlap(new Vector3(x, 0f, z), 1f, out _, out _) || LaneAt(new Vector3(x, 0f, z)));
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
                        float front = lot.FrontAt((e.Mouth - lot.Lot.Front).Dot(lot.Lot.Along));
                        Log.Print($"[towngrid]   way in at ({e.Mouth.X:0.0},{e.Mouth.Z:0.0}): {2f * roads.RoadHalfWidth(e.Lane.Road):0.0} m wide, planned {planned:0.00} m from ({e.Lane.Pts[0].X:0.0},{e.Lane.Pts[0].Z:0.0}) to ({e.Lane.Pts[^1].X:0.0},{e.Lane.Pts[^1].Z:0.0}); built {L:0.00} m from ({q0.X:0.0},{q0.Z:0.0}) to ({q.X:0.0},{q.Z:0.0}); "
                                + $"its end {e.TopY:0.000} against the lot's front {front:0.000} -- a step of {(e.TopY - front) * 100f:0.0} cm");
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
                float xL = -p.Width * 0.5f + LotKerbWidth, xR = p.Width * 0.5f - LotKerbWidth, yF = LotKerbWidth, yB = p.Depth - LotKerbWidth;
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
                // ...open across each way in, from where the kerb curves round into it on one side to the other: each mouth's
                // return ends up the way in's kerb line (its far end), and from there the kerb turns a quarter circle onto
                // the front kerb's line, its radius what is left of the way in -- in lot coordinates, the return's end, the
                // curve's centre, its radius, and which way along the front is away from the way in
                var throats = new List<(float a, float b)>();
                var curves = new List<(Vector2 end, Vector2 centre, float r, float away)>();
                foreach (var e in lot.Entrances)
                {
                    var m = e.Lane?.A;
                    if (m == null || e.Lane.Road < 0) continue;            // (no way in built: the kerb stays shut)
                    float xe = (e.Mouth - p.Front).Dot(p.Along), ta = xe, tb = xe;
                    for (int c = 0; c < 2; c++)
                    {
                        var kp = m.Kerb[c];
                        if (kp == null || kp.Count == 0) continue;
                        var end = (kp[0] - m.Edge).Dot(m.Into) > (kp[^1] - m.Edge).Dot(m.Into) ? kp[0] : kp[^1];
                        var rel = end - p.Front;
                        float ux = rel.Dot(p.Along), uy = -rel.Dot(p.Facing), away = ux < xe ? -1f : 1f;
                        float r = Mathf.Max(0.3f, yF - uy);
                        curves.Add((new Vector2(ux, uy), new Vector2(ux + away * r, uy), r, away));
                        if (away < 0f) ta = ux - r; else tb = ux + r;
                    }
                    throats.Add((ta, tb));
                }
                throats.Sort((u, v) => u.a.CompareTo(v.a));
                // the kerb line as runs of (x, y) corners, each run one ribbon: from the last throat round the back to the
                // first, and the front between the throats
                var runs = new List<List<Vector2>>();
                if (throats.Count == 0)
                    runs.Add(new List<Vector2> { new(xL, yF), new(xR, yF), new(xR, yB), new(xL, yB), new(xL, yF) });
                else
                {
                    runs.Add(new List<Vector2> { new(throats[^1].b, yF), new(xR, yF), new(xR, yB), new(xL, yB), new(xL, yF), new(throats[0].a, yF) });
                    for (int k = 0; k + 1 < throats.Count; k++) runs.Add(new List<Vector2> { new(throats[k].b, yF), new(throats[k + 1].a, yF) });
                }
                Vector3 OutOf(Vector2 a, Vector2 b)
                {
                    // the side of a kerb run away from the lot's middle
                    var d = b - a; var n = new Vector2(-d.Y, d.X).Normalized();
                    var mid = (a + b) * 0.5f; var c = new Vector2((xL + xR) * 0.5f, (yF + yB) * 0.5f);
                    if (n.Dot(mid - c) < 0f) n = -n;
                    return (p.Along * n.X - p.Facing * n.Y).Normalized();
                }
                float WidthOf(Vector2 a, Vector2 b) => Mathf.Abs(a.Y - yF) < 1e-3f && Mathf.Abs(b.Y - yF) < 1e-3f ? frontW : LotKerbWidth;
                void Keep(MeshInstance3D mi)
                {
                    parent.AddChild(mi);
                    var body = new StaticBody3D { Name = mi.Name + "_body", CollisionLayer = 1u << 0 };
                    body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Concrete);
                    body.AddChild(new CollisionShape3D { Shape = mi.Mesh.CreateTrimeshShape() });
                    mi.AddChild(body);
                    kerbs++;
                }
                foreach (var run in runs)
                {
                    // every 2 m along it, so the kerb follows the ground; each point's way out of the lot -- at a corner, out to
                    // where the two legs' back edges cross (the front's wider than the sides)
                    var pts = new List<Vector3>(); var outs = new List<Vector3>(); var widths = new List<float>();
                    for (int k = 0; k + 1 < run.Count; k++)
                    {
                        var a = run[k]; var b = run[k + 1];
                        float len = (b - a).Length(); if (len < 0.05f) continue;
                        var o = OutOf(a, b); float wd = WidthOf(a, b);
                        int n = Mathf.Max(1, Mathf.CeilToInt(len / 2f));
                        for (int q = (pts.Count == 0 ? 0 : 1); q <= n; q++)
                        {
                            var v = a.Lerp(b, (float)q / n);
                            var w = At(v.X, v.Y);
                            w.Y = LotSurfaceY(lot, w.X, w.Z, Ground(w.X, w.Z));
                            if (q == n && k + 2 < run.Count)
                            {
                                var back = o * wd + OutOf(run[k + 1], run[k + 2]) * WidthOf(run[k + 1], run[k + 2]);   // (a rectangle's corners are square)
                                pts.Add(w); outs.Add(back.Normalized()); widths.Add(back.Length());
                                continue;
                            }
                            pts.Add(w); outs.Add(o); widths.Add(wd);
                        }
                    }
                    if (pts.Count < 2) continue;
                    var mi = EditorSidewalk.BuildRibbon(pts, outs, LotKerbWidth, kerbH, 0.6f, $"Parking_Kerb_{made}_{kerbs}", groundFn, null, widths);
                    if (mi != null) Keep(mi);
                }
                // ⭐ AND ROUND INTO EACH WAY IN, CURVED (strawberry 2026-10-14: "add a curve for the sidewalk going into the parking
                // lot"): a quarter disc of pavement fanned onto its centre, from the mouth's return's end round onto the front
                // kerb's line. Its radius is what is left of the way in past the return -- the lot's kerb, less the return's
                // metre past the back of the street's pavement -- which is the return's own width, so the return, the curve and
                // the front kerb meet square and tile the corner. The asphalt it curves away from is filled under it.
                int steps = Mathf.Max(4, Mathf.CeilToInt(90f / ArcFacetDegrees));
                foreach (var (end, centre, r, away) in curves)
                {
                    var C = At(centre.X, centre.Y);
                    float top = LotSurfaceY(lot, C.X, C.Z, Ground(C.X, C.Z)) + kerbH;      // the fan's one apex, one height
                    var pts = new List<Vector3>(); var outs = new List<Vector3>(); var widths = new List<float>();
                    var kerbsAt = new List<float>(); var surf = new List<float>();
                    for (int i = 0; i <= steps; i++)
                    {
                        float ang = Mathf.Pi * 0.5f * i / steps;
                        var w = At(centre.X - away * r * Mathf.Cos(ang), centre.Y + r * Mathf.Sin(ang));
                        w.Y = LotSurfaceY(lot, w.X, w.Z, Ground(w.X, w.Z));
                        var toC = new Vector3(C.X - w.X, 0f, C.Z - w.Z); float l = toC.Length();
                        pts.Add(w); outs.Add(l > 1e-4f ? toC / l : -p.Facing); widths.Add(Mathf.Max(l, 0.01f));
                        kerbsAt.Add(kerbH); surf.Add(top - w.Y);
                    }
                    var mi = EditorSidewalk.BuildRibbon(pts, outs, r, kerbH, 0.6f, $"Parking_KerbCurve_{made}_{kerbs}", groundFn, kerbsAt, widths, surf);
                    if (mi != null) Keep(mi);
                    // the asphalt outside the curve, fanned from the corner it rounds off -- a hair under the way in's own
                    var K = At(end.X, yF); K.Y = LotSurfaceY(lot, K.X, K.Z, Ground(K.X, K.Z)) - 0.005f;
                    for (int i = 0; i + 1 < pts.Count; i++) Tri(ground, K, pts[i] - Vector3.Up * 0.005f, pts[i + 1] - Vector3.Up * 0.005f, null);
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
            Log.Print($"[towngrid] parking lots built: {ParkingLotsLaid.Count} lots, {kerbs} kerb runs round them, {lanes} ways in built as lanes; "
                    + $"{MouthsNearCorner} kept off a junction's corner ({ParkingWaysNearCorner} lots had to open their only one there)");
            return made + kerbs;
        }
        /// <summary>How far a lot's asphalt stands over the ground under it.</summary>
        public float LotLift = 0.04f;

        /// <summary>The blocks: retail's Block_Parking_0, one at the head of every stall, a little in from its end, square
        /// across it. A Z-up mesh stood up at 270 about X like every vanilla prop, its 3 m length (local X) across the stall.</summary>
        public int PlaceParkingBlocks(EditorObjects objs, List<Node3D> into = null)
        {
            if (objs == null) return 0;
            int made = 0;
            var terr = Terrain.Active;
            foreach (var lot in ParkingLotsLaid)
                foreach (var (at, along) in lot.Blocks)
                {
                    var q = at;
                    q.Y = LotSurfaceY(lot, q.X, q.Z, terr != null ? terr.SampleHeight(q.X, q.Z) : at.Y);
                    var node = objs.Place("Block_Parking_0", q, HedgeBasis(along, 1f, 1f, false));
                    if (node == null) continue;
                    into?.Add(node); made++;
                }
            if (made > 0) Log.Print($"[towngrid] parking blocks: {made} placed in {ParkingLotsLaid.Count} lots");
            return made;
        }
    }
}
