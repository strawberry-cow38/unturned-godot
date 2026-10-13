using System;
using System.Collections.Generic;
using System.Linq;
using static UnturnedSim.RoomKind;

namespace UnturnedSim
{
    /// <summary>Where a room's potted plant goes.</summary>
    public enum PlantSpot
    {
        /// <summary>On the floor, its back to a wall: in a free corner, or beside the couch.</summary>
        Floor,
        /// <summary>On a top: a bare kitchen counter's back corner, the middle of a dining or a kitchen table. (Not the
        /// television's table: the set leaves 0.38 m either side of it, and the smallest plant is 0.32 m across.)</summary>
        Top,
    }

    public static partial class BuildingGen
    {
        /// <summary>astraclaw's potted plants (strawberry: "integrate the potted plant props astra made a while ago"), the
        /// four she approved -- never Potted_Plant_Floor_0, which she did not. Measured off their OBJs (FurnitureCatalog):
        /// the table plant and the flowers stand 0.40 and 0.37 m on pots 0.32 and 0.44 m across, small enough for a counter
        /// or a table top; the upright one stands 0.85 m, 2.0 m to its top on a 1.19 m table -- over a standing player's
        /// eye -- and the tall one 1.70 m, its leaves 1.39 x 1.21 m across: those two stand on the floor, as astraclaw drew
        /// them ("an upright floor plant, and a larger broadleaf floor plant").</summary>
        public static readonly IReadOnlyList<string> FloorPlants = new[] { "Potted_Plant_Tall_0", "Potted_Plant_Upright_0" };
        public static readonly IReadOnlyList<string> TopPlants = new[] { "Potted_Plant_Table_0", "Potted_Flowers_0" };

        /// <summary>What each kind of room gets, in order: each spot at its odds, one plant to a spot -- so none to two a room
        /// -- and a spot with nowhere fitting gets none.</summary>
        public static readonly IReadOnlyDictionary<RoomKind, (PlantSpot Spot, int Chance)[]> PlantRecipes = new Dictionary<RoomKind, (PlantSpot, int)[]>
        {
            [LivingRoom] = new[] { (PlantSpot.Floor, 70), (PlantSpot.Floor, 30) },
            [DiningRoom] = new[] { (PlantSpot.Top, 60), (PlantSpot.Floor, 30) },
            [Kitchen] = new[] { (PlantSpot.Top, 60) },
            [Hall] = new[] { (PlantSpot.Floor, 40) },
            [Bedroom] = new[] { (PlantSpot.Floor, 35) },
            [Office] = new[] { (PlantSpot.Floor, 40) },
        };
        /// <summary>The odds a floor plant is the tall one rather than the upright one, by room: the big rooms more often
        /// (and either gives way to the other where it will not fit).</summary>
        public static readonly IReadOnlyDictionary<RoomKind, int> TallPlantOdds = new Dictionary<RoomKind, int>
        {
            [LivingRoom] = 60, [DiningRoom] = 50, [Office] = 50, [Hall] = 40, [Bedroom] = 30,
        };

        /// <summary>A floor plant's leaves keep PlantClear off every other piece (its outline, as Intersects reads it), and
        /// one beside the couch stands at most PlantBeside off its side. A top plant stands PlantInset in from its top's
        /// edges and off whatever else is on it.</summary>
        public const float PlantClear = 0.1f, PlantBeside = 0.5f, PlantInset = 0.05f;

        /// <summary>Testing only: furnish with no potted plants on this thread.</summary>
        [ThreadStatic] public static bool NoPlants;
        /// <summary>Debugging only: told each plant set down.</summary>
        [ThreadStatic] public static Action<FurniturePlan> PlantTrace;

        sealed partial class Furnisher
        {
            /// <summary>Set the room's potted plants, once its whole floor is furnished and before its wall décor (Furnish):
            /// its kind's recipe (<see cref="PlantRecipes"/>), spot by spot. Its draws are its OWN -- an Rng seeded from the
            /// building's seed and the room's key, as the décor's, never the Furnisher's shared one -- and a plant goes in
            /// through Put (clear floor, out of every doorway, swing, stair tile and hole, the room still walkable).</summary>
            public void Plants()
            {
                if (!PlantRecipes.TryGetValue(_room.Kind, out var recipe)) return;
                var rng = new Rng(unchecked((((((Seed * 486187739 + _room.Floor) * 486187739 + _room.X0) * 486187739 + _room.Z0) * 486187739 + _room.X1) * 486187739 + _room.Z1) ^ 0x9A47));
                foreach (var (spot, chance) in recipe)
                {
                    if (rng.Int(100) >= chance) continue;
                    if (spot == PlantSpot.Floor)
                    {
                        bool tall = rng.Int(100) < TallPlantOdds.GetValueOrDefault(_room.Kind);
                        foreach (var name in tall ? new[] { FloorPlants[0], FloorPlants[1] } : new[] { FloorPlants[1], FloorPlants[0] })
                            if (FloorPlant(name, rng) is { } m) { Told(m); break; }
                    }
                    else if (TopPlant(rng) is { } m) Told(m);
                }
            }

            void Told(FurniturePlan m)
            {
                FurnishTrace?.Invoke($"plant {m}{(m.On != null ? " on " + m.On.Name : "")}");
                PlantTrace?.Invoke(m);
            }

            /// <summary>A floor plant in a free corner (as far from the doors as it can, a floor lamp's place) or, better, just
            /// beside the couch -- its back to the wall either way, which keeps it out of the ways across the room; out of the
            /// windows' and the fireplace's zones (BlocksWindow), from under anything hung lower than its top (UnderHung), and
            /// PlantClear off every other piece. (A check that it never closes a way a player 0.7 m across had through the
            /// room refused nothing in 792 buildings -- corners and couch sides never stand in one -- and is not kept; the
            /// tests walk the rooms instead: PottedPlantsStandWhereTheyShould.)</summary>
            FurniturePlan FloorPlant(string name, Rng rng)
            {
                var s = FurnitureCatalog.Sizes[name];
                var couches = _mine.Where(m => m.Name.StartsWith("Couch") && m.On == null && !m.Mounted).ToList();
                double Score(float x, float z, float yaw)
                {
                    float gap = BesideCouch(name, (x, z, yaw), couches);
                    return gap >= 0f ? 20 - 5 * MathF.Abs(gap - 0.2f) : 0.5 * NearestDoor(x, z);
                }
                var spots = Against(name, Side.Back).Where(p =>
                {
                    if (BesideCouch(name, p, couches) < 0f && !Corner(name, p)) return false;
                    var fp = FurnitureCatalog.Footprint(name, p.X, p.Z, p.Yaw);
                    return !BlocksWindow(fp) && !UnderHung(fp, s.H) && !Crowds(name, p);
                }).ToList();
                if (spots.Count == 0) return null;
                return Put(name, spots, Score, 0f, rng);
            }

            /// <summary>How far a floor piece at p stands off the side of one of the couches -- the same way round, its back to
            /// the same wall, along from it -- or -1 where it is beside none (more than PlantBeside off).</summary>
            static float BesideCouch(string name, (float X, float Z, float Yaw) p, List<FurniturePlan> couches)
            {
                var fp = FurnitureCatalog.Footprint(name, p.X, p.Z, p.Yaw);
                var (fx, _) = FrontOf(p.Yaw);
                float best = -1f;
                foreach (var c in couches)
                {
                    if (MathF.Abs(((c.Yaw - p.Yaw) % 360f + 540f) % 360f - 180f) > 10f) continue;
                    var cf = c.Footprint;
                    // along the wall (across the way they face), and how much they share in depth
                    float gap = fx != 0 ? MathF.Max(fp.Z0 - cf.Z1, cf.Z0 - fp.Z1) : MathF.Max(fp.X0 - cf.X1, cf.X0 - fp.X1);
                    float shared = fx != 0 ? MathF.Min(fp.X1, cf.X1) - MathF.Max(fp.X0, cf.X0) : MathF.Min(fp.Z1, cf.Z1) - MathF.Max(fp.Z0, cf.Z0);
                    if (gap < 0f || gap > PlantBeside || shared < 0.3f) continue;
                    if (best < 0f || gap < best) best = gap;
                }
                return best;
            }

            /// <summary>Does a floor plant's footprint stand in a window's zone or the fire's: the floor plan's (the windows
            /// it was laid with, and the view from the couch to its set), and the windows as they stand now (Windows) --
            /// across the opening and 0.1 m either side, 0.7 m into the room, as the floor plan lays one -- but for a vent or a
            /// bathroom's slit, over the tallest plant's head.</summary>
            bool BlocksWindow((float X0, float Z0, float X1, float Z1) fp)
            {
                if (_p.Cells(fp).Any(c => _p.KeepTall[c.I, c.J])) return true;
                foreach (var (w, o, side) in Windows())
                {
                    if (o.V >= 2.5f) continue;
                    var (dx, dz) = PlanTrig.CosSin(w.Yaw); dz = -dz;
                    float nx = -dz * side, nz = dx * side, t = w.Thickness * 0.5f + 0.7f;
                    float ax = w.X + dx * (o.U - 0.1f), az = w.Z + dz * (o.U - 0.1f), bx = w.X + dx * (o.U1 + 0.1f), bz = w.Z + dz * (o.U1 + 0.1f);
                    var zone = (MathF.Min(MathF.Min(ax, bx), MathF.Min(ax, bx) + nx * t), MathF.Min(MathF.Min(az, bz), MathF.Min(az, bz) + nz * t),
                                MathF.Max(MathF.Max(ax, bx), MathF.Max(ax, bx) + nx * t), MathF.Max(MathF.Max(az, bz), MathF.Max(az, bz) + nz * t));
                    if (Overlaps(fp, zone)) return true;
                }
                return false;
            }

            /// <summary>Is anything hung on this floor (the television, a clock) within PlantClear of a plant standing to
            /// `top` over the floor, and lower than that top and PlantClear?</summary>
            bool UnderHung((float X0, float Z0, float X1, float Z1) fp, float top)
                => _res.Furniture.Any(o => o.Mounted && o.Floor == _room.Floor && Overlaps(Grow(fp, PlantClear), o.Footprint)
                                           && o.Y - FurnitureCatalog.Sizes[o.Name].H < _room.Floor * Storey + top + PlantClear);

            /// <summary>Would a floor piece at p stand within PlantClear of another standing in the room?</summary>
            bool Crowds(string name, (float X, float Z, float Yaw) p)
            {
                var me = new FurniturePlan { Name = name, X = p.X, Z = p.Z, Yaw = p.Yaw };
                return _mine.Any(o => o.On == null && !o.Mounted && !o.Ceiling && FurnitureCatalog.Intersects(me, o, -PlantClear));
            }

            /// <summary>A small plant on a top: a kitchen's bare counter in a back corner (the cook's, by the sink or the
            /// stove, last), else its table's middle; a dining table's middle. It stands PlantInset in from the top's edges
            /// and off whatever else is on it, from under anything hung lower than its top, and out of the windows' zones
            /// (BlocksWindow) as a floor plant does: on a counter under a window it stands in front of the glass.</summary>
            FurniturePlan TopPlant(Rng rng)
            {
                var spots = new List<(FurniturePlan Host, float Along, float Back, string[] Names)>();
                bool flowersFirst = rng.Int(100) < 50;
                var either = flowersFirst ? new[] { TopPlants[1], TopPlants[0] } : new[] { TopPlants[0], TopPlants[1] };
                bool Table(FurniturePlan m) => m.Name is "Table_Wood_0" or "Table_Wood_1" or "Table_Wood_3";
                if (_room.Kind == RoomKind.Kitchen)
                {
                    var bare = _mine.Where(m => m.Name == "Counter_0" && m.On == null && !_res.Furniture.Any(t => t.On == m))
                                    .OrderBy(c => _mine.Any(h => h.Name is "Counter_1" or "Oven_0" && Beside(c, h)) ? 1 : 0).ToList();
                    int side = rng.Int(2) * 2 - 1;
                    foreach (var c in bare)
                        foreach (int sg in new[] { side, -side })
                            spots.Add((c, sg, float.NaN, either));
                }
                if (_room.Kind is RoomKind.Kitchen or RoomKind.DiningRoom)
                    foreach (var t in _mine.Where(m => Table(m) && m.On == null && !_res.Furniture.Any(o => o.On == m)))
                        spots.Add((t, 0f, 0f, rng.Int(100) < 70 ? new[] { TopPlants[1], TopPlants[0] } : new[] { TopPlants[0], TopPlants[1] }));
                foreach (var (host, along0, back0, names) in spots)
                    foreach (var name in names)
                    {
                        var hs = FurnitureCatalog.Sizes[host.Name]; var ps = FurnitureCatalog.Sizes[name];
                        // a counter's back corner: along is the side (+1 right, -1 left) until here
                        float along = float.IsNaN(back0) ? (along0 > 0 ? hs.X1 - PlantInset - ps.X1 : hs.X0 + PlantInset - ps.X0) : along0;
                        float back = float.IsNaN(back0) ? hs.Y0 + PlantInset - ps.Y0 : back0;
                        if (!OnTopFits(host, name, along, back)) continue;
                        var (fx, fz) = host.Front; var (rx, rz) = host.Right;
                        var fp = FurnitureCatalog.Footprint(name, host.X + rx * along + fx * back, host.Z + rz * along + fz * back, host.Yaw);
                        // (a counter under a window: its plant would stand in the glass)
                        if (UnderHung(fp, host.Y - _room.Floor * Storey + hs.H + ps.H) || BlocksWindow(fp)) continue;
                        return On(host, name, along, back, host.Yaw);
                    }
                return null;
            }

            /// <summary>Does a plant at (along, back) on a host's top -- turned as the host is -- stand PlantInset in from its
            /// edges (a round table's rim) and off whatever else stands on it?</summary>
            bool OnTopFits(FurniturePlan host, string name, float along, float back)
            {
                var hs = FurnitureCatalog.Sizes[host.Name]; var ps = FurnitureCatalog.Sizes[name];
                float x0 = along + ps.X0, x1 = along + ps.X1, y0 = back + ps.Y0, y1 = back + ps.Y1;
                if (FurnitureCatalog.Round.TryGetValue(host.Name, out var r))
                {
                    if (new[] { (x0, y0), (x1, y0), (x0, y1), (x1, y1) }.Any(q => MathF.Sqrt(q.Item1 * q.Item1 + q.Item2 * q.Item2) > r - PlantInset)) return false;
                }
                else if (x0 < hs.X0 + PlantInset || x1 > hs.X1 - PlantInset || y0 < hs.Y0 + PlantInset || y1 > hs.Y1 - PlantInset) return false;
                // off what else is on it, in the host's frame
                var (fx, fz) = host.Front; var (rx, rz) = host.Right;
                foreach (var t in _res.Furniture.Where(t => t.On == host))
                {
                    var ts = FurnitureCatalog.Sizes[t.Name];
                    float ta = (t.X - host.X) * rx + (t.Z - host.Z) * rz, tb = (t.X - host.X) * fx + (t.Z - host.Z) * fz;
                    // (what stands on a host is turned as it is, or half round: its box either way)
                    float tx0 = ta + MathF.Min(ts.X0, -ts.X1), tx1 = ta + MathF.Max(ts.X1, -ts.X0), ty0 = tb + MathF.Min(ts.Y0, -ts.Y1), ty1 = tb + MathF.Max(ts.Y1, -ts.Y0);
                    if (x0 < tx1 + PlantInset && x1 > tx0 - PlantInset && y0 < ty1 + PlantInset && y1 > ty0 - PlantInset) return false;
                }
                return true;
            }
        }
    }
}
