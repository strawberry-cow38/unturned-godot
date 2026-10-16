using System;
using System.Collections.Generic;
using System.Linq;
using static UnturnedSim.RoomKind;

namespace UnturnedSim
{
    public static partial class BuildingGen
    {
        /// <summary>Testing only: no HVAC vents -- the building as it was before them.</summary>
        [ThreadStatic] public static bool NoHvac;

        /// <summary>The rooms that get vents (strawberry, round 12: astraclaw's HVAC covers, "one per furnished room"): every
        /// kind the furnisher furnishes but a closet, a garage and a warehouse -- spaces nobody heats -- and the stairwell,
        /// which it does not furnish.</summary>
        public static readonly IReadOnlyCollection<RoomKind> HvacRooms = new HashSet<RoomKind>
        {
            Bedroom, Bathroom, Kitchen, LivingRoom, DiningRoom, Hall, Workshop, Storage, Office, Shop, BreakRoom,
        };
        /// <summary>A floor register stands this far off its wall (from the wall's face to its back edge): out from under a
        /// floor-length curtain's hem, which hangs RuntimeDecor.CurtainDepth into the room, by CurtainClear.</summary>
        public const float RegisterOff = RuntimeDecor.CurtainDepth + CurtainClear;
        /// <summary>A wall grille's top this far under the ceiling over it (the lead's brief: "its top about 0.3 m under the
        /// ceiling").</summary>
        public const float GrilleUnderCeiling = 0.3f;
        /// <summary>A wall grille keeps this far off a curtain, in plan.</summary>
        public const float GrilleOffCurtain = 0.3f;

        /// <summary>Hand each room's HVAC and electrical records to its label in res.Rooms (the one anchored in it, its kind,
        /// on its storey): the label is what a save keeps and what reaches the editor. Done when the rooms are furnished, and
        /// again when the building is turned half round (TurnHalf makes the labels afresh).</summary>
        static void LabelRecords(BuildingGenResult res)
        {
            for (int i = 0; i < res.Rooms.Count; i++)
            {
                var d = res.Rooms[i];
                var g = res.Layout.FirstOrDefault(r => r.Floor == d.Floor && r.Kind == d.Kind && r.Holds(d.X, d.Z));
                d.Hvac = g?.Hvac;
                d.Electrical = g?.Electrical;
                res.Rooms[i] = d;
            }
        }

        sealed partial class Furnisher
        {
            /// <summary>The room's vents (astraclaw's covers: Floor_Vent_0, Wall_Vent_0), and its HVAC record: a floor register
            /// under a window where one will go, else along an outside wall (FloorRegister); a wall grille high on a wall
            /// between it and another room (WallGrille). On its own draws, after everything they keep clear of is in.</summary>
            public void Vents()
            {
                if (!HvacRooms.Contains(_room.Kind)) return;
                var rng = OwnRng(0x4A7C);
                var hvac = new RoomHvac();
                foreach (var (m, kind) in new[] { (FloorRegister(rng), HvacVentKind.FloorRegister), (WallGrille(rng), HvacVentKind.WallGrille) })
                {
                    if (m == null) continue;
                    hvac.Add(new HvacVent(kind, _res.HvacVents.Count, m.X, m.Y, m.Z, m.Yaw));
                    _res.HvacVents.Add(m);
                    FurnishTrace?.Invoke($"vent {m} ({kind})");
                }
                if (hvac.HasHvac) _room.Hvac = hvac;
            }

            /// <summary>A floor register, its long side along the wall RegisterOff out from it: under one of the room's windows
            /// (its middle, else along under the opening), else along a wall with no room behind it -- an outside wall --
            /// wherever it fits first on the room's own draws. It fits on clear floor of this room: no piece, rug, stair, flight's
            /// end or door's swing on it.</summary>
            FurniturePlan FloorRegister(Rng rng)
            {
                const string name = "Floor_Vent_0";
                var s = FurnitureCatalog.Sizes[name];
                float half = (s.X1 - s.X0) * 0.5f, back = RegisterOff - s.Y0;
                // under the windows, middle first
                foreach (var (w, o, side) in Windows().Where(q => q.O.Glazed && q.O.Archetype is ArchWindow or ArchTallWindow or ArchSlit))
                {
                    var (dx, dz) = PlanTrig.CosSin(w.Yaw); dz = -dz;
                    float nx = -dz * side, nz = dx * side, t = w.Thickness * 0.5f + back;
                    float mid = (o.U + o.U1) * 0.5f, reach = MathF.Max(0f, o.Width * 0.5f - half);
                    var us = new List<float> { mid };
                    for (float d = 0.25f; d <= reach + 1e-3f; d += 0.25f) { us.Add(mid - d); us.Add(mid + d); }
                    foreach (float u in us)
                    {
                        float x = w.X + dx * u + nx * t, z = w.Z + dz * u + nz * t, yaw = YawFacing(nx, nz);
                        if (RegisterFits(name, x, z, yaw)) return OnFloor(Fixture(name, x, z, yaw));
                    }
                }
                // along an outside wall: every 0.25 m of the faces with no room behind them, ranked on the room's own draws
                var cands = new List<(float X, float Z, float Yaw)>();
                foreach (var f in AllFaces().Where(f => !FireFace(f)))
                    for (float t = f.Lo + half + 0.05f; t <= f.Hi - half - 0.05f + 1e-3f; t += 0.25f)
                    {
                        if (Behind(f, t - half) != null || Behind(f, t) != null || Behind(f, t + half) != null) continue;
                        float off = back + 0.05f;   // (the grid's face is up to a cell in front of the wall's)
                        cands.Add(f.Nx != 0 ? (f.At + f.Nx * off, t, YawFacing(f.Nx, 0)) : (t, f.At + f.Nz * off, YawFacing(0, f.Nz)));
                    }
                foreach (var (x, z, yaw) in Rank(cands, null, rng))
                    if (RegisterFits(name, x, z, yaw)) return OnFloor(Fixture(name, x, z, yaw));
                return null;
            }

            /// <summary>A floor register stands ON the floor -- in a tiled or a concrete room, the room's cover 2 cm over the slab
            /// (BuildWalls), not the slab: at the slab's top its 3.5 cm grille stood 2 cm deep in the cover, the floor running
            /// through it (strawberry: "some floor vents are clipped with the floor"). So its base at the top of the highest floor
            /// surface under its middle that lies on its storey's slab (within 0.1 m over the slab's top: a cover, not a landing).</summary>
            FurniturePlan OnFloor(FurniturePlan m)
            {
                float slab = m.Y;
                foreach (var w in _res.Walls)
                    if (w.Kind == SurfaceKind.Floor && SurfaceTopAt(w, m.X, m.Z) is var t && t > m.Y && t < slab + 0.1f) m.Y = t;
                return m;
            }

            /// <summary>Is the floor clear for a register: all of it this room's floor, no wall, stair or piece on it (the grid,
            /// and the pieces' and stairs' exact outlines), no flight's end tile, no door's swing, no rug?</summary>
            bool RegisterFits(string name, float x, float z, float yaw)
            {
                var fp = FurnitureCatalog.Footprint(name, x, z, yaw);
                foreach (var (i, j) in _p.Cells(fp)) if (!InRoom(i, j) || _p.State[i, j] != FloorPlan.Free) return false;
                if (_p.StairRects.Concat(_p.TileRects).Concat(_p.LeafRects).Concat(_p.WallRects).Any(r => Overlaps(fp, r))) return false;
                if (_res.Furniture.Any(o => o.Floor == _room.Floor && o.On == null && !o.Mounted && !o.Ceiling && Overlaps(Grow(fp, 0.05f), o.Footprint))) return false;
                return !_res.RuntimeDecor.Concat(_res.HvacVents).Any(o => o.Floor == _room.Floor && !o.Mounted && Overlaps(fp, o.Footprint));
            }

            /// <summary>The room on this storey behind a face at `t` along it, past a wall of any thickness this generator builds
            /// (0.9 m back from the grid's face), or null: outside.</summary>
            GenRoom Behind(Face f, float t)
            {
                float x = f.Nx != 0 ? f.At - f.Nx * 0.9f : t, z = f.Nx != 0 ? t : f.At - f.Nz * 0.9f;
                return _res.Layout.FirstOrDefault(g => g.Floor == _room.Floor && g != _room && g.Holds(x, z));
            }

            /// <summary>A wall grille, its top GrilleUnderCeiling under the ceiling over it, on a wall with another room behind it
            /// all along its width -- never a fireplace's -- where the wall décor's rules let a piece that high hang
            /// (Stretches: DecorWindowGap off a window at any height, a doorway at its height, the stairs' wall, DecorGap off
            /// every hung piece -- the television, the clock, a mirror, the décor -- and nothing standing under it within
            /// DecorUnder), GrilleOffCurtain off a curtain: the middle of the longest such stretch, the room's own draw between
            /// near-equals.</summary>
            FurniturePlan WallGrille(Rng rng)
            {
                const string name = "Wall_Vent_0";
                var s = FurnitureCatalog.Sizes[name];
                float w = s.X1 - s.X0, half = w * 0.5f;
                // its span judged under the lowest ceiling a room has (it hangs higher under a roof: clearer still)
                float top = OpeningTop - GrilleUnderCeiling, bottom = top - 2f * s.H;
                var row = new DecorRow(name, new[] { _room.Kind }, 0f, "hvac");
                var cands = new List<(Face F, float T, double Score)>();
                foreach (var f in AllFaces())
                {
                    if (FireFace(f)) continue;
                    foreach (var (a, b) in Stretches(f, s.Y1 - s.Y0, bottom, top, row))
                    {
                        if (b - a < w) continue;
                        float mid = (a + b) * 0.5f;
                        double stretch = 0.2 * (b - a) + rng.Int(1000) * 0.002;
                        for (float t = a + half; t <= b - half + 1e-3f; t += 0.25f)
                            if (Behind(f, t - half + 0.05f) != null && Behind(f, t) != null && Behind(f, t + half - 0.05f) != null)
                                cands.Add((f, t, stretch - MathF.Abs(t - mid)));
                    }
                }
                foreach (var (f, t, _) in cands.OrderByDescending(c => c.Score).Take(60))
                {
                    var at = AtFace(name, f, t);
                    if (at == null) continue;
                    var fp = FurnitureCatalog.Footprint(name, at.Value.X, at.Value.Z, at.Value.Yaw);
                    if (_res.RuntimeDecor.Any(c => c.Floor == _room.Floor && c.Gen.Kind == RuntimeDecorKind.Curtain && Overlaps(Grow(fp, GrilleOffCurtain), c.Footprint))) continue;
                    float ceiling = new[] { (fp.X0, fp.Z0), (fp.X1, fp.Z0), (fp.X0, fp.Z1), (fp.X1, fp.Z1) }.Min(p => CeilingOver(p.Item1, p.Item2));
                    var m = Fixture(name, at.Value.X, at.Value.Z, at.Value.Yaw);
                    m.Y = ceiling - GrilleUnderCeiling - s.H;
                    m.Mounted = true;
                    return m;
                }
                return null;
            }

            FurniturePlan Fixture(string name, float x, float z, float yaw)
                => new FurniturePlan { Name = name, X = x, Y = _room.Floor * Storey, Z = z, Yaw = yaw, Floor = _room.Floor, Room = _room };
        }
    }
}
