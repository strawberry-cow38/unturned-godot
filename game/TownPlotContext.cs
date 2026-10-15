using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    // ==============================================================================================
    // WHAT A PLOT KNOWS OF WHAT IS ROUND IT (strawberry 2026-10-15: "in general just give plots some more context about them
    // and their neighbors, and features around them"). Built once the town is planned; read by whatever builds on a plot.
    // ==============================================================================================
    public sealed partial class TownRoadGrid
    {
        /// <summary>A plot's surroundings. Its sides are the plot's own: Back, Left (-Along, as Corners() names it) and Right.</summary>
        public sealed class PlotContext
        {
            public Plot Plot;
            /// <summary>The lot across each line, or null.</summary>
            public Plot Back, Left, Right;
            /// <summary>Its building zone runs to that line and so does the neighbour's: the two buildings will stand wall to wall
            /// (a PARTY wall -- no windows in it, and the two walls butted, not stacked).</summary>
            public bool PartyBack, PartyLeft, PartyRight;
            /// <summary>The parking lot laid for it, and which side of it that lot is on: -1 left, +1 right, 0 behind.</summary>
            public ParkingLot Parking; public int ParkingSide;
            /// <summary>A back lane runs along its back line.</summary>
            public bool LaneBehind;
            /// <summary>A second street along one side (a corner lot), and whether its own street is a main road.</summary>
            public bool Corner, OnMain;
            /// <summary>A pocket park beside it, and which side: -1 left, +1 right, 0 behind.</summary>
            public PocketPark Park; public int ParkSide;
            public override string ToString()
            {
                var w = new List<string>();
                if (Left != null) w.Add($"left {Left.Kind}{(PartyLeft ? " (party)" : "")}");
                if (Right != null) w.Add($"right {Right.Kind}{(PartyRight ? " (party)" : "")}");
                if (Back != null) w.Add($"back {Back.Kind}{(PartyBack ? " (party)" : "")}");
                if (Parking != null) w.Add($"parking {(ParkingSide < 0 ? "left" : ParkingSide > 0 ? "right" : "behind")}");
                if (LaneBehind) w.Add("lane behind");
                if (Corner) w.Add("corner");
                if (OnMain) w.Add("on a main road");
                if (Park != null) w.Add($"park {(ParkSide < 0 ? "left" : ParkSide > 0 ? "right" : "behind")}");
                return w.Count == 0 ? "on its own" : string.Join(", ", w);
            }
        }
        /// <summary>Each plot's context, as the last BuildContext found it.</summary>
        public readonly Dictionary<Plot, PlotContext> Context = new();

        /// <summary>Is (x, z) inside the convex polygon `q`, or within `grow` of it (a negative grow: that far inside)?</summary>
        static bool InConvex(Vector3[] q, float x, float z, float grow = 0f)
        {
            var c = Vector2.Zero; foreach (var v in q) c += new Vector2(v.X, v.Z); c /= q.Length;
            for (int i = 0; i < q.Length; i++)
            {
                var a = q[i]; var b = q[(i + 1) % q.Length];
                var e = new Vector2(b.X - a.X, b.Z - a.Z); float el = e.Length();
                if (el < 1e-4f) continue;
                var n = new Vector2(-e.Y, e.X) / el;
                if (n.Dot(c - new Vector2(a.X, a.Z)) > 0f) n = -n;                    // outward
                if (n.Dot(new Vector2(x - a.X, z - a.Z)) > grow) return false;
            }
            return true;
        }

        /// <summary>The yard a plot keeps on side `s` of EdgesOf (0 back, 1 left, 2 right, 3 front); an outline lot keeps none
        /// it can name, so it never counts as running to a line.</summary>
        static float YardOn(Plot q, int s) => q.Outline != null ? float.MaxValue
            : s == 0 ? q.YardBack : s == 1 ? q.YardLeft : s == 2 ? q.YardRight : q.YardFront;

        /// <summary>Find every plot's context: its neighbour across each line (parallel to 2 degrees, facing, within 0.6 m,
        /// sharing a metre or more), whether the two building zones meet there, its parking lot, a lane behind it, a park beside
        /// it, a second street, a main road. Logs a census. Call after the plan is done (greens included).</summary>
        public void BuildContext(RoadField roads)
        {
            Context.Clear();
            foreach (var pl in Plots)
                Context[pl] = new PlotContext { Plot = pl, Corner = pl.Road2 >= 0, OnMain = roads != null && pl.Road >= 0 && IsMain(roads, pl.Road) };
            float cosOpp = -Mathf.Cos(Mathf.DegToRad(2f));
            const float PartyYard = 0.3f;
            foreach (var pl in Plots)
            {
                if (pl.Outline != null) continue;
                var ctx = Context[pl];
                var mine = EdgesOf(pl);
                for (int s = 0; s < 3; s++)
                {
                    var (ea, eb, en, _) = mine[s];
                    var t = new Vector3(eb.X - ea.X, 0f, eb.Z - ea.Z); float elen = t.Length();
                    if (elen < 1f) continue;
                    t /= elen;
                    Plot best = null; int bestSide = -1; float bestShare = 0f;
                    foreach (var q in Plots)
                    {
                        if (ReferenceEquals(q, pl)) continue;
                        foreach (var (qa, qb, qn, qs) in EdgesOf(q))
                        {
                            if (en.Dot(qn) > cosOpp) continue;
                            float pa = new Vector3(qa.X - ea.X, 0f, qa.Z - ea.Z).Dot(t), pb = new Vector3(qb.X - ea.X, 0f, qb.Z - ea.Z).Dot(t);
                            float share = Mathf.Min(elen, Mathf.Max(pa, pb)) - Mathf.Max(0f, Mathf.Min(pa, pb));
                            if (share < 1f) continue;
                            var mid = (qa + qb) * 0.5f;
                            if (Mathf.Abs(new Vector3(mid.X - ea.X, 0f, mid.Z - ea.Z).Dot(en)) > 0.6f) continue;
                            if (share > bestShare) { bestShare = share; best = q; bestSide = qs; }
                        }
                    }
                    if (best == null) continue;
                    bool shared = YardOn(pl, s) < PartyYard && bestSide >= 0 && YardOn(best, bestSide) < PartyYard;
                    if (s == 0) { ctx.Back = best; ctx.PartyBack = shared; }
                    else if (s == 1) { ctx.Left = best; ctx.PartyLeft = shared; }
                    else { ctx.Right = best; ctx.PartyRight = shared; }
                }
            }
            int SideOfPoint(Plot pl, Vector3 at)
            {
                var d = new Vector3(at.X - pl.Centre.X, 0f, at.Z - pl.Centre.Z);
                float a = d.Dot(pl.Along.Normalized()), back = d.Dot(-pl.Facing.Normalized());
                return Mathf.Abs(a) >= Mathf.Abs(back) ? (a < 0f ? -1 : 1) : 0;
            }
            foreach (var lot in ParkingLotsLaid)
                if (lot.For != null && Context.TryGetValue(lot.For, out var pc) && lot.Lot != null)
                { pc.Parking = lot; pc.ParkingSide = SideOfPoint(lot.For, lot.Lot.Centre); }
            foreach (var pl in Plots)
            {
                if (pl.Outline != null) continue;
                var c = pl.Corners();
                var back = (c[2] + c[3]) * 0.5f - pl.Facing.Normalized() * 2f;      // two metres past the back line's middle
                foreach (var al in Alleys) if (InConvex(al.Corners(), back.X, back.Z, 1f)) { Context[pl].LaneBehind = true; break; }
            }
            foreach (var pk in PocketParks)
            {
                if (pk.Hedge?.Pts == null || pk.Hedge.Pts.Count < 3) continue;
                var ring = pk.Hedge.Pts.ToArray();
                foreach (var pl in Plots)
                {
                    if (pl.Outline != null || Flat(pl.Centre, pk.Centre) > Mathf.Sqrt(pk.Area) + pl.Width + pl.Depth) continue;
                    var c = pl.Corners();
                    foreach (var (mid, side) in new[] { ((c[2] + c[3]) * 0.5f - pl.Facing.Normalized() * 3f, 0),
                                                        ((c[3] + c[0]) * 0.5f - pl.Along.Normalized() * 3f, -1),
                                                        ((c[1] + c[2]) * 0.5f + pl.Along.Normalized() * 3f, 1) })
                        if (InConvex(ring, mid.X, mid.Z)) { Context[pl].Park = pk; Context[pl].ParkSide = side; break; }
                }
            }
            int party = 0, parking = 0, lanes = 0, parks = 0, alone = 0;
            foreach (var c in Context.Values)
            {
                if (c.PartyBack || c.PartyLeft || c.PartyRight) party++;
                if (c.Parking != null) parking++;
                if (c.LaneBehind) lanes++;
                if (c.Park != null) parks++;
                if (c.Back == null && c.Left == null && c.Right == null) alone++;
            }
            Log.Print($"[towngrid] plot context: {Context.Count} plots -- {party} with a party wall, {parking} with their own parking lot, "
                    + $"{lanes} with a lane behind, {parks} beside a park, {alone} with no neighbour on any side");
            if (ProbeEverything)
                foreach (var c in Context.Values) Log.Print($"[towngrid]   {c.Plot.Kind} at ({c.Plot.Centre.X:0},{c.Plot.Centre.Z:0}): {c}");
        }
    }
}
