using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace UnturnedSim.Tests
{
    // Round 12c, strawberry's feedback on the décor: the upper cabinets the counter itself hung upside down, the potted
    // plants twice the size they were drawn. Read off the meshes and the generated pieces, with numbers of the tests' own.
    public partial class BuildingGenTests
    {
        // AC: the upper cabinets are the counter hung upside down (strawberry: "replace the custom wall cabinets with
        // COUNTERS hung UPSIDE DOWN from the ceiling: the base counter mesh flipped"): Upper_Counter_0.obj is Counter_0.obj
        // turned half round about its front axis -- every vertex (x, y, z) at (-x, y - back, middle - z), every normal turned
        // with it, the faces and UVs as they were -- wearing the counter's own texture; so it can never drift from the
        // counter it is. And then a lid: the counter has no bottom (it stands on the floor), so turned over its top is open
        // and a cutaway looks down into it -- one quad over its whole outline at its top, facing up, and nothing else added.
        // (Placed: WallDecorFollowsTheRules, its top flush under the ceiling.)
        // BREAK IT: mirror it instead of turning it (z -> -z alone); hand it another texture; leave the lid off, or wind it
        // facing down.
        [Test]
        public void UpperCounterIsTheCounterHung()
        {
            string dir = Objects();
            Assert.That(dir, Is.Not.Null, "game/content/objects not found");
            var src = File.ReadAllLines(Path.Combine(dir, "Counter_0.obj"));
            var dst = File.ReadAllLines(Path.Combine(dir, "Upper_Counter_0.obj"));
            static float[] V(string l) => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(3).Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var sv = src.Where(l => l.StartsWith("v ")).Select(V).ToList();
            float back = sv.Min(v => v[1]), mid = (sv.Min(v => v[2]) + sv.Max(v => v[2])) * 0.5f;
            var bad = new List<string>();
            Assert.That(dst.Length, Is.EqualTo(src.Length + 8), "line for line, then the lid's four corners, uv, normal and two faces");
            for (int i = 0; i < src.Length; i++)
            {
                if (src[i].StartsWith("v "))
                {
                    var a = V(src[i]); var b = V(dst[i]);
                    if (MathF.Abs(b[0] + a[0]) > 1e-5f || MathF.Abs(b[1] - (a[1] - back)) > 1e-5f || MathF.Abs(b[2] - (mid - a[2])) > 1e-5f) bad.Add($"line {i}: {src[i]} -> {dst[i]}");
                }
                else if (src[i].StartsWith("vn "))
                {
                    var a = V(src[i]); var b = V(dst[i]);
                    if (MathF.Abs(b[0] + a[0]) > 1e-5f || MathF.Abs(b[1] - a[1]) > 1e-5f || MathF.Abs(b[2] + a[2]) > 1e-5f) bad.Add($"line {i}: normal {src[i]} -> {dst[i]}");
                }
                else if (src[i] != dst[i]) bad.Add($"line {i}: {src[i]} became {dst[i]}");
            }
            // the lid: its corners the turned outline's at its top, its normal up, its two triangles wound up and covering it
            var tv = dst.Take(src.Length).Where(l => l.StartsWith("v ")).Select(V).ToList();
            float x0 = tv.Min(v => v[0]), x1 = tv.Max(v => v[0]), y0 = tv.Min(v => v[1]), y1 = tv.Max(v => v[1]), top = tv.Max(v => v[2]);
            var lid = dst.Skip(src.Length).ToList();
            var lv = lid.Where(l => l.StartsWith("v ")).Select(V).ToList();
            var all = dst.Where(l => l.StartsWith("v ")).Select(V).ToList();
            var ln = lid.Where(l => l.StartsWith("vn ")).Select(V).ToList();
            if (lv.Count != 4 || ln.Count != 1 || lid.Count(l => l.StartsWith("vt ")) != 1 || lid.Count(l => l.StartsWith("f ")) != 2)
                bad.Add($"the lid is {lv.Count} corners, {ln.Count} normals, {lid.Count(l => l.StartsWith("f "))} faces");
            else
            {
                var want = new[] { (x0, y0), (x1, y0), (x1, y1), (x0, y1) };
                if (lv.Any(v => MathF.Abs(v[2] - top) > 1e-5f) || want.Any(w => !lv.Any(v => MathF.Abs(v[0] - w.Item1) < 1e-5f && MathF.Abs(v[1] - w.Item2) < 1e-5f)))
                    bad.Add($"the lid's corners {string.Join(" ", lv.Select(v => $"({v[0]:0.###},{v[1]:0.###},{v[2]:0.###})"))} are not the outline ({x0},{y0})..({x1},{y1}) at {top}");
                if (ln[0][2] < 0.999f) bad.Add($"the lid's normal is ({ln[0][0]},{ln[0][1]},{ln[0][2]}), not up");
                float area = 0f;
                foreach (var f in lid.Where(l => l.StartsWith("f ")))
                {
                    var c = f.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(q => all[int.Parse(q.Split('/')[0]) - 1]).ToArray();
                    float up = (c[1][0] - c[0][0]) * (c[2][1] - c[0][1]) - (c[1][1] - c[0][1]) * (c[2][0] - c[0][0]);
                    if (up <= 0f) bad.Add($"the lid's face {f} is wound facing down");
                    area += MathF.Abs(up) * 0.5f;
                }
                if (MathF.Abs(area - (x1 - x0) * (y1 - y0)) > 1e-4f) bad.Add($"the lid covers {area:0.000} m2 of the {(x1 - x0) * (y1 - y0):0.000} outline");
            }
            Assert.That(File.ReadAllBytes(Path.Combine(dir, "Upper_Counter_0_tex.png")), Is.EqualTo(File.ReadAllBytes(Path.Combine(dir, "Counter_0_tex.png"))), "the counter's own texture");
            TestContext.Progress.WriteLine($"[upper-counter] {sv.Count} vertices turned, a lid of {(x1 - x0) * (y1 - y0):0.000} m2 at {top:0.000}; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        // AD: the potted plants twice the size they were drawn (strawberry: "scale ALL four up by at least 2x"), registered at
        // that size so the furnisher places them by it -- each one's height 2x the art's (Table 0.401, Flowers 0.368, Upright
        // 0.852, Tall 1.701 m) -- and every one under the lowest ceiling a room has (a storey under another, 3.75 m) with
        // 0.3 m to spare; and none stands under a ceiling light hanging lower than its top.
        // BREAK IT: leave a plant at its drawn size; let the tall one stand under a pendant.
        [Test]
        public void PottedPlantsStandTwiceTheirSize()
        {
            var drawn = new Dictionary<string, float> { ["Potted_Plant_Table_0"] = 0.401f, ["Potted_Flowers_0"] = 0.368f, ["Potted_Plant_Upright_0"] = 0.852f, ["Potted_Plant_Tall_0"] = 1.701f };
            var bad = new List<string>();
            foreach (var (n, h) in drawn)
            {
                float s = FurnitureCatalog.Sizes[n].H / h;
                if (s < 1.99f) bad.Add($"{n}: {FurnitureCatalog.Sizes[n].H:0.000} m, {s:0.00}x the art's {h}");
                if (FurnitureCatalog.Sizes[n].H > Storey - BuildingGen.Slab - 0.3f) bad.Add($"{n}: {FurnitureCatalog.Sizes[n].H:0.000} m, too tall for a 3.75 m ceiling with 0.3 m to spare");
            }
            int plants = 0, underLights = 0;
            foreach (var (spec, res) in All())
                foreach (var m in res.Furniture.Where(m => drawn.ContainsKey(m.Name)))
                {
                    plants++;
                    float top = m.Y + FurnitureCatalog.Sizes[m.Name].H;
                    foreach (var o in res.Furniture.Where(o => o.Ceiling && o.Floor == m.Floor && Overlap(m.Footprint, o.Footprint, 0f)))
                    {
                        underLights++;
                        float bottom = o.Y - (o.Name.Contains("Pendant") ? 0.95f : 0.28f);
                        if (bottom < top + 0.05f) bad.Add($"{Name(spec)}: {m} stands up to {top - m.Floor * Storey:0.00} m under {o.Name}, down to {bottom - m.Floor * Storey:0.00}");
                    }
                }
            TestContext.Progress.WriteLine($"[plants-2x] {string.Join(", ", drawn.Select(kv => $"{kv.Key} {FurnitureCatalog.Sizes[kv.Key].H:0.000} m ({FurnitureCatalog.Sizes[kv.Key].H / kv.Value:0.00}x)"))}; "
                                           + $"{plants} plants placed, {underLights} under a light's outline; {bad.Count} problems{(bad.Count > 0 ? ": " + bad[0] : "")}");
            Assert.That(plants, Is.GreaterThan(300));
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(20)));
        }
    }
}
