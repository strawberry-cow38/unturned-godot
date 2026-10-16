using Godot;
using NUnit.Framework;
using System.Text.Json;
using UnturnedBevy;
using UnturnedSim;

namespace UnturnedBevyTests;

[TestFixture]
public sealed class BuildingSceneTests
{
    static string Root()
    {
        for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); directory != null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "game/content"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }
    static BuildingScene Scene() => new(Path.Combine(Root(), "game/content"));
    static float[] V(Vector3 p) => new[] { p.X, p.Y, p.Z };
    static void Same(JsonElement actual, JsonElement expected, string path)
    {
        Assert.That(actual.ValueKind, Is.EqualTo(expected.ValueKind), path);
        switch (actual.ValueKind)
        {
            case JsonValueKind.Object:
                Assert.That(actual.EnumerateObject().Count(), Is.EqualTo(expected.EnumerateObject().Count()), path);
                foreach (var p in expected.EnumerateObject()) Same(actual.GetProperty(p.Name), p.Value, path + "." + p.Name);
                break;
            case JsonValueKind.Array:
                Assert.That(actual.GetArrayLength(), Is.EqualTo(expected.GetArrayLength()), path);
                for (int i = 0; i < actual.GetArrayLength(); i++) Same(actual[i], expected[i], path + "[" + i + "]");
                break;
            case JsonValueKind.Number:
                Assert.That(actual.GetDouble(), Is.EqualTo(expected.GetDouble()).Within(2e-5), path);
                break;
            default: Assert.That(actual.ToString(), Is.EqualTo(expected.ToString()), path); break;
        }
    }

    [Test] public void CompleteGeneratedScenesMatchOriginalGodotAssemblyAndSavedBytes()
    {
        using var oracle = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "tests/UnturnedBevy.Tests/fixtures/building-scenes-godot.json")));
        var scene = Scene();
        int count = 0, wallCount = 0, rampCount = 0;
        foreach (var row in oracle.RootElement.EnumerateArray())
        {
            var spec = new BuildingGenSpec {
                Seed = row.GetProperty("seed").GetInt32(), Kind = Enum.Parse<BuildingKind>(row.GetProperty("kind").GetString()),
                MinFloors = row.GetProperty("floors").GetInt32(), MaxFloors = row.GetProperty("floors").GetInt32()
            };
            var generated = scene.Generate(spec);
            string name = spec.Kind + "/" + spec.Seed;
            Assert.That(generated.Palette, Is.EqualTo(row.GetProperty("palette").GetString()), name);
            Assert.That(scene.SaveText(), Is.EqualTo(row.GetProperty("text").GetString()), name + " complete WallSave bytes");
            Same(JsonSerializer.SerializeToElement(scene.Plans(), new JsonSerializerOptions { IncludeFields = true }), row.GetProperty("plans"), name);
            Same(JsonSerializer.SerializeToElement(scene.Ramps.Select(r => new {
                position = V(r.Position), basis = new[] { V(r.Basis.X), V(r.Basis.Y), V(r.Basis.Z) }, size = V(r.Size)
            })), row.GetProperty("ramps"), name + ".ramps");
            Assert.That(scene.Walls.All(w => w.Position.Y > 1900), Is.True, "source stage origin retained");
            count++; wallCount += scene.Walls.Count; rampCount += scene.Ramps.Count;
        }
        Assert.That(count, Is.EqualTo(8));
        Assert.That(wallCount, Is.EqualTo(493));
        Assert.That(rampCount, Is.EqualTo(8));
    }

    [Test] public void SavedLayoutPreservesDesignationsAndOpeningsWithoutInventingRampPersistence()
    {
        var scene = Scene();
        scene.Generate(new BuildingGenSpec { Seed = 17, Kind = BuildingKind.Residential, MinFloors = 2, MaxFloors = 2 });
        Assert.That(scene.Ramps, Is.Not.Empty);
        string saved = scene.SaveText();
        var reopened = Scene();
        Assert.That(reopened.LoadText(saved), Is.EqualTo(scene.Walls.Count));
        Assert.That(reopened.SaveText(), Is.EqualTo(saved));
        Assert.That(reopened.Ramps, Is.Empty, "source WallSave contains treads, not transient stair ramps");
        Assert.That(reopened.Furniture.Count, Is.EqualTo(scene.Furniture.Count));
        Assert.That(reopened.Designations.Count, Is.EqualTo(scene.Designations.Count));
        Assert.That(reopened.Walls.Any(w => w.Openings.Any(o => o.HasDoor)), Is.True);
        Assert.That(reopened.Walls.Any(w => w.Openings.Any(o => o.HasGlass)), Is.True);
        Assert.That(reopened.LoadText(WallSave.Write(Array.Empty<WallPlan>())), Is.Zero);
        Assert.That(reopened.Walls, Is.Empty);
        Assert.That(reopened.Furniture, Is.Empty);
        Assert.That(reopened.DesignatedKind, Is.Null);
        Assert.That(reopened.SaveText(), Is.EqualTo(WallSave.Write(Array.Empty<WallPlan>())));
    }

    [Test] public void StairFloorCutsUseWallFrameAndRetainSourcePartialOverlapRejection()
    {
        var scene = Scene();
        scene.LoadText("wall -5 4.25 5 0 10 .3 0 10 -90 Floor\nwall -5 8.5 5 0 10 .3 0 10 -90 Floor");
        Assert.That(scene.FitStairsToStructure(Vector3.Zero, 0, 4, 2, 4.25f), Is.EqualTo(1));
        Assert.That(scene.Walls[0].Openings, Has.Count.EqualTo(1));
        Assert.That(scene.Walls[1].Openings, Is.Empty, "upper storey not touched");
        Assert.That(scene.Ramps, Has.Count.EqualTo(1));
        Assert.That(scene.FitStairsToStructure(new(100, 0, 100), 37, 4, 2, 4.25f), Is.Zero);
        Assert.That(scene.Ramps, Has.Count.EqualTo(2), "ramp remains even without a floor to cut");
        Assert.That(scene.FitStairsToStructure(new(4.9f, 0, 0), 0, 4, 2, 4.25f), Is.Zero,
            "source rejects a footprint whose grown corners leave the slab UV bounds");
    }
}
