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
        /// <summary>A two-way aisle between two rows of stalls, the asphalt in front of the first stall -- the way in's throat,
        /// where it comes down off the pavement's height (LotRamp), so no stall sits on the slope -- and behind the last.</summary>
        public float LotAisle = 7f, LotFrontApron = 4f, LotBackMargin = 1.5f;
        /// <summary>⭐ A LOT HAS ITS OWN KERB (strawberry 2026-10-14: "the parking lot needs a kerb/sidewalk"): a pavement this
        /// wide round the inside of its edge, open only where its ways in come through.</summary>
        public float LotKerbWidth = 1.5f;
        /// <summary>⭐ THE BLOCK JUST PAST THE STRIPES' ENDS, WITH A SMALL GAP (strawberry 2026-10-14) -- which is where retail has
        /// it: PEI's blocks stand 0.4 m past the end of their Line_Parking_0 stripes, their middles. So a row's heads have
        /// a strip this deep behind them, between the stripes' ends and the lot's kerb, that the blocks stand in.</summary>
        public float LotHeadStrip = 0.9f, BlockPast = 0.4f;
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
        public int ApartmentParking = 14, ShopParking = 8;
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
        /// and laid, and its layout -- each stall (its middle, the way across it, the way in from its aisle to its head), each
        /// stripe (its centre line), each way in from the street.</summary>
        public sealed class ParkingLot
        {
            public Plot Lot, For; public int Wanted, Spaces, Aisles; public bool OneSided;
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

        /// <summary>⭐ SOLVE: `n` spaces into a lot no deeper than `maxDepth` -- aisles running back from the street, a row of
        /// stalls down each side; the fewest aisles that hold them, then the shallowest lot; or, for a few, one row down one
        /// side of a single aisle if that is the smaller lot. False if not even one stall fits that deep.</summary>
        public bool SolveParking(int n, float maxDepth, out int aisles, out int perSide, out bool oneSided, out float width, out float depth, int shape = 0)
        {
            aisles = perSide = 0; oneSided = false; width = depth = 0f;
            if (n < 1) return false;
            int maxPer = Mathf.FloorToInt((maxDepth - 2f * LotKerbWidth - LotFrontApron - LotBackMargin) / StallWidth + 1e-4f);
            if (maxPer < 1) return false;
            aisles = Mathf.CeilToInt(n / (2f * maxPer));
            perSide = Mathf.CeilToInt(n / (2f * aisles));
            width = 2f * LotKerbWidth + aisles * (2f * (StallDepth + LotHeadStrip) + LotAisle);
            depth = 2f * LotKerbWidth + LotFrontApron + perSide * StallWidth + LotBackMargin;
            bool canOne = n <= maxPer;
            float w1 = 2f * LotKerbWidth + StallDepth + LotHeadStrip + LotAisle, d1 = 2f * LotKerbWidth + LotFrontApron + n * StallWidth + LotBackMargin;
            // shape 0: the smaller lot; 1: one row down one side only (the narrowest frontage, for a tight street); 2: two rows
            bool one = shape == 1 ? canOne : shape == 2 ? false : canOne && w1 * d1 < width * depth;
            if (shape == 1 && !canOne) return false;
            if (one) { aisles = 1; perSide = n; oneSided = true; width = w1; depth = d1; }
            return true;
        }

        /// <summary>How far along its street (m, each way) a building and its lot may slide to find room for both.</summary>
        public float ParkingSlide = 60f;

        /// <summary>The plot a lot of `n` spaces would take beside `forPlot` on its frontage (`dir` -1 its left, +1 its right),
        /// as deep as the street's deepest plots at most -- null if none does.</summary>
        Plot ParkingPlotBeside(RoadField roads, Plot forPlot, int n, int dir, out int aisles, out int perSide, out bool oneSided)
        {
            aisles = perSide = 0; oneSided = false;
            float deepest = 0f; foreach (var ps in PlotSizes) if (ps.Kind != PlotKind.Parking) deepest = Mathf.Max(deepest, ps.Depth);
            // the deepest that fits there first, a stall row at a time shallower until one does
            for (float maxD = deepest; maxD >= 2f * LotKerbWidth + LotFrontApron + StallWidth + LotBackMargin - 1e-3f; maxD -= StallWidth)
            foreach (int shape in new[] { 0, 1 })
            {
                if (!SolveParking(n, maxD, out aisles, out perSide, out oneSided, out float w, out float d, shape)) { if (shape == 0) return null; continue; }
                var pl = new Plot
                {
                    Kind = PlotKind.Parking, Width = w, Depth = d, Along = forPlot.Along, Facing = forPlot.Facing, Road = forPlot.Road,
                    Front = forPlot.Front + forPlot.Along * (dir * (forPlot.Width * 0.5f + w * 0.5f)),
                    Arc = forPlot.Arc >= 0f ? forPlot.Arc + dir * (forPlot.Width * 0.5f + w * 0.5f) : -1f,
                    RoadSide = forPlot.RoadSide, Straight = forPlot.Straight
                };
                if (PlotFits(roads, pl) && LotCanOpen(roads, pl, aisles, oneSided, forPlot)) return pl;
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
                Plot pl = null, lotPlot = null; int aisles = 0, perSide = 0; bool oneSided = false;
                Plot Shifted(float by) => new Plot
                {
                    Kind = was.Kind, Width = was.Width, Depth = was.Depth, Along = was.Along, Facing = was.Facing, Road = was.Road, Road2 = was.Road2,
                    Front = was.Front + was.Along * by, Arc = was.Arc >= 0f ? was.Arc + by : -1f, RoadSide = was.RoadSide, Straight = was.Straight,
                    YardFront = was.YardFront, YardBack = was.YardBack, YardLeft = was.YardLeft, YardRight = was.YardRight, Setback = was.Setback
                };
                float reach = was.Straight && was.Road2 < 0 ? ParkingSlide : 0f;
                void Slide()
                {
                    for (float k = 0f; k <= reach && lotPlot == null; k += 1f)
                        foreach (float by in k == 0f ? new[] { 0f } : new[] { k, -k })
                        {
                            var cand = Shifted(by);
                            if (!PlotFits(roads, cand)) continue;
                            Plots.Add(cand);
                            foreach (int dir in new[] { first, -first })
                            {
                                lotPlot = ParkingPlotBeside(roads, cand, n, dir, out aisles, out perSide, out oneSided);
                                if (lotPlot != null && aisles * perSide * (oneSided ? 1 : 2) >= n) break;
                                lotPlot = null;
                            }
                            if (lotPlot != null) { pl = cand; break; }
                            Plots.RemoveAt(Plots.Count - 1);
                        }
                }
                Slide();
                // ...and if its frontage is too short between its two corner lots, those corner lots give way -- a car park
                // on a street corner, reached from both streets, is the commonest kind there is; the frontage fill puts
                // houses back in whatever the pair leaves
                if (lotPlot == null && reach > 0f)
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
                    if (lotPlot == null) Plots.AddRange(gave);
                    else
                    {
                        // (only the ones the pair actually needed: any it does not touch go back -- asked with the LOT in place too,
                        // or a corner the lot sits on came straight back over it)
                        foreach (var cl in gave) if (PlotFits(roads, cl) && !PlotsOverlap(cl, lotPlot)) Plots.Add(cl); else ParkingCornersTaken++;
                    }
                }
                if (lotPlot == null && PlotFits(roads, was))
                {
                    // the most that fits beside it where it stood
                    pl = was; Plots.Add(pl);
                    for (int want = n - 1; want >= 1 && lotPlot == null; want = want > 8 ? want - 2 : want - 1)
                        foreach (int dir in new[] { first, -first })
                            if ((lotPlot = ParkingPlotBeside(roads, pl, want, dir, out aisles, out perSide, out oneSided)) != null) break;
                }
                if (lotPlot == null)
                {
                    ParkingRefused++;
                    if (ProbeEverything) Log.Print($"[towngrid]   parking lot for the {was.Kind} at ({was.Front.X:0},{was.Front.Z:0}): asked {n} -- no room on its frontage");
                    continue;
                }
                Plots.Add(lotPlot);
                var lot = new ParkingLot { Lot = lotPlot, For = pl, Wanted = n, Aisles = aisles, OneSided = oneSided };
                LayOutLot(roads, lot, perSide);
                ParkingLotsLaid.Add(lot);
                ParkingSpacesLaid += lot.Spaces;
                if (ProbeEverything)
                    Log.Print($"[towngrid]   parking lot for the {pl.Kind} at ({pl.Front.X:0},{pl.Front.Z:0}): asked {n}, laid {lot.Spaces} "
                            + $"({lot.Aisles} aisle{(lot.Aisles > 1 ? "s" : "")}{(lot.OneSided ? ", one-sided" : "")}), {lotPlot.Width:0}x{lotPlot.Depth:0} m at ({lotPlot.Front.X:0},{lotPlot.Front.Z:0})");
            }
        }

        /// <summary>Where along a lot's front (m from its middle, along Along) each aisle -- and so each way in -- comes out:
        /// aisles side by side from its left kerb, each a row of stalls either side of it (a one-sided lot's on the side
        /// away from the building beside it).</summary>
        List<float> AisleMids(Plot p, int aisles, bool oneSided, bool forOnLeft)
        {
            float module = oneSided ? StallDepth + LotHeadStrip + LotAisle : 2f * (StallDepth + LotHeadStrip) + LotAisle;
            var mids = new List<float>(aisles);
            for (int i = 0; i < aisles; i++)
            {
                float x0 = -p.Width * 0.5f + LotKerbWidth + i * module;
                mids.Add(oneSided && forOnLeft ? x0 + LotAisle * 0.5f : x0 + LotHeadStrip + StallDepth + LotAisle * 0.5f);
            }
            return mids;
        }

        /// <summary>⚠ WHETHER A LOT HERE COULD OPEN A WAY IN -- OpenMouth's own test, ahead of time: at least one aisle's mouth
        /// on its street's pavement, clear of the junctions' corners by ParkingCornerClear. A two-lane way in opens 9 m of
        /// the street's pavement, and a lot beside a corner (its plot clear of the corner's pavement, its middle not) had
        /// none that could -- a lot with no way in. The solve slides on until one can.</summary>
        bool LotCanOpen(RoadField roads, Plot lotPlot, int aisles, bool oneSided, Plot forPlot)
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
            bool forOnLeft = forPlot != null && (forPlot.Front - lotPlot.Front).Dot(lotPlot.Along) < 0f;
            foreach (float xe in AisleMids(lotPlot, aisles, oneSided, forOnLeft))
            {
                float a = lotPlot.Arc + sgn * xe;
                if (a - reach >= lo && a + reach <= hi) return true;
            }
            return false;
        }

        /// <summary>A lot's layout, from its plot: each aisle running back from the street, a row of stalls each side of it
        /// (one side for a one-sided lot, the side away from the building that asked), the stripes between and either end of
        /// each row, and a way in at the front of each aisle.</summary>
        void LayOutLot(RoadField roads, ParkingLot lot, int perSide)
        {
            var p = lot.Lot;
            Vector3 At(float x, float y) => p.Front + p.Along * x - p.Facing * y;      // x along the street, y back from it
            float module = lot.OneSided ? StallDepth + LotHeadStrip + LotAisle : 2f * (StallDepth + LotHeadStrip) + LotAisle;
            float y0 = LotKerbWidth + LotFrontApron;                                     // the first stall's front stripe
            // a one-sided lot's stalls on the side away from the building beside it
            bool forOnLeft = lot.For != null && (lot.For.Front - p.Front).Dot(p.Along) < 0f;
            var mids = AisleMids(p, lot.Aisles, lot.OneSided, forOnLeft);
            for (int i = 0; i < lot.Aisles; i++)
            {
                float x0 = -p.Width * 0.5f + LotKerbWidth + i * module;
                // the rows: the head end and the aisle end across the street's way, and which way is in from the aisle to the head
                var rows = new List<(float head, float aisleEnd, float into)>();
                if (lot.OneSided)
                {
                    if (forOnLeft) rows.Add((x0 + module - LotHeadStrip, x0 + LotAisle, 1f));
                    else rows.Add((x0 + LotHeadStrip, x0 + LotHeadStrip + StallDepth, -1f));
                }
                else
                {
                    rows.Add((x0 + LotHeadStrip, x0 + LotHeadStrip + StallDepth, -1f));
                    rows.Add((x0 + module - LotHeadStrip, x0 + module - LotHeadStrip - StallDepth, 1f));
                }
                foreach (var (head, aisleEnd, into) in rows)
                {
                    float mid = (head + aisleEnd) * 0.5f;
                    for (int k = 0; k < perSide; k++)
                    {
                        float y = y0 + (k + 0.5f) * StallWidth;
                        lot.Stalls.Add((At(mid, y), -p.Facing, p.Along * into));
                        // its block: just past the end of its stripes, the small gap retail leaves
                        lot.Blocks.Add((At(head + into * BlockPast, y), -p.Facing));
                    }
                    for (int k = 0; k <= perSide; k++)
                    {
                        float y = y0 + k * StallWidth;
                        lot.Stripes.Add((At(head, y), At(aisleEnd, y)));
                    }
                }
                lot.Entrances.Add(new ParkingEntrance { Road = p.Road, Side = p.RoadSide, Mouth = At(mids[i], 0f), Into = -p.Facing,
                                                        Width = 2f * ParkingEntranceHalf, Lot = lot });
            }
            lot.Spaces = lot.Stalls.Count;
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
            // each lot's street's height along its front, a metre apart and a couple past each end: its asphalt across from
            // there, which is its pavement's top less a kerb (so the lot's front kerb stands exactly as high)
            foreach (var lot in ParkingLotsLaid)
            {
                var p = lot.Lot;
                int n = Mathf.CeilToInt(p.Width + 4f) + 1;
                lot.FrontU0 = -p.Width * 0.5f - 2f;
                lot.FrontY = new float[n];
                for (int k = 0; k < n; k++)
                    lot.FrontY[k] = RoadNear(roads, p.Road, p.Front + p.Along * (lot.FrontU0 + k), out _, out _) + PavementKerb - kerbH;
            }
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
