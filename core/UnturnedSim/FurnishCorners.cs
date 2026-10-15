using System;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedSim
{
    public static partial class BuildingGen
    {
        /// <summary>The corner cabinet that goes with a run's counters (corner_cabinet_art_notes.md): the grey one with the
        /// grey set's Counter_2, the wood one with Counter_0 -- and with anything else, the wood the kitchen lays.</summary>
        public static string CornerCabinetFor(IEnumerable<string> counters)
            => counters.Contains("Counter_2") ? "Corner_Cabinet_1_Solid" : "Corner_Cabinet_0_Solid";

        /// <summary>Percent of kitchens laid as a straight run that start it from a corner cabinet where an inside corner
        /// takes one, and of kitchens with a free inside corner left that put a lone one in it (strawberry, round 12b: "allow
        /// corner cabinets even if there arent 2 cabinets connecting a corner"). Each is a choice, not a rule: half.</summary>
        public const int CornerRunChance = 50, LoneCornerChance = 50;

        sealed partial class Furnisher
        {
            /// <summary>Draws for the corner cabinets' choices and trials, apart from the shared stream (as PaneRng): a kitchen
            /// that tries one and does without leaves every later room's furniture where it was.</summary>
            Rng _cornerRng;
            Rng CornerRng => _cornerRng ??= new Rng(unchecked(((Seed * 486187739 + _room.Floor) * 486187739 + _room.X0) * 486187739 + _room.Z0) ^ 0xC0C0);
            /// <summary>Set while the kitchen's layout is one only round 12b lays (a straight run from a corner cabinet, an L
            /// keeping its cabinet with the stove on its own wall): its counters are topped up on CornerRng too, so a layout
            /// given up after all leaves the shared stream -- and every later room -- as it was; and if the kitchen eats in and
            /// its table will not go, the kitchen is laid again without it (Kitchen).</summary>
            bool _cornered;
            Rng TopUpRng => _cornered ? CornerRng : null;

            /// <summary>The room's inside corners, found as L finds them: each pair of perpendicular wall runs where b's face
            /// stands at one end of a (endA -1: a's Lo end, +1: its Hi end) and a's at one end of b, within 0.15 m. Both orders
            /// of every pair.</summary>
            static IEnumerable<((int Nx, int Nz, float Face, float Lo, float Hi) A, (int Nx, int Nz, float Face, float Lo, float Hi) B, int EndA, int EndB)> InsideCorners(
                List<(int Nx, int Nz, float Face, float Lo, float Hi)> runs)
            {
                const float Near = 0.15f;
                foreach (var a in runs)
                    foreach (var b in runs)
                    {
                        if ((a.Nx != 0) == (b.Nx != 0)) continue;   // perpendicular
                        int endA = MathF.Abs(b.Face - a.Lo) < Near ? -1 : MathF.Abs(b.Face - a.Hi) < Near ? 1 : 0;
                        int endB = MathF.Abs(a.Face - b.Lo) < Near ? -1 : MathF.Abs(a.Face - b.Hi) < Near ? 1 : 0;
                        if (endA == 0 || endB == 0) continue;
                        yield return (a, b, endA, endB);
                    }
            }

            const float CornerSide = 1.75f;
            /// <summary>Do both walls of the corner back a corner cabinet for its whole 1.75 m square? (A run is wall all along:
            /// a doorway ends it.)</summary>
            static bool CornerBacked((int Nx, int Nz, float Face, float Lo, float Hi) a, (int Nx, int Nz, float Face, float Lo, float Hi) b)
                => a.Hi - a.Lo >= CornerSide + 0.03f && b.Hi - b.Lo >= CornerSide + 0.03f;

            /// <summary>A corner cabinet (`name`) in the inside corner where runs a and b meet: its back corner in the room's, a
            /// back to each wall (its local -Y to one, -X to the other), the diagonal front to the room -- and only where both
            /// walls back it (CornerBacked; `backing` false leaves that to the caller). `fits`, when given, is asked of its
            /// footprint first. Null when it will not go.</summary>
            FurniturePlan CornerCabinet((int Nx, int Nz, float Face, float Lo, float Hi) a, (int Nx, int Nz, float Face, float Lo, float Hi) b,
                                        string name = "Corner_Cabinet_0_Solid", Rng rng = null, Func<(float X0, float Z0, float X1, float Z1), bool> fits = null, bool backing = true)
            {
                bool backed = !backing || CornerBacked(a, b);
                float cx = a.Nx != 0 ? a.Face : b.Face, cz = a.Nx != 0 ? b.Face : a.Face;
                float off = CornerSide * 0.5f + 0.03f;
                float x = cx + (a.Nx + b.Nx) * off, z = cz + (a.Nz + b.Nz) * off;
                for (int k = 0; k < 4; k++)
                {
                    float yaw = k * 90f;
                    var (fx, fz) = FrontOf(yaw); var (rx, rz) = RightOf(yaw);
                    // front into the room off one wall, right off the other: its back corner is then the room's
                    bool square = (fx == a.Nx && fz == a.Nz && rx == b.Nx && rz == b.Nz) || (fx == b.Nx && fz == b.Nz && rx == a.Nx && rz == a.Nz);
                    if (!square) continue;
                    if (!backed || (fits != null && !fits(FurnitureCatalog.Footprint(name, x, z, yaw)))) return null;
                    return Put(name, new[] { (x, z, yaw) }, null, 0f, rng);
                }
                return null;
            }

            /// <summary>A straight run from a corner cabinet (strawberry, round 12b: "allow corner cabinets even if there arent 2
            /// cabinets connecting a corner. if the cabinet is in a corner, allow it to be a corner cabinet regardless of its
            /// connecting cabinets"): the cabinet in an inside corner, then along one of its walls the sink, the dishwasher and
            /// the stove, and the fridge closing it past a counter unless it stands free -- where it does not go in line, on a
            /// wall of its own keeping the triangle. On its own draws (CornerRng): tried and given up, it leaves the kitchen,
            /// and every room after, as they were. Null when no corner takes it.</summary>
            List<FurniturePlan> CornerRun(List<(int Nx, int Nz, float Face, float Lo, float Hi)> runs, bool freeFridge)
            {
                foreach (var (a, b, endA, _) in InsideCorners(runs))
                {
                    var cab = CornerCabinet(a, b, CornerCabinetFor(new[] { "Counter_0" }), CornerRng);
                    if (cab == null) continue;
                    var names = new List<string> { "Counter_1", "Disher_0", "Oven_0" };
                    if (!freeFridge) names.AddRange(new[] { "Counter_0", "Fridge_0" });
                    var placed = Leg(a, Edge(cab, a, -endA), -endA, names, 3, CornerRng, Abut);
                    if (placed == null) { Remove(cab); continue; }
                    var sink = placed.First(m => m.Name == "Counter_1"); var oven = placed.First(m => m.Name == "Oven_0");
                    var fridge = placed.FirstOrDefault(m => m.Name == "Fridge_0");
                    if (fridge == null)
                    {
                        fridge = Put("Fridge_0", Against("Fridge_0", Side.Back).Where(c => Triangle(oven, sink, ("Fridge_0", c.X, c.Z, c.Yaw))),
                                     (x, z, _) => -MathF.Abs(D(x, z, sink) - 2.5f), 0f, CornerRng);
                        if (fridge != null) placed.Add(fridge);
                    }
                    if (fridge != null && Triangle(fridge, sink, ("Oven_0", oven.X, oven.Z, oven.Yaw)))
                    {
                        _cornered = true;
                        FurnishTrace?.Invoke($"corner cabinet in {_room}: a straight run from it");
                        placed.Add(cab);
                        return placed;
                    }
                    foreach (var m in placed) Remove(m);
                    Remove(cab);
                }
                return null;
            }

            /// <summary>The stove at the first of `cands` where a plain counter also goes beside it on its wall line (Worktop), on
            /// the corner's draws: [stove, counter], or null and nothing left standing.</summary>
            List<FurniturePlan> StoveWithCounter(IEnumerable<(float X, float Z, float Yaw)> cands)
            {
                foreach (var c in cands)
                {
                    var oven = Put("Oven_0", new[] { c }, null, 0f, CornerRng);
                    if (oven == null) continue;
                    foreach (var at in Abutting(oven, "Counter_0"))
                        if (Put("Counter_0", new[] { at }, null, 0f, CornerRng) is { } counter) return new List<FurniturePlan> { oven, counter };
                    Remove(oven);
                }
                return null;
            }

            /// <summary>A lone corner cabinet (strawberry, round 12b), an option in the kitchen's pool: once the kitchen has its
            /// run, its table and its island, about half the kitchens without one put one in an inside corner left free --
            /// backed by both walls, off every doorway and door's swing and every appliance's (Put), and not crowding the work
            /// triangle: 0.9 m (SinkStand: where you stand at a counter) clear of the fridge, the sink and the stove, and not
            /// inside the triangle they make. The room's counters say which palette.</summary>
            void LoneCorner(List<(int Nx, int Nz, float Face, float Lo, float Hi)> runs)
            {
                if (_mine.Any(m => m.Name.StartsWith("Corner_Cabinet")) || !CornerRng.Chance(LoneCornerChance)) return;
                var three = new[] { "Fridge_0", "Counter_1", "Oven_0" }.Select(n => _mine.FirstOrDefault(m => m.Name == n && m.On == null)).Where(m => m != null).ToList();
                bool Apart((float X0, float Z0, float X1, float Z1) fp)
                {
                    var grown = Grow(fp, SinkStand);
                    if (three.Any(t => Overlaps(grown, t.Footprint))) return false;
                    if (three.Count < 3) return true;
                    // (its middle outside the triangle the three stand round)
                    float px = (fp.X0 + fp.X1) * 0.5f, pz = (fp.Z0 + fp.Z1) * 0.5f;
                    float S(FurniturePlan p, FurniturePlan q) => (q.X - p.X) * (pz - p.Z) - (q.Z - p.Z) * (px - p.X);
                    float s0 = S(three[0], three[1]), s1 = S(three[1], three[2]), s2 = S(three[2], three[0]);
                    return !((s0 > 0 && s1 > 0 && s2 > 0) || (s0 < 0 && s1 < 0 && s2 < 0));
                }
                string name = CornerCabinetFor(_mine.Select(m => m.Name));
                foreach (var (a, b, _, _) in InsideCorners(runs))
                    if (CornerCabinet(a, b, name, CornerRng, Apart) != null) { FurnishTrace?.Invoke($"corner cabinet in {_room}: a lone one"); return; }
            }
        }
    }
}
