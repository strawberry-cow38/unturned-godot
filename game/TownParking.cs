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
            public readonly List<ParkingEntrance> Entrances = new();
            /// <summary>The pavement's top along the lot's front, at its left and right ends: what the ways in come in at.</summary>
            public float FrontTopL, FrontTopR;
        }
        /// <summary>A way into a parking lot from its street: across the pavement -- which stays, its kerb dropped (From..To, arc
        /// metres on Road, on Side) -- and a short run of asphalt from the pavement's back to the lot's aisle.</summary>
        public sealed class ParkingEntrance { public int Road, Side; public float From, To; public Vector3 Mouth, Into; public float Width, TopY; public ParkingLot Lot; }
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
            int maxPer = Mathf.FloorToInt((maxDepth - LotFrontApron - LotBackMargin) / StallWidth + 1e-4f);
            if (maxPer < 1) return false;
            aisles = Mathf.CeilToInt(n / (2f * maxPer));
            perSide = Mathf.CeilToInt(n / (2f * aisles));
            width = aisles * (2f * StallDepth + LotAisle);
            depth = LotFrontApron + perSide * StallWidth + LotBackMargin;
            bool canOne = n <= maxPer;
            float w1 = StallDepth + LotAisle, d1 = LotFrontApron + n * StallWidth + LotBackMargin;
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
            for (float maxD = deepest; maxD >= LotFrontApron + StallWidth + LotBackMargin - 1e-3f; maxD -= StallWidth)
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
                if (PlotFits(roads, pl)) return pl;
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

        /// <summary>A lot's layout, from its plot: each aisle running back from the street, a row of stalls each side of it
        /// (one side for a one-sided lot, the side away from the building that asked), the stripes between and either end of
        /// each row, and a way in at the front of each aisle.</summary>
        void LayOutLot(RoadField roads, ParkingLot lot, int perSide)
        {
            var p = lot.Lot;
            Vector3 At(float x, float y) => p.Front + p.Along * x - p.Facing * y;      // x along the street, y back from it
            float module = lot.OneSided ? StallDepth + LotAisle : 2f * StallDepth + LotAisle;
            // a one-sided lot's stalls on the side away from the building beside it
            bool forOnLeft = lot.For != null && (lot.For.Front - p.Front).Dot(p.Along) < 0f;
            for (int i = 0; i < lot.Aisles; i++)
            {
                float x0 = -p.Width * 0.5f + i * module;
                // the rows: [from, to] across the street's way, and which way is in from the aisle to the head
                var rows = new List<(float a, float b, float into)>();
                float aisleMid;
                if (lot.OneSided)
                {
                    if (forOnLeft) { rows.Add((x0 + LotAisle, x0 + LotAisle + StallDepth, 1f)); aisleMid = x0 + LotAisle * 0.5f; }
                    else { rows.Add((x0, x0 + StallDepth, -1f)); aisleMid = x0 + StallDepth + LotAisle * 0.5f; }
                }
                else
                {
                    rows.Add((x0, x0 + StallDepth, -1f));
                    rows.Add((x0 + StallDepth + LotAisle, x0 + module, 1f));
                    aisleMid = x0 + StallDepth + LotAisle * 0.5f;
                }
                foreach (var (a, b, into) in rows)
                {
                    for (int k = 0; k < perSide; k++)
                    {
                        float y = LotFrontApron + (k + 0.5f) * StallWidth;
                        lot.Stalls.Add((At((a + b) * 0.5f, y), -p.Facing, p.Along * into));
                    }
                    for (int k = 0; k <= perSide; k++)
                    {
                        float y = LotFrontApron + k * StallWidth;
                        lot.Stripes.Add((At(a, y), At(b, y)));
                    }
                }
                // its way in: the front of its aisle, across the setback and the pavement to the street
                var mouth = At(aisleMid, 0f);
                var e = new ParkingEntrance { Road = p.Road, Side = p.RoadSide, Mouth = mouth, Into = -p.Facing, Width = LotAisle, Lot = lot };
                RoadNear(roads, p.Road, At(aisleMid - LotAisle * 0.5f, 0f), out float fa, out _);
                RoadNear(roads, p.Road, At(aisleMid + LotAisle * 0.5f, 0f), out float fb, out _);
                e.From = Mathf.Min(fa, fb); e.To = Mathf.Max(fa, fb);
                lot.Entrances.Add(e);
            }
            lot.Spaces = lot.Stalls.Count;
            // the pavement's top along its front, where the ways in come off it
            float TopAt(Vector3 q)
            {
                RoadNear(roads, p.Road, q, out float along, out _);
                // (the kerb height counts from the road's spline, not its asphalt: the lamps on the pavement stand at the same)
                return roads.EvaluateAlong(p.Road, along, out var rp, out _, snapTerrain: false) ? rp.Y + PavementKerb : q.Y;
            }
            lot.FrontTopL = TopAt(At(-p.Width * 0.5f, 0f)); lot.FrontTopR = TopAt(At(p.Width * 0.5f, 0f));
            foreach (var e in lot.Entrances) e.TopY = TopAt(e.Mouth);
        }

        /// <summary>How long a lot's asphalt takes to come down from the pavement's height at its front to the ground (m).</summary>
        public float LotRamp = 3.5f;
        /// <summary>⚠ THE WAY IN MEETS THE PAVEMENT, NOT THE GRASS. The pavement stands a kerb's height over the street, and its
        /// back edge a step over the ground behind it: a way in laid on the ground had cars dropping off that step. So across
        /// each way in the asphalt is at the pavement's top, and it eases down to the ground over LotRamp into the lot and
        /// 2.5 m to either side of the aisle -- the rest of the lot lies on the ground, never under it.</summary>
        float LotSurfaceY(ParkingLot lot, float x, float z, float ground)
        {
            float best = ground + LotLift;
            foreach (var e in lot.Entrances)
            {
                var rel = new Vector3(x - e.Mouth.X, 0f, z - e.Mouth.Z);
                float back = rel.Dot(e.Into), lat = Mathf.Abs(rel.Dot(new Vector3(-e.Into.Z, 0f, e.Into.X)));
                float Ease(float t) { t = Mathf.Clamp(t, 0f, 1f); return 1f - t * t * (3f - 2f * t); }
                float w = Ease(back / Mathf.Max(0.1f, LotRamp)) * Ease((lat - e.Width * 0.5f) / 2.5f);
                best = Mathf.Max(best, Mathf.Lerp(ground + LotLift, e.TopY + 0.005f, w));
            }
            return best;
        }

        /// <summary>The way into a parking lot that takes road `road`'s pavement on `side` anywhere in [a, b] (arc metres), if any.</summary>
        public ParkingEntrance EntranceAt(int road, int side, float a, float b)
        {
            foreach (var lot in ParkingLotsLaid)
                foreach (var e in lot.Entrances)
                    if (e.Road == road && e.Side == side && e.From < b && a < e.To) return e;
            return null;
        }

        /// <summary>The lots, built: the asphalt (the road's own, as the street's parking bays are), its ways in run out across
        /// the setback to the pavement's back, and the stripes -- retail's Line_Parking_0, its 0.5 x 5 m stripes at its 4 m,
        /// painted WHITE ("turned white"), off the road texture's own white like every line in the town.</summary>
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
            // a quad on the lot's surface, in pieces about `cell` across so it follows the ground and the ramp, `lift` over it
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
            foreach (var lot in ParkingLotsLaid)
            {
                cur = lot;
                var p = lot.Lot; var c = p.Corners();      // front-left, front-right, back-right, back-left
                Quad(ground, c[0], p.Along, -p.Facing, p.Width, p.Depth, 0f, null);
                // each way in: from the pavement's back (the setback in front of the lot) to the lot
                foreach (var e in lot.Entrances)
                    Quad(ground, e.Mouth - p.Along * (e.Width * 0.5f) + p.Facing * (PlotSetback + p.Setback), p.Along, -p.Facing, e.Width, PlotSetback + p.Setback, 0f, null);
                foreach (var (a, b) in lot.Stripes)
                {
                    var d = (b - a); float len = new Vector2(d.X, d.Z).Length(); if (len < 0.01f) continue;
                    var dn = new Vector3(d.X, 0f, d.Z) / len; var nn = new Vector3(-dn.Z, 0f, dn.X);
                    Quad(paint, a - nn * (StallStripe * 0.5f), dn, nn, len, StallStripe, 0.012f, uvWhite, 1f);
                }
            }
            int made = 0;
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
            return made;
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
                foreach (var (at, across, intoDir) in lot.Stalls)
                {
                    var q = at + intoDir * (StallDepth * 0.5f - 0.6f);
                    q.Y = LotSurfaceY(lot, q.X, q.Z, terr != null ? terr.SampleHeight(q.X, q.Z) : at.Y);
                    var node = objs.Place("Block_Parking_0", q, HedgeBasis(across, 1f, 1f, false));
                    if (node == null) continue;
                    into?.Add(node); made++;
                }
            if (made > 0) Log.Print($"[towngrid] parking blocks: {made} placed in {ParkingLotsLaid.Count} lots");
            return made;
        }
    }
}
