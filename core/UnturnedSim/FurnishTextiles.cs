using System;
using System.Collections.Generic;
using System.Linq;
using static UnturnedSim.RoomKind;

namespace UnturnedSim
{
    public static partial class BuildingGen
    {
        /// <summary>Testing only: no curtains and no rugs -- the building as it was before them.</summary>
        [ThreadStatic] public static bool NoRuntimeDecor;

        /// <summary>The rooms of a home that hang curtains, and the odds one does (then at every window of the kind that takes
        /// one -- a glazed window, never a bathroom's high slit -- where it fits): where you sit and where you sleep first,
        /// the dining room less often. Not a kitchen, whose windows stand over its counters; not a bathroom, whose slit is
        /// over your head.</summary>
        public static readonly IReadOnlyDictionary<RoomKind, int> CurtainRooms = new Dictionary<RoomKind, int> { [LivingRoom] = 90, [Bedroom] = 90, [DiningRoom] = 60 };
        /// <summary>How far the fabric runs past each jamb (astraclaw's handoff: "opening width + 0.40 m"), and the least it
        /// may where something beside the window leaves it less.</summary>
        public const float CurtainSide = 0.2f, CurtainMinSide = 0.05f;
        /// <summary>The cloth's top this far over the window's head, its fittings this far under the ceiling at least (a window
        /// under the floor above has its head AT the ceiling -- OpeningTop -- and gets a rail mounted to the ceiling, its
        /// cloth starting under it).</summary>
        public const float CurtainOverHead = 0.05f, CurtainUnderCeiling = 0.02f;
        /// <summary>A floor-length curtain's hem over the floor; a sill-length one's under the sill (its apron); what stands in
        /// front of a sill-length one this far under its hem at least, and a rail's end this far off what stands beside.</summary>
        public const float CurtainHem = 0.03f, CurtainApron = 0.1f, CurtainClear = 0.05f;
        /// <summary>Percent of curtains drawn back, each window drawing for itself.</summary>
        public const int CurtainOpen = 75;

        /// <summary>The rooms of a home that lay a rug, and the odds one does: under the couch and its set, under the bed.</summary>
        public static readonly IReadOnlyDictionary<RoomKind, int> RugRooms = new Dictionary<RoomKind, int> { [LivingRoom] = 85, [Bedroom] = 75 };
        /// <summary>A rug keeps this far off every wall and off its room's edge (an open plan's cut): it lies on the floor, not
        /// up a wall or into the next room.</summary>
        public const float RugWallGap = 0.2f;
        /// <summary>A living room's: under the front of the couch by RugUnderCouch (the front legs on it, as a real one), out
        /// toward the set by the first of RugDepths that fits, past each end of the couch by the first of RugPast.</summary>
        public const float RugUnderCouch = 0.4f;
        static readonly float[] RugDepths = { 2.6f, 2.2f, 1.8f, 1.4f }, RugPast = { 0.3f, 0.1f, -0.1f };
        /// <summary>A bedroom's: under the bed from RugBedHead below its head (clear of the lamps either side of the bedhead,
        /// which stand 0.77 m out from the wall) to past its foot by the first of RugBedFoot, past its sides by the first of
        /// RugBedSide; failing that a runner RugRunner wide along a long side, beside it.</summary>
        public const float RugBedHead = 1.2f, RugRunner = 0.9f;
        static readonly float[] RugBedFoot = { 0.5f, 0.3f, 0.1f }, RugBedSide = { 0.6f, 0.4f, 0.2f };
        /// <summary>No rug narrower than this either way.</summary>
        public const float RugMin = 0.8f;

        sealed partial class Furnisher
        {
            /// <summary>The soft furnishings' own draws, one Rng per room and purpose, as the décor's (Decorate): never the
            /// Furnisher's shared one, every draw from which would move every later room.</summary>
            Rng OwnRng(int salt) => new Rng(unchecked((((((Seed * 486187739 + _room.Floor) * 486187739 + _room.X0) * 486187739 + _room.Z0) * 486187739 + _room.X1) * 486187739 + _room.Z1) ^ salt));
            bool Home => _res.Kind is BuildingKind.Residential or BuildingKind.Apartment;

            /// <summary>Curtains at the room's windows (strawberry, round 12: astraclaw's runtime curtains, "living rooms and
            /// bedrooms first"): built at load (RuntimeDecorGenerators), sized off the window. A room of CurtainRooms' kinds
            /// draws once whether it hangs them, and the tint they share; each window draws open or closed and gets one where
            /// it fits (Curtain).</summary>
            public void Curtains()
            {
                if (!Home || !CurtainRooms.TryGetValue(_room.Kind, out int chance)) return;
                var rng = OwnRng(0xC0A7);
                if (rng.Int(100) >= chance) return;
                string tint = RuntimeDecor.CurtainTints[rng.Int(RuntimeDecor.CurtainTints.Length)];
                foreach (var (w, o, side) in Windows())
                {
                    if (o.Archetype is not (ArchWindow or ArchTallWindow) || !o.Glazed) continue;
                    bool open = rng.Int(100) < CurtainOpen;
                    var c = Curtain(w, o, side, open, tint);
                    FurnishTrace?.Invoke(System.FormattableString.Invariant($"curtain in {_room} at the window {o.U:0.00}..{o.U1:0.00} of the wall at ({w.X:0.00},{w.Z:0.00}) yaw {w.Yaw:0}: {(c == null ? "none fits: " + _curtainWhy : $"{c.Gen.Width:0.00} x {c.Gen.Height:0.00} m, rail at {c.Y - _room.Floor * Storey:0.00}, {(open ? "open" : "closed")}{(_curtainWhy.Length > 0 ? " (" + _curtainWhy + ")" : "")}")}"));
                    if (c != null) _res.RuntimeDecor.Add(c);
                }
            }

            /// <summary>Why the last window got no curtain, for FurnishTrace.</summary>
            string _curtainWhy;

            /// <summary>The ceiling a fixture meets over a point of this room's floor: the floor above's underside; the flat
            /// roof's, sitting on the wall heads; under a pitched roof the lower of the wall heads and the roof's underside --
            /// RoofTopAt is the plane, and a surface is built Slab thick about its plane (WallSurface: +-Thickness/2 along its
            /// normal), so half of it, measured along the slope, down. (The wall heads, because a top storey has no ceiling
            /// under a pitched roof: the roof over the middle of a house stands 6 m up, and "flush to the ceiling" there hung
            /// the counters over the kitchen out of reach -- out of the render's cutaway, which is how it was seen.)</summary>
            float CeilingOver(float x, float z)
            {
                int f = _room.Floor;
                if (f < _res.Floors - 1) return (f + 1) * Storey - Slab;
                if (_res.Roof == RoofKind.Flat) return (f + 1) * Storey;
                float h = RoofTopAt(_res, x, z);
                return float.IsNaN(h) ? (f + 1) * Storey : MathF.Min((f + 1) * Storey, h - Slab * 0.5f / MathF.Cos(_res.RoofPitch * MathF.PI / 180f));
            }

            /// <summary>The curtain for one window, or null where none fits. Its rail over the window's head (CurtainOverHead
            /// over it, or as high as the ceiling lets it), its fabric CurtainSide past each jamb -- less where the wall ends,
            /// where a doorway, another window's half of the wall between them, or anything standing or hung beside it comes
            /// first; down to the floor where nothing stands in front of the window -- the full drop reads as a curtain at this
            /// scale, a short one over bare floor as a mistake -- or to the sill's apron where what stands in front is lower
            /// than that (a desk), or to just over what does stand there (a couch's back, a bed's head): never into a piece, a
            /// stair or a door's swing, and never onto the floor a door is walked through.</summary>
            FurniturePlan Curtain(WallPlan w, WallOpening o, int side, bool open, string tint)
            {
                const float rail = RuntimeDecor.CurtainRailOver, depth = RuntimeDecor.CurtainDepth;
                float floorY = _room.Floor * Storey;
                var (dx, dz) = PlanTrig.CosSin(w.Yaw); dz = -dz;
                float nx = -dz * side, nz = dx * side, t = w.Thickness * 0.5f;
                // u along the wall, d out of its face into the room
                float U(float x, float z) => (x - w.X) * dx + (z - w.Z) * dz;
                float D(float x, float z) => (x - w.X) * nx + (z - w.Z) * nz - t;
                (float X, float Z) At(float u, float d) => (w.X + dx * u + nx * (t + d), w.Z + dz * u + nz * (t + d));
                float u0 = o.U, u1 = o.U1, sill = w.Y + o.V - floorY, head = sill + o.Height, reach = CurtainSide + rail;

                // how far the wall runs on past each jamb on this room's side (a corner, a jut, the room's end stop it): wall a cell
                // behind the face, this room's clear floor a cell in front of it and as deep as the curtain (a cell: the faces of
                // walls 0.7 and 0.5 thick lie on the 0.1 m grid's cell centres, so a sample off one by less is a coin toss)
                bool Wall((float X, float Z) p) => _p.State[_p.I(p.X), _p.J(p.Z)] == FloorPlan.Wall;
                bool Floor((float X, float Z) p) { int i = _p.I(p.X), j = _p.J(p.Z); return InRoom(i, j) && _p.State[i, j] != FloorPlan.Wall && _p.State[i, j] != FloorPlan.Stair; }
                float Run(int sgn)
                {
                    float e = 0f;
                    for (float k = 0.05f; k <= reach + 0.051f; k += 0.05f)
                    {
                        float u = sgn < 0 ? u0 - k : u1 + k;
                        if (!Wall(At(u, -FloorPlan.Step)) || !Floor(At(u, FloorPlan.Step)) || !Floor(At(u, depth))) break;
                        e = k;
                    }
                    return e;
                }
                float limL = Run(-1) - rail, limR = Run(1) - rail;
                // a doorway, and the next window's half of the wall between them, in this wall's line
                foreach (var v in RoomWalls())
                {
                    var (vx, vz) = PlanTrig.CosSin(v.Yaw); vz = -vz;
                    if (MathF.Abs(vx * dx + vz * dz) < 0.99f) continue;
                    // (its face toward the room in this one's plane: the same wall, or one running on from it)
                    var b = WallBox(v);
                    if (MathF.Abs(MathF.Max(D(b.X0, b.Z0), D(b.X1, b.Z1))) > 0.12f) continue;
                    foreach (var q in v.Openings)
                    {
                        if (v == w && q.U == o.U && q.V == o.V) continue;
                        float a0 = U(v.X + vx * q.U, v.Z + vz * q.U), a1 = U(v.X + vx * q.U1, v.Z + vz * q.U1);
                        float lo = MathF.Min(a0, a1), hi = MathF.Max(a0, a1);
                        // a doorway is a hole to the floor: the rail's end CurtainClear off it; another window: half way
                        float gap = q.V < 0.05f ? CurtainClear : 0f;
                        if (hi <= u0) limL = MathF.Min(limL, (q.V < 0.05f ? u0 - hi - gap : (u0 - hi) * 0.5f - 0.02f) - rail);
                        else if (lo >= u1) limR = MathF.Min(limR, (q.V < 0.05f ? lo - u1 - gap : (lo - u1) * 0.5f - 0.02f) - rail);
                    }
                }

                // the rail: over the head, under the ceiling over it
                float ceiling = float.MaxValue;
                foreach (float u in new[] { u0 - reach, (u0 + u1) * 0.5f, u1 + reach })
                {
                    var p = At(u, rail);
                    ceiling = MathF.Min(ceiling, CeilingOver(p.X, p.Z) - floorY);
                }
                float clothTop = MathF.Min(head + CurtainOverHead, ceiling - CurtainUnderCeiling - RuntimeDecor.CurtainCapOverRail - RuntimeDecor.CurtainRailOverCloth);
                float railY = clothTop + RuntimeDecor.CurtainRailOverCloth, top = railY + RuntimeDecor.CurtainCapOverRail;

                // what is in the way, by its run along the wall, its depth out of it and its heights: every piece of the floor,
                // standing, set on another, hung or hanging from the ceiling (the curtains already hung too); the stairs and the
                // tiles at their ends; the doors' leaves as they swing (a hung door's height); and, for a curtain down to the floor, the floor a doorway
                // is walked through
                var obs = new List<(float A0, float A1, float H0, float H1, string What, bool Stands)>();
                void Box((float X0, float Z0, float X1, float Z1) bx, float h0, float h1, string what, bool stands = false)
                {
                    float a0 = U(bx.X0, bx.Z0), a1 = U(bx.X1, bx.Z1), d0 = D(bx.X0, bx.Z0), d1 = D(bx.X1, bx.Z1);
                    (a0, a1) = (MathF.Min(a0, a1), MathF.Max(a0, a1)); (d0, d1) = (MathF.Min(d0, d1), MathF.Max(d0, d1));
                    if (d1 <= 0.005f || d0 >= depth + CurtainClear || a1 <= u0 - reach || a0 >= u1 + reach) return;
                    obs.Add((a0, a1, h0, h1, what, stands));
                }
                foreach (var m in _res.Furniture.Where(m => m.Floor == _room.Floor))
                {
                    var sz = FurnitureCatalog.Sizes[m.Name];
                    float y = m.Y - floorY;
                    if (m.Mounted) Box(m.Footprint, y - sz.H, y + sz.H, m.Name);
                    else if (m.Ceiling) Box(m.Footprint, y - (m.Name.Contains("Pendant") ? PendantDrop : 0.28f), y, m.Name);
                    else Box(m.Footprint, y, y + sz.H, m.Name, stands: true);
                }
                foreach (var c in _res.RuntimeDecor.Where(c => c.Floor == _room.Floor && c.Gen.Kind == RuntimeDecorKind.Curtain))
                    Box(c.Footprint, c.Y - floorY - RuntimeDecor.CurtainRailOverCloth - c.Gen.Height, c.Y - floorY + RuntimeDecor.CurtainCapOverRail, "a curtain");
                // (the stairs, and the tile at a flight's foot and top: you step off the stairs there, at any curtain's height)
                foreach (var r in _p.StairRects) Box(r, 0f, Storey, "the stairs");
                foreach (var r in _p.TileRects) Box(r, 0f, Storey, "a flight's end");
                foreach (var r in _p.LeafRects) Box(r, 0f, ArchSizes[ArchDoor].H, "a door's swing");
                // (the walk to and through a doorway, and a flight's ends: KeepDoor, less what this room's pieces hold clear for
                // themselves -- a bed's way in, a wardrobe's swing -- which is floor beside a piece, not a way through)
                var held = new HashSet<(int, int)>(_reserved.Values.SelectMany(l => l));
                var walk = new List<(float A0, float A1, float H0, float H1, string What, bool Stands)>();
                for (float u = u0 - reach + 0.05f; u < u1 + reach; u += FloorPlan.Step)
                    for (float d = FloorPlan.Step; d < depth + CurtainClear; d += FloorPlan.Step)
                    {
                        var p = At(u, d);
                        int i = _p.I(p.X), j = _p.J(p.Z);
                        if (_p.KeepDoor[i, j] && !held.Contains((i, j))) walk.Add((u - 0.05f, u + 0.05f, 0f, 0.05f, "a doorway's walk", false));
                    }
                _curtainWhy = "";

                // the drops, longest first: to the floor; to the sill's apron; over what stands in front of the window (a couch's
                // back, a bed's head against the wall) -- the glass under that hem is behind the piece -- as long as the hem
                // stays in the window's lower third
                var hems = new List<float> { CurtainHem, sill - CurtainApron };
                float over = obs.Where(q => q.Stands && q.A1 > u0 && q.A0 < u1).Select(q => q.H1 + CurtainClear).DefaultIfEmpty(float.NaN).Max();
                if (over > sill - CurtainApron && over <= sill + o.Height / 3f) hems.Add(over);
                for (int k = 0; k < hems.Count; k++)
                {
                    bool full = k == 0;
                    float hem = hems[k];
                    // (a window down near the floor has no sill to stop at)
                    if (!full && hem < 0.5f) break;
                    float extL = MathF.Min(CurtainSide, limL), extR = MathF.Min(CurtainSide, limR);
                    bool blocked = false;
                    string why = $"the wall's run {limL:0.00}/{limR:0.00}";
                    foreach (var (a0, a1, h0, h1, what, _) in full ? obs.Concat(walk) : obs)
                    {
                        // under its hem (less the clearance) or over its rail: clear
                        if (h1 <= hem - CurtainClear || h0 >= top) continue;
                        if (a1 <= u0) { if (u0 - a1 - CurtainClear - rail < extL) { extL = u0 - a1 - CurtainClear - rail; why = what + " beside it"; } }
                        else if (a0 >= u1) { if (a0 - u1 - CurtainClear - rail < extR) { extR = a0 - u1 - CurtainClear - rail; why = what + " beside it"; } }
                        else { blocked = true; why = what + " in front"; break; }
                    }
                    if (blocked || extL < CurtainMinSide || extR < CurtainMinSide)
                    {
                        _curtainWhy += (k == 0 ? "floor-length: " : k == 1 ? "; sill-length: " : "; over what stands there: ") + why;
                        continue;
                    }
                    float width = u1 - u0 + extL + extR, drop = clothTop - hem;
                    if (width < RuntimeDecor.CurtainMin || drop < RuntimeDecor.CurtainMin) continue;
                    var at = At((u0 - extL + u1 + extR) * 0.5f, DecorWallGap);
                    return new FurniturePlan
                    {
                        Name = "Curtain", X = at.X, Y = floorY + railY, Z = at.Z, Yaw = YawFacing(nx, nz), Floor = _room.Floor, Room = _room, Mounted = true,
                        Gen = new RuntimeDecor(RuntimeDecorKind.Curtain, RuntimeDecor.CurrentVersion, width, drop, open, tint),
                    };
                }
                return null;
            }

            /// <summary>A rug (strawberry, round 12: astraclaw's runtime rugs): in a living room under the front of the couch and
            /// out toward its set, in a bedroom under the bed (or a runner beside it), sized off the piece with a margin, the
            /// first of a few sizes that fits (RugFits); its pattern drawn per room. Square to the room: the couch's yaw to the
            /// quarter turn (Jitter turns it a degree or so).</summary>
            public void Rugs()
            {
                if (!Home || !RugRooms.TryGetValue(_room.Kind, out int chance)) return;
                var rng = OwnRng(0x7A6);
                if (rng.Int(100) >= chance) return;
                string style = RuntimeDecor.RugStyles[rng.Int(RuntimeDecor.RugStyles.Length)];
                var anchor = _mine.FirstOrDefault(m => m.On == null && !m.Mounted && !m.Ceiling && m.Name.StartsWith(_room.Kind == RoomKind.Bedroom ? "Bed" : "Couch"));
                if (anchor == null) return;
                float yaw = (MathF.Round(anchor.Yaw / 90f) * 90f % 360f + 360f) % 360f;
                var (fx, fz) = FrontOf(yaw); var (rx, rz) = RightOf(yaw);
                var s = FurnitureCatalog.Sizes[anchor.Name];
                float half = (s.X1 - s.X0) * 0.5f, mid = (s.X0 + s.X1) * 0.5f;
                // its place in the anchor's frame: across (along Right) from a0 to a1, out (along Front) from b0 to b1
                var tries = new List<(float A0, float A1, float B0, float B1, bool Under)>();
                if (_room.Kind == RoomKind.Bedroom)
                {
                    foreach (float sideM in RugBedSide)
                        foreach (float foot in RugBedFoot)
                            tries.Add((mid - half - sideM, mid + half + sideM, s.Y0 + RugBedHead, s.Y1 + foot, true));
                    foreach (int sgn in new[] { 1, -1 })
                    {
                        float a = sgn > 0 ? mid + half + 0.05f : mid - half - 0.05f - RugRunner;
                        tries.Add((a, a + RugRunner, s.Y0 + RugBedHead, s.Y1 - 0.1f, false));
                    }
                }
                else
                    foreach (float past in RugPast)
                        foreach (float d in RugDepths)
                            tries.Add((mid - half - past, mid + half + past, s.Y1 - RugUnderCouch, s.Y1 - RugUnderCouch + d, true));
                // what it may lie under: its anchor, and in a living room the set's table in front of the couch
                var under = new HashSet<FurniturePlan> { anchor };
                if (_room.Kind == RoomKind.LivingRoom)
                    foreach (var tv in _res.Furniture.Where(m => m.On != null && m.On.Room == _room && m.Name.StartsWith("Television"))) under.Add(tv.On);
                foreach (var (a0, a1, b0, b1, onAnchor) in tries)
                {
                    float wd = a1 - a0, dp = b1 - b0;
                    if (wd < RugMin || dp < RugMin) continue;
                    float ca = (a0 + a1) * 0.5f, cb = (b0 + b1) * 0.5f;
                    float x = anchor.X + rx * ca + fx * cb, z = anchor.Z + rz * ca + fz * cb;
                    var rug = new FurniturePlan
                    {
                        Name = "Rug", X = x, Y = _room.Floor * Storey, Z = z, Yaw = yaw, Floor = _room.Floor, Room = _room,
                        Gen = new RuntimeDecor(RuntimeDecorKind.Rug, RuntimeDecor.CurrentVersion, wd, dp, false, style),
                    };
                    if (!RugFits(rug.Footprint, onAnchor ? under : new HashSet<FurniturePlan>())) continue;
                    FurnishTrace?.Invoke(System.FormattableString.Invariant($"rug in {_room}: {style} {wd:0.00} x {dp:0.00} m {(onAnchor ? "under" : "beside")} {anchor.Name} at ({x:0.00},{z:0.00}) yaw {yaw:0}"));
                    _res.RuntimeDecor.Add(rug);
                    return;
                }
                FurnishTrace?.Invoke($"rug in {_room}: none fits by {anchor.Name}");
            }

            /// <summary>Does a rug lie here: wholly on this room's floor RugWallGap off every wall and off the room's edge, off
            /// the stairs, a flight's foot and top, and every door's swing (a leaf would drag over it), and under no piece but
            /// those it goes under?</summary>
            bool RugFits((float X0, float Z0, float X1, float Z1) r, ICollection<FurniturePlan> under)
            {
                foreach (var (i, j) in _p.Cells(Grow(r, RugWallGap)))
                    if (!InRoom(i, j) || _p.State[i, j] == FloorPlan.Wall || _p.State[i, j] == FloorPlan.Stair) return false;
                if (_p.StairRects.Concat(_p.TileRects).Concat(_p.LeafRects).Any(q => Overlaps(r, q))) return false;
                return !_res.Furniture.Any(o => o.Floor == _room.Floor && !o.Mounted && !o.Ceiling && o.On == null && !under.Contains(o) && Overlaps(r, o.Footprint));
            }
        }
    }
}
