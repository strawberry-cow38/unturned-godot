using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace UnturnedSim.Tests
{
    // Round 12, strawberry: "make sure the full wall tv is supported by the wall its on, not hanging off. fix gaps between
    // counters." Read off the generated walls, over the sweep's first seeds (her standing rule: no suite runs).
    public partial class BuildingGenTests
    {
        // AI: a wall television is on its wall end to end -- 3 cm behind its back, every 5 cm of its 3.8 m, is a full-storey
        // wall with no opening there at the set's height (not past a wall's end, round a corner, over a doorway or a window,
        // nor where a thinner wall carries the line on), and its back is on the wall's face (within 4 cm); and the hung
        // counters in a run abut, edge to edge.
        // BREAK IT: drop WallTv's seat (OnWall); drop PackCounterRuns.
        [Test]
        public void WallPiecesSitOnTheirWall()
        {
            var bad = new List<string>();
            int tvs = 0, pairs = 0;
            foreach (var spec in Specs().Where(s => s.Seed < 8 && s.Kind is BuildingKind.Residential or BuildingKind.Apartment))
            {
                var res = BuildingGen.Generate(spec);
                foreach (var m in res.Furniture.Where(m => m.Mounted && m.Name == "Television_0"))
                {
                    tvs++;
                    string nm = $"{Name(spec)}: {m}";
                    var s = FurnitureCatalog.Sizes[m.Name];
                    var (fx, fz) = m.Front; var (rx, rz) = m.Right;
                    float y0 = m.Y - m.Floor * Storey - s.H, y1 = m.Y - m.Floor * Storey + s.H;
                    var walls = WallsOn(res, m.Floor).ToList();
                    int off = 0, open = 0, gap = 0;
                    for (float u = s.X0 + 0.02f; u <= s.X1 - 0.02f; u += 0.05f)
                    {
                        float bx = m.X + rx * u + fx * s.Y0, bz = m.Z + rz * u + fz * s.Y0;
                        bool In(WallPlan w, float px, float pz, out float wu)
                        {
                            var d = Dir(w); var n = Normal(w);
                            wu = (px - w.X) * d.X + (pz - w.Z) * d.Z;
                            float wv = (px - w.X) * n.X + (pz - w.Z) * n.Z;
                            return wu >= 0f && wu <= w.Length && MathF.Abs(wv) <= w.Thickness * 0.5f;
                        }
                        var held = walls.Where(w => In(w, bx - fx * 0.03f, bz - fz * 0.03f, out _)).ToList();
                        if (held.Count == 0) { off++; continue; }
                        foreach (var w in held)
                        {
                            In(w, bx - fx * 0.03f, bz - fz * 0.03f, out float wu);
                            if (w.Openings.Any(o => wu > o.U && wu < o.U1 && o.V < y1 && o.V + o.Height > y0)) open++;
                        }
                        // its back on the face: a wall within 4 cm behind it
                        if (!walls.Any(w => In(w, bx - fx * 0.04f, bz - fz * 0.04f, out _) && !In(w, bx + fx * 0.002f, bz + fz * 0.002f, out _))) gap++;
                    }
                    if (off > 0) bad.Add($"{nm}: {off} of its 5 cm steps hang off the wall");
                    if (open > 0) bad.Add($"{nm}: {open} of its steps over an opening");
                    if (gap > 0) bad.Add($"{nm}: {gap} of its steps more than 4 cm off the wall's face");
                }
                foreach (var line in res.Furniture.Where(m => m.Mounted && m.Name == "Upper_Counter_0")
                                                  .GroupBy(m => (MathF.Round(m.Yaw), MathF.Round((MathF.Abs(m.Front.X) > 0.5f ? m.X : m.Z) * 20f), m.Floor)))
                {
                    bool alongZ = MathF.Abs(line.First().Front.X) > 0.5f;
                    var sorted = line.OrderBy(m => alongZ ? m.Z : m.X).ToList();
                    float w = FurnitureCatalog.Sizes["Upper_Counter_0"].X1 - FurnitureCatalog.Sizes["Upper_Counter_0"].X0;
                    for (int i = 1; i < sorted.Count; i++)
                    {
                        float d = alongZ ? sorted[i].Z - sorted[i - 1].Z : sorted[i].X - sorted[i - 1].X;
                        if (d > w + BuildingGen.CounterRunGap) continue;
                        pairs++;
                        if (MathF.Abs(d - w) > 0.01f) bad.Add($"{Name(spec)}: {sorted[i - 1]} and {sorted[i]} are {d - w:0.000} m apart edge to edge");
                    }
                }
            }
            TestContext.Progress.WriteLine($"[wall-support] {tvs} wall televisions, {pairs} neighbouring hung counters; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(tvs, Is.GreaterThan(20));
            Assert.That(pairs, Is.GreaterThan(50));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(20)));
        }
    }
}
