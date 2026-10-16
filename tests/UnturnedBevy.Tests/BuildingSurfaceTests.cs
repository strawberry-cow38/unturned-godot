using Godot;
using NUnit.Framework;
using System.Text.Json;
using UnturnedBevy;
using UnturnedSim;

namespace UnturnedBevyTests;

[TestFixture]
public sealed class BuildingSurfaceTests
{
    static string Root()
    {
        for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); directory != null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "game/content"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }
    static void Vector(Vector3 actual, JsonElement expected, string label, float tolerance = 2e-6f)
    {
        Assert.That(actual.X, Is.EqualTo(expected[0].GetSingle()).Within(tolerance), label + ".x");
        Assert.That(actual.Y, Is.EqualTo(expected[1].GetSingle()).Within(tolerance), label + ".y");
        Assert.That(actual.Z, Is.EqualTo(expected[2].GetSingle()).Within(tolerance), label + ".z");
    }
    static void Triangles(Vector3[] actual, JsonElement expected, Color tint, string label)
    {
        var vertices = expected.GetProperty("vertices");
        var normals = expected.GetProperty("normals");
        Assert.That(actual.Length, Is.EqualTo(vertices.GetArrayLength()), label);
        for (int i = 0; i < actual.Length; i++) Vector(actual[i], vertices[i], label + ".vertex" + i);
        for (int i = 0; i < actual.Length; i += 3)
        {
            var normal = (actual[i + 2] - actual[i]).Cross(actual[i + 1] - actual[i]).Normalized();
            // The oracle observes actual SurfaceTool arrays before ArrayMesh
            // normal packing, so geometry and normal arithmetic share a tight
            // tolerance rather than admitting compressed readback error.
            for (int k = 0; k < 3; k++) Vector(normal, normals[i + k], label + ".normal" + (i + k));
        }
        if (actual.Length == 0) return;
        var color = expected.GetProperty("color");
        Assert.That(new[] { tint.R, tint.G, tint.B, tint.A }, Is.EqualTo(color.EnumerateArray().Select(v => v.GetSingle()).ToArray()));
    }
    static void Shapes(BuildingCollision[] actual, JsonElement expected, string label)
    {
        Assert.That(actual.Length, Is.EqualTo(expected.GetArrayLength()), label);
        for (int i = 0; i < actual.Length; i++)
        {
            var row = expected[i];
            Vector(actual[i].Position, row.GetProperty("position"), label + ".position" + i);
            if (row.GetProperty("box").ValueKind != JsonValueKind.Null)
            {
                Assert.That(actual[i].Shape, Is.TypeOf<BuildingBox>());
                Vector(((BuildingBox)actual[i].Shape).Size, row.GetProperty("box"), label + ".box" + i);
            }
            else if (row.GetProperty("hull").ValueKind != JsonValueKind.Null)
            {
                Assert.That(actual[i].Shape, Is.TypeOf<BuildingHull>());
                var points = ((BuildingHull)actual[i].Shape).Points;
                var hull = row.GetProperty("hull");
                Assert.That(points.Length, Is.EqualTo(hull.GetArrayLength()));
                for (int j = 0; j < points.Length; j++) Vector(points[j], hull[j], label + ".hull" + i + "." + j);
            }
            else Assert.That(actual[i].Shape, Is.Null);
        }
    }

    [Test] public void CompleteMeshesMaterialsAndShapesMatchOriginalGodotSurface()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "tests/UnturnedBevy.Tests/fixtures/building-surfaces-godot.json")));
        var palettes = new BuildingPalettes(Path.Combine(Root(), "game/content"));
        int cases = 0;
        foreach (var row in document.RootElement.EnumerateArray())
        {
            string name = row.GetProperty("name").GetString();
            var plan = WallSave.Read(row.GetProperty("text").GetString().Split('\n')).Single();
            var surface = new BuildingSurface(plan, palettes);
            var geometry = surface.Build();
            Triangles(geometry.Front, row.GetProperty("front"), surface.Tint, name + ".front");
            Triangles(geometry.Back, row.GetProperty("back"), surface.BackTint, name + ".back");
            Triangles(geometry.Trim, row.GetProperty("trim"), surface.TrimTint, name + ".trim");
            Shapes(geometry.Solids, row.GetProperty("solids"), name + ".solids");
            Shapes(geometry.TrimSolids, row.GetProperty("trimSolids"), name + ".trimSolids");
            var inverse = surface.Basis.Inverse();
            Vector(inverse.X, row.GetProperty("inverse")[0], name + ".inverseX");
            Vector(inverse.Y, row.GetProperty("inverse")[1], name + ".inverseY");
            Vector(inverse.Z, row.GetProperty("inverse")[2], name + ".inverseZ");
            cases++;
        }
        Assert.That(cases, Is.EqualTo(12));
    }

    [Test] public void EmptyAndRebuiltSurfaceOutputsDoNotRetainOldSolids()
    {
        var surface = new BuildingSurface(new WallPlan { Length = 6, Height = 4 }, new BuildingPalettes(Path.Combine(Root(), "game/content")));
        Assert.That(surface.Build().Solids, Has.Length.EqualTo(1));
        surface.Kind = SurfaceKind.Floor;
        surface.Openings.Add(new WallOpening(0, 0, 6, 4));
        var empty = surface.Build();
        Assert.That(empty.Front, Is.Empty);
        Assert.That(empty.Solids, Is.Empty);
        Assert.That(empty.TrimSolids, Is.Empty);
        surface.Openings.Clear();
        Assert.That(surface.Build().Solids, Has.Length.EqualTo(1));
    }

    [Test] public void PaletteCacheBelongsToItsContentRootAndSourceModuloIsSigned()
    {
        var real = new BuildingPalettes(Path.Combine(Root(), "game/content"));
        var missing = new BuildingPalettes(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.That(real.Count, Is.GreaterThan(1));
        Assert.That(missing.Count, Is.Zero);
        Assert.That(missing.At(-1).Name, Is.EqualTo("fallback"));
        Assert.That(real.At(-1).Name, Is.EqualTo(real.At(real.Count - 1).Name));
        Assert.That(Mathf.PosMod(7, -5), Is.EqualTo(-3));
        Assert.That(Mathf.PosMod(-7, 5), Is.EqualTo(3));
        Assert.Throws<InvalidOperationException>(() => new Basis().Inverse());
    }
}
